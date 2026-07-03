// Steam achievements provider — WEB-FIRST, helper fallback. No public-profile compromise beyond the
// one "Game details = Public" toggle the user opted into.
//
//   Unlock state:
//     1) WEB  GetPlayerAchievements (needs the SteamApiKey + "Game details" public) — works with Steam
//        CLOSED and covers the whole owned library (installed or not), just like GOG's local DB.
//     2) HELPER  SteamWorks (LiteBox.exe --steam-ach) — the private, no-web fallback when the web route
//        is blocked (Game details private again); needs the Steam client running.
//   Definitions (names/desc/icons): GetSchemaForGame (SteamApiKey; app-level, no profile needed).
//   Rarity: GetGlobalAchievementPercentagesForApp (no key).
//
// Cache (Core\store-ach-cache\steam-<appId>.json) refreshes when played since cached or after a 1-day
// TTL. Normalises to the same StoreAchCache the store card renders.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Store;

internal static class SteamAchievements
{
    private const int CacheVer = 2;
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly object _flightGate = new();
    private static readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    private static readonly HttpClient Http = BuildClient();
    private static HttpClient BuildClient()
    {
        // Per-request backstop; each Fetch also caps the WHOLE run with a CancellationToken deadline so
        // the sequential calls can't add up to a long freeze.
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
        return c;
    }

    /// <summary>GET → body string, bounded by <paramref name="ct"/> (a per-fetch deadline). Null on any
    /// failure/timeout — callers degrade gracefully (cache/fallback) rather than block.</summary>
    private static string? GetJson(string url, CancellationToken ct)
    {
        try
        {
            using var resp = Http.GetAsync(url, ct).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            return resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    // ── LB Settings.xml: Steam API key + user (vanity or steamid64) ───────────────────────────
    private static string? Setting(string tag)
    {
        try
        {
            var p = Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Settings.xml");
            if (!File.Exists(p)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(p), "<" + tag + ">(.*?)</" + tag + ">");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    private static string? _key; private static bool _keyRead;
    private static string? ApiKey()
    {
        if (!_keyRead) { _keyRead = true; _key = Setting("SteamApiKey"); }
        return string.IsNullOrEmpty(_key) ? null : _key;
    }

    // SteamID64 from SteamUserName (a steamid64 verbatim, else resolve the vanity), or the logged-in
    // user in the registry. Cached for the session.
    private static string? _steamId; private static bool _steamIdTried;
    private static string? SteamId()
    {
        if (_steamIdTried) return _steamId;
        _steamIdTried = true;
        try
        {
            string user = Setting("SteamUserName") ?? "";
            if (user.Length == 17 && user.All(char.IsDigit)) { _steamId = user; return _steamId; }
            var key = ApiKey();
            if (!string.IsNullOrEmpty(user) && key != null)
            {
                string url = $"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key={key}&vanityurl={Uri.EscapeDataString(user)}";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var body = GetJson(url, cts.Token);
                if (body != null)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("response", out var r)
                        && r.TryGetProperty("success", out var s) && s.GetInt32() == 1
                        && r.TryGetProperty("steamid", out var sid))
                        { _steamId = sid.GetString(); return _steamId; }
                }
            }
        }
        catch { }
        // fallback: HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser (logged-in account id)
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            if (v != null && long.TryParse(v.ToString(), out var acc) && acc > 0)
                _steamId = (76561197960265728L + acc).ToString(CultureInfo.InvariantCulture);
        }
        catch { }
        return _steamId;
    }

    // Steam client display language (so web names match the client / helper), else english.
    private static string? _lang; private static bool _langRead;
    private static string Lang()
    {
        if (!_langRead)
        {
            _langRead = true;
            try { _lang = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "Language", null)?.ToString(); }
            catch { }
            if (string.IsNullOrWhiteSpace(_lang)) _lang = "english";
        }
        return _lang!;
    }

    // ── cache: Core\litebox\store-ach-cache\steam-<appId>.json ───────────────────────────────
    private static string CacheDir => LiteBoxPaths.Dir("store-ach-cache");
    private static string CacheFile(string appId) => Path.Combine(CacheDir, "steam-" + appId + ".json");

    public static StoreAchCache? ReadCache(string appId)
    {
        try { var f = CacheFile(appId); if (File.Exists(f)) return JsonSerializer.Deserialize<StoreAchCache>(File.ReadAllText(f), JsonOpts); }
        catch { }
        return null;
    }
    private static void WriteCache(string appId, StoreAchCache c)
    { try { File.WriteAllText(CacheFile(appId), JsonSerializer.Serialize(c)); } catch { } }

