// Shared background composition for the full-screen game overlays (pause, startup,
// end). Extracted from LegacyPauseScreen so the pause screen, the "NOW LOADING…"
// startup screen and the "GAME OVER" end screen all look identical: the game's
// fanart scaled-to-cover at low opacity, the clear logo (or title text), a tilted
// box-front accent, plus EITHER a small status line (pause) OR a big centred banner
// (startup / end). Pre-composited into one bitmap off the UI thread — the overlay
// forms just blit it (no transparent stacked controls = no repaint "waves").

#nullable enable

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LbApiHost.Host.Pause;

internal static class ScreenArt
{
    public static readonly Color Bg = Color.FromArgb(14, 14, 20);
    public static readonly Color Fg = Color.FromArgb(235, 235, 235);
    public static readonly Color Dim = Color.FromArgb(165, 165, 175);

    /// <summary>
    /// Composes the overlay background. <paramref name="statusLine"/> (pause) draws a
    /// small dim line under the logo; <paramref name="bannerText"/> (startup / end)
    /// draws a large centred banner over a translucent band. Either may be null.
    /// </summary>
    public static Bitmap Compose(Size size, PauseContext ctx, string? statusLine, string? bannerText)
    {
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Bg);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var full = new Rectangle(Point.Empty, size);

        // 1. Fanart, scaled to COVER, low opacity.
        using (var fanart = LoadBitmap(ctx.FanartPath))
            if (fanart != null)
            {
                float ir = (float)fanart.Width / fanart.Height, ar = (float)full.Width / Math.Max(1, full.Height);
                int w, h;
                if (ir > ar) { h = full.Height; w = (int)(h * ir); } else { w = full.Width; h = (int)(w / ir); }
                var att = new ImageAttributes();
                att.SetColorMatrix(new ColorMatrix { Matrix33 = 0.22f });
                g.DrawImage(fanart, new Rectangle((full.Width - w) / 2, (full.Height - h) / 2, w, h),
                    0, 0, fanart.Width, fanart.Height, GraphicsUnit.Pixel, att);
            }

        // 2. Box front accent, bottom-right, slightly tilted.
        using (var box = LoadBitmap(ctx.BoxFrontPath))
            if (box != null)
            {
                int bh = Math.Min(280, full.Height / 3);
                int bw = (int)(bh * (float)box.Width / Math.Max(1, box.Height));
                int x = full.Right - bw - 70, y = full.Bottom - bh - 60;
                var st = g.Save();
                g.TranslateTransform(x + bw / 2f, y + bh / 2f);
                g.RotateTransform(-4f);
                var att = new ImageAttributes();
                att.SetColorMatrix(new ColorMatrix { Matrix33 = 0.92f });
                g.DrawImage(box, new Rectangle(-bw / 2, -bh / 2, bw, bh), 0, 0, box.Width, box.Height, GraphicsUnit.Pixel, att);
                g.Restore(st);
            }

        // 3. Logo (centred top) or title text.
        bool logoDrawn = false;
        using (var logo = LoadBitmap(ctx.ClearLogoPath))
            if (logo != null)
            {
                int lh = 120;
                int lw = (int)(lh * (float)logo.Width / Math.Max(1, logo.Height));
                if (lw > full.Width - 160) { lw = full.Width - 160; lh = (int)(lw * (float)logo.Height / Math.Max(1, logo.Width)); }
                g.DrawImage(logo, new Rectangle((full.Width - lw) / 2, 44, lw, lh));
                logoDrawn = true;
            }
        if (!logoDrawn)
        {
            using var f = new Font("Segoe UI", 26f, FontStyle.Bold);
            using var br = new SolidBrush(Fg);
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(ctx.GameTitle, f, br, new RectangleF(0, 64, full.Width, 60), sf);
        }

        // 4a. Status line (pause).
        if (!string.IsNullOrEmpty(statusLine))
            using (var f2 = new Font("Segoe UI", 11f))
            using (var br2 = new SolidBrush(Dim))
            {
                var sf2 = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString(statusLine, f2, br2, new RectangleF(0, 174, full.Width, 26), sf2);
            }

        // 4b. Big centred banner (startup / end): a translucent full-width band + text,
        //     matching LaunchBox's "NOW LOADING…" / "GAME OVER" overlays.
        if (!string.IsNullOrEmpty(bannerText))
        {
            int bandH = Math.Max(120, full.Height / 7);
            int bandY = (full.Height - bandH) / 2;
            using (var band = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                g.FillRectangle(band, 0, bandY, full.Width, bandH);
            using var f = new Font("Segoe UI", 34f, FontStyle.Regular);
            using var br = new SolidBrush(Fg);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(bannerText, f, br, new RectangleF(0, bandY, full.Width, bandH), sf);
        }

        return bmp;
    }

    /// <summary>File → independent Bitmap (no file lock kept). Null on any error.</summary>
    public static Bitmap? LoadBitmap(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }
}
