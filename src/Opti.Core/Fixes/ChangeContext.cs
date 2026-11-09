using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Core.Fixes;

/// <summary>
/// Единственный путь, через который исправления меняют систему.
/// Гарантии:
///  1) исходное значение читается и пишется в журнал ДО изменения;
///  2) если прочитать/записать исходное не удалось — изменение не выполняется;
///  3) исходное фиксируется один раз: повторное применение не затирает оригинал.
/// </summary>
public sealed class ChangeContext
{
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;
    private readonly List<ChangeRecord> _records = new();

    public ChangeContext(IBackendRegistry backends, IChangeJournal journal, string sessionId, string fixId,
        SystemProfile profile, IReadOnlyDictionary<string, string>? options = null)
    {
        Profile = profile;
        _backends = backends;
        _journal = journal;
        SessionId = sessionId;
        FixId = fixId;
        Options = options ?? new Dictionary<string, string>();
    }

    public string SessionId { get; }
    /// <summary>Профиль, по которому строился план.</summary>
    public SystemProfile Profile { get; }
    public string FixId { get; }
    /// <summary>Параметры, выбранные пользователем (например, какие пункты автозагрузки отключить).</summary>
    public IReadOnlyDictionary<string, string> Options { get; }
    public IReadOnlyList<ChangeRecord> Records => _records;

    public StateValue Read(ChangeType type, string target) => _backends.Get(type).Read(target);

    /// <summary>Устанавливает значение с журналированием. Возвращает null, если значение уже такое.</summary>
    public ChangeRecord? Set(ChangeType type, string target, StateValue value)
    {
        var backend = _backends.Get(type);
        StateValue current;
        try { current = backend.Read(target); }
        catch (Exception ex) { throw new ChangeAbortedException(Loc.T($"Не удалось прочитать исходное значение {type} {target}: {ex.Message}"), ex); }

        if (current == value) return null;

        var original = _journal.GetActiveOriginal(type, target) ?? current;
        var record = new ChangeRecord
        {
            FixId = FixId, Type = type, Target = target,
            Before = original, After = value, SessionId = SessionId,
        };
        try { record.Id = _journal.Append(record); }
        catch (Exception ex) { throw new ChangeAbortedException(Loc.T($"Не удалось сохранить исходное значение в журнал: {ex.Message}"), ex); }

        try { backend.Write(target, value); }
        catch (Exception ex)
        {
            // изменение не произошло — запись в журнале не нужна
            _journal.Remove(record.Id);
            throw new ChangeAbortedException(Loc.T($"Не удалось изменить {type} {target}: {ex.Message}"), ex);
        }
        // Настройка уже изменена: если подтверждение не запишется, запись останется «неподтверждённой»,
        // но исходное значение в ней есть — откат всё равно возможен.
        try { _journal.Commit(record.Id); record.Committed = true; }
        catch (Exception ex)
        {
            record.Committed = false;
            Opti.Core.Diagnostics.OptiLog.Warn($"Изменение {type} {target} сделано, но не подтверждено в журнале", ex);
        }
        _records.Add(record);
        return record;
    }

    public ChangeRecord? Delete(ChangeType type, string target) => Set(type, target, StateValue.Missing);
}

public sealed class ChangeAbortedException : Exception
{
    public ChangeAbortedException(string message, Exception? inner = null) : base(message, inner) { }
}
