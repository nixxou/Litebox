// Everything RomM keeps between runs — its own database, Core\litebox\romm.db.
//
// It replaces romm-ids.json, which held five string→int dictionaries and kept all of them RESIDENT: on a
// 3057-game library that is 2.8 MB of keys that nothing reads on the hot path, and it grows linearly with
// the library (~35 MB at 40k games). The file ids alone were 14823 long strings — a game guid plus a path
// inside an archive — needed only when one client opens one game's page. That is a query, not a cache.
//
// What stays in memory is one integer per game (GameStore's RommRomId, Tier 1) plus a sparse map of the
// few games a client is locked on. Everything else is asked for when it is wanted.
//
// ── The identity model ────────────────────────────────────────────────────────
//
// A rom_id names a GAME AND A FILE. `rom` holds one row per combination that has ever been needed:
//
//   file_key = '*'            the game's DEFAULT slot — whatever the ranking picks right now. Allocated
//                             at the first listing, which is where the old ledger allocated too. It does
//                             NOT name a file, which is what lets 3057 games be listed without opening a
//                             single archive.
//   file_key = 'main'         the game's own ROM
//   file_key = 'app:{id}'     an additional application (a version, a disc)
//   file_key = 'entry:{path}' one ROM inside an archive, by its path — two entries can share a name
//
// A client is served the default row unless `client_lock` pins it to another. The lock SELECTS which
// existing rom_id that client sees; it never mints a private one. So rom 337 means the same file to
// everybody, and the catalogue stays shared — which is what makes a rom_id worth persisting on a device.
//
// The sentinel '*' rather than NULL is deliberate: SQLite treats NULLs as distinct in a UNIQUE index, so
// a nullable file_key would happily allow two default rows for one game.
//
// ── Ids are never reused ──────────────────────────────────────────────────────
//
// Clients persist these integers. A row is never deleted and a counter never goes back, so a game that
// leaves the library keeps its number reserved — a re-added game must not inherit somebody's handheld
// history. The old ledger's counters are imported on first run for exactly that reason: every id already
// handed out keeps its meaning.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host;
using LbApiHost.Host.Diag;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Romm;

/// <summary>One client's hold on one game: the rom_id it is pinned to.</summary>
internal sealed class RommLock
{
    public int TokenId;
    public string GameGuid = "";
    public int RomId;
    public string FileKey = "";
}

internal static class RommDb
{
    /// <summary>The default slot of a game — the row that does not name a file.</summary>
    public const string DefaultKey = "*";

    private static readonly object _init = new();
    private static bool _ready;
    private static string? _pathOverride;

    private static string DbPath => _pathOverride ?? LiteBoxPaths.File("romm.db");

    private static void Log(string m) { try { Console.WriteLine("[romm-db] " + m); } catch { } }

    /// <summary>Redirects the store to a scratch file — the self-test's hook.</summary>
    internal static void UseStore(string? path)
    {
        lock (_init) { _pathOverride = path; _ready = false; }
    }

