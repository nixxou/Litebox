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
using System.Linq;
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
            log._conn = SqliteBootstrap.OpenConnection(dbPath);
            // DB schema version (PRAGMA user_version): drop LiteBox's tables on a breaking bump, then stamp
            // the current version. On the current rollout ResetPendingDbBelow=0.0.0 → nothing is ever reset.
            int uv = ReadUserVersion(log._conn);
            int resetBelow = LbApiHost.Host.Install.LiteBoxVersion.Encode(LbApiHost.Host.Install.LiteBoxVersion.ResetPendingDbBelow);
            int cur = LbApiHost.Host.Install.LiteBoxVersion.Encode(LbApiHost.Host.Install.LiteBoxVersion.Current);
            bool reset = uv < resetBelow;
            using (var cmd = log._conn.CreateCommand())
            {
                cmd.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;" +
                    (reset ? "DROP TABLE IF EXISTS ops; DROP TABLE IF EXISTS launch_history;" : "") +
                    "CREATE TABLE IF NOT EXISTS ops(" +
                    " seq INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL," +
                    " op TEXT NOT NULL, entity TEXT NOT NULL, id TEXT, parent_id TEXT," +
                    " field TEXT, value TEXT);" +
                    // LiteBox's own last-launch history — SAME schema as ExtendDB's launch_history so the
                    // two are interchangeable. Persistent (NOT cleared with the ops on flush). LiteBox
                    // never tracks the ROM, so extracted_rom_path stays NULL here.
                    "CREATE TABLE IF NOT EXISTS launch_history(" +
                    " game_id TEXT NOT NULL PRIMARY KEY, additional_app_id TEXT, emulator_id TEXT," +
                    // detection_ms: launch → SmartCapture-detection latency (LiteBox-only; NULL until a
                    // launch under LiteBox actually detects the game window). Reused to extend the reveal
                    // ceiling and feed the startup progress bar. Same column added to ExtendDB's schema.
                    " extracted_rom_path TEXT, last_launched_utc TEXT NOT NULL, detection_ms INTEGER);" +
                    $"PRAGMA user_version={cur};";
                cmd.ExecuteNonQuery();
            }
            // Migrate pre-existing launch_history tables (created before detection_ms): add the column.
            try { using var alter = log._conn.CreateCommand(); alter.CommandText = "ALTER TABLE launch_history ADD COLUMN detection_ms INTEGER;"; alter.ExecuteNonQuery(); }
            catch { /* column already present */ }
            if (reset) Console.WriteLine($"[oplog] schema reset (user_version {uv} < {resetBelow})");
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

    /// <summary>Removes specific ops by seq — the scoped-flush counterpart of <see cref="Clear"/>.
    /// Same golden rule: call ONLY after the target XML swap succeeded.</summary>
    public void DeleteSeqs(IReadOnlyList<long> seqs)
    {
        if (!Enabled || seqs == null || seqs.Count == 0) return;
        lock (_lock)
        {
            try
            {
                using (var tx = _conn.BeginTransaction())
                {
                    for (int i = 0; i < seqs.Count; i += 500)
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM ops WHERE seq IN (" + string.Join(",", seqs.Skip(i).Take(500)) + ")";
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                using var wal = _conn.CreateCommand();
                wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                wal.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[oplog] delete seqs failed: " + ex.Message); }
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

    // ── LiteBox launch history (separate from the ops table; survives flush/Clear) ──────────
    /// <summary>Upsert the last emulator/version used for a game (LiteBox's own copy; ROM left NULL).
    /// NOT gated by ReadOnly — this is LiteBox state, not LaunchBox write-back. No-op when disabled.</summary>
    public void RecordLaunch(string gameId, string emulatorId, string additionalAppId)
    {
        if (!Enabled || string.IsNullOrEmpty(gameId)) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                // UPSERT (NOT INSERT OR REPLACE, which deletes+reinserts the whole row and would WIPE
                // detection_ms every launch — the value the progress bar / ceiling read back next time).
                cmd.CommandText =
                    "INSERT INTO launch_history(game_id, additional_app_id, emulator_id, extracted_rom_path, last_launched_utc) " +
                    "VALUES($g,$a,$e,NULL,$t) " +
                    "ON CONFLICT(game_id) DO UPDATE SET additional_app_id=excluded.additional_app_id, " +
                    "emulator_id=excluded.emulator_id, last_launched_utc=excluded.last_launched_utc";
                cmd.Parameters.AddWithValue("$g", gameId);
                cmd.Parameters.AddWithValue("$a", (object)additionalAppId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$e", (object)emulatorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[oplog] launch record failed: " + ex.Message); }
        }
    }

    /// <summary>Deletes the game's launch-history row — the reset-to-default button cancels the
    /// entry so the next GetLastLaunch seeds pure defaults. NOT gated by ReadOnly (LiteBox state,
    /// same as RecordLaunch). No-op when disabled or no row exists.</summary>
    public void ClearLaunch(string gameId)
    {
        if (!Enabled || string.IsNullOrEmpty(gameId)) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM launch_history WHERE game_id=$g";
                cmd.Parameters.AddWithValue("$g", gameId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[oplog] launch clear failed: " + ex.Message); }
        }
    }

    /// <summary>The last (emulatorId, additionalAppId) recorded for a game, or null if none. Either
    /// field may be null (= default emulator / Base version).</summary>
    public (string emulatorId, string additionalAppId)? GetLastLaunch(string gameId)
    {
        if (!Enabled || string.IsNullOrEmpty(gameId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT emulator_id, additional_app_id FROM launch_history WHERE game_id=$g";
                cmd.Parameters.AddWithValue("$g", gameId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
            }
            catch (Exception ex) { Console.WriteLine("[oplog] launch get failed: " + ex.Message); }
        }
        return null;
    }

    /// <summary>Record the launch → SmartCapture-detection latency (ms) for a game. UPSERT that ONLY
    /// touches detection_ms — preserves the emulator/app/rom columns (RecordLaunch wrote them). Creates
    /// a bare row (with last_launched_utc) for a game that has none yet (e.g. a store launch). No-op
    /// when disabled.</summary>
    public void RecordDetection(string gameId, long detectionMs)
    {
        if (!Enabled || string.IsNullOrEmpty(gameId)) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO launch_history(game_id, last_launched_utc, detection_ms) VALUES($g,$t,$d) " +
                    "ON CONFLICT(game_id) DO UPDATE SET detection_ms=excluded.detection_ms";
                cmd.Parameters.AddWithValue("$g", gameId);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$d", detectionMs);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[oplog] detection record failed: " + ex.Message); }
        }
    }

    /// <summary>The last recorded launch→detection latency (ms) for a game, or null if none / never
    /// detected. Used to extend the reveal ceiling and drive the startup progress bar.</summary>
    public long? GetLastDetectionMs(string gameId)
    {
        if (!Enabled || string.IsNullOrEmpty(gameId)) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT detection_ms FROM launch_history WHERE game_id=$g";
                cmd.Parameters.AddWithValue("$g", gameId);
                using var r = cmd.ExecuteReader();
                if (r.Read() && !r.IsDBNull(0)) return r.GetInt64(0);
            }
            catch (Exception ex) { Console.WriteLine("[oplog] detection get failed: " + ex.Message); }
        }
        return null;
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int ReadUserVersion(SqliteConnection conn)
    {
        try { using var c = conn.CreateCommand(); c.CommandText = "PRAGMA user_version;"; return Convert.ToInt32(c.ExecuteScalar()); }
        catch { return 0; }
    }

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
