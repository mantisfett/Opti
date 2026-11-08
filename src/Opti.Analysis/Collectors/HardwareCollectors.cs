using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Opti.Core.Profile;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Analysis.Collectors;

[SupportedOSPlatform("windows")]
public sealed class DeviceCollector : ICollector
{
    public string Name => Loc.T("Устройство");
    private static readonly int[] LaptopChassis = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };

    public void Collect(SystemProfile p)
    {
        var cs = Wmi.Query("SELECT Manufacturer, Model, PCSystemType, HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        p.Device.Manufacturer = cs?.Str("Manufacturer");
        p.Device.Model = cs?.Str("Model");
        p.Security.HypervisorPresent = cs?.Bool("HypervisorPresent");

        var chassis = Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
        bool chassisLaptop = chassis?["ChassisTypes"] is ushort[] types && types.Any(t => LaptopChassis.Contains(t));
        bool mobile = cs?.Long("PCSystemType") == 2;
        var (lid, _) = PowerNative.GetCapabilities();
        var (_, hasBattery, _) = PowerNative.GetStatus();
        p.Device.HasBattery = hasBattery || Wmi.Query("SELECT Name FROM Win32_Battery").Count > 0;
        p.Device.IsLaptop = chassisLaptop || mobile || (lid == true && p.Device.HasBattery);

        var bios = Wmi.Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        p.Device.BiosVersion = bios?.Str("SMBIOSBIOSVersion");
        p.Device.BiosDate = bios?.Date("ReleaseDate");

        var id = $"{p.Device.Manufacturer} {p.Device.Model}".ToLowerInvariant();
        p.Device.IsVirtualMachine = id.Contains("virtual") || id.Contains("vmware") || id.Contains("qemu") || id.Contains("virtualbox") || id.Contains("kvm");
    }
}

[SupportedOSPlatform("windows")]
public sealed class OsCollector : ICollector
{
    public string Name => "Windows";
    private const string Cv = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private static readonly (string Marker, string Name)[] KnownMods =
    {
        ("revios", "ReviOS"), ("revision", "ReviOS"), ("atlasos", "AtlasOS"), ("atlas", "AtlasOS"),
        ("ghost spectre", "Ghost Spectre"), ("tiny11", "tiny11"), ("x-lite", "X-Lite"), ("kernelos", "KernelOS"), ("ameliorated", "AME"),
    };

    public void Collect(SystemProfile p)
    {
        p.Os.Build = int.TryParse(Reg.Str(Cv, "CurrentBuildNumber"), out var b) ? b : Environment.OSVersion.Version.Build;
        p.Os.Ubr = Reg.Int(Cv, "UBR") ?? 0;
        p.Os.Edition = Reg.Str(Cv, "EditionID");
        p.Os.DisplayVersion = Reg.Str(Cv, "DisplayVersion") ?? Reg.Str(Cv, "ReleaseId");
        var product = Reg.Str(Cv, "ProductName") ?? "Windows";
        // ProductName в Windows 11 по-прежнему «Windows 10 …»
        p.Os.ProductName = p.Os.IsWindows11 ? product.Replace("Windows 10", "Windows 11") : product;
        p.Os.ModifiedBuild = DetectModifiedBuild();
    }

