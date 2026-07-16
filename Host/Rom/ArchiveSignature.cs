// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — archive signatures. Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// Two signatures, reproduced BYTE-FOR-BYTE from the ExtendDB plugin so LiteBox
// and the plugin (and the RetroAchievements module, which keys rom_hash off the
// path signature) agree on the same <SIG> for the same archive:
//
//   • ComputeSignature (CONTENT) — MD5 over "lower(basename)_" + for every
//     non-directory entry "C<crc>S<size>" (crc as decimal uint, size as decimal).
//     Robust to re-encoding. The ShortSignature ("<=10 alnum>_<FIRST8HEX>") keys
//     favourites / last-played (ArchiveHistory, wired in R3).
//
//   • ComputePathSignature (PATH) — first 10 UPPER-hex of
//     MD5(lower(portable-path) "|" size); == the <SIG> extraction-folder name and
//     the listing-cache DB key. Needs no archive open.
//
// The layout is load-bearing: any deviation splits the cache and detaches the RA
// module's stored rom_hash from the ROMs. Do not "clean up" the string building.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LbApiHost.Host.Rom;

/// <summary>Both forms of the content signature of an archive.</summary>
internal sealed class ArchiveSignature
{
    public string LongSignature { get; init; } = "";   // 32-char lower-hex md5
    public string ShortSignature { get; init; } = "";   // "<=10 alnum>_<FIRST8HEX>"
}

internal static class ArchiveSig
{
    /// <summary>Content signature: filename + CRC32+Size of every non-directory
    /// entry, MD5'd. Mirrors ExtendDB's ArchiveCache.ComputeSignature exactly
    /// (basename lowercased, '_' separator, then per entry 'C'&lt;crc&gt;'S'&lt;size&gt;).</summary>
    public static ArchiveSignature ComputeSignature(string archivePath, IReadOnlyList<ArchiveEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(archivePath ?? "").ToLowerInvariant()).Append('_');
        foreach (var e in entries)
        {
            if (e.IsDirectory) continue;
            sb.Append('C').Append(e.Crc).Append('S').Append((ulong)e.Size);
        }
        var md5Bytes = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var longHex = BitConverter.ToString(md5Bytes).Replace("-", "").ToLowerInvariant();

        // Short form: keep letter/digit/underscore/space of the basename, trim, take the
        // first 10, then "_" + first 8 hex of the MD5 uppercased.
        var cleaned = new StringBuilder();
        foreach (var c in Path.GetFileName(archivePath ?? ""))
            if (char.IsLetterOrDigit(c) || c == '_' || c == ' ') cleaned.Append(c);
        var trim = cleaned.ToString().Trim().Trim('_').Trim();
        var head = trim.Length >= 10 ? trim.Substring(0, 10) : trim;
        var shortSig = head + "_" + longHex.Substring(0, 8).ToUpperInvariant();

        return new ArchiveSignature { LongSignature = longHex, ShortSignature = shortSig };
    }

    /// <summary>Path signature = first 10 UPPER-hex of the listing key
    /// (MD5(lower(portable-path)|size)). Shares the exact (path|size) basis of
    /// <see cref="ArchiveListingCache.ComputeKey"/> so the folder name, the
    /// listing-cache row and the RA rom_hash key are the SAME key.</summary>
    public static string ComputePathSignature(string archivePath, long sizeBytes)
        => ArchiveListingCache.ComputeKey(archivePath, sizeBytes).Substring(0, 10).ToUpperInvariant();
}
