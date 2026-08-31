// Which playable files a game has, and which one a rom_id means.
//
// One rom_id names one file. A file is one of three things, and the key is the same vocabulary the id
// ledger has always used:
//
//   main            the game's own ROM
//   app:{id}        an additional application — a version, a second disc
//   entry:{path}    one ROM inside an archive, named by its PATH: two entries can share a file name in
//                   different folders, and binding to the name would pick the wrong one
//
// ── When a game has a CHOICE ──────────────────────────────────────────────────
//
// Only such games appear in the assignment screen, and the test is ordered by what it costs:
//
//   1. additional applications → yes, answered from LaunchBox's data, no disk touched;
//   2. otherwise the ROM must be an archive AND the extractor must actually handle it for this
//      (platform, emulator) pair — RomConfig's row, Mode != DoNothing;
//   3. and the archive must hold more than one eligible entry.
//
// Step 2 is not optional and not a detail. ListEntriesDetailed does NOT consult the mode: it reads the
// row for priorities and extensions, then analyses the archive and hands back its members. So a MAME
// arcade zip — which MAME reads natively, mode DoNothing — answers with its thirty internal files if you
// ask without checking. Every arcade game would look multi-version.
//
// Step 3 opens the archive, which is why nothing above the assignment screen and the download calls it on
// a whole library. A listing gets the game's DEFAULT slot, which names no file and costs nothing.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One playable file of a game, as the assignment screen and the API see it.</summary>
internal sealed class RommFile
{
    /// <summary>"main", "app:{id}" or "entry:{path}".</summary>
    public string Key = "";
    /// <summary>What the client is told the file is called.</summary>
    public string FileName = "";
    public long Size;
    /// <summary>Human label for the assignment screen — the version's name, or the entry's.</summary>
    public string Label = "";
}

internal static class RommFiles
{
    public const string MainKey = "main";

    // ── Eligibility ───────────────────────────────────────────────────────────

    /// <summary>Does this game offer a CHOICE of file? Only these appear in the assignment screen.
    /// Ordered so the cheap answers come first and the archive is opened last, if at all.</summary>
    public static bool HasChoice(IGame game)
    {
        try
        {
            if (AdditionalApps(game).Count > 0) return true;
            return ArchiveEntriesOf(game).Count > 1;
        }
        catch { return false; }
    }

    /// <summary>The CHEAP half of HasChoice: does this game plausibly offer one? Additional applications
    /// answer from LaunchBox's data, and an archive answers from a config lookup and an extension test —
    /// neither opens a file. The assignment screen lists on this, because settling it properly means
    /// analysing the archive and a platform's worth of those would freeze the dialog on first paint.</summary>
    public static bool MayHaveChoice(IGame game)
    {
        try { return AdditionalApps(game).Count > 0 || ExtractorHandles(game); }
        catch { return false; }
    }

    /// <summary>Is this game's ROM an archive the extractor would actually take apart? False for a game
    /// whose emulator reads archives natively — the MAME/Arcade case, mode DoNothing.</summary>
    public static bool ExtractorHandles(IGame game)
    {
        try
        {
            if (!RomExtractor.Available) return false;
            var abs = RommLibrary.RomAbsPath(game);
            if (abs == null || !RomExtractor.IsArchive(abs)) return false;

            var platform = RommLibrary.PlatformOf(game);
            var emu = RomExtractor.EffectiveEmulatorTitle(game);
            return RomConfig.Instance.Resolve(platform, emu).Mode != ArchiveMode.DoNothing;
        }
        catch { return false; }
    }

    /// <summary>Do we already KNOW what is inside this game's archive, without opening it?
    ///
    /// One file stat, one md5 and one indexed read — the listing cache is keyed on (portable path, size),
    /// and it is the same cache the desktop picker fills. So a game the extractor handles is "known" once
    /// anything has looked inside it: the picker, or an earlier RomM listing.
    ///
    /// This is what lets a listing be both truthful and free. A client caches fs_name the moment it sees
    /// a row — measured: one built its download URL from that field — so a listing may never answer with
    /// a name it would later contradict. Analysing on the spot would mean opening an archive per row, and
    /// we do not have the contents of every zip. Answering with the wrong name is worse than not
    /// answering, so an archive we cannot name is not advertised at all.</summary>
    public static bool ArchiveKnown(IGame game)
    {
        try
        {
            var abs = RommLibrary.RomAbsPath(game);
            if (abs == null) return false;
            long size; try { size = new FileInfo(abs).Length; } catch { return false; }
            var key = ArchiveListingCache.ComputeKey(abs, size);
            return ArchiveListingCache.TryGetRecord(key) != null;
        }
        catch { return false; }
    }

