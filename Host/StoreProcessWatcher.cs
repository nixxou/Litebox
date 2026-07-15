// Watches for a store game's process so LiteBox can hide the "game running" screen when it exits.
// LiteBox doesn't spawn store games (Galaxy/Steam own the process, launched via a .lnk / steam:// URI),
// so there's no Process handle to WaitForExit on. Instead we poll for any process whose image path
// lives under the game's install directory: wait for it to APPEAR (the client may take a while to
// start it), then wait for all such processes to be GONE = exited.
//
// Process image paths are read via QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION,
// which works cross-bitness (a 64-bit LiteBox reading a 32-bit game) and for same-user processes
// without elevation — unlike Process.MainModule, which throws across bitness.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LbApiHost.Host;

internal static class StoreProcessWatcher
{
    // The store's main client process (the one a launch may spin up). We deliberately target only the
    // top-level client (killing it cascades to its helpers); GalaxyClientService (a Windows service) is
    // never touched.
    private static string[] ClientNames(StoreKind kind) => kind switch
    {
        StoreKind.Gog   => new[] { "GalaxyClient" },
        StoreKind.Steam => new[] { "steam" },
        StoreKind.Epic  => new[] { "EpicGamesLauncher" },
        StoreKind.Uplay => new[] { "UbisoftConnect", "upc" },
        StoreKind.Ea    => new[] { "EADesktop", "Origin" },
        _               => Array.Empty<string>(),
    };

    /// <summary>PIDs of the store's client processes running right now (snapshot before a launch).</summary>
    public static HashSet<int> SnapshotClients(StoreKind kind)
    {
        var set = new HashSet<int>();
        foreach (var name in ClientNames(kind))
            foreach (var p in Process.GetProcessesByName(name))
            { try { set.Add(p.Id); } catch { } finally { try { p.Dispose(); } catch { } } }
        return set;
    }

    // A hard Kill can corrupt a store client's session (the EA app then re-prompts for login next launch).
    // So we ASK it to close cleanly first — Steam's real -shutdown command, WM_CLOSE to the others' windows
    // (like clicking the X) — wait a few seconds, and only Kill the ones that ignore it (tray-minimisers /
    // hung) as a fallback. The wait + fallback run on a background thread so the exit flow (GAME OVER, return
    // to the frontend) isn't held up.
    private const int GracefulWaitMs = 6000;

    /// <summary>Close (gracefully, kill as fallback) the store's client processes that are NOT in
    /// <paramref name="before"/> — i.e. the ones THIS launch started. A client the user already had open is
    /// left alone.</summary>
    public static void KillClientsStartedSince(StoreKind kind, HashSet<int> before)
    {
        if (before == null) return;
        CloseOrKill(kind, ClientPids(kind, before), "started-since");
    }

    /// <summary>Close (gracefully, kill as fallback) ALL of the store's client processes — including one the
    /// user already had running. Used when KillStoreLauncherEvenIfPreRunning is set.</summary>
    public static void KillAllClients(StoreKind kind)
        => CloseOrKill(kind, ClientPids(kind, null), "all");

    // Each target carries its StartTime alongside the PID: a bare PID is NOT a stable process identity to
    // hold across the grace-wait — Windows can hand an exited client's PID to an unrelated process before we
    // poll again, so we re-verify (pid + startTime) before ever treating one as alive or killing it.
    private static List<(string name, int pid, DateTime start)> ClientPids(StoreKind kind, HashSet<int>? exclude)
    {
        var list = new List<(string, int, DateTime)>();
        foreach (var name in ClientNames(kind))
            foreach (var p in Process.GetProcessesByName(name))
            { try { if (exclude == null || !exclude.Contains(p.Id)) list.Add((name, p.Id, SafeStart(p))); } catch { } finally { try { p.Dispose(); } catch { } } }
        return list;
    }

    /// <summary>A process's StartTime, or DateTime.MinValue when it can't be read (exited/elevated). Same-user
    /// store clients read fine, so MinValue only stands in for "unknown" — never a real client's identity.</summary>
    private static DateTime SafeStart(Process p) { try { return p.StartTime; } catch { return DateTime.MinValue; } }

