using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Opti.Core.Profile;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Analysis.Collectors;

[SupportedOSPlatform("windows")]
public sealed class BackgroundCollector : ICollector
{
    public string Name => Loc.T("Фоновые программы");

    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private static readonly (string RunKey, string ApprovedKey)[] RunKeys =
    {
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\" + Explorer + @"\Run"),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\" + Explorer + @"\Run"),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\" + Explorer + @"\Run32"),
    };

    /// <summary>Процесс → название оверлея.</summary>
    public static readonly Dictionary<string, string> OverlayProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Discord"] = "Discord (оверлей, если включён)",
        ["steam"] = "Steam (оверлей)",
        ["RTSS"] = "RivaTuner Statistics Server",
        ["Overwolf"] = "Overwolf",
        ["NVIDIA Overlay"] = "NVIDIA App / GeForce Experience (оверлей)",
        ["RadeonSoftware"] = "AMD Software (оверлей)",
        ["GameBar"] = "Xbox Game Bar",
        ["Medal"] = "Medal.tv",
        ["Outplayed"] = "Outplayed",
        ["MSIAfterburner"] = "MSI Afterburner (через RTSS)",
    };

    public void Collect(SystemProfile p)
    {
        foreach (var (runKey, approvedKey) in RunKeys)
        {
            var approved = Reg.Values(approvedKey).ToDictionary(v => v.Name, v => v.Value as byte[], StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in Reg.Values(runKey))
            {
                approved.TryGetValue(name, out var flag);
                p.Background.StartupItems.Add(new StartupItem
                {
                    Name = name,
                    Command = value?.ToString(),
                    Location = runKey,
                    Enabled = IsEnabled(flag),
                    ApprovedKey = approvedKey + "|" + name,
                });
            }
        }
        foreach (var (folder, approvedKey) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), @"HKCU\" + Explorer + @"\StartupFolder"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), @"HKLM\" + Explorer + @"\StartupFolder"),
                 })
        {
            if (!Directory.Exists(folder)) continue;
            var approved = Reg.Values(approvedKey).ToDictionary(v => v.Name, v => v.Value as byte[], StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var fn = Path.GetFileName(file);
                approved.TryGetValue(fn, out var flag);
                p.Background.StartupItems.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file), Command = file, Location = folder,
                    Enabled = IsEnabled(flag), ApprovedKey = approvedKey + "|" + fn,
                });
            }
        }

        var procs = Process.GetProcesses();
        try
        {
            var names = procs.Select(pr => pr.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            p.Background.RunningOverlays = OverlayProcesses.Where(kv => names.Contains(kv.Key)).Select(kv => kv.Value).ToList();

            // Загрузка CPU за 1 секунду
            var t0 = new Dictionary<int, (string Name, TimeSpan Cpu, long Ws)>();
            foreach (var pr in procs) { try { t0[pr.Id] = (pr.ProcessName, pr.TotalProcessorTime, pr.WorkingSet64); } catch { } }
            var sw = Stopwatch.StartNew();
            Thread.Sleep(1000);
            var elapsed = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
            var usage = new List<ProcessUsage>();
            foreach (var pr in procs)
            {
                try
                {
                    if (!t0.TryGetValue(pr.Id, out var a)) continue;
                    pr.Refresh();
                    var cpu = (pr.TotalProcessorTime - a.Cpu).TotalMilliseconds / elapsed * 100;
                    usage.Add(new ProcessUsage { Name = a.Name, Pid = pr.Id, WorkingSetMb = a.Ws / (1024 * 1024), CpuPercent = Math.Round(cpu, 1) });
                }
                catch { /* процесс завершился или нет доступа */ }
            }
            p.Background.HeavyProcesses = usage.Where(u => u.Name is not ("Idle" or "System") && (u.CpuPercent >= 2 || u.WorkingSetMb >= 500))
                .OrderByDescending(u => u.CpuPercent).ThenByDescending(u => u.WorkingSetMb).Take(15).ToList();
        }
        finally { foreach (var pr in procs) pr.Dispose(); }
    }

    /// <summary>StartupApproved: нечётный первый байт (03, 07) — отключено.</summary>
    public static bool IsEnabled(byte[]? flag) => flag is not { Length: > 0 } || (flag[0] & 1) == 0;
}

