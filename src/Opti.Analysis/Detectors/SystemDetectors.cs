using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Analysis.Detectors;

public static class PowerSchemes
{
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static bool Is(string? guid, string scheme) => string.Equals(guid?.Trim('{', '}'), scheme, StringComparison.OrdinalIgnoreCase);
}

public sealed class PowerPlanDetector : DetectorBase
{
    public override string Id => "POWER_BATTERY_PLAN";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var pw = p.Power;
        if (pw.OnAcPower != true) yield break;
        var ev = new List<string>();
        if (PowerSchemes.Is(pw.ActiveSchemeGuid, PowerSchemes.PowerSaver))
            ev.Add(Loc.T($"Активная схема: {pw.ActiveSchemeName ?? "Экономия энергии"}"));
        if (pw.AcMaxProcessorState is < 100)
            ev.Add(Loc.T($"Максимальное состояние процессора от сети: {pw.AcMaxProcessorState} %"));
        if (pw.AcCoreParkingMinCores is < 100 && !p.Device.IsLaptop && PowerSchemes.Is(pw.ActiveSchemeGuid, PowerSchemes.Balanced))
            ev.Add(Loc.T($"Сбалансированная схема с парковкой ядер (минимум {pw.AcCoreParkingMinCores} % ядер)"));
        if (string.Equals(pw.PowerModeOverlay, "BetterBattery", StringComparison.OrdinalIgnoreCase))
            ev.Add(Loc.T("Режим питания Windows: «Экономия заряда» / «Эффективность»"));
        if (ev.Count == 0) yield break;

        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Компьютер от сети, но питание настроено на экономию"),
            Severity = Severity.Important,
            Explanation = Loc.T("Схема питания ограничивает частоту процессора или паркует ядра, хотя компьютер подключён к сети. " +
                          "Из-за этого процессор медленнее «просыпается» под нагрузкой, и в играх появляются провалы кадров. " +
                          "Исправление меняет только параметры для работы от сети — режим батареи не трогается."),
            Evidence = ev,
            FixIds = { FixIds.PowerSchemePerf, FixIds.PowerAcTuning },
        };
        if (p.Device.IsLaptop) f.FixIds.Add(FixIds.PowerThrottlingOff);
        yield return f;
    }
}

public sealed class BcdRiskyTimersDetector : DetectorBase
{
    public override string Id => "BCD_RISKY_TIMERS";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var t = p.Timers;
        var ev = new List<string>();
        if (t.UsePlatformClock == true) ev.Add(Loc.T("bcdedit: useplatformclock = Yes (принудительный HPET)"));
        if (t.UsePlatformTick == true) ev.Add("bcdedit: useplatformtick = Yes");
        if (ev.Count == 0) yield break;
        if (t.DisableDynamicTick == true) ev.Add(Loc.T("bcdedit: disabledynamictick = Yes (оставляем, проверим замером)"));
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Нестандартные настройки системного таймера"),
            Severity = Severity.Important,
            Explanation = Loc.T("Кто-то (твикер, гайд или сборка) изменил источник системного таймера. Принудительный HPET увеличивает " +
                          "стоимость получения времени, что на многих современных системах снижает FPS и ухудшает фреймтайм. " +
                          "Программа вернёт стандартные значения и проверит результат замером."),
            Evidence = ev,
            FixIds = { FixIds.BcdTimersReset },
        };
    }
}

public sealed class MpoKeyDetector : DetectorBase
{
    public override string Id => "MPO_KEY_SET";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Graphics.OverlayTestMode is not { } v || !p.Os.IsWin11_24H2OrLater) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Задан ключ отключения MPO (OverlayTestMode)"),
            Severity = Severity.Important,
            Explanation = Loc.T("Старый «фикс мерцания» через OverlayTestMode на Windows 11 24H2 и новее не работает как раньше и может мешать " +
                          "режиму Independent Flip — а это лишняя задержка в оконных играх. Рекомендуется вернуть стандарт."),
            Evidence = { $@"HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = {v}", Loc.T($"Сборка Windows: {p.Os.Build}") },
            FixIds = { FixIds.MpoReset },
        };
    }
}

public sealed class HvciDetector : DetectorBase
{
    public override string Id => "HVCI_ON";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Security.HvciRunning != true) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Включена «Целостность памяти» (Memory Integrity / HVCI)"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Это защита ядра Windows от вредоносных драйверов. На части систем она стоит несколько процентов производительности " +
                          "(больше всего — на старых процессорах). Это компромисс между безопасностью и скоростью: программа её не отключает. " +
                          "Некоторые античиты (например, Vanguard, FACEIT) требуют, чтобы она была включена."),
            Evidence = { Loc.T("Win32_DeviceGuard: HVCI работает") },
            ManualSteps = { Loc.T("Если решите отключить: «Безопасность Windows» → «Безопасность устройства» → «Изоляция ядра» → «Целостность памяти». Требуется перезагрузка.") },
            Links = { Sources.HvciGaming },
        };
    }
}

