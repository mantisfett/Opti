using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public enum TestLevel { Full, Quick, None }

public sealed class TestLevelOption
{
    public required TestLevel Level { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Icon { get; init; }
}

/// <summary>
/// Настройка перед «Исправить автоматически»: какие исправления применить и какой тест провести
/// (полный, быстрый или без теста). Без теста результат честно помечается как непроверенный.
/// </summary>
public sealed partial class AutoSetupViewModel : PageViewModel
{
    private readonly TuneSession _session;

    public AutoSetupViewModel(MainViewModel main, TuneSession s) : base(main)
    {
        _session = s;
        foreach (var f in Candidates(main, s))
        {
            f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FixItem.IsSelected)) Refresh(); };
            Fixes.Add(f);
        }
        var st = main.Services.Settings;
        Levels = new[]
        {
            new TestLevelOption { Level = TestLevel.Full, Icon = "", Title = Loc.T("Полный тест"),
                Description = Loc.T($"{st.MeasureRuns} прогона по {st.SyntheticDurationSec} с до и после. Самый точный результат.") },
            new TestLevelOption { Level = TestLevel.Quick, Icon = "", Title = Loc.T("Быстрый тест"),
                Description = Loc.T("2 прогона по 20 с. Быстрее, но мелкие изменения утонут в шуме.") },
            new TestLevelOption { Level = TestLevel.None, Icon = "", Title = Loc.T("Без теста"),
                Description = Loc.T("Только применить. Эффект не будет доказан замером.") },
        };
        _selectedLevel = Levels[0];
        _runs = st.MeasureRuns;
        _duration = st.SyntheticDurationSec;
        Refresh();
    }

    public override string Title => Loc.T("Исправить автоматически");

    /// <summary>Исправления, которые можно применить в автоцикле (без A/B и без ручного выбора пунктов).</summary>
    public static List<FixItem> Candidates(MainViewModel main, TuneSession s) =>
        main.Services.Workflow.BuildPlan(s).Fixes
            .Where(f => !f.ApplySeparately && !f.Fix.NeedsUserChoice)
            .Select(f => new FixItem(f))
            .ToList();

    public ObservableCollection<FixItem> Fixes { get; } = new();
    public IReadOnlyList<TestLevelOption> Levels { get; }
    /// <summary>Замер «до» уже сделан — тест повторно не нужен.</summary>
    public bool HasBefore => _session.Before.Count > 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowTestSettings))] private TestLevelOption _selectedLevel;
    [ObservableProperty] private int _runs;
    [ObservableProperty] private int _duration;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private int _selectedCount;
    [ObservableProperty] private string? _planText;
    public bool ShowTestSettings => SelectedLevel.Level != TestLevel.None && !HasBefore;

    partial void OnSelectedLevelChanged(TestLevelOption value)
    {
        var st = Main.Services.Settings;
        (Runs, Duration) = value.Level switch
        {
            TestLevel.Quick => (2, 20),
            _ => (st.MeasureRuns, st.SyntheticDurationSec),
        };
        Refresh();
    }
    partial void OnRunsChanged(int value) => Refresh();
    partial void OnDurationChanged(int value) => Refresh();

    private void Refresh()
    {
        SelectedCount = Fixes.Count(f => f.IsSelected);
        bool reboot = Fixes.Any(f => f.IsSelected && f.RequiresReboot);
        int perRun = Duration + 5 + 3;
        int min = Math.Max(1, (int)Math.Round(2 * Runs * perRun / 60.0));
        var steps = new List<string>();
        bool test = SelectedLevel.Level != TestLevel.None && !HasBefore;
        if (test) steps.Add(Loc.T($"тест ≈ {min / 2.0:0.#} мин"));
        steps.Add(Loc.T($"{SelectedCount} {AnalysisViewModel.Plural(SelectedCount, "исправление", "исправления", "исправлений")} с точкой восстановления"));
        if (reboot) steps.Add(Loc.T("перезагрузка (Opti продолжит сам)"));
        if (SelectedLevel.Level != TestLevel.None) steps.Add(Loc.T("повторный тест и результат"));
        PlanText = string.Join(" → ", steps);
    }

    [RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand] private void SelectAll() { foreach (var f in Fixes) f.IsSelected = true; }
    [RelayCommand] private void ClearAll() { foreach (var f in Fixes) f.IsSelected = false; }

    private bool CanStart() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var s = _session;
        s.AutoFixIds = Fixes.Where(f => f.IsSelected).Select(f => f.Id).ToList();
        s.SkipMeasure = SelectedLevel.Level == TestLevel.None;
        if (!s.SkipMeasure && !HasBefore)
        {
            s.MeasureRuns = Math.Clamp(Runs, 1, 10);
            s.MeasureDurationSec = Math.Clamp(Duration, 10, 300);
        }
        try { await Main.RunAutoFlowAsync(s); }
        catch (Exception ex) { Main.ShowMessage(ex.Message, error: true); }
    }

    [RelayCommand] private void Back() => Main.Navigate(Main.Analysis);
}
