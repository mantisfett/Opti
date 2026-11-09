using System.Globalization;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

public sealed class PresentMonProcessData
{
    public required string Application { get; init; }
    public int ProcessId { get; init; }
    public List<double> FrameTimesMs { get; } = new();
    public Dictionary<string, int> PresentModes { get; } = new();
    public List<double> PcLatencyMs { get; } = new();
    public List<double> DisplayLatencyMs { get; } = new();

    public double? IndependentFlipShare
    {
        get
        {
            int total = PresentModes.Values.Sum();
            if (total == 0) return null;
            return PresentModes.Where(kv => kv.Key.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value) / (double)total;
        }
    }
}

/// <summary>
/// Разбор CSV PresentMon 1.x и 2.x. Столбцы ищутся по имени без учёта регистра, поэтому
/// поддерживаются обе версии формата («msBetweenPresents» / «MsBetweenPresents», «msUntilDisplayed» / «MsDisplayLatency»).
/// </summary>
public static class PresentMonCsv
{
    private static readonly string[] FrameCols = { "MsBetweenPresents" };
    private static readonly string[] ModeCols = { "PresentMode" };
    private static readonly string[] PcLatencyCols = { "MsPCLatency" };
    private static readonly string[] DisplayLatencyCols = { "MsDisplayLatency", "MsUntilDisplayed" };
    private static readonly string[] AppCols = { "Application" };
    private static readonly string[] PidCols = { "ProcessID" };

    public static List<PresentMonProcessData> Parse(TextReader reader)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException(Loc.T("Пустой CSV PresentMon"));
        var cols = SplitCsv(header).Select((name, i) => (name: name.Trim(), i)).ToList();
        int Find(string[] names) => cols.FirstOrDefault(c => names.Any(n => n.Equals(c.name, StringComparison.OrdinalIgnoreCase)), (name: "", i: -1)).i;
        int iFrame = Find(FrameCols), iMode = Find(ModeCols), iPc = Find(PcLatencyCols), iDisp = Find(DisplayLatencyCols),
            iApp = Find(AppCols), iPid = Find(PidCols);
        if (iFrame < 0) throw new InvalidDataException(Loc.T("В CSV PresentMon нет столбца MsBetweenPresents"));

        var byKey = new Dictionary<(string, int), PresentMonProcessData>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            string app = iApp >= 0 && iApp < f.Count ? f[iApp] : "?";
            int pid = iPid >= 0 && iPid < f.Count && int.TryParse(f[iPid], out var p) ? p : 0;
            if (!byKey.TryGetValue((app, pid), out var data)) byKey[(app, pid)] = data = new PresentMonProcessData { Application = app, ProcessId = pid };

            if (TryNum(f, iFrame, out var ft) && ft > 0) data.FrameTimesMs.Add(ft);
            if (iMode >= 0 && iMode < f.Count && f[iMode].Length > 0 && f[iMode] != "NA")
                data.PresentModes[f[iMode]] = data.PresentModes.GetValueOrDefault(f[iMode]) + 1;
            if (TryNum(f, iPc, out var pc) && pc > 0) data.PcLatencyMs.Add(pc);
            if (TryNum(f, iDisp, out var dl) && dl > 0) data.DisplayLatencyMs.Add(dl);
        }
        return byKey.Values.OrderByDescending(d => d.FrameTimesMs.Count).ToList();
    }

    private static bool TryNum(List<string> f, int i, out double v)
    {
        v = 0;
        return i >= 0 && i < f.Count && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>CSV с поддержкой кавычек (имена приложений могут содержать запятые).</summary>
    public static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') q = false;
                else cur.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }
}
