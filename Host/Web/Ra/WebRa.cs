// S5 — RetroAchievements web GLUE. Surfaces RA state (progress + per-achievement badges) in the theme
// detail.json, sourced ENTIRELY from LiteBox's already-native RA subsystem (Host/Ra) — NOT a re-port of
// ExtendDB's RetroAchievementsDb / RaScanner / RaCatalog.
//
// Data path (identical to the WinForms RetroAchievementsCard, MainWindow.LoadRaPanel):
//   • the game's raid (RetroAchievementsId) comes from RaFields.Raid(game)  (ILiteBoxFields <Game> XML),
//   • the achievement set + this user's unlock state come from RaService's on-disk cache
//     (Core\litebox\ra-cache\<raid>.json), fetched/normalised from the PUBLIC RA Web API,
//   • the "beat / master" medians come from the cache, falling back to the XML (RaFields.ReadMedians),
//   • each achievement badge PNG is served by BadgeHandle from RaBadges' disk cache (Core\ra-badges\).
//
// Non-blocking: the JSON is built from the CACHE only (disk read) so an HTTP handler never waits on the
// network. A background RaService.EnsureAndRead is fired to keep the cache fresh for this + the next load
// (freshness-gated + single-flight inside RaService, exactly like the native card's cache-first pattern).
//
// Gate: mirrors how LiteBox decides the RA panel is "on" for DISPLAY (MainWindow.LoadRaPanel) — a block is
// emitted only when RaService.Configured (RA key + username in Settings.xml) AND the game has a raid AND
// there is cached achievement data. Any of these missing ⇒ no `ra` field, and the theme simply shows no RA
// panel (graceful degrade). No secret literal: the key/username live in RaService (LB Settings.xml).

#nullable enable

using System;
using System.IO;
using System.Linq;
using LbApiHost.Host.Ra;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

/// <summary>Builds the RetroAchievements JSON block for a game's detail.json from Host/Ra, and serves the
/// per-achievement badge PNGs it references. See file header.</summary>
internal static class WebRa
{
    /// <summary>URL base for the per-achievement badge PNGs this block references (served by
    /// <see cref="BadgeHandle"/> at /api/ra/badge/{name}.png).</summary>
    private const string BadgeRoute = "/api/ra/badge/";

    /// <summary>The `ra` object for a game's detail.json, or null when RA isn't configured, the game has no
    /// raid, or nothing is cached yet (⇒ omit the field; the theme shows no RA panel).</summary>
    public static object? Block(IGame? game)
    {
        try
        {
            if (game == null) return null;
            if (!RaService.Configured) return null;      // no RA key/username → same display gate as the card
            int raid = RaFields.Raid(game);
            if (raid <= 0) return null;                  // game isn't RA-scored (no RetroAchievementsId)

            Warm(game, raid);                            // keep the cache fresh (freshness-gated, single-flight)

            var c = RaService.ReadCache(raid);           // disk-only, non-blocking
            if (c == null || c.total <= 0) return null;  // nothing usable cached yet → warm will fill it

            var (xmlBeat, xmlMaster) = RaFields.ReadMedians(game);   // XML fallback; cache medians preferred
            int beat = c.beatMin > 0 ? c.beatMin : xmlBeat;
            int master = c.masterMin > 0 ? c.masterMin : xmlMaster;

            var achievements = c.achievements.Select(a => new
            {
                id = a.id,
                title = a.title,
                desc = a.description,
                points = a.points,
                badge = a.badge,
                badgeUrl = BadgeUrl(a.badge, true),
                badgeLockedUrl = BadgeUrl(a.badge, false),
                unlocked = a.unlocked,
                unlockedHardcore = a.unlockedHardcore,
                order = a.order,
            }).ToArray();

            return new
            {
                gameId = c.gameId > 0 ? c.gameId : raid,
                title = c.title,
                icon = c.imageIcon,
                numAwarded = c.unlocked,
                numAwardedHardcore = c.unlockedHardcore,
                numPossible = c.total,
                completion = c.completion,
                beatenSoftcore = c.beatenSoftcore,
                beatenHardcore = c.beatenHardcore,
                beatMinutes = beat,
                masterMinutes = master,
                beatSamples = c.beatSamples,
                masterSamples = c.masterSamples,
                achievements,
            };
        }
        catch { return null; }
    }

    /// <summary>GET /api/ra/badge/{name}.png — one achievement badge PNG from RaBadges' disk cache
    /// (downloaded once on demand by Host/Ra, never bundled). {name} is the RA badge id, optionally with the
    /// "_lock" suffix for the greyed/locked variant. 404 when absent/unresolvable.</summary>
    public static HttpResponse BadgeHandle(RouteContext ctx)
    {
        var raw = ctx.GetRoute("name") ?? "";
        string name; try { name = Uri.UnescapeDataString(raw); } catch { name = raw; }
        if (string.IsNullOrWhiteSpace(name)) return HttpResponse.NotFound();

        // RA convention: "<id>_lock" = locked (greyed) badge, "<id>" = unlocked (coloured) badge.
        bool unlocked = !name.EndsWith("_lock", StringComparison.OrdinalIgnoreCase);
        string badge = unlocked ? name : name.Substring(0, name.Length - "_lock".Length);
        // Badge ids are alphanumeric — reject anything else so a crafted name can't traverse the cache dir.
        if (badge.Length == 0 || !badge.All(char.IsLetterOrDigit)) return HttpResponse.NotFound();

        string? path;
        try { path = RaBadges.Get(badge, unlocked); } catch { path = null; }
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return HttpResponse.NotFound();

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return HttpResponse.ServerError("read error"); }

        var resp = HttpResponse.Bytes(bytes, "image/png");
        resp.Headers["Cache-Control"] = "max-age=86400";
        return resp;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────
    private static string? BadgeUrl(string? badge, bool unlocked)
    {
        if (string.IsNullOrWhiteSpace(badge)) return null;
        var name = unlocked ? badge! : badge + "_lock";
        return BadgeRoute + Uri.EscapeDataString(name) + ".png";
    }

    // Fire-and-forget cache warm — freshness-gated + single-flight per raid inside RaService, so a fresh
    // cache is a cheap no-op and a played-since-cached game refetches (mirrors LoadRaPanel's EnsureAndRead).
    private static void Warm(IGame game, int raid)
    {
        DateTime lp = LastPlayedUtc(game);
        System.Threading.Tasks.Task.Run(() => { try { RaService.EnsureAndRead(raid, lp); } catch { } });
    }

    private static DateTime LastPlayedUtc(IGame game)
    {
        try { var d = game.LastPlayedDate; return d?.ToUniversalTime() ?? DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }
}
