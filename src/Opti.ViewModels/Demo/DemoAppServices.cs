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
using Opti.Core.Localization;

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
        if (Profile.Drivers.Count == 0) Profile.Drivers.AddRange(DemoDrivers);
        Profile.DeliveryOptimizationMode ??= 3;
        if (Profile.Background.StartupItems.Count == 0) Profile.Background.StartupItems.AddRange(DemoStartup);
        var backends = ProfileSeeder.CreateEmulation(Profile, out _emulation);
        Journal = new InMemoryChangeJournal();
        Settings = new OptiSettings { MeasureRuns = 3, CheckForUpdates = false };
        Workflow = new TuneWorkflow(Settings, new SessionStore(Path.Combine(dataDir, "sessions")), backends, Journal);
        Tweaks = new TweakService(backends, Journal);
        Startup = new Opti.Workflow.StartupManager(backends, Journal, () => Profile.Background.StartupItems);
        Game = new Opti.Workflow.GameSession(backends, Journal, () => Profile, _ => 1);
        Priority = new Opti.Workflow.GamePriority(backends, Journal);
    }

    private readonly Dictionary<ChangeType, InMemoryBackend> _emulation;

    private const string Approved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|";
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private static StartupItem Startup1(string name, string cmd, bool enabled = true) =>
        new() { Name = name, Command = cmd, Location = RunKey, Enabled = enabled, ApprovedKey = Approved + name };

    /// <summary>Примерные пункты автозагрузки для демо (в профиле их нет).</summary>
    private static readonly StartupItem[] DemoStartup =
    {
        Startup1("Discord", @"C:\Users\Demo\AppData\Local\Discord\Update.exe --processStart Discord.exe"),
        Startup1("Steam", @"C:\Program Files (x86)\Steam\steam.exe -silent"),
        Startup1("OneDrive", @"C:\Users\Demo\AppData\Local\Microsoft\OneDrive\OneDrive.exe /background"),
        Startup1("Spotify", @"C:\Users\Demo\AppData\Roaming\Spotify\Spotify.exe --autostart"),
        Startup1("Adobe Creative Cloud", @"C:\Program Files\Adobe\Adobe Creative Cloud\ACC\Creative Cloud.exe --showwindow=false"),
        Startup1("LGHUB", @"C:\Program Files\LGHUB\lghub.exe --background"),
        Startup1("EpicGamesLauncher", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe -silent", enabled: false),
        Startup1("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe"),
        Startup1("RtkAudUService", @"C:\Windows\System32\DriverStore\RtkAudUService64.exe -background"),
    };

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
    public Opti.Workflow.StartupManager? Startup { get; }
    public Opti.Workflow.GameSession? Game { get; }
    public Opti.Workflow.GamePriority? Priority { get; }
    public string AppVersion => AppInfo.Version + Loc.T(" (демо)");
    public string ReportsDir => Path.Combine(DataDir, "reports");
    public string DataDir { get; }
    public string ResumeExePath => "Opti.exe";
    public string? StartupNotice => null;
    /// <summary>Плашка «Демо-режим». В снимках интерфейса выключена, чтобы они показывали программу как есть.</summary>
    public bool IsDemo { get; init; } = true;
    public int OsBuild => 26100;
    /// <summary>Что «открыла» бы программа (ссылки, папки) — в демо только запоминается.</summary>
    public List<string> Opened { get; } = new();

    /// <summary>Драйверы демо-ПК: свежий видеодрайвер, старый сетевой, стандартный звук Microsoft, чипсет, NVMe.</summary>
    private static readonly DriverInfo[] DemoDrivers =
    {
        new() { DeviceName = "NVIDIA GeForce RTX 4070", Category = DriverCategory.Display, Manufacturer = "NVIDIA", Provider = "NVIDIA", Version = "32.0.15.6614", Date = new DateTime(2026, 9, 24), HardwareId = @"PCI\VEN_10DE&DEV_2786" },
        new() { DeviceName = "Realtek PCIe 2.5GbE Family Controller", Category = DriverCategory.Network, Manufacturer = "Realtek", Provider = "Realtek", Version = "10.38.1110.2019", Date = new DateTime(2019, 11, 10), HardwareId = @"PCI\VEN_10EC&DEV_8125" },
        new() { DeviceName = "High Definition Audio Device", Category = DriverCategory.Audio, Manufacturer = "Realtek", Provider = "Microsoft", Version = "10.0.26100.1", Date = new DateTime(2006, 6, 21), HardwareId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897" },
        new() { DeviceName = "AMD SMBus", Category = DriverCategory.Chipset, Manufacturer = "AMD", Provider = "Advanced Micro Devices, Inc", Version = "5.12.0.38", Date = new DateTime(2025, 3, 14), HardwareId = @"PCI\VEN_1022&DEV_790B" },
        new() { DeviceName = "Samsung SSD 990 PRO 2TB", Category = DriverCategory.Storage, Manufacturer = "Samsung", Provider = "Microsoft", Version = "10.0.26100.1", Date = new DateTime(2006, 6, 21), HardwareId = @"SCSI\DiskSamsung_SSD_990_PRO" },
        new() { DeviceName = "Intel(R) Wireless Bluetooth(R)", Category = DriverCategory.Bluetooth, Manufacturer = "Intel Corporation", Provider = "Intel Corporation", Version = "23.60.5.10", Date = new DateTime(2025, 7, 2), HardwareId = @"USB\VID_8087&PID_0033" },
    };

    public static SystemProfile LoadEmbeddedProfile(string name)
    {
        var asm = typeof(DemoAppServices).Assembly;
        using var s = asm.GetManifestResourceStream($"Opti.Demo.{name}.json")
                      ?? throw new FileNotFoundException(Loc.T("Нет демо-профиля ") + name);
        using var rd = new StreamReader(s);
        return OptiJson.Deserialize<SystemProfile>(rd.ReadToEnd());
    }

    public Task<AnalysisResult> AnalyzeAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report(Loc.T("демо-профиль"));
        return Task.FromResult(SystemAnalyzer.FromProfile(Profile));
    }

    public bool IsModeAvailable(MeasureMode mode) => mode is MeasureMode.Synthetic or MeasureMode.Game;
    public IMeasureRunner CreateRunner(MeasureMode mode) => new DemoRunner(this);
    public void RestartComputer() => Opened.Add("restart");
    public void OpenPath(string pathOrUrl) => Opened.Add(pathOrUrl);
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    public Task<IReadOnlyList<Opti.Measure.Capture.ProbeResult>> ProbeRegionsAsync(IProgress<Opti.Measure.Capture.ProbeResult> progress, CancellationToken ct)
    {
        // Примерные значения для ПК в Центральной Европе
        var data = new (string Id, double Ms, double Jitter, double Loss)[]
        {
            ("eu-central-1", 18, 1.4, 0), ("eu-north-1", 34, 2.1, 0), ("eu-west-2", 41, 2.6, 0), ("eu-west-1", 46, 3.0, 0), ("eu-south-1", 52, 3.8, 0),
            ("us-east-1", 112, 6.5, 0), ("me-south-1", 128, 9.5, 0), ("us-west-2", 168, 7.2, 0), ("sa-east-1", 214, 11.0, 0), ("ap-south-1", 142, 8.4, 0),
            ("ap-southeast-1", 176, 8.8, 0), ("ap-northeast-1", 232, 10.2, 0), ("ap-southeast-2", 296, 12.0, 0),
        };
        IReadOnlyList<Opti.Measure.Capture.ProbeResult> list = data.Select(d => new Opti.Measure.Capture.ProbeResult(
            Opti.Measure.Capture.ConnectionProbe.Regions.First(r => r.Id == d.Id), d.Ms, d.Jitter, d.Loss, 12)).ToList();
        foreach (var r in list) progress.Report(r);
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<Opti.Measure.Capture.DnsResult>> BenchmarkDnsAsync(IProgress<Opti.Measure.Capture.DnsResult> progress, CancellationToken ct)
    {
        var cur = new Opti.Measure.Capture.DnsResolver("current:192.168.1.1", "Сейчас: 192.168.1.1", "192.168.1.1");
        IReadOnlyList<Opti.Measure.Capture.DnsResult> list = new[]
        {
            new Opti.Measure.Capture.DnsResult(cur, 46, 0, 16),
            new Opti.Measure.Capture.DnsResult(Opti.Measure.Capture.DnsBenchmark.Public[0], 9, 0, 16),
            new Opti.Measure.Capture.DnsResult(Opti.Measure.Capture.DnsBenchmark.Public[1], 12, 0, 16),
            new Opti.Measure.Capture.DnsResult(Opti.Measure.Capture.DnsBenchmark.Public[2], 15, 0, 16),
            new Opti.Measure.Capture.DnsResult(Opti.Measure.Capture.DnsBenchmark.Public[3], 21, 0, 16),
        };
        foreach (var r in list) progress.Report(r);
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<Opti.Measure.Capture.RouteHop>> TraceRouteAsync(string host, IProgress<Opti.Measure.Capture.RouteHop> progress, CancellationToken ct)
    {
        IReadOnlyList<Opti.Measure.Capture.RouteHop> hops = new[]
        {
            new Opti.Measure.Capture.RouteHop(1, "192.168.1.1", 1.2, 0, 3), new Opti.Measure.Capture.RouteHop(2, "10.20.0.1", 6.8, 0, 3),
            new Opti.Measure.Capture.RouteHop(3, null, double.NaN, 100, 3), new Opti.Measure.Capture.RouteHop(4, "81.23.44.9", 9.4, 0, 3),
            new Opti.Measure.Capture.RouteHop(5, "80.81.192.18", 14.1, 0, 3), new Opti.Measure.Capture.RouteHop(6, "52.93.12.201", 17.6, 0, 3),
            new Opti.Measure.Capture.RouteHop(7, "52.94.100.5", 18.3, 0, 3),
        };
        foreach (var h in hops) progress.Report(h);
        return Task.FromResult(hops);
    }

    public Task<List<Opti.Workflow.BackgroundApp>> ScanBackgroundAsync(CancellationToken ct) => Task.FromResult(new List<Opti.Workflow.BackgroundApp>
    {
        new("chrome", 31, 6.4, 2150, Opti.Workflow.BackgroundCategory.Browser, true),
        new("Discord", 6, 3.1, 640, Opti.Workflow.BackgroundCategory.Chat, true),
        new("OneDrive", 1, 2.2, 180, Opti.Workflow.BackgroundCategory.Cloud, true),
        new("Steam", 5, 1.4, 410, Opti.Workflow.BackgroundCategory.Launcher, true),
        new("Spotify", 4, 0.9, 330, Opti.Workflow.BackgroundCategory.Media, true),
        new("LGHUB", 3, 0.6, 260, Opti.Workflow.BackgroundCategory.Vendor, true),
    });

    public int CloseBackgroundApp(string name) => 0;

    public Task<Opti.Measure.Capture.BufferbloatResult> RunBufferbloatAsync(string target, IProgress<string> phase, CancellationToken ct) =>
        Task.FromResult(new Opti.Measure.Capture.BufferbloatResult(target, 18, 1.4, 0, 71, 214));

    public async Task MonitorPingAsync(string target, IProgress<Opti.Measure.Capture.PingSample> progress, CancellationToken ct)
    {
        // Примерный ряд: ~20 мс с небольшим разбросом, редкие скачки и одна потеря; первые замеры приходят сразу
        var rng = new Random(7);
        for (int i = 0; !ct.IsCancellationRequested; i++)
        {
            double ms = 19 + rng.NextDouble() * 4 + (i % 23 == 17 ? 38 : 0);
            progress.Report(new Opti.Measure.Capture.PingSample(i == 41 ? double.NaN : ms));
            try { await Task.Delay(i < 60 ? 5 : 250, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public Task<Opti.Measure.Capture.WifiScanResult> ScanWifiAsync(CancellationToken ct)
    {
        // Примерный двор: свой роутер на 2,4 ГГц (канал 6) и 5 ГГц, соседи на 5–7 и 1
        var nets = new List<Opti.Measure.Capture.WifiNetwork>
        {
            new("HomeNet", "aa:bb:cc:00:00:01", 88, 6), new("HomeNet", "aa:bb:cc:00:00:02", 64, 36),
            new("Neighbor-5G", "11:22:33:00:00:01", 80, 6), new("TP-Link_4F2A", "11:22:33:00:00:02", 62, 5),
            new("Keenetic-77", "11:22:33:00:00:03", 55, 7), new("Xiaomi_Home", "11:22:33:00:00:04", 40, 1),
            new("ASUS_guest", "11:22:33:00:00:05", 48, 6), new("Rostelecom-A1", "11:22:33:00:00:06", 70, 44),
        };
        return Task.FromResult(new Opti.Measure.Capture.WifiScanResult(nets, new Opti.Measure.Capture.WifiCurrent("HomeNet", 6, 88)));
    }

    public Task<Opti.Fixes.Services.ApplyReport?> ApplyDnsAsync(string servers) => Task.FromResult<Opti.Fixes.Services.ApplyReport?>(null);

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
            Conditions = new RunConditions { ProcessName = "Opti Bench", Scenario = Loc.T("синтетический тест"), Width = 2560, Height = 1440, RefreshHz = 165, OnAcPower = true },
        };
    }

    private sealed class DemoRunner : IMeasureRunner
    {
        private readonly DemoAppServices _svc;
        public DemoRunner(DemoAppServices svc) => _svc = svc;
        public string Name => Loc.T("демо");

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
        new Fake("recycle", Loc.T("Корзина"), Loc.T("Файлы, удалённые в корзину"), 1_840_000_000, 312, true),
        new Fake("temp", Loc.T("Временные файлы"), Loc.T("Файлы старше 7 дней из TEMP Windows и пользователя"), 2_650_000_000, 18_204, true),
        new Fake("wu", Loc.T("Загруженные обновления Windows"), Loc.T("Уже установленные пакеты обновлений"), 3_100_000_000, 96, true),
        new Fake("shader", Loc.T("Кеш шейдеров"), Loc.T("Пересоздаётся при запуске игр — первый запуск будет дольше"), 1_200_000_000, 2_410, false,
            caution: Loc.T("Первый запуск игр после очистки будет дольше.")),
        new Fake("downloads", Loc.T("Загрузки"), Loc.T("Установщики и архивы, которые давно не нужны"), 14_700_000_000, 420, false,
            manualUri: "demo:downloads", warnAbove: 5L << 30, warnText: Loc.T("Больше 5 ГБ — стоит разобрать: обычно это старые установщики."), action: Loc.T("Открыть папку")),
        new Fake("restore", Loc.T("Точки восстановления"), Loc.T("Windows хранит их сама; объём можно ограничить"), 6_400_000_000, 3, false,
            manualUri: "demo:restore", action: Loc.T("Открыть «Защиту системы»")),
    };

    private sealed class Fake : ICleanupTarget
    {
        private readonly long _bytes;
        private readonly int _files;
        public Fake(string id, string title, string desc, long bytes, int files, bool selected, string? caution = null,
            string? manualUri = null, long? warnAbove = null, string? warnText = null, string? action = null)
        {
            Id = id; Title = title; Description = desc; _bytes = bytes; _files = files; DefaultSelected = selected; Caution = caution;
            ManualUri = manualUri; WarnAboveBytes = warnAbove; WarnText = warnText; ManualActionText = action ?? Loc.T("Открыть параметры");
        }
        public long? WarnAboveBytes { get; }
        public string? WarnText { get; }
        public string ManualActionText { get; }
        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public bool DefaultSelected { get; }
        public string? Caution { get; }
        public string? ManualUri { get; }
        public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.FromResult(new CleanupEstimate(_bytes, _files));
        public Task<CleanupResult> CleanAsync(CancellationToken ct = default) => Task.FromResult(new CleanupResult { FreedBytes = _bytes, Deleted = _files });
    }
}
