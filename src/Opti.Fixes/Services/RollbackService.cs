using Opti.Core.Fixes;
using Opti.Core.Localization;

namespace Opti.Fixes.Services;

public sealed class RollbackReport
{
    public List<ChangeRecord> Restored { get; } = new();
    /// <summary>Настройку позже изменило другое активное исправление — оставлена как есть.</summary>
    public List<ChangeRecord> SkippedSupersededBy { get; } = new();
    /// <summary>
    /// Значение изменили после Opti (вы сами, Windows или другая программа) — оставлено как есть,
    /// чтобы откат не затёр более свежую настройку. Принудительно — параметр force.
    /// </summary>
    public List<DriftedChange> ChangedSinceOpti { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Короткая сводка для уведомления.</summary>
    public string Summary(string? verb = null)
    {
        var text = $"{verb ?? Loc.T("Откачено изменений")}: {Restored.Count}.";
        if (ChangedSinceOpti.Count > 0) text += Loc.T($" Оставлено как есть (изменено после Opti): {ChangedSinceOpti.Count}.");
        if (Errors.Count > 0) text += Loc.T($" Ошибок: {Errors.Count}.");
        return text;
    }
}

/// <summary>Запись, значение которой сейчас отличается и от поставленного Opti, и от исходного.</summary>
public sealed record DriftedChange(ChangeRecord Record, StateValue Current);

/// <summary>Откат по исправлению, по сессии или полный. Восстанавливает исходные значения в обратном порядке.</summary>
public sealed class RollbackService
{
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;

    public RollbackService(IBackendRegistry backends, IChangeJournal journal)
    {
        _backends = backends;
        _journal = journal;
    }

    public RollbackReport RollbackFix(string fixId, string? sessionId = null, bool force = false) =>
        RollbackRecords(Active().Where(r => string.Equals(r.FixId, fixId, StringComparison.OrdinalIgnoreCase)
                                            && (sessionId is null || r.SessionId == sessionId)), force);

    public RollbackReport RollbackSession(string sessionId, bool force = false) =>
        RollbackRecords(Active().Where(r => r.SessionId == sessionId), force);

    public RollbackReport RollbackAll(bool force = false) => RollbackRecords(Active(), force);

    private IEnumerable<ChangeRecord> Active() => _journal.GetAll().Where(r => !r.RolledBack);

    /// <param name="force">Вернуть исходное значение, даже если настройку изменили после Opti.</param>
    public RollbackReport RollbackRecords(IEnumerable<ChangeRecord> records, bool force = false)
    {
        var report = new RollbackReport();
        var toRollback = records.Where(r => !r.RolledBack).OrderByDescending(r => r.Id).ToList();
        var ids = toRollback.Select(r => r.Id).ToHashSet();
        var allActive = _journal.GetAll().Where(r => !r.RolledBack).ToList();

        foreach (var rec in toRollback)
        {
            // Если ту же настройку позже изменило исправление, которое не откатывается, — не трогаем значение.
            bool superseded = allActive.Any(o => o.Id > rec.Id && !ids.Contains(o.Id) && o.Type == rec.Type
                                                 && string.Equals(o.Target, rec.Target, StringComparison.OrdinalIgnoreCase));
            try
            {
                var backend = _backends.Get(rec.Type);
                if (superseded)
                    report.SkippedSupersededBy.Add(rec);
                else if (!force && DetectsDrift(rec.Type) && backend.Read(rec.Target) is var current
                         && !Same(current, rec.After) && !Same(current, rec.Before))
                    // Opti своё изменение больше «не держит»: действует значение, поставленное позже.
                {
                    report.ChangedSinceOpti.Add(new DriftedChange(rec, current));
                    Opti.Core.Diagnostics.OptiLog.Info($"Откат {rec.FixId} {rec.Target}: оставлено {current} (изменено после Opti, Opti ставил {rec.After})");
                }
                else
                {
                    backend.Write(rec.Target, rec.Before);
                    report.Restored.Add(rec);
                }
                _journal.MarkRolledBack(rec.Id, DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{rec.FixId} {rec.Type} {rec.Target}: {ex.Message}");
                Opti.Core.Diagnostics.OptiLog.Warn($"Откат {rec.FixId} {rec.Type} {rec.Target} не удался", ex);
            }
        }
        return report;
    }

    /// <summary>
    /// Проверка «изменено после Opti» только там, где чтение возвращает ровно записанное значение.
    /// У экрана, питания и BCD формат чтения может отличаться от записанного — там откат всегда возвращает исходное.
    /// </summary>
    private static bool DetectsDrift(ChangeType t) => t is ChangeType.Registry or ChangeType.Service;

    internal static bool Same(StateValue a, StateValue b)
    {
        if (a.Exists != b.Exists) return false;
        if (!a.Exists) return true;
        return string.Equals(Norm(a.Value), Norm(b.Value), StringComparison.OrdinalIgnoreCase);
        static string Norm(string? v) => (v ?? "").Trim();
    }
}
