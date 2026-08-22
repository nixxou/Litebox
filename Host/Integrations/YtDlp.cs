// yt-dlp integration — a self-managed yt-dlp.exe under <LB>\Core\litebox\thirdparty, used by the video editor's
// YouTube source to SEARCH YouTube and DOWNLOAD videos. yt-dlp isn't bundled (its own licence + it self-updates
// weekly), so LiteBox fetches it on demand from the GitHub "latest" release and can refresh it in place.
//
// Nothing here streams: per the editor, a result opens in the browser (its watch URL) or is downloaded to disk.
// So the surface is small — a version probe, a search (ytsearchN → one JSON object per line), a single-URL probe
// (for a pasted link), and a download. Merged high-res formats need ffmpeg; we point yt-dlp at LiteBox's own
// ffmpeg (FfmpegService) when it's present, else fall back to a progressive format.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Video;

namespace LbApiHost.Host.Integrations;

internal static class YtDlp
{
    public const string ReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static string ExePath => Path.Combine(LiteBoxPaths.Dir("thirdparty"), "yt-dlp.exe");
    public static bool Available => File.Exists(ExePath);

    /// <summary>Which browser's cookies to hand yt-dlp (age-gated / region-locked / members-only videos).</summary>
    public enum CookieBrowser { None, Firefox, Chrome, Edge, Brave, Opera, Vivaldi }

    private static string? CookieArg(CookieBrowser b) => b switch
    {
        CookieBrowser.Firefox => "firefox",
        CookieBrowser.Chrome  => "chrome",
        CookieBrowser.Edge    => "edge",
        CookieBrowser.Brave   => "brave",
        CookieBrowser.Opera   => "opera",
        CookieBrowser.Vivaldi => "vivaldi",
        _ => null,
    };

