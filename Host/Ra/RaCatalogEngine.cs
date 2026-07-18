// RetroAchievements catalogue ENGINE — P1 of the RA-engine migration: faithful port of the
// plugin's RaCatalog + RaRefreshScheduler + the refresh half of RaGameSync, on top of RaStore.
//
// Behavior kept from the plugin:
//   • Heartbeat: one background timer, first tick +20s then every 30 min; each tick bails unless
//     the box is idle (no game running, no extraction in flight); then
//     refreshes only the consoles DUE per their stored schedule.
//   • Schedule: success → now + 20h + random(0..8h, 30-min steps); failure/empty/rejected →
//     back-off now + 2h + random(0..1h) WITHOUT touching games_refreshed_at (the UI keeps showing
//     the last successful pull).
//   • Apply guards before the destructive per-console replace: HTTP/parse error rejected; empty
//     list rejected; count-delta (fresh < 0.8 × known) rejected as truncation.
//   • CANARY gate (RaCanaries): a fresh pull is "genuine" when one frozen hash→raid pair is
//     present. Genuineness authorizes the DROP side of the IGame sync — activation (adding a
//     raid) is never gated. Consoles without canaries never drop (safe default).
//   • After an applied refresh: ReResolveScannedIds (re-link stored hashes, no re-hashing) then
//     the IGame sync — activate new/changed raids, drop dead ones only under the canary gate.
//
// Migration: RaStore.ImportJsonCatalog folds an existing lite-era catalog-<id>.json into the store
// before a console's first pull; once a REAL pull is applied, the legacy JSON is deleted (the store
// is the only catalogue since the lite reader was removed).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using LbApiHost.Host.Modules;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaCatalogEngine
{
    private static readonly TimeSpan FirstTick = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = BuildClient();
    private static HttpClient BuildClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
        return c;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly Random Rng = new();
    private static System.Threading.Timer? _timer;
    private static int _ticking;    // re-entrancy guard on the tick
    private static int _running;    // one catalogue run at a time (manual + scheduled share it)

    // ── Scheduler ───────────────────────────────────────────────────────────────

    /// <summary>Arm the heartbeat (idempotent). Call once at boot after the module system is up.</summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new System.Threading.Timer(_ => Tick(), null, FirstTick, TickPeriod);
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (Web.RecentState.IsGameRunning || Web.RecentState.IsExtractionInProgress) return;
            RaTokenRenew.MaybeRenewAsync(RaTokenRenew.AllowWrite);   // renew the RA session token if it's due (cheap, single-flight)
            RefreshDue();
        }
        catch (Exception ex) { Log("tick failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>Refresh every enabled+mapped console whose schedule says it is due.</summary>
    public static void RefreshDue()
    {
        var due = RaStore.FilterDueConsoles(EnabledConsoleIds(), DateTime.UtcNow);
        foreach (var id in due) RefreshOne(id);
    }

    /// <summary>Distinct RA console ids of the RA-enabled, RA-mapped LB platforms.</summary>
    public static List<int> EnabledConsoleIds()
    {
        var ids = new List<int>();
        try
        {
            var seen = new HashSet<int>();
            foreach (var p in PluginHelper.DataManager.GetAllPlatforms())
            {
                string name; try { name = p.Name; } catch { continue; }
                if (string.IsNullOrEmpty(name) || !RaPlatformState.IsPlatformEnabled(name)) continue;
                var cid = RaPlatformMap.ConsoleIdFor(name);
                if (cid != null && seen.Add(cid.Value)) ids.Add(cid.Value);
            }
        }
        catch (Exception ex) { Log("EnabledConsoleIds failed: " + ex.Message); }
        return ids;
    }

    // ── One console refresh (guards + apply + sync) ─────────────────────────────

    /// <summary>Pull + guard + apply one console's catalogue, then re-link and sync IGames.
    /// Returns true when a fresh catalogue was APPLIED (guard rejections return false).</summary>
    public static bool RefreshOne(int consoleId)
    {
        if (consoleId <= 0) return false;
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;   // single-flight
        try
        {
            RaStore.ImportJsonCatalog(consoleId);   // migration bridging (no-op once a catalogue exists)

            var pull = FetchGames(consoleId);
            var now = DateTime.UtcNow;
            if (pull == null)                        // HTTP / parse error
            {
                RaStore.SetConsoleNextRefresh(consoleId, Backoff(now));
                return false;
            }
            if (pull.Count == 0)                     // empty result — never applied
            {
                Log($"console {consoleId}: empty list → back off, keep catalogue.");
                RaStore.SetConsoleNextRefresh(consoleId, Backoff(now));
                return false;
            }
            int known = RaStore.CatalogueCount(consoleId);
            if (known > 0 && pull.Count < known * 0.8)   // truncation guard
            {
                Log($"console {consoleId}: {pull.Count} games vs {known} known (<0.8×) → rejected as truncation.");
                RaStore.SetConsoleNextRefresh(consoleId, Backoff(now));
                return false;
            }

            bool allowDrop = RaStore.CanaryPresent(consoleId, pull);
            RaStore.ReplaceConsoleCatalog(consoleId, pull, now, NextSuccess(now));
            RaStore.ReResolveScannedIds();
            DeleteLegacyJson(consoleId);             // the store owns the catalogue now
            SyncIGamesAfterRefresh(consoleId, allowDrop);
            Log($"console {consoleId}: applied {pull.Count} games (drop {(allowDrop ? "allowed" : "blocked — no canary")}).");
            return true;
        }
        catch (Exception ex) { Log($"RefreshOne({consoleId}) failed: " + ex.Message); return false; }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static DateTime NextSuccess(DateTime now)
        => now + TimeSpan.FromHours(RaPanelConfig.RefreshHours)                          // base (default 20h, panel option)
             + TimeSpan.FromMinutes(30 * Rng.Next(0, 17));                               // +0..8h jitter, 30-min steps

    private static DateTime Backoff(DateTime now)
        => now + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(Rng.Next(0, 61));         // +2h..3h

    /// <summary>IGame sync for every game of the platforms mapped to this console: stored hash →
    /// current raid; ACTIVATE on change; DROP (clear the id) only when <paramref name="allowDrop"/>
    /// (canary-verified pull) and the hash no longer maps. Fields via ILiteBoxFields (auto-persisted).</summary>
    private static void SyncIGamesAfterRefresh(int consoleId, bool allowDrop)
    {
        try
        {
            int activated = 0, dropped = 0;
            foreach (var g in PluginHelper.DataManager.GetAllGames())
            {
                string plat; try { plat = g.Platform ?? ""; } catch { continue; }
                if (plat.Length == 0 || RaPlatformMap.ConsoleIdFor(plat) != consoleId) continue;
                if (g is not ILiteBoxFields f) continue;

                string hash = f.GetField("RetroAchievementsHash") ?? "";
                if (hash.Length == 0 || hash.Equals("COULDNTFILEHASH", StringComparison.OrdinalIgnoreCase)) continue;

                int newRaid = RaStore.LookupRaid(hash);
                string cur = f.GetField("RetroAchievementsId") ?? "";
                if (newRaid > 0)
                {
                    if (cur != newRaid.ToString()) { f.SetField("RetroAchievementsId", newRaid.ToString()); activated++; }
                }
                else if (cur.Length > 0 && allowDrop)
                {
                    f.SetField("RetroAchievementsId", "");
                    dropped++;
                }
            }
            if (activated > 0 || dropped > 0)
                Log($"console {consoleId}: IGame sync — {activated} activated, {dropped} dropped.");
        }
        catch (Exception ex) { Log("SyncIGamesAfterRefresh failed: " + ex.Message); }
    }

    /// <summary>Delete the lite-era per-console JSON after a real pull was applied — its content was
    /// folded in by ImportJsonCatalog and the store is now authoritative (no reader left).</summary>
    private static void DeleteLegacyJson(int consoleId)
    {
        try
        {
            string file = Path.Combine(LiteBoxPaths.Dir("ra-cache"), $"catalog-{consoleId}.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) { Log("DeleteLegacyJson failed: " + ex.Message); }
    }

    // ── Fetch (full rows) ───────────────────────────────────────────────────────

    /// <summary>GET API_GetGameList (h=1 hashes, f=1 achievements-only) → full catalogue rows.
    /// Null on HTTP error / non-JSON / missing key (guard: the caller backs off, keeps data).</summary>
    private static List<RaGameRow>? FetchGames(int consoleId)
    {
        var key = RaService.ApiKey;
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            string url = "https://retroachievements.org/API/API_GetGameList.php"
                       + $"?i={consoleId}&h=1&f=1&y={Uri.EscapeDataString(key!)}";
            string body;
            using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) { Log($"console {consoleId}: HTTP {(int)resp.StatusCode}."); return null; }
                body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            if (string.IsNullOrWhiteSpace(body)) return new List<RaGameRow>();

            List<ApiGame>? games;
            try { games = JsonSerializer.Deserialize<List<ApiGame>>(body, JsonOpts); }
            catch (JsonException) { Log($"console {consoleId}: body is not a JSON array."); return null; }
            if (games == null) return new List<RaGameRow>();

            var rows = new List<RaGameRow>(games.Count);
            foreach (var g in games)
            {
                if (g == null || g.ID <= 0) continue;
                var row = new RaGameRow
                {
                    Id = g.ID,
                    Title = g.Title,
                    ConsoleName = g.ConsoleName,
                    ImageIcon = g.ImageIcon,
                    NumAchievements = g.NumAchievements,
                    NumLeaderboards = g.NumLeaderboards,
                    Points = g.Points,
                    DateModified = g.DateModified,
                    ForumTopicId = g.ForumTopicID,
                };
                if (g.Hashes != null)
                    foreach (var h in g.Hashes)
                        if (!string.IsNullOrWhiteSpace(h)) row.Hashes.Add(h.Trim());
                rows.Add(row);
            }
            return rows;
        }
        catch (Exception ex) { Log($"console {consoleId}: fetch failed: {ex.Message}."); return null; }
    }

    private sealed class ApiGame
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public string? ConsoleName { get; set; }
        public string? ImageIcon { get; set; }
        public int NumAchievements { get; set; }
        public int NumLeaderboards { get; set; }
        public int Points { get; set; }
        public string? DateModified { get; set; }
        public int? ForumTopicID { get; set; }
        public List<string>? Hashes { get; set; }
    }

    private static void Log(string msg) => Diag.LbLog.Info("ra", "catalog: " + msg);
}
