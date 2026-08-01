// Combine / Expand — LaunchBox's "additional versions", reproduced.
//
// HOW LAUNCHBOX STORES IT (read off a real merge, not guessed): combining deletes the non-root
// <Game> outright and re-creates it as an <AdditionalApplication> on the root, carrying
// Section=Version plus the absorbed game's own metadata — ApplicationPath, Version, Developer,
// Publisher, Region, ReleaseDate, Status, EmulatorId, Disc — and its play statistics, which are
// kept on the version rather than folded into the root. Priority continues after the versions
// already there.
//
// WHAT EXPANDING COSTS. LaunchBox loses three things: the absorbed game's DatabaseID, its GUID,
// and its title, which comes back re-derived from the file name and can differ from the original.
// It also deletes every save belonging to a version — 11 of 13 records gone in a measured expand,
// with all the files left on disk.
//
// We give two of the three back. A combine performed HERE stashes the DatabaseID and the real title
// against the version's own GUID in LiteBox's options database (scope "version"), and the expand
// hands them over and clears the entry. The saves come back either way, re-pointed at the restored
// game. The game GUID is the one thing still lost: restoring it would mean creating a game with a
// chosen id, which the store does not offer, and it buys nothing on its own.
//
// A combine done in LAUNCHBOX records nothing, so expanding its work falls back to LaunchBox's own
// behaviour — a title re-derived from the file name, with the disc folded back in.
//
// MEDIA. Combining only merges media when the two entries share a DatabaseID — same database entry
// means the same game, so pooling their images is what the user asked for. Anything else keeps its
// files where they are, because the merge is NOT reversible for media: after an expand the game
// comes back with a new GUID and possibly a different title, so files moved into the root's
// collection could never be handed back.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
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

    /// <summary>Folds every game but <paramref name="root"/> into it as a version. Returns how many
    /// were absorbed.</summary>
    public static int Combine(IReadOnlyList<IGame> games, IGame root, HostDataManagerXml dm)
    {
        if (games == null || root == null || dm == null) return 0;
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
        if (others.Count == 0) return 0;

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
                MergeMediaIfSameEntry(g, root);

                // EVERYTHING the absorbed game carried comes along, not just its versions: its
                // manuals (Section=Document) and any plain additional applications too. They used
                // to be left behind, which meant destroyed once deletion started taking the whole
                // subtree — two manuals silently gone on the first game that had any.
                //
                // LaunchBox's behaviour here is NOT known; this is the choice that does not lose
                // anything while we find out. If it turns out to drop them, that is a difference
                // worth keeping.
                foreach (var inner in AddAppsOf(g))
                    CopyAddApp(root, inner, IsVersion(inner) ? ++priority : (int?)null);

                var version = AddVersion(root, g, ++priority);
                RememberForExpand(g, version);
                MoveSaves(g, root, version, dm);
                dm.TryRemoveGame(g);
                absorbed++;
            }
            catch { }
        }
        return absorbed;
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

    /// <summary>Carries an absorbed game's save games and save states over to the root, tied to the
    /// version it became.
    ///
    /// This is a DELIBERATE DIVERGENCE from LaunchBox, the only one in the combine. Measured: it
    /// deletes them outright — 10 of 12 <GameSave> records vanished from a real combine — while
    /// leaving every file on disk, so the saves survive with nothing left pointing at them. Nobody
    /// is warned and nothing can be recovered without knowing the file names.
    ///
    /// Re-pointing costs nothing and invents nothing: AdditionalApplicationId is LaunchBox's own
    /// field for a save belonging to a version, and 7 of the 15 saves already in this library use
    /// it. The result is a file LaunchBox reads as it stands — it simply would not have written
    /// it.</summary>
    private static void MoveSaves(IGame source, IGame root, HostAdditionalApplication version, HostDataManagerXml dm)
    {
        if (version == null) return;
        if (!Guid.TryParse(Safe(() => source.Id) ?? "", out var sgid)) return;
        if (!Guid.TryParse(Safe(() => root.Id) ?? "", out var rgid)) return;

        var store = dm.Store;
        var moving = store.GetSubEntities(sgid, SaveEntity);
        if (moving.Count == 0) return;

        var kept = store.GetSubEntities(rgid, SaveEntity)
            .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();

        foreach (var row in moving)
        {
            // Rebuilt rather than edited in place so GameId and AdditionalApplicationId lead the
            // element, which is where LaunchBox puts them on the saves that already have one.
            var moved = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GameId"] = rgid.ToString(),
                ["AdditionalApplicationId"] = version.Id,
            };
            foreach (var kv in row)
                if (kv.Key != "GameId" && kv.Key != "AdditionalApplicationId")
                    moved[kv.Key] = kv.Value;
            kept.Add(moved);
        }

        store.SetSubEntities(rgid, SaveEntity, kept);
        store.SetSubEntities(sgid, SaveEntity, Array.Empty<IReadOnlyDictionary<string, string>>());
    }

    private const string SaveEntity = "GameSave";

    /// <summary>Keeps beside the version the two things a combine destroys and an expand can never
    /// work out again: the absorbed game's DatabaseID, and the title it actually had. The version's
    /// own GUID is the key — LaunchBox keeps it as the row key, so it survives its saves too.
    ///
    /// The game's GUID is not kept: restoring it would mean creating a game with a chosen id, which
    /// the store does not offer, and it buys nothing on its own.</summary>
    private static void RememberForExpand(IGame source, HostAdditionalApplication version)
    {
        string vid = version?.Id ?? "";
        if (vid.Length == 0) return;
        int? db = Safe(() => source.LaunchBoxDbId);
        string title = Safe(() => source.Title) ?? "";
        LiteBoxOptionsDb.Set(LiteBoxOption.ScopeVersion, vid, "Combine.DatabaseID",
            db.HasValue ? db.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        LiteBoxOptionsDb.Set(LiteBoxOption.ScopeVersion, vid, "Combine.Title",
            title.Length > 0 ? title : null);
        // The identity itself. Everything that refers to a game refers to this: the 917 playlist
        // entries in a real library, media named "<title>.<guid>-NN.ext" (1149 files over 73 games
        // there), launch history, per-game options. A combine orphans all of it the moment the game
        // stops existing — LaunchBox included — and without this the expand can never reattach any
        // of it, because the game comes back as someone else.
        string gid = Safe(() => source.Id) ?? "";
        LiteBoxOptionsDb.Set(LiteBoxOption.ScopeVersion, vid, "Combine.GameId",
            gid.Length > 0 ? gid : null);
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
                    DetachSaves(game, v, dm);
                    game.TryRemoveAdditionalApplication(v);
                    continue;
                }

                // What the combine stashed against this version, if it was OUR combine. LaunchBox
                // records nothing, so an expand of its work falls back to re-deriving the title.
                string vid = v.Id ?? "";
                string kept = vid.Length > 0
                    ? LiteBoxOptionsDb.Get(LiteBoxOption.ScopeVersion, vid, "Combine.Title") : null;
                string dbid = vid.Length > 0
                    ? LiteBoxOptionsDb.Get(LiteBoxOption.ScopeVersion, vid, "Combine.DatabaseID") : null;
                string oldId = vid.Length > 0
                    ? LiteBoxOptionsDb.Get(LiteBoxOption.ScopeVersion, vid, "Combine.GameId") : null;

                string title = !string.IsNullOrEmpty(kept) ? kept : TitleFromFileName(v.ApplicationPath, v.Disc);

                var g = dm.AddNewGame(title,
                    Guid.TryParse(oldId ?? "", out var back) ? back : (Guid?)null);
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
                if (!string.IsNullOrEmpty(dbid) && int.TryParse(dbid, out int id))
                    Set(() => g.LaunchBoxDbId = id);

                ReturnSaves(game, v, g, dm);
                if (vid.Length > 0)
                {
                    LiteBoxOptionsDb.Set(LiteBoxOption.ScopeVersion, vid, "Combine.Title", null);
                    LiteBoxOptionsDb.Set(LiteBoxOption.ScopeVersion, vid, "Combine.DatabaseID", null);
                    LiteBoxOptionsDb.Set(LiteBoxOption.ScopeVersion, vid, "Combine.GameId", null);
                }
                game.TryRemoveAdditionalApplication(v);
                restored++;
            }
            catch { }
        }
        return restored;
    }

    /// <summary>Drops the version link from the root's own saves when that version goes away,
    /// leaving them as plain game saves where they already were.</summary>
    private static void DetachSaves(IGame root, HostAdditionalApplication version, HostDataManagerXml dm)
    {
        if (!Guid.TryParse(Safe(() => root.Id) ?? "", out var rgid)) return;
        string vid = version.Id ?? "";
        if (vid.Length == 0) return;

        var store = dm.Store;
        var rows = store.GetSubEntities(rgid, SaveEntity);
        if (rows.Count == 0) return;

        bool touched = false;
        var kept = new List<Dictionary<string, string>>(rows.Count);
        foreach (var row in rows)
        {
            row.TryGetValue("AdditionalApplicationId", out var owner);
            if (!string.Equals(owner ?? "", vid, StringComparison.OrdinalIgnoreCase))
            { kept.Add(new Dictionary<string, string>(row, StringComparer.Ordinal)); continue; }
            touched = true;
            var flat = new Dictionary<string, string>(StringComparer.Ordinal) { ["GameId"] = rgid.ToString() };
            foreach (var kv in row)
                if (kv.Key != "GameId" && kv.Key != "AdditionalApplicationId") flat[kv.Key] = kv.Value;
            kept.Add(flat);
        }
        if (touched) store.SetSubEntities(rgid, SaveEntity, kept);
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

    /// <summary>Hands a version's saves back to the game it becomes. LaunchBox deletes them —
    /// measured: 11 of 13 records gone after one expand, every file left on disk — which is the
    /// same loss as the combine, in the other direction. Keeping them is what makes the round trip
    /// actually round.</summary>
    private static void ReturnSaves(IGame root, HostAdditionalApplication version, IGame restored, HostDataManagerXml dm)
    {
        if (!Guid.TryParse(Safe(() => root.Id) ?? "", out var rgid)) return;
        if (!Guid.TryParse(Safe(() => restored.Id) ?? "", out var ngid)) return;
        string vid = version.Id ?? "";
        if (vid.Length == 0) return;

        var store = dm.Store;
        var all = store.GetSubEntities(rgid, SaveEntity);
        if (all.Count == 0) return;

        var stay = new List<Dictionary<string, string>>();
        var move = new List<Dictionary<string, string>>();
        foreach (var row in all)
        {
            row.TryGetValue("AdditionalApplicationId", out var owner);
            if (!string.Equals(owner ?? "", vid, StringComparison.OrdinalIgnoreCase))
            { stay.Add(new Dictionary<string, string>(row, StringComparer.Ordinal)); continue; }

            // Back to a plain game save: the version it belonged to is about to stop existing.
            var back = new Dictionary<string, string>(StringComparer.Ordinal) { ["GameId"] = ngid.ToString() };
            foreach (var kv in row)
                if (kv.Key != "GameId" && kv.Key != "AdditionalApplicationId") back[kv.Key] = kv.Value;
            move.Add(back);
        }
        if (move.Count == 0) return;

        store.SetSubEntities(rgid, SaveEntity, stay);
        store.SetSubEntities(ngid, SaveEntity, move);
    }

    /// <summary>Media follow only when the two entries are the SAME database game. Otherwise they
    /// stay put: an expand cannot give them back, so moving them would be one-way.</summary>
    private static void MergeMediaIfSameEntry(IGame source, IGame root)
    {
        int? a = Safe(() => source.LaunchBoxDbId), b = Safe(() => root.LaunchBoxDbId);
        if (!a.HasValue || !b.HasValue || a.Value != b.Value) return;
        GameMediaSync.MergeInto(source, root);
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
    private static void CopyAddApp(IGame root, HostAdditionalApplication src, int? priority)
    {
        if (root.AddNewAdditionalApplication() is not HostAdditionalApplication v) return;
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
