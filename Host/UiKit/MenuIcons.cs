// The context-menu glyphs, loaded once from the embedded PNGs.
//
// They ship at 64x64 and are drawn at 16x16 (or 32 at 200% DPI), so the source is downscaled to the
// size the ToolStrip actually asks for rather than letting WinForms shrink a 64px bitmap on every
// paint — a nearest-ish runtime shrink is exactly what turns a legible glyph into mush.
//
// Every lookup is fail-soft: a missing or unreadable resource yields null, and a ToolStripMenuItem
// with a null Image simply renders without one. An icon is decoration; losing it must never cost a
// menu entry.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace LbApiHost.Host.UiKit;

internal static class MenuIcons
{
    // Names mirror the resource files; each is one menu action.
    public const string Add = "add";                                 // create a new game
    public const string AdditionalApps = "additionalapps";           // extra applications of a game
    public const string AdditionalDocuments = "additionaldocuments"; // manuals and documents
    public const string AdditionalVersions = "additionalversions";   // alternate versions
    public const string Combine = "combine";                         // merge the selection into one root game
    public const string Delete = "delete";
    public const string Edit = "edit";                               // bulk edit wizard
    public const string Expand = "expand";                           // split a combined game apart
    public const string Playlist = "playlist";                       // add to playlist
    public const string RefreshImages = "refreshall";
    public const string ResetCounts = "resetcounts";                 // play count and play time
    public const string ResetLastPlayed = "resetlastplayed";

    private static readonly ConcurrentDictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The glyph at the requested edge size, or null if it cannot be loaded.</summary>
    public static Image? Get(string name, int size = 16)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Cache.GetOrAdd($"{name}@{size}", _ => Load(name, size));
    }

    private static Image? Load(string name, int size)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream($"menu-icons/{name}.png");
            if (s == null) return null;
            using var src = new Bitmap(s);
            if (size <= 0 || (src.Width == size && src.Height == size)) return new Bitmap(src);

            var dst = new Bitmap(size, size);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return dst;
        }
        catch { return null; }
    }
}
