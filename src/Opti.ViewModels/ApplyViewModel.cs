using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Fixes.Services;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed partial class ApplyViewModel : PageViewModel
{
    private readonly bool _abMode;

    public ApplyViewModel(MainViewModel main, ApplyReport? report, bool abMode = false) : base(main)
    {
        _abMode = abMode;
        if (report is null) return;
        RestorePoint = report.RestorePoint is not null ? Loc.T($"Точка восстановления создана: {report.RestorePoint}") : report.RestorePointWarning;
        RebootRequired = report.RebootRequired;
        foreach (var r in report.Results)
        {
            var title = Main.Services.Workflow.Catalog.FirstOrDefault(f => f.Id == r.FixId)?.Title ?? r.FixId;
            Log.Add(new LogItem
            {
                Title = title,
                Status = r.Outcome switch { FixOutcome.Applied => Loc.T("применено"), FixOutcome.Failed => Loc.T("ошибка: ") + r.Error, FixOutcome.NothingToChange => Loc.T("уже в нужном состоянии"), _ => Loc.T("пропущено") },
                Color = r.Outcome switch { FixOutcome.Applied => Palette.Good, FixOutcome.Failed => Palette.Bad, _ => Palette.Neutral },
                Details = r.Changes.Select(c => $"{c.Type} {c.Target}: {c.Before} → {c.After}").ToList(),
            });
        }
        Headline = report.Results.Any(r => r.Outcome == FixOutcome.Failed)
            ? Loc.T("Часть исправлений не применилась — они откачены, остальное действует.")
            : Loc.T("Исправления применены.");
        NextText = RebootRequired ? Loc.T("Нужна перезагрузка. После входа в систему Opti откроется сам и продолжит.")
                                  : _abMode ? Loc.T("Теперь замер варианта B в тех же условиях.") : Loc.T("Теперь замер «после» в тех же условиях.");
    }

    public override string Title => _abMode ? Loc.T("A/B: вариант B применён") : Loc.T("Применение");
    public ObservableCollection<LogItem> Log { get; } = new();
    public ObservableCollection<LogItem> PostRebootItems { get; } = new();
    [ObservableProperty] private string? _headline;
    [ObservableProperty] private string? _restorePoint;
    [ObservableProperty] private bool _rebootRequired;
    [ObservableProperty] private string? _nextText;
    [ObservableProperty] private bool _suggestRollback;
    public PostRebootReport? PostReboot { get; set; }

    public void FillPostReboot(PostRebootReport check)
    {
        Headline = Loc.T("Перезагрузка выполнена. Самопроверка:");
        RebootRequired = false;
        PostRebootItems.Add(new LogItem { Title = Loc.T($"Подтверждено изменений: {check.Verified}"), Status = check.Healthy ? Loc.T("всё в порядке") : Loc.T("есть замечания"), Color = check.Healthy ? Palette.Good : Palette.Warn });
        foreach (var n in check.NotApplied) PostRebootItems.Add(new LogItem { Title = Loc.T("Не применилось"), Status = n, Color = Palette.Warn });
        foreach (var c in check.Crashes) PostRebootItems.Add(new LogItem { Title = Loc.T($"Сбой системы {c.Time.LocalDateTime:dd.MM HH:mm}"), Status = Loc.T($"событие {c.EventId} ({c.Source})"), Color = Palette.Bad });
        SuggestRollback = check.SuggestRollback;
        NextText = check.SuggestRollback ? Loc.T("После изменений были сбои системы. Рекомендуется откатить.") : Loc.T("Теперь замер «после» в тех же условиях, что и «до».");
    }

    [RelayCommand] private void RebootNow() => Main.Services.RestartComputer();

    [RelayCommand]
    private void Continue()
    {
        var s = Main.Session!;
        if (_abMode) { Main.Navigate(MeasureViewModel.ForAbB(Main)); return; }
        if (s.Before.Count == 0)
        {
            Main.Services.Workflow.Close(s);
            Main.ShowMessage(Loc.T("Замера «до» не было — сравнивать не с чем. Отчёт можно открыть в «Истории»."));
            Main.History.Refresh();
            Main.Navigate(Main.History);
            return;
        }
        Main.Navigate(MeasureViewModel.ForAfter(Main, auto: s.AutoMode));
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        var s = Main.Session!;
        if (!await Main.Services.ConfirmAsync(Loc.T("Откат"), Loc.T("Вернуть все изменения этой сессии?"))) return;
        var r = await Task.Run(() => Main.Services.Workflow.RollbackSession(s));
        Main.Services.Workflow.Close(s);
        Main.ShowMessage(r.Summary() + Loc.T(" Для части настроек нужна перезагрузка."), r.Errors.Count > 0);
        Main.History.Refresh();
        Main.Navigate(Main.History);
    }
}
