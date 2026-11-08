using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Measure.Stats;
using Opti.Workflow.Measuring;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public enum MeasurePhase { Before, After, AbA, AbB }

public sealed class ModeOption
{
    public required MeasureMode Mode { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool Recommended { get; init; }
    public bool Available { get; init; } = true;
}

/// <summary>Замер «до», «после» и варианты A/B. Синтетический тест — по умолчанию: воспроизводим и не требует игры.</summary>
public sealed partial class MeasureViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    private bool _autoStarted;

    private MeasureViewModel(MainViewModel main, MeasurePhase phase) : base(main)
    {
        Phase = phase;
        var svc = main.Services;
        Modes = new[]
        {
            new ModeOption { Mode = MeasureMode.Synthetic, Title = Loc.T("Синтетический тест"), Recommended = true, Available = svc.IsModeAvailable(MeasureMode.Synthetic),
                Description = Loc.T("Своя 3D-сцена с постоянной нагрузкой. Около двух минут, игра не нужна, результат воспроизводим.") },
            new ModeOption { Mode = MeasureMode.Game, Title = Loc.T("Своя игра"), Available = svc.IsModeAvailable(MeasureMode.Game),
                Description = Loc.T("Вы играете, Opti записывает кадры. Повторяйте один и тот же отрезок — иначе сравнение будет шумным.") },
        };
        var s = main.Session;
        // «После» и вариант B — строго в тех же условиях, что «до» / A
        ModeLocked = phase is MeasurePhase.After or MeasurePhase.AbB;
        var wanted = ModeLocked || phase == MeasurePhase.AbA ? s?.MeasureMode ?? MeasureMode.Synthetic : MeasureMode.Synthetic;
        _selectedMode = Modes.FirstOrDefault(m => m.Mode == wanted && m.Available) ?? Modes.FirstOrDefault(m => m.Available) ?? Modes[0];
        _runs = svc.Settings.MeasureRuns;
        _warmup = svc.Settings.MeasureWarmupSec;
        _duration = svc.Settings.MeasureDurationSec;
        ApplyModeDefaults();
        // Параметры, выбранные на странице настройки автоцикла
        if (s?.MeasureRuns is { } r) _runs = Math.Clamp(r, 1, 10);
        if (s?.MeasureDurationSec is { } dur && _selectedMode.Mode == MeasureMode.Synthetic) _duration = Math.Clamp(dur, 10, 300);
        _processName = s?.GameProcess;
        var previous = phase switch
        {
            MeasurePhase.After => s?.Before,
            MeasurePhase.AbB => s?.AbTests.LastOrDefault()?.RunsA,
            _ => null,
        };
        if (previous is { Count: > 0 }) _runs = Math.Max(1, previous.Count);
    }

    public static MeasureViewModel ForBefore(MainViewModel m, bool auto = false) => new(m, MeasurePhase.Before) { AutoRun = auto };
    public static MeasureViewModel ForAfter(MainViewModel m, bool auto = false) => new(m, MeasurePhase.After) { AutoRun = auto };
    public static MeasureViewModel ForAbA(MainViewModel m) => new(m, MeasurePhase.AbA);
    public static MeasureViewModel ForAbB(MainViewModel m) => new(m, MeasurePhase.AbB);

    public MeasurePhase Phase { get; }
    /// <summary>Запуститься сразу при показе и само перейти дальше (автоматический цикл).</summary>
    public bool AutoRun { get; init; }

