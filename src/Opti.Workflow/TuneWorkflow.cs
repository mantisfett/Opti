using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Fixes.Catalog;
using Opti.Fixes.Services;
using Opti.Measure.Stats;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.Workflow;

public enum Recommendation { Keep, MarkNoEffect, SuggestRollback, NeedMoreData }

public sealed record CompareOutcome(ComparisonResult Result, Recommendation Recommendation, string Message);

/// <summary>
/// Сценарий из §2 ТЗ: анализ → замер «до» → стратегия → применение (+ точка восстановления, журнал) →
/// перезагрузка с продолжением → самопроверка → замер «после» → честное сравнение → решение.
/// Общий для консольной программы и интерфейса.
/// </summary>
public sealed class TuneWorkflow
{
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;
    private readonly IRestorePointService? _restore;
    private readonly IResumeScheduler? _scheduler;
    private readonly ICrashEventSource? _crashes;

    public TuneWorkflow(OptiSettings settings, SessionStore store, IBackendRegistry backends, IChangeJournal journal,
        IRestorePointService? restore = null, IResumeScheduler? scheduler = null, ICrashEventSource? crashes = null,
        IReadOnlyList<IFix>? catalog = null)
    {
        Settings = settings;
        Store = store;
        _backends = backends;
        _journal = journal;
        _restore = restore;
        _scheduler = scheduler;
        _crashes = crashes;
        Catalog = catalog ?? FixCatalog.All();
    }

    public OptiSettings Settings { get; }
    public SessionStore Store { get; }
    public IReadOnlyList<IFix> Catalog { get; }
    public IChangeJournal Journal => _journal;
    /// <summary>Бэкенды настроек — для полного отката вне сессий.</summary>
    public IBackendRegistry BackendsForRollback => _backends;

    /// <summary>«Страж»: на месте ли действующие изменения Opti (обновления Windows и драйверов их иногда возвращают).</summary>
    public VerifyReport Verify() => new VerifyService(_backends, _journal).Verify();

    /// <summary>Применить значения Opti снова для выбранных настроек.</summary>
    public (int Reapplied, List<string> Errors) Reapply(IEnumerable<VerifyItem> items) => new VerifyService(_backends, _journal).Reapply(items);

    /// <summary>Понятное название исправления или твика по его идентификатору.</summary>
    public string FixTitle(string fixId) =>
        Catalog.FirstOrDefault(f => string.Equals(f.Id, fixId, StringComparison.OrdinalIgnoreCase))?.Title
        ?? Opti.Core.Tweaks.TweakCatalog.All.FirstOrDefault(t => string.Equals(t.FixId, fixId, StringComparison.OrdinalIgnoreCase))?.Title
        ?? fixId;

    public TuneSession StartSession(AnalysisResult analysis, UserGoal goal)
    {
        var s = new TuneSession { Goal = goal, Profile = analysis.Profile, Findings = analysis.Findings };
        Store.Save(s);
        return s;
    }

    public StrategyPlan BuildPlan(TuneSession s)
    {
        var plan = new StrategyEngine(Catalog).Build(s.Profile!, s.Findings, s.Goal);
        var excluded = new HashSet<string>(Settings.ExcludedFixIds, StringComparer.OrdinalIgnoreCase);
        foreach (var p in plan.Fixes.Where(p => excluded.Contains(p.Fix.Id)).ToList())
        {
            plan.Fixes.Remove(p);
            plan.Excluded.Add(new ExcludedFix(p.Fix.Id, Loc.T("исключено в настройках"), p.TriggeredBy));
        }
        return plan;
    }

    public void RecordBefore(TuneSession s, IEnumerable<MeasurementRun> runs)
    {
        s.Before.AddRange(runs);
        if (s.Stage < SessionStage.MeasuredBefore) s.Stage = SessionStage.MeasuredBefore;
        Store.Save(s);
    }

    /// <summary>
    /// Применяет исправления. Если нужна перезагрузка — ставит задачу продолжения при входе
    /// (resumeCommand — «"путь к exe" аргументы»).
    /// </summary>
    public ApplyReport Apply(TuneSession s, IReadOnlyList<IFix> fixes, string? resumeExe = null, string resumeArgs = "resume")
    {
        var options = s.FixOptions.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        var report = new ApplyService(_backends, _journal, _restore).Apply(s.Profile!, fixes, s.Goal, options, s.Id);
        s.SelectedFixIds = fixes.Select(f => f.Id).ToList();
        s.AppliedFixIds.AddRange(report.Results.Where(r => r.Outcome == FixOutcome.Applied).Select(r => r.FixId));
        s.FailedFixes.AddRange(report.Results.Where(r => r.Outcome == FixOutcome.Failed).Select(r => $"{r.FixId}: {r.Error}"));
        s.AppliedAt = DateTimeOffset.Now;
        s.RestorePoint ??= report.RestorePoint;
        if (report.RestorePointWarning is not null) s.Notes.Add(report.RestorePointWarning);
        s.RebootRequired = report.RebootRequired;
        s.Stage = report.RebootRequired ? SessionStage.AwaitingReboot : SessionStage.Applied;
        if (report.RebootRequired && _scheduler is not null && resumeExe is not null)
        {
            try { _scheduler.ScheduleOnLogon(resumeExe, resumeArgs); }
            catch (Exception ex)
            {
                s.Notes.Add(Loc.T("Задача продолжения не создана: ") + ex.Message + Loc.T(". После перезагрузки запустите Opti вручную."));
                Opti.Core.Diagnostics.OptiLog.Warn("Задача продолжения не создана", ex);
            }
        }
        Store.Save(s);
        return report;
    }

