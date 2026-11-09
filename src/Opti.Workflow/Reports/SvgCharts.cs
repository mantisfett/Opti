using System.Globalization;
using System.Text;
using Opti.Measure.Stats;
using Opti.Core.Localization;

namespace Opti.Workflow.Reports;

public sealed record ChartSeries(string Name, string CssColorVar, IReadOnlyList<double> FrameTimesMs);

/// <summary>Самодостаточные SVG-графики для отчёта (без скриптов и внешних библиотек).</summary>
public static class SvgCharts
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    /// <summary>Подписи для людей — с десятичной запятой.</summary>
    private static string L(double v) => v.ToString(v >= 10 ? "0" : "0.#", Opti.Core.Localization.Loc.Culture);

    /// <summary>
    /// Наложенные графики фреймтайма. Ось X — время с начала прогона, Y — мс.
    /// Длинные ряды прореживаются по корзинам с сохранением минимума и максимума — провалы не теряются.
    /// </summary>
    public static string FrameTimeLines(IReadOnlyList<ChartSeries> series, int width = 960, int height = 300)
    {
        var all = series.SelectMany(s => s.FrameTimesMs).ToArray();
        if (all.Length == 0) return "";
        Array.Sort(all);
        double yMax = NiceCeil(Math.Max(Metrics.PercentileSorted(all, 99.9) * 1.25, Metrics.PercentileSorted(all, 50) * 2));
        double xMax = series.Max(s => s.FrameTimesMs.Sum() / 1000.0);
        const int left = 48, right = 12, top = 12, bottom = 30;
        double pw = width - left - right, ph = height - top - bottom;
        double X(double sec) => left + sec / xMax * pw;
        double Y(double ms) => top + ph - Math.Min(ms, yMax) / yMax * ph;

        var sb = new StringBuilder();
        sb.Append(Loc.T($"<svg class=\"chart\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Фреймтайм во времени\">"));
        AppendGrid(sb, left, top, pw, ph, yMax, Loc.T("мс"), Y);
        for (int i = 0; i <= 5; i++)
        {
            double sec = xMax * i / 5;
            sb.Append(Loc.T($"<text class=\"axis\" x=\"{F(X(sec))}\" y=\"{height - 8}\" text-anchor=\"middle\">{L(sec)} с</text>"));
        }
        foreach (var s in series)
        {
            var pts = Decimate(s.FrameTimesMs, (int)pw);
            sb.Append($"<polyline fill=\"none\" stroke=\"var({s.CssColorVar})\" stroke-width=\"1.3\" stroke-linejoin=\"round\" points=\"");
            foreach (var (sec, ms) in pts) sb.Append(F(X(sec))).Append(',').Append(F(Y(ms))).Append(' ');
            sb.Append($"\"><title>{System.Net.WebUtility.HtmlEncode(s.Name)}</title></polyline>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Гистограмма распределения фреймтайма (доля кадров, %).</summary>
    public static string Histogram(IReadOnlyList<ChartSeries> series, int width = 960, int height = 260)
    {
        var all = series.SelectMany(s => s.FrameTimesMs).ToArray();
        if (all.Length == 0) return "";
        Array.Sort(all);
        double lo = Math.Floor(Metrics.PercentileSorted(all, 0.5));
        double hi = Math.Ceiling(Metrics.PercentileSorted(all, 99));
        if (hi - lo < 4) hi = lo + 4;
        int bins = 48;
        double bw = (hi - lo) / bins;
        var hist = series.Select(s =>
        {
            var h = new double[bins];
            foreach (var v in s.FrameTimesMs)
            {
                int b = (int)((v - lo) / bw);
                if (b >= 0 && b < bins) h[b]++;
            }
            int n = Math.Max(1, s.FrameTimesMs.Count);
            return h.Select(c => c * 100.0 / n).ToArray();
        }).ToList();
        double yMax = NiceCeil(hist.SelectMany(h => h).DefaultIfEmpty(1).Max());
        const int left = 48, right = 12, top = 12, bottom = 30;
        double pw = width - left - right, ph = height - top - bottom;
        double Y(double pct) => top + ph - pct / yMax * ph;

        var sb = new StringBuilder();
        sb.Append(Loc.T($"<svg class=\"chart\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Распределение фреймтайма\">"));
        AppendGrid(sb, left, top, pw, ph, yMax, "%", Y);
        double slot = pw / bins;
        double barW = Math.Max(1, (slot - 2) / Math.Max(1, series.Count));
        for (int si = 0; si < series.Count; si++)
            for (int b = 0; b < bins; b++)
            {
                double v = hist[si][b];
                if (v <= 0) continue;
                double x = left + b * slot + 1 + si * barW;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(Y(v))}\" width=\"{F(barW)}\" height=\"{F(top + ph - Y(v))}\" rx=\"1\" fill=\"var({series[si].CssColorVar})\">" +
                          Loc.T($"<title>{System.Net.WebUtility.HtmlEncode(series[si].Name)}: {L(lo + b * bw)}–{L(lo + (b + 1) * bw)} мс — {L(v)} %</title></rect>"));
            }
        for (int i = 0; i <= 6; i++)
        {
            double ms = lo + (hi - lo) * i / 6;
            sb.Append(Loc.T($"<text class=\"axis\" x=\"{F(left + pw * i / 6)}\" y=\"{height - 8}\" text-anchor=\"middle\">{L(ms)} мс</text>"));
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendGrid(StringBuilder sb, double left, double top, double pw, double ph, double yMax, string unit, Func<double, double> y)
    {
        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4;
            double yy = y(v);
            sb.Append($"<line class=\"grid\" x1=\"{F(left)}\" x2=\"{F(left + pw)}\" y1=\"{F(yy)}\" y2=\"{F(yy)}\"/>");
            sb.Append($"<text class=\"axis\" x=\"{F(left - 6)}\" y=\"{F(yy + 4)}\" text-anchor=\"end\">{L(v)}{(i == 4 ? " " + unit : "")}</text>");
        }
    }

    /// <summary>Прореживание: на каждую корзину по времени — точки минимума и максимума в порядке появления.</summary>
    public static List<(double Sec, double Ms)> Decimate(IReadOnlyList<double> frameTimes, int buckets)
    {
        var pts = new List<(double, double)>();
        if (frameTimes.Count == 0) return pts;
        if (frameTimes.Count <= buckets * 2)
        {
            double t = 0;
            foreach (var f in frameTimes) { t += f / 1000.0; pts.Add((t, f)); }
            return pts;
        }
        int per = (int)Math.Ceiling(frameTimes.Count / (double)buckets);
        double time = 0;
        for (int i = 0; i < frameTimes.Count; i += per)
        {
            int end = Math.Min(frameTimes.Count, i + per);
            double minV = double.MaxValue, maxV = double.MinValue, tMin = 0, tMax = 0;
            for (int j = i; j < end; j++)
            {
                time += frameTimes[j] / 1000.0;
                if (frameTimes[j] < minV) { minV = frameTimes[j]; tMin = time; }
                if (frameTimes[j] > maxV) { maxV = frameTimes[j]; tMax = time; }
            }
            if (tMin <= tMax) { pts.Add((tMin, minV)); pts.Add((tMax, maxV)); }
            else { pts.Add((tMax, maxV)); pts.Add((tMin, minV)); }
        }
        return pts;
    }

    public static double NiceCeil(double v)
    {
        if (v <= 0 || double.IsNaN(v)) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }
}
