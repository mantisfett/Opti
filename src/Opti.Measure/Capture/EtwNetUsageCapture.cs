using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

/// <summary>Сетевой трафик по программам из событий ядра Windows (TCP и UDP). Читает только размеры пакетов, содержимое не видно.</summary>
[SupportedOSPlatform("windows")]
public static class EtwNetUsageCapture
{
    public static List<NetUsageRow> Run(TimeSpan duration, CancellationToken ct = default)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
            throw new UnauthorizedAccessException(Loc.T("Для замера трафика нужны права администратора."));

        using var _ = EtwGate.Enter();
        var byPid = new Dictionary<int, (string Name, long Sent, long Received)>();
        var gate = new object();

        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        var kernel = session.Source.Kernel;

        void Record(int pid, string? name, long bytes, bool sent)
        {
            if (bytes <= 0) return;
            lock (gate)
            {
                byPid.TryGetValue(pid, out var v);
                byPid[pid] = (string.IsNullOrEmpty(v.Name) ? name ?? "" : v.Name, sent ? v.Sent + bytes : v.Sent, sent ? v.Received : v.Received + bytes);
            }
        }
        kernel.TcpIpSend += d => Record(d.ProcessID, d.ProcessName, d.size, sent: true);
        kernel.TcpIpRecv += d => Record(d.ProcessID, d.ProcessName, d.size, sent: false);
        kernel.UdpIpSend += d => Record(d.ProcessID, d.ProcessName, d.size, sent: true);
        kernel.UdpIpRecv += d => Record(d.ProcessID, d.ProcessName, d.size, sent: false);

        var pump = Task.Run(() => session.Source.Process());
        try { Task.Delay(duration, ct).Wait(ct); } catch (OperationCanceledException) { }
        session.Stop();
        pump.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);

        var agg = new NetUsageAggregator();
        lock (gate)
            foreach (var (pid, v) in byPid)
            {
                var name = v.Name;
                if (string.IsNullOrEmpty(name))
                    try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { /* процесс уже завершился */ }
                agg.Add(name, v.Sent, sent: true);
                agg.Add(name, v.Received, sent: false);
            }
        return agg.Rows(duration.TotalSeconds);
    }
}
