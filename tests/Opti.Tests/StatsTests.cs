using Opti.Measure.Stats;

namespace Opti.Tests;

public class StatsTests
{
    /// <summary>Синтетический прогон: фреймтайм ~ base мс с шумом и редкими провалами (детерминированно).</summary>
    private static MeasurementRun Run(string label, double baseMs, double noise, double spikeMs, int seed, int frames = 6000, bool throttled = false, double spikeRate = 0.02)
    {
        var rng = new Random(seed);
        var list = new List<double>(frames);
        for (int i = 0; i < frames; i++)
        {
            double ft = baseMs + (rng.NextDouble() - 0.5) * 2 * noise;
            if (rng.NextDouble() < spikeRate) ft += spikeMs;
            list.Add(ft);
        }
        return new MeasurementRun { Label = label, FrameTimesMs = list, ThermalThrottled = throttled, Conditions = new RunConditions { ProcessName = "game", Width = 1920, Height = 1080, OnAcPower = true } };
    }

    [Fact]
    public void Metrics_OnKnownData()
    {
        var ft = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToList();
        Assert.Equal(1000.0 * 1000 / (990 * 10 + 10 * 50), Metrics.Compute(MetricId.AvgFps, ft), 6);
        Assert.Equal(20.0, Metrics.Compute(MetricId.Low1Fps, ft), 6);    // худшие 1 % = 10 кадров по 50 мс
        Assert.Equal(20.0, Metrics.Compute(MetricId.Low01Fps, ft), 6);
        Assert.Equal(10.0, Metrics.Compute(MetricId.FrameTimeMedian, ft), 6);
        Assert.Equal(10.0, Metrics.Compute(MetricId.FrameTimeP95, ft), 6);
    }

    [Fact]
    public void Percentile_Interpolates()
    {
        var v = new double[] { 1, 2, 3, 4 };
        Assert.Equal(2.5, Metrics.Percentile(v, 50), 6);
        Assert.Equal(4, Metrics.Percentile(v, 100), 6);
        Assert.Equal(1, Metrics.Percentile(v, 0), 6);
    }

    [Fact]
    public void TrimWarmup_DropsFirstSeconds()
    {
        var ft = Enumerable.Repeat(10.0, 3000).ToList(); // 30 с
        var trimmed = MeasurementRun.TrimWarmup(ft, 10);
        Assert.Equal(2000, trimmed.Count);
    }

    [Fact]
    public void FromTimestamps_ComputesIntervals()
    {
        var ft = MeasurementRun.FromTimestamps(new[] { 0.0, 16.6, 33.3, 33.3, 50.0 });
        Assert.Equal(3, ft.Count); // нулевой интервал отброшен
        Assert.Equal(16.6, ft[0], 6);
    }

    [Fact]
    public void SameDistribution_IsNoChange()
    {
        var before = new[] { Run("b1", 8.0, 1.0, 10, 1), Run("b2", 8.0, 1.0, 10, 2), Run("b3", 8.0, 1.0, 10, 3) };
        var after = new[] { Run("a1", 8.0, 1.0, 10, 4), Run("a2", 8.0, 1.0, 10, 5), Run("a3", 8.0, 1.0, 10, 6) };
        var r = new Comparer().Compare(before, after);
        Assert.All(r.Metrics.Where(m => m.Metric.Id is MetricId.AvgFps or MetricId.FrameTimeMedian), m => Assert.Equal(Verdict.NoChange, m.Verdict));
        Assert.NotEqual(Verdict.Better, r.Overall);
        Assert.NotEqual(Verdict.Worse, r.Overall);
    }

    [Fact]
    public void ClearImprovement_IsBetter()
    {
        // до: 8,5 мс и провалы +25 мс; после: 7 мс и провалы +8 мс
        var before = new[] { Run("b1", 8.5, 1.0, 25, 1), Run("b2", 8.5, 1.0, 25, 2), Run("b3", 8.5, 1.0, 25, 3) };
        var after = new[] { Run("a1", 7.0, 1.0, 8, 4), Run("a2", 7.0, 1.0, 8, 5), Run("a3", 7.0, 1.0, 8, 6) };
        var r = new Comparer().Compare(before, after);
        Assert.Equal(Verdict.Better, r.Overall);
        Assert.Equal(Verdict.Better, r.Metrics.Single(m => m.Metric.Id == MetricId.AvgFps).Verdict);
        Assert.Equal(Verdict.Better, r.Metrics.Single(m => m.Metric.Id == MetricId.Low1Fps).Verdict);
        Assert.Equal(Verdict.Better, r.Metrics.Single(m => m.Metric.Id == MetricId.FrameTimeP99).Verdict);
        Assert.True(r.Metrics.Single(m => m.Metric.Id == MetricId.AvgFps).DeltaPercent > 15);
        Assert.True(r.Reliable);
    }

