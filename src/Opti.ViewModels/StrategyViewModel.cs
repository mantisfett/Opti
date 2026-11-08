using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed partial class StrategyViewModel : PageViewModel
{
    public StrategyViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Стратегия");

    public ObservableCollection<FixItem> Fixes { get; } = new();
    public ObservableCollection<FixItem> AbFixes { get; } = new();
    public ObservableCollection<string> Excluded { get; } = new();
    public ObservableCollection<FindingItem> Advice { get; } = new();
    public ObservableCollection<ChoiceItem> StartupChoices { get; } = new();
    [ObservableProperty] private string? _header;
    [ObservableProperty] private bool _showStartupChoices;
    [ObservableProperty] private bool _needsBitLockerAck;
    [ObservableProperty] private bool _bitLockerAcknowledged;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private bool _isApplying;

    public StrategyPlan? Plan { get; private set; }

    /// <summary>Режим можно сменить прямо здесь: план пересобирается, отметки — по политике нового режима.</summary>
    public IReadOnlyList<GoalOption> Goals { get; } = GoalOption.All;
    [ObservableProperty] private GoalOption? _selectedGoal;
    [ObservableProperty] private string? _goalSummary;
    private bool _loading;

    partial void OnSelectedGoalChanged(GoalOption? value)
    {
        if (_loading || value is null || Main.Session is not { } s || s.Goal == value.Goal) return;
        s.Goal = value.Goal;
        Main.Services.Workflow.Store.Save(s);
        Load(s);
    }

    public void Load(TuneSession s)
    {
        _loading = true;
        try
        {
            SelectedGoal = Goals.First(g => g.Goal == s.Goal);
            GoalSummary = SelectedGoal.Summary;
        }
        finally { _loading = false; }
        Plan = Main.Services.Workflow.BuildPlan(s);
        Header = Plan.Device.Describe();
        Fixes.Clear(); AbFixes.Clear(); Excluded.Clear(); Advice.Clear(); StartupChoices.Clear();
        foreach (var p in Plan.Fixes)
        {
            var item = new FixItem(p);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FixItem.IsSelected)) UpdateExtras(); };
            (p.ApplySeparately ? AbFixes : Fixes).Add(item);
        }
        foreach (var e in Plan.Excluded) Excluded.Add($"{e.FixId}: {e.Reason}");
        foreach (var a in Plan.AdviceOnly) Advice.Add(new FindingItem(a));
        foreach (var st in s.Profile!.Background.StartupItems.Where(i => i.Enabled && i.ApprovedKey is not null))
            StartupChoices.Add(new ChoiceItem { Name = st.Name, Detail = st.Command });
        UpdateExtras();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void UpdateExtras()
    {
        ShowStartupChoices = Fixes.Any(f => f.Id == FixIds.StartupDisable && f.IsSelected);
        NeedsBitLockerAck = Main.Session?.Profile?.Security.BitLockerOnSystemDrive == true
                            && Fixes.Any(f => f.Fix.TouchesBcd && f.IsSelected);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyList<IFix> SelectedFixes() => Fixes.Where(f => f.IsSelected).Select(f => f.Fix).ToList();

    private bool CanApply() => !IsApplying && Fixes.Any(f => f.IsSelected) && (!NeedsBitLockerAck || BitLockerAcknowledged);
    partial void OnBitLockerAcknowledgedChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var s = Main.Session!;
        var fixes = SelectedFixes();
        bool reboot = fixes.Any(f => f.RequiresReboot);
        var msg = Loc.T($"Будут применены исправления ({fixes.Count}):\n• ") + string.Join("\n• ", fixes.Select(f => f.Title)) +
                  Loc.T("\n\nПеред изменениями создаётся точка восстановления, каждое изменение записывается в журнал и может быть откачено.") +
                  (reboot ? Loc.T("\n\nПотребуется перезагрузка — Opti продолжит после неё сам.") : "");
        if (!await Main.Services.ConfirmAsync(Loc.T("Применить исправления?"), msg)) return;

        if (ShowStartupChoices)
        {
            var chosen = StartupChoices.Where(c => c.IsChecked).Select(c => c.Name).ToList();
            if (chosen.Count == 0) fixes = fixes.Where(f => f.Id != FixIds.StartupDisable).ToList();
            else s.FixOptions[FixIds.StartupDisable] = new() { ["items"] = string.Join('\n', chosen) };
        }
        if (NeedsBitLockerAck && BitLockerAcknowledged) s.FixOptions[FixIds.BcdTimersReset] = new() { ["bitlockerAcknowledged"] = "1" };

        IsApplying = true;
        try
        {
            var report = await Task.Run(() => Main.Services.Workflow.Apply(s, fixes, Main.Services.ResumeExePath));
            Main.Session = s;
            Main.Navigate(new ApplyViewModel(Main, report));
        }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Не удалось применить: ") + ex.Message, error: true); }
        finally { IsApplying = false; }
    }

    [RelayCommand]
    private async Task StartAbAsync(FixItem item)
    {
        var s = Main.Session!;
        if (s.AppliedFixIds.Count == 0 && Fixes.Any(f => f.IsSelected) &&
            !await Main.Services.ConfirmAsync(Loc.T("A/B-проверка"), Loc.T("Рекомендуется сначала применить основные исправления, а спорные настройки проверять потом — по одной. Начать A/B сейчас?")))
            return;
        try
        {
            Main.Services.Workflow.StartAb(s, item.Id);
            Main.Navigate(MeasureViewModel.ForAbA(Main));
        }
        catch (Exception ex) { Main.ShowMessage(ex.Message, error: true); }
    }

    [RelayCommand] private void OpenLink(string url) => Main.Services.OpenPath(url);
}
