// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — cache placement logic. Slice R3.
// ─────────────────────────────────────────────────────────────────────────────
//
// Pure logic deciding WHERE an extracted file lands and under WHAT name:
//
//     <cacheRoot>[\tmp]\<SIG>\<P|F>[\<subdir>]\<outputFileName>
//
//   • <SIG> (the path signature) is ALWAYS inserted right after the cache root
//     (or after \tmp) so the DB + evictor manage one archive by its <SIG> as one
//     unit.
//   • <P|F> is the extraction-mode sub-dir (P = preserve tree / F = flatten) —
//     it keeps the two on-disk layouts physically apart so a mode switch can drop
//     the other one WHOLE (no stale duplicate of any entry).
//   • <subdir> is the optional grouping after the mode, per the profile's
//     SubDirScheme (None / Title / Platform / Emulator / PlatformCode).
//   • \tmp is inserted when the extraction is ephemeral: the unpacked size is out
//     of the cache band, OR OutputName = Title (a game-specific rename can't be
//     shared in the persistent cache).
//
// No filesystem access here — the caller creates the dir and runs 7z. Ported from
// ExtendDB's ArchiveCachePlacement; PlatformMapper (a big platform→code table) is
// NOT ported, so the rarely-used PlatformCode scheme degrades to the platform name.

#nullable enable

using System.IO;
using System.Linq;

namespace LbApiHost.Host.Rom;

internal sealed class CachePlacement
{
    public string OutputDir { get; init; } = "";       // directory the file(s) land in
    public string OutputFileName { get; init; } = "";  // the launched file's name (after optional Title rename)
    public string OutputFilePath => Path.Combine(OutputDir, OutputFileName);
    public bool ToTmp { get; init; }                   // ephemeral (out-of-band or Title rename)
}

internal static class ArchiveCachePlacement
{
    /// <summary>Computes the placement for a picked entry. <paramref name="outOfBand"/> = the unpacked
    /// size is outside the configured cache band (caller computes it via
    /// <see cref="ArchiveCacheEvictor.QualifiesForCache"/>). Title-rename independently forces \tmp.</summary>
    public static CachePlacement Compute(
        string cacheRoot, string signature, ArchivePriorityRow? row,
        string originalFileName, bool outOfBand,
        string gameTitle, string platform, string emulator, bool preserveDirs = false)
    {
        // preserveDirs (7z 'x', selective set with sub-paths): the launched file keeps its in-archive
        // sub-path (originalFileName carries it) and a Title rename makes no sense, so it's forced off.
        bool titleRename = !preserveDirs && row != null && row.OutputName == OutputNameMode.Title && !string.IsNullOrEmpty(gameTitle);
        bool toTmp = outOfBand || titleRename;

        string baseDir = toTmp ? Path.Combine(cacheRoot ?? "", ArchiveCacheEvictor.TmpFolderName) : (cacheRoot ?? "");
        string dir = Path.Combine(baseDir, Sanitize(signature), preserveDirs ? "P" : "F");

        string sub = SubDir(row?.SubDirScheme ?? CacheSubDirScheme.None, gameTitle, platform, emulator);
        if (!string.IsNullOrEmpty(sub)) dir = Path.Combine(dir, Sanitize(sub));

        string fileName = originalFileName ?? "";
        if (titleRename)
            fileName = Sanitize(gameTitle) + Path.GetExtension(originalFileName ?? "");

        return new CachePlacement { OutputDir = dir, OutputFileName = fileName, ToTmp = toTmp };
    }

    private static string SubDir(CacheSubDirScheme scheme, string title, string platform, string emulator)
        => scheme switch
        {
            CacheSubDirScheme.Title => title ?? "",
            CacheSubDirScheme.Platform => platform ?? "",
            CacheSubDirScheme.Emulator => emulator ?? "",
            // Short stable platform CODE from the frozen RA platform map (override-aware) — the
            // plugin's PlatformMapper.PlatformCode analogue; unmapped platforms keep their name.
            CacheSubDirScheme.PlatformCode => Ra.RaPlatformMap.KeyFor(platform) is { Length: > 0 } code ? code : (platform ?? ""),
            _ => "",
        };

    /// <summary>Strips characters illegal in a file/dir name (the cache folder is derived from
    /// titles / platform names). Empty → "_".</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "_" : cleaned;
    }
}
