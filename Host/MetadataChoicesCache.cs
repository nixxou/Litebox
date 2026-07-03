// Cache of the library-distinct values that seed the Edit Game combos (Genre, Developer, Publisher, …).
// Computing these means a full pass over every game, so doing it on each editor open was slow. Instead:
//   • one cached string[] per field, each with a "dirty" flag (all dirty initially → built on first use);
//   • GameStore marks a single field dirty ONLY on an EFFECTIVE change (old value ≠ new) via that field's
//     setter chokepoint, and marks ALL fields dirty on a game add/remove;
//   • the editor pulls a field's list on demand — a rebuild pass runs only when something is dirty, and
//     accumulates every dirty field in ONE games pass. Unchanged → zero passes → instant open.
// Thread-safe (writes can arrive from background sync threads; reads from the UI thread).

using System;
using System.Collections.Generic;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class MetadataChoicesCache
{
    // <Game> XML field name (as passed to GameStore.SetGameField) → editor choice key (1:1 except Genre).
    private static readonly Dictionary<string, string> FieldToKey = new(StringComparer.Ordinal)
    {
        ["Genre"] = "Genre", ["Developer"] = "Developer", ["Publisher"] = "Publisher", ["Series"] = "Series",
        ["PlayMode"] = "PlayMode", ["Region"] = "Region", ["Source"] = "Source", ["Status"] = "Status",
        ["ReleaseType"] = "ReleaseType", ["Rating"] = "Rating", ["Progress"] = "Progress",
    };
    private static readonly HashSet<string> Multi = new(StringComparer.Ordinal) { "Genre", "Developer", "Publisher", "Series", "PlayMode" };
    // Known values merged in so common choices exist even on an empty library.
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.Ordinal)
    {
        ["Rating"] = new[] { "E", "E10+", "T", "M", "AO", "RP" },
        ["ReleaseType"] = new[] { "Full", "Demo", "Prototype", "Beta", "Homebrew", "Hack" },
        ["Status"] = new[] { "Imperfect", "Playable", "Preservable", "Unplayable" },
        ["Progress"] = new[] { "Not Started / Unplayed", "Playing / Progressing", "Beaten / Completed", "Mastered / 100%", "Abandoned" },
    };

    private static readonly object _lock = new();
    private static readonly Dictionary<string, string[]> _lists = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    static MetadataChoicesCache() => MarkAllDirty();   // everything needs building initially

    /// <summary>Called by GameStore ONLY when a game field's stored value actually changed.</summary>
    public static void MarkFieldDirty(string xmlName)
    { if (FieldToKey.TryGetValue(xmlName, out var k)) lock (_lock) _dirty.Add(k); }

    /// <summary>Called by GameStore on a game add/remove (values may appear/disappear anywhere).</summary>
    public static void MarkAllDirty()
    { lock (_lock) { foreach (var k in FieldToKey.Values) _dirty.Add(k); _dirty.Add("Platform"); } }

    /// <summary>The distinct values for a choice key (rebuilds only the dirty keys, in one games pass).</summary>
    public static string[] Get(string key, IDataManager dm)
    {
        EnsureBuilt(dm);
        lock (_lock) return _lists.TryGetValue(key, out var a) ? a : Array.Empty<string>();
    }

    private static void EnsureBuilt(IDataManager dm)
    {
        string[] toBuild;
        lock (_lock) { if (_dirty.Count == 0) return; toBuild = _dirty.ToArray(); }

        // Platform is independent of games (comes from the platform list) — cheap, rebuilt on its own.
        if (toBuild.Contains("Platform"))
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var pl in dm?.GetAllPlatforms() ?? Array.Empty<IPlatform>()) { var n = S(() => pl.Name).Trim(); if (n.Length > 0) set.Add(n); } }
            catch { }
            lock (_lock) { _lists["Platform"] = set.ToArray(); _dirty.Remove("Platform"); }
        }

        var keys = toBuild.Where(k => k != "Platform").ToArray();
        if (keys.Length == 0) return;

        var sets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            var s = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Known.TryGetValue(k, out var kn)) foreach (var v in kn) s.Add(v);
            sets[k] = s;
        }
        try
        {
            foreach (var g in dm?.GetAllGames() ?? Array.Empty<IGame>())
                foreach (var k in keys)
                {
                    string val = S(() => ReadKey(g, k));
                    if (Multi.Contains(k)) { foreach (var part in val.Split(';')) { var t = part.Trim(); if (t.Length > 0) sets[k].Add(t); } }
                    else { var t = val.Trim(); if (t.Length > 0) sets[k].Add(t); }
                }
        }
        catch { }
        lock (_lock) { foreach (var k in keys) { _lists[k] = sets[k].ToArray(); _dirty.Remove(k); } }
    }

    private static string ReadKey(IGame g, string key) => (key switch
    {
        "Genre" => g.GenresString, "Developer" => g.Developer, "Publisher" => g.Publisher, "Series" => g.Series,
        "PlayMode" => g.PlayMode, "Region" => g.Region, "Source" => g.Source, "Status" => g.Status,
        "ReleaseType" => g.ReleaseType, "Rating" => g.Rating, "Progress" => g.Progress, _ => "",
    }) ?? "";

    private static string S(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
}
