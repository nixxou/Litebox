// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — SQLite backing (LISTING half). Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// Stores the parsed entry list of each archive, keyed on the 10-hex UPPER path
// signature (== ArchiveSig.ComputePathSignature). Lives at
// <LB>\Core\litebox\rom-archive-cache.db (rebuildable — nuke it and it re-lists).
//
// R2 owns the listing rows (the `archive` head + `archive_entry` rows); R3 owns the
// cache-manifest (`cache_entry`). Since the RA-engine migration (docs/ra-engine-migration-plan.md
// P0) this file ALSO hosts the RetroAchievements engine's storage half — mirroring the plugin's
// own merge of its RA DB into the archive cache DB, for the same reason: the per-entry RA columns
// join the listing rows on the SAME (signature, path_in_archive) key, and re-linking ids after a
// catalogue refresh is one indexed UPDATE. The RA tables/columns are DEFINED here (single schema
// owner) but ACCESSED through Host/Ra/RaStore — keep RA SQL there, listing/manifest SQL here.
//   • archive.parse_state           — RA full-parse state (0 unparsed / 1 ok / 2 failed)
//   • archive_entry.RetroAchievementsHash / RetroAchievementsId
//   • rom_hash / ra_game / ra_hash / ra_console — see RaStore
//
// Connection-per-op (Pooling=false, WAL) so the dropdown, the picker and — later —
// the launch/web surfaces can read/write concurrently without a shared lock.

#nullable enable

using System;
using System.Collections.Generic;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Rom;

internal static class ArchiveCacheDb
{
    private static readonly object _init = new();
    private static bool _ready;

    private static string DbPath => LiteBoxPaths.File("rom-archive-cache.db");

    /// <summary>10-hex UPPER path signature from a 32-hex listing key.</summary>
    public static string Sig(string key) =>
        string.IsNullOrEmpty(key) || key.Length < 10 ? (key ?? "").ToUpperInvariant() : key.Substring(0, 10).ToUpperInvariant();

