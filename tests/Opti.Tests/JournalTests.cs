using Microsoft.Data.Sqlite;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;

namespace Opti.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opti-journal-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static readonly string Target = Targets.Reg(@"HKCU\Software\Opti\Test", "Value");

    /// <summary>Журнал в формате версий 1.0–1.2: без user_version и без столбца committed.</summary>
    private void CreateV1Journal()
    {
        using var db = new SqliteConnection($"Data Source={_path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE changes(id INTEGER PRIMARY KEY AUTOINCREMENT, fix_id TEXT NOT NULL, type TEXT NOT NULL,
              target TEXT NOT NULL COLLATE NOCASE, before_json TEXT NOT NULL, after_json TEXT NOT NULL, ts TEXT NOT NULL,
              session_id TEXT NOT NULL, rolled_back INTEGER NOT NULL DEFAULT 0, rolled_back_at TEXT NULL);
            CREATE TABLE sessions(session_id TEXT PRIMARY KEY, started_at TEXT NOT NULL, goal TEXT NULL, restore_point TEXT NULL, notes TEXT NULL);
            INSERT INTO changes(fix_id,type,target,before_json,after_json,ts,session_id)
              VALUES('GAME_MODE_ON','Registry','reg:HKCU\Software\Opti\Test|Value','{"exists":true,"kind":"DWord","value":"0"}',
                     '{"exists":true,"kind":"DWord","value":"1"}','2025-01-01T00:00:00.0000000+00:00','s1');
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void OldJournal_IsMigrated_AndRecordsStayCommitted()
    {
        CreateV1Journal();
        using var j = new SqliteChangeJournal(_path);
        Assert.Equal(SqliteChangeJournal.SchemaVersion, j.Version);
        var rec = Assert.Single(j.GetAll());
        Assert.True(rec.Committed);
        Assert.Equal(StateValue.DWord(0), rec.Before);
    }

    [Fact]
    public void NewerJournal_IsNotOpened()
    {
        using (var j = new SqliteChangeJournal(_path)) { }
        using (var db = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version={SqliteChangeJournal.SchemaVersion + 1};";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidDataException>(() => new SqliteChangeJournal(_path));
        Assert.Contains("более новой версией", ex.Message);
    }

    [Fact]
    public void Change_IsPendingUntilWritten_ThenCommitted()
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        map[ChangeType.Registry].Seed(Target, StateValue.DWord(0));
        using var j = new SqliteChangeJournal(_path);

        var ctx = new ChangeContext(reg, j, "s", "FIX", new SystemProfile());
        ctx.Set(ChangeType.Registry, Target, StateValue.DWord(1));
        var rec = Assert.Single(j.GetAll());
        Assert.True(rec.Committed);

        // Имитация аварии между журналом и изменением: запись осталась неподтверждённой, откат безопасен
        var pending = new ChangeRecord { FixId = "FIX", Type = ChangeType.Registry, Target = Target, Before = StateValue.DWord(0), After = StateValue.DWord(7), SessionId = "s" };
        j.Append(pending);
        Assert.Contains(j.GetAll(), r => !r.Committed);
        var report = new RollbackService(reg, j).RollbackAll();
        Assert.Empty(report.Errors);
        Assert.Equal(StateValue.DWord(0), map[ChangeType.Registry].Read(Target));
    }

    [Fact]
    public void Rollback_LeavesValuesChangedAfterOpti_UnlessForced()
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        map[ChangeType.Registry].Seed(Target, StateValue.DWord(0));
        var j = new InMemoryChangeJournal();
        new ChangeContext(reg, j, "s", "FIX", new SystemProfile()).Set(ChangeType.Registry, Target, StateValue.DWord(1));

        // Пользователь сам поменял значение после Opti
        map[ChangeType.Registry].Write(Target, StateValue.DWord(2));
        var report = new RollbackService(reg, j).RollbackAll();
        var drift = Assert.Single(report.ChangedSinceOpti);
        Assert.Equal(StateValue.DWord(2), drift.Current);
        Assert.Empty(report.Restored);
        Assert.Equal(StateValue.DWord(2), map[ChangeType.Registry].Read(Target));
        Assert.All(j.GetAll(), r => Assert.True(r.RolledBack));
        Assert.Contains("изменено после Opti", report.Summary());

        // То же, но принудительно
        var j2 = new InMemoryChangeJournal();
        new ChangeContext(reg, j2, "s", "FIX", new SystemProfile()).Set(ChangeType.Registry, Target, StateValue.DWord(1));
        map[ChangeType.Registry].Write(Target, StateValue.DWord(3));
        var forced = new RollbackService(reg, j2).RollbackAll(force: true);
        Assert.Single(forced.Restored);
        Assert.Equal(StateValue.DWord(2), map[ChangeType.Registry].Read(Target)); // исходное для второй цепочки — 2
    }

    [Fact]
    public void Rollback_OfChain_RestoresOriginal_WithoutFalseDrift()
    {
        // A/B: одна настройка дважды (вкл → выкл) в одной сессии — исходное значение общее
        var reg = InMemoryBackend.CreateRegistry(out var map);
        map[ChangeType.Registry].Seed(Target, StateValue.DWord(0));
        var j = new InMemoryChangeJournal();
        new ChangeContext(reg, j, "s", "FIX", new SystemProfile()).Set(ChangeType.Registry, Target, StateValue.DWord(1));
        new ChangeContext(reg, j, "s", "FIX", new SystemProfile()).Set(ChangeType.Registry, Target, StateValue.DWord(2));
        var report = new RollbackService(reg, j).RollbackSession("s");
        Assert.Empty(report.ChangedSinceOpti);
        Assert.Equal(StateValue.DWord(0), map[ChangeType.Registry].Read(Target));
    }
}
