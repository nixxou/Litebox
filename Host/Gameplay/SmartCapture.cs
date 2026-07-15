// SmartCapture: reveal the startup cover WHEN THE GAME IS ACTUALLY READY, not on a blind timer.
// Watches the launched process TREE (or all windows for store launches) and lifts the cover the
// moment a chosen detection condition is met, backed by a safety-max "reveal anyway after" ceiling
// (so a case WGC can't see — exclusive fullscreen — never hangs; it falls back to a timed reveal).
// That ceiling is the resolved LB "Startup Load Delay" (GameplaySettings.RevealMaxMs), default 5s.
//
// Detection is a BOOLEAN EXPRESSION (configurable — global / per-emulator / per-game):
//     detected(window) = titleMatch(if a title is set)  OR  ( fps-test  [AND|OR]  size-test )
//   • Title (wildcard, case-insensitive *,?): if set, a window whose title matches IS the game on
//     its own (OR-priority). A window that does NOT match can still be caught by fps/size.
//   • fps-test — WGC-measured presentation ≥ MinFps sustained ≥ SustainMs (API/size-agnostic).
//   • size-test — window ≥ MinSizePct % of its monitor (no WGC — for capture-unavailable setups).
//   • fps & size are toggled independently; when BOTH on, Combine ("and"/"or") joins them.
//   • Nothing on at all (no title, no fps, no size) ⇒ the first NEW window (legacy "any").
//
// Shutdown stays PROCESS-driven (the launch already waits on the process); SmartCapture only
// owns the reveal (startup) half.

#nullable enable

using System.Collections.Generic;
using LbApiHost.Host.Diag;
using Windows.Graphics.DirectX.Direct3D11;

namespace LbApiHost.Host.Gameplay;

internal sealed class SmartCaptureConfig
{
    public bool Enabled = true;
    // Detection is a boolean expression, NOT a single mode:
    //   detected(window) = titleMatch(if a title is set)  OR  ( fps-test [Combine] size-test )
    // • Title (wildcard): if set, a window whose title matches IS the game on its own (OR-priority).
    //   Empty ⇒ no title term. A window that does NOT match the title can still be caught by fps/size.
    // • fps / size: each toggled independently; when BOTH on, Combine ("and"/"or") joins them.
    // • Nothing on at all (no title, no fps, no size) ⇒ fall back to the first NEW window (old "any").
    public bool UseFps = true;         // fps-test enabled
    public bool UseSize = true;        // size-test enabled
    public string Combine = "and";     // how fps & size join when BOTH on: "and" | "or"
    public int MinFps = 25;
    public int SustainMs = 600;
    public int MinSizePct = 50;
    public string Title = "";          // wildcard, "" = no title term
    // The safety-max ("reveal anyway after") is NOT a SmartCapture key — it's the resolved LB "Startup
    // Load Delay" (per-emulator/game), computed by GameplaySettings.RevealMaxMs and passed to Start().
    public bool ShowBorder;            // keep the yellow WGC capture border (hidden ini opt-in; default off)
    public bool StopOnWindowClose;     // end the session when the game WINDOW closes (else process exit)
    public HashSet<string>? IgnoreExes; // process exe/bat names to skip (store clients) — resolved from the blacklist
    public List<string>? IgnoreTitles;  // window-title FRAGMENTS to skip (case-insensitive "contains", wildcard * ?)
    // Global-scan mode (store launches): the game isn't a descendant of anything we spawned, so instead
    // of a process tree we watch EVERY top-level window and keep only NEW ones — those not in Baseline
    // (the pre-launch snapshot) and not owned by LiteBox itself (cover / kiosk / main window).
    public bool GlobalScan;
    public HashSet<IntPtr>? Baseline;

    /// <summary>The keys stored in LiteBox.ini (global) / litebox-options.db (per-entity).</summary>
    public static readonly string[] Keys =
    {
        "SmartCaptureEnabled", "SmartCaptureUseFps", "SmartCaptureUseSize", "SmartCaptureCombine",
        "SmartCaptureMinFps", "SmartCaptureSustainMs", "SmartCaptureMinSizePct", "SmartCaptureTitle",
        "SmartCaptureStopOnWindowClose",
    };
}

