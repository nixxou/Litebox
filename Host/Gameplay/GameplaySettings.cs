// Effective gameplay-screen settings for a launch, resolved game → platform → global.
//
// Global lives in LaunchBox's Settings.xml (LB-owned field names, so LaunchBox keeps
// them on rewrite — see LbSettingsStore's strip caution):
//   UseStartupScreen, MinimumStartupScreenDisplayTime (ms), MinimumShutdownScreenDisplayTime (ms),
//   HideMouseCursorOnStartupScreens, UsePauseScreen, PauseScreenFading, PauseScreenMuting.
// The pause/screenshot HOTKEYS stay in LiteBox.ini (PauseHotkey / ScreenCaptureKey):
//   LaunchBox stores them as a single WPF-Key int (KeyboardGamePause), which can't
//   represent LiteBox's "Ctrl+F12"-style combos — so they're LiteBox-owned.
//
// Read FRESH each launch so option edits apply to the next game with no restart.
// Per-GAME overrides come from the LaunchedGame snapshot (captured pre-drop). A
// per-PLATFORM tier is reserved (no UI / source yet) — slot it in Resolve().

#nullable enable

using System.IO;
using System.Xml.Linq;

namespace LbApiHost.Host.Gameplay;

internal static class GameplaySettings
{
    private static string _lbRoot = "";
    public static void Configure(string lbRoot) => _lbRoot = lbRoot ?? "";

    public sealed class Resolved
    {
        public bool UseStartup;
        public int StartupMinMs;
        public int ShutdownMinMs;
        public bool HideCursor;
        public bool UsePause;
        public bool Fading;
        public bool Muting;
        public int LoadDelayMs;   // resolved LB "Startup Load Delay" (0 = unset) → SmartCapture reveal ceiling
    }

    /// <summary>Effective settings for <paramref name="snap"/> (the launching game), global
    /// values overridden by the game's per-game settings when present.</summary>
    public static Resolved Resolve(LaunchedGame? snap)
    {
        var d = ReadSettings();
        var r = new Resolved
        {
            UseStartup    = GBool(d, "UseStartupScreen"),
            // Display times use the 13.28 key names. Routed: shared in LB's XML on 13.28+,
            // in LiteBox's own DB on 13.27 (where those key names don't exist) — the boot
            // seed carried the pre-rename value over. Single mechanism, no version branching here.
            StartupMinMs  = GIntRouted(d, "StartupScreenPostLaunchDisplayTime"),
            ShutdownMinMs = GIntRouted(d, "ShutdownScreenPostReadyDisplayTime"),
            HideCursor    = GBool(d, "HideMouseCursorOnStartupScreens"),
            UsePause      = GBool(d, "UsePauseScreen"),
            Fading        = GBool(d, "PauseScreenFading"),
            Muting        = GBool(d, "PauseScreenMuting"),
        };

        // Emulator tier (LB Edit Emulator → Startup Screen): every emulator carries its own
        // copy seeded from the global defaults, so for a launch THROUGH an emulator these win
        // over the global — themselves overridden by the per-game override below. Direct /
        // store launches have no emulator captured, so the global stands.
        if (snap is { EmuCaptured: true })
        {
            r.UseStartup = snap.EmuUse;
            r.HideCursor = snap.EmuHideCursor;
            if (snap.EmuStartupMinMs >= 0) r.StartupMinMs = snap.EmuStartupMinMs;
            if (snap.EmuLoadDelay > 0) r.LoadDelayMs = snap.EmuLoadDelay;
            r.ShutdownMinMs = snap.EmuShutdownDisabled ? -1
                            : (snap.EmuShutdownMinMs >= 0 ? snap.EmuShutdownMinMs : r.ShutdownMinMs);
        }

        // Per-game override (LB "Override Default … Settings" on the Game) — top tier.
        if (snap is { StartupOverride: true })
        {
            r.UseStartup = snap.StartupUse;
            if (snap.StartupMinMs >= 0) r.StartupMinMs = snap.StartupMinMs;
            if (snap.StartupLoadDelay > 0) r.LoadDelayMs = snap.StartupLoadDelay;
            r.HideCursor = snap.StartupHideCursor;
            if (snap.ShutdownDisabled) r.ShutdownMinMs = -1;   // -1 ⇒ no end screen for this game
        }
        return r;
    }

