// Every LiteBox-own GLOBAL option must exist in LiteBox.ini with a visible value — no key we only know
// in code (Mehdi's rule, first applied to the gameplay globals in Gameplay/GameplayDefaults.cs).
//
// The ini TEMPLATE only covers a fresh install: any option added after a user's ini was created stayed
// invisible until they toggled it once. This boot pass closes that gap for the options the template
// doesn't write, and for everything added later.
//
// NOT seeded, on purpose:
//   • LaunchBox's own Settings.xml keys (UsePauseScreen, ExitDosBox, Language…) — not ours to define;
//   • UI STATE rather than options (ZoomPercent, PosterMode, MetaExpanded, Col.*, window geometry,
//     GenCacheSelection = the last Generate-Media-Cache selection) — written when the user changes them;
//   • PauseHotkey / ScreenCaptureKey — their correct default is "absent = inherit LaunchBox's key", which
//     no fixed value can express (empty means "disabled", a real value freezes it).

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Options;

internal static class GlobalDefaults
{
    /// <summary>Key → default, VERBATIM from each reader's fallback (seeding must never change behaviour).</summary>
    private static readonly (string Key, string Default)[] _defaults =
    {
        // Launching / startup
        ("CheckDependencies",               "true"),
        ("StartupProgressBar",              "true"),
        // Pause defaults for games with NO emulator (a per-game override always wins)
        ("NonEmuUsePauseScreen",            "true"),
        ("NonEmuSuspendOnPause",            "true"),
        ("NonEmuForcefulActivation",        "false"),
        ("WebReturnTiming",                 "behind"),      // behind | after | immediate
        // Progress automation triggers (the RULES live in LaunchBox; these choose WHEN we run them)
        ("ProgressSweepOnBoot",             "false"),
        ("ProgressApplyOnSelect",           "true"),
        // List / poster rendering
        ("AutoFitColumns",                  "true"),
        ("TwoLineRows",                     "true"),
        ("PosterOwnerDraw",                 "false"),
        ("TitleSortNormalization",          "simple"),      // without | simple | advanced
        ("RenameMediaWithGame",             "true"),        // always on; see LiteBoxConfig
        // Automatic cache cleaning (Options → Caches)
        ("CleanThumbsImages",               "true"),
        ("CleanThumbsVideo",                "true"),
        ("CleanThumbsRelated",              "true"),
        ("CleanThumbsWebImg",               "true"),
        ("CleanThumbsDocs",                 "true"),
        ("CleanThumbsBudget",               "true"),
        ("CleanModel3d",                    "true"),
        ("CleanOptionsDb",                  "true"),
        ("ThumbAlphaFormat",                "png"),         // png | webp
        // Viewer used when this LaunchBox has NO Reader (pre-14): empty = the file's default program.
        // With a Reader present it is LaunchBox's own setting that decides — see _retired below.
        ("ExternalReaderPath",              ""),
        // Videos in the right pane
        ("VideoAutoplay",                   "false"),
        ("VideoAutoplaySound",              "false"),
        // 3D case model: when is one worth showing AND baking (the front is always required)
        ("Model3dRequireBack",              "false"),
        ("Model3dRequireSpine",             "false"),
        ("Model3dRequireBothScans",         "false"),
        ("Model3dAcceptFullScan",           "false"),
        ("Model3dLbOracle",                 "false"),       // dev: LaunchBox comparison zone in Edit Platform
        ("Model3dAutoJewelCase",            "true"),        // Saturn/Sega CD: jewel case when the art fits it better
        ("Model3dAutoDoubleJewel",          "true"),        // multi-disc: double jewel when the spine scan measures one
        ("ImagesMatrixExpandScreenshots",   "false"),       // multi-edit Images: Screenshots split per type
    };

    /// <summary>Keys LiteBox no longer reads. Left behind in an existing ini they would look like live
    /// settings the user can tune, so the boot pass DELETES them — the same "no key we only know in
    /// code" rule, applied in reverse.
    ///
    /// UseLbReaderForDocs / LbReaderFullscreen: which viewer opens a document, and whether it opens
    /// fullscreen, are LaunchBox's own settings (Options → LB · Reader, stored in the Reader's
    /// database and shared by both apps). A LiteBox-side copy could only contradict them.</summary>
    private static readonly string[] _retired = { "UseLbReaderForDocs", "LbReaderFullscreen" };

    /// <summary>Write every missing key with its default, and drop the retired ones. One boot pass,
    /// idempotent — it only ever writes on a fresh install, when a new option joins the table, or
    /// once to clean a retired key. Never throws.</summary>
    public static void Seed(LiteBoxConfig ini)
    {
        if (ini == null) return;
        try
        {
            int seeded = 0;
            foreach (var (key, def) in _defaults)
                if (ini.Get(key) == null) { ini.Set(key, def); seeded++; }
            foreach (var key in _retired)
                if (ini.Get(key) != null) { ini.Remove(key); seeded++; Console.WriteLine($"[global-defaults] retired key removed: {key}"); }
            if (seeded > 0)
            {
                ini.Save();
                Model3d.Model3dOptions.Invalidate();   // the 3D snapshot may have read pre-seed values
                Console.WriteLine($"[global-defaults] ini seeded ({seeded} default(s))");
            }
        }
        catch (Exception ex) { Console.WriteLine("[global-defaults] seed failed: " + ex.Message); }
    }
}
