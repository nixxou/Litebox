// HARD layer, managed variant — the SECOND hard mode.
//
// The native .bin is the primary hard guard (it also does the read-filter that HIDES restricted games, which
// only a pre-Main native hook can do completely). This is its managed twin: Harmony-patch the exact System.IO
// primitives the write-audit proved LaunchBox uses to persist the library — File.Copy (write + create), and
// File.Delete (platform delete/rename via RemoveEmptyPlatforms) — plus File.Move/Replace for insurance. While
// parental is configured AND locked, any of these targeting a LIBRARY file is skipped (the method returns as if
// it succeeded, so LaunchBox sees no error and the real file is left untouched — same semantics as the .bin).
//
// It runs ALWAYS as a complement (idempotent with the native guard: whichever fires first wins, same result), so:
//   • full deployment (winhttp+.asi+.bin+dll)  → native read-filter + write-guard, this as belt-and-suspenders;
//   • dll-only deployment (no native files)     → THIS is the whole hard layer (read-only; games NOT hidden).
// Same IsLibraryWriteTarget scope as the .bin (Platforms\/Playlists\/Platforms.xml/Parents.xml — never the many
// transient Data\ files LaunchBox rewrites legitimately). Fully fail-safe: any error → allow.

using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace LiteBoxParental
{
    internal static class ManagedHardGuard
    {
        private static bool _installed;
        private static readonly object _gate = new object();

        public static void Install()
        {
            if (_installed) return;
            lock (_gate)
            {
                if (_installed) return;
                try
                {
                    if (!LockState.IsHost) { _installed = true; return; }   // LaunchBox/BigBox only
                    HarmonyLoader.Ensure();
                    InstallPatches();
                    _installed = true;
                }
                catch (Exception ex) { _installed = true; Log.Line("[HardManaged] install failed (inert): " + ex.Message); }
            }
        }

        private static void InstallPatches()
        {
            var h = new Harmony("litebox.parental.hardguard");
            var prefix = new HarmonyMethod(typeof(ManagedHardGuard).GetMethod(nameof(GuardPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            int n = 0;
            foreach (var mi in typeof(File).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                switch (mi.Name)
                {
                    case "Copy": case "Move": case "Replace": case "Delete":
                        try { h.Patch(mi, prefix: prefix); n++; }
                        catch (Exception ex) { Log.Line("[HardManaged] patch-fail " + mi.Name + ": " + ex.Message); }
                        break;
                }
            }
            Log.Line("[HardManaged] armed (" + n + " File.* patches)");
        }

        // Return false ⇒ skip the original (pretend success). NEVER throws (a guard bug must not break LaunchBox).
        private static bool GuardPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!LockState.ScopeActive || !LockState.Locked) return true;   // only a configured+locked session guards
                TestConfig.EnsureLoaded();
                var method = __originalMethod.Name;
                // DEV test ini: separate toggles for the delete vs write primitives.
                bool guarded = method == "Delete" ? TestConfig.HardManagedDelete : TestConfig.HardManagedWrite;
                if (!guarded) return true;
                var target = TargetPath(method, __args);
                if (target != null && IsLibraryWriteTarget(target))
                {
                    Log.Line("[HardManaged] BLOCKED " + method + " -> " + target);
                    return false;   // skip → file untouched, caller sees success
                }
            }
            catch (Exception ex) { Log.Line("[HardManaged] prefix error: " + ex.Message); }
            return true;
        }

        // The WRITE/DELETE target argument per method: Delete(path) = arg0; Copy/Move/Replace(_, dest, …) = arg1.
        private static string TargetPath(string method, object[] args)
        {
            if (args == null) return null;
            if (method == "Delete") return args.Length >= 1 ? args[0] as string : null;
            return args.Length >= 2 ? args[1] as string : null;   // Copy / Move / Replace destination
        }

        // Identical scope to the native .bin's IsLibraryWriteTarget (keep in sync).
        private static bool IsLibraryWriteTarget(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (!p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;
            return p.IndexOf("\\Data\\Platforms\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\Data\\Playlists\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.EndsWith("\\Data\\Platforms.xml", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("\\Data\\Parents.xml", StringComparison.OrdinalIgnoreCase);
        }
    }
}
