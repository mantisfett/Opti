using System.Diagnostics;
using System.Runtime.Versioning;
using Opti.Measure.Capture;
using Opti.Measure.Stats;
using Opti.Core.Localization;

namespace Opti.Workflow.Measuring;

/// <summary>
/// Замер через PresentMon (MIT, Intel/GameTechDev) как вспомогательную программу:
/// дополнительно даёт режим вывода (Independent Flip / Composed) и оценку задержки.
/// Ищется по пути из настроек или рядом с Opti (PresentMon*.exe, tools\PresentMon*.exe).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PresentMonRunner : IMeasureRunner
{
    private readonly string _exe;
    public PresentMonRunner(string exe) => _exe = exe;
    public string Name => "PresentMon";

    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "tools") })
        {
            if (!Directory.Exists(dir)) continue;
            var found = Directory.EnumerateFiles(dir, "PresentMon*.exe").OrderByDescending(f => f).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    public async Task<MeasurementRun> RunAsync(MeasureRequest req, IProgress<MeasureProgress>? progress = null, CancellationToken ct = default)
    {
        var csv = Path.Combine(Path.GetTempPath(), $"opti-pm-{Guid.NewGuid():N}.csv");
        int total = req.DurationSec + req.WarmupSec;
        var proc = req.ProcessName is null ? "" : $"--process_name \"{Path.GetFileName(req.ProcessName)}\" ";
        // Синтаксис PresentMon 2.x; при ошибке — 1.x (одинарный дефис).
        var args2 = $"{proc}--output_file \"{csv}\" --timed {total} --terminate_after_timed --no_console_stats --stop_existing_session --session_name Opti_PM";
        var args1 = (req.ProcessName is null ? "" : $"-process_name \"{Path.GetFileName(req.ProcessName)}\" ") +
                    $"-output_file \"{csv}\" -timed {total} -terminate_after_timed -no_top -stop_existing_session -session_name Opti_PM";
        var probe = new ThrottleProbe();

        int exit = await RunPm(args2, total, req, progress, probe, ct);
        if (exit != 0 && !File.Exists(csv)) exit = await RunPm(args1, total, req, progress, probe, ct);
        if (!File.Exists(csv)) throw new InvalidOperationException(Loc.T($"PresentMon не создал файл замера (код {exit}). Запущена ли игра и есть ли права администратора?"));

        List<PresentMonProcessData> data;
        using (var reader = new StreamReader(csv)) data = PresentMonCsv.Parse(reader);
        try { File.Delete(csv); } catch { }

        var ignored = new[] { "dwm.exe", "explorer.exe", "obs64.exe", "Discord.exe", "steamwebhelper.exe", "chrome.exe", "msedge.exe", "firefox.exe" };
        var game = data.FirstOrDefault(d => !ignored.Contains(d.Application, StringComparer.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(Loc.T("PresentMon не увидел ни одного процесса игры."));
        var run = new MeasurementRun
        {
            Label = req.Label,
            FrameTimesMs = MeasurementRun.TrimWarmup(game.FrameTimesMs, req.WarmupSec),
            Source = "PresentMon",
            PresentModes = game.PresentModes,
            IndependentFlipShare = game.IndependentFlipShare,
            AvgPcLatencyMs = game.PcLatencyMs.Count > 0 ? game.PcLatencyMs.Average() : null,
            AvgDisplayLatencyMs = game.DisplayLatencyMs.Count > 0 ? game.DisplayLatencyMs.Average() : null,
            ThermalThrottled = probe.Throttled,
            Conditions = MeasureConditions.Capture(Path.GetFileNameWithoutExtension(game.Application), req.Scenario),
        };
        if (probe.Reason is not null) run.Warnings.Add(probe.Reason);
        return run;
    }

    private async Task<int> RunPm(string args, int total, MeasureRequest req, IProgress<MeasureProgress>? progress, ThrottleProbe probe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("Не удалось запустить PresentMon"));
        // Вывод только вычитывается, чтобы PresentMon не завис на полном буфере; отмена — через Kill ниже.
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        int tick = 0;
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested) { try { p.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); }
            if (sw.Elapsed.TotalSeconds > total + 30) { try { p.Kill(true); } catch { } break; }
            await Task.Delay(500, CancellationToken.None);
            if (++tick % 10 == 0) probe.SampleInBackground();
            progress?.Report(new MeasureProgress(Math.Min(sw.Elapsed.TotalSeconds, total), total, Array.Empty<double>(), req.ProcessName, sw.Elapsed.TotalSeconds < req.WarmupSec));
            if (sw.Elapsed.TotalSeconds < 3 && p.HasExited) break; // мгновенный выход — неверные аргументы
        }
        return p.HasExited ? p.ExitCode : -1;
    }
}
