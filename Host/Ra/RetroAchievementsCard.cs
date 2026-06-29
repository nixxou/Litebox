// Right-panel RetroAchievements card — LiteBox-native, owner-drawn (mirrors MetaCard's look but with its
// own muted palette and no dependency on MainWindow internals).
//
//   collapsed → short box: "RetroAchievements   <unlocked>/<total>" + a "Beat / Mastered" time line
//               (the medians; "—" placeholder when the game XML hasn't got them yet).
//   expanded  → the full badge grid: each achievement coloured when the user unlocked it, greyed (RA's
//               _lock badge) otherwise. Hover a badge for its title / points / description.
//
// Data is handed in by MainWindow (RaService cache/API + medians from the game XML). This control only
// renders and lazy-loads badge PNGs (RaBadges) off the UI thread.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace LbApiHost.Host.Ra;

internal sealed class RetroAchievementsCard : Panel
{
    // Muted palette (deliberately softer than BigBox's vivid panel; close to MetaCard's box).
    private static readonly Color Box = Color.FromArgb(40, 40, 44);
    private static readonly Color BorderC = Color.FromArgb(58, 58, 62);
    private static readonly Color Fg = Color.FromArgb(208, 208, 210);
    private static readonly Color SubFg = Color.FromArgb(140, 140, 144);
    private static readonly Color Accent = Color.FromArgb(150, 170, 210);
    private static readonly Color Placeholder = Color.FromArgb(52, 52, 56);

    private const int Pad = 10, VMargin = 4, ChevW = 16;
    private const int Badge = 40, BadgeGap = 6;

    private readonly Font _titleFont = new("Segoe UI Semibold", 10.5f);
    private readonly Font _font = new("Segoe UI", 9f);

    private RaGameCache? _data;
    private int _beatMin, _masterMin;
    private bool _expanded;
    private bool _loading;
    private bool _badgeLoadStarted;

