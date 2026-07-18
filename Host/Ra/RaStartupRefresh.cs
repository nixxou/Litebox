// Startup rolling refresh for the LiteBox-native RA fallback (opt-in, LiteBoxConfig.RaStartupRollingRefresh).
//
// At launch, refresh the catalogue of up to 3 consoles whose cached list is older than 48h and re-link their
// games (pick up raids that appeared in RA since). Over successive launches this rolls through every console,
// so already-resolved games keep gaining raids without a manual scan — and WITHOUT any RAHasher work (re-link
// is a pure catalogue lookup). Gated: the checkbox is on, ExtendDB isn't resolving RA, and RA creds are set.
// Per-platform: a platform the user disabled in the RA options panel (RaPlatformState.IsPlatformEnabled) is
// skipped, so it stays honored here too. The auto-update trigger (select/launch) does NOT gate this rolling
// relink — it is a catalogue re-link, not the on-select re-hash the trigger governs.
//
// Call RunIfEnabled on the UI thread (it enumerates platforms/games there); the network refresh + re-link run
// on a background thread, and the op-log flush is marshalled back via the supplied action.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaStartupRefresh
{
    private const double StaleHours = 48.0;
    private const int MaxConsoles = 3;

    /// <summary>See file header. <paramref name="flushUi"/> persists the op-log (must marshal to the UI thread).</summary>
    public static void RunIfEnabled(IDataManager dm, bool enabled, Action flushUi)
    {
        try
        {
            if (!enabled || dm == null) return;
            if (!Modules.LbModules.On(Modules.LbModule.RetroAchievements)) return;   // RetroAchievements module off → LiteBox doesn't do per-ROM RA resolution
            if (!RaService.Configured) return;     // no creds → can't resolve raids

            // 1) Group RA-mapped platforms by console id (UI thread; no games gathered yet).
            var byConsole = new Dictionary<int, List<IPlatform>>();
            foreach (var p in dm.GetAllPlatforms() ?? Array.Empty<IPlatform>())
            {
                if (p == null) continue;
                string? pname = Safe(() => p.Name);
                int cid = RaPlatformMap.ConsoleIdFor(pname) ?? 0;
                if (cid <= 0) continue;
                if (!RaPlatformState.IsPlatformEnabled(pname)) continue;   // platform disabled in the RA panel → skip its startup relink

                if (!byConsole.TryGetValue(cid, out var list)) { list = new List<IPlatform>(); byConsole[cid] = list; }
                list.Add(p);
            }
            if (byConsole.Count == 0) return;

            // 2) Pick the ≤3 consoles with the OLDEST catalogue cache, only those past the 48h threshold.
            var pick = byConsole.Keys
                .Select(cid => (cid, age: RaCatalogLite.CacheAgeHours(cid)))
                .Where(x => x.age > StaleHours)
                .OrderByDescending(x => x.age)
                .Take(MaxConsoles)
                .ToList();
            if (pick.Count == 0) return;

            // 3) Gather just those consoles' games (UI thread).
            var games = new Dictionary<int, List<IGame>>();
            foreach (var (cid, _) in pick)
            {
                var list = new List<IGame>();
                foreach (var p in byConsole[cid])
                {
                    IGame[] gs = null; try { gs = p.GetAllGames(true, true); } catch { }
                    if (gs != null) foreach (var g in gs) if (g != null) list.Add(g);
                }
                games[cid] = list;
            }

            // 4) Network refresh + re-link on a background thread; flush on success.
            Task.Run(() =>
            {
                int updated = 0;
                foreach (var (cid, _) in pick)
                {
                    try
                    {
                        RaCatalogEngine.RefreshOne(cid);   // engine refresh (guards + store + IGame sync; keeps data on failure)
                        foreach (var g in games[cid]) if (RaResolveLite.RelinkRaid(g)) updated++;
                    }
                    catch { }
                }
                Console.WriteLine($"[ra-lite] startup rolling refresh: {pick.Count} console(s), {updated} raid(s) updated.");
                if (updated > 0) { try { flushUi?.Invoke(); } catch { } }
            });
        }
        catch (Exception ex) { Console.WriteLine("[ra-lite] startup rolling refresh failed: " + ex.Message); }
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
}