    private static SqliteConnection? Open()
    {
        try
        {
            var conn = SqliteBootstrap.OpenConnection(DbPath);
            lock (_init)
            {
                if (!_ready)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=4000;" +

                            // The identity. UNIQUE(game_guid, file_key) is what makes re-assigning to a
                            // combination already used give back its ORIGINAL id instead of a new one.
                            "CREATE TABLE IF NOT EXISTS rom(" +
                            "  rom_id INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "  game_guid TEXT NOT NULL, file_key TEXT NOT NULL, created_utc TEXT NOT NULL);" +
                            "CREATE UNIQUE INDEX IF NOT EXISTS ix_rom_key ON rom(game_guid, file_key);" +

                            // Who is pinned to what. One lock per (client, game): re-locking replaces.
                            "CREATE TABLE IF NOT EXISTS client_lock(" +
                            "  token_id INTEGER NOT NULL, game_guid TEXT NOT NULL," +
                            "  rom_id INTEGER NOT NULL, locked_utc TEXT NOT NULL," +
                            "  PRIMARY KEY(token_id, game_guid));" +

                            // The other id spaces, unchanged in meaning — a key, an integer, never reused.
                            "CREATE TABLE IF NOT EXISTS platform(" +
                            "  platform_id INTEGER PRIMARY KEY AUTOINCREMENT, lb_name TEXT NOT NULL UNIQUE);" +
                            "CREATE TABLE IF NOT EXISTS file(" +
                            "  file_id INTEGER PRIMARY KEY AUTOINCREMENT, k TEXT NOT NULL UNIQUE);" +
                            "CREATE TABLE IF NOT EXISTS asset(" +
                            "  asset_id INTEGER PRIMARY KEY AUTOINCREMENT, k TEXT NOT NULL UNIQUE);" +
                            "CREATE TABLE IF NOT EXISTS collection(" +
                            "  collection_id INTEGER PRIMARY KEY AUTOINCREMENT, lb_name TEXT NOT NULL UNIQUE);" +

                            // The identity model proper — one row per playable file. See RommGamesTable.
                            RommGamesTable.Schema;
                        cmd.ExecuteNonQuery();
                    }
                    RommGamesTable.Migrate(conn);
                    _ready = true;
                    TryImportLedger(conn);
                }
            }
            return conn;
        }
        catch (Exception ex) { Log("open failed: " + ex.Message); return null; }
    }

    // ── Allocation ────────────────────────────────────────────────────────────

    private static int Resolve(SqliteConnection conn, string table, string idCol, string keyCol, string key)
    {
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = $"SELECT {idCol} FROM {table} WHERE {keyCol}=$k";
            sel.Parameters.AddWithValue("$k", key);
            var got = sel.ExecuteScalar();
            if (got != null && got != DBNull.Value) return Convert.ToInt32(got);
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = $"INSERT INTO {table}({keyCol}) VALUES($k); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$k", key);
            return Convert.ToInt32(ins.ExecuteScalar());
        }
    }

    private static int ResolveSafe(string table, string idCol, string keyCol, string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        try
        {
            using var conn = Open(); if (conn == null) return 0;
            return Resolve(conn, table, idCol, keyCol, key);
        }
        catch (Exception ex) { Log($"{table} resolve failed: " + ex.Message); return 0; }
    }

    // Platform and collection ids are minted once and NEVER change — that stability is what lets
    // clients cache them, so it also lets US cache them: without this, every resolution opened its own
    // SQLite connection, and filter_values alone did it once per game — measured at 1.9s of a 2.1s
    // Arcade listing (2965 games), every page, every request.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _platIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _platNames = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _collIds = new(StringComparer.OrdinalIgnoreCase);

    public static int PlatformId(string lbPlatformName)
    {
        if (_platIds.TryGetValue(lbPlatformName, out var hit)) return hit;
        var id = ResolveSafe("platform", "platform_id", "lb_name", lbPlatformName);
        if (id > 0) { _platIds[lbPlatformName] = id; _platNames[id] = lbPlatformName; }
        return id;
    }
    public static int FileId(string gameGuid, string fileKey) => ResolveSafe("file", "file_id", "k", gameGuid + "|" + fileKey);
    public static int AssetId(string assetKey) => ResolveSafe("asset", "asset_id", "k", assetKey);
    public static int CollectionId(string name)
    {
        if (_collIds.TryGetValue(name, out var hit)) return hit;
        var id = ResolveSafe("collection", "collection_id", "lb_name", name);
        if (id > 0) _collIds[name] = id;
        return id;
    }

    public static string? PlatformNameOf(int id)
    {
        if (_platNames.TryGetValue(id, out var hit)) return hit;
        var name = ReverseSafe("platform", "platform_id", "lb_name", id);
        if (name != null) { _platNames[id] = name; _platIds[name] = id; }
        return name;
    }
    public static string? FileKeyOf(int id) => ReverseSafe("file", "file_id", "k", id);
    public static string? AssetKeyOf(int id) => ReverseSafe("asset", "asset_id", "k", id);
    public static string? CollectionNameOf(int id) => ReverseSafe("collection", "collection_id", "lb_name", id);

    /// <summary>The module's own key-value slots (token list, password hash). Null clears.</summary>
    public static string? GetKv(string k)
    {
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM kv WHERE k=$k";
            cmd.Parameters.AddWithValue("$k", k);
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception ex) { Log("kv read failed: " + ex.Message); return null; }
    }

    public static void SetKv(string k, string? v)
    {
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            if (v == null)
            {
                cmd.CommandText = "DELETE FROM kv WHERE k=$k";
                cmd.Parameters.AddWithValue("$k", k);
            }
            else
            {
                cmd.CommandText = "INSERT INTO kv(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$v", v);
            }
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("kv write failed: " + ex.Message); }
    }

    private static string? ReverseSafe(string table, string idCol, string keyCol, int id)
    {
        if (id <= 0) return null;
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {keyCol} FROM {table} WHERE {idCol}=$i";
            cmd.Parameters.AddWithValue("$i", id);
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception ex) { Log($"{table} reverse failed: " + ex.Message); return null; }
    }

    /// <summary>A connection for the index pass, which needs several statements against one open
    /// database and manages its own transactions. Callers dispose it.</summary>
    public static Microsoft.Data.Sqlite.SqliteConnection? OpenForIndex() => Open();

    // ── Roms ──────────────────────────────────────────────────────────────────

    /// <summary>The rom_id of one (game, file) combination, allocating it if this is the first time it is
    /// wanted. <paramref name="fileKey"/> is <see cref="DefaultKey"/> for the game's default slot.</summary>
    public static int RomId(string gameGuid, string fileKey)
    {
        if (string.IsNullOrEmpty(gameGuid)) return 0;
        try
        {
            using var conn = Open(); if (conn == null) return 0;
            return RomIdOn(conn, gameGuid, fileKey);
        }
        catch (Exception ex) { Log("rom resolve failed: " + ex.Message); return 0; }
    }

    private static int RomIdOn(SqliteConnection conn, string gameGuid, string fileKey)
    {
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT rom_id FROM rom WHERE game_guid=$g AND file_key=$f";
            sel.Parameters.AddWithValue("$g", gameGuid);
            sel.Parameters.AddWithValue("$f", fileKey);
            var got = sel.ExecuteScalar();
            if (got != null && got != DBNull.Value) return Convert.ToInt32(got);
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO rom(game_guid, file_key, created_utc) VALUES($g,$f,$t); " +
                              "SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$g", gameGuid);
            ins.Parameters.AddWithValue("$f", fileKey);
            ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt32(ins.ExecuteScalar());
        }
    }

    /// <summary>What a rom_id names: the game and the file, or null when the id was never allocated.</summary>
    public static (string GameGuid, string FileKey)? RomOf(int romId)
    {
        if (romId <= 0) return null;
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT game_guid, file_key FROM rom WHERE rom_id=$i";
            cmd.Parameters.AddWithValue("$i", romId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
        }
        catch (Exception ex) { Log("rom lookup failed: " + ex.Message); return null; }
    }

    /// <summary>Every game that already has a default row, with its id — the boot pass that stamps
    /// GameStore. One pass, no per-game query.</summary>
    public static List<(string GameGuid, int RomId)> AllDefaults()
    {
        var res = new List<(string, int)>();
        try
        {
            using var conn = Open(); if (conn == null) return res;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT game_guid, rom_id FROM rom WHERE file_key=$d";
            cmd.Parameters.AddWithValue("$d", DefaultKey);
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add((r.GetString(0), r.GetInt32(1)));
        }
        catch (Exception ex) { Log("defaults read failed: " + ex.Message); }
        return res;
    }

    // ── Locks ─────────────────────────────────────────────────────────────────

    /// <summary>Pins a client to one file of one game, allocating that file's rom_id if it is the first
    /// time anyone wanted it. Returns the rom_id the client is now on.</summary>
    public static int Lock(int tokenId, string gameGuid, string fileKey)
    {
        if (tokenId <= 0 || string.IsNullOrEmpty(gameGuid) || string.IsNullOrEmpty(fileKey)) return 0;
        try
        {
            using var conn = Open(); if (conn == null) return 0;
            using var tx = conn.BeginTransaction();
            int romId = RomIdOn(conn, gameGuid, fileKey);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO client_lock(token_id, game_guid, rom_id, locked_utc) VALUES($c,$g,$r,$t) " +
                    "ON CONFLICT(token_id, game_guid) DO UPDATE SET rom_id=excluded.rom_id, " +
                    "locked_utc=excluded.locked_utc";
                cmd.Parameters.AddWithValue("$c", tokenId);
                cmd.Parameters.AddWithValue("$g", gameGuid);
                cmd.Parameters.AddWithValue("$r", romId);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return romId;
        }
        catch (Exception ex) { Log("lock failed: " + ex.Message); return 0; }
    }

    /// <summary>Releases one client's hold on one game — it falls back to the default.</summary>
    public static bool Unlock(int tokenId, string gameGuid)
    {
        try
        {
            using var conn = Open(); if (conn == null) return false;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM client_lock WHERE token_id=$c AND game_guid=$g";
            cmd.Parameters.AddWithValue("$c", tokenId);
            cmd.Parameters.AddWithValue("$g", gameGuid);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex) { Log("unlock failed: " + ex.Message); return false; }
    }

    /// <summary>Drops every hold a client had — revoking it.</summary>
    public static int UnlockToken(int tokenId)
    {
        try
        {
            using var conn = Open(); if (conn == null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM client_lock WHERE token_id=$c";
            cmd.Parameters.AddWithValue("$c", tokenId);
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("unlock-token failed: " + ex.Message); return 0; }
    }

    /// <summary>Every lock there is, joined to the file it pins. Read once at boot into the sparse
    /// in-memory map, and again whenever a lock changes.</summary>
    public static List<RommLock> AllLocks()
    {
        var res = new List<RommLock>();
        try
        {
            using var conn = Open(); if (conn == null) return res;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT l.token_id, l.game_guid, l.rom_id, r.file_key " +
                              "FROM client_lock l JOIN rom r ON r.rom_id = l.rom_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                res.Add(new RommLock
                {
                    TokenId = r.GetInt32(0), GameGuid = r.GetString(1),
                    RomId = r.GetInt32(2), FileKey = r.GetString(3),
                });
        }
        catch (Exception ex) { Log("locks read failed: " + ex.Message); }
        return res;
    }

    // ── Importing the old ledger ──────────────────────────────────────────────

    /// <summary>Carries romm-ids.json over on first run, counters included.
    ///
    /// Not a nicety: clients persist these integers and attribute what they hold to them. Starting the
    /// counters fresh would hand somebody's handheld a rom 337 that now means a different game. The old
    /// "Roms" map was keyed by game guid alone — that is exactly this model's DEFAULT slot, so each of
    /// its rows becomes a '*' row and keeps its number.
    ///
    /// AUTOINCREMENT means sqlite_sequence decides the next id; it is bumped past the highest imported
    /// value so a fresh allocation can never collide with an imported one.</summary>
    private static void TryImportLedger(SqliteConnection conn)
    {
        try
        {
            using (var chk = conn.CreateCommand())
            {
                chk.CommandText = "SELECT COUNT(*) FROM rom";
                if (Convert.ToInt64(chk.ExecuteScalar()) > 0) return;      // already populated
            }

            var json = LiteBoxPaths.File("romm-ids.json");
            if (!File.Exists(json)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var root = doc.RootElement;
            using var tx = conn.BeginTransaction();
            int roms = 0, files = 0, others = 0;

            void Seed(string table, string idCol, string keyCol, string mapName, Func<string, string?> keyOf)
            {
                if (!root.TryGetProperty(mapName, out var map) || map.ValueKind != JsonValueKind.Object) return;
                foreach (var p in map.EnumerateObject())
                {
                    var k = keyOf(p.Name);
                    if (k == null) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"INSERT OR IGNORE INTO {table}({idCol},{keyCol}) VALUES($i,$k)";
                    cmd.Parameters.AddWithValue("$i", p.Value.GetInt32());
                    cmd.Parameters.AddWithValue("$k", k);
                    cmd.ExecuteNonQuery();
                    others++;
                }
            }

            if (root.TryGetProperty("Roms", out var romMap) && romMap.ValueKind == JsonValueKind.Object)
                foreach (var p in romMap.EnumerateObject())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO rom(rom_id, game_guid, file_key, created_utc) " +
                                      "VALUES($i,$g,$d,$t)";
                    cmd.Parameters.AddWithValue("$i", p.Value.GetInt32());
                    cmd.Parameters.AddWithValue("$g", p.Name);
                    cmd.Parameters.AddWithValue("$d", DefaultKey);
                    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                    roms++;
                }

            // "Files" was keyed "{guid}|{fileKey}" and the new table keeps that shape verbatim.
            if (root.TryGetProperty("Files", out var fileMap) && fileMap.ValueKind == JsonValueKind.Object)
                foreach (var p in fileMap.EnumerateObject())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR IGNORE INTO file(file_id, k) VALUES($i,$k)";
                    cmd.Parameters.AddWithValue("$i", p.Value.GetInt32());
                    cmd.Parameters.AddWithValue("$k", p.Name);
                    cmd.ExecuteNonQuery();
                    files++;
                }

            Seed("platform", "platform_id", "lb_name", "Platforms", n => n);
            Seed("asset", "asset_id", "k", "Assets", n => n);
            Seed("collection", "collection_id", "lb_name", "Collections", n => n);

            // Push each counter past what was imported AND past what the old ledger had already handed
            // out but not yet used — the "Next*" values.
            void Bump(string table, string nextName)
            {
                int next = root.TryGetProperty(nextName, out var v) && v.TryGetInt32(out var n) ? n : 1;
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $"INSERT INTO sqlite_sequence(name, seq) SELECT '{table}', $s " +
                    $"WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='{table}'); " +
                    $"UPDATE sqlite_sequence SET seq=MAX(seq,$s) WHERE name='{table}';";
                cmd.Parameters.AddWithValue("$s", Math.Max(0, next - 1));
                cmd.ExecuteNonQuery();
            }
            Bump("rom", "NextRom");
            Bump("file", "NextFile");
            Bump("platform", "NextPlatform");
            Bump("asset", "NextAsset");
            Bump("collection", "NextCollection");

            tx.Commit();
            Log($"imported romm-ids.json — {roms} rom(s), {files} file(s), {others} other(s)");
        }
        catch (Exception ex) { Log("ledger import failed (starting fresh): " + ex.Message); }
    }
}
