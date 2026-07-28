// A game VIDEO as a MEDIA-LIST ITEM — same glue as Media3dItem, so videos ride the right pane's
// instant/post-load pipeline like any image.
//
// The pipeline traffics in PATH STRINGS, so a video rides as the sentinel "video://<absolute path>".
// The image loaders decode it into the video's still frame (VideoThumbnailer, 20 % in, disk-cached),
// which makes the strip tile and the main box work unchanged; SetMainMedia adds the behaviour (hand the
// main zone to the VLC surface — see MainWindow's video overlay).
//
// Rules (decided with the user):
//   • frame extraction is SLOW, so it must never block: the loaders only ever return an ALREADY-CACHED
//     frame; the strip shows a black tile and fetches the missing frames after the post-load delay, on
//     its own cancellable worker (see MainWindow.KickVideoThumbs);
//   • the "▶" badge is painted at DRAW time over the tile, never baked into the cached frame;
//   • LaunchBox's video TYPES are the sub-folders of <LB>\Videos\<platform>\ — the root (the game's main
//     video) plus Trailer / Theme / Marquee / Recordings. The family takes them all in that order; an
//     exact type takes just one.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class MediaVideoItem
{
    /// <summary>MediaEntry.Sel / immediate-family value for the video pseudo-family.</summary>
    public const string FamilyKey = "Video";
    /// <summary>Display title in the layout editor's family combos.</summary>
    public const string FamilyTitle = "Videos";

    private const string Prefix = "video://";

    /// <summary>The video sub-folders LaunchBox uses, in family order. "" = the platform video root, i.e.
    /// the game's main video. Each is offerable as an EXACT type in the layout editor.</summary>
    public static readonly (string SubDir, string Title)[] Types =
    {
        ("",           "Video · Main"),
        ("Trailer",    "Video · Trailer"),
        ("Theme",      "Video · Theme"),
        ("Marquee",    "Video · Marquee"),
        ("Recordings", "Video · Recordings"),
    };

    /// <summary>Exact-type selector value for a sub-folder ("Video:Trailer", "Video:" for the root).</summary>
    public static string TypeKey(string subDir) => FamilyKey + ":" + subDir;

    /// <summary>True when a layout entry selects a video type or the whole video family.</summary>
    public static bool IsSelector(string? sel)
        => sel != null && (sel.Equals(FamilyKey, StringComparison.OrdinalIgnoreCase)
                        || sel.StartsWith(FamilyKey + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>The sub-folder an exact-type selector names, or null when it selects the whole family.</summary>
    public static string? SubDirOf(string sel)
        => sel.StartsWith(FamilyKey + ":", StringComparison.OrdinalIgnoreCase) ? sel.Substring(FamilyKey.Length + 1) : null;

    /// <summary>The media-list sentinel for a video file.</summary>
    public static string For(string path) => Prefix + path;

    /// <summary>True when a media-list item is a video sentinel.</summary>
    public static bool Is(string? item) => item != null && item.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The video file path inside a sentinel item.</summary>
    public static string PathOf(string item) => item.Substring(Prefix.Length);

    /// <summary>The video's still frame — ONLY if already extracted. Never decodes here: extraction costs
    /// hundreds of ms and this runs on the display path (see KickVideoThumbs for the deferred fetch).</summary>
    public static Image? CachedThumb(string item)
    {
        try
        {
            string p = PathOf(item);
            if (!Video.VideoThumbnailer.IsCached(p)) return null;
            return Video.VideoThumbnailer.Get(p);   // cache hit → a plain file read
        }
        catch { return null; }
    }

    /// <summary>The game's videos for a selector, in family order (root first, then Trailer/Theme/…), or
    /// just one sub-folder for an exact type. Cache-first, with the IGame fallback.</summary>
    public static List<string> Resolve(IGame g, string? subDir)
    {
        var res = new List<string>();
        try
        {
            string plat = g.Platform ?? "";
            if (plat.Length > 0 && Gc.HostGameCache.Ready(plat) && Guid.TryParse(g.Id, out var id))
            {
                var all = Gc.HostGameCache.AllVideoRefs(plat, id);
                // Order: the requested sub-folder only, else every type in Types order (root first).
                foreach (var (sub, _) in Types)
                {
                    if (subDir != null && !string.Equals(sub, subDir, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var v in all)
                    {
                        string vs;
                        try { vs = v.Value.SubDir ?? ""; } catch { continue; }
                        if (!string.Equals(vs, sub, StringComparison.OrdinalIgnoreCase)) continue;
                        if (v.FullPath is { Length: > 0 } fp) res.Add(fp);
                    }
                }
            }
            // No cache (or nothing found): LaunchBox's own single-video accessor covers the main video.
            if (res.Count == 0 && (subDir == null || subDir.Length == 0))
            {
                string? p = null;
                try { p = g.GetVideoPath(false); } catch { }
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) res.Add(p!);
            }
        }
        catch { }
        return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
