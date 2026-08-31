// PUT /api/roms/{id}/user + the collections — the write-backs a phone is allowed to make.
//
// The rom_user fields split by where the truth lives:
//   • hidden → IGame.Hide, rating (0–10) → IGame.StarRatingFloat (0–5), status "finished" ↔
//     IGame.Completed, last_played (the update_last_played query flag) → IGame.LastPlayedDate —
//     written through the game's setters + DataManager.Save, so BigBox shows what the phone set.
//   • backlogged / now_playing / difficulty / completion → no LaunchBox twin; persisted per-game in
//     the options DB ("Romm.RomUser", game scope) so they round-trip honestly.
//   • status AND completion are both. RomM says "done" twice — a status of finished/completed_100 and
//     a progress bar at 100 — where LaunchBox has one flag, so EITHER signal sets IGame.Completed and
//     the flag reports back as both. A client sending only one of the two does not erase the other:
//     what it sends is merged with what was already stored before the flag is computed.
//     Its finished axis is IGame.Completed, which somebody can also flip from BigBox,
//     so LaunchBox decides that axis on the way out; the finer values RomM has and LaunchBox does not
//     (retired, and the never-played / incomplete distinction) come from the stored value or, failing
//     that, from the play history. A game marked Completed in LaunchBox now reads "finished" on a phone
//     that never set anything, which is the point of a shared library.
//
// Collections: LaunchBox playlists, read-only, plus ONE writable synthetic collection — "Favorites"
// (is_favorite = true), whose membership IS IGame.Favorite. That is not a convenience: RomM has no
// favorite flag on rom_user at all; the heart button on every client is collection membership on the
// favorite collection. Wiring it to LB's own flag is what makes the phone's heart show in BigBox.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

internal static class RommUserApi
{
    private const string FavoritesName = "Favorites";
    private const string RomUserKey = "Romm.RomUser";

    // ── PUT /api/roms/{id}/user ───────────────────────────────────────────────

    /// <summary>A boolean sitting at the body's root, for the callers that put the flags there instead of
    /// in the query string.</summary>
    private static bool BodyFlag(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.True;

    public static HttpResponse UpdateRomUser(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "PUT", StringComparison.OrdinalIgnoreCase))
            return RommApi.Error(405, "Method not allowed");
        var refused = RommAuthApi.Require(ctx, RommScopes.RomsUserWrite, out var identity);
        if (refused != null) return refused;

