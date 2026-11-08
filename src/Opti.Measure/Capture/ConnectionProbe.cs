using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Opti.Measure.Capture;

public sealed record ProbeTarget(string Id, string Name, string Host);

/// <summary>Результат проверки одного направления. RTT — время установки TCP-соединения (ICMP у многих серверов закрыт).</summary>
public sealed record ProbeResult(ProbeTarget Target, double MedianMs, double JitterMs, double LossPercent, int Samples)
{
    public bool Reachable => Samples > 0 && !double.IsNaN(MedianMs);
}

/// <summary>
/// Качество связи до регионов, где часто размещены игровые серверы: время TCP-рукопожатия (порт 443), разброс и потери.
/// Ничего не скачивает и не отправляет, кроме пустого соединения; игровые серверы не опрашиваются.
/// Это ориентир «куда ближе и стабильнее», а не пинг до конкретной игры.
/// </summary>
public static class ConnectionProbe
{
    /// <summary>Публичные точки облачных регионов (на них размещены серверы многих игр и сервисов).</summary>
    public static IReadOnlyList<ProbeTarget> Regions { get; } = new ProbeTarget[]
    {
        new("eu-central-1", "Франкфурт (Германия)", "dynamodb.eu-central-1.amazonaws.com"),
        new("eu-north-1", "Стокгольм (Швеция)", "dynamodb.eu-north-1.amazonaws.com"),
        new("eu-west-1", "Ирландия", "dynamodb.eu-west-1.amazonaws.com"),
        new("eu-west-2", "Лондон", "dynamodb.eu-west-2.amazonaws.com"),
        new("eu-south-1", "Милан (Италия)", "dynamodb.eu-south-1.amazonaws.com"),
        new("us-east-1", "Вирджиния (США, восток)", "dynamodb.us-east-1.amazonaws.com"),
        new("us-west-2", "Орегон (США, запад)", "dynamodb.us-west-2.amazonaws.com"),
        new("sa-east-1", "Сан-Паулу (Бразилия)", "dynamodb.sa-east-1.amazonaws.com"),
        new("me-south-1", "Бахрейн (Ближний Восток)", "dynamodb.me-south-1.amazonaws.com"),
        new("ap-south-1", "Мумбаи (Индия)", "dynamodb.ap-south-1.amazonaws.com"),
        new("ap-southeast-1", "Сингапур", "dynamodb.ap-southeast-1.amazonaws.com"),
        new("ap-northeast-1", "Токио (Япония)", "dynamodb.ap-northeast-1.amazonaws.com"),
        new("ap-southeast-2", "Сидней (Австралия)", "dynamodb.ap-southeast-2.amazonaws.com"),
    };

    public static async Task<ProbeResult> ProbeAsync(ProbeTarget target, int samples = 12, int timeoutMs = 1500, CancellationToken ct = default)
    {
        var rtts = new List<double>(samples);
        int lost = 0;
        IPAddress[] addrs;
        try { addrs = await Dns.GetHostAddressesAsync(target.Host, ct); }
        catch (SocketException) { return new ProbeResult(target, double.NaN, 0, 100, 0); }
        var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
        if (ip is null) return new ProbeResult(target, double.NaN, 0, 100, 0);

        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var client = new TcpClient(ip.AddressFamily);
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                await client.ConnectAsync(ip, 443, cts.Token);
                rtts.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { lost++; }
            catch (SocketException) { lost++; }
            await Task.Delay(100, ct);
        }
        return Summarize(target, rtts, lost, samples);
    }

    public static ProbeResult Summarize(ProbeTarget target, IReadOnlyList<double> rtts, int lost, int samples) =>
        new(target, rtts.Count == 0 ? double.NaN : Median(rtts), NetworkTest.Jitter(rtts), samples == 0 ? 0 : 100.0 * lost / samples, samples);

    public static double Median(IReadOnlyList<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Все регионы по очереди (параллельно нельзя: пробы мешали бы друг другу). Сообщает о ходе после каждого.</summary>
    public static async Task<List<ProbeResult>> ProbeAllAsync(IReadOnlyList<ProbeTarget> targets, IProgress<ProbeResult>? progress = null, CancellationToken ct = default)
    {
        var list = new List<ProbeResult>();
        foreach (var t in targets)
        {
            var r = await ProbeAsync(t, ct: ct);
            list.Add(r);
            progress?.Report(r);
        }
        return list;
    }
}

