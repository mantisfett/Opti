using Opti.Workflow;

namespace Opti.Tests;

public class BackgroundTests
{
    private static BackgroundScanner.Sample S(int pid, string name, double cpuSec, long mb, int session = 1) =>
        new(pid, name, TimeSpan.FromSeconds(cpuSec), mb * 1024 * 1024, session);

    [Fact]
    public void Build_ComputesCpuShare_GroupsByName_AndProtectsSystem()
    {
        var before = new Dictionary<int, BackgroundScanner.Sample>
        {
            [1] = S(1, "chrome", 10, 300), [2] = S(2, "chrome", 5, 200), [3] = S(3, "svchost", 50, 100, 0), [4] = S(4, "Opti", 1, 200), [5] = S(5, "tiny", 1, 10),
        };
        var after = new Dictionary<int, BackgroundScanner.Sample>
        {
            [1] = S(1, "chrome", 10.8, 300), [2] = S(2, "chrome", 5.4, 200), [3] = S(3, "svchost", 51, 100, 0), [4] = S(4, "Opti", 2, 200), [5] = S(5, "tiny", 1.0, 10),
        };
        // 2 секунды, 4 ядра: chrome отработал 1,2 с процессора = 15 % всего процессора
        var list = BackgroundScanner.Build(before, after, elapsedMs: 2000, cpuCount: 4, selfPid: 4, top: 10);
        var chrome = list.Single(a => a.Name == "chrome");
        Assert.Equal(2, chrome.Processes);
        Assert.Equal(15, chrome.CpuPercent, 1);
        Assert.Equal(500, chrome.MemoryMb);
        Assert.True(chrome.CanClose);
        Assert.Equal(BackgroundCategory.Browser, chrome.Category);
        Assert.False(list.Single(a => a.Name == "svchost").CanClose);   // служба/защищённый
        Assert.DoesNotContain(list, a => a.Name == "Opti");              // сам Opti не показываем
        Assert.DoesNotContain(list, a => a.Name == "tiny");              // слишком лёгкий
        Assert.Equal("chrome", list[0].Name);
    }

    [Theory]
    [InlineData("Discord", BackgroundCategory.Chat)]
    [InlineData("steamwebhelper", BackgroundCategory.Launcher)]
    [InlineData("OneDrive", BackgroundCategory.Cloud)]
    [InlineData("AdobeUpdateService", BackgroundCategory.Updater)]
    [InlineData("LGHUB", BackgroundCategory.Vendor)]
    [InlineData("obs64", BackgroundCategory.Recorder)]
    [InlineData("Spotify", BackgroundCategory.Media)]
    [InlineData("notepad", BackgroundCategory.Other)]
    public void Classify_KnownApps(string name, BackgroundCategory expected) => Assert.Equal(expected, BackgroundScanner.Classify(name));

    [Theory]
    [InlineData("explorer")] [InlineData("lsass")] [InlineData("MsMpEng")] [InlineData("opti-cli")] [InlineData("Opti")]
    public void Close_RefusesProtectedProcesses(string name)
    {
        Assert.True(BackgroundScanner.IsProtected(name));
        Assert.Equal(0, BackgroundScanner.Close(name));
    }
}
