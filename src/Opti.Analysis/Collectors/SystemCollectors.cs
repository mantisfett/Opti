using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Opti.Core.Profile;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Analysis.Collectors;

[SupportedOSPlatform("windows")]
public sealed partial class DiskCollector : ICollector
{
    public string Name => Loc.T("Диски");
    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    public void Collect(SystemProfile p)
    {
        var sysLetter = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\', ':');
        // DriveLetter в MSFT_Partition — char16; сравниваем в коде, а не в WQL
        long? sysDiskNumber = Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", StorageNs)
            .Where(x => x["DriveLetter"] is { } dl && char.ToUpperInvariant(Convert.ToChar(dl)) == char.ToUpperInvariant(sysLetter[0]))
            .Select(x => x.Long("DiskNumber")).FirstOrDefault();

        var wear = ReadWear();
        foreach (var d in Wmi.Query("SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk", StorageNs))
        {
            var id = d.Str("DeviceId");
            var bus = d.Long("BusType");
            var media = d.Long("MediaType");
            var disk = new DiskInfo
            {
                Model = d.Str("FriendlyName"),
                MediaType = bus == 17 ? DiskMediaType.Nvme : bus == 7 ? DiskMediaType.Usb
                          : media == 3 ? DiskMediaType.Hdd : media == 4 ? DiskMediaType.Ssd : DiskMediaType.Unknown,
                SizeGb = (d.Long("Size") ?? 0) / 1e9,
                HealthStatus = d.Long("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null },
                IsSystem = sysDiskNumber is not null && id == sysDiskNumber.ToString(),
                WearPercent = id is not null && wear.TryGetValue(id, out var w) ? w : null,
            };
            if (disk.IsSystem)
            {
                var drive = new DriveInfo(sysLetter);
                disk.DriveLetter = sysLetter + ":";
                disk.SizeGb = drive.TotalSize / 1e9;
                disk.FreeGb = drive.AvailableFreeSpace / 1e9;
            }
            p.Disks.Add(disk);
        }

        var trim = ProcessRunner.Instance.RunConsoleUtf8("fsutil behavior query DisableDeleteNotify");
        var m = TrimRegex().Match(trim.StdOut);
        if (m.Success)
        {
            bool enabled = m.Groups[1].Value == "0";
            foreach (var d in p.Disks.Where(d => d.MediaType is DiskMediaType.Ssd or DiskMediaType.Nvme)) d.TrimEnabled = enabled;
        }
    }

    private static Dictionary<string, int> ReadWear()
    {
        var map = new Dictionary<string, int>();
        try
        {
            var r = ProcessRunner.Instance.RunPowerShell(
                "Get-PhysicalDisk | ForEach-Object { $c = $_ | Get-StorageReliabilityCounter; [pscustomobject]@{Id=$_.DeviceId; Wear=$c.Wear} } | ConvertTo-Json -Compress",
                TimeSpan.FromSeconds(30));
            if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut)) return map;
            using var doc = JsonDocument.Parse(r.StdOut);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
            foreach (var e in items)
                if (e.TryGetProperty("Wear", out var w) && w.ValueKind == JsonValueKind.Number && e.TryGetProperty("Id", out var id))
                    map[id.ToString()] = w.GetInt32();
        }
        catch { /* счётчики надёжности доступны не на всех дисках */ }
        return map;
    }

    [GeneratedRegex(@"NTFS\s+DisableDeleteNotify\s*=\s*(\d)")] private static partial Regex TrimRegex();
}

[SupportedOSPlatform("windows")]
public sealed class ThermalCollector : ICollector
{
    public string Name => Loc.T("Температуры");

