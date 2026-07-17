// Desktop "Related Games" UI — the WinForms counterpart of the web theme's OVERVIEW / RELATED GAMES
// tabs (launchbox-web app.js related-card renderer). Two controls, both consumed by MainWindow's
// detail pane:
//
//   • DetailTabStrip    — a compact, flat, web-style tab strip (uppercase labels, accent underline on
//                         the active one, hairline base). Used twice: the primary OVERVIEW/RELATED GAMES
//                         switch and the secondary RECOMMENDED/SIMILAR/POSSIBLE PORTS strip inside the
//                         related panel. Deliberately discreet — no TabControl chrome.
//   • RelatedGamesPanel — the Related tab's content: secondary strip + an internally-scrolling list of
//                         suggestion cards (thumb, title, platform·year, 2-line description, match %).
//
// Data comes from the native suggester engine (GameSuggester.RunAll — one run covers all three
// categories) + RelatedProvider.Overviews for the cloud descriptions (Extended DB). Thumbs: local games
// resolve through the SAME art chain as the detail pane (delegate injected by MainWindow); DB-only games
// fetch the cover via MetadataDb.ImagesForGame → MediaProxy.PickCover → MediaFetch.FetchBytes (the exact
// chain behind the web's /api/media/{dbid}.jpg).
//
// Threading: one background run per game, latest-wins token; card thumbs load lazily on the pool and
// land via BeginInvoke. The engine run is only triggered when the Related tab is actually shown
// (EnsureLoaded) so plain browsing never pays for it; results are cached per game id for the session's
// current selection (flipping tabs back and forth is free).
//
// Clicking a card: local game → MainWindow.SelectGameById (same bridge ExtendDB's viewer used);
// DB-only game → the LaunchBox Games DB page in the default browser.

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Similar;

// ── Compact tab strip ───────────────────────────────────────────────────────────────────────────────

/// <summary>Flat web-style tab strip: uppercase labels, active = Fg + accent underline, inactive =
/// SubFg, full-width hairline underneath. Primary flavour is slightly larger than secondary.</summary>
internal sealed class DetailTabStrip : Panel
{
    private readonly Font _font;
    private readonly bool _hairline;
    private string[] _tabs = Array.Empty<string>();
    private Rectangle[] _hits = Array.Empty<Rectangle>();
    private int _selected;
    private int _hover = -1;

    public Action<int> SelectedChanged;

    public DetailTabStrip(bool primary)
    {
        DoubleBuffered = true; ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _font = new Font("Segoe UI Semibold", primary ? 9.5f : 8.25f);
        _hairline = primary;
    }

