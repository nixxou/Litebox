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
        public readonly IntPtr Hwnd; public readonly uint Pid; public readonly RECT Rect; public readonly string Title; public readonly string Class;
        public Win(IntPtr h, uint pid, RECT r, string title, string cls) { Hwnd = h; Pid = pid; Rect = r; Title = title; Class = cls; }
        public int Area => Rect.W * Rect.H;
    }

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

    /// <summary>Visible top-level windows whose pid is in <paramref name="tree"/>.</summary>
    public static List<Win> TreeWindows(HashSet<uint> tree)
    {
        var list = new List<Win>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!tree.Contains(pid)) return true;
                GetWindowRect(h, out var r);
                var tit = new StringBuilder(160); GetWindowText(h, tit, 160);
                var cls = new StringBuilder(96); GetClassName(h, cls, 96);
                list.Add(new Win(h, pid, r, tit.ToString(), cls.ToString()));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
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
}
