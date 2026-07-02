// Left-panel "group by" views, mirroring LaunchBox's sidebar dropdown. The tree used to be hardcoded to
// the Platform Category hierarchy; now a ComboBox picks a SourceView and the tree is rebuilt from it.
//
// Three families of view:
//   • Metadata  (Platform / Platform Category / Playlist): nodes come from the DataManager (IPlatform,
//     IPlatformCategory hierarchy, IPlaylist) — LoadNode/NodeKey already handle those types.
//   • Enumerated (Publisher / Region / Series / Play Mode / Status / Rating / Release Type): one node per
//     distinct value across ALL games, built on the fly. Multi-value fields (Publishers, Series, PlayModes
//     are ';'-split on the model) put a game under EACH of its values.
//   • Buckets    (Release Date by year, Star Rating 1..5): computed groupings.
// Enumerated/bucket nodes are GroupNode (a labelled, pre-computed list of games); MainWindow.LoadNode maps a
// GroupNode straight to the current list. Blank values collapse to "<unknown>".

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

/// <summary>A dynamic sidebar node: a label + a fixed set of games. LoadNode treats it like a platform —
/// selecting it just makes <see cref="Games"/> the current list.</summary>
internal sealed class GroupNode
{
    public string Label { get; }
    public IReadOnlyList<IGame> Games { get; }
    public IReadOnlyList<GroupNode>? Children { get; }   // null/empty = leaf; else a 2-level group (Progress)
    public GroupNode(string label, IReadOnlyList<IGame> games, IReadOnlyList<GroupNode>? children = null)
    { Label = label; Games = games; Children = children; }
}

/// <summary>One "group by" view. <see cref="BuildRoots"/> returns the tree roots for the view, WITHOUT the
/// leading "All" node (the caller prepends it).</summary>
internal sealed class SourceView
{
    public string Id { get; }
    public string Label { get; }
    public Func<IDataManager, IReadOnlyList<IGame>, List<object>> BuildRoots { get; }
    public SourceView(string id, string label, Func<IDataManager, IReadOnlyList<IGame>, List<object>> build)
    { Id = id; Label = label; BuildRoots = build; }
}

internal static class SourceViews
{
    private const string Unknown = "<unknown>";
    public const string DefaultId = "platform-category";

    // Order mirrors LaunchBox's "group by" dropdown.
    public static readonly SourceView[] All =
    {
        new("platform",          "Platform",          (dm, g) => Meta(dm, MetaKind.Platform)),
        new("platform-category", "Platform Category", (dm, g) => Meta(dm, MetaKind.Category)),
        new("play-mode",         "Play Mode",         (dm, g) => Enumerated(g, x => x.PlayModes)),
        new("playlist",          "Playlist",          (dm, g) => Meta(dm, MetaKind.Playlist)),
        new("progress",          "Progress",          (dm, g) => ByProgress(g)),
        new("publisher",         "Publisher",         (dm, g) => Enumerated(g, x => x.Publishers)),
        new("rating",            "Rating",            (dm, g) => Enumerated(g, x => One(x.Rating))),
        new("region",            "Region",            (dm, g) => Enumerated(g, x => One(x.Region))),
        new("release-date",      "Release Date",      (dm, g) => ByYear(g)),
        new("release-type",      "Release Type",      (dm, g) => Enumerated(g, x => One(x.ReleaseType))),
        new("series",            "Series",            (dm, g) => Enumerated(g, x => x.SeriesValues)),
        new("star-rating",       "Star Rating",       (dm, g) => ByStars(g)),
        new("status",            "Status",            (dm, g) => Enumerated(g, x => One(x.Status))),
    };

    /// <summary>The view for an id, or the default (Platform Category) when unknown/null.</summary>
    public static SourceView ById(string? id)
        => All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? All.First(v => v.Id == DefaultId);

    // ── metadata views ──────────────────────────────────────────────────────
    private enum MetaKind { Platform, Category, Playlist }

    private static List<object> Meta(IDataManager dm, MetaKind kind)
    {
        try
        {
            switch (kind)
            {
                case MetaKind.Category:
                    // The native category hierarchy (categories ▸ platforms), as the old hardcoded tree.
                    if (dm is HostDataManagerXml h) return h.RootNodes.ToList();
                    return dm.GetAllPlatforms().Cast<object>().ToList();
                case MetaKind.Platform:
                    return dm.GetAllPlatforms()
                             .Where(p => p != null && !string.IsNullOrEmpty(p.Name))
                             .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .Cast<object>().ToList();
                case MetaKind.Playlist:
                    return (dm.GetAllPlaylists() ?? Array.Empty<IPlaylist>())
                             .Where(p => p != null && !string.IsNullOrEmpty(p.Name))
                             .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .Cast<object>().ToList();
            }
        }
        catch { }
        return new List<object>();
    }

    // ── enumerated views ────────────────────────────────────────────────────
    private static IEnumerable<string> One(string? v) { yield return v ?? ""; }

