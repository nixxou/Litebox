// Deleting media that nothing can reach any more.
//
// A combine and a merging rename both leave files behind: the ones the filters turned down (already
// present, or too similar to what the destination has) and, when the two games are not the same
// database entry, the whole of the absorbed game's collection. The game that answered to their name
// no longer exists, so nothing resolves them — they are invisible in both LiteBox and LaunchBox,
// and they stay that way for good.
//
// They used to be offered rather than removed, on the reasoning that unreferenced is not unwanted.
// In practice a library accumulates them silently and there is no way to tell later which orphan
// belonged to what, which is worse than the deletion it was protecting against. So they go.
//
// WHAT IS NEVER DELETED HERE. The caller decides what lands in the list, and it never puts in a file
// another game still answers to — a plain name belongs to a TITLE, so a third game carrying it owns
// those files too. That check happens before this is called, because only the caller knows who the
// other games are.
//
// Every deletion is logged with its reason. Something that removes files without a trace is not
// something anyone can debug afterwards.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace LbApiHost.Host.Media;

internal static class MediaCleanup
{
    /// <summary>Deletes what is handed over and reports how it went. Failures are counted, never
    /// thrown: a file that will not go is a file that stays, and that is not a reason to abandon
    /// the operation that called this.</summary>
    public static (int Deleted, int Failed) Delete(IReadOnlyCollection<string> files, string reason)
    {
        if (files == null || files.Count == 0) return (0, 0);
        int gone = 0, failed = 0;
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                File.Delete(f);
                gone++;
            }
            catch (Exception ex)
            {
                failed++;
                Diag.LbLog.Warn("media", $"orphan not deleted ({ex.GetType().Name}): {Path.GetFileName(f)}");
            }
        }
        if (gone > 0 || failed > 0)
            Diag.LbLog.Info("media", $"{reason}: {gone} orphan(s) deleted"
                                     + (failed > 0 ? $", {failed} could not be" : ""));
        return (gone, failed);
    }
}
