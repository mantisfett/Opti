using Opti.Core.Fixes;
using Opti.Core.Localization;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Catalog;
using Opti.Fixes.Services;

namespace Opti.Workflow;

public sealed record GameSessionResult(IReadOnlyList<string> Closed, IReadOnlyList<string> NotClosed, bool PowerChanged, string? PowerNote);

/// <summary>
/// «Игровая сессия»: перед игрой закрывает выбранные фоновые программы и на время игры включает схему питания
/// «Высокая производительность»; по кнопке «Завершить» схема возвращается. Схема питания идёт через журнал
/// (откат в «Истории» тоже работает), закрытые программы Opti не перезапускает — они откроются, как обычно, сами или вами.
/// Ноутбук и работа от батареи схему не меняют: там это только посадит заряд.
/// </summary>
public sealed class GameSession
{
    public const string SessionId = "game-session";
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;
    private readonly Func<SystemProfile> _readPower;
    private readonly Func<string, int> _closer;

    /// <param name="readPower">Профиль с устройством и питанием (ноутбук ли, какие схемы есть).</param>
    /// <param name="closer">Закрывает программу по имени, возвращает число закрытых процессов.</param>
    public GameSession(IBackendRegistry backends, IChangeJournal journal, Func<SystemProfile> readPower, Func<string, int> closer)
    {
        _backends = backends;
        _journal = journal;
        _readPower = readPower;
        _closer = closer;
    }

    public bool IsActive => _journal.GetAll().Any(r => r.SessionId == SessionId && !r.RolledBack);

    public GameSessionResult Start(IEnumerable<string> apps, bool powerPlan)
    {
        var closed = new List<string>();
        var notClosed = new List<string>();
        foreach (var name in apps.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int n;
            try { n = BackgroundScanner.IsProtected(name) ? 0 : _closer(name); }
            catch { n = 0; }
            (n > 0 ? closed : notClosed).Add(name);
        }

        bool changed = false;
        string? note = null;
        if (powerPlan)
        {
            try
            {
                var p = _readPower();
                var fix = new PowerSchemePerfFix();
                if (!fix.IsApplicable(p)) note = Loc.T("Схема питания не менялась: на ноутбуке и при работе от батареи это только посадит заряд (или схемы нет).");
                else if (fix.IsAlreadyApplied(p)) note = Loc.T("Схема питания уже «Высокая производительность».");
                else
                {
                    var report = new ApplyService(_backends, _journal, null).Apply(p, new IFix[] { fix }, UserGoal.Balanced, null, SessionId);
                    var r = report.Results.FirstOrDefault();
                    changed = r?.Outcome == FixOutcome.Applied;
                    if (r?.Outcome == FixOutcome.Failed) note = Loc.T("Не удалось сменить схему питания: ") + r.Error;
                }
            }
            catch (Exception ex) { note = Loc.T("Не удалось сменить схему питания: ") + ex.Message; }
        }
        return new GameSessionResult(closed, notClosed, changed, note);
    }

    /// <summary>Возвращает схему питания, какая была до сессии.</summary>
    public RollbackReport End() => new RollbackService(_backends, _journal).RollbackSession(SessionId);
}
