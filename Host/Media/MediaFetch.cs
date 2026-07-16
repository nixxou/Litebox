// Native, credential-aware media downloader for LiteBox's Extended-database module.
//
// This is a clean-room LiteBox reimplementation of the media-fetch logic that used to live only in the
// ExtendDB plugin (MediaApi + MediaSourcePolicy + ImageHelper + ScreenscraperApiBreaker). With it, LiteBox
// can download a game's web images / videos / manuals on its own — the plugin is no longer required for that.
//
// The model, kept faithful to the original design:
//   • Every DB row (MetadataDb.WebImage) carries an Origin (which CDN owns the canonical bytes) and a Type
//     (what the asset is). Type maps to a MediaContext; (context, origin) selects an ordered CHAIN of source
//     kinds to try. Each kind knows how to turn the row into a concrete (URL, Referer) pair. We walk the
//     chain and return the first upstream that answers 2xx.
//   • The ScreenScraper official API needs credentials. Those NEVER appear in source — the user account comes
//     from BaseCredentials.UserAccount() and the shipped developer credentials from BaseCredentials.DevCreds()
//     (an encrypted, gitignored .dat deployed with the build). When either is absent, the ScreenscraperApi
//     kind self-skips and the chain falls through to the credential-free sources.
//   • No Harmony, no HTTP intercept — LiteBox has no such machinery. A plain HttpClient does the work.
//
// Two public entry points:
//   • FetchBytes  — blocking; walks the chain and returns the first successful body (call it off the UI thread).
//   • ListUrls    — the non-fetching counterpart: the ordered upstream URLs, so a caller can e.g. rewrite a
//                   Steam ".m3u8.mp4" fake-mp4 into the real ".m3u8" and stream it instead of saving raw bytes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LbApiHost.Host.Media;

