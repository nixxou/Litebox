// The safety-critical half of the plugin: block any File.Copy whose destination is under
// LB\Data\ while the in-memory library may be the FILTERED subset (LockState.WritesUnsafe).
//
// WS0 (W0.1) proved LaunchBox/BigBox persist EVERY data file the same way — serialise to
// Metadata\Temp\<guid>, then File.Copy(temp, Data\X.xml, overwrite:true), then delete the temp
// — and that one edit/sync rewrites the WHOLE library (all Platforms\*.xml, the index,
// Parents.xml, Playlists\*). So the guard must cover ALL of Data\, not just Data\Platforms\:
// drop the File.Copy INTO Data\ and nothing filtered can ever hit disk. Everything else in the
// save pipeline (the temp write, the temp delete) is left alone.
//
// Widened + simplified vs ExtendDB's PlatformXmlWriteGuard: whole Data\ tree, Block only (no
// Merge — LiteBox never rewrites a filtered library to merge against). Prefix returns false to
// skip the original copy; any error fails OPEN (let the copy run) so we never break LB's exit.

using System;
using System.IO;
using HarmonyLib;

namespace LiteBoxParental
{
    [HarmonyPatch]
    internal static class WriteGuard
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(File), nameof(File.Copy), new[] { typeof(string), typeof(string) })]
        static bool Copy2(string sourceFileName, string destFileName) => Handle(destFileName);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(File), nameof(File.Copy), new[] { typeof(string), typeof(string), typeof(bool) })]
        static bool Copy3(string sourceFileName, string destFileName, bool overwrite) => Handle(destFileName);

        // false = SKIP the original File.Copy (write blocked); true = let it run.
        private static bool Handle(string destFileName)
        {
            try
            {
                if (!LockState.WritesUnsafe) return true;        // unlocked / not our scope → normal
                if (!IsUnderData(destFileName)) return true;     // not a library write → normal
                Log.Line($"[WriteGuard] BLOCKED copy → {destFileName} (locked; in-memory library may be filtered)");
                return false;
            }
            catch (Exception ex)
            {
                Log.Line("[WriteGuard] error, failing open: " + ex.Message);
                return true;   // never break the save pipeline on our own fault
            }
        }

        private static bool IsUnderData(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.Replace('/', '\\').IndexOf(@"\Data\", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
