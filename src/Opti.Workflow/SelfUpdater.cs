using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Opti.Core.Localization;

namespace Opti.Workflow;

public sealed record ReleaseAssets(string Tag, IReadOnlyDictionary<string, string> Assets);

/// <summary>
/// Базовое самообновление портативных Opti.exe / opti-cli.exe. Файлы берутся только из релиза
/// GitHub (HTTPS), сверяются с SHA256SUMS.txt того же релиза, а если текущий файл подписан — новый
/// должен быть подписан тем же сертификатом. Запущенный .exe переименовывается в .old (Windows это позволяет),
/// новый кладётся на его место; .old удаляется при следующем запуске.
/// </summary>
public static class SelfUpdater
{
    public const string SumsAsset = "SHA256SUMS.txt";
    public static readonly string[] Executables = { "Opti.exe", "opti-cli.exe" };

    /// <summary>Путь к лаунчеру, из которого запущена программа (его выставляет Opti.Launcher). null — запуск не из релизной сборки.</summary>
    public static string? LauncherPath
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("OPTI_LAUNCHER_PATH");
            return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
        }
    }

    public static bool CanSelfUpdate => OperatingSystem.IsWindows() && LauncherPath is not null;

    public static ReleaseAssets? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tag)) return null;
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (a.TryGetProperty("name", out var n) && a.TryGetProperty("browser_download_url", out var u)
                    && n.GetString() is { Length: > 0 } name && u.GetString() is { } url && IsTrustedUrl(url))
                    assets[name] = url;
        return new ReleaseAssets(tag.GetString() ?? "", assets);
    }

    /// <summary>Скачивать можно только с GitHub по HTTPS.</summary>
    public static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Хеш файла из SHA256SUMS.txt (формат sha256sum: «хеш  имя»).</summary>
    public static string? FindHash(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp != 64) continue;
            var name = line[sp..].Trim().TrimStart('*');
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) && line[..64].All(Uri.IsHexDigit))
                return line[..64].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Удалить .old, оставшиеся от прошлого обновления.</summary>
    public static void CleanupOld()
    {
        if (LauncherPath is not { } p || Path.GetDirectoryName(p) is not { } dir) return;
        foreach (var exe in Executables)
            try { File.Delete(Path.Combine(dir, exe + ".old")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Скачать и установить версию из релиза. Возвращает установленную версию; ошибки — исключением с понятным текстом.</summary>
    public static async Task<string> InstallLatestAsync(string currentVersion, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Loc.T("Обновление возможно только в Windows."));
        var launcher = LauncherPath ?? throw new InvalidOperationException(Loc.T("Opti запущен не из релизного файла: скачайте новую версию со страницы релизов."));
        var dir = Path.GetDirectoryName(launcher)!;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Opti", currentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        progress?.Report(Loc.T("Ищу новую версию…"));
        var release = ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.Repository}/releases/latest", ct))
                      ?? throw new InvalidOperationException(Loc.T("Не удалось прочитать описание релиза."));
        var version = release.Tag.TrimStart('v');
        if (!UpdateChecker.IsNewer(release.Tag, currentVersion)) return currentVersion;
        if (!release.Assets.TryGetValue(SumsAsset, out var sumsUrl)) throw new InvalidOperationException(Loc.T("В релизе нет SHA256SUMS.txt — обновление отменено."));
        var sums = await http.GetStringAsync(sumsUrl, ct);

        // Обновляем оба файла, если они лежат рядом; сначала всё скачиваем и проверяем, потом заменяем.
        var staged = new List<(string Target, string New)>();
        try
        {
            foreach (var exe in Executables)
            {
                var target = Path.Combine(dir, exe);
                if (!File.Exists(target) && !string.Equals(target, launcher, StringComparison.OrdinalIgnoreCase)) continue;
                if (!release.Assets.TryGetValue(exe, out var url)) throw new InvalidOperationException(Loc.T($"В релизе нет {exe} — обновление отменено."));
                var hash = FindHash(sums, exe) ?? throw new InvalidOperationException(Loc.T($"В SHA256SUMS.txt нет {exe} — обновление отменено."));
                progress?.Report(Loc.T($"Скачиваю {exe} {version}…"));
                var tmp = target + ".new";
                await using (var src = await http.GetStreamAsync(url, ct))
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
                staged.Add((target, tmp));
                await using (var f = File.OpenRead(tmp))
                    if (Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant() != hash)
                        throw new InvalidOperationException(Loc.T($"Контрольная сумма {exe} не совпала — обновление отменено."));
                if (!SignatureMatches(target, tmp))
                    throw new InvalidOperationException(Loc.T($"Подпись {exe} не совпадает с подписью текущей версии — обновление отменено."));
            }

            progress?.Report(Loc.T("Устанавливаю…"));
            foreach (var (target, tmp) in staged)
            {
                var old = target + ".old";
                if (File.Exists(target)) File.Move(target, old, overwrite: true);
                File.Move(tmp, target);
            }
            staged.Clear();
        }
        finally
        {
            foreach (var (_, tmp) in staged) try { File.Delete(tmp); } catch (IOException) { }
        }
        Opti.Core.Diagnostics.OptiLog.Info($"Обновлено до {version}");
        return version;
    }

    /// <summary>Если текущий файл подписан, новый должен быть подписан тем же сертификатом. Неподписанный текущий — проверка только по хешу.</summary>
    private static bool SignatureMatches(string current, string candidate)
    {
        var cur = SignerThumbprint(current);
        return cur is null || string.Equals(cur, SignerThumbprint(candidate), StringComparison.OrdinalIgnoreCase);
    }

    private static string? SignerThumbprint(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
#pragma warning disable SYSLIB0057
            using var c = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return c.Thumbprint;
        }
        catch (CryptographicException) { return null; }
    }
}
