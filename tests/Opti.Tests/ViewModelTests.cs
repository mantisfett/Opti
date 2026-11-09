using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Measure.Stats;
using Opti.ViewModels;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Opti.Workflow.Sessions;

namespace Opti.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-vm-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeRunner : IMeasureRunner
    {
        private readonly Func<int, double> _base;
        private int _n;
        public FakeRunner(Func<int, double> baseMs) => _base = baseMs;
        public string Name => "fake";
        public Task<MeasurementRun> RunAsync(MeasureRequest r, IProgress<MeasureProgress>? progress = null, CancellationToken ct = default)
        {
            int n = _n++;
            var rng = new Random(n * 31 + 7);
            double b = _base(n);
            var ft = Enumerable.Range(0, 5000).Select(_ => b + (rng.NextDouble() - 0.5) + (rng.NextDouble() < 0.02 ? b * 2 : 0)).ToList();
            progress?.Report(new MeasureProgress(1, 2, ft.Take(100).ToList(), "game", false));
            return Task.FromResult(new MeasurementRun { Label = r.Label, FrameTimesMs = ft, Conditions = new RunConditions { ProcessName = "game", OnAcPower = true } });
        }
    }

    private sealed class FakeServices : IAppServices
    {
        public FakeServices(string dir, string profile, Func<int, double> frameMs)
        {
            Profile = TestProfiles.Load(profile);
            var reg = ProfileSeeder.CreateEmulation(Profile, out var map);
            Map = map;
            Journal = new InMemoryChangeJournal();
            Settings = new OptiSettings { MeasureRuns = 2, CheckForUpdates = false };
            Workflow = new TuneWorkflow(Settings, new SessionStore(Path.Combine(dir, "sessions")), reg, Journal);
            Runner = new FakeRunner(frameMs);
            DataDir = dir;
            Tweaks = new Opti.Fixes.Services.TweakService(reg, Journal);
        }
        public Opti.Core.Profile.SystemProfile Profile { get; }
        public Dictionary<ChangeType, InMemoryBackend> Map { get; }
        public FakeRunner Runner { get; }
        public TuneWorkflow Workflow { get; }
        public OptiSettings Settings { get; }
        public IChangeJournal Journal { get; }
        public string AppVersion => "test";
        public string ReportsDir => Path.Combine(DataDir, "reports");
        public string DataDir { get; }
        public List<string> Opened { get; } = new();
        public bool ConfirmAnswer { get; set; } = true;
        public bool Restarted { get; private set; }
        public Task<AnalysisResult> AnalyzeAsync(IProgress<string> progress, CancellationToken ct) { progress.Report("профиль"); return Task.FromResult(SystemAnalyzer.FromProfile(Profile)); }
        public bool IsModeAvailable(MeasureMode mode) => true;
        public IMeasureRunner CreateRunner(MeasureMode mode) => Runner;
        public string ResumeExePath => "Opti.exe";
        public void RestartComputer() => Restarted = true;
        public void OpenPath(string pathOrUrl) => Opened.Add(pathOrUrl);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmAnswer);
        public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(new UpdateInfo("9.9.9", "https://example.org", true));
        public List<Opti.Workflow.Cleanup.ICleanupTarget> Cleanup { get; } = new();
        public IReadOnlyList<Opti.Workflow.Cleanup.ICleanupTarget> CleanupTargets() => Cleanup;
        public string? StartupNotice => null;
        public Opti.Fixes.Services.TweakService Tweaks { get; }
        public int OsBuild => 26100;
        public int ExplorerRestarts { get; private set; }
        public void RestartExplorer() => ExplorerRestarts++;
    }

    [Fact]
    public async Task FullUserJourney_AnalyzeMeasureApplyMeasureCompare()
    {
        // «до» — 8,5 мс, «после» — 7 мс: явное улучшение
        var svc = new FakeServices(_dir, "laptop-optimus", n => n < 2 ? 8.5 : 7.0);
        var main = new MainViewModel(svc);
        Assert.Same(main.Home, main.CurrentPage);
        Assert.False(main.Nav[1].IsEnabled);

        main.Home.SelectedGoal = GoalOption.All.First(g => g.Goal == Core.Strategy.UserGoal.Balanced);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        Assert.Same(main.Analysis, main.CurrentPage);
        Assert.True(main.Nav[1].IsEnabled);
        Assert.Contains(main.Analysis.Findings, f => f.Finding.Id == "DISP_LOW_HZ" && f.SeverityText == "критично");
        Assert.NotNull(main.Analysis.Summary);

        main.Analysis.MeasureBeforeCommand.Execute(null);
        var before = Assert.IsType<MeasureViewModel>(main.CurrentPage);
        Assert.Equal(MeasurePhase.Before, before.Phase);
        before.SelectedMode = before.Modes.First(m => m.Mode == MeasureMode.Synthetic); // без отсчёта перед прогоном
        Assert.False(before.FinishCommand.CanExecute(null));
        await before.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, before.Completed.Count);
        for (int i = 0; i < 50 && before.LiveFrameTimes.Count == 0; i++) await Task.Delay(20); // Progress<T> вызывается асинхронно
        Assert.NotEmpty(before.LiveFrameTimes);
        await before.FinishCommand.ExecuteAsync(null);

        Assert.Same(main.Strategy, main.CurrentPage);
        Assert.Contains(main.Strategy.Fixes, f => f.Id == FixIds.DisplayMaxHz && f.IsSelected);
        Assert.Contains(main.Strategy.AbFixes, f => f.Id == FixIds.HagsAb && !f.IsSelected);
        Assert.Contains(main.Strategy.Advice, a => a.Finding.Id == "HVCI_ON");
        Assert.True(main.Strategy.ApplyCommand.CanExecute(null));
        await main.Strategy.ApplyCommand.ExecuteAsync(null);

        var apply = Assert.IsType<ApplyViewModel>(main.CurrentPage);
        Assert.Contains(apply.Log, l => l.Status == "применено");
        Assert.Equal("144", svc.Map[ChangeType.Display].Read(Targets.DisplayRefresh(@"\\.\DISPLAY1")).Value);
        apply.ContinueCommand.Execute(null);

        var after = Assert.IsType<MeasureViewModel>(main.CurrentPage);
        Assert.Equal(MeasurePhase.After, after.Phase);
        Assert.True(after.ModeLocked);
        await after.StartCommand.ExecuteAsync(null);
        await after.FinishCommand.ExecuteAsync(null);

        var cmp = Assert.IsType<CompareViewModel>(main.CurrentPage);
        Assert.Contains(cmp.Cards, c => c.Name == "Средний FPS" && c.Color == Palette.Good);
        Assert.False(cmp.SuggestRollback);
        Assert.NotEmpty(cmp.BeforeFrames);
        Assert.Contains(cmp.AppliedFixes, f => f.FixId == FixIds.DisplayMaxHz && !f.RolledBack);
        Assert.True(main.Nav[3].IsEnabled);

        // Откат одного исправления
        await cmp.RollbackFixCommand.ExecuteAsync(cmp.AppliedFixes.First(f => f.FixId == FixIds.DisplayMaxHz));
        Assert.Equal("60", svc.Map[ChangeType.Display].Read(Targets.DisplayRefresh(@"\\.\DISPLAY1")).Value);

        cmp.ExportHtmlCommand.Execute(null);
        Assert.True(File.Exists(svc.Opened.Last()));
        cmp.FinishCommand.Execute(null);
        Assert.Same(main.History, main.CurrentPage);
        Assert.Single(main.History.Sessions);
        Assert.Equal("стало лучше", main.History.Sessions[0].OutcomeText);
    }

    [Fact]
    public async Task Worse_SuggestsRollback_AndRollbackRestoresEverything()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", n => n < 2 ? 7.0 : 8.5);
        var original = svc.Map[ChangeType.Registry].Snapshot().OrderBy(k => k.Key).ToList();
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        main.Analysis.MeasureBeforeCommand.Execute(null);
        var m1 = (MeasureViewModel)main.CurrentPage;
        m1.SelectedMode = m1.Modes.First(m => m.Mode == MeasureMode.Synthetic);
        await m1.StartCommand.ExecuteAsync(null);
        await m1.FinishCommand.ExecuteAsync(null);
        await main.Strategy.ApplyCommand.ExecuteAsync(null);
        var apply = (ApplyViewModel)main.CurrentPage;
        Assert.True(apply.RebootRequired);           // MPO/BCD требуют перезагрузки
        apply.RebootNowCommand.Execute(null);
        Assert.True(svc.Restarted);

        // Имитация запуска после перезагрузки
        var s = main.Session!;
        svc.Settings.CheckForUpdates = true;
        s.AppliedAt = RebootHelper.LastBootTime.AddMinutes(-3);
        svc.Workflow.Store.Save(s);
        var main2 = new MainViewModel(svc);
        await main2.InitializeAsync();
        var post = Assert.IsType<ApplyViewModel>(main2.CurrentPage);
        Assert.Contains(post.PostRebootItems, i => i.Status == "всё в порядке");
        Assert.Equal("Доступна новая версия 9.9.9", main2.UpdateMessage);
        post.ContinueCommand.Execute(null);
        var m2 = (MeasureViewModel)main2.CurrentPage;
        await m2.StartCommand.ExecuteAsync(null);
        await m2.FinishCommand.ExecuteAsync(null);
        var cmp = (CompareViewModel)main2.CurrentPage;
        Assert.True(cmp.SuggestRollback);
        await cmp.RollbackSessionCommand.ExecuteAsync(null);
        Assert.All(cmp.AppliedFixes, f => Assert.True(f.RolledBack));
        Assert.Equal(original, svc.Map[ChangeType.Registry].Snapshot().OrderBy(k => k.Key).ToList());
    }

    [Fact]
    public async Task AbFlow_FromStrategyPage()
    {
        // A: 8 мс; B: 8 мс — эффекта нет → вариант B откатывается
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        main.Analysis.SkipToStrategyCommand.Execute(null);
        var hags = main.Strategy.AbFixes.Single(f => f.Id == FixIds.HagsAb);
        svc.ConfirmAnswer = true;
        await main.Strategy.StartAbCommand.ExecuteAsync(hags);
        var a = Assert.IsType<MeasureViewModel>(main.CurrentPage);
        Assert.Equal(MeasurePhase.AbA, a.Phase);
        a.SelectedMode = a.Modes.First(m => m.Mode == MeasureMode.Synthetic);
        await a.StartCommand.ExecuteAsync(null);
        await a.FinishCommand.ExecuteAsync(null);
        var applied = Assert.IsType<ApplyViewModel>(main.CurrentPage);
        Assert.True(applied.RebootRequired);
        Assert.Equal("2", svc.Map[ChangeType.Registry].Read(Targets.Reg(Opti.Fixes.Catalog.HagsAbFix.Key, "HwSchMode")).Value);

        applied.ContinueCommand.Execute(null);
        var b = Assert.IsType<MeasureViewModel>(main.CurrentPage);
        Assert.Equal(MeasurePhase.AbB, b.Phase);
        await b.StartCommand.ExecuteAsync(null);
        await b.FinishCommand.ExecuteAsync(null);
        var cmp = Assert.IsType<CompareViewModel>(main.CurrentPage);
        Assert.True(cmp.IsAb);
        Assert.Contains("возвращено исходное", cmp.Message);
        Assert.Equal("1", svc.Map[ChangeType.Registry].Read(Targets.Reg(Opti.Fixes.Catalog.HagsAbFix.Key, "HwSchMode")).Value);
    }

    [Fact]
    public async Task Strategy_StartupChoicesAndBitLockerGate()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        svc.Profile.Security.BitLockerOnSystemDrive = true;
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        main.Analysis.SkipToStrategyCommand.Execute(null);
        var bcd = main.Strategy.Fixes.Single(f => f.Id == FixIds.BcdTimersReset);
        Assert.False(bcd.IsSelected);                       // из-за BitLocker не отмечено
        foreach (var f in main.Strategy.Fixes) f.IsSelected = false;
        bcd.IsSelected = true;
        Assert.True(main.Strategy.NeedsBitLockerAck);
        Assert.False(main.Strategy.ApplyCommand.CanExecute(null));
        main.Strategy.BitLockerAcknowledged = true;
        Assert.True(main.Strategy.ApplyCommand.CanExecute(null));
        await main.Strategy.ApplyCommand.ExecuteAsync(null);
        var apply = (ApplyViewModel)main.CurrentPage;
        Assert.Contains(apply.Log, l => l.Status == "применено");
        Assert.False(svc.Map[ChangeType.Bcd].Read(Targets.Bcd("useplatformclock")).Exists);
    }

    private static async Task<T> WaitForPage<T>(MainViewModel main, int timeoutMs = 60000) // с замером покрытия код в разы медленнее where T : PageViewModel
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (main.CurrentPage is T t) return t;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Страница {typeof(T).Name} не открылась, сейчас {main.CurrentPage.GetType().Name}");
    }

    [Fact]
    public async Task AutoFix_RunsWholeCycleWithoutQuestions()
    {
        var svc = new FakeServices(_dir, "laptop-optimus", n => n < 2 ? 8.5 : 7.0);
        var main = new MainViewModel(svc);
        main.Home.SelectedGoal = GoalOption.All.First(g => g.Goal == Core.Strategy.UserGoal.Balanced);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(main.Analysis.CanAutoFix);
        Assert.Contains("исправлен", main.Analysis.AutoFixText);

        await main.Analysis.AutoFixCommand.ExecuteAsync(null);
        var setup = Assert.IsType<AutoSetupViewModel>(main.CurrentPage);
        Assert.Equal(TestLevel.Full, setup.SelectedLevel.Level);
        Assert.True(setup.SelectedCount > 0);
        await setup.StartCommand.ExecuteAsync(null);
        var cmp = await WaitForPage<CompareViewModel>(main);
        var s = main.Session!;
        Assert.True(s.AutoMode);
        Assert.Equal(MeasureMode.Synthetic, s.MeasureMode);
        Assert.Equal(svc.Settings.MeasureRuns, s.Before.Count);
        Assert.Equal(svc.Settings.MeasureRuns, s.After.Count);
        Assert.Contains(FixIds.DisplayMaxHz, s.AppliedFixIds);
        Assert.Contains(cmp.Cards, c => c.Name == "Средний FPS" && c.Color == Palette.Good);
    }

    [Fact]
    public async Task AutoFix_WithReboot_ContinuesAfterRestart()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", n => n < 2 ? 8.5 : 7.0);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        await main.Analysis.AutoFixCommand.ExecuteAsync(null);
        await ((AutoSetupViewModel)main.CurrentPage).StartCommand.ExecuteAsync(null);
        var apply = await WaitForPage<ApplyViewModel>(main);
        Assert.True(apply.RebootRequired);

        var s = main.Session!;
        s.AppliedAt = RebootHelper.LastBootTime.AddMinutes(-2);
        svc.Workflow.Store.Save(s);
        var main2 = new MainViewModel(svc);
        await main2.InitializeAsync();
        await WaitForPage<CompareViewModel>(main2);
        Assert.Equal(svc.Settings.MeasureRuns, main2.Session!.After.Count);
    }

    [Fact]
    public async Task AutoFix_WithoutTest_AppliesOnlyChosenFixes()
    {
        var svc = new FakeServices(_dir, "laptop-optimus", _ => 8.0);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        await main.Analysis.AutoFixCommand.ExecuteAsync(null);
        var setup = (AutoSetupViewModel)main.CurrentPage;
        setup.SelectedLevel = setup.Levels.First(l => l.Level == TestLevel.None);
        Assert.False(setup.ShowTestSettings);
        setup.ClearAllCommand.Execute(null);
        Assert.False(setup.StartCommand.CanExecute(null));
        setup.Fixes.First(f => f.Id == FixIds.DisplayMaxHz).IsSelected = true;
        await setup.StartCommand.ExecuteAsync(null);

        var apply = await WaitForPage<ApplyViewModel>(main);
        var s = main.Session!;
        Assert.True(s.SkipMeasure);
        Assert.Empty(s.Before);
        Assert.Equal(new[] { FixIds.DisplayMaxHz }, s.AppliedFixIds);
        Assert.Contains("Замер пропущен", apply.NextText);
    }

    [Fact]
    public async Task AutoFix_QuickTest_UsesChosenRunsAndDuration()
    {
        var svc = new FakeServices(_dir, "laptop-optimus", n => n < 2 ? 8.5 : 7.0);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        await main.Analysis.AutoFixCommand.ExecuteAsync(null);
        var setup = (AutoSetupViewModel)main.CurrentPage;
        setup.SelectedLevel = setup.Levels.First(l => l.Level == TestLevel.Quick);
        Assert.Equal(2, setup.Runs);
        Assert.Equal(20, setup.Duration);
        await setup.StartCommand.ExecuteAsync(null);
        await WaitForPage<CompareViewModel>(main);
        var s = main.Session!;
        Assert.Equal(2, s.Before.Count);
        Assert.Equal(2, s.After.Count);
        Assert.Equal(20, s.MeasureDurationSec);
    }

    [Fact]
    public async Task DriversPage_EnabledAfterAnalysis_ShowsAttentionAndLinks()
    {
        var svc = new FakeServices(_dir, "laptop-optimus", _ => 8.0);
        var main = new MainViewModel(svc);
        var nav = main.Nav.First(n => n.Title == "Драйверы");
        Assert.False(nav.IsEnabled);
        await main.Home.OpenDriversCommand.ExecuteAsync(null); // без анализа — запускает анализ сам
        Assert.True(nav.IsEnabled);
        Assert.Same(main.Drivers, main.CurrentPage);
        Assert.Contains("требуют внимания 2", main.Drivers.Summary);
        Assert.Equal("Поддержка ASUS", main.Drivers.OemTitle);
        main.Drivers.OnlyAttention = true;
        var rows = main.Drivers.Groups.SelectMany(g => g.Rows).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.NeedsAttention));
        main.Drivers.OpenLinkCommand.Execute(rows[0].Url);
        Assert.Contains(rows[0].Url, svc.Opened);
    }

    [Fact]
    public async Task Cleanup_SplitsManualItems_AndWarnsAboveThreshold()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var dl = Directory.CreateDirectory(Path.Combine(_dir, "Downloads")).FullName;
        File.WriteAllBytes(Path.Combine(dl, "big.iso"), new byte[5000]);
        svc.Cleanup.Add(new Opti.Workflow.Cleanup.ManualCleanupTarget("downloads", "Загрузки", "", () => new[] { dl }, () => dl, warnAboveBytes: 4000, warnText: "разберите"));
        svc.Cleanup.Add(new Opti.Workflow.Cleanup.FolderCleanupTarget("tmp", "Временные", "", () => Array.Empty<string>(), TimeSpan.Zero));
        var main = new MainViewModel(svc);
        await main.Cleanup.EstimateAsync();
        var manual = Assert.Single(main.Cleanup.ManualItems);
        Assert.Single(main.Cleanup.AutoItems);
        Assert.Equal("разберите", manual.Warning);
        Assert.Equal("Открыть папку", manual.ManualActionText);
        main.Cleanup.OpenManualCommand.Execute(manual);
        Assert.Contains(dl, svc.Opened);
    }

    [Fact]
    public void SoftwarePage_OpensOfficialSite()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var main = new MainViewModel(svc);
        var wiztree = main.Software.Categories.SelectMany(c => c.Items).First(i => i.Name == "WizTree");
        main.Software.OpenCommand.Execute(wiztree);
        Assert.Equal("https://diskanalyzer.com/", Assert.Single(svc.Opened));
    }

    [Fact]
    public async Task Analysis_StatTilesFilterFindings()
    {
        var svc = new FakeServices(_dir, "laptop-optimus", _ => 8.0);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        var a = main.Analysis;
        int total = a.Findings.Count;
        Assert.Equal(total, a.CriticalCount + a.ImportantCount + a.AdviceCount);
        a.FilterCritical = true;
        Assert.Equal(a.CriticalCount, a.Findings.Count);
        Assert.All(a.Findings, f => Assert.Equal(Core.Findings.Severity.Critical, f.Finding.Severity));
        a.FilterFixable = true; // плитки взаимоисключающие
        Assert.False(a.FilterCritical);
        Assert.All(a.Findings, f => Assert.True(f.HasFix));
        a.ClearFilterCommand.Execute(null);
        Assert.Equal(total, a.Findings.Count);
        Assert.False(a.IsFiltered);
    }

    [Fact]
    public void Tweaks_ToggleOnAndOff_RestoresOriginalValues()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var reg = svc.Map[ChangeType.Registry];
        var delayTarget = Opti.Fixes.Backends.Targets.Reg(@"HKCU\Control Panel\Desktop", "MenuShowDelay");
        reg.Seed(delayTarget, StateValue.Str("400"));
        var main = new MainViewModel(svc);
        var page = main.Tweaks;
        page.OnShown();
        var items = page.Groups.SelectMany(g => g.Items).ToList();
        Assert.True(items.Count >= 10);
        Assert.All(items, i => Assert.False(i.IsOn));

        var menu = items.First(i => i.Tweak.Id == "CLASSIC_CONTEXT_MENU");
        menu.IsOn = true;
        Assert.Null(menu.Error);
        Assert.True(page.NeedsExplorerRestart);
        page.RestartExplorerCommand.Execute(null);
        Assert.Equal(1, svc.ExplorerRestarts);
        Assert.False(page.NeedsExplorerRestart);

        var delay = items.First(i => i.Tweak.Id == "FAST_MENUS");
        delay.IsOn = true;
        Assert.Equal(StateValue.Str("100"), reg.Read(delayTarget));
        Assert.Contains("выхода", page.PendingText);

        // Выключение — точное исходное состояние
        delay.IsOn = false;
        Assert.Equal(StateValue.Str("400"), reg.Read(delayTarget));
        menu.IsOn = false;
        Assert.False(reg.Read(Opti.Fixes.Backends.Targets.Reg(@"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "")).Exists);

        // Повторная загрузка видит реальное состояние
        page.EnableRecommendedCommand.Execute(null);
        page.Load();
        var reloaded = page.Groups.SelectMany(g => g.Items).ToList();
        Assert.All(reloaded.Where(i => i.Recommended && i.IsAvailable), i => Assert.True(i.IsOn, i.Title));
        Assert.Contains(svc.Journal.GetSessions(), x => x.SessionId == Opti.Fixes.Services.TweakService.SessionId);
    }

    [Fact]
    public void Nav_GroupsToolsAndFooter()
    {
        var main = new MainViewModel(new FakeServices(_dir, "desktop-rtx", _ => 8.0));
        Assert.Equal(new[] { "История", "Настройки" }, main.NavBottom.Select(n => n.Title));
        Assert.Contains(main.NavTop, n => n.Header == "ИНСТРУМЕНТЫ" && n.Title == "Драйверы");
        Assert.All(main.Nav.Where(n => !n.IsEnabled), n => Assert.False(string.IsNullOrEmpty(n.DisabledHint)));
    }

    [Fact]
    public async Task Measure_DefaultsToSynthetic_WithShortDuration()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        main.Analysis.MeasureBeforeCommand.Execute(null);
        var m = (MeasureViewModel)main.CurrentPage;
        Assert.Equal(MeasureMode.Synthetic, m.SelectedMode.Mode);
        Assert.Equal(svc.Settings.SyntheticDurationSec, m.Duration);
        Assert.DoesNotContain(m.Modes, x => x.Mode == MeasureMode.PresentMon);
        m.SelectedMode = m.Modes.First(x => x.Mode == MeasureMode.Game);
        Assert.Equal(svc.Settings.MeasureDurationSec, m.Duration);
        Assert.True(m.IsGameMode);
    }

    [Fact]
    public async Task Cleanup_EstimatesAndCleansSelected()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var root = Directory.CreateDirectory(Path.Combine(_dir, "junk", "temp")).FullName;
        for (int i = 0; i < 3; i++)
        {
            var f = Path.Combine(root, $"f{i}.tmp");
            File.WriteAllBytes(f, new byte[2048]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-5));
        }
        svc.Cleanup.Add(new Opti.Workflow.Cleanup.FolderCleanupTarget("t", "Временные", "", () => new[] { root }, TimeSpan.FromDays(1)));
        svc.Cleanup.Add(new Opti.Workflow.Cleanup.FolderCleanupTarget("u", "Не отмечено", "", () => new[] { root }, TimeSpan.FromDays(1), defaultSelected: false));
        var main = new MainViewModel(svc);
        await main.Cleanup.EstimateCommand.ExecuteAsync(null);
        Assert.Equal(6144, main.Cleanup.Items[0].Bytes);
        Assert.Contains("6 КБ", main.Cleanup.TotalText);
        Assert.True(main.Cleanup.CleanCommand.CanExecute(null));
        await main.Cleanup.CleanCommand.ExecuteAsync(null);
        Assert.Empty(Directory.GetFiles(root));
        Assert.Contains("Освобождено 6 КБ", main.Cleanup.ResultText);
    }

    [Fact]
    public void Settings_SaveAndExclusions()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8.0);
        var main = new MainViewModel(svc);
        var st = main.SettingsPage;
        st.MeasureRuns = 50;                                // ограничивается до 10
        st.ExcludedFixes.First(x => x.Name == FixIds.MpoReset).IsChecked = true;
        st.SaveCommand.Execute(null);
        Assert.Equal(10, svc.Settings.MeasureRuns);
        Assert.Contains(FixIds.MpoReset, svc.Settings.ExcludedFixIds);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void Navigation_OnlyCurrentPageIsActive()
    {
        var main = new MainViewModel(new FakeServices(_dir, "desktop-rtx", _ => 8));
        Assert.True(main.Nav[0].IsActive);
        // «Результат» без сравнения не должен подсвечиваться вместе с «Главной»
        Assert.All(main.Nav.Skip(1), n => Assert.False(n.IsActive));
        main.Navigate(main.Home);
        Assert.Single(main.Nav, n => n.IsActive);
        main.NavigateToCommand.Execute(main.Nav[3]); // недоступный пункт — ничего не происходит
        Assert.Same(main.Home, main.CurrentPage);
    }

    [Fact]
    public async Task Home_OffersThreeModes_BalancedByDefault()
    {
        var main = new MainViewModel(new FakeServices(_dir, "desktop-rtx", _ => 8));
        Assert.Equal(new[] { Core.Strategy.UserGoal.Maximum, Core.Strategy.UserGoal.Balanced, Core.Strategy.UserGoal.Minimal },
            main.Home.Goals.Select(g => g.Goal));
        Assert.Equal(Core.Strategy.UserGoal.Balanced, main.Home.SelectedGoal!.Goal);
        Assert.All(main.Home.Goals, g => { Assert.NotEmpty(g.Summary); Assert.NotEmpty(g.Includes); });
        Assert.All(main.Home.Comparison, r => Assert.Equal(3, r.Cells.Count));

        main.Home.SelectedGoal = main.Home.Goals.Single(g => g.Goal == Core.Strategy.UserGoal.Minimal);
        Assert.Contains("Минимальная", main.Home.SelectedGoalHint);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(Core.Strategy.UserGoal.Minimal, main.Session!.Goal);
    }

    [Fact]
    public async Task Strategy_ModeSwitch_RebuildsPlanAndSavesSession()
    {
        var main = new MainViewModel(new FakeServices(_dir, "desktop-rtx", _ => 8));
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        var s = main.Session!;
        main.Strategy.Load(s);
        Assert.Equal(Core.Strategy.UserGoal.Balanced, main.Strategy.SelectedGoal!.Goal);
        Assert.Contains(main.Strategy.Fixes, f => f.Id == FixIds.PowerSchemePerf && f.IsSelected);

        main.Strategy.SelectedGoal = main.Strategy.Goals.Single(g => g.Goal == Core.Strategy.UserGoal.Minimal);
        Assert.Equal(Core.Strategy.UserGoal.Minimal, s.Goal);
        Assert.Equal(Core.Strategy.UserGoal.Minimal, main.Services.Workflow.Store.Load(s.Id)!.Goal);
        Assert.Contains(main.Strategy.Fixes, f => f.Id == FixIds.PowerSchemePerf && !f.IsSelected);
        Assert.All(main.Strategy.Fixes.Where(f => f.IsSelected), f => Assert.Equal(FixIntent.Repair, f.Fix.Intent));
    }

    [Fact]
    public async Task Startup_FindsSettingsRevertedByWindows_AndReappliesFromHistory()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        var s = main.Session!;
        var wf = svc.Workflow;
        wf.Apply(s, wf.BuildPlan(s).Fixes.Where(f => f.Selected && !f.ApplySeparately).Select(f => f.Fix).ToList());
        var rec = svc.Journal.GetAll().First(r => r.Type == ChangeType.Registry && !r.RolledBack);
        svc.Map[ChangeType.Registry].Write(rec.Target, rec.Before); // «обновление Windows»

        var restarted = new MainViewModel(svc);
        await restarted.InitializeAsync();
        Assert.Contains("вернули", restarted.Toast);
        var row = Assert.Single(restarted.History.VerifyRows, r => r.Item.Record.Target == rec.Target);
        Assert.True(row.IsChecked);

        await restarted.History.ReapplyCommand.ExecuteAsync(null);
        Assert.Equal(rec.After, svc.Map[ChangeType.Registry].Read(rec.Target));
        Assert.False(restarted.History.HasDrift);
    }

    [Fact]
    public async Task Verify_DoesNotPreselectBootSettings()
    {
        var svc = new FakeServices(_dir, "desktop-rtx", _ => 8);
        var main = new MainViewModel(svc);
        await main.Home.AnalyzeCommand.ExecuteAsync(null);
        var s = main.Session!;
        s.FixOptions[FixIds.BcdTimersReset] = new() { ["bitlockerAcknowledged"] = "1" };
        svc.Workflow.Apply(s, new[] { svc.Workflow.Catalog.Single(f => f.Id == FixIds.BcdTimersReset) });
        var rec = svc.Journal.GetAll().First(r => r.Type == ChangeType.Bcd);
        svc.Map[ChangeType.Bcd].Write(rec.Target, rec.Before);

        await main.History.VerifyCommand.ExecuteAsync(null);
        var row = Assert.Single(main.History.VerifyRows, r => r.Item.Record.Type == ChangeType.Bcd && r.Item.Record.Target == rec.Target);
        Assert.False(row.IsChecked);
        Assert.Contains("BitLocker", row.StatusText);
    }
}
