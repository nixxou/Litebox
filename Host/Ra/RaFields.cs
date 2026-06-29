#nullable enable

namespace LbApiHost.Host.Ra;

/// <summary>Reads the RA-related &lt;Game&gt; XML fields LiteBox exposes via ILiteBoxFields.GetField — the
/// raid (RetroAchievementsId) plus the "time to beat / master" medians LB cached but the SDK never
/// surfaced. Call on the UI thread (reads the in-memory store), then hand the values to the bg fetch.</summary>
internal static class RaFields
{
    private static string Get(object? game, string name)
    {
        try { return (game as ILiteBoxFields)?.GetField(name) ?? ""; } catch { return ""; }
    }

    /// <summary>The numeric RA game id (raid) for a game, or 0 when absent/unscored.</summary>
    public static int Raid(object? game)
        => int.TryParse(Get(game, "RetroAchievementsId"), out var v) ? v : 0;

    /// <summary>The "Beat the Game" (hardcore) / "Mastered" median commitments in MINUTES, or 0 when absent.
    /// Read from the XML LB already wrote — the public API doesn't carry them (live refresh is a TODO).</summary>
    public static (int beatMinutes, int masterMinutes) ReadMedians(object? game)
    {
        int beat = int.TryParse(Get(game, "RetroAchievementsMedianTimeToBeatHardcore"), out var b) ? b : 0;
        int master = int.TryParse(Get(game, "RetroAchievementsMedianTimeToMaster"), out var m) ? m : 0;
        return (beat, master);
    }

    /// <summary>"12h 26m" / "46m" / "" for ≤0. Input is minutes (LB stores these medians in minutes).</summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return "";
        int h = minutes / 60, m = minutes % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }
}
