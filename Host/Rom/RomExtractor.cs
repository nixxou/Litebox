// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — native facade (READ surfaces). Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// The native replacement for the reflection RomBridge → ExtendDB.HostRomBridge.
// R2 delivers the READ-only half: list an archive's playable entries (through the
// listing cache, analysing on a miss) and open the advanced picker. No extraction,
// no launch substitution — that is R3.
//
// ONE shared listing implementation (ListEntries) feeds BOTH the quick ROM
// dropdown (GetArchiveEntriesJson, byte-identical field names to HostRomBridge so
// LaunchButtons' parser is unchanged) and the advanced picker (RomPickerWindow),
// so their scored order can never drift (memory: rom-list-surfaces-sync).
//
// Favourites / last-played / RetroAchievements decoration reads from the R3
// history DB + the RA module — absent in R2, so those flags are false / empty for
// now (the JSON keys stay present; only the write side is deferred).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Rom;

/// <summary>One playable entry, decorated + scored, for every ROM-list surface.</summary>
internal sealed class RomEntryView
{
    public string FileName { get; init; } = "";
    public string PathInArchive { get; init; } = "";
    public long Size { get; init; }
    public string Extension { get; init; } = "";
    public int Score { get; init; }
    public bool IsFavorite { get; init; }     // R3: ArchiveHistory
    public bool IsLastPlayed { get; init; }    // R3: ArchiveHistory
    public bool HasRa { get; init; }           // RA module
    public string RaTitle { get; init; } = ""; // RA module
}

/// <summary>The archive's scored+decorated entry list together with the archive metadata every
/// ROM-list surface needs (absolute path, listing key, short content signature). The single value
/// the dropdown, the picker AND the web archive-entries route all render from.</summary>
internal sealed class RomEntryListing
{
    public string ArchivePath { get; init; } = "";       // absolute path of the listed archive
    public string Key { get; init; } = "";               // md5(portable-path|size) — the listing-cache key
    public string ShortSignature { get; init; } = "";    // content signature (favourites/last-played key)
    public IReadOnlyList<RomEntryView> Entries { get; init; } = Array.Empty<RomEntryView>();
    public static readonly RomEntryListing Empty = new();
}

/// <summary>Outcome of a launch-time resolve. <see cref="Handled"/>+<see cref="Success"/> with an
/// <see cref="OutputFilePath"/> = the host launches against that path; Success=false = the host falls back
/// to its flat TryExtractArchive.</summary>
internal sealed class RomLaunchResult
{
    public bool Success { get; init; }
    public bool Handled { get; init; }
    public string OutputFilePath { get; init; } = "";
    public static readonly RomLaunchResult NotHandled = new() { Success = false, Handled = false };
}

internal static class RomExtractor
{
    /// <summary>The ROM extractor module is enabled. The host hides the ROM button otherwise.</summary>
    public static bool Available => LbModules.On(LbModule.Rom);

    /// <summary>Set once at boot by HostServices: writes the launched entry into LiteBox's op-log
    /// launch-history (game id, entry identity). Null-safe — the extractor works without it.</summary>
    public static Action<string, string>? RecordLaunchEntryHook;

    /// <summary>Absolute cache root (default <c>Core\litebox\romcache</c>). Ensured to exist.</summary>
    public static string CacheRoot
    {
        get
        {
            var root = RomPaths.ResolveAbsolute(RomConfig.Instance.CachePath);
            try { Directory.CreateDirectory(root); } catch { }
            return root;
        }
    }