    public void Collect(SystemProfile p)
    {
        var sources = new List<string>();
        try
        {
            var zones = Wmi.Query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\WMI");
            var temps = zones.Select(z => z.Long("CurrentTemperature")).Where(t => t > 0).Select(t => t!.Value / 10.0 - 273.15)
                             .Where(c => c is > 10 and < 120).ToList();
            if (temps.Count > 0) { p.Thermals.CpuTempC = temps.Max(); sources.Add(Loc.T("ACPI thermal zone (приблизительно)")); }
        }
        catch { /* часто недоступно */ }

        if (p.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia))
        {
            try
            {
                var r = ProcessRunner.Instance.Run("nvidia-smi", "--query-gpu=temperature.gpu,clocks_throttle_reasons.hw_thermal_slowdown,clocks_throttle_reasons.sw_thermal_slowdown --format=csv,noheader,nounits", TimeSpan.FromSeconds(10));
                if (r.Ok)
                {
                    var parts = r.StdOut.Split('\n')[0].Split(',').Select(s => s.Trim()).ToArray();
                    if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) p.Thermals.GpuTempC = t;
                    if (parts.Length >= 3 && ToolParsers.ParseNvidiaThrottleActive(string.Join(",", parts.Skip(1)))) p.Thermals.ThrottleObserved = true;
                    sources.Add("nvidia-smi");
                }
            }
            catch { /* nvidia-smi не установлен */ }
        }
        p.Thermals.Source = sources.Count > 0 ? string.Join(", ", sources) : null;
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class PowerCollector : ICollector
{
    public string Name => Loc.T("Питание");
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";

    public void Collect(SystemProfile p)
    {
        var (ac, _, pct) = PowerNative.GetStatus();
        p.Power.OnAcPower = ac;
        p.Power.BatteryPercent = p.Device.HasBattery ? pct : null;
        p.Power.PowerModeOverlay = PowerNative.GetOverlayMode();
        p.Power.ModernStandby = PowerNative.GetCapabilities().ModernStandby;

        var active = ProcessRunner.Instance.RunConsoleUtf8("powercfg /getactivescheme").StdOut;
        var m = SchemeRegex().Match(active);
        if (m.Success)
        {
            p.Power.ActiveSchemeGuid = m.Groups[1].Value.ToLowerInvariant();
            p.Power.ActiveSchemeName = m.Groups[2].Success ? m.Groups[2].Value : null;
        }
        var list = ProcessRunner.Instance.RunConsoleUtf8("powercfg /list").StdOut;
        p.Power.AvailableSchemeGuids = SchemeRegex().Matches(list).Select(x => x.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

        if (p.Power.ActiveSchemeGuid is { } scheme)
        {
            p.Power.AcMaxProcessorState = QueryAc(scheme, SubProcessor, "bc5038f7-23e0-4960-96da-33abaf5935ec");
            p.Power.AcCoreParkingMinCores = QueryAc(scheme, SubProcessor, "0cc5b647-c1df-4637-891a-dec35c318583");
        }
        p.Power.PowerThrottlingDisabledGlobally = Reg.Int(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff") == 1;
    }

    private static int? QueryAc(string scheme, string sub, string setting)
    {
        var r = ProcessRunner.Instance.RunConsoleUtf8($"powercfg /query {scheme} {sub} {setting}");
        if (!r.Ok) return null;
        var (acIdx, _) = ToolParsers.ParsePowercfgIndexes(r.StdOut);
        return (int?)acIdx;
    }

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*(?:\(([^)]*)\))?")]
    private static partial Regex SchemeRegex();
}

[SupportedOSPlatform("windows")]
public sealed class TimersAndSecurityCollector : ICollector
{
    public string Name => Loc.T("Таймеры и безопасность");

    public void Collect(SystemProfile p)
    {
        var bcd = ProcessRunner.Instance.RunConsoleUtf8("bcdedit /enum {current}");
        if (bcd.Ok)
        {
            var map = ToolParsers.ParseBcdEnum(bcd.StdOut);
            bool? Flag(string k) => map.TryGetValue(k, out var v) ? v == "Yes" : null;
            p.Timers.UsePlatformClock = Flag("useplatformclock");
            p.Timers.UsePlatformTick = Flag("useplatformtick");
            p.Timers.DisableDynamicTick = Flag("disabledynamictick");
            p.Timers.TscSyncPolicy = map.GetValueOrDefault("tscsyncpolicy");
            p.Security.HypervisorLaunchType = map.GetValueOrDefault("hypervisorlaunchtype");
        }
        else p.CollectorErrors.Add(Loc.T("bcdedit недоступен (нужны права администратора)"));

        p.Timers.GlobalTimerResolutionRequests =
            Reg.Int(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1;

        try
        {
            var dg = Wmi.Query("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault();
            if (dg is not null)
            {
                p.Security.VbsRunning = dg.Long("VirtualizationBasedSecurityStatus") == 2;
                p.Security.HvciRunning = dg["SecurityServicesRunning"] is uint[] running && running.Contains(2u);
            }
        }
        catch (Exception ex) { p.CollectorErrors.Add($"DeviceGuard: {ex.Message}"); }

        try
        {
            var sys = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            var vol = Wmi.Query($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{sys}'", @"root\CIMV2\Security\MicrosoftVolumeEncryption").FirstOrDefault();
            p.Security.BitLockerOnSystemDrive = vol is null ? false : vol.Long("ProtectionStatus") == 1;
        }
        catch (Exception ex) { p.CollectorErrors.Add($"BitLocker: {ex.Message}"); }

        p.Security.SystemRestoreEnabled = Reg.Get(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval") is int rp ? rp != 0 : null;
    }
}

[SupportedOSPlatform("windows")]
public sealed class GraphicsSettingsCollector : ICollector
{
    public string Name => Loc.T("Графические настройки Windows");
    private const string UserGpuPrefs = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";

    public void Collect(SystemProfile p)
    {
        var g = p.Graphics;
        if (g.HagsEnabled is null && Reg.Int(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is { } hw)
            g.HagsEnabled = hw == 2;
        g.OverlayTestMode = Reg.Int(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");

        var global = ParsePrefs(Reg.Str(UserGpuPrefs, "DirectXUserGlobalSettings"));
        g.WindowedOptimizationsEnabled = global.TryGetValue("SwapEffectUpgradeEnable", out var swap) ? swap == "1" : null;
        g.VrrOptimizationsEnabled = global.TryGetValue("VRROptimizeEnable", out var vrr) ? vrr == "1" : null;

        g.GameModeEnabled = Reg.Int(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled") is { } gm ? gm != 0 : true; // по умолчанию включён
        int? dvr = Reg.Int(@"HKCU\System\GameConfigStore", "GameDVR_Enabled");
        int? appCapture = Reg.Int(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        int? historical = Reg.Int(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled");
        g.GameDvrEnabled = dvr != 0 && appCapture != 0 && historical == 1;
    }

    public static Dictionary<string, string> ParsePrefs(string? s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = part.IndexOf('=');
            if (i > 0) d[part[..i]] = part[(i + 1)..];
        }
        return d;
    }

    /// <summary>Все назначения GPU из настроек графики Windows: exe → GpuPreference.</summary>
    public static Dictionary<string, int> ReadExePreferences()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Reg.Values(UserGpuPrefs))
        {
            if (name.Equals("DirectXUserGlobalSettings", StringComparison.OrdinalIgnoreCase)) continue;
            if (ParsePrefs(value?.ToString()).TryGetValue("GpuPreference", out var v) && int.TryParse(v, out var n)) map[name] = n;
        }
        return map;
    }
}
