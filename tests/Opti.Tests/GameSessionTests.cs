using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.ViewModels;
using Opti.ViewModels.Demo;
using Opti.Workflow;
using Xunit;

namespace Opti.Tests;

public class GameSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-game-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SystemProfile Desktop(string active = "a1841308-3541-4fab-bc81-f71556f20b4a", bool laptop = false, bool? onAc = true)
    {
        var p = new SystemProfile();
        p.Device.IsLaptop = laptop;
        p.Power.OnAcPower = onAc;
        p.Power.ActiveSchemeGuid = active;
        p.Power.AvailableSchemeGuids = new() { "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "a1841308-3541-4fab-bc81-f71556f20b4a" };
        return p;
    }

    private static (GameSession Game, Dictionary<ChangeType, InMemoryBackend> Map, List<string> Closed) Create(SystemProfile p)
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        map[ChangeType.PowerSetting].Seed(Targets.ActiveScheme, StateValue.Str(p.Power.ActiveSchemeGuid!));
        var closed = new List<string>();
        return (new GameSession(reg, new InMemoryChangeJournal(), () => p, n => { closed.Add(n); return n == "ghost" ? 0 : 1; }), map, closed);
    }

    [Fact]
    public void Start_closes_apps_switches_power_and_end_restores()
    {
        var (game, map, closed) = Create(Desktop());
        var r = game.Start(new[] { "chrome", "OneDrive", "ghost" }, powerPlan: true);
        Assert.Equal(new[] { "chrome", "OneDrive" }, r.Closed);
        Assert.Equal(new[] { "ghost" }, r.NotClosed);
        Assert.True(r.PowerChanged);
        Assert.True(game.IsActive);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", map[ChangeType.PowerSetting].Read(Targets.ActiveScheme).Value);

        var end = game.End();
        Assert.Empty(end.Errors);
        Assert.False(game.IsActive);
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", map[ChangeType.PowerSetting].Read(Targets.ActiveScheme).Value);
    }

    [Fact]
    public void Laptop_keeps_power_plan_with_explanation()
    {
        var (game, map, _) = Create(Desktop(laptop: true));
        var r = game.Start(Array.Empty<string>(), powerPlan: true);
        Assert.False(r.PowerChanged);
        Assert.NotNull(r.PowerNote);
        Assert.False(game.IsActive);
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", map[ChangeType.PowerSetting].Read(Targets.ActiveScheme).Value);
    }

    [Fact]
    public void Already_high_performance_is_not_changed_or_journaled()
    {
        var (game, _, _) = Create(Desktop(active: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"));
        var r = game.Start(Array.Empty<string>(), powerPlan: true);
        Assert.False(r.PowerChanged);
        Assert.False(game.IsActive);
    }

    [Fact]
    public void Power_plan_can_be_skipped()
    {
        var (game, map, _) = Create(Desktop());
        var r = game.Start(Array.Empty<string>(), powerPlan: false);
        Assert.False(r.PowerChanged);
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", map[ChangeType.PowerSetting].Read(Targets.ActiveScheme).Value);
    }

    [Fact]
    public void Protected_processes_are_never_closed()
    {
        var (game, _, closed) = Create(Desktop());
        var r = game.Start(new[] { "explorer", "svchost", "Opti" }, powerPlan: false);
        Assert.Empty(closed);
        Assert.Empty(r.Closed);
        Assert.Equal(3, r.NotClosed.Count);
    }

    [Theory]
    [InlineData(BackgroundCategory.Browser, true)]
    [InlineData(BackgroundCategory.Cloud, true)]
    [InlineData(BackgroundCategory.Updater, true)]
    [InlineData(BackgroundCategory.Media, true)]
    [InlineData(BackgroundCategory.Chat, false)]
    [InlineData(BackgroundCategory.Launcher, false)]
    [InlineData(BackgroundCategory.Recorder, false)]
    [InlineData(BackgroundCategory.Vendor, false)]
    [InlineData(BackgroundCategory.Other, false)]
    public void Default_marks_only_what_games_do_not_need(BackgroundCategory c, bool marked) => Assert.Equal(marked, BackgroundViewModel.MarkedByDefault(c));

    [Fact]
    public async Task ViewModel_prepare_and_end_flow()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        await main.Background.ScanCommand.ExecuteAsync(null);
        Assert.Contains(main.Background.Rows, r => r.Name == "chrome" && r.IsMarked);
        Assert.Contains(main.Background.Rows, r => r.Name == "Discord" && !r.IsMarked && r.MarkHint is not null);
        Assert.True(main.Background.GameAvailable);

        await main.Background.PrepareGameCommand.ExecuteAsync(null);
        Assert.True(main.Background.IsSessionActive);
        Assert.DoesNotContain(main.Background.Rows, r => r.Name == "chrome"); // закрыта — убрана из списка
        Assert.Contains(main.Background.Rows, r => r.Name == "Discord");

        main.Background.EndGameCommand.Execute(null);
        Assert.False(main.Background.IsSessionActive);
    }
}
