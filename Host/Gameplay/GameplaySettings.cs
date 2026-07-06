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
    }

    /// <summary>Effective settings for <paramref name="snap"/> (the launching game), global
    /// values overridden by the game's per-game settings when present.</summary>
    public static Resolved Resolve(LaunchedGame? snap)
    {
        var d = ReadSettings();
        var r = new Resolved
        {
            UseStartup    = GBool(d, "UseStartupScreen", true),
            StartupMinMs  = GInt(d, "MinimumStartupScreenDisplayTime", 1000),
            ShutdownMinMs = GInt(d, "MinimumShutdownScreenDisplayTime", 1000),
            HideCursor    = GBool(d, "HideMouseCursorOnStartupScreens", true),
            UsePause      = GBool(d, "UsePauseScreen", true),
            Fading        = GBool(d, "PauseScreenFading", true),
            Muting        = GBool(d, "PauseScreenMuting", true),
        };

        // Per-platform tier reserved here (none today).

        // Per-game override (LB "Override Default … Settings" on the Game).
        if (snap is { StartupOverride: true })
        {
            r.UseStartup = snap.StartupUse;
            if (snap.StartupMinMs >= 0) r.StartupMinMs = snap.StartupMinMs;
            r.HideCursor = snap.StartupHideCursor;
            if (snap.ShutdownDisabled) r.ShutdownMinMs = -1;   // -1 ⇒ no end screen for this game
        }
        return r;
    }

    /// <summary>Pause hotkey string (LiteBox.ini, combo-capable). Default "Pause".</summary>
    public static string PauseKey()
    {
        try { return LiteBoxConfig.LoadForExe().Get("PauseHotkey", "Pause") ?? "Pause"; }
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
        try { return GBool(ReadSettings(), "ForceFrontendFocusOnShutdown", true); }
        catch { return true; }
    }

    /// <summary>LiteBox.ini StartupStayOnTop (LiteBox-specific → NOT Settings.xml, which LB
    /// strips of unknown keys): the startup/end overlays keep TOPMOST for their whole
    /// configured duration WITHOUT ever taking the focus — non-blocking, so an emulator
    /// that pauses when unfocused (RetroArch pause_nonactive) keeps running behind the
    /// cover. Off = historical behaviour (overlay yields top+front at spawn).</summary>
    public static bool StartupStayOnTop()
    {
        try { return LiteBoxConfig.LoadForExe().GetBool("StartupStayOnTop", false); }
        catch { return false; }
    }

    /// <summary>True when "Mute Audio During Transitions" is on (Settings.xml
    /// PauseScreenMuting, LB default true). Read fresh — an options change applies
    /// to the next pause without a restart.</summary>
    public static bool PauseMutingGlobal()
    {
        try { return GBool(ReadSettings(), "PauseScreenMuting", true); }
        catch { return true; }
    }

    /// <summary>Screenshot hotkey string (LiteBox.ini). Empty/None ⇒ disabled.</summary>
    public static string ScreenCaptureKey()
    {
        try { return LiteBoxConfig.LoadForExe().Get("ScreenCaptureKey", "") ?? ""; }
        catch { return ""; }
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

    private static bool GBool(Dictionary<string, string> d, string name, bool def)
        => d.TryGetValue(name, out var v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : def;

    private static int GInt(Dictionary<string, string> d, string name, int def)
        => d.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : def;
}
