// LiteBox-native hash → raid resolver (fallback when ExtendDB isn't resolving RA).
//
// RA's public Web API has NO single-hash lookup (the emulator endpoint dorequest.php?r=gameid 403s), so we
// do what ExtendDB does: pull the whole console catalogue via API_GetGameList?i=<console>&h=1 (games + their
// hashes), build a hash → game-id map, and look up locally. Cached on disk per console at
// Core\ra-cache\catalog-<console>.json.
//
//   • GUARD: the web response is used (and cached) ONLY when it is HTTP-success, parses as a JSON array,
//     and is non-empty. Otherwise we keep whatever cache we already had (a blip never wipes a good catalogue).
//   • TTL: 24h, judged on the cache FILE's last-write time (no timestamp inside the JSON).
//
// First game of a console pays one download (on the background panel thread); the rest hit the local map.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace LbApiHost.Host.Ra;

internal static class RaCatalogLite
{
    private static readonly HttpClient Http = BuildClient();
    private static HttpClient BuildClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };   // NES/SNES/Genesis = thousands of games
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
        return c;
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly object _gate = new();
    // In-process memo: consoleId → (hash → raid). Avoids re-reading the file on every lookup.
    private static readonly Dictionary<int, Dictionary<string, int>> _memo = new();

    private static string CacheDir
    {
        get { var d = Path.Combine(AppContext.BaseDirectory, "ra-cache"); try { Directory.CreateDirectory(d); } catch { } return d; }
    }
    private static string CacheFile(int consoleId) => Path.Combine(CacheDir, $"catalog-{consoleId}.json");

    /// <summary>The raid (RA game id) for a ROM hash on a console, or 0 when not in the catalogue. BLOCKING
    /// (may fetch the console catalogue on first use) — call from a background thread.</summary>
    public static int LookupRaid(int consoleId, string? hash)
    {
        if (consoleId <= 0 || string.IsNullOrWhiteSpace(hash)) return 0;
        var map = GetMap(consoleId);
        return map.TryGetValue(hash!.Trim().ToLowerInvariant(), out var raid) ? raid : 0;
    }

    private static Dictionary<string, int> GetMap(int consoleId)
    {
        lock (_gate)
        {
            if (_memo.TryGetValue(consoleId, out var m)) return m;

            // Disk cache fresh enough (by file mtime)?
            string file = CacheFile(consoleId);
            try
            {
                if (File.Exists(file) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)) < Ttl)
                {
                    var cached = LoadFile(file);
                    if (cached != null) { _memo[consoleId] = cached; return cached; }
                }
            }
            catch { }

            // Fetch from the API (guarded). On any guard failure, fall back to a stale cache if present.
            var fetched = Fetch(consoleId);
            if (fetched != null)
            {
                try { File.WriteAllText(file, JsonSerializer.Serialize(fetched)); } catch { }
                _memo[consoleId] = fetched;
                return fetched;
            }

            var stale = LoadFile(file) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _memo[consoleId] = stale;   // memoize so we don't re-hit the network this session
            return stale;
        }
    }

    private static Dictionary<string, int>? LoadFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file), JsonOpts);
            return d == null ? null : new Dictionary<string, int>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    /// <summary>GETs API_GetGameList for a console and builds hash → raid. Returns null (NOT an empty map)
    /// when the GUARD fails — HTTP error, non-JSON / non-array body, or an empty list — so the caller keeps
    /// any existing cache instead of overwriting it with garbage.</summary>
    private static Dictionary<string, int>? Fetch(int consoleId)
    {
        var key = RaService.ApiKey;
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            // h=1 → include each game's Hashes[]; f=1 → only games that have achievements.
            string url = "https://retroachievements.org/API/API_GetGameList.php"
                       + $"?i={consoleId}&h=1&f=1&y={Uri.EscapeDataString(key!)}";
            string body;
            using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) { Console.WriteLine($"[ra-lite] catalog i={consoleId} HTTP {(int)resp.StatusCode} → keep cache."); return null; }
                body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            if (string.IsNullOrWhiteSpace(body)) { Console.WriteLine($"[ra-lite] catalog i={consoleId} empty body → keep cache."); return null; }

            List<ApiListGame>? games;
            try { games = JsonSerializer.Deserialize<List<ApiListGame>>(body, JsonOpts); }
            catch (JsonException) { Console.WriteLine($"[ra-lite] catalog i={consoleId} not a JSON array → keep cache."); return null; }
            if (games == null || games.Count == 0) { Console.WriteLine($"[ra-lite] catalog i={consoleId} 0 games → keep cache."); return null; }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                if (g == null || g.ID <= 0 || g.Hashes == null) continue;
                foreach (var h in g.Hashes)
                    if (!string.IsNullOrWhiteSpace(h)) map[h.Trim().ToLowerInvariant()] = g.ID;
            }
            if (map.Count == 0) { Console.WriteLine($"[ra-lite] catalog i={consoleId} parsed but 0 hashes → keep cache."); return null; }
            Console.WriteLine($"[ra-lite] catalog i={consoleId} fetched: {games.Count} games, {map.Count} hashes.");
            return map;
        }
        catch (Exception ex) { Console.WriteLine($"[ra-lite] catalog i={consoleId} failed: {ex.Message} → keep cache."); return null; }
    }

    private sealed class ApiListGame
    {
        public int ID { get; set; }
        public List<string>? Hashes { get; set; }
    }
}
