// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — persistent-cache manifest facade. Slice R3.
// ─────────────────────────────────────────────────────────────────────────────
//
// Manifest of what lives in the PERSISTENT archive cache — one row per top-level
// <SIG> folder (the eviction unit). Drives the cache-occupancy label + a future
// Cache Manager window. A thin facade over ArchiveCacheDb's cache_entry table
// (same shape as the plugin's, kept so both sides describe the cache identically).
//
// Scope: persistent disk cache ONLY (never \tmp, never RAM).

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Rom;

/// <summary>One cached archive/disc extraction = one top-level &lt;SIG&gt; folder.</summary>
internal sealed class CacheEntry
{
    public string Signature { get; set; } = "";
    public string GameTitle { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Emulator { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Mode { get; set; } = "";
    public string OutputFile { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CachedUtc { get; set; }
    public DateTime LastPlayedUtc { get; set; }
}

/// <summary>Facade over <see cref="ArchiveCacheDb"/>'s cache manifest.</summary>
internal static class ArchiveCacheIndex
{
    public static void Record(string cacheRoot, string sig, string gameTitle, string platform,
                              string emulator, string sourcePath, string mode, string outputFile)
        => ArchiveCacheDb.RecordCache(cacheRoot, sig, gameTitle, platform, emulator, sourcePath, mode, outputFile);

    public static void Remove(string cacheRoot, string sig) => ArchiveCacheDb.RemoveCache(sig);

    public static long Delete(string cacheRoot, string sig) => ArchiveCacheDb.DeleteCache(cacheRoot, sig);

    public static void Reconcile(string cacheRoot) => ArchiveCacheDb.Reconcile(cacheRoot);

    public static long TotalBytes(string cacheRoot) => ArchiveCacheDb.TotalBytes();

    public static List<CacheEntry> Entries(string cacheRoot) => ArchiveCacheDb.CacheEntries();
}