public sealed record DnsResolver(string Id, string Name, string Address);

public sealed record DnsResult(DnsResolver Resolver, double MedianMs, int Failed, int Queries)
{
    public bool Works => Queries > Failed && !double.IsNaN(MedianMs);
}

/// <summary>
/// Скорость DNS-серверов: UDP-запрос типа A к каждому серверу. Быстрый DNS ускоряет вход в игру, поиск матча и загрузку лаунчеров;
/// на пинг уже идущего матча он не влияет.
/// </summary>
public static class DnsBenchmark
{
    public static readonly string[] Domains = { "steampowered.com", "epicgames.com", "riotgames.com", "battle.net", "ea.com", "xbox.com", "cloudflare.com", "google.com" };

    public static IReadOnlyList<DnsResolver> Public { get; } = new DnsResolver[]
    {
        new("cloudflare", "Cloudflare 1.1.1.1", "1.1.1.1"),
        new("google", "Google 8.8.8.8", "8.8.8.8"),
        new("quad9", "Quad9 9.9.9.9", "9.9.9.9"),
        new("adguard", "AdGuard 94.140.14.14", "94.140.14.14"),
    };

    /// <summary>DNS-серверы, выданные сейчас активным адаптерам (обычно роутер или провайдер).</summary>
    public static IReadOnlyList<DnsResolver> Current()
    {
        var list = new List<DnsResolver>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                foreach (var a in nic.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    if (list.All(r => r.Address != a.ToString()))
                        list.Add(new DnsResolver("current:" + a, "Сейчас: " + a, a.ToString()));
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>Минимальный DNS-запрос типа A (RFC 1035).</summary>
    public static byte[] BuildQuery(string domain, ushort id)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
        foreach (var label in domain.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        ms.Write(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 });
        return ms.ToArray();
    }

    /// <summary>Ответ подходит, если совпал идентификатор, это ответ (QR) и код ошибки — 0 (NOERROR).</summary>
    public static bool IsGoodResponse(byte[] data, int length, ushort id) =>
        length >= 12 && data[0] == (byte)(id >> 8) && data[1] == (byte)id && (data[2] & 0x80) != 0 && (data[3] & 0x0F) == 0;

    public static async Task<DnsResult> RunAsync(DnsResolver resolver, int roundsPerDomain = 2, int timeoutMs = 1200, CancellationToken ct = default)
    {
        var times = new List<double>();
        int failed = 0, total = 0;
        var ep = new IPEndPoint(IPAddress.Parse(resolver.Address), 53);
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        // Первый заход прогревает кеш резолвера — он не учитывается: важна близость сервера, а не рекурсия до корня
        for (int round = 0; round < 1 + roundsPerDomain; round++)
            foreach (var d in Domains)
            {
                ct.ThrowIfCancellationRequested();
                using var udp = new UdpClient();
                var q = BuildQuery(d, ++id);
                var sw = Stopwatch.StartNew();
                try
                {
                    await udp.SendAsync(q, ep, ct);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeoutMs);
                    var r = await udp.ReceiveAsync(cts.Token);
                    sw.Stop();
                    if (round == 0) continue;
                    total++;
                    if (IsGoodResponse(r.Buffer, r.Buffer.Length, id)) times.Add(sw.Elapsed.TotalMilliseconds); else failed++;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (round > 0) { total++; failed++; } }
                catch (SocketException) { if (round > 0) { total++; failed++; } }
            }
        return new DnsResult(resolver, times.Count == 0 ? double.NaN : ConnectionProbe.Median(times), failed, total);
    }
}
