// Snapshot of the launched game, captured at launch time BEFORE the memory drop.
//
// Why: when a game starts, the host frees the optional data tier and clears the
// game cache ("free RAM at launch") — so anything that wants game info DURING the
// game (the pause screen's fanart background, logo, session time, a future
// notification overlay, …) must not touch the store or the cache. This object
// captures everything up front: plain strings + file PATHS (the files stay on
// disk; only the in-memory indexes go away).
//
// Media paths are resolved through MediaResolver (ExtendDB GameCache fast-path
// when ready, IO walk otherwise) at capture time, then never re-resolved.

#nullable enable

using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed class LaunchedGame
{
    public string GameId = "";
    public string Title = "";
    public string Platform = "";
    public string Developer = "";
    public int ReleaseYear;
    public DateTime LaunchedAtUtc;

    // Resolved at capture (absolute paths, may be null when the game has no such media).
    public string? FanartPath;
    public string? ClearLogoPath;
    public string? BoxFrontPath;
    public string? ManualPath;   // pause screen "View Manual"

    // Per-GAME pause-screen overrides (<OverrideDefaultPauseScreenSettings> +
    // the three toggles on the <Game> element). The game's _extra fields are
    // Tier-2 (dropped at launch), so they MUST be captured here. When
    // PauseOverride is false the emulator-level settings apply.
    public bool PauseOverride;
    public bool PauseUse, PauseSuspend, PauseForceful;

    // Per-GAME pause AHK scripts (the six Customize-dialog tabs). Only meaningful when
    // PauseOverride is true — the game's scripts then REPLACE the emulator's wholesale
    // (an empty tab = no script for that action, exactly like LB's override panel).
    // Keyed by the emulator-side field name (PauseAutoHotkeyScript, …).
    public Dictionary<string, string> PauseScripts = new(StringComparer.Ordinal);

    // Per-GAME startup/end-screen overrides (LB "Override Default Startup Screen
    // Settings" on the Game). StartupMinMs = -1 ⇒ "use the global value".
    public bool StartupOverride;
    public bool StartupUse, StartupHideCursor, ShutdownDisabled;
    public int StartupMinMs = -1;
    // LB "Startup Load Delay" (ms). Repurposed under LiteBox as the SmartCapture reveal-anyway ceiling
    // (give up waiting for a render and reveal the cover after this). -1/0 ⇒ unset → default. See
    // GameplaySettings.RevealMaxMs. Round-trips to LB, where it keeps its native meaning.
    public int StartupLoadDelay = -1;

    // Per-EMULATOR startup/end-screen tier (LB Edit Emulator → Startup Screen). Every
    // emulator in Emulators.xml carries its own copy (seeded from the global defaults at
    // add time), so when a game launches THROUGH an emulator these win over the global —
    // themselves overridden by the per-game override above. Captured here because the
    // emulator, like the game, isn't available once the screen path runs. -1/null ⇒ unset.
    public bool EmuCaptured;
    public bool EmuUse, EmuHideCursor, EmuShutdownDisabled;
    public int EmuStartupMinMs = -1, EmuShutdownMinMs = -1, EmuLoadDelay = -1;
    public bool? EmuForceFocus;

    // LiteBox-OWN options resolved (emulator override → global) at capture — LB has no field
    // for these, they live in litebox-options.db. Snapshotted here because the resolver needs
    // the emulator id, gone once the in-game surfaces run.
    public bool StayOnTop;               // startup/end screens topmost without focus
    public string ScreenCaptureKey = ""; // screenshot hotkey ("" = off for this launch)

    /// <summary>The game currently running, or null. Set by HostLaunch at launch,
    /// cleared in its exit finally.</summary>
    public static LaunchedGame? Current { get; private set; }

    public static void Clear() => Current = null;

    /// <summary>Capture everything the in-game surfaces may need. Call BEFORE
    /// DropOptional / GameCache clear. Never throws. <paramref name="emulator"/> (null
    /// for a store / direct launch) contributes the emulator startup/end-screen tier.
    /// <paramref name="stayCategory"/> (Emulator / App / DosBox / Store.Gog / …) selects the
    /// GLOBAL StartupStayOnTop default for this launch — a per-emu / per-game override still wins.</summary>
    public static void Capture(IGame game, IEmulator? emulator = null, string stayCategory = "Emulator")
    {
        try
        {
            var lg = new LaunchedGame
            {
                GameId = Safe(() => game.Id) ?? "",
                Title = Safe(() => game.Title) ?? "",
                Platform = Safe(() => game.Platform) ?? "",
                Developer = Safe(() => game.Developer) ?? "",
                ReleaseYear = SafeYear(game),
                LaunchedAtUtc = DateTime.UtcNow,
            };

            string plat = lg.Platform;
            bool hasId = Guid.TryParse(lg.GameId, out var id);
            if (!string.IsNullOrEmpty(plat) && hasId)
            {
                lg.FanartPath = FirstOrNull(MediaResolver.AllOfType(plat, id, lg.Title, "Fanart - Background"));
                lg.ClearLogoPath = MediaResolver.Image(plat, id, lg.Title, MediaResolver.ClearLogo);
                lg.BoxFrontPath = MediaResolver.Image(plat, id, lg.Title, MediaResolver.Front);
                lg.ManualPath = MediaResolver.Manual(plat, id, lg.Title);
            }
            // IGame property fallbacks (also cover non-GUID ids).
            lg.FanartPath ??= NonEmpty(Safe(() => game.BackgroundImagePath)) ?? NonEmpty(Safe(() => game.ScreenshotImagePath));
            lg.ClearLogoPath ??= NonEmpty(Safe(() => game.ClearLogoImagePath));
            lg.BoxFrontPath ??= NonEmpty(Safe(() => game.FrontImagePath)) ?? NonEmpty(Safe(() => game.Box3DImagePath));
            lg.ManualPath ??= NonEmpty(Safe(() => game.ManualPath));

            // Per-game pause overrides (read via ILiteBoxFields — these XML fields
            // aren't on the SDK IGame, and the backing _extra dict is dropped at launch).
            try
            {
                if (game is LbApiHost.ILiteBoxFields lf
                    && string.Equals(lf.GetField("OverrideDefaultPauseScreenSettings"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    lg.PauseOverride = true;
                    lg.PauseUse = string.Equals(lf.GetField("UsePauseScreen"), "true", StringComparison.OrdinalIgnoreCase);
                    lg.PauseSuspend = string.Equals(lf.GetField("SuspendProcessOnPause"), "true", StringComparison.OrdinalIgnoreCase);
                    lg.PauseForceful = string.Equals(lf.GetField("ForcefulPauseScreenActivation"), "true", StringComparison.OrdinalIgnoreCase);
                    foreach (var f in new[] { "PauseAutoHotkeyScript", "ResumeAutoHotkeyScript", "ResetAutoHotkeyScript",
                                              "SaveStateAutoHotkeyScript", "LoadStateAutoHotkeyScript", "SwapDiscsAutoHotkeyScript",
                                              "ExitAutoHotkeyScript" })
                        lg.PauseScripts[f] = lf.GetField(f) ?? "";
                }
            }
            catch { }

            // Per-game startup/end-screen override (LB "Override Default Startup Screen Settings").
            // CRITICAL: these are MODELLED IGame fields (HostGame stores them as typed bit-flags / int cells,
            // NOT in the sparse _extra dict). ILiteBoxFields.GetField only reads _extra, so it returns "" for
            // every one of them — reading the override through GetField silently missed it and the startup
            // screen kept using the GLOBAL default even when the game turned it off. Read the concrete IGame
            // properties instead. (The pause block above is fine: its fields ARE _extra, so GetField works.)
            try
            {
                if (game.OverrideDefaultStartupScreenSettings)
                {
                    lg.StartupOverride = true;
                    lg.StartupUse = game.UseStartupScreen;
                    lg.StartupHideCursor = game.HideMouseCursorInGame;
                    lg.ShutdownDisabled = game.DisableShutdownScreen;
                    // Display time of the "NOW LOADING…" screen (StartupScreenPostLaunchDisplayTime, ms) is NOT
                    // a modelled property — it lives in _extra, so GetField is the correct accessor for it.
                    lg.StartupMinMs = (game is LbApiHost.ILiteBoxFields lf2
                        && int.TryParse(lf2.GetField("StartupScreenPostLaunchDisplayTime"), out var ld)) ? ld : -1;
                    lg.StartupLoadDelay = game.StartupLoadDelay > 0 ? game.StartupLoadDelay : -1;
                }
            }
            catch { }

            // Per-emulator startup/end-screen tier (LB Edit Emulator → Startup Screen). Read
            // through ILiteBoxFields (HostEmulator round-trips every XML field) — not on the
            // SDK IEmulator. Only when a game launches through an emulator (store/direct: null).
            try
            {
                if (emulator is LbApiHost.ILiteBoxFields ef)
                {
                    lg.EmuCaptured = true;
                    lg.EmuUse = EBool(ef, "UseStartupScreen", true);
                    lg.EmuHideCursor = EBool(ef, "HideMouseCursorInGame", true);
                    lg.EmuShutdownDisabled = EBool(ef, "DisableShutdownScreen", false);
                    lg.EmuStartupMinMs = int.TryParse(ef.GetField("StartupScreenPostLaunchDisplayTime"), out var es) ? es : -1;
                    lg.EmuShutdownMinMs = int.TryParse(ef.GetField("ShutdownScreenPostReadyDisplayTime"), out var eh) ? eh : -1;
                    lg.EmuLoadDelay = int.TryParse(ef.GetField("StartupLoadDelay"), out var eld) ? eld : -1;
                    var ff = ef.GetField("ForceFrontendFocusOnShutdown");
                    lg.EmuForceFocus = string.IsNullOrEmpty(ff) ? (bool?)null
                                     : string.Equals(ff, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            // LiteBox-own per-emulator options (litebox-options.db) resolved through the emulator.
            try
            {
                string? emuId = emulator != null ? Safe(() => emulator.Id) : null;
                string? gameId = Safe(() => game.Id);
                lg.StayOnTop = LbApiHost.Host.Data.LiteBoxOption.ResolveBool(
                    "StartupStayOnTop", emuId, Gameplay.GameplaySettings.StartupStayOnTop(stayCategory), gameId);
                lg.ScreenCaptureKey = LbApiHost.Host.Data.LiteBoxOption.ResolveString(
                    "ScreenCaptureKey", emuId, Gameplay.GameplaySettings.ScreenCaptureKey(), gameId);
                Console.WriteLine($"[litebox-opt] resolved stayOnTop={lg.StayOnTop} screenshotKey='{lg.ScreenCaptureKey}' (emu={emuId ?? "none"} game={gameId ?? "none"})");
            }
            catch { }

            Current = lg;
            Console.WriteLine($"[launched] snapshot \"{lg.Title}\" fanart={(lg.FanartPath != null ? "yes" : "no")} logo={(lg.ClearLogoPath != null ? "yes" : "no")} box={(lg.BoxFrontPath != null ? "yes" : "no")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[launched] capture failed: " + ex.Message);
            Current = new LaunchedGame { Title = Safe(() => game.Title) ?? "", LaunchedAtUtc = DateTime.UtcNow };
        }
    }

    private static int SafeYear(IGame g) { try { return g.ReleaseYear ?? 0; } catch { return 0; } }
    private static string? FirstOrNull(List<string>? l) => l is { Count: > 0 } ? l[0] : null;
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static bool EBool(LbApiHost.ILiteBoxFields f, string name, bool def)
    { var v = f.GetField(name); return string.IsNullOrEmpty(v) ? def : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); }
    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
