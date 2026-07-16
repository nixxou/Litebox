// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — per-archive listing cache. Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// Caches the standalone entries of an archive keyed on a PORTABLE (LB-relative)
// path signature, so the ROM dropdown + picker never re-open a multi-MB archive
// they have seen before. Thin facade over ArchiveCacheDb (SQLite). Ported from
// ExtendDB's ArchiveListingCache — ComputeKey / PortablePath are reproduced
// EXACTLY (they feed the <SIG> shared with the RetroAchievements module).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LbApiHost.Host.Rom;

/// <summary>One playable entry inside an archive — enough to drive the ROM
/// dropdown / picker without re-reading the archive.</summary>
internal sealed class ArchiveListingEntry
{
    public string FileName { get; set; } = "";       // basename only
    public string PathInArchive { get; set; } = "";  // e.g. "subdir/foo.smc"
    public long Size { get; set; }
}

internal sealed class ArchiveListingRecord
{
    public string Key { get; set; } = "";            // 10-hex path signature (DB key)
    public string ArchivePath { get; set; } = "";    // absolute path (informational)
    public long ArchiveSize { get; set; }            // bytes (informational)
    public string ShortSignature { get; set; } = ""; // content sig — favourites / last-played lookups (R3)
    public DateTime CachedAtUtc { get; set; }
    public List<ArchiveListingEntry> Entries { get; set; } = new();
}

internal static class ArchiveListingCache
{
    /// <summary>md5(lower(PORTABLE-path) + "|" + sizeBytes), 32-char lower hex. The path
    /// is made location-independent first (<see cref="PortablePath"/>) so the key — and
    /// the &lt;SIG&gt; derived from it — survives moving the whole LaunchBox folder. The DB
    /// keys on the first 10 hex of this.</summary>
    public static string ComputeKey(string absolutePath, long sizeBytes)
    {
        var raw = PortablePath(absolutePath).ToLowerInvariant() + "|" + sizeBytes;
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(32);
        for (int i = 0; i < md5.Length; i++) sb.Append(md5[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Makes a path location-independent: a file UNDER the LaunchBox root is
    /// returned RELATIVE to that root (matching LaunchBox's own relative-path storage);
    /// a file outside LB keeps its absolute path. Never throws.</summary>
    public static string PortablePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
            var lb = RomPaths.LbRoot;
            if (!string.IsNullOrEmpty(lb))
            {
                string lbFull = Path.GetFullPath(lb);
                if (lbFull.Length > 0 && lbFull[lbFull.Length - 1] != Path.DirectorySeparatorChar)
                    lbFull += Path.DirectorySeparatorChar;
                if (full.StartsWith(lbFull, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(lbFull.Length);   // relative-to-LB (portable)
            }
            return full;                                     // outside LB → absolute (stable)
        }
        catch { return path; }
    }

    // ── Read / write (delegated to ArchiveCacheDb) ────────────────────────

    public static IReadOnlyList<ArchiveListingEntry>? TryGet(string key)
        => TryGetRecord(key)?.Entries;

    public static ArchiveListingRecord? TryGetRecord(string key)
        => string.IsNullOrEmpty(key) ? null : ArchiveCacheDb.GetListingRecord(ArchiveCacheDb.Sig(key));

    public static void Set(string key, IList<ArchiveListingEntry> entries, string archivePath, long sizeBytes, string shortSignature = "")
    {
        if (string.IsNullOrEmpty(key) || entries == null) return;
        ArchiveCacheDb.SetListing(ArchiveCacheDb.Sig(key), archivePath, sizeBytes, shortSignature, entries);
    }
}
