// JSON API for the database site — the endpoints the server-rendered pages fetch to populate their grids and
// detail views. Clean-room LiteBox rewrite of ExtendDB's Web/Api/{Platforms,PlatformDetail,PlatformGames,
// PlatformFilters,Search,GameDetail}Api.
//
//   GET /api/platforms
//   GET /api/platforms/{slug}
//   GET /api/platforms/{slug}/games            (page/sort/filter)
//   GET /api/platforms/{slug}/{genres|developers|publishers|release-types|origins}
//   GET /api/search
//   GET /api/games/{id}
//
// Media URLs point at the S2 endpoints: paginated-grid thumbnails use /api/media/{id}.jpg (MediaProxy cover-
// by-id); per-image detail URLs are signed proxy URLs via MediaProxy.BuildProxyUrl (→ /api/media/{tok}.{sig}.
// {ext}, resolved upstream by the native MediaFetch chain). Parental filtering uses the S2 WebParentalState
// API: EffectiveAdult drives the SQL adult gate, IsRatingAllowed gates the game detail, IsHidden hides
// platforms.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal static class PlatformsApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        if (!DbRepository.AnyDbReady()) return HttpResponse.Json("[]");
        var parental = WebParentalState.From(ctx.Request);
        var repo = new DbRepository();
        var platforms = HomeHandler.FilterHidden(repo.GetAllPlatforms(), parental);

        var dto = platforms.Select(p => new
        {
            slug = PlatformSlug.For(p.Name),
            name = p.Name,
            gameCount = p.GameCount,
            manufacturer = p.Manufacturer,
            category = p.Category,
            emulated = p.Emulated,
            releaseDate = p.ReleaseDate,
        }).ToArray();

        return HttpResponse.Json(JsonSerializer.Serialize(dto));
    }
}

internal static class PlatformDetailApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var slug = ctx.GetRoute("slug");
        if (string.IsNullOrEmpty(slug)) return HttpResponse.NotFound();
        if (!DbRepository.AnyDbReady()) return HttpResponse.NotFound();

        var repo = new DbRepository();
        var p = PlatformDetailHandler.ResolvePlatformBySlug(repo, slug);
        if (p == null) return HttpResponse.NotFound();

        var parental = WebParentalState.From(ctx.Request);
        if (parental.IsLocked && parental.IsHidden(p.Name)) return HttpResponse.NotFound();

        var dto = new
        {
            slug = PlatformSlug.For(p.Name),
            name = p.Name,
            gameCount = p.GameCount,
            manufacturer = p.Manufacturer,
            developer = p.Developer,
            category = p.Category,
            emulated = p.Emulated,
            releaseDate = p.ReleaseDate,
            cpu = p.Cpu,
            memory = p.Memory,
            graphics = p.Graphics,
            sound = p.Sound,
            display = p.Display,
            media = p.Media,
            maxControllers = p.MaxControllers,
            notes = p.Notes,
        };
        return HttpResponse.Json(JsonSerializer.Serialize(dto));
    }
}

internal static class PlatformFiltersApi
{
    private static DbPlatform Resolve(RouteContext ctx, DbRepository repo, out HttpResponse error)
    {
        error = null;
        if (!DbRepository.AnyDbReady()) { error = HttpResponse.Json("[]"); return null; }
        var slug = ctx.GetRoute("slug");
        var p = PlatformDetailHandler.ResolvePlatformBySlug(repo, slug);
        if (p == null) { error = HttpResponse.NotFound(); return null; }
        return p;
    }

    public static HttpResponse HandleGenres(RouteContext ctx)
    {
        var repo = new DbRepository();
        var p = Resolve(ctx, repo, out var err); if (p == null) return err;
        return HttpResponse.Json(JsonSerializer.Serialize(repo.GetDistinctGenres(p.Name)));
    }

    public static HttpResponse HandleDevelopers(RouteContext ctx)
    {
        var repo = new DbRepository();
        var p = Resolve(ctx, repo, out var err); if (p == null) return err;
        var q = ctx.Request.GetQuery("q");
        var limit = ctx.Request.GetQueryInt("limit", 100);
        return HttpResponse.Json(JsonSerializer.Serialize(repo.GetDistinctDevelopers(p.Name, q, limit)));
    }

