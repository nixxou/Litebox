// Native (plugin-free) LaunchBox -> ExtendDB database merge for LiteBox.
//
// PURPOSE
//   Folds the user's local LaunchBox metadata DB (<LB>\Metadata\LaunchBox.Metadata.db) into
//   LiteBox's OWN copy of the community Extended DB (Core\litebox\LaunchBox.Extended.Metadata.db,
//   the one ExtDbDownloader installs). This is the runtime backend for the BasePanel buttons
//   "Force re-merge", "Undo last merge" and "Unmerge" (previously disabled).
//
//   It NEVER touches the legacy plugin copy under <LB>\Plugins\ExtendDB — only LiteBox's own
//   file is mutated. If only the legacy copy exists, RunMerge refuses (the user must let
//   ExtDbDownloader install LiteBox's own copy first).
//
// RELATIONSHIP TO THE REFERENCE ENGINE (ExtendDB plugin: LbDbMerger / LbExtendedSync / LbDbUpdater)
//   The plugin's engine is a 9-phase mark-and-sweep (restore-then-rebuild) with per-row Backup_*
//   tables, whitelist in-place UPDATEs, Merge-mode duplicate enrichment, a precomputed
//   defaultOverview refresh and a live LB-wizard swap gate. LiteBox has no live LaunchBox process,
//   no plugin caches and uses the Extended DB read-only, so this is a deliberately REDUCED but
//   SAFE and IDEMPOTENT subset that keeps the essential fromlb contract:
//
//     * fromlb column on Games / GameImages / GameAlternateTitles / Platforms:
//         fromlb=1 -> row inserted by a merge (disposable). fromlb=0 -> stable (community/user).
//     * RunMerge is restore-then-rebuild: it first DELETEs every fromlb=1 row (reverting the
//       previous merge exactly, because we ONLY ever insert new rows and never mutate stable
//       ones), then inserts LB rows that are missing from the Extended DB, tagged fromlb=1.
//     * Undo = DELETE all fromlb=1 rows (returns the DB to its pristine, community state).
//     * Unmerge = restore the pristine snapshot captured before the first merge (Core\litebox\
//       merge\pristine.db, a VACUUM INTO clean copy); falls back to the fromlb DELETE when the
//       snapshot is absent. In this subset Undo and Unmerge converge on the same pristine state
//       (see "WHAT IS REDUCED").
//
//   WHAT IS REDUCED vs the plugin (all intentional, all safe):
//     - No per-row Backup_* tables. Undo relies on the fromlb DELETE + the pristine file snapshot.
//     - No Phase F whitelist in-place UPDATE of stable rows: we ONLY insert rows whose DatabaseId
//       is missing from the Extended DB. LB edits to a game already present in the community DB do
//       not propagate. (This is what makes the fromlb DELETE a perfect, lossless undo.)
//     - No Phase G.bis "Merge" duplicate enrichment. Duplicate handling honors Off and Skip; the
//       "Merge" setting behaves like Off (plain insert, no enrichment).
//     - No defaultOverview recompute (Phase I): LiteBox reads overview elsewhere / read-only.
//     - No verbatim EmulatorPlatforms / PlatformAlternateNames replacement (would need a backup to
//       be undoable): skipped.
//     - No wizard swap gate: there is no live LaunchBox reading the DB.
//
// SAFETY
//   Every mutation runs inside a single SQLite transaction (atomic rollback on any error). The
//   first merge on a pristine DB additionally captures a standalone VACUUM-INTO snapshot BEFORE
//   any fromlb=1 row is written, which doubles as the backup-before-mutate safety copy and the
//   Unmerge restore point. WAL + a 60s busy_timeout tolerate concurrent read-only readers.
//   IsMerged() reads the live fromlb=1 count (the ground truth) rather than trusting the sidecar
//   state file, so a DB replaced by ExtDbDownloader is correctly seen as unmerged again.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

internal static class ExtDbMerger
{
    private const string Tag = "extmerge";
    private const string Section = "Base";
    private const string OriginValue = "launchbox";
    private const string SrcAlias = "src";

