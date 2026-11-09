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

/// <summary>Страница «Сеть»: куда связь быстрее и стабильнее, какой DNS быстрее. Только чтение; DNS меняется по кнопке, с откатом.</summary>
public sealed partial class NetworkViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    public NetworkViewModel(MainViewModel main) : base(main) { }
    public override string Title => Loc.T("Сеть");

    public ObservableCollection<RegionRow> Regions { get; } = new();
    public ObservableCollection<DnsRow> DnsRows { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunRegionsCommand), nameof(RunDnsCommand), nameof(RunRouteCommand))] private bool _isBusy;
    [ObservableProperty] private string? _regionsSummary;
    [ObservableProperty] private string? _dnsSummary;
    [ObservableProperty] private string? _progressText;

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
        if (host.Length > 253 || host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or ':')))
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
