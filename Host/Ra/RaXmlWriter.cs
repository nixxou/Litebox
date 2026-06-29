// Writes the RetroAchievements playtime/beaten data back to the <Game> XML — the SAME field names
// LaunchBox/BigBox use, through ILiteBoxFields.SetField so it rides LiteBox's op-log / pending-write
// system exactly like every other IGame edit (surgical, crash-safe, flushed with the rest). This is what
// keeps BigBox's "PLAYTIME COMMITMENT" populated when LiteBox is the host.
//
// Surgical: each field is written ONLY when its value actually changed, so re-selecting a game (cache hit)
// doesn't churn the op-log. Medians are stored in MINUTES (LB's unit). PlaytimeCachedDate is derived from
// the cache's fetch time, so it's stable across cache hits and only rewrites on a real refetch.

#nullable enable

using System;
using System.Globalization;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaXmlWriter
{
    public static void Write(IGame game, RaGameCache? data)
    {
        if (game is not ILiteBoxFields f || data == null) return;
        try
        {
            Set(f, "RetroAchievementsMedianTimeToBeatHardcore",      data.beatMin   > 0 ? data.beatMin.ToString()      : null);
            Set(f, "RetroAchievementsMedianTimeToMaster",           data.masterMin > 0 ? data.masterMin.ToString()    : null);
            Set(f, "RetroAchievementsTimesUsedInHardcoreBeatMedian", data.beatSamples   > 0 ? data.beatSamples.ToString()   : null);
            Set(f, "RetroAchievementsTimesUsedInMasteryMedian",      data.masterSamples > 0 ? data.masterSamples.ToString() : null);
            Set(f, "RetroAchievementsBeatenSoftcore", data.beatenSoftcore ? "true" : "false");
            Set(f, "RetroAchievementsBeatenHardcore", data.beatenHardcore ? "true" : "false");

            if (DateTimeOffset.TryParse(data.fetchedAt, null, DateTimeStyles.RoundtripKind, out var dto))
                Set(f, "RetroAchievementsPlaytimeCachedDate", dto.ToLocalTime().ToString("o"));
        }
        catch { }
    }

    // null value ⇒ leave the field untouched. Otherwise write through the op-log only when it changed.
    private static void Set(ILiteBoxFields f, string name, string? value)
    {
        if (value == null) return;
        string cur = "";
        try { cur = f.GetField(name) ?? ""; } catch { }
        if (string.Equals(cur, value, StringComparison.Ordinal)) return;
        try { f.SetField(name, value); } catch { }
    }
}
