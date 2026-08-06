// The RetroAchievements ACCOUNT — the numbers LaunchBox puts in its menu bar ("HARDCORE POINTS: 30")
// and behind it, in its RetroAchievements window: the profile counters, the last games played, and the
// site's global top ten.
//
// RaService is the per-GAME sibling of this file (one raid → its achievements + this user's unlocks);
// this one is per-ACCOUNT, and deliberately mirrors its shape — same settings source (RaService.ApiKey /
// .Username, already parsed out of LB's Settings.xml), same browser User-Agent (RA's WAF 403s the default
// .NET one), same cache directory, same "a failed fetch keeps the previous cache" rule.
//
// Five public Web API calls, because no single endpoint carries the window:
//
//   API_GetUserProfile              → username, member-since, the three point totals
//   API_GetUserCompletionProgress   → achievements unlocked (Σ NumAwarded) + games beaten (award kinds)
//   API_GetUserSummary  (g=5, a=0)  → RecentlyPlayed[] joined to Awarded{} — the Recent Activity list
//   API_GetTopTenUsers              → the Global Leaderboard tab
//   API_GetUserRankAndScore         → the user's own row under it (Rank is NULL when unranked)
//
// Recent Activity comes from the SUMMARY, not from completion progress: the list includes games with
// zero unlocks (LaunchBox shows "Earned 0 of 85 achievements"), and completion progress only returns
// games that HAVE progress. The join is Awarded[GameID] — RecentlyPlayed alone has no score fields.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace LbApiHost.Host.Ra;

