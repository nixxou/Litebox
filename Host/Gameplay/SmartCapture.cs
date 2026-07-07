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
}

internal static class SmartCapture
{
    private const int PollMs = 200;

    private static Thread? _thread;
    private static volatile bool _run;

    /// <summary>Start watching. Reveals via <paramref name="onReveal"/> once (condition met and
    /// past <paramref name="minFloorMs"/>, or the safety max). No-op if disabled.</summary>
    public static void Start(int rootPid, SmartCaptureConfig cfg, int minFloorMs, int safetyMaxMs, Action onReveal)
    {
        Stop();
        if (!cfg.Enabled) return;
        _run = true;
        _thread = new Thread(() => Run(rootPid, cfg, Math.Max(0, minFloorMs), Math.Max(1000, safetyMaxMs), onReveal))
        { IsBackground = true, Name = "LiteBox-smartcapture" };
        _thread.Start();
        Console.WriteLine($"[smartcapture] watching pid={rootPid} mode={cfg.Mode} minFps={cfg.MinFps} sustain={cfg.SustainMs}ms " +
                          $"minSize={cfg.MinSizePct}% title='{cfg.Title}' floor={minFloorMs}ms max={safetyMaxMs}ms");
    }

    public static void Stop() { _run = false; _thread = null; }

    private static void Run(int rootPid, SmartCaptureConfig cfg, int floorMs, int maxMs, Action onReveal)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var meters = new Dictionary<IntPtr, (WgcFps meter, int sustainedMs)>();
        long lastPoll = 0;
        bool fps = cfg.Mode.Equals("fps", StringComparison.OrdinalIgnoreCase);
        bool size = cfg.Mode.Equals("size", StringComparison.OrdinalIgnoreCase);

        try
        {
            while (_run && sw.ElapsedMilliseconds < maxMs)
            {
                long now = sw.ElapsedMilliseconds;
                int dt = (int)(now - lastPoll); lastPoll = now;
                bool met = false;

                try
                {
                    var tree = WinScan.BuildTree((uint)rootPid);
                    var wins = WinScan.TreeWindows(tree);

                    foreach (var w in wins)
                    {
                        if (!WinScan.WildcardMatch(cfg.Title, w.Title)) continue;
                        if (!fps && !size) { met = true; break; }   // "any"
                        if (size)
                        {
                            var mon = WinScan.MonitorBounds(w.Hwnd);
                            long monArea = (long)mon.W * mon.H;
                            if (monArea > 0 && w.Area * 100L >= monArea * cfg.MinSizePct) { met = true; break; }
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
                            if (sustained >= cfg.SustainMs) { met = true; break; }
                        }
                    }
                    // Drop meters for windows that vanished.
                    if (fps && meters.Count > 0)
                        foreach (var h in new List<IntPtr>(meters.Keys))
                            if (!WinScan.Alive(h)) { try { meters[h].meter.Dispose(); } catch { } meters.Remove(h); }
                }
                catch { }

                if (met && now >= floorMs)
                {
                    Console.WriteLine($"[smartcapture] condition met at {now}ms — revealing");
                    Fire(onReveal);
                    return;
                }
                Thread.Sleep(PollMs);
            }
            if (_run) { Console.WriteLine($"[smartcapture] safety max {maxMs}ms — revealing (fallback)"); Fire(onReveal); }
        }
        finally { foreach (var kv in meters) { try { kv.Value.meter.Dispose(); } catch { } } }
    }

    private static void Fire(Action a) { try { a(); } catch { } }
}
