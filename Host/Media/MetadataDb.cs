// Read-only access to LaunchBox's offline games database (<LB>\Metadata\LaunchBox.Metadata.db) — present on
// ANY LaunchBox install, so this works standalone. The GameImages table lists every image the online DB has
// for a game (by DatabaseId): its CDN FileName, Type, Region and CRC32. On a plain LaunchBox that's all five
// columns there are. When ExtendDB's Extended Database module is active it MERGES enriched rows into the very
// same table (LbDbMerger), adding Origin / Duplicate / FileType and non-LaunchBox sources (screenscraper,
// steam, vndb…). We read those columns defensively when present so the download can route each image through
// ExtendDB's per-origin fetcher; on a base install they default to launchbox / 0.
//
// The launchbox CDN URL is https://images.launchbox-app.com/{FileName} — valid for Origin='launchbox' only.
// For other origins the FileName is a raw source token and the real URL is built by ExtendDB's MediaApi, so
// downloads/previews of those must go through MediaApiBridge, never the CDN.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Media;

internal static class MetadataDb
{
    public const string ImageCdnBase = "https://images.launchbox-app.com/";

    public readonly struct WebImage
    {
        public readonly int DatabaseId, Duplicate;
        public readonly string FileName, Type, Region, Origin, FileType;
        public readonly long Crc32;
        /// <summary>Byte size from the EXTENDED DB (base LaunchBox has no such column → 0). ExtendDB's second
        /// dedup key after CRC — and the only usable one for videos, whose CRC is never recomputed.</summary>
        public readonly long FileSize;
        public WebImage(int db, string fn, string ty, string rg, long crc, string origin, int dup, string ft, long fs = 0)
        {
            DatabaseId = db; FileName = fn ?? ""; Type = ty ?? ""; Region = rg ?? ""; Crc32 = crc;
            Origin = string.IsNullOrEmpty(origin) ? "launchbox" : origin; Duplicate = dup; FileType = ft ?? "";
            FileSize = fs;
        }
        /// <summary>Launchbox CDN URL — only correct when <see cref="Origin"/> is "launchbox".</summary>
        public string Url => ImageCdnBase + FileName.Replace("\\", "/");
        /// <summary>Stable per-row identity for selection / lookup (FileName alone isn't unique across origins).</summary>
        public string Key => $"{Origin}|{Type}|{Region}|{Duplicate}|{FileName}";
        public bool IsLaunchbox => string.Equals(Origin, "launchbox", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DbPath()
    {
        var root = MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(root)) return null;
        var p = Path.Combine(root, "Metadata", "LaunchBox.Metadata.db");
        return File.Exists(p) ? p : null;
    }

    /// <summary>True when the offline metadata DB is on disk (so web images can be listed at all).</summary>
    public static bool Available => DbPath() != null;

    /// <summary>
    /// The DEFAULT db of the int-keyed readers below (used by the web server's covers etc. — the editor download
    /// grids now name their DB explicitly: <see cref="LaunchBoxDbPath"/> for the orange "LaunchBox DB" source,
    /// <see cref="ExtendedDbPath"/> for the purple "ExtendDB" one). The rule: base LaunchBox's own Metadata.db,
    /// EXCEPT when all three hold — the ExtendDB plugin is loaded, its Extended Database module is Active, and
    /// the extended DB has been downloaded (= <see cref="MediaApiBridge.UseWizardPath"/> + the file present) —
    /// in which case the richer merged DB is used. So we never open ExtendDB's 3.8 GB asset unless ExtendDB is
    /// genuinely in play (and its non-launchbox rows are actually fetchable).
    /// </summary>
    public static string? WebDbPath()
        => (MediaApiBridge.UseWizardPath && ExtendedDbPath != null && UseExtendedAsMain) ? ExtendedDbPath : DbPath();

    /// <summary>[Base] UseAsMainDb (default true): with the Base module on and the extended DB present, it is
    /// the MAIN metadata DB. Unchecked → the legacy LaunchBox Metadata.db stays primary (the extended DB is
    /// still offered as an explicit extra source where surfaces expose it, e.g. the editor download grids).</summary>
    public static bool UseExtendedAsMain
    {
        get { try { return LiteBoxConfig.LoadForExe().GetSecBool("Base", "UseAsMainDb", true); } catch { return true; } }
    }

    /// <summary>LaunchBox's own Metadata.db (never the extended one), or null when absent — the explicit
    /// "LaunchBox DB" source of the editor download grids.</summary>
    public static string? LaunchBoxDbPath() => DbPath();

    /// <summary>Every image the online/merged DB has for a game (by its DatabaseId), or empty. Read-only.</summary>
    public static List<WebImage> ImagesForGame(int databaseId) => ImagesForGame(WebDbPath(), databaseId);

    // ── Videos ────────────────────────────────────────────────────────────────
    // Video rows ONLY exist in the EXTENDED database (LaunchBox's own Metadata.db has none — the LbDbMerger only
    // pushes image types into it). So the video "ExtendDB" source is meaningful only when ExtendDB is in play —
    // which is why the videos editor reads ExtendedDbPath explicitly and gates the source on the Base module +
    // the DB being present. They live in GameImages under Type 'Video' (146k:
    // screenscraper / steam / emumovies) and 'VideoAdvert'. CRC32 AND FileSize are always populated there —
    // which matters, because a video's CRC is never recomputed from disk (see the owned-detection in the page).

    private static string? _extDb;
    private static bool _extProbed;

    /// <summary>Forget the cached extended-DB probe — call after installing/refreshing the DB so the new file
    /// is visible without a restart.</summary>
    public static void InvalidateExtendedDbProbe() { _extProbed = false; _extDb = null; }

    /// <summary>The enriched extended DB (LaunchBox.Extended.Metadata.db), or null when it isn't on disk.
    /// LiteBox-own copy under Core\litebox\ first (where the ported downloader will put it); falls back to
    /// the legacy plugin location so an existing install keeps working without re-downloading ~4 GB.</summary>
    public static string? ExtendedDbPath
    {
        get
        {
            if (_extProbed) return _extDb;
            _extProbed = true;
            try
            {
                var own = LiteBoxPaths.File("LaunchBox.Extended.Metadata.db");
                if (File.Exists(own)) { _extDb = own; return _extDb; }
            }
            catch { }
            try
            {
                var root = MediaResolver.LbRoot;
                if (!string.IsNullOrEmpty(root))
                {
                    var p = Path.Combine(root, "Plugins", "ExtendDB", "LaunchBox.Extended.Metadata.db");
                    if (File.Exists(p)) _extDb = p;
                }
            }
            catch { }
            return _extDb;
        }
    }

    /// <summary>Every video the extended DB has for a game, or empty when the DB isn't there.</summary>
    public static List<WebImage> VideosForGame(int databaseId) => VideosForGame(WebDbPath(), databaseId);

    /// <summary>Path-parameterized reader (also the unit-test seam).</summary>
    internal static List<WebImage> VideosForGame(string? db, int databaseId)
    {
        var list = new List<WebImage>();
        if (db == null || databaseId <= 0) return list;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();

            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pc = con.CreateCommand())
            {
                pc.CommandText = "PRAGMA table_info(\"GameImages\")";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) cols.Add(pr.GetString(1));
            }
            if (!cols.Contains("FileName") || !cols.Contains("Type")) return list;
            string Col(string name, string literal) => cols.Contains(name) ? "\"" + name + "\"" : literal;