    /// <summary>Вызывается при запуске программы: если сессия ждала перезагрузку и она была — самопроверка.</summary>
    public PostRebootReport? ResumeIfRebooted(TuneSession s)
    {
        if (s.Stage != SessionStage.AwaitingReboot || s.AppliedAt is null) return null;
        if (!RebootHelper.RebootedSince(s.AppliedAt.Value)) return null;
        try { _scheduler?.Cancel(); } catch { /* задачи могло не быть */ }
        var report = new PostRebootCheck(_backends, _journal, _crashes).Run(s.Id, s.AppliedAt.Value);
        s.PostReboot = report;
        s.Stage = SessionStage.VerifiedAfterReboot;
        if (report.SuggestRollback) s.Notes.Add(Loc.T("После применения были сбои системы — рекомендуется откат."));
        Store.Save(s);
        return report;
    }

    public void RecordAfter(TuneSession s, IEnumerable<MeasurementRun> runs)
    {
        s.After.AddRange(runs);
        s.Stage = SessionStage.MeasuredAfter;
        Store.Save(s);
    }

    public CompareOutcome Compare(TuneSession s)
    {
        var result = new Comparer().Compare(s.Before, s.After);
        var (rec, outcome, msg) = result.Overall switch
        {
            Verdict.Better => (Recommendation.Keep, SessionOutcome.Improved, Loc.T("Стало лучше: улучшение больше шума измерений. Изменения сохранены.")),
            Verdict.Worse => (Recommendation.SuggestRollback, SessionOutcome.Worse, Loc.T("Стало хуже. Рекомендуется откатить изменения этой сессии.")),
            Verdict.NoChange => (Settings.RollbackWhenNoEffect ? Recommendation.SuggestRollback : Recommendation.MarkNoEffect, SessionOutcome.NoEffect,
                                 Loc.T("Без изменений: разница в пределах шума. Изменения помечены как «не дали эффекта»") +
                                 (Settings.RollbackWhenNoEffect ? Loc.T(" — по настройке будут откачены.") : ".")),
            _ => (Recommendation.NeedMoreData, SessionOutcome.Unknown, Loc.T("Недостаточно данных: нужно хотя бы по одному надёжному прогону «до» и «после».")),
        };
        if (!result.Reliable && rec == Recommendation.Keep) msg += Loc.T(" Внимание: сравнение помечено как ненадёжное.");
        s.Outcome = outcome;
        s.Stage = SessionStage.Compared;
        Store.Save(s);
        return new CompareOutcome(result, rec, msg);
    }

    public RollbackReport RollbackSession(TuneSession s, bool force = false)
    {
        var r = new RollbackService(_backends, _journal).RollbackRecords(_journal.GetAll().Where(x => x.SessionId.StartsWith(s.Id, StringComparison.Ordinal)), force);
        s.Outcome = SessionOutcome.RolledBack;
        s.Notes.Add(Loc.T($"Откат {DateTimeOffset.Now:yyyy-MM-dd HH:mm}: восстановлено {r.Restored.Count}, ошибок {r.Errors.Count}") +
                    (r.ChangedSinceOpti.Count > 0 ? Loc.T($", оставлено (изменено после Opti) {r.ChangedSinceOpti.Count}.") : "."));
        Store.Save(s);
        return r;
    }

    public RollbackReport RollbackFix(TuneSession s, string fixId, bool force = false)
    {
        var r = new RollbackService(_backends, _journal).RollbackRecords(
            _journal.GetAll().Where(x => x.SessionId.StartsWith(s.Id, StringComparison.Ordinal) && string.Equals(x.FixId, fixId, StringComparison.OrdinalIgnoreCase)), force);
        s.Notes.Add(Loc.T($"Откачено {fixId}."));
        Store.Save(s);
        return r;
    }

    public void Close(TuneSession s)
    {
        s.Stage = SessionStage.Closed;
        Store.Save(s);
    }

    // ---------------- A/B спорных настроек ----------------

