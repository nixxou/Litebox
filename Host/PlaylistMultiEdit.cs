// Editing SEVERAL playlists at once — the pure logic, deliberately free of any UI so it can be
// tested on its own (--selftest-game-sort covers it).
//
// Two merges, and one rule that governs both: what the editor does NOT show, it must not destroy.
//
//   Auto-Populate — the grid shows only the rules every selected auto-populate playlist has. Each
//                   playlist almost always has rules of its own that stay invisible, so applying
//                   is a DIFFERENCE (drop what was removed, add what was added), never a
//                   replacement. A replacement would wipe rules the user never saw.
//   Games         — the grid shows the games common to every selected playlist. Removing one
//                   removes it from all of them; every other membership is left alone, and each
//                   playlist keeps its own manual order.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class PlaylistMultiEdit
{
    /// <summary>Identity of a rule. Field, comparison and value all compared case-insensitively:
    /// "Arcade" and "arcade" select the same games, so showing them as two distinct rules would be
    /// noise — and would make the difference below add a duplicate.</summary>
    public static string RuleKey(string? field, string? comparison, string? value)
        => (field ?? "").Trim().ToLowerInvariant() + ""
         + (comparison ?? "").Trim().ToLowerInvariant() + ""
         + (value ?? "").Trim().ToLowerInvariant();

    public static string RuleKey(PlaylistFilterDef f)
        => f == null ? "" : RuleKey(f.FieldKey, f.ComparisonTypeKey, f.Value);

    public static List<PlaylistFilterDef> FiltersOf(HostPlaylist playlist)
    {
        var result = new List<PlaylistFilterDef>();
        try
        {
            foreach (var f in playlist?.GetAllPlaylistFilters() ?? Array.Empty<IPlaylistFilter>())
            {
                string field = Safe(() => f.FieldKey) ?? "";
                if (field.Trim().Length == 0) continue;
                result.Add(new PlaylistFilterDef(field, Safe(() => f.ComparisonTypeKey) ?? "", Safe(() => f.Value) ?? ""));
            }
        }
        catch { }
        return result;
    }

    /// <summary>Rules held by EVERY playlist in the set. Order follows the first playlist, so the
    /// grid does not reshuffle depending on which one happened to be selected first.</summary>
    public static List<PlaylistFilterDef> CommonFilters(IReadOnlyList<HostPlaylist> playlists)
    {
        if (playlists == null || playlists.Count == 0) return new List<PlaylistFilterDef>();
        var perPlaylist = playlists.Select(p => FiltersOf(p)).ToList();
        var shared = new HashSet<string>(perPlaylist[0].Select(RuleKey), StringComparer.Ordinal);
        for (int i = 1; i < perPlaylist.Count; i++)
            shared.IntersectWith(perPlaylist[i].Select(RuleKey));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PlaylistFilterDef>();
        foreach (var f in perPlaylist[0])
        {
            var key = RuleKey(f);
            if (!shared.Contains(key) || !seen.Add(key)) continue;
            result.Add(new PlaylistFilterDef(f.FieldKey, f.ComparisonTypeKey, f.Value));
        }
        return result;
    }

    /// <summary>Applies the grid's edits as a DIFFERENCE against what it was showing. A rule the
    /// user changed reads as one removal plus one addition, which is exactly right: the old rule
    /// leaves every playlist and the new one joins them.</summary>
    public static void ApplyFilterDifference(
        IReadOnlyList<HostPlaylist> playlists,
        IReadOnlyList<PlaylistFilterDef> shownBefore,
        IReadOnlyList<PlaylistFilterDef> shownAfter)
    {
        if (playlists == null || playlists.Count == 0) return;
        var before = new HashSet<string>((shownBefore ?? Array.Empty<PlaylistFilterDef>()).Select(RuleKey), StringComparer.Ordinal);
        var after = (shownAfter ?? Array.Empty<PlaylistFilterDef>()).ToList();
        var afterKeys = new HashSet<string>(after.Select(RuleKey), StringComparer.Ordinal);

        var removed = new HashSet<string>(before.Where(k => !afterKeys.Contains(k)), StringComparer.Ordinal);
        var added = after.Where(f => !before.Contains(RuleKey(f))).ToList();
        if (removed.Count == 0 && added.Count == 0) return;

        foreach (var playlist in playlists)
        {
            var own = FiltersOf(playlist);
            var next = own.Where(f => !removed.Contains(RuleKey(f))).ToList();
            var present = new HashSet<string>(next.Select(RuleKey), StringComparer.Ordinal);
            foreach (var f in added)
                if (present.Add(RuleKey(f)))
                    next.Add(new PlaylistFilterDef(f.FieldKey, f.ComparisonTypeKey, f.Value));
            try { playlist.ReplaceFilters(next); } catch { }
        }
    }

    // ── Games ────────────────────────────────────────────────────────────────────────────────
    private static List<string> GameIdsOf(HostPlaylist playlist)
    {
        try
        {
            return (playlist.GetAllGames(false) ?? Array.Empty<IGame>())
                .Select(g => Safe(() => g.Id) ?? "").Where(id => id.Length > 0).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Games every selected playlist contains, in the first playlist's order.</summary>
    public static List<string> CommonGameIds(IReadOnlyList<HostPlaylist> playlists)
    {
        if (playlists == null || playlists.Count == 0) return new List<string>();
        var perPlaylist = playlists.Select(GameIdsOf).ToList();
        var shared = new HashSet<string>(perPlaylist[0], StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < perPlaylist.Count; i++)
            shared.IntersectWith(perPlaylist[i]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return perPlaylist[0].Where(id => shared.Contains(id) && seen.Add(id)).ToList();
    }

    /// <summary>How many distinct games the selection holds in total — the count the "hidden"
    /// label subtracts the common ones from.</summary>
    public static int UnionGameCount(IReadOnlyList<HostPlaylist> playlists)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in playlists ?? Array.Empty<HostPlaylist>()) all.UnionWith(GameIdsOf(p));
        return all.Count;
    }

    /// <summary>Removes games from every selected playlist, leaving each one's other memberships
    /// and its own manual order untouched.</summary>
    public static void RemoveGames(IReadOnlyList<HostPlaylist> playlists, IReadOnlyCollection<string> ids)
    {
        if (playlists == null || ids == null || ids.Count == 0) return;
        var drop = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in playlists)
        {
            try
            {
                var kept = (playlist.GetAllPlaylistGames() ?? Array.Empty<IPlaylistGame>())
                    .OfType<HostPlaylistGame>()
                    .OrderBy(g => g.ManualOrderValue)
                    .Where(g => !drop.Contains(g.GameIdValue ?? ""))
                    .ToList();
                playlist.ReplaceGames(kept);
            }
            catch { }
        }
    }

    /// <summary>Merged value of a field over the selection: the value when they all agree,
    /// null when they differ — which the UI shows as an indeterminate / "multiple" state.</summary>
    public static T? Merge<T>(IReadOnlyList<HostPlaylist> playlists, Func<HostPlaylist, T> read) where T : struct
    {
        if (playlists == null || playlists.Count == 0) return null;
        T first = Safe(() => read(playlists[0]));
        for (int i = 1; i < playlists.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(Safe(() => read(playlists[i])), first)) return null;
        return first;
    }

    public static string? MergeText(IReadOnlyList<HostPlaylist> playlists, Func<HostPlaylist, string?> read)
    {
        if (playlists == null || playlists.Count == 0) return null;
        string first = Safe(() => read(playlists[0])) ?? "";
        for (int i = 1; i < playlists.Count; i++)
            if (!string.Equals(Safe(() => read(playlists[i])) ?? "", first, StringComparison.Ordinal)) return null;
        return first;
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
}
