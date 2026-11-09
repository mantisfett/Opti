using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Tweaks;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed partial class TweakItem : ObservableObject
{
    private readonly TweaksViewModel _owner;
    private bool _suppress;

    public TweakItem(TweaksViewModel owner, Tweak t, bool isOn, bool available)
    {
        _owner = owner;
        Tweak = t;
        _isOn = isOn;
        IsAvailable = available;
    }

    public Tweak Tweak { get; }
    public string Title => Loc.T(Tweak.Title);
    public string Description => Loc.T(Tweak.Description);
    public bool IsAvailable { get; }
    public bool Recommended => Tweak.Recommended;
    public string ApplyText => IsAvailable ? Loc.T("Вступает в силу: ") + Loc.T(TweakCatalog.ApplyModeText(Tweak.ApplyMode)) : Loc.T("Нужна более новая Windows 11");
    public string? Link => Tweak.Link;

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string? _error;

    partial void OnIsOnChanged(bool value)
    {
        if (_suppress) return;
        Error = _owner.Toggle(this, value);
        if (Error is not null)
        {
            _suppress = true;
            IsOn = !value; // не получилось — возвращаем переключатель
            _suppress = false;
        }
    }

    internal void SetSilently(bool value)
    {
        _suppress = true;
        IsOn = value;
        _suppress = false;
    }
}

public sealed class TweakGroup
{
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required IReadOnlyList<TweakItem> Items { get; init; }
}

/// <summary>
/// Твики удобства: заметные изменения поведения Windows, которые человек включает сам.
/// Не влияют на замеры FPS и не входят в «Исправить автоматически».
/// </summary>
public sealed partial class TweaksViewModel : PageViewModel
{
    public TweaksViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Твики");

    public ObservableCollection<TweakGroup> Groups { get; } = new();
    [ObservableProperty] private bool _needsExplorerRestart;
    [ObservableProperty] private string? _pendingText;
    [ObservableProperty] private string? _summary;

    public override void OnShown() => Load();

    public void Load()
    {
        Groups.Clear();
        var svc = Main.Services.Tweaks;
        foreach (var g in TweakCatalog.All.GroupBy(t => t.Category))
            Groups.Add(new TweakGroup
            {
                Title = Loc.T(TweakCatalog.CategoryTitle(g.Key)), Icon = TweakCatalog.CategoryIcon(g.Key),
                Items = g.Select(t => new TweakItem(this, t, svc.IsOn(t), svc.IsAvailable(t, Main.Services.OsBuild))).ToList(),
            });
        UpdateSummary();
    }

    private IEnumerable<TweakItem> AllItems => Groups.SelectMany(g => g.Items);

    private void UpdateSummary()
    {
        int on = AllItems.Count(i => i.IsOn), total = AllItems.Count(i => i.IsAvailable);
        Summary = Loc.T($"Включено {on} из {total}");
    }

    /// <summary>Возвращает текст ошибки или null.</summary>
    internal string? Toggle(TweakItem item, bool on)
    {
        try
        {
            if (on) Main.Services.Tweaks.Enable(item.Tweak);
            else Main.Services.Tweaks.Disable(item.Tweak);
        }
        catch (Exception ex) { return Loc.T("Не удалось: ") + ex.Message; }
        Note(item.Tweak.ApplyMode);
        UpdateSummary();
        return null;
    }

    private void Note(TweakApplyMode mode)
    {
        switch (mode)
        {
            case TweakApplyMode.RestartExplorer:
                NeedsExplorerRestart = true;
                PendingText ??= Loc.T("Часть изменений вступит в силу после перезапуска Проводника (окна папок закроются, программы — нет).");
                break;
            case TweakApplyMode.SignOut:
                PendingText = Loc.T("Часть изменений вступит в силу после выхода из системы и повторного входа.");
                break;
            case TweakApplyMode.Reboot:
                PendingText = Loc.T("Часть изменений вступит в силу после перезагрузки.");
                break;
        }
    }

    [RelayCommand]
    private void EnableRecommended()
    {
        var errors = new List<string>();
        foreach (var i in AllItems.Where(i => i.Recommended && i.IsAvailable && !i.IsOn))
        {
            i.IsOn = true;
            if (i.Error is not null) errors.Add($"{i.Title}: {i.Error}");
        }
        Main.ShowMessage(errors.Count == 0 ? Loc.T("Рекомендуемые твики включены.") : string.Join(" ", errors), errors.Count > 0);
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        Main.Services.RestartExplorer();
        NeedsExplorerRestart = false;
        PendingText = null;
    }

    [RelayCommand] private void OpenLink(string url) => Main.Services.OpenPath(url);
}
