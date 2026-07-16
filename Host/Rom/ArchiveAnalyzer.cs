// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — read-only archive analyzer. Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// Classifies an archive's entries (3-way: ROM candidate / companion / metadata),
// computes the content signature, and provides the shared display sort + weighted
// scorer used by EVERY ROM-list surface (dropdown, picker, later the web routes)
// so the ordering never drifts. Ported from ExtendDB's ArchiveAnalyzer; the only
// substantive change is the entry source: 7z.exe -slt (SevenZipList) instead of
// SevenZipSharp. Classification / scoring / sort logic is preserved.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LbApiHost.Host.Rom;

internal sealed class ArchiveEntryInfo
{
    public int Index { get; init; }
    public string PathInArchive { get; init; } = "";   // e.g. "subdir/foo.smc"
    public string FileName { get; init; } = "";        // basename only
    public string Extension { get; init; } = "";       // "smc" (no dot, lowercase)
    public ulong Size { get; init; }
    public uint Crc32 { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsMetadata { get; init; }
}

internal sealed class ArchiveAnalysis
{
    public string ArchivePath { get; init; } = "";
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; init; } = Array.Empty<ArchiveEntryInfo>();
    public long UnpackedSize { get; init; }

    /// <summary>Non-directory, non-metadata entries — the playable candidates.
    /// Deduplicated by FileName (first occurrence wins). Order = archive order.</summary>
    public IReadOnlyList<ArchiveEntryInfo> StandaloneFiles { get; init; } = Array.Empty<ArchiveEntryInfo>();
    public IReadOnlyList<ArchiveEntryInfo> MetadataFiles { get; init; } = Array.Empty<ArchiveEntryInfo>();
    public IReadOnlyList<ArchiveEntryInfo> CompanionFiles { get; init; } = Array.Empty<ArchiveEntryInfo>();

    public ArchiveEntryInfo? PriorityPick { get; init; }
    public ArchiveSignature? Signature { get; init; }
}

