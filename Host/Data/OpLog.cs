// Write-ahead operation log for plugin data write-back (Core\LiteBox.pending.db).
//
// One append-only SQLite table records every mutation as an ordered event
// (modify / add / delete) across the LaunchBox XMLs. The log is the source of
// truth until the XML is durably rewritten:
//   - append on each setter (when not ReadOnly),
//   - replay on boot if the previous close didn't finish the flush,
//   - flush to XML at a SAFE time (LB/BB not running) — and the table is cleared
//     ONLY after every .xml swap succeeded (the WAL golden rule; clearing before
//     the swaps would lose data / leave a partial, unrecoverable state).
//
// Crash-safety relies on idempotent replay (see GameStore): modify = last-write-
// wins, delete = delete-if-exists, add = upsert-by-id. GUIDs are minted at op
// time so replay reuses them.
//
// Fail-safe: any SQLite error disables the log for the session (Enabled=false)
// and every method becomes a no-op — write-back silently degrades, the host
// never crashes. Write-back is off by default anyway (GameStore.ReadOnly=true).

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

/// <summary>A single recorded mutation. <see cref="Field"/>/<see cref="Value"/> carry the
/// payload for "modify"; "add" stores its field map as JSON in <see cref="Value"/>; "delete"
/// uses only <see cref="Entity"/>+<see cref="Id"/>.</summary>
internal readonly struct Op
{
    public readonly long Seq;
    public readonly string OpType, Entity, Id, ParentId, Field, Value;
    public Op(long seq, string op, string entity, string id, string parentId, string field, string value)
    { Seq = seq; OpType = op; Entity = entity; Id = id; ParentId = parentId; Field = field; Value = value; }
}

internal sealed class OpLog : IDisposable
{
    private readonly object _lock = new();
    private SqliteConnection _conn;
    private SqliteCommand _insert;
    public bool Enabled { get; private set; }

    private OpLog() { }

    /// <summary>Opens (creates) the log DB. Never throws — returns a disabled log on any failure.</summary>
    public static OpLog Open(string dbPath)
    {
        var log = new OpLog();
        try
        {
            try { SQLitePCL.Batteries_V2.Init(); } catch { /* may already be initialised */ }
            var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false };
            log._conn = new SqliteConnection(csb.ToString());
            log._conn.Open();
            using (var cmd = log._conn.CreateCommand())
            {
                cmd.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;" +
                    "CREATE TABLE IF NOT EXISTS ops(" +
                    " seq INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL," +
                    " op TEXT NOT NULL, entity TEXT NOT NULL, id TEXT, parent_id TEXT," +
                    " field TEXT, value TEXT);";
                cmd.ExecuteNonQuery();
            }
            log._insert = log._conn.CreateCommand();
            log._insert.CommandText =
                "INSERT INTO ops(ts,op,entity,id,parent_id,field,value) VALUES($ts,$op,$e,$id,$pid,$f,$v)";
            foreach (var p in new[] { "$ts", "$op", "$e", "$id", "$pid", "$f", "$v" })
                log._insert.Parameters.Add(new SqliteParameter(p, null));
            log._insert.Prepare();
            log.Enabled = true;
            Console.WriteLine("[oplog] opened " + dbPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[oplog] disabled (open failed): " + ex.Message);
            log.Enabled = false;
            try { log._conn?.Dispose(); } catch { }
            log._conn = null;
        }
        return log;
    }

    /// <summary>Appends one operation. No-op when disabled.</summary>
    public void Append(string op, string entity, string id, string parentId, string field, string value)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            try
            {
                _insert.Parameters["$ts"].Value = DateTime.UtcNow.Ticks;
                _insert.Parameters["$op"].Value = op ?? "";
                _insert.Parameters["$e"].Value = entity ?? "";
                _insert.Parameters["$id"].Value = (object)id ?? DBNull.Value;
                _insert.Parameters["$pid"].Value = (object)parentId ?? DBNull.Value;
                _insert.Parameters["$f"].Value = (object)field ?? DBNull.Value;
                _insert.Parameters["$v"].Value = (object)value ?? DBNull.Value;
                _insert.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[oplog] append failed: " + ex.Message); }
        }
    }

    /// <summary>All ops in seq (chronological) order. Empty when disabled or on error.</summary>
    public List<Op> ReadAll()
    {
        var list = new List<Op>();
        if (!Enabled) return list;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT seq,ts,op,entity,id,parent_id,field,value FROM ops ORDER BY seq";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Op(
                        r.GetInt64(0), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6), Str(r, 7)));
            }
            catch (Exception ex) { Console.WriteLine("[oplog] read failed: " + ex.Message); }
        }
        return list;
    }

    public int Count()
    {
        if (!Enabled) return 0;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM ops";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { return 0; }
        }
    }

    /// <summary>Empties the log. Call ONLY after every target XML has been durably swapped.</summary>
    public void Clear()
    {
        if (!Enabled) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM ops; DELETE FROM sqlite_sequence WHERE name='ops';";
                cmd.ExecuteNonQuery();
                using var wal = _conn.CreateCommand();
                wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                wal.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[oplog] clear failed: " + ex.Message); }
        }
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public void Dispose()
    {
        lock (_lock)
        {
            try { _insert?.Dispose(); } catch { }
            try { _conn?.Dispose(); } catch { }
            _conn = null; _insert = null; Enabled = false;
        }
    }
}
