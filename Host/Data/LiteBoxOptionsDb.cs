// LiteBox's own options store — a small local SQLite DB (Core\litebox\litebox-options.db),
// SEPARATE from the op-log DB so an op-log schema reset never drops user settings.
//
// Two jobs, one table:
//   1. Home for Settings.xml keys that can't safely live in the current LB's XML
//      (ProblemKeys.IsDbManaged) — scope="global", entity_id="".
//   2. Home for the EXTRA per-entity data LiteBox adds that LaunchBox has no field for
//      (gameplay overrides, FieldLocks, Model3dImages, …) — scope="game"|"emulator"|
//      "platform"|"playlist", entity_id = the entity's id (platform: its NAME — LB platforms
//      have no guid).
//
// Schema — a key/value table (EAV) rather than a wide typed table on purpose: a new option
// is a new KEY (declared in the OptionKeys REGISTRY), never a schema migration. Sparse: a row
// exists only when an option is actually set.
//
//   options(scope TEXT, entity_id TEXT, key TEXT, value TEXT, PRIMARY KEY(scope, entity_id, key))
//
// NAMESPACE CONTROL — every access is validated against OptionKeys: an undeclared (scope, key)
// logs "[options-db] unknown key" (and THROWS under --debug via Strict), so a typo can no
// longer silently create a row or an override that never resolves. Values are NOT validated
// (raw string semantics unchanged; null/empty value = DELETE the row).
//
// HOT CACHE — keys declared Hot in the registry (read on list/search/detail paths) are
// pre-loaded at Open() into a write-through dictionary: reads are RAM hits, writes update RAM
// and DB under the same lock. Cold keys (launch-time / punctual reads) always hit the DB —
// which also guarantees cross-process freshness for the plugin-shared FieldLocks (ExtendDB,
// under real LaunchBox only, writes this file directly). RevalidateHotCache (main-window
// activation) reloads the hot sets when the DB file changed on disk.
//
// Resolution (global < platform < emulator < game) is the CALLER's job (it knows the ids);
// this class is the flat get/set substrate. Every method swallows + logs, never throws
// (except Strict namespace violations, which are dev-time bugs meant to fail loudly).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

internal static class LiteBoxOptionsDb
{
    public const string Global = "global";

    /// <summary>Throw on undeclared (scope, key) instead of logging — set at boot in --debug runs.</summary>
    public static bool Strict;

    private static SqliteConnection? _conn;
    private static readonly object _lock = new();
    private static bool _tried;
    private static string? _path;
    private static DateTime _loadedMtime;

    // Hot cache: (scope, key) → (entity_id → value). Only registry-Hot pairs are present.
    private static readonly Dictionary<(string scope, string key), Dictionary<string, string>> _hot = new();

    public static bool Enabled => _conn != null;

