using System.Runtime.Versioning;
using Opti.Measure.Capture;
using Opti.Measure.Stats;
using Opti.Core.Localization;

namespace Opti.Workflow.Measuring;

/// <summary>Замер реальной игры: ETW-события Present (как MsBetweenPresents в PresentMon).</summary>
[SupportedOSPlatform("windows")]
public sealed class EtwGameRunner : IMeasureRunner
{
    public string Name => Loc.T("Реальная игра (ETW)");

    public async Task<MeasurementRun> RunAsync(MeasureRequest req, IProgress<MeasureProgress>? progress = null, CancellationToken ct = default)
    {
        using var cap = new EtwPresentCapture();
        cap.Start();
        var probe = new ThrottleProbe();
        var total = req.DurationSec + req.WarmupSec;
        var started = DateTime.UtcNow;
        try
        {
            int tick = 0;
            while ((DateTime.UtcNow - started).TotalSeconds < total)
            {
                await Task.Delay(500, ct);
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (++tick % 10 == 0) probe.SampleInBackground();
                if (progress is not null)
                {
                    var guess = cap.GuessGameProcess(req.ProcessName);
                    var recent = guess is null ? new List<double>() : MeasurementRun.FromTimestamps(cap.GetTimestamps(guess.Pid).TakeLast(600).ToList());
                    progress.Report(new MeasureProgress(elapsed, total, recent, guess?.Name, elapsed < req.WarmupSec));
                }
            }
        }
        finally { cap.Stop(); }

        var proc = cap.GuessGameProcess(req.ProcessName)
                   ?? throw new InvalidOperationException(Loc.T("Не найдено ни одного процесса, выводящего кадры. Игра запущена и находится на экране?"));
        var frames = MeasurementRun.TrimWarmup(MeasurementRun.FromTimestamps(cap.GetTimestamps(proc.Pid)), req.WarmupSec);
        var run = new MeasurementRun
        {
            Label = req.Label,
            FrameTimesMs = frames,
            ThermalThrottled = probe.Throttled,
            Conditions = MeasureConditions.Capture(proc.Name, req.Scenario),
            Source = "ETW",
        };
        if (probe.Reason is not null) run.Warnings.Add(probe.Reason);
        return run;
    }
}
