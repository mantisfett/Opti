using Opti.Core.Localization;
namespace Opti.Measure.Stats;

public enum Verdict { Better, NoChange, Worse, Insufficient }

public sealed record ConfidenceInterval(double Low, double High)
{
    public bool Overlaps(ConfidenceInterval o) => Low <= o.High && o.Low <= High;
}

public sealed class MetricComparison
{
    public required MetricDef Metric { get; init; }
    public required double Before { get; init; }
    public required double After { get; init; }
    public required ConfidenceInterval BeforeCi { get; init; }
    public required ConfidenceInterval AfterCi { get; init; }
    public required double[] BeforeRuns { get; init; }
    public required double[] AfterRuns { get; init; }
    /// <summary>Разброс между прогонами (максимум размахов «до» и «после»).</summary>
    public required double RunSpread { get; init; }
    /// <summary>Доверительный интервал разности «после − до» (для ключевых метрик — с поправкой на три сравнения).</summary>
    public ConfidenceInterval DiffCi { get; init; } = new(double.NaN, double.NaN);
    public required Verdict Verdict { get; init; }

    public double DeltaPercent => Before == 0 ? 0 : (After - Before) / Before * 100.0;

    public string Format()
    {
        string v = Verdict switch
        {
            Verdict.Better => Loc.T("лучше, достоверно"),
            Verdict.Worse => Loc.T("хуже, достоверно"),
            Verdict.NoChange => Loc.T("в пределах шума"),
            _ => Loc.T("мало данных"),
        };
        return $"{Metric.Name}: {Before:0.#} → {After:0.#} {Metric.Unit} ({DeltaPercent:+0.#;-0.#;0} %, {v})";
    }
}

public sealed class ComparisonResult
{
    public List<MetricComparison> Metrics { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Reliable { get; set; } = true;

    /// <summary>Общий вердикт: хуже, если хоть одна ключевая метрика достоверно хуже; лучше — если есть улучшение и нет ухудшений.</summary>
    public Verdict Overall
    {
        get
        {
            var key = Metrics.Where(m => Comparer.IsKey(m.Metric.Id)).ToList();
            if (key.Count == 0 || key.All(m => m.Verdict == Verdict.Insufficient)) return Verdict.Insufficient;
            if (key.Any(m => m.Verdict == Verdict.Worse)) return Verdict.Worse;
            if (key.Any(m => m.Verdict == Verdict.Better)) return Verdict.Better;
            return Verdict.NoChange;
        }
    }
}

/// <summary>
/// Честное сравнение «до / после»:
///  • среднее по прогонам и разброс между ними;
///  • 95 % доверительный интервал каждого замера блочным бутстрепом (блоки ≈ 1 с, чтобы учесть автокорреляцию кадров);
///  • доверительный интервал РАЗНОСТИ «после − до» тем же бутстрепом. Для трёх ключевых метрик (средний FPS, 1 % low,
///    p99) уровень повышен до 1 − 0,05/3 (поправка Бонферрони), потому что общий вердикт смотрит на любую из них;
///  • «лучше/хуже» только если интервал разности не содержит ноль И разница больше разброса между прогонами.
/// Раньше требовалось, чтобы не пересекались интервалы самих замеров, — это заметно строже нужного
/// и пропускало реальные улучшения; поправка на число метрик держит ложные «улучшения» под контролем.
/// </summary>
public sealed class Comparer
{
    /// <summary>Метрики общего вердикта.</summary>
    public static bool IsKey(MetricId id) => id is MetricId.AvgFps or MetricId.Low1Fps or MetricId.FrameTimeP99;
    /// <summary>Уровень доверия для ключевых метрик (поправка Бонферрони на три сравнения).</summary>
    public const double KeyConfidence = 1 - 0.05 / 3;

    public int BootstrapIterations { get; init; } = 1000;
    public double BlockMs { get; init; } = 1000;
    public int Seed { get; init; } = 12345;

