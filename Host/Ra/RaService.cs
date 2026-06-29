// LiteBox-owned RetroAchievements panel data — PURE host, no ExtendDB dependency at runtime.
//
// The only handoff from ExtendDB (or native LB) is the game's <RetroAchievementsId> ("raid") in the
// <Game> XML; LiteBox reads it via ILiteBoxFields.GetField. From that raid this:
//   • reads the RA Web API key + username from LB's own Settings.xml,
//   • GETs the PUBLIC API (API_GetGameInfoAndUserProgress) for the achievement set + the user's unlocks,
//   • normalises + caches the result per raid under Core\ra-cache\<raid>.json (System.Text.Json).
//
// The "time to beat / master" MEDIANS are NOT in any public endpoint (they live in RA's authenticated
// connect API). LB already wrote them into the <Game> XML (RetroAchievementsMedianTimeTo…); LiteBox reads
// those via GetField at panel-build time. A live refresh of those medians is a documented TODO (capture
// LB's own call via the HTTP log first) — see RaFields.ReadMedians / MainWindow.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Ra;

/// <summary>Fetch + cache of one game's achievements/progress from the public RA Web API. See file header.</summary>
internal static class RaService
{
    private static readonly HttpClient Http = BuildClient();
    private static HttpClient BuildClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // RA's WAF 403s the default .NET UA on some paths; a browser UA sails through (proven in StoreProbe.py).
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
        return c;
    }

    private const int CacheVer = 3;   // bump to invalidate every cached file after a shape change (e.g. medians/beaten added)
    // Safety-net TTL for the game-level data (medians drift, achievements get added). The USER's own
    // progress is caught earlier by the LastPlayed trigger, so this can be generous.
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly object _flightGate = new();
    private static readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── settings: RA key + username straight from LB's Settings.xml (no ExtendDB) ────────────
    private static string? _key, _user;
    private static bool _settingsRead;
    private static void ReadSettings()
    {
        if (_settingsRead) return;
        _settingsRead = true;
        try
        {
            var p = Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Settings.xml");
            if (!File.Exists(p)) return;
            var txt = File.ReadAllText(p);
            _key = Between(txt, "RetroAchievementsApiKey");
            _user = Between(txt, "RetroAchievementsUsername");
        }
        catch { }
    }
    private static string? Between(string xml, string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, "<" + tag + ">(.*?)</" + tag + ">");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>True when a key + username are configured (else the panel can't fetch anything).</summary>
    public static bool Configured { get { ReadSettings(); return !string.IsNullOrEmpty(_key) && !string.IsNullOrEmpty(_user); } }

    // ── cache: Core\ra-cache\<raid>.json ─────────────────────────────────────────────────────
    private static string CacheDir
    {
        get { var d = Path.Combine(AppContext.BaseDirectory, "ra-cache"); try { Directory.CreateDirectory(d); } catch { } return d; }
    }
    private static string CacheFile(int raid) => Path.Combine(CacheDir, raid + ".json");

    public static RaGameCache? ReadCache(int raid)
    {
        try { var f = CacheFile(raid); if (File.Exists(f)) return JsonSerializer.Deserialize<RaGameCache>(File.ReadAllText(f), JsonOpts); }
        catch { }
        return null;
    }
    private static void WriteCache(int raid, RaGameCache c)
    {
        try { File.WriteAllText(CacheFile(raid), JsonSerializer.Serialize(c)); } catch { }
    }
    /// <summary>Fresh = right cache version, within the safety TTL, AND not played since we cached (the
    /// user's unlock progress only changes when they play, so a launch after the cache invalidates it).</summary>
    private static bool IsFresh(RaGameCache? c, DateTime lastPlayedUtc)
    {
        if (c == null || c.ver != CacheVer) return false;
        if (!DateTime.TryParse(c.fetchedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) return false;
        var fetched = dt.ToUniversalTime();
        if ((DateTime.UtcNow - fetched) >= Ttl) return false;
        return lastPlayedUtc <= fetched;   // played since we cached → stale
    }

    /// <summary>Returns the panel data for a raid, fetching from the API first when the cache is missing,
    /// stale (TTL), or the game was PLAYED since it was cached (<paramref name="lastPlayedUtc"/>). BLOCKING
    /// (network) — call from a background thread. Single-flight per raid.</summary>
    public static RaGameCache? EnsureAndRead(int raid, DateTime lastPlayedUtc)
    {
        if (raid <= 0) return null;
        var cache = ReadCache(raid);
        if (IsFresh(cache, lastPlayedUtc)) return cache;

        string key = raid.ToString();
        bool mine;
        lock (_flightGate) { mine = _inFlight.Add(key); }
        if (!mine) return cache;                  // another thread is fetching — show stale/empty for now
        try
        {
            var fetched = Fetch(raid);
            if (fetched != null) { WriteCache(raid, fetched); return fetched; }
            return cache;                         // network failed — keep whatever we had
        }
        finally { lock (_flightGate) { _inFlight.Remove(key); } }
    }

    // ── public API GET + normalise ───────────────────────────────────────────────────────────
    private static RaGameCache? Fetch(int raid)
    {
        ReadSettings();
        if (string.IsNullOrEmpty(_key) || string.IsNullOrEmpty(_user)) return null;
        try
        {
            // a=1 → include the award fields (HighestAwardKind) we need for the beaten-softcore/hardcore flags.
            string url = "https://retroachievements.org/API/API_GetGameInfoAndUserProgress.php"
                       + $"?g={raid}&u={Uri.EscapeDataString(_user!)}&y={Uri.EscapeDataString(_key!)}&a=1";
            string body;
            using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) { Console.WriteLine($"[ra] fetch g={raid} HTTP {(int)resp.StatusCode}"); return null; }
                body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            var api = JsonSerializer.Deserialize<ApiGame>(body, JsonOpts);
            if (api == null) return null;

            var c = new RaGameCache
            {
                gameId = api.ID != 0 ? api.ID : raid,
                title = api.Title,
                imageIcon = api.ImageIcon,
                total = api.NumAchievements,
                unlocked = api.NumAwardedToUser,
                unlockedHardcore = api.NumAwardedToUserHardcore,
                completion = api.UserCompletion,
                fetchedAt = DateTime.UtcNow.ToString("o"),
                ver = CacheVer,
            };
            // Beaten flags from the user's HighestAwardKind (a=1). Any award ⇒ beaten-softcore; the
            // hardcore variants ⇒ beaten-hardcore. null/absent (no progress) ⇒ both false (matches LB).
            string awk = api.HighestAwardKind ?? "";
            c.beatenSoftcore = awk is "beaten-softcore" or "beaten-hardcore" or "completed" or "mastered";
            c.beatenHardcore = awk is "beaten-hardcore" or "mastered";
            FetchMedians(raid, c);   // game-level "time to beat / master" commitments (separate endpoint)
            if (api.Achievements != null)
            {
                foreach (var kv in api.Achievements)
                {
                    var a = kv.Value;
                    if (a == null || a.ID == 0) continue;
                    bool hc = !string.IsNullOrEmpty(a.DateEarnedHardcore);
                    c.achievements.Add(new RaCacheAch
                    {
                        id = a.ID, title = a.Title, description = a.Description, points = a.Points,
                        badge = a.BadgeName, order = a.DisplayOrder,
                        unlocked = hc || !string.IsNullOrEmpty(a.DateEarned), unlockedHardcore = hc,
                    });
                }
                c.achievements.Sort((x, y) => x.order != y.order ? x.order.CompareTo(y.order) : x.id.CompareTo(y.id));
            }
            return c;
        }
        catch (Exception ex) { Console.WriteLine($"[ra] fetch g={raid} failed: {ex.Message}"); return null; }
    }

    // API_GetGameProgression — game-level "time to beat / master" medians. The API gives SECONDS; LB
    // stores (and we display) MINUTES (verified: MedianTimeToBeatHardcore 44765s → 746 min). Best-effort:
    // a failure leaves the medians 0 and the card falls back to whatever the game XML carries.
    private static void FetchMedians(int raid, RaGameCache c)
    {
        if (string.IsNullOrEmpty(_key)) return;
        try
        {
            string url = "https://retroachievements.org/API/API_GetGameProgression.php"
                       + $"?i={raid}&y={Uri.EscapeDataString(_key!)}"
                       + (string.IsNullOrEmpty(_user) ? "" : $"&z={Uri.EscapeDataString(_user!)}");
            string body;
            using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) return;
                body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            var p = JsonSerializer.Deserialize<ApiProgression>(body, JsonOpts);
            if (p == null) return;
            c.beatMin = SecToMin(p.MedianTimeToBeatHardcore);
            c.masterMin = SecToMin(p.MedianTimeToMaster);
            c.beatSamples = p.TimesUsedInHardcoreBeatMedian;
            c.masterSamples = p.TimesUsedInMasteryMedian;
        }
        catch (Exception ex) { Console.WriteLine($"[ra] medians g={raid} failed: {ex.Message}"); }
    }

    private static int SecToMin(int seconds) => seconds > 0 ? (int)Math.Round(seconds / 60.0) : 0;

    // ── API DTOs ─────────────────────────────────────────────────────────────────────────────
    private sealed class ApiProgression
    {
        public int MedianTimeToBeatHardcore { get; set; }
        public int MedianTimeToMaster { get; set; }
        public int TimesUsedInHardcoreBeatMedian { get; set; }
        public int TimesUsedInMasteryMedian { get; set; }
    }

    private sealed class ApiGame
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public string? ImageIcon { get; set; }
        public int NumAchievements { get; set; }
        public int NumAwardedToUser { get; set; }
        public int NumAwardedToUserHardcore { get; set; }
        public string? UserCompletion { get; set; }
        public string? UserCompletionHardcore { get; set; }
        public int UserTotalPlaytime { get; set; }
        public string? HighestAwardKind { get; set; }   // mastered / completed / beaten-hardcore / beaten-softcore (a=1)
        public Dictionary<string, ApiAch>? Achievements { get; set; }
    }
    private sealed class ApiAch
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Points { get; set; }
        public string? BadgeName { get; set; }
        public int DisplayOrder { get; set; }
        public string? Type { get; set; }
        public string? DateEarned { get; set; }
        public string? DateEarnedHardcore { get; set; }
    }
}

