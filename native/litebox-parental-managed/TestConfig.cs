// DEV-ONLY per-element toggle file: LB\Core\litebox-parental-test.ini.
//
// Lets us switch each protection ON/OFF independently to isolate behaviour during testing. ABSENT (production)
// or any key missing ⇒ that protection is ON — so a normal install with no ini has every guard active. The ini
// is NEVER shipped (not in assemble-payload / the installer). 0/false/no/off disables; anything else = on.
//
// BCL-ONLY (no SDK, no Harmony) so StartupHook — which applies the NATIVE toggles in the early phase — can read
// it safely. Parsed once and cached; never throws.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace LiteBoxParental
{
    internal static class TestConfig
    {
        internal const string FileName = "litebox-parental-test.ini";

        // Every PROTECTION defaults ON (true) — a normal install with no ini is fully guarded. The ini only ever
        // turns things OFF for a test.
        public static bool SoftAdminLock    = true;   // block admin windows while locked
        public static bool SoftConfirmGuard = true;   // force "No" on destructive delete confirmations
        public static bool SoftContextMenu  = true;   // grey out admin items in right-click context menus
        public static bool HardManagedWrite = true;   // managed Harmony guard on File.Copy/Move/Replace
        public static bool HardManagedDelete= true;   // managed Harmony guard on File.Delete
        public static bool NativeReadFilter = true;   // native .bin: hide restricted games (XML read filter)
        public static bool NativeWriteGuard = true;   // native .bin: block library writes/deletes (Win32)

        // DebugLog is NOT a protection → it defaults OFF, so the shipped product (no ini) writes NO parental logs.
        // Opt in for a test with DebugLog=1 in the ini.
        public static bool DebugLog         = false;  // write the parental debug logs (managed + native)

        // Replace LaunchBox's "Licensed to …/Free Version" corner text with the LIVE parental-control status
        // (only while parental is configured; a non-parental install is left untouched). Default ON.
        public static bool SoftStatusCorner = true;

        // Optional fine control: admin window SHORT type names (e.g. "OptionsView") to NOT block, for isolating one.
        public static readonly HashSet<string> SoftAllowWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static readonly object _gate = new object();

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    var path = IniPath();
                    if (path == null || !File.Exists(path)) return;   // absent ⇒ all defaults (everything ON)
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = line.Substring(0, eq).Trim();
                        var val = line.Substring(eq + 1);
                        int cm = val.IndexOfAny(new[] { '#', ';' });   // strip inline comment ("0   # …" → "0")
                        if (cm >= 0) val = val.Substring(0, cm);
                        val = val.Trim();
                        switch (key.ToLowerInvariant())
                        {
                            case "softadminlock":     SoftAdminLock    = On(val); break;
                            case "softconfirmguard":  SoftConfirmGuard = On(val); break;
                            case "softcontextmenu":   SoftContextMenu  = On(val); break;
                            case "softstatuscorner":  SoftStatusCorner = On(val); break;
                            case "hardmanagedwrite":  HardManagedWrite = On(val); break;
                            case "hardmanageddelete": HardManagedDelete= On(val); break;
                            case "nativereadfilter":  NativeReadFilter = On(val); break;
                            case "nativewriteguard":  NativeWriteGuard = On(val); break;
                            case "debuglog":          DebugLog         = On(val); break;
                            case "softallowwindow":   if (val.Length > 0) SoftAllowWindows.Add(ShortName(val)); break;
                        }
                    }
                    Log.Line("[TestConfig] loaded " + path + " — soft=" + SoftAdminLock + "/" + SoftConfirmGuard
                        + " hardMgd=" + HardManagedWrite + "/" + HardManagedDelete
                        + " native=" + NativeReadFilter + "/" + NativeWriteGuard
                        + (SoftAllowWindows.Count > 0 ? " allow=" + SoftAllowWindows.Count : ""));
                }
                catch (Exception ex) { try { Log.Line("[TestConfig] read failed (all defaults ON): " + ex.Message); } catch { } }
            }
        }

        private static bool On(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            return !(v == "0" || v == "false" || v == "no" || v == "off");   // anything else = ON
        }

        private static string ShortName(string s)
        {
            int dot = s.LastIndexOf('.');
            return dot >= 0 ? s.Substring(dot + 1) : s;
        }

        private static string IniPath()
        {
            try
            {
                var core = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
                return string.IsNullOrEmpty(core) ? null : Path.Combine(core, FileName);
            }
            catch { return null; }
        }
    }
}
