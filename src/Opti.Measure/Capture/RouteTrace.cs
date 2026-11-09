using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

/// <summary>Один шаг маршрута. Address — null, если узел не отвечает на служебные пакеты (так закрыто у многих провайдеров).</summary>
public sealed record RouteHop(int Ttl, string? Address, double MedianMs, double LossPercent, int Probes)
{
    public bool Responds => Address is not null && !double.IsNaN(MedianMs);
}

/// <summary>Вывод по маршруту: где начинается проблема.</summary>
public sealed record RouteVerdict(string Summary, int? ProblemHop, string Tone);

/// <summary>
/// Трассировка маршрута до сервера (как tracert / WinMTR): задержка и потери на каждом узле. Показывает, где теряется время —
/// в домашней сети, у провайдера или на дальнем участке. Именно эту картину вам показывают «пинг-редьюсеры» перед тем, как предложить свой маршрут.
/// Узлы, не отвечающие на ICMP, — не авария: важна задержка и потери на конечном узле и первые шаги.
/// </summary>
public static class RouteTrace
{
    public static async Task<List<RouteHop>> RunAsync(string host, int maxHops = 24, int probesPerHop = 3, int timeoutMs = 1000,
        IProgress<RouteHop>? progress = null, CancellationToken ct = default)
    {
        var addrs = await Dns.GetHostAddressesAsync(host, ct);
        var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.First();
        var hops = new List<RouteHop>();
        using var ping = new Ping();
        var buffer = new byte[32];
        int silent = 0;
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            var rtts = new List<double>();
            string? addr = null; int lost = 0; bool reached = false;
            for (int i = 0; i < probesPerHop; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sw = Stopwatch.StartNew();
                    var r = await ping.SendPingAsync(ip, timeoutMs, buffer, new PingOptions(ttl, dontFragment: true));
                    if (r.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        addr ??= r.Address?.ToString();
                        rtts.Add(r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds);
                        if (r.Status == IPStatus.Success) reached = true;
                    }
                    else lost++;
                }
                catch (PingException) { lost++; }
            }
            var hop = new RouteHop(ttl, addr, rtts.Count == 0 ? double.NaN : ConnectionProbe.Median(rtts), 100.0 * lost / probesPerHop, probesPerHop);
            hops.Add(hop);
            progress?.Report(hop);
            silent = hop.Responds ? 0 : silent + 1;
            if (reached || silent >= 5) break;
        }
        return hops;
    }

    /// <summary>
    /// Эвристики (все пороги осторожные — трассировка шумная):
    ///  1) первый узел (роутер) медленнее 15 мс или теряет пакеты — проблема дома (Wi-Fi, кабель, роутер);
    ///  2) на конечном узле потери — проблема на пути или у сервера;
    ///  3) самый большой скачок между отвечающими узлами ≥ 40 мс и ≥ 2× — участок, где теряется время (часто — выход на дальний регион или стык провайдеров).
    /// </summary>
    public static RouteVerdict Analyze(IReadOnlyList<RouteHop> hops)
    {
        var responding = hops.Where(h => h.Responds).ToList();
        if (responding.Count == 0) return new RouteVerdict(Loc.T("Ни один узел не ответил — проверьте подключение или попробуйте другой сервер."), null, "bad");
        var first = hops[0];
        if (first.Responds && (first.MedianMs > 15 || first.LossPercent >= 34))
            return new RouteVerdict(Loc.T($"Проблема дома: до роутера {first.MedianMs:0} мс, потери {first.LossPercent:0} %. Проверьте Wi-Fi (лучше кабель) и сам роутер."), 1, "bad");
        var last = responding[^1];
        bool reached = hops.Count > 0 && hops[^1].Responds && hops[^1] == last;
        if (reached && last.LossPercent >= 34)
            return new RouteVerdict(Loc.T($"Потери на конечном узле ({last.LossPercent:0} %) — пакеты теряются по пути или у сервера."), last.Ttl, "warn");
        RouteHop? jumpAt = null; double bestJump = 0;
        for (int i = 1; i < responding.Count; i++)
        {
            double jump = responding[i].MedianMs - responding[i - 1].MedianMs;
            if (jump > bestJump && jump >= 40 && responding[i].MedianMs >= 2 * Math.Max(1, responding[i - 1].MedianMs)) { bestJump = jump; jumpAt = responding[i]; }
        }
        if (jumpAt is not null)
            return new RouteVerdict(Loc.T($"Главный скачок задержки (+{bestJump:0} мс) — на узле {jumpAt.Ttl} ({jumpAt.Address}). Если дальше всё стабильно, это просто дальний участок; если растут потери — стык провайдеров."), jumpAt.Ttl, "warn");
        return new RouteVerdict(Loc.T($"Маршрут чистый: {last.MedianMs:0} мс до конца, без заметных скачков и потерь."), null, "good");
    }
}
