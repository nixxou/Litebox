// The GLOBAL defaults of the LiteBox-own gameplay options — their single source of truth, and the
// boot pass that guarantees every one is present in LiteBox.ini with a VISIBLE value.
//
// Rule (Mehdi): no hidden keys we define only in code. Every gameplay global must sit in the ini
// with a default a user can find and edit — including the ones no options page writes today
// (SmartCaptureShowBorder). This class is where that guarantee lives.
//
// It also carries the REVERSE migration for the short-lived R2 phase-A experiment, when these
// globals briefly lived in litebox-options.db (scope=global): any leftover global row is pulled
// back into the ini (preserving the user's customised value) and the row is dropped. The keys are
// no longer declared at global scope, so the drain uses LiteBoxOptionsDb.DrainGlobalKeys, which
// bypasses the namespace check on purpose.
//
// NOT here: the per-ENTITY overrides (game/emulator) — those legitimately live in the options DB
// (EAV, entity-keyed). And the two inherit-from-LaunchBox hotkeys (PauseHotkey / ScreenCaptureKey):
// their correct default is "absent = inherit LB's configured key", which no fixed ini value can
// express (an empty value means "disabled", a real value freezes it). They are drained back if a
// phase-A row exists, but never seeded with a default — see the note in Seed().

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

    // The two inherit-from-LB hotkeys: drained back if a phase-A row exists, but NEVER seeded with a
    // fixed default (absent = inherit LaunchBox's key — no ini value can mean that).
    private static readonly string[] _inheritHotkeys = { "PauseHotkey", "ScreenCaptureKey" };

    /// <summary>Guarantee every gameplay global is present in <paramref name="ini"/> with a visible
    /// value, and reverse-migrate any leftover options-DB global row (R2 phase-A) back into it.
    /// One boot pass; call right after LiteBoxOptionsDb.Open() and before anything resolves a launch.
    /// Idempotent: once seeded, the keys exist, so it only ever writes on a fresh/updated install or
    /// the one boot after the phase-A revert. Never throws.</summary>
    public static void Seed(LiteBoxConfig ini)
    {
        if (ini == null) return;
        try
        {
            // Reverse-migrate phase-A leftovers first, so a user's customised value wins over the
            // default. Drain the full set (defaults + the two hotkeys) — DrainGlobalKeys bypasses the
            // namespace check because these are no longer declared at global scope. No-op when the DB
            // is closed or holds no such rows.
            var drainKeys = new List<string>(_defaults.Length + _inheritHotkeys.Length);
            foreach (var (k, _) in _defaults) drainKeys.Add(k);
            drainKeys.AddRange(_inheritHotkeys);
            var drained = Data.LiteBoxOptionsDb.DrainGlobalKeys(drainKeys);

            bool dirty = false;
            int migrated = 0, seeded = 0;

            // Every declared default: a drained value (user's own) beats the default; else write the
            // default when the key is absent from the ini.
            foreach (var (key, def) in _defaults)
            {
                if (drained.TryGetValue(key, out var mv))
                {
                    ini.Set(key, Unsentinel(mv)); dirty = true; migrated++;
                }
                else if (ini.Get(key) == null)
                {
                    ini.Set(key, def); dirty = true; seeded++;
                }
            }
            // The inherit hotkeys: only restore a drained value; never seed a default (stay absent = inherit).
            foreach (var key in _inheritHotkeys)
                if (drained.TryGetValue(key, out var mv)) { ini.Set(key, Unsentinel(mv)); dirty = true; migrated++; }

            if (dirty)
            {
                ini.Save();
                Console.WriteLine($"[gameplay-defaults] ini seeded ({seeded} default(s), {migrated} reverse-migrated from the options DB)");
            }
        }
        catch (Exception ex) { Console.WriteLine("[gameplay-defaults] seed failed: " + ex.Message); }
    }

    // Phase A stored a deliberately-cleared hotkey as the "None" sentinel (the DB deletes empty rows).
    // The ini stores "" natively, so translate the sentinel back on the way in.
    private static string Unsentinel(string v)
        => string.Equals(v, Data.LiteBoxOption.Disabled, StringComparison.Ordinal) ? "" : v;
}
