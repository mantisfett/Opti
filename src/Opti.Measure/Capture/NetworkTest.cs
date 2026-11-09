using System.Diagnostics;
using System.Net.NetworkInformation;
using Opti.Core.Profile;

namespace Opti.Measure.Capture;

/// <summary>Пинг и джиттер в простое и под нагрузкой (скачивание) — для выявления bufferbloat.</summary>
public static class NetworkTest
{
    public const string DefaultTarget = "1.1.1.1";
    public const string DefaultLoadUrl = "https://speed.cloudflare.com/__down?bytes=200000000";

    public static async Task<NetworkQuality> RunAsync(string target = DefaultTarget, string? loadUrl = DefaultLoadUrl,
        int pings = 30, CancellationToken ct = default)
    {
        var idle = await PingSeriesAsync(target, pings, ct);
        double? loaded = null;
        if (loadUrl is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var load = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DownloadAsync(loadUrl, cts.Token)));
            await Task.Delay(2000, ct); // дать очереди наполниться
            var underLoad = await PingSeriesAsync(target, pings, ct);
            cts.Cancel();
            try { await load; } catch { /* отмена загрузки */ }
            loaded = underLoad.Ok.Count > 0 ? underLoad.Ok.Average() : null;
        }
        return new NetworkQuality
        {
            Target = target,
            IdlePingMs = idle.Ok.Count > 0 ? idle.Ok.Average() : double.NaN,
            IdleJitterMs = Jitter(idle.Ok),
            LoadedPingMs = loaded,
            PacketLossPercent = 100.0 * idle.Lost / Math.Max(1, pings),
        };
    }

    internal static async Task<(List<double> Ok, int Lost)> PingSeriesAsync(string target, int count, CancellationToken ct)
    {
        using var ping = new Ping();
        var ok = new List<double>();
        int lost = 0;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await ping.SendPingAsync(target, 1000);
                if (r.Status == IPStatus.Success) ok.Add(r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds);
                else lost++;
            }
            catch (PingException) { lost++; }
            await Task.Delay(200, ct);
        }
        return (ok, lost);
    }

    private static async Task DownloadAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[1 << 16];
        while (await s.ReadAsync(buf, ct) > 0) { }
    }

    /// <summary>Джиттер — среднее абсолютное изменение между соседними пингами (RFC 3550 в упрощённом виде).</summary>
    public static double Jitter(IReadOnlyList<double> rtts)
    {
        if (rtts.Count < 2) return 0;
        double sum = 0;
        for (int i = 1; i < rtts.Count; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
        return sum / (rtts.Count - 1);
    }
}
