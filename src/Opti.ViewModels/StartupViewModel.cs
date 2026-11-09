using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Localization;
using Opti.Workflow;

namespace Opti.ViewModels;

public sealed partial class StartupRow : ObservableObject
{
    private readonly StartupViewModel _owner;
    private bool _suppress;

    public StartupRow(StartupViewModel owner, StartupEntry entry)
    {
        _owner = owner;
        Entry = entry;
        _isOn = entry.Enabled;
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Source => Entry.Source;
    public string Category => BackgroundScanner.CategoryText(Entry.Category);
    public bool CanToggle => Entry.CanToggle;
    public bool IsSuggested => Entry.Advice == StartupAdvice.Suggest;
    public string AdviceText => Entry.Advice switch
    {
        StartupAdvice.Suggest => Loc.T("можно отключить"),
        StartupAdvice.Keep => Loc.T("лучше оставить"),
        _ => "",
    };
    public string AdviceTone => Entry.Advice switch { StartupAdvice.Suggest => Palette.Good, _ => Palette.Neutral };
    /// <summary>Совет «можно отключить» не нужен, когда пункт уже отключён.</summary>
    public bool ShowAdvice => Entry.Advice == StartupAdvice.Keep || (Entry.Advice == StartupAdvice.Suggest && IsOn);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowAdvice))] private bool _isOn;
    [ObservableProperty] private string? _error;

    partial void OnIsOnChanged(bool value)
    {
        if (_suppress) return;
        Error = _owner.Toggle(this, value);
        if (Error is not null) SetSilently(!value); // не получилось — возвращаем переключатель
    }

    internal void SetSilently(bool value) { _suppress = true; IsOn = value; _suppress = false; }
}

/// <summary>Страница «Автозагрузка»: что запускается вместе с Windows. Отключение обратимо (как в «Диспетчере задач») и записывается в журнал.</summary>
public sealed partial class StartupViewModel : PageViewModel
{
    public StartupViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Автозагрузка");

    public ObservableCollection<StartupRow> Rows { get; } = new();
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private bool _hasSuggested;

    public override void OnShown() => Load();

    public void Load()
    {
        Rows.Clear();
        var mgr = Main.Services.Startup;
        IsAvailable = mgr is not null;
        if (mgr is null) { Summary = Loc.T("В этом режиме автозагрузка недоступна."); HasSuggested = false; return; }
        try { foreach (var e in mgr.Scan()) Rows.Add(new StartupRow(this, e)); }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Не удалось прочитать автозагрузку: ") + ex.Message, error: true); }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int on = Rows.Count(r => r.IsOn);
        int suggested = Rows.Count(r => r.IsOn && r.IsSuggested);
        HasSuggested = suggested > 0;
        Summary = Rows.Count == 0 ? Loc.T("Автозагрузка пуста.")
            : suggested > 0 ? Loc.T($"При входе запускается {on} из {Rows.Count}. Без {suggested} из них можно обойтись — они обычно не нужны сразу после включения ПК.")
            : Loc.T($"При входе запускается {on} из {Rows.Count}. Лишнего не видно.");
    }

    /// <summary>Возвращает текст ошибки или null.</summary>
    internal string? Toggle(StartupRow row, bool on)
    {
        try { Main.Services.Startup!.SetEnabled(row.Entry, on); }
        catch (Exception ex) { return Loc.T("Не удалось: ") + ex.Message; }
        UpdateSummary();
        Main.History.Refresh();
        return null;
    }

    [RelayCommand]
    private async Task DisableSuggestedAsync()
    {
        var targets = Rows.Where(r => r.IsOn && r.IsSuggested && r.CanToggle).ToList();
        if (targets.Count == 0) return;
        if (!await Main.Services.ConfirmAsync(Loc.T("Отключить рекомендуемые?"),
                Loc.T($"Будет отключено пунктов: {targets.Count} ({string.Join(", ", targets.Select(t => t.Name))}). Программы не удаляются и запускаются вручную как обычно; включить обратно можно здесь или откатить в «Истории».")))
            return;
        var errors = new List<string>();
        foreach (var r in targets)
        {
            r.IsOn = false;
            if (r.Error is not null) errors.Add($"{r.Name}: {r.Error}");
        }
        Main.ShowMessage(errors.Count == 0 ? Loc.T("Лишнее убрано из автозагрузки. Заметно со следующего входа в Windows.") : string.Join(" ", errors), errors.Count > 0);
    }

    [RelayCommand] private void Refresh() => Load();
}
