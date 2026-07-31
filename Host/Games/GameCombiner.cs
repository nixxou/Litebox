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
        var others = games.Where(g => g != null && !string.Equals(Safe(() => g.Id) ?? "", rootId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0) return 0;

        int priority = VersionsOf(root).Select(v => v.Priority).DefaultIfEmpty(0).Max();
        int absorbed = 0;

        foreach (var g in others)
        {
            try
            {
                MergeMediaIfSameEntry(g, root);

                // The absorbed game's OWN versions come along, or they would vanish with it.
                foreach (var inner in VersionsOf(g))
                    CopyVersion(root, inner, ++priority);

                var v = root.AddNewAdditionalApplication() as HostAdditionalApplication;
                if (v != null)
                {
                    v.Section = VersionSection;
                    v.UseEmulator = true;
                    v.ApplicationPath = Safe(() => g.ApplicationPath) ?? "";
                    v.Version = VersionLabelOf(g);
                    v.Name = $"Play {v.Version} Version...";
                    v.Developer = Safe(() => g.Developer) ?? "";
                    v.Publisher = Safe(() => g.Publisher) ?? "";
                    v.Region = Safe(() => g.Region) ?? "";
                    v.Status = Safe(() => g.Status) ?? "";
                    v.EmulatorId = Safe(() => g.EmulatorId) ?? "";
                    v.ReleaseDate = Safe(() => g.ReleaseDate);
                    v.LastPlayed = Safe(() => g.LastPlayedDate);
                    v.PlayCount = Safe(() => g.PlayCount);
                    v.PlayTime = Safe(() => g.PlayTime);
                    v.Priority = ++priority;
                }

                dm.TryRemoveGame(g);
                absorbed++;
            }
            catch { }
        }
        return absorbed;
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
