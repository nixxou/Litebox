// --seed-writeback : drives the REAL plugin API (IGame setters + IDataManager.Save) against the
// real LB library to seed write-back changes across several Platform XMLs, then flush. Used for the
// LaunchBox-ingestion test (does LB swallow + reformat what we wrote?). Refuses to run while
// LaunchBox/BigBox are up (they own the XMLs). Back up Data before using.

using System;
using System.IO;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;

namespace LbApiHost.Tools;

internal static class WriteBackSeed
{
    public static int Run(string[] args)
    {
        if (GameStore.IsLaunchBoxRunning())
        {
            Console.WriteLine("[seed] ABORT: LaunchBox/BigBox is running — close it first (they own the XMLs).");
            return 1;
        }

        string coreDir = AppContext.BaseDirectory;
        string platformsDir = GetArg(args, "--library")
            ?? Path.GetFullPath(Path.Combine(coreDir, "..", "Data", "Platforms"));
        if (!Directory.Exists(platformsDir)) { Console.WriteLine("[seed] platforms dir not found: " + platformsDir); return 1; }

        string dataDir = Path.GetFullPath(Path.Combine(platformsDir, ".."));
        string lbRoot = Path.GetFullPath(Path.Combine(dataDir, ".."));
        string imagesRoot = Path.Combine(lbRoot, "Images");

        var store = GameStore.Load(platformsDir);
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, imagesRoot) { ReadOnly = false };

        string marker = "LBWB-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Console.WriteLine("[seed] marker = " + marker);

        // First game of each platform (= each Platform XML), capped, so we touch several files.
        var perPlatform = dm.GetAllGames()
            .Where(g => !string.IsNullOrEmpty(g.Platform))
            .GroupBy(g => g.Platform)
            .Select(grp => grp.First())
            .Take(8)
            .ToList();

        Console.WriteLine($"[seed] seeding {perPlatform.Count} game(s):");
        foreach (var g in perPlatform)
        {
            // A visible user-state field + a metadata field + a numeric field. Notes/MaxPlayers are
            // usually absent → exercises NEW-node creation; Favorite/StarRating are easy to eyeball in LB.
            g.Favorite = true;
            g.StarRating = 4;
            g.MaxPlayers = 4;
            g.Notes = marker + " — write-back ingestion test";
            Console.WriteLine($"[seed]   {g.Platform,-28} {g.Id}  {Trunc(g.Title, 40)}");
        }

        dm.Save(true);          // → store.Flush() → FlushOpsToXml (LB not running → writes now)
        store.CloseLog();

        // Sanity: re-read from disk and confirm our marker round-tripped.
        var reload = GameStore.Load(platformsDir);
        int ok = 0;
        foreach (var g in perPlatform)
            if (Guid.TryParse(g.Id, out var gid) && reload.ById.TryGetValue(gid, out var i)
                && (reload.NotesFor(i) ?? "").Contains(marker)) ok++;
        reload.CloseLog();
        Console.WriteLine($"[seed] on-disk verify: {ok}/{perPlatform.Count} games carry the marker");
        Console.WriteLine("[seed] DONE. Now launch LaunchBox, confirm no crash, close it, and re-check the files.");
        return ok == perPlatform.Count ? 0 : 1;
    }

    private static string GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }
    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));
}
