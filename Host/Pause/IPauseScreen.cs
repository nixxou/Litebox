// Pause-screen mode abstraction. Two modes are planned (LiteBox.ini: PauseMode=):
//
//   • "legacy"   — LaunchBox-legacy pause mode: a native full-screen overlay with the
//                  same actions LB's pause screen offers (Resume / Save State / Load
//                  State / Reset / Swap Discs / Exit Game), driven by the emulator's
//                  pause AHK scripts. Implemented (LegacyPauseScreen).
//   • "advanced" — LiteBox advanced pause mode: a WebView2-based screen with richer
//                  options (game info, media, manuals, …). NOT implemented yet —
//                  the factory falls back to legacy with a console note.
//
// The screen is pure PRESENTATION: it renders the available actions and reports the
// user's choice through PauseContext.OnAction. All the mechanics (process suspend /
// resume, AHK one-off scripts, exiting the emulator) live in PauseManager — so a new
// mode only has to draw buttons, never to re-implement the lifecycle.

#nullable enable

namespace LbApiHost.Host.Pause;

internal enum PauseAction
{
    Resume,
    ViewManual,
    SaveState,
    LoadState,
    Reset,
    SwapDiscs,
    ExitGame,
}

/// <summary>Everything a pause screen needs to render + report back.</summary>
internal sealed class PauseContext
{
    public string GameTitle = "";
    public string Platform = "";
    public string Developer = "";
    public int ReleaseYear;
    // Cosmetics, from the LaunchedGame snapshot (paths stay valid during the game —
    // only the in-memory caches were dropped at launch).
    public string? FanartPath;     // low-opacity background
    public string? ClearLogoPath;  // shown instead of the title text when present
    public string? BoxFrontPath;   // small box accent
    public DateTime SessionStartUtc;
    // Per-action availability (an action shows only when its AHK script is non-empty;
    // Resume and ExitGame are always available; ViewManual when the game has one).
    public bool CanSaveState, CanLoadState, CanReset, CanSwapDiscs;
    public bool CanViewManual;
    // Forceful activation (emulator field): keep stealing focus until the screen has it.
    public bool ForcefulActivation;
    /// <summary>The emulator's main window — the screen opens on ITS monitor
    /// (multi-monitor: the overlay must cover the game, not the primary).
    /// IntPtr.Zero → primary monitor fallback.</summary>
    public IntPtr EmulatorMainWindow;
    /// <summary>Invoked (on the screen's UI thread) when the user picks an action.
    /// The manager closes the screen itself — the screen must not self-close first.</summary>
    public Action<PauseAction>? OnAction;
}

/// <summary>A pause-screen presentation mode. Implementations are created on and
/// driven from the dedicated UI thread (UiThread.Invoke).</summary>
internal interface IPauseScreen
{
    bool IsOpen { get; }
    void Show(PauseContext ctx);
    void Close();
}

/// <summary>Picks the configured pause-screen mode.</summary>
internal static class PauseScreenFactory
{
    public static IPauseScreen Create(LiteBoxConfig cfg)
    {
        var mode = cfg?.Get("PauseMode", "legacy")?.Trim().ToLowerInvariant() ?? "legacy";
        if (mode == "advanced")
            Console.WriteLine("[pause] PauseMode=advanced is not implemented yet — falling back to the legacy mode.");
        return new LegacyPauseScreen();
    }
}
