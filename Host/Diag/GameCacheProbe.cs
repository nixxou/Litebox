// Diagnostic: reflect into ExtendDB's GameCache (if loaded) and print per-
// platform image/video counts — same numbers as ExtendDB's GameCache Debug
// window, but headless. Used to verify the image scan after the folder fix.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace LbApiHost.Host.Diag;

internal static class GameCacheProbe
{
    public static void Dump()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            if (asm == null) { Console.WriteLine("[gcdump] ExtendDB not loaded"); return; }

            var gc = asm.GetType("ExtendDB.GameCache") ?? asm.GetType("ExtendDB.Cache.GameCache");
            const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var plats = (gc?.GetField("Platforms", SF)?.GetValue(null)
                         ?? gc?.GetProperty("Platforms", SF)?.GetValue(null)) as IDictionary;
            if (plats == null) { Console.WriteLine("[gcdump] GameCache.Platforms not accessible"); return; }

            Console.WriteLine($"[gcdump] platforms={plats.Count}");
            foreach (DictionaryEntry e in plats)
            {
                var plat = e.Value;
                var gamesObj = plat.GetType()
                    .GetProperty("GamesByUUID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(plat) as IDictionary;

                int games = 0, img = 0, vid = 0;
                if (gamesObj != null)
                {
                    games = gamesObj.Count;
                    foreach (var g in gamesObj.Values)
                    {
                        var im = g.GetType().GetProperty("Images")?.GetValue(g) as Array;
                        var vi = g.GetType().GetProperty("Videos")?.GetValue(g) as Array;
                        img += im?.Length ?? 0;
                        vid += vi?.Length ?? 0;
                    }
                }
                Console.WriteLine($"[gcdump]   {e.Key}: games={games} img={img} vid={vid}");
            }
        }
        catch (Exception ex) { Console.WriteLine("[gcdump] error: " + ex.Message); }
    }
}
