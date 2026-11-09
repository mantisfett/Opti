using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;

namespace Opti.Tests;

public class RollbackTests
{
    private static (BackendRegistry Reg, Dictionary<ChangeType, InMemoryBackend> Map) Backends()
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        // «Реальное» состояние машины до Opti
        map[ChangeType.PowerSetting].Seed(Targets.ActiveScheme, StateValue.Str("381b4222-f694-41f0-9685-ff5bb260df2e"));
        map[ChangeType.Registry].Seed(Targets.Reg(MpoResetFix.Key, "OverlayTestMode"), StateValue.DWord(5));
        map[ChangeType.Registry].Seed(Targets.Reg(GpuPreferenceDgpuFix.Key, WindowedOptimizationsFix.ValueName), StateValue.Str("AutoHDREnable=1;"));
        map[ChangeType.Bcd].Seed(Targets.Bcd("useplatformclock"), StateValue.Str("Yes"));
        map[ChangeType.Display].Seed(Targets.DisplayRefresh(@"\\.\DISPLAY1"), StateValue.Str("60"));
        return (reg, map);
    }

    private static Dictionary<ChangeType, IReadOnlyDictionary<string, StateValue>> Snapshot(Dictionary<ChangeType, InMemoryBackend> map) =>
        map.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());

    private static void AssertSameState(Dictionary<ChangeType, IReadOnlyDictionary<string, StateValue>> a,
                                        Dictionary<ChangeType, IReadOnlyDictionary<string, StateValue>> b)
    {
        foreach (var type in a.Keys)
        {
            var x = a[type]; var y = b[type];
            Assert.Equal(x.Keys.OrderBy(k => k), y.Keys.OrderBy(k => k));
            foreach (var k in x.Keys) Assert.Equal(x[k], y[k]);
        }
    }

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Options = new()
    {
        [FixIds.StartupDisable] = new Dictionary<string, string> { ["items"] = "Teams\nSpotify" },
        [FixIds.BcdTimersReset] = new Dictionary<string, string> { ["bitlockerAcknowledged"] = "1" },
    };

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void ApplyEverything_ThenRollbackAll_RestoresExactState(string profileName)
    {
        var profile = TestProfiles.Load(profileName);
        var reg = ProfileSeeder.CreateEmulation(profile, out var map);
        var original = Snapshot(map);
        var journal = new InMemoryChangeJournal();

        var fixes = FixCatalog.All().Where(f => f.IsApplicable(profile)).ToList();
        var report = new ApplyService(reg, journal, null).Apply(profile, fixes, UserGoal.Maximum, Options);
        Assert.DoesNotContain(report.Results, r => r.Outcome == FixOutcome.Failed && !r.FixId.StartsWith("OBS_GAME", StringComparison.Ordinal));
        // После применения каждое «уже применено» должно стать правдой хотя бы для данных эмуляции
        Assert.True(report.Results.Count(r => r.Outcome == FixOutcome.Applied) > 0);

        new RollbackService(reg, journal).RollbackAll();
        AssertSameState(original, Snapshot(map));
        Assert.All(journal.GetAll(), r => Assert.True(r.RolledBack));
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> Variant(string v) =>
        new() { [FixIds.HagsAb] = new Dictionary<string, string> { ["variant"] = v } };

    public static IEnumerable<object[]> ProfileNames() => TestProfiles.All.Select(n => new object[] { n });

    [Fact]
    public void ReapplyingDoesNotOverwriteOriginal()
    {
        var profile = TestProfiles.Load("desktop-rtx");
        var (reg, map) = Backends();
        var journal = new InMemoryChangeJournal();
        var svc = new ApplyService(reg, journal, null);
        var hags = new HagsAbFix();

        // HAGS: исходно ключа нет → «on» → «off» → откат должен вернуть «нет ключа», а не «on»
        svc.Apply(profile, new[] { hags }, UserGoal.Maximum, Variant("on"));
        svc.Apply(profile, new[] { hags }, UserGoal.Maximum, Variant("off"));
        var target = Targets.Reg(HagsAbFix.Key, "HwSchMode");
        Assert.Equal(StateValue.DWord(1), map[ChangeType.Registry].Read(target));
        Assert.All(journal.GetAll().Where(r => r.Target == target), r => Assert.False(r.Before.Exists));

        new RollbackService(reg, journal).RollbackFix(FixIds.HagsAb);
        Assert.False(map[ChangeType.Registry].Read(target).Exists);
    }

    [Fact]
    public void FailedWriteMidFix_RollsBackPartialChanges()
    {
        var profile = TestProfiles.Load("laptop-optimus");
        var (reg, map) = Backends();
        var before = Snapshot(map);
        var journal = new InMemoryChangeJournal();
        map[ChangeType.Registry].FailWriteWhen = t => t.Contains("AppCaptureEnabled");

        var report = new ApplyService(reg, journal, null).Apply(profile, new[] { new GameDvrOffFix() }, UserGoal.Maximum);
        var r = Assert.Single(report.Results);
        Assert.Equal(FixOutcome.Failed, r.Outcome);
        AssertSameState(before, Snapshot(map));
    }

    [Fact]
    public void JournalFailure_PreventsChange()
    {
        var profile = TestProfiles.Load("desktop-rtx");
        var (reg, map) = Backends();
        var before = Snapshot(map);
        var report = new ApplyService(reg, new BrokenJournal(), null).Apply(profile, new[] { new GameModeOnFix() }, UserGoal.Maximum);
        Assert.Equal(FixOutcome.Failed, report.Results.Single().Outcome);
        AssertSameState(before, Snapshot(map));
    }

    [Fact]
    public void RollbackOneFix_LeavesOthers()
    {
        var profile = TestProfiles.Load("desktop-rtx");
        var (reg, map) = Backends();
        var journal = new InMemoryChangeJournal();
        new ApplyService(reg, journal, null).Apply(profile, new IFix[] { new GameModeOnFix(), new MpoResetFix() }, UserGoal.Maximum);
        Assert.False(map[ChangeType.Registry].Read(Targets.Reg(MpoResetFix.Key, "OverlayTestMode")).Exists);

        new RollbackService(reg, journal).RollbackFix(FixIds.MpoReset);
        Assert.Equal(StateValue.DWord(5), map[ChangeType.Registry].Read(Targets.Reg(MpoResetFix.Key, "OverlayTestMode")));
        Assert.Equal(StateValue.DWord(1), map[ChangeType.Registry].Read(Targets.Reg(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled")));
    }

    [Fact]
    public void WindowedOptimizations_PreservesOtherGlobalSettings()
    {
        var profile = TestProfiles.Load("desktop-rtx");
        var (reg, map) = Backends();
        new ApplyService(reg, new InMemoryChangeJournal(), null).Apply(profile, new[] { new WindowedOptimizationsFix() }, UserGoal.Maximum);
        var v = map[ChangeType.Registry].Read(Targets.Reg(GpuPreferenceDgpuFix.Key, WindowedOptimizationsFix.ValueName)).Value;
        Assert.Equal("AutoHDREnable=1;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;", v);
    }

    [Fact]
    public void GpuPreference_SkipsGamesWithUserChoice()
    {
        var profile = TestProfiles.Load("laptop-optimus");
        var (reg, map) = Backends();
        var report = new ApplyService(reg, new InMemoryChangeJournal(), null).Apply(profile, new[] { new GpuPreferenceDgpuFix() }, UserGoal.Maximum);
        var changes = report.Results.Single().Changes;
        Assert.Single(changes);
        Assert.EndsWith("cs2.exe", changes[0].Target);
        Assert.Equal("GpuPreference=2;", changes[0].After.Value);
    }

    [Fact]
    public void BcdFix_RequiresBitLockerAcknowledgement()
    {
        var profile = TestProfiles.Load("desktop-rtx");
        profile.Security.BitLockerOnSystemDrive = true;
        var (reg, map) = Backends();
        var report = new ApplyService(reg, new InMemoryChangeJournal(), null).Apply(profile, new[] { new BcdTimersResetFix() }, UserGoal.Maximum);
        Assert.Equal(FixOutcome.Failed, report.Results.Single().Outcome);
        Assert.True(map[ChangeType.Bcd].Read(Targets.Bcd("useplatformclock")).Exists);
    }

    [Fact]
    public void SqliteJournal_PersistsAndRollsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opti-test-{Guid.NewGuid():N}.db");
        try
        {
            var profile = TestProfiles.Load("desktop-rtx");
            var (reg, map) = Backends();
            var original = Snapshot(map);
            string session;
            using (var journal = new SqliteChangeJournal(path))
            {
                var rep = new ApplyService(reg, journal, null).Apply(profile, new IFix[] { new MpoResetFix(), new PowerSchemePerfFix(), new DisplayMaxHzFix() }, UserGoal.Maximum);
                session = rep.SessionId;
                Assert.Equal(2, rep.Results.Count(r => r.Outcome == FixOutcome.Applied)); // экран уже на максимуме
            }
            using (var journal = new SqliteChangeJournal(path)) // «после перезапуска программы»
            {
                Assert.Equal(2, journal.GetAll().Count);
                Assert.Single(journal.GetSessions());
                Assert.Equal(StateValue.DWord(5), journal.GetActiveOriginal(ChangeType.Registry, Targets.Reg(MpoResetFix.Key, "OverlayTestMode")));
                new RollbackService(reg, journal).RollbackSession(session);
                Assert.All(journal.GetAll(), r => Assert.True(r.RolledBack));
            }
            AssertSameState(original, Snapshot(map));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void FileBackend_BacksUpAndRestoresObsScenes()
    {
        var dir = Directory.CreateTempSubdirectory("opti-obs-").FullName;
        try
        {
            var scene = Path.Combine(dir, "Untitled.json");
            var originalJson = """{"name":"Untitled","sources":[{"id":"monitor_capture","versioned_id":"monitor_capture","name":"Экран","settings":{"monitor":1}},{"id":"wasapi_input_capture","name":"Mic"}]}""";
            File.WriteAllText(scene, originalJson);

            var profile = TestProfiles.Load("laptop-optimus");
            profile.Obs!.SceneFilesWithDisplayCapture = new() { scene };
            var reg = new BackendRegistry(new IStateBackend[] { new FileBackend(Path.Combine(dir, "backups")) });
            var journal = new InMemoryChangeJournal();
            var rep = new ApplyService(reg, journal, null).Apply(profile, new[] { new ObsGameCaptureFix() }, UserGoal.Balanced);
            Assert.Equal(FixOutcome.Applied, rep.Results.Single().Outcome);
            var changed = File.ReadAllText(scene);
            Assert.Contains("game_capture", changed);
            Assert.DoesNotContain("monitor_capture", changed);
            Assert.Contains("wasapi_input_capture", changed);

            new RollbackService(reg, journal).RollbackAll();
            Assert.Equal(originalJson, File.ReadAllText(scene));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DryRun_DoesNotTouchUnderlyingBackend()
    {
        var (reg, map) = Backends();
        var before = Snapshot(map);
        var dry = new DryRunBackendRegistry(reg);
        var rep = new ApplyService(dry, new InMemoryChangeJournal(), null)
            .Apply(TestProfiles.Load("desktop-rtx"), new IFix[] { new MpoResetFix(), new GameModeOnFix() }, UserGoal.Maximum);
        Assert.All(rep.Results, r => Assert.Equal(FixOutcome.Applied, r.Outcome));
        AssertSameState(before, Snapshot(map));
    }

    private sealed class BrokenJournal : IChangeJournal
    {
        public StateValue? GetActiveOriginal(ChangeType type, string target) => null;
        public long Append(ChangeRecord record) => throw new IOException("диск полон");
        public void Remove(long recordId) { }
        public void MarkRolledBack(long recordId, DateTimeOffset at) { }
        public IReadOnlyList<ChangeRecord> GetAll() => Array.Empty<ChangeRecord>();
        public void SaveSession(SessionInfo session) { }
        public IReadOnlyList<SessionInfo> GetSessions() => Array.Empty<SessionInfo>();
    }
}
