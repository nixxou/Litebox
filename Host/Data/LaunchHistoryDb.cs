// LiteBox's own last-launch history — its OWN database (Core\litebox\launch-history.db).
//
// This is DURABLE per-game state: the last emulator / version / ROM launched, plus the launch→detection
// latency. It lived inside the write-back op-log's DB (LiteBox.pending.db) for a while, but that DB's `ops`
// table is EPHEMERAL (flushed to LaunchBox and, on a breaking schema bump, DROPped — which would take this
// durable data with it). Different lifecycle, no cross-table query → it stands on its own.
//
// Consumers:
//   • the launch buttons (native + web) seed their initial emulator / version / ROM from here,
//   • RaLaunchCorrect reads the launched ROM entry to correct the IGame's RA hash,
//   • the startup progress bar / reveal ceiling read detection_ms.
//
// Connection-per-op (WAL), same shape as ArchiveHistory. The schema is ensured once per session.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

internal static class LaunchHistoryDb
{
    private static readonly object _init = new();
    private static bool _ready;

    private static string DbPath => LiteBoxPaths.File("launch-history.db");

    private static void Log(string m) { try { Console.WriteLine("[launch-history] " + m); } catch { } }

    /// <summary>Fresh connection, schema ensured once. Null on failure → callers
    /// no-op / return null, exactly like the old op-log's disabled state.</summary>
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
                            "CREATE TABLE IF NOT EXISTS launch_history(" +
                            "  game_id TEXT NOT NULL PRIMARY KEY, additional_app_id TEXT, emulator_id TEXT," +
                            "  extracted_rom_path TEXT, last_launched_utc TEXT NOT NULL, detection_ms INTEGER);";
                        cmd.ExecuteNonQuery();
                    }
                    _ready = true;
                }
            }
            return conn;
        }
        catch (Exception ex) { Log("open failed: " + ex.Message); return null; }
    }

    /// <summary>The whole table in one read: game id, the version last launched, the archive entry last
    /// extracted. The per-game getters below are right for a launch button; asking them once per game at
    /// boot would be one round trip per game for a table that fits in a breath.</summary>
    public static List<(string GameId, string? AppId, string? ExtractedRomPath)> AllLaunches()
    {
        var res = new List<(string, string?, string?)>();
        try
        {
            using var conn = Open(); if (conn == null) return res;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT game_id, additional_app_id, extracted_rom_path FROM launch_history";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                res.Add((r.GetString(0),
                         r.IsDBNull(1) ? null : r.GetString(1),
                         r.IsDBNull(2) ? null : r.GetString(2)));
        }
        catch (Exception ex) { Log("bulk read failed: " + ex.Message); }
        return res;
    }

    /// <summary>Upsert the last emulator/version used for a game (ROM left NULL — RecordLaunchRomEntry sets it).
    /// UPSERT, NOT INSERT OR REPLACE, so detection_ms / extracted_rom_path survive.</summary>
    public static void RecordLaunch(string gameId, string? emulatorId, string? additionalAppId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO launch_history(game_id, additional_app_id, emulator_id, extracted_rom_path, last_launched_utc) " +
                "VALUES($g,$a,$e,NULL,$t) " +
                "ON CONFLICT(game_id) DO UPDATE SET additional_app_id=excluded.additional_app_id, " +
                "emulator_id=excluded.emulator_id, last_launched_utc=excluded.last_launched_utc";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$a", (object?)additionalAppId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)emulatorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("record failed: " + ex.Message); }
    }

    /// <summary>Record the launched ROM entry (in-archive identity). UPSERT that ONLY touches
    /// extracted_rom_path (preserves emulator/app/detection_ms); creates a bare row when none exists.</summary>
    public static void RecordLaunchRomEntry(string gameId, string? romEntry)
    {
        // Le defaut RomM suit le dernier joue : cette ROM vient peut-etre de le deplacer.
        try { Romm.RommIndexer.Touch(gameId); } catch { }
        if (string.IsNullOrEmpty(gameId)) return;
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO launch_history(game_id, extracted_rom_path, last_launched_utc) VALUES($g,$r,$t) " +
                "ON CONFLICT(game_id) DO UPDATE SET extracted_rom_path=excluded.extracted_rom_path";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$r", (object?)romEntry ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("rom-entry record failed: " + ex.Message); }
    }

    /// <summary>Deletes the game's row (reset-to-default button → next read seeds pure defaults).</summary>
    public static void ClearLaunch(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM launch_history WHERE game_id=$g";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("clear failed: " + ex.Message); }
    }

    /// <summary>The last (emulatorId, additionalAppId) for a game, or null. Either field may be null.</summary>
    public static (string? emulatorId, string? additionalAppId)? GetLastLaunch(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT emulator_id, additional_app_id FROM launch_history WHERE game_id=$g";
            cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
        }
        catch (Exception ex) { Log("get failed: " + ex.Message); }
        return null;
    }

    /// <summary>The last (emulatorId, additionalAppId, extractedRomPath) for a game, or null. Any field may
    /// be null (default emulator / Base version / no ROM pick).</summary>
    public static (string? emulatorId, string? additionalAppId, string? extractedRomPath)? GetLastLaunchFull(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT emulator_id, additional_app_id, extracted_rom_path FROM launch_history WHERE game_id=$g";
            cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return (r.IsDBNull(0) ? null : r.GetString(0),
                        r.IsDBNull(1) ? null : r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2));
        }
        catch (Exception ex) { Log("get(full) failed: " + ex.Message); }
        return null;
    }

    /// <summary>Record the launch→SmartCapture-detection latency (ms). UPSERT that ONLY touches detection_ms;
    /// creates a bare row (with last_launched_utc) for a game that has none yet.</summary>
    public static void RecordDetection(string gameId, long detectionMs)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        try
        {
            using var conn = Open(); if (conn == null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO launch_history(game_id, last_launched_utc, detection_ms) VALUES($g,$t,$d) " +
                "ON CONFLICT(game_id) DO UPDATE SET detection_ms=excluded.detection_ms";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$d", detectionMs);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Log("detection record failed: " + ex.Message); }
    }

    /// <summary>The last launch→detection latency (ms) for a game, or null if none / never detected.</summary>
    public static long? GetLastDetectionMs(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;
        try
        {
            using var conn = Open(); if (conn == null) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT detection_ms FROM launch_history WHERE game_id=$g";
            cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            if (r.Read() && !r.IsDBNull(0)) return r.GetInt64(0);
        }
        catch (Exception ex) { Log("detection get failed: " + ex.Message); }
        return null;
    }
}
