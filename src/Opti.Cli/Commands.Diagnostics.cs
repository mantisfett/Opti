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
using Opti.Core.Localization;

namespace Opti.Cli;

/// <summary>Задержка системы (latency), сеть (net), версия (version).</summary>
internal static partial class Commands
{
    // ------------------------------------------------------------------ latency / net / version

    public static int Latency(Args a)
    {
        RequireWindows(Loc.T("Тест DPC/ISR"));
        int seconds = a.GetInt("seconds", 60);
        Console.WriteLine(Loc.T($"Тест задержки системы, {seconds} с. Не запускайте тяжёлые программы."));
        var dpc = EtwDpcCapture.Run(TimeSpan.FromSeconds(seconds));
        var timer = TimerAccuracyTest.Run();
        Output.Header(Loc.T("DPC/ISR — худшие драйверы"));
        foreach (var d in dpc.Drivers.Take(10))
        {
            var bad = Math.Max(d.MaxDpcUs, d.MaxIsrUs) > 1000;
            Output.Color(bad ? ConsoleColor.Red : ConsoleColor.Gray, Loc.T($"  {d.Driver,-28} DPC {d.MaxDpcUs,8:0} мкс   ISR {d.MaxIsrUs,8:0} мкс\n"));
        }
        Output.Header(Loc.T("Точность таймера (Sleep(1))"));
        Console.WriteLine(Loc.T($"  среднее {timer.MeanMs:0.###} мс, p99 {timer.P99Ms:0.###} мс, максимум {timer.MaxMs:0.###} мс") +
                          (timer.CurrentResolutionMs is { } res ? Loc.T($", текущее разрешение таймера {res:0.###} мс") : ""));
        Output.PrintFindings(DetectorCatalog.RunAll(new SystemProfile { Dpc = dpc }, new[] { new DpcLatencyDetector() }));
        return 0;
    }

    public static async Task<int> Net(Args a)
    {
        if (a.Has("regions") || a.Has("dns") || a.Has("wifi")) return await NetReport(a);
        var target = a.Get("target") ?? OptiSettings.Load().NetworkTarget;
        Console.WriteLine(Loc.T($"Пинг до {target} в простое{(a.Has("no-load") ? "" : " и под нагрузкой (скачивание ~200 МБ)")}…"));
        var q = await NetworkTest.RunAsync(target, a.Has("no-load") ? null : NetworkTest.DefaultLoadUrl);
        Console.WriteLine(Loc.T($"  В простое: {q.IdlePingMs:0.#} мс, джиттер {q.IdleJitterMs:0.#} мс, потери {q.PacketLossPercent:0.#} %"));
        if (q.LoadedPingMs is { } l) Console.WriteLine(Loc.T($"  Под нагрузкой: {l:0.#} мс (+{l - q.IdlePingMs:0.#} мс)"));
        Output.PrintFindings(DetectorCatalog.RunAll(new SystemProfile { NetQuality = q }, new[] { new BufferbloatDetector() }));
        return 0;
    }

    /// <summary>net --regions: задержка, разброс и потери до облачных регионов; net --dns: скорость DNS.</summary>
    private static async Task<int> NetReport(Args a)
    {
        if (a.Has("regions"))
        {
            Output.Header(Loc.T("Задержка до регионов (время TCP-соединения)"));
            var all = await ConnectionProbe.ProbeAllAsync(ConnectionProbe.Regions, null);
            var ok = all.Where(r => r.Reachable).OrderBy(r => r.MedianMs).ToList();
            foreach (var r in ok)
            {
                var color = r.LossPercent >= 2 || r.JitterMs >= 15 ? ConsoleColor.Yellow : r.MedianMs < 40 ? ConsoleColor.Green : ConsoleColor.Gray;
                Output.Color(color, $"  {Loc.T(r.Target.Name),-30} {r.MedianMs,6:0} {Loc.T("мс")}   {Loc.T("разброс")} {r.JitterMs:0.#}   {Loc.T("потери")} {r.LossPercent:0} %\n");
            }
            foreach (var r in all.Where(r => !r.Reachable)) Output.Color(ConsoleColor.Red, $"  {Loc.T(r.Target.Name),-30} {Loc.T("нет ответа")}\n");
            Console.WriteLine("\n" + ConnectionAdvice.Describe(ok));
        }
        if (a.Has("wifi"))
        {
            Output.Header(Loc.T("Wi-Fi: загруженность каналов"));
            var run = Opti.Native.ProcessRunner.Instance;
            var nets = WifiChannels.ParseNetworks(run.RunConsoleUtf8("netsh wlan show networks mode=bssid").StdOut);
            var cur = WifiChannels.ParseCurrent(run.RunConsoleUtf8("netsh wlan show interfaces").StdOut);
            var advice = WifiChannels.Analyze(nets, cur);
            foreach (var c in advice.Channels)
            {
                var tag = c.IsCurrent ? "  <- " + Loc.T("ваш канал") : c.IsBest ? "  <- " + Loc.T("свободнее всего") : "";
                Output.Color(c.IsCurrent && advice.Tone == "warn" ? ConsoleColor.Yellow : ConsoleColor.Gray,
                    $"  {(c.Is24 ? "2.4" : "5  ")} GHz  {Loc.T($"канал {c.Channel}"),-12} {Loc.T($"сетей рядом: {c.Neighbors}")}{tag}\n");
            }
            Console.WriteLine("\n" + advice.Summary);
        }
        if (a.Has("dns"))
        {
            Output.Header(Loc.T("Скорость DNS"));
            var rows = new List<DnsResult>();
            foreach (var r in DnsBenchmark.Current().Concat(DnsBenchmark.Public)) rows.Add(await DnsBenchmark.RunAsync(r));
            foreach (var r in rows.Where(r => r.Works).OrderBy(r => r.MedianMs))
                Console.WriteLine($"  {Loc.T(r.Resolver.Name),-34} {r.MedianMs,6:0} {Loc.T("мс")}");
            foreach (var r in rows.Where(r => !r.Works)) Output.Color(ConsoleColor.Red, $"  {Loc.T(r.Resolver.Name),-34} {Loc.T("нет ответа")}\n");
        }
        return 0;
    }

    public static async Task<int> Version(Args a)
    {
        Console.WriteLine($"Opti {AppInfo.Version}");
        if (OptiSettings.Load().CheckForUpdates && !a.Has("offline"))
        {
            var u = await UpdateChecker.CheckAsync(AppInfo.Version);
            if (u is null) Console.WriteLine(Loc.T("Не удалось проверить обновления."));
            else if (u.IsNewer) Output.Color(ConsoleColor.Green, Loc.T($"Доступна версия {u.LatestVersion}: {u.Url}\n"));
            else Console.WriteLine(Loc.T("Установлена последняя версия."));
        }
        return 0;
    }
}