    private static bool IsFresh(StoreAchCache? c, DateTime lastPlayedUtc)
    {
        if (c == null || c.ver != CacheVer) return false;
        if (!DateTime.TryParse(c.fetchedAt, null, DateTimeStyles.RoundtripKind, out var dt)) return false;
        var fetched = dt.ToUniversalTime();
        if ((DateTime.UtcNow - fetched) >= Ttl) return false;
        return lastPlayedUtc <= fetched;
    }

    /// <summary>Panel data for a Steam game — web unlock state first (works Steam-closed), else the
    /// Steamworks helper (needs Steam running). BLOCKING — call from a background thread. Single-flight.</summary>
    public static StoreAchCache? EnsureAndRead(string? appId, DateTime lastPlayedUtc)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        string id = appId!.Trim();
        var cache = ReadCache(id);
        if (IsFresh(cache, lastPlayedUtc)) return cache;

        bool mine;
        lock (_flightGate) { mine = _inFlight.Add(id); }
        if (!mine) return cache;
        try
        {
            var fetched = Fetch(id);
            if (fetched != null) { WriteCache(id, fetched); return fetched; }
            return cache;   // both sources unavailable — keep whatever we cached
        }
        finally { lock (_flightGate) { _inFlight.Remove(id); } }
    }

    private readonly struct Def { public Def(string? n, string? d, string? ic, string? gr, bool h) { name = n; desc = d; icon = ic; gray = gr; hidden = h; } public readonly string? name, desc, icon, gray; public readonly bool hidden; }

    // ── orchestration: defs + rarity (web) + unlock state (web → helper) → StoreAchCache ───────
    private static StoreAchCache? Fetch(string appId)
    {
        // One deadline for the WHOLE web run so the sequential calls can never add up to a long freeze.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;
        var (defs, order, schemaOk) = FetchSchema(appId, ct);   // full achievement set + icons (web, key)
        // The game genuinely has no Steam achievements (schema returned, but empty) → resolve to an empty
        // card at once. Crucially, do NOT fall through to the Steamworks helper (it would spawn a process
        // and sit for seconds only to confirm 0 — the "stuck on loading" symptom).
        if (schemaOk && defs.Count == 0)
        {
            Console.WriteLine($"[steamach] {appId}: no achievements (schema empty)");
            return new StoreAchCache { store = "Steam", appId = appId, ver = CacheVer, fetchedAt = DateTime.UtcNow.ToString("o"), total = 0 };
        }
        var rarity = FetchGlobalPercents(appId, ct);     // rarity % (web, no key)

        // Unlock state: WEB first (Steam-closed OK), then the Steamworks helper (Steam running).
        var unlocks = FetchWebUnlocks(appId, ct);        // apiname → (achieved, unlockTime, name)
        string source = "web";
        if (unlocks == null && SteamRunning())
        {
            var res = SteamHelper.Query(appId);
            if (res != null && res.ok)
            {
                unlocks = new Dictionary<string, (bool, long, string?)>(StringComparer.Ordinal);
                // Seed defs/order from the helper ONLY when the web schema gave us nothing at all - decided
                // once, before the loop. Re-checking defs.Count inside the loop would only ever be true for
                // the very first achievement (since adding it makes defs non-empty), silently dropping every
                // achievement after the first from "order" (and therefore from the rendered card).
                bool seedDefsFromHelper = defs.Count == 0;
                foreach (var a in res.achievements)
                {
                    var apiname = a.id ?? "";
                    if (apiname.Length == 0) continue;
                    unlocks[apiname] = (a.unlocked, a.unlockTime, a.name);
                    if (seedDefsFromHelper) { defs[apiname] = new Def(a.name, a.desc, null, null, a.hidden); order.Add(apiname); }
                }
                source = "helper";
            }
        }
        if (unlocks == null) { Console.WriteLine($"[steamach] {appId}: no source (web blocked, Steam off)"); return null; }

        var keys = order.Count > 0 ? order : unlocks.Keys.ToList();
        var c = new StoreAchCache
        {
            store = "Steam", appId = appId, ver = CacheVer, fetchedAt = DateTime.UtcNow.ToString("o"),
        };
        int ord = 0;
        foreach (var apiname in keys)
        {
            defs.TryGetValue(apiname, out var def);
            unlocks.TryGetValue(apiname, out var un);
            rarity.TryGetValue(apiname, out var pct);
            c.achievements.Add(new StoreAch
            {
                id = apiname,
                title = def.name ?? un.Item3 ?? apiname,
                description = (def.hidden && !un.Item1) ? null : def.desc,
                unlocked = un.Item1,
                unlockedAt = un.Item2 > 0 ? DateTimeOffset.FromUnixTimeSeconds(un.Item2).UtcDateTime.ToString("o") : null,
                rarity = pct,
                badgeUnlocked = def.icon,
                badgeLocked = def.gray ?? def.icon,
                order = ord++,
            });
        }
        c.total = c.achievements.Count;
        c.unlocked = c.achievements.Count(a => a.unlocked);
        Console.WriteLine($"[steamach] {appId}: {c.unlocked}/{c.total} via {source}");
        return c;
    }

    // GetPlayerAchievements → apiname → (achieved, unlockTime, name). null when unavailable (403 = Game
    // details not public, or no key/steamid) → caller falls back to the helper.
    private static Dictionary<string, (bool, long, string?)>? FetchWebUnlocks(string appId, CancellationToken ct)
    {
        var key = ApiKey(); var sid = SteamId();
        if (key == null || string.IsNullOrEmpty(sid)) return null;
        try
        {
            string url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v0001/?appid={appId}&key={key}&steamid={sid}&l={Lang()}";
            var body = GetJson(url, ct);
            if (body == null) return null;   // 403 (not public) / timeout / error → fall back to the helper
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("playerstats", out var ps)) return null;
            if (!ps.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            var map = new Dictionary<string, (bool, long, string?)>(StringComparer.Ordinal);
            if (ps.TryGetProperty("achievements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    string nm = Str(e, "apiname");
                    if (nm.Length == 0) continue;
                    bool ach = e.TryGetProperty("achieved", out var a) && (a.ValueKind == JsonValueKind.Number ? a.GetInt32() != 0 : a.GetBoolean());
                    long t = e.TryGetProperty("unlocktime", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0;
                    map[nm] = (ach, t, NullIfEmpty(Str(e, "name")));
                }
            return map;
        }
        catch (Exception ex) { Console.WriteLine($"[steamach] player {appId} failed: {ex.Message}"); return null; }
    }

    // GetSchemaForGame → full achievement definitions (name/desc/icon/icongray/hidden), preserving order.
    // `ok` = the request succeeded (HTTP 200); with ok && defs.Count==0 the game simply has no achievements.
    private static (Dictionary<string, Def> defs, List<string> order, bool ok) FetchSchema(string appId, CancellationToken ct)
    {
        var defs = new Dictionary<string, Def>(StringComparer.Ordinal);
        var order = new List<string>();
        var key = ApiKey();
        if (key == null) return (defs, order, false);
        try
        {
            string url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={key}&appid={appId}&l={Lang()}";
            var body = GetJson(url, ct);
            if (body == null) return (defs, order, false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("game", out var game)
                && game.TryGetProperty("availableGameStats", out var ags)
                && ags.TryGetProperty("achievements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    string nm = Str(e, "name");
                    if (nm.Length == 0 || defs.ContainsKey(nm)) continue;
                    bool hidden = e.TryGetProperty("hidden", out var h) && (h.ValueKind == JsonValueKind.Number ? h.GetInt32() != 0 : h.ValueKind == JsonValueKind.True);
                    defs[nm] = new Def(NullIfEmpty(Str(e, "displayName")), NullIfEmpty(Str(e, "description")),
                                       NullIfEmpty(Str(e, "icon")), NullIfEmpty(Str(e, "icongray")), hidden);
                    order.Add(nm);
                }
            return (defs, order, true);   // parsed OK (defs may be empty = no achievements)
        }
        catch (Exception ex) { Console.WriteLine($"[steamach] schema {appId} failed: {ex.Message}"); return (defs, order, false); }
    }

    // GetGlobalAchievementPercentagesForApp → rarity % per achievement (no key required).
    private static Dictionary<string, double> FetchGlobalPercents(string appId, CancellationToken ct)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            string url = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}";
            var body = GetJson(url, ct);
            if (body == null) return map;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("achievementpercentages", out var ap)
                && ap.TryGetProperty("achievements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    string nm = Str(e, "name");
                    if (nm.Length == 0) continue;
                    if (e.TryGetProperty("percent", out var p))
                        map[nm] = p.ValueKind == JsonValueKind.Number ? p.GetDouble()
                                : double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                }
        }
        catch (Exception ex) { Console.WriteLine($"[steamach] percents {appId} failed: {ex.Message}"); }
        return map;
    }

    /// <summary>Is the Steam client running? Only the helper path needs it (the web path works Steam-closed).</summary>
    private static bool SteamRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