    /// <summary>The SmartCapture "reveal anyway" ceiling (ms): how long to wait for a render before
    /// revealing the cover regardless. Resolved LB "Startup Load Delay" per-emulator/game (Emulators.xml)
    /// wins; else the LiteBox-managed GLOBAL default (LiteBox.ini StartupLoadDelay); else 5s.</summary>
    public static int RevealMaxMs(LaunchedGame? snap)
    {
        int d = 0; try { d = Resolve(snap).LoadDelayMs; } catch { }
        if (d > 0) return d;
        int g = 0; try { g = int.TryParse(LiteBoxConfig.LoadForExe().Get("StartupLoadDelay"), out var v) ? v : 0; } catch { }
        return g > 0 ? g : 5000;
    }

    /// <summary>Fade-out duration for a display screen: min(1s, displayMs), or 0 when LB's "Enable
    /// Fading" (PauseScreenFading) is off. The fade occupies the LAST fadeMs of the display window
    /// (total timing unchanged) so the startup / GAME OVER screen dissolves instead of popping.</summary>
    public static int FadeMs(LaunchedGame? snap, int displayMs)
    {
        bool on; try { on = Resolve(snap).Fading; } catch { on = false; }
        return on && displayMs > 0 ? Math.Min(1000, displayMs) : 0;
    }

    /// <summary>Pause hotkey string (LiteBox.ini, combo-capable). When the user never set one in
    /// LiteBox (key absent), INHERIT LaunchBox's own KeyboardGamePause (a WPF Key int) converted to
    /// our format — so someone coming from LaunchBox keeps their configured key. An explicit empty
    /// value in the ini (user cleared it) stays empty = disabled. Falls back to "Pause".</summary>
    public static string PauseKey()
    {
        try
        {
            var v = LiteBoxConfig.LoadForExe().Get("PauseHotkey");   // null = never set in LiteBox
            if (v != null) return v;
            return LbKeyToCombo(GIntXml("KeyboardGamePause", 7)) ?? "Pause";
        }
        catch { return "Pause"; }
    }

    /// <summary>True when the global pause-screen master switch is on (Settings.xml).</summary>
    public static bool PauseEnabledGlobal()
    {
        try { return GBool(ReadSettings(), "UsePauseScreen", true); }
        catch { return true; }
    }

