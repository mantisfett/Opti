using Opti.Core.Profile;

namespace Opti.Measure.Capture;

/// <summary>Результат теста задержек системы: DPC/ISR по драйверам и точность таймера.</summary>
public sealed record LatencyReport(DpcLatencyInfo Dpc, TimerAccuracyResult Timer);
