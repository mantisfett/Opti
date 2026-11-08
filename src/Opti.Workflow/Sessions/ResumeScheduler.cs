using System.Runtime.Versioning;
using Opti.Native;

namespace Opti.Workflow.Sessions;

public interface IResumeScheduler
{
    /// <summary>Запустить программу с указанными аргументами при следующем входе пользователя (с повышенными правами).</summary>
    void ScheduleOnLogon(string exePath, string arguments);
    void Cancel();
    bool IsScheduled();
}

/// <summary>Задача планировщика «Opti\Resume» — запускается при входе текущего пользователя, с наивысшими правами, без UAC.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsResumeScheduler : IResumeScheduler
{
    public const string TaskName = @"Opti\Resume";
    private readonly ProcessRunner _runner = ProcessRunner.Instance;

    public void ScheduleOnLogon(string exePath, string arguments)
    {
        // Задача запускается без UAC — только из защищённой копии, а не из «Загрузок».
        exePath = ResumeBinary.Protect(exePath, Opti.Fixes.Backends.OptiPaths.BinDir);
        if (exePath.Contains('"')) throw new ArgumentException("Недопустимый путь к программе");
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var tr = $"\\\"{exePath}\\\" {arguments}";
        var r = _runner.Run("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RU \"{user}\" /IT /RL HIGHEST /TR \"{tr}\"");
        if (!r.Ok) throw new IOException("Не удалось создать задачу продолжения: " + (r.StdErr + r.StdOut).Trim());
    }

    public void Cancel() => _runner.Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");

    public bool IsScheduled() => _runner.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"").Ok;

    /// <summary>Удаляет задачу продолжения версии 1.0.0-beta (GameTune), если она осталась.</summary>
    public void CancelLegacy()
    {
        try { _runner.Run("schtasks.exe", "/Delete /F /TN \"GameTune\\Resume\""); } catch { /* задачи нет */ }
    }
}

/// <summary>
/// Копия программы для задачи продолжения. Задача запускается с наивысшими правами без запроса UAC,
/// поэтому запускать её из «Загрузок» нельзя: файл там может заменить любая программа пользователя.
/// Копия лежит в защищённой папке данных (писать туда могут только администраторы, см. DataDirSecurity).
/// </summary>
public static class ResumeBinary
{
    public static string Protect(string exePath, string binDir)
    {
        try
        {
            var src = Path.GetFullPath(exePath);
            var dest = Path.Combine(Path.GetFullPath(binDir), Path.GetFileName(src));
            if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) return dest;
            // Запуск через лаунчер релиза: программа уже лежит в защищённой папке данных, и одна она без своей папки не работает
            var dataRoot = Path.GetFullPath(Path.Combine(binDir, ".."));
            if (src.StartsWith(dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return src;
            if (File.Exists(Path.ChangeExtension(src, ".dll"))) return src; // не однофайловая сборка (разработка)
            Directory.CreateDirectory(binDir);
            if (!File.Exists(dest) || !SameContent(src, dest))
            {
                var tmp = dest + ".tmp";
                File.Copy(src, tmp, overwrite: true);
                File.Move(tmp, dest, overwrite: true);
            }
            return dest;
        }
        catch
        {
            // Копию сделать не удалось (нет места, файл занят) — задача запустит исходный файл, как раньше.
            return exePath;
        }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return sha.ComputeHash(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
    }
}

public static class RebootHelper
{
    /// <summary>Время последней загрузки системы.</summary>
    public static DateTimeOffset LastBootTime => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Была ли перезагрузка после указанного момента.</summary>
    public static bool RebootedSince(DateTimeOffset moment) => LastBootTime > moment;

    [SupportedOSPlatform("windows")]
    public static void RestartNow(int delaySec = 10) =>
        ProcessRunner.Instance.Run("shutdown.exe", $"/r /t {delaySec} /c \"Opti: перезагрузка для применения настроек. Программа продолжит работу после входа.\"");
}
