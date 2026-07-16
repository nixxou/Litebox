// GET /api/badges/{name}.png — a LaunchBox "badge" PNG by name from the user's Badges media pack
// (GOG, Steam, EpicGames, Installed, "Not Installed", …). The web UI frames these as the store install-state
// badge (a round store logo inside a green/orange ring).
//
// Resolution: scan every pack under <LB>\Images\Media Packs\Badges\*\ for a {name}.png, case-insensitive,
// with underscores in {name} also matched as spaces (so "Not_Installed" resolves "Not Installed.png"). First
// match wins; 404 when the pack isn't present (the front-end then draws just the coloured ring). A path-
// traversal guard keeps the resolved file inside <LB>\Images. LB root comes from MediaResolver.LbRoot.
//
// Clean-room LiteBox rewrite of ExtendDB's BadgeApi. EnumerateFiles/EnumerateDirectories (not GetFiles) — no
// LiteBox equivalent of the plugin's directory cache exists, but the streaming enumerators are the right tool
// regardless.

#nullable enable

using System;
using System.IO;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal static class BadgeApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var lbRoot = MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(lbRoot)) return HttpResponse.NotFound();

        var badgesRoot = Path.Combine(lbRoot, "Images", "Media Packs", "Badges");
        if (!Directory.Exists(badgesRoot)) return HttpResponse.NotFound();
        var imagesRootFull = Path.GetFullPath(Path.Combine(lbRoot, "Images"))
                                 .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var raw = ctx.GetRoute("name") ?? "";
        string name; try { name = Uri.UnescapeDataString(raw); } catch { name = raw; }
        var friendly = name.Replace('_', ' ');

        try
        {
            foreach (var pack in Directory.EnumerateDirectories(badgesRoot))
            {
                string? match = null;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(pack, "*.png"))
                    {
                        var stem = Path.GetFileNameWithoutExtension(file);
                        if (string.Equals(stem, friendly, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(stem, name, StringComparison.OrdinalIgnoreCase))
                        { match = file; break; }
                    }
                }
                catch (Exception ex) { LbLog.Warn("web", $"badge scan error in {pack}: {ex.Message}"); continue; }

                if (match == null) continue;

                string full;
                try { full = Path.GetFullPath(match); }
                catch { return HttpResponse.NotFound(); }
                if (!full.StartsWith(imagesRootFull, StringComparison.OrdinalIgnoreCase))
                    return HttpResponse.NotFound();

                byte[] bytes;
                try { bytes = File.ReadAllBytes(full); }
                catch (Exception ex) { LbLog.Warn("web", $"badge read error {full}: {ex.Message}"); return HttpResponse.ServerError("read error"); }

                var resp = HttpResponse.Bytes(bytes, "image/png");
                resp.Headers["Cache-Control"] = "max-age=3600";
                return resp;
            }
        }
        catch (Exception ex) { LbLog.Warn("web", $"badge resolve error: {ex.Message}"); }

        return HttpResponse.NotFound();
    }
}
