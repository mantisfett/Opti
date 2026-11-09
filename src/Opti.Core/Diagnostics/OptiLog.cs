namespace Opti.Core.Diagnostics;

/// <summary>
/// Простой журнал работы программы: C:\ProgramData\Opti\logs\opti-ГГГГММДД.log, хранится 14 дней.
/// Пока не вызван <see cref="Init"/> (тесты, библиотечный код) — ничего не пишет.
/// Ошибки записи журнала никогда не мешают работе программы.
/// </summary>
public static class OptiLog
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static string? Directory => _dir;

    public static void Init(string dir, int keepDays = 14)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            _dir = dir;
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "opti-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
        }
        catch { /* журнал недоступен — работаем без него */ }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var dir = _dir;
        if (dir is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" + (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine;
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(dir, $"opti-{DateTime.Now:yyyyMMdd}.log"), line); }
            catch { /* диск занят или полон */ }
        }
    }
}
