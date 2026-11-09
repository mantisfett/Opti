using Opti.Core.Localization;

namespace Opti.Measure.Capture;

/// <summary>
/// Сессия «NT Kernel Logger» в системе одна: два замера ядра (задержки и сетевой трафик) одновременно не работают.
/// Вторая попытка получает понятную ошибку вместо загадочного сбоя.
/// </summary>
public static class EtwGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable Enter()
    {
        if (!Gate.Wait(0)) throw new InvalidOperationException(Loc.T("Другой замер на событиях ядра уже идёт (задержки или сетевой трафик). Дождитесь его окончания."));
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Gate.Release(); }
    }
}
