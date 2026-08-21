// One blurred wallpaper composed at window size, sliced per control.
//
// The look: a single background image behind the whole window, with each of the three panels keeping its own
// tint on top at a configurable opacity — lower the opacity and the original panel colours show through.
// The panels therefore do NOT get their own images; they get their SLICE of one composed bitmap, so the
// picture stays continuous across splitters and its features do not repeat or jump.
//
// Composed once per (image, window size, blur, darken, tint) and cached: every control then paints by
// blitting its own sub-rectangle, which is what makes this affordable on surfaces that repaint often.
//
// Blur without a per-pixel kernel: downscale hard, then scale back up with bicubic interpolation. Two passes
// give a soft, wide blur for the cost of two scaled blits — a real Gaussian over 2 Mpx in managed code would
// cost far more and look no different once darkened and tinted.
//
// The virtualized ListViews CAN carry their slice: measured with interleaved A/B rounds, a background image
// costs them 10-36% of scroll repaint time, not the factor of six a naive sequential benchmark suggested (see
// Tools/ListViewBenchProbe, which documents that trap). AverageColor remains for surfaces where an image is
// impractical — a control whose slice cannot be positioned reliably, or one repainting far too often — giving
// it the wallpaper's local average instead. Against an already-blurred picture that reads as a continuation
// rather than a patch.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LbApiHost.Host.UiKit;

internal static class Wallpaper
{
    private static string _path = "";
    private static int _blur = 24;          // 0 = none; otherwise the downscale divisor is derived from this
    private static int _darken = 45;        // percent of black laid over the image
    private static bool _enabled;

    private static Image? _source;          // the file, decoded once
    private static Bitmap? _composed;       // _source fitted to the current window size, blurred and darkened
    private static Size _composedFor;
    private static readonly object Gate = new();

    /// <summary>True when a usable image is configured — callers skip all wallpaper painting when false and
    /// keep their flat theme colours, so nothing is loaded or composed for users who leave this off.</summary>
    public static bool Enabled => _enabled && _source != null;

    /// <summary>Apply settings. Decodes the file (once) and drops any composition built from the old ones.
    /// A missing or unreadable file silently disables the feature rather than throwing into a paint path.</summary>
    public static void Configure(bool enabled, string path, int blurPercent, int darkenPercent)
    {
        lock (Gate)
        {
            bool reload = !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
            _enabled = enabled;
            _path = path ?? "";
            _blur = Math.Clamp(blurPercent, 0, 100);
            _darken = Math.Clamp(darkenPercent, 0, 100);

            if (reload)
            {
                _source?.Dispose();
                _source = null;
                if (_enabled && !string.IsNullOrWhiteSpace(_path) && File.Exists(_path))
                {
                    // Decode through a copy: Image.FromFile keeps the file locked for the object's lifetime,
                    // which would stop the user replacing their own wallpaper while LiteBox runs.
                    try
                    {
                        using var raw = Image.FromFile(_path);
                        _source = new Bitmap(raw);
                    }
                    catch { _source = null; }
                }
            }
            Drop();
        }
    }

    /// <summary>Discard the composed bitmap (window resized, or settings changed). The source stays decoded.</summary>
    public static void Drop()
    {
        lock (Gate)
        {
            _composed?.Dispose();
            _composed = null;
            _composedFor = Size.Empty;
        }
    }

