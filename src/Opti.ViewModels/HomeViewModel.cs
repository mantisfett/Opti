using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed partial class HomeViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    public HomeViewModel(MainViewModel main) : base(main)
    {
        _selectedGoal = Goals.First(g => g.Goal == main.Services.Settings.DefaultGoal);
    }

    public override string Title => Loc.T("Главная");
    public IReadOnlyList<GoalOption> Goals { get; } = GoalOption.All;
    /// <summary>Таблица «чем отличаются режимы».</summary>
    public IReadOnlyList<ModeComparisonRow> Comparison { get; } = ModeComparisonRow.All;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedGoalHint))] private GoalOption? _selectedGoal;

    /// <summary>Строка у кнопки анализа: какой режим выбран.</summary>
    public string SelectedGoalHint => SelectedGoal is { } g ? Loc.T($"Режим «{g.Title}» · 30–60 секунд · при анализе ничего не меняется") : Loc.T("30–60 секунд · при анализе ничего не меняется");
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))] private bool _isAnalyzing;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _openSessionText;
    [ObservableProperty] private bool _hasOpenSession;

    /// <summary>Свободное место на системном диске — подсказка открыть «Очистку».</summary>
    public string? DiskText
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) return null;
                var d = new DriveInfo(root);
                if (!d.IsReady || d.TotalSize == 0) return null;
                double free = d.AvailableFreeSpace / 1e9, total = d.TotalSize / 1e9;
                return Loc.T($"Диск {root.TrimEnd('\\', '/')} — свободно {free:0} ГБ из {total:0} ГБ");
            }
            catch { return null; }
        }
    }
    public bool DiskLow
    {
        get
        {
            try { var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!); return d.IsReady && d.AvailableFreeSpace < d.TotalSize * 0.15; }
            catch { return false; }
        }
    }

    [RelayCommand] private void OpenCleanup() => Main.Navigate(Main.Cleanup);
    [RelayCommand] private void OpenSoftware() => Main.Navigate(Main.Software);

    /// <summary>Драйверы проверяются в составе анализа: если его ещё не было — запускаем.</summary>
    [RelayCommand]
    private async Task OpenDriversAsync()
    {
        if (Main.Session?.Profile is null)
        {
            await AnalyzeAsync();
            if (Main.Session?.Profile is null) return;
        }
        Main.Navigate(Main.Drivers);
    }

    public void Refresh()
    {
        var s = Main.Session;
        HasOpenSession = s is not null && s.Stage is not (SessionStage.Closed or SessionStage.Analyzed);
        OpenSessionText = s is null ? null : s.Stage switch
        {
            SessionStage.MeasuredBefore => Loc.T("Замер «до» сделан — следующий шаг: стратегия."),
            SessionStage.Applied => Loc.T("Исправления применены — сделайте замер «после»."),
            SessionStage.AwaitingReboot => Loc.T("Ожидается перезагрузка. После неё Opti продолжит сам."),
            SessionStage.VerifiedAfterReboot => Loc.T("Перезагрузка выполнена и проверена — сделайте замер «после»."),
            SessionStage.MeasuredAfter => Loc.T("Замеры готовы — откройте сравнение."),
            SessionStage.Compared => Loc.T("Сравнение готово."),
            _ => null,
        };
    }

    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsAnalyzing = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(s => ProgressText = Loc.T("Собираю данные: ") + s + "…");
            var result = await Main.Services.AnalyzeAsync(progress, _cts.Token);
            var session = Main.Services.Workflow.StartSession(result, SelectedGoal?.Goal ?? Main.Services.Settings.DefaultGoal);
            Main.Session = session;
            Main.Analysis.Load(session, result.Timings.Select(t => t.Error is null ? null : $"{t.Name}: {t.Error}").OfType<string>().ToList());
            Main.Navigate(Main.Analysis);
        }
        catch (OperationCanceledException) { Main.ShowMessage(Loc.T("Анализ отменён.")); }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Анализ не удался: ") + ex.Message, error: true); }
        finally
        {
            IsAnalyzing = false;
            ProgressText = null;
        }
    }

    [RelayCommand] private void CancelAnalysis() => _cts?.Cancel();

    [RelayCommand]
    private void ContinueSession()
    {
        var s = Main.Session;
        if (s is null) return;
        switch (s.Stage)
        {
            case SessionStage.MeasuredBefore:
                Main.Strategy.Load(s);
                Main.Navigate(Main.Strategy);
                break;
            case SessionStage.Applied or SessionStage.VerifiedAfterReboot:
                Main.Navigate(MeasureViewModel.ForAfter(Main));
                break;
            case SessionStage.MeasuredAfter or SessionStage.Compared:
                Main.ShowCompare(CompareViewModel.ForSession(Main, s));
                break;
            default:
                Main.Analysis.Load(s, new());
                Main.Navigate(Main.Analysis);
                break;
        }
    }
}