[SupportedOSPlatform("windows")]
public sealed class ObsCollector : ICollector
{
    private readonly string? _configDirOverride;
    /// <param name="configDirOverride">Папка настроек портативного OBS (из настроек Opti).</param>
    public ObsCollector(string? configDirOverride = null) => _configDirOverride = configDirOverride;
    public string Name => "OBS";

    public void Collect(SystemProfile p)
    {
        var installDir = Reg.Str(@"HKLM\SOFTWARE\OBS Studio", "") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio");
        var exe = Path.Combine(installDir, "bin", "64bit", "obs64.exe");
        var configDir = !string.IsNullOrWhiteSpace(_configDirOverride) && Directory.Exists(_configDirOverride)
            ? _configDirOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        // Портативный OBS: config лежит рядом с программой, exe — на уровень выше
        if (!File.Exists(exe) && _configDirOverride is not null)
        {
            var portableExe = Path.GetFullPath(Path.Combine(configDir, "..", "..", "bin", "64bit", "obs64.exe"));
            if (File.Exists(portableExe)) exe = portableExe;
        }
        if (!File.Exists(exe) && !Directory.Exists(configDir)) return;

        var obs = new ObsInfo
        {
            Installed = File.Exists(exe),
            ExePath = File.Exists(exe) ? exe : null,
            ConfigDir = Directory.Exists(configDir) ? configDir : null,
            Running = Process.GetProcessesByName("obs64").Length > 0,
        };
        if (obs.ExePath is not null)
        {
            obs.RunsAsAdmin = new[]
            {
                Reg.Str(@"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", obs.ExePath),
                Reg.Str(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", obs.ExePath),
            }.Any(v => v?.Split(' ').Contains("RUNASADMIN") == true);
            if (GraphicsSettingsCollector.ReadExePreferences().TryGetValue(obs.ExePath, out var pref)) obs.GpuPreference = pref;
        }

        var scenes = Path.Combine(configDir, "basic", "scenes");
        if (Directory.Exists(scenes))
            foreach (var f in Directory.EnumerateFiles(scenes, "*.json"))
                if (ObsConfig.SceneHasDisplayCapture(File.ReadAllText(f))) obs.SceneFilesWithDisplayCapture.Add(f);
        obs.UsesDisplayCapture = obs.SceneFilesWithDisplayCapture.Count > 0;

        var profileIni = ObsConfig.FindCurrentProfileIni(configDir);
        if (profileIni is not null) ObsConfig.ReadReplayBuffer(File.ReadAllText(profileIni), obs);
        p.Obs = obs;
    }
}

/// <summary>Разбор конфигов OBS (без привязки к Windows — покрыт тестами).</summary>
public static class ObsConfig
{
    public static bool SceneHasDisplayCapture(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array &&
                   sources.EnumerateArray().Any(s => s.TryGetProperty("id", out var id) && id.GetString() == "monitor_capture");
        }
        catch (JsonException) { return false; }
    }

    public static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = result[""] = new(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']') { section = result[line[1..^1]] = new(StringComparer.OrdinalIgnoreCase); continue; }
            int i = line.IndexOf('=');
            if (i > 0) section[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return result;
    }

