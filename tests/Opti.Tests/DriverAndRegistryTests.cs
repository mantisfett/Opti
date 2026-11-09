using Opti.Core.Drivers;
using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Registry;
using Opti.Core.Software;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;

namespace Opti.Tests;

public class DriverAndRegistryTests
{
    // ---------- Реестр ----------

    [Fact]
    public void RegistryAudit_FindsHarmfulTweaks_IgnoresDefaults()
    {
        var findings = TestProfiles.Findings(TestProfiles.Load("weak-pc"));
        var f = Assert.Single(findings, x => x.Id == "REGISTRY_TWEAKS");
        Assert.Equal(Severity.Important, f.Severity);
        Assert.Contains(FixIds.RegistryPerfDefaults, f.FixIds);
        Assert.Contains(f.Evidence, e => e.Contains("LargeSystemCache"));
        Assert.Contains(f.Evidence, e => e.Contains("TdrLevel"));
        Assert.Contains(f.Evidence, e => e.Contains("SysMain"));
        // Стандартные значения (Win32PrioritySeparation = 2, Prefetch не задан) — не находки
        Assert.DoesNotContain(f.Evidence, e => e.Contains("Win32PrioritySeparation"));
        Assert.DoesNotContain(f.Evidence, e => e.Contains("EnablePrefetcher"));
        Assert.Contains(RegistryAuditCatalog.TdrDocs, f.Links);
        Assert.DoesNotContain(findings, x => x.Id == "REGISTRY_SECURITY");
    }

    [Fact]
    public void RegistryAudit_CleanProfiles_HaveNoFindings()
    {
        foreach (var name in new[] { "desktop-rtx", "laptop-optimus", "laptop-igpu", "fresh-install" })
            Assert.DoesNotContain(TestProfiles.FindingIds(name), id => id.StartsWith("REGISTRY_", StringComparison.Ordinal));
    }

    [Fact]
    public void RegistryRules_AreConsistent()
    {
        Assert.Equal(RegistryAuditCatalog.All.Count, RegistryAuditCatalog.All.Select(r => r.Id).Distinct().Count());
        foreach (var r in RegistryAuditCatalog.All)
        {
            Assert.Matches(@"^HK(LM|CU)\\", r.Key);
            Assert.False(r.IsBad(null), $"{r.Id}: отсутствие значения не может быть нарушением");
            if (r.DefaultDword is { } d) Assert.False(r.IsBad(d.ToString()), $"{r.Id}: стандартное значение считается нарушением");
            if (r.Link is not null) Assert.StartsWith("https://", r.Link);
        }
    }