    // ── Bootstrap (download / update the binary) ───────────────────────────────
    private static readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = true })
    { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Download yt-dlp.exe if it isn't already there. Returns true when the binary is present afterward.</summary>
    public static async Task<bool> EnsureAsync(CancellationToken ct = default)
        => Available || await DownloadBinaryAsync(ct).ConfigureAwait(false);

    /// <summary>(Re)download the latest yt-dlp.exe, replacing any existing one.</summary>
    public static Task<bool> UpdateAsync(CancellationToken ct = default) => DownloadBinaryAsync(ct);

    private static async Task<bool> DownloadBinaryAsync(CancellationToken ct)
    {
        try
        {
            var dest = ExePath;
            var tmp = dest + ".download";
            using (var req = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl))
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) { Console.WriteLine($"[yt-dlp] download HTTP {(int)resp.StatusCode}"); return false; }
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            File.Move(tmp, dest);
            return File.Exists(dest);
        }
        catch (Exception ex) { Console.WriteLine("[yt-dlp] download failed: " + ex.Message); return false; }
    }

    /// <summary>"yt-dlp --version" (e.g. "2024.08.06"), or null when it isn't installed / can't run.</summary>
    public static string? Version()
    {
        if (!Available) return null;
        try
        {
            var (code, stdout, _) = RunAsync(new[] { "--version" }, CancellationToken.None).GetAwaiter().GetResult();
            var v = stdout.Trim();
            return code == 0 && v.Length > 0 ? v.Split('\n')[0].Trim() : null;
        }
        catch { return null; }
    }

    // ── Search ─────────────────────────────────────────────────────────────────
    public sealed record Result(string Id, string Title, string Uploader, int DurationSec, string ThumbUrl, string WatchUrl);

    public static string WatchUrl(string id) => $"https://www.youtube.com/watch?v={id}";
    public static string ThumbUrl(string id) => $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";

    // Session cache: a search / probe is re-run for the SAME game every time you (re)open its video page or click
    // its matrix cell, so results are memoised (keyed by the query text / id-set + cookies). Non-empty only.
    private static readonly Dictionary<string, List<Result>> _resultCache = new(StringComparer.Ordinal);
    private static readonly object _resultLock = new();

    private static List<Result>? CacheGet(string key)
    {
        lock (_resultLock) return _resultCache.TryGetValue(key, out var hit) ? new List<Result>(hit) : null;
    }
    private static List<Result> CachePut(string key, List<Result> res)
    {
        if (res.Count > 0) lock (_resultLock) _resultCache[key] = new List<Result>(res);
        return res;
    }

    /// <summary>Top <paramref name="max"/> YouTube matches for a text query (via ytsearch). Cached. Empty on failure.</summary>
    public static async Task<List<Result>> SearchAsync(string query, int max, CookieBrowser cookies, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<Result>();
        int n = Math.Clamp(max, 1, 50);
        string key = $"s|{n}|{cookies}|{query.Trim()}";
        if (CacheGet(key) is { } hot) return hot;
        return CachePut(key, await QueryJsonAsync(new[] { $"ytsearch{n}:{query.Trim()}" }, flat: true, cookies, ct).ConfigureAwait(false));
    }

    /// <summary>Metadata for a single pasted YouTube URL (or id) — a one-element list, empty on failure. Cached.</summary>
    public static async Task<List<Result>> ProbeUrlAsync(string url, CookieBrowser cookies, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return new List<Result>();
        string key = $"u|{cookies}|{url.Trim()}";
        if (CacheGet(key) is { } hot) return hot;
        return CachePut(key, await QueryJsonAsync(new[] { url.Trim() }, flat: false, cookies, ct, noPlaylist: true).ConfigureAwait(false));
    }

    /// <summary>Metadata for a set of video ids in ONE yt-dlp call (order preserved) — for the priority items
    /// (the game's Video URL, GOG trailers). Cached. Empty on failure.</summary>
    public static async Task<List<Result>> ProbeIdsAsync(IReadOnlyList<string> ids, CookieBrowser cookies, CancellationToken ct = default)
    {
        if (ids == null || ids.Count == 0) return new List<Result>();
        string key = $"p|{cookies}|{string.Join(",", ids)}";
        if (CacheGet(key) is { } hot) return hot;
        var inputs = new List<string>(ids.Count);
        foreach (var id in ids) inputs.Add(WatchUrl(id));
        return CachePut(key, await QueryJsonAsync(inputs, flat: false, cookies, ct).ConfigureAwait(false));
    }

    /// <param name="noPlaylist">Probe ONE video even when the URL also names a playlist — a watch link copied
    /// from the browser usually carries the list it was played from, and the box that accepted it asked for that
    /// video, not for its neighbours. Never set for a search: "ytsearch12:" IS a playlist, and this would cut it
    /// down to a single hit.</param>
    private static async Task<List<Result>> QueryJsonAsync(IReadOnlyList<string> inputs, bool flat, CookieBrowser cookies, CancellationToken ct, bool noPlaylist = false)
    {
        var list = new List<Result>();
        if (!Available || inputs.Count == 0) return list;
        var args = new List<string> { "--dump-json", "--no-warnings", "--ignore-errors" };
        if (noPlaylist) args.Add("--no-playlist");
        if (flat) args.Add("--flat-playlist");
        AddImpersonate(args);
        AddJsRuntime(args);
        AddCookies(args, cookies);
        args.AddRange(inputs);
        try
        {
            var (_, stdout, _) = await RunAsync(args, ct).ConfigureAwait(false);
            foreach (var line in stdout.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0 || s[0] != '{') continue;
                var r = Parse(s);
                if (r != null) list.Add(r);
            }
        }
        catch { }
        return list;
    }

    private static Result? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? id = Str(root, "id");
            if (string.IsNullOrEmpty(id)) return null;
            string title = Str(root, "title") ?? id!;
            string uploader = Str(root, "uploader") ?? Str(root, "channel") ?? "";
            int dur = 0;
            if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number) dur = (int)d.GetDouble();
            return new Result(id!, title, uploader, dur,
                $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                $"https://www.youtube.com/watch?v={id}");
        }
        catch { return null; }
    }

    private static string? Str(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Verify (availability + age gate) ───────────────────────────────────────
    // Flat search is fast but blind to whether a result is actually playable. This does a real extraction per id
    // (batched, one yt-dlp call, impersonation + cookies) to tell: Available = has a real video format (not just
    // storyboards / not "Video unavailable"); AgeRestricted = age_limit >= 18. Verdicts are cached per session.
    public readonly record struct Verdict(bool Available, bool AgeRestricted);

    private static readonly Dictionary<string, Verdict> _verdictCache = new(StringComparer.Ordinal);
    private static readonly object _verdictLock = new();

    /// <summary>Verdicts for the given video ids (from cache where known, else one batched extraction). Ids that
    /// produced no output are reported Available=false ONLY when the batch worked for others (else it's likely a
    /// transient throttle and they're omitted, so the caller keeps them).</summary>
    public static async Task<Dictionary<string, Verdict>> VerifyAsync(IReadOnlyList<string> ids, CookieBrowser cookies, CancellationToken ct = default)
    {
        var result = new Dictionary<string, Verdict>(StringComparer.Ordinal);
        if (!Available || ids == null || ids.Count == 0) return result;

        var todo = new List<string>();
        lock (_verdictLock)
            foreach (var id in ids)
                if (_verdictCache.TryGetValue(id, out var v)) result[id] = v; else if (!todo.Contains(id)) todo.Add(id);
        if (todo.Count == 0) return result;

        var args = new List<string> { "--dump-json", "--no-warnings", "--ignore-errors" };
        AddImpersonate(args);
        AddJsRuntime(args);
        AddCookies(args, cookies);
        foreach (var id in todo) args.Add(WatchUrl(id));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var (_, stdout, _) = await RunAsync(args, ct).ConfigureAwait(false);
            foreach (var line in stdout.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0 || s[0] != '{') continue;
                var (id, verdict) = ParseVerdict(s);
                if (id == null) continue;
                seen.Add(id);
                result[id] = verdict;
                lock (_verdictLock) _verdictCache[id] = verdict;
            }
        }
        catch { }

        // Only conclude "unavailable" for the missing ids when the batch clearly WORKED (some ids came back). If
        // nothing came back it's probably a throttle/network blip — leave those ids unverdicted, don't drop them.
        if (seen.Count > 0)
            foreach (var id in todo)
                if (!seen.Contains(id))
                {
                    var v = new Verdict(false, false);
                    result[id] = v;
                    lock (_verdictLock) _verdictCache[id] = v;
                }
        return result;
    }

    private static (string? id, Verdict verdict) ParseVerdict(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? id = Str(root, "id");
            if (id == null) return (null, default);
            bool age = root.TryGetProperty("age_limit", out var al) && al.ValueKind == JsonValueKind.Number && al.GetInt32() >= 18;
            bool hasVideo = false;
            if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
                foreach (var f in fmts.EnumerateArray())
                {
                    var vcodec = f.TryGetProperty("vcodec", out var vc) ? vc.GetString() : null;
                    var ext = f.TryGetProperty("ext", out var ex) ? ex.GetString() : null;
                    if (!string.IsNullOrEmpty(vcodec) && vcodec != "none" && ext != "mhtml") { hasVideo = true; break; }   // real video, not a storyboard
                }
            return (id, new Verdict(hasVideo, age));
        }
        catch { return (null, default); }
    }

    // ── Download ───────────────────────────────────────────────────────────────
    // Quality presets → yt-dlp format selectors. The "+ba" merge needs ffmpeg; when it isn't present we can only
    // deliver a progressive stream (single file, video+audio already muxed — capped ~720p by YouTube).
    public static string FormatSelector(string quality)
    {
        bool merge = FfmpegService.Available;   // ffmpeg.exe on disk; nothing here needs ffprobe any more
        int? cap = quality?.Trim().ToLowerInvariant() switch
        {
            "2160p" or "4k" => 2160,
            "1440p"         => 1440,
            "1080p"         => 1080,
            "720p"          => 720,
            "480p"          => 480,
            _               => (int?)null,   // "best"
        };
        // Each selector ends with an unconstrained fallback (…/bv*+ba/b, …/b) so a video that has no rendition at
        // the requested cap degrades to the best available instead of failing with "requested format not available".
        if (merge)
            return cap is int c ? $"bv*[height<={c}]+ba/b[height<={c}]/bv*+ba/b" : "bv*+ba/b";
        // No ffmpeg → progressive only (has audio); still prefer the cap, then fall back to any progressive.
        return cap is int c2 ? $"b[height<={c2}][acodec!=none][vcodec!=none]/b[height<={c2}]/b[acodec!=none][vcodec!=none]/b" : "b[acodec!=none][vcodec!=none]/b";
    }

    public sealed record DownloadOutcome(string? Path, string? Error);

    /// <summary>Download a video (by watch URL or id) to <paramref name="outFileNoExt"/>.&lt;ext&gt;. The exact
    /// final path comes from yt-dlp itself (--print after_move:filepath), so a name it sanitised still resolves;
    /// on failure the outcome carries yt-dlp's own last message so the UI can show WHY.</summary>
    /// <summary>Download with automatic fallbacks: the quality preset degrades to best-available (in the format
    /// selector), and if it fails on an auth/age gate while no cookies are configured, it retries once with the
    /// browser's cookies (Firefox — the reliable store) before giving up.</summary>
    public static async Task<DownloadOutcome> DownloadAsync(string urlOrId, string quality, string outFileNoExt, CookieBrowser cookies,
        IProgress<double>? progress = null, IProgress<string>? phase = null, CancellationToken ct = default)
    {
        var o = await DownloadOnce(urlOrId, quality, outFileNoExt, cookies, progress, phase, ct).ConfigureAwait(false);
        if (o.Path != null || ct.IsCancellationRequested) return o;

        if (cookies == CookieBrowser.None && LooksAuthGated(o.Error))
        {
            phase?.Report("Retrying with Firefox cookies…");
            var o2 = await DownloadOnce(urlOrId, quality, outFileNoExt, CookieBrowser.Firefox, progress, phase, ct).ConfigureAwait(false);
            if (o2.Path != null) return o2;
            // Firefox absent / no cookie DB → the retry's error is noise; keep the original (the real reason).
            return LooksNoCookieStore(o2.Error) ? o : o2;
        }

        // The symmetric case, and the surprising one: cookies can make YouTube serve LESS. A signed-in request
        // gets the real streams only if the client can produce a PO token, which yt-dlp cannot; without one the
        // answer is storyboards and nothing else, and the selector has nothing to match. Measured on one trailer:
        // 39 formats anonymously (32 video, 7 audio), 4 mhtml storyboards with a Firefox session. Whoever set
        // the cookie option did it to make downloads work, so failing here with "format is not available" is the
        // worst possible answer — drop the session and ask again as a stranger.
        if (cookies != CookieBrowser.None && LooksNoFormat(o.Error))
        {
            phase?.Report("Retrying without cookies…");
            var o3 = await DownloadOnce(urlOrId, quality, outFileNoExt, CookieBrowser.None, progress, phase, ct).ConfigureAwait(false);
            if (o3.Path != null) return o3;
            // Anonymous failed too: the original error is about the video, not about the session.
            return o;
        }
        return o;
    }

    private static bool LooksAuthGated(string? e) => e != null &&
        (e.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || e.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
      || e.Contains("members-only", StringComparison.OrdinalIgnoreCase) || e.Contains("private video", StringComparison.OrdinalIgnoreCase)
      || e.Contains("cookies", StringComparison.OrdinalIgnoreCase));

    /// <summary>yt-dlp matched no format — either the video really offers none we can use, or the session we
    /// asked with was served storyboards only.</summary>
    private static bool LooksNoFormat(string? e) => e != null &&
        e.Contains("format is not available", StringComparison.OrdinalIgnoreCase);

    private static bool LooksNoCookieStore(string? e) => e != null &&
        (e.Contains("could not find", StringComparison.OrdinalIgnoreCase) || e.Contains("does not support", StringComparison.OrdinalIgnoreCase)
      || e.Contains("no such", StringComparison.OrdinalIgnoreCase) || e.Contains("unable to", StringComparison.OrdinalIgnoreCase));

    private static async Task<DownloadOutcome> DownloadOnce(string urlOrId, string quality, string outFileNoExt, CookieBrowser cookies,
        IProgress<double>? progress, IProgress<string>? phase, CancellationToken ct)
    {
        if (!Available) return new DownloadOutcome(null, "yt-dlp isn't installed.");
        if (string.IsNullOrWhiteSpace(urlOrId)) return new DownloadOutcome(null, "No URL.");

        var args = new List<string>
        {
            // No --no-warnings on the DOWNLOAD, unlike the metadata calls: yt-dlp's warnings are where it says
            // it had to do without something, and swallowing them here hid the missing JS runtime for hours.
            // They land in the failure trace and nowhere else — the dialog still shows only the ERROR line.
            "--no-playlist", "--no-simulate", "--newline",
            "--print", "after_move:filepath",
            "-f", FormatSelector(quality),
            "--merge-output-format", "mp4",
            "-o", outFileNoExt + ".%(ext)s",
        };
        if (FfmpegService.FfmpegExe is { } fexe) { args.Add("--ffmpeg-location"); args.Add(fexe); }   // yt-dlp accepts the exe path
        AddImpersonate(args);
        AddJsRuntime(args);
        AddCookies(args, cookies);
        args.Add(urlOrId.Trim());

        // yt-dlp writes progress to stderr; --newline makes each update its own line so we can parse the percent.
        void OnErr(string line)
        {
            if (line.IndexOf("Merging formats", StringComparison.OrdinalIgnoreCase) >= 0 || line.StartsWith("[Merger]", StringComparison.OrdinalIgnoreCase))
            { phase?.Report("Merging…"); return; }
            var m = _pctRx.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                progress?.Report(Math.Clamp(p / 100.0, 0, 1));
        }

        try
        {
            var (code, stdout, stderr) = await RunAsync(args, ct, OnErr).ConfigureAwait(false);
            // Preferred: the path yt-dlp printed (last stdout line that is an existing file).
            string? made = null;
            foreach (var line in stdout.Split('\n')) { var s = line.Trim(); if (s.Length > 0 && File.Exists(s)) made = s; }
            // Fallback: glob the stem (skip in-progress intermediates like ".f399.mp4" / ".part").
            if (made == null)
            {
                var dir = Path.GetDirectoryName(outFileNoExt) ?? "";
                var stem = Path.GetFileName(outFileNoExt);
                try
                {
                    var exact = Path.Combine(dir, stem + ".mp4");
                    if (File.Exists(exact)) made = exact;
                    else foreach (var f in Directory.GetFiles(dir, stem + ".*"))
                        if (!f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) { made = f; break; }
                }
                catch { }
            }
            if (made != null) return new DownloadOutcome(made, null);
            string err = LastLine(stderr) ?? LastLine(stdout) ?? $"yt-dlp exited with code {code}.";
            Console.WriteLine("[yt-dlp] download failed: " + err);
            return new DownloadOutcome(null, err);
        }
        catch (OperationCanceledException) { return new DownloadOutcome(null, "cancelled"); }
        catch (Exception ex) { Console.WriteLine("[yt-dlp] download error: " + ex.Message); return new DownloadOutcome(null, ex.Message); }
    }

    /// <summary>The HLS MASTER manifest URL for a Steam app's first trailer — playable in libvlc (video+audio) and
    /// downloadable via yt-dlp. Needed because the reconstructed movie_max.mp4 is a dead 404 for newer HLS-only
    /// trailers. yt-dlp's Steam extractor gives a signed VARIANT url; the master sits beside it. Null on failure.</summary>
    public static async Task<string?> SteamTrailerMasterAsync(string appid, CancellationToken ct = default)
    {
        if (!Available || string.IsNullOrWhiteSpace(appid)) return null;
        var args = new List<string> { "-g", "--no-warnings", "--playlist-items", "1", "-f", "hls-2600/best[protocol*=m3u8]/best" };
        AddImpersonate(args);
        AddJsRuntime(args);
        args.Add($"https://store.steampowered.com/app/{appid.Trim()}");
        try
        {
            var (_, stdout, _) = await RunAsync(args, ct).ConfigureAwait(false);
            foreach (var line in stdout.Split('\n'))
            {
                var u = line.Trim();
                if (u.IndexOf("hls_264_", StringComparison.OrdinalIgnoreCase) >= 0 && u.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    return _hlsVariantRx.Replace(u, "hls_264_master.m3u8");   // variant → master (references video + audio)
            }
        }
        catch { }
        return null;
    }

    private static readonly Regex _hlsVariantRx = new(@"hls_264_\d+_(video|audio)\.m3u8", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? LastLine(string s)
    {
        string? last = null;
        foreach (var l in s.Split('\n')) { var t = l.Trim(); if (t.Length > 0) last = t; }
        return last;
    }

    private static readonly Regex _pctRx = new(@"(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

    private static void AddCookies(List<string> args, CookieBrowser cookies)
    {
        if (CookieArg(cookies) is { } c) { args.Add("--cookies-from-browser"); args.Add(c); }
    }

    // ── JavaScript runtime ────────────────────────────────────────────────────
    // YouTube now answers with a JS challenge, and yt-dlp needs an engine to solve it. Without one it says so
    // in a WARNING — which --no-warnings used to swallow, at the cost of several hours: "extraction without a
    // JS runtime has been deprecated, and some formats may be missing".
    //
    // Missing how much depends on who is asking, and the combination is what matters. Measured on one
    // age-restricted video: nothing at all and a JS runtime alone both stop at "Sign in to confirm your age";
    // cookies alone get past the age gate and then die on "format is not available", because a signed-in
    // request without a solved challenge is served storyboards and nothing else; cookies AND a runtime
    // download it. Cookies were making things look WORSE on their own, which is exactly how they got blamed.
    //
    // yt-dlp finds deno by itself; node and bun have to be named. We resolve the full path rather than trust
    // the process PATH, and probe once — the answer cannot change while LiteBox is up.
    private static string? _jsRuntime;
    private static bool _jsProbed;

    private static string? JsRuntime()
    {
        if (_jsProbed) return _jsRuntime;
        _jsProbed = true;
        foreach (var name in new[] { "deno", "node", "bun" })
        {
            if (OnPath(name + ".exe") is { } exe) { _jsRuntime = name + ":" + exe; break; }
        }
        LbLog.Info("ytdlp", "js runtime: " + (_jsRuntime ?? "(none — YouTube may withhold formats)"));
        return _jsRuntime;
    }

    private static string? OnPath(string exe)
    {
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string p;
                try { p = Path.Combine(dir.Trim(), exe); } catch { continue; }   // a malformed PATH entry
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    private static void AddJsRuntime(List<string> args)
    {
        if (JsRuntime() is { } r) { args.Add("--js-runtimes"); args.Add(r); }
    }

    // Browser impersonation (curl_cffi TLS fingerprint) — YouTube increasingly throttles / returns only
    // storyboards to the default client; posing as a real Chrome gets the real formats. Gated on the bundled
    // yt-dlp actually shipping curl_cffi (the official win64 build does; probed once and cached).
    private static bool? _impersonate;
    private static bool ImpersonateOk()
    {
        if (_impersonate is bool b) return b;
        bool ok = false;
        try
        {
            var (code, stdout, _) = RunAsync(new[] { "--list-impersonate-targets" }, CancellationToken.None).GetAwaiter().GetResult();
            ok = code == 0 && stdout.IndexOf("curl_cffi", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { }
        _impersonate = ok;
        return ok;
    }

    private static void AddImpersonate(List<string> args) { if (ImpersonateOk()) { args.Add("--impersonate"); args.Add("chrome"); } }

    // ── Process runner ─────────────────────────────────────────────────────────
    private static async Task<(int code, string stdout, string stderr)> RunAsync(IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStderr = null)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // What we ACTUALLY ran. Two rounds of debugging a failed download were spent guessing at this: the
        // dialog shows yt-dlp's last line and nothing else, so a failure that reproduces differently from a
        // hand-typed command — same video, a different error — leaves nowhere to look. Through LbLog, so a
        // normal launch writes nothing and only --debug pays for it. No secret is in here: the cookie flag
        // names a browser, it does not carry its cookies.
        LbLog.Info("ytdlp", "run: " + string.Join(" ", args));

        using var proc = new Process { StartInfo = psi };
        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) outBuf.Append(e.Data).Append('\n'); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) { errBuf.Append(e.Data).Append('\n'); onStderr?.Invoke(e.Data); } };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(true); } catch { } throw; }
        string err = errBuf.ToString();
        // On failure, everything yt-dlp said — the dialog keeps only the ERROR line, and the lines above it
        // are the ones that say WHICH player client answered and what it returned.
        if (proc.ExitCode != 0)
            LbLog.Info("ytdlp", "exit " + proc.ExitCode + Environment.NewLine + err.TrimEnd());
        return (proc.ExitCode, outBuf.ToString(), err);
    }
}