    /// <summary>Open (create) the DB, stamp user_version, pre-load the Hot sets. Idempotent; never
    /// throws. Call once at boot.</summary>
    public static void Open(string? dbPath = null)
    {
        lock (_lock)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                dbPath ??= LiteBoxPaths.File("litebox-options.db");
                _path = dbPath;
                _conn = SqliteBootstrap.OpenConnection(dbPath);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS options(" +
                        "  scope TEXT NOT NULL, entity_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT," +
                        "  PRIMARY KEY(scope, entity_id, key)) WITHOUT ROWID;";
                    cmd.ExecuteNonQuery();
                }
                // Version stamp only (PRAGMA user_version) — this DB is USER data and is NEVER reset;
                // the stamp lets a future value-format migration key off the writing version.
                try
                {
                    using var uv = _conn.CreateCommand();
                    uv.CommandText = "PRAGMA user_version = " + Install.LiteBoxVersion.Encode(Install.LiteBoxVersion.Current) + ";";
                    uv.ExecuteNonQuery();
                }
                catch { }
                LoadHotLocked();
                Console.WriteLine($"[options-db] open {dbPath} (hot cache: {CountHotLocked()} row(s))");
            }
            catch (Exception ex)
            {
                // A failed Open is not a degraded store, it's NO store: every option then reads as
                // "unset" and the whole tri-state resolution silently falls back to defaults. In
                // production that must not stop LiteBox from starting (a locked file, a bad disk).
                // Under --debug it is always a bug — fail loudly, the way an undeclared key does.
                // (Found the hard way: a static-init ordering slip in OptionKeys surfaced here as one
                // easily-missed log line while every gameplay option quietly reverted to its default.)
                Console.WriteLine("[options-db] open failed: " + ex);
                _conn = null;
                if (Strict) throw;
            }
        }
    }

    // ── Namespace validation ─────────────────────────────────────────────────

    private static void Declared(string scope, string key, string op)
    {
        if (OptionKeys.IsDeclared(scope, key)) return;
        string msg = $"[options-db] unknown key ({op}): scope='{scope}' key='{key}' — declare it in OptionKeys";
        if (Strict) throw new InvalidOperationException(msg);
        Console.WriteLine(msg);   // log-only in production: never break a running feature over a registry gap
    }

    // ── Raw get/set ──────────────────────────────────────────────────────────

    /// <summary>The value for (scope, entityId, key), or null when unset. Hot keys are served from RAM.</summary>
    public static string? Get(string scope, string entityId, string key)
    {
        lock (_lock)
        {
            if (_conn == null) return null;
            Declared(scope, key, "get");
            if (_hot.TryGetValue((scope, key), out var map))
                return map.TryGetValue(entityId ?? "", out var v) ? v : null;
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
            Declared(scope, key, "set");
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
                // Write-through: keep the hot set exact.
                if (_hot.TryGetValue((scope, key), out var map))
                {
                    if (string.IsNullOrEmpty(value)) map.Remove(entityId ?? "");
                    else map[entityId ?? ""] = value!;
                }
                TouchMtimeLocked();
            }
            catch (Exception ex) { Console.WriteLine("[options-db] set failed: " + ex.Message); }
        }
    }

    /// <summary>Global-scope convenience (job 1: DB-managed Settings.xml keys).</summary>
    public static string? GetGlobal(string key) => Get(Global, "", key);
    public static void SetGlobal(string key, string? value) => Set(Global, "", key, value);

    // ── Typed accessors (centralised parsing — stop re-parsing in every consumer) ──

    /// <summary>Tri-state bool: null = no row (inherit), else the row parsed as "true"/anything-else.</summary>
    public static bool? GetBool(string scope, string entityId, string key)
    {
        var v = Get(scope, entityId, key);
        return string.IsNullOrEmpty(v) ? (bool?)null : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetBool(string scope, string entityId, string key, bool? value)
        => Set(scope, entityId, key, value == null ? null : (value.Value ? "true" : "false"));

    public static T? GetJson<T>(string scope, string entityId, string key) where T : class
    {
        var v = Get(scope, entityId, key);
        if (string.IsNullOrEmpty(v)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(v!); }
        catch (Exception ex) { Console.WriteLine($"[options-db] json parse failed ({key}): {ex.Message}"); return null; }
    }

    public static void SetJson<T>(string scope, string entityId, string key, T? value) where T : class
        => Set(scope, entityId, key, value == null ? null : System.Text.Json.JsonSerializer.Serialize(value));

    // ── Bulk reads ───────────────────────────────────────────────────────────

    /// <summary>All key→value for one (scope, entityId) — e.g. the launch path reads a single
    /// entity's options in one shot. Empty dict when none. (Direct DB read — bulk callers are Cold paths.)</summary>
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

    /// <summary>All entity_id→value rows of one (scope, key) — sweep/scan-style reads. Served from
    /// the hot cache when the key is Hot.</summary>
    public static Dictionary<string, string> AllOf(string scope, string key)
    {
        lock (_lock)
        {
            Declared(scope, key, "all-of");
            if (_hot.TryGetValue((scope, key), out var hot))
                return new Dictionary<string, string>(hot, StringComparer.Ordinal);
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_conn == null) return d;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT entity_id, value FROM options WHERE scope=$s AND key=$k";
                cmd.Parameters.AddWithValue("$s", scope);
                cmd.Parameters.AddWithValue("$k", key);
                using var r = cmd.ExecuteReader();
                while (r.Read()) d[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
            catch (Exception ex) { Console.WriteLine("[options-db] all-of failed: " + ex.Message); }
            return d;
        }
    }

    // ── Hot cache lifecycle ──────────────────────────────────────────────────

    // Caller holds _lock.
    private static void LoadHotLocked()
    {
        _hot.Clear();
        if (_conn == null) return;
        foreach (var scope in OptionKeys.AllScopes)
            foreach (var key in OptionKeys.HotKeys(scope))
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "SELECT entity_id, value FROM options WHERE scope=$s AND key=$k";
                    cmd.Parameters.AddWithValue("$s", scope);
                    cmd.Parameters.AddWithValue("$k", key);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) map[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                }
                catch (Exception ex) { Console.WriteLine("[options-db] hot load failed: " + ex.Message); }
                _hot[(scope, key)] = map;
            }
        TouchMtimeLocked();
    }

    private static int CountHotLocked()
    {
        int n = 0;
        foreach (var m in _hot.Values) n += m.Count;
        return n;
    }

    private static void TouchMtimeLocked()
    {
        try { if (_path != null) _loadedMtime = File.GetLastWriteTimeUtc(_path); } catch { }
    }

    /// <summary>Reload the hot sets when the DB file changed on disk since the last load — covers
    /// ExtendDB (under a simultaneously-running real LB) writing this file directly. One stat when
    /// nothing changed. Called on main-window activation.</summary>
    public static void RevalidateHotCache()
    {
        lock (_lock)
        {
            if (_conn == null || _path == null) return;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(_path);
                if (mtime == _loadedMtime) return;
                LoadHotLocked();
                Console.WriteLine($"[options-db] hot cache reloaded (db changed on disk; {CountHotLocked()} row(s))");
            }
            catch { }
        }
    }

    // ── Maintenance ──────────────────────────────────────────────────────────

    /// <summary>Delete every row of <paramref name="scope"/> whose entity_id is NOT in
    /// <paramref name="liveIds"/> (entity removed from the library). Global is never swept.
    /// Returns the number of rows deleted.</summary>
    public static int SweepOrphans(string scope, ISet<string> liveIds)
    {
        if (scope == Global) return 0;
        lock (_lock)
        {
            if (_conn == null) return 0;
            int deleted = 0;
            try
            {
                var orphans = new List<string>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT entity_id FROM options WHERE scope=$s";
                    cmd.Parameters.AddWithValue("$s", scope);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var id = r.GetString(0);
                        if (!liveIds.Contains(id)) orphans.Add(id);
                    }
                }
                foreach (var id in orphans)
                {
                    using var del = _conn.CreateCommand();
                    del.CommandText = "DELETE FROM options WHERE scope=$s AND entity_id=$e";
                    del.Parameters.AddWithValue("$s", scope);
                    del.Parameters.AddWithValue("$e", id);
                    deleted += del.ExecuteNonQuery();
                    foreach (var kv in _hot) if (kv.Key.scope == scope) kv.Value.Remove(id);
                }
                if (deleted > 0) TouchMtimeLocked();
            }
            catch (Exception ex) { Console.WriteLine("[options-db] sweep failed: " + ex.Message); }
            return deleted;
        }
    }

    /// <summary>Rename an entity in place — for PLATFORM rows, which are NAME-keyed (LB platforms have
    /// no guid). Existing rows under the new name win on conflict; the rest move.</summary>
    public static void RenameEntity(string scope, string oldId, string newId)
    {
        if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId)
            || string.Equals(oldId, newId, StringComparison.Ordinal)) return;
        lock (_lock)
        {
            if (_conn == null) return;
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE OR IGNORE options SET entity_id=$n WHERE scope=$s AND entity_id=$o; " +
                                      "DELETE FROM options WHERE scope=$s AND entity_id=$o;";
                    cmd.Parameters.AddWithValue("$s", scope);
                    cmd.Parameters.AddWithValue("$o", oldId);
                    cmd.Parameters.AddWithValue("$n", newId);
                    cmd.ExecuteNonQuery();
                }
                LoadHotLocked();   // simplest correct refresh for a rare operation
                Console.WriteLine($"[options-db] renamed {scope} '{oldId}' → '{newId}'");
            }
            catch (Exception ex) { Console.WriteLine("[options-db] rename failed: " + ex.Message); }
        }
    }
}
