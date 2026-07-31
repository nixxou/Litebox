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
    public static int Run(string lbRoot, string rootId, string otherIds)
    {
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

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
