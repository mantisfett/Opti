using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Opti.Workflow;

public static class AppInfo
{
    public const string Repository = "mantisfett/Opti";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";

    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
            var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }
}

public sealed record UpdateInfo(string LatestVersion, string Url, bool IsNewer);

/// <summary>Проверка новой версии через GitHub Releases. Только уведомление — ничего не скачивается и не запускается.</summary>
public static class UpdateChecker
{
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Opti", currentVersion));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // Стабильная версия смотрит только стабильные релизы; бета — и бета-релизы тоже.
            bool pre = currentVersion.Contains('-');
            var url = pre ? $"https://api.github.com/repos/{AppInfo.Repository}/releases?per_page=15"
                          : $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var releases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
            var best = releases
                .Where(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()))
                .Select(r => (Tag: r.GetProperty("tag_name").GetString() ?? "", Url: r.TryGetProperty("html_url", out var u) ? u.GetString() ?? AppInfo.ReleasesUrl : AppInfo.ReleasesUrl))
                .Where(r => r.Tag.Length > 0)
                .Aggregate(((string Tag, string Url)?)null, (acc, r) => acc is null || IsNewer(r.Tag, acc.Value.Tag) ? r : acc);
            if (best is null) return null;
            return new UpdateInfo(best.Value.Tag.TrimStart('v'), best.Value.Url, IsNewer(best.Value.Tag, currentVersion));
        }
        catch (Exception ex)
        {
            Opti.Core.Diagnostics.OptiLog.Info("Проверка обновлений не удалась: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Сравнение версий по SemVer 2.0: 1.2.3, префикс v допускается. Предрелиз ниже релиза; предрелизы сравниваются
    /// по частям: числа — как числа (beta.2 &lt; beta.10), текст — по алфавиту (alpha &lt; beta &lt; rc). Метаданные после «+» не учитываются.
    /// </summary>
    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    public static int CompareVersions(string x, string y)
    {
        static (Version V, string[] Pre) P(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            int dash = s.IndexOf('-');
            var pre = dash >= 0 ? s[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            if (dash >= 0) s = s[..dash];
            var v = System.Version.TryParse(s.Contains('.') ? s : s + ".0", out var parsed) ? parsed : new Version(0, 0);
            return (new Version(v.Major, v.Minor, Math.Max(v.Build, 0)), pre);
        }
        var (a, aPre) = P(x);
        var (b, bPre) = P(y);
        int c = a.CompareTo(b);
        if (c != 0) return c;
        // Релиз выше любого своего предрелиза
        if (aPre.Length == 0 || bPre.Length == 0) return aPre.Length == bPre.Length ? 0 : aPre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(aPre.Length, bPre.Length); i++)
        {
            bool an = int.TryParse(aPre[i], out var ai), bn = int.TryParse(bPre[i], out var bi);
            int r = (an, bn) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1, // числовые части ниже текстовых
                (false, true) => 1,
                _ => string.CompareOrdinal(aPre[i], bPre[i]),
            };
            if (r != 0) return Math.Sign(r);
        }
        return aPre.Length.CompareTo(bPre.Length);
    }
}
