using System.Runtime.Versioning;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Analysis.Collectors;

/// <summary>Всё, что важно после переустановки Windows: драйверы, устройства, ввод, файл подкачки, ожидающая перезагрузка.</summary>
[SupportedOSPlatform("windows")]
public sealed class ExtrasCollector : ICollector
{
    public string Name => Loc.T("Драйверы и настройки ввода");
    private const string Cv = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public void Collect(SystemProfile p)
    {
        if (Reg.Get(Cv, "InstallDate") is int unix && unix > 0)
            p.Os.InstallDate = DateTimeOffset.FromUnixTimeSeconds((uint)unix).LocalDateTime;
        p.Os.PendingReboot = Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                             || Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");

        try
        {
            foreach (var d in Wmi.Query("SELECT Name, PNPClass, ConfigManagerErrorCode, PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
            {
                int code = (int)(d.Long("ConfigManagerErrorCode") ?? 0);
                if (code is 0 or 45) continue; // 45 — устройство сейчас не подключено
                p.ProblemDevices.Add(new ProblemDevice { Name = d.Str("Name") ?? d.Str("PNPDeviceID") ?? "?", DeviceClass = d.Str("PNPClass"), ErrorCode = code, PnpDeviceId = d.Str("PNPDeviceID") });
            }
        }
        catch (Exception ex) { p.CollectorErrors.Add(Loc.T("Устройства: ") + ex.Message); }

        if (p.Cpu.Vendor == CpuVendor.Amd)
        {
            var amd = Wmi.Query("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%PPM Provisioning%' OR Name LIKE '%3D V-Cache%'").Select(x => x.Str("Name") ?? "").ToList();
            p.Chipset.AmdPpmDriver = amd.Any(n => n.Contains("PPM Provisioning", StringComparison.OrdinalIgnoreCase));
            p.Chipset.AmdVCacheOptimizer = amd.Any(n => n.Contains("3D V-Cache", StringComparison.OrdinalIgnoreCase));
        }

        if (Reg.Str(@"HKCU\Control Panel\Mouse", "MouseSpeed") is { } ms) p.Input.MouseAcceleration = ms.Trim() != "0";
        if (int.TryParse(Reg.Str(@"HKCU\Control Panel\Mouse", "MouseSensitivity"), out var speed) && speed is >= 1 and <= 20) p.Input.MousePointerSpeed = speed;
        if (int.TryParse(Reg.Str(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags"), out var sk)) p.Input.StickyKeysFlags = sk;
        if (int.TryParse(Reg.Str(@"HKCU\Control Panel\Accessibility\Keyboard Response", "Flags"), out var fk)) p.Input.FilterKeysFlags = fk;

        try
        {
            var cs = Wmi.Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").FirstOrDefault();
            bool auto = cs?.Bool("AutomaticManagedPagefile") == true;
            int settings = Wmi.Query("SELECT Name FROM Win32_PageFileSetting").Count;
            int usage = Wmi.Query("SELECT Name FROM Win32_PageFileUsage").Count;
            p.Memory.PageFileDisabled = !auto && settings == 0 && usage == 0;
        }
        catch (Exception ex) { p.CollectorErrors.Add(Loc.T("Файл подкачки: ") + ex.Message); }
    }
}