    public static HttpResponse HandlePublishers(RouteContext ctx)
    {
        var repo = new DbRepository();
        var p = Resolve(ctx, repo, out var err); if (p == null) return err;
        var q = ctx.Request.GetQuery("q");
        var limit = ctx.Request.GetQueryInt("limit", 100);
        return HttpResponse.Json(JsonSerializer.Serialize(repo.GetDistinctPublishers(p.Name, q, limit)));
    }

    public static HttpResponse HandleReleaseTypes(RouteContext ctx)
    {
        var repo = new DbRepository();
        var p = Resolve(ctx, repo, out var err); if (p == null) return err;
        return HttpResponse.Json(JsonSerializer.Serialize(repo.GetDistinctReleaseTypes(p.Name)));
    }

    public static HttpResponse HandleOrigins(RouteContext ctx)
    {
        var repo = new DbRepository();
        var p = Resolve(ctx, repo, out var err); if (p == null) return err;
        return HttpResponse.Json(JsonSerializer.Serialize(repo.GetDistinctOrigins(p.Name)));
    }
}

internal static class PlatformGamesApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var slug = ctx.GetRoute("slug");
        if (string.IsNullOrEmpty(slug)) return HttpResponse.NotFound();
        if (!DbRepository.AnyDbReady())
            return HttpResponse.Json(JsonSerializer.Serialize(new { total = 0, page = 1, pageSize = 50, thumbBase = "/api/media", items = Array.Empty<object>() }));

        var repo = new DbRepository();
        var p = PlatformDetailHandler.ResolvePlatformBySlug(repo, slug);
        if (p == null) return HttpResponse.NotFound();

        var req = ctx.Request;
        var parental = WebParentalState.From(req);
        if (parental.IsLocked && parental.IsHidden(p.Name)) return HttpResponse.NotFound();

        var threshold = repo.GetStarThreshold(p.Name);
        var userAdult = req.GetQueryInt("adult", 1);

        var opt = new DbRepository.GameListOptions
        {
            Platform = p.Name,
            Page = Math.Max(1, req.GetQueryInt("page", 1)),
            PageSize = Math.Clamp(req.GetQueryInt("pageSize", 50), 1, 500),
            Sort = req.GetQuery("sort") ?? "alpha",
            Genre = req.GetQuery("genre"),
            Search = req.GetQuery("q"),
            Developer = req.GetQuery("developer"),
            Publisher = req.GetQuery("publisher"),
            MinYear = ParseIntOrNull(req.GetQuery("minYear")),
            MaxYear = ParseIntOrNull(req.GetQuery("maxYear")),
            MinRating = ParseDoubleOrNull(req.GetQuery("minRating")),
            MinVotes = ParseIntOrNull(req.GetQuery("minVotes")),
            MinPlayers = ParseIntOrNull(req.GetQuery("minPlayers")),
            Coop = ParseBoolOrNull(req.GetQuery("coop")),
            ReleaseType = req.GetQuery("releaseType"),
            Origin = req.GetQuery("origin"),
            // When locked, EffectiveAdult forces 0 → the SQL AO gate hides adult content across all pages;
            // the rating RULES ride the same query (counts/paging stay correct — plugin parity).
            Adult = parental.EffectiveAdult(userAdult),
            ExtraWhere = parental.EsrbSqlFilter(),
            OwnedOnly = ResolveOwnedOnly(req),
            StarThreshold = threshold,
        };

        var result = repo.QueryGames(opt);
        var ownedIds = OwnedLookup.GetIdsForPlatform(p.Name);

        var dto = new
        {
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
            thumbBase = "/api/media",
            starThreshold = threshold,
            items = result.Items.Select(g => new
            {
                id = g.DatabaseID,
                name = g.Name,
                year = g.EffectiveYear,
                genres = g.Genres,
                rating = g.CommunityRating,
                ratingCount = g.CommunityRatingCount,
                maxPlayers = g.MaxPlayers,
                coop = g.Cooperative,
                releaseType = g.ReleaseType,
                origin = g.Origin,
                developer = g.Developer,
                publisher = g.Publisher,
                adult = g.IsAdult,
                coverBlur = g.CoverNeedsBlur,
                starTier = DbRepository.ComputeStarTier(g.CommunityRating, g.CommunityRatingCount, threshold),
                owned = ownedIds.Contains(g.DatabaseID),
            }),
        };
        return HttpResponse.Json(JsonSerializer.Serialize(dto));
    }

    private static bool ResolveOwnedOnly(HttpRequest req)
    {
        if (req == null) return false;
        var qp = req.GetQuery("owned");
        if (!string.IsNullOrEmpty(qp)) return qp == "1";
        return req.GetCookie("litebox_owned") == "1";
    }

    private static int? ParseIntOrNull(string s) => int.TryParse(s, out var v) ? v : (int?)null;
    private static double? ParseDoubleOrNull(string s)
        => double.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
    private static bool? ParseBoolOrNull(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }
}