internal static class SmartCapture
{
    private const int PollMs = 200;
    // After the cover reveals on the safety-max fallback (game not detected in time), keep hunting the game
    // window in the BACKGROUND up to this long (from launch) so a slow-loading game still populates the
    // detected-window (freeze-target / window-close signal / progress-bar detection time). Bounded so a game
    // that never shows a capturable window (WGC-blind exclusive fullscreen) doesn't scan for the whole session.
    private const int BackgroundDetectMaxMs = 180_000;   // 3 min

    private static Thread? _thread;
    private static volatile bool _run;

    /// <summary>The game window SmartCapture locked onto (Zero until detected, reset on Start/Stop). Store
    /// launches use its CLOSE as a fast, precise exit signal — far quicker than StoreProcessWatcher's
    /// multi-second process-gone debounce, so GAME OVER shows right when the game window disappears.</summary>
    public static IntPtr DetectedGameWindow { get; private set; }

    /// <summary>ms from SmartCapture start (≈ launch) to the ★ MATCH, or null if not yet detected /
    /// after Stop. Recorded to launch_history to extend the reveal ceiling + drive the progress bar.</summary>
    public static long? DetectedAtMs { get; private set; }

    /// <summary>True once a game window was detected AND it has since closed — the game really ended.</summary>
    public static bool GameWindowDetectedAndGone()
        => DetectedGameWindow != IntPtr.Zero && !WinScan.Alive(DetectedGameWindow);

    /// <summary>Start watching. Reveals via <paramref name="onReveal"/> once the game is detected
    /// rendering, then <paramref name="displayMs"/> (the Post-Launch Display Time) AFTER the render
    /// actually started — the detection window (SustainMs in fps mode) is subtracted because it
    /// already elapsed while confirming. Falls back to <paramref name="safetyMaxMs"/> if the game
    /// is never detected (exclusive fullscreen). No-op if disabled.</summary>
    public static void Start(int rootPid, SmartCaptureConfig cfg, int displayMs, int safetyMaxMs, Action onReveal, int fadeMs = 0)
    {
        Stop();
        if (!cfg.Enabled) return;
        _run = true;
        _thread = new Thread(() => Run(rootPid, cfg, Math.Max(0, displayMs), Math.Max(1000, safetyMaxMs), onReveal, Math.Max(0, fadeMs)))
        { IsBackground = true, Name = "LiteBox-smartcapture" };
        _thread.Start();
        Console.WriteLine($"[smartcapture] watching {(cfg.GlobalScan ? $"GLOBAL (baseline={cfg.Baseline?.Count ?? 0} pre-launch windows)" : $"tree(pid={rootPid})")} " +
                          $"title='{cfg.Title}'(OR-priority) fps={(cfg.UseFps ? $"on≥{cfg.MinFps}/{cfg.SustainMs}ms" : "off")} " +
                          $"size={(cfg.UseSize ? $"on≥{cfg.MinSizePct}%" : "off")} combine={(cfg.UseFps && cfg.UseSize ? cfg.Combine.ToUpperInvariant() : "n/a")} " +
                          $"displayTime={displayMs}ms max={safetyMaxMs}ms blacklist={cfg.IgnoreExes?.Count ?? 0} exes+{cfg.IgnoreTitles?.Count ?? 0} titles");
    }

    public static void Stop() { _run = false; _thread = null; DetectedGameWindow = IntPtr.Zero; DetectedAtMs = null; }