    public ComparisonResult Compare(IReadOnlyList<MeasurementRun> before, IReadOnlyList<MeasurementRun> after)
    {
        var result = new ComparisonResult();
        result.Warnings.AddRange(CheckConditions(before, after));

        var b = SelectRuns(before, Loc.T("до"), result);
        var a = SelectRuns(after, Loc.T("после"), result);
        if (result.Warnings.Count > 0) result.Reliable = false;

        var bPerRun = b.Select(r => Metrics.ComputeAll(r.FrameTimesMs)).ToList();
        var aPerRun = a.Select(r => Metrics.ComputeAll(r.FrameTimesMs)).ToList();
        var bSamples = BootstrapSamples(b);
        var aSamples = BootstrapSamples(a);
        var bCis = ToCis(bSamples, 0.95);
        var aCis = ToCis(aSamples, 0.95);

        foreach (var def in Metrics.All)
        {
            var bRuns = bPerRun.Select(m => m[def.Id]).ToArray();
            var aRuns = aPerRun.Select(m => m[def.Id]).ToArray();
            if (bRuns.Length == 0 || aRuns.Length == 0 || bRuns.Concat(aRuns).Any(double.IsNaN))
            {
                result.Metrics.Add(new MetricComparison
                {
                    Metric = def, Before = double.NaN, After = double.NaN, BeforeCi = new(double.NaN, double.NaN),
                    AfterCi = new(double.NaN, double.NaN), BeforeRuns = bRuns, AfterRuns = aRuns, RunSpread = double.NaN,
                    Verdict = Verdict.Insufficient,
                });
                continue;
            }

            double bMean = bRuns.Average(), aMean = aRuns.Average();
            var bCi = bCis[def.Id];
            var aCi = aCis[def.Id];
            double spread = Math.Max(Range(bRuns), Range(aRuns));
            double diff = aMean - bMean;
            var diffCi = DiffCi(aSamples[def.Id], bSamples[def.Id], IsKey(def.Id) ? KeyConfidence : 0.95);
            bool significant = (diffCi.Low > 0 || diffCi.High < 0) && Math.Abs(diff) > spread;
            bool improved = def.HigherIsBetter ? diff > 0 : diff < 0;
            var verdict = !significant ? Verdict.NoChange : improved ? Verdict.Better : Verdict.Worse;

            result.Metrics.Add(new MetricComparison
            {
                Metric = def, Before = bMean, After = aMean, BeforeCi = bCi, AfterCi = aCi,
                BeforeRuns = bRuns, AfterRuns = aRuns, RunSpread = spread, DiffCi = diffCi, Verdict = verdict,
            });
        }
        return result;
    }

    private static List<MeasurementRun> SelectRuns(IReadOnlyList<MeasurementRun> runs, string label, ComparisonResult result)
    {
        var reliable = runs.Where(r => r.IsReliable).ToList();
        foreach (var r in runs.Where(r => !r.IsReliable))
            result.Warnings.Add(Loc.T($"Прогон «{r.Label}» ненадёжен: ") + (r.ThermalThrottled ? Loc.T("был троттлинг") : Loc.T("слишком мало кадров")) +
                                (reliable.Count > 0 ? Loc.T(" — исключён из расчёта.") : "."));
        if (runs.Count < 2) result.Warnings.Add(Loc.T($"Для «{label}» меньше двух прогонов — разброс между прогонами не оценить."));
        return reliable.Count > 0 ? reliable : runs.ToList();
    }

    public static IEnumerable<string> CheckConditions(IReadOnlyList<MeasurementRun> before, IReadOnlyList<MeasurementRun> after)
    {
        var all = before.Concat(after).Select(r => r.Conditions).ToList();
        if (all.Count == 0) yield break;
        string? Diff<T>(string name, Func<RunConditions, T?> sel)
        {
            var vals = all.Select(sel).Where(v => v is not null).Distinct().ToList();
            return vals.Count > 1 ? Loc.T($"Разные условия замера: {name} ({string.Join(" / ", vals)})") : null;
        }
        var checks = new[]
        {
            Diff(Loc.T("процесс"), c => c.ProcessName),
            Diff(Loc.T("сценарий"), c => c.Scenario),
            Diff(Loc.T("разрешение"), c => c.Width is null ? null : $"{c.Width}×{c.Height}"),
            Diff(Loc.T("экран"), c => c.DisplayDevice),
            Diff(Loc.T("питание"), c => c.OnAcPower is null ? null : c.OnAcPower.Value ? Loc.T("от сети") : Loc.T("от батареи")),
        };
        foreach (var c in checks) if (c is not null) yield return c;
        if (all.Any(c => c.OnAcPower == false)) yield return Loc.T("Замер шёл от батареи — результаты могут быть занижены и нестабильны.");
    }

