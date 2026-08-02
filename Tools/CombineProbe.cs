// Replays a Combine through the real code on a throwaway copy of a platform, so its output can be
// diffed against one LaunchBox produced from the same starting point.
//
//   --combine-probe <lbRoot> <rootGameId> <otherId,otherId,...>
//
// It exists because parity with LaunchBox cannot be reasoned out: every rule here — the root
// becoming a version of itself, the disc number being derived, empty strings written rather than
// omitted — was wrong until a real before/after pair said so. Being able to re-run the experiment
// in a second, without asking anyone to click through LaunchBox again, is what makes fixing them
// cheap enough to do properly.

#nullable enable

using System;
using System.IO;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Games;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class CombineProbe
{
    /// <summary>Rejoue un RENOMMAGE par la vraie porte d entree — le meme ordre exact que
    /// EditGameWindow : le titre d abord, puis GameMediaSync.OnTitleChanged. C est la seule facon
    /// de verifier la spec de bout en bout ; les tests unitaires ne traversent ni le store ni le
    /// DataManager, et on sait ce que valent les verifications qui ne traversent rien.</summary>
    public static int RunRename(string lbRoot, string gameId, string newTitle, string choiceName = "Auto")
    {
        LbApiHost.Host.Media.MediaResolver.Init(lbRoot);
        LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning = false;
        string dataDir = Path.Combine(lbRoot, "Data");
        var store = GameStore.Load(Path.Combine(dataDir, "Platforms"), Path.Combine(dataDir, "probe.pending.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, Path.Combine(lbRoot, "Images")) { ReadOnly = false };
        PluginHelper.DataManager = dm;
        LbApiHost.Host.Media.GameMediaSync.Attach(store);

        var game = dm.GetAllGames().FirstOrDefault(g =>
            string.Equals(Safe(() => g.Id) ?? "", gameId, StringComparison.OrdinalIgnoreCase));
        if (game == null) { Console.WriteLine($"[probe] game {gameId} not found"); return 1; }

        string oldTitle = Safe(() => game.Title) ?? "";
        game.Title = newTitle;
        // Le banc rejoue chaque branche du dialogue sans interface : le choix arrive en argument.
        Enum.TryParse<LbApiHost.Host.Media.CollisionChoice>(choiceName, true, out var choice);
        LbApiHost.Host.Media.GameMediaSync.OnTitleChanged(game, oldTitle, newTitle, choice);
        dm.Save(true);
        store.CloseLog();
        Console.WriteLine($"[probe] renamed \"{oldTitle}\" -> \"{newTitle}\"");
        return 0;
    }

    public static int Run(string lbRoot, string rootId, string otherIds)
    {
        // Le boot initialise le resolveur de medias ; une sonde ne le traverse pas. Sans cette
        // ligne toute la branche media sort immediatement et les verifications passent A VIDE —
        // exactement ce qui s'etait deja produit avec la base d'options.
        LbApiHost.Host.Media.MediaResolver.Init(lbRoot);
        // The probe is handed an explicit root and told to mutate it; that root is a scratch copy,
        // never the library LaunchBox has open. Without this the flush gate silently keeps every
        // change in the op-log, the XML on disk never moves, and the simulation that reads it back
        // checks its invariants against an UNCHANGED file — passing while proving nothing.
        LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning = false;
        string dataDir = Path.Combine(lbRoot, "Data");
        var store = GameStore.Load(Path.Combine(dataDir, "Platforms"), Path.Combine(dataDir, "probe.pending.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, Path.Combine(lbRoot, "Images")) { ReadOnly = false };
        PluginHelper.DataManager = dm;

        var all = dm.GetAllGames();
        IGame? Find(string id) => all.FirstOrDefault(g =>
            string.Equals(Safe(() => g.Id) ?? "", id, StringComparison.OrdinalIgnoreCase));

        var root = Find(rootId);
        if (root == null) { Console.WriteLine($"[probe] root {rootId} not found"); return 1; }

        // Order matters: it decides the priorities, so the caller controls it.
        var picked = otherIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Find(s.Trim())).Where(g => g != null).Select(g => g!).ToList();
        if (picked.Count == 0) { Console.WriteLine("[probe] no other game found"); return 1; }

        var selection = new[] { root }.Concat(picked).ToList();
        int n = GameCombiner.Combine(selection, root, dm);
        dm.Save(true);
        store.CloseLog();

        Console.WriteLine($"[probe] absorbed {n}, {dm.GetAllGames().Length} games left");
        return 0;
    }

    /// <summary>Same idea in the other direction: expand a game's versions on a throwaway copy.</summary>
    public static int RunExpand(string lbRoot, string gameId)
    {
        // Le boot initialise le resolveur de medias ; une sonde ne le traverse pas. Sans cette
        // ligne toute la branche media sort immediatement et les verifications passent A VIDE —
        // exactement ce qui s'etait deja produit avec la base d'options.
        LbApiHost.Host.Media.MediaResolver.Init(lbRoot);
        // The probe is handed an explicit root and told to mutate it; that root is a scratch copy,
        // never the library LaunchBox has open. Without this the flush gate silently keeps every
        // change in the op-log, the XML on disk never moves, and the simulation that reads it back
        // checks its invariants against an UNCHANGED file — passing while proving nothing.
        LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning = false;
        string dataDir = Path.Combine(lbRoot, "Data");
        var store = GameStore.Load(Path.Combine(dataDir, "Platforms"), Path.Combine(dataDir, "probe.pending.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, Path.Combine(lbRoot, "Images")) { ReadOnly = false };
        PluginHelper.DataManager = dm;

        var game = dm.GetAllGames().FirstOrDefault(g =>
            string.Equals(Safe(() => g.Id) ?? "", gameId, StringComparison.OrdinalIgnoreCase));
        if (game == null) { Console.WriteLine($"[probe] game {gameId} not found"); return 1; }

        int n = GameCombiner.Expand(game, dm);
        dm.Save(true);
        store.CloseLog();
        Console.WriteLine($"[probe] restored {n}, {dm.GetAllGames().Length} games");
        return 0;
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
