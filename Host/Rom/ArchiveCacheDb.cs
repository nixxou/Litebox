// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — SQLite backing (LISTING half). Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// Stores the parsed entry list of each archive, keyed on the 10-hex UPPER path
// signature (== ArchiveSig.ComputePathSignature). Lives at
// <LB>\Core\litebox\rom-archive-cache.db (rebuildable — nuke it and it re-lists).
//
// R2 owns ONLY the listing rows (the `archive` head + `archive_entry` rows). The
// cache-manifest (on-disk extractions) and per-entry RA hash/id halves are R3 /
// the RetroAchievements module's own DB — this schema deliberately carries NO RA
// columns so the two never collide: the RA module keeps rom_hash in its dedicated
// retroachievements DB, cross-referenced by the SAME <SIG> + path_in_archive.
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
                        "  signature TEXT PRIMARY KEY, path TEXT, size INTEGER, short_sig TEXT, cached_at TEXT);" +
                        "CREATE TABLE IF NOT EXISTS archive_entry(" +
                        "  signature TEXT NOT NULL, filename TEXT, path_in_archive TEXT, size INTEGER," +
                        "  PRIMARY KEY(signature, path_in_archive));";
                    cmd.ExecuteNonQuery();
                    _ready = true;
                }
            }
            return conn;
        }
        catch (Exception ex) { Log("Open failed: " + ex.Message); return null; }
    }

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
            // Replace the entry set for this signature.
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM archive_entry WHERE signature=$s;";
                del.Parameters.AddWithValue("$s", sig);
                del.ExecuteNonQuery();
            }
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

    private static DateTime ParseUtc(string? s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : default;

    private static void Log(string msg) => LbLog.Info("rom", "cache-db: " + msg);
}
