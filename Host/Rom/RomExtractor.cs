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

internal static class RomExtractor
{
    /// <summary>The ROM extractor module is enabled. The host hides the ROM button otherwise.</summary>
    public static bool Available => LbModules.On(LbModule.Rom);

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

        var ext = (Path.GetExtension(abs) ?? "").TrimStart('.').ToLowerInvariant();
        bool isArchive = RomConfig.SplitCsv(RomConfig.Instance.ArchiveExtensions)
            .Any(e => string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase));
        return isArchive ? abs : null;
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
