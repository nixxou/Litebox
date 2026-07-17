// The embedded server's single media endpoint: every game image / video / manual the web UI shows flows
// through here. Two entry points, both generate-on-demand (no bytes cached C#-side — the Cache-Control just
// lets the BROWSER cache):
//
//   • Handle          — GET /api/media/{token}.{sig}.{ext}. HMAC-verifies the URL, decodes the token, then:
//                        local disk FIRST when token.p is a real file (with HTTP Range for <video> seeking),
//                        else fetches the bytes upstream via the native per-origin chain (Host/Media/MediaFetch).
//   • HandleThumbById — GET /api/media/{id}.{ext}. Cover-by-id: pick the game's cover from the metadata DB
//                        and resolve it through the same chain (the extenddb mirror is one of its steps).
//
// Clean-room LiteBox rewrite of ExtendDB's MediaApi + the token half of its MediaResolver. The heavy
// per-origin URL/policy machinery is NOT re-ported — LiteBox already owns it in MediaFetch; this file is only
// the thin HTTP shell (token decode, disk-first + Range, content-type) around it. No Harmony, no reflection,
// no HTTP intercept.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal static class MediaProxy
{
    // ── Token (URL ⇄ payload) ───────────────────────────────────────────────────
    // The proxy URL carries everything the handler needs in its own path — no process-wide map, so URLs survive
    // restarts and browser tabs. Compact JSON keys keep the Base32 segment short. Keys are FROZEN as p/f/c/o/t;
    // renaming any invalidates browser-cached URLs.

    internal sealed class MediaToken
    {
        /// <summary>Absolute path on disk. Wins when set and the file exists.</summary>
        public string? p { get; set; }
        /// <summary>DB FileName — drives the upstream URL builder / mirror fallback.</summary>
        public string? f { get; set; }
        /// <summary>DB CRC32 (unsigned), paired with <see cref="f"/> for the mirror URL.</summary>
        public long c { get; set; }
        /// <summary>DB Origin ("launchbox" / "screenscraper" / "steam" / "local" / …). Selects the URL recipe.</summary>
        public string? o { get; set; }
        /// <summary>DB Type ("Box - Front" / "Manual" / "Video" / …). Selects the media context.</summary>
        public string? t { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };

    /// <summary>
    /// Encode (localPath?, fileName?, crc?, ext) into a stable, signed proxy URL
    /// <c>/api/media/{token}.{sig}.{ext}</c>. The sig is a 16-byte truncated HMAC of the token segment under the
    /// per-install <see cref="MediaTokenSecret"/> key — without it, a forged token can't make the proxy read an
    /// arbitrary disk file. Returns null when there's nothing to encode. (Producer side — used by the theme data
    /// slices; kept here as the exact counterpart of <see cref="TryDecodeAndVerify"/>.)
    /// </summary>
    public static string? BuildProxyUrl(
        string? localPath, string? fileName, long crc, string? ext,
        string? origin = null, string? type = null)
    {
        if (string.IsNullOrEmpty(localPath) && string.IsNullOrEmpty(fileName)) return null;
        var token = new MediaToken { p = localPath, f = fileName, c = crc, o = origin, t = type };
        var json = JsonSerializer.Serialize(token, _jsonOpts);
        var b32Token = MediaTokenSecret.Base32.Encode(Encoding.UTF8.GetBytes(json));
        var b32Sig = MediaTokenSecret.Base32.Encode(MediaTokenSecret.Sign(b32Token));
        return $"/api/media/{b32Token}.{b32Sig}.{NormalizeExt(ext)}";
    }

    /// <summary>Verify the HMAC (constant time) THEN decode the token JSON. False on bad encoding, bad JSON, or
    /// signature mismatch — the caller answers 404 either way (never leaks which step failed).</summary>
    public static bool TryDecodeAndVerify(string? b32Token, string? b32Sig, out MediaToken? token)
    {
        token = null;
        if (string.IsNullOrEmpty(b32Token) || string.IsNullOrEmpty(b32Sig)) return false;
        try
        {
            var sigBytes = MediaTokenSecret.Base32.Decode(b32Sig!);
            if (!MediaTokenSecret.Verify(b32Token!, sigBytes)) return false;   // authenticate before parsing

            var json = Encoding.UTF8.GetString(MediaTokenSecret.Base32.Decode(b32Token!));
            token = JsonSerializer.Deserialize<MediaToken>(json, _jsonOpts);
            return token != null;
        }
        catch { return false; }
    }

    // ── Entry point 1: signed-token proxy ───────────────────────────────────────

    public static HttpResponse Handle(RouteContext ctx)
    {
        var b32Token = ctx.GetRoute("token");
        var b32Sig = ctx.GetRoute("sig");
        var ext = ctx.GetRoute("ext");
        if (string.IsNullOrEmpty(b32Token) || string.IsNullOrEmpty(b32Sig) || string.IsNullOrEmpty(ext))
            return HttpResponse.NotFound();

        if (!TryDecodeAndVerify(b32Token, b32Sig, out var token) || token == null)
            return HttpResponse.NotFound();

        // Local disk first when the token names a path. `q` selects the tier: "thumb"/"logo" = degraded resized,
        // "full" = original with a short TTL, absent = original + 2 h browser cache.
        var q = ctx.Request?.GetQuery("q");
        var fromDisk = TryServeLocal(token, ext!, q, ctx.Request);
        if (fromDisk != null) return fromDisk;

        // No usable local file → fetch the bytes upstream via the native per-origin chain.
        var fromUpstream = TryServeUpstream(token, ext!);
        if (fromUpstream != null) return fromUpstream;

        // 404 — surface the resolved path so a missing / mis-resolved file is diagnosable from DevTools.
        var nf = HttpResponse.NotFound();
        if (!string.IsNullOrEmpty(token.p))
        {
            nf.Headers["X-Local-Path"] = token.p!;
            if (!System.IO.File.Exists(token.p)) nf.Headers["X-Local-Missing"] = "1";
        }
        return nf;
    }

    // ── Entry point 2: cover-by-id ──────────────────────────────────────────────
    // /api/media/{id}.{ext}. Pick the game's cover from the metadata DB and resolve it through the same
    // per-origin chain (the extenddb mirror is one of its steps, so this is the "mirror → DB" fallback).

    public static HttpResponse HandleThumbById(RouteContext ctx)
    {
        var idStr = ctx.GetRoute("id");
        var ext = ctx.GetRoute("ext");
        if (string.IsNullOrEmpty(idStr) || string.IsNullOrEmpty(ext)) return HttpResponse.NotFound();
        if (!int.TryParse(idStr, out var id) || id <= 0) return HttpResponse.NotFound();

        // Thumb-context walk (plugin parity): pre-made id-based thumbs first — no DB read on the happy
        // path — with the cover row materialised lazily only when both pre-made sources fail. The lambda
        // may run 0 or 1 times; memoise so the last-resort fallback below doesn't re-query.
        MetadataDb.WebImage? cover = null; bool picked = false;
        MetadataDb.WebImage? Cover()
        {
            if (!picked) { picked = true; cover = PickCover(MetadataDb.ImagesForGame(id)); }
            return cover;
        }

        var bytes = MediaFetch.FetchThumbById(id, Cover);

        // Beyond-plugin last resort: every thumb source is down → serve the full-size cover rather than 404.
        if (bytes == null || bytes.Length == 0)
        {
            var c = Cover();
            if (c != null) bytes = MediaFetch.FetchBytes(c.Value, platform: null!);
        }
        if (bytes == null || bytes.Length == 0) return HttpResponse.NotFound();

        return BytesWithBrowserCache(bytes, ContentTypeFor(ext!));
    }

    // ── Cover selection (server-side mirror of the static-site generator's PickCover) ─
    // Preference order and exclusions MUST match so the local fallback and any pre-rendered thumbnail agree.

    private static readonly string[] CoverTypePreference =
    {
        "Poster", "Box - Front", "Fanart - Box - Front", "Advertisement Flyer - Front",
        "Box - 3D", "Cart - Front", "Cart - 3D", "Fanart - Background",
        "Screenshot - Game Title", "Screenshot - Gameplay",
    };

    private static readonly HashSet<string> ExcludedCoverTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Icon", "Manual", "Press", "Map", "Music", "Video", "VideoAdvert",
    };

    /// <summary>Best cover row from <paramref name="images"/>, or null. Walks the preference order first, then
    /// falls back to the first non-excluded image-typed row.</summary>
    public static MetadataDb.WebImage? PickCover(List<MetadataDb.WebImage> images)
    {
        if (images == null || images.Count == 0) return null;

        foreach (var preferred in CoverTypePreference)
            for (int i = 0; i < images.Count; i++)
                if (string.Equals(images[i].Type, preferred, StringComparison.OrdinalIgnoreCase))
                    return images[i];

        for (int i = 0; i < images.Count; i++)
        {
            var t = images[i].Type;
            if (string.IsNullOrEmpty(t) || ExcludedCoverTypes.Contains(t)) continue;
            return images[i];
        }
        return null;
    }

    // ── Local-disk serve (with HTTP Range) ──────────────────────────────────────

    private static HttpResponse? TryServeLocal(MediaToken token, string ext, string? q, HttpRequest? req)
    {
        if (string.IsNullOrEmpty(token.p)) return null;
        try
        {
            if (!System.IO.File.Exists(token.p)) return null;

            // Degraded tier: resized cached file for images. q=thumb → JPEG, q=logo → WebP w/ alpha (clear logos;
            // JPEG would blacken transparency). Versioned by source size → safe to cache immutably for a year.
            bool wantThumb = string.Equals(q, "thumb", StringComparison.OrdinalIgnoreCase);
            bool wantLogo = string.Equals(q, "logo", StringComparison.OrdinalIgnoreCase);
            if ((wantThumb || wantLogo) && IsImageExt(ext))
            {
                var tp = ThumbCache.GetOrCreate(token.p!, ThumbCache.DefaultMaxDim, keepAlpha: wantLogo);
                if (tp != null && System.IO.File.Exists(tp))
                {
                    var tb = System.IO.File.ReadAllBytes(tp);
                    var ctype = tp.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";
                    var rt = HttpResponse.Bytes(tb, ctype);
                    rt.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                    rt.Headers["Content-Disposition"] = "inline";
                    rt.Headers["X-Local-Path"] = token.p!;
                    return rt;
                }
                // Generation failed (e.g. Magick absent) → fall through to the full original.
            }

            long total = new FileInfo(token.p!).Length;
            var contentType = ContentTypeFor(ext);

            // HTTP Range for <video>/<audio> seeking: a "Range: bytes=start-end" gets a 206 with Content-Range so
            // the browser can map the slice onto the full timeline. Single range only (all a media element sends).
            var rangeHeader = req?.GetHeader("Range");
            if (TryParseSingleRange(rangeHeader, total, out long start, out long end))
            {
                long len = end - start + 1;
                var slice = new byte[len];
                using (var fs = new FileStream(token.p!, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    int read = 0;
                    while (read < len)
                    {
                        int n = fs.Read(slice, read, (int)(len - read));
                        if (n <= 0) break;
                        read += n;
                    }
                }
                var partial = HttpResponse.Bytes(slice, contentType, 206);
                partial.StatusText = "Partial Content";
                partial.Headers["Accept-Ranges"] = "bytes";
                partial.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
                partial.Headers["Cache-Control"] = "public, max-age=7200";
                partial.Headers["Content-Disposition"] = "inline";
                partial.Headers["X-Local-Path"] = token.p!;
                return partial;
            }

            var bytes = System.IO.File.ReadAllBytes(token.p!);
            var resp = HttpResponse.Bytes(bytes, contentType);
            resp.Headers["Accept-Ranges"] = "bytes";
            resp.Headers["Content-Disposition"] = "inline";
            // Full versions (q=full) get a short TTL so they don't accumulate; default keeps the standard 2 h.
            resp.Headers["Cache-Control"] = string.Equals(q, "full", StringComparison.OrdinalIgnoreCase)
                ? "public, max-age=180"
                : "public, max-age=7200, must-revalidate";
            resp.Headers["X-Local-Path"] = token.p!;
            return resp;
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", $"media local read failed for {token.p}: {ex.Message}");
            return null;
        }
    }

    // ── Upstream serve (native per-origin chain) ────────────────────────────────

    private static HttpResponse? TryServeUpstream(MediaToken token, string ext)
    {
        if (string.IsNullOrEmpty(token.f)) return null;

        // Map the token onto a WebImage the fetcher understands. dbId is unknown on the token path (0 → the
        // id-based thumb kinds self-skip); FileName + Origin + CRC drive the URL recipe.
        var w = new MetadataDb.WebImage(
            db: 0, fn: token.f!, ty: token.t ?? "", rg: "", crc: token.c,
            origin: token.o ?? "", dup: 0, ft: "");

        byte[]? bytes;
        try { bytes = MediaFetch.FetchBytes(w, platform: null!); }
        catch (Exception ex) { LbLog.Warn("web", $"media upstream fetch failed for {token.f}: {ex.Message}"); return null; }
        if (bytes == null || bytes.Length == 0) return null;

        return BytesWithBrowserCache(bytes, ContentTypeFor(ext));
    }

    private static HttpResponse BytesWithBrowserCache(byte[] bytes, string contentType)
    {
        var resp = HttpResponse.Bytes(bytes, contentType);
        resp.Headers["Cache-Control"] = "public, max-age=7200, must-revalidate";
        resp.Headers["Content-Disposition"] = "inline";
        resp.Headers["Accept-Ranges"] = "bytes";
        return resp;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Parses a single "bytes=start-end" (open-ended forms allowed) against <paramref name="total"/>.
    /// False when the header is absent, malformed, multi-range, or unsatisfiable.</summary>
    private static bool TryParseSingleRange(string? header, long total, out long start, out long end)
    {
        start = 0; end = total - 1;
        if (string.IsNullOrEmpty(header) || total <= 0) return false;

        const string prefix = "bytes=";
        if (!header!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header.Substring(prefix.Length).Trim();
        if (spec.IndexOf(',') >= 0) return false;   // multi-range not supported → serve full 200

        int dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var startStr = spec.Substring(0, dash).Trim();
        var endStr = spec.Substring(dash + 1).Trim();

        if (startStr.Length == 0)
        {
            // Suffix range: "-N" = last N bytes.
            if (!long.TryParse(endStr, out long suffix) || suffix <= 0) return false;
            if (suffix > total) suffix = total;
            start = total - suffix;
            end = total - 1;
        }
        else
        {
            if (!long.TryParse(startStr, out start) || start < 0) return false;
            if (endStr.Length == 0) end = total - 1;
            else if (!long.TryParse(endStr, out end) || end < start) return false;
            if (end > total - 1) end = total - 1;
        }
        return start <= end && start < total;
    }

    private static string NormalizeExt(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return "bin";
        ext = ext!.TrimStart('.').ToLowerInvariant();
        if (ext.Length > 6) ext = ext.Substring(0, 6);
        var sb = new StringBuilder(ext.Length);
        foreach (var c in ext)
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
        return sb.Length == 0 ? "bin" : sb.ToString();
    }

    private static bool IsImageExt(string ext) => (ext ?? "").ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" => true,
        _ => false,
    };

    // Extension → MIME. Only entries that actually change browser behaviour (inline display vs download) matter;
    // unknown extensions fall through to octet-stream and let the browser sniff.
    private static string ContentTypeFor(string ext) => (ext ?? "").ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "avif" => "image/avif",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "ico" => "image/x-icon",
        "tif" or "tiff" => "image/tiff",
        "heic" => "image/heic",
        "heif" => "image/heif",
        "mp4" or "m4v" => "video/mp4",
        "webm" => "video/webm",
        "mkv" => "video/x-matroska",
        "mov" => "video/quicktime",
        "avi" => "video/x-msvideo",
        "ts" => "video/mp2t",
        "m3u8" => "application/vnd.apple.mpegurl",
        "mp3" => "audio/mpeg",
        "ogg" => "audio/ogg",
        "wav" => "audio/wav",
        "flac" => "audio/flac",
        "m4a" => "audio/mp4",
        "aac" => "audio/aac",
        "opus" => "audio/opus",
        "pdf" => "application/pdf",
        "txt" => "text/plain; charset=utf-8",
        "json" => "application/json",
        "xml" => "application/xml",
        "html" or "htm" => "text/html; charset=utf-8",
        "css" => "text/css",
        "js" => "application/javascript",
        _ => "application/octet-stream",
    };
}