internal static class SearchApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var q = ctx.Request.GetQuery("q");
        if (string.IsNullOrWhiteSpace(q) || !DbRepository.AnyDbReady())
            return HttpResponse.Json("[]");

        var limit = ctx.Request.GetQueryInt("limit", 20);
        var userAdult = ctx.Request.GetQueryInt("adult", 1);

        var parental = WebParentalState.From(ctx.Request);
        var adult = parental.EffectiveAdult(userAdult);

        var repo = new DbRepository();
        IEnumerable<DbRepository.SearchResult> raw = repo.Search(q, limit, adult);

        // Under lock, additionally drop titles the rating rules block (small capped list → post-filter is fine).
        if (parental.IsLocked)
            raw = raw.Where(r => parental.IsRatingAllowed(r.ESRB));

        var dto = raw.Select(r => new
        {
            id = r.DatabaseID,
            name = r.Name,
            platform = r.Platform,
            year = r.Year,
            adult = r.IsAdult,
            matchedAlt = r.MatchedAlt,
        });
        return HttpResponse.Json(JsonSerializer.Serialize(dto));
    }
}

internal static class GameDetailApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var idStr = ctx.GetRoute("id");
        if (!int.TryParse(idStr, out var id)) return HttpResponse.NotFound();
        if (!DbRepository.AnyDbReady()) return HttpResponse.NotFound();

        var repo = new DbRepository();
        var g = repo.GetGameById(id);
        if (g == null) return HttpResponse.NotFound();

        var parental = WebParentalState.From(ctx.Request);
        if (parental.IsLocked && !parental.IsRatingAllowed(g.ESRB))
            return HttpResponse.PlainText("Locked", 403);

        var images = repo.GetImagesForGame(id);
        var alts = repo.GetAltsForGame(id);
        var roms = repo.GetRomsForGame(id);

        var overviews = g.GetAiOverviews();
        string fallback = overviews.Count == 0 ? g.PickOverview() : null;

        var dto = new
        {
            id = g.DatabaseID,
            name = g.Name,
            platform = g.Platform,
            platformSlug = PlatformSlug.For(g.Platform),
            year = g.ReleaseYear,
            releaseDate = g.ReleaseDate,
            esrb = g.ESRB,
            genres = g.Genres,
            developer = g.Developer,
            publisher = g.Publisher,
            rating = g.CommunityRating,
            ratingCount = g.CommunityRatingCount,
            maxPlayers = g.MaxPlayers,
            cooperative = g.Cooperative,
            releaseType = g.ReleaseType,
            videoUrl = g.VideoURL,
            wikipediaUrl = g.WikipediaURL,
            steamId = g.SteamId,
            steamAppId = g.SteamAppId,
            vndbId = g.VNDBID,
            screenscraperId = g.ScreenscraperId,
            igdbSlug = g.IgdbSlug,
            origin = g.Origin,
            adult = g.IsAdult,
            overviews = overviews,
            fallbackOverview = fallback,
            // Each image resolves through the S2 signed media proxy (upstream via the native MediaFetch chain).
            // Local-disk enrichment (hasLocal / orphans) is a theme-data (S4) concern; here every row is remote.
            images = images
                .Select(i => new
                {
                    type = i.Type,
                    region = i.Region,
                    origin = i.Origin,
                    blur = i.NeedsBlur,
                    orphan = false,
                    hasLocal = false,
                    url = MediaProxy.BuildProxyUrl(null, i.FileName, i.CRC32, ExtOf(i.FileName), i.Origin, i.Type),
                    name = (string)null,
                })
                .Where(i => i.url != null),
            alts = alts.Select(a => new { name = a.AlternateName, region = a.Region }),
            roms = roms.Select(r => new
            {
                fileName = r.FileName,
                size = r.FileSizeHuman,
                sizeBytes = r.FileSize,
                crc32 = r.Crc32Hex,
                origin = r.Origin,
            }),
        };
        return HttpResponse.Json(JsonSerializer.Serialize(dto));
    }

    private static string ExtOf(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return string.IsNullOrEmpty(ext) ? "jpg" : ext;
    }
}
