using System.Runtime.Versioning;
using Opti.Core.Fixes;

namespace Opti.Fixes.Backends;

public static class OptiPaths
{
    /// <summary>
    /// C:\ProgramData\Opti. Переменная окружения OPTI_DATA_DIR переопределяет путь
    /// (портативный режим и тесты); такая папка защищается так же, как стандартная (см. <see cref="PrepareDataDir"/>).
    /// </summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("OPTI_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Opti");
    public static string JournalPath => Path.Combine(DataDir, "journal.db");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    public static string ReportsDir => Path.Combine(DataDir, "reports");
    public static string SessionsDir => Path.Combine(DataDir, "sessions");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    /// <summary>Копия программы для задачи продолжения после перезагрузки.</summary>
    public static string BinDir => Path.Combine(DataDir, "bin");

    /// <summary>Папка данных версии 1.0.0-beta (программа тогда называлась GameTune).</summary>
    public static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameTune");

    /// <summary>
    /// Переносит журнал отката, сессии и резервные копии из C:\ProgramData\GameTune в C:\ProgramData\Opti,
    /// чтобы изменения, сделанные бета-версией, можно было откатить. Возвращает текст для пользователя или null.
    /// </summary>
    public static string? MigrateLegacyData()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("OPTI_DATA_DIR") is { Length: > 0 }) return null;
            var legacy = LegacyDataDir;
            if (!Directory.Exists(legacy) || Directory.Exists(DataDir)) return null;
            if (OperatingSystem.IsWindows() && DataDirSecurity.IsElevated() && !DataDirSecurity.IsTrustedDirectory(legacy))
                return "Папка " + legacy + " создана не администратором — данные GameTune не перенесены.";
            try { Directory.Move(legacy, DataDir); }
            catch (IOException) { CopyDirectory(legacy, DataDir); }
            return "Данные GameTune (журнал отката и история) перенесены в " + DataDir + ".";
        }
        catch (Exception ex) { return "Не удалось перенести данные GameTune: " + ex.Message; }
    }

    /// <summary>
    /// Вызывается при запуске программы и командной строки: перенос данных GameTune и защита папки данных
    /// от подмены (права только для администраторов, карантин подброшенных файлов). Возвращает сообщения для пользователя.
    /// </summary>
    public static IReadOnlyList<string> PrepareDataDir()
    {
        var notes = new List<string>();
        // Лаунчер релиза уже защитил папку до распаковки приложения — его сообщения (карантин и т. п.) показываем здесь
        if (Environment.GetEnvironmentVariable("OPTI_LAUNCHER_NOTES") is { Length: > 0 } fromLauncher)
            notes.AddRange(fromLauncher.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (MigrateLegacyData() is { } m) notes.Add(m);
        if (OperatingSystem.IsWindows())
        {
            try { notes.AddRange(DataDirSecurity.Harden(DataDir)); }
            catch (Exception ex) { notes.Add("Не удалось защитить папку данных " + DataDir + ": " + ex.Message); }
        }
        return notes;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsBackends
{
    public static BackendRegistry Create(string? backupDir = null) => new(new IStateBackend[]
    {
        new WindowsRegistryBackend(),
        new WindowsBcdBackend(),
        new WindowsPowerBackend(),
        new WindowsDisplayBackend(),
        new WindowsNetAdapterBackend(),
        new FileBackend(backupDir ?? OptiPaths.BackupDir),
    });
}
