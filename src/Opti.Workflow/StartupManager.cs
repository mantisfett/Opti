using Opti.Core.Fixes;
using Opti.Core.Localization;
using Opti.Core.Profile;
using Opti.Fixes.Services;

namespace Opti.Workflow;

/// <summary>Что Opti советует делать с пунктом автозагрузки.</summary>
public enum StartupAdvice
{
    /// <summary>Защита, драйверы устройств, сам Opti: оставить, переключатель заблокирован.</summary>
    Keep,
    /// <summary>Решать вам: нужна ли программа сразу после входа.</summary>
    Optional,
    /// <summary>Обычно не нужна при старте (магазины игр, мессенджеры, облака, обновлялки): можно отключить.</summary>
    Suggest,
}

public sealed record StartupEntry(string Name, string Command, string Source, string ApprovedKey, bool Enabled, BackgroundCategory Category, StartupAdvice Advice)
{
    public bool CanToggle => Advice != StartupAdvice.Keep;
}

/// <summary>
/// Менеджер автозагрузки. Состояние — значения StartupApproved (так же, как у «Диспетчера задач»), поэтому программа не удаляется,
/// ярлыки и ключи Run не трогаются, и пункт можно вернуть в любой момент. Каждое изменение пишется в журнал и откатывается в «Истории».
/// Время загрузки не измеряется: «влияние» из Диспетчера задач берётся из системной телеметрии, которой у Opti нет.
/// </summary>
public sealed class StartupManager
{
    public const string SessionId = "startup";
    public const string ManagerFixId = "STARTUP_MANAGER";

    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;
    private readonly Func<IReadOnlyList<StartupItem>> _reader;

    public StartupManager(IBackendRegistry backends, IChangeJournal journal, Func<IReadOnlyList<StartupItem>> reader)
    {
        _backends = backends;
        _journal = journal;
        _reader = reader;
    }

    private static readonly string[] KeepMarkers =
    {
        "securityhealth", "defender", "windows security", "antivirus", "kaspersky", "bitdefender", "norton", "mcafee", "malwarebytes",
        "sophos", "realtek", "rtkaud", "synaptics", "touchpad", "trackpad", "bluetooth",
    };
    private static readonly string[] KeepTokens = { "eset", "avast", "avg", "elan", "opti", "audio" };

    public static string ExeName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var c = command.Trim();
        string path;
        if (c.StartsWith('"')) { var end = c.IndexOf('"', 1); path = end > 0 ? c[1..end] : c[1..]; }
        else { var i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); path = i >= 0 ? c[..(i + 4)] : c.Split(' ')[0]; }
        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    public static StartupAdvice Advise(string name, string? command, out BackgroundCategory category)
    {
        var exe = Path.GetFileNameWithoutExtension(ExeName(command));
        category = BackgroundScanner.Classify(name);
        if (category == BackgroundCategory.Other && exe.Length > 0) category = BackgroundScanner.Classify(exe);

        var hay = (name + " " + exe).ToLowerInvariant();
        var tokens = hay.Split(new[] { ' ', '-', '_', '.', '(', ')', '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (KeepMarkers.Any(hay.Contains) || KeepTokens.Any(tokens.Contains)) return StartupAdvice.Keep;
        return category is BackgroundCategory.Vendor or BackgroundCategory.Other ? StartupAdvice.Optional : StartupAdvice.Suggest;
    }

    private static string SourceText(string? location)
    {
        var l = location ?? "";
        if (l.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) return Loc.T("реестр, этот пользователь");
        if (l.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)) return Loc.T("реестр, все пользователи (32-бит)");
        if (l.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)) return Loc.T("реестр, все пользователи");
        return Loc.T("папка «Автозагрузка»");
    }

    /// <summary>Флаг StartupApproved: нечётный первый байт — отключено; нет значения — включено.</summary>
    public static bool IsEnabledValue(StateValue v)
    {
        if (!v.Exists || string.IsNullOrEmpty(v.Value) || v.Value.Length < 2) return true;
        return (Convert.ToByte(v.Value[..2], 16) & 1) == 0;
    }

    public static StateValue EnabledValue => new(true, "Binary", "02" + new string('0', 22));
    public static StateValue DisabledValue() => new(true, "Binary", "03000000" + Convert.ToHexString(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc())));

    /// <summary>Текущий список: пункты — из системы, включено или нет — из реестра. Сначала то, что можно отключить.</summary>
    public IReadOnlyList<StartupEntry> Scan()
    {
        var reg = _backends.Get(ChangeType.Registry);
        var list = new List<StartupEntry>();
        foreach (var it in _reader().Where(i => i.ApprovedKey is not null))
        {
            bool enabled;
            try { enabled = IsEnabledValue(reg.Read(it.ApprovedKey!)); } catch { enabled = it.Enabled; }
            var advice = Advise(it.Name, it.Command, out var cat);
            list.Add(new StartupEntry(it.Name, it.Command ?? "", SourceText(it.Location), it.ApprovedKey!, enabled, cat, advice));
        }
        return list.OrderBy(e => e.Advice == StartupAdvice.Suggest ? 0 : e.Advice == StartupAdvice.Optional ? 1 : 2)
                   .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Включает или отключает пункт. Ошибка — исключение с понятным текстом.</summary>
    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (!entry.CanToggle) throw new InvalidOperationException(Loc.T("Этот пункт защищён: его лучше не отключать."));
        if (!_journal.GetSessions().Any(s => s.SessionId == SessionId))
            _journal.SaveSession(new SessionInfo { SessionId = SessionId, Goal = Loc.T("Автозагрузка"), Notes = Loc.T("Менеджер автозагрузки") });
        var reg = _backends.Get(ChangeType.Registry);
        var ctx = new ChangeContext(_backends, _journal, SessionId, ManagerFixId, new SystemProfile());
        if (!enabled)
        {
            ctx.Set(ChangeType.Registry, entry.ApprovedKey, DisabledValue());
            return;
        }
        // Включаем: если отключали мы (или исправление «Автозагрузка») — откат возвращает то, что было до Opti
        var active = _journal.GetAll().Where(r => !r.RolledBack && r.Type == ChangeType.Registry
            && string.Equals(r.Target, entry.ApprovedKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count > 0)
        {
            var report = new RollbackService(_backends, _journal).RollbackRecords(active, force: true);
            if (report.Errors.Count > 0) throw new InvalidOperationException(string.Join("; ", report.Errors));
        }
        // Было отключено ещё до Opti (или откат вернул «отключено») — включаем явно
        if (!IsEnabledValue(reg.Read(entry.ApprovedKey)))
            ctx.Set(ChangeType.Registry, entry.ApprovedKey, EnabledValue);
    }
}
