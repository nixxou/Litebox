// The LiteBox module registry — the single source of truth for "is module X enabled?".
//
// Mirrors ExtendDB's module model (independently toggleable features) but native to LiteBox: state lives in
// litebox-options.db (global scope, key "Module.<key>"), default OFF, and the enable flag gates a FEATURE, not
// any shared low-level plumbing. The catalog (titles + descriptions) drives the Options → Modules page.
//
// Logging is deliberately NOT here (unlike ExtendDB, where the dedupe log lived inside Modules) — see
// Host/Diag/LbLog. Modules only answer on/off and describe themselves.

#nullable enable

using System;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Modules;

/// <summary>The functional modules ported from ExtendDB. Caching stays a GLOBAL option, never a module.</summary>
internal enum LbModule
{
    Base,               // extended metadata DB + credentialed media download
    Rom,                // ROM extractor / ArchiveMGS
    RetroAchievements,  // per-ROM RA hashing with our RAHasher
    Parental,           // parental control on BigBox's native PIN
    Web,                // embedded web frontends (LiteBox Web / BigBox Web / database Web)
    Monitors,           // monitor profiles: layout / display mode / sound card, switchable from Tools
    Rules,              // launch rules: BigBoxProfile's sondes & actions on the command line, per entity
}

internal static class LbModules
{
    internal readonly record struct Info(LbModule Module, string Key, string Title, string Description, bool DefaultOn, bool Ready);

    /// <summary>Display order + metadata for the Modules options page. <c>Ready=false</c> = ported but the
    /// implementation is still landing (shown, toggle persists, feature no-op until the port completes).</summary>
    public static readonly Info[] Catalog =
    {
        new(LbModule.Base,   "base",   "Extended metadata database",
            "A second metadata database (ScreenScraper, VNDB, IGDB, Steam) with richer overviews, locked fields, and credentialed media downloads.", false, true),
        new(LbModule.Rom,    "rom",    "ROM extractor (ArchiveMGS)",
            "Extract the picked ROM from an archive on launch, with the Archive Multi-Game Selector: per-emulator/platform profiles, extraction modes, an LRU disk cache, RAM disk, disc-image conversion, texture-pack and .m3u handling — all native. Off: archives fall back to a flat extract-everything launch.", false, true),
        // RetroAchievements is NOT a module: it has its own dedicated options page (like Similar Games)
        // and no activation flag — the feature is always operational. The enum member stays only so
        // historical "Module.retroachievements" db rows keep a stable key namespace.
        new(LbModule.Parental, "parental", "Parental control",
            "A PIN gate for restricted content, using BigBox's own parental PIN (set or remove it here — BigBox sees the change).", false, true),
        new(LbModule.Web,    "web",    "Web frontends",
            "The embedded web server: LiteBox Web, BigBox Web and the database Web, each served from its own folder.", false, true),
        new(LbModule.Monitors, "monitors", "Monitor profiles",
            "Named desktop presets — monitor layout (position, resolution, refresh, rotation, per-screen zoom), a single monitor's display mode, the default sound card and its volume, or a solo-primary blackout. Switch between them from Tools. Off: the Tools entry is hidden and nothing touches the display.", false, true),
        new(LbModule.Rules, "rules", "Launch rules",
            "BigBoxProfile's probes & actions, native: ordered rules attached to an EMULATOR rewrite the command line right before the spawn, guarded by filters — per-game targeting via marker arguments in the game's custom parameters, stripped before the emulator sees them. Ported action by action — today: Prefix, Suffix, Change exe, Change rom path, Replace (with a variables system), Replace in file, Create file, HID device detector. Off: launches run untouched.", false, true),
    };

    public static Info Meta(LbModule m) => Catalog.First(c => c.Module == m);
    public static string Key(LbModule m) => Meta(m).Key;

    /// <summary>True when the module is enabled (litebox-options.db global "Module.&lt;key&gt;"; default = its
    /// catalog DefaultOn). A future per-emulator / per-game override could layer on top, like the other options.</summary>
    public static bool On(LbModule m)
    {
        try
        {
            var v = LiteBoxOptionsDb.GetGlobal("Module." + Key(m));
            if (string.IsNullOrEmpty(v)) return Meta(m).DefaultOn;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return Meta(m).DefaultOn; }
    }

    /// <summary>Persist the on/off flag (null clears it back to the default).</summary>
    public static void SetOn(LbModule m, bool on)
    {
        try { LiteBoxOptionsDb.SetGlobal("Module." + Key(m), on == Meta(m).DefaultOn ? null : (on ? "true" : "false")); }
        catch { }
    }

    /// <summary>Boot recap: "[modules] ON: base, editor | OFF: rom, web, …" — one generic tagged line.</summary>
    public static void LogState()
    {
        try
        {
            var on = Catalog.Where(c => On(c.Module)).Select(c => c.Key);
            var off = Catalog.Where(c => !On(c.Module)).Select(c => c.Key);
            LbLog.Info("modules", $"ON: {string.Join(", ", on).PadRight(1)} | OFF: {string.Join(", ", off)}");
        }
        catch { }
    }
}
