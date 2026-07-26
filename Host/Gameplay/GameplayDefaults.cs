// The GLOBAL defaults of the LiteBox-own gameplay options — their single source of truth, and the
// boot pass that guarantees every one is present in LiteBox.ini with a VISIBLE value.
//
// Rule (Mehdi): no hidden keys we define only in code. Every gameplay global must sit in the ini
// with a default a user can find and edit — including the ones no options page writes today
// (SmartCaptureShowBorder). This class is where that guarantee lives.
//
// NOT here: the per-ENTITY overrides (game/emulator) — those legitimately live in the options DB
// (EAV, entity-keyed). And the two inherit-from-LaunchBox hotkeys (PauseHotkey / ScreenCaptureKey):
// their correct default is "absent = inherit LB's configured key", which no fixed ini value can
// express (an empty value means "disabled", a real value freezes it) — never seeded.

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Gameplay;

internal static class GameplayDefaults
{
    /// <summary>Every gameplay-global key with its default, in ini form. This is what Seed writes when
    /// a key is absent, and the full set of names WriteTemplate would document. Order = ini appearance.</summary>
    public static IReadOnlyList<(string Key, string Default)> Defaults => _defaults;

    private static readonly (string Key, string Default)[] _defaults = BuildDefaults();

    private static (string Key, string Default)[] BuildDefaults()
    {
        var d = new List<(string, string)>
        {
            // Exit / end screen
            ("ExitScreenEagerMs",              "-1"),          // -1 = disabled
            // Controller pause
            ("PadPauseEnabled",                "false"),
            ("PadPauseButton",                 "Back+Start"),
            // Pause freeze target + timing
            ("PauseTarget",                    "smartcapture"),// smartcapture | process
            ("PauseFreezeTree",                "false"),
            ("PauseScreenFreezeTiming",        "before"),      // before | after
            ("PauseScreenFreezeOffsetMs",      "0"),
            // Pause-exit force-kill (mode:seconds)
            ("PauseExitKill",                  GameplaySettings.PauseExitKillDefaultMode + ":" + GameplaySettings.PauseExitKillDefaultSeconds),
            ("PauseExitKillAhk",               GameplaySettings.PauseExitKillDefaultMode + ":" + GameplaySettings.PauseExitKillAhkDefaultSeconds),
            // SmartCapture
            ("SmartCaptureEnabled",            "true"),
            ("SmartCaptureUseFps",             "true"),
            ("SmartCaptureUseSize",            "true"),
            ("SmartCaptureCombine",            "and"),         // and | or
            ("SmartCaptureMinFps",             "25"),
            ("SmartCaptureSustainMs",          "600"),
            ("SmartCaptureMinSizePct",         "50"),
            ("SmartCaptureTitle",              ""),            // empty = no title filter
            ("SmartCaptureStopOnWindowClose",  "false"),
            ("SmartCaptureShowBorder",         "false"),       // was a hidden opt-in; now visible in the ini
            ("SmartCaptureIgnoreExes",         ""),            // empty = use the built-in store-client blacklist
        };
        // Stay-on-top GLOBAL default is split per launch category (Emulator ON, everything else OFF).
        foreach (var (cat, _) in GameplaySettings.StayOnTopCategories)
            d.Add((GameplaySettings.StayOnTopIniKey(cat), GameplaySettings.StayOnTopDefault(cat) ? "true" : "false"));
        return d.ToArray();
    }

    /// <summary>Guarantee every gameplay global is present in <paramref name="ini"/> with a visible
    /// value. One boot pass; idempotent — once seeded the keys exist, so it only ever writes on a
    /// fresh install or when a new key is added to <see cref="Defaults"/>. Never throws.</summary>
    public static void Seed(LiteBoxConfig ini)
    {
        if (ini == null) return;
        try
        {
            bool dirty = false;
            int seeded = 0;
            foreach (var (key, def) in _defaults)
                if (ini.Get(key) == null) { ini.Set(key, def); dirty = true; seeded++; }
            if (dirty)
            {
                ini.Save();
                Console.WriteLine($"[gameplay-defaults] ini seeded ({seeded} default(s))");
            }
        }
        catch (Exception ex) { Console.WriteLine("[gameplay-defaults] seed failed: " + ex.Message); }
    }
}