    /// <summary>Paint <paramref name="c"/>'s slice of the wallpaper, then <paramref name="tint"/> over it at
    /// <paramref name="tintOpacity"/> percent. Returns false when there is nothing to paint, so callers fall
    /// back to their flat colour in one line: <c>if (!Wallpaper.Paint(...)) g.Clear(BackColor);</c></summary>
    public static bool Paint(Graphics g, Control c, Color tint, int tintOpacity)
    {
        var root = c.TopLevelControl ?? c;
        var wall = ComposeFor(root.ClientSize);
        if (wall == null) return false;

        // Where this control sits inside the window: the slice to show is exactly that rectangle, drawn at
        // the control's own origin so the picture appears fixed to the window while the control scrolls.
        var origin = root.PointToClient(c.PointToScreen(Point.Empty));
        var src = new Rectangle(origin.X, origin.Y, c.Width, c.Height);
        src.Intersect(new Rectangle(Point.Empty, wall.Size));
        if (src.Width <= 0 || src.Height <= 0) return false;

        g.DrawImage(wall, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
        if (src.Width < c.Width || src.Height < c.Height)   // control extends past the composed area
            using (var pad = new SolidBrush(tint))
            {
                if (src.Width < c.Width) g.FillRectangle(pad, src.Width, 0, c.Width - src.Width, c.Height);
                if (src.Height < c.Height) g.FillRectangle(pad, 0, src.Height, c.Width, c.Height - src.Height);
            }

        if (tintOpacity > 0)
        {
            using var b = new SolidBrush(Color.FromArgb(Math.Clamp(tintOpacity, 0, 100) * 255 / 100, tint));
            g.FillRectangle(b, 0, 0, c.Width, c.Height);
        }
        return true;
    }

    /// <summary>The control's slice as a standalone bitmap, tint already applied — for native controls that
    /// take a <c>BackgroundImage</c> rather than letting us paint into their DC (ListView, and TreeView if it
    /// honours it). Null when no wallpaper is configured. The caller owns the bitmap and must dispose the one
    /// it replaces, since assigning BackgroundImage does not free the previous value.</summary>
    public static Bitmap? Slice(Control c, Color tint, int tintOpacity)
    {
        var root = c.TopLevelControl ?? c;
        var wall = ComposeFor(root.ClientSize);
        if (wall == null || c.Width <= 0 || c.Height <= 0) return null;

        var origin = root.PointToClient(c.PointToScreen(Point.Empty));
        var src = new Rectangle(origin.X, origin.Y, c.Width, c.Height);
        src.Intersect(new Rectangle(Point.Empty, wall.Size));
        if (src.Width <= 0 || src.Height <= 0) return null;

        var outp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(outp))
        {
            using (var pad = new SolidBrush(tint)) g.FillRectangle(pad, 0, 0, c.Width, c.Height);
            lock (Gate) g.DrawImage(wall, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
            if (tintOpacity > 0)
                using (var b = new SolidBrush(Color.FromArgb(Math.Clamp(tintOpacity, 0, 100) * 255 / 100, tint)))
                    g.FillRectangle(b, 0, 0, c.Width, c.Height);
        }
        return outp;
    }

    /// <summary>The wallpaper's average colour under <paramref name="c"/>, blended with <paramref name="tint"/>
    /// at the same opacity Paint would have used — the flat stand-in for controls that cannot take an image.
    /// Returns <paramref name="tint"/> unchanged when no wallpaper is configured.</summary>
    public static Color AverageColor(Control c, Color tint, int tintOpacity)
    {
        var root = c.TopLevelControl ?? c;
        var wall = ComposeFor(root.ClientSize);
        if (wall == null) return tint;

        var origin = root.PointToClient(c.PointToScreen(Point.Empty));
        var r = new Rectangle(origin.X, origin.Y, c.Width, c.Height);
        r.Intersect(new Rectangle(Point.Empty, wall.Size));
        if (r.Width <= 0 || r.Height <= 0) return tint;

        var avg = Average(wall, r);
        int a = Math.Clamp(tintOpacity, 0, 100);
        return Color.FromArgb(
            (avg.R * (100 - a) + tint.R * a) / 100,
            (avg.G * (100 - a) + tint.G * a) / 100,
            (avg.B * (100 - a) + tint.B * a) / 100);
    }

    private static Bitmap? ComposeFor(Size window)
    {
        if (window.Width <= 0 || window.Height <= 0) return null;
        lock (Gate)
        {
            if (!Enabled) return null;
            if (_composed != null && _composedFor == window) return _composed;

            _composed?.Dispose();
            _composed = Compose(_source!, window, _blur, _darken);
            _composedFor = window;
            return _composed;
        }
    }

    private static Bitmap Compose(Image src, Size window, int blurPercent, int darkenPercent)
    {
        var outp = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(outp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // Cover-fit: fill the window entirely, cropping the overflowing axis and keeping the centre.
            double sx = (double)window.Width / src.Width, sy = (double)window.Height / src.Height;
            double s = Math.Max(sx, sy);
            int w = (int)Math.Ceiling(src.Width * s), h = (int)Math.Ceiling(src.Height * s);
            g.DrawImage(src, new Rectangle((window.Width - w) / 2, (window.Height - h) / 2, w, h));
        }

        if (blurPercent > 0)
        {
            // Two down/up passes. The divisor grows with the setting: 1% ─ barely soft, 100% ─ pure colour
            // wash. Clamped so the intermediate never collapses below 2 px on either axis.
            int div = 1 + blurPercent * 39 / 100;
            for (int pass = 0; pass < 2; pass++)
            {
                int sw = Math.Max(2, window.Width / div), sh = Math.Max(2, window.Height / div);
                using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;   // averages when shrinking
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(outp, new Rectangle(0, 0, sw, sh));
                }
                using (var g = Graphics.FromImage(outp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;    // smooth on the way back
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(small, new Rectangle(0, 0, window.Width, window.Height));
                }
            }
        }

        if (darkenPercent > 0)
            using (var g = Graphics.FromImage(outp))
            using (var b = new SolidBrush(Color.FromArgb(darkenPercent * 255 / 100, Color.Black)))
                g.FillRectangle(b, 0, 0, window.Width, window.Height);

        return outp;
    }

    // Sampled, not exhaustive: a stride keeps this at a few thousand reads whatever the panel's size, which
    // matters because it runs on layout changes rather than once. Rows are copied out one at a time
    // (Marshal.Copy) rather than walked through a pointer — the project does not enable unsafe blocks, and at
    // 48 sampled rows the copy is not worth a compiler flag.
    private static Color Average(Bitmap bmp, Rectangle r)
    {
        const int Samples = 48;   // per axis
        int stepX = Math.Max(1, r.Width / Samples), stepY = Math.Max(1, r.Height / Samples);
        long sr = 0, sg = 0, sb = 0, n = 0;

        var data = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < r.Height; y += stepY)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < r.Width; x += stepX)
                {
                    int px = x * 4;   // BGRA
                    if (px + 2 >= row.Length) break;
                    sb += row[px]; sg += row[px + 1]; sr += row[px + 2]; n++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        if (n == 0) return Color.Black;
        return Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
    }
}