            using var cmd = con.CreateCommand();
            cmd.CommandText =
                $"SELECT \"FileName\", \"Type\", {Col("Region", "''")}, {Col("CRC32", "0")}, " +
                $"{Col("Origin", "'launchbox'")}, {Col("duplicate", "0")}, {Col("FileSize", "0")} " +
                "FROM \"GameImages\" WHERE \"DatabaseId\" = $id AND \"Type\" IN ('Video','VideoAdvert')";
            cmd.Parameters.Add(new SqliteParameter("$id", databaseId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string fn = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(fn)) continue;
                list.Add(new WebImage(
                    databaseId, fn,
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    r.IsDBNull(4) ? "launchbox" : r.GetString(4),
                    r.IsDBNull(5) ? 0 : (int)r.GetInt64(5),
                    ImageFileType.Extract(fn),          // the extended DB has no FileType column — derive it
                    r.IsDBNull(6) ? 0 : r.GetInt64(6)));
            }
        }
        catch { }
        return list;
    }

    // ── Documents (manuals / maps / press kits) ─────────────────────────────────
    // Document rows live in the SAME GameImages table under Type 'Manual' / 'Map' / 'Press'
    // (screenscraper / emumovies; no launchbox). Same DB rule as every other tab (WebDbPath): extended only
    // when ExtendDB is genuinely in play, else base LaunchBox. Download reuses the image path (a document row
    // is just a WebImage) — and since there are no launchbox rows, without ExtendDB's credentialed fetcher
    // none are downloadable (the editor notes that). The row's Type distinguishes a manual (joins the manual
    // collection) from a map / press kit (additional documents only).
    public static List<WebImage> ManualsForGame(int databaseId)
        => DocumentsForGame(WebDbPath(), databaseId).Where(w => string.Equals(w.Type, "Manual", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Music rows (Type 'Music') — same table, same download path, same ExtendDB-fetcher caveat.</summary>
    internal static List<WebImage> MusicForGame(string? db, int databaseId)
        => RowsOfTypes(db, databaseId, "'Music'");

    internal static List<WebImage> DocumentsForGame(string? db, int databaseId)
        => RowsOfTypes(db, databaseId, "'Manual','Map','Press'");

    private static List<WebImage> RowsOfTypes(string? db, int databaseId, string typesSql)
    {
        var list = new List<WebImage>();
        if (db == null || databaseId <= 0) return list;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();

            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pc = con.CreateCommand())
            {
                pc.CommandText = "PRAGMA table_info(\"GameImages\")";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) cols.Add(pr.GetString(1));
            }
            if (!cols.Contains("FileName") || !cols.Contains("Type")) return list;
            string Col(string name, string literal) => cols.Contains(name) ? "\"" + name + "\"" : literal;

            using var cmd = con.CreateCommand();
            cmd.CommandText =
                $"SELECT \"FileName\", \"Type\", {Col("Region", "''")}, {Col("CRC32", "0")}, " +
                $"{Col("Origin", "'launchbox'")}, {Col("duplicate", "0")}, {Col("FileSize", "0")} " +
                $"FROM \"GameImages\" WHERE \"DatabaseId\" = $id AND \"Type\" IN ({typesSql})";
            cmd.Parameters.Add(new SqliteParameter("$id", databaseId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string fn = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(fn)) continue;
                list.Add(new WebImage(
                    databaseId, fn,
                    r.IsDBNull(1) ? "Manual" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    r.IsDBNull(4) ? "launchbox" : r.GetString(4),
                    r.IsDBNull(5) ? 0 : (int)r.GetInt64(5),
                    ImageFileType.Extract(fn),
                    r.IsDBNull(6) ? 0 : r.GetInt64(6)));
            }
        }
        catch { }
        return list;
    }

    // ── Steam appid ───────────────────────────────────────────────────────────
    // The Games table carries a SteamAppId column (present on BOTH base LaunchBox's Metadata.db and the merged
    // Extended DB), keyed by DatabaseId. That's how a game that ISN'T launched via a steam:// URI — a plain
    // "Windows" import that merely matches a Steam title in the DB — still resolves to a Steam appid. We prefer
    // the extended DB (richer / user-curated) then fall back to base LaunchBox.

    private static readonly Dictionary<int, string?> _steamAppIdMemo = new();
    private static readonly object _steamMemoLock = new();

    /// <summary>The Steam appid LaunchBox's metadata associates with a game by its DatabaseId (extended DB
    /// preferred, else base), or null when there is none. Memoized.</summary>
    // ── Description by DatabaseID ───────────────────────────────────────────────────────────────────
    // The game's own Notes are Tier-2 and a game launch frees them, so a detail payload built while a game
    // runs has no description at all. The metadata DB has one, keyed by DatabaseID, and it is on disk — no
    // resident memory to give back.
    //
    // It is an APPROXIMATION and knowingly so: a description edited or written by hand locally lives in the
    // Notes, not here, so the two can differ. That is acceptable only because a response built in this state
    // is marked degraded and no cache keeps it — see EmbeddedWebServer.MarkIfDegraded.
    //
    // Which DB, and in which order, follows the rule the rest of the app already uses: the extended one when
    // it is the main DB (Base module on, downloaded, UseAsMainDb), else LaunchBox's own. On the extended DB
    // the column is not "Overview" but whatever OverviewCache resolves — the precomputed defaultOverview when
    // valid, else the COALESCE over the user's ordered sources and languages. Falling back to LaunchBox's DB
    // covers the case the user asked about: a DatabaseID that is set but has no row in the extended copy.
    private static readonly object _overviewMemoLock = new();
    private static readonly Dictionary<int, string?> _overviewMemo = new();

    public static string? OverviewForGame(int databaseId)
    {
        if (databaseId <= 0) return null;
        lock (_overviewMemoLock) { if (_overviewMemo.TryGetValue(databaseId, out var m)) return m; }

        string? main = WebDbPath();
        string? text = OverviewFrom(main, databaseId);
        if (string.IsNullOrWhiteSpace(text) && !string.Equals(main, DbPath(), StringComparison.OrdinalIgnoreCase))
            text = OverviewFrom(DbPath(), databaseId);

        lock (_overviewMemoLock) _overviewMemo[databaseId] = text;
        return text;
    }

    /// <summary>Path-parameterized reader (also the unit-test seam). On the extended DB the expression comes
    /// from OverviewCache so the source/language priority is the one the user configured; anywhere else it is
    /// the plain Overview column.</summary>
    internal static string? OverviewFrom(string? db, int databaseId)
    {
        if (db == null || databaseId <= 0) return null;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();

            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pc = con.CreateCommand())
            {
                pc.CommandText = "PRAGMA table_info(\"Games\")";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) cols.Add(pr.GetString(1));
            }
            if (!cols.Contains("DatabaseID")) return null;

            bool extended = string.Equals(db, ExtendedDbPath, StringComparison.OrdinalIgnoreCase);
            string expr = extended ? Data.OverviewCache.ReadExpression(db) : "\"Overview\"";
            if (!extended && !cols.Contains("Overview")) return null;

            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT " + expr + " FROM \"Games\" WHERE \"DatabaseID\" = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", databaseId);
            var v = cmd.ExecuteScalar();
            var t = v == null || v is DBNull ? null : Convert.ToString(v);
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    public static string? SteamAppIdForGame(int databaseId)
    {
        if (databaseId <= 0) return null;
        lock (_steamMemoLock) { if (_steamAppIdMemo.TryGetValue(databaseId, out var m)) return m; }
        string? appid = SteamAppIdFrom(ExtendedDbPath, databaseId) ?? SteamAppIdFrom(DbPath(), databaseId);
        lock (_steamMemoLock) _steamAppIdMemo[databaseId] = appid;
        return appid;
    }

    /// <summary>Path-parameterized reader (also the unit-test seam): reads Games.SteamAppId from an explicit DB.</summary>
    internal static string? SteamAppIdFrom(string? db, int databaseId)
    {
        if (db == null || databaseId <= 0) return null;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();

            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pc = con.CreateCommand())
            {
                pc.CommandText = "PRAGMA table_info(\"Games\")";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) cols.Add(pr.GetString(1));
            }
            if (!cols.Contains("SteamAppId")) return null;
            string idCol = cols.Contains("DatabaseID") ? "DatabaseID" : (cols.Contains("DatabaseId") ? "DatabaseId" : "DatabaseID");

            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT \"SteamAppId\" FROM \"Games\" WHERE \"{idCol}\" = $id LIMIT 1";
            cmd.Parameters.Add(new SqliteParameter("$id", databaseId));
            var val = cmd.ExecuteScalar();
            if (val == null || val is DBNull) return null;
            string s = (Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? "").Trim();
            if (s.Length == 0 || s == "0" || !s.All(char.IsDigit)) return null;   // stored as int; 0 == "no appid"
            return s;
        }
        catch { }
        return null;
    }

    /// <summary>Path-parameterized reader (also the unit-test seam): reads GameImages from an explicit DB file.</summary>
    internal static List<WebImage> ImagesForGame(string? db, int databaseId)
    {
        var list = new List<WebImage>();
        if (db == null || databaseId <= 0) return list;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();

            // Discover which columns this DB actually has — base LB has 5, an Extended-merged one has more.
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pc = con.CreateCommand())
            {
                pc.CommandText = "PRAGMA table_info(\"GameImages\")";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) cols.Add(pr.GetString(1));
            }
            if (!cols.Contains("FileName") || !cols.Contains("Type")) return list;

            string idCol = cols.Contains("DatabaseId") ? "DatabaseId" : (cols.Contains("DatabaseID") ? "DatabaseID" : "DatabaseId");
            string Col(string name, string literal) => cols.Contains(name) ? "\"" + name + "\"" : literal;
            string sql =
                $"SELECT \"FileName\", \"Type\", {Col("Region", "''")}, {Col("CRC32", "0")}, " +
                $"{Col("Origin", "'launchbox'")}, {Col("Duplicate", "0")}, {Col("FileType", "''")} " +
                $"FROM \"GameImages\" WHERE \"{idCol}\" = $id";

            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqliteParameter("$id", databaseId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string fn = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(fn)) continue;
                string ty = r.IsDBNull(1) ? "" : r.GetString(1);
                string rg = r.IsDBNull(2) ? "" : r.GetString(2);
                long crc = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                string origin = r.IsDBNull(4) ? "launchbox" : r.GetString(4);
                int dup = r.IsDBNull(5) ? 0 : (int)r.GetInt64(5);
                string ft = r.IsDBNull(6) ? "" : r.GetString(6);
                list.Add(new WebImage(databaseId, fn, ty, rg, crc, origin, dup, ft));
            }
        }
        catch { }
        return list;
    }
}