    private static string? DetectModifiedBuild()
    {
        var clues = new List<string?>
        {
            Reg.Str(Cv, "RegisteredOrganization"),
            Reg.Str(Cv, "RegisteredOwner"),
            Reg.Str(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Manufacturer"),
            Reg.Str(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model"),
            Reg.Str(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "SupportURL"),
        };
        try
        {
            var bcd = ProcessRunner.Instance.RunConsoleUtf8("bcdedit /enum {current}");
            var m = Regex.Match(bcd.StdOut, @"^description\s+(.+)$", RegexOptions.Multiline);
            if (m.Success) clues.Add(m.Groups[1].Value);
        }
        catch { /* bcdedit без прав администратора недоступен */ }
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Directory.Exists(Path.Combine(win, "AtlasModules"))) return "AtlasOS";
        foreach (var c in clues.Where(c => !string.IsNullOrWhiteSpace(c)))
            foreach (var (marker, name) in KnownMods)
                if (c!.Contains(marker, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }
}

[SupportedOSPlatform("windows")]
public sealed class CpuCollector : ICollector
{
    public string Name => Loc.T("Процессор");

    public void Collect(SystemProfile p)
    {
        var cpus = Wmi.Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed FROM Win32_Processor");
        var first = cpus.FirstOrDefault();
        if (first is null) return;
        p.Cpu.Name = first.Str("Name");
        var man = first.Str("Manufacturer") ?? "";
        p.Cpu.Vendor = man.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Intel
                     : man.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Amd : CpuVendor.Other;
        p.Cpu.Cores = (int)cpus.Sum(c => c.Long("NumberOfCores") ?? 0);
        p.Cpu.Threads = (int)cpus.Sum(c => c.Long("NumberOfLogicalProcessors") ?? 0);
        p.Cpu.MaxClockMhz = (int)(first.Long("MaxClockSpeed") ?? 0);
        p.Cpu.CurrentClockMhz = (int?)first.Long("CurrentClockSpeed");
        var perf = Wmi.Query("SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'").FirstOrDefault();
        p.Cpu.PerformancePercent = perf?.Long("PercentProcessorPerformance");
    }
}

[SupportedOSPlatform("windows")]
public sealed class GpuDisplayCollector : ICollector
{
    public string Name => Loc.T("Видеокарты и экраны");

    public void Collect(SystemProfile p)
    {
        var adapters = DxgiNative.GetAdapters();
        var wmi = Wmi.Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterCompatibility FROM Win32_VideoController");
        var outputToAdapter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in adapters.GroupBy(a => a.Luid).Select(g => g.First()))
        {
            var vendor = GpuClassifier.Vendor(a.VendorId, a.Name);
            var w = wmi.FirstOrDefault(x => string.Equals(x.Str("Name"), a.Name, StringComparison.OrdinalIgnoreCase));
            var gpu = new GpuInfo
            {
                Name = a.Name,
                Vendor = vendor,
                DedicatedVramMb = a.DedicatedVramMb,
                IsSoftware = a.IsSoftware || a.VendorId == 0x1414,
                IsIntegrated = !a.IsSoftware && GpuClassifier.IsIntegrated(vendor, a.Name, a.DedicatedVramMb),
                DrivesDisplay = a.OutputDeviceNames.Count > 0,
                Luid = a.Luid,
                DriverVersion = w?.Str("DriverVersion"),
                DriverDate = w?.Date("DriverDate"),
                PnpDeviceId = w?.Str("PNPDeviceID"),
            };
            if (gpu.IsSoftware) continue;
            p.Gpus.Add(gpu);
            foreach (var o in a.OutputDeviceNames) outputToAdapter[o] = a.Name;

            // HAGS — глобальная настройка; поддержка есть, если её заявляет хотя бы один адаптер.
            if (HagsNative.Query(a.Luid) is { } hags)
            {
                if (hags.Supported) { p.Graphics.HagsSupported = true; p.Graphics.HagsEnabled = hags.Enabled; }
                else p.Graphics.HagsSupported ??= false;
            }
        }

        // Видеокарты на «Базовом видеоадаптере Майкрософт»: DXGI показывает их как адаптер Microsoft — берём из WMI
        foreach (var w in wmi)
        {
            var pnp = w.Str("PNPDeviceID") ?? "";
            var name = w.Str("Name") ?? "";
            var compat = w.Str("AdapterCompatibility") ?? "";
            bool basic = name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains(Loc.T("Базовый видеоадаптер"), StringComparison.OrdinalIgnoreCase)
                         || compat.Contains("Standard display types", StringComparison.OrdinalIgnoreCase) || compat.Contains(Loc.T("Стандартные видеоадаптеры"), StringComparison.OrdinalIgnoreCase);
            if (!basic || !pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            var vendor = GpuClassifier.VendorFromPnp(pnp);
            p.Gpus.Add(new GpuInfo
            {
                Name = name, Vendor = vendor, UsingBasicDriver = true, PnpDeviceId = pnp,
                DriverVersion = w.Str("DriverVersion"), DriverDate = w.Date("DriverDate"),
                IsIntegrated = vendor == GpuVendor.Intel, DrivesDisplay = true,
            });
        }

        var targets = DisplayNative.GetTargets();
        foreach (var d in DisplayNative.GetDisplays())
        {
            var mode = DisplayNative.GetCurrentMode(d.DeviceName);
            if (mode is null) continue;
            int maxHz = DisplayNative.GetModes(d.DeviceName)
                .Where(m => m.Width == mode.Width && m.Height == mode.Height)
                .Select(m => m.Hz).DefaultIfEmpty(mode.Hz).Max();
            var t = targets.FirstOrDefault(x => string.Equals(x.GdiDeviceName, d.DeviceName, StringComparison.OrdinalIgnoreCase));
            p.Displays.Add(new DisplayInfo
            {
                DeviceName = d.DeviceName,
                FriendlyName = t?.FriendlyName,
                Width = mode.Width,
                Height = mode.Height,
                CurrentHz = mode.Hz,
                MaxHzAtCurrentResolution = maxHz,
                IsPrimary = d.IsPrimary,
                IsInternal = t?.IsInternal,
                HdrEnabled = t?.HdrEnabled,
                AdapterName = outputToAdapter.GetValueOrDefault(d.DeviceName) ?? d.AdapterName,
            });
        }

        var igpu = p.IntegratedGpu;
        var dgpu = p.DiscreteGpu;
        p.Hybrid.IsHybrid = igpu is not null && dgpu is not null;
        if (p.Hybrid.IsHybrid)
        {
            var internalDisplay = p.Displays.FirstOrDefault(x => x.IsInternal == true);
            if (internalDisplay is not null)
            {
                bool onIgpu = string.Equals(internalDisplay.AdapterName, igpu!.Name, StringComparison.OrdinalIgnoreCase);
                p.Hybrid.InternalDisplayOnIntegrated = onIgpu;
                // Встроенный экран на dGPU в гибридном ноутбуке возможен только через MUX.
                if (!onIgpu && string.Equals(internalDisplay.AdapterName, dgpu!.Name, StringComparison.OrdinalIgnoreCase))
                    p.Hybrid.HasMux = true;
            }
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class MemoryCollector : ICollector
{
    public string Name => Loc.T("Память");

    public void Collect(SystemProfile p)
    {
        var os = Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem").FirstOrDefault();
        p.Memory.TotalMb = (os?.Long("TotalVisibleMemorySize") ?? 0) / 1024;
        p.Memory.AvailableMb = (os?.Long("FreePhysicalMemory") ?? 0) / 1024;

        var channels = new HashSet<string>();
        foreach (var m in Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, BankLabel, PartNumber, Manufacturer FROM Win32_PhysicalMemory"))
        {
            var mod = new MemoryModule
            {
                CapacityMb = (m.Long("Capacity") ?? 0) / (1024 * 1024),
                RatedSpeed = (int)(m.Long("Speed") ?? 0),
                ConfiguredSpeed = (int)(m.Long("ConfiguredClockSpeed") ?? 0),
                Slot = m.Str("DeviceLocator"),
                PartNumber = m.Str("PartNumber"),
                Manufacturer = m.Str("Manufacturer"),
            };
            p.Memory.Modules.Add(mod);
            var ch = ChannelOf(m.Str("DeviceLocator")) ?? ChannelOf(m.Str("BankLabel"));
            if (ch is not null) channels.Add(ch);
        }
        p.Memory.ChannelsPopulated = channels.Count > 0 && channels.Count <= p.Memory.Modules.Count ? channels.Count : null;
        p.Memory.TotalSlots = (int?)Wmi.Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => a.Long("MemoryDevices") ?? 0);

        p.Memory.TopConsumers = Process.GetProcesses()
            .Select(pr => { try { return (pr.ProcessName, pr.WorkingSet64); } catch { return (pr.ProcessName, 0L); } finally { pr.Dispose(); } })
            .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUsage { Name = g.Key, WorkingSetMb = g.Sum(x => x.Item2) / (1024 * 1024) })
            .OrderByDescending(x => x.WorkingSetMb).Take(10).ToList();
    }

    public static string? ChannelOf(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var m = ChannelRegex().Match(label);
        if (!m.Success) return null;
        var g = m.Groups[1].Success ? m.Groups[1] : m.Groups[2];
        return g.Value.ToUpperInvariant();
    }

    // «ChannelA-DIMM0», «P0 CHANNEL A», «DIMM_A1», «DIMM A2»
    [GeneratedRegex(@"channel\s*([a-h])(?![a-z])|dimm[_\s]?([a-h])\d", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRegex();
}
