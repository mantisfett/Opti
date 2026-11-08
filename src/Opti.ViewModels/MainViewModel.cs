using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Strategy;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(MainViewModel main) => Main = main;
    public MainViewModel Main { get; }
    public abstract string Title { get; }
    /// <summary>Вызывается при показе страницы.</summary>
    public virtual void OnShown() { }
}

public sealed partial class NavItem : ObservableObject
{
    public required string Title { get; init; }
    public required string Icon { get; init; }
    /// <summary>Страница пункта; null — страницы ещё нет (например, «Результат» до сравнения).</summary>
    public required Func<PageViewModel?> Page { get; init; }
    /// <summary>Заголовок группы над пунктом (первый пункт группы).</summary>
    public string? Header { get; init; }
    /// <summary>Пункт внизу панели (История, Настройки).</summary>
    public bool IsFooter { get; init; }
    /// <summary>Подсказка для недоступного пункта.</summary>
    public string? DisabledHint { get; init; }
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isActive;
}

/// <summary>Корневая модель окна: навигация, текущая сессия, общие сообщения.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(IAppServices services)
    {
        Services = services;
        Home = new HomeViewModel(this);
        Analysis = new AnalysisViewModel(this);
        Strategy = new StrategyViewModel(this);
        History = new HistoryViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        Cleanup = new CleanupViewModel(this);
        Drivers = new DriversViewModel(this);
        Software = new SoftwareViewModel(this);
        Tweaks = new TweaksViewModel(this);
        Network = new NetworkViewModel(this);
        Nav = new ObservableCollection<NavItem>
        {
            // Значки — глифы шрифта Segoe Fluent Icons / Segoe MDL2 Assets
            new() { Title = Loc.T("Главная"), Icon = "\uE80F", Page = () => Home },
            new() { Title = Loc.T("Анализ"), Icon = "\uE9D9", Page = () => Analysis, IsEnabled = false, Header = Loc.T("ОПТИМИЗАЦИЯ"), DisabledHint = Loc.T("Появится после анализа") },
            new() { Title = Loc.T("Исправления"), Icon = "\uE90F", Page = () => Strategy, IsEnabled = false, DisabledHint = Loc.T("Появится после анализа") },
            new() { Title = Loc.T("Результат"), Icon = "\uE9D2", Page = () => Compare, IsEnabled = false, DisabledHint = Loc.T("Появится после замера «после»") },
            new() { Title = Loc.T("Драйверы"), Icon = "\uE950", Page = () => Drivers, IsEnabled = false, Header = Loc.T("ИНСТРУМЕНТЫ"), DisabledHint = Loc.T("Появится после анализа") },
            new() { Title = Loc.T("Твики"), Icon = "\uE70F", Page = () => Tweaks },
            new() { Title = Loc.T("Сеть"), Icon = "\uE839", Page = () => Network },
            new() { Title = Loc.T("Очистка"), Icon = "\uEA99", Page = () => Cleanup },
            new() { Title = Loc.T("Программы"), Icon = "\uE71D", Page = () => Software },
            new() { Title = Loc.T("История"), Icon = "\uE81C", Page = () => History, IsFooter = true },
            new() { Title = Loc.T("Настройки"), Icon = "\uE713", Page = () => SettingsPage, IsFooter = true },
        };
        _currentPage = Home;
        Nav[0].IsActive = true;
    }

    public IAppServices Services { get; }
    public HomeViewModel Home { get; }
    public AnalysisViewModel Analysis { get; }
    public StrategyViewModel Strategy { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel SettingsPage { get; }
    public CleanupViewModel Cleanup { get; }
    public DriversViewModel Drivers { get; }
    public SoftwareViewModel Software { get; }
    public TweaksViewModel Tweaks { get; }
    public NetworkViewModel Network { get; }
    public CompareViewModel? Compare { get; private set; }
    public ObservableCollection<NavItem> Nav { get; }
    public IEnumerable<NavItem> NavTop => Nav.Where(n => !n.IsFooter);
    public IEnumerable<NavItem> NavBottom => Nav.Where(n => n.IsFooter);
    public string WindowTitle => $"Opti {Services.AppVersion}";
    /// <summary>Плашка демо-режима над страницей.</summary>
    public string? DemoBanner => Services.IsDemo
        ? Loc.T("Демо-режим: пример настольного ПК с RTX 4070. Анализ, исправления, замеры и откат работают на эмуляции — система не меняется.")
        : null;

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private TuneSession? _session;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private string? _toastColor;
    [ObservableProperty] private string? _updateMessage;
    [ObservableProperty] private string? _updateUrl;

    partial void OnSessionChanged(TuneSession? value)
    {
        Nav[1].IsEnabled = value?.Profile is not null;
        Nav[2].IsEnabled = value?.Profile is not null;
        Nav[3].IsEnabled = Compare is not null;
        Nav[4].IsEnabled = value?.Profile is not null;
        Home.Refresh();
    }

    public void Navigate(PageViewModel page)
    {
        CurrentPage = page;
        foreach (var n in Nav)
        {
            var target = n.Page();
            n.IsActive = target is not null && ReferenceEquals(target, page);
        }
        page.OnShown();
    }

    [RelayCommand]
    private void NavigateTo(NavItem item)
    {
        if (item.IsEnabled && item.Page() is { } page) Navigate(page);
    }

    public void ShowMessage(string text, bool error = false)
    {
        Toast = text;
        ToastColor = error ? Palette.Bad : Palette.Accent;
    }

    [RelayCommand] private void CloseToast() => Toast = null;

    [RelayCommand]
    private void OpenUpdate()
    {
        if (UpdateUrl is not null) Services.OpenPath(UpdateUrl);
    }

    public void ShowCompare(CompareViewModel vm)
    {
        Compare = vm;
        Nav[3].IsEnabled = true;
        Navigate(vm);
    }

    /// <summary>Запуск окна: проверка обновлений и продолжение сессии после перезагрузки.</summary>
    public async Task InitializeAsync()
    {
        var wf = Services.Workflow;
        var open = wf.Store.LatestOpen();
        if (open is not null)
        {
            Session = open;
            if (open.AbTests.Any(a => a.Phase == AbPhase.AwaitRebootB))
            {
                if (await Task.Run(() => wf.ResumeAbIfRebooted(open)))
                {
                    ShowMessage(Loc.T("Перезагрузка выполнена. Продолжаем A/B-проверку: замер варианта B."));
                    Navigate(MeasureViewModel.ForAbB(this));
                }
            }
            else if (open.Stage == SessionStage.AwaitingReboot)
            {
                ShowMessage(Loc.T("Проверяю, что изменения применились после перезагрузки…"));
                var check = await Task.Run(() => wf.ResumeIfRebooted(open));
                Toast = null;
                if (check is not null)
                {
                    Session = open;
                    if (open.AutoMode && check.Healthy && open.Before.Count > 0)
                    {
                        ShowMessage(Loc.T($"Перезагрузка выполнена, все изменения на месте ({check.Verified}). Продолжаю: замер после изменений."));
                        Navigate(MeasureViewModel.ForAfter(this, auto: true));
                    }
                    else
                    {
                        var apply = new ApplyViewModel(this, report: null) { PostReboot = check };
                        apply.FillPostReboot(check);
                        Navigate(apply);
                    }
                }
            }
            else if (open.Stage >= SessionStage.Applied)
            {
                Home.Refresh();
            }
            Session = open;
        }

        if (Services.StartupNotice is { } notice) ShowMessage(notice);

        // «Страж»: обновления Windows и драйверов иногда возвращают настройки — проверяем тихо при запуске
        if (Services.Settings.VerifyOnStartup && Services.Journal.GetAll().Any(r => !r.RolledBack))
        {
            try
            {
                var report = await Task.Run(() => Services.Workflow.Verify());
                History.ShowVerify(report);
                if (report.Reverted.Any())
                    ShowMessage(Loc.T($"Windows или драйвер вернули настройки, изменённые Opti ({report.Reverted.Count()}). ") +
                                Loc.T("Откройте «Историю», чтобы применить их снова."));
            }
            catch (Exception ex) { Opti.Core.Diagnostics.OptiLog.Warn("Проверка изменений при запуске не удалась", ex); }
        }

        if (Services.Settings.CheckForUpdates)
        {
            var u = await Services.CheckForUpdatesAsync();
            if (u is { IsNewer: true })
            {
                UpdateMessage = Loc.T($"Доступна новая версия {u.LatestVersion}");
                UpdateUrl = u.Url;
            }
        }
    }

    public UserGoal SelectedGoal => Home.SelectedGoal?.Goal ?? Services.Settings.DefaultGoal;

    // ---------------- Автоматический цикл ----------------

    /// <summary>Рекомендованные исправления без спорных (A/B) — то, что применяет автоматический режим.</summary>
    public IReadOnlyList<Opti.Core.Fixes.IFix> RecommendedFixes(TuneSession s) =>
        Services.Workflow.BuildPlan(s).Fixes.Where(f => f.Selected && !f.ApplySeparately).Select(f => f.Fix).ToList();

    /// <summary>«Исправить автоматически»: сначала страница настройки (что исправлять, какой тест или без теста).</summary>
    public Task StartAutoFlowAsync()
    {
        var s = Session ?? throw new InvalidOperationException(Loc.T("Сначала анализ."));
        if (RecommendedFixes(s).Count == 0 && AutoSetupViewModel.Candidates(this, s).Count == 0)
        {
            ShowMessage(Loc.T("Автоматически исправлять нечего — посмотрите советы в отчёте."));
            return Task.CompletedTask;
        }
        Navigate(new AutoSetupViewModel(this, s));
        return Task.CompletedTask;
    }

    /// <summary>Запуск автоцикла с выбранными параметрами.</summary>
    public async Task RunAutoFlowAsync(TuneSession s)
    {
        s.AutoMode = true;
        Services.Workflow.Store.Save(s);
        Session = s;
        if (s.SkipMeasure || s.Before.Count > 0) Navigate(await ApplyRecommendedNavigateAsync(s));
        else Navigate(MeasureViewModel.ForBefore(this, auto: true));
    }

    /// <summary>Исправления автоцикла: выбранные на странице настройки или рекомендованные.</summary>
    public IReadOnlyList<Opti.Core.Fixes.IFix> AutoFixes(TuneSession s) =>
        s.AutoFixIds is { } ids
            ? Services.Workflow.Catalog.Where(f => ids.Contains(f.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : RecommendedFixes(s);

    /// <summary>Шаг автоцикла после замера «до».</summary>
    public async Task ApplyRecommendedAsync() => Navigate(await ApplyRecommendedNavigateAsync(Session!));

    private async Task<PageViewModel> ApplyRecommendedNavigateAsync(TuneSession s)
    {
        var fixes = AutoFixes(s);
        var report = await Task.Run(() => Services.Workflow.Apply(s, fixes, Services.ResumeExePath));
        Session = s;
        if (!report.RebootRequired && !s.SkipMeasure) return MeasureViewModel.ForAfter(this, auto: true);
        var vm = new ApplyViewModel(this, report);
        if (s.SkipMeasure)
            vm.NextText = report.RebootRequired
                ? Loc.T("Нужна перезагрузка. Замер пропущен — эффект не проверен; при желании сделайте замер позже.")
                : Loc.T("Готово. Замер пропущен, поэтому эффект не доказан. Любое изменение можно откатить в «Истории».");
        return vm;
    }
}
