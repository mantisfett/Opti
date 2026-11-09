using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed record LanguageOption(string Code, string Title);

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(MainViewModel main) : base(main) => Reload();
    public override string Title => Loc.T("Настройки");

    public IReadOnlyList<GoalOption> Goals { get; } = GoalOption.All;
    public ObservableCollection<ChoiceItem> ExcludedFixes { get; } = new();
    [ObservableProperty] private GoalOption? _defaultGoal;
    /// <summary>Язык интерфейса: название выбирается на своём языке, чтобы его можно было найти при любом текущем.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } = new LanguageOption[]
    {
        new("auto", Loc.T("Автоматически (как в Windows)")),
        new("ru", Loc.T("Русский")),
        new("en", "English"),
    };
    [ObservableProperty] private LanguageOption? _language;
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
    [ObservableProperty] private bool _autoGameSession;
    [ObservableProperty] private string? _gameProcesses;
    [ObservableProperty] private string? _updateStatus;
    public string Version => Main.Services.AppVersion;
    public string DataDir => Main.Services.DataDir;

    private void Reload()
    {
        var s = Main.Services.Settings;
        DefaultGoal = Goals.First(g => g.Goal == s.DefaultGoal);
        Language = Languages.FirstOrDefault(l => l.Code == s.Language) ?? Languages[0];
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
        AutoGameSession = s.AutoGameSession;
        GameProcesses = s.GameProcesses;
        ExcludedFixes.Clear();
        foreach (var f in Main.Services.Workflow.Catalog)
            ExcludedFixes.Add(new ChoiceItem { Name = f.Id, Detail = f.Title, IsChecked = s.ExcludedFixIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase) });
    }

    [RelayCommand]
    private void Save()
    {
        var s = Main.Services.Settings;
        s.DefaultGoal = DefaultGoal?.Goal ?? s.DefaultGoal;
        s.Language = Language?.Code ?? "auto";
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
        s.AutoGameSession = AutoGameSession;
        s.GameProcesses = string.Join(", ", Opti.Workflow.GameWatcher.ParseNames(GameProcesses));
        s.ExcludedFixIds = ExcludedFixes.Where(x => x.IsChecked).Select(x => x.Name).ToList();
        try
        {
            s.Save(Path.Combine(Main.Services.DataDir, "settings.json"));
            Main.ShowMessage(Loc.T("Настройки сохранены."));
        }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Не удалось сохранить: ") + ex.Message, error: true); }
        Reload();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = Loc.T("Проверяю…");
        var u = await Main.Services.CheckForUpdatesAsync();
        UpdateStatus = u is null ? Loc.T("Не удалось проверить (нет сети или репозиторий недоступен).")
                     : u.IsNewer ? Loc.T($"Доступна версия {u.LatestVersion}.") : Loc.T("Установлена последняя версия.");
        if (u is { IsNewer: true })
        {
            Main.UpdateMessage = Loc.T($"Доступна новая версия {u.LatestVersion}");
            Main.UpdateUrl = u.Url;
        }
    }

    [RelayCommand] private void OpenDataDir() => Main.Services.OpenPath(DataDir);

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (!await Main.Services.ConfirmAsync(Loc.T("Удалить Opti с компьютера?"),
                Loc.T("Opti откатит ВСЕ свои изменения, уберёт задачу продолжения и удалит свою папку данных (журнал, отчёты, файлы программы в ") +
                DataDir + Loc.T("). После этого закроется; файл Opti.exe удалите сами. Для части настроек понадобится перезагрузка."))) return;
        var r = await Main.Services.UninstallAsync();
        if (r is null) { Main.ShowMessage(Loc.T("В этом режиме удаление недоступно.")); return; }
        if (r.Rollback.Errors.Count > 0)
        {
            Main.ShowMessage(Loc.T("Часть изменений откатить не удалось, поэтому папка данных оставлена — без журнала их не вернуть. ") +
                             string.Join("; ", r.Rollback.Errors), error: true);
            return;
        }
        Main.ShowMessage(r.Rollback.Summary(Loc.T("Откачено изменений")) + Loc.T(" Папка данных будет удалена после закрытия. Перезагрузите компьютер."));
        await Task.Delay(1500);
        Main.Services.Exit();
    }

    /// <summary>Архив для обращения в поддержку: сохраняется на рабочий стол и показывается в Проводнике.</summary>
    [RelayCommand]
    private void CreateProblemReport()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Opti.Workflow.ProblemReport.Create(DataDir, Main.Services.Journal,
                string.IsNullOrEmpty(desktop) ? Main.Services.ReportsDir : desktop, Main.Services.AppVersion);
            Main.ShowMessage(Loc.T("Отчёт о проблеме сохранён: ") + path + Loc.T(". Он нигде не публикуется — приложите его к обращению сами."));
            Main.Services.RevealFile(path);
        }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Не удалось собрать отчёт: ") + ex.Message, error: true); }
    }
    [RelayCommand] private void OpenReleases() => Main.Services.OpenPath(Opti.Workflow.AppInfo.ReleasesUrl);
}
