// The early entry point of the single-artifact parental control. .NET runs this top-level class named
// exactly "StartupHook" (NO namespace) with a static Initialize() BEFORE LaunchBox's Main, because the
// native trigger (litebox-parental.asi) set DOTNET_STARTUP_HOOKS to this assembly. Here we LoadLibrary the
// native .bin sitting next to us and call its arm export — installing the CreateFileW read filter AND the
// CopyFileExW write guard together, before LaunchBox reads anything.
//
// CRITICAL: a startup hook that FAILS TO LOAD crashes LaunchBox, so this must touch ONLY BCL + kernel32 —
// no LaunchBox SDK, no Harmony, no other managed dependency (their early resolution would brick startup).
// The plugin half (events / menu, which DO reference the SDK) loads later via the normal plugin scan.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal class StartupHook
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ArmDelegate();

    internal static void Initialize()
    {
        try
        {
            // Host guard: only arm inside the two third-party apps. DOTNET_STARTUP_HOOKS is inherited by child
            // .NET processes (and LiteBox.exe if it loads the trigger), so skip everything else early.
            var proc = Process.GetCurrentProcess().ProcessName;
            if (!proc.Equals("LaunchBox", StringComparison.OrdinalIgnoreCase) &&
                !proc.Equals("BigBox", StringComparison.OrdinalIgnoreCase))
                return;

            string dir = AsmDir();
            string native = Path.Combine(dir, "litebox-parental-native.bin");
            var h = LoadLibraryW(native);
            if (h == IntPtr.Zero) { Log("[StartupHook] native .bin LoadLibrary FAILED (" + Marshal.GetLastWin32Error() + "): " + native + " — parental NOT armed (fail-safe)."); return; }

            var p = GetProcAddress(h, "litebox_parental_arm");
            if (p == IntPtr.Zero) { Log("[StartupHook] litebox_parental_arm export missing — NOT armed."); return; }
            int rc = Marshal.GetDelegateForFunctionPointer<ArmDelegate>(p)();
            Log("[StartupHook] armed native parental for " + proc + " → rc=" + rc + " (0=armed, 1=inert, 2=hook-fail).");
        }
        catch (Exception ex)
        {
            // Never let the startup hook throw — that crashes LaunchBox. Log + continue unarmed (fail-safe).
            try { Log("[StartupHook] EXCEPTION (continuing unarmed): " + ex); } catch { }
        }
    }

    private static string AsmDir()
    {
        try { var l = typeof(StartupHook).Assembly.Location; if (!string.IsNullOrEmpty(l)) return Path.GetDirectoryName(l); } catch { }
        return AppContext.BaseDirectory;
    }

    // Own tiny logger (Log.cs writes next to the managed dll; reuse the same file, but stay BCL-only here).
    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(AsmDir(), "litebox-parental.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
    }
}
