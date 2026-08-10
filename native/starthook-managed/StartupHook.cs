// startuphook.dll — the MANAGED half of the DOTNET_STARTUP_HOOKS experiment.
//
// .NET runs a top-level class named exactly "StartupHook" (NO namespace) with a static
// Initialize() BEFORE the application's Main, when DOTNET_STARTUP_HOOKS points at this assembly.
// The native trigger (starthook.asi) sets that env var early (via winhttp), so this runs before
// LaunchBox reads the platform XMLs. Here we LoadLibrary the MinHook probe (filterprobe-native.bin)
// and arm the CreateFileW hook — if the log then shows platform-XML opens at startup, managed code
// ran early enough to filter, entirely before plugins load.

using System;
using System.IO;
using System.Runtime.InteropServices;

internal class StartupHook
{
    internal static void Initialize()
    {
        string dir = AppContext.BaseDirectory;   // Core (LaunchBox.exe's base)
        Log(dir, "[StartupHook.Initialize] RAN — before LaunchBox Main");
        try
        {
            var native = Path.Combine(dir, "filterprobe-native.bin");
            var h = LoadLibraryW(native);
            if (h == IntPtr.Zero) { Log(dir, $"LoadLibrary FAILED ({Marshal.GetLastWin32Error()}): {native}"); return; }
            var p = GetProcAddress(h, "Probe_Install");
            if (p == IntPtr.Zero) { Log(dir, "Probe_Install export not found"); return; }
            int rc = Marshal.GetDelegateForFunctionPointer<ProbeInstall_t>(p)();
            Log(dir, $"Probe_Install returned {rc} (0 = CreateFileW hook armed)");
        }
        catch (Exception ex) { Log(dir, "EXCEPTION: " + ex); }
    }

    private static void Log(string dir, string msg)
    {
        try { File.AppendAllText(Path.Combine(dir, "starthook.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProbeInstall_t();
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
}
