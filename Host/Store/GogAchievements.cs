// GOG achievements provider — fully LOCAL, no web, no OAuth, no public profile.
//
// GOG Galaxy syncs the WHOLE owned library's achievements into galaxy-2.0.db (installed AND
// non-installed). We read it through the shared, change-detected snapshot (GalaxyDb.Read — one
// copy shared with the install-state reader) and join three tables on gameReleaseKey='gog_'+id:
//   Achievements          (apikey, imageUnlockedUrl, imageLockedUrl, rarity, isVisible)
//   LocalizedAchievements (apikey, name, description, languageId)   — prefer English (16)
//   UserAchievements      (apikey, unlockTime, isUnlocked)          — this user's progress
//
// Cache (Core\store-ach-cache\gog-<appId>.json) refreshes on the SAME trigger as RA — played
// since cached (lastPlayed > fetchedAt) — PLUS a Galaxy-DB signature change (catches a Galaxy
// sync even without a LiteBox launch), behind a generous safety-net TTL.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Store;

internal static class GogAchievements
{
    private const int CacheVer = 1;
    private const int PreferredLanguageId = 16;   // English (most common); fall back to whatever exists
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly object _flightGate = new();
    private static readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    // ── cache: Core\store-ach-cache\gog-<appId>.json ─────────────────────────────────────────
    private static string CacheDir
    {
        get { var d = Path.Combine(AppContext.BaseDirectory, "store-ach-cache"); try { Directory.CreateDirectory(d); } catch { } return d; }
    }
    private static string CacheFile(string appId) => Path.Combine(CacheDir, "gog-" + appId + ".json");

    public static StoreAchCache? ReadCache(string appId)
    {
        try { var f = CacheFile(appId); if (File.Exists(f)) return JsonSerializer.Deserialize<StoreAchCache>(File.ReadAllText(f), JsonOpts); }
        catch { }
        return null;
    }
    private static void WriteCache(string appId, StoreAchCache c)
    {
        try { File.WriteAllText(CacheFile(appId), JsonSerializer.Serialize(c)); } catch { }
    }

    /// <summary>The current galaxy-2.0.db change signature ("" when GOG isn't installed).</summary>
    private static string GalaxySig() => GalaxyDb.SourceDbPath() is string src ? GalaxyDb.Sig(src) : "";

    /// <summary>Fresh = right version, within TTL, NOT played since cached, AND the Galaxy DB hasn't
    /// changed since (a sync would alter unlock state without a LiteBox launch).</summary>
    private static bool IsFresh(StoreAchCache? c, DateTime lastPlayedUtc, string galaxySig)
    {
        if (c == null || c.ver != CacheVer) return false;
        if (!DateTime.TryParse(c.fetchedAt, null, DateTimeStyles.RoundtripKind, out var dt)) return false;
        var fetched = dt.ToUniversalTime();
        if ((DateTime.UtcNow - fetched) >= Ttl) return false;
        if (!string.IsNullOrEmpty(galaxySig) && !string.Equals(c.sourceSig, galaxySig, StringComparison.Ordinal)) return false;
        return lastPlayedUtc <= fetched;
    }

    /// <summary>Panel data for a GOG game, reading the Galaxy DB when the cache is missing, stale, played
    /// since cached, or the DB changed. BLOCKING (sqlite snapshot) — call from a background thread.
    /// Single-flight per appId.</summary>
    public static StoreAchCache? EnsureAndRead(string? gogAppId, DateTime lastPlayedUtc)
    {
        if (string.IsNullOrWhiteSpace(gogAppId)) return null;
        string id = gogAppId!.Trim();
        string sig = GalaxySig();
        var cache = ReadCache(id);
        if (IsFresh(cache, lastPlayedUtc, sig)) return cache;

        bool mine;
        lock (_flightGate) { mine = _inFlight.Add(id); }
        if (!mine) return cache;                  // another thread is reading — show stale/empty for now
        try
        {
            var fetched = Fetch(id, sig);
            if (fetched != null) { WriteCache(id, fetched); return fetched; }
            return cache;                         // read failed (GOG not installed?) — keep whatever we had
        }
        finally { lock (_flightGate) { _inFlight.Remove(id); } }
    }