    /// <summary>Open a fresh connection, ensuring the schema once per session. Returns
    /// null (never throws) when SQLite is unavailable — the caller degrades to a re-list.</summary>
    private static SqliteConnection? Open()
    {
        try
        {
            var conn = SqliteBootstrap.OpenConnection(DbPath);
            lock (_init)
            {
                if (!_ready)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=4000;" +
                        "CREATE TABLE IF NOT EXISTS archive(" +
                        "  signature TEXT PRIMARY KEY, path TEXT, size INTEGER, short_sig TEXT, cached_at TEXT," +
                        "  parse_state INTEGER DEFAULT 0);" +
                        "CREATE TABLE IF NOT EXISTS archive_entry(" +
                        "  signature TEXT NOT NULL, filename TEXT, path_in_archive TEXT, size INTEGER," +
                        "  RetroAchievementsHash TEXT, RetroAchievementsId INTEGER," +
                        "  PRIMARY KEY(signature, path_in_archive));" +
                        // R3 cache-manifest half: one row per on-disk <SIG> extraction (the eviction unit).
                        // Rebuildable like the listing rows, so it shares this DB (never mixes with the
                        // durable favourites/last-played state in rom-archive-history.db).
                        "CREATE TABLE IF NOT EXISTS cache_entry(" +
                        "  signature TEXT PRIMARY KEY, game_title TEXT, platform TEXT, emulator TEXT," +
                        "  source_path TEXT, mode TEXT, output_file TEXT, size_bytes INTEGER," +
                        "  cached_utc TEXT, last_played_utc TEXT);" +
                        // RA-engine storage half (schema copied from the plugin's CacheDb; accessed via RaStore).
                        "CREATE TABLE IF NOT EXISTS rom_hash(" +
                        "  signature TEXT PRIMARY KEY, path TEXT, size INTEGER," +
                        "  RetroAchievementsHash TEXT, RetroAchievementsId INTEGER, computed_at TEXT);" +
                        "CREATE TABLE IF NOT EXISTS ra_game(" +
                        "  id INTEGER PRIMARY KEY, console_id INTEGER, title TEXT, console_name TEXT," +
                        "  image_icon TEXT, num_achievements INTEGER, num_leaderboards INTEGER, points INTEGER," +
                        "  date_modified TEXT, forum_topic_id INTEGER);" +
                        "CREATE TABLE IF NOT EXISTS ra_hash(hash TEXT PRIMARY KEY, game_id INTEGER);" +
                        "CREATE TABLE IF NOT EXISTS ra_console(" +
                        "  id INTEGER PRIMARY KEY, key TEXT, name TEXT, games_refreshed_at TEXT, next_refresh_at TEXT);" +
                        "CREATE INDEX IF NOT EXISTS ix_rom_hash_raid ON rom_hash(RetroAchievementsId);" +
                        "CREATE INDEX IF NOT EXISTS ix_archive_entry_raid ON archive_entry(RetroAchievementsId);" +
                        "CREATE INDEX IF NOT EXISTS ix_ra_game_console ON ra_game(console_id);" +
                        "CREATE INDEX IF NOT EXISTS ix_ra_hash_game ON ra_hash(game_id);";
                    cmd.ExecuteNonQuery();
                    _ready = true;
                }
            }
            return conn;
        }
        catch (Exception ex) { Log("Open failed: " + ex.Message); return null; }
    }

    /// <summary>Connection for the RA storage half (Host/Ra/RaStore) — same file, same schema
    /// bootstrap, so RaStore never has to duplicate the DDL. Null when SQLite is unavailable.</summary>
    internal static SqliteConnection? OpenForRa() => Open();

    public static ArchiveListingRecord? GetListingRecord(string sig)
    {
        if (string.IsNullOrEmpty(sig)) return null;
        try
        {
            using var conn = Open();
            if (conn == null) return null;
            ArchiveListingRecord? rec = null;
            using (var head = conn.CreateCommand())
            {
                head.CommandText = "SELECT path, size, short_sig, cached_at FROM archive WHERE signature=$s;";
                head.Parameters.AddWithValue("$s", sig);
                using var rd = head.ExecuteReader();
                if (!rd.Read()) return null;
                rec = new ArchiveListingRecord
                {
                    Key = sig,
                    ArchivePath = rd.IsDBNull(0) ? "" : rd.GetString(0),
                    ArchiveSize = rd.GetInt64(1),
                    ShortSignature = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    CachedAtUtc = ParseUtc(rd.IsDBNull(3) ? null : rd.GetString(3)),
                    Entries = new List<ArchiveListingEntry>(),
                };
            }
            using (var ent = conn.CreateCommand())
            {
                ent.CommandText = "SELECT filename, path_in_archive, size FROM archive_entry WHERE signature=$s;";
                ent.Parameters.AddWithValue("$s", sig);
                using var rd = ent.ExecuteReader();
                while (rd.Read())
                    rec.Entries.Add(new ArchiveListingEntry { FileName = rd.GetString(0), PathInArchive = rd.GetString(1), Size = rd.GetInt64(2) });
            }
            return rec;
        }
        catch (Exception ex) { Log("GetListingRecord failed: " + ex.Message); return null; }
    }

    public static void SetListing(string sig, string path, long size, string shortSig, IList<ArchiveListingEntry> entries)
    {
        if (string.IsNullOrEmpty(sig) || entries == null) return;
        try
        {
            using var conn = Open();
            if (conn == null) return;
            using var tx = conn.BeginTransaction();
            PurgeOtherPathVersions(conn, tx, sig, path);   // drop stale rows left by an older version of this archive
            using (var head = conn.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = @"INSERT INTO archive (signature, path, size, short_sig, cached_at)
                                     VALUES ($s, $p, $z, $g, $t)
                                     ON CONFLICT(signature) DO UPDATE SET
                                        path=excluded.path, size=excluded.size,
                                        short_sig=excluded.short_sig, cached_at=excluded.cached_at;";
                head.Parameters.AddWithValue("$s", sig);
                head.Parameters.AddWithValue("$p", path ?? "");
                head.Parameters.AddWithValue("$z", size);
                head.Parameters.AddWithValue("$g", (object?)shortSig ?? DBNull.Value);
                head.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                head.ExecuteNonQuery();
            }
            // Replace the entry set for this signature, PRESERVING the RA columns on surviving rows
            // (the RA engine's per-entry hash/id must not be wiped by a mere re-list): upsert every
            // current entry (RA columns untouched by the update), then drop only rows whose
            // path_in_archive is no longer present.
            foreach (var e in entries)
            {
                using var up = conn.CreateCommand();
                up.Transaction = tx;
                up.CommandText = @"INSERT INTO archive_entry (signature, filename, path_in_archive, size)
                                   VALUES ($s, $f, $p, $z)
                                   ON CONFLICT(signature, path_in_archive) DO UPDATE SET
                                      filename=excluded.filename, size=excluded.size;";
                up.Parameters.AddWithValue("$s", sig);
                up.Parameters.AddWithValue("$f", e.FileName ?? "");
                up.Parameters.AddWithValue("$p", e.PathInArchive ?? "");
                up.Parameters.AddWithValue("$z", e.Size);
                up.ExecuteNonQuery();
            }
            using (var del = conn.CreateCommand())
            {
                var keep = new List<string>(entries.Count);
                foreach (var e in entries) keep.Add(e.PathInArchive ?? "");
                del.Transaction = tx;
                del.CommandText = @"DELETE FROM archive_entry WHERE signature=$s
                                    AND path_in_archive NOT IN (SELECT value FROM json_each($keep));";
                del.Parameters.AddWithValue("$s", sig);
                del.Parameters.AddWithValue("$keep", System.Text.Json.JsonSerializer.Serialize(keep));
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex) { Log("SetListing failed: " + ex.Message); }
    }

    /// <summary>Drops cached rows for the SAME archive <paramref name="path"/> under a
    /// DIFFERENT signature — the path signature embeds the archive SIZE, so editing an
    /// archive yields a new signature and the old head + entries would otherwise pile up.</summary>
    private static void PurgeOtherPathVersions(SqliteConnection conn, SqliteTransaction tx, string sig, string path)
    {
        if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(path)) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"DELETE FROM archive_entry WHERE signature IN
                                (SELECT signature FROM archive WHERE path=$p COLLATE NOCASE AND signature<>$s);
                            DELETE FROM archive WHERE path=$p COLLATE NOCASE AND signature<>$s;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", sig);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════
    //  CACHE MANIFEST  (on-disk extractions) — the R3 half.
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Upsert the manifest row for a persistent extraction. Insert stamps cached_utc once;
    /// re-launches refresh size + last_played_utc but keep the original cached_utc.</summary>
    public static void RecordCache(string cacheRoot, string sig, string title, string platform, string emulator,
                                   string path, string mode, string outputFile)
    {
        if (string.IsNullOrEmpty(sig)) return;
        try
        {
            long size = ArchiveCacheEvictor.DirSize(System.IO.Path.Combine(cacheRoot ?? "", sig));
            using var conn = Open();
            if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO cache_entry
                    (signature, game_title, platform, emulator, source_path, mode, output_file, size_bytes, cached_utc, last_played_utc)
                    VALUES ($s,$g,$p,$e,$src,$m,$o,$z,$t,$t)
                    ON CONFLICT(signature) DO UPDATE SET
                        game_title=excluded.game_title, platform=excluded.platform, emulator=excluded.emulator,
                        source_path=excluded.source_path, mode=excluded.mode, output_file=excluded.output_file,
                        size_bytes=excluded.size_bytes, last_played_utc=excluded.last_played_utc;";
            cmd.Parameters.AddWithValue("$s", sig);
            cmd.Parameters.AddWithValue("$g", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)platform ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)emulator ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", (object?)path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", (object?)mode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$o", (object?)outputFile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$z", size);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("RecordCache failed: " + ex.Message); }
    }

    public static void RemoveCache(string sig)
    {
        if (string.IsNullOrEmpty(sig)) return;
        try
        {
            using var conn = Open();
            if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cache_entry WHERE signature=$s;";
            cmd.Parameters.AddWithValue("$s", sig);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("RemoveCache failed: " + ex.Message); }
    }

    /// <summary>Deletes the cached &lt;SIG&gt; folder AND its manifest row. Returns bytes freed.</summary>
    public static long DeleteCache(string cacheRoot, string sig)
    {
        long freed = 0;
        try
        {
            var dir = System.IO.Path.Combine(cacheRoot ?? "", sig);
            if (System.IO.Directory.Exists(dir)) { freed = ArchiveCacheEvictor.DirSize(dir); System.IO.Directory.Delete(dir, true); }
        }
        catch (Exception ex) { Log("DeleteCache folder failed: " + ex.Message); }
        RemoveCache(sig);
        return freed;
    }

    public static long TotalBytes()
    {
        try
        {
            using var conn = Open();
            if (conn == null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(size_bytes),0) FROM cache_entry;";
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
        }
        catch (Exception ex) { Log("TotalBytes failed: " + ex.Message); return 0; }
    }

    public static List<CacheEntry> CacheEntries()
    {
        var list = new List<CacheEntry>();
        try
        {
            using var conn = Open();
            if (conn == null) return list;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT signature, game_title, platform, emulator, source_path, mode,
                                       output_file, size_bytes, cached_utc, last_played_utc FROM cache_entry;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new CacheEntry
                {
                    Signature = rd.GetString(0),
                    GameTitle = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Platform = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    Emulator = rd.IsDBNull(3) ? "" : rd.GetString(3),
                    SourcePath = rd.IsDBNull(4) ? "" : rd.GetString(4),
                    Mode = rd.IsDBNull(5) ? "" : rd.GetString(5),
                    OutputFile = rd.IsDBNull(6) ? "" : rd.GetString(6),
                    SizeBytes = rd.GetInt64(7),
                    CachedUtc = ParseUtc(rd.IsDBNull(8) ? null : rd.GetString(8)),
                    LastPlayedUtc = ParseUtc(rd.IsDBNull(9) ? null : rd.GetString(9)),
                });
        }
        catch (Exception ex) { Log("CacheEntries failed: " + ex.Message); }
        return list;
    }

    /// <summary>Repairs drift: drop manifest rows whose &lt;SIG&gt; folder vanished, and index any folder
    /// not yet listed (pre-existing cache or folders LiteBox didn't create).</summary>
    public static void Reconcile(string cacheRoot)
    {
        var root = cacheRoot;
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return;
        try
        {
            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in System.IO.Directory.GetDirectories(root))
            {
                var name = System.IO.Path.GetFileName(d);
                if (string.Equals(name, ArchiveCacheEvictor.TmpFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                onDisk.Add(name);
            }

            using var conn = Open();
            if (conn == null) return;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT signature FROM cache_entry;";
                using var rd = sel.ExecuteReader();
                while (rd.Read()) known.Add(rd.GetString(0));
            }
            foreach (var sig in known)
                if (!onDisk.Contains(sig))
                {
                    using var del = conn.CreateCommand();
                    del.CommandText = "DELETE FROM cache_entry WHERE signature=$s;";
                    del.Parameters.AddWithValue("$s", sig);
                    del.ExecuteNonQuery();
                }

            foreach (var sig in onDisk)
            {
                if (known.Contains(sig)) continue;
                string dir = System.IO.Path.Combine(root, sig);
                DateTime when; try { when = System.IO.Directory.GetLastWriteTimeUtc(dir); } catch { when = DateTime.UtcNow; }
                using var ins = conn.CreateCommand();
                ins.CommandText = @"INSERT OR IGNORE INTO cache_entry
                        (signature, game_title, platform, emulator, source_path, mode, output_file, size_bytes, cached_utc, last_played_utc)
                        VALUES ($s,'(unindexed)','','','','','',$z,$t,$t);";
                ins.Parameters.AddWithValue("$s", sig);
                ins.Parameters.AddWithValue("$z", ArchiveCacheEvictor.DirSize(dir));
                ins.Parameters.AddWithValue("$t", when.ToString("O"));
                ins.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log("Reconcile failed: " + ex.Message); }
    }

    private static DateTime ParseUtc(string? s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : default;

    private static void Log(string msg) => LbLog.Info("rom", "cache-db: " + msg);
}
