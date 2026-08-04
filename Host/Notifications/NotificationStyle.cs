// The shared LOOK of the notification surfaces (popup cards + the bell's list), factored out so the two
// can't drift apart: palette, rounded-corner helpers, the app glyph, and the flat rounded button.
//
// The reference is LaunchBox's own toast: a near-black rounded card with a drop shadow, the sender's
// COLOURFUL icon on the left (LaunchBox shows the plugin-manager cube; we show the LiteBox icon), dim
// timestamp, and full-width buttons drawn as a thin 1px outline over a slightly lighter fill — not the
// chunky filled WinForms button.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LbApiHost.Host.Notifications;

internal static class NotificationStyle
{
    // Near-black card over the app's dark grey, like LaunchBox's — the popup must read as "above" the
    // window without a loud border doing the work.
    public static Color CardBack => Color.FromArgb(32, 33, 39);
    public static Color CardBorder => Color.FromArgb(70, 72, 84);
    public static Color BtnBack => Color.FromArgb(43, 45, 54);
    public static Color BtnHover => Color.FromArgb(60, 64, 78);
    public static Color BtnBorder => Color.FromArgb(94, 97, 112);

    /// <summary>Rounded-rect path. Callers dispose it.</summary>
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Clips a control to rounded corners. Re-call after any resize — the region doesn't follow.</summary>
    public static void Round(Control c, int radius)
    {
        using var path = RoundedPath(new Rectangle(0, 0, c.Width, c.Height), radius);
        var old = c.Region;
        c.Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>Paints the card border just inside the clipped edge.</summary>
    public static void PaintBorder(Graphics g, Size size, int radius, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, size.Width - 1, size.Height - 1), radius);
        using var pen = new Pen(color);
        g.DrawPath(pen, path);
    }

    // ── The app glyph: litebox.ico at the asked size, cached. The multi-size ico picks its best frame;
    //    a miss (resource renamed…) yields null and the caller falls back to a drawn glyph. ─────────────
    private static readonly ConcurrentDictionary<int, Image?> _appIcon = new();

    public static Image? AppIcon(int size) => _appIcon.GetOrAdd(size, LoadAppIcon);

    private static Image? LoadAppIcon(int size)
    {
        try
        {
            using var s = typeof(NotificationStyle).Assembly.GetManifestResourceStream("LbApiHost.litebox.ico");
            if (s == null) return null;
            using var ico = new Icon(s, size, size);
            using var raw = ico.ToBitmap();
            if (raw.Width == size && raw.Height == size) return new Bitmap(raw);
            var dst = new Bitmap(size, size);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(raw, new Rectangle(0, 0, size, size));
            return dst;
        }
        catch { return null; }
    }
}

/// <summary>The button a notification card carries: flat rounded rect, 1px outline, lighter on hover —
/// LaunchBox's "Open Plugin Manager" look. A stock WinForms Button can't do the rounded outline without
/// clipping its own square border, so this draws itself.</summary>
internal sealed class CardButton : Control
{
    private bool _hover;

    public CardButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        BackColor = NotificationStyle.CardBack;   // the corners outside the rounded rect show this
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = (int)Math.Round(4 * DeviceDpi / 96f);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = NotificationStyle.RoundedPath(r, radius);
        using (var b = new SolidBrush(_hover ? NotificationStyle.BtnHover : NotificationStyle.BtnBack)) g.FillPath(b, path);
        using (var p = new Pen(NotificationStyle.BtnBorder)) g.DrawPath(p, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
