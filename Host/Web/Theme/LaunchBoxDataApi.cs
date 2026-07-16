// Data-contract handlers for the LiteBox Web theme's /launchbox/data/* routes. Each is a thin alias of the
// matching BigBox handler: it reads the same route values and delegates to the SAME OwnedDataProvider method.
// The only difference from the /bigbox/ counterparts is the URL prefix — the data and JSON shape are identical.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/LaunchBox/LaunchBoxDataApi.cs. The combined Recent()/CatMedia()
// handlers fan out on the "kind" capture (platforms|playlists|categories).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

internal static class LaunchBoxDataApi
{
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static HttpResponse CatTree(RouteContext ctx)
        => Json(OwnedDataProvider.CatTree(WebParentalState.From(ctx.Request)));

    public static HttpResponse PlatformGames(RouteContext ctx)
        => Json(OwnedDataProvider.PlatformGames(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    public static HttpResponse PlaylistGames(RouteContext ctx)
        => Json(OwnedDataProvider.PlaylistGames(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    public static HttpResponse GameDetail(RouteContext ctx)
    {
        var extra = string.Equals(ctx.Request.GetQuery("extra"), "1", StringComparison.OrdinalIgnoreCase);
        var o = OwnedDataProvider.GameDetail(ctx.GetRoute("id"), WebParentalState.From(ctx.Request), extra);
        return o == null ? HttpResponse.NotFound() : Json(o);
    }

    public static HttpResponse InstallState(RouteContext ctx)
    {
        var o = OwnedDataProvider.InstallStateOf(ctx.GetRoute("id"));
        return o == null ? HttpResponse.NotFound() : Json(o);
    }

    public static HttpResponse Recent(RouteContext ctx)
    {
        var slug = ctx.GetRoute("slug");
        var st = WebParentalState.From(ctx.Request);
        var kind = (ctx.GetRoute("kind") ?? "").ToLowerInvariant();
        object result = kind switch
        {
            "playlists"  => OwnedDataProvider.PlaylistRecent(slug, st),
            "categories" => OwnedDataProvider.CategoryRecent(slug, st),
            _            => OwnedDataProvider.PlatformRecent(slug, st),
        };
        return Json(result);
    }

    public static HttpResponse CatMedia(RouteContext ctx)
    {
        var slug = ctx.GetRoute("slug");
        var order = ctx.Request.GetQuery("order");
        var st = WebParentalState.From(ctx.Request);
        var kind = (ctx.GetRoute("kind") ?? "").ToLowerInvariant();
        object result = kind switch
        {
            "playlists"  => OwnedDataProvider.PlaylistCatMedia(slug, order, st),
            "categories" => OwnedDataProvider.CategoryCatMedia(slug, order, st),
            _            => OwnedDataProvider.PlatformCatMedia(slug, order, st),
        };
        return Json(result);
    }

    // ── data/platforms/<slug>/stars.json (same path as BigBox; local memo cache) ─
    private static readonly ConcurrentDictionary<string, Dictionary<int, int>> _starTierCache = new();

    public static HttpResponse Stars(RouteContext ctx)
    {
        var slug = ctx.GetRoute("slug");
        string name = null;
        try { name = OwnedDataProvider.PlatformNameForSlug(slug); } catch { }
        if (string.IsNullOrEmpty(name)) return Json(new Dictionary<string, int>());

        string cacheKey = (DbRepository.ExtendedDbReady() ? "e|" : "n|") + name;
        var map = _starTierCache.GetOrAdd(cacheKey, _ =>
        {
            try { return new DbRepository().GetStarTiers(name); }
            catch (Exception ex) { LbLog.Warn("web", "GetStarTiers(" + name + "): " + ex.Message); return new Dictionary<int, int>(); }
        });
        var outMap = new Dictionary<string, int>(map.Count);
        foreach (var kv in map) outMap[kv.Key.ToString()] = kv.Value;
        return Json(outMap);
    }

    // ── data/games/<id>/related.json + related/overviews.json (native suggester engine) ─

    public static HttpResponse Related(RouteContext ctx)
        => Json(OwnedDataProvider.Related(ctx.GetRoute("id"), WebParentalState.From(ctx.Request), ctx.Request.GetQueryInt("limit", 50)));

    public static HttpResponse RelatedOverviews(RouteContext ctx)
        => Json(RelatedProvider.Overviews(ctx.Request.GetQuery("ids"), WebParentalState.From(ctx.Request)));

    private static HttpResponse Json(object obj)
        => HttpResponse.Json(JsonSerializer.Serialize(obj, _json));
}

/// <summary>Thin-alias mutation handler for /launchbox/api/games/{id}/{kind} — delegates to the shared
/// BigBoxMutationApi dispatch (rating/favorite/hide/broken/play/resethistory). Archive routes are S6.</summary>
internal static class LaunchBoxMutationApi
{
    public static HttpResponse Handle(RouteContext ctx) => BigBoxMutationApi.Handle(ctx);
}