/// <summary>Normalised, cached achievements + the user's unlock state for one game (Core\ra-cache\&lt;raid&gt;.json).</summary>
internal sealed class RaGameCache
{
    public int gameId { get; set; }
    public string? title { get; set; }
    public string? imageIcon { get; set; }
    public int total { get; set; }
    public int unlocked { get; set; }
    public int unlockedHardcore { get; set; }
    public string? completion { get; set; }
    public int beatMin { get; set; }        // median "time to beat" (hardcore), minutes — from GetGameProgression
    public int masterMin { get; set; }      // median "time to master", minutes
    public int beatSamples { get; set; }    // sample size behind each median (TimesUsedIn…Median)
    public int masterSamples { get; set; }
    public bool beatenSoftcore { get; set; }   // from HighestAwardKind — mirrors LB's RetroAchievementsBeaten* XML
    public bool beatenHardcore { get; set; }
    public int ver { get; set; }            // cache-shape version (RaService.CacheVer) — old files are refetched
    public string? fetchedAt { get; set; }
    public List<RaCacheAch> achievements { get; set; } = new();
}

/// <summary>One achievement def + this user's unlock state.</summary>
internal sealed class RaCacheAch
{
    public int id { get; set; }
    public string? title { get; set; }
    public string? description { get; set; }
    public int points { get; set; }
    public string? badge { get; set; }
    public int order { get; set; }
    public bool unlocked { get; set; }
    public bool unlockedHardcore { get; set; }
}
