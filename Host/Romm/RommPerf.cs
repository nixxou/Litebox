// Phase timing for the RomM routes that have shown up slow in live traffic. Thread-local, always
// on, allocation-light: a route Reset()s, the hot helpers Add() into named buckets, and the route
// logs one line when the total crosses the threshold - so the log says WHERE a slow response spent
// its time instead of leaving us to guess between disk, covers, archives and save integrations.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LbApiHost.Host.Romm;

internal static class RommPerf
{
    [ThreadStatic] private static Dictionary<string, long>? _acc;

    public static void Reset() => _acc = new Dictionary<string, long>(StringComparer.Ordinal);

    public static long Tick() => Stopwatch.GetTimestamp();

    public static void Add(string bucket, long fromTick)
    {
        var a = _acc;
        if (a == null) return;
        var dt = Stopwatch.GetTimestamp() - fromTick;
        a[bucket] = a.TryGetValue(bucket, out var v) ? v + dt : dt;
    }

    /// <summary>Buckets sorted biggest-first, in milliseconds; clears the slate.</summary>
    public static string Report()
    {
        var a = _acc;
        _acc = null;
        if (a == null || a.Count == 0) return "(no buckets)";
        double toMs = 1000.0 / Stopwatch.Frequency;
        return string.Join(" ", a.OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key + "=" + (kv.Value * toMs).ToString("0") + "ms"));
    }
}
