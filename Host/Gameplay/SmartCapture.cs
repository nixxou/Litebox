// SmartCapture: reveal the startup cover WHEN THE GAME IS ACTUALLY READY, not on a blind timer.
// Watches the launched process TREE and lifts the cover the moment a chosen detection condition
// is met, floored by StartupLoadDelay and backed by a safety-max (so a case WGC can't see —
// exclusive fullscreen — never hangs; it falls back to a timed reveal).
//
// Detection MODE (configurable — global, later per-emulator/game):
//   • "fps"  — a window renders: WGC-measured presentation ≥ MinFps sustained ≥ SustainMs.
//              Robust: API-agnostic, size-agnostic, works for GPU-light / windowed games.
//   • "size" — a window ≥ MinSizePct % of its monitor appears (no WGC — for setups where
//              capture is unavailable).
//   • "any"  — any (title-matching) top-level window of the tree appears.
// A TitleWildcard (case-insensitive *,? — empty = any) filters which windows count, so you can
// pin the actual game window by title.
//
// Shutdown stays PROCESS-driven (the launch already waits on the process); SmartCapture only
// owns the reveal (startup) half.

#nullable enable

using System.Collections.Generic;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Gameplay;

internal sealed class SmartCaptureConfig
{
    public bool Enabled = true;
    public string Mode = "fps";       // fps | size | any
    public int MinFps = 10;
    public int SustainMs = 600;
    public int MinSizePct = 50;
    public string Title = "";          // wildcard, "" = any window
    public bool StopOnWindowClose;     // end the session when the game WINDOW closes (else process exit)
    public HashSet<string>? IgnoreExes; // exe filenames to skip (store clients) — resolved from the global blacklist

    /// <summary>The keys stored in LiteBox.ini (global) / litebox-options.db (per-entity).</summary>
    public static readonly string[] Keys =
    {
        "SmartCaptureEnabled", "SmartCaptureMode", "SmartCaptureMinFps", "SmartCaptureSustainMs",
        "SmartCaptureMinSizePct", "SmartCaptureTitle", "SmartCaptureStopOnWindowClose",
    };
}

internal static class SmartCapture
{
    private const int PollMs = 200;

    private static Thread? _thread;
    private static volatile bool _run;

    /// <summary>Start watching. Reveals via <paramref name="onReveal"/> once the game is detected
    /// rendering, then <paramref name="displayMs"/> (the Post-Launch Display Time) AFTER the render
    /// actually started — the detection window (SustainMs in fps mode) is subtracted because it
    /// already elapsed while confirming. Falls back to <paramref name="safetyMaxMs"/> if the game
    /// is never detected (exclusive fullscreen). No-op if disabled.</summary>
    public static void Start(int rootPid, SmartCaptureConfig cfg, int displayMs, int safetyMaxMs, Action onReveal)
    {
        Stop();
        if (!cfg.Enabled) return;
        _run = true;
        _thread = new Thread(() => Run(rootPid, cfg, Math.Max(0, displayMs), Math.Max(1000, safetyMaxMs), onReveal))
        { IsBackground = true, Name = "LiteBox-smartcapture" };
        _thread.Start();
        Console.WriteLine($"[smartcapture] watching pid={rootPid} mode={cfg.Mode} minFps={cfg.MinFps} sustain={cfg.SustainMs}ms " +
                          $"minSize={cfg.MinSizePct}% title='{cfg.Title}' displayTime={displayMs}ms max={safetyMaxMs}ms");
    }

    public static void Stop() { _run = false; _thread = null; }

    private static void Run(int rootPid, SmartCaptureConfig cfg, int displayMs, int maxMs, Action onReveal)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var meters = new Dictionary<IntPtr, (WgcFps meter, int sustainedMs)>();
        long lastPoll = 0;
        bool fps = cfg.Mode.Equals("fps", StringComparison.OrdinalIgnoreCase);
        bool size = cfg.Mode.Equals("size", StringComparison.OrdinalIgnoreCase);
        IntPtr metHwnd = IntPtr.Zero;
        long revealAt = -1;   // absolute ms: render-start + displayTime, set when detected