    /// <summary>The disc-image extension test (R4 handles the convert/copy branch; R3 only needs the
    /// predicate to leave those files alone).</summary>
    public static bool IsDiscImage(string path)
    {
        var ext = (Path.GetExtension(path ?? "") ?? "").TrimStart('.').ToLowerInvariant();
        return RomConfig.SplitCsv(RomConfig.Instance.DiscImageExtensions)
            .Any(e => string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Archive-extension test, sourced from RomConfig.ArchiveExtensions — the SAME list the
    /// extractor recognises, so callers never duplicate it. Extension only (no disk probe), matching the
    /// plugin's GameLauncher.IsRecognisedArchive; returns false for a rooted-or-relative empty extension.</summary>
    public static bool IsArchive(string path)
    {
        var ext = (Path.GetExtension(path ?? "") ?? "").TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return false;
        return RomConfig.SplitCsv(RomConfig.Instance.ArchiveExtensions)
            .Any(e => string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Purge the ephemeral \tmp band after a game exits (the emulator has released the files).
    /// Persistent &lt;SIG&gt; cache entries survive (LRU-evicted on the next extraction).</summary>
    public static void OnGameExitCleanup()
    {
        if (!Available) return;
        // Unmount any RAM drive first (only one game runs at a time, so UnmountAll is exact), then purge the
        // ephemeral \tmp band now the emulator has released the files. Both best-effort, never throw.
        try { ArchiveRamDisk.UnmountAll(); } catch (Exception ex) { LbLog.Info("rom", "exit ramdisk unmount failed: " + ex.Message); }
        try { ArchiveCacheEvictor.PurgeTmp(CacheRoot); } catch (Exception ex) { LbLog.Info("rom", "exit cleanup failed: " + ex.Message); }
    }

    /// <summary>Playlist test — the main path is an .m3u (per-archive rewrite branch).</summary>
    public static bool IsM3u(string path)
    {
        var ext = (Path.GetExtension(path ?? "") ?? "").TrimStart('.').ToLowerInvariant();
        return string.Equals(ext, "m3u", StringComparison.OrdinalIgnoreCase);
    }

    // ── Launch-time resolve (the crux) ─────────────────────────────────
    //
    // Ported from the plugin's ApplyArchiveExtractionIfAny, MINUS Harmony/PathSubstitution (LiteBox owns
    // the launch — the "substitution" is simply returning a different path). R3 handles ARCHIVES only;
    // m3u / disc-image (convert/copy/ramdisk) are R4, so this returns NotHandled for them and the host
    // falls back to its flat extractor.

    /// <summary>Resolve the loose-ROM path the emulator should launch against for an archive.
    /// <paramref name="romAbs"/> is the resolved archive path. Consumes the in-process armed pick once.</summary>
    public static RomLaunchResult ResolveLaunch(IGame game, IEmulator emulator, IEmulatorPlatform ep, string romAbs, string label)
    {
        if (game == null || !Available || string.IsNullOrEmpty(romAbs)) return RomLaunchResult.NotHandled;
        // Read the armed pick exactly once, whatever branch we take.
        var pick = RomLaunchPick.Consume(game);
        try
        {
            if (!File.Exists(romAbs)) return RomLaunchResult.NotHandled;

            string platform = Safe(() => game.Platform) ?? "";
            string emuTitle = Safe(() => emulator?.Title) ?? "";
            string gameTitle = Safe(() => game.Title) ?? "";
            string gameId = Safe(() => game.Id) ?? "";

            var row = RomConfig.Instance.Resolve(platform, emuTitle);

            // DoNothing → passthrough: the emulator reads the archive natively. Report handled so the host
            // returns the archive itself and does NOT run its flat extract-everything fallback.
            if (row.Mode == ArchiveMode.DoNothing)
            {
                LbLog.Info("rom", $"launch: mode=DoNothing → passthrough \"{Path.GetFileName(romAbs)}\"");
                return new RomLaunchResult { Success = true, Handled = true, OutputFilePath = romAbs };
            }

            // R4: per-archive m3u REWRITE (each listed file run through the pipeline) and bare disc-image
            // Convert/Copy branches. Archives fall through to the SmartExtract engine below.
            if (IsM3u(romAbs))
                return ResolveM3u(romAbs, row, gameTitle, platform, emuTitle, emulator, game);
            if (!IsArchive(romAbs))
            {
                if (IsDiscImage(romAbs))
                    return ResolveDiscImage(romAbs, row, gameId, gameTitle, platform, emuTitle);
                return RomLaunchResult.NotHandled;   // unknown extension → host flat fallback
            }

            long archiveSize = 0; try { archiveSize = new FileInfo(romAbs).Length; } catch { }
            string sig = ArchiveSig.ComputePathSignature(romAbs, archiveSize);
            string cacheRoot = CacheRoot;
            long maxBytes = (long)Math.Max(0, RomConfig.Instance.CacheMaxGb) * 1024L * 1024L * 1024L;

            // Fast path: persisted listing + already-extracted file → relaunch WITHOUT opening the archive.
            // Skipped on the "Clear → pure priority" arm (it would reuse the last-extracted entry and defeat
            // the re-pick), and when a follow-up convert / texture step is armed (both re-run every launch).
            if (!pick.ForcePriority && !row.ConvertAfterExtract && !row.TextureEnabled)
            {
                var fast = TryListingFastHit(romAbs, archiveSize, sig, row, pick, gameTitle, platform, emuTitle);
                if (fast.outputFile != null)
                {
                    LbLog.Info("rom", $"launch: listing-cache FAST HIT → \"{fast.outputFile}\"");
                    RecordSideEffects(gameId, fast.shortSig, fast.entryIdentity);
                    if (IsPersistentCachePath(cacheRoot, fast.outputFile))
                        ArchiveCacheIndex.Record(cacheRoot, sig, gameTitle, platform, emuTitle, romAbs, row.Mode.ToString(), fast.outputFile);
                    return new RomLaunchResult { Success = true, Handled = true, OutputFilePath = fast.outputFile };
                }
            }

            ArchiveAnalysis analysis;
            try { analysis = ArchiveAnalyzer.Analyze(romAbs, RomConfig.Instance, row.Priority, row.RomExtensions, row.IgnoredExtensions); }
            catch (Exception ex) { LbLog.Info("rom", "launch: analyze failed: " + ex.Message); return RomLaunchResult.NotHandled; }
            if (analysis.StandaloneFiles.Count == 0) { LbLog.Info("rom", "launch: archive has no playable candidate"); return RomLaunchResult.NotHandled; }

            // Populate the listing cache from the launch path too (so the next launch can fast-hit).
            try
            {
                var lentries = analysis.StandaloneFiles
                    .Select(e => new ArchiveListingEntry { FileName = e.FileName, PathInArchive = e.PathInArchive, Size = (long)e.Size })
                    .ToList();
                ArchiveListingCache.Set(ArchiveListingCache.ComputeKey(romAbs, archiveSize),
                    lentries, romAbs, archiveSize, analysis.Signature?.ShortSignature ?? "");
            }
            catch { }

            // Pick: explicit armed selection → weighted/priority/alpha auto (last-played honoured unless the
            // "Clear" arm forced priority).
            ArchiveEntryInfo? target = null; string pickRule = "auto";
            if (pick.HasPick && !string.IsNullOrEmpty(pick.Entry))
            {
                var sel = pick.Entry!;   // PathInArchive identity, basename fallback
                foreach (var f in analysis.StandaloneFiles)
                    if (string.Equals(f.PathInArchive, sel, StringComparison.OrdinalIgnoreCase)) { target = f; pickRule = "explicit"; break; }
                if (target == null)
                    foreach (var f in analysis.StandaloneFiles)
                        if (string.Equals(f.FileName, sel, StringComparison.OrdinalIgnoreCase)) { target = f; pickRule = "explicit"; break; }
            }
            if (target == null)
            {
                var lastPlayed = pick.ForcePriority
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : ArchiveHistory.GetLastPlayed(analysis.Signature?.ShortSignature ?? "");
                // RA bonus needs the RA module's matched-paths (kept in its own DB); not wired in R3 → null,
                // so the bonus is inert and the pick falls to tag weights / priority / alpha.
                target = ArchiveAnalyzer.PickAutoLaunch(analysis.StandaloneFiles, row.TagWeights, row.Priority, lastPlayed, 0, null);
                pickRule = pick.ForcePriority ? "auto(priority)" : "auto";
            }
            if (target == null) { LbLog.Info("rom", "launch: no playable entry"); return RomLaunchResult.NotHandled; }

            bool outOfBand = !ArchiveCacheEvictor.QualifiesForCache(analysis.UnpackedSize, RomConfig.Instance.CacheMinMb, RomConfig.Instance.CacheMaxMb);

            // Follow-up conversion (SmartExtract convert-after-extract): when the picked entry's format has
            // a rule in Conversions (e.g. an archived cue/bin → chd) we extract the disc image (+ companions)
            // to a \tmp intermediate and convert THAT into the persistent cache.
            string targetExt = (Path.GetExtension(target.FileName) ?? "").TrimStart('.').ToLowerInvariant();
            ConvertRule? followUp = row.ConvertAfterExtract ? FindConvertRule(row, targetExt) : null;
            bool willConvert = followUp != null;
            if (willConvert) LbLog.Info("rom", $"launch: follow-up convert armed .{targetExt} → {followUp!.Output}");
            else if (row.ConvertAfterExtract) LbLog.Info("rom", $"launch: convert-after ON but no Conversions rule matches .{targetExt} — extracting as-is");

            // Optional RAM disk: a per-game ImDisk drive sized to the game, used as the cache root for THIS
            // extraction only. Skipped when converting (the intermediate + the tool output go through the
            // disk band). Degrades to the disk cache on any failure (driver absent, no elevation, low RAM).
            string cacheRootForThis = cacheRoot;
            bool usedRam = false;
            if (!willConvert && row.RamDiskEnabled && ArchiveRamDisk.IsDriverInstalled())
            {
                int needMb = (int)(analysis.UnpackedSize / (1024 * 1024)) + 50;
                int freeMb = ArchiveRamDisk.GetFreeRamMb();
                if (needMb <= row.RamDiskMaxMb && needMb < freeMb)
                {
                    var ram = ArchiveRamDisk.MountForGame(gameId, needMb);
                    if (!string.IsNullOrEmpty(ram)) { cacheRootForThis = ram!; usedRam = true; LbLog.Info("rom", "launch: ramdisk " + ram + " for this launch"); }
                }
                else LbLog.Info("rom", $"launch: ramdisk skipped (need {needMb}MB, max {row.RamDiskMaxMb}MB, free {freeMb}MB)");
            }

            LbLog.Info("rom", $"launch: target \"{target.FileName}\" (rule={pickRule}) unpacked={analysis.UnpackedSize / (1024 * 1024)}MB → {(willConvert ? "CONVERT" : usedRam ? "RAM" : outOfBand ? "TMP" : "CACHE")}");

            ArchiveExtractionResult result;
            // Convert forces the extracted disc image to \tmp (intermediate); RAM extractions are ephemeral
            // (never \tmp on the RAM root); otherwise honour the size band.
            bool extractOob = willConvert || (!usedRam && outOfBand);
            try { result = ArchiveExtractor.ExtractOrReuse(analysis, target, row, cacheRootForThis, sig, extractOob, gameTitle, platform, emuTitle); }
            catch (Exception ex) { LbLog.Info("rom", "launch: ExtractOrReuse threw: " + ex.Message); return RomLaunchResult.NotHandled; }
            if (!result.Success) { LbLog.Info("rom", "launch: extraction failed: " + result.ErrorMessage); return RomLaunchResult.NotHandled; }

            string launchFile = result.OutputFilePath;
            bool launchToTmp = result.ToTmp;
            if (willConvert)
            {
                // chdman createcd needs the .cue/.gdi sheet, not a raw .bin/.img track — switch to the sibling
                // descriptor extracted alongside (companions are forced on for convert-after).
                string convInput = result.OutputFilePath;
                string convInExt = (Path.GetExtension(convInput) ?? "").TrimStart('.').ToLowerInvariant();
                if (convInExt != "cue" && convInExt != "gdi" && convInExt != "iso" && convInExt != "chd")
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(result.OutputFilePath) ?? "";
                        var desc = Directory.EnumerateFiles(dir, "*.cue").Concat(Directory.EnumerateFiles(dir, "*.gdi")).FirstOrDefault();
                        if (!string.IsNullOrEmpty(desc)) { LbLog.Info("rom", $"launch: convert input .{convInExt} is a raw track → using descriptor \"{Path.GetFileName(desc)}\""); convInput = desc!; }
                    }
                    catch (Exception ex) { LbLog.Info("rom", "launch: descriptor lookup failed: " + ex.Message); }
                }
                var convPl = ArchiveCachePlacement.Compute(cacheRoot, sig, row, target.FileName, outOfBand, gameTitle, platform, emuTitle);
                BackendResult? conv = null;
                try { conv = ConvertBackend.Convert(convInput, followUp!.Output, convPl.OutputDir, default); }
                catch (Exception ex) { LbLog.Info("rom", "launch: follow-up convert threw: " + ex.Message); }
                if (conv != null && conv.Success) { LbLog.Info("rom", $"launch: converted → \"{conv.OutputFilePath}\" (cacheHit={conv.WasCacheHit})"); launchFile = conv.OutputFilePath; launchToTmp = convPl.ToTmp; }
                else LbLog.Info("rom", $"launch: follow-up convert failed ({conv?.ErrorMessage ?? "null"}) — launching extracted file as-is");
            }

            // Texture pack: extract entries matching the profile's TextureExtensions to the (token-expanded)
            // install path. Best-effort — a failure never blocks the launch.
            if (row.TextureEnabled)
                try { ExtractTextures(analysis, row, romAbs, game, emulator); } catch (Exception ex) { LbLog.Info("rom", "texture: " + ex.Message); }

            // Side effects: persistent-cache manifest + LRU trim (only when we wrote the persistent DISK
            // cache — not RAM, not \tmp), per-archive last-played, op-log launch-history entry.
            if (!launchToTmp && !usedRam)
            {
                ArchiveCacheIndex.Record(cacheRoot, sig, gameTitle, platform, emuTitle, romAbs, row.Mode.ToString(), launchFile);
                ArchiveCacheEvictor.KeepCacheUnder(cacheRoot, maxBytes);
            }
            RecordSideEffects(gameId, analysis.Signature?.ShortSignature ?? "", target.PathInArchive);

            return new RomLaunchResult { Success = true, Handled = true, OutputFilePath = launchFile };
        }
        catch (Exception ex)
        {
            LbLog.Info("rom", "ResolveLaunch failed: " + ex.Message);
            return RomLaunchResult.NotHandled;
        }
    }

    /// <summary>Per-archive last-played + op-log launch-history entry (best-effort).</summary>
    private static void RecordSideEffects(string gameId, string shortSig, string entryIdentity)
    {
        if (!string.IsNullOrEmpty(shortSig) && !string.IsNullOrEmpty(entryIdentity))
            try { ArchiveHistory.RecordPlayed(shortSig, entryIdentity); } catch { }
        if (!string.IsNullOrEmpty(gameId) && !string.IsNullOrEmpty(entryIdentity))
            try { RecordLaunchEntryHook?.Invoke(gameId, entryIdentity); } catch { }
    }

    /// <summary>Resolve the already-extracted launch file from ONLY the persisted listing cache (no archive
    /// open). Returns nulls on a miss.</summary>
    private static (string? outputFile, string? entryIdentity, string shortSig) TryListingFastHit(
        string romAbs, long archiveSize, string sig, ArchivePriorityRow row, RomPick pick,
        string gameTitle, string platform, string emuTitle)
    {
        try
        {
            var key = ArchiveListingCache.ComputeKey(romAbs, archiveSize);
            var rec = ArchiveListingCache.TryGetRecord(key);
            if (rec?.Entries == null || rec.Entries.Count == 0) return (null, null, "");

            var standalone = rec.Entries.Select(e => new ArchiveEntryInfo
            {
                FileName = e.FileName, PathInArchive = e.PathInArchive, Size = (ulong)e.Size,
                Extension = (Path.GetExtension(e.FileName) ?? "").TrimStart('.').ToLowerInvariant(),
            }).ToList();

            ArchiveEntryInfo? t = null;
            if (pick.HasPick && !string.IsNullOrEmpty(pick.Entry))
            {
                var sel = pick.Entry!;
                foreach (var f in standalone)
                    if (string.Equals(f.PathInArchive, sel, StringComparison.OrdinalIgnoreCase)) { t = f; break; }
                if (t == null)
                    foreach (var f in standalone)
                        if (string.Equals(f.FileName, sel, StringComparison.OrdinalIgnoreCase)) { t = f; break; }
            }
            if (t == null)
            {
                var lastPlayed = ArchiveHistory.GetLastPlayed(rec.ShortSignature ?? "");
                t = ArchiveAnalyzer.PickAutoLaunch(standalone, row.TagWeights, row.Priority, lastPlayed, 0, null);
            }
            if (t == null) return (null, null, "");

            // The original extraction could have gone to cache or \tmp; probe both. Mode-aware: PRESERVE
            // lands at the entry's sub-path, FLAT at the basename.
            bool preserve = !(row?.FlattenExtraction ?? false);
            string nameForPlacement = preserve ? t.PathInArchive.Replace('/', Path.DirectorySeparatorChar) : t.FileName;
            foreach (var ob in new[] { false, true })
            {
                var pl = ArchiveCachePlacement.Compute(CacheRoot, sig, row, nameForPlacement, ob,
                    gameTitle, platform, emuTitle, preserveDirs: preserve);
                if (File.Exists(pl.OutputFilePath) && (ulong)new FileInfo(pl.OutputFilePath).Length == t.Size)
                    return (pl.OutputFilePath, t.PathInArchive, rec.ShortSignature ?? "");
            }
        }
        catch (Exception ex) { LbLog.Info("rom", "listing fast-path error: " + ex.Message); }
        return (null, null, "");
    }

    private static bool IsPersistentCachePath(string cacheRoot, string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(cacheRoot)) return false;
        if (!path.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var rel = path.Substring(cacheRoot.Length).TrimStart('\\', '/');
        return !rel.StartsWith(ArchiveCacheEvictor.TmpFolderName + "\\", StringComparison.OrdinalIgnoreCase)
            && !rel.Equals(ArchiveCacheEvictor.TmpFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The archive's playable entries, in scored display order. Thin wrapper over
    /// <see cref="ListEntriesDetailed"/> — the SINGLE source the dropdown JSON, the picker AND the
    /// web archive-entries route render from. Empty when unavailable (module off, not an archive,
    /// unreadable).</summary>
    public static IReadOnlyList<RomEntryView> ListEntries(IGame game, string? appId)
        => ListEntriesDetailed(game, appId).Entries;

    /// <summary>The archive's playable entries — scored, DECORATED (favourites/last-played from the
    /// durable per-archive history), and floated by <see cref="ArchiveAnalyzer.SortForDisplay"/> — plus
    /// the archive path/key/short-signature the web route echoes back. The one listing+scoring impl every
    /// surface shares (memory: rom-list-surfaces-sync); no caller re-lists or re-scores. RA per-entry
    /// titles are NOT sourced here (the RA module keeps them in its own per-archive DB, not yet ported to
    /// the native side) so <see cref="RomEntryView.RaTitle"/> stays empty and the theme simply shows no
    /// 🏆 marker.</summary>
    public static RomEntryListing ListEntriesDetailed(IGame game, string? appId)
    {
        if (game == null || !Available) return RomEntryListing.Empty;
        try
        {
            var absPath = ResolveArchivePath(game, appId);
            if (absPath == null) return RomEntryListing.Empty;

            var platform = Safe(() => game.Platform) ?? "";
            var emuTitle = ResolveEmuTitle(game);
            var row = RomConfig.Instance.Resolve(platform, emuTitle);

            // Listing cache (md5(portable-path|size)) → analyse on miss, then memoise.
            long size; try { size = new FileInfo(absPath).Length; } catch { size = 0; }
            var key = ArchiveListingCache.ComputeKey(absPath, size);
            var rec = ArchiveListingCache.TryGetRecord(key);
            List<ArchiveListingEntry> entries;
            string shortSig;
            if (rec != null) { entries = rec.Entries; shortSig = rec.ShortSignature ?? ""; }
            else
            {
                var analysis = ArchiveAnalyzer.Analyze(absPath, RomConfig.Instance, row.Priority, row.RomExtensions, row.IgnoredExtensions);
                entries = analysis.StandaloneFiles
                    .Select(f => new ArchiveListingEntry { FileName = f.FileName ?? "", PathInArchive = f.PathInArchive ?? "", Size = (long)f.Size })
                    .ToList();
                shortSig = analysis.Signature?.ShortSignature ?? "";
                ArchiveListingCache.Set(key, entries, absPath, size, shortSig);
            }
            if (entries.Count == 0) return new RomEntryListing { ArchivePath = absPath, Key = key, ShortSignature = shortSig };

            var infoEntries = entries.Select(e => new ArchiveEntryInfo
            {
                FileName = e.FileName, PathInArchive = e.PathInArchive, Size = (ulong)e.Size,
                Extension = (Path.GetExtension(e.FileName) ?? "").TrimStart('.').ToLowerInvariant(),
            }).ToList();

            // Decorate + float from the durable per-archive history (favourites never drive auto-launch;
            // last-played floats to the top). Keyed by the archive's SHORT signature (survives rename/move).
            var favs = ArchiveHistory.GetFavorites(shortSig);
            var lastPlayed = ArchiveHistory.GetLastPlayed(shortSig);
            var lastPlayedSet = new HashSet<string>(lastPlayed, StringComparer.OrdinalIgnoreCase);
            var sorted = ArchiveAnalyzer.SortForDisplay(infoEntries, row.Priority, favs, lastPlayed,
                row.TagWeights, row.RetroAchievementsBonus, null);

            var views = sorted.Select(e => new RomEntryView
            {
                FileName = e.FileName,
                PathInArchive = e.PathInArchive,
                Size = (long)e.Size,
                Extension = e.Extension,
                Score = ArchiveAnalyzer.ScoreEntry(e.FileName, row.TagWeights),
                // Favourites / last-played are keyed by PathInArchive; fall back to basename for flat
                // archives (and rows recorded before the switch) — matches the source's OkSorted.
                IsFavorite = favs.Contains(e.PathInArchive) || favs.Contains(e.FileName),
                IsLastPlayed = lastPlayedSet.Contains(e.PathInArchive) || lastPlayedSet.Contains(e.FileName),
            }).ToList();

            return new RomEntryListing { ArchivePath = absPath, Key = key, ShortSignature = shortSig, Entries = views };
        }
        catch (Exception ex) { LbLog.Info("rom", "ListEntriesDetailed failed: " + ex.Message); return RomEntryListing.Empty; }
    }

    /// <summary>The absolute path of the archive to act on for (game, version-appId), or null when the
    /// game/version has no on-disk recognised archive. Used by the web metadata route (which needs the
    /// path but not the entry list). Same resolution as the listing.</summary>
    public static string? ResolveArchiveAbsolutePath(IGame game, string? appId)
        => game == null || !Available ? null : ResolveArchivePath(game, appId);

    /// <summary>The archive's SHORT content signature for (game, version-appId) — the key the favourite
    /// toggle needs — resolved through the listing cache (analyse + memoise on a miss). "" when there is
    /// no on-disk recognised archive. Lighter than <see cref="ListEntriesDetailed"/> (no decoration/sort).</summary>
    public static string ResolveArchiveShortSig(IGame game, string? appId)
    {
        if (game == null || !Available) return "";
        try
        {
            var absPath = ResolveArchivePath(game, appId);
            if (absPath == null) return "";
            long size; try { size = new FileInfo(absPath).Length; } catch { size = 0; }
            var key = ArchiveListingCache.ComputeKey(absPath, size);
            var rec = ArchiveListingCache.TryGetRecord(key);
            if (rec != null) return rec.ShortSignature ?? "";

            var platform = Safe(() => game.Platform) ?? "";
            var row = RomConfig.Instance.Resolve(platform, ResolveEmuTitle(game));
            var analysis = ArchiveAnalyzer.Analyze(absPath, RomConfig.Instance, row.Priority, row.RomExtensions, row.IgnoredExtensions);
            var shortSig = analysis.Signature?.ShortSignature ?? "";
            var entries = analysis.StandaloneFiles
                .Select(f => new ArchiveListingEntry { FileName = f.FileName ?? "", PathInArchive = f.PathInArchive ?? "", Size = (long)f.Size })
                .ToList();
            ArchiveListingCache.Set(key, entries, absPath, size, shortSig);   // memoise so a later archive-entries fast-hits
            return shortSig;
        }
        catch (Exception ex) { LbLog.Info("rom", "ResolveArchiveShortSig failed: " + ex.Message); return ""; }
    }

    /// <summary>Sorted + decorated archive entries, as JSON with field names byte-identical
    /// to ExtendDB's HostRomBridge.GetArchiveEntriesJson (so LaunchButtons' dropdown parser
    /// is unchanged): { entries: [{ fileName, pathInArchive, size, isFavorite, isLastPlayed,
    /// retroAchievements }] }. Null when unavailable.</summary>
    public static string? GetArchiveEntriesJson(IGame game, string? appId)
    {
        var list = ListEntries(game, appId);
        if (list.Count == 0) return null;
        try
        {
            return JsonSerializer.Serialize(new
            {
                entries = list.Select(e => new
                {
                    fileName = e.FileName,
                    pathInArchive = e.PathInArchive,
                    size = e.Size,
                    isFavorite = e.IsFavorite,
                    isLastPlayed = e.IsLastPlayed,
                    retroAchievements = e.RaTitle,
                }),
            });
        }
        catch (Exception ex) { LbLog.Info("rom", "GetArchiveEntriesJson failed: " + ex.Message); return null; }
    }

    /// <summary>Opens the advanced ROM picker MODALLY and returns the chosen entry's
    /// in-archive path (null = cancelled / unavailable). The host arms + launches itself (R3).</summary>
    public static string? PickRomModal(IGame game, string? appId)
    {
        if (game == null || !Available) return null;
        try
        {
            var entries = ListEntries(game, appId);
            if (entries.Count == 0) return null;
            var title = Safe(() => game.Title) ?? "?";
            using var win = new RomPickerWindow(title, entries);
            return win.ShowDialog() == System.Windows.Forms.DialogResult.OK ? win.ChosenEntry : null;
        }
        catch (Exception ex) { LbLog.Info("rom", "PickRomModal failed: " + ex.Message); return null; }
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>Absolute path of the archive to list: the version's additional-app path when
    /// <paramref name="appId"/> is set, else the game's ApplicationPath. Null when missing, not
    /// on disk, or not a recognised archive extension.</summary>
    private static string? ResolveArchivePath(IGame game, string? appId)
    {
        string? rawPath = null;
        if (!string.IsNullOrEmpty(appId))
        {
            var app = FindAdditionalApp(game, appId!);
            rawPath = app != null ? Safe(() => app.ApplicationPath) : null;
        }
        if (string.IsNullOrWhiteSpace(rawPath)) rawPath = Safe(() => game.ApplicationPath);
        if (string.IsNullOrWhiteSpace(rawPath)) return null;

        var abs = RomPaths.ResolveAbsolute(rawPath);
        if (!File.Exists(abs)) return null;

        return IsArchive(abs) ? abs : null;
    }

    private static IAdditionalApplication? FindAdditionalApp(IGame game, string appId)
    {
        try
        {
            foreach (var a in game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                if (a != null && string.Equals(Safe(() => a.Id), appId, StringComparison.Ordinal)) return a;
        }
        catch { }
        return null;
    }

    private static string? ResolveEmuTitle(IGame game)
    {
        try
        {
            var id = Safe(() => game.EmulatorId);
            if (string.IsNullOrEmpty(id)) return null;
            return Safe(() => PluginHelper.DataManager?.GetEmulatorById(id)?.Title);
        }
        catch { return null; }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

    // ── R4: m3u rewrite / disc-image convert-copy / texture / convert-rule helpers ──

    /// <summary>Passthrough result — the emulator launches the file untouched (profile asks for nothing, or
    /// nothing changed). Handled=true so the host uses this path and skips its flat fallback.</summary>
    private static RomLaunchResult Passthrough(string path)
        => new() { Success = true, Handled = true, OutputFilePath = path };

    /// <summary>m3u REWRITE: read the playlist, run each referenced file through the pipeline (archive →
    /// extract, disc image → convert/copy), and write a rewritten <c>&lt;cacheRoot&gt;\tmp\&lt;SIG&gt;.m3u</c>.
    /// Returns the original path when nothing changed (or M3uInput is off).</summary>
    private static RomLaunchResult ResolveM3u(string m3uPath, ArchivePriorityRow row,
        string gameTitle, string platform, string emuTitle, IEmulator emulator, IGame game)
    {
        try
        {
            if (!row.M3uInput) { LbLog.Info("rom", "m3u: M3uInput off → passthrough"); return Passthrough(m3uPath); }
            var mgs = RomConfig.Instance;
            long size = 0; try { size = new FileInfo(m3uPath).Length; } catch { }
            string sig = ArchiveSig.ComputePathSignature(m3uPath, size);
            long maxBytes = (long)Math.Max(0, mgs.CacheMaxGb) * 1024L * 1024L * 1024L;

            string[] lines; try { lines = File.ReadAllLines(m3uPath); } catch (Exception ex) { LbLog.Info("rom", "m3u read failed: " + ex.Message); return RomLaunchResult.NotHandled; }
            string m3uDir = Path.GetDirectoryName(m3uPath) ?? "";
            var outLines = new List<string>(lines.Length);
            bool anyChanged = false;
            foreach (var raw in lines)
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) { outLines.Add(raw ?? ""); continue; }
                string entry = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(m3uDir, line));
                var pr = ProcessFileForM3u(entry, row, mgs, gameTitle, platform, emuTitle, maxBytes);
                outLines.Add(pr.outputPath);
                if (pr.changed) anyChanged = true;
            }
            LbLog.Info("rom", $"m3u: {lines.Length} line(s), changed={anyChanged}");
            if (!anyChanged) return Passthrough(m3uPath);

            string dir = Path.Combine(CacheRoot, ArchiveCacheEvictor.TmpFolderName);
            Directory.CreateDirectory(dir);
            string newM3u = Path.Combine(dir, sig + ".m3u");
            File.WriteAllLines(newM3u, outLines);
            LbLog.Info("rom", $"m3u: rewritten → \"{newM3u}\"");
            return new RomLaunchResult { Success = true, Handled = true, OutputFilePath = newM3u };
        }
        catch (Exception ex) { LbLog.Info("rom", "ResolveM3u failed: " + ex.Message); return RomLaunchResult.NotHandled; }
    }

    /// <summary>Runs ONE file through the pipeline for an m3u line: archive → selective extract, disc image →
    /// convert (Convert mode) / copy (otherwise), anything else → passthrough. Returns the launch path and
    /// whether it changed.</summary>
    private static (string outputPath, bool changed) ProcessFileForM3u(
        string filePath, ArchivePriorityRow row, RomConfig mgs,
        string gameTitle, string platform, string emulator, long maxBytes)
    {
        try
        {
            if (!File.Exists(filePath)) return (filePath, false);
            string ext = (Path.GetExtension(filePath) ?? "").TrimStart('.').ToLowerInvariant();
            bool isArchive = IsArchive(filePath);
            bool isDisc = IsDiscImage(filePath);
            long size = 0; try { size = new FileInfo(filePath).Length; } catch { }
            string sig = ArchiveSig.ComputePathSignature(filePath, size);
            string cacheRoot = CacheRoot;

            if (isArchive)
            {
                ArchiveAnalysis a;
                try { a = ArchiveAnalyzer.Analyze(filePath, mgs, row.Priority, row.RomExtensions, row.IgnoredExtensions); }
                catch { return (filePath, false); }
                if (a.StandaloneFiles.Count == 0) return (filePath, false);
                var lastPlayed = ArchiveHistory.GetLastPlayed(a.Signature?.ShortSignature ?? "");
                var t = ArchiveAnalyzer.PickAutoLaunch(a.StandaloneFiles, row.TagWeights, row.Priority, lastPlayed, 0, null);
                if (t == null) return (filePath, false);
                bool ob = !ArchiveCacheEvictor.QualifiesForCache(a.UnpackedSize, mgs.CacheMinMb, mgs.CacheMaxMb);
                var r = ArchiveExtractor.ExtractOrReuse(a, t, row, cacheRoot, sig, ob, gameTitle, platform, emulator);
                if (r.Success) { if (!r.ToTmp) ArchiveCacheEvictor.KeepCacheUnder(cacheRoot, maxBytes); return (r.OutputFilePath, true); }
                return (filePath, false);
            }
            if (isDisc)
            {
                bool ob = !ArchiveCacheEvictor.QualifiesForCache(size, mgs.CacheMinMb, mgs.CacheMaxMb);
                var pl = ArchiveCachePlacement.Compute(cacheRoot, sig, row, Path.GetFileName(filePath), ob, gameTitle, platform, emulator);
                BackendResult? br;
                if (row.Mode == ArchiveMode.Convert)
                {
                    var rule = FindConvertRule(row, ext);
                    if (rule == null) return (filePath, false);
                    br = ConvertBackend.Convert(filePath, rule.Output, pl.OutputDir, default);
                }
                else br = CopyBackend.Copy(filePath, pl.OutputDir, pl.OutputFileName);
                if (br != null && br.Success) { if (!pl.ToTmp) ArchiveCacheEvictor.KeepCacheUnder(cacheRoot, maxBytes); return (br.OutputFilePath, true); }
                return (filePath, false);
            }
            return (filePath, false);
        }
        catch (Exception ex) { LbLog.Info("rom", "m3u entry error (" + filePath + "): " + ex.Message); return (filePath, false); }
    }

    /// <summary>Bare disc-image (not an archive) Convert / Copy branch. Passthrough unless the profile asks
    /// for a disc operation (Convert with a matching rule, or Copy). Optional RAM disk, graceful fallback.</summary>
    private static RomLaunchResult ResolveDiscImage(string romAbs, ArchivePriorityRow row,
        string gameId, string gameTitle, string platform, string emuTitle)
    {
        try
        {
            var mgs = RomConfig.Instance;
            string spawnExt = (Path.GetExtension(romAbs) ?? "").TrimStart('.').ToLowerInvariant();

            ConvertRule? rule = null;
            if (row.Mode == ArchiveMode.Convert)
            {
                rule = FindConvertRule(row, spawnExt);
                if (rule == null) { LbLog.Info("rom", $"disc: Convert but no rule for .{spawnExt} → passthrough"); return Passthrough(romAbs); }
            }
            else if (row.Mode != ArchiveMode.Copy)
            {
                // SmartExtract / default on a bare disc image — the profile did not ask for a disc op.
                return Passthrough(romAbs);
            }

            long discSize = 0; try { discSize = new FileInfo(romAbs).Length; } catch { }
            string sig = ArchiveSig.ComputePathSignature(romAbs, discSize);
            string cacheRoot = CacheRoot;
            long maxBytes = (long)Math.Max(0, mgs.CacheMaxGb) * 1024L * 1024L * 1024L;
            string discFileName = Path.GetFileName(romAbs);

            BackendResult? RunDisc(string root, bool ram)
            {
                bool oob = !ram && !ArchiveCacheEvictor.QualifiesForCache(discSize, mgs.CacheMinMb, mgs.CacheMaxMb);
                var pl = ArchiveCachePlacement.Compute(root, sig, row, discFileName, oob, gameTitle, platform, emuTitle);
                if (row.Mode == ArchiveMode.Convert)
                {
                    LbLog.Info("rom", $"disc: convert .{spawnExt} → {rule!.Output}{(ram ? " (RAM " + root + ")" : oob ? " (tmp)" : " (cache)")}");
                    return ConvertBackend.Convert(romAbs, rule!.Output, pl.OutputDir, default);
                }
                LbLog.Info("rom", $"disc: copy \"{discFileName}\" → {(ram ? "RAM " + root : oob ? "tmp" : "cache")}");
                return CopyBackend.Copy(romAbs, pl.OutputDir, pl.OutputFileName);
            }

            // RAM disk decided up front so the tool writes straight into it. A convert's output size is
            // unknown → size to the cap; a copy's size IS known.
            string rootForThis = cacheRoot;
            bool usedRam = false;
            if (row.RamDiskEnabled && ArchiveRamDisk.IsDriverInstalled())
            {
                int freeMb = ArchiveRamDisk.GetFreeRamMb();
                int srcMb = (int)(discSize / (1024 * 1024));
                int wantMb = (row.Mode == ArchiveMode.Copy ? srcMb : row.RamDiskMaxMb) + 64;
                bool capOk = row.Mode != ArchiveMode.Copy || srcMb <= row.RamDiskMaxMb;
                if (capOk && wantMb < freeMb)
                {
                    var ram = ArchiveRamDisk.MountForGame(gameId, wantMb);
                    if (!string.IsNullOrEmpty(ram)) { rootForThis = ram!; usedRam = true; LbLog.Info("rom", $"disc: ramdisk {ram} ({wantMb}MB)"); }
                    else LbLog.Info("rom", "disc: ramdisk mount failed — using disk cache");
                }
                else LbLog.Info("rom", $"disc: ramdisk skipped (want {wantMb}MB, max {row.RamDiskMaxMb}MB, free {freeMb}MB)");
            }

            BackendResult? br = RunDisc(rootForThis, usedRam);
            if ((br == null || !br.Success) && usedRam)
            {
                LbLog.Info("rom", "disc: ramdisk backend failed (output likely exceeds the cap) — retrying on disk cache");
                try { ArchiveRamDisk.UnmountForGame(gameId); } catch { }
                usedRam = false;
                br = RunDisc(cacheRoot, false);
            }
            if (br == null || !br.Success) { LbLog.Info("rom", "disc: backend failed: " + (br?.ErrorMessage ?? "null")); return RomLaunchResult.NotHandled; }

            if (!usedRam && ArchiveCacheEvictor.QualifiesForCache(discSize, mgs.CacheMinMb, mgs.CacheMaxMb))
            {
                ArchiveCacheIndex.Record(cacheRoot, sig, gameTitle, platform, emuTitle, romAbs, row.Mode.ToString(), br.OutputFilePath);
                ArchiveCacheEvictor.KeepCacheUnder(cacheRoot, maxBytes);
            }
            return new RomLaunchResult { Success = true, Handled = true, OutputFilePath = br.OutputFilePath };
        }
        catch (Exception ex) { LbLog.Info("rom", "ResolveDiscImage failed: " + ex.Message); return RomLaunchResult.NotHandled; }
    }

    /// <summary>Texture pack: 7z-extract (flatten) the archive entries whose extension is in the profile's
    /// TextureExtensions into the token-expanded install path. Best-effort; no-op when disabled / empty / no
    /// path / no matching entries / 7z absent.</summary>
    private static void ExtractTextures(ArchiveAnalysis analysis, ArchivePriorityRow row, string archivePath, IGame game, IEmulator emulator)
    {
        var texExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in RomConfig.SplitCsv(row.TextureExtensions ?? "")) { var x = e.TrimStart('.').Trim().ToLowerInvariant(); if (x.Length > 0) texExts.Add(x); }
        if (texExts.Count == 0) return;

        var texEntries = analysis.Entries.Where(e => !e.IsDirectory && texExts.Contains(e.Extension ?? "")).ToList();
        if (texEntries.Count == 0) return;

        string dest = ResolveTexturePath(row, game, emulator);
        if (string.IsNullOrWhiteSpace(dest)) { LbLog.Info("rom", "texture: enabled but no install path resolved — skipping"); return; }
        try { Directory.CreateDirectory(dest); } catch (Exception ex) { LbLog.Info("rom", "texture: mkdir failed: " + ex.Message); return; }

        string exe = RomPaths.SevenZipExe;
        if (!File.Exists(exe)) { LbLog.Info("rom", "texture: 7z.exe missing — skipping"); return; }

        var args = new List<string> { "e", archivePath };
        foreach (var t in texEntries) args.Add(t.PathInArchive);
        args.Add("-o" + dest);
        args.Add("-y");
        args.Add("-aos");   // skip already-present files
        int exit = RomToolRunner.Run(exe, args, default, "texture");
        LbLog.Info("rom", $"texture: {texEntries.Count} file(s) → \"{dest}\" (7z exit={exit})");
    }

    /// <summary>Token-expanded texture install path (<c>{EmuDir}</c> <c>{GameId}</c> <c>{GameTitle}</c>), or
    /// "" when the profile has no path.</summary>
    private static string ResolveTexturePath(ArchivePriorityRow row, IGame game, IEmulator emulator)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(row.TextureExtractPath)) return "";
            string emuExe = RomPaths.ResolveAbsolute(Safe(() => emulator?.ApplicationPath) ?? "");
            string emuDir = string.IsNullOrEmpty(emuExe) ? "" : (Path.GetDirectoryName(emuExe) ?? "");
            return row.TextureExtractPath
                .Replace("{EmuDir}", emuDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{GameId}", Safe(() => game.Id) ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{GameTitle}", Safe(() => game.Title) ?? "", StringComparison.OrdinalIgnoreCase);
        }
        catch { return ""; }
    }

    /// <summary>First convert rule whose Input matches <paramref name="inputExt"/> (compound tokens like
    /// "cue/bin" match any component) and whose Output is a real target (not empty / "ignore"). Null when the
    /// format is left untouched.</summary>
    private static ConvertRule? FindConvertRule(ArchivePriorityRow row, string inputExt)
    {
        if (row?.Conversions == null || string.IsNullOrEmpty(inputExt)) return null;
        foreach (var c in row.Conversions)
        {
            if (string.IsNullOrWhiteSpace(c.Input)) continue;
            if (string.IsNullOrWhiteSpace(c.Output)
                || string.Equals(c.Output, "ignore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Output, "(ignore)", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var part in c.Input.Split('/', '+', ','))
                if (string.Equals(part.Trim(), inputExt, StringComparison.OrdinalIgnoreCase))
                    return c;
        }
        return null;
    }
}