    // ── galaxy-2.0.db read + normalise ───────────────────────────────────────────────────────
    private static StoreAchCache? Fetch(string appId, string sig)
    {
        string releaseKey = "gog_" + appId;
        StoreAchCache? result = null;

        bool ok = GalaxyDb.Read(con =>
        {
            // 1) definitions (one row per achievement)
            var byKey = new Dictionary<string, StoreAch>(StringComparer.Ordinal);
            var order = new List<string>();
            var hiddenKeys = new HashSet<string>(StringComparer.Ordinal);   // isVisible = 0 (secret until unlocked)
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT apikey, imageUnlockedUrl, imageLockedUrl, rarity, isVisible " +
                    "FROM Achievements WHERE gameReleaseKey = $rk";
                cmd.Parameters.AddWithValue("$rk", releaseKey);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string? apikey = Str(r, 0);
                    if (string.IsNullOrEmpty(apikey) || byKey.ContainsKey(apikey)) continue;
                    byKey[apikey] = new StoreAch
                    {
                        id = apikey,
                        badgeUnlocked = Str(r, 1),
                        badgeLocked = Str(r, 2),
                        rarity = Dbl(r, 3),
                        order = order.Count,
                    };
                    order.Add(apikey);
                    if (!Bool(r, 4)) hiddenKeys.Add(apikey);
                }
            }
            if (byKey.Count == 0) { result = null; return; }   // not a GOG-achievement game (or unknown id)

            // 2) this user's unlock state (OR across any users present)
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT apikey, unlockTime, isUnlocked FROM UserAchievements WHERE gameReleaseKey = $rk";
                cmd.Parameters.AddWithValue("$rk", releaseKey);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string? apikey = Str(r, 0);
                    if (string.IsNullOrEmpty(apikey) || !byKey.TryGetValue(apikey, out var ach)) continue;
                    string? when = UnlockTime(r, 1);
                    bool un = Bool(r, 2) || when != null;
                    if (un)
                    {
                        ach.unlocked = true;
                        if (when != null && (ach.unlockedAt == null || string.CompareOrdinal(when, ach.unlockedAt) < 0))
                            ach.unlockedAt = when;   // keep the earliest unlock timestamp
                    }
                }
            }

            // 3) localized name/description (prefer English; fall back to any)
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT apikey, name, description, languageId FROM LocalizedAchievements WHERE gameReleaseKey = $rk";
                cmd.Parameters.AddWithValue("$rk", releaseKey);
                using var r = cmd.ExecuteReader();
                var chosenLang = new Dictionary<string, long>(StringComparer.Ordinal);
                while (r.Read())
                {
                    string? apikey = Str(r, 0);
                    if (string.IsNullOrEmpty(apikey) || !byKey.TryGetValue(apikey, out var ach)) continue;
                    long lang = Lng(r, 3);
                    bool have = chosenLang.TryGetValue(apikey, out var cur);
                    // take the first row, then upgrade to the preferred language if we meet it
                    bool better = !have || (lang == PreferredLanguageId && cur != PreferredLanguageId);
                    if (!better) continue;
                    chosenLang[apikey] = lang;
                    ach.title = Str(r, 1);
                    string? desc = Str(r, 2);
                    // Hidden + still locked → keep the description secret (GOG convention).
                    ach.description = (hiddenKeys.Contains(apikey) && !ach.unlocked) ? null : desc;
                }
            }

            result = new StoreAchCache
            {
                store = "GOG",
                appId = appId,
                total = byKey.Count,
                unlocked = byKey.Values.Count(a => a.unlocked),
                ver = CacheVer,
                fetchedAt = DateTime.UtcNow.ToString("o"),
                sourceSig = sig,
                achievements = order.Select(k => byKey[k]).ToList(),
            };
        });

        return ok ? result : null;
    }


    // ── defensive sqlite readers (galaxy stores these with loose affinity) ─────────────────────
    private static string? Str(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);

    private static double Dbl(SqliteDataReader r, int i)
    { if (r.IsDBNull(i)) return 0; try { return Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture); } catch { return 0; } }

    private static long Lng(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0;
        var v = r.GetValue(i);
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        catch { try { return (long)Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; } }
    }

    private static bool Bool(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return false;
        var v = r.GetValue(i);
        if (v is bool b) return b;
        if (v is string s) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0; }
        catch { try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; } catch { return false; } }
    }

    // unlockTime → ISO-8601 UTC. Galaxy stores a "2022-11-02 12:21:08" UTC timestamp string here (verified),
    // but stay defensive about an epoch (seconds/millis) form too.
    private static string? UnlockTime(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        try
        {
            if (v is string s)
            {
                if (s.Length == 0) return null;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var es)) return FromEpoch(es);
                // No timezone in the string → it's UTC; AssumeUniversal keeps it UTC (no local shift).
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    return dt.ToString("o");
                return null;
            }
            long e = Convert.ToInt64(v, CultureInfo.InvariantCulture);
            return FromEpoch(e);
        }
        catch { return null; }
    }

    private static string? FromEpoch(long e)
    {
        if (e <= 0) return null;
        if (e > 100_000_000_000L) e /= 1000;   // looks like milliseconds → seconds
        try { return DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime.ToString("o"); }
        catch { return null; }
    }
}
