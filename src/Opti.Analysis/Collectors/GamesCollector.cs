using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Analysis.Collectors;

[SupportedOSPlatform("windows")]
public sealed class GamesCollector : ICollector
{
    public string Name => Loc.T("Игры");

    public void Collect(SystemProfile p)
    {
        var prefs = GraphicsSettingsCollector.ReadExePreferences();
        var games = new List<GameInfo>();
        Try(p, "Steam", () => games.AddRange(Steam()));
        Try(p, "Epic", () => games.AddRange(Epic()));
        Try(p, "GOG", () => games.AddRange(Gog()));
        Try(p, "Xbox", () => games.AddRange(Xbox()));

        foreach (var g in games)
        {
            if (g.InstallDir is not null && Directory.Exists(g.InstallDir))
            {
                if (g.Executables.Count == 0) g.Executables = GameScan.FindExecutables(g.InstallDir);
                g.IsOnline = GameScan.LooksOnline(g.Name, g.InstallDir);
            }
            foreach (var exe in g.Executables)
                if (prefs.TryGetValue(exe, out var v)) g.ExeGpuPreferences[exe] = v;
        }
        p.Games = games.Where(g => g.Executables.Count > 0)
            .GroupBy(g => g.InstallDir ?? g.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
    }

    private static void Try(SystemProfile p, string what, Action a)
    {
        try { a(); } catch (Exception ex) { p.CollectorErrors.Add(Loc.T($"Игры {what}: {ex.Message}")); }
    }

    private static IEnumerable<GameInfo> Steam()
    {
        var steam = Reg.Str(@"HKCU\Software\Valve\Steam", "SteamPath");
        if (steam is null) yield break;
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libs = new List<string> { steam };
        if (File.Exists(vdf)) libs.AddRange(GameScan.ParseSteamLibraries(File.ReadAllText(vdf)));
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var kv = GameScan.ParseAcf(File.ReadAllText(acf));
                if (!kv.TryGetValue("installdir", out var dir) || !kv.TryGetValue("name", out var name)) continue;
                if (GameScan.IsSteamTool(name)) continue;
                yield return new GameInfo { Name = name, Launcher = GameLauncher.Steam, InstallDir = Path.Combine(apps, "common", dir) };
            }
        }
    }

    private static IEnumerable<GameInfo> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var install = S("InstallLocation");
            var launch = S("LaunchExecutable");
            if (install is null) continue;
            var g = new GameInfo { Name = S("DisplayName") ?? Path.GetFileName(install), Launcher = GameLauncher.Epic, InstallDir = install };
            if (launch is not null && File.Exists(Path.Combine(install, launch))) g.Executables.Add(Path.GetFullPath(Path.Combine(install, launch)));
            yield return g;
        }
    }

    private static IEnumerable<GameInfo> Gog()
    {
        const string root = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";
        foreach (var id in Reg.SubKeys(root))
        {
            var k = root + "\\" + id;
            var path = Reg.Str(k, "path");
            if (path is null) continue;
            var g = new GameInfo { Name = Reg.Str(k, "gameName") ?? id, Launcher = GameLauncher.Gog, InstallDir = path };
            var exe = Reg.Str(k, "exe");
            if (exe is not null && File.Exists(exe)) g.Executables.Add(exe);
            yield return g;
        }
    }

    private static IEnumerable<GameInfo> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var content = Path.Combine(dir, "Content");
                yield return new GameInfo { Name = Path.GetFileName(dir), Launcher = GameLauncher.Xbox, InstallDir = Directory.Exists(content) ? content : dir };
            }
        }
    }
}

/// <summary>Разбор файлов лаунчеров и поиск exe (без привязки к Windows — покрыт тестами).</summary>
public static partial class GameScan
{
    private static readonly string[] ExcludedExe =
    {
        "unins", "uninstall", "setup", "install", "redist", "vc_redist", "vcredist", "dxsetup", "dotnet", "crash", "report",
        "easyanticheat", "eac_", "beservice", "battleye", "launcher_helper", "ue4prereq", "ueprereq", "prereq", "helper", "updater",
        "cefprocess", "webhelper", "quicksfv", "touchup", "cleanup", "dxwebsetup", "oalinst", "physx", "benchmark_uploader",
    };

    private static readonly string[] OnlineKeywords =
    {
        "counter-strike", "dota", "valorant", "apex", "fortnite", "pubg", "rust", "overwatch", "rainbow six", "league of legends",
        "call of duty", "warzone", "battlefield", "the finals", "deadlock", "marvel rivals", "tarkov", "war thunder",
        "world of tanks", "world of warships", "destiny", "rocket league", "dead by daylight", "naraka", "warframe", "lost ark",
    };

    public static List<string> FindExecutables(string installDir, int maxDepth = 3, int maxCount = 5)
    {
        var result = new List<(string Path, long Size)>();
        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.exe").ToList(); } catch { return; }
            foreach (var f in files)
            {
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (ExcludedExe.Any(n.Contains)) continue;
                long size;
                try { size = new FileInfo(f).Length; } catch { continue; }
                if (size >= 1_000_000) result.Add((f, size));
            }
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir).ToList(); } catch { return; }
            foreach (var d in dirs)
            {
                var dn = Path.GetFileName(d).ToLowerInvariant();
                if (dn is "_commonredist" or "redist" or "redistributables" or "directx" or "easyanticheat" or "battleye" or "__installer" or "support") continue;
                Walk(d, depth + 1);
            }
        }
        Walk(installDir, 0);
        return result.OrderByDescending(r => r.Size).Take(maxCount).Select(r => r.Path).ToList();
    }

    public static bool LooksOnline(string name, string installDir)
    {
        var n = name.ToLowerInvariant();
        if (OnlineKeywords.Any(n.Contains)) return true;
        try
        {
            return Directory.EnumerateDirectories(installDir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Any(d => Path.GetFileName(d) is { } dn && (dn.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || dn.Equals("BattlEye", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    public static bool IsSteamTool(string name) =>
        name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase) || name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>Пути библиотек из libraryfolders.vdf («"path" "D:\\SteamLibrary"»).</summary>
    public static IEnumerable<string> ParseSteamLibraries(string vdf) =>
        VdfPathRegex().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    /// <summary>Плоский разбор пар «"key" "value"» из appmanifest_*.acf (первое вхождение ключа).</summary>
    public static Dictionary<string, string> ParseAcf(string acf)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPairRegex().Matches(acf)) d.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Replace(@"\\", @"\"));
        return d;
    }

    [GeneratedRegex(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase)] private static partial Regex VdfPathRegex();
    [GeneratedRegex(@"""([^""]+)""\s+""([^""]*)""")] private static partial Regex VdfPairRegex();
}
