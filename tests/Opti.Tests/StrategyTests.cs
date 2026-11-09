using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Fixes.Catalog;

namespace Opti.Tests;

public class StrategyTests
{
    private static StrategyPlan Plan(string profile, UserGoal goal)
    {
        var p = TestProfiles.Load(profile);
        return new StrategyEngine(FixCatalog.All()).Build(p, TestProfiles.Findings(p), goal);
    }

    [Fact]
    public void DeviceClassification_MatchesProfiles()
    {
        Assert.Equal(DeviceClass.HybridLaptop, DeviceClassification.From(TestProfiles.Load("laptop-optimus")).Class);
        Assert.Equal(DeviceClass.DesktopDgpu, DeviceClassification.From(TestProfiles.Load("desktop-rtx")).Class);
        Assert.Equal(DeviceClass.IgpuLaptop, DeviceClassification.From(TestProfiles.Load("laptop-igpu")).Class);
        Assert.True(DeviceClassification.From(TestProfiles.Load("weak-pc")).IsWeak);
        Assert.True(DeviceClassification.From(TestProfiles.Load("revios")).IsModifiedOs);
        Assert.False(DeviceClassification.From(TestProfiles.Load("desktop-rtx")).IsWeak);
    }

    [Fact]
    public void FixesComeOnlyFromTriggeredDetectors()
    {
        foreach (var name in TestProfiles.All)
        {
            var p = TestProfiles.Load(name);
            var triggered = TestProfiles.Findings(p).SelectMany(f => f.FixIds).ToHashSet();
            var plan = new StrategyEngine(FixCatalog.All()).Build(p, TestProfiles.Findings(p), UserGoal.Maximum);
            Assert.All(plan.Fixes, f => Assert.Contains(f.Fix.Id, triggered));
        }
    }

    [Fact]
    public void Minimal_KeepsOnlyRepairsSelected()
    {
        var plan = Plan("desktop-rtx", UserGoal.Minimal);
        // настройки под производительность остаются в плане, но не отмечены
        var scheme = plan.Fixes.Single(f => f.Fix.Id == FixIds.PowerSchemePerf);
        Assert.False(scheme.Selected);
        Assert.Contains(scheme.Notes, n => n.Contains("Минимальная"));
        Assert.All(plan.Fixes.Where(f => f.Selected), f => Assert.Equal(FixIntent.Repair, f.Fix.Intent));
        Assert.DoesNotContain(plan.Fixes, f => f.Selected && f.Fix.RequiresAbTest);
        // исправления ошибок отмечены
        Assert.True(plan.Fixes.Single(f => f.Fix.Id == FixIds.MpoReset).Selected);

        var max = Plan("desktop-rtx", UserGoal.Maximum);
        Assert.Contains(max.Fixes, f => f.Fix.Id == FixIds.PowerSchemePerf && f.Selected);
    }

    [Fact]
    public void Modes_AreNested_MinimalWithinBalancedWithinMaximum()
    {
        foreach (var name in TestProfiles.All)
        {
            HashSet<string> Sel(UserGoal g) => Plan(name, g).Fixes.Where(f => f.Selected).Select(f => f.Fix.Id).ToHashSet();
            var min = Sel(UserGoal.Minimal);
            var bal = Sel(UserGoal.Balanced);
            var max = Sel(UserGoal.Maximum);
            Assert.True(min.IsSubsetOf(bal), $"{name}: Минимальная ⊄ Баланс");
            Assert.True(bal.IsSubsetOf(max), $"{name}: Баланс ⊄ Максимальная");
        }
    }

    [Fact]
    public void Modes_DifferForPowerThrottlingAndMouse()
    {
        var p = TestProfiles.Load("desktop-rtx");
        p.Power.PowerThrottlingDisabledGlobally = false;
        p.Input.MouseAcceleration = true;
        var engine = new StrategyEngine(FixCatalog.All());
        var findings = TestProfiles.Findings(p);
        bool Selected(UserGoal g, string id) => engine.Build(p, findings, g).Fixes.SingleOrDefault(f => f.Fix.Id == id)?.Selected ?? false;
        Assert.True(Selected(UserGoal.Maximum, FixIds.MouseAccelOff));
        Assert.False(Selected(UserGoal.Balanced, FixIds.MouseAccelOff));
        Assert.False(Selected(UserGoal.Minimal, FixIds.MouseAccelOff));
    }

