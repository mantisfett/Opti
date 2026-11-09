using Opti.Core.Localization;

namespace Opti.Measure.Capture;

/// <param name="SentBytes">Отдано за время замера.</param>
public sealed record NetUsageRow(string Process, long SentBytes, long ReceivedBytes, double Seconds)
{
    public double SentKbps => Seconds <= 0 ? 0 : SentBytes / Seconds / 1024.0;
    public double ReceivedKbps => Seconds <= 0 ? 0 : ReceivedBytes / Seconds / 1024.0;
    public double TotalKbps => SentKbps + ReceivedKbps;
}

/// <summary>Складывает объём сетевого трафика по программам (все процессы одной программы — вместе).</summary>
public sealed class NetUsageAggregator
{
    private readonly Dictionary<string, (long Sent, long Received)> _by = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Add(string process, long bytes, bool sent)
    {
        if (bytes <= 0) return;
        var name = string.IsNullOrWhiteSpace(process) ? Loc.T("неизвестный процесс") : process.Trim();
        lock (_lock)
        {
            _by.TryGetValue(name, out var v);
            _by[name] = sent ? (v.Sent + bytes, v.Received) : (v.Sent, v.Received + bytes);
        }
    }

    public List<NetUsageRow> Rows(double seconds, int top = 15)
    {
        lock (_lock)
            return _by.Select(kv => new NetUsageRow(kv.Key, kv.Value.Sent, kv.Value.Received, seconds))
                .OrderByDescending(r => r.TotalKbps).Take(top).ToList();
    }
}
