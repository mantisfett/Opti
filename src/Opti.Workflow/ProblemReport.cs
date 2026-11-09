using System.IO.Compression;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Localization;

namespace Opti.Workflow;

/// <summary>
/// «Отчёт о проблеме»: один архив с тем, что нужно для разбора ошибки, — журнал работы, сессии
/// (профиль железа и замеры), журнал изменений и настройки. Ничего никуда не отправляется: архив сохраняется
/// на компьютере, пользователь сам решает, прикладывать ли его к обращению.
/// Имя пользователя в путях заменяется на «%USERNAME%».
/// </summary>
public static class ProblemReport
{
    public static string Create(string dataDir, IChangeJournal journal, string outputDir, string appVersion)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"opti-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        AddText(zip, "about.txt",
            Loc.T($"Opti {appVersion}\nWindows {Environment.OSVersion.Version}\n64-бит: {Environment.Is64BitOperatingSystem}\nСоздан: {DateTimeOffset.Now:O}\n"));
        AddText(zip, "journal.json", OptiJson.Serialize(new { sessions = journal.GetSessions(), changes = journal.GetAll() }));

        foreach (var (sub, pattern, max) in new[] { ("logs", "*.log", 14), ("sessions", "*.json", 20) })
        {
            var dir = Path.Combine(dataDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTime).Take(max))
                AddFile(zip, f.FullName, $"{sub}/{f.Name}");
        }
        var settings = Path.Combine(dataDir, "settings.json");
        if (File.Exists(settings)) AddFile(zip, settings, "settings.json");
        return path;
    }

    private static void AddFile(ZipArchive zip, string file, string entry)
    {
        try
        {
            // Файл журнала может быть открыт на запись — читаем с общим доступом
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs);
            AddText(zip, entry, rd.ReadToEnd());
        }
        catch (IOException) { /* занят или удалён — пропускаем */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddText(ZipArchive zip, string entry, string text)
    {
        var user = Environment.UserName;
        if (user.Length > 1) text = text.Replace(user, "%USERNAME%", StringComparison.OrdinalIgnoreCase);
        var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open());
        w.Write(text);
    }
}