    public static string? FindCurrentProfileIni(string configDir)
    {
        foreach (var name in new[] { "user.ini", "global.ini" })
        {
            var path = Path.Combine(configDir, name);
            if (!File.Exists(path)) continue;
            var ini = ParseIni(File.ReadAllText(path));
            if (ini.TryGetValue("Basic", out var basic) && basic.TryGetValue("ProfileDir", out var dir))
            {
                var p = Path.Combine(configDir, "basic", "profiles", dir, "basic.ini");
                if (File.Exists(p)) return p;
            }
        }
        var profiles = Path.Combine(configDir, "basic", "profiles");
        return Directory.Exists(profiles) ? Directory.EnumerateFiles(profiles, "basic.ini", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    public static void ReadReplayBuffer(string basicIni, ObsInfo obs)
    {
        var ini = ParseIni(basicIni);
        bool advanced = ini.TryGetValue("Output", out var o) && o.GetValueOrDefault("Mode")?.Equals("Advanced", StringComparison.OrdinalIgnoreCase) == true;
        var sec = ini.GetValueOrDefault(advanced ? "AdvOut" : "SimpleOutput");
        if (sec is null) return;
        if (sec.TryGetValue("RecRB", out var rb)) obs.ReplayBufferEnabled = rb.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (sec.TryGetValue("RecRBTime", out var t) && int.TryParse(t, out var sec2)) obs.ReplayBufferSeconds = sec2;
        if (sec.TryGetValue("RecRBSize", out var s) && int.TryParse(s, out var mb)) obs.ReplayBufferMaxMb = mb;
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class NetworkCollector : ICollector
{
    public string Name => Loc.T("Сеть");

    public void Collect(SystemProfile p)
    {
        const string script =
            "$pm = @{}; Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue | ForEach-Object { $pm[$_.Name] = [string]$_.AllowComputerToTurnOffDevice };" +
            "Get-NetAdapter -Physical | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Desc=$_.InterfaceDescription; Status=[string]$_.Status;" +
            " Speed=$_.ReceiveLinkSpeed; Driver=$_.DriverVersionString; DriverDate=$_.DriverDate; Media=[string]$_.PhysicalMediaType; Pm=$pm[$_.Name] } } | ConvertTo-Json -Compress";
        var r = ProcessRunner.Instance.RunPowerShell(script, TimeSpan.FromSeconds(30));
        if (!r.Ok) { p.CollectorErrors.Add("Get-NetAdapter: " + r.StdErr.Trim()); return; }
        if (string.IsNullOrWhiteSpace(r.StdOut)) return;
        using var doc = JsonDocument.Parse(r.StdOut);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
        foreach (var e in items)
        {
            string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var media = S("Media") ?? "";
            var adapter = new NetAdapterInfo
            {
                Name = S("Name") ?? "",
                Description = S("Desc"),
                Kind = media.Contains("802.11") ? NetAdapterKind.WiFi : media.Contains("802.3") ? NetAdapterKind.Ethernet : NetAdapterKind.Other,
                IsUp = string.Equals(S("Status"), "Up", StringComparison.OrdinalIgnoreCase),
                LinkSpeedMbps = e.TryGetProperty("Speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt64() / 1_000_000 : null,
                DriverVersion = S("Driver"),
                AllowComputerToTurnOff = S("Pm") switch { "Enabled" => true, "Disabled" => false, _ => null },
            };
            p.Network.Add(adapter);
        }

        var wifi = p.Network.FirstOrDefault(n => n.Kind == NetAdapterKind.WiFi && n.IsUp);
        if (wifi is not null)
        {
            var w = ProcessRunner.Instance.RunConsoleUtf8("netsh wlan show interfaces");
            wifi.WifiBandGhz = ParseWifiBand(w.StdOut);
            wifi.WifiSignalPercent = ParseWifiSignal(w.StdOut);
        }
        p.DeliveryOptimizationMode = ReadDeliveryOptimizationMode();
    }

    /// <summary>Политика (приоритетнее) или пользовательская настройка «Разрешить загрузки с других компьютеров».</summary>
    private static int? ReadDeliveryOptimizationMode()
    {
        try
        {
            foreach (var path in new[] { @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config" })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (k?.GetValue("DODownloadMode") is int mode) return mode;
            }
        }
        catch { /* нет доступа — не задан */ }
        return null;
    }

    public static int? ParseWifiSignal(string netshOutput)
    {
        var m = SignalRegex().Match(netshOutput);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    [GeneratedRegex(@"^\s*(?:Signal|Сигнал)\s*:\s*(\d{1,3})\s*%", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex SignalRegex();

    public static double? ParseWifiBand(string netshOutput)
    {
        var m = BandRegex().Match(netshOutput);
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ghz))
            return ghz;
        var ch = ChannelRegex().Match(netshOutput);
        if (ch.Success && int.TryParse(ch.Groups[1].Value, out var c)) return c <= 14 ? 2.4 : 5;
        return null;
    }

    [GeneratedRegex(@":\s*(\d+(?:[.,]\d+)?)\s*(?:GHz|ГГц)", RegexOptions.IgnoreCase)] private static partial Regex BandRegex();
    [GeneratedRegex(@"^\s*(?:Channel|Канал)\s*:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex ChannelRegex();
}
