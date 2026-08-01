// Keeping save games and save states attached to the ROM they were made on.
//
// A <GameSave> names a game and, optionally, one of its versions. It never names a rom. So the
// meaning of a game-level save is "the save of whatever this game currently launches" — which stops
// being true the moment anything changes what it launches. Three operations do:
//
//   COMBINE       an absorbed game stops existing; its saves would be orphaned or deleted
//   EXPAND        a version becomes a game again; its saves would be deleted
//   MAKE DEFAULT  the game swaps its rom with one of its versions; its saves stay put and
//                 silently become the saves of a different rom
//
// LaunchBox handles none of the three. Measured: 10 records of 12 destroyed by a combine, 11 of 13
// by an expand, files left on disk with nothing pointing at them. Make Default leaves them
// mislabelled, which is quieter and worse — nothing looks lost.
//
// This is the one place LiteBox deliberately differs, and it is the only piece of that work that
// survived the decision to otherwise match LaunchBox exactly. The reason it survived: it consults
// nothing. Every move below re-points records that are already in the file, using LaunchBox's own
// AdditionalApplicationId field, so it works on a game LaunchBox combined and LiteBox expands, or
// the other way round. Nothing here depends on LiteBox having performed the previous step.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Games;

internal static class GameSaveMover
{
    private const string Entity = "GameSave";

    /// <summary>Combine: an absorbed game's saves become saves of the version it turned into.</summary>
    public static void ToVersion(IGame source, IGame root, HostAdditionalApplication version, HostDataManagerXml dm)
    {
        if (version == null || dm == null) return;
        if (!Guid.TryParse(Safe(() => source?.Id) ?? "", out var sgid)) return;
        if (!Guid.TryParse(Safe(() => root?.Id) ?? "", out var rgid)) return;

        var store = dm.Store;
        var moving = store.GetSubEntities(sgid, Entity);
        if (moving.Count == 0) return;

        var kept = Copy(store.GetSubEntities(rgid, Entity));
        foreach (var row in moving) kept.Add(Retag(row, rgid.ToString(), version.Id));

        store.SetSubEntities(rgid, Entity, kept);
        store.SetSubEntities(sgid, Entity, Array.Empty<IReadOnlyDictionary<string, string>>());
    }

    /// <summary>Expand: the saves of a version go to the game it becomes, as plain game saves.</summary>
    public static void ToGame(IGame root, HostAdditionalApplication version, IGame restored, HostDataManagerXml dm)
    {
        if (version == null || dm == null) return;
        if (!Guid.TryParse(Safe(() => root?.Id) ?? "", out var rgid)) return;
        if (!Guid.TryParse(Safe(() => restored?.Id) ?? "", out var ngid)) return;
        string vid = version.Id ?? "";
        if (vid.Length == 0) return;

        var store = dm.Store;
        var all = store.GetSubEntities(rgid, Entity);
        if (all.Count == 0) return;

        var stay = new List<Dictionary<string, string>>();
        var move = new List<Dictionary<string, string>>();
        foreach (var row in all)
        {
            if (!Owns(row, vid)) { stay.Add(new Dictionary<string, string>(row, StringComparer.Ordinal)); continue; }
            move.Add(Retag(row, ngid.ToString(), null));
        }
        if (move.Count == 0) return;

        store.SetSubEntities(rgid, Entity, stay);
        store.SetSubEntities(ngid, Entity, move);
    }

    /// <summary>Expand: the version that WAS the root disappears without becoming a game. Its saves
    /// stay with the root — they just stop naming a version that no longer exists, which would
    /// otherwise leave them pointing at nothing. Found by counting: five did exactly that.</summary>
    public static void Detach(IGame root, HostAdditionalApplication version, HostDataManagerXml dm)
    {
        if (version == null || dm == null) return;
        if (!Guid.TryParse(Safe(() => root?.Id) ?? "", out var rgid)) return;
        string vid = version.Id ?? "";
        if (vid.Length == 0) return;

        var store = dm.Store;
        var rows = store.GetSubEntities(rgid, Entity);
        if (rows.Count == 0) return;

        bool touched = false;
        var kept = new List<Dictionary<string, string>>(rows.Count);
        foreach (var row in rows)
        {
            if (!Owns(row, vid)) { kept.Add(new Dictionary<string, string>(row, StringComparer.Ordinal)); continue; }
            touched = true;
            kept.Add(Retag(row, rgid.ToString(), null));
        }
        if (touched) store.SetSubEntities(rgid, Entity, kept);
    }

    /// <summary>Make Default: the game hands its rom over to a version and takes that version's in
    /// exchange. Its game-level saves were made on the rom it is giving up, so they follow it onto
    /// the version that now holds it.
    ///
    /// Saves already tied to the promoted version are left alone — they were made on the rom the
    /// game is taking, and that version row still exists to hold them.
    ///
    /// LAUNCHBOX'S BEHAVIOUR HERE IS NOT MEASURED. It has a Make Default of its own and what it does
    /// with saves is unknown; if it moves them differently, that is worth aligning on. Doing
    /// nothing, which is what we did until now, is the one option that is certainly wrong: it
    /// relabels a save as belonging to a rom it was never made on, and leaves no trace.</summary>
    public static void FollowDemotedRom(IGame game, HostAdditionalApplication demoted, HostDataManagerXml dm)
    {
        if (demoted == null || dm == null) return;
        if (!Guid.TryParse(Safe(() => game?.Id) ?? "", out var gid)) return;
        string vid = demoted.Id ?? "";
        if (vid.Length == 0) return;

        var store = dm.Store;
        var rows = store.GetSubEntities(gid, Entity);
        if (rows.Count == 0) return;

        bool touched = false;
        var kept = new List<Dictionary<string, string>>(rows.Count);
        foreach (var row in rows)
        {
            // Only the game-level ones. A save already naming a version belongs to that version.
            row.TryGetValue("AdditionalApplicationId", out var owner);
            if (!string.IsNullOrEmpty(owner)) { kept.Add(new Dictionary<string, string>(row, StringComparer.Ordinal)); continue; }
            touched = true;
            kept.Add(Retag(row, gid.ToString(), vid));
        }
        if (touched) store.SetSubEntities(gid, Entity, kept);
    }

    private static bool Owns(IReadOnlyDictionary<string, string> row, string versionId)
    {
        row.TryGetValue("AdditionalApplicationId", out var owner);
        return string.Equals(owner ?? "", versionId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A row with a new owner. Rebuilt rather than edited so GameId and
    /// AdditionalApplicationId lead the element, which is where LaunchBox puts them.</summary>
    private static Dictionary<string, string> Retag(IReadOnlyDictionary<string, string> row, string gameId, string? versionId)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal) { ["GameId"] = gameId };
        if (!string.IsNullOrEmpty(versionId)) next["AdditionalApplicationId"] = versionId;
        foreach (var kv in row)
            if (kv.Key != "GameId" && kv.Key != "AdditionalApplicationId") next[kv.Key] = kv.Value;
        return next;
    }

    private static List<Dictionary<string, string>> Copy(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        => rows.Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
