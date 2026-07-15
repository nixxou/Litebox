// LiteBox's own options store — a small local SQLite DB (Core\litebox\litebox-options.db),
// SEPARATE from the op-log DB so an op-log schema reset never drops user settings.
//
// Two jobs, one table:
//   1. Home for Settings.xml keys that can't safely live in the current LB's XML
//      (ProblemKeys.IsDbManaged) — scope="global", entity_id="".
//   2. Home for the EXTRA per-entity options LiteBox adds that LaunchBox has no field for
//      (startup-screen tweaks, and whatever we add next) — scope="game"|"emulator"|
//      "platform"|"playlist", entity_id = the entity's id.
//
// Schema — a key/value table (EAV) rather than a wide typed table on purpose: a new option
// is a new KEY, never a schema migration, and one table serves both the arbitrary global
// keys (job 1) and the growing per-entity set (job 2). Sparse: a row exists only when an
// option is actually set, so 100k games with no custom option cost 0 rows and never touch a
// list load — the launch path reads one entity's keys by an indexed PK lookup.
//
//   options(scope TEXT, entity_id TEXT, key TEXT, value TEXT, PRIMARY KEY(scope, entity_id, key))
//
// Resolution (global < platform < emulator < game) is the CALLER's job (it knows the ids);
// this class is the flat get/set/list substrate. Every method swallows + logs, never throws.

#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

internal static class LiteBoxOptionsDb
{
    public const string Global = "global";

    private static SqliteConnection? _conn;
    private static readonly object _lock = new();
    private static bool _tried;

    public static bool Enabled => _conn != null;

    /// <summary>Open (create) the DB. Idempotent; never throws. Call once at boot.</summary>
    public static void Open(string? dbPath = null)
    {
        lock (_lock)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                dbPath ??= LiteBoxPaths.File("litebox-options.db");
                _conn = SqliteBootstrap.OpenConnection(dbPath);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS options(" +
                    "  scope TEXT NOT NULL, entity_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT," +
                    "  PRIMARY KEY(scope, entity_id, key)) WITHOUT ROWID;";
                cmd.ExecuteNonQuery();
                Console.WriteLine($"[options-db] open {dbPath}");
            }
            catch (Exception ex) { Console.WriteLine("[options-db] open failed: " + ex.Message); _conn = null; }
        }
    }

    /// <summary>The value for (scope, entityId, key), or null when unset.</summary>
    public static string? Get(string scope, string entityId, string key)
    {
        lock (_lock)
        {
            if (_conn == null) return null;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM options WHERE scope=$s AND entity_id=$e AND key=$k";
                cmd.Parameters.AddWithValue("$s", scope);
                cmd.Parameters.AddWithValue("$e", entityId ?? "");
                cmd.Parameters.AddWithValue("$k", key);
                var o = cmd.ExecuteScalar();
                return o == null || o is DBNull ? null : (string)o;
            }
            catch (Exception ex) { Console.WriteLine("[options-db] get failed: " + ex.Message); return null; }
        }
    }

    /// <summary>Upsert (scope, entityId, key) = value. A null/empty value DELETES the row
    /// (back to "unset" → the resolver falls through to the wider scope / default).</summary>
    public static void Set(string scope, string entityId, string key, string? value)
    {
        lock (_lock)
        {
            if (_conn == null) return;
            try
            {
                using var cmd = _conn.CreateCommand();
                if (string.IsNullOrEmpty(value))
                {
                    cmd.CommandText = "DELETE FROM options WHERE scope=$s AND entity_id=$e AND key=$k";
                }
                else
                {
                    cmd.CommandText =
                        "INSERT INTO options(scope, entity_id, key, value) VALUES($s,$e,$k,$v) " +
                        "ON CONFLICT(scope, entity_id, key) DO UPDATE SET value=excluded.value";
                    cmd.Parameters.AddWithValue("$v", value);
                }
                cmd.Parameters.AddWithValue("$s", scope);
                cmd.Parameters.AddWithValue("$e", entityId ?? "");
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine("[options-db] set failed: " + ex.Message); }
        }
    }

    /// <summary>Global-scope convenience (job 1: DB-managed Settings.xml keys).</summary>
    public static string? GetGlobal(string key) => Get(Global, "", key);
    public static void SetGlobal(string key, string? value) => Set(Global, "", key, value);

    /// <summary>All key→value for one (scope, entityId) — the launch path reads a single
    /// entity's options in one shot. Empty dict when none.</summary>
    public static Dictionary<string, string> All(string scope, string entityId)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_lock)
        {
            if (_conn == null) return d;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM options WHERE scope=$s AND entity_id=$e";
                cmd.Parameters.AddWithValue("$s", scope);
                cmd.Parameters.AddWithValue("$e", entityId ?? "");
                using var r = cmd.ExecuteReader();
                while (r.Read()) d[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
            catch (Exception ex) { Console.WriteLine("[options-db] all failed: " + ex.Message); }
        }
        return d;
    }
}