    /// <summary>Can this game be advertised to this client? Answered ENTIRELY FROM MEMORY, because it
    /// is asked for every row of every listing.
    ///
    /// A game the extractor does not take apart is always advertisable — that test is an extension check
    /// and a config lookup, no file touched. For one it does, the client's PIN is the answer: a pin
    /// exists only for a game we managed to settle at pairing, and settling required the archive to be
    /// known. So "do we know what is inside" needs no cache probe here; it was already answered once,
    /// and the pin is the record of that answer.
    ///
    /// An unpinned archive is therefore not advertised. That is deliberate: a client caches the file
    /// name the moment it sees a row, so a name we might contradict later is worse than no row at all.</summary>
    public static bool Advertisable(IGame game, int? tokenId)
    {
        try
        {
            if (!ExtractorHandles(game)) return true;
            return RommRoms.Advertisable(game, tokenId);
        }
        catch { return true; }
    }

    /// <summary>The file name a key names, WITHOUT opening anything. An entry key carries the path
    /// inside the archive, so its basename is the answer — which is what makes a listing free once the
    /// client is locked.</summary>
    public static string? NameOfKey(IGame game, string? fileKey)
    {
        if (string.IsNullOrEmpty(fileKey)) return null;
        if (fileKey!.StartsWith("entry:", StringComparison.Ordinal))
        {
            var p = fileKey.Substring("entry:".Length).Replace('/', '\\').TrimEnd('\\');
            var name = Path.GetFileName(p);
            return name.Length > 0 ? name : null;
        }
        return Resolve(game, fileKey)?.FileName;      // main / app: are cheap, they are LaunchBox data
    }

    // La regle d'emulation

    /// <summary>Is this game part of the identity mechanism at all?
    ///
    /// Two terms. The first: does it launch through an emulator — its own EmulatorId, else its
    /// platform's default. Null for a Windows, Steam, GOG or Epic game.
    ///
    /// The second: DOSBox and ScummVM are excluded for now. They do not distribute a FILE but a game
    /// FOLDER, and until that is packaged there is nothing to hand a client. These are two BITS already
    /// resident in the game row — a bit test, not a comparison against an emulator's title, which would
    /// be sand: an emulator gets renamed, and on this very install they are called "Aaa" and "Bbb".
    ///
    /// The day those folders are shipped as generated archives, drop the second term and give them a
    /// candidate whose filepath is the game folder. Nothing else in the mechanism changes — it only ever
    /// compares recorded paths.</summary>
    public static bool IsEmulated(IGame game)
    {
        try
        {
            if (SafeFlag(() => game.UseDosBox) || SafeFlag(() => game.UseScummVm)) return false;
            return DefaultEmulatorTitle(game) != null;
        }
        catch { return false; }
    }

    private static bool SafeFlag(Func<bool> f) { try { return f(); } catch { return false; } }

    // Platform to its default emulator, built once and reused. ResolveEffectiveEmulator walks every
    // emulator and every one of its platforms when a game carries no EmulatorId of its own — the common
    // case — so asking it per game over 3057 games is a cartesian product for nothing.
    private static readonly object _emuGate = new();
    private static Dictionary<string, string?>? _platformEmu;

    /// <summary>Drops the platform-to-emulator memo. Called at the start of a pass, and by whatever
    /// changes an emulator's platform assignments.</summary>
    public static void ForgetEmulatorMap()
    {
        lock (_emuGate) { _platformEmu = null; _extractOn.Clear(); }
        RommPlatformMap.ForgetZipNative();
    }

