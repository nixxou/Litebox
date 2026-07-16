// GET /thumbs/{id}.jpg — a small DEGRADED cover for a game by its metadata DB id (the platform-grid tile).
//
// ExtendDB's ThumbHandler just 302-redirected to a pre-rendered remote thumbnail. LiteBox has no such remote,
// so it produces the degraded thumb itself: pick the game's cover from the metadata DB, materialise the full
// image once into the web-image thumb-source cache, then hand it to Host/Media/ThumbCache which resizes it to
// a JPEG (q72, longest edge 360 px). A cache hit needs no download and no Magick; the resized file is served
// immutable. Falls back to the full cover, then 404, when a step can't complete.

#nullable enable

using System;
using System.IO;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal static class ThumbHandler
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var idStr = ctx.GetRoute("id");
        if (string.IsNullOrEmpty(idStr) || !int.TryParse(idStr, out var id) || id <= 0)
            return HttpResponse.NotFound();

        var cover = MediaProxy.PickCover(MetadataDb.ImagesForGame(id));
        if (cover == null) return HttpResponse.NotFound();

        try
        {
            // Materialise the full cover once into a STABLE source path so ThumbCache's size-versioned key stays
            // hit across requests. Extension derives from the DB FileName; defaults to jpg.
            var ext = SafeExt(cover.Value.FileName);
            var srcPath = Path.Combine(ThumbCache.WebImgFolder, id + "." + ext);

            if (!File.Exists(srcPath))
            {
                var bytes = MediaFetch.FetchBytes(cover.Value, platform: null!);
                if (bytes == null || bytes.Length == 0) return HttpResponse.NotFound();
                var tmp = srcPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try { File.Move(tmp, srcPath, overwrite: false); }
                catch { try { File.Delete(tmp); } catch { } }
                if (!File.Exists(srcPath)) return HttpResponse.NotFound();
            }

            // Degrade. On a miss with Magick absent this returns null → serve the full source instead of failing.
            var thumbPath = ThumbCache.GetOrCreate(srcPath) ?? srcPath;
            var thumbBytes = File.ReadAllBytes(thumbPath);
            var ctype = thumbPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";

            var resp = HttpResponse.Bytes(thumbBytes, ctype);
            resp.Headers["Cache-Control"] = "public, max-age=86400";
            resp.Headers["Content-Disposition"] = "inline";
            return resp;
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", $"thumb {id} failed: {ex.Message}");
            return HttpResponse.NotFound();
        }
    }

    private static string SafeExt(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        // Screenscraper/api rows carry no real extension in the path — those aren't covers anyway, but default
        // to jpg so the source file always has a sane name for Magick to sniff.
        foreach (var c in ext) if (!(char.IsLetterOrDigit(c))) { ext = ""; break; }
        return string.IsNullOrEmpty(ext) || ext.Length > 5 ? "jpg" : ext;
    }
}