    [Fact]
    public void RegistryFix_RestoresDefaults_AndRollsBackExactly()
    {
        var p = TestProfiles.Load("weak-pc");
        var reg = ProfileSeeder.CreateEmulation(p, out var map);
        var r = map[ChangeType.Registry];
        var before = r.Snapshot();
        var journal = new InMemoryChangeJournal();
        var fix = new RegistryPerfDefaultsFix();
        Assert.True(fix.IsApplicable(p));

        var report = new ApplyService(reg, journal, null).Apply(p, new IFix[] { fix }, UserGoal.Maximum);
        Assert.Equal(FixOutcome.Applied, Assert.Single(report.Results).Outcome);
        var mm = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        Assert.Equal(StateValue.DWord(0), r.Read(Targets.Reg(mm, "LargeSystemCache")));
        Assert.False(r.Read(Targets.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel")).Exists);
        Assert.Equal(StateValue.DWord(2), r.Read(Targets.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\SysMain", "Start")));
        // Стандартное значение не трогаем
        Assert.Equal(StateValue.DWord(2), r.Read(Targets.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation")));

        new RollbackService(reg, journal).RollbackAll();
        var after = r.Snapshot();
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var k in before.Keys) Assert.Equal(before[k], after[k]);
    }

    [Fact]
    public void RegistryFix_RulesOption_LimitsChanges()
    {
        var p = TestProfiles.Load("weak-pc");
        var reg = ProfileSeeder.CreateEmulation(p, out var map);
        var options = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [FixIds.RegistryPerfDefaults] = new Dictionary<string, string> { [RegistryDefaultsFixBase.RulesOption] = "TDR_DISABLED" },
        };
        new ApplyService(reg, new InMemoryChangeJournal(), null).Apply(p, new IFix[] { new RegistryPerfDefaultsFix() }, UserGoal.Maximum, options);
        var r = map[ChangeType.Registry];
        Assert.False(r.Read(Targets.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel")).Exists);
        Assert.Equal(StateValue.DWord(1), r.Read(Targets.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache")));
    }

    [Fact]
    public void ModifiedBuild_RegistrySecurity_IsFoundButNotPreselected()
    {
        var p = TestProfiles.Load("revios");
        var findings = TestProfiles.Findings(p);
        Assert.Contains(findings, f => f.Id == "REGISTRY_SECURITY" && f.Severity == Severity.Important);
        var plan = new StrategyEngine(FixCatalog.All()).Build(p, findings, UserGoal.Maximum);
        var sec = Assert.Single(plan.Fixes, f => f.Fix.Id == FixIds.RegistrySecurityDefaults);
        Assert.False(sec.Selected);
        Assert.Contains(sec.Notes, n => n.Contains("сборкой"));
    }

    // ---------- Драйверы ----------

    [Fact]
    public void DriverAudit_FlagsOldAndGenericDrivers_WithOfficialLinks()
    {
        var p = TestProfiles.Load("laptop-optimus");
        var f = Assert.Single(TestProfiles.Findings(p), x => x.Id == "DRIVERS_UPDATE");
        Assert.Equal(Severity.Important, f.Severity); // сетевой драйвер старше трёх лет
        Assert.Contains(f.Evidence, e => e.Contains("Realtek PCIe GbE") && e.Contains("2019"));
        Assert.Contains(f.Evidence, e => e.Contains("High Definition Audio") && e.Contains("стандартный драйвер Microsoft"));
        Assert.DoesNotContain(f.Evidence, e => e.Contains("AX201")); // свежий
        Assert.DoesNotContain(f.Evidence, e => e.Contains("RTX 4060")); // видеокарту проверяют другие детекторы
        Assert.Contains("https://www.asus.com/support/download-center/", f.Links); // ноутбук ASUS → сайт ASUS
        Assert.All(f.Links, l => Assert.True(l.StartsWith("https://", StringComparison.Ordinal) || l.StartsWith("ms-settings:", StringComparison.Ordinal), l));
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2684", DriverCategory.Display, false, Sources.NvidiaDrivers)]
    [InlineData(@"PCI\VEN_1002&DEV_744C", DriverCategory.Display, false, Sources.AmdDrivers)]
    [InlineData(@"PCI\VEN_8086&DEV_A7A0", DriverCategory.Display, true, Sources.IntelDsa)]
    [InlineData(@"PCI\VEN_8086&DEV_2725", DriverCategory.Network, true, Sources.IntelDsa)]
    [InlineData(@"PCI\VEN_1022&DEV_790B", DriverCategory.Chipset, false, Sources.AmdDrivers)]
    [InlineData(@"PCI\VEN_10EC&DEV_8125", DriverCategory.Network, false, "https://www.msi.com/support/download")]
    [InlineData(@"PCI\VEN_144D&DEV_A80A", DriverCategory.Storage, false, "https://semiconductor.samsung.com/consumer-storage/support/tools/")]
    public void DriverLinks_PointToOfficialSources(string hwid, DriverCategory cat, bool laptop, string expected)
    {
        var dev = new DeviceInfo { IsLaptop = laptop, Manufacturer = laptop ? "Unknown OEM" : "To Be Filled By O.E.M.", BoardManufacturer = "Micro-Star International Co., Ltd." };
        var d = new DriverInfo { DeviceName = "x", Category = cat, HardwareId = hwid };
        Assert.Equal(expected, DriverAdvisor.LinkFor(d, dev).Url);
    }

    [Fact]
    public void DriverStatus_InboxDatesAreNotTreatedAsOld()
    {
        var now = TestProfiles.Now;
        var inbox = new DriverInfo { DeviceName = "Standard SATA AHCI Controller", Category = DriverCategory.Storage, Provider = "Microsoft", Manufacturer = "Standard SATA AHCI Controller", Date = new DateTime(2006, 6, 21) };
        Assert.Equal(DriverStatus.Ok, DriverAdvisor.Evaluate(inbox, now));
        var basic = new DriverInfo { DeviceName = "Microsoft Basic Display Adapter", Category = DriverCategory.Display, Provider = "Microsoft" };
        Assert.Equal(DriverStatus.Missing, DriverAdvisor.Evaluate(basic, now));
        var oldGpu = new DriverInfo { DeviceName = "GPU", Category = DriverCategory.Display, Provider = "NVIDIA", Date = new DateTime(2024, 1, 1) };
        Assert.Equal(DriverStatus.Old, DriverAdvisor.Evaluate(oldGpu, now));
    }

    // ---------- Программы ----------

    [Fact]
    public void SoftwareCatalog_OnlyHttpsOfficialLinks_NoDuplicates()
    {
        var all = SoftwareCatalog.All.SelectMany(c => c.Items).ToList();
        Assert.True(all.Count >= 20);
        Assert.All(all, e => Assert.StartsWith("https://", e.Url));
        Assert.Equal(all.Count, all.Select(e => e.Name).Distinct().Count());
        Assert.Contains(all, e => e.Name == "WizTree");
        Assert.Contains(all, e => e.Name == "Discord");
        Assert.All(SoftwareCatalog.All, c => Assert.NotEmpty(c.Items));
    }

    // ---------- Твики ----------

    [Fact]
    public void TweakCatalog_IsConsistent()
    {
        var all = Opti.Core.Tweaks.TweakCatalog.All;
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        foreach (var t in all)
        {
            Assert.NotEmpty(t.Values);
            foreach (var v in t.Values)
            {
                Assert.Matches(@"^HK(LM|CU)\\", v.Key);
                Assert.Contains(v.Kind, new[] { "DWord", "String" });
                if (v.Kind == "DWord") Assert.True(uint.TryParse(v.On, out _), $"{t.Id}: {v.On}");
            }
            if (t.Link is not null) Assert.StartsWith("https://", t.Link);
        }
        Assert.Contains(all, t => t.Id == "CLASSIC_CONTEXT_MENU" && t.MinBuild == 22000);
    }
}
