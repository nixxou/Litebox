using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Data;

/// <summary>Tiny memory reporter so we can see what each phase costs.</summary>
internal static class Mem
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr handle, IntPtr min, IntPtr max);

    public static void Report(string label)
    {
        long ws = Process.GetCurrentProcess().WorkingSet64;
        long managed = GC.GetTotalMemory(false);
        long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
        Console.WriteLine($"[mem] {label,-26} WS={MB(ws)}MB  managed={MB(managed)}MB  heap={MB(heap)}MB");
    }

    public static void Collect() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    /// <summary>
    /// Aggressive compacting GC, run twice around finalizers, THEN trim the OS
    /// working set so the freed pages actually leave the process (the GC alone
    /// rarely returns them — Task Manager's "memory" only drops after this).
    /// They page back in on demand; during a game that's exactly what we want.
    /// </summary>
    public static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); }
        catch { }
    }

    private static string MB(long bytes) => (bytes / 1048576.0).ToString("F1");
}
