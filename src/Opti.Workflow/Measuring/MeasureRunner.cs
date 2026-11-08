using System.Runtime.Versioning;
using Opti.Measure.Stats;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Workflow.Measuring;

public sealed record MeasureRequest(string Label, int DurationSec, int WarmupSec, string? ProcessName = null, string Scenario = "игра");

/// <summary>Ход замера: для живого графика фреймтайма.</summary>
public sealed record MeasureProgress(double ElapsedSec, double TotalSec, IReadOnlyList<double> RecentFrameTimesMs, string? Process, bool Warmup);

public interface IMeasureRunner
{
    string Name { get; }
    Task<MeasurementRun> RunAsync(MeasureRequest request, IProgress<MeasureProgress>? progress = null, CancellationToken ct = default);
}

[SupportedOSPlatform("windows")]
public static class MeasureConditions
{
    public static RunConditions Capture(string? process, string scenario)
    {
        var primary = DisplayNative.GetDisplays().FirstOrDefault(d => d.IsPrimary);
        var mode = primary is null ? null : DisplayNative.GetCurrentMode(primary.DeviceName);
        string? scheme = null;
        try
        {
            var r = ProcessRunner.Instance.RunConsoleUtf8("powercfg /getactivescheme");
            var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, "[0-9a-fA-F-]{36}");
            if (m.Success) scheme = m.Value.ToLowerInvariant();
        }
        catch { /* необязательно */ }
        return new RunConditions
        {
            ProcessName = process, Scenario = scenario,
            Width = mode?.Width, Height = mode?.Height, RefreshHz = mode?.Hz, DisplayDevice = primary?.DeviceName,
            OnAcPower = PowerNative.GetStatus().OnAc, PowerScheme = scheme,
        };
    }
}

/// <summary>Тепловой троттлинг во время замера. NVIDIA — через nvidia-smi; иначе — падение частоты CPU ниже 75 % от номинала.</summary>
[SupportedOSPlatform("windows")]
public sealed class ThrottleProbe
{
    private volatile bool _seen;
    private int _cpuLowStreak;
    private int _busy;

    /// <summary>Неблокирующий вызов: проверка идёт в фоне, чтобы не мешать замеру и интерфейсу.</summary>
    public void SampleInBackground()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(() => { try { Sample(); } finally { Volatile.Write(ref _busy, 0); } });
    }
    public bool Throttled => _seen;
    public string? Reason { get; private set; }

    public void Sample()
    {
        try
        {
            var r = ProcessRunner.Instance.Run("nvidia-smi",
                "--query-gpu=clocks_throttle_reasons.hw_thermal_slowdown,clocks_throttle_reasons.sw_thermal_slowdown --format=csv,noheader", TimeSpan.FromSeconds(5));
            if (r.Ok && ToolParsers.ParseNvidiaThrottleActive(r.StdOut)) { _seen = true; Reason = Loc.T("тепловой троттлинг GPU (nvidia-smi)"); }
        }
        catch { /* nvidia-smi нет */ }
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT PercentProcessorPerformance, PercentProcessorUtility FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
            foreach (var o in s.Get())
            {
                var perf = Convert.ToDouble(o["PercentProcessorPerformance"]);
                var util = Convert.ToDouble(o["PercentProcessorUtility"]);
                // Падение частоты под нагрузкой должно повториться дважды подряд — одиночный замер счётчика шумный
                if (util > 70 && perf > 0 && perf < 60)
                {
                    if (++_cpuLowStreak >= 2) { _seen = true; Reason = Loc.T($"частота CPU {perf:0} % от номинала под нагрузкой"); }
                }
                else _cpuLowStreak = 0;
            }
        }
        catch { /* счётчик недоступен */ }
    }
}
