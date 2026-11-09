using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Core.Localization;
using Opti.Core.Profile;
using Opti.Measure.Capture;

namespace Opti.ViewModels;

public sealed class LatencyRow
{
    public required string Driver { get; init; }
    public required string DpcText { get; init; }
    public required string IsrText { get; init; }
    public required string Tone { get; init; }
    /// <summary>Длина полоски 0..1 относительно худшего драйвера.</summary>
    public required double Bar { get; init; }
}

/// <summary>
/// Страница «Задержки»: какие драйверы надолго занимают процессор (DPC/ISR) и насколько точен таймер.
/// Именно эти задержки дают микрофризы, треск звука и «ватную» мышь. Тест только читает события ядра, ничего не меняет.
/// </summary>
public sealed partial class LatencyViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    public LatencyViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Задержки");

    public ObservableCollection<LatencyRow> Rows { get; } = new();
    public ObservableCollection<string> Steps { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] [NotifyPropertyChangedFor(nameof(CanChangeDuration))] private bool _isBusy;
    [ObservableProperty] private bool _longTest;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string _summaryTone = Palette.Neutral;
    [ObservableProperty] private string? _timerText;
    [ObservableProperty] private string? _progressText;

    public int Seconds => LongTest ? 120 : 30;
    public bool CanChangeDuration => !IsBusy;

    /// <summary>Тон строки: больше 1 мс — плохо (порог находки), больше 0,5 мс — внимание.</summary>
    public static string ToneFor(double us) => us > DpcLatencyDetector.ThresholdUs ? Palette.Bad : us > DpcLatencyDetector.ThresholdUs / 2 ? Palette.Warn : Palette.Good;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true; Summary = null; TimerText = null; Rows.Clear(); Steps.Clear();
        var cts = _cts = new CancellationTokenSource();
        int total = Seconds;
        try
        {
            var task = Main.Services.MeasureLatencyAsync(total, cts.Token);
            for (int left = total; !task.IsCompleted; left = Math.Max(0, left - 1))
            {
                ProgressText = Loc.T($"Идёт замер, осталось около {left} с. Не запускайте тяжёлые программы — а лучше поиграйте, чтобы поймать задержки в деле.");
                await Task.WhenAny(task, Task.Delay(1000, CancellationToken.None));
            }
            Show(await task);
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException ex) { Main.ShowMessage(ex.Message, error: true); }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void Show(LatencyReport r)
    {
        Rows.Clear(); Steps.Clear();
        var drivers = r.Dpc.Drivers.OrderByDescending(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs)).Take(10).ToList();
        double worst = Math.Max(1, drivers.Select(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs)).DefaultIfEmpty(1).Max());
        foreach (var d in drivers)
        {
            var max = Math.Max(d.MaxDpcUs, d.MaxIsrUs);
            Rows.Add(new LatencyRow
            {
                Driver = d.Driver, DpcText = $"{d.MaxDpcUs:0} {Loc.T("мкс")}", IsrText = $"{d.MaxIsrUs:0} {Loc.T("мкс")}",
                Tone = ToneFor(max), Bar = max / worst,
            });
        }

        var finding = DetectorCatalog.RunAll(new SystemProfile { Dpc = r.Dpc }, new[] { new DpcLatencyDetector() }).FirstOrDefault();
        if (finding is not null)
        {
            Summary = finding.Title + ". " + finding.Evidence.FirstOrDefault();
            SummaryTone = Palette.Bad;
            foreach (var s in finding.ManualSteps) Steps.Add(s);
        }
        else if (drivers.Count == 0)
        {
            Summary = Loc.T("Событий драйверов не поймано — повторите тест, пока ПК чем-то занят (игра, звук, сеть).");
            SummaryTone = Palette.Neutral;
        }
        else
        {
            var top = drivers[0];
            Summary = Loc.T($"Всё в порядке: худший драйвер — {top.Driver}, {Math.Max(top.MaxDpcUs, top.MaxIsrUs):0} мкс (порог проблем — 1000 мкс).");
            SummaryTone = Palette.Good;
        }

        var t = r.Timer;
        TimerText = Loc.T($"Точность таймера (Sleep 1 мс): среднее {t.MeanMs:0.##} мс, 99 % — до {t.P99Ms:0.##} мс, максимум {t.MaxMs:0.##} мс")
            + (t.CurrentResolutionMs is { } res ? Loc.T($"; текущее разрешение таймера {res:0.###} мс") : "");
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();
}
