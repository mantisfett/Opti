using System.Diagnostics;
using System.Windows;
using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.ViewModels;
using Opti.Workflow;
using Opti.Workflow.Cleanup;
using Opti.Workflow.Measuring;
using Opti.Workflow.Sessions;

namespace Opti.App.Services;

/// <summary>Реализация сервисов для Windows: реальные коллекторы, реестр/BCD/powercfg, журнал SQLite, планировщик.</summary>
public sealed class WindowsAppServices : IAppServices, IDisposable
{
    private readonly SqliteChangeJournal _journal;
    private readonly WindowsResumeScheduler _scheduler;

    public WindowsAppServices()
    {
        // Сначала защита папки данных: только потом читаем настройки и журнал из неё.
        var notices = OptiPaths.PrepareDataDir().ToList();
        Opti.Core.Diagnostics.OptiLog.Init(OptiPaths.LogsDir);
        SelfUpdater.CleanupOld();
        Opti.Core.Diagnostics.OptiLog.Info($"Opti {AppInfo.Version} запущен: Windows {Environment.OSVersion.Version}, администратор: {DataDirSecurity.IsElevated()}");
        foreach (var n in notices) Opti.Core.Diagnostics.OptiLog.Warn(n);
        if (Opti.Native.SessionUser.MismatchWarning() is { } who) notices.Add(Opti.Core.Localization.Loc.T(who));
        StartupNotice = notices.Count > 0 ? string.Join("\n\n", notices) : null;
        var scheduler = _scheduler = new WindowsResumeScheduler();
        scheduler.CancelLegacy();
        Settings = OptiSettings.Load();
        _journal = new SqliteChangeJournal(OptiPaths.JournalPath);
        var backends = WindowsBackends.Create();
        Workflow = new TuneWorkflow(Settings, new SessionStore(), backends, _journal,
            new WindowsRestorePointService(), scheduler, new WindowsCrashEventSource());
        Tweaks = new TweakService(backends, _journal);
        Priority = new Opti.Workflow.GamePriority(backends, _journal);
        Game = new Opti.Workflow.GameSession(backends, _journal, ReadPowerProfile, Opti.Workflow.BackgroundScanner.Close);
        _watcher = new Opti.Workflow.GameWatcher(Game, () => Settings.AutoGameSession,
            () => Opti.Workflow.GameWatcher.ParseNames(Settings.GameProcesses), RunningProcessNames);
        _watcher.Start(TimeSpan.FromSeconds(5));
        Startup = new Opti.Workflow.StartupManager(backends, _journal, () => Opti.Analysis.Collectors.BackgroundCollector.ReadStartupItems());
    }

    public TuneWorkflow Workflow { get; }
    public TweakService Tweaks { get; }
    public Opti.Workflow.StartupManager? Startup { get; }
    public Opti.Workflow.GameSession? Game { get; }
    public Opti.Workflow.GamePriority? Priority { get; }
    private readonly Opti.Workflow.GameWatcher _watcher;

    private static ISet<string> RunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) { try { set.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
        return set;
    }

    /// <summary>Быстрый сбор только того, что нужно игровой сессии: тип устройства и схемы питания.</summary>
    private static Opti.Core.Profile.SystemProfile ReadPowerProfile()
    {
        var p = new Opti.Core.Profile.SystemProfile();
        new Opti.Analysis.Collectors.DeviceCollector().Collect(p);
        new Opti.Analysis.Collectors.PowerCollector().Collect(p);
        return p;
    }
    public int OsBuild => Environment.OSVersion.Version.Build;

    public void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            // Обычно Windows перезапускает оболочку сама; если нет — запускаем
            Thread.Sleep(1500);
            if (Process.GetProcessesByName("explorer").Length == 0) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Opti", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public OptiSettings Settings { get; }
    public IChangeJournal Journal => _journal;
    public string AppVersion => AppInfo.Version;
    public string ReportsDir => OptiPaths.ReportsDir;
    public string DataDir => OptiPaths.DataDir;
    public string? StartupNotice { get; }
    public Task<UninstallReport?> UninstallAsync() =>
        Task.Run<UninstallReport?>(() => Uninstaller.Run(Workflow, _journal, _scheduler, OptiPaths.DataDir));

    public void Exit()
    {
        _journal.Dispose(); // файл журнала не должен мешать удалению папки
        Application.Current.Shutdown();
    }

    public string ResumeExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Opti.exe");

    public Task<AnalysisResult> AnalyzeAsync(IProgress<string> progress, CancellationToken ct) =>
        Task.Run(() => SystemAnalyzer.Collect(SystemAnalyzer.DefaultCollectors(Settings.ObsConfigDir), progress.Report), ct);

    public bool IsModeAvailable(MeasureMode mode) => mode switch
    {
        MeasureMode.Synthetic => SyntheticRunner.Locate() is not null,
        MeasureMode.Game => true,
        _ => false,
    };

    public IMeasureRunner CreateRunner(MeasureMode mode) => MeasureRunnerFactory.Create(mode, Settings);

    public void RestartComputer() => RebootHelper.RestartNow(5);

    public void OpenPath(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Opti", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public void RevealFile(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Opti", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public Task<UpdateInfo?> CheckForUpdatesAsync() => UpdateChecker.CheckAsync(AppVersion);

    public bool CanSelfUpdate => SelfUpdater.CanSelfUpdate;

    public Task<string> InstallUpdateAsync(IProgress<string> progress) => SelfUpdater.InstallLatestAsync(AppVersion, progress);

    public IReadOnlyList<ICleanupTarget> CleanupTargets() => CleanupCatalog.ForWindows();

    public void Dispose() { _watcher.Dispose(); _journal.Dispose(); }
}
