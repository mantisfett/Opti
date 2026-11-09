using Opti.Core.Fixes;

namespace Opti.Fixes.Journal;

public sealed class InMemoryChangeJournal : IChangeJournal
{
    private readonly List<ChangeRecord> _records = new();
    private readonly List<SessionInfo> _sessions = new();
    private long _nextId = 1;

    public StateValue? GetActiveOriginal(ChangeType type, string target) =>
        _records.Where(r => !r.RolledBack && r.Type == type && string.Equals(r.Target, target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Id).FirstOrDefault()?.Before;

    public long Append(ChangeRecord record)
    {
        record.Id = _nextId++;
        record.Committed = false;
        _records.Add(record);
        return record.Id;
    }

    public void Remove(long recordId) => _records.RemoveAll(r => r.Id == recordId);

    public void Commit(long recordId)
    {
        var r = _records.FirstOrDefault(x => x.Id == recordId);
        if (r is not null) r.Committed = true;
    }

    public void MarkRolledBack(long recordId, DateTimeOffset at)
    {
        var r = _records.First(x => x.Id == recordId);
        r.RolledBack = true;
        r.RolledBackAt = at;
    }

    public IReadOnlyList<ChangeRecord> GetAll() => _records.OrderBy(r => r.Id).ToList();

    public void SaveSession(SessionInfo session)
    {
        _sessions.RemoveAll(s => s.SessionId == session.SessionId);
        _sessions.Add(session);
    }

    public IReadOnlyList<SessionInfo> GetSessions() => _sessions.OrderBy(s => s.StartedAt).ToList();
}