    public override string Title => Phase switch
    {
        MeasurePhase.Before => Loc.T("Замер до изменений"),
        MeasurePhase.After => Loc.T("Замер после изменений"),
        MeasurePhase.AbA => Loc.T("A/B: вариант A"),
        _ => Loc.T("A/B: вариант B"),
    };
    public string Hint => Phase switch
    {
        MeasurePhase.Before => Loc.T("Чтобы честно показать результат, сначала нужен замер. Синтетический тест сделает всё сам."),
        MeasurePhase.After => Loc.T("Тот же тест, что и до изменений, — так сравнение будет честным."),
        MeasurePhase.AbA => Loc.T("Сначала замер текущего состояния."),
        _ => Loc.T("Теперь тот же замер с изменённой настройкой."),
    };
    public IReadOnlyList<ModeOption> Modes { get; }
    public bool ModeLocked { get; }
    public bool ModeEditable => !ModeLocked && !AutoRun;
    public bool CanSkip => Phase == MeasurePhase.Before && !AutoRun;
    public string FinishText => Phase switch
    {
        MeasurePhase.Before => Loc.T("Дальше"),
        MeasurePhase.After => Loc.T("Показать результат"),
        MeasurePhase.AbA => Loc.T("Применить вариант B"),
        _ => Loc.T("Показать решение"),
    };
    public string EstimateText
    {
        get
        {
            int perRun = Duration + Warmup + (SelectedMode.Mode == MeasureMode.Synthetic ? 3 : 8);
            var min = Math.Max(1, (int)Math.Round(Runs * perRun / 60.0));
            return Loc.T($"≈ {min} мин · {Runs} × {Duration} с");
        }
    }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] [NotifyPropertyChangedFor(nameof(EstimateText), nameof(IsGameMode))] private ModeOption _selectedMode;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EstimateText))] private int _runs;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EstimateText))] private int _duration;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EstimateText))] private int _warmup;
    [ObservableProperty] private string? _processName;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(FinishCommand))] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private IReadOnlyList<double> _liveFrameTimes = Array.Empty<double>();
    [ObservableProperty] private string? _liveFps;
    [ObservableProperty] private bool _showAdvanced;
    public bool IsGameMode => SelectedMode.Mode == MeasureMode.Game;
    public ObservableCollection<RunSummary> Completed { get; } = new();

    partial void OnSelectedModeChanged(ModeOption value)
    {
        if (!IsRunning) ApplyModeDefaults();
    }

    private void ApplyModeDefaults()
    {
        var st = Main.Services.Settings;
        if (SelectedMode.Mode == MeasureMode.Synthetic) { Duration = st.SyntheticDurationSec; Warmup = 5; }
        else { Duration = st.MeasureDurationSec; Warmup = st.MeasureWarmupSec; }
    }

    public override async void OnShown()
    {
        if (!AutoRun || _autoStarted) return;
        _autoStarted = true;
        await StartCommand.ExecuteAsync(null);
        if (Completed.Count > 0 && FinishCommand.CanExecute(null)) await FinishCommand.ExecuteAsync(null);
    }

    private bool CanStart() => !IsRunning && SelectedMode.Available;
    private bool CanFinish() => !IsRunning && Completed.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            var runner = Main.Services.CreateRunner(SelectedMode.Mode);
            int toDo = Math.Max(1, Runs - Completed.Count);
            for (int i = 0; i < toDo; i++)
            {
                if (SelectedMode.Mode == MeasureMode.Game)
                    for (int c = 5; c > 0; c--)
                    {
                        Status = Loc.T($"Переключитесь в игру — прогон {Completed.Count + 1} из {Runs} начнётся через {c} с");
                        await Task.Delay(1000, _cts.Token);
                    }
                Status = Loc.T($"Прогон {Completed.Count + 1} из {Runs}");
                var progress = new Progress<MeasureProgress>(p =>
                {
                    Progress = p.TotalSec > 0 ? 100 * p.ElapsedSec / p.TotalSec : 0;
                    if (p.RecentFrameTimesMs.Count > 0)
                    {
                        LiveFrameTimes = p.RecentFrameTimesMs;
                        LiveFps = $"{1000.0 / p.RecentFrameTimesMs.Average():0} FPS";
                    }
                    Status = Loc.T($"Прогон {Completed.Count + 1} из {Runs} · {(p.Warmup ? "прогрев" : "запись")} {p.ElapsedSec:0} / {p.TotalSec:0} с");
                });
                var label = Phase switch { MeasurePhase.Before => Loc.T("до"), MeasurePhase.After => Loc.T("после"), MeasurePhase.AbA => "A", _ => "B" };
                var run = await runner.RunAsync(new MeasureRequest($"{label} #{Completed.Count + 1}", Duration, Warmup,
                    string.IsNullOrWhiteSpace(ProcessName) || SelectedMode.Mode == MeasureMode.Synthetic ? null : ProcessName,
                    SelectedMode.Mode == MeasureMode.Synthetic ? Loc.T("синтетика") : Loc.T("игра")), progress, _cts.Token);
                Completed.Add(new RunSummary(run));
                if (SelectedMode.Mode == MeasureMode.Game) ProcessName ??= run.Conditions.ProcessName;
                FinishCommand.NotifyCanExecuteChanged();
            }
            Status = Loc.T("Готово");
        }
        catch (OperationCanceledException) { Status = Loc.T("Остановлено"); }
        catch (Exception ex) { Status = null; Main.ShowMessage(ex.Message, error: true); }
        finally
        {
            IsRunning = false;
            Progress = 0;
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RemoveRun(RunSummary run)
    {
        if (!IsRunning) Completed.Remove(run);
        FinishCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task FinishAsync()
    {
        var wf = Main.Services.Workflow;
        var s = Main.Session!;
        var runs = Completed.Select(r => r.Run).ToList();
        try
        {
            switch (Phase)
            {
                case MeasurePhase.Before:
                    s.MeasureMode = SelectedMode.Mode;
                    s.GameProcess = SelectedMode.Mode == MeasureMode.Synthetic ? null : runs[0].Conditions.ProcessName;
                    wf.RecordBefore(s, runs);
                    if (s.AutoMode) await Main.ApplyRecommendedAsync();
                    else
                    {
                        Main.Strategy.Load(s);
                        Main.Navigate(Main.Strategy);
                    }
                    break;
                case MeasurePhase.After:
                    wf.RecordAfter(s, runs);
                    Main.ShowCompare(CompareViewModel.ForSession(Main, s, wf.Compare(s)));
                    break;
                case MeasurePhase.AbA:
                    s.MeasureMode = SelectedMode.Mode;
                    wf.RecordAbA(s, runs);
                    var report = await Task.Run(() => wf.ApplyAbB(s, Main.Services.ResumeExePath));
                    Main.Navigate(new ApplyViewModel(Main, report, abMode: true));
                    break;
                case MeasurePhase.AbB:
                    var (ab, result, _) = wf.DecideAb(s, runs);
                    Main.ShowCompare(CompareViewModel.ForAb(Main, s, ab, result));
                    break;
            }
            Main.Session = s;
        }
        catch (Exception ex) { Main.ShowMessage(ex.Message, error: true); }
    }

    [RelayCommand]
    private void Skip()
    {
        Main.Strategy.Load(Main.Session!);
        Main.Navigate(Main.Strategy);
    }
}
