// The art a 3D case can consume — the five slots, resolved ONCE per game.
//
// The cache KEY and the BUILDER used to answer the same question independently: the key resolved five
// slots to describe what would be consumed, then threw the paths away; the builder resolved them again
// to actually load them. Two answers to one question, and the invariant that keeps a cached model from
// re-baking on every display is that they agree EXACTLY — a key describing an image the builder does not
// load never matches the key recomputed at the next display. Keeping two call sites in step by hand is a
// guarantee that only holds while nobody edits one of them.
//
// So the question is asked once, here, and the answer is carried (Model3dCache.Identity → the builders).
// MediaResolver.Image is the single door: it serves the game cache when it is up and walks the disk when
// it is not, and this code does not know which — which is what makes VALIDATING an existing model cost a
// few dictionary lookups instead of forty directory probes.
//
// The paths, not the decoded images: two derivations read the FILE PATH rather than its pixels — the
// LaunchBox region folder an image sits in (LbCaseObj.RegionOfImagePath), which drives the Auto-Detect
// spine version. Carrying bitmaps would lose that, and would decode art a given case type never uses.

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Model3d;

/// <summary>The resolved art sources for one model build. <c>FullScan</c> = the sheet mode applies to this
/// game (option or image-override pick) — the builders still arbitrate it against the spine scan.</summary>
internal sealed record Model3dArt(string? Front, string? Logo, string? Spine, string? Back, string? Full,
                                  bool FullScan)
{
    /// <summary>Nothing resolved — the bare case a builder falls back to when there is no game and no
    /// platform to resolve against.</summary>
    public static readonly Model3dArt None = new(null, null, null, null, null, false);

    /// <summary>Resolve every slot for one game. <paramref name="id"/> = the game's Guid (Guid.Empty for the
    /// platform-settings preview, which has only a sample title — MediaResolver falls back on its own).
    /// <paramref name="ov"/> = the effective per-slot image override (Edit Game → Image Selection).</summary>
    public static Model3dArt Resolve(Dictionary<string, string>? map, string? platform, Guid id, string? title,
                                     Dictionary<string, string>? ov)
    {
        bool fullScan = IsFullScan(map, ov);
        return new Model3dArt(
            Slot(ov, "front", platform, id, title, Media.MediaResolver.FrontChain()),
            Slot(ov, "logo", platform, id, title, Media.MediaResolver.ClearLogo),
            Slot(ov, "spine", platform, id, title, new[] { "Box - Spine" }),
            Slot(ov, "back", platform, id, title, Media.MediaResolver.BackChain()),
            // Only ever resolved when the sheet mode applies TO THIS GAME: off, the slot is not a source the
            // builders can reach, so it must not enter the key either (an unrelated Box - Full landing on
            // disk would re-key — and re-bake — a model it cannot change).
            fullScan ? Slot(ov, "full", platform, id, title, new[] { "Box - Full" }) : null,
            fullScan);
    }

    /// <summary>Full-scan mode: the UseFullScanImages option, or an image override that picked a full scan
    /// (picking one implies the mode). The single expression of the rule — key and builders read it here.</summary>
    private static bool IsFullScan(Dictionary<string, string>? map, Dictionary<string, string>? ov)
        => (map != null && map.TryGetValue("UseFullScanImages", out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase))
           || (ov != null && ov.ContainsKey("full"));

    // ── Per-slot resolution with the image-override layer (Edit Game → Image Selection tab) ──
    //   • slot forced in the override → that exact file;
    //   • override selects a FULL SCAN → front/spine/back are SUPPRESSED (the sheet replaces the three;
    //     without this an auto-resolved spine scan would win the full-scan arbiter over the user's pick);
    //   • otherwise → the automatic type→region→number resolution.
    private static string? Slot(Dictionary<string, string>? ov, string slot, string? platform, Guid id,
                                string? title, string[] typeChain)
    {
        if (ov != null)
        {
            if (ov.TryGetValue(slot, out var p) && !string.IsNullOrEmpty(p)) return p;
            if (ov.ContainsKey("full") && slot is "front" or "spine" or "back") return null;
        }
        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(title)) return null;
        try { return Media.MediaResolver.Image(platform!, id, title!, typeChain); } catch { return null; }
    }
}
