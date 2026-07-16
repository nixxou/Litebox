// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — selective extractor + disk-cache reuse. Slice R3.
// ─────────────────────────────────────────────────────────────────────────────
//
// Runs the bundled 7z.exe to extract the SELECTIVE set (the picked ROM + its
// companions, + other ROMs only if the profile asks) into the placement dir, OR
// reuses an already-extracted copy (fast path). NOT the whole archive — other ROMs
// stay excluded; a launcher / complete game's data folders come in because they are
// COMPANIONS (non-ROM), not "everything".
//
//   • preserve (7z 'x', DEFAULT): keeps each entry's in-archive sub-path so a
//     launcher keeps its data folders / a .cue keeps its tree.
//   • flatten (7z 'e', opt-in FlattenExtraction): drops picked + companions into the
//     cache dir by basename, then renames the launched file to the game Title when
//     OutputName = Title.
//
// The blocking 7z run is wrapped in Task.Run + WaitForExit: LiteBox calls this
// synchronously from its launch worker thread AFTER the "Launching…" cover is shown
// (same contract as the host's existing flat TryExtractArchive), so a few seconds'
// block is acceptable and never touches the WPF/WinForms UI thread. Ported from
// ExtendDB's ArchiveExtractor; the only rewires are RomPaths.SevenZipExe (bundled
// 7z) and LbLog.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rom;

/// <summary>Result of an extraction run.</summary>
internal sealed class ArchiveExtractionResult
{
    public bool Success { get; init; }
    public string OutputFilePath { get; init; } = "";   // file the emulator should be launched against
    public string OutputDirectory { get; init; } = "";  // dir containing the extracted file(s)
    public bool WasCacheHit { get; init; }
    public bool ToTmp { get; init; }                     // landed in \tmp (ephemeral) → not LRU-counted
    public string ErrorMessage { get; init; } = "";
}

internal static class ArchiveExtractor
{
    /// <summary>Extracts (or reuses cached) one file from <paramref name="analysis"/>. The blocking part
    /// is wrapped in Task.Run so the calling launch thread never deadlocks the dispatcher.</summary>
    public static ArchiveExtractionResult ExtractOrReuse(
        ArchiveAnalysis analysis, ArchiveEntryInfo target, ArchivePriorityRow row,
        string cacheRoot, string signature, bool outOfBand,
        string gameTitle, string platform, string emulator,
        CancellationToken ct = default)
    {
        return Task.Run(() => ExtractOrReuseCore(analysis, target, row, cacheRoot, signature,
            outOfBand, gameTitle, platform, emulator, ct), ct).GetAwaiter().GetResult();
    }

    private static ArchiveExtractionResult ExtractOrReuseCore(
        ArchiveAnalysis analysis, ArchiveEntryInfo target, ArchivePriorityRow row,
        string cacheRoot, string signature, bool outOfBand,
        string gameTitle, string platform, string emulator, CancellationToken ct)
    {
        if (analysis == null) return Fail("analysis is null");
        if (target == null) return Fail("target is null");
        if (string.IsNullOrEmpty(cacheRoot)) return Fail("cacheRoot is empty");
        if (string.IsNullOrEmpty(signature)) signature = "NOSIG";

        // Preserve the archive's directory tree (7z 'x') by default; flatten (7z 'e', by basename) only
        // when the profile opts in.
        bool preserve = !(row?.FlattenExtraction ?? false);

        string placementName = preserve ? target.PathInArchive.Replace('/', Path.DirectorySeparatorChar) : target.FileName;
        var placement = ArchiveCachePlacement.Compute(cacheRoot, signature, row,
            placementName, outOfBand, gameTitle, platform, emulator, preserveDirs: preserve);
        string outDir = placement.OutputDir;
        string expectedFile = placement.OutputFilePath;
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { return Fail("CreateDirectory failed: " + ex.Message); }
        Log($"placement: dir=\"{outDir}\" file=\"{placement.OutputFileName}\" tmp={placement.ToTmp}");

        // Cache hit: the picked file is present at the right size AND its companions are still there (a
        // missing .bin for a cached .cue would launch broken). Missing companion → fall through + re-extract.
        if (File.Exists(expectedFile))
        {
            var fi = new FileInfo(expectedFile);
            if ((ulong)fi.Length == target.Size)
            {
                bool companionsOk = true;
                if (row?.ExtractCompanions ?? false)
                    foreach (var c in analysis.CompanionFiles)
                    {
                        string cp = Path.Combine(outDir, preserve ? c.PathInArchive.Replace('/', Path.DirectorySeparatorChar) : c.FileName);
                        if (!File.Exists(cp)) { companionsOk = false; break; }
                    }
                if (companionsOk)
                {
                    try { File.SetLastWriteTime(expectedFile, DateTime.Now); Directory.SetLastWriteTime(outDir, DateTime.Now); } catch { }
                    Log("cache hit: " + expectedFile);
                    return new ArchiveExtractionResult
                    {
                        Success = true, OutputFilePath = expectedFile, OutputDirectory = outDir,
                        WasCacheHit = true, ToTmp = placement.ToTmp,
                    };
                }
                Log("cache hit on the main file but a companion is missing — re-extracting the gaps");
            }
            else Log($"cache stale (size {fi.Length} != {target.Size}) — re-extracting");
        }

        // SAME selective set in BOTH modes — the picked ROM + its companions (+ other ROMs only if asked).
        var includeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.PathInArchive };
        if (row != null && row.ExtractOtherRoms)
            foreach (var f in analysis.StandaloneFiles)
                includeSet.Add(f.PathInArchive);
        if (row != null && row.ExtractCompanions)
        {
            foreach (var c in analysis.CompanionFiles)
                includeSet.Add(c.PathInArchive);
            // ROM-ext files that DOUBLE as companions (e.g. .bin for a launched .cue): pulled when the
            // launched file isn't itself one of those extensions — so cue/bin works without "other ROMs".
            var compExts = row.CompanionExtensionSet();
            if (compExts.Count > 0 && !compExts.Contains(target.Extension ?? ""))
                foreach (var f in analysis.StandaloneFiles)
                    if (compExts.Contains(f.Extension ?? ""))
                        includeSet.Add(f.PathInArchive);
        }
        var includes = new List<string>(includeSet);

