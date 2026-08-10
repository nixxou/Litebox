// Managed → native control channel to litebox-parentalcontrol.asi (WS5.1). The ASI is
// loaded into the process by Ultimate ASI Loader long before this assembly; we just resolve
// its already-loaded module + exports via GetModuleHandle / GetProcAddress.
//
// Mirrors ExtendDB's ExtendDbAsiBridge, retargeted to our exports:
//   • SetFiltering(bool)      → litebox_parental_set_filtering(int). The FIRST call flips the
//     ASI out of its cold-start gate (g_managedTookOver) — from then on the ASI obeys us alone.
//   • OpenRealFile(string)    → litebox_parental_open_real_file(LPCWSTR): the UNFILTERED file,
//     bypassing the ASI's own read redirect (used when reloading the real library).
// Missing ASI (never deployed) → delegates stay null, every call is a logged no-op.

using System;
using System.Runtime.InteropServices;

namespace LiteBoxParental
{
    internal static class AsiBridge
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetFilteringDelegate(int enabled);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate IntPtr OpenRealFileDelegate([MarshalAs(UnmanagedType.LPWStr)] string path);

        private static SetFilteringDelegate _setFiltering;
        private static OpenRealFileDelegate _openRealFile;
        private static bool _tried;

        public static bool Available => _setFiltering != null;

        private static void Init()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                var h = GetModuleHandleW("litebox-parentalcontrol.asi");
                if (h == IntPtr.Zero) { Log.Line("[AsiBridge] litebox-parentalcontrol.asi not loaded in this process."); return; }

                var pf = GetProcAddress(h, "litebox_parental_set_filtering");
                if (pf != IntPtr.Zero) _setFiltering = Marshal.GetDelegateForFunctionPointer<SetFilteringDelegate>(pf);

                var po = GetProcAddress(h, "litebox_parental_open_real_file");
                if (po != IntPtr.Zero) _openRealFile = Marshal.GetDelegateForFunctionPointer<OpenRealFileDelegate>(po);

                Log.Line($"[AsiBridge] resolved: set_filtering={_setFiltering != null} open_real_file={_openRealFile != null}");
            }
            catch (Exception ex) { Log.Line("[AsiBridge] init error: " + ex.Message); }
        }

        public static void SetFiltering(bool enabled)
        {
            Init();
            if (_setFiltering == null) { Log.Line("[AsiBridge] SetFiltering: export unavailable."); return; }
            try { _setFiltering(enabled ? 1 : 0); }
            catch (Exception ex) { Log.Line($"[AsiBridge] SetFiltering error: {ex.GetType().Name} {ex.Message}"); }
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
