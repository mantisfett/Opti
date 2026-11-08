using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Workflow.Cleanup;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed partial class CleanupItem : ObservableObject
{
    public CleanupItem(ICleanupTarget t)
    {
        Target = t;
        _isChecked = t.DefaultSelected;
    }
    public ICleanupTarget Target { get; }
    public string Title => Target.Title;
    public string Description => Target.Description;
    public string? Caution => Target.Caution;
    public bool IsManual => Target.ManualUri is not null;
    public bool IsAutomatic => !IsManual;
    public string ManualActionText => Target.ManualActionText;
    public ObservableCollection<CleanupDetail> Details { get; } = new();
    /// <summary>Рекомендация показывается, только когда размер превысил порог (например, «Загрузки» больше 5 ГБ).</summary>
    public string? Warning => Target.WarnAboveBytes is { } t && Bytes > t ? Target.WarnText : null;
    partial void OnBytesChanged(long value) => OnPropertyChanged(nameof(Warning));

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private bool _measured;
    [ObservableProperty] private string _sizeText = "…";
}

/// <summary>Бережная очистка: показываем размер каждой категории, удаляем только отмеченное.</summary>
public sealed partial class CleanupViewModel : PageViewModel
{
    private bool _estimated;
    public CleanupViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Очистка");

    public ObservableCollection<CleanupItem> Items { get; } = new();
    /// <summary>Очищается автоматически (с галочками).</summary>
    public ObservableCollection<CleanupItem> AutoItems { get; } = new();
    /// <summary>Решает человек: «Загрузки», тяжёлые видео, точки восстановления, Windows.old.</summary>
    public ObservableCollection<CleanupItem> ManualItems { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CleanCommand))] private bool _isBusy;
    [ObservableProperty] private string? _totalText;
    [ObservableProperty] private string? _resultText;

    public override async void OnShown()
    {
        if (_estimated) return;
        _estimated = true;
        await EstimateAsync();
    }

    [RelayCommand]
    public async Task EstimateAsync()
    {
        if (Items.Count == 0)
            foreach (var t in Main.Services.CleanupTargets())
            {
                var item = new CleanupItem(t);
                item.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanupItem.IsChecked) or nameof(CleanupItem.Bytes)) UpdateTotal(); };
                Items.Add(item);
                (item.IsManual ? ManualItems : AutoItems).Add(item);
            }
        IsBusy = true;
        try
        {
            await Task.WhenAll(Items.Select(async i =>
            {
                i.SizeText = "…";
                try
                {
                    var e = await i.Target.EstimateAsync();
                    i.Bytes = e.Bytes;
                    i.SizeText = e.Bytes == 0 ? (i.Target is LargeFilesTarget ? Loc.T("не найдено") : Loc.T("пусто")) : SizeFormat.Bytes(e.Bytes);
                    i.Details.Clear();
                    foreach (var d in i.Target.Details) i.Details.Add(d);
                }
                catch { i.Bytes = 0; i.SizeText = Loc.T("нет доступа"); }
                i.Measured = true;
            }));
        }
        finally { IsBusy = false; }
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        long sel = Items.Where(i => i.IsChecked && i.IsAutomatic).Sum(i => i.Bytes);
        TotalText = sel > 0 ? Loc.T($"Будет освобождено около {SizeFormat.Bytes(sel)}") : Loc.T("Выберите, что очистить");
        CleanCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !IsBusy && Items.Any(i => i.IsChecked && i.IsAutomatic && i.Bytes > 0);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var chosen = Items.Where(i => i.IsChecked && i.IsAutomatic && i.Bytes > 0).ToList();
        var cautions = chosen.Where(c => c.Caution is not null).Select(c => $"• {c.Title}: {c.Caution}").ToList();
        var msg = Loc.T("Очистить:\n") + string.Join("\n", chosen.Select(c => $"• {c.Title} — {c.SizeText}")) +
                  (cautions.Count > 0 ? Loc.T("\n\nОбратите внимание:\n") + string.Join("\n", cautions) : "") +
                  Loc.T("\n\nУдаление необратимо. Документы, игры и настройки не затрагиваются.");
        if (!await Main.Services.ConfirmAsync(Loc.T("Очистка"), msg)) return;

        IsBusy = true;
        long freed = 0; int skipped = 0; var errors = new List<string>();
        try
        {
            foreach (var c in chosen)
            {
                c.SizeText = Loc.T("очищаю…");
                try
                {
                    var r = await c.Target.CleanAsync();
                    freed += r.FreedBytes; skipped += r.Skipped; errors.AddRange(r.Errors);
                }
                catch (Exception ex) { errors.Add($"{c.Title}: {ex.Message}"); }
            }
        }
        finally { IsBusy = false; }
        ResultText = Loc.T($"Освобождено {SizeFormat.Bytes(freed)}.") + (skipped > 0 ? Loc.T($" Пропущено занятых файлов: {skipped}.") : "") +
                     (errors.Count > 0 ? " " + string.Join(" ", errors) : "");
        Main.ShowMessage(ResultText, errors.Count > 0);
        await EstimateAsync();
    }

    [RelayCommand]
    private void OpenManual(CleanupItem item)
    {
        if (item.Target.ManualUri is { } uri && uri.Length > 0) Main.Services.OpenPath(uri);
    }

    [RelayCommand] private void RevealFile(CleanupDetail d) => Main.Services.RevealFile(d.Path);

    /// <summary>Выбрать всё (кроме ручных пунктов) или вернуть выбор по умолчанию.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        bool all = Items.Where(i => i.IsAutomatic).All(i => i.IsChecked);
        foreach (var i in Items.Where(i => i.IsAutomatic)) i.IsChecked = all ? i.Target.DefaultSelected : true;
    }
}
