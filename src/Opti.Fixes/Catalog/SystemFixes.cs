using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Core.Localization;

namespace Opti.Fixes.Catalog;

public static class PowerGuids
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    public const string ProcThrottleMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    public const string CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

    public const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>Режим питания Windows «Максимальная производительность».</summary>
    public const string OverlayBestPerformance = "ded574b5-45a0-4f42-8737-46345c09c238";

    public const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    public const string WirelessPowerSaving = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
}

/// <summary>Только для ПК: переключает активную схему на «Высокая производительность» (или «Максимальная», если она уже есть).</summary>
public sealed class PowerSchemePerfFix : FixBase
{
    public override string Id => FixIds.PowerSchemePerf;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Схема питания «Высокая производительность»");
    public override string Explanation => Loc.T("Процессор быстрее выходит на высокие частоты и не паркует ядра. Только для настольных ПК; на ноутбуке меняются лишь параметры от сети.");
    public override FixCategory Category => FixCategory.Power;
    public override int Priority => -10; // раньше POWER_AC_TUNING, чтобы тонкая настройка применялась к новой схеме

    public static string? PickScheme(SystemProfile p) =>
        p.Power.AvailableSchemeGuids.FirstOrDefault(g => Eq(g, PowerGuids.UltimatePerformance))
        ?? p.Power.AvailableSchemeGuids.FirstOrDefault(g => Eq(g, PowerGuids.HighPerformance));

    public override bool IsApplicable(SystemProfile p) => !p.Device.IsLaptop && p.Power.OnAcPower != false && PickScheme(p) is not null;
    public override bool IsAlreadyApplied(SystemProfile p) =>
        Eq(p.Power.ActiveSchemeGuid, PowerGuids.HighPerformance) || Eq(p.Power.ActiveSchemeGuid, PowerGuids.UltimatePerformance);

    protected override void Execute(ChangeContext ctx)
    {
        var scheme = PickScheme(ctx.Profile) ?? throw new ChangeAbortedException(Loc.T("Схема высокой производительности не найдена"));
        ctx.Set(ChangeType.PowerSetting, Targets.ActiveScheme, StateValue.Str(scheme.Trim('{', '}').ToLowerInvariant()));
    }

