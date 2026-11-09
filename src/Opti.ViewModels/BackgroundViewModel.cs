using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Localization;
using Opti.Workflow;

namespace Opti.ViewModels;

public sealed partial class BackgroundRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string CpuText { get; init; }
    public required string MemoryText { get; init; }
    public required string Detail { get; init; }
    public required string Tone { get; init; }
    public required bool CanClose { get; init; }
    public required string ProcessName { get; init; }
    /// <summary>Отмечено для игровой сессии.</summary>
    [ObservableProperty] private bool _isMarked;
    /// <summary>Подсказка, почему программа не отмечена по умолчанию.</summary>
    public string? MarkHint { get; init; }
}

/// <summary>Страница «Фон»: что грузит ПК, пока вы играете. Закрыть программу можно по кнопке; ничего не отключается навсегда.</summary>
public sealed partial class BackgroundViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    public BackgroundViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Фон");
    public override void OnShown() => IsSessionActive = Main.Services.Game?.IsActive ?? false;

    public ObservableCollection<BackgroundRow> Rows { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand))] private bool _isBusy;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string _summaryTone = Palette.Neutral;
    [ObservableProperty] private bool _usePowerPlan = true;
    [ObservableProperty] private bool _isSessionActive;
    public bool GameAvailable => Main.Services.Game is not null;

    /// <summary>Что закрывать перед игрой по умолчанию: то, что не нужно во время игры. Мессенджеры, магазины и запись часто нужны (голос, античит, клипы) — не отмечаем.</summary>
    public static bool MarkedByDefault(BackgroundCategory c) => c is BackgroundCategory.Browser or BackgroundCategory.Cloud or BackgroundCategory.Updater or BackgroundCategory.Media;

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsBusy = true; Summary = null;
        _cts = new CancellationTokenSource();
        try { Show(await Main.Services.ScanBackgroundAsync(_cts.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; }
    }

    public void Show(IReadOnlyList<BackgroundApp> apps)
    {
        Rows.Clear();
        foreach (var a in apps)
            Rows.Add(new BackgroundRow
            {
                Name = a.Name, ProcessName = a.Name, Category = BackgroundScanner.CategoryText(a.Category),
                CpuText = a.CpuPercent >= 0.1 ? $"{a.CpuPercent:0.#} %" : "<0.1 %",
                MemoryText = a.MemoryMb >= 1024 ? $"{a.MemoryMb / 1024.0:0.0} {Loc.T("ГБ")}" : $"{a.MemoryMb} {Loc.T("МБ")}",
                Detail = a.Processes > 1 ? Loc.T($"процессов: {a.Processes}") : "",
                Tone = a.CpuPercent >= 5 ? Palette.Warn : Palette.Neutral, CanClose = a.CanClose,
                IsMarked = a.CanClose && MarkedByDefault(a.Category),
                MarkHint = a.Category is BackgroundCategory.Chat or BackgroundCategory.Launcher or BackgroundCategory.Recorder ? Loc.T("часто нужна во время игры (голос, запуск, клипы) — не отмечена") : null,
            });
        var cpu = apps.Sum(a => a.CpuPercent);
        var ram = apps.Sum(a => a.MemoryMb);
        if (apps.Count == 0) { Summary = Loc.T("Фон спокоен: заметных нагрузок нет."); SummaryTone = Palette.Good; return; }
        var ramText = ram >= 1024 ? $"{ram / 1024.0:0.0} {Loc.T("ГБ")}" : $"{ram} {Loc.T("МБ")}";
        Summary = Loc.T($"Эти программы сейчас занимают около {cpu:0.#} % процессора и {ramText} памяти. Закройте ненужные на время игры — при следующем запуске они откроются как обычно.");
        SummaryTone = cpu >= 10 ? Palette.Warn : Palette.Neutral;
    }

    [RelayCommand]
    private async Task CloseAppAsync(BackgroundRow row)
    {
        if (!row.CanClose) return;
        if (!await Main.Services.ConfirmAsync(Loc.T($"Закрыть {row.Name}?"),
                Loc.T("Программа закроется сейчас. Несохранённые данные в ней могут пропасть. Навсегда ничего не отключается.")))
            return;
        var n = Main.Services.CloseBackgroundApp(row.ProcessName);
        Main.ShowMessage(n > 0 ? Loc.T($"Закрыто: {row.Name}.") : Loc.T($"Не удалось закрыть {row.Name} — возможно, она уже закрыта или защищена."), n == 0);
        if (n > 0) Rows.Remove(row);
    }

    [RelayCommand]
    private async Task PrepareGameAsync()
    {
        var game = Main.Services.Game;
        if (game is null) return;
        var marked = Rows.Where(r => r.IsMarked && r.CanClose).ToList();
        var list = marked.Count == 0 ? Loc.T("ничего") : string.Join(", ", marked.Select(r => r.Name));
        var power = UsePowerPlan ? Loc.T(" Схема питания на время игры станет «Высокая производительность» (на ноутбуке не меняется).") : "";
        if (!await Main.Services.ConfirmAsync(Loc.T("Подготовить к игре?"),
                Loc.T($"Будет закрыто: {list}. Несохранённые данные в этих программах могут пропасть.") + power))
            return;
        var result = game.Start(marked.Select(r => r.ProcessName), UsePowerPlan);
        foreach (var name in result.Closed) if (Rows.FirstOrDefault(r => r.ProcessName == name) is { } row) Rows.Remove(row);
        IsSessionActive = game.IsActive;
        var parts = new List<string>();
        if (result.Closed.Count > 0) parts.Add(Loc.T($"Закрыто: {result.Closed.Count}."));
        if (result.NotClosed.Count > 0) parts.Add(Loc.T($"Не закрылись: {string.Join(", ", result.NotClosed)}."));
        if (result.PowerChanged) parts.Add(Loc.T("Схема питания — «Высокая производительность»."));
        if (result.PowerNote is not null) parts.Add(result.PowerNote);
        if (parts.Count == 0) parts.Add(Loc.T("Всё уже готово."));
        Main.ShowMessage(string.Join(" ", parts), error: result.NotClosed.Count > 0 && result.Closed.Count == 0);
        Main.History.Refresh();
    }

    [RelayCommand]
    private void EndGame()
    {
        var game = Main.Services.Game;
        if (game is null) return;
        var report = game.End();
        IsSessionActive = game.IsActive;
        Main.ShowMessage(report.Errors.Count == 0 ? Loc.T("Игровая сессия завершена: схема питания возвращена.") : string.Join("; ", report.Errors), report.Errors.Count > 0);
        Main.History.Refresh();
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();
}
