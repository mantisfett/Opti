using Opti.Measure.Capture;
using Opti.ViewModels;
using Opti.ViewModels.Demo;
using Xunit;

namespace Opti.Tests;

public class PingToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-ping-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(0, BloatGrade.A)]
    [InlineData(29.9, BloatGrade.A)]
    [InlineData(30, BloatGrade.B)]
    [InlineData(59, BloatGrade.B)]
    [InlineData(60, BloatGrade.C)]
    [InlineData(199, BloatGrade.C)]
    [InlineData(200, BloatGrade.D)]
    [InlineData(399, BloatGrade.D)]
    [InlineData(400, BloatGrade.F)]
    public void Grade_thresholds(double increase, BloatGrade expected) => Assert.Equal(expected, Bufferbloat.GradeFor(increase));

    [Fact]
    public void Result_uses_worst_direction()
    {
        var r = new BufferbloatResult("1.1.1.1", 18, 1, 0, 71, 214);
        Assert.Equal(196, r.UploadIncreaseMs);
        Assert.Equal(196, r.WorstIncreaseMs);
        Assert.Equal(BloatGrade.C, r.Grade);
        Assert.Contains("196", Bufferbloat.Describe(r));
    }

    [Fact]
    public void Result_without_load_data_is_graded_a_but_unmeasured_directions_are_null()
    {
        var r = new BufferbloatResult("x", 20, 1, 0, null, null);
        Assert.Equal(0, r.WorstIncreaseMs);
        Assert.Null(r.DownloadIncreaseMs);
    }

    [Fact]
    public void Unreachable_target_gets_message_not_grade()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Network.ShowBloat(new BufferbloatResult("x", double.NaN, 0, 100, null, null));
        Assert.Equal("", main.Network.BloatGrade);
        Assert.Equal(Palette.Bad, main.Network.BloatTone);
    }

    [Fact]
    public void PingStats_window_average_jitter_and_loss()
    {
        var s = new PingStats(4);
        foreach (var ms in new[] { 100.0, 20, 30, double.NaN, 40 }) s.Add(new PingSample(ms));
        Assert.Equal(4, s.Count); // первое выпало из окна
        Assert.Equal(30, s.Average);
        Assert.Equal(20, s.Min);
        Assert.Equal(40, s.Max);
        Assert.Equal(25, s.LossPercent);
        Assert.Equal(10, s.Jitter); // |30-20| и |40-30| по успешным
        Assert.Equal(40, s.Last);
    }

    [Fact]
    public void PingStats_empty_is_nan()
    {
        var s = new PingStats();
        Assert.True(double.IsNaN(s.Average));
        Assert.Equal(0, s.LossPercent);
        s.Add(new PingSample(double.NaN));
        Assert.True(double.IsNaN(s.Min));
        Assert.Equal(100, s.LossPercent);
    }

    [Theory]
    [InlineData(20, 0, Palette.Good)]
    [InlineData(80, 0, Palette.Neutral)]
    [InlineData(20, 3, Palette.Bad)]
    [InlineData(double.NaN, 0, Palette.Bad)]
    public void Monitor_tone(double avg, double loss, string tone) => Assert.Equal(tone, NetworkViewModel.MonitorToneFor(avg, loss));

    [Fact]
    public async Task Monitor_collects_samples_and_stops_on_toggle_and_on_leaving_page()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        var run = main.Network.ToggleMonitorCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && main.Network.Bars.Count < 5; i++) await Task.Delay(20);
        Assert.True(main.Network.IsMonitoring);
        Assert.NotEmpty(main.Network.Bars);
        Assert.NotEqual("—", main.Network.MonitorAvg);
        Assert.All(main.Network.Bars, b => Assert.InRange(b.Height, 2, 48));

        main.Navigate(main.Network);
        main.Navigate(main.Home); // ушли со страницы — монитор остановился
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(main.Network.IsMonitoring);
    }

    [Fact]
    public async Task Bloat_command_fills_summary()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        await main.Network.RunBloatCommand.ExecuteAsync(null);
        Assert.NotNull(main.Network.BloatSummary);
        Assert.Equal("C", main.Network.BloatGrade);
        Assert.Contains("+196", main.Network.BloatUp);
        Assert.False(main.Network.IsBusy);
    }

    [Fact]
    public async Task Invalid_monitor_target_is_rejected()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Network.MonitorTarget = "bad host;rm";
        await main.Network.ToggleMonitorCommand.ExecuteAsync(null);
        Assert.False(main.Network.IsMonitoring);
        Assert.Empty(main.Network.Bars);
    }
}
