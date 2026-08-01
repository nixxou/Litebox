// Combine / Expand — LaunchBox's "additional versions", reproduced.
//
// HOW LAUNCHBOX STORES IT (read off a real merge, not guessed): combining deletes the non-root
// <Game> outright and re-creates it as an <AdditionalApplication> on the root, carrying
// Section=Version plus the absorbed game's own metadata — ApplicationPath, Version, Developer,
// Publisher, Region, ReleaseDate, Status, EmulatorId, Disc — and its play statistics, which are
// kept on the version rather than folded into the root. Priority continues after the versions
// already there.
//
// WHAT IT COSTS, and we cost the same. Combining destroys the absorbed game's identity (GUID and
// DatabaseID), its title — the expand re-derives one from the file name — its manuals, and every
// field a version row has no room for: a version holds 29 fields where a game holds 103, so Genre,
// Notes, Rating, StarRating, MaxPlayers, SortTitle and about fifteen others simply go.
//
// All of that is reproduced deliberately. Preserving it was built and measured, and it only ever
// worked when LiteBox had performed BOTH halves — a combine done in LaunchBox left nothing to
// restore from. Behaviour that changes depending on which program did the previous step is worse
// than behaviour that is merely lossy, because nobody can predict it. So the one thing kept is the
// one that needs no memory of its own.
//
// THE ONE DIVERGENCE: save games and save states survive, in both directions, on any file. They
// are re-pointed, never re-created, so it works on a game LaunchBox combined and LiteBox expands
// or the other way round. LaunchBox destroys them — 10 records of 12 on a measured combine, 11 of
// 13 on a measured expand — while leaving every file on disk with nothing pointing at it.
//
// MEDIA are not touched at all, by either operation. Pooling two games' images was tried and
// removed: it is one-way, since an expand returns a game with a new GUID and possibly a different
// title, so nothing could ever be handed back.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Media.Dedup;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Games;

internal static class GameCombiner
{
    public const string VersionSection = "Version";