internal static class ArchiveAnalyzer
{
    /// <summary>Lists the archive (7z.exe -slt), classifies every entry, runs the
    /// priority pick, and computes the content signature. Throws if the archive is
    /// unreadable — the caller decides whether to fall through or skip.</summary>
    public static ArchiveAnalysis Analyze(string archivePath, RomConfig cfg, string priorityCsv,
        string? romExtensionsCsv = null, string? ignoredExtensionsCsv = null)
    {
        if (string.IsNullOrEmpty(archivePath)) throw new ArgumentException("archivePath is empty");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Archive not found", archivePath);

        // Ignored (metadata) list: profile override → global default.
        var metadataExt = ToExtSet(string.IsNullOrWhiteSpace(ignoredExtensionsCsv) ? cfg.MetadataExtensions : ignoredExtensionsCsv);
        // ROM-candidate list. Empty → every non-metadata file is a candidate (no companions).
        var romExt = ToExtSet(romExtensionsCsv);

        var raw = SevenZipList.List(archivePath);

        var entries = new List<ArchiveEntryInfo>(raw.Count);
        long unpackedSize = 0;
        int index = 0;
        foreach (var d in raw)
        {
            var fileName = string.IsNullOrEmpty(d.Path) ? "" : Path.GetFileName(d.Path);
            var ext = (Path.GetExtension(fileName) ?? "").TrimStart('.').ToLowerInvariant();
            var isMetadata = !d.IsDirectory && metadataExt.Contains(ext);
            if (!d.IsDirectory) unpackedSize += d.Size;

            entries.Add(new ArchiveEntryInfo
            {
                Index = index++,
                PathInArchive = d.Path ?? "",
                FileName = fileName,
                Extension = ext,
                Size = (ulong)d.Size,
                Crc32 = d.Crc,
                IsDirectory = d.IsDirectory,
                IsMetadata = isMetadata,
            });
        }

        // 3-way classification: metadata → ignored; ROM-ext (or everything non-metadata when
        // RomExtensions is empty) → playable candidate (deduped); the rest → companion.
        var standaloneFiles = new List<ArchiveEntryInfo>();
        var companionFiles = new List<ArchiveEntryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.IsDirectory || e.IsMetadata) continue;
            bool isCandidate = romExt.Count == 0 || romExt.Contains(e.Extension);
            if (isCandidate) { if (seen.Add(e.FileName)) standaloneFiles.Add(e); }
            else companionFiles.Add(e);
        }
        var metadataFiles = entries.Where(e => e.IsMetadata).ToList();

        return new ArchiveAnalysis
        {
            ArchivePath = archivePath,
            Entries = entries,
            UnpackedSize = unpackedSize,
            StandaloneFiles = standaloneFiles,
            MetadataFiles = metadataFiles,
            CompanionFiles = companionFiles,
            PriorityPick = PickByPriority(standaloneFiles, priorityCsv),
            Signature = ArchiveSig.ComputeSignature(archivePath, raw),
        };
    }

    /// <summary>Per-entry priority rank — 0-based index of the first matching pattern in
    /// <paramref name="priorityCsv"/>, or <see cref="int.MaxValue"/> when none match. Bare
    /// extensions ("bin") are normalised to "*.bin".</summary>
    public static Dictionary<string, int> ComputePriorityRanks(
        IReadOnlyList<ArchiveEntryInfo> standaloneFiles, string priorityCsv)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (standaloneFiles == null) return ranks;
        var patterns = RomConfig.SplitCsv(priorityCsv);
        var prepared = new List<string?>();
        foreach (var rawp in patterns)
        {
            var pattern = rawp.Trim();
            if (pattern.Length == 0) { prepared.Add(null); continue; }
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                pattern = pattern.StartsWith(".") ? "*" + pattern : "*." + pattern;
            prepared.Add(pattern.ToLowerInvariant());
        }
        foreach (var f in standaloneFiles)
        {
            if (ranks.ContainsKey(f.FileName)) continue;
            var lower = f.FileName.ToLowerInvariant();
            for (int i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i];
                if (p == null) continue;
                if (Wildcard.Match(lower, p)) { ranks[f.FileName] = i; break; }
            }
        }
        return ranks;
    }

    /// <summary>Shared display sort: 1) last-played (MRU first), 2) favourites not also
    /// last-played (alpha), 3) the rest by score (tag weights + RA bonus) desc when
    /// configured, else legacy priority rank asc. FileName asc is the final tiebreak.</summary>
    public static List<ArchiveEntryInfo> SortForDisplay(
        IReadOnlyList<ArchiveEntryInfo> standaloneFiles,
        string priorityCsv,
        ISet<string> favorites,
        IReadOnlyList<string> lastPlayed,
        IList<TagWeight>? weights = null,
        int raBonus = 0,
        HashSet<string>? raMatchedPaths = null)
    {
        var list = (standaloneFiles ?? Array.Empty<ArchiveEntryInfo>()).ToList();
        if (list.Count == 0) return list;

        bool scoreMode = (weights != null && weights.Count > 0)
                      || (raBonus != 0 && raMatchedPaths != null && raMatchedPaths.Count > 0);
        int Score(ArchiveEntryInfo f) =>
            ScoreEntry(f.FileName, weights)
            + (raBonus != 0 && raMatchedPaths != null && raMatchedPaths.Contains(f.PathInArchive ?? "") ? raBonus : 0);

        var ranks = ComputePriorityRanks(list, priorityCsv);
        var lpRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int r = 1;
        foreach (var f in lastPlayed ?? Array.Empty<string>())
            if (!lpRank.ContainsKey(f)) lpRank[f] = r++;
        var favs = favorites ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int Lp(ArchiveEntryInfo f) => (f.PathInArchive != null && lpRank.TryGetValue(f.PathInArchive, out var lp)) ? lp
                                    : (lpRank.TryGetValue(f.FileName ?? "", out var lp2) ? lp2 : 0);
        bool Fav(ArchiveEntryInfo f) => favs.Contains(f.PathInArchive ?? "") || favs.Contains(f.FileName ?? "");

        list.Sort((a, b) =>
        {
            int aLp = Lp(a), bLp = Lp(b);
            bool aFav = Fav(a), bFav = Fav(b);
            int aBucket = aLp > 0 ? 0 : (aFav ? 1 : 2);
            int bBucket = bLp > 0 ? 0 : (bFav ? 1 : 2);
            if (aBucket != bBucket) return aBucket.CompareTo(bBucket);

            if (aBucket == 0)
            {
                int cmp = aLp.CompareTo(bLp);
                if (cmp != 0) return cmp;
            }
            if (aBucket == 2)
            {
                if (scoreMode)
                {
                    int aSc = Score(a), bSc = Score(b);
                    if (aSc != bSc) return bSc.CompareTo(aSc);   // higher score first
                }
                else
                {
                    int aPrio = ranks.TryGetValue(a.FileName, out var rp1) ? rp1 : int.MaxValue;
                    int bPrio = ranks.TryGetValue(b.FileName, out var rp2) ? rp2 : int.MaxValue;
                    int cmp = aPrio.CompareTo(bPrio);
                    if (cmp != 0) return cmp;
                }
            }
            return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>Weighted score of a filename: sum of the weights of every matched tag —
    /// wildcard match when the tag has '*'/'?', else case-insensitive substring.</summary>
    public static int ScoreEntry(string fileName, IList<TagWeight>? weights)
    {
        if (string.IsNullOrEmpty(fileName) || weights == null || weights.Count == 0) return 0;
        var lower = fileName.ToLowerInvariant();
        int score = 0;
        foreach (var w in weights)
        {
            if (string.IsNullOrEmpty(w.Tag)) continue;
            var t = w.Tag.ToLowerInvariant();
            bool match = (t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0) ? Wildcard.Match(lower, t) : lower.Contains(t);
            if (match) score += w.Weight;
        }
        return score;
    }

    /// <summary>Highest-scoring candidate (ties broken alphabetically), or null when
    /// there is nothing to score on.</summary>
    public static ArchiveEntryInfo? PickByWeights(
        IReadOnlyList<ArchiveEntryInfo> standaloneFiles, IList<TagWeight>? weights,
        int raBonus = 0, HashSet<string>? raMatchedPaths = null)
    {
        if (standaloneFiles == null || standaloneFiles.Count == 0) return null;
        bool hasWeights = weights != null && weights.Count > 0;
        bool hasBonus = raBonus != 0 && raMatchedPaths != null && raMatchedPaths.Count > 0;
        if (!hasWeights && !hasBonus) return null;

        ArchiveEntryInfo? best = null;
        int bestScore = int.MinValue;
        foreach (var f in standaloneFiles.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
        {
            int score = ScoreEntry(f.FileName, weights);
            if (hasBonus && raMatchedPaths!.Contains(f.PathInArchive)) score += raBonus;
            if (score > bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    /// <summary>Auto-pick chain: last-played MRU → weighted score → legacy wildcard CSV →
    /// alpha first. Favourites are display-only, never a launch hint.</summary>
    public static ArchiveEntryInfo? PickAutoLaunch(
        IReadOnlyList<ArchiveEntryInfo> standaloneFiles,
        IList<TagWeight>? weights, string priorityCsv,
        IReadOnlyList<string>? lastPlayed,
        int raBonus = 0, HashSet<string>? raMatchedPaths = null)
    {
        if (standaloneFiles == null || standaloneFiles.Count == 0) return null;
        if (lastPlayed != null)
            foreach (var name in lastPlayed)
                foreach (var f in standaloneFiles)
                    if (string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase))
                        return f;
        var byWeight = PickByWeights(standaloneFiles, weights, raBonus, raMatchedPaths);
        if (byWeight != null) return byWeight;
        var byPrio = PickByPriority(standaloneFiles, priorityCsv);
        if (byPrio != null) return byPrio;
        return standaloneFiles.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>Walks the comma-separated priority patterns left-to-right and returns the
    /// first standalone entry matching (bare extensions normalised to "*.ext").</summary>
    public static ArchiveEntryInfo? PickByPriority(
        IReadOnlyList<ArchiveEntryInfo> standaloneFiles, string priorityCsv)
    {
        if (standaloneFiles == null || standaloneFiles.Count == 0) return null;
        foreach (var rawp in RomConfig.SplitCsv(priorityCsv))
        {
            var pattern = rawp.Trim();
            if (pattern.Length == 0) continue;
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                pattern = pattern.StartsWith(".") ? "*" + pattern : "*." + pattern;
            foreach (var f in standaloneFiles)
                if (Wildcard.Match(f.FileName.ToLowerInvariant(), pattern.ToLowerInvariant()))
                    return f;
        }
        return null;
    }

    private static HashSet<string> ToExtSet(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in RomConfig.SplitCsv(csv ?? "")) set.Add(p.TrimStart('.').ToLowerInvariant());
        return set;
    }
}
