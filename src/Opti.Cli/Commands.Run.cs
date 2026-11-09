using System.Diagnostics;
using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Measure.Capture;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.Cli;

/// <summary>Мастер run, продолжение после перезагрузки (resume) и A/B-проверка (ab).</summary>
internal static partial class Commands
{
    // ------------------------------------------------------------------ run (мастер)

    public static async Task<int> Run(Args a)
    {
        RequireWindows(Loc.T("Оптимизация"));
        using var host = new CliHost();
        var wf = host.Workflow;

        var open = wf.Store.LatestOpen();
        if (open is not null && open.Stage is SessionStage.AwaitingReboot or SessionStage.Applied or SessionStage.VerifiedAfterReboot or SessionStage.MeasuredAfter)
        {
            Console.WriteLine(Loc.T($"Есть незавершённая сессия {open.Id} ({open.Stage}). Продолжаю её."));
            return await ContinueSession(host, open, a);
        }

        var goal = AskGoal(a, host.Settings);
        var analysis = AnalysisLoader.LoadOrCollect(a);
        Output.PrintFindings(analysis.Findings);
        var s = wf.StartSession(analysis, goal);
        s.MeasureMode = AskMode(a);
        wf.Store.Save(s);

        Output.Header(Loc.T("Шаг 1. Замер «ДО»"));
        if (Output.Confirm(Loc.T("Сделать замер «до»? (без него программа не сможет честно показать результат)")))
            wf.RecordBefore(s, await MeasureSeries(host.Settings, s.MeasureMode, Loc.T("до"), a, s.GameProcess));
        s.GameProcess = s.Before.FirstOrDefault()?.Conditions.ProcessName is { } pn && s.MeasureMode != MeasureMode.Synthetic ? pn : s.GameProcess;

        Output.Header(Loc.T("Шаг 2. Стратегия"));
        var plan = wf.BuildPlan(s);
        Output.PrintPlan(plan);
        var chosen = plan.Fixes.Where(p => p.Selected && !p.ApplySeparately).Select(p => p.Fix).ToList();
        var abCandidates = plan.Fixes.Where(p => p.ApplySeparately).Select(p => p.Fix.Id).ToList();
        if (chosen.Count == 0)
        {
            Console.WriteLine(Loc.T("\nАвтоматически применять нечего. Советы — в отчёте."));
            Console.WriteLine(Loc.T($"Отчёт: {WriteReport(host, s, null)}"));
            wf.Close(s);
            return 0;
        }
        Console.WriteLine(Loc.T("\nБудут применены: ") + string.Join(", ", chosen.Select(f => f.Id)));
        if (abCandidates.Count > 0) Console.WriteLine(Loc.T("Спорные настройки проверяются отдельно: ") + string.Join(", ", abCandidates.Select(id => $"opti-cli ab --fix {id}")));
        if (!a.Has("yes") && !Output.Confirm(Loc.T("Применить? Перед изменениями создаётся точка восстановления, всё записывается в журнал для отката."))) return 1;

        Output.Header(Loc.T("Шаг 3. Применение"));
        var report = wf.Apply(s, chosen, CliHost.ExePath, "resume");
        PrintApplyReport(report);
        if (report.RebootRequired) { OfferReboot(a); return 0; }
        return await ContinueSession(host, s, a);
    }

