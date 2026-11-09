using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;

namespace Opti.Tests;

public class ReportTests
{
    internal static List<MeasurementRun> Runs(string label, double baseMs, double spikeMs, int seed, Dictionary<string, int>? modes = null) =>
        Enumerable.Range(0, 3).Select(i =>
        {
            var rng = new Random(seed + i);
            var ft = Enumerable.Range(0, 7000).Select(_ => baseMs + (rng.NextDouble() - 0.5) * 1.6 + (rng.NextDouble() < 0.02 ? spikeMs * rng.NextDouble() + spikeMs / 2 : 0)).ToList();
            return new MeasurementRun
            {
                Label = $"{label} #{i + 1}", FrameTimesMs = ft, Source = "ETW",
                Conditions = new RunConditions { ProcessName = "cs2", OnAcPower = true },
                PresentModes = modes ?? new(), AvgPcLatencyMs = modes is null ? null : baseMs * 2.1,
            };
        }).ToList();

    internal static (ReportData Data, TuneSession Session) Sample()
    {
        var dir = Directory.CreateTempSubdirectory("opti-rep-").FullName;
        var profile = TestProfiles.Load("laptop-optimus");
        var reg = ProfileSeeder.CreateEmulation(profile, out _);
        var journal = new InMemoryChangeJournal();
        var wf = new TuneWorkflow(new OptiSettings(), new SessionStore(dir), reg, journal);
        var s = wf.StartSession(SystemAnalyzer.FromProfile(profile), UserGoal.Balanced);
        wf.RecordBefore(s, Runs("до", 9.0, 22, 1, new() { ["Composed: Flip"] = 7000, ["Hardware: Independent Flip"] = 14000 }));
        var plan = wf.BuildPlan(s);
        wf.Apply(s, plan.Fixes.Where(f => f.Selected && !f.ApplySeparately).Select(f => f.Fix).ToList());
        wf.RecordAfter(s, Runs("после", 6.9, 9, 50, new() { ["Hardware: Independent Flip"] = 21000 }));
        var cmp = wf.Compare(s);
        s.AbTests.Add(new AbTestRecord { FixId = FixIds.HagsAb, VariantB = "HAGS выключено", Phase = AbPhase.Decided, Verdict = Verdict.NoChange,
            RunsA = Runs("A", 7.0, 9, 70), RunsB = Runs("B", 7.0, 9, 80), Summary = "«HAGS выключено» не дал достоверного эффекта — возвращено исходное состояние." });
        var data = new ReportData { Session = s, Comparison = cmp.Result, CompareMessage = cmp.Message, Changes = journal.GetAll(), Catalog = FixCatalog.All(), AppVersion = "test" };
        Directory.Delete(dir, true);
        return (data, s);
    }

    [Fact]
    public void Report_ContainsAllSections_AndIsSelfContained()
    {
        var (data, _) = Sample();
        var html = HtmlReport.Build(data);
        Assert.Contains("Сравнение «до / после»", html);
        Assert.Contains("<polyline", html);
        Assert.Contains("<rect", html);
        Assert.Contains("Режим вывода кадров", html);
        Assert.Contains("A/B-проверка", html);
        Assert.Contains("Что изменено", html);
        Assert.Contains("Найденные проблемы", html);
        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        var outPath = Environment.GetEnvironmentVariable("OPTI_SAMPLE_REPORT");
        if (outPath is not null) File.WriteAllText(outPath, html);
    }

    [Fact]
    public void Report_EscapesUserData()
    {
        var (data, s) = Sample();
        s.Notes.Add("<script>alert(1)</script>");
        Assert.DoesNotContain("<script>alert", HtmlReport.Build(data));
    }

    [Fact]
    public void Report_WorksWithAnalysisOnly()
    {
        var profile = TestProfiles.Load("weak-pc");
        var s = new TuneSession { Profile = profile, Findings = TestProfiles.Findings(profile) };
        var html = HtmlReport.Build(new ReportData { Session = s });
        Assert.Contains("Перегрев", html);
        Assert.DoesNotContain("<polyline", html);
    }

    [Fact]
    public void Decimate_KeepsSpikes()
    {
        var ft = Enumerable.Repeat(7.0, 20000).ToList();
        ft[12345] = 80;
        var pts = SvgCharts.Decimate(ft, 500);
        Assert.True(pts.Count <= 1100);
        Assert.Contains(pts, p => p.Ms == 80);
    }
}
