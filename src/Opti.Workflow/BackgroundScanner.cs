using System.Diagnostics;
using Opti.Core.Localization;

namespace Opti.Workflow;

public enum BackgroundCategory { Browser, Chat, Launcher, Cloud, Updater, Vendor, Recorder, Media, Other }

/// <summary>Программа (все её процессы вместе), занимающая процессор и память в фоне.</summary>
public sealed record BackgroundApp(string Name, int Processes, double CpuPercent, long MemoryMb, BackgroundCategory Category, bool CanClose);

/// <summary>
/// «Что грузит ПК»: короткий замер процессора по процессам и список самых тяжёлых программ, которые можно закрыть на время игры.
/// Системные процессы, службы и сам Opti закрыть нельзя. Ничего не отключается навсегда — программа запустится заново, как обычно.
/// </summary>
public static class BackgroundScanner
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost",
        "explorer", "sihost", "taskhostw", "ctfmon", "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "audiodg", "spoolsv", "wudfhost", "conhost", "taskmgr", "opti", "opti-cli", "dotnet",
        "applicationframehost", "lockapp", "searchindexer", "wmiprvse", "dllhost", "csrss", "ntoskrnl",
    };

    public static bool IsProtected(string name) => Protected.Contains(name) || name.StartsWith("opti", StringComparison.OrdinalIgnoreCase);

    public static BackgroundCategory Classify(string name)
    {
        var n = name.ToLowerInvariant();
        if (n is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "yandex" or "browser") return BackgroundCategory.Browser;
        if (n.Contains("discord") || n.Contains("teams") || n.Contains("slack") || n.Contains("telegram") || n.Contains("whatsapp") || n.Contains("skype") || n.Contains("zoom")) return BackgroundCategory.Chat;
        if (n.Contains("steam") || n.Contains("epic") || n.Contains("battle.net") || n.Contains("origin") || n.Contains("eadesktop") || n.Contains("ubisoft") || n.Contains("upc") || n.Contains("galaxyclient") || n.Contains("riotclient") || n.Contains("launcher")) return BackgroundCategory.Launcher;
        if (n.Contains("onedrive") || n.Contains("dropbox") || n.Contains("googledrive") || n.Contains("yandexdisk") || n.Contains("megasync") || n.Contains("icloud")) return BackgroundCategory.Cloud;
        if (n.Contains("update") || n.Contains("updater") || n.Contains("jusched") || n.Contains("adobe") || n.Contains("creativecloud")) return BackgroundCategory.Updater;
        if (n.Contains("razer") || n.Contains("icue") || n.Contains("lghub") || n.Contains("logi") || n.Contains("armoury") || n.Contains("asus") || n.Contains("msi") || n.Contains("nahimic") || n.Contains("steelseries") || n.Contains("corsair") || n.Contains("wallpaper")) return BackgroundCategory.Vendor;
        if (n.Contains("obs") || n.Contains("shadowplay") || n.Contains("nvcontainer") || n.Contains("medal") || n.Contains("overwolf")) return BackgroundCategory.Recorder;
        if (n.Contains("spotify") || n.Contains("itunes") || n.Contains("vlc") || n.Contains("music")) return BackgroundCategory.Media;
        return BackgroundCategory.Other;
    }

    public static string CategoryText(BackgroundCategory c) => c switch
    {
        BackgroundCategory.Browser => Loc.T("браузер"),
        BackgroundCategory.Chat => Loc.T("мессенджер"),
        BackgroundCategory.Launcher => Loc.T("игровой магазин"),
        BackgroundCategory.Cloud => Loc.T("облачная синхронизация"),
        BackgroundCategory.Updater => Loc.T("обновления"),
        BackgroundCategory.Vendor => Loc.T("утилита устройства"),
        BackgroundCategory.Recorder => Loc.T("запись и оверлей"),
        BackgroundCategory.Media => Loc.T("плеер"),
        _ => Loc.T("программа"),
    };

    /// <summary>Замер: процент одного ядра, делённый на число логических процессоров = доля всего процессора.</summary>
    public static async Task<List<BackgroundApp>> ScanAsync(int sampleMs = 2000, int top = 15, CancellationToken ct = default)
    {
        var before = Snapshot();
        var sw = Stopwatch.StartNew();
        await Task.Delay(sampleMs, ct);
        var after = Snapshot();
        double elapsed = sw.Elapsed.TotalMilliseconds;
        return Build(before, after, elapsed, Environment.ProcessorCount, Environment.ProcessId, top);
    }

    public sealed record Sample(int Pid, string Name, TimeSpan Cpu, long WorkingSet, int SessionId);

    public static Dictionary<int, Sample> Snapshot()
    {
        var map = new Dictionary<int, Sample>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = new Sample(p.Id, p.ProcessName, p.TotalProcessorTime, p.WorkingSet64, p.SessionId); }
            catch { /* нет доступа — процесс не показываем */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    public static List<BackgroundApp> Build(IReadOnlyDictionary<int, Sample> before, IReadOnlyDictionary<int, Sample> after, double elapsedMs, int cpuCount, int selfPid, int top)
    {
        var rows = new Dictionary<string, (int Count, double Cpu, long Mem, bool Closable)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, a) in after)
        {
            if (pid == selfPid) continue;
            double cpu = before.TryGetValue(pid, out var b) && elapsedMs > 0 ? (a.Cpu - b.Cpu).TotalMilliseconds / elapsedMs / cpuCount * 100.0 : 0;
            bool closable = a.SessionId != 0 && !IsProtected(a.Name);
            rows.TryGetValue(a.Name, out var r);
            rows[a.Name] = (r.Count + 1, r.Cpu + Math.Max(0, cpu), r.Mem + a.WorkingSet, (r.Count == 0 || r.Closable) && closable);
        }
        return rows
            .Select(kv => new BackgroundApp(kv.Key, kv.Value.Count, Math.Round(kv.Value.Cpu, 1), kv.Value.Mem / (1024 * 1024), Classify(kv.Key), kv.Value.Closable))
            .OrderByDescending(a => a.CpuPercent * 20 + a.MemoryMb / 100.0) // грузящие процессор раньше «просто занявших память»
            .Where(a => a.CpuPercent >= 0.1 || a.MemoryMb >= 150)
            .Take(top).ToList();
    }

    /// <summary>Закрывает все процессы программы с этим именем (только пользовательские, не защищённые). Возвращает число закрытых.</summary>
    public static int Close(string name)
    {
        if (IsProtected(name)) return 0;
        int closed = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.SessionId == 0 || p.Id == Environment.ProcessId) continue;
                if (!p.CloseMainWindow() || !p.WaitForExit(1500)) p.Kill(entireProcessTree: false);
                closed++;
            }
            catch { /* уже закрыт или нет прав */ }
            finally { p.Dispose(); }
        }
        return closed;
    }
}
