// RetroAchievements engine STORAGE — the SQLite half of the RA-engine migration (P0 of
// docs/ra-engine-migration-plan.md), a faithful port of the plugin's RetroAchievementsDb +
// ArchiveCacheDb RA surface. Lives in the ROM module's rom-archive-cache.db (schema owned by
// Host/Rom/ArchiveCacheDb — the per-entry RA columns join the listing rows on the same
// (signature, path_in_archive) key, mirroring the plugin's own DB merge). Everything here is
// rebuildable: nuke the file and scans/refreshes repopulate it.
//
// Tables used (see ArchiveCacheDb for DDL):
//   rom_hash       — standalone ROMs: signature → path/size/hash/raid (signature = the ROM
//                    module's 10-hex portable path signature, deduping multi-game files)
//   archive_entry  — RA columns per archive entry (hash/raid); archive.parse_state tracks the
//                    RA full-parse (0 unparsed / 1 ok / 2 failed), distinct from the listing flags
//   ra_game/ra_hash — per-console catalogue + reverse hash→raid index (lowercase hashes)
//   ra_console      — refresh schedule stamps (games_refreshed_at / next_refresh_at)

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LbApiHost.Host.Rom;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Ra;

internal sealed class RaGameRow
{
    public int Id;
    public string? Title, ConsoleName, ImageIcon, DateModified;
    public int NumAchievements, NumLeaderboards, Points;
    public int? ForumTopicId;
    public List<string> Hashes = new();
}

internal static class RaStore
{
    public const int ParseUnparsed = 0, ParseOk = 1, ParseFailed = 2;

    private static SqliteConnection? Open() => ArchiveCacheDb.OpenForRa();

    // ── rom_hash (standalone ROMs) ──────────────────────────────────────────────

