using Opti.Core.Fixes;
using Opti.Core.Localization;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Services;

namespace Opti.Workflow;

public sealed record GamePriorityResult(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Invalid, IReadOnlyList<string> Errors);

/// <summary>
/// Высокий приоритет процессов выбранных игр. Используется штатный механизм Windows: значение CpuPriorityClass (3 = «Высокий»)
/// в разделе Image File Execution Options\&lt;игра&gt;.exe\PerfOptions. Windows сама выставляет приоритет при запуске игры;
/// отдельной программы-«регулятора» не нужно. Запись идёт через журнал и откатывается; в «Страже» проверяется как обычная настройка.
/// Эффект зависит от игры (Windows и так поднимает приоритет активного окна) — проверяйте замером «до / после».
/// </summary>
public sealed class GamePriority
{
    public const string SessionId = "game-priority";
    public const string FixId = "GAME_PRIORITY";
    private const string Ifeo = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Suffix = @".exe\PerfOptions";
    private const string ValueName = "CpuPriorityClass";

    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;

    public GamePriority(IBackendRegistry backends, IChangeJournal journal)
    {
        _backends = backends;
        _journal = journal;
    }

    /// <summary>Имя попадает в путь реестра, поэтому допускаются только буквы, цифры, точка, дефис, подчёркивание и пробел; без «..» и без обратной косой черты.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 64 && !name.Contains("..") && !name.StartsWith(' ') && !name.EndsWith(' ')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ');

    public static string Target(string name) => Targets.Reg(Ifeo + "\\" + name + Suffix, ValueName);

    private IEnumerable<ChangeRecord> ActiveRecords() =>
        _journal.GetAll().Where(r => !r.RolledBack && r.FixId == FixId && r.Type == ChangeType.Registry);

    private static string NameOf(ChangeRecord r)
    {
        var path = Targets.Split2(r.Target).Left;
        var start = Ifeo.Length + 1;
        return path[start..^Suffix.Length];
    }

    /// <summary>Игры, для которых Opti сейчас держит высокий приоритет.</summary>
    public IReadOnlyList<string> Active() =>
        ActiveRecords().Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Приводит систему к списку: добавляет приоритет новым играм, снимает у убранных из списка (или у всех, если выключено).</summary>
    public GamePriorityResult Sync(IEnumerable<string> names, bool enabled)
    {
        var invalid = new List<string>();
        var desired = new List<string>();
        if (enabled)
            foreach (var n in names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                (IsValidName(n) ? desired : invalid).Add(n);

        var errors = new List<string>();
        var removed = new List<string>();
        var added = new List<string>();
        var desiredSet = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = ActiveRecords().Where(r => !desiredSet.Contains(NameOf(r))).ToList();
        if (stale.Count > 0)
        {
            var report = new RollbackService(_backends, _journal).RollbackRecords(stale);
            removed.AddRange(report.Restored.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase));
            errors.AddRange(report.Errors);
        }

        if (desired.Count > 0)
        {
            if (!_journal.GetSessions().Any(s => s.SessionId == SessionId))
                _journal.SaveSession(new SessionInfo { SessionId = SessionId, Goal = Loc.T("Приоритет игр"), Notes = Loc.T("Высокий приоритет процессов игр") });
            var ctx = new ChangeContext(_backends, _journal, SessionId, FixId, new SystemProfile());
            foreach (var n in desired)
            {
                try { if (ctx.Set(ChangeType.Registry, Target(n), StateValue.DWord(3)) is not null) added.Add(n); }
                catch (Exception ex) { errors.Add($"{n}: {ex.Message}"); }
            }
        }
        return new GamePriorityResult(added, removed, invalid, errors);
    }
}