    /// <summary>Продолжение сессии: самопроверка → замер «после» → сравнение → отчёт → решение.</summary>
    private static async Task<int> ContinueSession(CliHost host, TuneSession s, Args a)
    {
        var wf = host.Workflow;
        if (s.Stage == SessionStage.AwaitingReboot)
        {
            var check = wf.ResumeIfRebooted(s);
            if (check is null) { Console.WriteLine(Loc.T("Сессия ждёт перезагрузки. Перезагрузите компьютер — Opti продолжит сам.")); return 0; }
            PrintPostReboot(check);
            if (check.SuggestRollback && Output.Confirm(Loc.T("После изменений были сбои системы. Откатить изменения этой сессии?")))
            {
                PrintRollback(wf.RollbackSession(s));
                wf.Close(s);
                return 0;
            }
        }

        if (s.Stage is SessionStage.Applied or SessionStage.VerifiedAfterReboot && s.Before.Count > 0)
        {
            Output.Header(Loc.T("Шаг 4. Замер «ПОСЛЕ» — в тех же условиях"));
            wf.RecordAfter(s, await MeasureSeries(host.Settings, s.MeasureMode, Loc.T("после"), a, s.GameProcess));
        }
        if (s.Before.Count == 0 || s.After.Count == 0)
        {
            Console.WriteLine(Loc.T($"Замера «до» нет — сравнение невозможно. Отчёт: {WriteReport(host, s, null)}"));
            wf.Close(s);
            return 0;
        }

        Output.Header(Loc.T("Шаг 5. Сравнение"));
        var cmp = wf.Compare(s);
        PrintComparison(cmp.Result);
        Console.WriteLine("\n" + cmp.Message);
        var path = WriteReport(host, s, cmp);
        Console.WriteLine(Loc.T($"Отчёт: {path}"));
        if (cmp.Recommendation == Recommendation.SuggestRollback && (a.Has("yes") || Output.Confirm(Loc.T("Откатить изменения этой сессии?"))))
        {
            PrintRollback(wf.RollbackSession(s));
            WriteReport(host, s, cmp, path);
        }
        wf.Close(s);
        if (!a.Has("no-open")) OpenFile(path);
        return 0;
    }

    private static void PrintPostReboot(PostRebootReport check)
    {
        Output.Header(Loc.T("Самопроверка после перезагрузки"));
        Console.WriteLine(Loc.T($"  Подтверждено изменений: {check.Verified}"));
        foreach (var n in check.NotApplied) Output.Color(ConsoleColor.Yellow, Loc.T("  Не применилось: ") + n + "\n");
        foreach (var e in check.ReadErrors) Output.Color(ConsoleColor.DarkGray, "  " + e + "\n");
        foreach (var c in check.Crashes) Output.Color(ConsoleColor.Red, Loc.T($"  Сбой системы {c.Time.LocalDateTime:dd.MM HH:mm}: событие {c.EventId} {c.Source}\n"));
        if (check.Healthy) Output.Color(ConsoleColor.Green, Loc.T("  Всё применилось, сбоев не было.\n"));
    }

    private static void PrintComparison(ComparisonResult result)
    {
        foreach (var m in result.Metrics)
        {
            var color = m.Verdict switch { Verdict.Better => ConsoleColor.Green, Verdict.Worse => ConsoleColor.Red, _ => ConsoleColor.Gray };
            Output.Color(color, "  " + m.Format() + "\n");
            Output.Color(ConsoleColor.DarkGray, Loc.T($"     95 % ДИ: до [{m.BeforeCi.Low:0.##}; {m.BeforeCi.High:0.##}], после [{m.AfterCi.Low:0.##}; {m.AfterCi.High:0.##}], разность [{m.DiffCi.Low:+0.##;-0.##;0}; {m.DiffCi.High:+0.##;-0.##;0}], разброс прогонов {m.RunSpread:0.##}\n"));
        }
        foreach (var w in result.Warnings) Output.Color(ConsoleColor.Yellow, "  ! " + w + "\n");
    }

    private static void PrintRollback(RollbackReport rep)
    {
        foreach (var r in rep.Restored) Output.Color(ConsoleColor.Green, Loc.T($"  восстановлено {r.Type} {r.Target} = {r.Before}\n"));
        foreach (var r in rep.SkippedSupersededBy) Output.Color(ConsoleColor.Yellow, Loc.T($"  оставлено {r.Type} {r.Target}: позже изменено другим исправлением\n"));
        foreach (var d in rep.ChangedSinceOpti)
            Output.Color(ConsoleColor.Yellow, Loc.T($"  оставлено {d.Record.Type} {d.Record.Target}: изменено после Opti (сейчас {d.Current}, Opti ставил {d.Record.After}, до Opti было {d.Record.Before}). Вернуть исходное: --force\n"));
        foreach (var e in rep.Errors) Output.Color(ConsoleColor.Red, Loc.T($"  ошибка: {e}\n"));
        if (rep.Restored.Count + rep.SkippedSupersededBy.Count + rep.ChangedSinceOpti.Count + rep.Errors.Count == 0) Console.WriteLine(Loc.T("  Нечего откатывать."));
        if (rep.Restored.Any(r => r.Type is ChangeType.Bcd) || rep.Restored.Any(r => r.Target.Contains("GraphicsDrivers") || r.Target.Contains("Dwm") || r.Target.Contains("kernel")))
            Output.Color(ConsoleColor.Yellow, Loc.T("  Часть возвращённых настроек вступит в силу после перезагрузки.\n"));
    }

