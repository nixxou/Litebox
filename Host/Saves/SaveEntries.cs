// Per-ENTRY save identity — the piece that makes an extracted ROM's save findable.
//
// The integration plugins are not wrong; they are asked the wrong question. RetroArch names a save after
// the content it loaded, and we hand the plugin the ARCHIVE, so the basename it derives is not the one on
// disk. Everything the archive case breaks (the save directory when savefiles_in_content_dir is on, the
// content sub-folder when sort_savefiles_by_content_enable is on, and the basename in every configuration)
// comes from that single input. Give the plugin the entry's path and it computes exactly what RetroArch
// did — no plugin change, and Dolphin stops failing to identify a .zip as a disc image at the same time.
//
// This file supplies the two halves:
//   • EntryGame — an IGame carrying one entry's path, the real game's identity for everything else;
//   • SaveEntries.For — which entries a game has, read from the listing cache WITHOUT extracting or even
//     opening the archive (ArchiveListingCache persists FileName/PathInArchive per entry).
//
// A save belongs to an entry, and the entry identity is carried in SaveGroupId — a field LaunchBox knows
// and round-trips, already used as a namespaced string by the plugins themselves ("saturn-<base>",
// "pcsx2:<card>:<dir>"), with UseSaveGroupIdForPersistedMatch existing precisely so a host matches on it.
// A new <GameSave> child element would read better and would be dropped by LaunchBox's next write.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Generated;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Saves;

/// <summary>One playable entry of a game's archive, as the save system addresses it.</summary>
internal sealed class SaveEntry
{
    /// <summary>The entry's file name inside the archive ("Sonic (USA).smd") — what the emulator saw, and
    /// therefore the basename the save is named after.</summary>
    public string FileName = "";

    /// <summary>Path inside the archive; the identity component, since two entries can share a file name
    /// in different folders.</summary>
    public string PathInArchive = "";

    /// <summary>The archive's content signature — survives a rename or a move of the archive itself.</summary>
    public string ShortSignature = "";

    /// <summary>The absolute path to hand the plugin. The real extracted path when the launch history
    /// still knows it, otherwise a synthetic one beside the archive: in every configuration that does not
    /// derive the save folder from the content's folder, only the basename is read.</summary>
    public string ProbePath = "";

    /// <summary>This entry has actually been played (ArchiveHistory MRU). Un-played entries are scanned
    /// only on an explicit deep search — a save exists only if something wrote it.</summary>
    public bool Played;

    /// <summary>Stable identity for SaveGroupId. Content-keyed, so it survives renaming the archive.</summary>
    public string Key => $"entry:{ShortSignature}:{PathInArchive}";

    public string DisplayName => FileName;
}

/// <summary>An IGame that answers with ONE entry's path while staying, for every other purpose, the real
/// game — its Id above all, so the plugin attributes what it finds to the right game. The IEmulator twin
/// of this trick (AbsPathEmulator) already lives in SaveManager for the same class of problem.</summary>
internal sealed class EntryGame : DummyGame
{
    private readonly IGame _inner;
    private readonly string _path;

    public EntryGame(IGame inner, string entryPath) { _inner = inner; _path = entryPath; }

    public override string ApplicationPath { get => _path; set { } }

    public override string Id { get { try { return _inner.Id; } catch { return ""; } } }
    public override string Title { get { try { return _inner.Title; } catch { return ""; } } set { } }
    public override string Platform { get { try { return _inner.Platform; } catch { return ""; } } set { } }
    public override string CommandLine { get { try { return _inner.CommandLine; } catch { return ""; } } set { } }
    public override string EmulatorId { get { try { return _inner.EmulatorId; } catch { return ""; } } set { } }
    public override string Rating { get { try { return _inner.Rating; } catch { return ""; } } set { } }

    /// <summary>The core is resolved from this by regex, so it has to be the real one or the plugin looks
    /// in the wrong per-core sub-folder.</summary>
    public override string GetEffectiveCommandLine()
    { try { return _inner.GetEffectiveCommandLine(); } catch { return CommandLine; } }

    // Left as the base's empty array on purpose: the plugin skips a game whose path is covered by one of
    // its additional applications, and this game's path is an entry no app can own.
}

internal static class SaveEntries
{
    /// <summary>The entries of a game's archive, or empty when the ROM module is off, the path is not an
    /// archive, or the archive has never been listed. Never opens the archive: a save scan must not pay
    /// for a 7z listing, and an unlisted archive degrades to exactly today's behaviour.</summary>
    public static List<SaveEntry> For(IGame game, IAdditionalApplication? focus)
    {
        var result = new List<SaveEntry>();
        if (game == null || !RomExtractor.Available) return result;

        string? appId = null;
        try { appId = focus?.Id; } catch { }

        string? archive = null;
        try { archive = RomExtractor.ResolveArchiveAbsolutePath(game, appId); } catch { }
        if (string.IsNullOrEmpty(archive) || !File.Exists(archive)) return result;

        long size; try { size = new FileInfo(archive!).Length; } catch { return result; }

        var record = ArchiveListingCache.TryGetRecord(ArchiveListingCache.ComputeKey(archive!, size));
        if (record?.Entries == null || record.Entries.Count == 0) return result;

        var shortSig = record.ShortSignature ?? "";
        var played = new HashSet<string>(
            shortSig.Length > 0 ? ArchiveHistory.GetLastPlayed(shortSig) : Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // The last launch may still name the real extracted file. Reused when it matches this entry, so a
        // configuration that DOES derive the save folder from the content's folder still resolves.
        string? lastExtracted = null;
        try { lastExtracted = Data.LaunchHistoryDb.GetLastLaunchFull(game.Id)?.extractedRomPath; } catch { }

        var archiveDir = Path.GetDirectoryName(archive!) ?? "";

        foreach (var e in record.Entries)
        {
            var name = e.FileName ?? "";
            if (name.Length == 0) continue;

            string probe = archiveDir.Length > 0 ? Path.Combine(archiveDir, name) : name;
            if (!string.IsNullOrEmpty(lastExtracted)
                && string.Equals(Path.GetFileName(lastExtracted), name, StringComparison.OrdinalIgnoreCase))
                probe = lastExtracted!;

            result.Add(new SaveEntry
            {
                FileName = name,
                PathInArchive = e.PathInArchive ?? name,
                ShortSignature = shortSig,
                ProbePath = probe,
                Played = played.Contains(name),
            });
        }

        // Played first (they are the ones that can have saves), then alphabetical — the order the picker
        // and the save page both read.
        result.Sort((a, b) => a.Played != b.Played
            ? (a.Played ? -1 : 1)
            : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>The basenames every entry would produce, longest first. Longest-first matters: with
    /// "Sonic (USA)" and "Sonic (USA) Beta" both present, a prefix match on the shorter one would claim
    /// the longer one's save.</summary>
    public static List<SaveEntry> ByLongestName(IEnumerable<SaveEntry> entries)
        => entries.OrderByDescending(e => Path.GetFileNameWithoutExtension(e.FileName).Length).ToList();

    /// <summary>Does this save file belong to <paramref name="entry"/> by name? Prefix, like the plugin's
    /// own wildcard, so ".srm" / ".state3" / a companion suffix all match.</summary>
    public static bool Matches(SaveEntry entry, string savePath)
    {
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        if (stem.Length == 0) return false;
        var file = Path.GetFileName(savePath) ?? "";
        return file.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
    }
}
