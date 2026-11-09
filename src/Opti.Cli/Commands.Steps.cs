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

/// <summary>Отдельные шаги: замер (measure), применение (apply), сравнение (compare).</summary>
internal static partial class Commands
{
    // ------------------------------------------------------------------ measure / apply / compare

    public static async Task<int> Measure(Args a)
    {
        RequireWindows(Loc.T("Замер"));
        var settings = OptiSettings.Load();
        var mode = ParseMode(a.Get("mode"), MeasureMode.Game);
        if (a.Get("out") is { } outPath)
        {
            var runs = await MeasureSeries(settings, mode, Path.GetFileNameWithoutExtension(outPath), a, null);
            File.WriteAllText(outPath, OptiJson.Serialize(runs));
            Console.WriteLine(Loc.T($"\nЗамер сохранён: {outPath}"));
            return 0;
        }
        using var host = new CliHost();
        var s = SessionForWork(host, a);
        if (a.Has("mode")) s.MeasureMode = mode;
        bool after = a.Get("phase") is { } ph ? ph.Equals("after", StringComparison.OrdinalIgnoreCase) : s.Stage >= SessionStage.Applied;
        var series = await MeasureSeries(settings, s.MeasureMode, after ? Loc.T("после") : Loc.T("до"), a, s.GameProcess);
        if (after) host.Workflow.RecordAfter(s, series); else host.Workflow.RecordBefore(s, series);
        Console.WriteLine(Loc.T($"\nЗаписано в сессию {s.Id} как «{(after ? "после" : "до")}»."));
        return 0;
    }

    public static int Apply(Args a)
    {
        RequireWindows(Loc.T("Применение исправлений"));
        using var host = new CliHost();
        var wf = host.Workflow;
        var s = SessionForWork(host, a);
        if (a.Get("goal") is { } g) s.Goal = ParseGoal(g);
        var plan = wf.BuildPlan(s);
        Output.PrintPlan(plan);

        var requested = a.Get("fix")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = plan.Fixes.Where(p => requested is null ? p.Selected : requested.Contains(p.Fix.Id)).ToList();
        if (requested is not null)
            foreach (var id in requested.Where(id => chosen.All(c => !c.Fix.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                Output.Color(ConsoleColor.Yellow, Loc.T($"{id}: не входит в план для этого ПК и цели — пропущено.\n"));
        var separate = chosen.Where(c => c.ApplySeparately).ToList();
        if (separate.Count > 0 && chosen.Count > 1)
        {
            Output.Color(ConsoleColor.Yellow, Loc.T("Спорные настройки проверяются отдельно: ") + string.Join(", ", separate.Select(x => $"opti-cli ab --fix {x.Fix.Id}")) + "\n");
            chosen = chosen.Except(separate).ToList();
        }
        if (chosen.Count == 0) { Console.WriteLine(Loc.T("Нечего применять.")); return 0; }

        foreach (var (fix, opts) in ParseOptions(a)) s.FixOptions[fix] = opts;
        Console.WriteLine(Loc.T("\nБудут применены: ") + string.Join(", ", chosen.Select(c => c.Fix.Id)));
        if (!a.Has("yes") && !Output.Confirm(Loc.T("Продолжить? Перед изменениями будет создана точка восстановления."))) return 1;

        var report = wf.Apply(s, chosen.Select(c => c.Fix).ToList(), CliHost.ExePath, "resume");
        PrintApplyReport(report);
        Console.WriteLine(Loc.T($"Откат этой сессии: opti-cli rollback --session {s.Id}"));
        if (report.RebootRequired) OfferReboot(a);
        else Console.WriteLine(Loc.T("Дальше: «opti-cli measure» (замер «после»), затем «opti-cli compare»."));
        return report.Results.Any(x => x.Outcome == FixOutcome.Failed) ? 3 : 0;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseOptions(Args a)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in a.GetAll("option"))
        {
            int c = o.IndexOf(':'), e = o.IndexOf('=');
            if (c <= 0 || e <= c) throw new ArgumentException(Loc.T($"Неверная опция «{o}». Формат: FIX_ID:ключ=значение"));
            var fix = o[..c];
            if (!map.TryGetValue(fix, out var d)) map[fix] = d = new(StringComparer.OrdinalIgnoreCase);
            d[o[(c + 1)..e]] = o[(e + 1)..].Replace(';', '\n');
        }
        return map;
    }

    public static int Compare(Args a)
    {
        if (a.Positional.Count >= 2)
        {
            var before = OptiJson.Deserialize<List<MeasurementRun>>(File.ReadAllText(a.Positional[0]));
            var after = OptiJson.Deserialize<List<MeasurementRun>>(File.ReadAllText(a.Positional[1]));
            var result = new Comparer().Compare(before, after);
            Output.Header(Loc.T("Сравнение «до / после»"));
            PrintComparison(result);
            Console.WriteLine(Loc.T($"\nИтог: {result.Overall switch { Verdict.Better => "стало лучше", Verdict.Worse => "стало хуже", Verdict.NoChange => "без изменений (в пределах шума)", _ => "недостаточно данных" }}"));
            if (a.Get("csv") is { } csv) WriteCsv(csv, before, after);
            if (a.Get("html") is { } html)
            {
                var s = new TuneSession { Before = before, After = after };
                File.WriteAllText(html, HtmlReport.Build(new ReportData { Session = s, Comparison = result, AppVersion = AppInfo.Version }));
                Console.WriteLine(Loc.T($"HTML-отчёт: {html}"));
            }
            return 0;
        }
        RequireWindows(Loc.T("Сравнение сессии"));
        using var host = new CliHost();
        var session = SessionForWork(host, a, needAnalysis: false);
        var cmp = host.Workflow.Compare(session);
        Output.Header(Loc.T("Сравнение «до / после»"));
        PrintComparison(cmp.Result);
        Console.WriteLine("\n" + cmp.Message);
        var path = WriteReport(host, session, cmp, a.Get("html"));
        Console.WriteLine(Loc.T($"Отчёт: {path}"));
        if (a.Get("csv") is { } csv2) WriteCsv(csv2, session.Before, session.After);
        if (cmp.Recommendation == Recommendation.SuggestRollback && Output.Confirm(Loc.T("Откатить изменения этой сессии?")))
            PrintRollback(host.Workflow.RollbackSession(session));
        return 0;
    }

    private static void WriteCsv(string path, IReadOnlyList<MeasurementRun> before, IReadOnlyList<MeasurementRun> after)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("set,run,frame,frametime_ms");
        foreach (var (set, runs) in new[] { ("before", before), ("after", after) })
            for (int r = 0; r < runs.Count; r++)
                for (int f = 0; f < runs[r].FrameTimesMs.Count; f++)
                    w.WriteLine(FormattableString.Invariant($"{set},{r + 1},{f},{runs[r].FrameTimesMs[f]:0.###}"));
        Console.WriteLine(Loc.T($"Сырые фреймтаймы: {path}"));
    }
}