    [Fact]
    public void ClearRegression_IsWorse()
    {
        var before = new[] { Run("b1", 7.0, 1.0, 8, 1), Run("b2", 7.0, 1.0, 8, 2), Run("b3", 7.0, 1.0, 8, 3) };
        var after = new[] { Run("a1", 8.5, 1.0, 25, 4), Run("a2", 8.5, 1.0, 25, 5), Run("a3", 8.5, 1.0, 25, 6) };
        Assert.Equal(Verdict.Worse, new Comparer().Compare(before, after).Overall);
    }

    [Fact]
    public void SmallDifferenceWithinRunSpread_IsNoChange()
    {
        // Прогоны «до» сильно различаются между собой — небольшая разница не должна считаться улучшением
        var before = new[] { Run("b1", 8.0, 1.0, 10, 1), Run("b2", 8.6, 1.0, 10, 2), Run("b3", 7.4, 1.0, 10, 3) };
        var after = new[] { Run("a1", 7.8, 1.0, 10, 4), Run("a2", 7.9, 1.0, 10, 5), Run("a3", 7.7, 1.0, 10, 6) };
        var avg = new Comparer().Compare(before, after).Metrics.Single(m => m.Metric.Id == MetricId.AvgFps);
        Assert.Equal(Verdict.NoChange, avg.Verdict);
    }

    [Fact]
    public void ThrottledRun_IsExcludedAndFlagged()
    {
        var before = new[] { Run("b1", 8.0, 1.0, 10, 1), Run("b2", 8.0, 1.0, 10, 2), Run("b3", 14.0, 1.0, 40, 3, throttled: true) };
        var after = new[] { Run("a1", 8.0, 1.0, 10, 4), Run("a2", 8.0, 1.0, 10, 5) };
        var r = new Comparer().Compare(before, after);
        Assert.False(r.Reliable);
        Assert.Contains(r.Warnings, w => w.Contains("b3") && w.Contains("троттлинг"));
        Assert.Equal(2, r.Metrics.First().BeforeRuns.Length);
        Assert.NotEqual(Verdict.Better, r.Overall); // без троттлинга разницы нет
    }

    [Fact]
    public void DifferentConditions_AreWarned()
    {
        var b = Run("b1", 8, 1, 10, 1);
        var a = new MeasurementRun { Label = "a1", FrameTimesMs = Run("x", 8, 1, 10, 2).FrameTimesMs, Conditions = new RunConditions { ProcessName = "game", Width = 2560, Height = 1440, OnAcPower = false } };
        var r = new Comparer().Compare(new[] { b }, new[] { a });
        Assert.Contains(r.Warnings, w => w.Contains("разрешение"));
        Assert.Contains(r.Warnings, w => w.Contains("батаре"));
        Assert.False(r.Reliable);
    }

    [Fact]
    public void Bootstrap_IsDeterministic()
    {
        var runs = new[] { Run("b1", 8.0, 1.0, 10, 1) };
        var c = new Comparer();
        Assert.Equal(c.BootstrapCi(MetricId.Low1Fps, runs), c.BootstrapCi(MetricId.Low1Fps, runs));
    }

    [Fact]
    public void SameDistribution_NeverClaimsImprovement()
    {
        // Ложные «улучшения» при отсутствии эффекта: шесть пар замеров из одного распределения
        for (int k = 0; k < 6; k++)
        {
            var before = Enumerable.Range(0, 3).Select(i => Run($"b{i}", 8, 1.5, 20, 1000 * k + i)).ToList();
            var after = Enumerable.Range(0, 3).Select(i => Run($"a{i}", 8, 1.5, 20, 1000 * k + 500 + i)).ToList();
            var r = new Comparer { BootstrapIterations = 300 }.Compare(before, after);
            Assert.NotEqual(Verdict.Better, r.Overall);
            Assert.NotEqual(Verdict.Worse, r.Overall);
        }
    }

    [Fact]
    public void SmallButConsistentGain_IsDetected_ByDifferenceInterval()
    {
        // Медиана −0,3 мс при шуме ±1,5 мс: интервалы замеров пересекаются, но разность устойчиво меньше нуля
        var before = Enumerable.Range(0, 3).Select(i => Run($"b{i}", 8.0, 1.5, 0, 10 + i, frames: 8000)).ToList();
        var after = Enumerable.Range(0, 3).Select(i => Run($"a{i}", 7.7, 1.5, 0, 20 + i, frames: 8000)).ToList();
        var r = new Comparer { BootstrapIterations = 300 }.Compare(before, after);
        var fps = r.Metrics.Single(m => m.Metric.Id == MetricId.AvgFps);
        Assert.True(fps.DiffCi.Low > 0, $"интервал разности {fps.DiffCi}");
        Assert.Equal(Verdict.Better, fps.Verdict);
    }
}
