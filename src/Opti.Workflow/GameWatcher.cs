using Opti.Core.Diagnostics;

namespace Opti.Workflow;

/// <summary>
/// Автоматическая игровая сессия: когда запускается одна из выбранных игр, включается схема питания «Высокая производительность»,
/// когда игра закрыта — прежняя схема возвращается. Программы не закрываются (без вашего согласия это слишком навязчиво).
/// Если сессию вы начали вручную, наблюдатель её не трогает.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private readonly GameSession _game;
    private readonly Func<bool> _enabled;
    private readonly Func<IEnumerable<string>> _games;
    private readonly Func<ISet<string>> _running;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _owned;

    /// <param name="running">Имена запущенных процессов (без «.exe»).</param>
    public GameWatcher(GameSession game, Func<bool> enabled, Func<IEnumerable<string>> games, Func<ISet<string>> running)
    {
        _game = game;
        _enabled = enabled;
        _games = games;
        _running = running;
    }

    /// <summary>true — сессию начал и держит сам наблюдатель.</summary>
    public bool OwnsSession => _owned;

    /// <summary>Строка настроек («game.exe, Another; third») → имена процессов без «.exe».</summary>
    public static IReadOnlyList<string> ParseNames(string? text) =>
        (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Один шаг проверки. Возвращает true, если идёт игра из списка.</summary>
    public bool Tick()
    {
        lock (_lock)
        {
            if (!_enabled())
            {
                if (_owned) EndOwned();
                return false;
            }
            var playing = IsPlaying();
            if (playing && !_owned && !_game.IsActive)
            {
                var r = _game.Start(Array.Empty<string>(), powerPlan: true);
                if (r.PowerChanged)
                {
                    _owned = true;
                    OptiLog.Info("Игра запущена — включена схема «Высокая производительность»");
                }
            }
            else if (!playing && _owned) EndOwned();
            return playing;
        }
    }

    private bool IsPlaying()
    {
        var wanted = _games().Select(n => n.Trim()).Where(n => n.Length > 0)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Count > 0 && _running().Any(wanted.Contains);
    }

    private void EndOwned()
    {
        _owned = false;
        var report = _game.End();
        OptiLog.Info(report.Errors.Count == 0 ? "Игра закрыта — схема питания возвращена" : "Не удалось вернуть схему питания: " + string.Join("; ", report.Errors));
    }

    public void Start(TimeSpan interval)
    {
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            try { Tick(); } catch (Exception ex) { OptiLog.Warn("Наблюдатель игр: ошибка проверки", ex); }
        }, null, interval, interval);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        // При выходе из Opti: игры уже нет — вернуть схему; игра ещё идёт — оставляем (вернётся «Завершить сессию» или откат в «Истории»)
        try { lock (_lock) { if (_owned && !(_enabled() && IsPlaying())) EndOwned(); } }
        catch { /* выход из программы */ }
    }
}
