using Opti.Core.Localization;
namespace Opti.Workflow.Cleanup;

public sealed record CleanupEstimate(long Bytes, int Files);

/// <summary>Отдельный найденный объект (например, тяжёлое видео) — с путём, чтобы показать его в Проводнике.</summary>
public sealed record CleanupDetail(string Text, string Path, long Bytes);

public sealed class CleanupResult
{
    public long FreedBytes { get; set; }
    public int Deleted { get; set; }
    /// <summary>Пропущено: файл занят, нет доступа или слишком новый.</summary>
    public int Skipped { get; set; }
    public List<string> Errors { get; } = new();
}

public interface ICleanupTarget
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>Отмечено по умолчанию: безопасно и не влияет ни на что, кроме места.</summary>
    bool DefaultSelected { get; }
    /// <summary>О чём предупредить перед очисткой (null — нечего).</summary>
    string? Caution { get; }
    /// <summary>Очистку делает Windows (например, Windows.old) — открываем параметры.</summary>
    string? ManualUri { get; }
    Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default);
    Task<CleanupResult> CleanAsync(CancellationToken ct = default);
    /// <summary>Порог, после которого показывается рекомендация WarnText (например, «Загрузки» больше 5 ГБ).</summary>
    long? WarnAboveBytes => null;
    string? WarnText => null;
    /// <summary>Найденные объекты после оценки (тяжёлые видео и т. п.).</summary>
    IReadOnlyList<CleanupDetail> Details => Array.Empty<CleanupDetail>();
    /// <summary>Подпись кнопки для ручной цели.</summary>
    string ManualActionText => Loc.T("Открыть параметры");
}

/// <summary>
/// Бережная очистка папки: только файлы старше minAge, ссылки (symlink/junction) не обходятся,
/// занятые и защищённые файлы пропускаются, корневые папки не удаляются.
/// </summary>
public sealed class FolderCleanupTarget : ICleanupTarget
{
    private readonly Func<IEnumerable<string>> _roots;
    private readonly TimeSpan _minAge;
    private readonly Func<string, bool>? _fileFilter;
    private readonly bool _recursive;

    public FolderCleanupTarget(string id, string title, string description, Func<IEnumerable<string>> roots, TimeSpan minAge,
        bool defaultSelected = true, string? caution = null, Func<string, bool>? fileFilter = null, bool recursive = true)
    {
        Id = id; Title = title; Description = description; _roots = roots; _minAge = minAge;
        DefaultSelected = defaultSelected; Caution = caution; _fileFilter = fileFilter; _recursive = recursive;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public bool DefaultSelected { get; }
    public string? Caution { get; }
    public string? ManualUri => null;
    public DateTime Now { get; init; } = DateTime.UtcNow;

    private IEnumerable<string> SafeRoots() => _roots().Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r) && IsSafeRoot(r)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Защита от ошибок конфигурации: не корень диска, не сама папка Windows/профиля/Program Files.</summary>
    public static bool IsSafeRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dangerous = new[]
        {
            Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        if (dangerous.Any(d => !string.IsNullOrEmpty(d) && string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase)))
            return false;
        return full.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) >= 2;
    }

    private IEnumerable<FileInfo> Candidates(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = _recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", opts); }
        catch { yield break; }
        var cutoff = Now - _minAge;
        foreach (var f in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(f); if (!fi.Exists) continue; }
            catch { continue; }
            if (_fileFilter is not null && !_fileFilter(fi.FullName)) continue;
            if (fi.LastWriteTimeUtc > cutoff) continue;
            yield return fi;
        }
    }

    public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        long bytes = 0; int files = 0;
        foreach (var root in SafeRoots())
            foreach (var fi in Candidates(root))
            {
                ct.ThrowIfCancellationRequested();
                try { bytes += fi.Length; files++; } catch { }
            }
        return new CleanupEstimate(bytes, files);
    }, ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var result = new CleanupResult();
        foreach (var root in SafeRoots())
        {
            foreach (var fi in Candidates(root).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    result.FreedBytes += len;
                    result.Deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skipped++; }
            }
            if (_recursive) RemoveEmptyDirectories(root, isRoot: true);
        }
        return result;
    }, ct);

    private void RemoveEmptyDirectories(string dir, bool isRoot)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(sub);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // ссылки не трогаем
                RemoveEmptyDirectories(sub, isRoot: false);
            }
            if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any() && Directory.GetLastWriteTimeUtc(dir) <= Now - _minAge)
                Directory.Delete(dir);
        }
        catch { /* занято или нет доступа */ }
    }
}

