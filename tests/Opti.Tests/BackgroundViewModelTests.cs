using Opti.ViewModels;
using Opti.ViewModels.Demo;
using Opti.Workflow;
using Xunit;

namespace Opti.Tests;

public class BackgroundViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-bgvm-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Scan_fills_rows_and_summary()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        await main.Background.ScanCommand.ExecuteAsync(null);
        Assert.NotEmpty(main.Background.Rows);
        Assert.False(string.IsNullOrEmpty(main.Background.Summary));
        Assert.False(main.Background.IsBusy);
    }

    [Fact]
    public void Show_empty_list_reports_quiet_background()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Background.Show(Array.Empty<BackgroundApp>());
        Assert.Empty(main.Background.Rows);
        Assert.Equal(Palette.Good, main.Background.SummaryTone);
    }

    [Fact]
    public void Heavy_app_gets_warning_tone_and_gb_text()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Background.Show(new[] { new BackgroundApp("chrome", 20, 8, 2048, BackgroundCategory.Browser, true) });
        var row = Assert.Single(main.Background.Rows);
        Assert.Equal(Palette.Warn, row.Tone);
        Assert.Contains("2", row.MemoryText);
    }

    [Fact]
    public void Page_is_in_navigation()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        Assert.Contains(main.Nav, n => n.Page() == main.Background);
    }
}
