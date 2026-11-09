using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.ViewModels;
using Opti.ViewModels.Demo;
using Opti.Workflow;
using Xunit;

namespace Opti.Tests;

public class StartupTests : IDisposable
{
    private const string Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-startup-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static StartupItem Item(string name, string cmd) =>
        new() { Name = name, Command = cmd, Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", ApprovedKey = Key + name };

    private static (StartupManager Mgr, Dictionary<ChangeType, InMemoryBackend> Map, InMemoryChangeJournal Journal) Create(params StartupItem[] items)
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        var journal = new InMemoryChangeJournal();
        return (new StartupManager(reg, journal, () => items), map, journal);
    }

    [Theory]
    [InlineData("Discord", @"C:\Users\x\AppData\Local\Discord\Update.exe --processStart Discord.exe", StartupAdvice.Suggest)]
    [InlineData("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", StartupAdvice.Suggest)]
    [InlineData("OneDrive", @"""C:\x\OneDrive.exe"" /background", StartupAdvice.Suggest)]
    [InlineData("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe", StartupAdvice.Keep)]
    [InlineData("RtkAudUService", @"C:\Windows\RtkAudUService64.exe", StartupAdvice.Keep)]
    [InlineData("Kaspersky Anti-Virus", @"C:\x\avp.exe", StartupAdvice.Keep)]
    [InlineData("Opti", @"C:\ProgramData\Opti\app\Opti.exe", StartupAdvice.Keep)]
    [InlineData("MyTool", @"C:\Tools\mytool.exe", StartupAdvice.Optional)]
    [InlineData("LGHUB", @"C:\Program Files\LGHUB\lghub.exe", StartupAdvice.Optional)]
    [InlineData("Resetter", @"C:\Tools\resetter.exe", StartupAdvice.Optional)] // «eset» внутри слова — не ESET
    public void Advice_by_name_and_command(string name, string cmd, StartupAdvice expected) =>
        Assert.Equal(expected, StartupManager.Advise(name, cmd, out _));

    [Theory]
    [InlineData(@"""C:\Program Files\A B\a.exe"" -x", "a.exe")]
    [InlineData(@"C:\Program Files\A B\a.exe -x", "a.exe")]
    [InlineData("tool.exe", "tool.exe")]
    [InlineData("", "")]
    public void ExeName_parses_commands(string cmd, string expected) => Assert.Equal(expected, StartupManager.ExeName(cmd));

    [Theory]
    [InlineData("02000000", true)]
    [InlineData("06000000", true)]
    [InlineData("03000000", false)]
    [InlineData("07000000", false)]
    public void Flag_parsing(string hex, bool enabled) => Assert.Equal(enabled, StartupManager.IsEnabledValue(new StateValue(true, "Binary", hex)));

    [Fact]
    public void Missing_flag_means_enabled() => Assert.True(StartupManager.IsEnabledValue(StateValue.Missing));

    [Fact]
    public void Scan_orders_suggested_first_and_reads_state_from_registry()
    {
        var (mgr, map, _) = Create(Item("SecurityHealth", "SecurityHealthSystray.exe"), Item("Steam", "steam.exe"), Item("MyTool", "mytool.exe"));
        map[ChangeType.Registry].Seed(Key + "Steam", new StateValue(true, "Binary", "03000000" + new string('0', 16)));
        var list = mgr.Scan();
        Assert.Equal(new[] { "Steam", "MyTool", "SecurityHealth" }, list.Select(e => e.Name));
        Assert.False(list[0].Enabled);
        Assert.True(list[1].Enabled);
    }

    [Fact]
    public void Disable_then_enable_restores_original_state_via_journal()
    {
        var (mgr, map, journal) = Create(Item("Steam", "steam.exe"));
        var e = mgr.Scan().Single();
        mgr.SetEnabled(e, false);
        Assert.False(mgr.Scan().Single().Enabled);
        Assert.Single(journal.GetAll(), r => r.FixId == StartupManager.ManagerFixId && !r.RolledBack);

        mgr.SetEnabled(mgr.Scan().Single(), true);
        Assert.True(mgr.Scan().Single().Enabled);
        Assert.All(journal.GetAll(), r => Assert.True(r.RolledBack));
        Assert.False(map[ChangeType.Registry].Snapshot().ContainsKey(Key + "Steam")); // было «нет значения» — осталось так
    }

    [Fact]
    public void Enabling_item_disabled_before_opti_writes_enabled_flag()
    {
        var (mgr, map, _) = Create(Item("Steam", "steam.exe"));
        map[ChangeType.Registry].Seed(Key + "Steam", new StateValue(true, "Binary", "03000000" + new string('0', 16)));
        mgr.SetEnabled(mgr.Scan().Single(), true);
        Assert.True(mgr.Scan().Single().Enabled);
    }

    [Fact]
    public void Protected_item_cannot_be_toggled()
    {
        var (mgr, _, _) = Create(Item("SecurityHealth", "SecurityHealthSystray.exe"));
        var e = mgr.Scan().Single();
        Assert.False(e.CanToggle);
        Assert.Throws<InvalidOperationException>(() => mgr.SetEnabled(e, false));
    }

    [Fact]
    public async Task ViewModel_demo_flow()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.Startup.Load();
        Assert.True(main.Startup.Rows.Count >= 8);
        Assert.True(main.Startup.HasSuggested);
        Assert.False(main.Startup.Rows.Single(r => r.Name == "SecurityHealth").CanToggle);
        var steam = main.Startup.Rows.Single(r => r.Name == "Steam");
        Assert.True(steam.ShowAdvice);
        steam.IsOn = false;
        Assert.Null(steam.Error);
        Assert.False(steam.ShowAdvice);
        Assert.False(main.Startup.Rows.Single(r => r.Name == "Steam").IsOn);
        await main.Startup.DisableSuggestedCommand.ExecuteAsync(null);
        Assert.False(main.Startup.HasSuggested);
        Assert.Contains(main.Nav, n => n.Page() == main.Startup);
    }
}