    /// <summary>LB's "Force LaunchBox or Big Box back into focus when the shutdown screen
    /// closes" (Settings.xml ForceFrontendFocusOnShutdown — LB's own key, so the setting is
    /// shared with a real LaunchBox). Under LiteBox the frontend to refocus is the ExtendDB
    /// web kiosk when one is up (it relaunches after the game), else the LiteBox window.</summary>
    public static bool ForceFrontendFocusOnShutdown()
    {
        try { return string.Equals(GRouted(ReadSettings(), "ForceFrontendFocusOnShutdown", "true"), "true", StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    /// <summary>LiteBox.ini StartupStayOnTop (LiteBox-specific → NOT Settings.xml, which LB
    /// strips of unknown keys): the startup/end overlays keep TOPMOST for their whole
    /// configured duration WITHOUT ever taking the focus — non-blocking, so an emulator
    /// that pauses when unfocused (RetroArch pause_nonactive) keeps running behind the
    /// cover. Off = historical behaviour (overlay yields top+front at spawn).
    ///
    /// SPLIT per launch type (StartupStayOnTop.{category}), since it works great for emulators
    /// but less so for Windows/store games: emulators default ON, everything else OFF. This is
    /// only the GLOBAL default — a per-emulator or per-game override still wins (see LiteBoxOption).
    /// The no-arg overload is a defensive fallback (no launch context) → historical Off.</summary>
    public static bool StartupStayOnTop() => false;

    /// <summary>The launch categories the StartupStayOnTop default splits over (ini key · UI label).
    /// Emulator defaults ON; App / DOSBox / every store default OFF. Store keys are "Store."+StoreKind.</summary>
    public static readonly (string Key, string Label)[] StayOnTopCategories =
    {
        ("Emulator",    "Emulators"),
        ("App",         "Windows apps"),
        ("DosBox",      "DOSBox"),
        ("Store.Gog",   "GOG"),
        ("Store.Steam", "Steam"),
        ("Store.Epic",  "Epic"),
        ("Store.Uplay", "Ubisoft Connect"),
        ("Store.Ea",    "EA"),
    };

    public static bool StayOnTopDefault(string category) => string.Equals(category, "Emulator", StringComparison.OrdinalIgnoreCase);
    public static string StayOnTopIniKey(string category) => "StartupStayOnTop." + category;

    /// <summary>The GLOBAL default for whether startup/end screens stay on top, for a given launch
    /// <paramref name="category"/> (Emulator / App / DosBox / Store.Gog / …). Emulator defaults ON, the
    /// rest OFF. A per-emulator / per-game override still overrides this (resolved in LiteBoxOption).</summary>
    public static bool StartupStayOnTop(string category)
    {
        bool def = StayOnTopDefault(category);
        try { return LiteBoxConfig.LoadForExe().GetBool(StayOnTopIniKey(category), def); }
        catch { return def; }
    }

    /// <summary>Global toggle (LiteBox.ini) for the startup-cover progress bar (the ≤100%-at-fade bar fed
    /// by the game's historical detection latency). Default ON; only shows once a game has history.</summary>
    public static bool StartupProgressBar()
    {
        try { return LiteBoxConfig.LoadForExe().GetBool("StartupProgressBar", true); }
        catch { return true; }
    }

    /// <summary>True when "Mute Audio During Transitions" is on (Settings.xml
    /// PauseScreenMuting, LB default true). Read fresh — an options change applies
    /// to the next pause without a restart.</summary>
    public static bool PauseMutingGlobal()
    {
        try { return GBool(ReadSettings(), "PauseScreenMuting", true); }
        catch { return true; }
    }

    // ── Non-emulator pause defaults (LiteBox.ini) ────────────────────────
    // Store / direct-exe / DOSBox games have no IEmulator to source the per-emulator pause fields from,
    // so these globals are their fallback defaults. A per-game pause override still wins over them.

    /// <summary>Default "use the pause screen" for non-emulator games (LiteBox.ini, default true).</summary>
    public static bool NonEmuUsePause()
    { try { return LiteBoxConfig.LoadForExe().GetBool("NonEmuUsePauseScreen", true); } catch { return true; } }

    /// <summary>Default "suspend/freeze the process on pause" for non-emulator games (LiteBox.ini, default true).</summary>
    public static bool NonEmuSuspend()
    { try { return LiteBoxConfig.LoadForExe().GetBool("NonEmuSuspendOnPause", true); } catch { return true; } }

    /// <summary>Default "force the pause screen to the foreground" for non-emulator games (LiteBox.ini, default true).</summary>
    public static bool NonEmuForceful()
    { try { return LiteBoxConfig.LoadForExe().GetBool("NonEmuForcefulActivation", false); } catch { return false; } }

    /// <summary>Which process the pause FREEZES: "smartcapture" (default — the detected game window's owner,
    /// fallback to the launched process) or "process" (always the launched emulator/app). LiteBox.ini.</summary>
    public static string PauseTargetGlobal()
    { try { var v = LiteBoxConfig.LoadForExe().Get("PauseTarget"); return string.IsNullOrWhiteSpace(v) ? "smartcapture" : v!; } catch { return "smartcapture"; } }

    /// <summary>Freeze the whole process TREE on pause (not just the target process). LiteBox.ini, default false.</summary>
    public static bool PauseFreezeTreeGlobal()
    { try { return LiteBoxConfig.LoadForExe().GetBool("PauseFreezeTree", false); } catch { return false; } }

    /// <summary>When suspending, WHEN to show the pause screen relative to the freeze: (showBefore, offsetMs).
    /// showBefore=true (default) ⇒ show then freeze (paint the overlay over the still-running game so no flash
    /// of the frozen frame); false ⇒ freeze then show. offsetMs (0..5000) is the gap between the two, letting
    /// the overlay land exactly over the frozen frame. Only used when the process is actually suspended.</summary>
    public static (bool showBefore, int offsetMs) PauseScreenFreezeTiming()
    {
        try
        {
            var ini = LiteBoxConfig.LoadForExe();
            bool before = !string.Equals(ini.Get("PauseScreenFreezeTiming", "before"), "after", StringComparison.OrdinalIgnoreCase);
            int off = int.TryParse(ini.Get("PauseScreenFreezeOffsetMs"), out var v) ? Math.Max(0, Math.Min(5000, v)) : 0;
            return (before, off);
        }
        catch { return (true, 0); }
    }

    /// <summary>Freeze↔screen timing resolved game → emulator → global (LiteBox.ini), like SmartCapture.
    /// Per-entity overrides live in litebox-options.db (edited by <see cref="LiteBoxGameplayEditor"/>).</summary>
    public static (bool showBefore, int offsetMs) ResolvePauseScreenFreezeTiming(string? emuId, string? gameId)
    {
        try
        {
            var ini = LiteBoxConfig.LoadForExe();
            string? R(string key)
            {
                if (!string.IsNullOrEmpty(gameId)) { var g = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeGame, gameId!, key); if (!string.IsNullOrEmpty(g)) return g; }
                if (!string.IsNullOrEmpty(emuId)) { var e = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeEmulator, emuId!, key); if (!string.IsNullOrEmpty(e)) return e; }
                return ini.Get(key);
            }
            bool before = !string.Equals(R("PauseScreenFreezeTiming") ?? "before", "after", StringComparison.OrdinalIgnoreCase);
            int off = int.TryParse(R("PauseScreenFreezeOffsetMs"), out var v) ? Math.Max(0, Math.Min(5000, v)) : 0;
            return (before, off);
        }
        catch { return (true, 0); }
    }

    /// <summary>Screenshot hotkey string (LiteBox.ini). Empty/None ⇒ disabled. When never set in
    /// LiteBox, INHERIT LaunchBox's KeyboardScreenshot (WPF Key int) — 0/None ⇒ disabled.</summary>
    public static string ScreenCaptureKey()
    {
        try
        {
            var v = LiteBoxConfig.LoadForExe().Get("ScreenCaptureKey");   // null = never set in LiteBox
            if (v != null) return v;
            return LbKeyToCombo(GIntXml("KeyboardScreenshot", 0)) ?? "";
        }
        catch { return ""; }
    }

    /// <summary>SmartCapture config resolved game → emulator → global (LiteBox-own). Per-entity
    /// overrides live in litebox-options.db; the global default in LiteBox.ini.</summary>
    public static SmartCaptureConfig ResolveSmartCapture(string? emuId, string? gameId)
    {
        var ini = LiteBoxConfig.LoadForExe();
        string R(string key, string def)
        {
            if (!string.IsNullOrEmpty(gameId)) { var g = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeGame, gameId!, key); if (!string.IsNullOrEmpty(g)) return g; }
            if (!string.IsNullOrEmpty(emuId)) { var e = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeEmulator, emuId!, key); if (!string.IsNullOrEmpty(e)) return e; }
            return ini.Get(key) ?? def;
        }
        int I(string key, int def) => int.TryParse(R(key, def.ToString()), out var n) ? n : def;
        bool B(string key, bool def) => R(key, def ? "true" : "false").Equals("true", StringComparison.OrdinalIgnoreCase);

