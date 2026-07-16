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
    Editor,             // game / media editor + Download Medias
    Similar,            // similar-games suggestions
    RetroAchievements,  // per-ROM RA hashing with our RAHasher
    Parental,           // PIN parental control
    Web,                // embedded web frontend (LaunchBox / BigBox Web)
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
        new(LbModule.Editor, "editor", "Game and media editor",
            "The per-game metadata editor and \"Download Medias\", backed by the extended database.", false, false),
        new(LbModule.Rom,    "rom",    "ROM extractor",
            "Archive ROM extraction and the ArchiveMGS on-select handling.", false, false),
        new(LbModule.RetroAchievements, "retroachievements", "RetroAchievements",
            "Per-ROM RetroAchievements hashing computed with our own RAHasher instead of LaunchBox's scan-on-select.", false, false),
        new(LbModule.Similar, "similar", "Similar games",
            "Similar-games suggestions from the extended database.", false, false),
        new(LbModule.Parental, "parental", "Parental control",
            "A PIN gate for restricted games and platforms.", false, false),
        new(LbModule.Web,    "web",    "Web frontend",
            "The embedded web server that serves LaunchBox Web / BigBox Web from LiteBox.", false, false),
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
