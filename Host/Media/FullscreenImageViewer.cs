// Fullscreen image viewer (LB parity): double-click the detail pane's main image. Black backdrop,
// aspect-fit rendering (nearest-neighbour when heavily upscaled — retro screenshots stay crisp),
// a caption line at the bottom ("Image X of Y: <LB type> | WxH" — origin/ADS info can join later),
// and ←/→ / wheel / hover-chevron navigation across the game's IMAGE items only — the 3D model
// (and any future video item) never enters this list. Esc, the top-right X or double-click closes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LbApiHost.Host.Media;

internal sealed class FullscreenImageViewer : Form
{
    private readonly List<string> _paths;
    private readonly Func<string, Image?> _load;   // MainWindow's LoadImage (WebP-capable)
    private int _ix;
    private Image? _img;
    private int _hoverZone;   // -1 = left chevron zone, 1 = right, 0 = none

    private int ZoneW => Math.Max(60, ClientSize.Width / 8);

    public FullscreenImageViewer(List<string> paths, int start, Func<string, Image?> load)
    {
        _paths = paths;
        _load = load;
        _ix = Math.Max(0, Math.Min(paths.Count - 1, start));

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.ResizeRedraw, true);

        // NOTE: keyboard handling lives in ProcessCmdKey (see below), never in KeyDown — ProcessCmdKey runs
        // first and consumes the key, so a KeyDown handler for Esc / ←/→ here would never fire at all.
        MouseDoubleClick += (_, _) => Close();
        MouseWheel += (_, e) => Nav(e.Delta < 0 ? +1 : -1);
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.X < ZoneW) Nav(-1);
            else if (e.X > ClientSize.Width - ZoneW) Nav(+1);
        };
        MouseMove += (_, e) =>
        {
            int z = _paths.Count > 1 ? (e.X < ZoneW ? -1 : e.X > ClientSize.Width - ZoneW ? 1 : 0) : 0;
            if (z != _hoverZone) { _hoverZone = z; Cursor = z != 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        };
        MouseLeave += (_, _) => { if (_hoverZone != 0) { _hoverZone = 0; Invalidate(); } };

        var close = new FullscreenCloseButton();
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();

        void PlaceClose() => close.Location = new Point(
            Math.Max(0, ClientSize.Width - close.Width - 14), 14);
        Resize += (_, _) => PlaceClose();
        Shown += (_, _) => { PlaceClose(); close.BringToFront(); Focus(); };

        LoadCurrent();
    }

    private void Nav(int d)
    {
        if (_paths.Count < 2) return;
        _ix = (_ix + d + _paths.Count) % _paths.Count;   // wraps
        LoadCurrent();
    }

    // Arrow keys are normally treated as WinForms dialog-navigation keys before KeyDown reaches
    // the form (notably since the close control was added). Catch them at command-key level so
    // image navigation keeps working regardless of which child currently owns the focus.
    // Alt/Ctrl combinations are left alone — only the bare keys navigate.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & (Keys.Alt | Keys.Control)) != Keys.None) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
                Nav(-1);
                return true;
            case Keys.Right:
                Nav(+1);
                return true;
            case Keys.Escape:
                Close();
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    private void LoadCurrent()
    {
        Image? img = null;
        try { img = _load(_paths[_ix]); } catch { }
        var old = _img; _img = img; old?.Dispose();
        Invalidate();
    }

    // The LB type of an image = the folder directly under Images\<Platform>\ (region subfolders and the
    // file itself excluded). Paths outside an Images tree fall back to their parent folder's name.
    private static string TypeLabel(string path)
    {
        try
        {
            var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Equals("Images", StringComparison.OrdinalIgnoreCase))
                    return i + 2 < parts.Length - 1 ? parts[i + 2] : "";
            return Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        }
        catch { return ""; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var area = ClientRectangle;
        const int CaptionH = 34;   // reserved caption band at the bottom (LB-like)
        var box = new Rectangle(area.X, area.Y, area.Width, Math.Max(1, area.Height - CaptionH));

        if (_img != null)
        {
            // Fit inside the screen, but never enlarge beyond twice the image's native pixel size.
            double scale = Math.Min(2.0, Math.Min(
                (double)box.Width / Math.Max(1, _img.Width),
                (double)box.Height / Math.Max(1, _img.Height)));
            int iw = Math.Max(1, (int)Math.Round(_img.Width * scale));
            int ih = Math.Max(1, (int)Math.Round(_img.Height * scale));
            // Heavy upscales (small retro screenshots) look right with hard pixels; scans stay bicubic.
            bool pixelArt = iw >= _img.Width * 2;
            g.InterpolationMode = pixelArt ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                                           : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_img, box.X + (box.Width - iw) / 2, box.Y + (box.Height - ih) / 2, Math.Max(1, iw), Math.Max(1, ih));
        }

        // Caption: "Image X of Y: <type> | WxH" (origin / ADS info will extend this line later).
        string label = TypeLabel(_paths[_ix]);
        string dims = _img != null ? $"{_img.Width}x{_img.Height}" : "unavailable";
        string caption = $"Image {_ix + 1} of {_paths.Count}" + (label.Length > 0 ? $": {label}" : "") + $" | {dims}";
        TextRenderer.DrawText(g, caption, Font, new Rectangle(0, area.Bottom - CaptionH, area.Width, CaptionH),
            Color.FromArgb(200, 200, 205), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Hover chevrons (only when there is somewhere to go).
        if (_hoverZone != 0 && _paths.Count > 1)
            DrawChevron(g, _hoverZone > 0, box);
    }

    private void DrawChevron(Graphics g, bool right, Rectangle box)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cy = box.Y + box.Height / 2;
        int cx = right ? box.Right - ZoneW / 2 : box.X + ZoneW / 2;
        const int R = 24, s = 9;
        using (var bg = new SolidBrush(Color.FromArgb(150, 25, 25, 28)))
            g.FillEllipse(bg, cx - R, cy - R, 2 * R, 2 * R);
        using var pen = new Pen(Color.White, 2.4f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        int dx = right ? s / 2 : -s / 2;
        g.DrawLines(pen, new[] { new Point(cx - dx, cy - s), new Point(cx + dx, cy), new Point(cx - dx, cy + s) });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _img?.Dispose(); _img = null; }
        base.Dispose(disposing);
    }
}
