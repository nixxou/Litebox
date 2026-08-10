// parentalprobe.dll — DECISIVE test, ISOLATED to one question first:
//   can ONE managed assembly be BOTH a DOTNET_STARTUP_HOOKS target (early read-filter) AND a LaunchBox
//   plugin (Tools menu)? No Harmony, no ALC resolver here — those destabilised startup last run, so we
//   answer the base question first, then re-add Harmony.
//
// Initialize() uses ONLY BCL + kernel32 (zero external managed deps), so it loads clean. The SDK reference
// is used ONLY by the plugin class, which LaunchBox loads later.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;

internal static class ProbeState
{
    public static bool EarlyRan;
    public static int  ProbeInstallRc = -999;
    public static ProbeCountDelegate NativeCount;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ProbeCountDelegate();
    public static int Reads() { try { return NativeCount != null ? NativeCount() : -1; } catch { return -2; } }
    public static void Log(string m)
    { try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "parentalprobe.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {m}\r\n"); } catch { } }
}

internal class StartupHook
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProbeInstall_t();
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string p);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string n);

    internal static void Initialize()
    {
        ProbeState.EarlyRan = true;
        ProbeState.Log("[StartupHook] RAN before LaunchBox Main (asm dir: " + AsmDir() + ")");
        try
        {
            var native = Path.Combine(AsmDir(), "filterprobe-native.bin");
            var h = LoadLibraryW(native);
            if (h == IntPtr.Zero) { ProbeState.Log("  probe LoadLibrary FAILED " + Marshal.GetLastWin32Error() + ": " + native); return; }
            var pc = GetProcAddress(h, "Probe_Count");
            if (pc != IntPtr.Zero) ProbeState.NativeCount = Marshal.GetDelegateForFunctionPointer<ProbeState.ProbeCountDelegate>(pc);
            var pi = GetProcAddress(h, "Probe_Install");
            if (pi != IntPtr.Zero) ProbeState.ProbeInstallRc = Marshal.GetDelegateForFunctionPointer<ProbeInstall_t>(pi)();
            ProbeState.Log("  Probe_Install rc=" + ProbeState.ProbeInstallRc);
        }
        catch (Exception ex) { ProbeState.Log("  probe EX: " + ex.Message); }
    }

    private static string AsmDir()
    {
        try { var l = typeof(StartupHook).Assembly.Location; if (!string.IsNullOrEmpty(l)) return Path.GetDirectoryName(l); } catch { }
        return AppContext.BaseDirectory;
    }
}

public sealed class ParentalProbeMenu : ISystemMenuItemPlugin
{
    public string Caption =>
        $"Parental probe — early:{(ProbeState.EarlyRan ? "YES" : "no")} reads:{ProbeState.Reads()}";
    public System.Drawing.Image IconImage => System.Drawing.SystemIcons.Information.ToBitmap();
    public bool ShowInLaunchBox => true;
    public bool ShowInBigBox => true;
    public bool AllowInBigBoxWhenLocked => true;

    public void OnSelected()
    {
        MessageBox.Show(
            "This item proves the assembly loaded as a PLUGIN.\r\n\r\n" +
            $"Startup-hook ran early (before Main): {ProbeState.EarlyRan}\r\n" +
            $"Probe_Install rc: {ProbeState.ProbeInstallRc}\r\n" +
            $"Platform-xml reads caught: {ProbeState.Reads()}\r\n\r\n" +
            "If 'ran early' is True HERE, the SAME assembly is both the startup hook and the plugin — one " +
            "file, both roles, shared state.",
            "Parental probe", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