/// <summary>Цель, которую очищает сама Windows: показываем размер и открываем нужный раздел параметров.</summary>
public sealed class ManualCleanupTarget : ICleanupTarget
{
    private readonly Func<IEnumerable<string>> _paths;
    private readonly Func<string>? _uri;
    public ManualCleanupTarget(string id, string title, string description, Func<IEnumerable<string>> paths, string uri,
        long? warnAboveBytes = null, string? warnText = null, string? actionText = null)
    {
        Id = id; Title = title; Description = description; _paths = paths; _staticUri = uri;
        WarnAboveBytes = warnAboveBytes; WarnText = warnText; ManualActionText = actionText ?? Loc.T("Открыть параметры");
    }
    /// <summary>Ручная цель, у которой адрес вычисляется при открытии (например, папка «Загрузки»).</summary>
    public ManualCleanupTarget(string id, string title, string description, Func<IEnumerable<string>> paths, Func<string> uri,
        long? warnAboveBytes = null, string? warnText = null, string? actionText = null)
        : this(id, title, description, paths, "", warnAboveBytes, warnText, actionText ?? Loc.T("Открыть папку")) => _uri = uri;
    private readonly string _staticUri;
    public long? WarnAboveBytes { get; }
    public string? WarnText { get; }
    public string ManualActionText { get; }
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public bool DefaultSelected => false;
    public string? Caution => null;
    public string? ManualUri => _uri?.Invoke() ?? _staticUri;

    public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        long bytes = 0; int files = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var p in _paths().Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(p, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try { bytes += new FileInfo(f).Length; files++; } catch { }
            }
        return new CleanupEstimate(bytes, files);
    }, ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct = default) => Task.FromResult(new CleanupResult());
}

/// <summary>
/// Тяжёлые видеофайлы (записи игр, скачанные фильмы) в пользовательских папках. Ничего не удаляет —
/// показывает список, чтобы человек сам решил, что ему не нужно.
/// </summary>
public sealed class LargeFilesTarget : ICleanupTarget
{
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".ts", ".m2ts", ".wmv", ".m4v" };
    private readonly Func<IEnumerable<string>> _roots;
    private readonly long _minBytes;
    private readonly int _maxItems;
    private List<CleanupDetail> _details = new();

    public LargeFilesTarget(string id, string title, string description, Func<IEnumerable<string>> roots, long minBytes, int maxItems = 15)
    {
        Id = id; Title = title; Description = description; _roots = roots; _minBytes = minBytes; _maxItems = maxItems;
    }
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public bool DefaultSelected => false;
    public string? Caution => null;
    public string? ManualUri => _details.Count > 0 ? Path.GetDirectoryName(_details[0].Path) : _roots().FirstOrDefault(Directory.Exists);
    public string ManualActionText => Loc.T("Открыть папку");
    public IReadOnlyList<CleanupDetail> Details => _details;

    public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var found = new List<CleanupDetail>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", opts); } catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || !seen.Add(f)) continue;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length >= _minBytes)
                        found.Add(new CleanupDetail($"{fi.Name} — {SizeFormat.Bytes(fi.Length)}, {fi.LastWriteTime:dd.MM.yyyy}", fi.FullName, fi.Length));
                }
                catch { }
            }
        }
        _details = found.OrderByDescending(d => d.Bytes).Take(_maxItems).ToList();
        return new CleanupEstimate(found.Sum(d => d.Bytes), found.Count);
    }, ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct = default) => Task.FromResult(new CleanupResult());
}

public static class SizeFormat
{
    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => Loc.T($"{b / (double)(1L << 30):0.#} ГБ"),
        >= 1L << 20 => Loc.T($"{b / (double)(1L << 20):0} МБ"),
        >= 1L << 10 => Loc.T($"{b / (double)(1L << 10):0} КБ"),
        _ => Loc.T($"{b} Б"),
    };
}
