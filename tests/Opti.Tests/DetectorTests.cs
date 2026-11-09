using Opti.Analysis.Detectors;
using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Profile;

namespace Opti.Tests;

public class DetectorTests
{
    [Fact]
    public void OptimusLaptop_FindsDisplayGpuObsAndPowerIssues()
    {
        var ids = TestProfiles.FindingIds("laptop-optimus");
        Assert.Contains("DISP_LOW_HZ", ids);
        Assert.Contains("HYBRID_GAME_ON_IGPU", ids);
        Assert.Contains("HYBRID_NO_MUX_INFO", ids);
        Assert.Contains("OBS_DISPLAY_CAPTURE_HYBRID", ids);
        Assert.Contains("OBS_ADMIN", ids);
        Assert.Contains("OBS_REPLAY_BUFFER_BIG", ids);
        Assert.Contains("OVERLAY_HEAVY", ids);
        Assert.Contains("HVCI_ON", ids);
        Assert.Contains("NET_WIFI_GAMING", ids);
        Assert.Contains("POWER_BATTERY_PLAN", ids);   // от сети, но режим «Экономия заряда»
        Assert.Contains("DRIVER_OLD", ids);           // драйвер iGPU от 2023 года
        Assert.Contains("GAME_DVR_ON", ids);
        // На ноутбуке XMP не проверяется, каналы заполнены
        Assert.DoesNotContain("RAM_BELOW_RATED", ids);
        Assert.DoesNotContain("RAM_SINGLE_CHANNEL", ids);
    }

    [Fact]
    public void HybridGames_UserChoiceIsRespected()
    {
        var f = TestProfiles.Findings(TestProfiles.Load("laptop-optimus")).Single(x => x.Id == "HYBRID_GAME_ON_IGPU");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(f.Evidence, e => e.Contains("Counter-Strike 2"));
        Assert.Contains(f.Evidence, e => e.Contains("Cyberpunk") && e.Contains("не трогает"));
    }

    [Fact]
    public void Desktop_FindsXmpTimersMpoDpcBufferbloat()
    {
        var ids = TestProfiles.FindingIds("desktop-rtx");
        Assert.Contains("RAM_BELOW_RATED", ids);
        Assert.Contains("BCD_RISKY_TIMERS", ids);
        Assert.Contains("MPO_KEY_SET", ids);
        Assert.Contains("DPC_LATENCY_HIGH", ids);
        Assert.Contains("NET_BUFFERBLOAT", ids);
        Assert.Contains("POWER_BATTERY_PLAN", ids);
        Assert.Contains("DISK_FULL", ids);
        Assert.Contains("DRIVER_OLD", ids);
        Assert.Contains("GAME_MODE_OFF", ids);
        Assert.Contains("WINDOWED_OPT_OFF", ids);
        Assert.DoesNotContain("DISP_LOW_HZ", ids);
        Assert.DoesNotContain("HYBRID_GAME_ON_IGPU", ids);
        Assert.DoesNotContain("NET_WIFI_GAMING", ids);
        Assert.DoesNotContain("OVERLAY_HEAVY", ids); // один оверлей — норма
    }

    [Fact]
    public void Dpc_NamesTheWorstDriver()
    {
        var f = TestProfiles.Findings(TestProfiles.Load("desktop-rtx")).Single(x => x.Id == "DPC_LATENCY_HIGH");
        Assert.StartsWith("rt640x64.sys", f.Evidence[0]);
        Assert.DoesNotContain(f.Evidence, e => e.StartsWith("ndis.sys", StringComparison.Ordinal));
    }

    [Fact]
    public void IgpuLaptop_FindsMemoryIssues_AndNoPowerFindingOnBattery()
    {
        var p = TestProfiles.Load("laptop-igpu");
        var findings = TestProfiles.Findings(p);
        var ids = findings.Select(f => f.Id).ToHashSet();
        Assert.Contains("RAM_SINGLE_CHANNEL", ids);
        Assert.Equal(Severity.Important, findings.Single(f => f.Id == "RAM_SINGLE_CHANNEL").Severity); // для iGPU — важно
        Assert.Contains("RAM_PRESSURE", ids);
        Assert.Contains("STARTUP_HEAVY", ids);
        Assert.DoesNotContain("POWER_BATTERY_PLAN", ids);  // работа от батареи — не трогаем
        Assert.DoesNotContain("DISP_LOW_HZ", ids);
        Assert.DoesNotContain("HAGS_AB_CANDIDATE", ids);   // HAGS не поддерживается
        Assert.DoesNotContain("WINDOWED_OPT_OFF", ids);
    }

    [Fact]
    public void WeakPc_ReportsHardwareHonestly()
    {
        var findings = TestProfiles.Findings(TestProfiles.Load("weak-pc"));
        var hw = findings.Where(f => f.IsHardwareLimitation).Select(f => f.Id).ToHashSet();
        Assert.Contains("THERMAL_THROTTLE", hw);
        Assert.Contains("DISK_SYSTEM_HDD", hw);
        Assert.Contains("DISK_HEALTH", hw);
        Assert.Contains("HW_WEAK", hw);
        Assert.Contains("RAM_SINGLE_CHANNEL", hw);
        // аппаратные проблемы не предлагают программных «исправлений»
        Assert.All(findings.Where(f => f.Id is "THERMAL_THROTTLE" or "DISK_SYSTEM_HDD" or "HW_WEAK"), f => Assert.Empty(f.FixIds));
    }

    [Fact]
    public void Findings_AreSortedBySeverity()
    {
        foreach (var name in TestProfiles.All)
        {
            var f = TestProfiles.Findings(TestProfiles.Load(name));
            Assert.Equal(f.OrderByDescending(x => x.Severity).Select(x => x.Severity), f.Select(x => x.Severity));
        }
    }

    [Fact]
    public void EmptyProfile_ProducesNoFindingsAndNoErrors()
    {
        var p = new SystemProfile();
        var findings = TestProfiles.Findings(p);
        Assert.Empty(p.CollectorErrors);
        // в пустом профиле всё «неизвестно» — детекторы не должны ничего выдумывать
        Assert.Empty(findings);
    }

    [Fact]
    public void AllFixIdsReferencedByDetectorsExistInCatalog()
    {
        var catalog = Opti.Fixes.Catalog.FixCatalog.All().Select(f => f.Id).ToHashSet();
        foreach (var name in TestProfiles.All)
            foreach (var f in TestProfiles.Findings(TestProfiles.Load(name)))
                Assert.All(f.FixIds, id => Assert.Contains(id, catalog));
    }

    [Fact]
    public void Profile_RoundTripsThroughJson()
    {
        var p = TestProfiles.Load("laptop-optimus");
        var again = OptiJson.Deserialize<SystemProfile>(OptiJson.Serialize(p));
        Assert.Equal(OptiJson.Serialize(p), OptiJson.Serialize(again));
        Assert.Equal(1, again.Games[1].ExeGpuPreferences.Values.Single());
    }
}
