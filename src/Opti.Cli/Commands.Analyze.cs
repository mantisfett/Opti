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

/// <summary>Анализ системы (analyze) и план исправлений (plan).</summary>
internal static partial class Commands
{
    // ------------------------------------------------------------------ analyze / plan

    public static int Analyze(Args a)
    {
        var result = AnalysisLoader.LoadOrCollect(a);
        if (a.Get("dpc") is { } dpc && OperatingSystem.IsWindows())
        {
            Console.WriteLine($"Тест DPC/ISR {dpc} с — не нагружайте компьютер…");
            result.Profile.Dpc = EtwDpcCapture.Run(TimeSpan.FromSeconds(int.Parse(dpc)));
        }
        if (a.Has("net"))
        {
            Console.WriteLine("Тест сети (~20 с)…");
            result.Profile.NetQuality = NetworkTest.RunAsync(OptiSettings.Load().NetworkTarget).GetAwaiter().GetResult();
        }
        if (a.Has("dpc") || a.Has("net")) result = SystemAnalyzer.FromProfile(result.Profile);

        PrintProfileSummary(result.Profile);
        Output.PrintFindings(result.Findings);
        if (result.Profile.CollectorErrors.Count > 0)
        {
            Output.Header("Не удалось собрать");
            foreach (var e in result.Profile.CollectorErrors) Output.Color(ConsoleColor.DarkGray, "  " + e + "\n");
        }

        var outPath = a.Get("out") ?? Path.Combine(OptiPaths.ReportsDir, $"analysis-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, OptiJson.Serialize(new { profile = result.Profile, findings = result.Findings, timings = result.Timings }));
        Console.WriteLine($"\nПрофиль и находки: {outPath}");
        if (a.Get("html") is { } html)
        {
            File.WriteAllText(html, HtmlReport.Build(new ReportData
            {
                Session = new TuneSession { Profile = result.Profile, Findings = result.Findings, Goal = OptiSettings.Load().DefaultGoal },
                Catalog = FixCatalog.All(), AppVersion = AppInfo.Version,
            }));
            Console.WriteLine($"HTML-отчёт: {html}");
        }
        return 0;
    }

    private static void PrintProfileSummary(SystemProfile p)
    {
        Output.Header("Профиль ПК");
        Console.WriteLine($"  {DeviceClassification.From(p).Describe()}");
        Console.WriteLine($"  {p.Device.Manufacturer} {p.Device.Model}");
        Console.WriteLine($"  {p.Os.ProductName} {p.Os.DisplayVersion} (сборка {p.Os.Build}.{p.Os.Ubr}){(p.Os.ModifiedBuild is { } m ? $", модифицированная: {m}" : "")}");
        Console.WriteLine($"  CPU: {p.Cpu.Name} — {p.Cpu.Cores} ядер / {p.Cpu.Threads} потоков");
        foreach (var g in p.Gpus)
            Console.WriteLine($"  GPU: {g.Name} ({(g.IsIntegrated ? "встроенная" : "дискретная")}, драйвер {g.DriverVersion} от {g.DriverDate:yyyy-MM-dd})");
        foreach (var d in p.Displays)
            Console.WriteLine($"  Экран: {d.FriendlyName ?? d.DeviceName} {d.Width}×{d.Height} @ {d.CurrentHz} Гц (макс. {d.MaxHzAtCurrentResolution}){(d.IsInternal == true ? ", встроенный" : "")}");
        Console.WriteLine($"  ОЗУ: {p.Memory.TotalMb / 1024.0:0.#} ГБ, свободно {p.Memory.AvailablePercent:0} %, модулей {p.Memory.Modules.Count}");
        if (p.SystemDisk is { } sd) Console.WriteLine($"  Системный диск: {sd.Model} ({sd.MediaType}), свободно {sd.FreeGb:0} ГБ");
        Console.WriteLine($"  Питание: {(p.Power.OnAcPower == true ? "от сети" : p.Power.OnAcPower == false ? "от батареи" : "?")}, схема {p.Power.ActiveSchemeName ?? p.Power.ActiveSchemeGuid}");
        Console.WriteLine($"  Игр найдено: {p.Games.Count}");
    }

    public static int Plan(Args a)
    {
        var r = AnalysisLoader.LoadOrCollect(a);
        var settings = OptiSettings.Load();
        var wf = new TuneWorkflow(settings, new SessionStore(Path.Combine(Path.GetTempPath(), "opti-plan")), InMemoryBackend.CreateRegistry(out _), new InMemoryChangeJournal());
        var session = new TuneSession { Profile = r.Profile, Findings = r.Findings, Goal = ParseGoal(a.Get("goal"), settings.DefaultGoal) };
        var plan = wf.BuildPlan(session);
        Output.PrintPlan(plan);

        bool emulate = true;
        IBackendRegistry backends;
        if (!a.Has("profile") && OperatingSystem.IsWindows())
        {
            emulate = false;
            backends = new DryRunBackendRegistry(WindowsBackends.Create());
        }
        else backends = ProfileSeeder.CreateEmulation(r.Profile, out _);
        var preview = new ApplyService(backends, new InMemoryChangeJournal(), restore: null)
            .Apply(r.Profile, plan.Fixes.Where(f => f.Selected).Select(f => f.Fix), plan.Goal);
        Output.Header(emulate ? "Что будет изменено (эмуляция на сохранённом профиле)" : "Что будет изменено (сухой прогон, ничего не записано)");
        foreach (var res in preview.Results)
        {
            Console.WriteLine($"  {res.FixId}: {res.Outcome}{(res.Error is null ? "" : " — " + res.Error)}");
            foreach (var c in res.Changes) Output.Color(ConsoleColor.DarkGray, $"     {c.Type} {c.Target}: {c.Before} → {c.After}\n");
        }
        return 0;
    }
}
