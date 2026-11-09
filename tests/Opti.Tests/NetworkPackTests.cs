using Opti.Analysis.Collectors;
using Opti.Analysis.Detectors;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Measure.Capture;
using Opti.ViewModels;
using Opti.ViewModels.Demo;

namespace Opti.Tests;

public sealed class NetworkPackTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-net-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static List<string> Ids(SystemProfile p) => TestProfiles.Findings(p).Select(f => f.Id).ToList();

    [Fact]
    public void DeliveryOptimization_Internet_IsFound_OtherModesAreNot()
    {
        var p = TestProfiles.Load("desktop-rtx");
        p.DeliveryOptimizationMode = 3;
        Assert.Contains("NET_DELIVERY_OPTIMIZATION", Ids(p));
        foreach (int? mode in new int?[] { null, 0, 1, 2 })
        {
            p.DeliveryOptimizationMode = mode;
            Assert.DoesNotContain("NET_DELIVERY_OPTIMIZATION", Ids(p));
        }
    }

    [Fact]
    public void DeliveryOptimizationFix_AppliesLanOnly_AndRollsBackToOriginal()
    {
        var p = TestProfiles.Load("desktop-rtx");
        p.DeliveryOptimizationMode = 3;
        var reg = ProfileSeeder.CreateEmulation(p, out var map);
        var journal = new InMemoryChangeJournal();
        var key = Targets.Reg(DeliveryOptimizationLanFix.Key, "DODownloadMode");

        var plan = new StrategyEngine(FixCatalog.All()).Build(p, TestProfiles.Findings(p), UserGoal.Balanced);
        Assert.True(plan.Fixes.Single(f => f.Fix.Id == FixIds.DeliveryOptimizationLan).Selected);
        var minimal = new StrategyEngine(FixCatalog.All()).Build(p, TestProfiles.Findings(p), UserGoal.Minimal);
        Assert.False(minimal.Fixes.Single(f => f.Fix.Id == FixIds.DeliveryOptimizationLan).Selected); // настройка, не исправление ошибки

        var report = new ApplyService(reg, journal, null).Apply(p, new IFix[] { new DeliveryOptimizationLanFix() }, UserGoal.Balanced);
        Assert.Equal(FixOutcome.Applied, report.Results.Single().Outcome);
        Assert.Equal(StateValue.DWord(1), map[ChangeType.Registry].Read(key));

        new RollbackService(reg, journal).RollbackAll();
        Assert.Equal(StateValue.DWord(3), map[ChangeType.Registry].Read(key));
    }

    [Fact]
    public void EthernetSlowLink_And_WeakWifi_AreFound()
    {
        var p = TestProfiles.Load("desktop-rtx");
        p.Network.Clear();
        p.Network.Add(new NetAdapterInfo { Name = "Ethernet", Kind = NetAdapterKind.Ethernet, IsUp = true, LinkSpeedMbps = 100 });
        Assert.Contains("NET_ETHERNET_SLOW_LINK", Ids(p));
        p.Network[0].LinkSpeedMbps = 1000;
        Assert.DoesNotContain("NET_ETHERNET_SLOW_LINK", Ids(p));
        p.Network[0].IsUp = false; p.Network[0].LinkSpeedMbps = 100; // отключённый адаптер не в счёт
        Assert.DoesNotContain("NET_ETHERNET_SLOW_LINK", Ids(p));

        p.Network.Clear();
        p.Network.Add(new NetAdapterInfo { Name = "Wi-Fi", Kind = NetAdapterKind.WiFi, IsUp = true, WifiSignalPercent = 42 });
        Assert.Contains("NET_WIFI_WEAK_SIGNAL", Ids(p));
        p.Network[0].WifiSignalPercent = 85;
        Assert.DoesNotContain("NET_WIFI_WEAK_SIGNAL", Ids(p));
    }

    [Theory]
    [InlineData("    Signal                 : 76%", 76)]
    [InlineData("    Сигнал                 : 58%", 58)]
    [InlineData("    SSID : Home", null)]
    public void WifiSignal_IsParsedFromNetsh(string line, int? expected)
    {
#pragma warning disable CA1416
        Assert.Equal(expected, NetworkCollector.ParseWifiSignal(line));
#pragma warning restore CA1416
    }

    [Fact]
    public void DnsFix_SetsServersOnAdapters_AndRollbackRestoresAutomatic()
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        var original = DnsSetFix.ActiveAdapters;
        DnsSetFix.ActiveAdapters = () => new[] { "Ethernet", "Wi-Fi" };
        try
        {
            map[ChangeType.Dns].Seed(Targets.Dns("Wi-Fi"), StateValue.Str("192.168.1.1")); // Ethernet — автоматически (значения нет)
            var journal = new InMemoryChangeJournal();
            var options = new Dictionary<string, IReadOnlyDictionary<string, string>> { [FixIds.DnsSet] = new Dictionary<string, string> { ["servers"] = "1.1.1.1,1.0.0.1" } };
            var report = new ApplyService(reg, journal, null).Apply(new SystemProfile(), new IFix[] { new DnsSetFix() }, UserGoal.Balanced, options);
            Assert.Equal(FixOutcome.Applied, report.Results.Single().Outcome);
            Assert.Equal(StateValue.Str("1.1.1.1,1.0.0.1"), map[ChangeType.Dns].Read(Targets.Dns("Ethernet")));

            new RollbackService(reg, journal).RollbackAll();
            Assert.False(map[ChangeType.Dns].Read(Targets.Dns("Ethernet")).Exists);              // снова автоматически
            Assert.Equal(StateValue.Str("192.168.1.1"), map[ChangeType.Dns].Read(Targets.Dns("Wi-Fi")));
        }
        finally { DnsSetFix.ActiveAdapters = original; }
    }

    [Fact]
    public void DnsFix_WithoutServers_Fails_AndIsNeverPlanned()
    {
        var reg = InMemoryBackend.CreateRegistry(out _);
        var report = new ApplyService(reg, new InMemoryChangeJournal(), null).Apply(new SystemProfile(), new IFix[] { new DnsSetFix() }, UserGoal.Balanced);
        Assert.Equal(FixOutcome.Failed, report.Results.Single().Outcome);
        foreach (var name in TestProfiles.All)
        {
            var p = TestProfiles.Load(name);
            var plan = new StrategyEngine(FixCatalog.All()).Build(p, TestProfiles.Findings(p), UserGoal.Maximum);
            Assert.DoesNotContain(plan.Fixes, f => f.Fix.Id == FixIds.DnsSet);
        }
    }

    [Fact]
    public async Task NetworkPage_ShowsRegionsAndDns_WithAdviceAndBestFirst()
    {
        var svc = new DemoAppServices(_dir);
        var main = new MainViewModel(svc);
        await main.Network.RunRegionsCommand.ExecuteAsync(null);
        Assert.Equal(13, main.Network.Regions.Count);
        Assert.True(main.Network.Regions[0].IsBest);
        Assert.Contains("Франкфурт", main.Network.Regions[0].Name);
        Assert.Equal(Palette.Good, main.Network.Regions[0].Tone);
        Assert.Contains("Ближе всего", main.Network.RegionsSummary);

        await main.Network.RunDnsCommand.ExecuteAsync(null);
        Assert.True(main.Network.DnsRows[0].IsBest);
        Assert.Contains(main.Network.DnsRows, r => r.IsCurrent && !r.CanApply);
        Assert.Contains("Cloudflare", main.Network.DnsRows[0].Name);
        Assert.Contains("1.1.1.1,1.0.0.1", main.Network.DnsRows[0].Servers);
        Assert.Contains("ускорит", main.Network.DnsSummary);
    }

    [Fact]
    public void Describe_ExplainsLossJitterAndDistance()
    {
        var t = ConnectionProbe.Regions[0];
        string D(double ms, double jitter, double loss) => NetworkViewModel.Describe(new[] { new ProbeResult(t, ms, jitter, loss, 12) });
        Assert.Contains("Потери", D(20, 1, 5));
        Assert.Contains("разброс", D(20, 30, 0));
        Assert.Contains("далеко", D(90, 2, 0));
        Assert.DoesNotContain("далеко", D(20, 2, 0));
        Assert.Contains("не ответил", NetworkViewModel.Describe(Array.Empty<ProbeResult>()));
    }
}
