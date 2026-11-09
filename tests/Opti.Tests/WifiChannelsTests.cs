using Opti.Measure.Capture;
using Xunit;

namespace Opti.Tests;

public class WifiChannelsTests
{
    private const string En = """
        Interface name : Wi-Fi
        There are 4 networks currently visible.

        SSID 1 : HomeNet
            Network type            : Infrastructure
            Authentication          : WPA2-Personal
            BSSID 1                 : aa:bb:cc:00:00:01
                 Signal             : 90%
                 Radio type         : 802.11n
                 Channel            : 6
                 Basic rates (Mbps) : 1 2 5.5 11
            BSSID 2                 : aa:bb:cc:00:00:02
                 Signal             : 70%
                 Radio type         : 802.11ac
                 Channel            : 36

        SSID 2 : Neighbor1
            BSSID 1                 : 11:22:33:00:00:01
                 Signal             : 80%
                 Channel            : 6
                 Channel Utilization: 120 (47 %)

        SSID 3 : Neighbor2
            BSSID 1                 : 11:22:33:00:00:02
                 Signal             : 60%
                 Channel            : 7

        SSID 4 :
            BSSID 1                 : 11:22:33:00:00:03
                 Signal             : 40%
                 Channel            : 1
        """;

    private const string Ru = """
        SSID 1 : Дом
            BSSID 1                 : aa:bb:cc:00:00:01
                 Сигнал             : 55%
                 Канал              : 11
        SSID 2 : Сосед
            BSSID 1                 : 11:22:33:00:00:01
                 Сигнал             : 85%
                 Канал              : 11
        """;

    [Fact]
    public void Parses_english_networks_with_multiple_bssids_and_ignores_utilization_line()
    {
        var n = WifiChannels.ParseNetworks(En);
        Assert.Equal(5, n.Count);
        Assert.Equal(("HomeNet", 90, 6), (n[0].Ssid, n[0].SignalPercent, n[0].Channel));
        Assert.Equal(("HomeNet", 70, 36), (n[1].Ssid, n[1].SignalPercent, n[1].Channel));
        Assert.Equal("", n[4].Ssid); // скрытая сеть
        Assert.True(n[0].Is24);
        Assert.False(n[1].Is24);
    }

    [Fact]
    public void Parses_russian_output()
    {
        var n = WifiChannels.ParseNetworks(Ru);
        Assert.Equal(2, n.Count);
        Assert.Equal(("Сосед", 85, 11), (n[1].Ssid, n[1].SignalPercent, n[1].Channel));
    }

    [Fact]
    public void Garbage_or_empty_output_gives_no_networks()
    {
        Assert.Empty(WifiChannels.ParseNetworks(""));
        Assert.Empty(WifiChannels.ParseNetworks("The wireless local area network interface is powered down"));
    }

    [Fact]
    public void Parses_current_connection()
    {
        const string iface = """
            Name                   : Wi-Fi
            SSID                   : HomeNet
            BSSID                  : aa:bb:cc:00:00:01
            Radio type             : 802.11n
            Channel                : 6
            Signal                 : 88%
            """;
        var c = WifiChannels.ParseCurrent(iface);
        Assert.Equal(("HomeNet", 6, 88), (c.Ssid, c.Channel, c.SignalPercent));
        Assert.True(c.Connected);
        Assert.False(WifiChannels.ParseCurrent("There is no wireless interface on the system.").Connected);
    }

    [Theory]
    [InlineData(6, 6, true, 1.0)]
    [InlineData(6, 8, true, 0.6)]   // 2 канала разницы: 1 - 0.4
    [InlineData(6, 10, true, 0.2)]  // 4 канала разницы: 1 - 0.8
    [InlineData(6, 11, true, 0.0)]  // 5 каналов — не мешает
    [InlineData(36, 36, false, 1.0)]
    [InlineData(36, 40, false, 0.0)] // на 5 ГГц соседние каналы не пересекаются
    public void Interference_by_channel_distance(int netCh, int ch, bool is24, double expectedAt100)
    {
        var n = new WifiNetwork("x", "b", 100, netCh);
        Assert.Equal(expectedAt100, WifiChannels.Interference(n, ch, is24), 3);
    }