        // Mode-change hygiene: drop the OTHER mode's WHOLE sub-dir for this archive (<SIG>\F or <SIG>\P, in
        // both the cache root and \tmp) so a flat↔preserve switch leaves no stale duplicate.
        try
        {
            string otherMode = preserve ? "F" : "P";
            foreach (var baseR in new[] { cacheRoot ?? "", Path.Combine(cacheRoot ?? "", ArchiveCacheEvictor.TmpFolderName) })
            {
                var otherDir = Path.Combine(baseR, signature, otherMode);
                if (Directory.Exists(otherDir)) try { Directory.Delete(otherDir, true); } catch { }
            }
        }
        catch { }
        Log($"7z {(preserve ? "x" : "e")}: {includes.Count} entr{(includes.Count == 1 ? "y" : "ies")} (companions={(row?.ExtractCompanions ?? false)} otherRoms={(row?.ExtractOtherRoms ?? false)}) → \"{outDir}\"");

        string exe = RomPaths.SevenZipExe;
        if (!File.Exists(exe)) return Fail("7z.exe not found at " + exe);

        int exitCode;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            psi.ArgumentList.Add(preserve ? "x" : "e");   // x = keep each entry's tree, e = flatten to basenames
            psi.ArgumentList.Add(analysis.ArchivePath);
            foreach (var inc in includes) psi.ArgumentList.Add(inc);   // same selective set in both modes
            psi.ArgumentList.Add("-o" + outDir);
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-aos");   // skip files already present — don't blow away a partial cache

            using var proc = Process.Start(psi);
            if (proc == null) return Fail("Process.Start returned null");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested) { try { proc.Kill(entireProcessTree: true); } catch { } return Fail("Cancelled"); }
                proc.WaitForExit(250);
            }
            exitCode = proc.ExitCode;
        }
        catch (Exception ex) { return Fail("7z.exe run failed: " + ex.Message); }

        if (exitCode != 0) return Fail($"7z.exe exited with code {exitCode}");

        // Title rename: 7z wrote the picked file under its OWN basename; rename only the launched file
        // (companions keep their names so a .cue's internal references stay valid). Skipped in preserve mode.
        if (!preserve && !string.Equals(placement.OutputFileName, target.FileName, StringComparison.Ordinal))
        {
            var extracted = Path.Combine(outDir, target.FileName);
            try
            {
                if (File.Exists(extracted))
                {
                    if (File.Exists(expectedFile)) File.Delete(expectedFile);
                    File.Move(extracted, expectedFile);
                    Log($"renamed \"{target.FileName}\" → \"{placement.OutputFileName}\"");
                }
            }
            catch (Exception ex) { return Fail("Title rename failed: " + ex.Message); }
        }

        if (!File.Exists(expectedFile)) return Fail("Extraction completed but expected output is missing: " + expectedFile);
        try { File.SetLastWriteTime(expectedFile, DateTime.Now); Directory.SetLastWriteTime(outDir, DateTime.Now); } catch { }

        return new ArchiveExtractionResult
        {
            Success = true, OutputFilePath = expectedFile, OutputDirectory = outDir,
            WasCacheHit = false, ToTmp = placement.ToTmp,
        };
    }

    private static ArchiveExtractionResult Fail(string reason)
    {
        Log("FAIL: " + reason);
        return new ArchiveExtractionResult { Success = false, ErrorMessage = reason };
    }

    private static void Log(string msg) => LbLog.Info("rom", "extract: " + msg);
}
