// Serves LaunchBox platform icon images from the Nostalgic Platform Icons media pack shipped with LaunchBox.
//   /api/launchbox/icons/<name>.<ext>
//
// Resolution order (first match wins), under <LB>\Images\Media Packs\Platform Icons\Nostalgic Platform Icons\:
//   1. Platforms\<name>.png   2. Platform Categories\<name>.png   3. Playlists\<name>.png
//
// <name> is URL-decoded and underscores become spaces before a case-insensitive scan, so
// "super_nintendo_entertainment_system" resolves against "Super Nintendo Entertainment System.png". The <ext>
// capture is accepted but ignored — the pack ships only PNGs. Path-traversal guard: the resolved file must stay
// inside <LB>\Images\.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/LaunchBox/LaunchBoxIconsApi.cs — the LB root comes from LiteBox's
// own MediaResolver.LbRoot (not the plugin's ExtendDBPlugin.LBPath).

using System;
using System.IO;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal static class LaunchBoxIconsApi
{
    private static readonly string[] _subDirs =
    {
        Path.Combine("Media Packs", "Platform Icons", "Nostalgic Platform Icons", "Platforms"),
        Path.Combine("Media Packs", "Platform Icons", "Nostalgic Platform Icons", "Platform Categories"),
        Path.Combine("Media Packs", "Platform Icons", "Nostalgic Platform Icons", "Playlists"),
    };

    public static HttpResponse Handle(RouteContext ctx)
    {
        var lbPath = MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(lbPath)) return HttpResponse.NotFound();

        var imagesRoot = Path.Combine(lbPath, "Images");
        if (!Directory.Exists(imagesRoot)) return HttpResponse.NotFound();
        var imagesRootFull = Path.GetFullPath(imagesRoot);

        var rawName = ctx.GetRoute("name") ?? "";
        string decodedName;
        try { decodedName = Uri.UnescapeDataString(rawName); }
        catch { decodedName = rawName; }
        var friendlyName = decodedName.Replace('_', ' ');

        foreach (var sub in _subDirs)
        {
            var dir = Path.Combine(imagesRoot, sub);
            if (!Directory.Exists(dir)) continue;

            string match = null;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(stem, friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(stem, decodedName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = file;
                        break;
                    }
                }
            }
            catch (Exception ex) { LbLog.Warn("web", "icon scan error in " + dir + ": " + ex.Message); continue; }

            if (match == null) continue;

            string matchFull;
            try { matchFull = Path.GetFullPath(match); }
            catch { return HttpResponse.NotFound(); }
            if (!matchFull.StartsWith(imagesRootFull, StringComparison.OrdinalIgnoreCase))
                return HttpResponse.NotFound();

            byte[] bytes;
            try { bytes = File.ReadAllBytes(matchFull); }
            catch (Exception ex) { LbLog.Warn("web", "icon read error " + matchFull + ": " + ex.Message); return HttpResponse.ServerError("read error"); }

            var resp = HttpResponse.Bytes(bytes, "image/png");
            resp.Headers["Cache-Control"] = "max-age=3600";
            return resp;
        }

        return HttpResponse.NotFound();
    }
}
