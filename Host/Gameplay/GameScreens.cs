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

        // StartupStayOnTop: the overlay never activates (WS_EX_NOACTIVATE) and keeps
        // TOPMOST for its whole duration — the emulator spawns, takes the focus and RUNS
        // behind the cover (RetroArch pause_nonactive included) until the timer reveals it.
        bool stayTop = Safe2(GameplaySettings.StartupStayOnTop);

        UiThread.Invoke(() =>
        {
            lock (_lock)
            {
                CloseLocked();
                try
                {
                    _overlay = new InfoOverlay(ctx, "NOW LOADING…", hide, noActivate: stayTop);
                    _overlay.Show();
                    _overlay.ForceToFront(8);   // no-op in stay-on-top mode
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

        bool stayTop = Safe2(GameplaySettings.StartupStayOnTop);
        UiThread.Invoke(() =>
        {
            lock (_lock)
            {
                CloseLocked();
                try { _overlay = new InfoOverlay(ctx, "GAME OVER", hide, noActivate: stayTop); _overlay.Show(); _overlay.ForceToFront(8); }
                catch { CloseLocked(); }
            }
        });
        try { Thread.Sleep(ms); } catch { }
        // LB's "Force LaunchBox or Big Box back into focus when the shutdown screen closes":
        // decide BEFORE closing, apply AFTER — the frontend is the ExtendDB web kiosk when
        // one is up (it relaunched during the exit), else the LiteBox window. When OFF the
        // overlay hides-then-closes and the foreground falls wherever Windows says (the
        // "stay in another app after alt-tabbing away" behaviour LB documents). The emulator
        // carries its own copy of this flag (Edit Emulator → Startup Screen) → it wins over
        // the global for an emulator launch.
        bool refocus = snap?.EmuForceFocus ?? Safe2(GameplaySettings.ForceFrontendFocusOnShutdown);
        UiThread.Invoke(() => { lock (_lock) CloseLocked(); });
        if (refocus) FocusFrontend();
    }

    /// <summary>Refocus after the end screen closed (also called when no end screen is
    /// configured — see HostLaunch). Kiosk first, LiteBox window as the fallback.</summary>
    public static void FocusFrontend()
    {
        try
        {
            IntPtr target = FindKioskWindow();
            if (target == IntPtr.Zero)
                try { target = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { }
            if (target != IntPtr.Zero) SetForegroundWindow(target);
        }
        catch { }
    }

    // The ExtendDB web kiosk (BigBox Web / LaunchBox Web fullscreen window) lives IN this
    // process — find it by pid + visibility + its "ExtendDB — … Web …" title. IntPtr.Zero
    // when none is up (plugin absent, kiosk off, or plain-browser use).
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static IntPtr FindKioskWindow()
    {
        IntPtr found = IntPtr.Zero;
        uint myPid = (uint)Environment.ProcessId;
        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != myPid) return true;
                    var sb = new System.Text.StringBuilder(128);
                    GetWindowText(h, sb, 128);
                    var t = sb.ToString();
                    if (t.StartsWith("ExtendDB", StringComparison.Ordinal) && t.Contains("Web", StringComparison.Ordinal))
                    { found = h; return false; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    private static bool Safe2(Func<bool> f) { try { return f(); } catch { return false; } }

    /// <summary>Hand the foreground to the emulator about to spawn: the startup overlay drops
    /// its always-on-top + foreground-stealing so the emulator window can take and keep focus.
    /// The overlay stays on screen (covering the desktop during load) until its timer closes it.
    /// No-op when no overlay is up. Call right before spawning the emulator.</summary>
    public static void ReleaseStartupTopFront()
    {
        UiThread.Invoke(() => { lock (_lock) { try { _overlay?.ReleaseTopFront(); } catch { } } });
    }

    public static void Close()
    {
        UiThread.Invoke(() => { lock (_lock) CloseLocked(); });
    }

    private static void CloseLocked()
    {
        try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        _timer = null;
        try { _overlay?.HideThenClose(); } catch { }   // never yank the foreground off the game
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
