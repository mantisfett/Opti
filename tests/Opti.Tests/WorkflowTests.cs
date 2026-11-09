using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Sessions;

namespace Opti.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-wf-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeScheduler : IResumeScheduler
    {
        public string? Scheduled;
        public void ScheduleOnLogon(string exePath, string arguments) => Scheduled = exePath + " " + arguments;
        public void Cancel() => Scheduled = null;
        public bool IsScheduled() => Scheduled is not null;
    }

    private sealed class FakeCrashes : IResumeScheduler, ICrashEventSource
    {
        public List<CrashEvent> Events { get; } = new();
        public IReadOnlyList<CrashEvent> Since(DateTimeOffset time) => Events.Where(e => e.Time >= time).ToList();
        public void ScheduleOnLogon(string exePath, string arguments) { }
        public void Cancel() { }
        public bool IsScheduled() => false;
    }

    private (TuneWorkflow Wf, Dictionary<ChangeType, InMemoryBackend> Map, FakeScheduler Sched, FakeCrashes Crashes, TuneSession Session) Create(string profileName, UserGoal goal, OptiSettings? settings = null)
    {
        var profile = TestProfiles.Load(profileName);
        var reg = ProfileSeeder.CreateEmulation(profile, out var map);
        var sched = new FakeScheduler();
        var crashes = new FakeCrashes();
        var wf = new TuneWorkflow(settings ?? new OptiSettings(), new SessionStore(Path.Combine(_dir, "sessions")), reg, new InMemoryChangeJournal(), null, sched, crashes);
        var s = wf.StartSession(SystemAnalyzer.FromProfile(profile), goal);
        return (wf, map, sched, crashes, s);
    }

    private static List<MeasurementRun> Runs(double baseMs, double spikeMs, int seed, int n = 3) =>
        Enumerable.Range(0, n).Select(i =>
        {
            var rng = new Random(seed + i);
            var ft = Enumerable.Range(0, 6000).Select(_ => baseMs + (rng.NextDouble() - 0.5) * 2 + (rng.NextDouble() < 0.02 ? spikeMs : 0)).ToList();
            return new MeasurementRun { Label = $"r{seed + i}", FrameTimesMs = ft, Conditions = new RunConditions { ProcessName = "bench", OnAcPower = true } };
        }).ToList();

    [Fact]
    public void FullScenario_RebootContinuation_SelfCheck_Compare()
    {
        var (wf, map, sched, _, s) = Create("desktop-rtx", UserGoal.Maximum);
        wf.RecordBefore(s, Runs(8.5, 25, 1));
        var plan = wf.BuildPlan(s);
        var fixes = plan.Fixes.Where(f => f.Selected && !f.ApplySeparately).Select(f => f.Fix).ToList();
        var report = wf.Apply(s, fixes, resumeExe: @"C:\Program Files\Opti\Opti.exe");

        Assert.True(report.RebootRequired);                    // MPO / BCD требуют перезагрузки
        Assert.Equal(SessionStage.AwaitingReboot, s.Stage);
        Assert.NotNull(sched.Scheduled);
        Assert.Contains(FixIds.MpoReset, s.AppliedFixIds);

        // «Перезагрузка»: момент применения раньше последней загрузки
        Assert.Null(wf.ResumeIfRebooted(s));                    // перезагрузки ещё не было
        s.AppliedAt = RebootHelper.LastBootTime.AddMinutes(-5);
        var check = wf.ResumeIfRebooted(s);
        Assert.NotNull(check);
        Assert.True(check!.Healthy);
        Assert.True(check.Verified > 0);
        Assert.Null(sched.Scheduled);                           // задача продолжения снята
        Assert.Equal(SessionStage.VerifiedAfterReboot, s.Stage);

        wf.RecordAfter(s, Runs(7.0, 8, 100));
        var cmp = wf.Compare(s);
        Assert.Equal(Recommendation.Keep, cmp.Recommendation);
        Assert.Equal(SessionOutcome.Improved, s.Outcome);

        // Сессия сохраняется и читается обратно
        var loaded = wf.Store.Load(s.Id)!;
        Assert.Equal(SessionStage.Compared, loaded.Stage);
        Assert.Equal(3, loaded.Before.Count);
        Assert.Equal(s.AppliedFixIds, loaded.AppliedFixIds);
    }

    [Fact]
    public void SelfCheck_DetectsNotAppliedAndCrashes()
    {
        var (wf, map, _, crashes, s) = Create("desktop-rtx", UserGoal.Maximum);
        wf.Apply(s, new IFix[] { new MpoResetFix(), new GameModeOnFix() });
        s.Stage = SessionStage.AwaitingReboot;
        s.AppliedAt = RebootHelper.LastBootTime.AddMinutes(-5);
        // Кто-то вернул ключ MPO, и был синий экран
        map[ChangeType.Registry].Seed(Targets.Reg(MpoResetFix.Key, "OverlayTestMode"), StateValue.DWord(5));
        crashes.Events.Add(new CrashEvent(DateTimeOffset.Now, 1001, "BugCheck", "0x0000009f"));

        var check = wf.ResumeIfRebooted(s)!;
        Assert.Contains(check.NotApplied, x => x.Contains("OverlayTestMode"));
        Assert.True(check.SuggestRollback);
        Assert.False(check.Healthy);
    }

    [Fact]
    public void WorseResult_SuggestsRollback_AndRollbackRestores()
    {
        var (wf, map, _, _, s) = Create("desktop-rtx", UserGoal.Maximum);
        var before = map[ChangeType.Registry].Snapshot();
        wf.RecordBefore(s, Runs(7.0, 8, 1));
        wf.Apply(s, new IFix[] { new GameModeOnFix(), new MpoResetFix() });
        wf.RecordAfter(s, Runs(8.5, 25, 50));
        var cmp = wf.Compare(s);
        Assert.Equal(Recommendation.SuggestRollback, cmp.Recommendation);

        var rb = wf.RollbackSession(s);
        Assert.Empty(rb.Errors);
        Assert.Equal(SessionOutcome.RolledBack, s.Outcome);
        Assert.Equal(before.OrderBy(k => k.Key), map[ChangeType.Registry].Snapshot().OrderBy(k => k.Key));
    }

    [Fact]
    public void NoEffect_RespectsSetting()
    {
        var (wf, _, _, _, s) = Create("desktop-rtx", UserGoal.Maximum, new OptiSettings { RollbackWhenNoEffect = true });
        wf.RecordBefore(s, Runs(8.0, 10, 1));
        wf.RecordAfter(s, Runs(8.0, 10, 1000));
        Assert.Equal(Recommendation.SuggestRollback, wf.Compare(s).Recommendation);
        Assert.Equal(SessionOutcome.NoEffect, s.Outcome);
    }

    [Fact]
    public void ExcludedInSettings_RemovedFromPlan()
    {
        var (wf, _, _, _, s) = Create("desktop-rtx", UserGoal.Maximum, new OptiSettings { ExcludedFixIds = { FixIds.MpoReset } });
        var plan = wf.BuildPlan(s);
        Assert.DoesNotContain(plan.Fixes, f => f.Fix.Id == FixIds.MpoReset);
        Assert.Contains(plan.Excluded, e => e.FixId == FixIds.MpoReset && e.Reason.Contains("настройках"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbTest_KeepsOnlyProvenImprovement(bool bIsBetter)
    {
        var (wf, map, sched, _, s) = Create("desktop-rtx", UserGoal.Maximum);
        var target = Targets.Reg(HagsAbFix.Key, "HwSchMode");
        var original = map[ChangeType.Registry].Read(target);   // HAGS выключен (1)

        wf.StartAb(s, FixIds.HagsAb);
        wf.RecordAbA(s, Runs(8.0, 20, 1));
        var rep = wf.ApplyAbB(s, resumeExe: "Opti.exe");
        Assert.True(rep.RebootRequired);
        Assert.Equal(AbPhase.AwaitRebootB, wf.CurrentAb(s).Phase);
        Assert.Equal(StateValue.DWord(2), map[ChangeType.Registry].Read(target));
        Assert.NotNull(sched.Scheduled);

        s.AppliedAt = RebootHelper.LastBootTime.AddMinutes(-1);
        Assert.True(wf.ResumeAbIfRebooted(s));
        var (ab, _, rb) = wf.DecideAb(s, bIsBetter ? Runs(7.0, 6, 200) : Runs(8.0, 20, 300));

        Assert.Equal(AbPhase.Decided, ab.Phase);
        Assert.Equal(bIsBetter, ab.KeptB);
        if (bIsBetter)
        {
            Assert.Equal(StateValue.DWord(2), map[ChangeType.Registry].Read(target));
            Assert.Null(rb);
        }
        else
        {
            Assert.Equal(original, map[ChangeType.Registry].Read(target));
            Assert.Contains("Перезагрузите", ab.Summary);
        }
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(_dir, "settings.json");
        new OptiSettings { DefaultGoal = UserGoal.Maximum, ExcludedFixIds = { "HAGS_AB" }, MeasureRuns = 5 }.Save(path);
        var s = OptiSettings.Load(path);
        Assert.Equal(UserGoal.Maximum, s.DefaultGoal);
        Assert.Equal(5, s.MeasureRuns);
        Assert.Contains("HAGS_AB", s.ExcludedFixIds);
        Assert.Equal(3, OptiSettings.Load(Path.Combine(_dir, "missing.json")).MeasureRuns);
    }
}

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.0-beta", true)]
    [InlineData("v1.0.0-beta", "1.0.0", false)]
    [InlineData("v0.9.9", "1.0.0", false)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("v1.3.0-beta.2", "1.3.0-beta.1", true)]
    [InlineData("v1.3.0-beta.10", "1.3.0-beta.2", true)]
    [InlineData("v1.3.0-beta", "1.3.0-beta.1", false)]
    [InlineData("v1.3.0-rc.1", "1.3.0-beta.5", true)]
    [InlineData("v1.3.0-beta.1", "1.3.0-rc.1", false)]
    [InlineData("v1.3.0+build.7", "1.3.0", false)]
    [InlineData("v1.3.0", "1.3.0-rc.2", true)]
    public void ComparesVersions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));
}
