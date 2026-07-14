// Steam media for a game, straight from Steam's public Store API (no key, no login):
//   GET https://store.steampowered.com/api/appdetails?appids={appid}&l=english
//
// The response carries header_image / capsule_image / background, the full screenshot list (path_full), and
// the trailers. Trailers now ship as HLS/DASH manifests (the old mp4/webm fields are null), but a DIRECT,
// downloadable mp4 is reconstructible from each movie's numeric id:
//   https://video.akamai.steamstatic.com/store_trailers/{movieId}/movie_max.mp4   (verified 200)
// The portrait box art and the clear logo aren't in appdetails at all — they're the constructed library assets
// (library_600x900_2x.jpg / logo.png), added by SteamCatalog.
//
// One call per game; cached in memory for the edit-window's lifetime and on disk (7 days) — the Store API is
// IP-rate-limited (~200 / 5 min), so a batch (the matrix) paces itself to ≥1s between live calls and, on a 429
// (or a transient 5xx / timeout), retries with exponential backoff honouring any Retry-After header.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host;

namespace LbApiHost.Host.Integrations;

internal static class SteamApi
{
    public const string CdnBase = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/";
    public const string TrailerBase = "https://video.akamai.steamstatic.com/store_trailers/";
    public const string Referer = "https://store.steampowered.com/";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);          // ≥1s between live calls (rate limit)
    private const int MaxAttempts = 4;                                        // 1 try + 3 retries on 429 / 5xx / timeout
    private static DateTime _nextSlotUtc = DateTime.MinValue;                 // reserved earliest time of the next call
    private static readonly object _paceLock = new();

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>The media fields we pull from appdetails (each already an absolute URL).</summary>
    public sealed record AppMedia(
        string? Header, string? Background, IReadOnlyList<string> Screenshots, IReadOnlyList<long> MovieIds);

    private static readonly Dictionary<string, AppMedia?> _mem = new(StringComparer.Ordinal);

    /// <summary>appdetails media for a Steam appid. Null on failure / unknown app. Cached (memory + disk).</summary>
    public static async Task<AppMedia?> GetAppMediaAsync(string appid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appid)) return null;
        lock (_mem) { if (_mem.TryGetValue(appid, out var hot)) return hot; }

        string? json = ReadFreshCache(appid);
        if (json == null)
        {
            json = await FetchLiveAsync(appid, ct).ConfigureAwait(false);
            if (json != null) WriteCache(appid, json);
        }
        var parsed = json != null ? Parse(appid, json) : null;
        lock (_mem) { _mem[appid] = parsed; }
        return parsed;
    }

    /// <summary>One live appdetails fetch, paced ≥1s from the previous call and retried with exponential backoff
    /// on 429 / transient 5xx / timeout (honouring Retry-After). Returns the raw JSON, or null when it gives up.
    /// A genuine cancellation (ct) propagates; an HttpClient timeout is treated as transient.</summary>
    private static async Task<string?> FetchLiveAsync(string appid, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appid}&l=english";
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await PaceAsync(ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                int code = (int)resp.StatusCode;
                bool transient = code == 429 || code >= 500;
                if (!transient) { Console.WriteLine($"[steam] appdetails({appid}) HTTP {code}"); return null; }
                if (attempt == MaxAttempts) { Console.WriteLine($"[steam] appdetails({appid}) HTTP {code} — giving up after {attempt} tries"); return null; }
                var back = RetryDelay(attempt, resp);
                Console.WriteLine($"[steam] appdetails({appid}) HTTP {code} — retry {attempt}/{MaxAttempts - 1} in {back.TotalSeconds:0.#}s");
                await Task.Delay(back, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // real cancel, not a timeout
            catch (Exception ex)
            {
                if (attempt == MaxAttempts) { Console.WriteLine($"[steam] appdetails({appid}) failed after {attempt} tries: {ex.Message}"); return null; }
                var back = RetryDelay(attempt, null);
                Console.WriteLine($"[steam] appdetails({appid}) error '{ex.Message}' — retry {attempt}/{MaxAttempts - 1} in {back.TotalSeconds:0.#}s");
                await Task.Delay(back, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    /// <summary>Backoff before the next attempt: Steam's Retry-After if present, else 2s, 4s, 8s.</summary>
    private static TimeSpan RetryDelay(int attempt, HttpResponseMessage? resp)
    {
        var ra = resp?.Headers.RetryAfter;
        if (ra != null)
        {
            if (ra.Delta is { } d && d > TimeSpan.Zero) return d;
            if (ra.Date is { } when) { var s = when - DateTimeOffset.UtcNow; if (s > TimeSpan.Zero) return s; }
        }
        return TimeSpan.FromSeconds(Math.Pow(2, attempt));   // attempt 1→2s, 2→4s, 3→8s
    }

    /// <summary>Reserve the next ≥1s call slot so concurrent/looping callers never fire faster than the rate limit.</summary>
    private static async Task PaceAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_paceLock)
        {
            var now = DateTime.UtcNow;
            var slot = _nextSlotUtc > now ? _nextSlotUtc : now;   // wait until the reserved slot
            wait = slot - now;
            _nextSlotUtc = slot + Pace;                            // reserve the following slot
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    private static AppMedia? Parse(string appid, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appid, out var node)) return null;
            if (!node.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            if (!node.TryGetProperty("data", out var d)) return null;

            string? Str(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var shots = new List<string>();
            if (d.TryGetProperty("screenshots", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var s in ss.EnumerateArray())
                    if (s.TryGetProperty("path_full", out var pf) && pf.GetString() is { } u) shots.Add(u);

            var movies = new List<long>();
            if (d.TryGetProperty("movies", out var mv) && mv.ValueKind == JsonValueKind.Array)
                foreach (var m in mv.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.TryGetInt64(out var mid)) movies.Add(mid);

            return new AppMedia(Str("header_image"), Str("background_raw") ?? Str("background"), shots, movies);
        }
        catch { return null; }
    }

    // ── Disk cache ──────────────────────────────────────────────────────────
    private static string? CachePath(string appid)
    {
        try { return Path.Combine(LiteBoxPaths.Dir("steam"), appid + ".json"); }
        catch { return null; }
    }
    private static string? ReadFreshCache(string appid)
    {
        try
        {
            var p = CachePath(appid);
            if (p == null || !File.Exists(p)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > CacheTtl) return null;
            var s = File.ReadAllText(p);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }
    private static void WriteCache(string appid, string json)
    {
        try { var p = CachePath(appid); if (p != null) File.WriteAllText(p, json); } catch { }
    }
}