    internal static bool Eq(string? a, string b) => string.Equals(a?.Trim('{', '}'), b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Параметры активной схемы только для работы от сети: без ограничения частоты, без энергосбережения USB/PCIe/Wi-Fi.</summary>
public sealed class PowerAcTuningFix : FixBase
{
    public override string Id => FixIds.PowerAcTuning;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Питание от сети: без энергосберегающих ограничений");
    public override string Explanation => Loc.T("Для режима «от сети»: режим питания Windows «Максимальная производительность» (если стоит «Экономия заряда»), ") +
                                          Loc.T("максимальное состояние процессора 100 %, без парковки ядер (только ПК), выключено выборочное отключение USB, ") +
                                          Loc.T("энергосбережение PCIe (ASPM) и Wi-Fi. Параметры батареи не меняются.");
    public override FixCategory Category => FixCategory.Power;

    public override bool IsApplicable(SystemProfile p) => p.Power.OnAcPower != false;
    public override bool IsAlreadyApplied(SystemProfile p) =>
        p.Power.AcMaxProcessorState is 100 && (p.Device.IsLaptop || p.Power.AcCoreParkingMinCores is 100)
        && !string.Equals(p.Power.PowerModeOverlay, "BetterBattery", StringComparison.OrdinalIgnoreCase);

    protected override void Execute(ChangeContext ctx)
    {
        if (ctx.Profile.Power.OnAcPower == true && string.Equals(ctx.Profile.Power.PowerModeOverlay, "BetterBattery", StringComparison.OrdinalIgnoreCase))
            ctx.Set(ChangeType.PowerSetting, Targets.PowerOverlay, StateValue.Str(PowerGuids.OverlayBestPerformance));

        var scheme = ctx.Read(ChangeType.PowerSetting, Targets.ActiveScheme).Value
                     ?? throw new ChangeAbortedException(Loc.T("Не удалось определить активную схему питания"));
        void Set(string sub, string setting, uint v) =>
            ctx.Set(ChangeType.PowerSetting, Targets.PowerAc(scheme, sub, setting), StateValue.Str(v.ToString()));

        Set(PowerGuids.SubProcessor, PowerGuids.ProcThrottleMax, 100);
        // На ноутбуках с гибридными ядрами (P/E) парковкой управляет Thread Director — не трогаем.
        if (!ctx.Profile.Device.IsLaptop) Set(PowerGuids.SubProcessor, PowerGuids.CpMinCores, 100);
        Set(PowerGuids.SubUsb, PowerGuids.UsbSelectiveSuspend, 0);
        Set(PowerGuids.SubPciExpress, PowerGuids.PcieAspm, 0);
        Set(PowerGuids.SubWireless, PowerGuids.WirelessPowerSaving, 0);
    }
}

public sealed class PowerThrottlingOffFix : FixBase
{
    public const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
    public override string Id => FixIds.PowerThrottlingOff;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Отключить Power Throttling");
    public override string Explanation => Loc.T("Power Throttling переводит «фоновые» процессы на энергоэффективные режимы. Иногда под него попадают лаунчеры, OBS и сама игра в окне. ") +
                                          Loc.T("Отключение глобальное — точечное отключение по процессу Windows не поддерживает постоянно.");
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override bool RequiresReboot => true;
    public override FixCategory Category => FixCategory.Power;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Power.PowerThrottlingDisabledGlobally == true;
    protected override void Execute(ChangeContext ctx) => SetDword(ctx, Key, "PowerThrottlingOff", 1);
}

public sealed class BcdTimersResetFix : FixBase
{
    public override string Id => FixIds.BcdTimersReset;
    public override string Title => Loc.T("Вернуть стандартные настройки системного таймера (BCD)");
    public override string Explanation => Loc.T("Удаляет useplatformclock и useplatformtick из конфигурации загрузки — Windows сама выберет оптимальный таймер. Результат проверяется замером.");
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override bool RequiresReboot => true;
    public override bool TouchesBcd => true;
    public override FixCategory Category => FixCategory.Timers;

    public override bool IsAlreadyApplied(SystemProfile p) => p.Timers.UsePlatformClock != true && p.Timers.UsePlatformTick != true;

    protected override void Execute(ChangeContext ctx)
    {
        if (ctx.Profile.Security.BitLockerOnSystemDrive == true && !ctx.Options.ContainsKey("bitlockerAcknowledged"))
            throw new ChangeAbortedException(Loc.T("Включён BitLocker: подтвердите, что ключ восстановления под рукой (опция bitlockerAcknowledged)."));
        foreach (var el in new[] { "useplatformclock", "useplatformtick" })
        {
            var t = Targets.Bcd(el);
            if (ctx.Read(ChangeType.Bcd, t).Exists) ctx.Delete(ChangeType.Bcd, t);
        }
    }
}

public sealed class TimerResolutionGlobalFix : FixBase
{
    public const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    public override string Id => FixIds.TimerResGlobal;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Глобальные запросы разрешения таймера (Windows 11)");
    public override string Explanation => Loc.T("Возвращает поведение Windows 10: запрос точного таймера от игры действует, даже когда её окно не на переднем плане. Оставляется только при улучшении по замеру.");
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override bool RequiresReboot => true;
    public override bool RequiresAbTest => true;
    public override FixCategory Category => FixCategory.Timers;
    public override bool IsApplicable(SystemProfile p) => p.Os.IsWindows11;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Timers.GlobalTimerResolutionRequests == true;
    protected override void Execute(ChangeContext ctx) => SetDword(ctx, Key, "GlobalTimerResolutionRequests", 1);
}

public sealed class StartupDisableFix : FixBase
{
    public override string Id => FixIds.StartupDisable;
    public override FixIntent Intent => FixIntent.Preference;
    public override string Title => Loc.T("Отключить выбранные программы в автозагрузке");
    public override string Explanation => Loc.T("Отключает только отмеченные вами пункты тем же способом, что и «Диспетчер задач». Программы не удаляются; включить обратно можно в любой момент.");
    public override FixCategory Category => FixCategory.Background;
    public override bool NeedsUserChoice => true;

    public override bool IsApplicable(SystemProfile p) => p.Background.StartupItems.Any(i => i.Enabled && i.ApprovedKey is not null);
    public override bool IsAlreadyApplied(SystemProfile p) => false;

    /// <summary>Опция «items»: имена пунктов через перевод строки.</summary>
    protected override void Execute(ChangeContext ctx)
    {
        if (!ctx.Options.TryGetValue("items", out var raw)) return;
        var names = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 03 00 00 00 + FILETIME — так помечает отключённые пункты Диспетчер задач
        var disabled = "03000000" + Convert.ToHexString(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        foreach (var item in ctx.Profile.Background.StartupItems.Where(i => i.Enabled && i.ApprovedKey is not null && names.Contains(i.Name)))
            ctx.Set(ChangeType.Registry, item.ApprovedKey!, new StateValue(true, "Binary", disabled));
    }
}

public sealed class NetAdapterPowerOffFix : FixBase
{
    public override string Id => FixIds.NetAdapterPowerOff;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Запретить отключение сетевого адаптера для экономии энергии");
    public override string Explanation => Loc.T("Снимает «Разрешить отключение этого устройства для экономии энергии» у активных сетевых адаптеров.");
    public override FixCategory Category => FixCategory.Network;
    public override bool IsAlreadyApplied(SystemProfile p) => !p.Network.Any(n => n.IsUp && n.AllowComputerToTurnOff == true);
    protected override void Execute(ChangeContext ctx)
    {
        foreach (var a in ctx.Profile.Network.Where(n => n.IsUp && n.AllowComputerToTurnOff == true))
            ctx.Set(ChangeType.NetAdapter, Targets.NetAdapter(a.Name, "AllowComputerToTurnOffDevice"), StateValue.Str("Disabled"));
    }
}