    public void SetTabs(params string[] labels)
    {
        _tabs = labels ?? Array.Empty<string>();
        _hits = new Rectangle[_tabs.Length];
        if (_selected >= _tabs.Length) _selected = 0;
        Invalidate();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => _selected;
        set { if (value >= 0 && value < _tabs.Length && _selected != value) { _selected = value; Invalidate(); } }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 && h != _selected ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int h = HitTest(e.Location);
        if (h >= 0 && h != _selected) { _selected = h; Invalidate(); SelectedChanged?.Invoke(h); }
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _hits.Length; i++)
            if (_hits[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int baseY = ClientSize.Height - 2;   // underline sits on the strip's bottom edge

        if (_hairline)
            using (var pen = new Pen(Color.FromArgb(64, 64, 68)))
                g.DrawLine(pen, 0, baseY + 1, ClientSize.Width, baseY + 1);

        const TextFormatFlags tf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int x = 2, gap = 22;
        for (int i = 0; i < _tabs.Length; i++)
        {
            string label = _tabs[i];
            var sz = TextRenderer.MeasureText(g, label, _font, Size.Empty, tf);
            int y = baseY - 4 - sz.Height;
            var fg = i == _selected ? LiteBoxTheme.Fg
                   : i == _hover    ? ControlPaint.Light(LiteBoxTheme.SubFg)
                                    : LiteBoxTheme.SubFg;
            TextRenderer.DrawText(g, label, _font, new Point(x, y), fg, tf);
            _hits[i] = new Rectangle(x - 4, 0, sz.Width + 8, ClientSize.Height);
            if (i == _selected)
                using (var pen = new Pen(LiteBoxTheme.Accent, 2f))
                    g.DrawLine(pen, x, baseY, x + sz.Width, baseY);
            x += sz.Width + gap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

// ── Related panel ───────────────────────────────────────────────────────────────────────────────────

/// <summary>The Related tab's content: RECOMMENDED / SIMILAR / POSSIBLE PORTS sub-strip + an
/// internally-scrolling card list fed by the native suggester engine.</summary>
internal sealed class RelatedGamesPanel : Panel
{
    private const int Limit = 30;          // per category (matches the web related list's scale)

    /// <summary>Parental gate applied to every candidate (injected: MainWindow owns the state).</summary>
    public Func<CandidateGame, bool> CandidateFilter;
    /// <summary>Resolves a LOCAL game's box-art path off the UI thread (same chain as the detail pane).</summary>
    public Func<string, string> LocalArtResolver;
    /// <summary>Navigates the main window to an owned game (IGame.Id).</summary>
    public Action<string> OpenLocalGame;

    /// <summary>The scrollable viewport — exposed so MainWindow can apply its dark-scrollbar theming.</summary>
    public Control ScrollHost => _scroll;

    private static readonly SuggesterCategory[] TabCats =
    {
        SuggesterCategory.RecommendedGames, SuggesterCategory.SimilarGames, SuggesterCategory.PossiblePorts,
    };

    private readonly DetailTabStrip _subTabs;
    private readonly Panel _scroll;
    private readonly Label _status;

    private IGame _game;                   // current subject (set by ShowFor)
    private string _loadedId;              // game id the cached results belong to
    private int _token;                    // latest-wins guard for the background run
    private bool _running;
    private Dictionary<SuggesterCategory, List<CardData>> _results;

    private sealed class CardData
    {
        public string GameId;              // local IGame.Id ("" for DB-only)
        public int DbId;
        public bool IsLocal;
        public string Title, Sub, Desc;
        public int Pct;
        public string LocalArt;            // resolved art path for local games (may be null)
    }

    public RelatedGamesPanel()
    {
        DoubleBuffered = true;
        _subTabs = new DetailTabStrip(primary: false) { Dock = DockStyle.Top, Height = 24, BackColor = LiteBoxTheme.PanelC };
        _subTabs.SetTabs("RECOMMENDED", "SIMILAR", "POSSIBLE PORTS");
        _subTabs.SelectedChanged = _ => RenderCurrent();

        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = LiteBoxTheme.PanelC };
        _scroll.Resize += (_, _) => LayoutCards();

        _status = new Label
        {
            Dock = DockStyle.Top, Height = 46, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.PanelC,
            Font = new Font("Segoe UI", 9f), Padding = new Padding(2, 0, 0, 0), Visible = false,
        };
        _scroll.Controls.Add(_status);

        Controls.Add(_scroll);
        Controls.Add(_subTabs);
    }

    // ── Public lifecycle (all UI thread) ────────────────────────────────────────────────────────────

    /// <summary>New detail-pane subject. Drops stale results; computes now only when the Related tab is
    /// the visible one (<paramref name="active"/>), else lazily on first EnsureLoaded.</summary>
    public void ShowFor(IGame g, bool active)
    {
        _game = g;
        string id = Safe(() => g?.Id) ?? "";
        if (!string.Equals(id, _loadedId, StringComparison.OrdinalIgnoreCase))
        {
            _results = null; _loadedId = null;
            _token++;                      // invalidate any in-flight run for the previous game
            _running = false;
            ClearCards();
        }
        if (active) EnsureLoaded();
    }

    /// <summary>Node selected / pane cleared — drop everything.</summary>
    public void ClearAll()
    {
        _game = null; _results = null; _loadedId = null; _running = false;
        _token++;
        ClearCards();
    }

    /// <summary>Kick the engine for the current game if its results aren't already in. Called when the
    /// Related tab becomes visible (and by ShowFor when it already is).</summary>
    public void EnsureLoaded()
    {
        if (_game == null) { ClearCards(); return; }
        string id = Safe(() => _game.Id) ?? "";
        if (_results != null && string.Equals(id, _loadedId, StringComparison.OrdinalIgnoreCase)) return;
        if (_running) return;

        _running = true;
        int token = ++_token;
        var game = _game;
        var filter = CandidateFilter;
        var artOf = LocalArtResolver;
        SetStatus("Loading suggestions…");

        Task.Run(() =>
        {
            Dictionary<SuggesterCategory, List<CardData>> results = null;
            try { results = Compute(game, filter, artOf); }
            catch { }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (token != _token) return;   // a newer selection superseded this run
                    _running = false;
                    _results = results ?? new Dictionary<SuggesterCategory, List<CardData>>();
                    _loadedId = Safe(() => game.Id) ?? "";
                    RenderCurrent();
                }));
            }
            catch { /* handle torn down */ }
        });
    }

    // ── Engine + shaping (background thread) ────────────────────────────────────────────────────────

    private static Dictionary<SuggesterCategory, List<CardData>> Compute(
        IGame game, Func<CandidateGame, bool> filter, Func<string, string> artOf)
    {
        var runs = GameSuggester.RunAll(game, Limit, filter);
        var result = new Dictionary<SuggesterCategory, List<CardData>>();
        var dbIds = new HashSet<int>();

        foreach (var run in runs ?? new List<CategorySuggestions>())
        {
            var list = new List<CardData>();
            foreach (var e in run.Top ?? new List<SuggestionEntry>())
            {
                var c = e.Cand;
                string year = (c.Year.HasValue && c.Year.Value > 0) ? c.Year.Value.ToString() : "";
                string plat = c.Platform ?? "";
                var card = new CardData
                {
                    GameId = c.IsLocal ? (c.Id ?? "") : "",
                    DbId = c.LbDbId,
                    IsLocal = c.IsLocal,
                    Title = c.Title ?? "",
                    Sub = year.Length > 0 && plat.Length > 0 ? plat + " · " + year : (plat.Length > 0 ? plat : year),
                    Desc = c.IsLocal ? FirstChars(c.Notes, 220) : "",
                    Pct = e.Pct,
                };
                if (c.LbDbId > 0) dbIds.Add(c.LbDbId);
                list.Add(card);
            }
            result[run.Category] = list;
        }

        // Cloud descriptions in one batch (Extended DB), keyed by DatabaseID. Local games without notes
        // also benefit when they carry a cloud id.
        if (dbIds.Count > 0)
        {
            Dictionary<string, string> overviews = null;
            try { overviews = Web.RelatedProvider.Overviews(string.Join(",", dbIds), null); } catch { }
            if (overviews != null)
                foreach (var list in result.Values)
                    foreach (var card in list)
                        if (card.Desc.Length == 0 && card.DbId > 0 && overviews.TryGetValue(card.DbId.ToString(), out var ov))
                            card.Desc = FirstChars(ov, 220);
        }

        // Local art paths (detail-pane chain), resolved here so the UI thread never touches the cache.
        if (artOf != null)
            foreach (var list in result.Values)
                foreach (var card in list)
                    if (card.IsLocal && card.GameId.Length > 0)
                        card.LocalArt = Safe(() => artOf(card.GameId));

        return result;
    }

    private static string FirstChars(string s, int max)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ── Rendering (UI thread) ───────────────────────────────────────────────────────────────────────

    private void RenderCurrent()
    {
        ClearCards();
        if (_results == null) return;

        var cat = TabCats[Math.Min(_subTabs.Selected, TabCats.Length - 1)];
        _results.TryGetValue(cat, out var list);
        if (list == null || list.Count == 0)
        {
            SetStatus("No suggestions for this category.");
            return;
        }

        _status.Visible = false;
        _scroll.SuspendLayout();
        foreach (var d in list)
        {
            var card = new RelatedCard(d.Title, d.Sub, d.Desc, d.Pct, d.IsLocal);
            card.Clicked = () => OpenCard(d);
            card.StartThumb(d.LocalArt, d.DbId);
            _scroll.Controls.Add(card);
        }
        _scroll.ResumeLayout();
        LayoutCards();
        _scroll.AutoScrollPosition = new Point(0, 0);
    }

    private void ClearCards()
    {
        _status.Visible = false;
        var old = _scroll.Controls.OfType<RelatedCard>().ToArray();
        foreach (var c in old) { _scroll.Controls.Remove(c); c.Dispose(); }
        _scroll.AutoScrollPosition = new Point(0, 0);
    }

    private void SetStatus(string text)
    {
        ClearCards();
        _status.Text = text;
        _status.Visible = true;
    }

    /// <summary>Stack the cards vertically, full viewport width (fixed card height).</summary>
    private void LayoutCards()
    {
        var cards = _scroll.Controls.OfType<RelatedCard>().ToArray();
        if (cards.Length == 0) return;
        int w = _scroll.ClientSize.Width - 2;
        if (w < 60) return;
        int y = 4 + _scroll.AutoScrollPosition.Y;
        foreach (var c in cards)   // Controls preserves add order → ranked order top-down
        {
            c.SetBounds(0, y, w, RelatedCard.CardH);
            y += RelatedCard.CardH + 6;
        }
    }

    private void OpenCard(CardData d)
    {
        if (d.IsLocal && d.GameId.Length > 0) { try { OpenLocalGame?.Invoke(d.GameId); } catch { } }
        else if (d.DbId > 0)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://gamesdb.launchbox-app.com/games/dbid/" + d.DbId,
                    UseShellExecute = true,
                });
            }
            catch { }
        }
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

    // ── Cloud-thumb cache (dbid-keyed) ──────────────────────────────────────────────────────────────

    /// <summary>Desktop-side cache for the DB-only card thumbnails (the web relies on the browser cache;
    /// the desktop has no equivalent). Two bounded layers over MediaFetch.FetchThumbById:
    ///   • in-memory : encoded bytes (thumbs are a few KB), LRU-capped — sub-tab flips are free
    ///     (parity with the plugin viewer's per-form Image dictionary, but shared across renders);
    ///   • disk : Core\litebox\cache\related-thumbs\{dbid}.jpg, capped by a count sweep at write time
    ///     (oldest-mtime files dropped) — survives restarts and CANNOT accumulate unbounded.
    /// A fetch that fails caches nothing, so a dead CDN is retried on the next render.</summary>
    private static class RelatedThumbCache
    {
        private const int MemCap = 300;                 // ≈ a few MB of encoded thumbs
        private const int DiskCap = 500, DiskTrimTo = 400;

        private static readonly object Lock = new();
        private static readonly Dictionary<int, byte[]> Mem = new();
        private static readonly LinkedList<int> Lru = new();   // most-recent first

        private static string Dir => LiteBoxPaths.Dir("cache") is { } c
            ? System.IO.Path.Combine(c, "related-thumbs") : null;

        public static byte[] Get(int dbId)
        {
            lock (Lock)
            {
                if (Mem.TryGetValue(dbId, out var hit)) { Lru.Remove(dbId); Lru.AddFirst(dbId); return hit; }
            }

            string dir = Dir, path = null;
            try
            {
                if (dir != null)
                {
                    System.IO.Directory.CreateDirectory(dir);
                    path = System.IO.Path.Combine(dir, dbId + ".jpg");
                    if (System.IO.File.Exists(path))
                    {
                        var fromDisk = System.IO.File.ReadAllBytes(path);
                        if (fromDisk.Length > 0) { Remember(dbId, fromDisk); return fromDisk; }
                    }
                }
            }
            catch { }

            byte[] bytes = null;
            try
            {
                bytes = Media.MediaFetch.FetchThumbById(dbId,
                    () => Web.MediaProxy.PickCover(Media.MetadataDb.ImagesForGame(dbId)));
                // Beyond-plugin last resort: full cover when every thumb source is down.
                if (bytes == null || bytes.Length == 0)
                {
                    var cover = Web.MediaProxy.PickCover(Media.MetadataDb.ImagesForGame(dbId));
                    if (cover != null) bytes = Media.MediaFetch.FetchBytes(cover.Value, platform: null);
                }
            }
            catch { }
            if (bytes == null || bytes.Length == 0) return null;

            Remember(dbId, bytes);
            try
            {
                if (path != null)
                {
                    var tmp = path + ".tmp";
                    System.IO.File.WriteAllBytes(tmp, bytes);
                    System.IO.File.Move(tmp, path, overwrite: true);
                    SweepDisk(dir);
                }
            }
            catch { }
            return bytes;
        }

        private static void Remember(int dbId, byte[] bytes)
        {
            lock (Lock)
            {
                if (!Mem.ContainsKey(dbId))
                {
                    Mem[dbId] = bytes; Lru.AddFirst(dbId);
                    while (Lru.Count > MemCap) { Mem.Remove(Lru.Last.Value); Lru.RemoveLast(); }
                }
                else { Lru.Remove(dbId); Lru.AddFirst(dbId); }
            }
        }

        /// <summary>Bound the disk layer: past the cap, drop the oldest files down to the trim mark.</summary>
        private static void SweepDisk(string dir)
        {
            try
            {
                var files = new System.IO.DirectoryInfo(dir).GetFiles("*.jpg");
                if (files.Length <= DiskCap) return;
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                for (int i = 0; i < files.Length - DiskTrimTo; i++)
                    try { files[i].Delete(); } catch { }
            }
            catch { }
        }
    }

    // ── One suggestion card ─────────────────────────────────────────────────────────────────────────

    /// <summary>Custom-drawn card: thumb | title / platform·year / 2-line description, match % top-right
    /// (cloud glyph for DB-only games). Same rounded-box look as the detail pane's other cards.</summary>
    private sealed class RelatedCard : Panel
    {
        public const int CardH = 88;
        private const int Pad = 8, ThumbW = 54, ThumbH = CardH - 2 * Pad;

        private static readonly Font TitleFont = new Font("Segoe UI Semibold", 9.5f);
        private static readonly Font SubFont = new Font("Segoe UI", 8f);
        private static readonly Font DescFont = new Font("Segoe UI", 8.25f);

        private readonly string _title, _sub, _desc, _pct;
        private readonly bool _isLocal;
        private Image _thumb;
        private bool _hover;
        private bool _dead;

        public Action Clicked;

        public RelatedCard(string title, string sub, string desc, int pct, bool isLocal)
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = LiteBoxTheme.PanelC;
            _title = title; _sub = sub; _desc = desc; _isLocal = isLocal;
            _pct = pct > 0 ? pct + "%" : "";
            Cursor = Cursors.Hand;
        }

        /// <summary>Kick the async thumb: local art file when given, else the pre-made thumb chain
        /// (Malkav → extenddb /thumbs → per-origin, full cover as last resort) through the cache.</summary>
        public void StartThumb(string localArt, int dbId)
        {
            Task.Run(() =>
            {
                Image img = null;
                try
                {
                    if (!string.IsNullOrEmpty(localArt) && System.IO.File.Exists(localArt))
                        img = LoadScaled(System.IO.File.ReadAllBytes(localArt));
                    else if (dbId > 0)
                    {
                        var bytes = RelatedThumbCache.Get(dbId);
                        if (bytes != null && bytes.Length > 0) img = LoadScaled(bytes);
                    }
                }
                catch { }
                if (img == null) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_dead) { img.Dispose(); return; }
                        _thumb = img; Invalidate();
                    }));
                }
                catch { img.Dispose(); }
            });
        }

        /// <summary>Decode + downscale to ≤ 2× the drawn thumb size (memory: up to 90 cards alive).</summary>
        private static Image LoadScaled(byte[] bytes)
        {
            using var ms = new System.IO.MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            int maxW = ThumbW * 2, maxH = ThumbH * 2;
            double k = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            if (k >= 1) return new Bitmap(src);
            var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left) Clicked?.Invoke(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var box = new Rectangle(0, 0, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 1));
            using (var path = RoundedRect(box, 8))
            {
                using (var bg = new SolidBrush(_hover ? Color.FromArgb(54, 54, 59) : Color.FromArgb(46, 46, 50)))
                    g.FillPath(bg, path);
                using (var bd = new Pen(Color.FromArgb(64, 64, 68)))
                    g.DrawPath(bd, path);
            }

            // Thumb (letterboxed into its slot) or a dark placeholder.
            var slot = new Rectangle(Pad, Pad, ThumbW, ThumbH);
            using (var ph = new SolidBrush(Color.FromArgb(34, 34, 37))) g.FillRectangle(ph, slot);
            if (_thumb != null)
            {
                double k = Math.Min((double)slot.Width / _thumb.Width, (double)slot.Height / _thumb.Height);
                int w = Math.Max(1, (int)(_thumb.Width * k)), h = Math.Max(1, (int)(_thumb.Height * k));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_thumb, slot.X + (slot.Width - w) / 2, slot.Y + (slot.Height - h) / 2, w, h);
            }

            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int tx = Pad + ThumbW + 10;
            int ty = Pad;

            // Match % (and cloud glyph for DB-only) pinned top-right; title gets the remaining width.
            string badge = _pct.Length > 0 ? (_isLocal ? _pct : "☁ " + _pct) : (_isLocal ? "" : "☁");
            int badgeW = badge.Length > 0 ? TextRenderer.MeasureText(g, badge, SubFont, Size.Empty, one).Width : 0;
            if (badge.Length > 0)
                TextRenderer.DrawText(g, badge, SubFont, new Point(ClientSize.Width - Pad - badgeW, ty + 1), LiteBoxTheme.SubFg, one);

            int titleW = Math.Max(10, ClientSize.Width - tx - Pad - badgeW - 6);
            TextRenderer.DrawText(g, _title, TitleFont, new Rectangle(tx, ty, titleW, 18), LiteBoxTheme.Fg, one);
            ty += 19;
            if (_sub.Length > 0)
            {
                TextRenderer.DrawText(g, _sub, SubFont, new Rectangle(tx, ty, titleW, 14), LiteBoxTheme.SubFg, one);
                ty += 16;
            }
            if (_desc.Length > 0)
            {
                var descRect = new Rectangle(tx, ty, Math.Max(10, ClientSize.Width - tx - Pad), Math.Max(10, ClientSize.Height - Pad - ty));
                TextRenderer.DrawText(g, _desc, DescFont, descRect, LiteBoxTheme.SubFg,
                    TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _dead = true; _thumb?.Dispose(); _thumb = null; }
            base.Dispose(disposing);
        }
    }
}
