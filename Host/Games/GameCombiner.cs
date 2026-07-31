// Combine / Expand — LaunchBox's "additional versions", reproduced.
//
// HOW LAUNCHBOX STORES IT (read off a real merge, not guessed): combining deletes the non-root
// <Game> outright and re-creates it as an <AdditionalApplication> on the root, carrying
// Section=Version plus the absorbed game's own metadata — ApplicationPath, Version, Developer,
// Publisher, Region, ReleaseDate, Status, EmulatorId, Disc — and its play statistics, which are
// kept on the version rather than folded into the root. Priority continues after the versions
// already there.
//
// WHAT EXPANDING LOSES — the same as LaunchBox, on purpose for now. The games come back, but:
//   • the DatabaseID is gone,
//   • the GUID is new, so nothing links the restored game to the one that was absorbed,
//   • the title is re-derived and can come back altered — an observed "A.IV - Evolution Global"
//     returned as "A.IV: Evolution Global".
// Carrying those across would mean stashing them ON the version, and an additional application has
// no free-field channel in the model (unlike a game or a playlist). Adding one is a change of its
// own; until then, matching LaunchBox exactly beats inventing a half-scheme.
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
        if (rootVersions.Count == 0) AddVersion(root, root, ++priority);

        foreach (var g in others)
        {
            try
            {
                MergeMediaIfSameEntry(g, root);

                // The absorbed game's OWN versions come along, or they would vanish with it.
                foreach (var inner in VersionsOf(g))
                    CopyVersion(root, inner, ++priority);

                AddVersion(root, g, ++priority);
                dm.TryRemoveGame(g);
                absorbed++;
            }
            catch { }
        }
        return absorbed;
    }

    /// <summary>Turns <paramref name="source"/> into a version of <paramref name="root"/>.</summary>
    private static void AddVersion(IGame root, IGame source, int priority)
    {
        if (root.AddNewAdditionalApplication() is not HostAdditionalApplication v) return;
        v.Section = VersionSection;
        v.UseEmulator = true;
        v.ApplicationPath = Safe(() => source.ApplicationPath) ?? "";
        v.Version = VersionLabelOf(source);
        v.Name = $"Play {v.Version} Version...";
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
    /// Returns how many were restored.</summary>
    public static int Expand(IGame game, HostDataManagerXml dm)
    {
        if (game == null || dm == null) return 0;
        var versions = VersionsOf(game);
        if (versions.Count == 0) return 0;

        string platform = Safe(() => game.Platform) ?? "";
        int restored = 0;
        foreach (var v in versions)
        {
            try
            {
                // The Version label is what the absorbed game was called (see VersionLabelOf), so
                // it is the closest thing to its original title still on record.
                string title = string.IsNullOrWhiteSpace(v.Version) ? Safe(() => game.Title) ?? "" : v.Version;

                var g = dm.AddNewGame(title);
                if (g == null) continue;
                Set(() => g.Platform = platform);
                Set(() => g.ApplicationPath = v.ApplicationPath);
                Set(() => g.Version = v.Version);
                Set(() => g.Developer = v.Developer);
                Set(() => g.Publisher = v.Publisher);
                Set(() => g.Region = v.Region);
                Set(() => g.Status = v.Status);
                Set(() => g.EmulatorId = v.EmulatorId);
                Set(() => g.ReleaseDate = v.ReleaseDate);
                Set(() => g.LastPlayedDate = v.LastPlayed);
                Set(() => g.PlayCount = v.PlayCount);
                Set(() => g.PlayTime = v.PlayTime);

                game.TryRemoveAdditionalApplication(v);
                restored++;
            }
            catch { }
        }
        return restored;
    }

    /// <summary>Media follow only when the two entries are the SAME database game. Otherwise they
    /// stay put: an expand cannot give them back, so moving them would be one-way.</summary>
    private static void MergeMediaIfSameEntry(IGame source, IGame root)
    {
        int? a = Safe(() => source.LaunchBoxDbId), b = Safe(() => root.LaunchBoxDbId);
        if (!a.HasValue || !b.HasValue || a.Value != b.Value) return;
        GameMediaSync.MergeInto(source, root);
    }

    private static void CopyVersion(IGame root, HostAdditionalApplication src, int priority)
    {
        if (root.AddNewAdditionalApplication() is not HostAdditionalApplication v) return;
        v.Section = VersionSection;
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
        v.Priority = priority;
    }

    /// <summary>What to call the version: the game's own Version field when it has one, else its
    /// title — which is what makes an absorbed game recognisable in the root's version list.</summary>
    private static string VersionLabelOf(IGame g)
    {
        string v = Safe(() => g.Version) ?? "";
        return v.Trim().Length > 0 ? v.Trim() : (Safe(() => g.Title) ?? "");
    }

    private static void Set(Action a) { try { a(); } catch { } }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
