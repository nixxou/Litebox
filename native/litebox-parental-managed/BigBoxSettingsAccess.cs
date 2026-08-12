// Reaches LaunchBox's IN-MEMORY BigBoxSettings singleton so a LockPin change persists through LaunchBox's OWN
// save. A direct BigBoxSettings.xml edit does NOT stick: LaunchBox holds the settings in memory and rewrites
// its own copy over ours on close. So we set the PIN on the live object and let LaunchBox flush it.
//
// Path (discovered by reflection, obfuscation-safe — public member NAMES are preserved):
//   PluginHelper.DataManager                                   (concrete Unbroken.LaunchBox.Windows.Data.DataManager)
//     .GetGlobalBigBoxSettings()  -> BigBoxSettings            (the global singleton)
//     .LockPin  (string, [DataTableExport])                    (the encrypted PIN blob)
//   then  DataManager.BigBoxSettingsChanged = true  (static)   (mark dirty so Save flushes the file)
//         DataManager.Save(false)                              ("saves only changed XML files")
//
// All reflection, all fail-soft: if any step is missing (unexpected LB build), we return false and the caller
// falls back to reading the file. Read-only calls never mutate anything.

using System;
using System.Reflection;
using Unbroken.LaunchBox.Plugins;

namespace LiteBoxParental
{
    internal static class BigBoxSettingsAccess
    {
        private const BindingFlags Inst   = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags StatAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static object DataManager()
        {
            try { return PluginHelper.DataManager; } catch { return null; }
        }

        /// <summary>The live global BigBoxSettings object, or null when unreachable.</summary>
        private static object GlobalSettings(object dm)
        {
            if (dm == null) return null;
            try
            {
                var m = dm.GetType().GetMethod("GetGlobalBigBoxSettings", Inst, null, Type.EmptyTypes, null);
                return m?.Invoke(dm, null);
            }
            catch { return null; }
        }

        /// <summary>True when we can reach the in-memory settings object (i.e. we're inside a running LaunchBox/BigBox).</summary>
        public static bool Available
        {
            get { try { return GlobalSettings(DataManager()) != null; } catch { return false; } }
        }

        /// <summary>Read the raw (encrypted) LockPin blob from the in-memory settings; "" when unavailable/empty.</summary>
        public static string ReadLockPinBlob()
        {
            try
            {
                var s = GlobalSettings(DataManager());
                var p = s?.GetType().GetProperty("LockPin", Inst);
                return (p?.GetValue(s) as string) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Set the LockPin blob on the in-memory settings, flag BigBoxSettings dirty, and ask LaunchBox to
        /// save now (it re-saves at close too). Returns true when the in-memory set succeeded — persistence then
        /// rides LaunchBox's own writer, so the file is no longer clobbered.</summary>
        public static bool WriteLockPinBlob(string blob)
        {
            try
            {
                var dm = DataManager();
                var s = GlobalSettings(dm);
                if (s == null) { Dbg("write: no in-memory BigBoxSettings"); return false; }

                var p = s.GetType().GetProperty("LockPin", Inst);
                if (p == null || !p.CanWrite) { Dbg("write: LockPin not settable"); return false; }
                p.SetValue(s, blob ?? "");

                // Mark BigBoxSettings dirty (static on the concrete DataManager) so Save() actually flushes it.
                try { dm.GetType().GetProperty("BigBoxSettingsChanged", StatAll)?.SetValue(null, true); } catch { }

                // Persist immediately (wait:false). Fall back to a parameterless Save if the overload differs.
                try
                {
                    var save = dm.GetType().GetMethod("Save", Inst, null, new[] { typeof(bool) }, null);
                    if (save != null) save.Invoke(dm, new object[] { false });
                    else dm.GetType().GetMethod("Save", Inst, null, Type.EmptyTypes, null)?.Invoke(dm, null);
                }
                catch (Exception ex) { Dbg("write: Save threw " + ex.Message); }

                Dbg("write: LockPin updated in memory + saved");
                return true;
            }
            catch (Exception ex) { Dbg("write: " + ex.Message); return false; }
        }

        private static void Dbg(string m)
        {
            try { if (TestConfig.DebugLog) Log.Line("[BBSettings] " + m); } catch { }
        }
    }
}