    private static void Run(int rootPid, SmartCaptureConfig cfg, int displayMs, int maxMs, Action onReveal, int fadeMs = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var meters = new Dictionary<IntPtr, (WgcFps meter, int sustainedMs)>();
        // One D3D11 device shared by every meter this run creates below (lazily — never created at all when
        // useFps is off) instead of each meter making its own; several candidate windows appearing close
        // together (a store launch, say) would otherwise stack up several live devices for no benefit. Disposed
        // in the finally, after its meters.
        IDirect3DDevice? sharedDevice = null;
        var announced = new HashSet<IntPtr>();   // log each window's skip/consider verdict once
        long lastPoll = 0;
        bool useFps = cfg.UseFps, useSize = cfg.UseSize;
        bool titleSet = !string.IsNullOrEmpty(cfg.Title) && cfg.Title != "*";
        bool combineOr = cfg.Combine.Equals("or", StringComparison.OrdinalIgnoreCase);
        bool nothingSet = !titleSet && !useFps && !useSize;   // → first NEW window (legacy "any")
        bool matchedFps = false;   // did THIS match rely on sustained fps? (drives the display-time subtraction)
        uint ownPid = (uint)Environment.ProcessId;   // global scan: exclude LiteBox's own windows (cover / kiosk / main)
        IntPtr metHwnd = IntPtr.Zero;
        long revealAt = -1;   // absolute ms: detection + (displayTime − detWindow), set when detected
        long detectAt = -1;   // absolute ms of the MATCH (for the "held since detection" log)
        bool revealed = false; // the cover has been closed (either on the hold, or on the safety-max fallback)

        try
        {
            while (_run)
            {
                long now = sw.ElapsedMilliseconds;
                int dt = (int)(now - lastPoll); lastPoll = now;

                if (metHwnd == IntPtr.Zero)
                {
                    WinScan.Win matched = default; string reason = "";
                    try
                    {
                        // Store launches: watch EVERY window and keep only NEW ones (not in the pre-launch
                        // baseline, not LiteBox's own). Emulator/app launches: the process tree of what we spawned.
                        var wins = cfg.GlobalScan
                            ? WinScan.AllTopLevelWindows()
                            : WinScan.TreeWindows(WinScan.BuildTree((uint)rootPid));
                        foreach (var w in wins)
                        {
                            if (cfg.GlobalScan)
                            {
                                if (w.Pid == ownPid) continue;                                        // our cover / kiosk / main window
                                if (cfg.Baseline != null && cfg.Baseline.Contains(w.Hwnd)) continue;   // already open before launch
                            }
                            // A store client (Steam/Epic/GOG/…) in the tree shows its OWN windows —
                            // launcher, "preparing to launch", overlay, login — which are NOT the game.
                            // Skip them so we detect the actual game window, not the store UI. The list
                            // is the editable global blacklist (Options → LiteBox-Options → Smart Capture).
                            if (cfg.IgnoreExes != null && cfg.IgnoreExes.Contains(w.Exe))
                            { if (announced.Add(w.Hwnd)) Console.WriteLine($"[smartcapture]   skip[blacklist exe] {WinInfo(w)}"); continue; }
                            // Same blacklist, its NON-exe/.bat lines: skip when the window TITLE contains one
                            // (case-insensitive, wildcard) — e.g. a launcher splash titled "Preparing game...".
                            if (cfg.IgnoreTitles is { Count: > 0 } && !string.IsNullOrEmpty(w.Title)
                                && cfg.IgnoreTitles.Exists(t => WinScan.WildcardContains(t, w.Title)))
                            { if (announced.Add(w.Hwnd)) Console.WriteLine($"[smartcapture]   skip[blacklist title] {WinInfo(w)}"); continue; }

                            // (1) TITLE — OR-priority: a title-matching window IS the game on its own.
                            if (titleSet && WinScan.WildcardMatch(cfg.Title, w.Title))
                            { matched = w; reason = $"title matches '{cfg.Title}'"; matchedFps = false; metHwnd = w.Hwnd; break; }

                            // (2) Nothing configured at all → first NEW window (legacy "any").
                            if (nothingSet)
                            { matched = w; reason = "any: first new window (no condition set)"; matchedFps = false; metHwnd = w.Hwnd; break; }

                            // (3) Only a title is set (no fps/size term) → a non-title window can't match; wait.
                            if (!useFps && !useSize) continue;

                            // (4) fps and/or size tests. size is instantaneous; fps needs sustained metering.
                            bool sizePass = false;
                            if (useSize)
                            {
                                var mon = WinScan.MonitorBounds(w.Hwnd);
                                long monArea = (long)mon.W * mon.H;
                                int pct = monArea > 0 ? (int)(w.Area * 100L / monArea) : 0;
                                if (announced.Add(w.Hwnd)) Console.WriteLine($"[smartcapture]   consider[size] {WinInfo(w)} mon={mon.W}x{mon.H} pct={pct}% (need ≥ {cfg.MinSizePct}%)");
                                sizePass = monArea > 0 && w.Area * 100L >= monArea * cfg.MinSizePct;
                            }
                            bool fpsPass = false; double lastFps = 0;
                            if (useFps)
                            {
                                if (!meters.TryGetValue(w.Hwnd, out var m))
                                {
                                    sharedDevice ??= WgcFps.CreateSharedDevice();
                                    var meter = WgcFps.TryCreate(w.Hwnd, cfg.ShowBorder, sharedDevice);
                                    if (meter == null)
                                    {
                                        if (announced.Add(w.Hwnd)) Console.WriteLine($"[smartcapture]   skip[no WGC capture] {WinInfo(w)}");
                                        if (!useSize) continue;   // fps required but unmeasurable, and no size term to decide
                                    }
                                    else
                                    {
                                        m = (meter, 0); meters[w.Hwnd] = m;
                                        Console.WriteLine($"[smartcapture]   consider[fps] {WinInfo(w)} — metering (need ≥{cfg.MinFps}fps for {cfg.SustainMs}ms)");
                                    }
                                }
                                if (meters.TryGetValue(w.Hwnd, out m))
                                {
                                    double secs = dt / 1000.0;
                                    lastFps = secs > 0 ? m.meter.TakeFrames() / secs : 0;
                                    int sustained = lastFps >= cfg.MinFps ? m.sustainedMs + dt : 0;
                                    meters[w.Hwnd] = (m.meter, sustained);
                                    Console.WriteLine($"[smartcapture]   fps exe='{w.Exe}' class='{w.Class}' title='{w.Title}' → {lastFps:0.#}fps sustained={sustained}ms");
                                    fpsPass = sustained >= cfg.SustainMs;
                                }
                            }

                            bool ok = (useFps && useSize) ? (combineOr ? (fpsPass || sizePass) : (fpsPass && sizePass))
                                                          : (useFps ? fpsPass : sizePass);
                            if (ok)
                            {
                                reason = (useFps && useSize)
                                    ? $"fps+size {(combineOr ? "OR" : "AND")}: fps={(fpsPass ? $"{lastFps:0.#}fps✓" : "✗")} size={(sizePass ? "✓" : "✗")}"
                                    : useFps ? $"fps: {lastFps:0.#}fps sustained ≥ {cfg.SustainMs}ms" : $"size: ≥ {cfg.MinSizePct}% of screen";
                                matchedFps = fpsPass; matched = w; metHwnd = w.Hwnd; break;
                            }
                        }
                        if (useFps && meters.Count > 0)
                            foreach (var h in new List<IntPtr>(meters.Keys))
                                if (!WinScan.Alive(h)) { try { meters[h].meter.Dispose(); } catch { } meters.Remove(h); }
                    }
                    catch (Exception ex) { Console.WriteLine("[smartcapture] scan error: " + ex.Message); }

                    if (metHwnd != IntPtr.Zero)
                    {
                        DetectedGameWindow = metHwnd;   // expose to StoreProcessWatcher for a window-close exit signal
                        detectAt = now;
                        DetectedAtMs = now;             // launch → detection latency, recorded to launch_history at exit
                        // The Post-Launch Display Time counts from when the game STARTED rendering — which
                        // was ~SustainMs before this fps confirmation (the detection window). So the hold
                        // after detection = displayMs − detWindow. The reveal then fires fadeMs earlier so
                        // the dissolve lands right at revealAt.
                        int detWindow = matchedFps ? cfg.SustainMs : 0;
                        int hold = Math.Max(0, displayMs - detWindow);
                        revealAt = now + hold;
                        Console.WriteLine($"[smartcapture] ★ MATCH @ {now}ms — {WinInfo(matched)}");
                        Console.WriteLine($"[smartcapture]   reason: {reason} (matchedFps={matchedFps})");
                        Console.WriteLine($"[smartcapture]   → hold {hold}ms ({displayMs}ms display − {detWindow}ms detWindow) → revealAt {revealAt}ms; fade last {fadeMs}ms (starts {revealAt - fadeMs}ms)");
                    }
                }

                // Detected within the cover window: fire fadeMs early so the reveal fade dissolves over the
                // LAST fadeMs and completes right at revealAt — total display time unchanged. Then we're done.
                if (!revealed && revealAt >= 0 && now >= revealAt - fadeMs)
                {
                    Console.WriteLine($"[smartcapture] REVEAL at {now}ms (held {now - detectAt}ms since detection, fade {fadeMs}ms → done ~{revealAt}ms)");
                    Fire(onReveal);
                    revealed = true;
                    break;   // normal path → StopOnWindowClose watch below
                }
                // Safety-max fallback: reveal the cover so the user isn't stuck staring at it, but DO NOT stop —
                // keep hunting the game window in the background (freeze-target / close-signal / progress-bar
                // time all need it). Drop the expensive WGC fps metering; background detection is size/title
                // only (cheap) and capped by BackgroundDetectMaxMs.
                if (!revealed && metHwnd == IntPtr.Zero && now >= maxMs)
                {
                    Console.WriteLine($"[smartcapture] safety max {maxMs}ms — revealing (no render detected yet); keeping LIGHT background detection up to {BackgroundDetectMaxMs}ms");
                    Fire(onReveal);
                    revealed = true;
                    useFps = false;
                    foreach (var kv in meters) { try { kv.Value.meter.Dispose(); } catch { } }
                    meters.Clear();
                }
                // Late detection AFTER the safety-max reveal: we captured the window — done scanning.
                if (revealed && metHwnd != IntPtr.Zero)
                {
                    Console.WriteLine($"[smartcapture] late detection @ {now}ms (cover already gone) — window 0x{metHwnd.ToInt64():X} captured for freeze/close/progress");
                    break;   // → StopOnWindowClose watch below
                }
                // Give up the background hunt after the hard cap (game never showed a capturable window).
                if (revealed && metHwnd == IntPtr.Zero && now >= BackgroundDetectMaxMs)
                {
                    Console.WriteLine($"[smartcapture] background detection give-up at {now}ms — no capturable game window appeared");
                    return;
                }
                Thread.Sleep(revealed ? 500 : PollMs);   // lighter cadence once we're only background-hunting
            }
            if (metHwnd == IntPtr.Zero) return;
        }
        finally
        {
            foreach (var kv in meters) { try { kv.Value.meter.Dispose(); } catch { } }
            try { (sharedDevice as IDisposable)?.Dispose(); } catch { }   // release the shared device AFTER its meters
        }

        // Stop-on-window-close: keep watching the game window; when it closes, force the process
        // tree to exit so the normal end-of-game flow (GAME OVER, cleanup) runs. Default off — the
        // launch already waits on process exit, which is the usual "game ended".
        if (cfg.StopOnWindowClose && metHwnd != IntPtr.Zero)
        {
            Console.WriteLine($"[smartcapture] watching hwnd=0x{metHwnd.ToInt64():X} for close (stop-on-window-close)");
            while (_run && WinScan.Alive(metHwnd)) Thread.Sleep(300);
            if (_run)
            {
                Console.WriteLine("[smartcapture] game window closed — ending process");
                try { using var p = System.Diagnostics.Process.GetProcessById(rootPid); p.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static void Fire(Action a) { try { a(); } catch { } }

    /// <summary>One-line dump of a window for the detection log — every field that could explain a match.</summary>
    private static string WinInfo(WinScan.Win w)
        => $"hwnd=0x{w.Hwnd.ToInt64():X} pid={w.Pid} exe='{w.Exe}' class='{w.Class}' size={w.Rect.W}x{w.Rect.H} area={w.Area} title='{w.Title}'";
}
