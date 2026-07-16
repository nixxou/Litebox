// R5 — Select-ROM web routes (the archive-entries + archive-favorite verbs) for BOTH themes.
//
//   GET  /bigbox|launchbox/api/games/{id}/archive-entries[?appId=…]   → the archive's playable entries
//   POST /bigbox|launchbox/api/games/{id}/archive-favorite            → pin/unpin an in-archive entry
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Theme/ArchiveListingApi.cs. The listing REUSES the ONE
// native listing+scoring impl (RomExtractor.ListEntriesDetailed) — the SAME scored order + ↻★🏆 markers as
// the GUI dropdown/picker, so the surfaces can never drift (memory: rom-list-surfaces-sync). No archive is
// re-listed or re-scored here: the analyzer/listing-cache live behind RomExtractor.
//
// The JSON shape is byte-compatible with what the shipped theme JS parses (engine/app.js openSelectRomMenu,
// launchbox/app.js lbFetchArchiveEntries):
//   { ok:true, archivePath, key, signature,
//     entries:[ { fileName, pathInArchive, size, isFavorite, isLastPlayed, retroAchievements } ] }
// The favourite POST body is { entry, value, appId? } and answers { ok:true, isFavorite:value }.
//
// Gate: RomExtractor.Available (== LbModules.On(LbModule.Rom)). When the module is off the handlers answer
// {ok:false,reason} with 404 and the theme simply hides its Select-ROM sub-menu (graceful degrade). The
// routes are only registered inside the per-site EnableBigBoxWeb/EnableLiteBoxWeb blocks. No reflection,
// no secret literal.

#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class ArchiveListingApi
{
    // ── GET archive-entries ───────────────────────────────────────────────────
    public static HttpResponse Handle(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.PlainText("Method not allowed", 405);
        try
        {
            if (!RomExtractor.Available) return Fail("feature disabled", 404);

            var id = ctx.GetRoute("id");
            if (string.IsNullOrEmpty(id)) return Fail("bad id");
            var game = ResolveGame(id);
            if (game == null) return Fail("not in library");

            var appId = ctx.Request?.GetQuery("appId");
            var listing = RomExtractor.ListEntriesDetailed(game, appId);
            if (string.IsNullOrEmpty(listing.ArchivePath))
                return Fail("not an archive", 404);   // no on-disk recognised archive for this game/version

            var body = JsonSerializer.Serialize(new
            {
                ok = true,
                archivePath = listing.ArchivePath,
                key = listing.Key,
                signature = listing.ShortSignature,
                entries = listing.Entries.Select(e => new
                {
                    fileName = e.FileName,
                    pathInArchive = e.PathInArchive,
                    size = e.Size,
                    isFavorite = e.IsFavorite,
                    isLastPlayed = e.IsLastPlayed,
                    retroAchievements = e.RaTitle,   // "" natively — per-entry RA titles aren't ported (see header)
                }),
            });
            Log($"entries id={id} appId={appId ?? "<main>"} → {listing.Entries.Count} entry(ies)");
            return HttpResponse.Json(body);
        }
        catch (Exception ex) { Log("entries threw: " + ex.Message); return Fail("server error: " + ex.Message); }
    }

    // ── POST archive-favorite ──────────────────────────────────────────────────
    // Body { entry, value, appId? } — resolve the archive's short signature the same way as the listing
    // and flip the favourite via ArchiveHistory. Theme-agnostic (wired under both /launchbox and /bigbox).
    public static HttpResponse HandleFavorite(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.PlainText("Method not allowed", 405);
        try
        {
            if (!RomExtractor.Available) return Fail("feature disabled", 404);

            var id = ctx.GetRoute("id");
            if (string.IsNullOrEmpty(id)) return Fail("bad id");
            var game = ResolveGame(id);
            if (game == null) return Fail("not in library");

            var bodyStr = ctx.Request?.Body;
            var entry = JsonStr(bodyStr, "entry");
            if (string.IsNullOrEmpty(entry)) return Fail("missing entry");
            var appId = JsonStr(bodyStr, "appId");
            bool value = JsonBool(bodyStr, "value");

            var shortSig = RomExtractor.ResolveArchiveShortSig(game, appId);
            if (string.IsNullOrEmpty(shortSig)) return Fail("no signature");

            ArchiveHistory.ToggleFavorite(shortSig, entry, value);
            Log($"favorite {(value ? "set" : "unset")} sig={shortSig} entry=\"{entry}\"");
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, isFavorite = value }));
        }
        catch (Exception ex) { Log("favorite threw: " + ex.Message); return Fail("error: " + ex.Message); }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static HttpResponse Fail(string reason, int status = 200)
        => HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason }), status);

    /// <summary>Opaque id → IGame: LaunchBox GUID (GetGameById) or the metadata DatabaseID matched against
    /// IGame.LaunchBoxDbId. Mirrors the other native theme handlers.</summary>
    internal static IGame? ResolveGame(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Guid.TryParse(id, out _))
        {
            try { var g = PluginHelper.DataManager.GetGameById(id); if (g != null) return g; }
            catch { }
        }
        if (int.TryParse(id, out var dbId) && dbId > 0)
        {
            IGame[]? all;
            try { all = PluginHelper.DataManager.GetAllGames(); } catch { return null; }
            if (all == null) return null;
            foreach (var g in all)
                try { if (g?.LaunchBoxDbId is int lid && lid == dbId) return g; } catch { }
        }
        return null;
    }

    private static string? JsonStr(string? body, string key)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(key, out var el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch { return null; }
    }

    private static bool JsonBool(string? body, string key)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(key, out var el)) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.Number) return el.GetDouble() != 0;
            return false;
        }
        catch { return false; }
    }

    private static void Log(string msg) => LbLog.Info("web", "[archive] " + msg);
}
