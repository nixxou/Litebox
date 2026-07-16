// Data-contract handlers for the BigBox Web theme's /bigbox/data/* + /bigbox/api/* routes. Each handler
// forwards to OwnedDataProvider, which reads LiteBox's real in-memory library (PluginHelper.DataManager) +
// GameCache media — the same JSON contract the shipped theme JS (engine/data.js) fetches over HTTP.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Theme/BigBoxThemeApi.cs. Static-file serving (index / catch-all)
// stays with the S1 static handler, so this file is data/api only. Game id is the LaunchBox GUID string.
//
// DEVIATIONS: the Similar-Games engine is not ported (out of S4) → "View Related Games" is withheld from the
// detail menu, and Related / RelatedOverviews serve empty. Unknown slug ⇒ empty 200 payload (NOT 404) so the
// engine doesn't fall back to its dummy data.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

internal static class BigBoxThemeApi
{
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    // ── data/cattree.json ──────────────────────────────────────────

    public static HttpResponse CatTree(RouteContext ctx)
        => Json(OwnedDataProvider.CatTree(WebParentalState.From(ctx.Request)));

    // ── data/detailmenu.json ───────────────────────────────────────

    public static HttpResponse DetailMenu(RouteContext ctx)
    {
        var menu = new List<object>
        {
            new { label = "Play",        action = "play" },
            new { label = "Star Rating", action = "rating" },
            // S4: "View Related Games" withheld — the Similar-Games engine isn't ported to LiteBox.
            new { label = "Favorite",       action = "favorite" },
            new { label = "Hide",           action = "hide" },
            new { label = "Mark as Broken", action = "broken" },
        };
        if (WebParentalState.From(ctx.Request).IsActive)
            menu.Add(new { label = "Unlock", action = "unlock" });
        return Json(menu.ToArray());
    }

    // ── data/system.json ───────────────────────────────────────────

    public static HttpResponse SystemMenu(RouteContext ctx)
    {
        var items = new List<string> { "Back", "About" };
        if (WebParentalState.From(ctx.Request).IsActive) items.Add("Unlock");
        items.Add("Exit");
        return Json(items);
    }

    // ── data/platforms/<slug>/games.json + playlists ───────────────

    public static HttpResponse PlatformGames(RouteContext ctx)
        => Json(OwnedDataProvider.PlatformGames(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    public static HttpResponse PlaylistGames(RouteContext ctx)
        => Json(OwnedDataProvider.PlaylistGames(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    // ── data/platforms/<slug>/stars.json ───────────────────────────
    // Quality star tiers per game DatabaseID, ALWAYS from the Extended DB (a global ranking, not per-user).
    // Memoized per platform name, keyed also by DB mode so tiers aren't reused across a DB swap.
    private static readonly ConcurrentDictionary<string, Dictionary<int, int>> _starTierCache = new();

    public static HttpResponse PlatformStars(RouteContext ctx)
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

    // ── recent.json / catmedia.json / installstate.json ────────────

    public static HttpResponse PlatformRecent(RouteContext ctx)
        => Json(OwnedDataProvider.PlatformRecent(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    public static HttpResponse PlaylistRecent(RouteContext ctx)
        => Json(OwnedDataProvider.PlaylistRecent(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    public static HttpResponse CategoryRecent(RouteContext ctx)
        => Json(OwnedDataProvider.CategoryRecent(ctx.GetRoute("slug"), WebParentalState.From(ctx.Request)));

    public static HttpResponse InstallState(RouteContext ctx)
    {
        var o = OwnedDataProvider.InstallStateOf(ctx.GetRoute("id"));
        return o == null ? HttpResponse.NotFound() : Json(o);
    }

    public static HttpResponse PlatformCatMedia(RouteContext ctx)
        => Json(OwnedDataProvider.PlatformCatMedia(ctx.GetRoute("slug"), ctx.Request.GetQuery("order"), WebParentalState.From(ctx.Request)));

    public static HttpResponse PlaylistCatMedia(RouteContext ctx)
        => Json(OwnedDataProvider.PlaylistCatMedia(ctx.GetRoute("slug"), ctx.Request.GetQuery("order"), WebParentalState.From(ctx.Request)));

    public static HttpResponse CategoryCatMedia(RouteContext ctx)
        => Json(OwnedDataProvider.CategoryCatMedia(ctx.GetRoute("slug"), ctx.Request.GetQuery("order"), WebParentalState.From(ctx.Request)));

    // ── data/games/<id>/detail.json ────────────────────────────────

    public static HttpResponse GameDetail(RouteContext ctx)
    {
        var extra = string.Equals(ctx.Request.GetQuery("extra"), "1", StringComparison.OrdinalIgnoreCase);
        var o = OwnedDataProvider.GameDetail(ctx.GetRoute("id"), WebParentalState.From(ctx.Request), extra);
        return o == null ? HttpResponse.NotFound() : Json(o);
    }

    // ── data/games/<id>/related.json + overviews (empty — Similar not ported) ─

    public static HttpResponse Related(RouteContext ctx)
    {
        int limit = ctx.Request.GetQueryInt("limit", 50);
        return Json(OwnedDataProvider.Related(ctx.GetRoute("id"), WebParentalState.From(ctx.Request), limit));
    }

    public static HttpResponse RelatedOverviews(RouteContext ctx)
        => Json(new Dictionary<string, string>());

    // ── JSON helper ────────────────────────────────────────────────

    private static HttpResponse Json(object obj)
        => HttpResponse.Json(JsonSerializer.Serialize(obj, _json));
}