        var game = RommLibrary.GameByRomId(ctx.GetRouteInt("id", -1));
        if (game == null) return RommApi.Error(404, "Rom not found");
        var gameId = RommLibrary.IdOf(game);

        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrEmpty(ctx.Request!.Body) ? "{}" : ctx.Request.Body).RootElement; }
        catch { return RommApi.Error(400, "Malformed body"); }

        // Two shapes in the wild for the same write. Fields at the root with the flags in the query
        // string, and fields nested under "data" with the flags in the body -- which is what Freegosy
        // sends. Reading both costs a few lines and spares a client an error it cannot act on.
        var body = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Object
            ? wrapped : root;

        bool updateLastPlayed = ctx.Request.GetQueryBool("update_last_played") || BodyFlag(root, "update_last_played");
        bool removeLastPlayed = ctx.Request.GetQueryBool("remove_last_played") || BodyFlag(root, "remove_last_played");
        if (updateLastPlayed && removeLastPlayed)
            return RommApi.Error(400, "update_last_played and remove_last_played are mutually exclusive.");

        var extras = LoadExtras(gameId);
        bool lbDirty = false;

        if (body.TryGetProperty("hidden", out var hid) && (hid.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            try { game.Hide = hid.GetBoolean(); lbDirty = true; } catch { }
        }
        if (body.TryGetProperty("rating", out var rat) && rat.TryGetInt32(out var rating))
        {
            try { game.StarRatingFloat = Math.Clamp(rating, 0, 10) / 2f; lbDirty = true; } catch { }
        }
        if (body.TryGetProperty("status", out var stat))
        {
            var status = stat.ValueKind == JsonValueKind.String ? stat.GetString() : null;
            extras["status"] = status;
            // IGame.Completed is set below, once completion has been read too.

        }
        foreach (var name in new[] { "backlogged", "now_playing" })
            if (body.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False))
                extras[name] = v.GetBoolean();
        foreach (var name in new[] { "difficulty", "completion" })
            if (body.TryGetProperty(name, out var v) && v.TryGetInt32(out var n))
                extras[name] = Math.Clamp(n, 0, name == "completion" ? 100 : 10);

        // Both signals, merged with whatever was already stored, decide the one LaunchBox flag. Reading
        // `extras` rather than the body is what makes a client that sends only "completion" keep its
        // status, and the other way round.
        try
        {
            game.Completed = IsFinished(AsString(extras.GetValueOrDefault("status")))
                          || AsInt(extras.GetValueOrDefault("completion")) >= 100;
            lbDirty = true;
        }
        catch { }

        if (updateLastPlayed) { try { game.LastPlayedDate = DateTime.Now; lbDirty = true; } catch { } }
        else if (removeLastPlayed) { try { game.LastPlayedDate = null; lbDirty = true; } catch { } }

        SaveExtras(gameId, extras);
        if (lbDirty)
        {
            try { PluginHelper.DataManager.Save(false); }
            catch (Exception ex) { LbLog.Warn("romm", "library save failed: " + ex.Message); }
        }

        return RommApi.Json(RomUserDto(game));
    }

    /// <summary>The rom_user block, merged from the LB fields and the stored extras — used by the PUT's
    /// answer and by the rom DTOs.</summary>
    public static object RomUserDto(IGame game)
    {
        var gameId = RommLibrary.IdOf(game);
        int romId = (int)RommRoms.DefaultRomId(game, gameId);
        var extras = LoadExtras(gameId);
        var lastPlayed = RommLibrary.LastPlayedOf(game);

        T Val<T>(string key, T dflt)
        {
            if (!extras.TryGetValue(key, out var raw) || raw is not JsonElement e)
                return extras.TryGetValue(key, out var direct) && direct is T t ? t : dflt;
            try
            {
                if (typeof(T) == typeof(bool) && (e.ValueKind is JsonValueKind.True or JsonValueKind.False)) return (T)(object)e.GetBoolean();
                if (typeof(T) == typeof(int) && e.TryGetInt32(out var n)) return (T)(object)n;
                if (typeof(T) == typeof(string) && e.ValueKind == JsonValueKind.String) return (T)(object)e.GetString()!;
            }
            catch { }
            return dflt;
        }

        // A game LaunchBox calls completed reads as 100% even if nobody ever moved the bar — the two
        // are the same claim — and a bar at 100 counts as completed even when the flag was never ticked.
        bool lbCompleted = false;
        try { lbCompleted = game.Completed; } catch { }
        int completion = Val("completion", 0);
        bool completed = lbCompleted || completion >= 100;
        if (completed) completion = Math.Max(completion, 100);

        return new
        {
            id = romId,
            user_id = RommAuthApi.UserId,
            rom_id = romId,
            created_at = RommAuthApi.Iso(RommLibrary.AddedOf(game)),
            updated_at = RommAuthApi.Iso(RommLibrary.ModifiedOf(game)),
            last_played = lastPlayed == null ? null : RommAuthApi.Iso(lastPlayed.Value),
            is_main_sibling = false,
            backlogged = Val("backlogged", false),
            now_playing = Val("now_playing", false),
            hidden = RommLibrary.HiddenOf(game),
            rating = (int)Math.Round(RommLibrary.RatingOf(game) * 2),
            difficulty = Val("difficulty", 0),
            completion = completion,
            status = StatusOf(completed, Val<string?>("status", null), lastPlayed, game),
        };
    }

    /// <summary>RomM's vocabulary for "where am I with this game", resolved from both stores.
    ///
    /// LaunchBox owns the finished axis: <c>IGame.Completed</c> is what BigBox shows and what a person
    /// can tick there, so it wins on the way out — otherwise a game completed on the desktop would keep
    /// reading "incomplete" on a phone for ever. Everything LaunchBox cannot express falls back to the
    /// stored value, and a game nobody has ever set anything on is described by its play history rather
    /// than by nothing at all.</summary>
    private static string? StatusOf(bool completed, string? stored, DateTime? lastPlayed, IGame game)
    {
        if (completed)
            return string.Equals(stored, "completed_100", StringComparison.OrdinalIgnoreCase)
                ? "completed_100" : "finished";

        // Not completed in LaunchBox: a stored value that says otherwise is stale, anything else stands.
        if (!string.IsNullOrEmpty(stored) && !IsFinished(stored)) return stored;

        int plays = 0;
        try { plays = RommLibrary.PlayCountOf(game); } catch { }
        return plays == 0 && lastPlayed == null ? "never_played" : "incomplete";
    }

    /// <summary>Reads a value out of the extras bag, which holds JsonElements for what was loaded and
    /// plain values for what this request just set.</summary>
    private static string? AsString(object? v)
        => v is JsonElement e ? (e.ValueKind == JsonValueKind.String ? e.GetString() : null) : v as string;

    private static int AsInt(object? v)
    {
        if (v is int i) return i;
        if (v is JsonElement e && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) return n;
        return 0;
    }

    private static bool IsFinished(string? status)
        => string.Equals(status, "finished", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "completed_100", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> LoadExtras(string gameId)
    {
        try
        {
            var raw = LiteBoxOptionsDb.Get(LiteBoxOption.ScopeGame, gameId, RomUserKey);
            if (string.IsNullOrEmpty(raw)) return new Dictionary<string, object?>();
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw!);
            return doc == null
                ? new Dictionary<string, object?>()
                : doc.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch { return new Dictionary<string, object?>(); }
    }

    private static void SaveExtras(string gameId, Dictionary<string, object?> extras)
    {
        try
        {
            LiteBoxOptionsDb.Set(LiteBoxOption.ScopeGame, gameId, RomUserKey,
                extras.Count == 0 ? null : JsonSerializer.Serialize(extras));
        }
        catch (Exception ex) { LbLog.Warn("romm", "rom-user store failed: " + ex.Message); }
    }

    // ── Collections ───────────────────────────────────────────────────────────

    public static HttpResponse Collections(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.CollectionsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        var list = new List<object> { FavoritesDto(st, identity?.TokenId) };
        foreach (var pl in Playlists())
        {
            var dto = PlaylistDto(pl, st, identity?.TokenId);
            if (dto != null) list.Add(dto);
        }
        return RommApi.Json(list.ToArray());
    }

    public static HttpResponse CollectionById(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.CollectionsRead, out var identity);
        if (refused != null) return refused;

        var st = RommLibrary.Parental(ctx.Request);
        int id = ctx.GetRouteInt("id", -1);
        var name = RommIdMap.CollectionNameOf(id);
        if (name == null) return RommApi.Error(404, "Collection not found");

        if (string.Equals(name, FavoritesName, StringComparison.OrdinalIgnoreCase))
            return RommApi.Json(FavoritesDto(st, identity?.TokenId));

        var pl = Playlists().FirstOrDefault(p => Safe(() => p.Name) == name);
        var dto = pl == null ? null : PlaylistDto(pl, st, identity?.TokenId);
        return dto == null ? RommApi.Error(404, "Collection not found") : RommApi.Json(dto);
    }

    /// <summary>POST /api/collections/{id}/roms {rom_ids:[…]} — membership add. Only Favorites is
    /// writable, and adding to it IS setting IGame.Favorite.</summary>
    public static HttpResponse AddToCollection(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return RommApi.Error(405, "Method not allowed");
        return Membership(ctx, add: true, romIdFromRoute: null);
    }

    /// <summary>DELETE /api/collections/{id}/roms/{rom_id} — membership remove.</summary>
    public static HttpResponse RemoveFromCollection(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
            return RommApi.Error(405, "Method not allowed");
        return Membership(ctx, add: false, romIdFromRoute: ctx.GetRouteInt("rom_id", -1));
    }

    private static HttpResponse Membership(RouteContext ctx, bool add, int? romIdFromRoute)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.CollectionsWrite, out var identity);
        if (refused != null) return refused;

        int id = ctx.GetRouteInt("id", -1);
        var name = RommIdMap.CollectionNameOf(id);
        if (name == null) return RommApi.Error(404, "Collection not found");
        if (!string.Equals(name, FavoritesName, StringComparison.OrdinalIgnoreCase))
            return RommApi.Error(403, "LaunchBox playlists are managed in LaunchBox; only Favorites is writable here");

        var romIds = new List<int>();
        if (romIdFromRoute is int r && r > 0) romIds.Add(r);
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(ctx.Request!.Body);
                if (doc.RootElement.TryGetProperty("rom_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    romIds.AddRange(arr.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()));
                else if (doc.RootElement.TryGetProperty("rom_id", out var one) && one.TryGetInt32(out var o))
                    romIds.Add(o);
            }
            catch { return RommApi.Error(400, "Malformed body"); }
        }
        if (romIds.Count == 0) return RommApi.Error(400, "No rom ids provided");

        bool any = false;
        foreach (var romId in romIds)
        {
            var game = RommLibrary.GameByRomId(romId);
            if (game == null) continue;
            try { game.Favorite = add; any = true; } catch { }
        }
        if (any)
        {
            try { PluginHelper.DataManager.Save(false); }
            catch (Exception ex) { LbLog.Warn("romm", "library save failed: " + ex.Message); }
        }

        var st = RommLibrary.Parental(ctx.Request);
        return RommApi.Json(FavoritesDto(st, identity?.TokenId));
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    // The CLIENT matters here too: a collection must not name a rom the client cannot see, nor hand it
    // the default id when it is pinned to another file.
    private static object FavoritesDto(WebParentalState? st, int? tokenId)
    {
        var romIds = new List<int>();
        foreach (var p in RommLibrary.Platforms(st))
            foreach (var g in RommLibrary.GamesOf(p.LbName, st, tokenId))
                if (RommLibrary.FavoriteOf(g)) romIds.Add((int)RommRoms.RomIdFor(g, tokenId));
        return CollectionDto(RommIdMap.CollectionId(FavoritesName), FavoritesName,
            "Your LaunchBox favorites", romIds, isFavorite: true);
    }

    private static object? PlaylistDto(IPlaylist pl, WebParentalState? st, int? tokenId)
    {
        var name = Safe(() => pl.Name);
        if (string.IsNullOrEmpty(name)) return null;

        var romIds = new List<int>();
        try
        {
            foreach (var g in pl.GetAllGames(true) ?? Array.Empty<IGame>())
            {
                if (g == null) continue;
                if (st != null && (st.IsHidden(RommLibrary.PlatformOf(g)) || !st.IsRatingAllowed(RommLibrary.EsrbOf(g)))) continue;
                if (RommLibrary.HiddenOf(g) && !RommConfig.ExposeHiddenGames) continue;
                romIds.Add((int)RommRoms.RomIdFor(g, tokenId));
            }
        }
        catch { }
        if (romIds.Count == 0) return null;

        return CollectionDto(RommIdMap.CollectionId(name!), name!, "", romIds, isFavorite: false);
    }

    private static object CollectionDto(int id, string name, string description, List<int> romIds, bool isFavorite) => new
    {
        id,
        name,
        description,
        rom_ids = romIds,
        rom_count = romIds.Count,
        path_cover_small = (string?)null,
        path_cover_large = (string?)null,
        path_covers_small = Array.Empty<string>(),
        path_covers_large = Array.Empty<string>(),
        is_public = true,
        is_favorite = isFavorite,
        is_virtual = false,
        is_smart = false,
        created_at = RommAuthApi.Iso(DateTime.UnixEpoch),
        updated_at = RommAuthApi.Iso(DateTime.UtcNow),
        url_cover = (string?)null,
        user_id = RommAuthApi.UserId,
        owner_username = RommConfig.Username,
    };

    private static List<IPlaylist> Playlists()
    {
        try
        {
            return (PluginHelper.DataManager.GetAllPlaylists() ?? Array.Empty<IPlaylist>())
                .Where(p => p != null).ToList();
        }
        catch { return new List<IPlaylist>(); }
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
}
