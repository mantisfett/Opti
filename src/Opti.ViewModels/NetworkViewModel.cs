using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Core.Localization;
using Opti.Measure.Capture;

namespace Opti.ViewModels;

public sealed class RegionRow
{
    public required string Name { get; init; }
    public required string MedianText { get; init; }
    public required string DetailText { get; init; }
    public required string Tone { get; init; }
    /// <summary>Длина полоски 0..1 относительно худшего региона (чем короче, тем лучше).</summary>
    public required double Bar { get; init; }
    public bool IsBest { get; init; }
    public string Host { get; init; } = "";
}

public sealed class HopRow
{
    public required string Ttl { get; init; }
    public required string Address { get; init; }
    public required string MedianText { get; init; }
    public required string LossText { get; init; }
    public required string Tone { get; init; }
}

public sealed class DnsRow
{
    public required string Name { get; init; }
    public required string MedianText { get; init; }
    public required string Tone { get; init; }
    public required string Servers { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsBest { get; init; }
    public bool CanApply => !IsCurrent && Servers.Length > 0;
}

public sealed class TrafficRow
{
    public required string Name { get; init; }
    public required string UpText { get; init; }
    public required string DownText { get; init; }
    public required string Tone { get; init; }
    public required double Bar { get; init; }
    public required string ProcessName { get; init; }
    public bool CanClose { get; init; }
}

public sealed class WifiRow
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required string Tag { get; init; }
    public required string Tone { get; init; }
    /// <summary>Длина полоски 0..1 относительно самого загруженного канала.</summary>
    public required double Bar { get; init; }
    public bool HasTag => Tag.Length > 0;
}

public sealed class PingBar
{
    /// <summary>Высота столбика, 2..48.</summary>
    public required double Height { get; init; }
    public required string Tone { get; init; }
    public required string Tip { get; init; }
}

