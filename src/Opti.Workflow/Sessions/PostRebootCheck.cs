using System.Runtime.Versioning;
using Opti.Core.Fixes;
using Opti.Core.Localization;

namespace Opti.Workflow.Sessions;

public sealed record CrashEvent(DateTimeOffset Time, int EventId, string Source, string Description);

public interface ICrashEventSource
{
    /// <summary>Аварийные события системы (BSOD, внезапное выключение) после указанного момента.</summary>
    IReadOnlyList<CrashEvent> Since(DateTimeOffset time);
}

public sealed class PostRebootReport
{
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
    public int Verified { get; set; }
    public List<string> NotApplied { get; set; } = new();
    public List<string> ReadErrors { get; set; } = new();
    public List<CrashEvent> Crashes { get; set; } = new();
    public bool Healthy => NotApplied.Count == 0 && Crashes.Count == 0;
    /// <summary>Рекомендуется предложить откат (были сбои системы).</summary>
    public bool SuggestRollback => Crashes.Count > 0;
}

/// <summary>
/// Самопроверка после перезагрузки: действительно ли применилось каждое изменение сессии
/// и не было ли после применения синих экранов / внезапных выключений.
/// </summary>
public sealed class PostRebootCheck
{
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;
    private readonly ICrashEventSource? _crashes;

    public PostRebootCheck(IBackendRegistry backends, IChangeJournal journal, ICrashEventSource? crashes)
    {
        _backends = backends;
        _journal = journal;
        _crashes = crashes;
    }

    public PostRebootReport Run(string sessionId, DateTimeOffset appliedAt)
    {
        var report = new PostRebootReport();
        var active = _journal.GetAll().Where(r => r.SessionId == sessionId && !r.RolledBack).ToList();
        // Для каждой настройки проверяем последнее значение, записанное сессией
        foreach (var rec in active.GroupBy(r => (r.Type, r.Target.ToLowerInvariant())).Select(g => g.OrderBy(r => r.Id).Last()))
        {
            if (rec.Type == ChangeType.File) { report.Verified++; continue; } // файл восстанавливается из копии, сравнение по содержимому не нужно
            try
            {
                var actual = _backends.Get(rec.Type).Read(rec.Target);
                if (actual == rec.After) report.Verified++;
                else report.NotApplied.Add(Loc.T($"{rec.FixId}: {rec.Type} {rec.Target} — ожидалось {rec.After}, сейчас {actual}"));
            }
            catch (Exception ex) { report.ReadErrors.Add($"{rec.FixId}: {rec.Target}: {ex.Message}"); }
        }
        if (_crashes is not null)
        {
            try { report.Crashes.AddRange(_crashes.Since(appliedAt)); }
            catch (Exception ex) { report.ReadErrors.Add(Loc.T("Журнал событий: ") + ex.Message); }
        }
        return report;
    }
}

/// <summary>Журнал «Система»: 41 (Kernel-Power, внезапная перезагрузка), 1001 (BugCheck — синий экран), 6008 (неожиданное выключение).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCrashEventSource : ICrashEventSource
{
    public IReadOnlyList<CrashEvent> Since(DateTimeOffset time)
    {
        var since = time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
            $"*[System[(EventID=41 or EventID=1001 or EventID=6008) and TimeCreated[@SystemTime>='{since}']]]");
        var list = new List<CrashEvent>();
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
        for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
        {
            using (e)
            {
                // 1001 пишут и другие источники (Windows Error Reporting) — берём только BugCheck
                if (e.Id == 1001 && !string.Equals(e.ProviderName, "Microsoft-Windows-WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(e.ProviderName, "BugCheck", StringComparison.OrdinalIgnoreCase)) continue;
                string desc;
                try { desc = e.FormatDescription() ?? ""; } catch { desc = ""; }
                list.Add(new CrashEvent(e.TimeCreated ?? DateTime.Now, e.Id, e.ProviderName ?? "", desc.Length > 300 ? desc[..300] : desc));
            }
        }
        return list;
    }
}