    private readonly object _imgGate = new();
    private readonly Dictionary<int, Image> _badgeImg = new();   // ach id → loaded badge image
    private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 250, ReshowDelay = 80, AutoPopDelay = 20000 };
    private int _hoverId = -1;

    public Action? ExpandedChanged;   // persist the open/closed toggle across selections
    public Action? LayoutChanged;     // ask the host to re-measure this row's height

    public RetroAchievementsCard()
    {
        DoubleBuffered = true; ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public bool HasData => _data != null && _data.total > 0;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; if (_expanded) KickBadgeLoads(); Invalidate(); } }
    }

    /// <summary>Show one game's RA data + the median commitments (minutes). null ⇒ clear/hide.</summary>
    public void Show(RaGameCache? data, int beatMinutes, int masterMinutes)
    {
        _data = data; _beatMin = beatMinutes; _masterMin = masterMinutes;
        _loading = false; _badgeLoadStarted = false; _hoverId = -1;
        DisposeBadges();
        try { _tip.SetToolTip(this, ""); } catch { }
        if (_expanded && HasData) KickBadgeLoads();
        Invalidate();
        LayoutChanged?.Invoke();
    }

    /// <summary>Show a brief "loading…" box while the background fetch runs (first view of a game).</summary>
    public void ShowLoading()
    {
        _data = null; _loading = true; _badgeLoadStarted = false;
        DisposeBadges();
        Invalidate();
        LayoutChanged?.Invoke();
    }

    /// <summary>Empties the card so it renders nothing and its row collapses to 0 (named to not shadow
    /// Control.Hide, which only flips Visible).</summary>
    public void HidePanel()
    {
        if (_data == null && !_loading) return;
        _data = null; _loading = false; DisposeBadges();
        Invalidate(); LayoutChanged?.Invoke();
    }

    // ── layout geometry (shared by measure / paint / hit-test) ───────────────────────────────
    private int HeaderBottom()
    {
        int y = VMargin + Pad + _titleFont.Height;
        if (HasData) y += 4 + _font.Height;       // the Beat/Mastered line is always shown when we have data
        return y;
    }

    private int PerRow(int cardWidth)
    {
        int innerW = Math.Max(20, cardWidth - 2 * Pad);
        return Math.Max(1, (innerW + BadgeGap) / (Badge + BadgeGap));
    }

    public int HeightForWidth(int cardWidth)
    {
        if (!HasData && !_loading) return 0;       // hidden — no row
        if (_loading && !HasData) return VMargin + Pad + _titleFont.Height + Pad + VMargin;
        int y = HeaderBottom();
        if (_expanded && _data!.achievements.Count > 0)
        {
            int perRow = PerRow(cardWidth);
            int rows = (_data.achievements.Count + perRow - 1) / perRow;
            y += 8 + rows * (Badge + BadgeGap) - BadgeGap;
        }
        return y + Pad + VMargin;
    }

    private IEnumerable<(int idx, Rectangle r)> BadgeRects()
    {
        if (_data == null) yield break;
        int perRow = PerRow(ClientSize.Width);
        int top = HeaderBottom() + 8;
        for (int i = 0; i < _data.achievements.Count; i++)
        {
            int col = i % perRow, row = i / perRow;
            yield return (i, new Rectangle(Pad + col * (Badge + BadgeGap), top + row * (Badge + BadgeGap), Badge, Badge));
        }
    }

    // ── interaction ──────────────────────────────────────────────────────────────────────────
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!HasData) return;
        // Only a click in the header band toggles — clicks on badges leave the grid open.
        if (e.Y <= HeaderBottom())
        {
            _expanded = !_expanded;
            if (_expanded) KickBadgeLoads();
            Invalidate(); ExpandedChanged?.Invoke(); LayoutChanged?.Invoke();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool inHeader = HasData && e.Y <= HeaderBottom();
        Cursor = inHeader ? Cursors.Hand : Cursors.Default;

        int id = -1;
        if (_expanded && _data != null)
            foreach (var (idx, r) in BadgeRects())
                if (r.Contains(e.Location)) { id = _data.achievements[idx].id; break; }

        if (id != _hoverId)
        {
            _hoverId = id;
            string text = "";
            if (id >= 0)
            {
                var a = _data!.achievements.Find(x => x.id == id);
                if (a != null)
                    text = $"{a.title}  ·  {a.points} pts\n{(a.unlocked ? "✓ Débloqué" : "🔒 Verrouillé")}"
                         + (string.IsNullOrEmpty(a.description) ? "" : "\n" + a.description);
            }
            try { _tip.SetToolTip(this, text); } catch { }
        }
    }

    // ── badge image loading (one background loop, cached on disk by RaBadges) ──────────────────
    private void KickBadgeLoads()
    {
        if (_data == null || _badgeLoadStarted) return;
        _badgeLoadStarted = true;
        var data = _data;
        System.Threading.Tasks.Task.Run(() =>
        {
            int sinceRepaint = 0;
            foreach (var a in data.achievements)
            {
                if (_data != data) return;                 // selection changed → abandon
                lock (_imgGate) { if (_badgeImg.ContainsKey(a.id)) continue; }
                var path = RaBadges.Get(a.badge, a.unlocked);
                if (path == null) continue;
                try
                {
                    Image img;
                    using (var fs = File.OpenRead(path)) img = Image.FromStream(fs);  // copy out, no file lock
                    if (_data != data) { img.Dispose(); return; }
                    lock (_imgGate) _badgeImg[a.id] = img;
                    if (++sinceRepaint >= 4) { sinceRepaint = 0; SafeInvalidate(); }
                }
                catch { }
            }
            SafeInvalidate();
        });
    }

    private void SafeInvalidate()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate)); } catch { }
    }

    // ── paint ──────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (!HasData && !_loading) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var box = new Rectangle(0, VMargin, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 2 * VMargin - 1));
        using (var path = Rounded(box, 8))
        {
            using var bg = new SolidBrush(Box); g.FillPath(bg, path);
            using var bd = new Pen(BorderC); g.DrawPath(bd, path);
        }

        int innerW = Math.Max(20, ClientSize.Width - 2 * Pad);
        int x = Pad, y = VMargin + Pad;

        if (_loading && !HasData)
        {
            TextRenderer.DrawText(g, "RetroAchievements — chargement…", _titleFont, new Point(x, y), SubFg);
            return;
        }

        // header: title (left) + "u/total" (right, before chevron) + chevron
        TextRenderer.DrawText(g, "RetroAchievements", _titleFont, new Point(x, y), Fg, TextFormatFlags.NoPadding);
        string count = $"{_data!.unlocked} / {_data.total}";
        var cntSz = TextRenderer.MeasureText(count, _font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding);
        int cntX = Pad + innerW - ChevW - cntSz.Width;
        TextRenderer.DrawText(g, count, _font, new Rectangle(cntX, y, cntSz.Width, _titleFont.Height),
            _data.unlocked > 0 ? Accent : SubFg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        DrawChevron(g, Pad + innerW - ChevW / 2, y + _titleFont.Height / 2, _expanded);
        y += _titleFont.Height + 4;

        // medians line: "Beat  12h 26m      Mastered  19h 53m" ("—" until the XML carries them)
        DrawCommit(g, ref x, y, "Beat", _beatMin);
        x += 22;
        DrawCommit(g, ref x, y, "Mastered", _masterMin);
        y += _font.Height;

        if (!_expanded) return;

        // badge grid
        foreach (var (idx, r) in BadgeRects())
        {
            var a = _data.achievements[idx];
            Image? img; lock (_imgGate) _badgeImg.TryGetValue(a.id, out img);
            if (img != null)
            {
                g.DrawImage(img, r);
            }
            else
            {
                using var ph = new SolidBrush(Placeholder);
                using var pp = Rounded(r, 5);
                g.FillPath(ph, pp);
            }
        }
    }

    private void DrawCommit(Graphics g, ref int x, int y, string label, int minutes)
    {
        var lblSz = TextRenderer.MeasureText(label + "  ", _font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, label + "  ", _font, new Point(x, y), SubFg, TextFormatFlags.NoPadding);
        x += lblSz.Width;
        string val = RaFields.Duration(minutes);
        if (string.IsNullOrEmpty(val)) val = "—";
        var valSz = TextRenderer.MeasureText(val, _font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, val, _font, new Point(x, y), minutes > 0 ? Fg : SubFg, TextFormatFlags.NoPadding);
        x += valSz.Width;
    }

    private static void DrawChevron(Graphics g, int cx, int cy, bool expanded)
    {
        const int s = 4;
        using var pen = new Pen(Color.FromArgb(170, 170, 172), 1.8f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        Point[] pts = expanded
            ? new[] { new Point(cx - s, cy - s / 2), new Point(cx, cy + s / 2), new Point(cx + s, cy - s / 2) }
            : new[] { new Point(cx - s / 2, cy - s), new Point(cx + s / 2, cy), new Point(cx - s / 2, cy + s) };
        g.DrawLines(pen, pts);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void DisposeBadges()
    {
        lock (_imgGate)
        {
            foreach (var im in _badgeImg.Values) { try { im.Dispose(); } catch { } }
            _badgeImg.Clear();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _titleFont.Dispose(); _font.Dispose(); _tip.Dispose(); DisposeBadges(); }
        base.Dispose(disposing);
    }
}
