using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Catalog;

namespace Opti.Fixes.Backends;

/// <summary>
/// Заполняет бэкенды в памяти значениями из сохранённого профиля — для эмуляции плана
/// и тестов отката на профилях без реального железа.
/// </summary>
public static class ProfileSeeder
{
    public static BackendRegistry CreateEmulation(SystemProfile p, out Dictionary<ChangeType, InMemoryBackend> map)
    {
        var reg = InMemoryBackend.CreateRegistry(out map);
        Seed(p, map);
        return reg;
    }

    public static void Seed(SystemProfile p, IReadOnlyDictionary<ChangeType, InMemoryBackend> map)
    {
        var r = map[ChangeType.Registry];
        var pw = map[ChangeType.PowerSetting];

        foreach (var d in p.Displays)
            map[ChangeType.Display].Seed(Targets.DisplayRefresh(d.DeviceName), StateValue.Str(d.CurrentHz.ToString()));

        if (p.Power.ActiveSchemeGuid is { } scheme)
        {
            pw.Seed(Targets.ActiveScheme, StateValue.Str(scheme));
            if (p.Power.AcMaxProcessorState is { } max)
                pw.Seed(Targets.PowerAc(scheme, PowerGuids.SubProcessor, PowerGuids.ProcThrottleMax), StateValue.Str(max.ToString()));
            if (p.Power.AcCoreParkingMinCores is { } park)
                pw.Seed(Targets.PowerAc(scheme, PowerGuids.SubProcessor, PowerGuids.CpMinCores), StateValue.Str(park.ToString()));
        }
        var overlay = p.Power.PowerModeOverlay switch
        {
            "BetterBattery" => "961cc777-2547-4f9d-8174-7d86181b8a7a",
            "BetterPerformance" => "3af9b8d9-7c97-431d-ad78-34a8bfea439f",
            "BestPerformance" => PowerGuids.OverlayBestPerformance,
            "Balanced" => Guid.Empty.ToString(),
            _ => null,
        };
        if (overlay is not null) pw.Seed(Targets.PowerOverlay, StateValue.Str(overlay));

        if (p.DeliveryOptimizationMode is { } dom) r.Seed(Targets.Reg(DeliveryOptimizationLanFix.Key, "DODownloadMode"), StateValue.DWord((uint)dom));
        if (p.Graphics.OverlayTestMode is { } otm) r.Seed(Targets.Reg(MpoResetFix.Key, "OverlayTestMode"), StateValue.DWord((uint)otm));
        if (p.Graphics.HagsEnabled is { } hags) r.Seed(Targets.Reg(HagsAbFix.Key, "HwSchMode"), StateValue.DWord(hags ? 2u : 1u));
        if (p.Graphics.GameModeEnabled == false) r.Seed(Targets.Reg(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled"), StateValue.DWord(0));
        if (p.Graphics.GameDvrEnabled == true)
        {
            r.Seed(Targets.Reg(@"HKCU\System\GameConfigStore", "GameDVR_Enabled"), StateValue.DWord(1));
            r.Seed(Targets.Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"), StateValue.DWord(1));
            r.Seed(Targets.Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"), StateValue.DWord(1));
        }
        var global = new List<string>();
        if (p.Graphics.WindowedOptimizationsEnabled is { } wo) global.Add($"SwapEffectUpgradeEnable={(wo ? 1 : 0)};");
        if (p.Graphics.VrrOptimizationsEnabled is { } vrr) global.Add($"VRROptimizeEnable={(vrr ? 1 : 0)};");
        if (global.Count > 0) r.Seed(Targets.Reg(GpuPreferenceDgpuFix.Key, WindowedOptimizationsFix.ValueName), StateValue.Str(string.Concat(global)));
        foreach (var g in p.Games)
            foreach (var (exe, pref) in g.ExeGpuPreferences)
                r.Seed(Targets.Reg(GpuPreferenceDgpuFix.Key, exe), StateValue.Str($"GpuPreference={pref};"));
        if (p.Timers.GlobalTimerResolutionRequests == true) r.Seed(Targets.Reg(TimerResolutionGlobalFix.Key, "GlobalTimerResolutionRequests"), StateValue.DWord(1));
        if (p.Power.PowerThrottlingDisabledGlobally == true) r.Seed(Targets.Reg(PowerThrottlingOffFix.Key, "PowerThrottlingOff"), StateValue.DWord(1));
        if (p.Obs is { RunsAsAdmin: true, ExePath: { } obsExe }) r.Seed(Targets.Reg(ObsNoAdminFix.LayersKeyHkcu, obsExe), StateValue.Str("~ RUNASADMIN"));
        foreach (var s in p.Background.StartupItems.Where(s => s.ApprovedKey is not null))
            r.Seed(s.ApprovedKey!, new StateValue(true, "Binary", (s.Enabled ? "02" : "03") + new string('0', 22)));

        if (p.Input.MouseAcceleration is { } accel)
        {
            r.Seed(Targets.Reg(MouseAccelOffFix.Key, "MouseSpeed"), StateValue.Str(accel ? "1" : "0"));
            r.Seed(Targets.Reg(MouseAccelOffFix.Key, "MouseThreshold1"), StateValue.Str(accel ? "6" : "0"));
            r.Seed(Targets.Reg(MouseAccelOffFix.Key, "MouseThreshold2"), StateValue.Str(accel ? "10" : "0"));
        }
        if (p.Input.StickyKeysFlags is { } sk) r.Seed(Targets.Reg(AccessibilityHotkeysOffFix.StickyKey, "Flags"), StateValue.Str(sk.ToString()));
        if (p.Input.FilterKeysFlags is { } fk) r.Seed(Targets.Reg(AccessibilityHotkeysOffFix.FilterKey, "Flags"), StateValue.Str(fk.ToString()));
        if (p.Memory.PageFileDisabled == true) r.Seed(Targets.Reg(PageFileAutoFix.Key, "PagingFiles"), new StateValue(true, "MultiString", ""));

        foreach (var rule in Opti.Core.Registry.RegistryAuditCatalog.All)
            if (p.RegistryAudit.TryGetValue(rule.Id, out var rv) && rv is not null)
                r.Seed(Targets.Reg(rule.Key, rule.ValueName), uint.TryParse(rv, out var dw) ? StateValue.DWord(dw) : StateValue.Str(rv));

        var bcd = map[ChangeType.Bcd];
        if (p.Timers.UsePlatformClock is { } upc) bcd.Seed(Targets.Bcd("useplatformclock"), StateValue.Str(upc ? "Yes" : "No"));
        if (p.Timers.UsePlatformTick is { } upt) bcd.Seed(Targets.Bcd("useplatformtick"), StateValue.Str(upt ? "Yes" : "No"));
        if (p.Timers.DisableDynamicTick is { } ddt) bcd.Seed(Targets.Bcd("disabledynamictick"), StateValue.Str(ddt ? "Yes" : "No"));

        foreach (var n in p.Network.Where(n => n.AllowComputerToTurnOff is not null))
            map[ChangeType.NetAdapter].Seed(Targets.NetAdapter(n.Name, "AllowComputerToTurnOffDevice"), StateValue.Str(n.AllowComputerToTurnOff == true ? "Enabled" : "Disabled"));
    }
}