internal static class MediaFetch
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Vocabulary
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The URL recipes the fetcher knows how to emit. Each has a builder in <see cref="BuildUrl"/>.</summary>
    internal enum MediaSourceKind
    {
        /// <summary>{RemoteImageBaseUrl}/{permuted-md5}.{crc}.{ext} — the extenddb mirror.</summary>
        ExtenddbImage,
        /// <summary>{mirror host}/thumbs/{id}.jpg — id-based thumbnail.</summary>
        ExtenddbThumb,
        /// <summary>https://thumb.malkav.net/{id}.jpg — Cloudflare-fronted thumb CDN (403 = local blackout).</summary>
        MalkavThumb,
        /// <summary>https://images.launchbox-app.com/{file} (LB basename → CDN).</summary>
        LaunchboxCdn,
        /// <summary>https://gamesdb.launchbox-app.com/Handlers/ThumbnailRedirect.ashx?f={file}</summary>
        LaunchboxThumb,
        /// <summary>https://screenscraper.fr/image.php?... (public, resized).</summary>
        ScreenscraperPhp,
        /// <summary>https://screenscraper.fr/medias/{sys}/{jeu}/{media}.{filetype} (public, full-size).</summary>
        ScreenscraperMedia,
        /// <summary>Official neoclone.screenscraper.fr API with the user's + dev credentials injected. Creds-gated.</summary>
        ScreenscraperApi,
        /// <summary>DB FileName verbatim (already an absolute URL); Referer = {scheme}://{host}/.</summary>
        NormalCdn,
        /// <summary>Like NormalCdn but strips a trailing ".mp4" off a ".m3u8.mp4" to reach the real HLS manifest.</summary>
        SteamCdn,
    }

    /// <summary>What the bytes will be used for — drives which per-context chain we consult.</summary>
    internal enum MediaContext
    {
        Thumb,
        Cover,
        GalleryImage,
        Manual,
        Music,
        Video,
        OtherNonMedia,
    }

    /// <summary>Derive a <see cref="MediaContext"/> from the DB Type column. Image / unknown rows are galleries;
    /// the Cover distinction needs an out-of-band signal we don't have here.</summary>
    internal static MediaContext ContextFromType(string? dbType)
    {
        if (string.IsNullOrEmpty(dbType)) return MediaContext.GalleryImage;
        return dbType switch
        {
            "Manual" => MediaContext.Manual,
            "Music" => MediaContext.Music,
            "Video" or "VideoAdvert" => MediaContext.Video,
            "Press" or "Map" => MediaContext.OtherNonMedia,
            _ => MediaContext.GalleryImage,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Policy chains: (context, origin) → ordered kinds. "default" is the catch-all
    //  for any origin not explicitly listed (emumovies / igdb / vndb / …). Empty or
    //  "local" origin → empty chain (local disk is handled before any network call).
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<MediaContext, Dictionary<string, MediaSourceKind[]>> BuiltIn = Build();

    private static Dictionary<MediaContext, Dictionary<string, MediaSourceKind[]>> Build()
    {
        // The non-thumb image/media chains are identical per-origin today; they are kept as separate context
        // entries so a future tweak (e.g. Video preferring NormalCdn) touches only one line.
        Dictionary<string, MediaSourceKind[]> ImageLike() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["launchbox"] = new[] { MediaSourceKind.LaunchboxCdn, MediaSourceKind.ExtenddbImage },
            ["screenscraper"] = new[] { MediaSourceKind.ExtenddbImage, MediaSourceKind.ScreenscraperMedia },
            ["steam"] = new[] { MediaSourceKind.SteamCdn, MediaSourceKind.ExtenddbImage },
            ["default"] = new[] { MediaSourceKind.NormalCdn, MediaSourceKind.ExtenddbImage },
        };

        return new Dictionary<MediaContext, Dictionary<string, MediaSourceKind[]>>
        {
            // Thumbs lead with the dedicated Malkav CDN, then the extenddb thumb, then the per-origin fallback.
            [MediaContext.Thumb] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["launchbox"] = new[] { MediaSourceKind.MalkavThumb, MediaSourceKind.ExtenddbThumb, MediaSourceKind.LaunchboxThumb },
                ["screenscraper"] = new[] { MediaSourceKind.MalkavThumb, MediaSourceKind.ExtenddbThumb, MediaSourceKind.ScreenscraperPhp },
                ["steam"] = new[] { MediaSourceKind.MalkavThumb, MediaSourceKind.ExtenddbThumb, MediaSourceKind.SteamCdn },
                ["default"] = new[] { MediaSourceKind.MalkavThumb, MediaSourceKind.ExtenddbThumb, MediaSourceKind.NormalCdn },
            },

            [MediaContext.Cover] = ImageLike(),
            [MediaContext.GalleryImage] = ImageLike(),

            // Manual/screenscraper additionally leads with the credentialed API (best quality, quota-gated).
            [MediaContext.Manual] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["launchbox"] = new[] { MediaSourceKind.LaunchboxCdn, MediaSourceKind.ExtenddbImage },
                ["screenscraper"] = new[] { MediaSourceKind.ScreenscraperApi, MediaSourceKind.ExtenddbImage, MediaSourceKind.ScreenscraperMedia },
                ["steam"] = new[] { MediaSourceKind.SteamCdn, MediaSourceKind.ExtenddbImage },
                ["default"] = new[] { MediaSourceKind.NormalCdn, MediaSourceKind.ExtenddbImage },
            },

            [MediaContext.Music] = ImageLike(),
            [MediaContext.Video] = ImageLike(),
            [MediaContext.OtherNonMedia] = ImageLike(),
        };
    }

    /// <summary>Ordered chain of kinds to try for a (context, origin). "default" wins for unlisted origins;
    /// empty or "local" origin → empty chain.</summary>
    private static IReadOnlyList<MediaSourceKind> ChainFor(MediaContext ctx, string? originRaw)
    {
        var origin = (originRaw ?? "").Trim().ToLowerInvariant();
        if (origin.Length == 0 || origin == "local") return Array.Empty<MediaSourceKind>();

        if (!BuiltIn.TryGetValue(ctx, out var table)) table = BuiltIn[MediaContext.GalleryImage];
        if (table.TryGetValue(origin, out var chain)) return chain;
        return table.TryGetValue("default", out var fallback) ? fallback : Array.Empty<MediaSourceKind>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One ordered upstream candidate for a row (no fetch performed).</summary>
    public readonly record struct UrlCandidate(string Kind, string Url, string? Referer);

    /// <summary>
    /// Blocking fetch: walks the (context, origin) chain for <paramref name="w"/>, builds each candidate URL,
    /// GETs it with a plain HttpClient (setting Referer when the builder supplies one), and returns the body of
    /// the first 2xx response — or null when nothing succeeds. HLS manifests (.m3u8 / .m3u) are skipped (the raw
    /// manifest bytes are useless to save). Call off the UI thread.
    /// </summary>
    public static byte[]? FetchBytes(MetadataDb.WebImage w, string platform)
    {
        _ = platform; // URLs don't depend on the platform today; accepted for API symmetry / future use.
        if (string.IsNullOrEmpty(w.FileName)) return null;

        var ctx = ContextFromType(w.Type);
        int? dbId = w.DatabaseId > 0 ? w.DatabaseId : null;

        _gate.Wait();
        try
        {
            foreach (var kind in ChainFor(ctx, w.Origin))
            {
                var (url, referer) = BuildUrl(kind, w, dbId);
                if (string.IsNullOrEmpty(url)) continue;
                if (IsHlsManifestUrl(url)) continue;

                var (bytes, status, transportOk) = TryFetch(url, referer);

                // Per-kind circuit-breaker hooks (parity with the plugin).
                if (kind == MediaSourceKind.MalkavThumb && status == 403) MarkMalkavBlocked();
                if (kind == MediaSourceKind.ScreenscraperApi && IsBlackoutTrigger(status)) MarkScreenscraperBlocked();

                if (transportOk && bytes != null) return bytes;
            }
        }
        finally { _gate.Release(); }

        return null;
    }

    /// <summary>
    /// The ordered upstream URLs for a row WITHOUT fetching — the streaming counterpart of <see cref="FetchBytes"/>.
    /// Mirrors the plugin's ListDirectUrls: id-only kinds (ExtenddbThumb / MalkavThumb) are skipped, and
    /// ExtenddbImage is skipped when the row has no CRC (its URL is built from the CRC and would be malformed).
    /// </summary>
    public static List<UrlCandidate> ListUrls(MetadataDb.WebImage w)
    {
        var outList = new List<UrlCandidate>();
        if (string.IsNullOrEmpty(w.FileName)) return outList;

        var ctx = ContextFromType(w.Type);
        if (ctx == MediaContext.Thumb) return outList;

        foreach (var kind in ChainFor(ctx, w.Origin))
        {
            if (kind == MediaSourceKind.ExtenddbThumb || kind == MediaSourceKind.MalkavThumb) continue;
            if (kind == MediaSourceKind.ExtenddbImage && w.Crc32 == 0) continue;

            var (url, referer) = BuildUrl(kind, w, dbId: null);
            if (string.IsNullOrEmpty(url)) continue;
            outList.Add(new UrlCandidate(kind.ToString(), url!, referer));
        }
        return outList;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Per-kind URL builders
    // ─────────────────────────────────────────────────────────────────────────

    private static (string? Url, string? Referer) BuildUrl(MediaSourceKind kind, MetadataDb.WebImage w, int? dbId)
        => kind switch
        {
            MediaSourceKind.ExtenddbImage => BuildExtenddbImage(w),
            MediaSourceKind.ExtenddbThumb => BuildExtenddbThumb(dbId),
            MediaSourceKind.MalkavThumb => BuildMalkavThumb(dbId),
            MediaSourceKind.LaunchboxCdn => BuildLaunchboxCdn(w),
            MediaSourceKind.LaunchboxThumb => BuildLaunchboxThumb(w),
            MediaSourceKind.ScreenscraperPhp => BuildScreenscraperPhp(w),
            MediaSourceKind.ScreenscraperMedia => BuildScreenscraperMedia(w),
            MediaSourceKind.ScreenscraperApi => BuildScreenscraperApi(w),
            MediaSourceKind.NormalCdn => BuildNormalCdn(w),
            MediaSourceKind.SteamCdn => BuildSteamCdn(w),
            _ => (null, null),
        };

    private static (string?, string?) BuildExtenddbImage(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        return (ExtenddbImageUrl(w.FileName, w.Crc32), null); // the mirror doesn't gate on Referer
    }

    private static (string?, string?) BuildExtenddbThumb(int? dbId)
    {
        if (!dbId.HasValue) return (null, null);
        return ($"{MirrorThumbBase()}/thumbs/{dbId.Value}.jpg", null);
    }

    private static (string?, string?) BuildMalkavThumb(int? dbId)
    {
        if (!dbId.HasValue) return (null, null);
        if (IsMalkavBlocked()) return (null, null); // honour the local quota blackout
        return ($"https://thumb.malkav.net/{dbId.Value}.jpg", null);
    }

    private static (string?, string?) BuildLaunchboxCdn(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        string url = Uri.TryCreate(w.FileName, UriKind.Absolute, out _)
            ? w.FileName
            : "https://images.launchbox-app.com/" + w.FileName.TrimStart('/');
        return (url, "https://images.launchbox-app.com/");
    }

    private static (string?, string?) BuildLaunchboxThumb(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        var url = "https://gamesdb.launchbox-app.com/Handlers/ThumbnailRedirect.ashx?f="
                + Uri.EscapeDataString(w.FileName);
        return (url, "https://gamesdb.launchbox-app.com/");
    }

    private static (string?, string?) BuildScreenscraperPhp(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        if (!Uri.TryCreate(w.FileName, UriKind.Absolute, out var uri)) return (null, null);

        var systemeId = GetQueryParam(uri.Query, "systemeid");
        var jeuId = GetQueryParam(uri.Query, "jeuid");
        var media = GetQueryParam(uri.Query, "media") ?? "";
        if (string.IsNullOrEmpty(systemeId) || string.IsNullOrEmpty(jeuId)) return (null, null);

        // "sstitle(wor)" → media=sstitle, region=wor
        var mediaBase = media;
        var region = "";
        var m = Regex.Match(media, @"^(.*?)\((.*?)\)$");
        if (m.Success) { mediaBase = m.Groups[1].Value; region = m.Groups[2].Value; }

        var url = "https://screenscraper.fr/image.php"
                + $"?plateformid={Uri.EscapeDataString(systemeId)}"
                + $"&gameid={Uri.EscapeDataString(jeuId)}"
                + $"&media={Uri.EscapeDataString(mediaBase)}"
                + "&hd=0"
                + $"&region={Uri.EscapeDataString(region)}"
                + "&num="
                + "&version="
                + "&maxwidth=338"
                + "&maxheight=190";
        var referer = $"https://screenscraper.fr/gameinfos.php?plateforme={systemeId}&gameid={jeuId}";
        return (url, referer);
    }

    private static (string?, string?) BuildScreenscraperMedia(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        if (!Uri.TryCreate(w.FileName, UriKind.Absolute, out var uri)) return (null, null);

        var systemeId = GetQueryParam(uri.Query, "systemeid");
        var jeuId = GetQueryParam(uri.Query, "jeuid");
        var media = GetQueryParam(uri.Query, "media") ?? "";
        var fileType = GetQueryParam(uri.Query, "filetype") ?? "";
        if (string.IsNullOrEmpty(systemeId) || string.IsNullOrEmpty(jeuId)) return (null, null);
        if (string.IsNullOrEmpty(fileType)) fileType = "png";

        var url = $"https://screenscraper.fr/medias/{systemeId}/{jeuId}/{media}.{fileType}";
        var referer = $"https://screenscraper.fr/gameinfos.php?plateforme={systemeId}&gameid={jeuId}";
        return (url, referer);
    }

    /// <summary>
    /// The official screenscraper.fr API endpoint. Starts from the neoclone URL stored in the row, strips the
    /// useless filetype / lbname params, and injects the user's account (ssid / sspassword) plus the shipped
    /// developer credentials (devid / devpassword / softname). NO credential literal lives here: the user account
    /// comes from <see cref="BaseCredentials.UserAccount"/> and the dev credentials from
    /// <see cref="BaseCredentials.DevCreds"/>. When EITHER is null — or the quota breaker is in blackout — this
    /// kind self-skips (returns null) so the chain falls through to the credential-free sources.
    /// </summary>
    private static (string?, string?) BuildScreenscraperApi(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        if (!w.FileName.StartsWith("https://neoclone.screenscraper.fr", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var user = BaseCredentials.UserAccount();
        var dev = BaseCredentials.DevCreds();
        if (user == null || dev == null) return (null, null); // no credentials → skip this source entirely
        if (!ScreenscraperShouldAllow()) return (null, null);  // blackout / quota back-off

        try
        {
            var url = ReinjectParameters(
                w.FileName,
                remove: new[] { "filetype", "lbname" },
                inject: new[]
                {
                    ("ssid", user.Value.User),
                    ("sspassword", user.Value.Password),
                    ("devid", dev.Value.DevId),
                    ("devpassword", dev.Value.DevPassword),
                    ("softname", dev.Value.SoftName),
                });
            return (url, null); // the API gates on credentials, not Referer
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string?, string?) BuildNormalCdn(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        if (!Uri.TryCreate(w.FileName, UriKind.Absolute, out var u)) return (null, null);
        return (w.FileName, $"{u.Scheme}://{u.Host}/");
    }

    private static (string?, string?) BuildSteamCdn(MetadataDb.WebImage w)
    {
        if (string.IsNullOrEmpty(w.FileName)) return (null, null);
        var file = w.FileName;
        if (file.EndsWith(".m3u8.mp4", StringComparison.OrdinalIgnoreCase))
            file = file.Substring(0, file.Length - ".mp4".Length); // reach the real HLS manifest
        if (!Uri.TryCreate(file, UriKind.Absolute, out var u)) return (null, null);
        return (file, $"{u.Scheme}://{u.Host}/");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  extenddb mirror URL permutation (must match the CDN's nginx rewrite)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Base URL of the image mirror (path prefix included), from config. Trailing slash trimmed.</summary>
    private static string MirrorImageBase()
    {
        var v = BaseCredentials.RemoteImageBaseUrl();
        return string.IsNullOrWhiteSpace(v) ? "https://extenddb.com" : v.TrimEnd('/');
    }

    /// <summary>Thumb base — host portion (scheme + authority) of the image base only; thumbs live at /thumbs/.</summary>
    private static string MirrorThumbBase()
    {
        var raw = MirrorImageBase();
        try { var u = new Uri(raw); return $"{u.Scheme}://{u.Authority}"; }
        catch { return raw; }
    }

    /// <summary>
    /// Permuted mirror URL for a full-size image: {base}/{permuted32}.{crc}.{ext}. The 32-char MD5 of the
    /// lowercased filename is split into four 8-char blocks G1 G2 G3 G4 and re-emitted G3 G1 G4 G2; the tail is
    /// ".{crc-unsigned}.{ext}". The ext comes from the screenscraper `filetype` query param when the filename is
    /// a screenscraper URL, else Path.GetExtension, defaulting to "png". Purely obfuscation — the formula must
    /// stay in lockstep with the CDN's reverse-proxy rewrite.
    /// </summary>
    private static string? ExtenddbImageUrl(string fileName, long crc32)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var hash = Md5Hex((fileName ?? "").ToLowerInvariant());
        var crcUnsigned = (uint)crc32;
        var ext = ExtractExt(fileName!);
        var s = $"{hash}.{crcUnsigned}.{ext}"; // s = G1G2G3G4.{crc}.{ext}
        if (s.Length < 32) return null;
        return $"{MirrorImageBase()}/{s[16..24]}{s[0..8]}{s[24..32]}{s[8..16]}{s[32..]}";
    }

    private static string ExtractExt(string fileName)
    {
        string ext;
        if (!string.IsNullOrEmpty(fileName)
            && fileName.Contains("screenscraper.fr", StringComparison.OrdinalIgnoreCase))
        {
            ext = (GetQueryParam(SplitQuery(fileName), "filetype") ?? "").ToLowerInvariant();
        }
        else
        {
            ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        }
        return string.IsNullOrEmpty(ext) ? "png" : ext;
    }

    private static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Query helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the "?..." portion of a URL, or "" when there is none.</summary>
    private static string SplitQuery(string url)
    {
        int q = url.IndexOf('?');
        return q < 0 ? "" : url.Substring(q);
    }

    /// <summary>Reads a single query parameter (case-insensitive, URL-decoded), or null when absent.</summary>
    private static string? GetQueryParam(string queryString, string key)
    {
        if (string.IsNullOrEmpty(queryString)) return null;
        var s = queryString.StartsWith("?") ? queryString.Substring(1) : queryString;
        foreach (var pair in s.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string k = eq < 0 ? pair : pair.Substring(0, eq);
            string v = eq < 0 ? "" : pair.Substring(eq + 1);
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            try { return Uri.UnescapeDataString(v.Replace('+', ' ')); }
            catch { return v; }
        }
        return null;
    }

    /// <summary>
    /// Rebuilds <paramref name="url"/> with the named params removed and the injected (key,value) pairs added
    /// first. Existing params (minus the removed / re-injected keys) are preserved in order. Values are
    /// URL-escaped; keys are emitted verbatim.
    /// </summary>
    private static string ReinjectParameters(string url, string[] remove, (string Key, string Value)[] inject)
    {
        var uri = new Uri(url);
        var basePart = uri.GetLeftPart(UriPartial.Path);

        var removeSet = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
        var injectKeys = new HashSet<string>(inject.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

        var final = new List<(string Key, string Value)>();
        foreach (var (k, v) in inject) final.Add((k, v));

        var q = uri.Query.StartsWith("?") ? uri.Query.Substring(1) : uri.Query;
        foreach (var pair in q.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string k = eq < 0 ? pair : pair.Substring(0, eq);
            string rawV = eq < 0 ? "" : pair.Substring(eq + 1);
            if (removeSet.Contains(k) || injectKeys.Contains(k)) continue;
            string v;
            try { v = Uri.UnescapeDataString(rawV.Replace('+', ' ')); }
            catch { v = rawV; }
            final.Add((k, v));
        }

        var query = string.Join("&", final.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return query.Length == 0 ? basePart : $"{basePart}?{query}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  HTTP
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Concurrency gate: caps in-flight upstream fetches at 6.</summary>
    private static readonly SemaphoreSlim _gate = new(6, 6);

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    /// <summary>~30s per-request timeout via a linked CTS.</summary>
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Single GET. Returns (body, httpStatus, transportOk). body/status are null/0 on transport failure.</summary>
    private static (byte[]? Bytes, int Status, bool TransportOk) TryFetch(string url, string? referer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            req.Headers.Referrer = refUri;

        using var cts = new CancellationTokenSource(PerRequestTimeout);
        try
        {
            using var resp = _http
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .GetAwaiter().GetResult();

            int status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode) return (null, status, true);

            var body = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return (body, status, true);
        }
        catch
        {
            return (null, 0, false);
        }
    }

    private static bool IsHlsManifestUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        int cut = url!.IndexOf('?');
        var path = cut >= 0 ? url.Substring(0, cut) : url;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  In-memory circuit breakers (Malkav thumb CDN + ScreenScraper API quota)
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan BlackoutDuration = TimeSpan.FromHours(1);
    private static readonly object _breakerLock = new();

    private static DateTime _malkavBlockedUntilUtc = DateTime.MinValue;
    private static DateTime _ssBlockedUntilUtc = DateTime.MinValue;

    private static bool IsMalkavBlocked()
    {
        lock (_breakerLock) return DateTime.UtcNow < _malkavBlockedUntilUtc;
    }

    private static void MarkMalkavBlocked()
    {
        lock (_breakerLock) _malkavBlockedUntilUtc = DateTime.UtcNow + BlackoutDuration;
    }

    /// <summary>True iff the SS API may be called now (not in a quota blackout window).</summary>
    private static bool ScreenscraperShouldAllow()
    {
        lock (_breakerLock) return DateTime.UtcNow >= _ssBlockedUntilUtc;
    }

    private static void MarkScreenscraperBlocked()
    {
        lock (_breakerLock) _ssBlockedUntilUtc = DateTime.UtcNow + BlackoutDuration;
    }

    /// <summary>The SS "quota / credentials" status codes that arm the blackout (401 / 403 / 430 / 431).</summary>
    private static bool IsBlackoutTrigger(int statusCode)
        => statusCode == 401 || statusCode == 403 || statusCode == 430 || statusCode == 431;
}
