// Managed → native control channel to litebox-parental-native.bin (the single-artifact native half).
// The .bin is LoadLibrary'd + armed by StartupHook.Initialize; here we resolve its already-loaded module +
// control exports via GetModuleHandle / GetProcAddress. Two independent flags:
//   • SetReadFiltering(bool) → litebox_parental_set_read_filtering(int): filter platform-XML reads (locked).
//   • SetWritesBlocked(bool) → litebox_parental_set_writes_blocked(int): refuse copies into Data\ (the latch).
//   • OpenRealFile(string)   → litebox_parental_open_real_file(LPCWSTR): the UNFILTERED file (real-library read).
// Missing .bin → delegates stay null, every call is a logged no-op.

using System;
using System.Runtime.InteropServices;

namespace LiteBoxParental
{
    internal static class AsiBridge
    {
        private const string NativeModule = "litebox-parental-native.bin";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetFlagDelegate(int on);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate IntPtr OpenRealFileDelegate([MarshalAs(UnmanagedType.LPWStr)] string path);

        private static SetFlagDelegate _setReadFiltering;
        private static SetFlagDelegate _setWritesBlocked;
        private static OpenRealFileDelegate _openRealFile;
        private static bool _tried;
        private static bool _nativeLoaded;

        /// <summary>The native .bin is loaded in THIS process (armed by StartupHook). When false, nothing is
        /// filtering reads or guarding writes here.</summary>
        public static bool IsNativeLoaded { get { Init(); return _nativeLoaded; } }

        private static void Init()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                var h = GetModuleHandleW(NativeModule);
                if (h == IntPtr.Zero) { Log.Line("[AsiBridge] " + NativeModule + " not loaded in this process."); return; }
                _nativeLoaded = true;

                var pr = GetProcAddress(h, "litebox_parental_set_read_filtering");
                if (pr != IntPtr.Zero) _setReadFiltering = Marshal.GetDelegateForFunctionPointer<SetFlagDelegate>(pr);
                var pw = GetProcAddress(h, "litebox_parental_set_writes_blocked");
                if (pw != IntPtr.Zero) _setWritesBlocked = Marshal.GetDelegateForFunctionPointer<SetFlagDelegate>(pw);
                var po = GetProcAddress(h, "litebox_parental_open_real_file");
                if (po != IntPtr.Zero) _openRealFile = Marshal.GetDelegateForFunctionPointer<OpenRealFileDelegate>(po);

                Log.Line($"[AsiBridge] resolved: read_filtering={_setReadFiltering != null} writes_blocked={_setWritesBlocked != null} open_real_file={_openRealFile != null}");
            }
            catch (Exception ex) { Log.Line("[AsiBridge] init error: " + ex.Message); }
        }

        /// <summary>Filter (or stop filtering) platform-XML reads. Returns true iff the export was invoked.</summary>
        public static bool SetReadFiltering(bool on)
        {
            Init();
            // DEV test ini can force the native read-filter OFF regardless of lock state (see TestConfig). This is
            // the single choke point every caller (StartupHook, LockState) funnels through, so honouring it here
            // keeps their logic untouched.
            if (on) { TestConfig.EnsureLoaded(); if (!TestConfig.NativeReadFilter) { on = false; Log.Line("[AsiBridge] read-filtering forced OFF by test ini"); } }
            if (_setReadFiltering == null) { Log.Line("[AsiBridge] SetReadFiltering: export unavailable."); return false; }
            try { _setReadFiltering(on ? 1 : 0); return true; }
            catch (Exception ex) { Log.Line($"[AsiBridge] SetReadFiltering error: {ex.GetType().Name} {ex.Message}"); return false; }
        }

        /// <summary>Arm (or clear) the write latch: block copies into Data\. Returns true iff the export was invoked.</summary>
        public static bool SetWritesBlocked(bool on)
        {
            Init();
            // DEV test ini can force the native write guard OFF (see TestConfig) — same choke-point trick as reads.
            if (on) { TestConfig.EnsureLoaded(); if (!TestConfig.NativeWriteGuard) { on = false; Log.Line("[AsiBridge] writes-blocked forced OFF by test ini"); } }
            if (_setWritesBlocked == null) { Log.Line("[AsiBridge] SetWritesBlocked: export unavailable."); return false; }
            try { _setWritesBlocked(on ? 1 : 0); return true; }
            catch (Exception ex) { Log.Line($"[AsiBridge] SetWritesBlocked error: {ex.GetType().Name} {ex.Message}"); return false; }
        }

        public static IntPtr OpenRealFile(string path)
        {
            Init();
            if (_openRealFile == null) return IntPtr.Zero;
            try { return _openRealFile(path); }
            catch { return IntPtr.Zero; }
        }
    }
}
