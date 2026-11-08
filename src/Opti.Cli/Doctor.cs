using System.Security.Principal;
using Opti.Fixes.Backends;
using Opti.Native;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Microsoft.Diagnostics.Tracing.Session;
using Opti.Core.Localization;

namespace Opti.Cli;

/// <summary>Проверка окружения: всё ли доступно для анализа, исправлений и замеров. Ничего не меняет.</summary>
internal static class Doctor
{
    public static int Run(Args a)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Loc.T("Проверка окружения возможна только в Windows."));
        Output.Header(Loc.T($"Opti {AppInfo.Version}: проверка окружения"));
        int fails = 0;
        void Check(string name, Func<(bool Ok, string Detail)> f, bool critical = true)
        {
            (bool ok, string detail) r;
            try { r = f(); } catch (Exception ex) { r = (false, ex.Message); }
            Output.Color(r.ok ? ConsoleColor.Green : critical ? ConsoleColor.Red : ConsoleColor.Yellow, r.ok ? "  ✓ " : critical ? "  ✗ " : "  ! ");
            Console.WriteLine($"{name}: {r.detail}");
            if (!r.ok && critical) fails++;
        }

        Check(Loc.T("Права администратора"), () =>
        {
            using var id = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            return (admin, admin ? Loc.T("есть") : Loc.T("нет — запустите от имени администратора"));
        });
        Check("Windows", () => (Environment.OSVersion.Version.Build >= 17763, Loc.T($"сборка {Environment.OSVersion.Version.Build}")));
        Check(Loc.T("ETW (сессии трассировки)"), () => (TraceEventSession.IsElevated() == true, TraceEventSession.IsElevated() == true ? Loc.T("доступны") : Loc.T("нужны права администратора")));
        Check("bcdedit", () => { var r = ProcessRunner.Instance.RunConsoleUtf8("bcdedit /enum {current}"); return (r.Ok, r.Ok ? Loc.T("читается") : (r.StdOut + r.StdErr).Trim()); });
        Check("powercfg", () => { var r = ProcessRunner.Instance.RunConsoleUtf8("powercfg /getactivescheme"); return (r.Ok, r.StdOut.Trim()); });
        Check(Loc.T("PowerShell (сетевые адаптеры)"), () => { var r = ProcessRunner.Instance.RunPowerShell("(Get-NetAdapter -Physical | Measure-Object).Count"); return (r.Ok, r.Ok ? Loc.T($"адаптеров: {r.StdOut.Trim()}") : r.StdErr.Trim()); });
        Check(Loc.T("Папка данных"), () =>
        {
            Directory.CreateDirectory(OptiPaths.DataDir);
            var probe = Path.Combine(OptiPaths.DataDir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, OptiPaths.DataDir);
        });
        Check(Loc.T("Журнал изменений"), () =>
        {
            using var j = new Opti.Fixes.Journal.SqliteChangeJournal(OptiPaths.JournalPath);
            return (true, Loc.T($"{j.GetAll().Count} записей, активных {j.GetAll().Count(r => !r.RolledBack)}"));
        });
        Check(Loc.T("Синтетический тест"), () => SyntheticRunner.Locate() is { } b ? (true, b) : (false, Loc.T("Opti.Bench.exe не найден рядом с программой")), critical: false);
        Check("PresentMon", () => PresentMonRunner.Locate(OptiSettings.Load().PresentMonPath) is { } pm ? (true, pm) : (false, Loc.T("не найден — режим «Игра через PresentMon» недоступен (необязательно)")), critical: false);
        Check("nvidia-smi", () => { var r = ProcessRunner.Instance.Run("nvidia-smi", "-L", TimeSpan.FromSeconds(5)); return (r.Ok, r.StdOut.Split('\n')[0].Trim()); }, critical: false);
        Check(Loc.T("Задача продолжения"), () => new Opti.Workflow.Sessions.WindowsResumeScheduler().IsScheduled()
            ? (true, Loc.T("запланирована (ожидается перезагрузка)")) : (true, Loc.T("не запланирована")), critical: false);

        Console.WriteLine(fails == 0 ? Loc.T("\nВсё необходимое доступно.") : Loc.T($"\nПроблем: {fails}. Исправьте их перед оптимизацией."));
        return fails == 0 ? 0 : 1;
    }
}
