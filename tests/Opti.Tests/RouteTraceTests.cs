using Opti.Measure.Capture;

namespace Opti.Tests;

public class RouteTraceTests
{
    private static RouteHop H(int ttl, double ms, double loss = 0, string? addr = "x") => new(ttl, double.IsNaN(ms) ? null : addr ?? $"10.0.0.{ttl}", ms, loss, 3);

    [Fact]
    public void CleanRoute_IsGood()
    {
        var v = RouteTrace.Analyze(new[] { H(1, 1), H(2, 8), H(3, 12), H(4, 18) });
        Assert.Equal("good", v.Tone);
        Assert.Contains("чистый", v.Summary);
    }

    [Fact]
    public void SlowFirstHop_MeansHomeProblem()
    {
        var v = RouteTrace.Analyze(new[] { H(1, 38), H(2, 45), H(3, 50) });
        Assert.Equal("bad", v.Tone);
        Assert.Equal(1, v.ProblemHop);
        Assert.Contains("дома", v.Summary);
    }

    [Fact]
    public void LossAtDestination_IsReported()
    {
        var v = RouteTrace.Analyze(new[] { H(1, 1), H(2, 10), H(3, 20, loss: 67) });
        Assert.Equal("warn", v.Tone);
        Assert.Equal(3, v.ProblemHop);
    }

    [Fact]
    public void BigJump_IsLocated_ButSilentHopsAreNotAnAlarm()
    {
        var hops = new[] { H(1, 1), H(2, double.NaN, 100), H(3, 12), H(4, 95), H(5, 100) };
        var v = RouteTrace.Analyze(hops);
        Assert.Equal(4, v.ProblemHop);
        Assert.Contains("+83", v.Summary);
    }

    [Fact]
    public void NoAnswers_IsBad() => Assert.Equal("bad", RouteTrace.Analyze(new[] { H(1, double.NaN, 100), H(2, double.NaN, 100) }).Tone);
}
