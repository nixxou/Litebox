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
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetFlagDelegate(int on);

    internal static void Initialize()
    {
        try
        {
            // CRITICAL: DOTNET_STARTUP_HOOKS is inherited by EVERY child process. LaunchBox spawns .NET Core
            // children — notably CefSharp.BrowserSubprocess.exe (netcoreapp3.1, rollForward=Major) — which HONOUR
            // the variable and would try to load THIS assembly into a browser subprocess, crashing it (and taking
            // libcef/LaunchBox down with it during storefront scans). Every REAL LaunchBox/BigBox process arms via
            // its OWN native trigger (winhttp+asi in Core\), never via inheritance — so scrub the variable now, the
            // instant our hook runs and before Main spawns any child: children get a clean environment, the browser
            // subprocess stops dying, and legitimate host relaunches still re-arm through their own trigger.
            try { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null); } catch { }

            // Host guard: only arm inside the two third-party apps. (After the scrub above this is belt-and-braces,
            // but it also covers any process that reached us by a path other than inheritance.)
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

            // DEV test ini: the .bin boots with read-filter + write-guard ON; apply the initial per-element OFF
            // overrides here so a native toggle takes effect from the very first read (BCL + kernel32 only).
            if (rc == 0)
            {
                try
                {
                    LiteBoxParental.TestConfig.EnsureLoaded();
                    if (!LiteBoxParental.TestConfig.NativeReadFilter) { SetNativeFlag(h, "litebox_parental_set_read_filtering", 0); Log("[StartupHook] test ini: native read-filter OFF"); }
                    if (!LiteBoxParental.TestConfig.NativeWriteGuard) { SetNativeFlag(h, "litebox_parental_set_writes_blocked", 0); Log("[StartupHook] test ini: native write-guard OFF"); }
                    if (!LiteBoxParental.TestConfig.DebugLog)         { SetNativeFlag(h, "litebox_parental_set_debug_log", 0); }   // silence the .bin log too
                }
                catch (Exception tex) { Log("[StartupHook] test-ini native override skipped: " + tex.Message); }
            }
        }
        catch (Exception ex)
        {
            // Never let the startup hook throw — that crashes LaunchBox. Log + continue unarmed (fail-safe).
            try { Log("[StartupHook] EXCEPTION (continuing unarmed): " + ex); } catch { }
        }
    }

    private static void SetNativeFlag(IntPtr h, string export, int on)
    {
        var p = GetProcAddress(h, export);
        if (p != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<SetFlagDelegate>(p)(on);
    }

    private static string AsmDir()
    {
        try { var l = typeof(StartupHook).Assembly.Location; if (!string.IsNullOrEmpty(l)) return Path.GetDirectoryName(l); } catch { }
        return AppContext.BaseDirectory;
    }

    // Own tiny logger (Log.cs writes next to the managed dll; reuse the same file, but stay BCL-only here).
    private static void Log(string msg)
    {
        // DEV test ini can silence logging (DebugLog=0). On any TestConfig error, fall through to logging so we
        // never lose the arm diagnostics.
        try { LiteBoxParental.TestConfig.EnsureLoaded(); if (!LiteBoxParental.TestConfig.DebugLog) return; } catch { }
        try { File.AppendAllText(Path.Combine(AsmDir(), "litebox-parental.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
    }
}
