using System.Diagnostics;
using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Measure.Capture;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;

namespace Opti.Cli;

/// <summary>Задержка системы (latency), сеть (net), версия (version).</summary>
internal static partial class Commands
{
    // ------------------------------------------------------------------ latency / net / version

    public static int Latency(Args a)
    {
        RequireWindows("Тест DPC/ISR");
        int seconds = a.GetInt("seconds", 60);
        Console.WriteLine($"Тест задержки системы, {seconds} с. Не запускайте тяжёлые программы.");
        var dpc = EtwDpcCapture.Run(TimeSpan.FromSeconds(seconds));
        var timer = TimerAccuracyTest.Run();
        Output.Header("DPC/ISR — худшие драйверы");
        foreach (var d in dpc.Drivers.Take(10))
        {
            var bad = Math.Max(d.MaxDpcUs, d.MaxIsrUs) > 1000;
            Output.Color(bad ? ConsoleColor.Red : ConsoleColor.Gray, $"  {d.Driver,-28} DPC {d.MaxDpcUs,8:0} мкс   ISR {d.MaxIsrUs,8:0} мкс\n");
        }
        Output.Header("Точность таймера (Sleep(1))");
        Console.WriteLine($"  среднее {timer.MeanMs:0.###} мс, p99 {timer.P99Ms:0.###} мс, максимум {timer.MaxMs:0.###} мс" +
                          (timer.CurrentResolutionMs is { } res ? $", текущее разрешение таймера {res:0.###} мс" : ""));
        Output.PrintFindings(DetectorCatalog.RunAll(new SystemProfile { Dpc = dpc }, new[] { new DpcLatencyDetector() }));
        return 0;
    }

    public static async Task<int> Net(Args a)
    {
        var target = a.Get("target") ?? OptiSettings.Load().NetworkTarget;
        Console.WriteLine($"Пинг до {target} в простое{(a.Has("no-load") ? "" : " и под нагрузкой (скачивание ~200 МБ)")}…");
        var q = await NetworkTest.RunAsync(target, a.Has("no-load") ? null : NetworkTest.DefaultLoadUrl);
        Console.WriteLine($"  В простое: {q.IdlePingMs:0.#} мс, джиттер {q.IdleJitterMs:0.#} мс, потери {q.PacketLossPercent:0.#} %");
        if (q.LoadedPingMs is { } l) Console.WriteLine($"  Под нагрузкой: {l:0.#} мс (+{l - q.IdlePingMs:0.#} мс)");
        Output.PrintFindings(DetectorCatalog.RunAll(new SystemProfile { NetQuality = q }, new[] { new BufferbloatDetector() }));
        return 0;
    }

    public static async Task<int> Version(Args a)
    {
        Console.WriteLine($"Opti {AppInfo.Version}");
        if (OptiSettings.Load().CheckForUpdates && !a.Has("offline"))
        {
            var u = await UpdateChecker.CheckAsync(AppInfo.Version);
            if (u is null) Console.WriteLine("Не удалось проверить обновления.");
            else if (u.IsNewer) Output.Color(ConsoleColor.Green, $"Доступна версия {u.LatestVersion}: {u.Url}\n");
            else Console.WriteLine("Установлена последняя версия.");
        }
        return 0;
    }
}