    /// <summary>Начало A/B: вариант A — текущее состояние системы.</summary>
    public AbTestRecord StartAb(TuneSession s, string fixId, string? variantB = null)
    {
        var fix = FindFix(fixId);
        if (!fix.RequiresAbTest && fix.Risk == RiskLevel.Safe)
            throw new ArgumentException(Loc.T($"{fixId} не требует A/B-проверки."));
        s.AbTests.RemoveAll(a => a.FixId.Equals(fixId, StringComparison.OrdinalIgnoreCase) && a.Phase != AbPhase.Decided);
        var ab = new AbTestRecord { FixId = fix.Id, VariantA = Loc.T("как сейчас"), VariantB = variantB ?? fix.Title };
        if (variantB is not null)
        {
            if (!s.FixOptions.TryGetValue(fix.Id, out var o)) s.FixOptions[fix.Id] = o = new();
            o["variant"] = variantB;
        }
        s.AbTests.Add(ab);
        Store.Save(s);
        return ab;
    }

    public AbTestRecord CurrentAb(TuneSession s) =>
        s.AbTests.LastOrDefault(a => a.Phase != AbPhase.Decided) ?? throw new InvalidOperationException(Loc.T("Нет незавершённой A/B-проверки."));

    public void RecordAbA(TuneSession s, IEnumerable<MeasurementRun> runs)
    {
        var ab = CurrentAb(s);
        ab.RunsA.AddRange(runs);
        ab.Phase = AbPhase.ApplyB;
        Store.Save(s);
    }

    /// <summary>Применяет вариант B отдельной подсессией журнала («{id}-ab-{fix}»), чтобы откатывать его независимо.</summary>
    public ApplyReport ApplyAbB(TuneSession s, string? resumeExe = null)
    {
        var ab = CurrentAb(s);
        var fix = FindFix(ab.FixId);
        var options = s.FixOptions.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        var report = new ApplyService(_backends, _journal, _restore).Apply(s.Profile!, new[] { fix }, s.Goal, options, AbSessionId(s, ab));
        var res = report.Results.Single();
        if (res.Outcome == FixOutcome.Failed) throw new InvalidOperationException(Loc.T($"Не удалось применить {ab.FixId}: {res.Error}"));
        s.AppliedAt = DateTimeOffset.Now;
        ab.Phase = report.RebootRequired ? AbPhase.AwaitRebootB : AbPhase.MeasureB;
        if (report.RebootRequired && _scheduler is not null && resumeExe is not null)
        {
            try { _scheduler.ScheduleOnLogon(resumeExe, "resume"); }
            catch (Exception ex) { s.Notes.Add(Loc.T("Задача продолжения не создана: ") + ex.Message); }
        }
        Store.Save(s);
        return report;
    }

    /// <summary>После перезагрузки в A/B: переводит в фазу замера B.</summary>
    public bool ResumeAbIfRebooted(TuneSession s)
    {
        var ab = s.AbTests.LastOrDefault(a => a.Phase == AbPhase.AwaitRebootB);
        if (ab is null || s.AppliedAt is null || !RebootHelper.RebootedSince(s.AppliedAt.Value)) return false;
        try { _scheduler?.Cancel(); } catch { }
        ab.Phase = AbPhase.MeasureB;
        Store.Save(s);
        return true;
    }

    /// <summary>Решение A/B: вариант B остаётся только при достоверном улучшении, иначе откатывается.</summary>
    public (AbTestRecord Ab, ComparisonResult Result, RollbackReport? Rollback) DecideAb(TuneSession s, IEnumerable<MeasurementRun> runsB)
    {
        var ab = CurrentAb(s);
        ab.RunsB.AddRange(runsB);
        var result = new Comparer().Compare(ab.RunsA, ab.RunsB);
        ab.Verdict = result.Overall;
        RollbackReport? rb = null;
        if (result.Overall == Verdict.Better)
        {
            ab.KeptB = true;
            ab.Summary = Loc.T($"«{ab.VariantB}» лучше — оставлено.");
            if (!s.AppliedFixIds.Contains(ab.FixId)) s.AppliedFixIds.Add(ab.FixId);
        }
        else
        {
            ab.KeptB = false;
            rb = new RollbackService(_backends, _journal).RollbackSession(AbSessionId(s, ab), force: true);
            ab.Summary = (result.Overall == Verdict.Worse
                ? Loc.T($"«{ab.VariantB}» хуже — возвращено исходное состояние.")
                : Loc.T($"«{ab.VariantB}» не дал достоверного эффекта — возвращено исходное состояние."))
                + (FindFix(ab.FixId).RequiresReboot ? Loc.T(" Перезагрузите компьютер, чтобы возврат вступил в силу.") : "");
        }
        ab.Phase = AbPhase.Decided;
        Store.Save(s);
        return (ab, result, rb);
    }

    public static string AbSessionId(TuneSession s, AbTestRecord ab) => $"{s.Id}-ab-{ab.FixId}";

    private IFix FindFix(string id) =>
        Catalog.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException(Loc.T($"Неизвестное исправление {id}"));
}
