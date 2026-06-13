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

    /// <summary>Kill the store's client processes that are NOT in <paramref name="before"/> — i.e. the
    /// ones THIS launch started. A client the user already had open (its PID is in the snapshot) is left
    /// running.</summary>
    public static void KillClientsStartedSince(StoreKind kind, HashSet<int> before)
    {
        if (before == null) return;
        foreach (var name in ClientNames(kind))
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!before.Contains(p.Id)) { p.Kill(); StoreTrace.Log($"killed store launcher {name} pid={p.Id}"); }
                }
                catch (Exception ex) { StoreTrace.Log($"kill {name} pid={p.Id} failed: {ex.Message}"); }
                finally { try { p.Dispose(); } catch { } }
            }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buf, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // A store launch can route through a launcher.exe that exits immediately (handing off to the real
    // game, or the game runs from outside installDir). So we DON'T treat a short-lived process under
    // installDir as "the game ended": only a process that ran for a real while (≥ MinRealRunSeconds)
    // and is then gone counts as a genuine session ending on its own. For shorter runs we wait until
    // LiteBox is back in the foreground (regainedFocus) before declaring the game over.
    private const int MinRealRunSeconds = 60;   // a process under installDir running this long = a real session
    private const int GoneConfirmSeconds = 4;   // process must stay gone this long (bridges launcher→game gaps)

    /// <summary>Block until the store game is over. regainedFocus() should return true once LiteBox has
    /// LOST and then REGAINED the foreground since the launch (the reliable "user is back" signal).
    /// Returns true if a game process was actually observed (for play-time billing). Never hangs forever
    /// once focus is regained; otherwise the overlay's click-to-dismiss is the backstop.</summary>
    public static bool WaitForGame(string? installDir, Func<bool>? regainedFocus = null)
    {
        string? dir = string.IsNullOrWhiteSpace(installDir) ? null : installDir!.TrimEnd('\\', '/') + "\\";
        DateTime? firstSeen = null, lastRunningAt = null;
        bool seenLong = false;
        bool Regained() { try { return regainedFocus?.Invoke() ?? false; } catch { return false; } }

        while (true)
        {
            bool running = dir != null && AnyProcessUnder(dir);
            if (running)
            {
                var now = DateTime.UtcNow;
                firstSeen ??= now;
                lastRunningAt = now;
                if (!seenLong && (now - firstSeen.Value).TotalSeconds >= MinRealRunSeconds)
                { seenLong = true; StoreTrace.Log($"watch: game running ≥{MinRealRunSeconds}s (real session)"); }
            }
            else
            {
                // Real session whose process is now gone (debounced to bridge a launcher→game gap):
                // end on the process alone — works even if LiteBox kept the foreground (2nd monitor).
                if (seenLong && lastRunningAt != null && (DateTime.UtcNow - lastRunningAt.Value).TotalSeconds >= GoneConfirmSeconds)
                { StoreTrace.Log("watch: end (process gone after a real session)"); return true; }
                // Short launch / launcher.exe that didn't stay, or game running outside installDir:
                // wait until LiteBox is back in front before declaring it over.
                if (Regained())
                { StoreTrace.Log($"watch: end (focus regained; seenProcess={firstSeen != null})"); return firstSeen != null; }
            }
            System.Threading.Thread.Sleep(1500);
        }
    }

    private static bool AnyProcessUnder(string dirWithSep)
    {
        var procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                string? path = null;
                try { path = ImagePath(p.Id); } catch { }
                if (!string.IsNullOrEmpty(path) && path!.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
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
