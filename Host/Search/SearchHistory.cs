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

    /// <summary>Forget one past search. Matched by VALUE (the same <see cref="FilterCriteria.Key"/> that
    /// dedups <see cref="Add"/>), not by position — the caller's list is a snapshot and the file may have
    /// moved on since it was loaded.</summary>
    public static void Remove(FilterCriteria c)
    {
        try
        {
            if (c == null) return;
            string key = c.Key();
            var kept = Load().Where(x => x.Key() != key).ToList();
            File.WriteAllText(Path, JsonSerializer.Serialize(kept));
        }
        catch { }
    }

    /// <summary>Forget every past search. The file is deleted rather than emptied — <see cref="Load"/>
    /// already treats "no file" as "no history", so an empty array would be a second way to say the same
    /// thing.</summary>
    public static void Clear()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch { }
    }
}