    // fromlb-tracked tables, in child -> parent order for deletes.
    private static readonly string[] ChildTables = { "GameImages", "GameAlternateTitles" };
    private static readonly string[] AllMergedTables = { "GameImages", "GameAlternateTitles", "Games", "Platforms" };

    // ── Paths ───────────────────────────────────────────────────────────────

    /// <summary>LiteBox's OWN Extended DB copy under Core\litebox\ — the only file this class mutates.</summary>
    public static string OwnExtendedDbPath => ExtDbDownloader.TargetPath;

    /// <summary>The user's LaunchBox metadata DB (&lt;LB&gt;\Metadata\LaunchBox.Metadata.db), or null if absent.</summary>
    public static string? LbMetadataDbPath
    {
        get
        {
            try
            {
                var root = MediaResolver.LbRoot;
                if (string.IsNullOrEmpty(root)) return null;
                var p = Path.Combine(root, "Metadata", "LaunchBox.Metadata.db");
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }
    }

    private static string MergeDir => LiteBoxPaths.Dir("merge");
    private static string PristinePath => Path.Combine(MergeDir, "pristine.db");
    private static string StatePath => Path.Combine(MergeDir, "merge-state.json");

    // ── Config gate ─────────────────────────────────────────────────────────

    /// <summary>[Base] EnableLbMerge — the master switch honored at boot and by RunMerge.</summary>
    public static bool EnableMerge
    {
        get
        {
            try { return LiteBoxConfig.LoadForExe().GetSecBool(Section, "EnableLbMerge", false); }
            catch { return false; }
        }
    }

    private enum DuplicateMode { Off = 0, Skip = 1, Merge = 2 }

    private static DuplicateMode ReadDuplicateMode()
    {
        try
        {
            var v = (LiteBoxConfig.LoadForExe().GetSec(Section, "DuplicateHandling", "Off") ?? "Off").Trim();
            if (string.Equals(v, "Skip", StringComparison.OrdinalIgnoreCase)) return DuplicateMode.Skip;
            if (string.Equals(v, "Merge", StringComparison.OrdinalIgnoreCase)) return DuplicateMode.Merge;
        }
        catch { }
        return DuplicateMode.Off;
    }

    // ── Result ──────────────────────────────────────────────────────────────

    public sealed class MergeResult
    {
        public bool Success;
        public string Message = "";
        public int GamesInserted, ImagesInserted, AltTitlesInserted, PlatformsInserted, FromLbDeleted;
        public override string ToString() => Message;
    }

    // ── Status ──────────────────────────────────────────────────────────────

    /// <summary>True when LiteBox's own Extended DB currently holds any fromlb=1 (merge-inserted) rows.</summary>
    public static bool IsMerged()
    {
        string path = OwnExtendedDbPath;
        if (!File.Exists(path)) return false;
        try
        {
            using var conn = Open(path, readOnly: true);
            return CountFromLb(conn, null) > 0;
        }
        catch { return false; }
    }

    // ════════════════════════════════════════════════════════════════════════
    // RunMerge — restore-then-rebuild fold of the LB DB into the own Extended DB
    // ════════════════════════════════════════════════════════════════════════

    public static MergeResult RunMerge(IProgress<string>? progress = null)
    {
        var r = new MergeResult();

        string extPath = OwnExtendedDbPath;
        if (!File.Exists(extPath))
        {
            r.Message = "LiteBox has no own Extended database copy yet. Use \"Update from GitHub\" first.";
            Report(progress, "! " + r.Message);
            return r;
        }

        string? lbPath = LbMetadataDbPath;
        if (lbPath == null)
        {
            r.Message = "LaunchBox metadata DB (Metadata\\LaunchBox.Metadata.db) not found.";
            Report(progress, "! " + r.Message);
            return r;
        }

        var dupMode = ReadDuplicateMode();
        Report(progress, "──── LB -> ExtendDB merge (LiteBox) ────");
        Report(progress, $"Source: {lbPath}");
        Report(progress, $"Target: {extPath}   (duplicate mode: {dupMode})");

        try
        {
            using var conn = Open(extPath, readOnly: false);
            ApplyWritePragmas(conn);

            EnsureStructure(conn, progress);

            bool alreadyMerged = CountFromLb(conn, null) > 0;

            // Backup-before-mutate + Unmerge restore point: only meaningful from a pristine DB.
            if (!alreadyMerged)
                EnsurePristineSnapshot(conn, extPath, progress);

            long lbSize = 0; try { lbSize = new FileInfo(lbPath).Length; } catch { }

            Attach(conn, lbPath);
            try
            {
                using var tx = conn.BeginTransaction();

                // Restore-then-rebuild: revert the previous merge (delete every fromlb=1 row) so a
                // re-merge is idempotent and starts from the pristine community rows.
                r.FromLbDeleted = RevertFromLb(conn, tx, progress);

                r.PlatformsInserted = InsertMissingPlatforms(conn, tx, progress);
                r.GamesInserted = InsertMissingGames(conn, tx, dupMode, progress);
                r.ImagesInserted = InsertMissingChild(conn, tx, "GameImages",
                    keyColumns: new[] { "DatabaseId", "FileName", "Region" },
                    normalize: ("Region", "'World'"),
                    derivedCol: null, progress);
                r.AltTitlesInserted = InsertMissingChild(conn, tx, "GameAlternateTitles",
                    keyColumns: new[] { "DatabaseId", "AlternateName", "Region" },
                    normalize: null,
                    derivedCol: ("Platform",
                        "(SELECT g.\"Platform\" FROM main.\"Games\" g WHERE g.\"DatabaseId\" = s.\"DatabaseId\")"),
                    progress);

                tx.Commit();
            }
            finally
            {
                Detach(conn);
            }

            CheckpointTruncate(conn);
            SaveState(pristineVersion: LoadState().PristineVersion, lastLbSize: lbSize, merged: true);

            MetadataDb.InvalidateExtendedDbProbe();

            r.Success = true;
            r.Message =
                $"Merge done. Games +{r.GamesInserted}, images +{r.ImagesInserted}, " +
                $"alt-titles +{r.AltTitlesInserted}, platforms +{r.PlatformsInserted} " +
                $"(reverted {r.FromLbDeleted} prior rows).";
            Report(progress, "✓ " + r.Message);
            return r;
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, "RunMerge failed: " + ex.Message);
            r.Message = "Merge failed: " + ex.Message;
            Report(progress, "! " + r.Message);
            return r;
        }
        finally
        {
            ClearPools();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // UndoLastMerge — delete every fromlb=1 row (exact revert to pristine)
    // ════════════════════════════════════════════════════════════════════════

    public static MergeResult UndoLastMerge(IProgress<string>? progress = null)
    {
        var r = new MergeResult();
        string extPath = OwnExtendedDbPath;
        if (!File.Exists(extPath))
        {
            r.Message = "LiteBox has no own Extended database copy.";
            Report(progress, "! " + r.Message);
            return r;
        }

        Report(progress, "──── Undo last merge ────");
        try
        {
            using var conn = Open(extPath, readOnly: false);
            ApplyWritePragmas(conn);
            EnsureStructure(conn, progress);

            using (var tx = conn.BeginTransaction())
            {
                r.FromLbDeleted = RevertFromLb(conn, tx, progress);
                tx.Commit();
            }

            CheckpointTruncate(conn);
            SaveState(pristineVersion: LoadState().PristineVersion, lastLbSize: LoadState().LastLbSize, merged: false);
            MetadataDb.InvalidateExtendedDbProbe();

            r.Success = true;
            r.Message = r.FromLbDeleted > 0
                ? $"Last merge undone ({r.FromLbDeleted} merge-inserted rows removed)."
                : "Nothing to undo (no merge-inserted rows present).";
            Report(progress, "✓ " + r.Message);
            return r;
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, "UndoLastMerge failed: " + ex.Message);
            r.Message = "Undo failed: " + ex.Message;
            Report(progress, "! " + r.Message);
            return r;
        }
        finally
        {
            ClearPools();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Unmerge — restore the pristine (pre-merge) snapshot; fromlb DELETE fallback
    // ════════════════════════════════════════════════════════════════════════

    public static MergeResult Unmerge(IProgress<string>? progress = null)
    {
        var r = new MergeResult();
        string extPath = OwnExtendedDbPath;
        if (!File.Exists(extPath))
        {
            r.Message = "LiteBox has no own Extended database copy.";
            Report(progress, "! " + r.Message);
            return r;
        }

        Report(progress, "──── Unmerge (restore pristine) ────");

        if (File.Exists(PristinePath))
        {
            try
            {
                Report(progress, "Restoring the pristine snapshot over the live database…");
                ClearPools();
                bool ok = RestoreFileWithRetry(PristinePath, extPath);
                if (!ok)
                {
                    r.Message = "The Extended database is in use and could not be replaced. Close readers and retry.";
                    Report(progress, "! " + r.Message);
                    return r;
                }
                SaveState(pristineVersion: LoadState().PristineVersion, lastLbSize: 0, merged: false);
                MetadataDb.InvalidateExtendedDbProbe();
                r.Success = true;
                r.Message = "Extended database restored to its pristine (pre-merge) snapshot.";
                Report(progress, "✓ " + r.Message);
                return r;
            }
            catch (Exception ex)
            {
                LbLog.Warn(Tag, "Unmerge (snapshot) failed: " + ex.Message);
                Report(progress, "! Snapshot restore failed (" + ex.Message + "), falling back to fromlb cleanup.");
                // fall through to the fromlb fallback
            }
        }
        else
        {
            Report(progress, "No pristine snapshot on disk — removing merge-inserted rows instead.");
        }

        // Fallback: equivalent to Undo — delete every fromlb=1 row.
        var undo = UndoLastMerge(progress);
        undo.Message = undo.Success
            ? "Unmerge done via fromlb cleanup — " + undo.Message
            : undo.Message;
        return undo;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Structure sync (fromlb columns + indexes)
    // ════════════════════════════════════════════════════════════════════════

    private static void EnsureStructure(SqliteConnection conn, IProgress<string>? progress)
    {
        foreach (var table in AllMergedTables)
        {
            if (!TableExists(conn, table)) continue;

            var cols = GetColumns(conn, null, "main", table);
            if (!cols.Contains("fromlb", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    ExecNonQuery(conn, null,
                        $"ALTER TABLE main.\"{table}\" ADD COLUMN \"fromlb\" INTEGER NOT NULL DEFAULT 0;");
                    Report(progress, $"Added fromlb column on {table}.");
                }
                catch (SqliteException ex) when (ex.Message.IndexOf("duplicate column", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // race / already there
                }
            }

            try
            {
                ExecNonQuery(conn, null,
                    $"CREATE INDEX IF NOT EXISTS \"idx_{table.ToLowerInvariant()}_fromlb\" ON \"{table}\" (\"fromlb\");");
            }
            catch (Exception ex) { LbLog.Warn(Tag, $"index {table}.fromlb: {ex.Message}"); }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Revert / insert phases
    // ════════════════════════════════════════════════════════════════════════

    private static int RevertFromLb(SqliteConnection conn, SqliteTransaction tx, IProgress<string>? progress)
    {
        int total = 0;
        // children -> parents (Games) -> Platforms for FK-safe ordering.
        foreach (var t in ChildTables)
            if (TableExists(conn, t)) total += ExecNonQuery(conn, tx, $"DELETE FROM main.\"{t}\" WHERE \"fromlb\" = 1;");
        if (TableExists(conn, "Games")) total += ExecNonQuery(conn, tx, "DELETE FROM main.\"Games\" WHERE \"fromlb\" = 1;");
        if (TableExists(conn, "Platforms")) total += ExecNonQuery(conn, tx, "DELETE FROM main.\"Platforms\" WHERE \"fromlb\" = 1;");
        if (total > 0) Report(progress, $"Reverted {total} previously merge-inserted rows.");
        return total;
    }

    private static int InsertMissingGames(
        SqliteConnection conn, SqliteTransaction tx, DuplicateMode dupMode, IProgress<string>? progress)
    {
        if (!TableExists(conn, "Games")) return 0;

        var mainCols = GetColumns(conn, tx, "main", "Games");
        var srcCols = GetColumns(conn, tx, SrcAlias, "Games");
        if (srcCols.Count == 0) { Report(progress, "Source has no Games table — skipped."); return 0; }

        var common = mainCols
            .Where(c => srcCols.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Where(c => !Eq(c, "fromlb") && !Eq(c, "Origin") && !Eq(c, "InternalIdLaunchbox"))
            .ToList();
        if (!common.Any(c => Eq(c, "DatabaseId")))
        {
            Report(progress, "Games has no DatabaseId column — skipped.");
            return 0;
        }

        string colList = string.Join(", ", common.Select(Q));
        string selList = string.Join(", ", common.Select(c => "s." + Q(c)));

        string extraCols = ", \"fromlb\", \"Origin\"";
        string extraSel = $", 1, '{OriginValue}'";
        if (mainCols.Contains("InternalIdLaunchbox", StringComparer.OrdinalIgnoreCase))
        {
            extraCols += ", \"InternalIdLaunchbox\"";
            extraSel += ", s.\"DatabaseId\"";
        }

        // Duplicate handling: Skip drops LB rows that collide with a stable (fromlb=0) community row
        // on (CompareName, Platform). Off and Merge both insert (no enrichment in this subset).
        string skipClause = "";
        bool canSkip = dupMode == DuplicateMode.Skip
            && mainCols.Contains("CompareName", StringComparer.OrdinalIgnoreCase)
            && mainCols.Contains("Platform", StringComparer.OrdinalIgnoreCase)
            && srcCols.Contains("CompareName", StringComparer.OrdinalIgnoreCase)
            && srcCols.Contains("Platform", StringComparer.OrdinalIgnoreCase);
        if (canSkip)
        {
            skipClause =
                " AND NOT EXISTS (SELECT 1 FROM main.\"Games\" m " +
                "                 WHERE m.\"CompareName\" = s.\"CompareName\" " +
                "                   AND m.\"Platform\" = s.\"Platform\" AND m.\"fromlb\" = 0)";
        }

        string sql =
            $"INSERT OR IGNORE INTO main.\"Games\" ({colList}{extraCols}) " +
            $"SELECT {selList}{extraSel} FROM {SrcAlias}.\"Games\" s " +
            "WHERE s.\"DatabaseId\" NOT IN (SELECT m.\"DatabaseId\" FROM main.\"Games\" m)" +
            skipClause + ";";
        int n = ExecNonQuery(conn, tx, sql);
        Report(progress, $"Games inserted: {n}.");
        return n;
    }

    private static int InsertMissingChild(
        SqliteConnection conn, SqliteTransaction tx, string table,
        string[] keyColumns, (string Name, string Fallback)? normalize,
        (string Name, string Expr)? derivedCol, IProgress<string>? progress)
    {
        if (!TableExists(conn, table)) return 0;

        var mainCols = GetColumns(conn, tx, "main", table);
        var srcCols = GetColumns(conn, tx, SrcAlias, table);
        if (srcCols.Count == 0) return 0;

        string? normName = normalize?.Name;
        string? normExpr = normalize.HasValue
            ? $"COALESCE(NULLIF(s.\"{normalize.Value.Name}\", ''), {normalize.Value.Fallback})"
            : null;
        string? derivedName = derivedCol?.Name;

        var common = mainCols
            .Where(c => srcCols.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Where(c => !Eq(c, "fromlb") && !Eq(c, "Origin"))
            .Where(c => derivedName == null || !Eq(c, derivedName))
            .ToList();

        var colParts = new List<string>(common.Select(Q));
        var selParts = new List<string>();
        foreach (var c in common)
            selParts.Add(normName != null && Eq(c, normName) ? normExpr! : "s." + Q(c));

        colParts.Add("\"fromlb\""); selParts.Add("1");
        colParts.Add("\"Origin\""); selParts.Add($"'{OriginValue}'");
        if (derivedCol.HasValue) { colParts.Add(Q(derivedCol.Value.Name)); selParts.Add(derivedCol.Value.Expr); }

        string keyJoin = string.Join(" AND ", keyColumns.Select(k =>
            normName != null && Eq(k, normName) ? $"m.{Q(k)} = {normExpr}" : $"m.{Q(k)} = s.{Q(k)}"));

        string sql =
            $"INSERT OR IGNORE INTO main.\"{table}\" ({string.Join(", ", colParts)}) " +
            $"SELECT {string.Join(", ", selParts)} FROM {SrcAlias}.\"{table}\" s " +
            "WHERE EXISTS (SELECT 1 FROM main.\"Games\" g WHERE g.\"DatabaseId\" = s.\"DatabaseId\") " +
            $"  AND NOT EXISTS (SELECT 1 FROM main.\"{table}\" m WHERE {keyJoin});";
        int n = ExecNonQuery(conn, tx, sql);
        Report(progress, $"{table} inserted: {n}.");
        return n;
    }

    private static int InsertMissingPlatforms(SqliteConnection conn, SqliteTransaction tx, IProgress<string>? progress)
    {
        if (!TableExists(conn, "Platforms")) return 0;

        var mainCols = GetColumns(conn, tx, "main", "Platforms");
        var srcCols = GetColumns(conn, tx, SrcAlias, "Platforms");
        if (srcCols.Count == 0 || !mainCols.Any(c => Eq(c, "Name")) || !srcCols.Any(c => Eq(c, "Name")))
            return 0;

        var common = mainCols
            .Where(c => srcCols.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Where(c => !Eq(c, "fromlb"))
            .ToList();

        string colList = string.Join(", ", common.Select(Q));
        string selList = string.Join(", ", common.Select(c => "s." + Q(c)));
        string sql =
            $"INSERT INTO main.\"Platforms\" ({colList}, \"fromlb\") " +
            $"SELECT {selList}, 1 FROM {SrcAlias}.\"Platforms\" s " +
            "WHERE NOT EXISTS (SELECT 1 FROM main.\"Platforms\" m WHERE m.\"Name\" = s.\"Name\");";
        int n = ExecNonQuery(conn, tx, sql);
        Report(progress, $"Platforms inserted: {n}.");
        return n;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Pristine snapshot + state
    // ════════════════════════════════════════════════════════════════════════

    private static void EnsurePristineSnapshot(SqliteConnection conn, string extPath, IProgress<string>? progress)
    {
        long liveVersion = ReadVersion(conn);
        var state = LoadState();

        bool need = !File.Exists(PristinePath) || state.PristineVersion != liveVersion;
        if (!need) return;

        try
        {
            Report(progress, "Capturing pristine snapshot (backup before first merge)…");
            Directory.CreateDirectory(MergeDir);
            if (File.Exists(PristinePath)) { try { File.Delete(PristinePath); } catch { } }

            // VACUUM INTO produces a standalone, sidecar-free copy usable as the Unmerge restore point.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "VACUUM main INTO @p;";
                cmd.Parameters.AddWithValue("@p", PristinePath);
                cmd.ExecuteNonQuery();
            }
            SaveState(pristineVersion: liveVersion, lastLbSize: state.LastLbSize, merged: state.Merged);
            Report(progress, $"Pristine snapshot captured (version {liveVersion}).");
        }
        catch (Exception ex)
        {
            // Non-fatal: the transaction still protects the merge; only Unmerge-from-snapshot is lost.
            LbLog.Warn(Tag, "pristine snapshot failed: " + ex.Message);
            Report(progress, "! Could not capture pristine snapshot (" + ex.Message + "); Undo still works.");
        }
    }

    private sealed class MergeState
    {
        public long PristineVersion { get; set; }
        public long LastLbSize { get; set; }
        public bool Merged { get; set; }
        public string LastMergeUtc { get; set; } = "";
    }

    private static MergeState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<MergeState>(File.ReadAllText(StatePath)) ?? new MergeState();
        }
        catch { }
        return new MergeState();
    }

    private static void SaveState(long pristineVersion, long lastLbSize, bool merged)
    {
        try
        {
            Directory.CreateDirectory(MergeDir);
            var st = new MergeState
            {
                PristineVersion = pristineVersion,
                LastLbSize = lastLbSize,
                Merged = merged,
                LastMergeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { LbLog.Warn(Tag, "save state: " + ex.Message); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // SQLite helpers
    // ════════════════════════════════════════════════════════════════════════

    private static SqliteConnection Open(string path, bool readOnly)
    {
        try { SQLitePCL.Batteries_V2.Init(); } catch { }
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false, // no lingering handle to block a later file swap
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    private static void ApplyWritePragmas(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 60000; PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { LbLog.Warn(Tag, "write pragmas: " + ex.Message); }
    }

    private static void CheckpointTruncate(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private static void Attach(SqliteConnection conn, string lbPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE @p AS {SrcAlias};";
        cmd.Parameters.AddWithValue("@p", lbPath);
        cmd.ExecuteNonQuery();
    }

    private static void Detach(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DETACH DATABASE {SrcAlias};";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { LbLog.Warn(Tag, "detach: " + ex.Message); }
    }

    private static int CountFromLb(SqliteConnection conn, SqliteTransaction? tx)
    {
        int total = 0;
        foreach (var t in AllMergedTables)
        {
            if (!TableExists(conn, t)) continue;
            var cols = GetColumns(conn, tx, "main", t);
            if (!cols.Contains("fromlb", StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                using var cmd = conn.CreateCommand();
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandText = $"SELECT COUNT(*) FROM main.\"{t}\" WHERE \"fromlb\" = 1;";
                var v = cmd.ExecuteScalar();
                total += (v == null || v == DBNull.Value) ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch { }
        }
        return total;
    }

    private static long ReadVersion(SqliteConnection conn)
    {
        try
        {
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='__version' LIMIT 1;";
                if (check.ExecuteScalar() == null) return 0;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM \"__version\" ORDER BY version DESC LIMIT 1;";
            var v = cmd.ExecuteScalar();
            return v is long l ? l : (v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture));
        }
        catch { return 0; }
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1;";
            cmd.Parameters.AddWithValue("@n", table);
            var v = cmd.ExecuteScalar();
            return v != null && v != DBNull.Value;
        }
        catch { return false; }
    }

    private static List<string> GetColumns(SqliteConnection conn, SqliteTransaction? tx, string schema, string table)
    {
        var cols = new List<string>();
        try
        {
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = "SELECT name FROM pragma_table_info(@t, @s);";
            cmd.Parameters.AddWithValue("@t", table);
            cmd.Parameters.AddWithValue("@s", schema);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                if (!rdr.IsDBNull(0)) cols.Add(rdr.GetString(0));
        }
        catch (Exception ex) { LbLog.Warn(Tag, $"columns {schema}.{table}: {ex.Message}"); }
        return cols;
    }

    private static int ExecNonQuery(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static bool RestoreFileWithRetry(string src, string dst)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                // WAL sidecars of the live file must go, or a stale -wal would resurrect merged rows.
                foreach (var side in new[] { dst + "-wal", dst + "-shm" })
                    try { if (File.Exists(side)) File.Delete(side); } catch { }
                File.Copy(src, dst, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500);
                ClearPools();
            }
        }
        return false;
    }

    private static void ClearPools()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        catch { }
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Q(string col) => "\"" + col + "\"";

    private static void Report(IProgress<string>? progress, string message)
    {
        LbLog.Info(Tag, message);
        progress?.Report(message);
    }
}
