using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Opti.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(MainViewModel main) : base(main) => Reload();
    public override string Title => "Настройки";

    public IReadOnlyList<GoalOption> Goals => GoalOption.All;
    public ObservableCollection<ChoiceItem> ExcludedFixes { get; } = new();
    [ObservableProperty] private GoalOption? _defaultGoal;
    [ObservableProperty] private int _measureRuns;
    [ObservableProperty] private int _measureDuration;
    [ObservableProperty] private int _measureWarmup;
    [ObservableProperty] private int _syntheticDuration;
    [ObservableProperty] private string? _presentMonPath;
    [ObservableProperty] private string? _obsConfigDir;
    [ObservableProperty] private string? _networkTarget;
    [ObservableProperty] private bool _rollbackWhenNoEffect;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _verifyOnStartup;
    [ObservableProperty] private string? _updateStatus;
    public string Version => Main.Services.AppVersion;
    public string DataDir => Main.Services.DataDir;

    private void Reload()
    {
        var s = Main.Services.Settings;
        DefaultGoal = GoalOption.All.First(g => g.Goal == s.DefaultGoal);
        MeasureRuns = s.MeasureRuns;
        MeasureDuration = s.MeasureDurationSec;
        MeasureWarmup = s.MeasureWarmupSec;
        SyntheticDuration = s.SyntheticDurationSec;
        PresentMonPath = s.PresentMonPath;
        ObsConfigDir = s.ObsConfigDir;
        NetworkTarget = s.NetworkTarget;
        RollbackWhenNoEffect = s.RollbackWhenNoEffect;
        CheckForUpdates = s.CheckForUpdates;
        VerifyOnStartup = s.VerifyOnStartup;
        ExcludedFixes.Clear();
        foreach (var f in Main.Services.Workflow.Catalog)
            ExcludedFixes.Add(new ChoiceItem { Name = f.Id, Detail = f.Title, IsChecked = s.ExcludedFixIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase) });
    }

    [RelayCommand]
    private void Save()
    {
        var s = Main.Services.Settings;
        s.DefaultGoal = DefaultGoal?.Goal ?? s.DefaultGoal;
        s.MeasureRuns = Math.Clamp(MeasureRuns, 1, 10);
        s.MeasureDurationSec = Math.Clamp(MeasureDuration, 10, 600);
        s.MeasureWarmupSec = Math.Clamp(MeasureWarmup, 0, 120);
        s.SyntheticDurationSec = Math.Clamp(SyntheticDuration, 10, 300);
        s.PresentMonPath = string.IsNullOrWhiteSpace(PresentMonPath) ? null : PresentMonPath.Trim('"', ' ');
        s.ObsConfigDir = string.IsNullOrWhiteSpace(ObsConfigDir) ? null : ObsConfigDir.Trim('"', ' ');
        s.NetworkTarget = string.IsNullOrWhiteSpace(NetworkTarget) ? "1.1.1.1" : NetworkTarget.Trim();
        s.RollbackWhenNoEffect = RollbackWhenNoEffect;
        s.CheckForUpdates = CheckForUpdates;
        s.VerifyOnStartup = VerifyOnStartup;
        s.ExcludedFixIds = ExcludedFixes.Where(x => x.IsChecked).Select(x => x.Name).ToList();
        try
        {
            s.Save(Path.Combine(Main.Services.DataDir, "settings.json"));
            Main.ShowMessage("Настройки сохранены.");
        }
        catch (Exception ex) { Main.ShowMessage("Не удалось сохранить: " + ex.Message, error: true); }
        Reload();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Проверяю…";
        var u = await Main.Services.CheckForUpdatesAsync();
        UpdateStatus = u is null ? "Не удалось проверить (нет сети или репозиторий недоступен)."
                     : u.IsNewer ? $"Доступна версия {u.LatestVersion}." : "Установлена последняя версия.";
        if (u is { IsNewer: true })
        {
            Main.UpdateMessage = $"Доступна новая версия {u.LatestVersion}";
            Main.UpdateUrl = u.Url;
        }
    }

    [RelayCommand] private void OpenDataDir() => Main.Services.OpenPath(DataDir);

    /// <summary>Архив для обращения в поддержку: сохраняется на рабочий стол и показывается в Проводнике.</summary>
    [RelayCommand]
    private void CreateProblemReport()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Opti.Workflow.ProblemReport.Create(DataDir, Main.Services.Journal,
                string.IsNullOrEmpty(desktop) ? Main.Services.ReportsDir : desktop, Main.Services.AppVersion);
            Main.ShowMessage("Отчёт о проблеме сохранён: " + path + ". Он нигде не публикуется — приложите его к обращению сами.");
            Main.Services.RevealFile(path);
        }
        catch (Exception ex) { Main.ShowMessage("Не удалось собрать отчёт: " + ex.Message, error: true); }
    }
    [RelayCommand] private void OpenReleases() => Main.Services.OpenPath(Opti.Workflow.AppInfo.ReleasesUrl);
}
