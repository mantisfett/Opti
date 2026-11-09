namespace Opti.Workflow;

/// <summary>
/// Один работающий экземпляр Opti на компьютер (окно или команда, которая меняет систему).
/// Два процесса, одновременно пишущие в журнал и реестр, могут перепутать исходные значения для отката.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Global\Opti.SingleInstance";
    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>Пытается занять блокировку. null — уже работает другой экземпляр.</summary>
    public static SingleInstance? TryAcquire(TimeSpan? wait = null, string name = MutexName)
    {
        var m = new Mutex(false, name);
        bool owned;
        try { owned = m.WaitOne(wait ?? TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; } // прошлый экземпляр аварийно завершился — блокировка наша
        if (owned) return new SingleInstance(m, true);
        m.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* уже освобождён */ }
            _owned = false;
        }
        _mutex.Dispose();
    }
}
