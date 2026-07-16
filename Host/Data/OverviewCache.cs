// The precomputed "best description" cache — native port of ExtendDB's DefaultOverviewCache.
//
// The Extended DB carries one overview column per source (Overview = LaunchBox, OverviewSc{En..Pt},
// OverviewSteam{En..Pt}, OverviewVndb, OverviewIgdb, OverviewIgdbStoryline, OverviewAi{En..Pt}). The user
// orders sources in Options → Modules → Base ("Metadata description sources", [Base] OverviewSources); this
// engine materialises `defaultOverview` = COALESCE(columns in that order) on the Games table so readers get
// the resolved text in one cheap column read instead of a per-row COALESCE over a wide column set.
//
// Signature model (same as the plugin): ExtendDBState["defaultoverview.signature"] holds the "|"-joined source
// list the column was built for — absent/empty = never built, "WIP" = rebuild in progress, else compare with
// the current list. Readers use ReadExpression(): the plain column when valid, the dynamic COALESCE otherwise,
// so they are always correct even mid-rebuild.
//
// Writes only ever touch LiteBox's OWN Extended-DB copy (Core\litebox — see ExtDbDownloader adoption); a
// legacy plugin copy stays read-only and simply reads through the COALESCE fallback until adopted.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Diag;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

internal static class OverviewCache
{
    public const string ColumnName = "defaultOverview";
    private const string StateKey = "defaultoverview.signature";
    private const string SignatureWip = "WIP";
    private static int _running;   // single-flight

    // ── Source list + signature ────────────────────────────────────────────────

    /// <summary>The ordered source list from [Base] OverviewSources (same default as the Base panel).</summary>
    public static List<string> Sources()
    {
        var raw = LiteBoxConfig.LoadForExe().GetSec("Base", "OverviewSources",
                      "Launchbox,ScreenScraper-En,Steam-En,VNDB,Ai-En") ?? "Launchbox";
        var items = raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (!items.Any(s => s.Equals("Launchbox", StringComparison.OrdinalIgnoreCase))) items.Insert(0, "Launchbox");
        return items;
    }

    public static string Signature() => string.Join("|", Sources());

    /// <summary>Maps a "Source-Lang" token to its overview column (quoted), mirroring the plugin's mapping.</summary>
    public static string? ColumnFor(string sourceWithLang)
    {
        if (string.IsNullOrWhiteSpace(sourceWithLang)) return null;
        if (sourceWithLang.Equals("Launchbox", StringComparison.OrdinalIgnoreCase)) return "\"Overview\"";
        if (sourceWithLang.Equals("VNDB", StringComparison.OrdinalIgnoreCase)) return "\"OverviewVndb\"";
        if (sourceWithLang.Equals("Igdb", StringComparison.OrdinalIgnoreCase)) return "\"OverviewIgdb\"";
        if (sourceWithLang.Equals("IgdbStoryline", StringComparison.OrdinalIgnoreCase)) return "\"OverviewIgdbStoryline\"";
        var parts = sourceWithLang.Split('-');
        if (parts.Length != 2) return null;
        string suffix = parts[1].Trim().ToUpperInvariant() switch
        {
            "EN" => "En", "FR" => "Fr", "DE" => "De", "ES" => "Es", "IT" => "It", "PT" => "Pt", _ => "",
        };
        if (suffix.Length == 0) return null;
        return parts[0].Trim() switch
        {
            "ScreenScraper" => $"\"OverviewSc{suffix}\"",
            "Steam"         => $"\"OverviewSteam{suffix}\"",
            "Ai"            => $"\"OverviewAi{suffix}\"",
            _ => null,
        };
    }