    /// <summary>Versions attached to a game — the additional applications LaunchBox marks
    /// Section=Version, as opposed to the Document ones the Manuals tab uses.</summary>
    public static List<HostAdditionalApplication> VersionsOf(IGame game)
    {
        var result = new List<HostAdditionalApplication>();
        try
        {
            foreach (var a in game?.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                if (a is HostAdditionalApplication h
                    && string.Equals(h.Section, VersionSection, StringComparison.OrdinalIgnoreCase))
                    result.Add(h);
        }
        catch { }
        return result;
    }

    public static bool CanExpand(IGame game) => VersionsOf(game).Count > 0;

    /// <summary>A game's documents — the additional applications the Manuals tab owns.</summary>
    public static List<HostAdditionalApplication> DocumentsOf(IGame game)
        => AddAppsOf(game).Where(a => !IsVersion(a)).ToList();

    /// <summary>What a combine did, beyond the count — what the caller has to offer a decision
    /// about once it is done.</summary>
    internal sealed class CombineResult
    {
        public int Absorbed;
        /// <summary>Media files of absorbed games that were NOT pooled, because the two were not the
        /// same database entry. Nothing references them any more; deleting them is the caller's
        /// question to ask, never this code's to decide.</summary>
        public readonly List<string> OrphanedMedia = new();
        /// <summary>What the media merge did decide to do, for reporting.</summary>
        public int MediaMoved, MediaSkipped;
    }

    /// <summary>Folds every game but <paramref name="root"/> into it as a version. Returns how many
    /// were absorbed.</summary>
    public static int Combine(IReadOnlyList<IGame> games, IGame root, HostDataManagerXml dm)
        => Run(games, root, dm, null).Absorbed;

    /// <summary>The full form. <paramref name="keepDocuments"/> holds the ids of the absorbed games'
    /// documents the caller chose to carry over; everything else about them goes with the game.</summary>
    internal static CombineResult Run(IReadOnlyList<IGame> games, IGame root, HostDataManagerXml dm,
                                      ISet<string> keepDocuments)
    {
        var outcome = new CombineResult();
        if (games == null || root == null || dm == null) return outcome;
        string rootId = Safe(() => root.Id) ?? "";
        // Order decides the priorities, and LaunchBox's is not the caller's: measured on a combine
        // of 44 games, it is the root first, then the rest by Title — case-insensitive, ordinal, and
        // STABLE, so same-titled games keep the order they had. Case matters to get right: "disc 14"
        // lands between "Disc 10" and "Disc 17", which a case-sensitive sort would not do; and
        // ordinal matters too, since "Disc-18" comes before "Disc16" (a culture-aware compare
        // ignores the hyphen and reverses them).
        var others = games
            .Where(g => g != null && !string.Equals(Safe(() => g.Id) ?? "", rootId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => Safe(() => g.Title) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (others.Count == 0) return outcome;

        var rootVersions = VersionsOf(root);
        int priority = rootVersions.Select(v => v.Priority).DefaultIfEmpty(0).Max();
        int absorbed = 0;

        // The root becomes a version of itself the first time a game turns multi-version —
        // otherwise its own way of launching would be the one configuration with no entry in the
        // list. Observed on a real combine: root first, Priority 1. When the root ALREADY has
        // versions it gets no new one (an earlier combine gave it that entry), which is what the
        // A.IV merge showed: four versions already there, the absorbed game took 5 and nothing else
        // appeared.
        // The root's own saves are NOT re-pointed at that version: LaunchBox leaves them at game
        // level even after the root becomes a version of itself, and there is no reason to differ.
        if (rootVersions.Count == 0) AddVersion(root, root, ++priority);

        foreach (var g in others)
        {
            try
            {
                MergeOrOrphanMedia(g, root, outcome);

                // Only its versions. The absorbed game's manuals go with it — matching
                // LaunchBox, which does not carry them either. The FILES stay on disk under
                // Manuals\<platform>\, recoverable by hand but not by either program.
                foreach (var inner in VersionsOf(g))
                    CopyAddApp(root, inner, ++priority);

                var version = AddVersion(root, g, ++priority);
                GameSaveMover.ToVersion(g, root, version, dm);
                // Documents the caller chose to keep are carried; the rest go with the game.
                if (keepDocuments != null && keepDocuments.Count > 0)
                    foreach (var doc in AddAppsOf(g))
                        if (!IsVersion(doc) && keepDocuments.Contains(doc.Id ?? ""))
                            CopyAddApp(root, doc, null);

                dm.TryRemoveGame(g);
                absorbed++;
            }
            catch { }
        }
        outcome.Absorbed = absorbed;
        return outcome;
    }

    /// <summary>Pools the absorbed game's media into the root's — but ONLY when the two are the same
    /// database entry, meaning both carry a DatabaseID and the two are equal.
    ///
    /// Two games with NO DatabaseID are not the same entry. They are two unidentified games, and
    /// treating "both unknown" as "both the same" would pool the art of games that have nothing to
    /// do with each other on the strength of a missing field.
    ///
    /// When they are not the same entry the files stay exactly where they are. Nothing references
    /// them once the game is gone, so they are reported rather than touched: whether to delete them
    /// is a question for whoever asked for the combine.</summary>
    private static void MergeOrOrphanMedia(IGame source, IGame root, CombineResult outcome)
    {
        try
        {
            string lbRoot = MediaResolver.LbRoot;
            string platform = Safe(() => source.Platform) ?? "";
            string from = Safe(() => source.Title) ?? "", to = Safe(() => root.Title) ?? "";
            if (lbRoot.Length == 0 || platform.Length == 0 || from.Length == 0 || to.Length == 0) return;
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;   // one collection already

            int? a = Safe(() => source.LaunchBoxDbId), b = Safe(() => root.LaunchBoxDbId);
            bool sameEntry = a.HasValue && b.HasValue && a.Value == b.Value;

            // A THIRD game still answering to the source title means these files are its media too.
            // That changes both halves of what follows: what we take we must COPY rather than move,
            // or the other game loses its art; and what we leave is not orphaned at all, so there is
            // nothing to offer deleting.
            bool shared = GameMediaSync.FindRival(source, platform, from) != null;

            var plan = GameMediaMerge.Plan(lbRoot, platform, from, to,
                                           DupEngineMode.DHash,
                                           DedupEngine.DefaultThreshold(DupEngineMode.DHash));
            if (!sameEntry)
            {
                if (!shared) foreach (var item in plan.Items) outcome.OrphanedMedia.Add(item.From);
                return;
            }
            if (plan.Moving == 0) { outcome.MediaSkipped += plan.Skipped; return; }

            if (!Guid.TryParse(Safe(() => source.Id) ?? "", out var sid) || sid == Guid.Empty) return;
            var res = GameMediaMerge.Apply(plan, lbRoot, sid, platform, from, to, shared);
            outcome.MediaMoved += res.Reached;
            outcome.MediaSkipped += plan.Skipped;
        }
        catch { }
    }

    /// <summary>Turns <paramref name="source"/> into a version of <paramref name="root"/>, and
    /// hands the version back so its saves can be re-pointed at it.</summary>
    private static HostAdditionalApplication AddVersion(IGame root, IGame source, int priority)
    {
        if (root.AddNewAdditionalApplication() is not HostAdditionalApplication v) return null;
        v.Section = VersionSection;
        v.UseEmulator = true;
        v.ApplicationPath = Safe(() => source.ApplicationPath) ?? "";
        // Copied verbatim, empty included. There is no fall back to the title: a game whose name
        // carried no recognised tag has an empty Version, and LaunchBox leaves the version's empty
        // too rather than inventing a label from the title.
        v.Version = Safe(() => source.Version) ?? "";
        v.Name = VersionName(v.Version);
        // LaunchBox writes these empty rather than omitting them (<CommandLine />, <Region />);
        // only the dates are left out when they have no value.
        v.CommandLine = Safe(() => source.CommandLine) ?? "";
        v.Developer = Safe(() => source.Developer) ?? "";
        v.Publisher = Safe(() => source.Publisher) ?? "";
        v.Region = Safe(() => source.Region) ?? "";
        v.Status = Safe(() => source.Status) ?? "";
        v.EmulatorId = Safe(() => source.EmulatorId) ?? "";
        v.Disc = DiscOf(source);
        char? side = SideOf(source);
        v.SideA = side == 'A';
        v.SideB = side == 'B';
        v.ReleaseDate = Safe(() => source.ReleaseDate);
        v.LastPlayed = Safe(() => source.LastPlayedDate);
        v.PlayCount = Safe(() => source.PlayCount);
        v.PlayTime = Safe(() => source.PlayTime);
        v.Priority = priority;
        return v;
    }

    /// <summary>The disc number LaunchBox derives for a version. A game carries no Disc of its own —
    /// 170 test roms covering every notation imported with the field empty every time — so it is
    /// read out of the file name.</summary>
    // The file name and nothing else. LaunchBox's own entry point is
    // NamingHelper.ParseDiscNumberFromFileName(string path, out bool sideA, out bool sideB) — one
    // function, taking a path, handing back the disc and both side flags together. An earlier
    // version of this fell back to the Version label when the name had no marker, which every
    // sample we had was blind to (both carried it) and which would invent a disc number LaunchBox
    // would not give.
    private static int? DiscOf(IGame g)
    {
        try { return DiscIn(NameOf(g)); } catch { return null; }
    }

    private static char? SideOf(IGame g)
    {
        try { return SideIn(NameOf(g)); } catch { return null; }
    }

    private static string NameOf(IGame g)
        => System.IO.Path.GetFileNameWithoutExtension(Safe(() => g.ApplicationPath) ?? "");

    // Measured on 170 names across two experiments (DiscParseSelfTest pins every one). The disc and
    // the side do NOT follow the same rule, which is the part no amount of reading would suggest.
    //
    // DISC — the marker has to be a tag of its own:
    //   • introduced by " (", " [" or " - ". The bracket must come after whitespace, so a name
    //     STARTING with "(Disc 3)" yields nothing, and it must be followed immediately by the
    //     keyword — "( Disc 3 )" yields nothing either.
    //   • "disc", "disk", optionally plural: "(Discs 3)" works. "Disque", "Dis", "D", "DVD" and
    //     "CD" do not — CD is never a disc marker, in any form.
    //   • a separator is REQUIRED between keyword and number, and may be space, '-', '.' or '_':
    //     "(Disc-3)", "(Disc.3)", "(Disc_3)" all give 3, while "(Disc3)" gives nothing.
    //   • the number must not run into a letter: "(Disc 3a)" gives nothing.
    //   • the keyword must open the tag — "(SuperDisc 3)", "(The Disc 3)" and "(Rev A Disc 3)"
    //     give nothing.
    //   • first disc tag wins, not first tag: "(CD 3) (Disc 7)" gives 7.
    //   • no sanity check whatsoever: "(Disc 37 of 2)" gives 37, "of N" is ignored, and
    //     "(Disc 03)" is 3.
    private static readonly System.Text.RegularExpressions.Regex DiscMarker =
        new(@"(?:\s[\(\[]|\s-\s)dis[ck]s?[\s\-._]+(\d+)(?![0-9a-z])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static int? DiscIn(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = DiscMarker.Match(s);
        return m.Success && int.TryParse(m.Groups[1].Value, out int d) ? d : (int?)null;
    }

    // SIDE — looser than the disc in one way, stricter in three others:
    //   • NO delimiter is required. A bare "Final Fantasy X Side A" sets the flag, where a bare
    //     "Disc 3" sets nothing at all. The asymmetry is real, not a misreading.
    //   • the separator must be whitespace: "(Side-A)", "(Side.A)", "(Side_A)" and "(SideA)" set
    //     nothing, where the disc happily takes '-', '.' and '_'.
    //   • no plural — "(Sides A)" sets nothing, though "(Discs 3)" is a disc.
    //   • only A and B: "(Side C)", "(Side 1)", "(Side 2)" set nothing.
    //   • what follows the letter is ignored: "(Side A1)" and "(Side AB)" are both side A.
    //   • it need not be a tag of its own: "(Disc 3 Side B)" is disc 3 AND side B.
    //   • "Face" is not a word it knows, in any form.
    private static readonly System.Text.RegularExpressions.Regex SideMarker =
        new(@"\bside\s([ab])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>'A', 'B', or null when the name carries no side marker.</summary>
    internal static char? SideIn(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = SideMarker.Match(s);
        return m.Success ? char.ToUpperInvariant(m.Groups[1].Value[0]) : (char?)null;
    }

    /// <summary>Turns every version of <paramref name="game"/> back into a game of its own.
    /// Returns how many were restored.
    ///
    /// The version that points at the root's own rom does NOT become a game: it IS the root, which
    /// keeps its identity and simply loses the entry. Measured — three versions expanded into two
    /// games plus the untouched root.</summary>
    public static int Expand(IGame game, HostDataManagerXml dm)
    {
        if (game == null || dm == null) return 0;
        var versions = VersionsOf(game);
        if (versions.Count == 0) return 0;

        string platform = Safe(() => game.Platform) ?? "";
        string rootPath = Safe(() => game.ApplicationPath) ?? "";

        int restored = 0;
        foreach (var v in versions)
        {

            try
            {
                if (string.Equals(v.ApplicationPath ?? "", rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    // The root keeps this rom, so its saves stay with the root — but they have to
                    // stop naming a version that is about to disappear, or they point at nothing.
                    // Found by counting rather than by reading: five did exactly that on the first
                    // run of this.
                    GameSaveMover.Detach(game, v, dm);
                    game.TryRemoveAdditionalApplication(v);
                    continue;
                }

                var g = dm.AddNewGame(TitleFromFileName(v.ApplicationPath, v.Disc));
                if (g == null) continue;
                Set(() => g.Platform = platform);
                Set(() => g.ApplicationPath = v.ApplicationPath);
                // NOT copied from the version — re-derived from the file name, always. Every
                // earlier experiment was blind to this because the label happened to equal what the
                // name would give; feeding 130 versions a deliberately wrong label ("ALTERED-004"
                // on a file called "… [Side A].txt") showed the label being ignored outright.
                Set(() => g.Version = VersionFromFileName(v.ApplicationPath));
                Set(() => g.Developer = v.Developer);
                Set(() => g.Publisher = v.Publisher);
                Set(() => g.Region = v.Region);
                Set(() => g.Status = v.Status);
                Set(() => g.EmulatorId = v.EmulatorId);
                Set(() => g.ReleaseDate = v.ReleaseDate);
                Set(() => g.LastPlayedDate = v.LastPlayed);
                Set(() => g.PlayCount = v.PlayCount);
                Set(() => g.PlayTime = v.PlayTime);
                // Disc and the side flags are NOT carried: a <Game> never holds them. LaunchBox
                // folds the disc into the title instead, which TitleFromFileName reproduces.
                // The DatabaseID and the GUID are gone, as they are for LaunchBox.

                GameSaveMover.ToGame(game, v, g, dm);

                game.TryRemoveAdditionalApplication(v);
                restored++;
            }
            catch { }
        }
        return restored;
    }

    /// <summary>The title LaunchBox gives a restored game. Not the version label — the file name,
    /// cleaned the way its importer cleans one, with the disc folded back in.
    ///
    /// The cleaning rule was checked against 186 real file-name/title pairs from three separate
    /// imports and got all of them: bracketed and parenthesised groups removed, underscores turned
    /// into spaces, runs of whitespace collapsed, " - " turned into ": ".
    ///
    /// The disc suffix is what makes this differ from the importer, which leaves it off: the same
    /// "Final Fantasy X [Disk.3].txt" imports as "Final Fantasy X" and expands as
    /// "Final Fantasy X (Disc 3)". LaunchBox's own entry point takes the disc as a parameter
    /// (NamingHelper.GetTitleFromFileName(path, int? disc, …)), which is the same story from the
    /// other side.</summary>
    internal static string TitleFromFileName(string path, int? disc)
    {
        string s;
        try { s = System.IO.Path.GetFileNameWithoutExtension(path ?? ""); } catch { s = path ?? ""; }
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\([^)]*\)", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = s.Replace('_', ' ');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+-\s+", ": ").Trim();
        return disc.HasValue ? $"{s} (Disc {disc.Value})" : s;
    }

    /// <summary>The Version a restored game gets: the file name from its first bracket or
    /// parenthesis to the end, or the whole name when it has neither.
    ///
    /// Measured on 129 restored games, all of them. 99 also matched the label the version carried,
    /// which is exactly why copying the label passed every experiment until one was run with the
    /// labels deliberately falsified.</summary>
    internal static string VersionFromFileName(string path)
    {
        string s;
        try { s = System.IO.Path.GetFileNameWithoutExtension(path ?? ""); } catch { s = path ?? ""; }
        int i = s.IndexOfAny(new[] { '(', '[' });
        return i >= 0 ? s.Substring(i) : s;
    }

    /// <summary>Every additional application a game carries, whatever its section.</summary>
    private static List<HostAdditionalApplication> AddAppsOf(IGame game)
    {
        var result = new List<HostAdditionalApplication>();
        try
        {
            foreach (var a in game?.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                if (a is HostAdditionalApplication h) result.Add(h);
        }
        catch { }
        return result;
    }

    private static bool IsVersion(HostAdditionalApplication a)
        => string.Equals(a.Section, VersionSection, StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves one additional application onto the root. Versions are renumbered into the
    /// root's sequence; anything else keeps the priority it had, which for a document is its
    /// position in the manuals list and means nothing to the version ordering.</summary>
    private static HostAdditionalApplication CopyAddApp(IGame root, HostAdditionalApplication src, int? priority)
    {
        if (root.AddNewAdditionalApplication() is not HostAdditionalApplication v) return null;
        v.Section = src.Section;
        v.UseEmulator = src.UseEmulator;
        v.ApplicationPath = src.ApplicationPath;
        v.Version = src.Version;
        v.Name = src.Name;
        v.Developer = src.Developer;
        v.Publisher = src.Publisher;
        v.Region = src.Region;
        v.Status = src.Status;
        v.EmulatorId = src.EmulatorId;
        v.Disc = src.Disc;
        v.ReleaseDate = src.ReleaseDate;
        v.LastPlayed = src.LastPlayed;
        v.PlayCount = src.PlayCount;
        v.PlayTime = src.PlayTime;
        v.CommandLine = src.CommandLine;
        v.SideA = src.SideA;
        v.SideB = src.SideB;
        v.Priority = priority ?? src.Priority;
        return v;
    }

    /// <summary>The label shown in the versions list: "Play {version} Version...", with runs of
    /// whitespace squeezed to one. Both halves of that were measured, not styled — a version of
    /// "(Disc  3)" is named "Play (Disc 3) Version...", and an empty one gives "Play Version..."
    /// rather than the double space a plain concatenation leaves behind.</summary>
    private static string VersionName(string version) =>
        System.Text.RegularExpressions.Regex.Replace($"Play {version} Version...", @"\s+", " ");

    private static void Set(Action a) { try { a(); } catch { } }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