    public ConfidenceInterval BootstrapCi(MetricId id, IReadOnlyList<MeasurementRun> runs) => BootstrapAll(runs)[id];

    /// <summary>95 % интервал для каждой метрики.</summary>
    public Dictionary<MetricId, ConfidenceInterval> BootstrapAll(IReadOnlyList<MeasurementRun> runs) => ToCis(BootstrapSamples(runs), 0.95);

    private static Dictionary<MetricId, ConfidenceInterval> ToCis(Dictionary<MetricId, double[]> samples, double level) =>
        samples.ToDictionary(kv => kv.Key, kv => Interval((double[])kv.Value.Clone(), level));

    /// <summary>Интервал разности: выборки «до» и «после» независимы, поэтому i-я пара — бутстреп-выборка разности.</summary>
    private static ConfidenceInterval DiffCi(double[] after, double[] before, double level)
    {
        int n = Math.Min(after.Length, before.Length);
        var d = new double[n];
        for (int i = 0; i < n; i++) d[i] = after[i] - before[i];
        return Interval(d, level);
    }

    private static ConfidenceInterval Interval(double[] values, double level)
    {
        if (values.Any(double.IsNaN)) return new(double.NaN, double.NaN);
        Array.Sort(values);
        double tail = (1 - level) / 2 * 100;
        return new ConfidenceInterval(Metrics.PercentileSorted(values, tail), Metrics.PercentileSorted(values, 100 - tail));
    }

    /// <summary>
    /// Блочный бутстреп по объединённым прогонам: блоки ≈ 1 с выбираются с возвращением, пока не наберётся
    /// исходное число кадров. Детерминирован (фиксированный seed). Возвращает значения метрик по итерациям (не отсортированы).
    /// </summary>
    private Dictionary<MetricId, double[]> BootstrapSamples(IReadOnlyList<MeasurementRun> runs)
    {
        var blocks = runs.SelectMany(r => MakeBlocks(r.FrameTimesMs)).ToList();
        if (blocks.Count < 2)
        {
            var point = Metrics.ComputeAll(runs.SelectMany(r => r.FrameTimesMs).ToList());
            return point.ToDictionary(kv => kv.Key, kv => Enumerable.Repeat(kv.Value, BootstrapIterations).ToArray());
        }
        int totalFrames = blocks.Sum(b => b.Length);
        int maxBlock = blocks.Max(b => b.Length);
        var rng = new Random(Seed);
        var stats = Metrics.All.ToDictionary(m => m.Id, _ => new double[BootstrapIterations]);
        var buffer = new double[totalFrames + maxBlock];
        for (int i = 0; i < BootstrapIterations; i++)
        {
            int count = 0;
            while (count < totalFrames)
            {
                var blk = blocks[rng.Next(blocks.Count)];
                Array.Copy(blk, 0, buffer, count, blk.Length);
                count += blk.Length;
            }
            var sample = buffer.AsSpan(0, count).ToArray();
            Array.Sort(sample);
            foreach (var (id, v) in Metrics.ComputeAllSorted(sample)) stats[id][i] = v;
        }
        return stats;
    }

    private IEnumerable<double[]> MakeBlocks(IReadOnlyList<double> frames)
    {
        var cur = new List<double>();
        double acc = 0;
        foreach (var f in frames)
        {
            cur.Add(f);
            acc += f;
            if (acc >= BlockMs) { yield return cur.ToArray(); cur.Clear(); acc = 0; }
        }
        if (cur.Count > 0) yield return cur.ToArray();
    }

    private static double Range(double[] v) => v.Length < 2 ? 0 : v.Max() - v.Min();
}
