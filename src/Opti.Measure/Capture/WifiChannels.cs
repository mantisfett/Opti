using System.Globalization;
using System.Text.RegularExpressions;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

public sealed record WifiNetwork(string Ssid, string Bssid, int SignalPercent, int Channel)
{
    public bool Is24 => Channel is >= 1 and <= 14;
}

public sealed record WifiCurrent(string? Ssid, int? Channel, int? SignalPercent)
{
    public bool Connected => Channel is not null;
    public bool Is24 => Channel is >= 1 and <= 14;
}

/// <param name="Load">Сумма сигналов соседних сетей, мешающих этому каналу (1,0 ≈ одна сильная сеть на том же канале).</param>
public sealed record WifiChannelScore(int Channel, bool Is24, double Load, int Neighbors, bool IsCurrent, bool IsBest);

public sealed record WifiScanResult(IReadOnlyList<WifiNetwork> Networks, WifiCurrent Current);

public sealed record WifiAdvice(IReadOnlyList<WifiChannelScore> Channels, int? BestChannel, string Summary, string Tone);

/// <summary>
/// Загруженность Wi-Fi каналов по соседним сетям: на 2,4 ГГц каналы 1–13 перекрываются (чистые — 1, 6, 11), на 5 ГГц сети мешают только на своём канале.
/// Рекомендации — только каналы без радаров (DFS) на 5 ГГц: переход на DFS-канал при радаре даёт обрыв связи и скачок пинга.
/// Разбор рассчитан на вывод <c>netsh wlan show networks mode=bssid</c> (английский и русский Windows).
/// </summary>
public static partial class WifiChannels
{
    public static readonly int[] Candidates24 = { 1, 6, 11 };
    public static readonly int[] Candidates5 = { 36, 40, 44, 48, 149, 153, 157, 161 };