    private static string? DefaultEmulatorTitle(IGame game)
    {
        // A game with its own emulator answers without the map at all.
        try
        {
            var own = game.EmulatorId;
            if (!string.IsNullOrEmpty(own)) return RomExtractor.EffectiveEmulatorTitle(game);
        }
        catch { }

        var platform = RommLibrary.PlatformOf(game);
        if (platform.Length == 0) return null;

        lock (_emuGate)
        {
            if (_platformEmu == null)
            {
                _platformEmu = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var emu in Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllEmulators()
                                        ?? Array.Empty<IEmulator>())
                    {
                        IEmulatorPlatform[] eps;
                        try { eps = emu.GetAllEmulatorPlatforms() ?? Array.Empty<IEmulatorPlatform>(); }
                        catch { continue; }
                        foreach (var ep in eps)
                        {
                            try
                            {
                                if (ep == null || !ep.IsDefault || string.IsNullOrEmpty(ep.Platform)) continue;
                                if (!_platformEmu.ContainsKey(ep.Platform)) _platformEmu[ep.Platform] = emu.Title;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return _platformEmu.TryGetValue(platform, out var t) ? t : null;
        }
    }

    // Les candidats, pour la passe

    /// <summary>Every playable file this game offers, in the shape the pass validates against.
    ///
    /// Two filters before anything else, and both matter: a version that does NOT run through the
    /// emulator is not distributable — Mario 64 can carry its ROM and a PC port as a version, and the
    /// port must never become a candidate — and two versions pointing at the same file ARE the same
    /// file, which is why the unique key needs no app_id.</summary>
    public static List<RommCandidate> CandidatesOf(IGame game)
    {
        var res = new List<RommCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string appId, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!seen.Add(RommIndexPass.Norm(path))) return;      // premier gagnant

            var c = new RommCandidate { AppId = appId, FilePath = path!.Trim() };
            c.IsExtract = ExtractorHandlesPath(game, c.FilePath);
            if (c.IsExtract) FillArchive(c);
            res.Add(c);
        }

        try { Add("", game.ApplicationPath); } catch { }

        try
        {
            foreach (var a in game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
            {
                if (a == null) continue;
                try { if (!a.UseEmulator) continue; } catch { continue; }   // portage PC : pas un candidat
                try { Add(a.Id ?? "", a.ApplicationPath); } catch { }
            }
        }
        catch { }

        return res;
    }

    /// <summary>The archive entry the ranking puts first — last played, then favourites, then tag score,
    /// then priority rank. Exactly what the desktop picker would select.</summary>
    public static string? RankedFirst(IGame game, RommCandidate c)
    {
        try
        {
            if (!c.Known || c.Roms.Count == 0) return null;
            var listing = RomExtractor.ListEntriesDetailed(game, c.AppId.Length == 0 ? null : c.AppId,
                                                           probeCache: false);
            foreach (var e in listing.Entries)
                if (c.Roms.Contains(e.PathInArchive)) return e.PathInArchive;
        }
        catch { }
        return null;
    }

    private static bool ExtractorHandlesPath(IGame game, string path)
    {
        try
        {
            if (!RomExtractor.Available) return false;
            var abs = RomPaths.ResolveAbsolute(path);
            if (string.IsNullOrEmpty(abs) || !RomExtractor.IsArchive(abs)) return false;

            // TWO questions, in this order, and getting the order wrong is what made 1804 arcade zips
            // look extractable on this install.
            //
            //   1. is extraction even ON for this (emulator, platform)? That is the per-platform
            //      AutoExtract — NULLABLE, null meaning "inherit" — falling back to the emulator-level
            //      flag. It is the box in Edit Emulator, and it is the only thing that says whether the
            //      module runs at all.
            //   2. only then, HOW: RomConfig's mode. DoNothing means the emulator reads the archive
            //      natively. That is the second question, never the first.
            // The zip-native family answers BEFORE the option: on these platforms the archive is
            // the rom format itself (Argosy launches it whole, hashes it whole), so whatever the
            // extraction module is set to, RomM serves the file as-is. Scrape As carries custom
            // platforms into the family.
            if (RommPlatformMap.ZipNative(RommLibrary.PlatformOf(game))) return false;

            if (!ExtractionEnabled(game)) return false;

            var platform = RommLibrary.PlatformOf(game);
            var emu = RomExtractor.EffectiveEmulatorTitle(game);
            return RomConfig.Instance.Resolve(platform, emu).Mode != ArchiveMode.DoNothing;
        }
        catch { return false; }
    }

    // (emulator title | platform) -> extraction on? Memoised: resolving it walks the emulator's
    // platform rows, and the pass asks once per candidate over the whole library.
    private static readonly Dictionary<string, bool> _extractOn = new(StringComparer.OrdinalIgnoreCase);

    private static bool ExtractionEnabled(IGame game)
    {
        var platform = RommLibrary.PlatformOf(game);
        var emu = RomExtractor.EffectiveEmulator(game);
        if (emu == null) return false;

        string title; try { title = emu.Title ?? ""; } catch { title = ""; }
        var key = title + "|" + platform;

        lock (_emuGate)
        {
            if (_extractOn.TryGetValue(key, out var hit)) return hit;

            bool on = false;
            try
            {
                bool? perPlatform = null;
                foreach (var ep in emu.GetAllEmulatorPlatforms() ?? Array.Empty<IEmulatorPlatform>())
                {
                    if (ep == null) continue;
                    try
                    {
                        if (!string.Equals(ep.Platform, platform, StringComparison.OrdinalIgnoreCase)) continue;
                        perPlatform = ep.AutoExtract;
                        break;
                    }
                    catch { }
                }
                on = perPlatform ?? SafeFlag(() => emu.AutoExtract);
            }
            catch { }

            _extractOn[key] = on;
            return on;
        }
    }

    /// <summary>Fills a candidate's archive knowledge WITHOUT opening the archive: the listing cache is
    /// keyed on (portable path, size), so this is a stat, a hash and an indexed read.</summary>
    private static void FillArchive(RommCandidate c)
    {
        try
        {
            var abs = RomPaths.ResolveAbsolute(c.FilePath);
            if (string.IsNullOrEmpty(abs)) return;
            long size; try { size = new FileInfo(abs).Length; } catch { return; }
            var rec = ArchiveListingCache.TryGetRecord(ArchiveListingCache.ComputeKey(abs, size));
            if (rec?.Entries == null || rec.Entries.Count == 0) return;
            foreach (var e in rec.Entries)
                if (!string.IsNullOrEmpty(e.PathInArchive)) c.Roms.Add(e.PathInArchive);
            c.Known = c.Roms.Count > 0;
        }
        catch { }
    }

    // ── The candidates ────────────────────────────────────────────────────────

    /// <summary>Every file this game could be served as, best first. The head is what the desktop picker
    /// would select — SortForDisplay has already floated last-played, then favourites, then score.</summary>
    public static List<RommFile> Candidates(IGame game)
    {
        var res = new List<RommFile>();
        try
        {
            var entries = ArchiveEntriesOf(game);
            if (entries.Count > 0)
            {
                foreach (var e in entries)
                    res.Add(new RommFile
                    {
                        Key = "entry:" + e.PathInArchive,
                        FileName = e.FileName,
                        Size = (long)e.Size,
                        Label = e.FileName,
                    });
                return res;
            }

            var abs = RommLibrary.RomAbsPath(game);
            res.Add(new RommFile
            {
                Key = MainKey,
                FileName = abs != null ? Path.GetFileName(abs) : RommLibrary.TitleOf(game) + ".rom",
                Size = RommLibrary.SizeOf(game),
                Label = "Main ROM",
            });

            foreach (var a in AdditionalApps(game))
            {
                string? p = null, name = null, id = null;
                try { p = a.ApplicationPath; name = a.Name; id = a.Id; } catch { }
                if (string.IsNullOrEmpty(id)) continue;
                res.Add(new RommFile
                {
                    Key = "app:" + id,
                    FileName = !string.IsNullOrEmpty(p) ? Path.GetFileName(p!) : (name ?? "version"),
                    Size = SafeSize(p),
                    Label = name ?? Path.GetFileName(p ?? "") ,
                });
            }
        }
        catch { }
        return res;
    }

    /// <summary>The file a rom_id means. A key of null — the DEFAULT slot — resolves to what the ranking
    /// picks right now; a key naming a file resolves to it and never drifts.
    ///
    /// Returns null when the key names something the game no longer has: an entry whose archive changed,
    /// a version that was deleted. The caller decides what to do about it rather than being handed a
    /// silent substitute.</summary>
    public static RommFile? Resolve(IGame game, string? fileKey)
    {
        var all = Candidates(game);
        if (all.Count == 0) return null;
        if (string.IsNullOrEmpty(fileKey) || fileKey == RommDb.DefaultKey) return all[0];
        return all.FirstOrDefault(f => string.Equals(f.Key, fileKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The key of the file a game would be served by default. Costs an archive analysis on a
    /// cold cache, so it belongs on the game page and the download — never on a library listing.</summary>
    public static string? DefaultKeyOf(IGame game) => Candidates(game).FirstOrDefault()?.Key;

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static List<Rom.RomEntryView> ArchiveEntriesOf(IGame game)
    {
        try
        {
            if (!ExtractorHandles(game)) return new List<Rom.RomEntryView>();
            // probeCache:false — the ✓ column is the picker's, and it costs two placement computations
            // and two file stats PER ENTRY.
            return RomExtractor.ListEntriesDetailed(game, null, probeCache: false).Entries.ToList();
        }
        catch { return new List<Rom.RomEntryView>(); }
    }

    private static List<IAdditionalApplication> AdditionalApps(IGame game)
    {
        var res = new List<IAdditionalApplication>();
        try
        {
            foreach (var a in game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
            {
                if (a == null) continue;
                // Only the ones that are a FILE to play. LaunchBox also stores tool entries here.
                try { if (string.IsNullOrWhiteSpace(a.ApplicationPath)) continue; } catch { continue; }
                res.Add(a);
            }
        }
        catch { }
        return res;
    }

    private static long SafeSize(string? relOrAbs)
    {
        try
        {
            if (string.IsNullOrEmpty(relOrAbs)) return 0;
            var abs = RomPaths.ResolveAbsolute(relOrAbs!);
            return File.Exists(abs) ? new FileInfo(abs).Length : 0;
        }
        catch { return 0; }
    }
}
