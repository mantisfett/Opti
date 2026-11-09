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
    /// <summary>Демо-режим: данные примерные, система не меняется.</summary>
    bool IsDemo => false;

    /// <summary>Задержка, разброс и потери до облачных регионов (TCP-рукопожатие).</summary>
    Task<IReadOnlyList<Opti.Measure.Capture.ProbeResult>> ProbeRegionsAsync(IProgress<Opti.Measure.Capture.ProbeResult> progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<Opti.Measure.Capture.ProbeResult>>(async () => await Opti.Measure.Capture.ConnectionProbe.ProbeAllAsync(Opti.Measure.Capture.ConnectionProbe.Regions, progress, ct), ct);

    /// <summary>Трассировка маршрута до сервера.</summary>
    Task<IReadOnlyList<Opti.Measure.Capture.RouteHop>> TraceRouteAsync(string host, IProgress<Opti.Measure.Capture.RouteHop> progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<Opti.Measure.Capture.RouteHop>>(async () => await Opti.Measure.Capture.RouteTrace.RunAsync(host, progress: progress, ct: ct), ct);

    /// <summary>Пинг в простое и под нагрузкой (скачивание и отдача) — bufferbloat.</summary>
    Task<Opti.Measure.Capture.BufferbloatResult> RunBufferbloatAsync(string target, IProgress<string> phase, CancellationToken ct) =>
        Task.Run(() => Opti.Measure.Capture.Bufferbloat.RunAsync(target, phase, ct: ct), ct);

    /// <summary>Живой пинг: замеры приходят, пока не отменят.</summary>
    Task MonitorPingAsync(string target, IProgress<Opti.Measure.Capture.PingSample> progress, CancellationToken ct) =>
        Task.Run(() => Opti.Measure.Capture.LivePing.RunAsync(target, progress, ct: ct), ct);

    /// <summary>Скорость DNS: текущие серверы и популярные публичные.</summary>
    Task<IReadOnlyList<Opti.Measure.Capture.DnsResult>> BenchmarkDnsAsync(IProgress<Opti.Measure.Capture.DnsResult> progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<Opti.Measure.Capture.DnsResult>>(async () =>
        {
            var list = new List<Opti.Measure.Capture.DnsResult>();
            foreach (var r in Opti.Measure.Capture.DnsBenchmark.Current().Concat(Opti.Measure.Capture.DnsBenchmark.Public))
            {
                var res = await Opti.Measure.Capture.DnsBenchmark.RunAsync(r, ct: ct);
                list.Add(res);
                progress.Report(res);
            }
            return list;
        }, ct);

    /// <summary>Игровая сессия (закрыть лишнее + схема питания на время игры). null — недоступно.</summary>
    Opti.Workflow.GameSession? Game => null;

    /// <summary>Менеджер автозагрузки. null — недоступно.</summary>
    Opti.Workflow.StartupManager? Startup => null;

    /// <summary>Самые тяжёлые фоновые программы (замер процессора по процессам).</summary>
    Task<List<Opti.Workflow.BackgroundApp>> ScanBackgroundAsync(CancellationToken ct) => Opti.Workflow.BackgroundScanner.ScanAsync(ct: ct);

    /// <summary>Закрыть программу по имени процесса. Возвращает число закрытых процессов.</summary>
    int CloseBackgroundApp(string name) => Opti.Workflow.BackgroundScanner.Close(name);

    /// <summary>Применить DNS-серверы (через журнал: откат в «Истории»). null — недоступно (демо).</summary>
    Task<Opti.Fixes.Services.ApplyReport?> ApplyDnsAsync(string servers) => Task.Run<Opti.Fixes.Services.ApplyReport?>(() =>
        new Opti.Fixes.Services.ApplyService(Workflow.BackendsForRollback, Journal, null).Apply(new Opti.Core.Profile.SystemProfile(),
            new Opti.Core.Fixes.IFix[] { new Opti.Fixes.Catalog.DnsSetFix() }, Opti.Core.Strategy.UserGoal.Balanced,
            new Dictionary<string, IReadOnlyDictionary<string, string>> { [Opti.Core.Fixes.FixIds.DnsSet] = new Dictionary<string, string> { ["servers"] = servers } }, "network-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
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
