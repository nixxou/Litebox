// Read-only data-access layer for the database site. Clean-room LiteBox rewrite of ExtendDB's
// Web/Backend/DbRepository.cs, reduced to the Extended-DB-only path:
//
//   • The DB path comes from MetadataDb.ExtendedDbPath (LiteBox's own resolver). There is NO native-DB
//     fallback and NO SqliteConnectionPatches.BypassRedirect dance — LiteBox has no read-intercept, so we
//     open the Extended DB directly (Microsoft.Data.Sqlite, read-only). WAL is a property of the file; a
//     read-only connection reads a WAL DB fine. Pooling is off so a mid-session DB swap isn't pinned to a
//     stale handle.
//   • Every read first checks AnyDbReady(); when the Extended DB isn't installed the handlers degrade to an
//     empty result / a "not installed" page rather than throwing "no such table: Platforms".
//
// No plugin, no reflection, no Harmony. Each method opens and disposes its own connection.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LbApiHost.Host.Media;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Web;

internal sealed class DbRepository
{
    static DbRepository()
    {
        try { SQLitePCL.Batteries.Init(); } catch { /* idempotent, may already be initialised by the host */ }
    }

    // ── Connection ─────────────────────────────────────────────────────────────

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        }.ToString();

    private static SqliteConnection Open()
    {
        var path = MetadataDb.ExtendedDbPath
                   ?? throw new InvalidOperationException("Extended DB not installed.");
        var con = new SqliteConnection(ConnectionString(path));
        con.Open();
        ApplyPragmas(con);
        return con;
    }

    private static void ApplyPragmas(SqliteConnection con)
    {
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "PRAGMA cache_size=-65536;" +   // 64 MiB page cache
                "PRAGMA mmap_size=268435456;" + // 256 MiB memory-mapped reads
                "PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();
        }
        catch { /* tuning is best-effort */ }
    }

    // ── Availability guard ─────────────────────────────────────────────────────
    // True only when the Extended DB exists AND carries the core "Platforms" table. Throttled (≤ one FS check
    // per 3 s) and re-probed only when the file timestamp changes, so it stays cheap on hot paths.

    private static readonly ConcurrentDictionary<string, (long checkedTick, long stamp, bool ok)> _readyCache = new();
    private const long ReadyThrottleMs = 3000;

    private static bool DbReady(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            long now = Environment.TickCount64;
            if (_readyCache.TryGetValue(path, out var fresh) && (now - fresh.checkedTick) < ReadyThrottleMs)
                return fresh.ok;

            if (!File.Exists(path)) { _readyCache[path] = (now, 0L, false); return false; }
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            if (_readyCache.TryGetValue(path, out var c) && c.stamp == stamp)
            {
                _readyCache[path] = (now, stamp, c.ok);
                return c.ok;
            }

            bool ok = false;
            try
            {
                using var con = new SqliteConnection(ConnectionString(path));
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='Platforms' LIMIT 1";
                ok = cmd.ExecuteScalar() != null;
            }
            catch { ok = false; }

            _readyCache[path] = (now, stamp, ok);
            return ok;
        }
        catch { return false; }
    }

    /// <summary>The Extended DB is on disk and carries the core schema.</summary>
    internal static bool ExtendedDbReady() => DbReady(MetadataDb.ExtendedDbPath);

    /// <summary>Any browsable DB — false when the Extended DB isn't installed (handlers degrade gracefully).</summary>
    internal static bool AnyDbReady() => ExtendedDbReady();

    // ── Platforms ──────────────────────────────────────────────────────────────

    public List<DbPlatform> GetAllPlatforms()
    {
        const string sql = """
            SELECT p.*, COUNT(g.DatabaseID) AS GameCount
            FROM   Platforms p
            LEFT JOIN Games g ON g.Platform = p.Name
            GROUP  BY p.PlatformKey
            ORDER  BY p.Name
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var rdr = cmd.ExecuteReader();
        var list = new List<DbPlatform>();
        while (rdr.Read()) list.Add(MapPlatform(rdr));
        return list;
    }

    // ── Games ──────────────────────────────────────────────────────────────────
    // LB's plugin rating ("PEGI 18", "Adults Only 18+", …) lives in the historical ESRB column; the parental
    // rules match against that same string, so we pass ESRB straight through.

    public DbGame GetGameById(int id)
    {
        const string sql = """
            SELECT DatabaseID, Name, ReleaseDate, ReleaseYear, Overview,
                   MaxPlayers, ReleaseType, Cooperative, VideoURL,
                   CommunityRating, CommunityRatingCount, WikipediaURL,
                   Platform, ESRB, Genres, Developer, Publisher,
                   OverviewAiFr, OverviewAiEn, OverviewAiDe, OverviewAiEs, OverviewAiIt, OverviewAiPt,
                   OverviewSteamFr, OverviewScFr, OverviewSteamEn, OverviewScEn,
                   SteamId, SteamAppId, VNDBID, ScreenscraperId, IgdbSlug, Origin
            FROM   Games
            WHERE  DatabaseID = $id
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? MapGame(rdr) : null;
    }

    // ── Paged platform games (sort + filter) ───────────────────────────────────

    public sealed class GameListOptions
    {
        public string Platform { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public string Sort { get; set; } = "alpha";  // alpha | year_asc | year_desc | rating | tier | stars
        public string Genre { get; set; }
        public string Search { get; set; }
        public string Developer { get; set; }
        public string Publisher { get; set; }
        public int? MinYear { get; set; }
        public int? MaxYear { get; set; }
        public double? MinRating { get; set; }
        public int? MinVotes { get; set; }
        public int? MinPlayers { get; set; }
        public bool? Coop { get; set; }
        public string ReleaseType { get; set; }
        public string Origin { get; set; }
        public int Adult { get; set; } = 1;  // 0=hide (AO), 1=blur, 2=show
        public bool OwnedOnly { get; set; }
        public int StarThreshold { get; set; }
    }

    public sealed class GameListResult
    {
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<DbGameSummary> Items { get; set; } = new();
    }

    public GameListResult QueryGames(GameListOptions opt)
    {
        if (string.IsNullOrEmpty(opt.Platform))
            throw new ArgumentException("Platform required.");

        var where = new List<string> { "g.Platform = $platform" };
        var prms = new Dictionary<string, object> { { "$platform", opt.Platform } };

        if (!string.IsNullOrEmpty(opt.Genre)) { where.Add("g.Genres LIKE $genre"); prms["$genre"] = "%" + opt.Genre + "%"; }
        if (!string.IsNullOrEmpty(opt.Developer)) { where.Add("g.Developer LIKE $dev"); prms["$dev"] = "%" + opt.Developer + "%"; }
        if (!string.IsNullOrEmpty(opt.Publisher)) { where.Add("g.Publisher LIKE $pub"); prms["$pub"] = "%" + opt.Publisher + "%"; }
        if (opt.MinYear.HasValue) { where.Add("g.ReleaseYear >= $minY"); prms["$minY"] = opt.MinYear.Value; }
        if (opt.MaxYear.HasValue) { where.Add("g.ReleaseYear <= $maxY"); prms["$maxY"] = opt.MaxYear.Value; }
        if (opt.MinRating.HasValue) { where.Add("g.CommunityRating >= $minR"); prms["$minR"] = opt.MinRating.Value; }
        if (opt.MinVotes.HasValue) { where.Add("g.CommunityRatingCount >= $minV"); prms["$minV"] = opt.MinVotes.Value; }
        if (opt.MinPlayers.HasValue) { where.Add("g.MaxPlayers >= $minP"); prms["$minP"] = opt.MinPlayers.Value; }
        if (opt.Coop.HasValue)
            where.Add(opt.Coop.Value ? "g.Cooperative = 1" : "(g.Cooperative IS NULL OR g.Cooperative = 0)");
        if (!string.IsNullOrEmpty(opt.ReleaseType)) { where.Add("g.ReleaseType = $rt"); prms["$rt"] = opt.ReleaseType; }
        if (!string.IsNullOrEmpty(opt.Origin)) { where.Add("g.Origin = $origin"); prms["$origin"] = opt.Origin; }
        if (opt.Adult == 0) where.Add("(g.ESRB IS NULL OR g.ESRB NOT LIKE 'AO%')");
        if (opt.OwnedOnly)
        {
            // Bind the owned DatabaseIDs as a single JSON array; SQLite expands it row-side via json_each.
            where.Add("g.DatabaseID IN (SELECT value FROM json_each($owned_json))");
            prms["$owned_json"] = OwnedLookup.GetJsonArrayForPlatform(opt.Platform);
        }
        if (!string.IsNullOrEmpty(opt.Search))
        {
            where.Add("(g.Name LIKE $q OR g.DatabaseID IN (SELECT DatabaseID FROM GameAlternateTitles WHERE AlternateName LIKE $q))");
            prms["$q"] = "%" + opt.Search + "%";
        }

        string orderBy = opt.Sort switch
        {
            "year_asc" => "ORDER BY COALESCE(g.ReleaseYear, 9999) ASC, g.Name ASC",
            "year_desc" => "ORDER BY COALESCE(g.ReleaseYear, 0) DESC, g.Name ASC",
            "rating" => "ORDER BY g.CommunityRating DESC NULLS LAST, g.Name ASC",
            "tier" => "ORDER BY (g.CommunityRating * g.CommunityRatingCount) DESC NULLS LAST, g.Name ASC",
            "stars" => $"ORDER BY (CASE WHEN g.CommunityRating >= 4.5 AND g.CommunityRatingCount >= {opt.StarThreshold} THEN 3 WHEN g.CommunityRating >= 4.0 AND g.CommunityRatingCount >= {opt.StarThreshold} THEN 2 WHEN g.CommunityRating >= 3.7 AND g.CommunityRatingCount >= {opt.StarThreshold} THEN 1 ELSE 0 END) DESC, g.Name ASC",
            _ => "ORDER BY g.Name ASC",
        };

        int page = Math.Max(1, opt.Page);
        int pageSize = Math.Clamp(opt.PageSize, 1, 500);
        int offset = (page - 1) * pageSize;
        var whereClause = "WHERE " + string.Join(" AND ", where);

        int total;
        using (var con = Open())
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM Games g {whereClause}";
            foreach (var (k, v) in prms) cmd.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        var items = new List<DbGameSummary>();
        using (var con = Open())
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT g.DatabaseID, g.Name, g.Platform, g.ReleaseYear, g.Genres, g.ESRB,
                       g.CompareName, g.ReleaseDate,
                       g.CommunityRating, g.CommunityRatingCount,
                       g.MaxPlayers, g.Cooperative, g.Origin, g.ReleaseType,
                       g.Developer, g.Publisher
                FROM   Games g
                {whereClause}
                {orderBy}
                LIMIT $limit OFFSET $offset
                """;
            foreach (var (k, v) in prms) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) items.Add(MapGameSummary(rdr));
        }

        if (items.Count > 0)
        {
            var covers = GetCoversForGames(items.Select(g => g.DatabaseID).ToList());
            foreach (var g in items)
                if (covers.TryGetValue(g.DatabaseID, out var c))
                {
                    g.CoverFileName = c.FileName;
                    g.CoverCrc32 = c.Crc32;
                    g.CoverNeedsBlur = c.NeedsBlur;
                }
        }

        return new GameListResult { Total = total, Page = page, PageSize = pageSize, Items = items };
    }

    // ── Distinct values for filters ────────────────────────────────────────────

    public List<string> GetDistinctGenres(string platform)
    {
        const string sql = "SELECT Genres FROM Games WHERE Platform = $p AND Genres IS NOT NULL AND Genres != ''";
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", platform);

        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            if (rdr.IsDBNull(0)) continue;
            foreach (var g in rdr.GetString(0).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(g);
        }
        return set.ToList();
    }

    public List<string> GetDistinctDevelopers(string platform, string queryFilter, int limit)
        => GetDistinctSingle("Developer", platform, queryFilter, limit);

    public List<string> GetDistinctPublishers(string platform, string queryFilter, int limit)
        => GetDistinctSingle("Publisher", platform, queryFilter, limit);

    public List<string> GetDistinctReleaseTypes(string platform)
        => GetDistinctSingle("ReleaseType", platform, null, 200);

    public List<string> GetDistinctOrigins(string platform)
        => GetDistinctSingle("Origin", platform, null, 200);

    private List<string> GetDistinctSingle(string column, string platform, string queryFilter, int limit)
    {
        string filter = string.IsNullOrEmpty(queryFilter) ? "" : $"AND {column} LIKE $q";
        var sql = $"""
            SELECT DISTINCT {column}
            FROM   Games
            WHERE  Platform = $p AND {column} IS NOT NULL AND {column} != ''
            {filter}
            ORDER  BY {column}
            LIMIT  $limit
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", platform);
        if (!string.IsNullOrEmpty(queryFilter)) cmd.Parameters.AddWithValue("$q", "%" + queryFilter + "%");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var list = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    // ── Star tiers ─────────────────────────────────────────────────────────────

    /// <summary>Votes-count cutoff = 70% of the mean CommunityRatingCount across rated (rating>3) games.</summary>
    public int GetStarThreshold(string platform)
    {
        const string sql = """
            SELECT AVG(CommunityRatingCount)
            FROM   Games
            WHERE  Platform = $p AND CommunityRating > 3 AND CommunityRatingCount > 0
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", platform);
        var v = cmd.ExecuteScalar();
        if (v is null || v is DBNull) return 0;
        return (int)(Convert.ToDouble(v, CultureInfo.InvariantCulture) * 0.7);
    }

    /// <summary>0 = no star, 1 = bronze, 2 = silver, 3 = gold.</summary>
    public static int ComputeStarTier(double? rating, int ratingCount, int threshold)
    {
        if (!rating.HasValue || ratingCount < threshold || threshold <= 0) return 0;
        var rt = rating.Value;
        if (rt >= 4.5) return 3;
        if (rt >= 4.0) return 2;
        if (rt >= 3.7) return 1;
        return 0;
    }

    /// <summary>Quality star tier (1/2/3) per game DatabaseID for a platform — the overlay the theme wheels
    /// draw on posters (stars.json). Empty when the DB isn't ready or the platform has too few rated games for a
    /// meaningful threshold. Global ranking (community rating + votes), NOT per-user state.</summary>
    public Dictionary<int, int> GetStarTiers(string platform)
    {
        var map = new Dictionary<int, int>();
        if (string.IsNullOrEmpty(platform) || !AnyDbReady()) return map;

        int threshold = GetStarThreshold(platform);
        if (threshold <= 0) return map;

        const string sql = """
            SELECT DatabaseID, CommunityRating, CommunityRatingCount
            FROM   Games
            WHERE  Platform = $p AND CommunityRating IS NOT NULL AND CommunityRating > 0
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", platform);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            int id = rdr.GetInt32(0);
            double? rating = rdr.IsDBNull(1) ? null : rdr.GetDouble(1);
            int cnt = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
            int tier = ComputeStarTier(rating, cnt, threshold);
            if (tier > 0) map[id] = tier;
        }
        return map;
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    public sealed class SearchResult
    {
        public int DatabaseID { get; set; }
        public string Name { get; set; }
        public string Platform { get; set; }
        public int? Year { get; set; }
        public bool IsAdult { get; set; }
        public string ESRB { get; set; }
        public string MatchedAlt { get; set; }
    }

    /// <summary>Substring search over game Name + alternate titles. adult=0 excludes AO-rated content.</summary>
    public List<SearchResult> Search(string q, int limit, int adult)
    {
        if (string.IsNullOrWhiteSpace(q)) return new();

        string sql = """
            SELECT g.DatabaseID, g.Name, g.Platform, g.ReleaseYear, g.ESRB,
                   (CASE
                     WHEN g.Name LIKE $q THEN g.Name
                     ELSE (SELECT a.AlternateName FROM GameAlternateTitles a
                           WHERE a.DatabaseID = g.DatabaseID AND a.AlternateName LIKE $q
                           LIMIT 1)
                    END) AS Matched
            FROM   Games g
            WHERE  (g.Name LIKE $q
               OR   g.DatabaseID IN (SELECT DatabaseID FROM GameAlternateTitles WHERE AlternateName LIKE $q))
            """;
        if (adult == 0) sql += " AND (g.ESRB IS NULL OR g.ESRB NOT LIKE 'AO%')";
        sql += " ORDER BY g.Name LIMIT $limit";

        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$q", "%" + q.Trim() + "%");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var list = new List<SearchResult>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var esrb = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            list.Add(new SearchResult
            {
                DatabaseID = rdr.GetInt32(0),
                Name = rdr.GetString(1),
                Platform = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                Year = rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                IsAdult = esrb is not null && esrb.StartsWith("AO", StringComparison.OrdinalIgnoreCase),
                ESRB = esrb,
                MatchedAlt = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            });
        }
        return list;
    }

    // ── Per-game details (images, alts, roms) ──────────────────────────────────

    public List<DbGameImage> GetImagesForGame(int id)
    {
        const string sql = """
            SELECT FileName, DatabaseId, Type, Region, CRC32, Origin, Sex, FileSize
            FROM   GameImages
            WHERE  DatabaseId = $id
            AND    (duplicate IS NULL OR duplicate < 100)
            AND    (FileSize IS NULL OR FileSize >= 500)
            ORDER  BY CASE Type
                        WHEN 'Poster' THEN 1
                        WHEN 'Box - Front' THEN 2
                        WHEN 'Fanart - Box - Front' THEN 3
                        WHEN 'Box - 3D' THEN 4
                        WHEN 'Cart - Front' THEN 5
                        WHEN 'Fanart - Background' THEN 6
                        WHEN 'Screenshot - Game Title' THEN 7
                        WHEN 'Screenshot - Gameplay' THEN 8
                        ELSE 99 END,
                      Type, Region
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);

        var list = new List<DbGameImage>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new DbGameImage
            {
                FileName = rdr.GetString(0),
                DatabaseId = rdr.GetInt32(1),
                Type = rdr.GetString(2),
                Region = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                CRC32 = rdr.GetInt64(4),
                Origin = rdr.IsDBNull(5) ? "launchbox" : rdr.GetString(5),
                Sex = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                FileSize = rdr.IsDBNull(7) ? 0 : rdr.GetInt64(7),
            });
        }
        return list;
    }

    public List<DbAlternateTitle> GetAltsForGame(int id)
    {
        const string sql = """
            SELECT AlternateName, DatabaseID, Region
            FROM   GameAlternateTitles
            WHERE  DatabaseID = $id
            ORDER  BY Region, AlternateName
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);

        var list = new List<DbAlternateTitle>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new DbAlternateTitle
            {
                AlternateName = rdr.GetString(0),
                DatabaseID = rdr.GetInt32(1),
                Region = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
            });
        }
        return list;
    }

    public List<DbGameRom> GetRomsForGame(int id)
    {
        const string sql = """
            SELECT FileName, FileSize, CRC32, Origin
            FROM   GameRoms
            WHERE  DatabaseID = $id
            ORDER  BY FileName
            """;
        var list = new List<DbGameRom>();
        try
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                list.Add(new DbGameRom
                {
                    FileName = rdr.GetString(0),
                    FileSize = rdr.GetInt64(1),
                    CRC32 = rdr.GetInt64(2),
                    Origin = rdr.IsDBNull(3) ? "launchbox" : rdr.GetString(3),
                });
            }
        }
        catch { /* GameRoms may be absent on an older Extended DB — return empty */ }
        return list;
    }

    public Dictionary<int, (string FileName, long Crc32, bool NeedsBlur, string Origin)>
        GetCoversForGames(IReadOnlyList<int> gameIds)
    {
        if (gameIds.Count == 0) return new();
        var idList = string.Join(",", gameIds);

        // Join Games to read each game's ESRB so the "steam-blur ONLY when AO" rule can consult the rating.
        var sql = $"""
            SELECT gi.DatabaseId, gi.FileName, gi.CRC32, gi.Origin, gi.Sex, g.ESRB
            FROM   GameImages gi
            JOIN   Games g ON g.DatabaseID = gi.DatabaseId
            WHERE  gi.DatabaseId IN ({idList})
            AND    gi.Type NOT IN ('Icon', 'Manual', 'Press', 'Map', 'Music', 'Video', 'VideoAdvert')
            AND    (gi.duplicate IS NULL OR gi.duplicate < 100)
            AND    (gi.FileSize IS NULL OR gi.FileSize >= 500)
            ORDER  BY gi.DatabaseId, CASE gi.Type
                        WHEN 'Poster' THEN 1
                        WHEN 'Box - Front' THEN 2
                        WHEN 'Fanart - Box - Front' THEN 3
                        WHEN 'Box - 3D' THEN 4
                        WHEN 'Cart - Front' THEN 5
                        ELSE 99 END
            """;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;

        var dict = new Dictionary<int, (string, long, bool, string)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var id = rdr.GetInt32(0);
            if (dict.ContainsKey(id)) continue;
            var origin = rdr.IsDBNull(3) ? "launchbox" : rdr.GetString(3);
            var sex = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
            var esrb = rdr.IsDBNull(5) ? "" : rdr.GetString(5);
            bool isAO = esrb.StartsWith("AO", StringComparison.OrdinalIgnoreCase);
            var blur = (origin.Equals("steam", StringComparison.OrdinalIgnoreCase) && isAO)
                       || (origin.Equals("vndb", StringComparison.OrdinalIgnoreCase) && sex == 1);
            dict[id] = (rdr.GetString(1), rdr.GetInt64(2), blur, origin);
        }
        return dict;
    }

    // ── Mappers ────────────────────────────────────────────────────────────────

    private static DbGame MapGame(SqliteDataReader r) => new()
    {
        DatabaseID = r.GetInt32(r.GetOrdinal("DatabaseID")),
        Name = r.GetString(r.GetOrdinal("Name")),
        ReleaseDate = SafeStr(r, "ReleaseDate"),
        ReleaseYear = SafeIntOrNull(r, "ReleaseYear"),
        Overview = SafeStr(r, "Overview"),
        MaxPlayers = SafeIntOrNull(r, "MaxPlayers"),
        ReleaseType = SafeStr(r, "ReleaseType"),
        Cooperative = (SafeIntOrNull(r, "Cooperative") ?? 0) != 0,
        VideoURL = SafeStr(r, "VideoURL"),
        CommunityRating = SafeDoubleOrNull(r, "CommunityRating"),
        CommunityRatingCount = SafeIntOrNull(r, "CommunityRatingCount") ?? 0,
        WikipediaURL = SafeStr(r, "WikipediaURL"),
        Platform = r.GetString(r.GetOrdinal("Platform")),
        ESRB = SafeStr(r, "ESRB"),
        Genres = SafeStr(r, "Genres"),
        Developer = SafeStr(r, "Developer"),
        Publisher = SafeStr(r, "Publisher"),
        OverviewAiFr = SafeStr(r, "OverviewAiFr"),
        OverviewAiEn = SafeStr(r, "OverviewAiEn"),
        OverviewAiDe = SafeStr(r, "OverviewAiDe"),
        OverviewAiEs = SafeStr(r, "OverviewAiEs"),
        OverviewAiIt = SafeStr(r, "OverviewAiIt"),
        OverviewAiPt = SafeStr(r, "OverviewAiPt"),
        OverviewSteamFr = SafeStr(r, "OverviewSteamFr"),
        OverviewScFr = SafeStr(r, "OverviewScFr"),
        OverviewSteamEn = SafeStr(r, "OverviewSteamEn"),
        OverviewScEn = SafeStr(r, "OverviewScEn"),
        SteamId = SafeIntOrNull(r, "SteamId"),
        SteamAppId = SafeIntOrNull(r, "SteamAppId"),
        VNDBID = SafeIntOrNull(r, "VNDBID"),
        ScreenscraperId = SafeIntOrNull(r, "ScreenscraperId"),
        IgdbSlug = SafeStr(r, "IgdbSlug"),
        Origin = SafeStr(r, "Origin") ?? "launchbox",
    };

    private static DbPlatform MapPlatform(SqliteDataReader r) => new()
    {
        PlatformKey = SafeIntOrNull(r, "PlatformKey") ?? 0,
        Name = r.GetString(r.GetOrdinal("Name")),
        Emulated = (SafeIntOrNull(r, "Emulated") ?? 0) != 0,
        ReleaseDate = SafeStr(r, "ReleaseDate"),
        Developer = SafeStr(r, "Developer"),
        Manufacturer = SafeStr(r, "Manufacturer"),
        Cpu = SafeStr(r, "Cpu"),
        Memory = SafeStr(r, "Memory"),
        Graphics = SafeStr(r, "Graphics"),
        Sound = SafeStr(r, "Sound"),
        Display = SafeStr(r, "Display"),
        Media = SafeStr(r, "Media"),
        MaxControllers = SafeStr(r, "MaxControllers"),
        Notes = SafeStr(r, "Notes"),
        Category = SafeStr(r, "Category"),
        GameCount = (int)r.GetInt64(r.GetOrdinal("GameCount")),
    };

    private static DbGameSummary MapGameSummary(SqliteDataReader r) => new()
    {
        DatabaseID = r.GetInt32(0),
        Name = r.GetString(1),
        Platform = r.IsDBNull(2) ? "" : r.GetString(2),
        ReleaseYear = r.IsDBNull(3) ? null : r.GetInt32(3),
        Genres = r.IsDBNull(4) ? null : r.GetString(4),
        ESRB = r.IsDBNull(5) ? null : r.GetString(5),
        CompareName = r.IsDBNull(6) ? null : r.GetString(6),
        ReleaseDate = r.IsDBNull(7) ? null : r.GetString(7),
        CommunityRating = r.IsDBNull(8) ? null : r.GetDouble(8),
        CommunityRatingCount = r.IsDBNull(9) ? 0 : r.GetInt32(9),
        MaxPlayers = r.IsDBNull(10) ? null : r.GetInt32(10),
        Cooperative = !r.IsDBNull(11) && r.GetInt32(11) != 0,
        Origin = r.IsDBNull(12) ? null : r.GetString(12),
        ReleaseType = r.IsDBNull(13) ? null : r.GetString(13),
        Developer = r.IsDBNull(14) ? null : r.GetString(14),
        Publisher = r.IsDBNull(15) ? null : r.GetString(15),
    };

    private static string SafeStr(SqliteDataReader r, string col)
    {
        int o; try { o = r.GetOrdinal(col); } catch { return null; }
        return r.IsDBNull(o) ? null : r.GetString(o);
    }
    private static int? SafeIntOrNull(SqliteDataReader r, string col)
    {
        int o; try { o = r.GetOrdinal(col); } catch { return null; }
        return r.IsDBNull(o) ? null : r.GetInt32(o);
    }
    private static double? SafeDoubleOrNull(SqliteDataReader r, string col)
    {
        int o; try { o = r.GetOrdinal(col); } catch { return null; }
        return r.IsDBNull(o) ? null : r.GetDouble(o);
    }
}
