namespace Opti.Core.Fixes;

/// <summary>
/// Унифицированный доступ к одному виду настроек. Read должен вернуть точное
/// текущее состояние, Write(Missing) — удалить значение. Откат = Write(before).
/// </summary>
public interface IStateBackend
{
    ChangeType Type { get; }
    StateValue Read(string target);
    void Write(string target, StateValue value);
}

public interface IBackendRegistry
{
    IStateBackend Get(ChangeType type);
}

public sealed class BackendRegistry : IBackendRegistry
{
    private readonly Dictionary<ChangeType, IStateBackend> _map;
    public BackendRegistry(IEnumerable<IStateBackend> backends) => _map = backends.ToDictionary(b => b.Type);
    public IStateBackend Get(ChangeType type) =>
        _map.TryGetValue(type, out var b) ? b : throw new NotSupportedException($"Нет бэкенда для {type}");
}

/// <summary>Журнал изменений (SQLite в продукте, память в тестах).</summary>
public interface IChangeJournal
{
    /// <summary>Самое раннее исходное значение для активной (не откаченной) цепочки изменений этой настройки.</summary>
    StateValue? GetActiveOriginal(ChangeType type, string target);
    /// <summary>Записывает изменение как неподтверждённое. Вызывается ДО фактического изменения настройки.</summary>
    long Append(ChangeRecord record);
    /// <summary>Подтверждает, что настройка изменена (вызывается ПОСЛЕ изменения).</summary>
    void Commit(long recordId) { }
    void Remove(long recordId);
    void MarkRolledBack(long recordId, DateTimeOffset at);
    IReadOnlyList<ChangeRecord> GetAll();
    void SaveSession(SessionInfo session);
    IReadOnlyList<SessionInfo> GetSessions();
}

public sealed class SessionInfo
{
    public required string SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public string? Goal { get; set; }
    public string? RestorePoint { get; set; }
    public string? Notes { get; set; }
}

public interface IRestorePointService
{
    /// <summary>Создаёт точку восстановления. Возвращает описание или null, если защита системы выключена.</summary>
    string? TryCreate(string description, out string? error);
}