    /// <summary>COALESCE(...) over the current priority order (always ends on "Overview" as the floor).
    /// When <paramref name="dbPath"/> is given, columns absent from that DB's Games table are dropped so the
    /// expression never breaks a query against an older/partial schema.</summary>
    public static string CoalesceExpression(string? dbPath = null)
    {
        var cols = new List<string>();
        foreach (var s in Sources())
        {
            var c = ColumnFor(s);
            if (c != null && !cols.Contains(c)) cols.Add(c);
        }
        if (dbPath != null && File.Exists(dbPath))
        {
            try
            {
                using var conn = Open(dbPath, readOnly: true);
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(\"Games\")";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) present.Add(r.GetString(1));
                }
                cols = cols.Where(c => present.Contains(c.Trim('"'))).ToList();
            }
            catch { }
        }
        if (cols.Count == 0) cols.Add("\"Overview\"");
        return $"COALESCE({string.Join(", ", cols)})";
    }

    // ── Validity + reader expression ───────────────────────────────────────────

    /// <summary>True when the stored signature matches the current source list AND the column exists —
    /// i.e. readers may use the plain column. Reads the DB passed (defaults to the extended DB in use).</summary>
    public static bool IsValid(string? dbPath = null)
    {
        try
        {
            dbPath ??= Media.MetadataDb.ExtendedDbPath;
            if (dbPath == null || !File.Exists(dbPath)) return false;
            using var conn = Open(dbPath, readOnly: true);
            var sig = ReadState(conn, StateKey);
            if (string.IsNullOrEmpty(sig) || sig == SignatureWip || sig != Signature()) return false;
            return ColumnExists(conn, "Games", ColumnName);
        }
        catch { return false; }
    }

    /// <summary>The SQL expression a reader should use for "the description": the cached column when valid,
    /// else the dynamic COALESCE (filtered to the DB's real columns) — always correct, cache = just faster.</summary>
    public static string ReadExpression(string? dbPath = null)
        => IsValid(dbPath) ? $"\"{ColumnName}\"" : CoalesceExpression(dbPath ?? Media.MetadataDb.ExtendedDbPath);

    // ── Rebuild ────────────────────────────────────────────────────────────────

    /// <summary>Recomputes the cache when needed (module on, [Base] EnableOverviewCache, LiteBox owns the DB,
    /// signature stale). Single-flight; safe to call from boot, after a DB install, and from Apply.</summary>
    public static void RunSyncIfNeeded()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        Task.Run(() =>
        {
            try { RunSyncCore(); }
            catch (Exception ex) { LbLog.Warn("overview", "sync failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    private static void RunSyncCore()
    {
        if (!Modules.LbModules.On(Modules.LbModule.Base)) return;
        if (!LiteBoxConfig.LoadForExe().GetSecBool("Base", "EnableOverviewCache", true)) return;

        // Writes only on LiteBox's OWN copy (adoption brings a legacy DB under Core\litebox first).
        var own = ExtDbDownloader.TargetPath;
        if (!File.Exists(own))
        {
            LbLog.Once("overview", "cache skipped: LiteBox doesn't own the Extended DB yet (use Update from GitHub to adopt it) — readers fall back to the dynamic COALESCE.");
            return;
        }

        var sig = Signature();
        using var conn = Open(own, readOnly: false);

        // Ensure schema (column + state table) — idempotent.
        try { Exec(conn, $"ALTER TABLE \"Games\" ADD COLUMN \"{ColumnName}\" TEXT NULL;"); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        Exec(conn, "CREATE TABLE IF NOT EXISTS \"ExtendDBState\" (\"Key\" TEXT NOT NULL PRIMARY KEY, \"Value\" TEXT);");

        var stored = ReadState(conn, StateKey);
        if (stored == sig) return;   // already built for this priority order

        LbLog.Info("overview", $"rebuilding defaultOverview for [{sig}]...");
        WriteState(conn, StateKey, SignatureWip);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exec(conn, $"UPDATE \"Games\" SET \"{ColumnName}\" = {CoalesceExpression()};");
        WriteState(conn, StateKey, sig);
        LbLog.Info("overview", $"defaultOverview rebuilt in {sw.ElapsedMilliseconds} ms.");
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        }.ToString());
        conn.Open();
        if (!readOnly) Exec(conn, "PRAGMA busy_timeout = 60000;");
        return conn;
    }

    private static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static string? ReadState(SqliteConnection c, string key)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT \"Value\" FROM \"ExtendDBState\" WHERE \"Key\" = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }   // table absent → never built
    }

    private static void WriteState(SqliteConnection c, string key, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO \"ExtendDBState\"(\"Key\",\"Value\") VALUES($k,$v) " +
                          "ON CONFLICT(\"Key\") DO UPDATE SET \"Value\"=excluded.\"Value\";";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
