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

public class GameWatcherTests
{
    private static (GameWatcher W, GameSession G, Dictionary<ChangeType, InMemoryBackend> Map, HashSet<string> Running, Func<bool>[] Enabled) Create(bool enabled = true, bool laptop = false)
    {
        var p = new SystemProfile();
        p.Device.IsLaptop = laptop;
        p.Power.OnAcPower = true;
        p.Power.ActiveSchemeGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
        p.Power.AvailableSchemeGuids = new() { "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "a1841308-3541-4fab-bc81-f71556f20b4a" };
        var reg = InMemoryBackend.CreateRegistry(out var map);
        map[ChangeType.PowerSetting].Seed(Targets.ActiveScheme, StateValue.Str(p.Power.ActiveSchemeGuid));
        var game = new GameSession(reg, new InMemoryChangeJournal(), () => p, _ => 1);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flag = new[] { () => enabled };
        var w = new GameWatcher(game, () => flag[0](), () => new[] { "cs2.exe", " Valorant " }, () => running);
        return (w, game, map, running, flag);
    }

    private static string Scheme(Dictionary<ChangeType, InMemoryBackend> map) => map[ChangeType.PowerSetting].Read(Targets.ActiveScheme).Value!;

    [Fact]
    public void Game_start_switches_power_and_game_exit_restores()
    {
        var (w, game, map, running, _) = Create();
        Assert.False(w.Tick());
        running.Add("cs2");
        Assert.True(w.Tick());
        Assert.True(w.OwnsSession);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Scheme(map));
        Assert.True(w.Tick()); // повторные проверки ничего не меняют
        running.Clear();
        Assert.False(w.Tick());
        Assert.False(w.OwnsSession);
        Assert.False(game.IsActive);
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", Scheme(map));
    }

    [Fact]
    public void Names_are_matched_without_exe_and_case()
    {
        var (w, _, _, running, _) = Create();
        running.Add("VALORANT");
        Assert.True(w.Tick());
    }

    [Fact]
    public void Disabled_watcher_does_nothing_and_releases_when_turned_off()
    {
        var (w, game, map, running, flag) = Create(enabled: false);
        running.Add("cs2");
        Assert.False(w.Tick());
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", Scheme(map));

        flag[0] = () => true;
        Assert.True(w.Tick());
        flag[0] = () => false; // выключили в настройках во время игры — схема возвращается
        w.Tick();
        Assert.False(game.IsActive);
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", Scheme(map));
    }

    [Fact]
    public void Manual_session_is_not_touched_by_watcher()
    {
        var (w, game, map, running, _) = Create();
        game.Start(Array.Empty<string>(), powerPlan: true);
        running.Add("cs2");
        w.Tick();
        Assert.False(w.OwnsSession);
        running.Clear();
        w.Tick();
        Assert.True(game.IsActive); // вручную начатую сессию завершает только пользователь
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Scheme(map));
    }

    [Fact]
    public void Laptop_is_left_alone()
    {
        var (w, game, map, running, _) = Create(laptop: true);
        running.Add("cs2");
        Assert.True(w.Tick());
        Assert.False(w.OwnsSession);
        Assert.False(game.IsActive);
        Assert.Equal("a1841308-3541-4fab-bc81-f71556f20b4a", Scheme(map));
    }

    [Fact]
    public void Dispose_restores_when_game_is_gone_but_keeps_plan_while_playing()
    {
        var (w, game, _, running, _) = Create();
        running.Add("cs2");
        w.Tick();
        running.Clear();
        w.Dispose();
        Assert.False(game.IsActive);

        var (w2, game2, _, running2, _) = Create();
        running2.Add("cs2");
        w2.Tick();
        w2.Dispose(); // игра идёт — схему не трогаем
        Assert.True(game2.IsActive);
    }

    [Theory]
    [InlineData("cs2, game.exe; Other\nthird", new[] { "cs2", "game", "Other", "third" })]
    [InlineData("", new string[0])]
    [InlineData(" , ;", new string[0])]
    [InlineData("CS2.EXE, cs2", new[] { "CS2" })]
    public void ParseNames_handles_separators(string text, string[] expected) => Assert.Equal(expected, GameWatcher.ParseNames(text));
}

public class GamePriorityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opti-prio-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static (GamePriority P, Dictionary<ChangeType, InMemoryBackend> Map, InMemoryChangeJournal J) Create()
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        var j = new InMemoryChangeJournal();
        return (new GamePriority(reg, j), map, j);
    }

    [Theory]
    [InlineData("cs2", true)]
    [InlineData("VALORANT-Win64-Shipping", true)]
    [InlineData("my game_1.2", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData(@"evil\PerfOptions", false)]
    [InlineData("evil/x", false)]
    [InlineData("имя", false)]
    [InlineData(" lead", false)]
    [InlineData("a|b", false)]
    public void Name_validation_keeps_registry_path_safe(string name, bool ok) => Assert.Equal(ok, GamePriority.IsValidName(name));

    [Fact]
    public void Sync_sets_cpu_priority_class_and_removal_restores_original()
    {
        var (p, map, _) = Create();
        // у одной игры уже есть значение до Opti — откат должен вернуть именно его
        map[ChangeType.Registry].Seed(GamePriority.Target("old"), StateValue.DWord(2));

        var r = p.Sync(new[] { "cs2", "old" }, enabled: true);
        Assert.Equal(new[] { "cs2", "old" }, r.Added.OrderBy(x => x));
        Assert.Equal(3u, uint.Parse(map[ChangeType.Registry].Read(GamePriority.Target("cs2")).Value!));
        Assert.Equal(new[] { "cs2", "old" }, p.Active());

        var r2 = p.Sync(new[] { "cs2" }, enabled: true); // old убрали из списка
        Assert.Equal(new[] { "old" }, r2.Removed);
        Assert.Empty(r2.Added);
        Assert.Equal("2", map[ChangeType.Registry].Read(GamePriority.Target("old")).Value);
        Assert.Equal(new[] { "cs2" }, p.Active());

        var r3 = p.Sync(new[] { "cs2" }, enabled: false); // выключили совсем
        Assert.Equal(new[] { "cs2" }, r3.Removed);
        Assert.False(map[ChangeType.Registry].Read(GamePriority.Target("cs2")).Exists);
        Assert.Empty(p.Active());
    }

    [Fact]
    public void Sync_is_idempotent_and_skips_invalid_names()
    {
        var (p, map, j) = Create();
        p.Sync(new[] { "cs2" }, true);
        int records = j.GetAll().Count;
        var r = p.Sync(new[] { "cs2", @"bad\name", "../x" }, true);
        Assert.Empty(r.Added);
        Assert.Equal(2, r.Invalid.Count);
        Assert.Equal(records, j.GetAll().Count);
        Assert.DoesNotContain(map[ChangeType.Registry].Snapshot().Keys, k => k.Contains("bad"));
    }

    [Fact]
    public async Task Settings_save_applies_priority()
    {
        var main = new MainViewModel(new DemoAppServices(_dir));
        main.SettingsPage.OnShown();
        main.SettingsPage.GameProcesses = "cs2.exe, bad\\name";
        main.SettingsPage.GamePriorityHigh = true;
        main.SettingsPage.SaveCommand.Execute(null);
        Assert.Equal(new[] { "cs2" }, main.Services.Priority!.Active());
        Assert.Contains("cs2", main.Toast);

        main.SettingsPage.GamePriorityHigh = false;
        main.SettingsPage.SaveCommand.Execute(null);
        Assert.Empty(main.Services.Priority!.Active());
        await Task.CompletedTask;
    }
}
