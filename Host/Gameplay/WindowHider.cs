// "Hide All Windows that are not in Exclusive Fullscreen Mode" (LB emulator/game field
// HideAllNonExclusiveFullscreenWindows). Hides every top-level alt-tab window EXCEPT the game's own,
// LiteBox's own (our overlays / GUI), and the shell/desktop — so a bordered or borderless game sits on a
// clean screen with nothing peeking around it. A true exclusive-fullscreen game isn't in the window list
// anyway, so this is a no-op cleanup for those and a real one for windowed games. Tracks what it hid and
// restores it on game exit; Restore always runs from the launch finally so nothing stays hidden.

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LbApiHost.Host.Gameplay;

internal static class WindowHider
{
    // EnumWindows/IsWindowVisible/GetWindowThreadProcessId/GetClassName + the visible-top-level enumeration
    // loop already live in Host.Diag.WinScan (shared with the RenderProbe diagnostic and SmartCapture) — Hide()
    // reuses WinScan.AllTopLevelWindows() instead of a third copy of that P/Invoke set and loop. Only the checks
    // WinScan.Win doesn't carry (owner-popup, tool-window style) keep their own declarations here.
    // GetWindowThreadProcessId stays because Activate() below still calls it directly.
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    private const int SW_HIDE = 0, SW_RESTORE = 9, SW_SHOWNA = 8;

    /// <summary>Force a window to the foreground and un-minimise it. The completion of aggressive hiding:
    /// the game was kept behind our cover during load, so at the reveal it must be made active or it never
    /// gets focus/input. No-op on a zero handle.</summary>
    public static void Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            // Do NOT yank the foreground onto a window that already has it. An exclusive-fullscreen game owns
            // the display + focus during load, so it is already the foreground window even while our cover is
            // topmost over it; a redundant SetForegroundWindow there can knock it out of exclusive mode and
            // glitch its resolution switch. Only step in when the game was genuinely kept behind (something
            // else holds the foreground) — that's the only case aggressive hiding actually needs the nudge.
            IntPtr fg = GetForegroundWindow();
            if (fg == hWnd) { Console.WriteLine("[windowhide] reveal: game already foreground — not re-activating (exclusive-fullscreen safe)"); return; }
            GetWindowThreadProcessId(hWnd, out uint gamePid);
            if (fg != IntPtr.Zero && gamePid != 0)
            {
                GetWindowThreadProcessId(fg, out uint fgPid);
                if (fgPid == gamePid) { Console.WriteLine("[windowhide] reveal: game process already foreground — not re-activating"); return; }
            }
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            Console.WriteLine($"[windowhide] activated game window 0x{hWnd.ToInt64():X}");
        }
        catch (Exception ex) { Console.WriteLine("[windowhide] activate failed: " + ex.Message); }
    }

    private static readonly object _lock = new();
    private static readonly List<IntPtr> _hidden = new();
    private static bool _armed;

    /// <summary>Hide every non-game, non-LiteBox, non-shell top-level window (idempotent). keepPid = the
    /// game's process id (its own windows stay).</summary>
    public static void Hide(int keepPid)
    {
        lock (_lock)
        {
            if (_armed) return;
            _armed = true;
            uint ownPid = (uint)Environment.ProcessId;
            var toHide = new List<IntPtr>();
            try
            {
                // WinScan.AllTopLevelWindows() already does the EnumWindows/IsWindowVisible pass (visible
                // top-level windows only); only the owner-popup / tool-window / titleless / class checks below
                // are specific to what Hide() must exclude.
                foreach (var w in Diag.WinScan.AllTopLevelWindows())
                {
                    var h = w.Hwnd;
                    try
                    {
                        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) continue;               // owned popups
                        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) continue;
                        if (w.Title.Length == 0) continue;                                 // titleless = not an app window
                        if (w.Pid == keepPid || w.Pid == ownPid) continue;                // the game / us
                        switch (w.Class)
                        {
                            case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":
                            case "Progman": case "WorkerW": case "Button":
                                continue;                                                   // taskbar / desktop / start
                        }
                        toHide.Add(h);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Console.WriteLine("[windowhide] enum failed: " + ex.Message); }

            foreach (var h in toHide) { try { if (ShowWindow(h, SW_HIDE)) _hidden.Add(h); } catch { } }
            Console.WriteLine($"[windowhide] hid {_hidden.Count} non-game window(s)");
        }
    }

    /// <summary>Wait (background) until the game process has a top-level window, then hide the rest. Used by
    /// the emulator/direct-exe path where the window doesn't exist yet at spawn.</summary>
    public static void ArmFor(int keepPid, Func<IntPtr> mainWindow)
    {
        new Thread(() =>
        {
            try { for (int i = 0; i < 40 && mainWindow() == IntPtr.Zero; i++) Thread.Sleep(250); } catch { }   // ≤10s
            Hide(keepPid);
        })
        { IsBackground = true, Name = "litebox-windowhide" }.Start();
    }

    /// <summary>Show everything we hid (idempotent), without stealing focus from the game.</summary>
    public static void Restore()
    {
        lock (_lock)
        {
            if (!_armed) return;
            _armed = false;
            int n = _hidden.Count;
            foreach (var h in _hidden) { try { ShowWindow(h, SW_SHOWNA); } catch { } }
            _hidden.Clear();
            if (n > 0) Console.WriteLine($"[windowhide] restored {n} window(s)");
        }
    }
}