/// <summary>Страница «Сеть»: куда связь быстрее и стабильнее, какой DNS быстрее. Только чтение; DNS меняется по кнопке, с откатом.</summary>
public sealed partial class NetworkViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    public NetworkViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Сеть");

    public ObservableCollection<RegionRow> Regions { get; } = new();
    public ObservableCollection<DnsRow> DnsRows { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunRegionsCommand), nameof(RunDnsCommand), nameof(RunRouteCommand), nameof(RunBloatCommand), nameof(RunWifiCommand), nameof(RunTrafficCommand))] private bool _isBusy;
    [ObservableProperty] private string? _regionsSummary;
    [ObservableProperty] private string? _dnsSummary;
    [ObservableProperty] private string? _progressText;

    // ---------------- Кто грузит сеть ----------------
    public const int TrafficSeconds = 15;
    public ObservableCollection<TrafficRow> TrafficRows { get; } = new();
    [ObservableProperty] private string? _trafficSummary;
    [ObservableProperty] private string _trafficTone = Palette.Neutral;

    /// <summary>Тон по скорости: от 200 КБ/с — заметно (внимание), от 2 МБ/с — сильно занимает канал.</summary>
    public static string TrafficToneFor(double kbps) => kbps >= 2048 ? Palette.Bad : kbps >= 200 ? Palette.Warn : Palette.Neutral;

    public static string FormatRate(double kbps) => kbps >= 1024 ? $"{kbps / 1024:0.#} {Loc.T("МБ/с")}" : $"{kbps:0} {Loc.T("КБ/с")}";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunTrafficAsync()
    {
        IsBusy = true; TrafficSummary = null; TrafficRows.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            var task = Main.Services.MeasureTrafficAsync(TrafficSeconds, _cts.Token);
            for (int left = TrafficSeconds; !task.IsCompleted; left = Math.Max(0, left - 1))
            {
                ProgressText = Loc.T($"Считаю трафик, осталось около {left} с…");
                await Task.WhenAny(task, Task.Delay(1000, CancellationToken.None));
            }
            ShowTraffic(await task);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void ShowTraffic(IReadOnlyList<NetUsageRow> rows)
    {
        TrafficRows.Clear();
        double max = Math.Max(1, rows.Select(r => r.TotalKbps).DefaultIfEmpty(1).Max());
        foreach (var r in rows)
            TrafficRows.Add(new TrafficRow
            {
                Name = r.Process, ProcessName = r.Process,
                UpText = FormatRate(r.SentKbps), DownText = FormatRate(r.ReceivedKbps),
                Tone = TrafficToneFor(Math.Max(r.SentKbps, r.ReceivedKbps)), Bar = r.TotalKbps / max,
                CanClose = !Opti.Workflow.BackgroundScanner.IsProtected(r.Process),
            });
        var top = rows.FirstOrDefault();
        if (top is null || top.TotalKbps < 50)
        {
            TrafficSummary = Loc.T("Фоновой сетевой активности почти нет — канал свободен для игры.");
            TrafficTone = Palette.Good;
        }
        else
        {
            TrafficSummary = Loc.T($"Больше всего трафика у {top.Process}: отдаёт {FormatRate(top.SentKbps)}, качает {FormatRate(top.ReceivedKbps)}.")
                + (Math.Max(top.SentKbps, top.ReceivedKbps) >= 200 ? Loc.T(" Пока это идёт, пинг в играх растёт — закройте программу или поставьте её загрузки на паузу на время игры.") : "");
            TrafficTone = TrafficToneFor(Math.Max(top.SentKbps, top.ReceivedKbps)) == Palette.Neutral ? Palette.Good : Palette.Warn;
        }
    }

    [RelayCommand]
    private async Task CloseTrafficAppAsync(TrafficRow row)
    {
        if (!row.CanClose) return;
        if (!await Main.Services.ConfirmAsync(Loc.T($"Закрыть {row.Name}?"),
                Loc.T("Программа закроется сейчас. Несохранённые данные в ней могут пропасть. Навсегда ничего не отключается.")))
            return;
        var n = Main.Services.CloseBackgroundApp(row.ProcessName);
        Main.ShowMessage(n > 0 ? Loc.T($"Закрыто: {row.Name}.") : Loc.T($"Не удалось закрыть {row.Name} — возможно, она уже закрыта или защищена."), n == 0);
        if (n > 0) TrafficRows.Remove(row);
    }

    // ---------------- Wi-Fi: каналы ----------------
    public ObservableCollection<WifiRow> WifiRows { get; } = new();
    [ObservableProperty] private string? _wifiSummary;
    [ObservableProperty] private string _wifiTone = Palette.Neutral;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunWifiAsync()
    {
        IsBusy = true; WifiSummary = null; WifiRows.Clear();
        _cts = new CancellationTokenSource();
        try { ShowWifi(await Main.Services.ScanWifiAsync(_cts.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void ShowWifi(WifiScanResult scan)
    {
        var advice = WifiChannels.Analyze(scan.Networks, scan.Current);
        WifiSummary = advice.Summary;
        WifiTone = advice.Tone;
        WifiRows.Clear();
        double max = Math.Max(1, advice.Channels.Select(c => c.Load).DefaultIfEmpty(1).Max());
        foreach (var c in advice.Channels)
            WifiRows.Add(new WifiRow
            {
                Name = (c.Is24 ? Loc.T("2,4 ГГц") : Loc.T("5 ГГц")) + " · " + Loc.T($"канал {c.Channel}"),
                Detail = Loc.T($"сетей рядом: {c.Neighbors}"),
                Tag = c.IsCurrent ? Loc.T("ваш канал") : c.IsBest ? Loc.T("свободнее всего") : "",
                Tone = c.IsCurrent ? (c.IsBest || advice.Tone == Palette.Good ? Palette.Good : Palette.Warn) : c.IsBest ? Palette.Good : Palette.Neutral,
                Bar = c.Load / max,
            });
    }

    // ---------------- Нагрузка на канал (bufferbloat) ----------------
    [ObservableProperty] private string? _bloatSummary;
    [ObservableProperty] private string _bloatGrade = "";
    [ObservableProperty] private string _bloatTone = Palette.Neutral;
    [ObservableProperty] private string _bloatIdle = "";
    [ObservableProperty] private string _bloatDown = "";
    [ObservableProperty] private string _bloatUp = "";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBloatAsync()
    {
        var host = string.IsNullOrWhiteSpace(RouteTarget) ? NetworkTest.DefaultTarget : RouteTarget.Trim();
        if (!ValidHost(host)) { Main.ShowMessage(Loc.T("Введите имя сервера или IP-адрес."), error: true); return; }
        IsBusy = true; BloatSummary = null;
        _cts = new CancellationTokenSource();
        try
        {
            var phase = new Progress<string>(t => { if (IsBusy) ProgressText = t; });
            ShowBloat(await Main.Services.RunBufferbloatAsync(host, phase, _cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void ShowBloat(BufferbloatResult r)
    {
        BloatSummary = Bufferbloat.Describe(r);
        bool ok = !double.IsNaN(r.IdleMs);
        BloatGrade = ok && (r.DownloadMs is not null || r.UploadMs is not null) ? r.Grade.ToString() : "";
        BloatTone = ok ? Bufferbloat.GradeTone(r.Grade) : Palette.Bad;
        string Inc(double? ms, double? inc) => ms is null ? "—" : $"{ms:0} {Loc.T("мс")} (+{inc:0})";
        BloatIdle = ok ? $"{r.IdleMs:0} {Loc.T("мс")}" : "—";
        BloatDown = Inc(r.DownloadMs, r.DownloadIncreaseMs);
        BloatUp = Inc(r.UploadMs, r.UploadIncreaseMs);
    }

    // ---------------- Живой пинг ----------------
    private CancellationTokenSource? _monitorCts;
    private readonly PingStats _stats = new(60);
    public ObservableCollection<PingBar> Bars { get; } = new();
    [ObservableProperty] private string _monitorTarget = NetworkTest.DefaultTarget;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEditMonitorTarget))] private bool _isMonitoring;
    public bool CanEditMonitorTarget => !IsMonitoring;
    [ObservableProperty] private string _monitorLast = "—";
    [ObservableProperty] private string _monitorAvg = "—";
    [ObservableProperty] private string _monitorMin = "—";
    [ObservableProperty] private string _monitorMax = "—";
    [ObservableProperty] private string _monitorJitter = "—";
    [ObservableProperty] private string _monitorLoss = "—";
    [ObservableProperty] private string _monitorTone = Palette.Neutral;

    private static bool ValidHost(string host) => host.Length is > 0 and <= 253 && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':');

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleMonitorAsync()
    {
        if (IsMonitoring) { StopMonitor(); return; }
        var host = string.IsNullOrWhiteSpace(MonitorTarget) ? NetworkTest.DefaultTarget : MonitorTarget.Trim();
        if (!ValidHost(host)) { Main.ShowMessage(Loc.T("Введите имя сервера или IP-адрес."), error: true); return; }
        _stats.Clear();
        Bars.Clear();
        IsMonitoring = true;
        var cts = _monitorCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<PingSample>(s => { if (!cts.IsCancellationRequested) AddSample(s); });
            await Main.Services.MonitorPingAsync(host, progress, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { if (ReferenceEquals(_monitorCts, cts)) { IsMonitoring = false; _monitorCts = null; } }
    }

    public void StopMonitor() => _monitorCts?.Cancel();
    public override void OnHidden() => StopMonitor();

    /// <summary>Тон по среднему пингу и потерям: до 50 мс без потерь — хорошо.</summary>
    public static string MonitorToneFor(double avg, double loss) => loss >= 2 ? Palette.Bad : double.IsNaN(avg) ? Palette.Bad : avg < 50 && loss < 1 ? Palette.Good : Palette.Neutral;

    public void AddSample(PingSample s)
    {
        _stats.Add(s);
        string F(double v) => double.IsNaN(v) ? "—" : $"{v:0}";
        MonitorLast = s.Lost ? Loc.T("потеря") : $"{s.Ms:0} {Loc.T("мс")}";
        MonitorAvg = F(_stats.Average);
        MonitorMin = F(_stats.Min);
        MonitorMax = F(_stats.Max);
        MonitorJitter = $"{_stats.Jitter:0.#}";
        MonitorLoss = $"{_stats.LossPercent:0} %";
        MonitorTone = MonitorToneFor(_stats.Average, _stats.LossPercent);
        double cap = Math.Max(60, double.IsNaN(_stats.Max) ? 60 : _stats.Max);
        Bars.Clear();
        foreach (var x in _stats.Samples)
            Bars.Add(new PingBar
            {
                Height = x.Lost ? 48 : Math.Max(2, Math.Min(48, x.Ms / cap * 48)),
                Tone = x.Lost ? Palette.Bad : x.Ms >= _stats.Average * 2 && x.Ms - _stats.Average > 20 ? Palette.Warn : Palette.Good,
                Tip = x.Lost ? Loc.T("потеря") : $"{x.Ms:0} {Loc.T("мс")}",
            });
    }

    // ---------------- Маршрут ----------------
    public ObservableCollection<HopRow> Hops { get; } = new();
    [ObservableProperty] private string _routeTarget = "";
    [ObservableProperty] private string? _routeSummary;
    [ObservableProperty] private string _routeTone = Palette.Neutral;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunRouteAsync()
    {
        var host = string.IsNullOrWhiteSpace(RouteTarget) ? NetworkTest.DefaultTarget : RouteTarget.Trim();
        // Адрес вводит человек и уходит в трассировку — только имя хоста или IP, без лишнего
        if (!ValidHost(host))
        { Main.ShowMessage(Loc.T("Введите имя сервера или IP-адрес."), error: true); return; }
        IsBusy = true; Hops.Clear(); RouteSummary = null;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<RouteHop>(h => { if (IsBusy) ProgressText = Loc.T($"Узел {h.Ttl}…"); });
            ShowRoute(await Main.Services.TraceRouteAsync(host, progress, _cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void ShowRoute(IReadOnlyList<RouteHop> hops)
    {
        Hops.Clear();
        foreach (var h in hops)
            Hops.Add(new HopRow
            {
                Ttl = h.Ttl.ToString(), Address = h.Responds ? h.Address! : Loc.T("нет ответа (узел не отвечает на служебные пакеты)"),
                MedianText = h.Responds ? $"{h.MedianMs:0.#} {Loc.T("мс")}" : "—", LossText = h.LossPercent > 0 && h.Responds ? $"{h.LossPercent:0} %" : "",
                Tone = !h.Responds ? Palette.Neutral : h.LossPercent >= 34 ? Palette.Warn : Palette.Good,
            });
        var v = RouteTrace.Analyze(hops);
        RouteSummary = v.Summary;
        RouteTone = v.Tone;
    }

    /// <summary>Тон по задержке до региона: до 70 мс — хорошо, дальше — нейтрально (это география, а не поломка).</summary>
    public static string RttTone(double ms) => double.IsNaN(ms) ? Palette.Bad : ms < 70 ? Palette.Good : Palette.Neutral;

    /// <summary>Тон по времени ответа DNS: до 30 мс — хорошо.</summary>
    public static string DnsTone(double ms) => double.IsNaN(ms) ? Palette.Bad : ms < 30 ? Palette.Good : Palette.Neutral;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunRegionsAsync()
    {
        IsBusy = true; Regions.Clear(); RegionsSummary = null;
        _cts = new CancellationTokenSource();
        try
        {
            var done = new List<ProbeResult>();
            var progress = new Progress<ProbeResult>(r => { done.Add(r); if (IsBusy) ProgressText = Loc.T($"Проверено регионов: {done.Count} из {ConnectionProbe.Regions.Count}"); });
            var all = await Main.Services.ProbeRegionsAsync(progress, _cts.Token);
            ShowRegions(all);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void ShowRegions(IReadOnlyList<ProbeResult> all)
    {
        Regions.Clear();
        var ok = all.Where(r => r.Reachable).OrderBy(r => r.MedianMs).ToList();
        double worst = ok.Count == 0 ? 1 : Math.Max(1, ok.Max(r => r.MedianMs));
        foreach (var r in ok)
            Regions.Add(new RegionRow
            {
                Name = Loc.T(r.Target.Name), MedianText = $"{r.MedianMs:0} {Loc.T("мс")}", Tone = RttTone(r.MedianMs), Bar = r.MedianMs / worst, IsBest = r == ok[0], Host = r.Target.Host,
                DetailText = Loc.T($"разброс {r.JitterMs:0.#} мс · потери {r.LossPercent:0} %"),
            });
        foreach (var r in all.Where(r => !r.Reachable))
            Regions.Add(new RegionRow { Name = Loc.T(r.Target.Name), MedianText = Loc.T("нет ответа"), DetailText = "", Tone = Palette.Bad, Bar = 0 });
        RegionsSummary = Describe(ok);
        if (ok.Count > 0 && string.IsNullOrWhiteSpace(RouteTarget)) RouteTarget = ok[0].Target.Host;
    }

    /// <summary>Вывод простым языком: где ближе всего и есть ли нестабильность.</summary>
    public static string Describe(IReadOnlyList<ProbeResult> ok) => ConnectionAdvice.Describe(ok);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunDnsAsync()
    {
        IsBusy = true; DnsRows.Clear(); DnsSummary = null;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DnsResult>(r => { if (IsBusy) ProgressText = Loc.T($"Проверен DNS: {r.Resolver.Name}"); });
            var all = await Main.Services.BenchmarkDnsAsync(progress, _cts.Token);
            ShowDns(all);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.ShowMessage(Loc.T("Проверка не удалась: ") + ex.Message, error: true); }
        finally { IsBusy = false; ProgressText = null; }
    }

    public void ShowDns(IReadOnlyList<DnsResult> all)
    {
        DnsRows.Clear();
        var working = all.Where(r => r.Works).OrderBy(r => r.MedianMs).ToList();
        var best = working.FirstOrDefault();
        var current = all.Where(r => r.Resolver.Id.StartsWith("current:", StringComparison.Ordinal)).ToList();
        foreach (var r in working)
            DnsRows.Add(new DnsRow
            {
                Name = Loc.T(r.Resolver.Name), MedianText = $"{r.MedianMs:0} {Loc.T("мс")}", Tone = DnsTone(r.MedianMs),
                Servers = r.Resolver.Id.StartsWith("current:", StringComparison.Ordinal) ? "" : PairFor(r.Resolver.Address),
                IsCurrent = r.Resolver.Id.StartsWith("current:", StringComparison.Ordinal), IsBest = r == best,
            });
        foreach (var r in all.Where(r => !r.Works))
            DnsRows.Add(new DnsRow { Name = Loc.T(r.Resolver.Name), MedianText = Loc.T("нет ответа"), Tone = Palette.Bad, Servers = "" });
        var cur = current.Where(r => r.Works).OrderBy(r => r.MedianMs).FirstOrDefault();
        DnsSummary = best is null ? Loc.T("DNS не ответил — проверьте подключение.")
            : cur is null ? Loc.T($"Самый быстрый: {Loc.T(best.Resolver.Name)} — {best.MedianMs:0} мс.")
            : cur.MedianMs <= best.MedianMs * 1.3 || cur.MedianMs - best.MedianMs < 8
                ? Loc.T($"Ваш DNS ({cur.MedianMs:0} мс) уже достаточно быстрый — менять не нужно.")
                : Loc.T($"Ваш DNS отвечает за {cur.MedianMs:0} мс, {Loc.T(best.Resolver.Name)} — за {best.MedianMs:0} мс. Быстрый DNS ускорит вход в игру и поиск матча (на пинг идущего матча он не влияет).");
    }

    /// <summary>Основной и запасной адреса одного сервиса.</summary>
    public static string PairFor(string address) => address switch
    {
        "1.1.1.1" => "1.1.1.1,1.0.0.1",
        "8.8.8.8" => "8.8.8.8,8.8.4.4",
        "9.9.9.9" => "9.9.9.9,149.112.112.112",
        "94.140.14.14" => "94.140.14.14,94.140.15.15",
        _ => address,
    };

    [RelayCommand]
    private async Task ApplyDnsAsync(DnsRow row)
    {
        if (!row.CanApply) return;
        if (!await Main.Services.ConfirmAsync(Loc.T("Сменить DNS?"),
                Loc.T($"DNS на активных подключениях станет {row.Servers.Replace(",", ", ")}. Прежние серверы запоминаются — вернуть их можно в «Истории».")))
            return;
        var report = await Main.Services.ApplyDnsAsync(row.Servers);
        if (report is null) { Main.ShowMessage(Loc.T("В этом режиме смена DNS недоступна.")); return; }
        var failed = report.Results.FirstOrDefault(r => r.Outcome == Opti.Fixes.Services.FixOutcome.Failed);
        Main.ShowMessage(failed is null ? Loc.T("DNS изменён.") : Loc.T("Не удалось применить: ") + failed.Error, failed is not null);
        Main.History.Refresh();
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();
}
