// Right-panel store-achievements card (GOG today; Steam later) — LiteBox-native, owner-drawn.
// A trimmed sibling of RetroAchievementsCard: same collapsed/expanded badge-grid look, but no
// "time to beat/master" medians (stores don't have them) and the badge art comes from arbitrary
// URLs (StoreBadges) rather than RA's fixed Badge/<name>.png. The title is configurable so the
// same control serves "GOG Achievements" / "Steam Achievements".

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace LbApiHost.Host.Store;

internal sealed class StoreAchievementsCard : Panel
{
    private static readonly Color Box = Color.FromArgb(40, 40, 44);
    private static readonly Color BorderC = Color.FromArgb(58, 58, 62);
    private static readonly Color Fg = Color.FromArgb(208, 208, 210);
    private static readonly Color SubFg = Color.FromArgb(140, 140, 144);
    private static readonly Color Accent = Color.FromArgb(150, 170, 210);
    private static readonly Color Placeholder = Color.FromArgb(52, 52, 56);

    private const int Pad = 10, VMargin = 4, ChevW = 16;
    private const int Badge = 40, BadgeGap = 6;

    // Locked-badge filter: luminosity grayscale, dimmed to ~60% brightness + 85% alpha, so earned
    // (full-colour) badges clearly stand out from the desaturated locked ones.
    private static readonly ImageAttributes LockedAttr = BuildLockedAttr();
    private static ImageAttributes BuildLockedAttr()
    {
        const float d = 0.6f;
        var cm = new ColorMatrix(new[]
        {
            new[] { 0.30f * d, 0.30f * d, 0.30f * d, 0f, 0f },
            new[] { 0.59f * d, 0.59f * d, 0.59f * d, 0f, 0f },
            new[] { 0.11f * d, 0.11f * d, 0.11f * d, 0f, 0f },
            new[] { 0f, 0f, 0f, 0.85f, 0f },
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
        var ia = new ImageAttributes();
        ia.SetColorMatrix(cm);
        return ia;
    }

    private readonly Font _titleFont = new("Segoe UI Semibold", 10.5f);
    private readonly Font _font = new("Segoe UI", 9f);

    private StoreAchCache? _data;
    private bool _expanded;
    private bool _loading;
    private bool _badgeLoadStarted;

    private readonly object _imgGate = new();
    private readonly Dictionary<string, Image> _badgeImg = new(StringComparer.Ordinal);   // ach id → badge image
    private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 250, ReshowDelay = 80, AutoPopDelay = 20000 };
    private string _hoverId = "";

    /// <summary>Card heading, e.g. "GOG Achievements" — set before Show.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "Achievements";

    public Action? ExpandedChanged;   // persist the open/closed toggle across selections
    public Action? LayoutChanged;     // ask the host to re-measure this row's height

    public StoreAchievementsCard()
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

    /// <summary>Show one game's store achievements. null ⇒ clear/hide.</summary>
    public void Show(StoreAchCache? data)
    {
        _data = data; _loading = false; _badgeLoadStarted = false; _hoverId = "";
        DisposeBadges();
        try { _tip.SetToolTip(this, ""); } catch { }
        if (_expanded && HasData) KickBadgeLoads();
        Invalidate();
        LayoutChanged?.Invoke();
    }

    /// <summary>Brief "loading…" box while the background read runs.</summary>
    public void ShowLoading()
    {
        _data = null; _loading = true; _badgeLoadStarted = false;
        DisposeBadges();
        Invalidate();
        LayoutChanged?.Invoke();
    }

    /// <summary>Empties the card so it renders nothing and its row collapses to 0.</summary>
    public void HidePanel()
    {
        if (_data == null && !_loading) return;
        _data = null; _loading = false; DisposeBadges();
        Invalidate(); LayoutChanged?.Invoke();
    }

    // ── layout geometry ──────────────────────────────────────────────────────────────────────
    private int HeaderBottom() => VMargin + Pad + _titleFont.Height;

    private int PerRow(int cardWidth)
    {
        int innerW = Math.Max(20, cardWidth - 2 * Pad);
        return Math.Max(1, (innerW + BadgeGap) / (Badge + BadgeGap));
    }

    public int HeightForWidth(int cardWidth)
    {
        if (!HasData && !_loading) return 0;
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

        string id = "";
        if (_expanded && _data != null)
            foreach (var (idx, r) in BadgeRects())
                if (r.Contains(e.Location)) { id = _data.achievements[idx].id ?? ""; break; }

        if (id != _hoverId)
        {
            _hoverId = id;
            string text = "";
            if (id.Length > 0)
            {
                var a = _data!.achievements.Find(x => x.id == id);
                if (a != null)
                {
                    string state = a.unlocked
                        ? "✓ Unlocked" + (FormatDate(a.unlockedAt) is string d ? "  ·  " + d : "")
                        : "🔒 Locked";
                    string rarity = a.rarity > 0 ? $"\n{a.rarity.ToString("0.#", CultureInfo.InvariantCulture)}% of players" : "";
                    text = $"{a.title}\n{state}{rarity}"
                         + (string.IsNullOrEmpty(a.description) ? "" : "\n" + a.description);
                }
            }
            try { _tip.SetToolTip(this, text); } catch { }
        }
    }

    private static string? FormatDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null;
    }

    // ── badge image loading (one background loop, cached on disk by StoreBadges) ────────────────
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
                string key = a.id ?? "";
                if (key.Length == 0) continue;
                lock (_imgGate) { if (_badgeImg.ContainsKey(key)) continue; }
                var url = a.unlocked ? a.badgeUnlocked : (a.badgeLocked ?? a.badgeUnlocked);
                var path = StoreBadges.Get(url);
                if (path == null) continue;
                try
                {
                    Image img;
                    using (var fs = File.OpenRead(path)) img = Image.FromStream(fs);  // copy out, no file lock
                    if (_data != data) { img.Dispose(); return; }
                    lock (_imgGate) _badgeImg[key] = img;
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
            TextRenderer.DrawText(g, Title + " — loading…", _titleFont, new Point(x, y), SubFg);
            return;
        }

        // header: title (left) + "u/total" (right, before chevron) + chevron
        TextRenderer.DrawText(g, Title, _titleFont, new Point(x, y), Fg, TextFormatFlags.NoPadding);
        string count = $"{_data!.unlocked} / {_data.total}";
        var cntSz = TextRenderer.MeasureText(count, _font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding);
        int cntX = Pad + innerW - ChevW - cntSz.Width;
        TextRenderer.DrawText(g, count, _font, new Rectangle(cntX, y, cntSz.Width, _titleFont.Height),
            _data.unlocked > 0 ? Accent : SubFg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        DrawChevron(g, Pad + innerW - ChevW / 2, y + _titleFont.Height / 2, _expanded);

        if (!_expanded) return;

        // badge grid
        foreach (var (idx, r) in BadgeRects())
        {
            var a = _data.achievements[idx];
            Image? img; lock (_imgGate) _badgeImg.TryGetValue(a.id ?? "", out img);
            if (img != null)
            {
                // Locked → render greyed + dimmed (GOG serves a coloured "?" for locked; we desaturate it
                // so earned badges clearly stand out). Unlocked → full colour.
                if (a.unlocked) g.DrawImage(img, r);
                else g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, LockedAttr);
            }
            else
            {
                using var ph = new SolidBrush(Placeholder);
                using var pp = Rounded(r, 5);
                g.FillPath(ph, pp);
            }
        }
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
