using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Opti.Measure.Capture;

/// <param name="Ms">Время ответа; NaN — потеря.</param>
public readonly record struct PingSample(double Ms)
{
    public bool Lost => double.IsNaN(Ms);
}

/// <summary>Скользящая статистика пинга: последнее, среднее, минимум, максимум, джиттер, потери по последним N замерам.</summary>
public sealed class PingStats
{
    private readonly int _window;
    private readonly Queue<PingSample> _samples = new();
    public PingStats(int window = 120) => _window = Math.Max(2, window);

    public void Clear() => _samples.Clear();

    public void Add(PingSample s)
    {
        _samples.Enqueue(s);
        while (_samples.Count > _window) _samples.Dequeue();
    }

    private List<double> Ok => _samples.Where(s => !s.Lost).Select(s => s.Ms).ToList();
    public int Count => _samples.Count;
    public double Last => _samples.Count == 0 ? double.NaN : _samples.Last().Ms;
    public double Average => Ok is { Count: > 0 } ok ? ok.Average() : double.NaN;
    public double Min => Ok is { Count: > 0 } ok ? ok.Min() : double.NaN;
    public double Max => Ok is { Count: > 0 } ok ? ok.Max() : double.NaN;
    public double Jitter => NetworkTest.Jitter(Ok);
    public double LossPercent => _samples.Count == 0 ? 0 : 100.0 * _samples.Count(s => s.Lost) / _samples.Count;
    public IReadOnlyList<PingSample> Samples => _samples.ToList();
}

/// <summary>Живой пинг до выбранного сервера (ICMP): для проверки «лагает ли сеть прямо сейчас», в том числе во время игры.</summary>
public static class LivePing
{
    public static async Task RunAsync(string target, IProgress<PingSample> progress, int intervalMs = 500, CancellationToken ct = default)
    {
        using var ping = new Ping();
        var sw = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            sw.Restart();
            PingSample sample;
            try
            {
                var r = await ping.SendPingAsync(target, 1000);
                sample = new PingSample(r.Status == IPStatus.Success ? (r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds) : double.NaN);
            }
            catch (PingException) { sample = new PingSample(double.NaN); }
            progress.Report(sample);
            var wait = intervalMs - (int)sw.ElapsedMilliseconds;
            try { if (wait > 0) await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
