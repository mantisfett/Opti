using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Opti.Workflow.Sessions;

namespace Opti.ViewModels;

/// <summary>Всё, что ViewModel'ям нужно от платформы. В приложении — Windows-реализация, в тестах — эмуляция на профиле.</summary>
public interface IAppServices
{
    TuneWorkflow Workflow { get; }
    OptiSettings Settings { get; }
    IChangeJournal Journal { get; }
    string AppVersion { get; }
    string ReportsDir { get; }
    string DataDir { get; }

    Task<AnalysisResult> AnalyzeAsync(IProgress<string> progress, CancellationToken ct);
    bool IsModeAvailable(MeasureMode mode);
    IMeasureRunner CreateRunner(MeasureMode mode);
    /// <summary>Путь и аргументы для продолжения после перезагрузки.</summary>
    string ResumeExePath { get; }
    void RestartComputer();
    void OpenPath(string pathOrUrl);
    /// <summary>Показать файл в Проводнике (выделенным).</summary>
    void RevealFile(string path) => OpenPath(Path.GetDirectoryName(path) ?? path);
    Task<bool> ConfirmAsync(string title, string message);
    Task<UpdateInfo?> CheckForUpdatesAsync();
    /// <summary>Категории бережной очистки.</summary>
    IReadOnlyList<Opti.Workflow.Cleanup.ICleanupTarget> CleanupTargets();
    /// <summary>Твики удобства (контекстное меню, Проводник, «Пуск»…).</summary>
    Opti.Fixes.Services.TweakService Tweaks { get; }
    /// <summary>Сборка Windows (0 — неизвестно).</summary>
    int OsBuild => 0;
    /// <summary>Перезапуск Проводника, чтобы твики вступили в силу без выхода из системы.</summary>
    void RestartExplorer() { }
    /// <summary>Сообщение о переносе данных из GameTune (если был перенос).</summary>
    string? StartupNotice { get; }
    /// <summary>Удалить Opti с компьютера: откат всего, задача продолжения, папка данных. null — недоступно (демо, тесты).</summary>
    Task<Opti.Workflow.UninstallReport?> UninstallAsync() => Task.FromResult<Opti.Workflow.UninstallReport?>(null);
    /// <summary>Закрыть программу (после удаления).</summary>
    void Exit() { }
}

/// <summary>
/// Смысловые тона статусов. Интерфейс сопоставляет их с кистями системной темы (светлой или тёмной),
/// поэтому ViewModel'и не знают конкретных цветов.
/// </summary>
public static class Palette
{
    public const string Good = "good";
    public const string Bad = "bad";
    public const string Warn = "warn";
    public const string Neutral = "neutral";
    public const string Accent = "accent";
    public const string Critical = "bad";
    public const string Important = "warn";
    public const string Advice = "neutral";
}
