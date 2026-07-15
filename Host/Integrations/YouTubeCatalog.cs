// Assembles the initial YouTube result list for a game, in the order the user asked for:
//   1. the game's own Video URL (IGame.VideoUrl), if it's a YouTube link
//   2. the GOG store trailers (api.gog.com/products/{id}?expand=videos → provider=="youtube"), if it has a GogAppId
//   3. a default text search ("{GameName} trailer") via yt-dlp
// deduped by video id, priority order preserved. The in-window search box replaces this list with a specific
// search; the "+" button appends one (a search term or a pasted URL). All of that goes back through YtDlp.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LbApiHost.Host.Integrations;

internal static class YouTubeCatalog
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = true })
    { Timeout = TimeSpan.FromSeconds(20) };

    // watch?v=ID / youtu.be/ID / embed/ID / shorts/ID / v/ID, or a bare 11-char id.
    private static readonly Regex _idRx = new(
        @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The YouTube video id in a URL (or a bare id), else null.</summary>
    public static string? ExtractId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();
        var m = _idRx.Match(s);
        if (m.Success) return m.Groups[1].Value;
        if (Regex.IsMatch(s, @"^[A-Za-z0-9_-]{11}$")) return s;   // already a bare id
        return null;
    }

    /// <summary>True when the string looks like a URL (so the "+" box probes it instead of searching for it).</summary>
    public static bool LooksLikeUrl(string? s)
        => !string.IsNullOrWhiteSpace(s) && (s.Contains("://") || s.Contains("youtu", StringComparison.OrdinalIgnoreCase));

    // Session cache: the GOG videos of a product don't change during an edit session.
    private static readonly Dictionary<string, List<string>> _gogCache = new(StringComparer.Ordinal);
    private static readonly object _gogLock = new();

    /// <summary>YouTube video ids from a GOG product's trailers (provider=="youtube"), or empty. Cached.</summary>
    public static async Task<List<string>> GogYouTubeIdsAsync(string? gogId, CancellationToken ct = default)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(gogId)) return ids;
        string gid = gogId.Trim();
        lock (_gogLock) { if (_gogCache.TryGetValue(gid, out var hit)) return new List<string>(hit); }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.gog.com/products/{gogId.Trim()}?expand=videos");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return ids;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("videos", out var vids) && vids.ValueKind == JsonValueKind.Array)
                foreach (var v in vids.EnumerateArray())
                {
                    var provider = v.TryGetProperty("provider", out var p) ? p.GetString() : null;
                    if (!string.Equals(provider, "youtube", StringComparison.OrdinalIgnoreCase)) continue;
                    var url = v.TryGetProperty("video_url", out var u) ? u.GetString() : null;
                    var id = ExtractId(url);
                    if (id != null && !ids.Contains(id)) ids.Add(id);
                }
            lock (_gogLock) _gogCache[gid] = new List<string>(ids);   // deterministic response → cache it (incl. "no videos")
        }
        catch { }
        return ids;
    }

    /// <summary>The ordered, deduped initial result set for a game: Video URL → GOG → each default search (in
    /// order). Multiple search lines let a game try, say, "{GameName} trailer" then "{AltName1} trailer".</summary>
    public static async Task<List<YtDlp.Result>> ResolveInitialAsync(
        string? videoUrl, string? gogId, IReadOnlyList<string> queries, int max, YtDlp.CookieBrowser cookies, CancellationToken ct = default)
    {
        // 1+2. Priority ids (game Video URL, then GOG trailers) — probed together for real titles/durations.
        var priorityIds = new List<string>();
        var fromMeta = ExtractId(videoUrl);
        if (fromMeta != null) priorityIds.Add(fromMeta);
        foreach (var gid in await GogYouTubeIdsAsync(gogId, ct).ConfigureAwait(false))
            if (!priorityIds.Contains(gid)) priorityIds.Add(gid);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<YtDlp.Result>();
        void Add(YtDlp.Result r) { if (r != null && seen.Add(r.Id)) results.Add(r); }

        if (priorityIds.Count > 0)
            foreach (var r in await YtDlp.ProbeIdsAsync(priorityIds, cookies, ct).ConfigureAwait(false)) Add(r);

        // 3. Default searches, in order.
        if (queries != null)
            foreach (var q in queries)
            {
                if (string.IsNullOrWhiteSpace(q)) continue;
                foreach (var r in await YtDlp.SearchAsync(q, max, cookies, ct).ConfigureAwait(false)) Add(r);
            }
        return results;
    }
}
