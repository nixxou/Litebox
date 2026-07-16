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
        try { ArchiveCacheEvictor.PurgeTmp(CacheRoot); } catch (Exception ex) { LbLog.Info("rom", "exit cleanup failed: " + ex.Message); }
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

            // R3 covers SmartExtract of archives. Copy/Convert of a bare disc image (no archive) is R4.
            if (!IsArchive(romAbs)) return RomLaunchResult.NotHandled;   // disc image / m3u → R4, host flat fallback

            long archiveSize = 0; try { archiveSize = new FileInfo(romAbs).Length; } catch { }
            string sig = ArchiveSig.ComputePathSignature(romAbs, archiveSize);
            string cacheRoot = CacheRoot;
            long maxBytes = (long)Math.Max(0, RomConfig.Instance.CacheMaxGb) * 1024L * 1024L * 1024L;

            // Fast path: persisted listing + already-extracted file → relaunch WITHOUT opening the archive.
            // Skipped on the "Clear → pure priority" arm (it would reuse the last-extracted entry and defeat
            // the re-pick).
            if (!pick.ForcePriority)
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
            LbLog.Info("rom", $"launch: target \"{target.FileName}\" (rule={pickRule}) unpacked={analysis.UnpackedSize / (1024 * 1024)}MB → {(outOfBand ? "TMP" : "CACHE")}");

            ArchiveExtractionResult result;
            try { result = ArchiveExtractor.ExtractOrReuse(analysis, target, row, cacheRoot, sig, outOfBand, gameTitle, platform, emuTitle); }
            catch (Exception ex) { LbLog.Info("rom", "launch: ExtractOrReuse threw: " + ex.Message); return RomLaunchResult.NotHandled; }
            if (!result.Success) { LbLog.Info("rom", "launch: extraction failed: " + result.ErrorMessage); return RomLaunchResult.NotHandled; }

            // Side effects: persistent-cache manifest + LRU trim (only when we wrote the persistent cache),
            // per-archive last-played, op-log launch-history entry.
            if (!result.ToTmp)
            {
                ArchiveCacheIndex.Record(cacheRoot, sig, gameTitle, platform, emuTitle, romAbs, row.Mode.ToString(), result.OutputFilePath);
                ArchiveCacheEvictor.KeepCacheUnder(cacheRoot, maxBytes);
            }
            RecordSideEffects(gameId, analysis.Signature?.ShortSignature ?? "", target.PathInArchive);

            return new RomLaunchResult { Success = true, Handled = true, OutputFilePath = result.OutputFilePath };
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

    /// <summary>The archive's playable entries, in scored display order. The single source
    /// the dropdown JSON + the picker both render from. Empty when unavailable (module off,
    /// not an archive, unreadable).</summary>
    public static IReadOnlyList<RomEntryView> ListEntries(IGame game, string? appId)
    {
        if (game == null || !Available) return Array.Empty<RomEntryView>();
        try
        {
            var absPath = ResolveArchivePath(game, appId);
            if (absPath == null) return Array.Empty<RomEntryView>();

            var platform = Safe(() => game.Platform) ?? "";
            var emuTitle = ResolveEmuTitle(game);
            var row = RomConfig.Instance.Resolve(platform, emuTitle);

            // Listing cache (md5(portable-path|size)) → analyse on miss, then memoise.
            long size; try { size = new FileInfo(absPath).Length; } catch { size = 0; }
            var key = ArchiveListingCache.ComputeKey(absPath, size);
            var rec = ArchiveListingCache.TryGetRecord(key);
            List<ArchiveListingEntry> entries;
            if (rec != null) entries = rec.Entries;
            else
            {
                var analysis = ArchiveAnalyzer.Analyze(absPath, RomConfig.Instance, row.Priority, row.RomExtensions, row.IgnoredExtensions);
                entries = analysis.StandaloneFiles
                    .Select(f => new ArchiveListingEntry { FileName = f.FileName ?? "", PathInArchive = f.PathInArchive ?? "", Size = (long)f.Size })
                    .ToList();
                ArchiveListingCache.Set(key, entries, absPath, size, analysis.Signature?.ShortSignature ?? "");
            }
            if (entries.Count == 0) return Array.Empty<RomEntryView>();

            var infoEntries = entries.Select(e => new ArchiveEntryInfo
            {
                FileName = e.FileName, PathInArchive = e.PathInArchive, Size = (ulong)e.Size,
                Extension = (Path.GetExtension(e.FileName) ?? "").TrimStart('.').ToLowerInvariant(),
            }).ToList();

            // R2: no favourites / last-played / RA decoration (R3 history DB + RA module).
            var favs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lastPlayed = Array.Empty<string>();
            var sorted = ArchiveAnalyzer.SortForDisplay(infoEntries, row.Priority, favs, lastPlayed,
                row.TagWeights, row.RetroAchievementsBonus, null);

            return sorted.Select(e => new RomEntryView
            {
                FileName = e.FileName,
                PathInArchive = e.PathInArchive,
                Size = (long)e.Size,
                Extension = e.Extension,
                Score = ArchiveAnalyzer.ScoreEntry(e.FileName, row.TagWeights),
            }).ToList();
        }
        catch (Exception ex) { LbLog.Info("rom", "ListEntries failed: " + ex.Message); return Array.Empty<RomEntryView>(); }
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
}
