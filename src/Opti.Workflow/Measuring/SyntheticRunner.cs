using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Opti.Core.Json;
using Opti.Measure.Stats;
using Opti.Core.Localization;

namespace Opti.Workflow.Measuring;

/// <summary>Синтетический тест Opti (D3D11, встроен в программу): одна и та же сцена и длительность — полная воспроизводимость.</summary>
[SupportedOSPlatform("windows")]
public sealed class SyntheticRunner : IMeasureRunner
{
    private readonly string _exe;
    private readonly string _prefixArgs;
    /// <param name="exe">Программа, которая умеет запускать тест (Opti.exe или opti-cli.exe).</param>
    /// <param name="prefixArgs">Ключ запуска теста.</param>
    public SyntheticRunner(string exe, string prefixArgs = "--bench")
    {
        _exe = exe;
        _prefixArgs = prefixArgs;
    }
    public string Name => Loc.T("Синтетический тест Opti");

    public int Instances { get; init; } = 8192;
    public double GpuLoad { get; init; } = 24;
    public double CpuLoadMs { get; init; } = 1.0;

    /// <summary>Тест встроен в сам Opti.exe / opti-cli.exe: запускаем текущую программу с ключом --bench.</summary>
    public static string? Locate() => Environment.ProcessPath is { } p && File.Exists(p) ? p : null;

    public async Task<MeasurementRun> RunAsync(MeasureRequest req, IProgress<MeasureProgress>? progress = null, CancellationToken ct = default)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"opti-bench-{Guid.NewGuid():N}.json");
        var args = _prefixArgs + " " + string.Format(CultureInfo.InvariantCulture,
            "--out \"{0}\" --duration {1} --warmup {2} --instances {3} --gpu-load {4} --cpu-load {5} --label \"{6}\"",
            outFile, req.DurationSec, req.WarmupSec, Instances, GpuLoad, CpuLoadMs, req.Label.Replace("\"", "'"));
        var probe = new ThrottleProbe();
        using var p = Process.Start(new ProcessStartInfo(_exe, args) { UseShellExecute = false })
                      ?? throw new InvalidOperationException(Loc.T("Не удалось запустить Opti.Bench"));
        var sw = Stopwatch.StartNew();
        double total = req.DurationSec + req.WarmupSec;
        int tick = 0;
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested) { try { p.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); }
            if (sw.Elapsed.TotalSeconds > total + 60) { try { p.Kill(true); } catch { } throw new TimeoutException(Loc.T("Синтетический тест завис.")); }
            await Task.Delay(500, CancellationToken.None);
            if (++tick % 10 == 0) probe.SampleInBackground();
            progress?.Report(new MeasureProgress(Math.Min(sw.Elapsed.TotalSeconds, total), total, Array.Empty<double>(), "Opti.Bench", sw.Elapsed.TotalSeconds < req.WarmupSec));
        }
        if (p.ExitCode == 2) throw new OperationCanceledException(Loc.T("Синтетический тест прерван клавишей Esc."));
        if (p.ExitCode != 0 || !File.Exists(outFile))
        {
            var err = File.Exists(outFile + ".error.txt") ? File.ReadAllText(outFile + ".error.txt") : Loc.T($"код {p.ExitCode}");
            throw new InvalidOperationException(Loc.T("Синтетический тест завершился с ошибкой: ") + err.Split('\n')[0]);
        }
        var run = OptiJson.Deserialize<MeasurementRun>(File.ReadAllText(outFile));
        try { File.Delete(outFile); } catch { }
        run.ThermalThrottled |= probe.Throttled;
        if (probe.Reason is not null) run.Warnings.Add(probe.Reason);
        return run;
    }
}

public static class MeasureRunnerFactory
{
    /// <summary>Подбирает исполнителя замера по режиму сессии.</summary>
    [SupportedOSPlatform("windows")]
    public static IMeasureRunner Create(Sessions.MeasureMode mode, OptiSettings settings) => mode switch
    {
        Sessions.MeasureMode.Synthetic => new SyntheticRunner(SyntheticRunner.Locate()
            ?? throw new FileNotFoundException(Loc.T("Не удалось определить путь к программе для запуска теста."))),
        Sessions.MeasureMode.PresentMon => new PresentMonRunner(PresentMonRunner.Locate(settings.PresentMonPath)
            ?? throw new FileNotFoundException(Loc.T("PresentMon не найден. Скачайте PresentMon (https://github.com/GameTechDev/PresentMon/releases), положите рядом с Opti или укажите путь в настройках."))),
        _ => new EtwGameRunner(),
    };
}
