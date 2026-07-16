// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — per-archive memory (durable state). Slice R3.
// ─────────────────────────────────────────────────────────────────────────────
//
// Durable USER STATE per archive, keyed on the archive's SHORT SIGNATURE (content
// signature — survives a rename/move; only a content change invalidates it):
//
//   • LastPlayed — up to 5 entries per archive, MRU order. The auto-pick prefers
//     the last-played entry (unless "Clear → pure priority" forces it off), and the
//     ROM-list surfaces float it to the top.
//   • Favorites — the user's starred entries (display-only; never drive auto-launch).
//
// This is PRECIOUS state, so it lives in its OWN db (rom-archive-history.db) apart
// from the rebuildable listing/cache-manifest (rom-archive-cache.db) — nuking the
// cache must never lose favourites. Connection-per-op + WAL (like the plugin's
// ArchiveHistory) so the launch thread can write while the UI reads. Ported from
// ExtendDB's ArchiveHistory + the archive_history schema of its CacheDb.

#nullable enable

using System;
using System.Collections.Generic;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Rom;

internal static class ArchiveHistory
{
    private static readonly object _init = new();
    private static bool _ready;

    private static string DbPath => LiteBoxPaths.File("rom-archive-history.db");

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
                        "CREATE TABLE IF NOT EXISTS archive_history(" +
                        "  short_signature TEXT NOT NULL, entry_filename TEXT NOT NULL," +
                        "  kind TEXT NOT NULL, last_touched_utc TEXT NOT NULL," +
                        "  PRIMARY KEY(short_signature, entry_filename, kind));";
                    cmd.ExecuteNonQuery();
                    _ready = true;
                }
            }
            return conn;
        }
        catch (Exception ex) { Log("Open failed: " + ex.Message); return null; }
    }

    /// <summary>Records that <paramref name="entryFilename"/> was just launched from the archive
    /// identified by <paramref name="shortSignature"/>. Keeps only the last 5 'played' entries per
    /// archive. The entry identity is the launched entry's PathInArchive (basename for flat archives).</summary>
    public static void RecordPlayed(string shortSignature, string entryFilename)
    {
        if (string.IsNullOrEmpty(shortSignature) || string.IsNullOrEmpty(entryFilename)) return;
        try
        {
            using var conn = Open();
            if (conn == null) return;
            using (var up = conn.CreateCommand())
            {
                up.CommandText =
                    "INSERT INTO archive_history (short_signature, entry_filename, kind, last_touched_utc) " +
                    "VALUES ($s, $e, 'played', $t) " +
                    "ON CONFLICT(short_signature, entry_filename, kind) DO UPDATE SET last_touched_utc = excluded.last_touched_utc;";
                up.Parameters.AddWithValue("$s", shortSignature);
                up.Parameters.AddWithValue("$e", entryFilename);
                up.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                up.ExecuteNonQuery();
            }
            using (var trim = conn.CreateCommand())
            {
                trim.CommandText =
                    "DELETE FROM archive_history WHERE short_signature = $s AND kind = 'played' " +
                    "AND entry_filename NOT IN (" +
                    "  SELECT entry_filename FROM archive_history WHERE short_signature = $s AND kind = 'played' " +
                    "  ORDER BY last_touched_utc DESC LIMIT 5);";
                trim.Parameters.AddWithValue("$s", shortSignature);
                trim.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log($"RecordPlayed({shortSignature}/{entryFilename}) failed: {ex.Message}"); }
    }

    /// <summary>Adds or removes <paramref name="entryFilename"/> from the favourites set of
    /// <paramref name="shortSignature"/>. Favourites are display-only — they do NOT drive auto-launch.</summary>
    public static void ToggleFavorite(string shortSignature, string entryFilename, bool favorite)
    {
        if (string.IsNullOrEmpty(shortSignature) || string.IsNullOrEmpty(entryFilename)) return;
        try
        {
            using var conn = Open();
            if (conn == null) return;
            using var cmd = conn.CreateCommand();
            if (favorite)
            {
                cmd.CommandText =
                    "INSERT INTO archive_history (short_signature, entry_filename, kind, last_touched_utc) " +
                    "VALUES ($s, $e, 'favorite', $t) ON CONFLICT DO NOTHING;";
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            }
            else
            {
                cmd.CommandText =
                    "DELETE FROM archive_history WHERE short_signature = $s AND entry_filename = $e AND kind = 'favorite';";
            }
            cmd.Parameters.AddWithValue("$s", shortSignature);
            cmd.Parameters.AddWithValue("$e", entryFilename);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log($"ToggleFavorite({shortSignature}/{entryFilename}, {favorite}) failed: {ex.Message}"); }
    }

    /// <summary>The favourites set (entry identities, case-insensitive) for an archive. Empty when
    /// nothing is starred.</summary>
    public static HashSet<string> GetFavorites(string shortSignature)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(shortSignature)) return set;
        try
        {
            using var conn = Open();
            if (conn == null) return set;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT entry_filename FROM archive_history WHERE short_signature = $s AND kind = 'favorite';";
            cmd.Parameters.AddWithValue("$s", shortSignature);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) set.Add(rd.GetString(0));
        }
        catch (Exception ex) { Log($"GetFavorites({shortSignature}) failed: {ex.Message}"); }
        return set;
    }

    /// <summary>The last-played entries (MRU, up to 5) for an archive. Empty when never played.</summary>
    public static List<string> GetLastPlayed(string shortSignature)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(shortSignature)) return list;
        try
        {
            using var conn = Open();
            if (conn == null) return list;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT entry_filename FROM archive_history WHERE short_signature = $s AND kind = 'played' " +
                "ORDER BY last_touched_utc DESC LIMIT 5;";
            cmd.Parameters.AddWithValue("$s", shortSignature);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(rd.GetString(0));
        }
        catch (Exception ex) { Log($"GetLastPlayed({shortSignature}) failed: {ex.Message}"); }
        return list;
    }

    private static void Log(string msg) => LbLog.Info("rom", "history: " + msg);
}