        return new SmartCaptureConfig
        {
            Enabled           = B("SmartCaptureEnabled", true),
            UseFps            = B("SmartCaptureUseFps",  true),
            UseSize           = B("SmartCaptureUseSize", true),
            Combine           = R("SmartCaptureCombine", "and"),
            MinFps            = I("SmartCaptureMinFps", 25),
            SustainMs         = I("SmartCaptureSustainMs", 600),
            MinSizePct        = I("SmartCaptureMinSizePct", 50),
            Title             = R("SmartCaptureTitle", ""),
            ShowBorder        = B("SmartCaptureShowBorder", false),   // hidden ini opt-in: keep the yellow WGC border
            StopOnWindowClose = B("SmartCaptureStopOnWindowClose", false),
            IgnoreExes        = SmartCaptureIgnoredExes(),     // process-name entries (.exe/.bat) — store clients by default
            IgnoreTitles      = SmartCaptureIgnoredTitles(),   // window-title fragments (every other blacklist line)
        };
    }

    /// <summary>The default game-window-detection blacklist (store clients), newline-separated + sorted.
    /// This is what seeds the editable list in the options.</summary>
    public static string SmartCaptureIgnoreDefaultRaw()
        => string.Join("\n", System.Linq.Enumerable.OrderBy(Diag.WinScan.StoreClientExes, x => x, StringComparer.OrdinalIgnoreCase));

    /// <summary>The effective blacklist as a newline-separated string (LiteBox.ini SmartCaptureIgnoreExes,
    /// else the built-in default). This is what the options modal edits.</summary>
    public static string SmartCaptureIgnoreExesRaw()
    {
        try { var v = LiteBoxConfig.LoadForExe().Get("SmartCaptureIgnoreExes"); return string.IsNullOrWhiteSpace(v) ? SmartCaptureIgnoreDefaultRaw() : v.Replace("\r\n", "\n"); }
        catch { return SmartCaptureIgnoreDefaultRaw(); }
    }

    // Blacklist entries split by TYPE: a line ending in .exe or .bat is a PROCESS name (matched against a
    // window's owning-process exe); anything else is a window-TITLE fragment (case-insensitive "contains").
    private static bool IsExeOrBatEntry(string e)
        => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    // One trimmed, non-empty entry per line. Split on NEWLINES only (not ',' / ';') so a title fragment may
    // itself contain commas or semicolons.
    private static IEnumerable<string> SmartCaptureIgnoreEntries()
    {
        foreach (var line in SmartCaptureIgnoreExesRaw().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        { var e = line.Trim(); if (e.Length > 0) yield return e; }
    }

    /// <summary>The blacklist's PROCESS-name entries (lines ending in .exe/.bat), case-insensitive. SmartCapture
    /// skips any window whose owning process's exe is in here — so the store client's own UI isn't taken for the
    /// game. (Empty-config → store-client default is handled by <see cref="SmartCaptureIgnoreExesRaw"/>.)</summary>
    public static HashSet<string> SmartCaptureIgnoredExes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in SmartCaptureIgnoreEntries()) if (IsExeOrBatEntry(e)) set.Add(e);
        return set;
    }

    /// <summary>The blacklist's window-TITLE fragments (every line NOT ending in .exe/.bat). SmartCapture skips
    /// a window when its title CONTAINS one (case-insensitive, wildcards * and ?; "fenetre" matches "ma fenetre
    /// de jeu" like "ma*jeu" does — see <see cref="Diag.WinScan.WildcardContains"/>). Empty by default.</summary>
    public static List<string> SmartCaptureIgnoredTitles()
    {
        var list = new List<string>();
        foreach (var e in SmartCaptureIgnoreEntries()) if (!IsExeOrBatEntry(e)) list.Add(e);
        return list;
    }

    /// <summary>LiteBox-own: on an explicit exit (pause-menu "Exit Game"), show the end/exit screen
    /// this many ms AFTER the exit AutoHotkey script runs — covering the display while the emulator
    /// is still closing, instead of waiting for the process to fully exit. -1 = disabled (default).
    /// Resolved game → emulator → global (litebox-options.db over LiteBox.ini).</summary>
    public static int ResolveExitScreenEagerMs(string? emuId, string? gameId = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(gameId))
            {
                var gv = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeGame, gameId!, "ExitScreenEagerMs");
                if (!string.IsNullOrEmpty(gv)) return int.TryParse(gv, out var gn) ? gn : -1;
            }
            if (!string.IsNullOrEmpty(emuId))
            {
                var ov = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeEmulator, emuId!, "ExitScreenEagerMs");
                if (!string.IsNullOrEmpty(ov)) return int.TryParse(ov, out var n) ? n : -1;
            }
            return ExitScreenEagerMsGlobal();
        }
        catch { return -1; }
    }

    /// <summary>Global "exit screen early" delay (LiteBox.ini ExitScreenEagerMs). -1 = disabled.</summary>
    public static int ExitScreenEagerMsGlobal()
    {
        try { var v = LiteBoxConfig.LoadForExe().Get("ExitScreenEagerMs"); return int.TryParse(v, out var n) ? n : -1; }
        catch { return -1; }
    }

    // ── Pause-menu "Exit Game" force-close fallback (no custom exit AHK script) ────────────
    // When the emulator/game has NO user-authored ExitAutoHotkeyScript, LiteBox sends the default
    // exit key ("Send {Escape}") and, if the game is still up after a grace period, force-kills it.
    // This option makes BOTH the grace period AND what gets killed configurable, resolved
    // game → emulator → global. Encoded as one string: "none" | "smartcapture:<sec>" | "process:<sec>".
    //   • none         — never force-kill (leave the game to close itself, like a custom exit script would)
    //   • smartcapture — kill the SmartCapture-detected game process tree (fallback: launched emulator/app)
    //   • process      — kill the launched emulator/app process tree (the historical behaviour)
    // Default ("smartcapture", 30). A per-emulator / per-game override (litebox-options.db) wins.

    public const string PauseExitKillDefaultMode = "smartcapture";
    public const int PauseExitKillDefaultSeconds = 30;
    private const int PauseExitKillMaxSeconds = 600;

    /// <summary>Parse a "PauseExitKill" value ("none" | "smartcapture:&lt;sec&gt;" | "process:&lt;sec&gt;").
    /// Empty / unrecognised ⇒ the default ("smartcapture", 30). Seconds clamped to 0..600.</summary>
    public static (string mode, int seconds) ParsePauseExitKill(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (PauseExitKillDefaultMode, PauseExitKillDefaultSeconds);
        var s = raw.Trim();
        if (string.Equals(s, "none", StringComparison.OrdinalIgnoreCase)) return ("none", PauseExitKillDefaultSeconds);
        var parts = s.Split(':');
        string mode = parts[0].Trim().ToLowerInvariant();
        if (mode != "smartcapture" && mode != "process") return (PauseExitKillDefaultMode, PauseExitKillDefaultSeconds);
        int sec = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var n)
            ? Math.Max(0, Math.Min(PauseExitKillMaxSeconds, n)) : PauseExitKillDefaultSeconds;
        return (mode, sec);
    }

    /// <summary>Global pause-exit force-kill default (LiteBox.ini PauseExitKill). Default ("smartcapture", 30).</summary>
    public static (string mode, int seconds) PauseExitKillGlobal()
    {
        try { return ParsePauseExitKill(LiteBoxConfig.LoadForExe().Get("PauseExitKill")); }
        catch { return (PauseExitKillDefaultMode, PauseExitKillDefaultSeconds); }
    }

    /// <summary>Pause-exit force-kill behaviour resolved game → emulator → global (litebox-options.db over
    /// LiteBox.ini), like <see cref="ResolvePauseScreenFreezeTiming"/>. Consumed by PauseManager's exit path.</summary>
    public static (string mode, int seconds) ResolvePauseExitKill(string? emuId, string? gameId)
    {
        try
        {
            var ini = LiteBoxConfig.LoadForExe();
            string? R(string key)
            {
                if (!string.IsNullOrEmpty(gameId)) { var g = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeGame, gameId!, key); if (!string.IsNullOrEmpty(g)) return g; }
                if (!string.IsNullOrEmpty(emuId)) { var e = Data.LiteBoxOption.GetOverride(Data.LiteBoxOption.ScopeEmulator, emuId!, key); if (!string.IsNullOrEmpty(e)) return e; }
                return ini.Get(key);
            }
            return ParsePauseExitKill(R("PauseExitKill"));
        }
        catch { return (PauseExitKillDefaultMode, PauseExitKillDefaultSeconds); }
    }

    /// <summary>When the web frontend (ExtendDB kiosk) comes back after a game, relative to the GAME
    /// OVER screen (LiteBox.ini WebReturnTiming). Only meaningful when ExtendDB is loaded and an end
    /// screen actually shows — the caller degrades to "immediate" otherwise.
    ///   • "immediate" — OnGameExited fires at process exit (LB parity); the kiosk may flash first.
    ///   • "after"     — OnGameExited fires once GAME OVER closes (delays all exit cleanup by that time).
    ///   • "behind"    — kiosk reloads hidden BEHIND GAME OVER, revealed the instant it closes (default).</summary>
    public static string WebReturnTiming()
    {
        try
        {
            var v = (LiteBoxConfig.LoadForExe().Get("WebReturnTiming") ?? "").Trim().ToLowerInvariant();
            return v is "immediate" or "after" or "behind" ? v : "behind";
        }
        catch { return "behind"; }
    }

    /// <summary>Controller-pause master switch (LiteBox-own, LiteBox.ini). Off by default —
    /// opt-in. LaunchBox has no equivalent, so this never touches Settings.xml.</summary>
    public static bool PadPauseEnabled()
    {
        try { return LiteBoxConfig.LoadForExe().GetBool("PadPauseEnabled", false); }
        catch { return false; }
    }

    /// <summary>Controller-pause button/combo (LiteBox.ini), e.g. "Back+Start". Default a safe combo.</summary>
    public static string PadPauseButton()
    {
        try { var v = LiteBoxConfig.LoadForExe().Get("PadPauseButton"); return string.IsNullOrEmpty(v) ? "Back+Start" : v; }
        catch { return "Back+Start"; }
    }

    /// <summary>An int Settings.xml value (LB-native keys, never DB-routed).</summary>
    private static int GIntXml(string key, int def)
    {
        try { var d = ReadSettings(); return d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def; }
        catch { return def; }
    }

    /// <summary>LaunchBox stores its single game-pause / screenshot key as a WPF
    /// System.Windows.Input.Key int (no modifiers). Convert to our bare-key-name combo format
    /// (the parser is case-insensitive and shares the key names). 0/None ⇒ null (unset).</summary>
    private static string? LbKeyToCombo(int wpfKey)
    {
        if (wpfKey <= 0) return null;
        try
        {
            var name = Enum.GetName(typeof(System.Windows.Input.Key), wpfKey);
            return string.IsNullOrEmpty(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch { return null; }
    }

    // ── Settings.xml read (fresh) ────────────────────────────────────────────
    private static Dictionary<string, string> ReadSettings()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = Path.Combine(_lbRoot ?? "", "Data", "Settings.xml");
            if (!File.Exists(file)) return d;
            var root = XDocument.Load(file).Root?.Element("Settings");
            if (root != null)
                foreach (var e in root.Elements()) d[e.Name.LocalName] = e.Value;
        }
        catch { }
        return d;
    }

    // GBool/GInt default to the centralised SettingDefaults when no explicit fallback is given,
    // so the "no value anywhere" case matches LbSettingsStore and the options page exactly.
    private static bool GBool(Dictionary<string, string> d, string name)
        => d.TryGetValue(name, out var v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                                          : Data.SettingDefaults.GetBool(name);

    private static bool GBool(Dictionary<string, string> d, string name, bool def)
        => d.TryGetValue(name, out var v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : def;

    /// <summary>Routed read: LiteBox's DB for a DB-managed key (ProblemKeys), else Settings.xml.
    /// The single choke point that keeps GameplaySettings and LbSettingsStore consistent.</summary>
    private static string? GRouted(Dictionary<string, string> d, string key, string? fallback)
    {
        if (Data.ProblemKeys.IsDbManaged(key)) return Data.LiteBoxOptionsDb.GetGlobal(key) ?? fallback;
        return d.TryGetValue(key, out var v) ? v : fallback;
    }

    private static int GIntRouted(Dictionary<string, string> d, string key)
        => int.TryParse(GRouted(d, key, Data.SettingDefaults.Get(key)), out var n) ? n : 0;
}
