// Where the profiles live: one JSON list under the global "MonitorProfiles" option key.
//
// A single row rather than one key per profile — the set is small, always read and written whole (the
// menu needs all of them, the editor rewrites all of them), and the EAV store's rule is "a new option
// is a new KEY, never a schema change". A list under one declared key keeps that promise while adding
// a profile stays a pure data edit.
//
// Reads are cached for the session and invalidated on every write, because the Tools menu rebuilds its
// profile entries on each open and has no business hitting SQLite for that.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Monitors;

internal static class MonitorProfileStore
{
    private const string Tag = "monitors";
    private const string Key = "MonitorProfiles";

    private static readonly object _gate = new();
    private static List<MonitorProfile>? _cache;

    /// <summary>Every saved profile, in user order. Never null; never the cached instance itself, so a
    /// caller editing what it got back cannot mutate the store behind its back.</summary>
    public static List<MonitorProfile> All()
    {
        lock (_gate)
        {
            if (_cache == null)
            {
                try
                {
                    _cache = LiteBoxOptionsDb.GetJson<List<MonitorProfile>>(LiteBoxOptionsDb.Global, "", Key)
                             ?? new List<MonitorProfile>();
                }
                catch (Exception ex)
                {
                    LbLog.Warn(Tag, "profile load failed: " + ex.Message);
                    _cache = new List<MonitorProfile>();
                }
            }
            return _cache.Select(Clone).ToList();
        }
    }

    /// <summary>One profile by id, or null.</summary>
    public static MonitorProfile? ById(string id)
        => string.IsNullOrEmpty(id) ? null : All().FirstOrDefault(p => p.Id == id);

    /// <summary>Replaces the whole set (the editor's Save). An empty list clears the row.</summary>
    public static void Save(IEnumerable<MonitorProfile> profiles)
    {
        var list = (profiles ?? Enumerable.Empty<MonitorProfile>()).Where(p => p != null && !p.IsEmpty).ToList();
        lock (_gate)
        {
            try
            {
                LiteBoxOptionsDb.SetJson(LiteBoxOptionsDb.Global, "", Key, list.Count == 0 ? null : list);
                _cache = list;
                LbLog.Info(Tag, $"saved {list.Count} profile(s)");
            }
            catch (Exception ex) { LbLog.Warn(Tag, "profile save failed: " + ex.Message); }
        }
    }

    /// <summary>Drops the session cache — for the (rare) case another window wrote the row.</summary>
    public static void Invalidate() { lock (_gate) _cache = null; }

    /// <summary>Deep copy through the same JSON the store round-trips, so the editor can cancel
    /// without having half-mutated the live objects.</summary>
    private static MonitorProfile Clone(MonitorProfile p)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(p);
            return System.Text.Json.JsonSerializer.Deserialize<MonitorProfile>(json) ?? p;
        }
        catch { return p; }
    }
}