    private static void CloseOrKill(StoreKind kind, List<(string name, int pid, DateTime start)> targets, string tag)
    {
        if (targets.Count == 0) return;

        // 1. Graceful signal (instant, non-blocking).
        if (kind == StoreKind.Steam)
        {
            string? exe = null;
            foreach (var t in targets) { var pth = ImagePath(t.pid); if (!string.IsNullOrEmpty(pth)) { exe = pth; break; } }
            if (!string.IsNullOrEmpty(exe))
                try { Process.Start(new ProcessStartInfo(exe!, "-shutdown") { UseShellExecute = false, CreateNoWindow = true }); StoreTrace.Log("store close: steam -shutdown (graceful)"); }
                catch (Exception ex) { StoreTrace.Log("store close: steam -shutdown failed: " + ex.Message); }
        }
        foreach (var t in targets) PostCloseToWindows(t.pid, t.name);

        // 2. Wait for a clean exit, then Kill the survivors — on a background thread so GAME OVER / the return
        //    to the frontend isn't delayed by the grace period.
        new System.Threading.Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < GracefulWaitMs)
            {
                bool anyAlive = false;
                foreach (var t in targets) if (IsAlive(t.pid, t.start)) { anyAlive = true; break; }
                if (!anyAlive) break;
                System.Threading.Thread.Sleep(200);
            }
            foreach (var t in targets)
            {
                if (!IsAlive(t.pid, t.start)) { StoreTrace.Log($"store close: {t.name} pid={t.pid} exited cleanly ({tag})"); continue; }
                try
                {
                    using var p = Process.GetProcessById(t.pid);
                    // Re-check identity in the same instant we Kill: the PID could have been reused between the
                    // IsAlive poll above and now. A matching PID alone is not enough — the StartTime must match too.
                    if (SafeStart(p) != t.start) { StoreTrace.Log($"store close: {t.name} pid={t.pid} was reused by another process — not killing ({tag})"); continue; }
                    p.Kill(); StoreTrace.Log($"store close: {t.name} pid={t.pid} ignored close — killed ({tag})");
                }
                catch (Exception ex) { StoreTrace.Log($"store close: kill {t.name} pid={t.pid} failed: {ex.Message}"); }
            }
        }) { IsBackground = true, Name = "litebox-store-close" }.Start();
    }

    // A bare PID is not a safe identity once a grace period has elapsed: Windows can reassign an exited
    // process's PID to an unrelated new one. StartTime (unique per real process lifetime) re-verifies we're
    // still looking at the SAME process before treating it as alive/killable.
    private static bool IsAlive(int pid, DateTime start)
    { try { using var p = Process.GetProcessById(pid); return !p.HasExited && SafeStart(p) == start; } catch { return false; } }

    /// <summary>PostMessage WM_CLOSE to every visible top-level window owned by <paramref name="pid"/> — the
    /// same as clicking the X. Clean-exiting clients quit; tray-minimisers stay up and get killed as the
    /// fallback. Async: the app handles it on its own thread while we poll for the real exit.</summary>
    private static void PostCloseToWindows(int pid, string name)
    {
        // WinScan.AllTopLevelWindows() already does the EnumWindows/IsWindowVisible/GetWindowThreadProcessId
        // pass (shared with WindowHider and the RenderProbe diagnostic) — no third copy of that trio + loop here.
        int posted = 0;
        try
        {
            foreach (var w in Diag.WinScan.AllTopLevelWindows())
            {
                try { if (w.Pid == (uint)pid) { PostMessage(w.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); posted++; } }
                catch { }
            }
        }
        catch { }
        if (posted > 0) StoreTrace.Log($"store close: WM_CLOSE → {name} pid={pid} ({posted} window(s))");
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_CLOSE = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    // CharSet.Unicode is REQUIRED: this is the W (wide) variant, so the StringBuilder
    // must be marshalled as UTF-16. Without it the default (ANSI) marshalling reads the
    // wide buffer back as single-byte → the path collapses to its first character ("C")
    // at the first embedded NUL, so EVERY StartsWith(installDir) check fails and no game
    // process is ever detected.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buf, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Detection strategy (install-dir process FIRST, focus only as a backstop):
    //   • Wait for any process whose image lives under installDir to APPEAR, then end
    //     once they're ALL gone for GoneConfirmSeconds. No minimum run duration — a
    //     30-second indie session ends on the process alone, and it works on a 2nd
    //     monitor where LiteBox never lost the foreground (the old focus path failed
    //     both). The gone-debounce bridges a launcher.exe → game handoff inside the
    //     install folder.
    //   • Focus is a BACKSTOP only for when NO install-dir process is ever seen
    //     (installDir unknown/wrong, or the game truly runs outside it). We give the
    //     store client a startup grace before trusting that signal, so clicking back
    //     to LiteBox while Epic/Steam is still spawning the game doesn't end early.
    private const int GoneConfirmSeconds  = 8;    // install-dir processes must stay gone this long
    private const int FocusGraceSeconds   = 25;   // min wait before the focus backstop can fire

    /// <summary>Block until the store game is over. regainedFocus() should return true once LiteBox has
    /// LOST and then REGAINED the foreground since the launch (the reliable "user is back" signal).
    /// Returns true if a game process was actually observed (for play-time billing). Never hangs forever
    /// once focus is regained; otherwise the overlay's click-to-dismiss is the backstop.</summary>
    ///
    /// <remarks>
    /// Scan strategy (cheap once locked on): a full <see cref="Process.GetProcesses"/> sweep is expensive,
    /// so we only do it while HUNTING for the game's process. Once we've matched a PID under installDir we
    /// LOCK ONTO it and each poll only re-checks THAT one PID (a single OpenProcess) — no system-wide sweep.
    /// We resume full sweeps only if the tracked PID is lost, to catch a launcher.exe → game.exe handoff
    /// inside the install folder (a different PID, still under installDir). If a re-sweep then finds nothing
    /// either, the gone-debounce elapses and the game is over.
    /// </remarks>
    public static bool WaitForGame(string? installDir, Func<bool>? regainedFocus = null, Func<bool>? gameWindowGone = null, Action<int>? onGamePid = null)
    {
        string? dir = string.IsNullOrWhiteSpace(installDir) ? null : installDir!.TrimEnd('\\', '/') + "\\";
        DateTime startedAt = DateTime.UtcNow;
        DateTime? firstSeen = null, lastRunningAt = null;
        DateTime lastDiag = DateTime.MinValue; int diagCount = 0;
        int trackedPid = -1;   // PID we're locked onto; -1 = hunting (full sweeps)
        bool Regained() { try { return regainedFocus?.Invoke() ?? false; } catch { return false; } }
        bool WindowGone() { try { return gameWindowGone?.Invoke() ?? false; } catch { return false; } }

        while (true)
        {
            var now = DateTime.UtcNow;
            bool running = false;
            if (dir != null)
            {
                // Locked on? Cheap single-PID check first — no full sweep while the game runs.
                if (trackedPid != -1)
                {
                    if (PidStillUnder(trackedPid, dir)) running = true;
                    else { StoreTrace.Log($"watch: tracked pid={trackedPid} gone, re-scanning"); trackedPid = -1; }
                }
                // Hunting (never locked, or just lost the PID): one full sweep to (re)acquire.
                // This bridges a launcher.exe → game.exe handoff (new PID, same install folder).
                if (trackedPid == -1)
                {
                    int pid = FirstPidUnder(dir);
                    if (pid != -1)
                    {
                        trackedPid = pid; running = true; StoreTrace.Log($"watch: locked onto pid={pid} under install dir");
                        // Hand the game's PID to the caller so it can arm pause on the actual game process
                        // (fires again after a launcher.exe → game.exe handoff so pause re-targets the game).
                        try { onGamePid?.Invoke(pid); } catch { }
                    }
                }
            }

            if (running)
            {
                if (firstSeen == null) StoreTrace.Log("watch: game process seen under install dir");
                firstSeen ??= now;
                lastRunningAt = now;
            }
            else if (firstSeen != null)
            {
                // Saw the game's process under installDir, and it's now gone. Normally we debounce
                // GoneConfirmSeconds to bridge a launcher.exe → game.exe handoff. But if SmartCapture
                // locked onto the actual game WINDOW and it has closed, the game truly ended (a handoff
                // keeps a game window up) — end at once, no debounce, so GAME OVER isn't ~8s late.
                if (WindowGone())
                { StoreTrace.Log("watch: end (game window closed — SmartCapture signal, skipping gone-debounce)"); return true; }

                // Otherwise the primary, focus-independent end signal: all install-dir processes gone
                // for the full debounce (covers games SmartCapture never locked a window onto).
                if (lastRunningAt != null && (now - lastRunningAt.Value).TotalSeconds >= GoneConfirmSeconds)
                { StoreTrace.Log("watch: end (install-dir process gone)"); return true; }
            }
            else
            {
                // No install-dir process seen yet. Periodically dump what IS running so
                // we can see why (game exe outside installDir, or unreadable/elevated).
                if (diagCount < 5 && (now - lastDiag).TotalSeconds >= 8)
                { lastDiag = now; diagCount++; DumpCandidates(installDir); }

                // Fall back to focus, but only after a startup grace so we don't end
                // while the store client is still bringing the game up.
                if ((now - startedAt).TotalSeconds >= FocusGraceSeconds && Regained())
                { StoreTrace.Log("watch: end (focus backstop; no install-dir process seen)"); return false; }
            }
            System.Threading.Thread.Sleep(1500);
        }
    }

    /// <summary>Diagnostic: logs why no process matched under installDir — the count of
    /// unreadable PIDs (elevation/cross-integrity) and any readable process whose image
    /// lives under the store ROOT (installDir's parent, e.g. "…\Epic Games"), so a real
    /// game exe sitting in a sibling/other subfolder is visible vs. the resolved dir.</summary>
    private static void DumpCandidates(string? installDir)
    {
        try
        {
            string? root = null;
            try { root = string.IsNullOrWhiteSpace(installDir) ? null : System.IO.Directory.GetParent(installDir!.TrimEnd('\\', '/'))?.FullName; }
            catch { }
            string rootSep = root == null ? "" : root.TrimEnd('\\', '/') + "\\";

            int total = 0, unreadable = 0;
            var hits = new List<string>();
            var procs = Process.GetProcesses();
            try
            {
                foreach (var p in procs)
                {
                    total++;
                    string? path = null;
                    try { path = ImagePath(p.Id); } catch { }
                    if (string.IsNullOrEmpty(path)) { unreadable++; continue; }
                    if (rootSep.Length > 0 && path!.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase))
                        hits.Add(path!);
                }
            }
            finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }

            StoreTrace.Log($"watch-diag: waiting under '{installDir}' — {total} procs, {unreadable} unreadable, "
                + $"{hits.Count} under store root '{root}'"
                + (hits.Count > 0 ? ": " + string.Join(" | ", hits.GetRange(0, Math.Min(hits.Count, 6))) : ""));
        }
        catch (Exception ex) { StoreTrace.Log("watch-diag error: " + ex.Message); }
    }

    /// <summary>Full system sweep: returns the PID of the first process whose image lives under
    /// <paramref name="dirWithSep"/>, or -1 if none. Expensive (enumerates every process) — only used
    /// while hunting for the game, never once a PID is locked on.</summary>
    private static int FirstPidUnder(string dirWithSep)
    {
        var procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                string? path = null;
                try { path = ImagePath(p.Id); } catch { }
                if (!string.IsNullOrEmpty(path) && path!.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
                    return p.Id;
            }
            return -1;
        }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
    }

    /// <summary>Cheap check for a single PID: is it still alive AND still imaged under
    /// <paramref name="dirWithSep"/>? One OpenProcess, no system-wide enumeration. A dead PID returns
    /// null from <see cref="ImagePath"/> (handle won't open) → false. We also re-verify the path because
    /// PIDs can be recycled to an unrelated process.</summary>
    private static bool PidStillUnder(int pid, string dirWithSep)
    {
        string? path = null;
        try { path = ImagePath(pid); } catch { }
        return !string.IsNullOrEmpty(path) && path!.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
