using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Opti.Core.Localization;

namespace Opti.Measure.Capture;

public sealed record PresentProcessStats(int Pid, string Name, int Presents);

/// <summary>
/// Сбор моментов Present через ETW (провайдеры DXGI и D3D9), как MsBetweenPresents в PresentMon.
/// Только чтение событий — в процесс игры ничего не внедряется (безопасно для античитов).
/// Требует прав администратора.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EtwPresentCapture : IDisposable
{
    public static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    public static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "dwm", "explorer", "obs64", "discord", "steamwebhelper", "msedgewebview2", "chrome", "msedge", "firefox", "Opti", "opti" };

    private readonly object _lock = new();
    private readonly Dictionary<int, List<double>> _presents = new();
    private TraceEventSession? _session;
    private Task? _pump;

    public string SessionName { get; } = "Opti-Present-" + Environment.ProcessId;

    public void Start()
    {
        if (!(TraceEventSession.IsElevated() ?? false))
            throw new UnauthorizedAccessException(Loc.T("Для замера кадров через ETW нужны права администратора."));
        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
        _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
        var parser = new RegisteredTraceEventParser(_session.Source);
        parser.All += OnEvent;
        _pump = Task.Run(() => _session.Source.Process());
    }

    private void OnEvent(TraceEvent e)
    {
        bool present = (e.ProviderGuid == DxgiProvider && (int)e.ID == DxgiPresentStart)
                       || (e.ProviderGuid == D3D9Provider && (int)e.ID == D3D9PresentStart);
        if (!present) return;
        lock (_lock)
        {
            if (!_presents.TryGetValue(e.ProcessID, out var list)) _presents[e.ProcessID] = list = new List<double>(8192);
            list.Add(e.TimeStampRelativeMSec);
        }
    }

    public void Stop()
    {
        _session?.Stop();
        _pump?.Wait(TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<PresentProcessStats> GetProcesses()
    {
        lock (_lock)
            return _presents.Select(kv => new PresentProcessStats(kv.Key, ProcessName(kv.Key), kv.Value.Count))
                .OrderByDescending(p => p.Presents).ToList();
    }

    /// <summary>Процесс игры: больше всего кадров, исключая DWM, браузеры, OBS и т. п.</summary>
    public PresentProcessStats? GuessGameProcess(string? preferredName = null)
    {
        var procs = GetProcesses();
        if (preferredName is not null)
            return procs.FirstOrDefault(p => p.Name.Equals(Path.GetFileNameWithoutExtension(preferredName), StringComparison.OrdinalIgnoreCase));
        return procs.FirstOrDefault(p => !IgnoredProcesses.Contains(p.Name));
    }

    public List<double> GetTimestamps(int pid)
    {
        lock (_lock) return _presents.TryGetValue(pid, out var l) ? new List<double>(l) : new List<double>();
    }

    private static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return $"pid {pid}"; }
    }

    public void Dispose() => _session?.Dispose();
}
