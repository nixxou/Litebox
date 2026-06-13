// --store-sync : read-only diagnostic. Loads the real library, lists every GOG/Steam game with its
// Installed / ApplicationPath / GogAppId, runs StoreInstallStateSync (which reads Galaxy's DB and
// Steam's appmanifests), then lists them again so the before/after reconcile is visible. The store
// stays ReadOnly → SetGameField still applies in memory (for the diff) but nothing hits disk.

using System;
using System.IO;
using LbApiHost.Host;
using LbApiHost.Host.Data;

namespace LbApiHost.Tools;

internal static class StoreSyncDump
{
    public static int Run(string[] args)
    {
        string coreDir = AppContext.BaseDirectory;
        string platformsDir = GetArg(args, "--library")
            ?? Path.GetFullPath(Path.Combine(coreDir, "..", "Data", "Platforms"));
        if (!Directory.Exists(platformsDir)) { Console.WriteLine("[store-sync] platforms dir not found: " + platformsDir); return 1; }

        var store = GameStore.Load(platformsDir);   // ReadOnly stays true → no disk writes
        Console.WriteLine("[store-sync] BEFORE:");
        DumpStoreGames(store);

        StoreInstallStateSync.Sync(store);

        Console.WriteLine("[store-sync] AFTER:");
        DumpStoreGames(store);
        store.CloseLog();
        return 0;
    }

    private static void DumpStoreGames(GameStore store)
    {
        int n = 0;
        for (int i = 0; i < store.Count; i++)
        {
            var kind = StoreSupport.KindOf(store.Str(store.Rows[i].SourceIdx));
            if (kind == StoreKind.None) continue;
            n++;
            var g = new HostGame(store, i);
            string inst = store.Rows[i].Installed switch { 1 => "false", 2 => "true", _ => "(null)" };
            Console.WriteLine($"[store-sync]   {kind,-5} Installed={inst,-7} GogAppId='{store.Str(store.Rows[i].GogAppIdIdx)}' " +
                              $"AppPath='{store.Str(store.Rows[i].AppPathIdx)}'  {g.Title}");
        }
        if (n == 0) Console.WriteLine("[store-sync]   (no GOG/Steam games)");
    }

    private static string GetArg(string[] args, string name)
    { int i = Array.IndexOf(args, name); return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null; }
}