        try
        {
            while (_run)
            {
                long now = sw.ElapsedMilliseconds;
                int dt = (int)(now - lastPoll); lastPoll = now;

                if (metHwnd == IntPtr.Zero)
                {
                    try
                    {
                        var tree = WinScan.BuildTree((uint)rootPid);
                        var wins = WinScan.TreeWindows(tree);
                        foreach (var w in wins)
                        {
                            // A store client (Steam/Epic/GOG/…) in the tree shows its OWN windows —
                            // launcher, "preparing to launch", overlay, login — which are NOT the game.
                            // Skip them so we detect the actual game window, not the store UI. The list
                            // is the editable global blacklist (Options → LiteBox-Options → Smart Capture).
                            if (cfg.IgnoreExes != null && cfg.IgnoreExes.Contains(w.Exe)) continue;
                            if (!WinScan.WildcardMatch(cfg.Title, w.Title)) continue;
                            if (!fps && !size) { metHwnd = w.Hwnd; break; }   // "any"
                            if (size)
                            {
                                var mon = WinScan.MonitorBounds(w.Hwnd);
                                long monArea = (long)mon.W * mon.H;
                                if (monArea > 0 && w.Area * 100L >= monArea * cfg.MinSizePct) { metHwnd = w.Hwnd; break; }
                            }
                            else // fps: attach a meter, accumulate sustained-above-threshold time.
                            {
                                if (!meters.TryGetValue(w.Hwnd, out var m))
                                {
                                    var meter = WgcFps.TryCreate(w.Hwnd);
                                    if (meter == null) continue;
                                    m = (meter, 0); meters[w.Hwnd] = m;
                                }
                                double secs = dt / 1000.0;
                                double curFps = secs > 0 ? m.meter.TakeFrames() / secs : 0;
                                int sustained = curFps >= cfg.MinFps ? m.sustainedMs + dt : 0;
                                meters[w.Hwnd] = (m.meter, sustained);
                                if (sustained >= cfg.SustainMs) { metHwnd = w.Hwnd; break; }
                            }
                        }
                        if (fps && meters.Count > 0)
                            foreach (var h in new List<IntPtr>(meters.Keys))
                                if (!WinScan.Alive(h)) { try { meters[h].meter.Dispose(); } catch { } meters.Remove(h); }
                    }
                    catch { }

                    if (metHwnd != IntPtr.Zero)
                    {
                        // The display time counts from when the game STARTED rendering, not from
                        // this confirmation. In fps mode the render started SustainMs ago (that
                        // window was spent confirming), so subtract it. size/any are instantaneous.
                        int detWindow = fps ? cfg.SustainMs : 0;
                        revealAt = now + Math.Max(0, displayMs - detWindow);
                        Console.WriteLine($"[smartcapture] rendering detected at {now}ms (hwnd=0x{metHwnd.ToInt64():X}) — " +
                                          $"revealing at {revealAt}ms ({displayMs}ms display − {detWindow}ms det window after detection)");
                    }
                }

                if (revealAt >= 0 && now >= revealAt)
                {
                    Console.WriteLine($"[smartcapture] revealing at {now}ms");
                    Fire(onReveal);
                    break;
                }
                if (metHwnd == IntPtr.Zero && now >= maxMs)
                {
                    Console.WriteLine($"[smartcapture] safety max {maxMs}ms — revealing (fallback, no render detected)");
                    Fire(onReveal);
                    return;
                }
                Thread.Sleep(PollMs);
            }
            if (metHwnd == IntPtr.Zero) return;
        }
        finally { foreach (var kv in meters) { try { kv.Value.meter.Dispose(); } catch { } } }

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
}