    [Fact]
    public void Different_band_never_interferes() =>
        Assert.Equal(0, WifiChannels.Interference(new WifiNetwork("x", "b", 100, 6), 36, false));

    [Fact]
    public void Congested_24_channel_recommends_a_free_one_and_ignores_own_access_points()
    {
        var nets = WifiChannels.ParseNetworks(En);
        var adv = WifiChannels.Analyze(nets, new WifiCurrent("HomeNet", 6, 90));
        Assert.Equal("warn", adv.Tone);
        Assert.Equal(11, adv.BestChannel); // 1 занят слабой сетью (40 %), 11 — свободен
        var cur = adv.Channels.Single(c => c.IsCurrent);
        Assert.Equal(6, cur.Channel);
        Assert.Equal(2, cur.Neighbors); // Neighbor1 (канал 6) и Neighbor2 (канал 7); свои точки не в счёт
        Assert.Contains(adv.Channels, c => c.Channel == 11 && c.IsBest);
    }

    [Fact]
    public void Clean_24_channel_is_good()
    {
        var nets = new List<WifiNetwork> { new("Home", "a", 90, 1), new("Far", "b", 20, 11) };
        var adv = WifiChannels.Analyze(nets, new WifiCurrent("Home", 1, 90));
        Assert.Equal("good", adv.Tone);
    }

    [Fact]
    public void Crowded_24_with_no_better_channel_still_warns()
    {
        var nets = new List<WifiNetwork> { new("Home", "a", 90, 6) };
        for (int i = 0; i < 6; i++) { nets.Add(new($"n{i}", $"1{i}", 70, 1)); nets.Add(new($"m{i}", $"2{i}", 70, 6)); nets.Add(new($"k{i}", $"3{i}", 70, 11)); }
        var adv = WifiChannels.Analyze(nets, new WifiCurrent("Home", 6, 90));
        Assert.Equal("warn", adv.Tone);
    }

    [Fact]
    public void Five_ghz_busy_channel_recommends_non_dfs_alternative()
    {
        var nets = new List<WifiNetwork> { new("Home", "a", 90, 36), new("A", "b", 80, 36), new("B", "c", 80, 36) };
        var adv = WifiChannels.Analyze(nets, new WifiCurrent("Home", 36, 90));
        Assert.Equal("warn", adv.Tone);
        Assert.NotNull(adv.BestChannel);
        Assert.Contains(adv.BestChannel!.Value, WifiChannels.Candidates5);
        Assert.NotEqual(36, adv.BestChannel);
    }

    [Fact]
    public void Not_connected_and_no_networks_cases()
    {
        Assert.DoesNotContain(WifiChannels.Analyze(Array.Empty<WifiNetwork>(), new WifiCurrent(null, null, null)).Channels, c => c.Neighbors > 0);
        var adv = WifiChannels.Analyze(WifiChannels.ParseNetworks(En), new WifiCurrent(null, null, null));
        Assert.Equal("neutral", adv.Tone);
        Assert.Null(adv.BestChannel);
        Assert.DoesNotContain(adv.Channels, c => c.IsCurrent);
    }
}

public class WifiViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-wifi-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Demo_scan_marks_current_and_best_channel_with_bars()
    {
        var main = new Opti.ViewModels.MainViewModel(new Opti.ViewModels.Demo.DemoAppServices(_dir));
        await main.Network.RunWifiCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(main.Network.WifiSummary));
        Assert.Contains(main.Network.WifiRows, r => r.Tag.Length > 0);
        Assert.All(main.Network.WifiRows, r => Assert.InRange(r.Bar, 0, 1));
        Assert.False(main.Network.IsBusy);
    }
}
