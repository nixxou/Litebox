// Diagnostic-only render-detection probe. Given the launched process, it logs a TIMELINE of
// two non-invasive, non-admin signals so we can pick a reliable "the real game window is now
// rendering" trigger (to replace the blind startup-cover timer) — empirically, across e.g.
// Dolphin's D3D11 / D3D12 / Vulkan / OpenGL backends.
//
// Signals logged (relative ms from launch):
//   • WINDOWS of the launched process TREE (pid + descendants): first sighting + foreground
//     changes — HWND, class, title, size, isForeground. Distinguishes candidate game windows
//     from console/splash by class/size.
//   • GPU 3D-engine utilization for the tree (PDH "GPU Engine" counter — what Task Manager
//     shows; no admin, cross-API since D3D/Vulkan/GL all hit the 3D engine). A sustained ramp
//     is the "it's actually rendering" signal.
//
// It NEVER touches the cover or any behaviour — pure logging. Opt-in via the env var
// LITEBOX_RENDERPROBE=1 (so it costs nothing on a normal launch). Gentle: ~200 ms polling on
// a background thread for a bounded window, then it stops.

#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LbApiHost.Host.Diag;

internal static class RenderProbe
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    private struct RECT { public int L, T, R, B; }

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

    public static bool Enabled => Environment.GetEnvironmentVariable("LITEBOX_RENDERPROBE") == "1";

    /// <summary>No-op unless LITEBOX_RENDERPROBE=1. Starts the timeline log for the launched process.</summary>
    public static void MaybeStart(Process? proc)
    {
        if (!Enabled || proc == null) return;
        int rootPid;
        try { rootPid = proc.Id; } catch { return; }
        var t = new Thread(() => Run(rootPid)) { IsBackground = true, Name = "LiteBox-renderprobe" };
        t.Start();
    }

    private const int DurationMs = 25000, PollMs = 200, GpuEveryMs = 500;

    private static void Run(int rootPid)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[renderprobe] START rootPid={rootPid}");
        var seenWindows = new HashSet<IntPtr>();
        IntPtr lastFg = IntPtr.Zero;
        long nextGpu = 0;
        HashSet<uint> tree = new() { (uint)rootPid };
        long nextTree = 0;

        while (sw.ElapsedMilliseconds < DurationMs)
        {
            long now = sw.ElapsedMilliseconds;
            try
            {
                if (now >= nextTree) { tree = BuildTree((uint)rootPid); nextTree = now + 1000; }

                // Windows of the tree: log first sighting + foreground transitions.
                var fg = GetForegroundWindow();
                EnumWindows((h, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(h)) return true;
                        GetWindowThreadProcessId(h, out uint pid);
                        if (!tree.Contains(pid)) return true;
                        bool isFg = h == fg;
                        bool first = seenWindows.Add(h);
                        if (first || (isFg && h != lastFg))
                        {
                            GetWindowRect(h, out var r);
                            var cls = new StringBuilder(128); GetClassName(h, cls, 128);
                            var tit = new StringBuilder(128); GetWindowText(h, tit, 128);
                            Console.WriteLine($"[renderprobe] {now,6}ms WIN {(first ? "new" : "fg ")} pid={pid} hwnd=0x{h.ToInt64():X} " +
                                              $"{r.R - r.L}x{r.B - r.T} fg={isFg} class='{cls}' title='{tit}'");
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                lastFg = fg;

                if (now >= nextGpu) { LogGpu3D(tree, now); nextGpu = now + GpuEveryMs; }
            }
            catch { }
            Thread.Sleep(PollMs);
        }
        Console.WriteLine($"[renderprobe] END ({sw.ElapsedMilliseconds}ms)");
    }

    private static HashSet<uint> BuildTree(uint root)
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
        while (grew)
        {
            grew = false;
            foreach (var kv in parentOf)
                if (tree.Contains(kv.Value) && tree.Add(kv.Key)) grew = true;
        }
        return tree;
    }

    // GPU 3D-engine utilization for the tree via the "GPU Engine" PDH category (instance names
    // carry the pid + engtype). Non-admin. Slow-ish enumeration → only every GpuEveryMs.
    private static PerformanceCounterCategory? _gpuCat;
    private static bool _gpuTried;
    private static void LogGpu3D(HashSet<uint> tree, long now)
    {
        try
        {
            if (!_gpuTried) { _gpuTried = true; try { _gpuCat = new PerformanceCounterCategory("GPU Engine"); } catch { _gpuCat = null; } }
            if (_gpuCat == null) return;
            double total = 0; int hits = 0;
            foreach (var inst in _gpuCat.GetInstanceNames())
            {
                if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // "pid_1234_luid_..." → pull the pid.
                int p = inst.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                if (p < 0) continue;
                int s = p + 4, e = s; while (e < inst.Length && char.IsDigit(inst[e])) e++;
                if (e == s || !uint.TryParse(inst.Substring(s, e - s), out var pid) || !tree.Contains(pid)) continue;
                try
                {
                    using var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                    c.NextValue(); Thread.Sleep(10); total += c.NextValue(); hits++;
                }
                catch { }
            }
            if (hits > 0) Console.WriteLine($"[renderprobe] {now,6}ms GPU3D={total:F1}% (instances={hits})");
        }
        catch { }
    }
}
