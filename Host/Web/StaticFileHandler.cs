// One generic static-file handler for every web site the server mounts.
//
// It replaces ExtendDB's three near-identical static handlers (ThemeStaticFiles, LaunchBoxPageHandler and
// VendorStaticHandler) with a single instance bound to a root folder — one per mount (bigbox / litebox /
// vendor / database). Each handler serves files under its own root, resolves the content-type by extension,
// treats an empty sub-path as index.html, and refuses to leave the root (path-traversal guard → 404, never
// 403, so the directory structure isn't leaked). Responses carry Cache-Control: no-cache so on-disk edits
// show up on reload. The root is resolved lazily on each request (via the supplied provider) because
// LiteBoxPaths.Web(site) creates the folder on demand.

using System;
using System.IO;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

internal sealed class StaticFileHandler
{
    private readonly Func<string> _rootProvider;
    private readonly string _tag;

    /// <param name="rootProvider">Resolves the absolute root folder for this mount (e.g.
    /// <c>() =&gt; LiteBoxPaths.Web("bigbox")</c>).</param>
    /// <param name="tag">Short label for 404/read log lines.</param>
    public StaticFileHandler(Func<string> rootProvider, string tag)
    {
        _rootProvider = rootProvider;
        _tag = tag;
    }

    /// <summary>Route entry point: serves the captured <c>path</c> group under the root.</summary>
    public HttpResponse Handle(RouteContext ctx) => Serve(ctx.GetRoute("path"), ctx.Request);

    /// <summary>Serves <paramref name="relPath"/> relative to the root. Empty / "/" → index.html. Missing
    /// file or a path that escapes the root → 404. Passing <paramref name="req"/> enables Range replies —
    /// which media elements and the EmulatorJS core loader both ask for.</summary>
    public HttpResponse Serve(string relPath, HttpRequest req = null)
    {
        string root = _rootProvider();
        relPath = (relPath ?? "").Replace('\\', '/').TrimStart('/');
        if (relPath.Length == 0) relPath = "index.html";

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relPath));
        }
        catch
        {
            return HttpResponse.NotFound();
        }

        // Traversal guard: the resolved path must stay inside the root. Compare against the root WITH a trailing
        // separator so a sibling whose name merely starts with the root (…\web\bigbox2 vs …\web\bigbox) can't
        // pass; allow the root itself (index request already rewrote empty → index.html, so full > root here).
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return HttpResponse.NotFound();

        if (!File.Exists(full))
        {
            LbLog.Info("web", $"{_tag} 404 {relPath} (looked in {full})");
            return HttpResponse.NotFound();
        }

        // Streamed, not buffered: a theme is small but the vendor mount also carries the EmulatorJS cores
        // (tens of MB each) and video/audio, which the browser fetches by Range.
        var resp = HttpResponse.FromFile(full, ContentTypeFor(Path.GetExtension(full)), req);
        // Dev-friendly: never cache the shell so theme edits appear on reload.
        resp.Headers["Cache-Control"] = "no-cache";
        return resp;
    }

    private static string ContentTypeFor(string ext) => (ext ?? "").TrimStart('.').ToLowerInvariant() switch
    {
        "html" or "htm" => "text/html; charset=utf-8",
        "js" or "mjs"   => "application/javascript; charset=utf-8",
        "css"           => "text/css; charset=utf-8",
        "json"          => "application/json; charset=utf-8",
        "webmanifest"   => "application/manifest+json; charset=utf-8",
        "map"           => "application/json; charset=utf-8",
        "txt"           => "text/plain; charset=utf-8",
        "svg"           => "image/svg+xml",
        "png"           => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif"           => "image/gif",
        "webp"          => "image/webp",
        "avif"          => "image/avif",
        "ico"           => "image/x-icon",
        "bmp"           => "image/bmp",
        "mp4" or "m4v"  => "video/mp4",
        "webm"          => "video/webm",
        "mkv"           => "video/x-matroska",
        "mp3"           => "audio/mpeg",
        "ogg"           => "audio/ogg",
        "wav"           => "audio/wav",
        "flac"          => "audio/flac",
        "m4a"           => "audio/mp4",
        "woff2"         => "font/woff2",
        "woff"          => "font/woff",
        "ttf"           => "font/ttf",
        "otf"           => "font/otf",
        _               => "application/octet-stream",
    };
}
