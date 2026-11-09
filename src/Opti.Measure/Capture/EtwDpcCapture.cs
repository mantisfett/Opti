using System.Runtime.Versioning;
using Opti.Core.Profile;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

/// <summary>
/// Тест задержки системы: длительность DPC и ISR по драйверам (ядерная сессия ETW).
/// Адрес процедуры сопоставляется с загруженным образом драйвера по событиям ImageLoad/DCStart.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EtwDpcCapture
{
    private sealed record Image(ulong Base, ulong Size, string Name);

    private sealed class Acc { public double MaxDpcUs; public double MaxIsrUs; public long Count; }

    public static DpcLatencyInfo Run(TimeSpan duration, CancellationToken ct = default)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
            throw new UnauthorizedAccessException(Loc.T("Для теста DPC/ISR нужны права администратора."));

        using var _ = EtwGate.Enter();
        var images = new List<Image>();
        var byRoutine = new Dictionary<ulong, Acc>();
        var gate = new object();

        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.DeferedProcedureCalls |
            KernelTraceEventParser.Keywords.Interrupt |
            KernelTraceEventParser.Keywords.ImageLoad);

        var kernel = session.Source.Kernel;
        void AddImage(ImageLoadTraceData d)
        {
            if (d.ProcessID is 0 or 4)
                lock (gate) images.Add(new Image(d.ImageBase, (ulong)d.ImageSize, Path.GetFileName(d.FileName)));
        }
        kernel.ImageLoad += AddImage;
        kernel.ImageDCStart += AddImage;
        kernel.PerfInfoDPC += d => Record(d.Routine, d.ElapsedTimeMSec * 1000, isr: false);
        kernel.PerfInfoTimerDPC += d => Record(d.Routine, d.ElapsedTimeMSec * 1000, isr: false);
        kernel.PerfInfoThreadedDPC += d => Record(d.Routine, d.ElapsedTimeMSec * 1000, isr: false);
        kernel.PerfInfoISR += d => Record(d.Routine, d.ElapsedTimeMSec * 1000, isr: true);

        void Record(ulong routine, double us, bool isr)
        {
            if (us < 0 || us > 1_000_000) return;
            lock (gate)
            {
                if (!byRoutine.TryGetValue(routine, out var a)) byRoutine[routine] = a = new Acc();
                a.Count++;
                if (isr) a.MaxIsrUs = Math.Max(a.MaxIsrUs, us); else a.MaxDpcUs = Math.Max(a.MaxDpcUs, us);
            }
        }

        var pump = Task.Run(() => session.Source.Process());
        try { Task.Delay(duration, ct).Wait(ct); } catch (OperationCanceledException) { }
        session.Stop();
        pump.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); // остановка сессии уже запрошена — ждём разбор

        var drivers = new Dictionary<string, DriverLatency>(StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            foreach (var (routine, acc) in byRoutine)
            {
                var img = images.FirstOrDefault(i => routine >= i.Base && routine < i.Base + i.Size);
                var name = img?.Name ?? Loc.T("неизвестный драйвер");
                if (!drivers.TryGetValue(name, out var dl)) drivers[name] = dl = new DriverLatency { Driver = name };
                dl.MaxDpcUs = Math.Max(dl.MaxDpcUs, acc.MaxDpcUs);
                dl.MaxIsrUs = Math.Max(dl.MaxIsrUs, acc.MaxIsrUs);
                dl.DpcCount += acc.Count;
            }
        }
        return new DpcLatencyInfo
        {
            DurationSec = duration.TotalSeconds,
            Drivers = drivers.Values.OrderByDescending(d => Math.Max(d.MaxDpcUs, d.MaxIsrUs)).ToList(),
        };
    }
}
