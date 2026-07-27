// The 3D case model as a MEDIA-LIST ITEM — the glue that lets the baked GLB behave like any other
// image in the right pane's instant/post-load pipeline.
//
// The media pipeline traffics in PATH STRINGS (BuildMediaList → strip thumbs → SetMainMedia). The 3D
// model therefore rides as a SENTINEL path: "3dmodel://<absolute GLB path>". One chokepoint in the
// image loaders (LoadImage / LoadThumbOrFull) decodes the sentinel into the GLB's embedded thumb PNG,
// which makes the instant image, the strip tile and the main-box preload all work unchanged; only
// SetMainMedia adds behaviour (swap in the live viewport over the PNG — see MainWindow's 3D overlay).
//
// Rules (decided with the user):
//   • the item exists only when the model CAN exist: front art present (or a full scan when the
//     platform/game runs full-scan mode) — exactly Model3dCache.Resolve's HasArt;
//   • the dup-checker ignores it BOTH ways (its PNG is a render of the front — comparing would evict
//     the real front);
//   • instant = PNG only, never a bake, never a viewport; post-load may bake at settle.

#nullable enable

using System;
using System.Drawing;
using System.IO;

namespace LbApiHost.Host.Media;

internal static class Media3dItem
{
    /// <summary>MediaEntry.Sel / immediate-family value for the 3D pseudo-family.</summary>
    public const string FamilyKey = "Model3d";
    /// <summary>Display title in the layout editor's family combos.</summary>
    public const string FamilyTitle = "3D Model";

    private const string Prefix = "3dmodel://";

    /// <summary>The media-list sentinel for a baked (or bakeable) GLB.</summary>
    public static string For(string glbPath) => Prefix + glbPath;

    /// <summary>True when a media-list item is the 3D model sentinel.</summary>
    public static bool Is(string? item) => item != null && item.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The GLB path inside a sentinel item.</summary>
    public static string GlbPath(string item) => item.Substring(Prefix.Length);

    /// <summary>The baked snapshot PNG as a detached Image — the .png SIDECAR beside the GLB when
    /// present (plain fast read), else extracted from the GLB head and restored beside it.
    /// Null when the GLB doesn't exist yet (bake pending) or is unreadable.</summary>
    public static Image? Thumb(string item)
    {
        try
        {
            var png = Model3d.Model3dCache.ReadThumbPng(GlbPath(item));
            if (png == null) { Console.WriteLine("[media3d] thumb read null: " + GlbPath(item)); return null; }
            using var ms = new MemoryStream(png);
            using var tmp = Image.FromStream(ms);
            return new Bitmap(tmp);   // detach from the stream (GDI+ keeps streams alive otherwise)
        }
        catch (Exception ex) { Console.WriteLine("[media3d] thumb failed: " + ex.Message); return null; }
    }
}
