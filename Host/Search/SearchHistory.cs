// Persistent advanced-search history — the desktop analog of BigBox-web's localStorage "bbw.advHistory".
// Stores the last N applied FilterCriteria (newest first, deduped by value) as JSON under
// Core\litebox\search-history.json. Each entry is the CRITERIA, never a result set.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LbApiHost.Host.Search;

internal static class SearchHistory
{
    private const int Max = 10;
    private static string Path => LiteBoxPaths.File("search-history.json");

    public static List<FilterCriteria> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new();
            var list = JsonSerializer.Deserialize<List<FilterCriteria>>(File.ReadAllText(Path));
            return list ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Record an applied criteria: dedup by value, move-to-front, cap at <see cref="Max"/>.</summary>
    public static void Add(FilterCriteria c)
    {
        try
        {
            if (c == null || !c.IsActive) return;   // only real filters, like the web (advActive gate)
            string key = c.Key();
            var list = Load().Where(x => x.Key() != key).ToList();
            list.Insert(0, c.Clone());
            if (list.Count > Max) list = list.Take(Max).ToList();
            File.WriteAllText(Path, JsonSerializer.Serialize(list));
        }
        catch { }
    }
}
