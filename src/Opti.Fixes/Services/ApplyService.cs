using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Core.Localization;

namespace Opti.Fixes.Services;

public enum FixOutcome { Applied, NothingToChange, Failed, Skipped }

public sealed record FixApplyResult(string FixId, FixOutcome Outcome, IReadOnlyList<ChangeRecord> Changes, string? Error = null);

public sealed class ApplyReport
{
    public required string SessionId { get; init; }
    public string? RestorePoint { get; set; }
    public string? RestorePointWarning { get; set; }
    public List<FixApplyResult> Results { get; } = new();
    public bool RebootRequired { get; set; }
}

/// <summary>
/// Применяет выбранные исправления. Каждое исправление атомарно: при ошибке посередине
/// уже сделанные им изменения откатываются.
/// </summary>
public sealed class ApplyService
{
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;
    private readonly IRestorePointService? _restore;

    public ApplyService(IBackendRegistry backends, IChangeJournal journal, IRestorePointService? restore)
    {
        _backends = backends;
        _journal = journal;
        _restore = restore;
    }

    public ApplyReport Apply(SystemProfile profile, IEnumerable<IFix> fixes, UserGoal goal,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? options = null, string? sessionId = null)
    {
        var list = fixes.ToList();
        var report = new ApplyReport { SessionId = sessionId ?? DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] };
        var session = new SessionInfo { SessionId = report.SessionId, Goal = goal.ToString() };

        if (list.Count > 0 && _restore is not null)
        {
            report.RestorePoint = _restore.TryCreate($"Opti {report.SessionId}", out var err);
            report.RestorePointWarning = err;
            session.RestorePoint = report.RestorePoint;
        }
        _journal.SaveSession(session);

        foreach (var fix in list)
        {
            var opts = options?.GetValueOrDefault(fix.Id);
            var ctx = new ChangeContext(_backends, _journal, report.SessionId, fix.Id, profile, opts);
            try
            {
                var changes = fix.Apply(ctx);
                report.Results.Add(new FixApplyResult(fix.Id, changes.Count > 0 ? FixOutcome.Applied : FixOutcome.NothingToChange, changes));
                Opti.Core.Diagnostics.OptiLog.Info($"{report.SessionId}: {fix.Id} — изменений {changes.Count}");
                if (changes.Count > 0 && fix.RequiresReboot) report.RebootRequired = true;
            }
            catch (Exception ex)
            {
                // откат частично применённого исправления
                var rollback = new RollbackService(_backends, _journal);
                var partial = rollback.RollbackRecords(ctx.Records, force: true);
                var msg = ex.Message + (partial.Errors.Count > 0 ? Loc.T(" | ошибки отката: ") + string.Join("; ", partial.Errors) : "");
                report.Results.Add(new FixApplyResult(fix.Id, FixOutcome.Failed, Array.Empty<ChangeRecord>(), msg));
                Opti.Core.Diagnostics.OptiLog.Warn($"{report.SessionId}: {fix.Id} не применено: {msg}", ex);
            }
        }

        session.Notes = report.RebootRequired ? "pending-reboot" : null;
        _journal.SaveSession(session);
        return report;
    }
}
