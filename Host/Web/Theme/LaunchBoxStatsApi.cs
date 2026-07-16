// Aggregate play statistics grouped by LaunchBox platform name.
//   /api/launchbox/platforms/stats
//
// Response: { "stats": { "<platformName>": { lastPlayed:<ms|null>, playCount:<int>, totalPlaytime:<int secs>,
//   mostPlayedId:<string|null>, mostPlayedName:<string|null> }, … } }
//
// Data source: PluginHelper.DataManager.GetAllGames() — LiteBox's live in-memory library. Every IGame read is
// guarded (the runtime can throw for odd records). IGame.PlayTime is int seconds (NOT a TimeSpan).
//
// Clean-room LiteBox rewrite of ExtendDB's Web/LaunchBox/LaunchBoxStatsApi.cs — data path is the SDK data
// manager, which is native in-process in LiteBox, so it compiles as-is bar the log seam.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class LaunchBoxStatsApi
{
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly DateTime _epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static HttpResponse Handle(RouteContext ctx)
    {
        IEnumerable<IGame> allGames;
        try { allGames = PluginHelper.DataManager.GetAllGames() ?? Enumerable.Empty<IGame>(); }
        catch (Exception ex) { LbLog.Warn("web", "stats GetAllGames: " + ex.Message); allGames = Enumerable.Empty<IGame>(); }

        var acc = new Dictionary<string, PlatformAcc>(StringComparer.Ordinal);

        foreach (var game in allGames)
        {
            if (game == null) continue;

            string platform;
            try { platform = game.Platform; } catch { continue; }
            if (string.IsNullOrEmpty(platform)) continue;

            if (!acc.TryGetValue(platform, out var a)) { a = new PlatformAcc(); acc[platform] = a; }

            DateTime? lp = null;
            try { lp = game.LastPlayedDate; } catch { }
            if (lp.HasValue && lp.Value > DateTime.MinValue && (!a.LastPlayed.HasValue || lp.Value > a.LastPlayed.Value))
                a.LastPlayed = lp.Value;

            int pc = 0; try { pc = game.PlayCount; } catch { }
            a.PlayCount += pc;

            int pt = 0; try { pt = game.PlayTime; } catch { }
            a.TotalPlaytime += pt;

            if (pc > a.MostPlayedCount)
            {
                a.MostPlayedCount = pc;
                try { a.MostPlayedId = game.Id; } catch { a.MostPlayedId = null; }
                try { a.MostPlayedName = game.Title; } catch { a.MostPlayedName = null; }
            }
        }

        var stats = new Dictionary<string, object>(acc.Count, StringComparer.Ordinal);
        foreach (var kv in acc)
        {
            var a = kv.Value;
            long? lastPlayedMs = null;
            if (a.LastPlayed.HasValue)
            {
                var utc = a.LastPlayed.Value.Kind == DateTimeKind.Utc ? a.LastPlayed.Value : a.LastPlayed.Value.ToUniversalTime();
                lastPlayedMs = (long)(utc - _epoch).TotalMilliseconds;
            }
            stats[kv.Key] = new
            {
                lastPlayed = lastPlayedMs,
                playCount = a.PlayCount,
                totalPlaytime = a.TotalPlaytime,
                mostPlayedId = a.MostPlayedCount > 0 ? a.MostPlayedId : null,
                mostPlayedName = a.MostPlayedCount > 0 ? a.MostPlayedName : null,
            };
        }

        return HttpResponse.Json(JsonSerializer.Serialize(new { stats }, _json));
    }

    private sealed class PlatformAcc
    {
        public DateTime? LastPlayed;
        public int PlayCount;
        public int TotalPlaytime;
        public int MostPlayedCount;
        public string MostPlayedId;
        public string MostPlayedName;
    }
}
