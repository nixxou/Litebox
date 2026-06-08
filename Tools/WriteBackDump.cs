// --dump-extra <title-substring> : read-only diagnostic. Loads the real library and, for each game
// whose title matches, prints the non-IGame fields LiteBox exposes via ILiteBoxFields (proves the
// full-field access works against real data). Never writes (store stays ReadOnly).

using System;
using System.IO;
using System.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Tools;

internal static class WriteBackDump
{
    public static int Run(string[] args)
    {
        int idx = Array.IndexOf(args, "--dump-extra");
        string needle = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : "";
        string coreDir = AppContext.BaseDirectory;
        string platformsDir = GetArg(args, "--library")
            ?? Path.GetFullPath(Path.Combine(coreDir, "..", "Data", "Platforms"));
        if (!Directory.Exists(platformsDir)) { Console.WriteLine("[dump] platforms dir not found: " + platformsDir); return 1; }

        var store = GameStore.Load(platformsDir);   // ReadOnly stays true → no writes
        store.LogStats();                            // memory footprint, incl. extra/sub-entity stores
        int shown = 0;
        for (int i = 0; i < store.Count && shown < 10; i++)
        {
            var g = new HostGame(store, i);
            if (!string.IsNullOrEmpty(needle) && (g.Title ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            shown++;
            var lf = (ILiteBoxFields)g;
            var extra = lf.ExtraFieldNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine($"[dump] {g.Title}  ({g.Id})  Platform={g.Platform}");
            Console.WriteLine($"[dump]   IGame.Developer='{g.Developer}'  Favorite={g.Favorite}  StarRating={g.StarRating}");
            Console.WriteLine($"[dump]   non-IGame fields stored ({extra.Count}):");
            foreach (var f in extra) Console.WriteLine($"[dump]      {f} = {lf.GetField(f)}");
            var lg = (ILiteBoxGame)g;
            foreach (var t in lg.SubEntityTypes)
            {
                Console.WriteLine($"[dump]   sub-entity <{t}>:");
                foreach (var row in lg.GetSubEntities(t))
                    Console.WriteLine("[dump]      { " + string.Join(", ", row.Select(kv => kv.Key + "=" + kv.Value)) + " }");
            }
        }
        store.CloseLog();
        if (shown == 0) Console.WriteLine("[dump] no game matched '" + needle + "'");
        return 0;
    }

    private static string GetArg(string[] args, string name)
    { int i = Array.IndexOf(args, name); return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null; }
}
