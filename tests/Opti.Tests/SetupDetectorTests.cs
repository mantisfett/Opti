using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;

namespace Opti.Tests;

public class SetupDetectorTests
{
    [Fact]
    public void FreshInstall_FindsReinstallProblems()
    {
        var f = TestProfiles.Findings(TestProfiles.Load("fresh-install"));
        var ids = f.Select(x => x.Id).ToHashSet();
        Assert.Contains("MONITOR_ON_IGPU", ids);
        Assert.Contains("DEVICES_WITHOUT_DRIVERS", ids);
        Assert.Contains("FRESH_WINDOWS", ids);
        Assert.Contains("AMD_X3D_DRIVER", ids);
        Assert.Contains("PENDING_REBOOT", ids);
        Assert.Contains("MOUSE_ACCEL_ON", ids);
        Assert.Contains("ACCESSIBILITY_HOTKEYS_ON", ids);
        Assert.Contains("RAM_BELOW_RATED", ids);
        Assert.DoesNotContain("HYBRID_GAME_ON_IGPU", ids);      // это ПК, а не ноутбук с гибридной графикой
        // Отключённое пользователем устройство (код 22) не считается проблемой
        var dev = f.Single(x => x.Id == "DEVICES_WITHOUT_DRIVERS");
        Assert.DoesNotContain(dev.Evidence, e => e.Contains("Bluetooth"));
        Assert.Contains(dev.Evidence, e => e.Contains("драйвер не установлен"));
        Assert.Equal(Severity.Critical, f.Single(x => x.Id == "MONITOR_ON_IGPU").Severity);
    }

    [Fact]
    public void AllLinksAreHttpsOrSettings()
    {
        foreach (var name in TestProfiles.All)
            foreach (var f in TestProfiles.Findings(TestProfiles.Load(name)))
                Assert.All(f.Links, l => Assert.True(l.StartsWith("https://", StringComparison.Ordinal) || l.StartsWith("ms-settings:", StringComparison.Ordinal), l));
    }

    [Fact]
    public void GpuDriverMissing_AndIntelHybridWin10()
    {
        var p = TestProfiles.Load("weak-pc");
        p.Gpus[0].UsingBasicDriver = true;
        p.Cpu = new() { Name = "12th Gen Intel(R) Core(TM) i7-12700K", Vendor = Core.Profile.CpuVendor.Intel, Cores = 12, Threads = 20 };
        var ids = TestProfiles.Findings(p).Select(x => x.Id).ToHashSet();
        Assert.Contains("GPU_DRIVER_MISSING", ids);
        Assert.Contains("INTEL_HYBRID_WIN10", ids);

        p.Cpu = new() { Name = "12th Gen Intel(R) Core(TM) i5-12400F", Vendor = Core.Profile.CpuVendor.Intel, Cores = 6, Threads = 12 };
        Assert.DoesNotContain(TestProfiles.Findings(p), x => x.Id == "INTEL_HYBRID_WIN10"); // без E-ядер
    }

    [Fact]
    public void PageFileDisabled_IsFixedAndRolledBack()
    {
        var p = TestProfiles.Load("revios");
        p.Memory.PageFileDisabled = true;
        Assert.Contains(TestProfiles.Findings(p), x => x.Id == "PAGEFILE_DISABLED" && x.FixIds.Contains(FixIds.PageFileAuto));
        var reg = ProfileSeeder.CreateEmulation(p, out var map);
        var journal = new InMemoryChangeJournal();
        new ApplyService(reg, journal, null).Apply(p, new[] { new PageFileAutoFix() }, UserGoal.Maximum);
        Assert.Equal("?:\\pagefile.sys", map[ChangeType.Registry].Read(Targets.Reg(PageFileAutoFix.Key, "PagingFiles")).Value);
        new RollbackService(reg, journal).RollbackAll();
        Assert.Equal("", map[ChangeType.Registry].Read(Targets.Reg(PageFileAutoFix.Key, "PagingFiles")).Value);
    }

    [Fact]
    public void AccessibilityHotkeys_ClearOnlyHotkeyBit()
    {
        var p = TestProfiles.Load("fresh-install");
        var reg = ProfileSeeder.CreateEmulation(p, out var map);
        new ApplyService(reg, new InMemoryChangeJournal(), null).Apply(p, new[] { new AccessibilityHotkeysOffFix() }, UserGoal.Maximum);
        Assert.Equal("506", map[ChangeType.Registry].Read(Targets.Reg(AccessibilityHotkeysOffFix.StickyKey, "Flags")).Value);
        Assert.Equal("122", map[ChangeType.Registry].Read(Targets.Reg(AccessibilityHotkeysOffFix.FilterKey, "Flags")).Value);
    }

    [Fact]
    public void MouseAccel_SelectedOnlyForLatencyGoal()
    {
        var p = TestProfiles.Load("fresh-install");
        var engine = new StrategyEngine(FixCatalog.All());
        Assert.True(engine.Build(p, TestProfiles.Findings(p), UserGoal.Maximum).Fixes.Single(f => f.Fix.Id == FixIds.MouseAccelOff).Selected);
        Assert.False(engine.Build(p, TestProfiles.Findings(p), UserGoal.Balanced).Fixes.Single(f => f.Fix.Id == FixIds.MouseAccelOff).Selected);
    }
}
