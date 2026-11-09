using System.IO.Compression;
using System.Security.Cryptography;

namespace Opti.Launcher;

/// <summary>
/// Выкладывает встроенное приложение в папку &lt;root&gt;\&lt;версия&gt;-&lt;хеш&gt; и переиспользует её при следующих запусках.
/// Распаковка идёт во временную соседнюю папку, метка завершения пишется последней, затем папка переименовывается —
/// поэтому прерванная распаковка никогда не выглядит готовой. Старые версии удаляются (одна предыдущая остаётся).
/// </summary>
public sealed class PayloadInstaller
{
    public const string MarkerName = ".opti-payload";
    private readonly string _root;

    public PayloadInstaller(string root) => _root = Path.GetFullPath(root);

    public string Root => _root;

    public static string HashOf(Stream payload)
    {
        payload.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        payload.Position = 0;
        return hash;
    }

    public string DirFor(string version, string hash) => Path.Combine(_root, $"{Safe(version)}-{hash[..12]}");

    public string EnsureExtracted(Stream payload, string version, out bool extracted)
    {
        var hash = HashOf(payload);
        var dir = DirFor(version, hash);
        extracted = false;
        if (IsComplete(dir, hash)) return dir;

        Directory.CreateDirectory(_root);
        var tmp = $"{dir}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true))
                zip.ExtractToDirectory(tmp); // пути вне папки (zip slip) ExtractToDirectory отклоняет сам
            File.WriteAllText(Path.Combine(tmp, MarkerName), hash);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // недоделанная или повреждённая
            try { Directory.Move(tmp, dir); }
            catch (IOException) when (IsComplete(dir, hash))
            {
                // Параллельный запуск успел раньше — его папка готова
                TryDelete(tmp);
                return dir;
            }
            extracted = true;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        Cleanup(keep: dir);
        return dir;
    }

    public static bool IsComplete(string dir, string hash)
    {
        var marker = Path.Combine(dir, MarkerName);
        try { return File.Exists(marker) && File.ReadAllText(marker).Trim() == hash; }
        catch (IOException) { return false; }
    }

    /// <summary>Оставляет текущую версию и одну предыдущую; убирает недоделанные распаковки старше часа.</summary>
    public void Cleanup(string keep)
    {
        try
        {
            var dirs = new DirectoryInfo(_root).GetDirectories();
            var old = dirs.Where(d => !d.Name.Contains(".tmp-", StringComparison.Ordinal)
                                      && !string.Equals(d.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(d => d.LastWriteTimeUtc)
                          .Skip(1);
            var stale = dirs.Where(d => d.Name.Contains(".tmp-", StringComparison.Ordinal) && d.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1));
            foreach (var d in old.Concat(stale)) TryDelete(d.FullName);
        }
        catch (IOException) { /* очистка — не обязательна */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string dir)
    {
        // Папка может быть занята (запущена старая версия) — тогда удалим в следующий раз
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Safe(string version) =>
        new string(version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
}
