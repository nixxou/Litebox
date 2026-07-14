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

    /// <summary>Every image the online/merged DB has for a game (by its DatabaseId), or empty. Read-only.</summary>
    public static List<WebImage> ImagesForGame(int databaseId) => ImagesForGame(DbPath(), databaseId);

    // ── Videos ────────────────────────────────────────────────────────────────
    // Videos are read from the EXTENDED database, not from LaunchBox's own Metadata.db — the same source
    // ExtendDB's own video downloader queries (ExtendMediaDownloader.QueryCandidates reads ExtendedDbPath
    // directly). LaunchBox's DB has no video rows at all, and the LbDbMerger only pushes image types into it.
    // They live in the same GameImages table, under Type 'Video' (146k rows: screenscraper / steam / emumovies)
    // and 'VideoAdvert' (emumovies). CRC32 AND FileSize are always populated there — which matters, because a
    // video's CRC is never recomputed from disk (see the owned-detection in the video page).

    private static string? _extDb;
    private static bool _extProbed;

    /// <summary>ExtendDB's enriched DB (LaunchBox.Extended.Metadata.db), or null when it isn't on disk. Path
    /// comes from ExtendDBPlugin.ExtendedDbPath when the plugin is loaded; else the conventional location.</summary>
    public static string? ExtendedDbPath
    {
        get
        {
            if (_extProbed) return _extDb;
            _extProbed = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ExtendDB");
                var f = asm?.GetType("ExtendDB.ExtendDBPlugin")?.GetField("ExtendedDbPath",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (f?.GetValue(null) is string p && File.Exists(p)) { _extDb = p; return _extDb; }
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
    public static List<WebImage> VideosForGame(int databaseId) => VideosForGame(ExtendedDbPath, databaseId);

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

    // ── Steam appid ───────────────────────────────────────────────────────────
    // The Games table carries a SteamAppId column (present on BOTH base LaunchBox's Metadata.db and the merged
    // Extended DB), keyed by DatabaseId. That's how a game that ISN'T launched via a steam:// URI — a plain
    // "Windows" import that merely matches a Steam title in the DB — still resolves to a Steam appid. We prefer
    // the extended DB (richer / user-curated) then fall back to base LaunchBox.

    private static readonly Dictionary<int, string?> _steamAppIdMemo = new();
    private static readonly object _steamMemoLock = new();

    /// <summary>The Steam appid LaunchBox's metadata associates with a game by its DatabaseId (extended DB
    /// preferred, else base), or null when there is none. Memoized.</summary>
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
