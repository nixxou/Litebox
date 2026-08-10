// filterprobe-plugin — installs the CreateFileW probe at the EARLIEST managed entry point a LaunchBox
// plugin has: a [ModuleInitializer], which the CLR runs the moment this assembly is first touched (i.e.
// as LaunchBox's plugin scanner loads it). If that is before LaunchBox reads the platform XMLs, the probe
// catches them and Option B (one managed DLL = filter + guard, no winhttp/ASI) is viable.
//
// NO winhttp / ASI loader is involved on purpose — the plugin LoadLibrary's the native probe itself.
//
// The native helper is deployed as "filterprobe-native.bin" (NOT .dll) so LaunchBox's plugin scanner —
// which tries to load every *.dll in the folder as a managed assembly — skips it (a native dll there gives
// "Bad IL format"). LoadLibrary loads any extension. Both exports are resolved via GetProcAddress, so no
// DllImport (which would key on a filename) is needed.

using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;

namespace FilterProbe
{
    internal static class Boot
    {
        internal const string NativeFile = "filterprobe-native.bin";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ProbeInstall_t();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ProbeCount_t();
        internal static ProbeCount_t Count;   // cached so the menu can read the live count

        [ModuleInitializer]
        internal static void Init()
        {
            string dir = SelfDir();
            LogM(dir, "[ModuleInitializer] managed plugin init — about to load native probe + arm the hook");
            try
            {
                var native = Path.Combine(dir, NativeFile);
                var h = LoadLibraryW(native);
                if (h == IntPtr.Zero) { LogM(dir, $"[ModuleInitializer] LoadLibrary FAILED ({Marshal.GetLastWin32Error()}): {native}"); return; }

                var pInstall = GetProcAddress(h, "Probe_Install");
                var pCount   = GetProcAddress(h, "Probe_Count");
                if (pInstall == IntPtr.Zero) { LogM(dir, "[ModuleInitializer] Probe_Install export not found"); return; }
                if (pCount   != IntPtr.Zero) Count = Marshal.GetDelegateForFunctionPointer<ProbeCount_t>(pCount);

                int rc = Marshal.GetDelegateForFunctionPointer<ProbeInstall_t>(pInstall)();
                LogM(dir, $"[ModuleInitializer] Probe_Install returned {rc} (0 = hook armed)");
            }
            catch (Exception ex) { LogM(dir, "[ModuleInitializer] EXCEPTION: " + ex); }
        }

        internal static string SelfDir()
        {
            try { var l = typeof(Boot).Assembly.Location; if (!string.IsNullOrEmpty(l)) return Path.GetDirectoryName(l); } catch { }
            return AppContext.BaseDirectory;
        }
        internal static void LogM(string dir, string msg)
        {
            try { File.AppendAllText(Path.Combine(dir, "filterprobe-managed.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
    }

    // A visible menu item = confirmation the plugin loaded, plus a live count of platform-xml opens caught.
    public sealed class ProbeMenuItem : ISystemMenuItemPlugin
    {
        public string Caption => "Filter probe: " + SafeCount() + " platform-xml reads caught";
        public Image IconImage => SystemIcons.Information.ToBitmap();
        public bool ShowInLaunchBox => true;
        public bool ShowInBigBox => true;
        public bool AllowInBigBoxWhenLocked => true;

        public void OnSelected()
        {
            int n = SafeCount();
            MessageBox.Show(
                $"Platform-xml opens caught since the plugin's ModuleInitializer: {n}\r\n\r\n" +
                "If this is > 0 AND the log (Core\\filterprobe.log) shows those opens at STARTUP (before you " +
                "touched anything), the managed plugin armed the hook early enough — Option B is viable and the " +
                "winhttp + ASI early-loader can be dropped.\r\n\r\nSee Core\\filterprobe.log and the plugin " +
                "folder's filterprobe-managed.log for timestamps.",
                "Filter probe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static int SafeCount() { try { return Boot.Count != null ? Boot.Count() : -1; } catch { return -1; } }
    }
}