public sealed class DpcLatencyDetector : DetectorBase
{
    public override string Id => "DPC_LATENCY_HIGH";
    public const double ThresholdUs = 1000;

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Dpc is null) yield break;
        var bad = p.Dpc.Drivers.Where(d => d.MaxDpcUs > ThresholdUs || d.MaxIsrUs > ThresholdUs)
            .OrderByDescending(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs)).ToList();
        if (bad.Count == 0) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Драйвер с большой задержкой обработки прерываний"),
            Severity = Severity.Critical,
            Explanation = Loc.T("Один или несколько драйверов надолго занимают процессор на уровне ядра (DPC/ISR дольше 1 мс). " +
                          "В это время не обрабатываются ввод, звук и кадры — отсюда микрофризы, треск звука и «ватная» мышь. " +
                          "Обычно помогает обновление драйвера этого устройства (сеть, Wi-Fi, звук, ACPI, видео) или его отключение, если оно не нужно."),
            Evidence = bad.Take(5).Select(d => Loc.T($"{d.Driver}: DPC до {d.MaxDpcUs:0} мкс, ISR до {d.MaxIsrUs:0} мкс")).ToList(),
            ManualSteps =
            {
                Loc.T("Обновите драйвер указанного устройства с сайта производителя (ноутбука или устройства)."),
                Loc.T("Если устройство не нужно (например, второй сетевой адаптер), отключите его в «Диспетчере устройств» и повторите тест задержки."),
            },
        };
    }
}

public sealed class StartupHeavyDetector : DetectorBase
{
    public override string Id => "STARTUP_HEAVY";
    public const int MaxItems = 10;

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var enabled = p.Background.StartupItems.Where(i => i.Enabled).ToList();
        int high = enabled.Count(i => i.Impact == StartupImpact.High);
        if (enabled.Count <= MaxItems && high < 3) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Много программ в автозагрузке"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Каждая программа в автозагрузке занимает память и иногда процессор в фоне. Выберите, что не нужно запускать вместе с Windows — " +
                          "программа отключит только отмеченные пункты (так же, как «Диспетчер задач»), их можно вернуть в любой момент."),
            Evidence = enabled.OrderByDescending(i => i.Impact).Take(20).Select(i => $"{i.Name}" + (i.Impact > StartupImpact.None ? Loc.T($" (влияние: {i.Impact})") : "")).ToList(),
            FixIds = { FixIds.StartupDisable },
        };
    }
}

public sealed class GameSettingsDetector : DetectorBase
{
    public override string Id => "WIN_GAME_SETTINGS";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var g = p.Graphics;
        if (g.GameModeEnabled == false)
            yield return new Finding
            {
                Id = "GAME_MODE_OFF",
                Title = Loc.T("Игровой режим Windows выключен"),
                Severity = Severity.Advice,
                Explanation = Loc.T("Игровой режим откладывает установку обновлений и фоновое обслуживание во время игры и отдаёт игре приоритет."),
                Evidence = { @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 0" },
                FixIds = { FixIds.GameModeOn },
            };
        if (g.GameDvrEnabled == true)
            yield return new Finding
            {
                Id = "GAME_DVR_ON",
                Title = Loc.T("Включена фоновая запись Xbox Game Bar"),
                Severity = Severity.Advice,
                Explanation = Loc.T("Фоновая запись игрового процесса постоянно кодирует видео, нагружая GPU и диск. Если вы не пользуетесь «Записать последние 30 секунд», её стоит выключить."),
                Evidence = { @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled = 1" },
                FixIds = { FixIds.GameDvrOff },
            };
        if (p.Os.IsWindows11 && (g.WindowedOptimizationsEnabled != true || g.VrrOptimizationsEnabled != true))
            yield return new Finding
            {
                Id = "WINDOWED_OPT_OFF",
                Title = Loc.T("Не включена оптимизация для игр в окне"),
                Severity = Severity.Advice,
                Explanation = Loc.T("В Windows 11 игры DirectX 10/11 в окне или «окне без рамки» могут работать в режиме Flip, как в полноэкранном: " +
                              "меньше задержка и работает VRR (G-Sync/FreeSync) в оконном режиме."),
                Evidence =
                {
                    Loc.T($"Оптимизация оконных игр: {(g.WindowedOptimizationsEnabled is null ? "по умолчанию" : g.WindowedOptimizationsEnabled.Value ? "вкл" : "выкл")}"),
                    Loc.T($"Оптимизация VRR: {(g.VrrOptimizationsEnabled is null ? "по умолчанию" : g.VrrOptimizationsEnabled.Value ? "вкл" : "выкл")}"),
                },
                FixIds = { FixIds.WindowedOptimizations },
                Links = { Sources.WindowedGames },
            };
    }
}

public sealed class HagsAbCandidateDetector : DetectorBase
{
    public override string Id => "HAGS_AB_CANDIDATE";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Graphics.HagsSupported != true) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Аппаратное планирование GPU (HAGS) можно проверить замером"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Влияние HAGS зависит от видеокарты, драйвера и игры: где-то снижает задержку, где-то даёт фризы. " +
                          "Единого правильного значения нет, поэтому программа проверит оба варианта A/B-замером и оставит лучший."),
            Evidence = { Loc.T($"HAGS сейчас: {(p.Graphics.HagsEnabled == true ? "включено" : p.Graphics.HagsEnabled == false ? "выключено" : "неизвестно")}") },
            FixIds = { FixIds.HagsAb },
            Links = { Sources.Hags },
        };
    }
}

public sealed class TimerResolutionAbCandidateDetector : DetectorBase
{
    public override string Id => "TIMER_RES_AB_CANDIDATE";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (!p.Os.IsWindows11 || p.Timers.GlobalTimerResolutionRequests == true) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Разрешение таймера для свёрнутых игр можно проверить замером"),
            Severity = Severity.Advice,
            Explanation = Loc.T("В Windows 11 запрос точного таймера от игры перестаёт действовать, когда окно не на переднем плане (например, игра на втором экране " +
                          "или поверх неё оверлей). Ключ GlobalTimerResolutionRequests возвращает старое поведение. Эффект зависит от игры — проверяется A/B-замером."),
            Evidence = { Loc.T("GlobalTimerResolutionRequests не задан") },
            FixIds = { FixIds.TimerResGlobal },
        };
    }
}
