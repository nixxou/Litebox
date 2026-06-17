// Startup ("NOW LOADING…") and End ("GAME OVER") overlays — the screen siblings of
// the pause screen. Driven from HostLaunch around the emulator's lifetime:
//   • Startup: shown right before the emulator spawns, auto-closed after the
//     configured minimum time (non-blocking — the game loads behind it).
//   • End: shown after the game exits, held for the minimum time (blocking the
//     already-idle launch worker), then closed.
// Cosmetics come from the LaunchedGame snapshot (captured pre-drop), reusing the
// pause screen's ScreenArt. Settings resolve game → platform → global (GameplaySettings).

#nullable enable

using LbApiHost.Host.Pause;

namespace LbApiHost.Host.Gameplay;

internal static class GameScreens
{
    private static readonly object _lock = new();
    private static InfoOverlay? _overlay;
    private static System.Windows.Forms.Timer? _timer;

    public static void Configure(string lbRoot)
    {
        GameplaySettings.Configure(lbRoot);
        UiThread.Start();   // ensure the pumping UI thread exists (idempotent)
    }

    /// <summary>Show the startup screen (if enabled), auto-closing after the minimum
    /// display time. Returns immediately — the game keeps launching behind it.</summary>
    public static void ShowStartup(LaunchedGame? snap)
    {
        if (snap == null) return;
        GameplaySettings.Resolved? rr = Safe(() => GameplaySettings.Resolve(snap));
        if (rr is not { UseStartup: true }) return;
        int ms = Math.Max(0, rr.StartupMinMs);
        var ctx = BuildCtx(snap);
        bool hide = rr.HideCursor;

        UiThread.Invoke(() =>
        {
            lock (_lock)
            {
                CloseLocked();
                try
                {
                    _overlay = new InfoOverlay(ctx, "NOW LOADING…", hide);
                    _overlay.Show();
                    _overlay.ForceToFront(8);
                }
                catch { CloseLocked(); return; }

                if (ms <= 0) { CloseLocked(); return; }
                _timer = new System.Windows.Forms.Timer { Interval = ms };
                _timer.Tick += (_, _) => { lock (_lock) { _timer?.Stop(); CloseLocked(); } };
                _timer.Start();
            }
        });
    }

    /// <summary>Show the end ("GAME OVER") screen (if enabled) and BLOCK the caller for
    /// the minimum display time, then close. Call on the launch worker after exit.</summary>
    public static void ShowEndBlocking(LaunchedGame? snap)
    {
        if (snap == null) return;
        GameplaySettings.Resolved? rr = Safe(() => GameplaySettings.Resolve(snap));
        if (rr is not { UseStartup: true }) return;       // same master toggle as startup
        if (rr.ShutdownMinMs < 0) return;                  // per-game DisableShutdownScreen
        int ms = Math.Max(0, rr.ShutdownMinMs);
        if (ms == 0) return;
        var ctx = BuildCtx(snap);
        bool hide = rr.HideCursor;

        UiThread.Invoke(() =>
        {
            lock (_lock)
            {
                CloseLocked();
                try { _overlay = new InfoOverlay(ctx, "GAME OVER", hide); _overlay.Show(); _overlay.ForceToFront(8); }
                catch { CloseLocked(); }
            }
        });
        try { Thread.Sleep(ms); } catch { }
        UiThread.Invoke(() => { lock (_lock) CloseLocked(); });
    }

    public static void Close()
    {
        UiThread.Invoke(() => { lock (_lock) CloseLocked(); });
    }

    private static void CloseLocked()
    {
        try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        _timer = null;
        try { _overlay?.Close(); } catch { }
        try { _overlay?.Dispose(); } catch { }
        _overlay = null;
    }

    private static PauseContext BuildCtx(LaunchedGame snap) => new()
    {
        GameTitle = snap.Title,
        Platform = snap.Platform,
        Developer = snap.Developer,
        ReleaseYear = snap.ReleaseYear,
        FanartPath = snap.FanartPath,
        ClearLogoPath = snap.ClearLogoPath,
        BoxFrontPath = snap.BoxFrontPath,
        SessionStartUtc = snap.LaunchedAtUtc,
        EmulatorMainWindow = IntPtr.Zero,   // game window not up yet (startup) / gone (end) → primary monitor
    };

    private static T? Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
}
