// Small shared Win32 helpers: build a process TREE (pid + descendants) and enumerate its
// visible top-level windows. Used by the RenderProbe diagnostic and by SmartCapture.

#nullable enable

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LbApiHost.Host.Diag;

internal static class WinScan
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    private delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public int W => R - L; public int H => B - T; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rc; public RECT work; public uint flags; }

    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID;
        public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase;
        public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    public readonly struct Win
    {
        public readonly IntPtr Hwnd; public readonly uint Pid; public readonly RECT Rect; public readonly string Title; public readonly string Class; public readonly string Exe;
        public Win(IntPtr h, uint pid, RECT r, string title, string cls, string exe) { Hwnd = h; Pid = pid; Rect = r; Title = title; Class = cls; Exe = exe; }
        public int Area => Rect.W * Rect.H;
    }

    /// <summary>Store-CLIENT executables whose own windows are never the game — the launcher UI,
    /// "preparing to launch" splash, login, the WebView helper, the overlay browser. The game-window
    /// detection (SmartCapture) skips them so it doesn't reveal the cover on the store client instead
    /// of the actual game (which runs as its own, differently-named .exe). Filename match, case-insensitive.</summary>
    public static readonly HashSet<string> StoreClientExes = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe",
        "galaxyclient.exe", "galaxyclientservice.exe",
        "battle.net.exe",
        "origin.exe", "eadesktop.exe", "eabackgroundservice.exe", "ealaunchhelper.exe",
        "upc.exe", "ubisoftconnect.exe", "uplaywebcore.exe",
    };

    public static bool IsStoreClientExe(string? exe) => !string.IsNullOrEmpty(exe) && StoreClientExes.Contains(exe);

    /// <summary>root pid + all descendant pids (one toolhelp snapshot).</summary>
    public static HashSet<uint> BuildTree(uint root)
    {
        var parentOf = new Dictionary<uint, uint>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /*SNAPPROCESS*/, 0);
        if (snap == (IntPtr)(-1)) return new HashSet<uint> { root };
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
                do { parentOf[pe.th32ProcessID] = pe.th32ParentProcessID; } while (Process32Next(snap, ref pe));
        }
        finally { CloseHandle(snap); }

        var tree = new HashSet<uint> { root };
        bool grew = true;
        while (grew) { grew = false; foreach (var kv in parentOf) if (tree.Contains(kv.Value) && tree.Add(kv.Key)) grew = true; }
        return tree;
    }

    /// <summary>Visible top-level windows whose pid is in <paramref name="tree"/> (each carries its
    /// owning process's exe filename, so consumers can skip store-client windows).</summary>
    public static List<Win> TreeWindows(HashSet<uint> tree) => EnumTopLevel(tree);

    /// <summary>EVERY visible top-level window on the desktop (no process filter) — for the global-scan
    /// SmartCapture mode used by store launches, where the game is not a descendant of anything we spawned.</summary>
    public static List<Win> AllTopLevelWindows() => EnumTopLevel(null);

    private static List<Win> EnumTopLevel(HashSet<uint>? tree)
    {
        var exeOf = ExeMap();
        var list = new List<Win>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (tree != null && !tree.Contains(pid)) return true;
                GetWindowRect(h, out var r);
                var tit = new StringBuilder(160); GetWindowText(h, tit, 160);
                var cls = new StringBuilder(96); GetClassName(h, cls, 96);
                list.Add(new Win(h, pid, r, tit.ToString(), cls.ToString(), exeOf.TryGetValue(pid, out var e) ? e : ""));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>hwnds of every currently-visible top-level window — the pre-launch baseline the global
    /// scan EXCLUDES, so only NEW windows (the launched game) are considered.</summary>
    public static HashSet<IntPtr> BaselineHwnds()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) => { try { if (IsWindowVisible(h)) set.Add(h); } catch { } return true; }, IntPtr.Zero);
        return set;
    }

    /// <summary>pid → executable file name (e.g. "steam.exe"), from one toolhelp snapshot.</summary>
    public static Dictionary<uint, string> ExeMap()
    {
        var map = new Dictionary<uint, string>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /*SNAPPROCESS*/, 0);
        if (snap == (IntPtr)(-1)) return map;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
                do { map[pe.th32ProcessID] = pe.szExeFile ?? ""; } while (Process32Next(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    public static bool Alive(IntPtr hwnd) { try { return IsWindow(hwnd); } catch { return false; } }

    /// <summary>The window's monitor bounds (for the min-size-% detection mode).</summary>
    public static RECT MonitorBounds(IntPtr hwnd)
    {
        try
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(hwnd, 2 /*NEAREST*/), ref mi)) return mi.rc;
        }
        catch { }
        return new RECT { L = 0, T = 0, R = 1920, B = 1080 };
    }

    /// <summary>Case-insensitive wildcard match (* and ?). Empty/"*" pattern matches everything.</summary>
    public static bool WildcardMatch(string? pattern, string? text)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        text ??= "";
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try { return System.Text.RegularExpressions.Regex.IsMatch(text, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
        catch { return false; }
    }

    /// <summary>Case-insensitive "CONTAINS" wildcard match (* and ?): the pattern may match ANYWHERE in the
    /// text — no ^…$ anchors — so a plain "fenetre" matches "ma fenetre de jeu" exactly like "ma*jeu" does.
    /// Empty pattern never matches (unlike <see cref="WildcardMatch"/>, which is the whole-title term).</summary>
    public static bool WildcardContains(string? pattern, string? text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        text ??= "";
        var rx = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        try { return System.Text.RegularExpressions.Regex.IsMatch(text, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
        catch { return false; }
    }
}
