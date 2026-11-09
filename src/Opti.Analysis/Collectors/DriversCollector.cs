using System.Runtime.Versioning;
using Opti.Core.Drivers;
using Opti.Core.Profile;
using Opti.Core.Registry;
using Opti.Core.Localization;

namespace Opti.Analysis.Collectors;

/// <summary>Драйверы ключевых устройств, материнская плата и аудит реестра на «твики».</summary>
[SupportedOSPlatform("windows")]
public sealed class DriversCollector : ICollector
{
    public string Name => Loc.T("Драйверы и реестр");

    private static readonly string[] Classes = { "DISPLAY", "NET", "MEDIA", "BLUETOOTH", "SYSTEM", "SCSIADAPTER", "HDC" };

    public void Collect(SystemProfile p)
    {
        try
        {
            var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
            p.Device.BoardManufacturer = board?.Str("Manufacturer");
            p.Device.BoardProduct = board?.Str("Product");
        }
        catch (Exception ex) { p.CollectorErrors.Add(Loc.T("Материнская плата: ") + ex.Message); }

        try
        {
            var where = string.Join(" OR ", Classes.Select(c => $"DeviceClass = '{c}'"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Wmi.Query($"SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, HardWareID FROM Win32_PnPSignedDriver WHERE {where}"))
            {
                var name = d.Str("DeviceName");
                if (string.IsNullOrEmpty(name)) continue;
                var cat = DriverAdvisor.CategoryFromClass(d.Str("DeviceClass"), name);
                if (cat == DriverCategory.Other) continue;
                // Виртуальные адаптеры (VPN, Hyper-V, WAN Miniport) не интересны
                if (cat == DriverCategory.Network && IsVirtualAdapter(name)) continue;
                if (!seen.Add(name + "|" + d.Str("DriverVersion"))) continue;
                p.Drivers.Add(new DriverInfo
                {
                    DeviceName = name, Category = cat, Manufacturer = d.Str("Manufacturer"), Provider = d.Str("DriverProviderName"),
                    Version = d.Str("DriverVersion"), Date = d.Date("DriverDate"), HardwareId = d.Str("HardWareID"),
                });
            }
        }
        catch (Exception ex) { p.CollectorErrors.Add(Loc.T("Драйверы: ") + ex.Message); }

        foreach (var rule in RegistryAuditCatalog.All)
        {
            try { p.RegistryAudit[rule.Id] = Format(Reg.Get(rule.Key, rule.ValueName)); }
            catch (Exception ex) { p.CollectorErrors.Add(Loc.T($"Реестр {rule.Id}: {ex.Message}")); }
        }
    }

    internal static string? Format(object? v) => v switch
    {
        null => null,
        int i => unchecked((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string[] a => string.Join("\0", a),
        byte[] b => Convert.ToHexString(b),
        _ => v.ToString(),
    };

    private static bool IsVirtualAdapter(string n) =>
        n.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) || n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || n.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) || n.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
        || n.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) || n.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) || n.Contains("6to4", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Teredo", StringComparison.OrdinalIgnoreCase) || n.Contains("IP-HTTPS", StringComparison.OrdinalIgnoreCase);
}