    /// <summary>Cached (hash, raid) for a standalone ROM signature, or null when never scanned.</summary>
    public static (string Hash, int Raid)? GetRomHash(string sig)
    {
        if (string.IsNullOrEmpty(sig)) return null;
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT RetroAchievementsHash, RetroAchievementsId FROM rom_hash WHERE signature=$s;";
            cmd.Parameters.AddWithValue("$s", sig);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read() || rd.IsDBNull(0)) return null;
            return (rd.GetString(0), rd.IsDBNull(1) ? 0 : rd.GetInt32(1));
        }
        catch (Exception ex) { Log("GetRomHash: " + ex.Message); return null; }
    }

    public static void UpsertRomHash(string sig, string path, long size, string hash, int raid)
    {
        if (string.IsNullOrEmpty(sig)) return;
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO rom_hash (signature, path, size, RetroAchievementsHash, RetroAchievementsId, computed_at)
                                VALUES ($s,$p,$z,$h,$i,$t)
                                ON CONFLICT(signature) DO UPDATE SET
                                   path=excluded.path, size=excluded.size,
                                   RetroAchievementsHash=excluded.RetroAchievementsHash,
                                   RetroAchievementsId=excluded.RetroAchievementsId,
                                   computed_at=excluded.computed_at;";
            cmd.Parameters.AddWithValue("$s", sig);
            cmd.Parameters.AddWithValue("$p", path ?? "");
            cmd.Parameters.AddWithValue("$z", size);
            cmd.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$i", raid > 0 ? raid : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("UpsertRomHash: " + ex.Message); }
    }

    // ── archive_entry RA columns + parse_state ──────────────────────────────────

    public static int GetParseState(string sig)
    {
        try
        {
            using var conn = Open(); if (conn == null) return ParseUnparsed;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT parse_state FROM archive WHERE signature=$s;";
            cmd.Parameters.AddWithValue("$s", sig);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? ParseUnparsed : Convert.ToInt32(v);
        }
        catch (Exception ex) { Log("GetParseState: " + ex.Message); return ParseUnparsed; }
    }

    public static void SetParseState(string sig, int state)
    {
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            // Upsert: the RA parse can run before the listing ever cached this archive — the head row
            // is created bare and the listing upsert fills path/size later (it leaves parse_state alone).
            cmd.CommandText = @"INSERT INTO archive (signature, parse_state) VALUES ($s, $v)
                                ON CONFLICT(signature) DO UPDATE SET parse_state=excluded.parse_state;";
            cmd.Parameters.AddWithValue("$v", state);
            cmd.Parameters.AddWithValue("$s", sig);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("SetParseState: " + ex.Message); }
    }

    /// <summary>Write one archive entry's RA hash/raid (inserting the row if the listing hasn't
    /// cached it yet — the RA parse may run before the first list).</summary>
    public static void UpsertEntryRa(string sig, string pathInArchive, long size, string hash, int raid)
    {
        if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(pathInArchive)) return;
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO archive_entry (signature, filename, path_in_archive, size, RetroAchievementsHash, RetroAchievementsId)
                                VALUES ($s,$f,$p,$z,$h,$i)
                                ON CONFLICT(signature, path_in_archive) DO UPDATE SET
                                   RetroAchievementsHash=excluded.RetroAchievementsHash,
                                   RetroAchievementsId=excluded.RetroAchievementsId;";
            cmd.Parameters.AddWithValue("$s", sig);
            cmd.Parameters.AddWithValue("$f", Path.GetFileName(pathInArchive));
            cmd.Parameters.AddWithValue("$p", pathInArchive);
            cmd.Parameters.AddWithValue("$z", size);
            cmd.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$i", raid > 0 ? raid : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("UpsertEntryRa: " + ex.Message); }
    }

    /// <summary>All RA-annotated entries of an archive: path_in_archive → (hash, raid).</summary>
    public static Dictionary<string, (string Hash, int Raid)> GetEntriesRa(string sig)
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(sig)) return map;
        try
        {
            using var conn = Open(); if (conn == null) return map;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT path_in_archive, RetroAchievementsHash, RetroAchievementsId
                                FROM archive_entry WHERE signature=$s AND RetroAchievementsHash IS NOT NULL;";
            cmd.Parameters.AddWithValue("$s", sig);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                map[rd.GetString(0)] = (rd.GetString(1), rd.IsDBNull(2) ? 0 : rd.GetInt32(2));
        }
        catch (Exception ex) { Log("GetEntriesRa: " + ex.Message); }
        return map;
    }

    /// <summary>path_in_archive → RA game TITLE for the picker surfaces (join on ra_game).</summary>
    public static Dictionary<string, string> GetEntryRaTitles(string sig)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(sig)) return map;
        try
        {
            using var conn = Open(); if (conn == null) return map;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT e.path_in_archive, g.title
                                FROM archive_entry e JOIN ra_game g ON g.id = e.RetroAchievementsId
                                WHERE e.signature=$s AND e.RetroAchievementsId IS NOT NULL;";
            cmd.Parameters.AddWithValue("$s", sig);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                if (!rd.IsDBNull(1)) map[rd.GetString(0)] = rd.GetString(1);
        }
        catch (Exception ex) { Log("GetEntryRaTitles: " + ex.Message); }
        return map;
    }

    /// <summary>Paths (in-archive) of entries with a matched raid — feeds the auto-pick RA bonus.</summary>
    public static HashSet<string> GetRaMatchedPaths(string sig)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(sig)) return set;
        try
        {
            using var conn = Open(); if (conn == null) return set;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT path_in_archive FROM archive_entry
                                WHERE signature=$s AND RetroAchievementsId IS NOT NULL;";
            cmd.Parameters.AddWithValue("$s", sig);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) set.Add(rd.GetString(0));
        }
        catch (Exception ex) { Log("GetRaMatchedPaths: " + ex.Message); }
        return set;
    }

    // ── Catalogue (ra_game / ra_hash / ra_console) ──────────────────────────────

    /// <summary>Atomic whole-console replace: wipe its ra_game/ra_hash rows, insert the fresh set,
    /// stamp the schedule. A crash mid-pull never leaves a half-catalogue (plugin parity).</summary>
    public static void ReplaceConsoleCatalog(int consoleId, IEnumerable<RaGameRow> games,
                                             DateTime refreshedUtc, DateTime nextUtc)
    {
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var tx = conn.BeginTransaction();

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = @"DELETE FROM ra_hash WHERE game_id IN (SELECT id FROM ra_game WHERE console_id=$c);
                                    DELETE FROM ra_game WHERE console_id=$c;";
                del.Parameters.AddWithValue("$c", consoleId);
                del.ExecuteNonQuery();
            }

            using (var insG = conn.CreateCommand())
            using (var insH = conn.CreateCommand())
            {
                insG.Transaction = tx;
                insG.CommandText = @"INSERT OR REPLACE INTO ra_game
                                        (id, console_id, title, console_name, image_icon,
                                         num_achievements, num_leaderboards, points, date_modified, forum_topic_id)
                                     VALUES ($id, $c, $t, $cn, $img, $n, $nl, $pts, $d, $ftid);";
                var gId = insG.Parameters.Add("$id", SqliteType.Integer);
                insG.Parameters.AddWithValue("$c", consoleId);
                var gT = insG.Parameters.Add("$t", SqliteType.Text);
                var gCn = insG.Parameters.Add("$cn", SqliteType.Text);
                var gImg = insG.Parameters.Add("$img", SqliteType.Text);
                var gN = insG.Parameters.Add("$n", SqliteType.Integer);
                var gNl = insG.Parameters.Add("$nl", SqliteType.Integer);
                var gPts = insG.Parameters.Add("$pts", SqliteType.Integer);
                var gD = insG.Parameters.Add("$d", SqliteType.Text);
                var gFt = insG.Parameters.Add("$ftid", SqliteType.Integer);

                insH.Transaction = tx;
                insH.CommandText = "INSERT OR REPLACE INTO ra_hash (hash, game_id) VALUES ($h, $g);";
                var hH = insH.Parameters.Add("$h", SqliteType.Text);
                var hG = insH.Parameters.Add("$g", SqliteType.Integer);

                if (games != null)
                    foreach (var g in games)
                    {
                        if (g == null || g.Id <= 0) continue;
                        gId.Value = g.Id;
                        gT.Value = (object?)g.Title ?? DBNull.Value;
                        gCn.Value = (object?)g.ConsoleName ?? DBNull.Value;
                        gImg.Value = (object?)g.ImageIcon ?? DBNull.Value;
                        gN.Value = g.NumAchievements;
                        gNl.Value = g.NumLeaderboards;
                        gPts.Value = g.Points;
                        gD.Value = (object?)g.DateModified ?? DBNull.Value;
                        gFt.Value = g.ForumTopicId.HasValue ? g.ForumTopicId.Value : (object)DBNull.Value;
                        insG.ExecuteNonQuery();

                        foreach (var h in g.Hashes)
                        {
                            if (string.IsNullOrWhiteSpace(h)) continue;
                            hH.Value = h.Trim().ToLowerInvariant();
                            hG.Value = g.Id;
                            insH.ExecuteNonQuery();
                        }
                    }
            }

            StampSchedule(conn, tx, consoleId, refreshedUtc, nextUtc);
            tx.Commit();
        }
        catch (Exception ex) { Log("ReplaceConsoleCatalog: " + ex.Message); }
    }

    /// <summary>Back-off the next attempt WITHOUT touching games_refreshed_at (the UI keeps showing
    /// the last successful pull) — after a failed/empty/rejected pull.</summary>
    public static void SetConsoleNextRefresh(int consoleId, DateTime nextUtc)
    {
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ra_console (id, next_refresh_at) VALUES ($id, $x)
                                ON CONFLICT(id) DO UPDATE SET next_refresh_at=excluded.next_refresh_at;";
            cmd.Parameters.AddWithValue("$x", nextUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$id", consoleId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("SetConsoleNextRefresh: " + ex.Message); }
    }

    /// <summary>(games_refreshed_at, next_refresh_at) for a console — (null, null) when never pulled.</summary>
    public static (DateTime? RefreshedUtc, DateTime? NextUtc) GetConsoleSchedule(int consoleId)
    {
        try
        {
            using var conn = Open(); if (conn == null) return (null, null);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT games_refreshed_at, next_refresh_at FROM ra_console WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", consoleId);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return (null, null);
            return (ParseUtc(rd.IsDBNull(0) ? null : rd.GetString(0)),
                    ParseUtc(rd.IsDBNull(1) ? null : rd.GetString(1)));
        }
        catch (Exception ex) { Log("GetConsoleSchedule: " + ex.Message); return (null, null); }
    }

    /// <summary>Of the given candidate consoles, those DUE for a catalogue pull as of now:
    /// never pulled, or past their next_refresh_at.</summary>
    public static List<int> FilterDueConsoles(IEnumerable<int> candidates, DateTime nowUtc)
    {
        var due = new List<int>();
        foreach (var id in candidates)
        {
            var (_, next) = GetConsoleSchedule(id);
            if (next == null || next.Value <= nowUtc) due.Add(id);
        }
        return due;
    }

    /// <summary>Raid for a RA hash from the catalogue (case-insensitive), 0 when unknown.</summary>
    public static int LookupRaid(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return 0;
        try
        {
            using var conn = Open(); if (conn == null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT game_id FROM ra_hash WHERE hash=$h;";
            cmd.Parameters.AddWithValue("$h", hash.Trim().ToLowerInvariant());
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
        }
        catch (Exception ex) { Log("LookupRaid: " + ex.Message); return 0; }
    }

    /// <summary>Hours since the console's last SUCCESSFUL catalogue pull (games_refreshed_at);
    /// double.MaxValue when never pulled. Drives the panel's "Refresh · Nh/Nd/never" label and the
    /// startup rolling refresh's oldest-first pick.</summary>
    public static double CatalogueAgeHours(int consoleId)
    {
        var (refreshed, _) = GetConsoleSchedule(consoleId);
        return refreshed == null ? double.MaxValue : (DateTime.UtcNow - refreshed.Value).TotalHours;
    }

    /// <summary>ra_game row count for a console (0 = no catalogue yet) — the count-delta guard's baseline.</summary>
    public static int CatalogueCount(int consoleId)
    {
        try
        {
            using var conn = Open(); if (conn == null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ra_game WHERE console_id=$c;";
            cmd.Parameters.AddWithValue("$c", consoleId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex) { Log("CatalogueCount: " + ex.Message); return 0; }
    }

    /// <summary>Re-link every already-scanned ROM to the CURRENT catalogue (no re-hashing): one
    /// indexed UPDATE per table, raid set to the matching ra_hash row or NULL when the hash left
    /// the catalogue. Run after a console refresh (plugin's ReResolveScannedIds).</summary>
    public static void ReResolveScannedIds()
    {
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE rom_hash SET RetroAchievementsId =
                    (SELECT h.game_id FROM ra_hash h WHERE h.hash = lower(rom_hash.RetroAchievementsHash))
                  WHERE RetroAchievementsHash IS NOT NULL;
                UPDATE archive_entry SET RetroAchievementsId =
                    (SELECT h.game_id FROM ra_hash h WHERE h.hash = lower(archive_entry.RetroAchievementsHash))
                  WHERE RetroAchievementsHash IS NOT NULL;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("ReResolveScannedIds: " + ex.Message); }
    }

    /// <summary>Wipe OUR computed hashes (rom_hash + archive_entry RA columns + parse_state), keep
    /// the downloaded catalogue. IGame fields are cleared separately by the caller.</summary>
    public static void ClearScannedHashes()
    {
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM rom_hash;
                UPDATE archive_entry SET RetroAchievementsHash=NULL, RetroAchievementsId=NULL;
                UPDATE archive SET parse_state=0;";
            cmd.ExecuteNonQuery();
            Log("ClearScannedHashes: rom_hash wiped, entry RA cols + parse_state reset (catalogue kept).");
        }
        catch (Exception ex) { Log("ClearScannedHashes: " + ex.Message); }
    }

    /// <summary>Canary genuineness check on a FRESH pull's hash set (before it is applied): true when
    /// at least one frozen canary pair is present, or the console has no canaries (never-drop default
    /// is decided by the CALLER — this only reports presence).</summary>
    public static bool CanaryPresent(int consoleId, IReadOnlyCollection<RaGameRow> pulled)
    {
        var pairs = RaCanaries.For(consoleId);
        if (pairs.Length == 0) return false;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in pulled) foreach (var h in g.Hashes) hashes.Add(h.Trim());
        foreach (var (hash, raid) in pairs)
            if (hashes.Contains(hash)) return true;
        return false;
    }

    private static void StampSchedule(SqliteConnection conn, SqliteTransaction? tx, int consoleId,
                                      DateTime refreshedUtc, DateTime nextUtc)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO ra_console (id, games_refreshed_at, next_refresh_at) VALUES ($id, $r, $x)
                            ON CONFLICT(id) DO UPDATE SET
                               games_refreshed_at=excluded.games_refreshed_at,
                               next_refresh_at=excluded.next_refresh_at;";
        cmd.Parameters.AddWithValue("$id", consoleId);
        cmd.Parameters.AddWithValue("$r", refreshedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$x", nextUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static DateTime? ParseUtc(string? s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static void Log(string msg) => Diag.LbLog.Info("ra", "store: " + msg);
}