    [GeneratedRegex(@"^\s*SSID\s+\d+\s*:\s*(.*?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex SsidHeader();
    [GeneratedRegex(@"^\s*BSSID\s+\d+\s*:\s*(\S+)", RegexOptions.IgnoreCase)] private static partial Regex BssidLine();
    [GeneratedRegex(@"^\s*(?:Signal|Сигнал)\s*:\s*(\d{1,3})\s*%", RegexOptions.IgnoreCase)] private static partial Regex SignalLine();
    [GeneratedRegex(@"^\s*(?:Channel|Канал)\s*:\s*(\d{1,3})\s*$", RegexOptions.IgnoreCase)] private static partial Regex ChannelLine();
    [GeneratedRegex(@"^\s*SSID\s*:\s*(.*?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex CurrentSsidLine();

    /// <summary>Список сетей из <c>netsh wlan show networks mode=bssid</c>: у каждой точки доступа SSID, BSSID, сигнал и канал.</summary>
    public static List<WifiNetwork> ParseNetworks(string netsh)
    {
        var list = new List<WifiNetwork>();
        string ssid = "", bssid = "";
        int? signal = null;
        foreach (var line in netsh.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (SsidHeader().Match(l) is { Success: true } s) { ssid = s.Groups[1].Value; bssid = ""; signal = null; continue; }
            if (BssidLine().Match(l) is { Success: true } b) { bssid = b.Groups[1].Value; signal = null; continue; }
            if (SignalLine().Match(l) is { Success: true } sg) { signal = Math.Clamp(int.Parse(sg.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100); continue; }
            if (ChannelLine().Match(l) is { Success: true } c && bssid.Length > 0 && signal is { } sig)
            {
                list.Add(new WifiNetwork(ssid, bssid, sig, int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture)));
                signal = null;
            }
        }
        return list;
    }

    /// <summary>Текущее подключение из <c>netsh wlan show interfaces</c>.</summary>
    public static WifiCurrent ParseCurrent(string netsh)
    {
        string? ssid = null; int? channel = null, signal = null;
        foreach (var line in netsh.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (ssid is null && CurrentSsidLine().Match(l) is { Success: true } s && !Regex.IsMatch(l, @"^\s*B?SSID\s+\d", RegexOptions.IgnoreCase)) ssid = s.Groups[1].Value;
            if (SignalLine().Match(l) is { Success: true } sg) signal ??= int.Parse(sg.Groups[1].Value, CultureInfo.InvariantCulture);
            if (ChannelLine().Match(l) is { Success: true } c) channel ??= int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return new WifiCurrent(ssid, channel, signal);
    }

    /// <summary>Вклад сети в загрузку канала: сильнее сигнал — больше помех; на 2,4 ГГц соседние каналы мешают слабее.</summary>
    public static double Interference(WifiNetwork n, int channel, bool is24)
    {
        if (is24 != n.Is24) return 0;
        int d = Math.Abs(n.Channel - channel);
        double w = n.SignalPercent / 100.0;
        if (!is24) return d == 0 ? w : 0;
        return d <= 4 ? w * (1 - 0.2 * d) : 0;
    }

    public static WifiAdvice Analyze(IReadOnlyList<WifiNetwork> all, WifiCurrent current)
    {
        // Свои точки доступа (тот же SSID, что у текущего подключения) помехой не считаем
        var others = all.Where(n => current.Ssid is null || !string.Equals(n.Ssid, current.Ssid, StringComparison.Ordinal)).ToList();
        double Load(int ch, bool is24) => others.Sum(n => Interference(n, ch, is24));
        int Neighbors(int ch, bool is24) => others.Count(n => Interference(n, ch, is24) > 0);

        var channels = new List<WifiChannelScore>();
        void AddBand(IEnumerable<int> candidates, bool is24)
        {
            var set = candidates.ToList();
            if (current.Connected && current.Is24 == is24 && !set.Contains(current.Channel!.Value)) set.Add(current.Channel.Value);
            set.Sort();
            var scores = set.Select(c => (c, load: Load(c, is24))).ToList();
            var allowed = is24 ? Candidates24 : Candidates5;
            double min = scores.Where(s => Array.IndexOf(allowed, s.c) >= 0).Select(s => s.load).DefaultIfEmpty(0).Min();
            // Одна рекомендация на диапазон: из равных по загрузке — ближайший к текущему каналу (меньше менять в роутере), иначе самый низкий
            int anchor = current.Connected && current.Is24 == is24 ? current.Channel!.Value : allowed[0];
            int? best = scores.Where(s => Array.IndexOf(allowed, s.c) >= 0 && Math.Abs(s.load - min) < 1e-9)
                .OrderBy(s => Math.Abs(s.c - anchor)).ThenBy(s => s.c).Select(s => (int?)s.c).FirstOrDefault();
            foreach (var (c, load) in scores)
                channels.Add(new WifiChannelScore(c, is24, load, Neighbors(c, is24), current.Connected && current.Channel == c && current.Is24 == is24, c == best));
        }
        AddBand(Candidates24, true);
        if (all.Any(n => !n.Is24) || (current.Connected && !current.Is24)) AddBand(Candidates5, false);

        if (all.Count == 0)
            return new WifiAdvice(channels, null, Loc.T("Сети Wi-Fi рядом не найдены: нет адаптера, Wi-Fi выключен или Windows закрыла список сетей — включите «Службы геолокации» в параметрах конфиденциальности."), "neutral");

        if (!current.Connected)
            return new WifiAdvice(channels, null, Loc.T("Вы не подключены к Wi-Fi, поэтому совет по каналу не нужен. Ниже — загруженность каналов рядом."), "neutral");

        var band = channels.Where(c => c.Is24 == current.Is24).ToList();
        var cur = band.First(c => c.IsCurrent);
        var best = band.Where(c => c.IsBest).OrderBy(c => Math.Abs(c.Channel - cur.Channel)).FirstOrDefault();
        int? bestCh = best?.Channel;
        bool betterExists = best is not null && best.Channel != cur.Channel && cur.Load - best.Load >= (current.Is24 ? 0.5 : 0.7);
        bool has5Own = all.Any(n => !n.Is24 && current.Ssid is not null && string.Equals(n.Ssid, current.Ssid, StringComparison.Ordinal));

        if (current.Is24)
        {
            var five = has5Own ? Loc.T(" Ваш роутер раздаёт и 5 ГГц — подключитесь к нему: там меньше помех.") : Loc.T(" Если роутер поддерживает 5 ГГц, подключитесь к нему: там меньше помех.");
            if (betterExists)
                return new WifiAdvice(channels, bestCh, Loc.T($"Вы на 2,4 ГГц, канал {cur.Channel}: рядом {cur.Neighbors} сетей мешают на этом канале. Канал {best!.Channel} свободнее — смените его в настройках роутера.") + five, "warn");
            if (cur.Neighbors >= 5 || cur.Load >= 2)
                return new WifiAdvice(channels, bestCh, Loc.T($"Вы на 2,4 ГГц, канал {cur.Channel}: диапазон забит ({cur.Neighbors} сетей рядом), лучше канала нет.") + five, "warn");
            return new WifiAdvice(channels, bestCh, Loc.T($"Канал {cur.Channel} на 2,4 ГГц свободен от помех соседей.") + (has5Own ? Loc.T(" Для игр всё равно лучше 5 ГГц.") : ""), "good");
        }
        if (betterExists)
            return new WifiAdvice(channels, bestCh, Loc.T($"Вы на 5 ГГц, канал {cur.Channel}: на нём {cur.Neighbors} соседних сетей. Канал {best!.Channel} свободнее — смените его в настройках роутера (избегайте каналов 52–144: при радарах они дают обрывы)."), "warn");
        return new WifiAdvice(channels, bestCh, Loc.T($"Вы на 5 ГГц, канал {cur.Channel}: помех от соседей почти нет."), "good");
    }
}
