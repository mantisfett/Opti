using System.Text;
using Opti.Cli;

// Синтетический тест встроен: opti-cli.exe --bench …
if (Opti.Bench.BenchProgram.IsBenchInvocation(args)) return Opti.Bench.BenchProgram.Run(args);
Console.OutputEncoding = Encoding.UTF8;
foreach (var note in Opti.Fixes.Backends.OptiPaths.PrepareDataDir())
    Output.Color(ConsoleColor.Yellow, note + "\n");
if (OperatingSystem.IsWindows() && Opti.Native.SessionUser.MismatchWarning() is { } who)
    Output.Color(ConsoleColor.Yellow, who + "\n");
var a = new Args(args);
Opti.Core.Diagnostics.OptiLog.Init(Opti.Fixes.Backends.OptiPaths.LogsDir);
Opti.Core.Diagnostics.OptiLog.Info($"opti-cli {Opti.Workflow.AppInfo.Version}: {a.Command}");

try
{
    // Команды, которые меняют систему или сессию, — только в одном экземпляре (окно Opti тоже держит блокировку).
    using var instance = a.Command is "run" or "resume" or "measure" or "apply" or "ab" or "compare" or "rollback" or "verify" or "uninstall"
        ? Opti.Workflow.SingleInstance.TryAcquire(TimeSpan.FromSeconds(3))
          ?? throw new InvalidOperationException("Opti уже работает (окно или другая команда). Закройте его и повторите.")
        : null;
    return a.Command switch
    {
        "run" => await Commands.Run(a),
        "analyze" => Commands.Analyze(a),
        "plan" => Commands.Plan(a),
        "measure" => await Commands.Measure(a),
        "apply" => Commands.Apply(a),
        "compare" => Commands.Compare(a),
        "ab" => await Commands.Ab(a),
        "resume" => await Commands.Resume(a),
        "report" => Commands.Report(a),
        "rollback" => Commands.Rollback(a),
        "history" => Commands.History(a),
        "latency" => Commands.Latency(a),
        "net" => await Commands.Net(a),
        "version" or "--version" => await Commands.Version(a),
        "doctor" => Doctor.Run(a),
        "bugreport" => Commands.BugReport(a),
        "verify" => Commands.Verify(a),
        "uninstall" => Commands.Uninstall(a),
        _ => Commands.Help(),
    };
}
catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or FileNotFoundException
                               or ArgumentException or InvalidOperationException or OperationCanceledException or TimeoutException)
{
    Opti.Core.Diagnostics.OptiLog.Warn("Команда завершилась с ошибкой", ex);
    Output.Color(ConsoleColor.Red, "Ошибка: " + ex.Message + "\n");
    return 2;
}
