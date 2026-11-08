using Opti.Measure.Capture;

namespace Opti.Tests;

public class ConnectionProbeTests
{
    [Fact]
    public void Summarize_ComputesMedianJitterAndLoss()
    {
        var t = new ProbeTarget("x", "X", "x.example");
        var r = ConnectionProbe.Summarize(t, new[] { 20.0, 22, 21, 80, 20 }, lost: 1, samples: 6);
        Assert.Equal(21, r.MedianMs);
        Assert.InRange(r.JitterMs, 25, 40);
        Assert.InRange(r.LossPercent, 16, 17);
        Assert.True(r.Reachable);
        Assert.False(ConnectionProbe.Summarize(t, Array.Empty<double>(), 5, 5).Reachable);
    }

    [Fact]
    public void Median_HandlesEvenAndOdd()
    {
        Assert.Equal(3, ConnectionProbe.Median(new[] { 5.0, 1, 3 }));
        Assert.Equal(2.5, ConnectionProbe.Median(new[] { 1.0, 2, 3, 4 }));
    }

    [Fact]
    public void DnsQuery_IsWellFormed()
    {
        var q = DnsBenchmark.BuildQuery("example.com", 0x1234);
        Assert.Equal(0x12, q[0]); Assert.Equal(0x34, q[1]);
        Assert.Equal(1, q[5]);                         // один вопрос
        Assert.Equal(7, q[12]);                        // длина метки «example»
        Assert.Equal("example", System.Text.Encoding.ASCII.GetString(q, 13, 7));
        Assert.Equal(0, q[^5]);                        // конец имени
        Assert.Equal(1, q[^3]); Assert.Equal(1, q[^1]); // тип A, класс IN
    }

    [Fact]
    public void DnsResponse_Validation()
    {
        var ok = new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 };
        Assert.True(DnsBenchmark.IsGoodResponse(ok, ok.Length, 0x1234));
        Assert.False(DnsBenchmark.IsGoodResponse(ok, ok.Length, 0x9999));               // чужой идентификатор
        var nx = (byte[])ok.Clone(); nx[3] = 0x83;                                      // NXDOMAIN
        Assert.False(DnsBenchmark.IsGoodResponse(nx, nx.Length, 0x1234));
        var query = (byte[])ok.Clone(); query[2] = 0x01;                                // не ответ
        Assert.False(DnsBenchmark.IsGoodResponse(query, query.Length, 0x1234));
        Assert.False(DnsBenchmark.IsGoodResponse(ok, 5, 0x1234));
    }

    [Fact]
    public void Regions_AreUniqueAndNamed()
    {
        Assert.Equal(ConnectionProbe.Regions.Count, ConnectionProbe.Regions.Select(r => r.Id).Distinct().Count());
        Assert.All(ConnectionProbe.Regions, r => Assert.Contains('.', r.Host));
    }

    [Fact]
    public async Task Dns_ClosedPort_IsReportedAsFailure()
    {
        var closed = new DnsResolver("closed", "closed", "127.0.0.1");
        var r = await DnsBenchmark.RunAsync(closed, roundsPerDomain: 1, timeoutMs: 80);
        Assert.False(r.Works);
        Assert.True(r.Failed > 0);
    }
}
