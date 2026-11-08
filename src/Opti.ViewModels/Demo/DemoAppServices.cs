using System.Reflection;
using Opti.Analysis;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Cleanup;
using Opti.Workflow.Measuring;
using Opti.Workflow.Sessions;

namespace Opti.ViewModels.Demo;

/// <summary>
/// Демо-окружение: настоящий интерфейс и логика, но система не трогается. Профиль ПК — встроенный пример
/// (настольный ПК с RTX), настройки «пишутся» в эмуляцию, замеры синтетические, данные — во временной папке.
/// Нужен для снимков интерфейса в CI (Opti.exe --screenshots) и для показа программы без изменений в системе.
/// </summary>
public sealed class DemoAppServices : IAppServices
{
    public DemoAppServices(string dataDir, SystemProfile? profile = null)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Profile = profile ?? LoadEmbeddedProfile("desktop-rtx");
        var backends = ProfileSeeder.CreateEmulation(Profile, out _emulation);
        Journal = new InMemoryChangeJournal();
        Settings = new OptiSettings { MeasureRuns = 3, CheckForUpdates = false };
        Workflow = new TuneWorkflow(Settings, new SessionStore(Path.Combine(dataDir, "sessions")), backends, Journal);
        Tweaks = new TweakService(backends, Journal);
    }

    private readonly Dictionary<ChangeType, InMemoryBackend> _emulation;

    public SystemProfile Profile { get; }

    /// <summary>Демо «Стража»: будто обновление Windows вернуло одну из настроек, изменённых Opti.</summary>
    public bool SimulateWindowsRevert()
    {
        var rec = Journal.GetAll().LastOrDefault(r => !r.RolledBack && r.Type == ChangeType.Registry);
        if (rec is null) return false;
        _emulation[ChangeType.Registry].Write(rec.Target, rec.Before);
        return true;
    }
    public TuneWorkflow Workflow { get; }
    public OptiSettings Settings { get; }
    public IChangeJournal Journal { get; }
    public TweakService Tweaks { get; }
    public string AppVersion => AppInfo.Version + " (демо)";
    public string ReportsDir => Path.Combine(DataDir, "reports");
    public string DataDir { get; }
    public string ResumeExePath => "Opti.exe";
    public string? StartupNotice => null;
    public int OsBuild => 26100;
    /// <summary>Что «открыла» бы программа (ссылки, папки) — в демо только запоминается.</summary>
    public List<string> Opened { get; } = new();

    public static SystemProfile LoadEmbeddedProfile(string name)
    {
        var asm = typeof(DemoAppServices).Assembly;
        using var s = asm.GetManifestResourceStream($"Opti.Demo.{name}.json")
                      ?? throw new FileNotFoundException("Нет демо-профиля " + name);
        using var rd = new StreamReader(s);
        return OptiJson.Deserialize<SystemProfile>(rd.ReadToEnd());
    }

    public Task<AnalysisResult> AnalyzeAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("демо-профиль");
        return Task.FromResult(SystemAnalyzer.FromProfile(Profile));
    }

    public bool IsModeAvailable(MeasureMode mode) => mode is MeasureMode.Synthetic or MeasureMode.Game;
    public IMeasureRunner CreateRunner(MeasureMode mode) => new DemoRunner(this);
    public void RestartComputer() => Opened.Add("restart");
    public void OpenPath(string pathOrUrl) => Opened.Add(pathOrUrl);
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(null);
    public IReadOnlyList<ICleanupTarget> CleanupTargets() => DemoCleanup.Targets;

    /// <summary>Синтетические замеры: «после» исправлений — заметно ровнее и быстрее.</summary>
    public static MeasurementRun SyntheticRun(string label, bool after, int seed)
    {
        var rng = new Random(seed);
        double baseMs = after ? 7.6 : 8.4, spike = after ? 14 : 22, rate = after ? 0.012 : 0.025;
        var ft = new List<double>(5000);
        for (int i = 0; i < 5000; i++)
            ft.Add(baseMs + (rng.NextDouble() - 0.5) * 1.2 + (rng.NextDouble() < rate ? spike * rng.NextDouble() : 0));
        return new MeasurementRun
        {
            Label = label, FrameTimesMs = ft,
            Conditions = new RunConditions { ProcessName = "Opti Bench", Scenario = "синтетический тест", Width = 2560, Height = 1440, RefreshHz = 165, OnAcPower = true },
        };
    }

    private sealed class DemoRunner : IMeasureRunner
    {
        private readonly DemoAppServices _svc;
        public DemoRunner(DemoAppServices svc) => _svc = svc;
        public string Name => "демо";

        public Task<MeasurementRun> RunAsync(MeasureRequest r, IProgress<MeasureProgress>? progress = null, CancellationToken ct = default)
        {
            bool after = _svc.Workflow.Store.LatestOpen() is { Before.Count: > 0 };
            var run = SyntheticRun(r.Label, after, r.Label.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
            progress?.Report(new MeasureProgress(r.DurationSec, r.DurationSec, run.FrameTimesMs.TakeLast(300).ToList(), "Opti Bench", false));
            return Task.FromResult(run);
        }
    }
}

/// <summary>Цели очистки для демо: размеры фиксированные, ничего не удаляется.</summary>
internal static class DemoCleanup
{
    public static readonly IReadOnlyList<ICleanupTarget> Targets = new ICleanupTarget[]
    {
        new Fake("recycle", "Корзина", "Файлы, удалённые в корзину", 1_840_000_000, 312, true),
        new Fake("temp", "Временные файлы", "Файлы старше 7 дней из TEMP Windows и пользователя", 2_650_000_000, 18_204, true),
        new Fake("wu", "Загруженные обновления Windows", "Уже установленные пакеты обновлений", 3_100_000_000, 96, true),
        new Fake("shader", "Кеш шейдеров", "Пересоздаётся при запуске игр — первый запуск будет дольше", 1_200_000_000, 2_410, false,
            caution: "Первый запуск игр после очистки будет дольше."),
    };

    private sealed class Fake : ICleanupTarget
    {
        private readonly long _bytes;
        private readonly int _files;
        public Fake(string id, string title, string desc, long bytes, int files, bool selected, string? caution = null)
        {
            Id = id; Title = title; Description = desc; _bytes = bytes; _files = files; DefaultSelected = selected; Caution = caution;
        }
        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public bool DefaultSelected { get; }
        public string? Caution { get; }
        public string? ManualUri => null;
        public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.FromResult(new CleanupEstimate(_bytes, _files));
        public Task<CleanupResult> CleanAsync(CancellationToken ct = default) => Task.FromResult(new CleanupResult { FreedBytes = _bytes, Deleted = _files });
    }
}
