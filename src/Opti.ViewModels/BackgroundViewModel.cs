using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Localization;
using Opti.Workflow;

namespace Opti.ViewModels;

public sealed class BackgroundRow
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string CpuText { get; init; }
    public required string MemoryText { get; init; }
    public required string Detail { get; init; }
    public required string Tone { get; init; }
    public required bool CanClose { get; init; }
    public required string ProcessName { get; init; }
}

/// <summary>Страница «Фон»: что грузит ПК, пока вы играете. Закрыть программу можно по кнопке; ничего не отключается навсегда.</summary>
public sealed partial class BackgroundViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    public BackgroundViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Фон");

    public ObservableCollection<BackgroundRow> Rows { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand))] private bool _isBusy;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string _summaryTone = Palette.Neutral;

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

    [RelayCommand] private void Cancel() => _cts?.Cancel();
}