    // ------------------------------------------------------------------ resume

    public static async Task<int> Resume(Args a)
    {
        RequireWindows(Loc.T("Продолжение"));
        using var host = new CliHost();
        var wf = host.Workflow;
        var s = wf.Store.LatestOpen();
        if (s is null) { Console.WriteLine(Loc.T("Нет незавершённых сессий.")); try { new WindowsResumeScheduler().Cancel(); } catch { } return 0; }
        Console.WriteLine(Loc.T($"Opti {AppInfo.Version}: продолжаю сессию {s.Id} (режим «{s.Goal.Ru()}»)."));

        int rc;
        if (s.AbTests.Any(x => x.Phase == AbPhase.AwaitRebootB))
            rc = await ContinueAb(host, s, a);
        else
            rc = await ContinueSession(host, s, a);

        if (!a.Has("yes")) { Console.Write(Loc.T("\nНажмите Enter, чтобы закрыть окно…")); Console.ReadLine(); }
        return rc;
    }

    // ------------------------------------------------------------------ ab

    public static async Task<int> Ab(Args a)
    {
        RequireWindows(Loc.T("A/B-проверка"));
        var fixId = a.Get("fix") ?? throw new ArgumentException(Loc.T("Укажите --fix (например, HAGS_AB)."));
        using var host = new CliHost();
        var wf = host.Workflow;
        var s = SessionForWork(host, a);
        s.MeasureMode = ParseMode(a.Get("mode"), SyntheticRunner.Locate() is not null ? MeasureMode.Synthetic : MeasureMode.Game);
        var ab = wf.StartAb(s, fixId, a.Get("variant"));
        Output.Header($"A/B: {ab.VariantB}");
        Console.WriteLine(Loc.T("Вариант A — текущее состояние, вариант B — с изменением. Останется тот, что достоверно лучше; при равенстве — исходный."));

        Output.Header(Loc.T("Замер варианта A"));
        wf.RecordAbA(s, await MeasureSeries(host.Settings, s.MeasureMode, "A", a, s.GameProcess));
        Output.Header(Loc.T("Применение варианта B"));
        var rep = wf.ApplyAbB(s, CliHost.ExePath);
        PrintApplyReport(rep);
        if (rep.RebootRequired) { OfferReboot(a); return 0; }
        return await ContinueAb(host, s, a);
    }

    private static async Task<int> ContinueAb(CliHost host, TuneSession s, Args a)
    {
        var wf = host.Workflow;
        if (s.AbTests.Any(x => x.Phase == AbPhase.AwaitRebootB) && !wf.ResumeAbIfRebooted(s))
        {
            Console.WriteLine(Loc.T("A/B-проверка ждёт перезагрузки."));
            return 0;
        }
        Output.Header(Loc.T("Замер варианта B"));
        var runs = await MeasureSeries(host.Settings, s.MeasureMode, "B", a, s.GameProcess);
        var (ab, result, rb) = wf.DecideAb(s, runs);
        PrintComparison(result);
        Output.Color(ab.KeptB ? ConsoleColor.Green : ConsoleColor.Yellow, "\n" + ab.Summary + "\n");
        if (rb is not null) PrintRollback(rb);
        Console.WriteLine(Loc.T($"Отчёт: {WriteReport(host, s, null)}"));
        return 0;
    }
}
