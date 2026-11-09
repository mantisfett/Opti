using Opti.Measure.Capture;
using Opti.ViewModels;
using Opti.ViewModels.Demo;
using Xunit;

namespace Opti.Tests;

public class NetUsageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-traffic-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Aggregator_sums_by_program_ignoring_case_and_sorts_by_total_rate()
    {
        var a = new NetUsageAggregator();
        a.Add("chrome", 1_000_000, sent: false);
        a.Add("Chrome", 500_000, sent: false);
        a.Add("chrome", 100_000, sent: true);
        a.Add("onedrive", 10_000_000, sent: true);
        a.Add("idle", 0, sent: true);   // пустые не считаются
        a.Add("neg", -5, sent: true);
        var rows = a.Rows(seconds: 10);
        Assert.Equal(new[] { "onedrive", "chrome" }, rows.Select(r => r.Process.ToLowerInvariant()));
        var chrome = rows[1];
        Assert.Equal(1_500_000, chrome.ReceivedBytes);
        Assert.Equal(100_000, chrome.SentBytes);
        Assert.Equal(1_500_000 / 10.0 / 1024, chrome.ReceivedKbps, 3);
    }

    [Fact]
    public void Aggregator_names_empty_process_and_limits_rows()
    {
        var a = new NetUsageAggregator();
        a.Add("", 100, sent: true);
        for (int i = 0; i < 30; i++) a.Add($"p{i}", 1000 + i, sent: false);
        Assert.Equal(15, a.Rows(1).Count);
        var b = new NetUsageAggregator();
        b.Add("  ", 5, sent: true);
        Assert.Single(b.Rows(1));
    }

    [Fact]
    public void Zero_duration_gives_zero_rates()
    {
        var row = new NetUsageRow("x", 1000, 1000, 0);
        Assert.Equal(0, row.TotalKbps);
    }

    [Theory]
    [InlineData(10, "neutral")]
    [InlineData(199, "neutral")]
    [InlineData(200, "warn")]
    [InlineData(2047, "warn")]
    [InlineData(2048, "bad")]
    public void Traffic_tone_thresholds(double kbps, string tone) => Assert.Equal(tone, NetworkViewModel.TrafficToneFor(kbps));

    [Fact]
    public void Gate_allows_only_one_kernel_session_at_a_time()
    {
        using (EtwGate.Enter())
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EtwGate.Enter());
            Assert.False(string.IsNullOrEmpty(ex.Message));
        }
        using (EtwGate.Enter()) { } // после освобождения снова доступен
    }

    [Fact]
    public async Task ViewModel_shows_top_talker_and_protects_system_processes()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        await main.Network.RunTrafficCommand.ExecuteAsync(null);
        Assert.False(main.Network.IsBusy);
        Assert.Equal("OneDrive", main.Network.TrafficRows[0].Name);
        Assert.Contains("OneDrive", main.Network.TrafficSummary);
        Assert.Equal(Palette.Warn, main.Network.TrafficTone);
        Assert.All(main.Network.TrafficRows, r => Assert.InRange(r.Bar, 0, 1));
        Assert.False(main.Network.TrafficRows.Single(r => r.Name == "svchost").CanClose);
        Assert.True(main.Network.TrafficRows.Single(r => r.Name == "chrome").CanClose);
    }

    [Fact]
    public void Quiet_network_is_reported_free()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Network.ShowTraffic(new[] { new NetUsageRow("svchost", 1000, 2000, 15) });
        Assert.Equal(Palette.Good, main.Network.TrafficTone);
        main.Network.ShowTraffic(Array.Empty<NetUsageRow>());
        Assert.Equal(Palette.Good, main.Network.TrafficTone);
        Assert.Empty(main.Network.TrafficRows);
    }

    [Theory]
    [InlineData(50, "50")]
    [InlineData(1024, "1")]
    [InlineData(5120, "5")]
    public void Rate_format_switches_to_megabytes(double kbps, string fragment) => Assert.Contains(fragment, NetworkViewModel.FormatRate(kbps));
}
