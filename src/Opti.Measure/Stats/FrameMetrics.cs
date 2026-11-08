using Opti.Core.Localization;
namespace Opti.Measure.Stats;

public enum MetricId { AvgFps, Low1Fps, Low01Fps, FrameTimeMedian, FrameTimeP95, FrameTimeP99, FrameTimeStdDev }

public sealed record MetricDef(MetricId Id, string Name, string Unit, bool HigherIsBetter);

public static class Metrics
{
    public static readonly IReadOnlyList<MetricDef> All = new[]
    {
        new MetricDef(MetricId.AvgFps, Loc.T("Средний FPS"), "fps", true),
        new MetricDef(MetricId.Low1Fps, "1 % low", "fps", true),
        new MetricDef(MetricId.Low01Fps, "0,1 % low", "fps", true),
        new MetricDef(MetricId.FrameTimeMedian, Loc.T("Фреймтайм, медиана"), Loc.T("мс"), false),
        new MetricDef(MetricId.FrameTimeP95, Loc.T("Фреймтайм p95"), Loc.T("мс"), false),
        new MetricDef(MetricId.FrameTimeP99, Loc.T("Фреймтайм p99"), Loc.T("мс"), false),
        new MetricDef(MetricId.FrameTimeStdDev, Loc.T("Фреймтайм, ст. отклонение"), Loc.T("мс"), false),
    };

    public static MetricDef Def(MetricId id) => All.First(m => m.Id == id);

    /// <summary>
    /// Вычисляет метрику по фреймтаймам (мс).
    /// 1 % / 0,1 % low — FPS, соответствующий СРЕДНЕМУ из худших 1 % / 0,1 % кадров (как в CapFrameX «1% low average»).
    /// </summary>
    public static double Compute(MetricId id, IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0) return double.NaN;
        return id switch
        {
            MetricId.AvgFps => 1000.0 * frameTimesMs.Count / frameTimesMs.Sum(),
            MetricId.Low1Fps => 1000.0 / WorstMean(frameTimesMs, 0.01),
            MetricId.Low01Fps => 1000.0 / WorstMean(frameTimesMs, 0.001),
            MetricId.FrameTimeMedian => Percentile(frameTimesMs, 50),
            MetricId.FrameTimeP95 => Percentile(frameTimesMs, 95),
            MetricId.FrameTimeP99 => Percentile(frameTimesMs, 99),
            MetricId.FrameTimeStdDev => StdDev(frameTimesMs),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    public static Dictionary<MetricId, double> ComputeAll(IReadOnlyList<double> frameTimesMs)
    {
        var sorted = frameTimesMs.ToArray();
        Array.Sort(sorted);
        return ComputeAllSorted(sorted);
    }

    /// <summary>Все метрики по уже отсортированному по возрастанию массиву — одна сортировка вместо семи (для бутстрепа).</summary>
    public static Dictionary<MetricId, double> ComputeAllSorted(double[] sorted)
    {
        var r = new Dictionary<MetricId, double>(All.Count);
        if (sorted.Length == 0) { foreach (var m in All) r[m.Id] = double.NaN; return r; }
        double sum = 0, sumSq = 0;
        foreach (var v in sorted) { sum += v; sumSq += v * v; }
        int n = sorted.Length;
        double WorstMeanSorted(double fraction)
        {
            int k = Math.Max(1, (int)Math.Ceiling(n * fraction));
            double s = 0;
            for (int i = n - k; i < n; i++) s += sorted[i];
            return s / k;
        }
        r[MetricId.AvgFps] = 1000.0 * n / sum;
        r[MetricId.Low1Fps] = 1000.0 / WorstMeanSorted(0.01);
        r[MetricId.Low01Fps] = 1000.0 / WorstMeanSorted(0.001);
        r[MetricId.FrameTimeMedian] = PercentileSorted(sorted, 50);
        r[MetricId.FrameTimeP95] = PercentileSorted(sorted, 95);
        r[MetricId.FrameTimeP99] = PercentileSorted(sorted, 99);
        r[MetricId.FrameTimeStdDev] = n < 2 ? 0 : Math.Sqrt(Math.Max(0, (sumSq - sum * sum / n) / (n - 1)));
        return r;
    }

    public static double PercentileSorted(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double WorstMean(IReadOnlyList<double> values, double fraction)
    {
        int n = Math.Max(1, (int)Math.Ceiling(values.Count * fraction));
        return values.OrderByDescending(v => v).Take(n).Average();
    }

    /// <summary>Перцентиль с линейной интерполяцией.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}