    [Theory]
    [InlineData("\"LowLatency\"", UserGoal.Maximum)]
    [InlineData("\"MaxFps\"", UserGoal.Maximum)]
    [InlineData("\"Smoothness\"", UserGoal.Balanced)]
    [InlineData("\"Streaming\"", UserGoal.Balanced)]
    [InlineData("\"QuietCool\"", UserGoal.Minimal)]
    [InlineData("\"Minimal\"", UserGoal.Minimal)]
    [InlineData("\"что-то новое\"", UserGoal.Balanced)]
    public void LegacyGoals_AreReadAsModes(string json, UserGoal expected)
    {
        Assert.Equal(expected, Opti.Core.Json.OptiJson.Deserialize<Holder>($"{{\"goal\": {json}}}").Goal);
        Assert.Contains($"\"{expected}\"", Opti.Core.Json.OptiJson.Serialize(new Holder { Goal = expected }));
    }

    public sealed class Holder { public UserGoal Goal { get; set; } }

    [Fact]
    public void Laptop_DoesNotSwitchWholeSchemeButTunesAc()
    {
        var plan = Plan("laptop-optimus", UserGoal.Maximum);
        Assert.DoesNotContain(plan.Fixes, f => f.Fix.Id == FixIds.PowerSchemePerf);
        Assert.Contains(plan.Fixes, f => f.Fix.Id == FixIds.PowerAcTuning);
    }

    [Fact]
    public void RiskyAndAbFixes_GoLastAndSeparately()
    {
        var plan = Plan("desktop-rtx", UserGoal.Maximum);
        var hags = plan.Fixes.Single(f => f.Fix.Id == FixIds.HagsAb);
        Assert.True(hags.ApplySeparately);
        var lastSafe = plan.Fixes.Where(f => !f.ApplySeparately).Max(f => f.Order);
        Assert.All(plan.Fixes.Where(f => f.ApplySeparately), f => Assert.True(f.Order > lastSafe));
        // Сначала «бесплатные»: экран/GPU/OBS раньше питания
        var order = plan.Fixes.Select(f => f.Fix.Id).ToList();
        Assert.True(order.IndexOf(FixIds.MpoReset) < order.IndexOf(FixIds.PowerSchemePerf));
        Assert.True(order.IndexOf(FixIds.PowerSchemePerf) < order.IndexOf(FixIds.PowerAcTuning));
    }

    [Fact]
    public void BitLocker_UnchecksBcdChanges()
    {
        var p = TestProfiles.Load("desktop-rtx");
        p.Security.BitLockerOnSystemDrive = true;
        var plan = new StrategyEngine(FixCatalog.All()).Build(p, TestProfiles.Findings(p), UserGoal.Maximum);
        var bcd = plan.Fixes.Single(f => f.Fix.Id == FixIds.BcdTimersReset);
        Assert.False(bcd.Selected);
        Assert.Contains(bcd.Notes, n => n.Contains("BitLocker"));
    }

    [Fact]
    public void ModifiedOs_DoesNotDuplicateOrForceItsSettings()
    {
        var plan = Plan("revios", UserGoal.Maximum);
        Assert.DoesNotContain(plan.Fixes, f => f.Fix.Id == FixIds.TimerResGlobal); // уже включено сборкой
        var mpo = plan.Fixes.Single(f => f.Fix.Id == FixIds.MpoReset);
        Assert.False(mpo.Selected);
        Assert.Contains(mpo.Notes, n => n.Contains("ReviOS"));
        var bcd = plan.Fixes.Single(f => f.Fix.Id == FixIds.BcdTimersReset);
        Assert.False(bcd.Selected);
    }

    [Fact]
    public void StartupFix_NeedsUserChoice()
    {
        var plan = Plan("laptop-igpu", UserGoal.Balanced);
        var s = plan.Fixes.Single(f => f.Fix.Id == FixIds.StartupDisable);
        Assert.False(s.Selected);
    }

    [Fact]
    public void ObsFixes_InPlanInEveryMode()
    {
        var plan = Plan("laptop-optimus", UserGoal.Balanced);
        Assert.Contains(plan.Fixes, f => f.Fix.Id == FixIds.ObsGameCapture && f.Selected);
        Assert.Contains(plan.Fixes, f => f.Fix.Id == FixIds.ObsNoAdmin && f.Selected);
        Assert.Contains(plan.Fixes.Single(f => f.Fix.Id == FixIds.GameDvrOff).Notes, n => n.Contains("Game Bar"));
    }

    [Fact]
    public void AdviceOnlyFindings_AreSeparated()
    {
        var plan = Plan("weak-pc", UserGoal.Maximum);
        Assert.Contains(plan.AdviceOnly, f => f.Id == "THERMAL_THROTTLE");
        Assert.DoesNotContain(plan.Fixes, f => f.TriggeredBy.Contains("THERMAL_THROTTLE"));
    }
}
