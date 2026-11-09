using Opti.Core.Profile;
using Opti.Measure.Capture;
using Opti.ViewModels;
using Opti.ViewModels.Demo;
using Xunit;

namespace Opti.Tests;

public class LatencyViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-lat-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static LatencyReport Report(params (string Name, double Dpc, double Isr)[] drivers)
    {
        var d = new DpcLatencyInfo { DurationSec = 30 };
        foreach (var (n, dpc, isr) in drivers) d.Drivers.Add(new DriverLatency { Driver = n, MaxDpcUs = dpc, MaxIsrUs = isr });
        return new LatencyReport(d, new TimerAccuracyResult(1000, 1.1, 1.5, 2.0, 0.5));
    }

    [Theory]
    [InlineData(100, "good")]
    [InlineData(500, "good")]
    [InlineData(501, "warn")]
    [InlineData(1000, "warn")]
    [InlineData(1001, "bad")]
    public void Tone_thresholds_match_detector(double us, string tone) => Assert.Equal(tone, LatencyViewModel.ToneFor(us));

    [Fact]
    public void Bad_driver_gives_critical_summary_steps_and_sorted_rows()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Latency.Show(Report(("a.sys", 100, 10), ("ndis.sys", 2000, 50), ("b.sys", 700, 5)));
        Assert.Equal(new[] { "ndis.sys", "b.sys", "a.sys" }, main.Latency.Rows.Select(r => r.Driver));
        Assert.Equal(1.0, main.Latency.Rows[0].Bar);
        Assert.Equal(Palette.Bad, main.Latency.SummaryTone);
        Assert.Contains("ndis.sys", main.Latency.Summary);
        Assert.NotEmpty(main.Latency.Steps);
        Assert.NotNull(main.Latency.TimerText);
    }

    [Fact]
    public void Healthy_system_is_reported_good_without_steps()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Latency.Show(Report(("a.sys", 120, 10), ("b.sys", 300, 5)));
        Assert.Equal(Palette.Good, main.Latency.SummaryTone);
        Assert.Empty(main.Latency.Steps);
        Assert.Contains("b.sys", main.Latency.Summary);
    }

    [Fact]
    public void No_events_asks_to_repeat()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Latency.Show(Report());
        Assert.Empty(main.Latency.Rows);
        Assert.Equal(Palette.Neutral, main.Latency.SummaryTone);
    }

    [Fact]
    public void Only_top_ten_drivers_are_shown()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Latency.Show(Report(Enumerable.Range(0, 15).Select(i => ($"d{i}.sys", 10.0 + i, 1.0)).ToArray()));
        Assert.Equal(10, main.Latency.Rows.Count);
        Assert.Equal("d14.sys", main.Latency.Rows[0].Driver);
    }

    [Fact]
    public async Task Run_command_uses_service_and_resets_busy()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        await main.Latency.RunCommand.ExecuteAsync(null);
        Assert.False(main.Latency.IsBusy);
        Assert.NotEmpty(main.Latency.Rows);
        Assert.Equal("ndis.sys", main.Latency.Rows[0].Driver);
        Assert.Equal(30, main.Latency.Seconds);
        main.Latency.LongTest = true;
        Assert.Equal(120, main.Latency.Seconds);
        Assert.Contains(main.Nav, n => n.Page() == main.Latency);
    }
}
