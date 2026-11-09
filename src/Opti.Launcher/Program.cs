using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Opti.Fixes.Backends;

namespace Opti.Launcher;

/// <summary>
/// Opti.exe / opti-cli.exe из релиза. Портативная однофайловая сборка WPF распаковывала бы нативные библиотеки
/// во временную папку пользователя — туда может писать любая программа этого пользователя, а Opti работает
/// с правами администратора. Поэтому лаунчер:
///  1) защищает C:\ProgramData\Opti (писать могут только администраторы);
///  2) выкладывает встроенное приложение в C:\ProgramData\Opti\app\&lt;версия&gt;-&lt;хеш&gt; (один раз на версию);
///  3) запускает оттуда Opti.exe или opti-cli.exe с теми же аргументами.
/// </summary>
public static class Program
{
    private const string PayloadResource = "Opti.Payload.zip";

    private static bool IsCli => string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "opti-cli", StringComparison.OrdinalIgnoreCase);

    public static string Version
    {
        get
        {
            var v = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? v[..plus] : v;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return Fail("Opti работает только в Windows.");
            if (!DataDirSecurity.IsElevated()) return Fail("Opti нужны права администратора. Запустите программу ещё раз и подтвердите запрос Windows.");

            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Opti");
            var notes = DataDirSecurity.Harden(dataDir);

            using var payload = typeof(Program).Assembly.GetManifestResourceStream(PayloadResource);
            if (payload is null) return Fail("Эта сборка Opti собрана без приложения внутри. Скачайте Opti.exe со страницы релизов.");
            var dir = new PayloadInstaller(Path.Combine(dataDir, "app")).EnsureExtracted(payload, Version, out _);

            var exe = Path.Combine(dir, IsCli ? "opti-cli.exe" : "Opti.exe");
            if (!File.Exists(exe)) return Fail("Повреждённая сборка: нет " + Path.GetFileName(exe));
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // Путь к самому лаунчеру — для самообновления (SelfUpdater заменяет этот файл новой версией из релиза)
            if (Environment.ProcessPath is { } self) psi.Environment["OPTI_LAUNCHER_PATH"] = self;
            if (notes.Count > 0) psi.Environment["OPTI_LAUNCHER_NOTES"] = string.Join("\n", notes);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + exe);
            if (!IsCli) return 0;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            return Fail("Не удалось запустить Opti: " + ex.Message);
        }
    }

    private static int Fail(string message)
    {
        if (IsCli || !OperatingSystem.IsWindows()) Console.Error.WriteLine(message);
        else _ = MessageBoxW(IntPtr.Zero, message, "Opti", 0x10 /* MB_ICONERROR */);
        return 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
