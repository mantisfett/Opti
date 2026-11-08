using System.Globalization;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Microsoft.Data.Sqlite;
using Opti.Core.Localization;

namespace Opti.Fixes.Journal;

/// <summary>
/// Журнал изменений в SQLite (C:\ProgramData\Opti\journal.db).
/// Версия схемы хранится в PRAGMA user_version; при открытии журнал обновляется до текущей версии в транзакции.
/// Журнал более новой версии не открывается: старая программа могла бы неправильно его прочитать и откатить не то.
/// </summary>
public sealed class SqliteChangeJournal : IChangeJournal, IDisposable
{
    /// <summary>1 — версии 1.0–1.2; 2 — подтверждение записи (столбец committed).</summary>
    public const int SchemaVersion = 2;
    private readonly SqliteConnection _db;

    public SqliteChangeJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _db.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=FULL;");
            Exec("PRAGMA busy_timeout=5000;");
            Migrate();
        }
        catch
        {
            // Журнал не открылся (например, он новее программы) — файл не должен остаться занятым
            _db.Dispose();
            throw;
        }
    }

    public int Version => Convert.ToInt32(Scalar("PRAGMA user_version;"));

    private void Migrate()
    {
        int version = Version;
        if (version > SchemaVersion)
            throw new InvalidDataException(Loc.T($"Журнал отката создан более новой версией Opti (схема {version}, эта программа знает {SchemaVersion}). ") +
                                           Loc.T("Обновите Opti, чтобы не повредить журнал."));
        if (version == SchemaVersion) return;

        using var tx = _db.BeginTransaction();
        // Версия 1 (и пустая база): исходные таблицы. Для старых журналов user_version = 0, но таблицы уже есть.
        Exec("""
            CREATE TABLE IF NOT EXISTS changes(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              fix_id TEXT NOT NULL,
              type TEXT NOT NULL,
              target TEXT NOT NULL COLLATE NOCASE,
              before_json TEXT NOT NULL,
              after_json TEXT NOT NULL,
              ts TEXT NOT NULL,
              session_id TEXT NOT NULL,
              rolled_back INTEGER NOT NULL DEFAULT 0,
              rolled_back_at TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_changes_target ON changes(type, target);
            CREATE TABLE IF NOT EXISTS sessions(
              session_id TEXT PRIMARY KEY,
              started_at TEXT NOT NULL,
              goal TEXT NULL,
              restore_point TEXT NULL,
              notes TEXT NULL);
            """, tx);
        // Версия 2: подтверждение записи. Старые записи считаются подтверждёнными.
        if (!HasColumn("changes", "committed", tx))
            Exec("ALTER TABLE changes ADD COLUMN committed INTEGER NOT NULL DEFAULT 1;", tx);
        Exec($"PRAGMA user_version={SchemaVersion};", tx);
        tx.Commit();
    }

    private bool HasColumn(string table, string column, SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$c";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public StateValue? GetActiveOriginal(ChangeType type, string target)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT before_json FROM changes WHERE type=$t AND target=$g AND rolled_back=0 ORDER BY id LIMIT 1";
        cmd.Parameters.AddWithValue("$t", type.ToString());
        cmd.Parameters.AddWithValue("$g", target);
        return cmd.ExecuteScalar() is string json ? OptiJson.Deserialize<StateValue>(json) : null;
    }

    public long Append(ChangeRecord r)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO changes(fix_id,type,target,before_json,after_json,ts,session_id,committed)
            VALUES($f,$t,$g,$b,$a,$ts,$s,0); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", r.FixId);
        cmd.Parameters.AddWithValue("$t", r.Type.ToString());
        cmd.Parameters.AddWithValue("$g", r.Target);
        cmd.Parameters.AddWithValue("$b", OptiJson.Serialize(r.Before));
        cmd.Parameters.AddWithValue("$a", OptiJson.Serialize(r.After));
        cmd.Parameters.AddWithValue("$ts", r.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$s", r.SessionId);
        return (long)cmd.ExecuteScalar()!;
    }

    public void Remove(long recordId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM changes WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.ExecuteNonQuery();
    }

    public void Commit(long recordId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE changes SET committed=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.ExecuteNonQuery();
    }

    public void MarkRolledBack(long recordId, DateTimeOffset at)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE changes SET rolled_back=1, rolled_back_at=$at WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ChangeRecord> GetAll()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,fix_id,type,target,before_json,after_json,ts,session_id,rolled_back,rolled_back_at,committed FROM changes ORDER BY id";
        using var rd = cmd.ExecuteReader();
        var list = new List<ChangeRecord>();
        while (rd.Read())
        {
            list.Add(new ChangeRecord
            {
                Id = rd.GetInt64(0),
                FixId = rd.GetString(1),
                Type = Enum.Parse<ChangeType>(rd.GetString(2)),
                Target = rd.GetString(3),
                Before = OptiJson.Deserialize<StateValue>(rd.GetString(4)),
                After = OptiJson.Deserialize<StateValue>(rd.GetString(5)),
                Timestamp = DateTimeOffset.Parse(rd.GetString(6), CultureInfo.InvariantCulture),
                SessionId = rd.GetString(7),
                RolledBack = rd.GetInt64(8) != 0,
                RolledBackAt = rd.IsDBNull(9) ? null : DateTimeOffset.Parse(rd.GetString(9), CultureInfo.InvariantCulture),
                Committed = rd.GetInt64(10) != 0,
            });
        }
        return list;
    }

    public void SaveSession(SessionInfo s)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions(session_id,started_at,goal,restore_point,notes) VALUES($id,$at,$g,$rp,$n)
            ON CONFLICT(session_id) DO UPDATE SET goal=excluded.goal, restore_point=excluded.restore_point, notes=excluded.notes;
            """;
        cmd.Parameters.AddWithValue("$id", s.SessionId);
        cmd.Parameters.AddWithValue("$at", s.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$g", (object?)s.Goal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rp", (object?)s.RestorePoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)s.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SessionInfo> GetSessions()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT session_id,started_at,goal,restore_point,notes FROM sessions ORDER BY started_at";
        using var rd = cmd.ExecuteReader();
        var list = new List<SessionInfo>();
        while (rd.Read())
            list.Add(new SessionInfo
            {
                SessionId = rd.GetString(0),
                StartedAt = DateTimeOffset.Parse(rd.GetString(1), CultureInfo.InvariantCulture),
                Goal = rd.IsDBNull(2) ? null : rd.GetString(2),
                RestorePoint = rd.IsDBNull(3) ? null : rd.GetString(3),
                Notes = rd.IsDBNull(4) ? null : rd.GetString(4),
            });
        return list;
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _db.Dispose();
}