/// <summary>Fetch + cache of the RA ACCOUNT summary (profile counters, recent activity, top ten).
/// See file header.</summary>
internal static class RaProfileService
{
    private static readonly HttpClient Http = BuildClient();
    private static HttpClient BuildClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Same reason as RaService: RA's WAF 403s the default .NET UA on some paths.
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
        return c;
    }

    private const int CacheVer = 3;              // bump to invalidate every cached profile after a shape change
    /// <summary>How many recently-played games to pull. The window shows five and reveals the rest on
    /// demand, so this is the ceiling of "Show more", not what is drawn. An account with a shorter history
    /// simply comes back shorter — RA returns what it has.</summary>
    public const int RecentGames = 100;
    // Short by RaService's standards: these numbers move every time the user unlocks anything, and the
    // window refreshes in the background anyway — the TTL only decides whether the MENU LABEL refetches.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly object _gate = new();
    private static bool _inFlight;
    private static RaProfile? _mem;              // last known profile, so the menu label never re-reads the file
    private static string? _sig;                 // its Signature(), so a refresh that found nothing stays silent

    /// <summary>Raised on the fetching thread when a refresh brought back something DIFFERENT — the menu
    /// label and the open window both marshal to the UI thread themselves. A refresh that finds the same
    /// numbers raises nothing, so the window doesn't rebuild (and reset your scroll) for no reason.</summary>
    public static event Action? Changed;

    /// <summary>The payload with the timestamp taken out. Two refreshes an hour apart that found nothing
    /// new have to compare EQUAL, and <see cref="RaProfile.fetchedAt"/> alone would make them differ every
    /// time. Only ever called on a profile we still own privately — never on the published one.</summary>
    private static string Signature(RaProfile p)
    {
        string? stamp = p.fetchedAt;
        p.fetchedAt = null;
        try { return JsonSerializer.Serialize(p); }
        finally { p.fetchedAt = stamp; }
    }

    /// <summary>True when a Web API key + username are configured (shared with the per-game service).</summary>
    public static bool Configured => RaService.Configured;

    // ── cache: Core\litebox\cache\ra-cache\user-<name>.json ──────────────────────────────────
    // One file next to the per-raid ones. The username is in the NAME, not just the payload, so
    // switching accounts reads a different file instead of showing the previous account's points.
    private static string? CacheFile()
    {
        string user = RaService.Username ?? "";
        if (user.Length == 0) return null;
        foreach (char c in Path.GetInvalidFileNameChars()) user = user.Replace(c, '_');
        return Path.Combine(LiteBoxPaths.CacheDir("ra-cache"), "user-" + user.ToLowerInvariant() + ".json");
    }

    /// <summary>The last profile we have — memory, else the cache file, else null. NEVER fetches, so the
    /// menu bar can label itself on the UI thread at boot.</summary>
    public static RaProfile? Cached()
    {
        var m = _mem;
        if (m != null) return m;
        try
        {
            var f = CacheFile();
            if (f != null && File.Exists(f))
            {
                var p = JsonSerializer.Deserialize<RaProfile>(File.ReadAllText(f), JsonOpts);
                if (p != null && p.ver == CacheVer)
                {
                    _sig = Signature(p);        // before publishing: the object is still ours alone
                    return _mem = p;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>True when the cache is old enough to be worth a refetch (or absent entirely).</summary>
    public static bool IsStale()
    {
        var c = Cached();
        if (c == null) return true;
        return !DateTime.TryParse(c.fetchedAt, null, DateTimeStyles.RoundtripKind, out var dt)
            || (DateTime.UtcNow - dt.ToUniversalTime()) >= Ttl;
    }

    /// <summary>Fetch the whole account picture and cache it. BLOCKING (five GETs) — call from a
    /// background thread. Single-flight: a second caller returns immediately rather than queueing.
    /// Never throws; on any failure the previous cache is left exactly as it was.
    /// <see cref="Changed"/> only fires when the numbers actually moved.</summary>
    public static RaProfile? Refresh()
    {
        if (!Configured) return null;
        lock (_gate) { if (_inFlight) return _mem; _inFlight = true; }
        try
        {
            var p = Fetch();
            if (p == null) return _mem;                 // network/parse failure — keep what we had
            string sig = Signature(p);                  // p is still private to this thread
            bool moved = _sig == null || sig != _sig;
            _mem = p; _sig = sig;
            // The file is rewritten either way: even an identical payload refreshes fetchedAt, which is
            // what stops the boot-time RefreshIfStale from re-fetching on every restart.
            try { var f = CacheFile(); if (f != null) File.WriteAllText(f, JsonSerializer.Serialize(p)); } catch { }
            if (moved) { try { Changed?.Invoke(); } catch { } }
            return p;
        }
        finally { lock (_gate) { _inFlight = false; } }
    }

    /// <summary>Refresh only if the cache has aged past the TTL — what the boot-time kick calls, so a
    /// restart two minutes later doesn't spend five requests re-fetching numbers that cannot have moved.</summary>
    public static void RefreshIfStale() { if (IsStale()) Refresh(); }

    // ── the five calls ───────────────────────────────────────────────────────────────────────
    private static RaProfile? Fetch()
    {
        string? key = RaService.ApiKey, user = RaService.Username;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(user)) return null;

        string u = Uri.EscapeDataString(user!), y = Uri.EscapeDataString(key!);
        string Api(string php, string args) => $"https://retroachievements.org/API/{php}?{args}&y={y}";

        // The profile is the only REQUIRED call — it carries the points the menu label exists for.
        // The other four decorate the window and are best-effort: one of them failing must not cost
        // the user their points display.
        var prof = Get<ApiProfile>(Api("API_GetUserProfile.php", $"u={u}"));
        if (prof == null) return null;

        var p = new RaProfile
        {
            user = string.IsNullOrEmpty(prof.User) ? user : prof.User,
            memberSince = prof.MemberSince,
            hardcorePoints = prof.TotalPoints,
            softcorePoints = prof.TotalSoftcorePoints,
            retroPoints = prof.TotalTruePoints,
            motto = prof.Motto,
            fetchedAt = DateTime.UtcNow.ToString("o"),
            ver = CacheVer,
        };

        // Achievements unlocked / games beaten. c=500 pages the whole set for anyone short of a
        // completionist; RA caps the page anyway, and Total tells us if we were cut off.
        var comp = Get<ApiCompletion>(Api("API_GetUserCompletionProgress.php", $"u={u}&c=500"));
        if (comp?.Results != null)
        {
            foreach (var g in comp.Results)
            {
                p.achievementsUnlocked += g.NumAwarded;
                // The award kinds that count as "beaten" — the same set RaService maps onto its
                // beatenSoftcore flag (mastery and completion imply the game was beaten).
                if (g.HighestAwardKind is "beaten-softcore" or "beaten-hardcore" or "completed" or "mastered")
                    p.gamesBeaten++;
            }
        }

        // Recent activity. g=100 pulls the whole list in ONE call so the window's "Show more" is instant
        // and costs no second round trip — measured at 12 KB / 180 ms against 3.6 KB / 98 ms for g=5, which
        // is not a trade worth making the user wait for. The window shows five of them until asked.
        // a=1, not a=0: a=0 does NOT mean "none" to RA, it means "no limit", and the reply comes back
        // 70 KB of full unlock history. 1 is the smallest number that actually caps it.
        var sum = Get<ApiSummary>(Api("API_GetUserSummary.php", $"u={u}&g={RecentGames}&a=1"));
        if (sum?.RecentlyPlayed != null)
        {
            foreach (var g in sum.RecentlyPlayed)
            {
                var row = new RaRecentGame
                {
                    gameId = g.GameID, title = g.Title, console = g.ConsoleName, consoleId = g.ConsoleID,
                    lastPlayed = g.LastPlayed, total = g.AchievementsTotal,
                    imageIcon = g.ImageIcon,   // RA's own box art — the thumb for a game we don't own
                };
                // The counts live in a SEPARATE map keyed by game id — RecentlyPlayed itself has none.
                if (sum.Awarded != null && sum.Awarded.TryGetValue(g.GameID.ToString(), out var a) && a != null)
                {
                    row.total = a.NumPossibleAchievements > 0 ? a.NumPossibleAchievements : row.total;
                    row.earned = a.NumAchieved;
                    row.points = a.ScoreAchieved;
                    row.possiblePoints = a.PossibleScore;
                }
                p.recent.Add(row);
            }
        }

        // Global leaderboard. The rows are positional: "1" name, "2" points, "3" retro points.
        var top = Get<List<Dictionary<string, JsonElement>>>(Api("API_GetTopTenUsers.php", "u=" + u));
        if (top != null)
        {
            int rank = 0;
            foreach (var row in top)
            {
                rank++;
                p.leaderboard.Add(new RaLeaderRow
                {
                    rank = rank,
                    user = row.TryGetValue("1", out var n) ? Str(n) : "",
                    points = row.TryGetValue("2", out var s) ? Num(s) : 0,
                    retroPoints = row.TryGetValue("3", out var t) ? Num(t) : 0,
                });
            }
        }

        // The user's own row under the top ten. Rank is null for an unranked account — LaunchBox
        // leaves the cell blank rather than printing a 0, and 0 is what a non-nullable int would give.
        var rs = Get<ApiRank>(Api("API_GetUserRankAndScore.php", $"u={u}"));
        if (rs != null) { p.rank = rs.Rank; p.totalRanked = rs.TotalRanked; }

        return p;
    }

    private static T? Get<T>(string url) where T : class
    {
        try
        {
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                Diag.LbLog.Info("ra", $"profile GET {Redact(url)} → HTTP {(int)resp.StatusCode}");
                return null;
            }
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (Exception ex) { Diag.LbLog.Info("ra", $"profile GET {Redact(url)} failed: {ex.Message}"); return null; }
    }

    /// <summary>The API key rides in the query string; the log must not.</summary>
    private static string Redact(string url)
    {
        int i = url.IndexOf("&y=", StringComparison.Ordinal);
        return i < 0 ? url : url.Substring(0, i) + "&y=***";
    }

    // RA is loose about JSON types — the same field comes back quoted on one endpoint and bare on
    // another (points are numbers in TopTenUsers, strings in older payloads). Read both.
    private static string Str(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : e.ToString();
    private static int Num(JsonElement e)
        => e.ValueKind == JsonValueKind.Number ? (e.TryGetInt32(out var n) ? n : 0)
         : e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var m) ? m : 0;

    // ── API DTOs ─────────────────────────────────────────────────────────────────────────────
    private sealed class ApiProfile
    {
        public string? User { get; set; }
        public string? MemberSince { get; set; }
        public int TotalPoints { get; set; }
        public int TotalSoftcorePoints { get; set; }
        public int TotalTruePoints { get; set; }
        public string? Motto { get; set; }
    }

    private sealed class ApiRank
    {
        public int Score { get; set; }
        public int? Rank { get; set; }              // null when the account isn't ranked yet
        public int TotalRanked { get; set; }
    }

    private sealed class ApiCompletion
    {
        public int Count { get; set; }
        public int Total { get; set; }
        public List<ApiCompletionGame>? Results { get; set; }
    }
    private sealed class ApiCompletionGame
    {
        public int GameID { get; set; }
        public int NumAwarded { get; set; }
        public int NumAwardedHardcore { get; set; }
        public string? HighestAwardKind { get; set; }
    }

    private sealed class ApiSummary
    {
        public List<ApiRecent>? RecentlyPlayed { get; set; }
        public Dictionary<string, ApiAwarded>? Awarded { get; set; }
    }
    private sealed class ApiRecent
    {
        public int GameID { get; set; }
        public int ConsoleID { get; set; }
        public string? Title { get; set; }
        public string? ConsoleName { get; set; }
        public string? LastPlayed { get; set; }
        public int AchievementsTotal { get; set; }
        public string? ImageIcon { get; set; }         // "/Images/106799.png", relative to media.retroachievements.org
    }
    private sealed class ApiAwarded
    {
        public int NumPossibleAchievements { get; set; }
        public int PossibleScore { get; set; }
        public int NumAchieved { get; set; }
        public int ScoreAchieved { get; set; }
        public int NumAchievedHardcore { get; set; }
        public int ScoreAchievedHardcore { get; set; }
    }
}

/// <summary>The cached account picture (Core\litebox\cache\ra-cache\user-&lt;name&gt;.json).</summary>
internal sealed class RaProfile
{
    public string? user { get; set; }
    public string? memberSince { get; set; }      // RA's "yyyy-MM-dd HH:mm:ss"
    public string? motto { get; set; }
    public int hardcorePoints { get; set; }
    public int softcorePoints { get; set; }
    public int retroPoints { get; set; }
    public int achievementsUnlocked { get; set; }
    public int gamesBeaten { get; set; }
    public int? rank { get; set; }                // null ⇒ unranked, and the leaderboard cell stays blank
    public int totalRanked { get; set; }
    public int ver { get; set; }
    public string? fetchedAt { get; set; }
    public List<RaRecentGame> recent { get; set; } = new();
    public List<RaLeaderRow> leaderboard { get; set; } = new();

    /// <summary>"Tuesday, June 2, 2026" — the long date LaunchBox prints, from RA's own format.
    /// Falls back to the raw string if RA ever changes it.</summary>
    public string MemberSinceLong()
    {
        if (string.IsNullOrWhiteSpace(memberSince)) return "";
        return DateTime.TryParse(memberSince, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("D", CultureInfo.CurrentCulture) : memberSince!;
    }
}

/// <summary>One row of Recent Activity: a game the user last played, with their progress on it.</summary>
internal sealed class RaRecentGame
{
    public int gameId { get; set; }
    public string? title { get; set; }
    public string? console { get; set; }
    public int consoleId { get; set; }       // RA's console id — checked against the library entry's platform
    public string? lastPlayed { get; set; }
    public string? imageIcon { get; set; }   // RA-relative art path ("/Images/106799.png")
    public int earned { get; set; }
    public int total { get; set; }
    public int points { get; set; }
    public int possiblePoints { get; set; }

    /// <summary>The matching game in OUR library, or null when we don't have it. Filled in at display time
    /// by matching <see cref="gameId"/> (the RA id) against each game's &lt;RetroAchievementsId&gt;, and
    /// deliberately NOT serialised: the library changes under the cache, so a stale id would send a click
    /// to a game that has since been removed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? localGameId { get; set; }

    /// <summary>The "N% Complete" figure — POINTS earned over points possible, TRUNCATED. Not the
    /// achievement ratio: 1 of 32 achievements worth 5 of 385 points reads 1%, not 3%, and that is
    /// what LaunchBox shows.</summary>
    public int PercentComplete()
        => possiblePoints > 0 ? (int)Math.Floor(points * 100.0 / possiblePoints) : 0;

    /// <summary>"7/30/2026 10:50 PM" — RA's timestamp in the machine's short date + short time.</summary>
    public string LastPlayedLocal()
    {
        if (string.IsNullOrWhiteSpace(lastPlayed)) return "";
        return DateTime.TryParse(lastPlayed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("g", CultureInfo.CurrentCulture) : lastPlayed!;
    }
}

/// <summary>One row of the global top ten.</summary>
internal sealed class RaLeaderRow
{
    public int rank { get; set; }
    public string? user { get; set; }
    public int points { get; set; }
    public int retroPoints { get; set; }
}
