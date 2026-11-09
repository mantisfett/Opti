using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Tweaks;
using Opti.Fixes.Backends;
using Opti.Core.Localization;

namespace Opti.Fixes.Services;

/// <summary>
/// Включение и выключение твиков удобства через общий журнал изменений.
/// Включение пишет исходные значения в журнал до изменения; выключение откатывает именно эти записи,
/// поэтому возвращается состояние, которое было до Opti (а не «какое-то стандартное»).
/// </summary>
public sealed class TweakService
{
    public const string SessionId = "tweaks";
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;

    public TweakService(IBackendRegistry backends, IChangeJournal journal)
    {
        _backends = backends;
        _journal = journal;
    }

    public bool IsAvailable(Tweak t, int osBuild) => osBuild == 0 || osBuild >= t.MinBuild;

    public bool IsOn(Tweak t)
    {
        try
        {
            var reg = _backends.Get(ChangeType.Registry);
            return t.Values.All(v => reg.Read(Targets.Reg(v.Key, v.Name)) is { Exists: true } s && string.Equals(s.Value, v.On, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    public void Enable(Tweak t)
    {
        if (!_journal.GetSessions().Any(s => s.SessionId == SessionId))
            _journal.SaveSession(new SessionInfo { SessionId = SessionId, Goal = Loc.T("Твики"), Notes = Loc.T("Твики удобства") });
        var ctx = new ChangeContext(_backends, _journal, SessionId, t.FixId, new SystemProfile());
        var done = new List<ChangeRecord>();
        try
        {
            foreach (var v in t.Values)
            {
                var value = v.Kind == "DWord" ? StateValue.DWord(uint.Parse(v.On)) : StateValue.Str(v.On);
                if (ctx.Set(ChangeType.Registry, Targets.Reg(v.Key, v.Name), value) is { } r) done.Add(r);
            }
        }
        catch
        {
            // Твик атомарный: не получилось целиком — возвращаем то, что успели изменить
            new RollbackService(_backends, _journal).RollbackRecords(done, force: true);
            throw;
        }
    }

    /// <summary>Выключает твик: откатывает записи журнала. Если Opti его не включал (был включён до нас) — удаляет значения.</summary>
    public void Disable(Tweak t)
    {
        var active = _journal.GetAll().Where(r => r.FixId == t.FixId && !r.RolledBack).ToList();
        if (active.Count > 0)
        {
            var report = new RollbackService(_backends, _journal).RollbackRecords(active);
            if (report.Errors.Count > 0) throw new InvalidOperationException(string.Join("; ", report.Errors));
            return;
        }
        var ctx = new ChangeContext(_backends, _journal, SessionId, t.FixId, new SystemProfile());
        foreach (var v in t.Values) ctx.Delete(ChangeType.Registry, Targets.Reg(v.Key, v.Name));
    }
}