    /// <summary>One node per distinct value produced by <paramref name="sel"/> (A→Z). Multi-value selectors
    /// place a game under every value it has. Blank → "&lt;unknown&gt;".</summary>
    private static List<object> Enumerated(IReadOnlyList<IGame> games, Func<HostGame, IEnumerable<string>> sel)
    {
        var map = new Dictionary<string, List<IGame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ig in games)
        {
            if (ig is not HostGame g) continue;
            IEnumerable<string> vals;
            try { vals = sel(g) ?? Array.Empty<string>(); } catch { continue; }
            foreach (var raw in vals)
            {
                var v = string.IsNullOrWhiteSpace(raw) ? Unknown : raw.Trim();
                if (!map.TryGetValue(v, out var list)) map[v] = list = new List<IGame>();
                list.Add(ig);
            }
        }
        return map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                  .Select(kv => (object)new GroupNode(kv.Key, kv.Value)).ToList();
    }

    // ── bucket views ────────────────────────────────────────────────────────
    // Release Date → one node per year (chronological); undated games under "<unknown>" (last).
    private static List<object> ByYear(IReadOnlyList<IGame> games)
    {
        var map = new SortedDictionary<int, List<IGame>>();
        var undated = new List<IGame>();
        foreach (var ig in games)
        {
            if (ig is not HostGame g) continue;
            DateTime? d; try { d = g.ReleaseDate; } catch { d = null; }
            if (d.HasValue)
            {
                if (!map.TryGetValue(d.Value.Year, out var l)) map[d.Value.Year] = l = new List<IGame>();
                l.Add(ig);
            }
            else undated.Add(ig);
        }
        var roots = map.Select(kv => (object)new GroupNode(kv.Key.ToString(), kv.Value)).ToList();
        if (undated.Count > 0) roots.Add(new GroupNode(Unknown, undated));
        return roots;
    }

    // Star Rating → exact "N Star(s)" plus cumulative "N Star(s) +" (= N or more), then "Not Rated".
    // Mirrors LaunchBox: 1 Star, 1 Star +, 2 Stars, 2 Stars +, … 4 Stars +, 5 Stars, Not Rated
    // ("5 Stars +" is omitted — it equals "5 Stars").
    private static List<object> ByStars(IReadOnlyList<IGame> games)
    {
        var exact = new Dictionary<int, List<IGame>>();   // 1..5, exact rounded rating
        var cum = new Dictionary<int, List<IGame>>();     // "N +" = rating >= N
        var notRated = new List<IGame>();
        foreach (var ig in games)
        {
            if (ig is not HostGame g) continue;
            int s = 0;
            try { var r = g.StarRatingFloat; if (r > 0f) s = Math.Max(1, Math.Min(5, (int)Math.Round(r))); } catch { }
            if (s == 0) { notRated.Add(ig); continue; }
            if (!exact.TryGetValue(s, out var e)) exact[s] = e = new List<IGame>(); e.Add(ig);
            for (int m = 1; m <= s; m++) { if (!cum.TryGetValue(m, out var c)) cum[m] = c = new List<IGame>(); c.Add(ig); }
        }
        string Lab(int n, bool plus) => (n == 1 ? "1 Star" : n + " Stars") + (plus ? " +" : "");
        var roots = new List<object>();
        for (int s = 1; s <= 5; s++)
        {
            if (exact.TryGetValue(s, out var e)) roots.Add(new GroupNode(Lab(s, false), e));
            if (s < 5 && cum.TryGetValue(s, out var c)) roots.Add(new GroupNode(Lab(s, true), c));
        }
        if (notRated.Count > 0) roots.Add(new GroupNode("Not Rated", notRated));
        return roots;
    }

    // Progress values are stored "Bucket / Leaf" (e.g. "Not Started / Unplayed", "Active / In Progress"),
    // so LaunchBox shows a 2-level tree: bucket ▸ leaf. Bucket order is fixed (Not Started ▸ Active ▸ Done),
    // leaves A→Z; any unrecognised bucket sorts last. A value with no " / " becomes a flat node.
    private static readonly string[] ProgressOrder = { "Not Started", "Active", "Done" };
    private static List<object> ByProgress(IReadOnlyList<IGame> games)
    {
        var leaves = new Dictionary<string, Dictionary<string, List<IGame>>>(StringComparer.OrdinalIgnoreCase);
        var bucketGames = new Dictionary<string, List<IGame>>(StringComparer.OrdinalIgnoreCase);
        var hasLeaves = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void Add(string bucket, string? leaf, IGame ig)
        {
            if (!leaves.TryGetValue(bucket, out var map))
            { leaves[bucket] = map = new(StringComparer.OrdinalIgnoreCase); bucketGames[bucket] = new List<IGame>(); hasLeaves[bucket] = false; }
            bucketGames[bucket].Add(ig);
            if (leaf != null)
            {
                hasLeaves[bucket] = true;
                if (!map.TryGetValue(leaf, out var l)) map[leaf] = l = new List<IGame>();
                l.Add(ig);
            }
        }

        foreach (var ig in games)
        {
            if (ig is not HostGame g) continue;
            string raw; try { raw = g.Progress ?? ""; } catch { raw = ""; }
            int slash = raw.IndexOf('/');
            if (slash >= 0) Add(raw.Substring(0, slash).Trim(), raw.Substring(slash + 1).Trim(), ig);
            else Add(string.IsNullOrWhiteSpace(raw) ? Unknown : raw.Trim(), null, ig);
        }

        int Order(string b) { int i = Array.FindIndex(ProgressOrder, x => string.Equals(x, b, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
        var roots = new List<object>();
        foreach (var bkt in leaves.Keys.OrderBy(Order).ThenBy(b => b, StringComparer.OrdinalIgnoreCase))
        {
            List<GroupNode>? children = hasLeaves[bkt]
                ? leaves[bkt].OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                             .Select(kv => new GroupNode(kv.Key, kv.Value)).ToList()
                : null;
            roots.Add(new GroupNode(bkt, bucketGames[bkt], children));
        }
        return roots;
    }
}
