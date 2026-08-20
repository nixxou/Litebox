// The RetroAchievements window — LaunchBox's, reproduced: the thing behind "HARDCORE POINTS: 30".
//
//   Profile           — who you are, the five counters, and the last five games you played with your
//                       progress on each ("Earned 1 of 49 achievements and 25 of 845 points", "2% Complete").
//   Global Leaderboard — RA's site-wide top ten, with your own row pinned underneath it. Your rank is
//                       blank rather than 0 when RA hasn't ranked the account (it doesn't rank everyone).
//
// Data comes from RaProfileService, which caches: the window opens on whatever was last fetched and
// repaints when the background refresh lands, so it never shows an empty frame while five HTTP calls
// run. Nothing here blocks the UI thread.
//
// The Recent Activity rows are PAINTED rather than put in a ListView: each row is three lines with a
// right-aligned figure, which no ListView column layout renders without a fight. Same choice, and the
// same technique, as RetroAchievementsCard.
//
// Each row is also tied back to the LIBRARY. RA names its games by a numeric id (the "raid"), and LiteBox
// already stores that id per game in the <RetroAchievementsId> field, so one pass over the library turns
// the five recent games into five local ids. A matched row gets the game's own art, a hover highlight and
// a click that selects it in the main window — the same gesture as a card in RELATED GAMES, and the same
// two hooks behind it (MainWindow.RelatedLocalArt / SelectGameById, injected by the caller so this window
// never reaches into MainWindow itself). An unmatched row falls back to RA's own box art and stays inert.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal sealed class RaProfileWindow : LiteBoxForm
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color PanelC = LiteBoxTheme.PanelC;
    private static readonly Color Field = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;
    private static readonly Color Accent = LiteBoxTheme.Accent;
    // One notch darker than the list, the value Manage Controllers and Manage Badges use for headers.
    private static readonly Color HeaderBg = Color.FromArgb(24, 24, 28);
    // The faint stripe on odd leaderboard rows (RA's own table alternates; so does LaunchBox's).
    private static readonly Color Stripe = Color.FromArgb(38, 39, 47);

    private static RaProfileWindow? _open;   // the window is modeless, so a second click raises the first

    private readonly Panel _profileHost;
    private readonly HeaderPanel _header;
    private readonly ActivityPanel _activity;
    private readonly ListView _board;
    private readonly Label _empty;
    private readonly Font _headerFont = new("Segoe UI Semibold", 9.5f);
    // null until the first Apply: BoardSignature never returns null, so the first pass always builds.
    private string? _boardSig;

    private readonly Func<IReadOnlyList<IGame>> _games;
    private readonly Func<string, string?> _localArt;
    private readonly Action<string> _openGame;

    private RaProfileWindow(Func<IReadOnlyList<IGame>> games, Func<string, string?> localArt, Action<string> openGame)
    {
        _games = games; _localArt = localArt; _openGame = openGame;

        Text = "RetroAchievements";
        ClientSize = new Size(S(810), S(700));
        MinimumSize = new Size(S(560), S(400));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        ShowInTaskbar = true;

        var tabs = NewDarkTabs();
        var profileTab = NewTabPage(tabs, "Profile");
        var boardTab = NewTabPage(tabs, "Global Leaderboard");

        // ── Profile tab ──────────────────────────────────────────────────────
        // Header docked Top, activity filling the rest: the scrollbar then belongs to the activity
        // list alone, which is where LaunchBox puts it.
        _activity = new ActivityPanel(this) { Dock = DockStyle.Fill };
        _header = new HeaderPanel(this) { Dock = DockStyle.Top, Height = S(190) };
        _empty = new Label
        {
            Dock = DockStyle.Fill, ForeColor = SubFg, BackColor = Bg,
            TextAlign = ContentAlignment.MiddleCenter, Visible = false,
        };
        _profileHost = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _profileHost.Controls.Add(_activity);
        _profileHost.Controls.Add(_header);
        _profileHost.Controls.Add(_empty);
        _empty.BringToFront();
        profileTab.Controls.Add(_profileHost);

        // ── Global Leaderboard tab ───────────────────────────────────────────
        _board = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = true, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = PanelC, ForeColor = Fg, OwnerDraw = true,
        };
        _board.Columns.Add("Rank", S(90));
        _board.Columns.Add("Profile Name", S(500));
        _board.Columns.Add("Points", S(150), HorizontalAlignment.Right);
        // The native header paints its own light background with BLACK text, unreadable on this theme —
        // so we fill it ourselves and take the text colour from the theme (a Color.SubFg override in
        // Options ▸ Theme moves it too, instead of freezing a literal here).
        _board.DrawColumnHeader += (_, e) =>
        {
            using var b = new SolidBrush(HeaderBg);
            e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.Inflate(-S(6), 0);
            var align = e.Header?.TextAlign ?? HorizontalAlignment.Left;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                      | (align == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", _headerFont, r, Fg, flags);
        };
        // The rows carry their own colours (stripe, and Accent for the user's own row), so the system
        // draws them: DrawDefault on the ITEM renders the whole row, sub-items included.
        _board.DrawItem += (_, e) => e.DrawDefault = true;
        boardTab.Controls.Add(_board);

        // Open on the cache, then refresh in the background and repaint when it lands.
        RaProfileService.Changed += OnProfileChanged;
        FormClosed += (_, _) => { RaProfileService.Changed -= OnProfileChanged; _open = null; };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        SizeChanged += (_, _) => { FitTabs(tabs); FitBoard(); };

        Load += (_, _) =>
        {
            FitTabs(tabs); FitBoard();
            Apply(RaProfileService.Cached());
            if (RaProfileService.Configured)
                System.Threading.Tasks.Task.Run(() => { try { RaProfileService.Refresh(); } catch { } });
        };
    }

    /// <summary>Open the window (modeless, one at a time — a second call raises the one already up).</summary>
    public static void Open(IWin32Window owner, Func<IReadOnlyList<IGame>> games,
                            Func<string, string?> localArt, Action<string> openGame)
    {
        if (_open != null && !_open.IsDisposed)
        {
            try { _open.WindowState = FormWindowState.Normal; _open.Activate(); } catch { }
            return;
        }
        var w = new RaProfileWindow(games, localArt, openGame);
        _open = w;
        // Placed by hand: CenterParent does nothing for a MODELESS window (ShowDialog-only), so this
        // used to open wherever Windows liked — the primary monitor, not LiteBox's.
        // Shown WITH the owner, then un-owned immediately: an owned window floats above its owner
        // forever, so clicking a game would raise LiteBox behind a window that refuses to get out of
        // the way. Un-owned it drops behind like any other window, and it still has a taskbar button.
        UiKit.DialogPlacement.CenterOnOwner(w, owner as Form);
        w.Show(owner);
        try { w.Owner = null; } catch { }
    }

    private void OnProfileChanged()
    {
        // Raised on the fetching thread — hop to the UI thread, and survive the window closing mid-flight.
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() => { if (!IsDisposed) Apply(RaProfileService.Cached()); }));
        }
        catch { }
    }

    private void Apply(RaProfile? p)
    {
        _header.Data = p;
        _activity.Data = p;

        if (!RaProfileService.Configured)
        {
            _empty.Text = "No RetroAchievements account is configured.\r\n\r\n"
                        + "Add your username and Web API key in Options ▸ RetroAchievements.";
            _empty.Visible = true;
        }
        else if (p == null)
        {
            _empty.Text = "Loading your RetroAchievements profile…";
            _empty.Visible = true;
        }
        else _empty.Visible = false;

        // The Profile tab is painted, so repainting it costs nothing and loses nothing. The leaderboard
        // is a ListView with rows, a scroll position and a selection — rebuilding it when nothing in it
        // changed would throw all three away in front of the user. So it is rebuilt only when the board
        // itself moved: the top ten, or the row that is theirs.
        _header.Invalidate();
        _activity.Rebuild();
        string sig = BoardSignature(p);
        if (sig != _boardSig) { _boardSig = sig; BuildBoard(p); }

        MatchAgainstLibrary(p);
    }

    /// <summary>Turn the recent games' RA ids into LIBRARY games: ONE pass over the catalogue reading each
    /// game's &lt;RetroAchievementsId&gt;, keeping only the handful of raids on screen.
    ///
    /// On the UI thread on purpose — RaFields reads the in-memory store, and a background pass could race
    /// a field write. One dictionary lookup per game is cheap enough to afford even on a large library, and
    /// it runs once per refresh that changed something, not per repaint.
    ///
    /// A raid the library doesn't have simply stays unmatched: the row keeps RA's own art and no click.</summary>
    private void MatchAgainstLibrary(RaProfile? p)
    {
        var byRaid = new Dictionary<int, RaRecentGame>();
        if (p != null)
            foreach (var r in p.recent)
            {
                r.localGameId = null;                       // a re-match must not keep a stale hit
                if (r.gameId > 0) byRaid[r.gameId] = r;
            }
        if (byRaid.Count > 0)
        {
            try
            {
                // Several library entries CAN carry the same RA id — regions, versions, clones of one
                // arcade set all map to a single RA game. So this keeps the BEST candidate per raid
                // rather than the first one seen, and walks the whole catalogue instead of stopping at
                // the first hit each: a later entry can outrank an earlier one, and "first wins" made
                // the click target depend on enumeration order, which is a different game from one
                // opening to the next.
                var best = new Dictionary<int, IGame>();
                foreach (var g in _games())
                {
                    int raid = RaFields.Raid(g);
                    if (raid <= 0 || !byRaid.TryGetValue(raid, out var want)) continue;
                    if (!best.TryGetValue(raid, out var cur) || Outranks(g, cur, want.consoleId)) best[raid] = g;
                }
                foreach (var kv in best) byRaid[kv.Key].localGameId = Safe(() => kv.Value.Id);
            }
            catch { }
        }
        _activity.StartThumbs();
    }

    /// <summary>The cover file for a matched row, or null to fall back to RA's art. Resolving it walks the
    /// store and probes the disk, so it is called ONLY for rows actually on screen — with "Show more"
    /// open that is the difference between five lookups and a hundred.</summary>
    private string? LocalArtFor(RaRecentGame r)
        => r.localGameId == null ? null : Safe(() => _localArt(r.localGameId!));

    /// <summary>Of two library entries carrying the same RA id, is <paramref name="a"/> the one the row
    /// should point at?
    ///
    /// Most recently played wins, because that is what the row is ABOUT — RA is telling us the user played
    /// this game, and the entry they launched is the one whose LastPlayedDate moved. (Comparing against
    /// RA's own timestamp would be sharper still, but RA reports server time and LaunchBox stores local
    /// time, so the two are hours apart for most of the world and the comparison would pick by timezone.)
    ///
    /// Next, the entry whose PLATFORM is the console RA names for that game. This is what separates a real
    /// region variant from a mis-scanned one: a library can carry the same raid on a Game Boy entry and on
    /// a copy filed under Super Nintendo, and only one of those can be right. It ranks BELOW last-played on
    /// purpose — RA identifies a game by ROM hash, not by our platform tag, so an entry you actually
    /// launched is the entry you want even when its platform is mislabelled.
    ///
    /// Then most played, then a playable copy over one flagged broken or hidden, and finally title and id —
    /// which decide nothing on merit but make the answer the SAME on every open. Without that last step two
    /// equal copies would swap places with the catalogue's enumeration order, and the click would land on a
    /// different game from one opening to the next.</summary>
    private static bool Outranks(IGame a, IGame b, int raConsoleId)
    {
        var (aLast, aCon, aPlays, aSound, aTitle, aId) = MatchKey(a, raConsoleId);
        var (bLast, bCon, bPlays, bSound, bTitle, bId) = MatchKey(b, raConsoleId);
        if (aLast != bLast) return aLast > bLast;
        if (aCon != bCon) return aCon > bCon;
        if (aPlays != bPlays) return aPlays > bPlays;
        if (aSound != bSound) return aSound > bSound;
        int t = string.Compare(aTitle, bTitle, StringComparison.OrdinalIgnoreCase);
        if (t != 0) return t < 0;
        return string.CompareOrdinal(aId, bId) < 0;
    }

    /// <summary>The ranking fields, each read defensively — every one of these is a live store property.
    /// "console" is 1 when the entry's platform maps to the console RA names for this game (0 when it
    /// doesn't, and 0 for both when either side is unmapped, which makes the criterion sit out rather than
    /// punish). "sound" is 2 for a normal entry, minus a point per broken/hidden flag.</summary>
    private static (long lastPlayed, int console, int playCount, int sound, string title, string id)
        MatchKey(IGame g, int raConsoleId)
    {
        long last = 0; int con = 0, plays = 0, sound = 2; string title = "", id = "";
        try { last = g.LastPlayedDate?.Ticks ?? 0; } catch { }
        try { plays = g.PlayCount; } catch { }
        try { if (g.Broken) sound--; } catch { }
        try { if (g.Hide) sound--; } catch { }
        try { title = g.Title ?? ""; } catch { }
        try { id = g.Id ?? ""; } catch { }
        try
        {
            if (raConsoleId > 0 && RaPlatformMap.ConsoleIdFor(Safe(() => g.Platform)) == raConsoleId) con = 1;
        }
        catch { }
        return (last, con, plays, sound, title, id);
    }

    /// <summary>Everything this window asks of the host — the game list, an id, a cover path — reads the
    /// live store, which can throw mid-edit. None of it is worth taking the window down for.</summary>
    private static T? Safe<T>(Func<T?> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    /// <summary>What the Global Leaderboard tab actually shows — the ten rows plus the user's own.</summary>
    private static string BoardSignature(RaProfile? p)
    {
        if (p == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var r in p.leaderboard) sb.Append(r.rank).Append('|').Append(r.user).Append('|').Append(r.points).Append(';');
        return sb.Append(p.user).Append('|').Append(p.rank).Append('|').Append(p.hardcorePoints).ToString();
    }

    private void BuildBoard(RaProfile? p)
    {
        _board.BeginUpdate();
        try
        {
            _board.Items.Clear();
            if (p == null) return;
            foreach (var row in p.leaderboard)
            {
                var it = new ListViewItem(row.rank.ToString());
                it.SubItems.Add(row.user ?? "");
                it.SubItems.Add(row.points.ToString("N0"));
                it.UseItemStyleForSubItems = true;
                it.BackColor = row.rank % 2 == 1 ? Stripe : PanelC;
                it.ForeColor = Fg;
                _board.Items.Add(it);
            }
            // The user's own row, under the ten. An unranked account (Rank null) gets a BLANK cell —
            // printing the 0 a non-nullable int would give reads as "ranked first from the bottom".
            if (!string.IsNullOrEmpty(p.user))
            {
                var me = new ListViewItem(p.rank?.ToString() ?? "");
                me.SubItems.Add(p.user!);
                me.SubItems.Add(p.hardcorePoints.ToString("N0"));
                me.UseItemStyleForSubItems = true;
                me.BackColor = Accent;
                me.ForeColor = Color.White;
                _board.Items.Add(me);
            }
        }
        finally { _board.EndUpdate(); }
    }

    // ── the painted halves of the Profile tab ────────────────────────────────

    /// <summary>Name, member-since, the five counters, and the "Recent Activity" heading that the
    /// scrolling list below runs under.</summary>
    private sealed class HeaderPanel : Panel
    {
        private readonly RaProfileWindow _w;
        public RaProfile? Data;

        public HeaderPanel(RaProfileWindow w)
        {
            _w = w;
            BackColor = Bg;
            DoubleBuffered = true; ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var p = Data;
            if (p == null) return;

            var g = e.Graphics;
            int pad = _w.S(90), y = _w.S(18);

            using var name = new Font("Segoe UI", 16f, FontStyle.Bold);
            using var sub = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var small = new Font("Segoe UI", 8.25f);
            using var head = new Font("Segoe UI", 16f, FontStyle.Bold);

            string title = $"{p.user} ({p.hardcorePoints:N0})";
            TextRenderer.DrawText(g, title, name, new Point(pad, y), Fg, TextFormatFlags.NoPrefix);
            y += TextRenderer.MeasureText(title, name).Height + _w.S(2);

            string since = "Member Since: " + p.MemberSinceLong();
            TextRenderer.DrawText(g, since, sub, new Point(pad, y), Fg, TextFormatFlags.NoPrefix);
            y += TextRenderer.MeasureText(since, sub).Height + _w.S(18);

            // Two rows of counters on a shared column grid, so "Retro Points" and "Games Beaten" line up.
            int col = _w.S(150), line = TextRenderer.MeasureText("Hg", small).Height + _w.S(4);
            Cell(g, small, $"Hardcore Points: {p.hardcorePoints:N0}", pad + 0 * col, y);
            Cell(g, small, $"Retro Points: {p.retroPoints:N0}", pad + 1 * col, y);
            Cell(g, small, $"Softcore Points: {p.softcorePoints:N0}", pad + 2 * col, y);
            y += line;
            Cell(g, small, $"Achievements Unlocked: {p.achievementsUnlocked:N0}", pad + 0 * col, y);
            Cell(g, small, $"Games Beaten: {p.gamesBeaten:N0}", pad + 1 * col, y);
            y += line + _w.S(14);

            TextRenderer.DrawText(g, "Recent Activity", head, new Point(_w.S(18), y), Fg, TextFormatFlags.NoPrefix);
            y += TextRenderer.MeasureText("Hg", head).Height + _w.S(6);

            // The panel is docked Top, so its height is ours to set — and the fonts are the only thing
            // that knows how tall the block actually came out. Measuring here rather than hard-coding a
            // number keeps the "Recent Activity" heading off the scrolling list at every DPI and every
            // Windows text-size setting. Guarded by the comparison: setting Height repaints.
            if (Height != y) Height = y;
        }

        private static void Cell(Graphics g, Font f, string text, int x, int y)
            => TextRenderer.DrawText(g, text, f, new Point(x, y), SubFg, TextFormatFlags.NoPrefix);
    }

    /// <summary>The scrolling Recent Activity list: one three-line row per game, with the completion
    /// percentage right-aligned against the far edge.</summary>
    private sealed class ActivityPanel : Panel
    {
        private readonly RaProfileWindow _w;

        private RaProfile? _data;

        /// <summary>The profile being drawn. Replacing it drops every per-row cache: the open grid, the
        /// fetched achievement sets and the badges are all keyed by ROW INDEX, and a refresh can reorder
        /// the list — row 2 after is not the game row 2 was before. Keeping any of it would show one
        /// game's achievements under another's name.</summary>
        // A public property on a Control is a designer property as far as the analyser is concerned; this
        // one is set from code only and there is no designer here.
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public RaProfile? Data
        {
            get => _data;
            set
            {
                if (ReferenceEquals(_data, value)) return;
                _data = value;
                _openRow = -1;
                _ach.Clear(); _achLoading.Clear();
                _hoverBadge = -1;
                DisposeBadges();
                try { _tip.SetToolTip(this, ""); } catch { }
            }
        }

        public ActivityPanel(RaProfileWindow w)
        {
            _w = w;
            BackColor = Bg;
            AutoScroll = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        private int RowHeight => _w.S(86);
        private int ThumbW => _w.S(52);
        private int ThumbH => _w.S(64);
        private int ThumbX => _w.S(22);
        private int TextLeft => ThumbX + ThumbW + _w.S(14);

        private readonly Dictionary<int, Image> _thumbs = new();   // row index → its loaded art
        private int _hover = -1;
        private bool _hoverFooter;
        private int _hoverChevron = -1;
        private int _thumbGen;                                     // latest-wins: an old load must not land
        private bool _expanded;

        // ── the achievement grid ─────────────────────────────────────────────
        // ONE row at a time. An accordion keeps the list navigable and, more to the point, bounds the
        // work: opening a game pulls its achievement set and then downloads a badge per achievement, so
        // "all of them at once" would be a few hundred requests for a list nobody reads at once.
        private int _openRow = -1;
        private readonly Dictionary<int, RaGameCache> _ach = new();   // row index → its achievement set
        private readonly HashSet<int> _achLoading = new();
        private readonly object _badgeGate = new();
        private readonly Dictionary<int, Image> _badges = new();      // achievement id → its badge image
        private int _badgeGen;
        private int _hoverBadge = -1;
        private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 250, ReshowDelay = 80, AutoPopDelay = 20000 };

        private int BadgeSize => _w.S(40);
        private int BadgeGap => _w.S(6);
        private int ChevW => _w.S(24);

        /// <summary>How many rows before "Show more" — LaunchBox's own count, and what the service used to
        /// fetch before the whole list came down in one call.</summary>
        private const int Collapsed = 5;

        private int Total => Data?.recent.Count ?? 0;
        private int Shown => _expanded ? Total : Math.Min(Collapsed, Total);
        // Only while collapsed: once the list is open there is nothing left to ask for, and a "Show less"
        // sitting at the very bottom is a button you have to scroll past everything to reach in order to
        // undo something you wanted.
        private bool HasFooter => !_expanded && Total > Collapsed;
        private int FooterH => _w.S(34);

        // ── layout ───────────────────────────────────────────────────────────
        // Every row is RowHeight tall except the open one, which also carries its achievement grid. With
        // only ONE row ever open, positions stay a one-liner: everything below the open row shifts by the
        // grid's height, everything above is untouched.

        /// <summary>Height of the open row's grid — 0 when nothing is open.</summary>
        private int ExtraH() => _openRow < 0 ? 0 : GridHeight(_openRow);

        /// <summary>Top of row <paramref name="i"/> in CONTENT coordinates (scroll not applied).</summary>
        private int RowTopContent(int i)
            => i * RowHeight + (_openRow >= 0 && i > _openRow ? ExtraH() : 0);

        /// <summary>Top of row <paramref name="i"/> on SCREEN.</summary>
        private int RowTop(int i) => RowTopContent(i) + AutoScrollPosition.Y;

        private int ContentHeight()
            => Shown * RowHeight + ExtraH() + (HasFooter ? FooterH : 0) + _w.S(12);

        /// <summary>How tall the badge grid for a row is: the loading line while it is being fetched, else
        /// as many rows of badges as the panel's width allows. 0 when there is nothing to draw.</summary>
        private int GridHeight(int i)
        {
            if (i < 0 || i >= Total) return 0;
            if (!_ach.TryGetValue(i, out var data)) return _w.S(34);          // "Loading achievements…"
            int n = data.achievements.Count;
            if (n == 0) return _w.S(34);                                      // "No achievements"
            int cols = GridCols();
            int rows = (n + cols - 1) / cols;
            return rows * (BadgeSize + BadgeGap) + _w.S(12);
        }

        private int GridCols()
        {
            int usable = ClientSize.Width - TextLeft - _w.S(46);
            return Math.Max(1, usable / (BadgeSize + BadgeGap));
        }

        /// <summary>Re-measure the scroll extent after the data changed, then repaint. The extent counts
        /// the SHOWN rows plus whatever the open row added, so the scrollbar tracks the real content.</summary>
        public void Rebuild()
        {
            AutoScrollMinSize = new Size(0, ContentHeight());
            _hover = -1; _hoverFooter = false; _hoverChevron = -1;
            Invalidate();
        }

        /// <summary>Open a row's achievements, or close the one that is open. Accordion: opening one closes
        /// the other, and the badges of the row that left are dropped.</summary>
        private void ToggleAchievements(int i)
        {
            if (i < 0 || i >= Total) return;
            _openRow = _openRow == i ? -1 : i;
            _hoverBadge = -1;
            try { _tip.SetToolTip(this, ""); } catch { }
            DisposeBadges();
            if (_openRow >= 0) EnsureAchievements(_openRow);
            Rebuild();
        }

        /// <summary>Fetch (or re-read from cache) one game's achievement set. RaService is BLOCKING — it is
        /// the same call the detail pane's card makes — so it runs on the pool and the result comes back
        /// through BeginInvoke. Guarded so a second click while it is in flight doesn't fetch twice.</summary>
        private void EnsureAchievements(int i)
        {
            if (_ach.ContainsKey(i) || _achLoading.Contains(i)) { KickBadges(i); return; }
            var rows = Data?.recent;
            if (rows == null || i >= rows.Count) return;
            int raid = rows[i].gameId;
            if (raid <= 0) return;
            // RA's timestamp decides whether the per-game cache is stale ("played since we cached"). It is
            // server time with no zone marker, so it is read as UTC — an hour either way only ever costs a
            // refetch, never a wrong answer.
            DateTime played = DateTime.MinValue;
            try
            {
                if (DateTime.TryParse(rows[i].lastPlayed, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt)) played = dt;
            }
            catch { }
            _achLoading.Add(i);
            System.Threading.Tasks.Task.Run(() =>
            {
                RaGameCache? data = null;
                try { data = RaService.EnsureAndRead(raid, played); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed) return;
                        _achLoading.Remove(i);
                        if (data != null) _ach[i] = data;
                        if (_openRow == i) { Rebuild(); KickBadges(i); }
                    }));
                }
                catch { }
            });
        }

        /// <summary>Download + decode the badges of one open row, ONE task walking them in order rather
        /// than a task per badge: a 100-achievement set would otherwise open 100 sockets at once. Repaints
        /// every few badges so the grid fills in visibly instead of appearing all at once at the end.
        /// Same shape as RetroAchievementsCard.KickBadgeLoads, which this borrows wholesale.</summary>
        private void KickBadges(int i)
        {
            if (!_ach.TryGetValue(i, out var data)) return;
            int gen = ++_badgeGen;
            var list = data.achievements;
            System.Threading.Tasks.Task.Run(() =>
            {
                int since = 0;
                foreach (var a in list)
                {
                    if (gen != _badgeGen) return;                      // another row opened → abandon
                    lock (_badgeGate) { if (_badges.ContainsKey(a.id)) continue; }
                    var path = RaBadges.Get(a.badge, a.unlocked);
                    if (path == null) continue;
                    try
                    {
                        Image img;
                        using (var fs = System.IO.File.OpenRead(path)) img = Image.FromStream(fs);  // copy out, no file lock
                        if (gen != _badgeGen) { img.Dispose(); continue; }
                        lock (_badgeGate)
                        {
                            if (_badges.ContainsKey(a.id)) img.Dispose();
                            else _badges[a.id] = img;
                        }
                        if (++since >= 4) { since = 0; SafeInvalidate(); }
                    }
                    catch { }
                }
                SafeInvalidate();
            });
        }

        private void SafeInvalidate()
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { }
        }

        private void DisposeBadges()
        {
            _badgeGen++;
            lock (_badgeGate)
            {
                foreach (var img in _badges.Values) { try { img?.Dispose(); } catch { } }
                _badges.Clear();
            }
        }

        /// <summary>Reveal the rest of the list. One way on purpose — see <see cref="HasFooter"/>.</summary>
        private void Expand()
        {
            if (_expanded) return;
            _expanded = true;
            Rebuild();
            StartThumbs();
        }

        /// <summary>Load one thumb per SHOWN row: the local cover when the row matched a game we own, else
        /// RA's own art, downloaded once and disk-cached. The local path is resolved here, on the UI thread
        /// (it reads the store), and only for what is on screen — the download and the decode go to the
        /// pool. Each image lands back on the UI thread and repaints just its own row.</summary>
        public void StartThumbs()
        {
            int gen = ++_thumbGen;
            DisposeThumbs();
            var rows = Data?.recent;
            if (rows == null) return;
            int n = Shown;
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                string? local = _w.LocalArtFor(rows[idx]);
                string? icon = rows[idx].imageIcon;
                System.Threading.Tasks.Task.Run(() =>
                {
                    Image? img = null;
                    try
                    {
                        string? file = !string.IsNullOrEmpty(local) && System.IO.File.Exists(local)
                            ? local : RaBadges.GameArt(icon);
                        if (file != null) img = LoadScaled(file);
                    }
                    catch { }
                    if (img == null) return;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            // Disposed panel, or a newer StartThumbs already ran: drop it rather than
                            // leaking it into a table that has moved on.
                            if (IsDisposed || gen != _thumbGen) { img.Dispose(); return; }
                            if (_thumbs.TryGetValue(idx, out var old)) old?.Dispose();
                            _thumbs[idx] = img;
                            Invalidate(RowBounds(idx));
                        }));
                    }
                    catch { img.Dispose(); }
                });
            }
        }

        /// <summary>Decode and downscale to about the drawn size — a 2000px cover held at full resolution
        /// for five rows is megabytes for nothing.</summary>
        private Image LoadScaled(string file)
        {
            using var src = Image.FromFile(file);
            int maxW = ThumbW * 2, maxH = ThumbH * 2;
            double k = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            if (k >= 1) return new Bitmap(src);
            var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }

        private void DisposeThumbs()
        {
            foreach (var img in _thumbs.Values) { try { img?.Dispose(); } catch { } }
            _thumbs.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _thumbGen++; DisposeThumbs(); DisposeBadges(); try { _tip.Dispose(); } catch { } }
            base.Dispose(disposing);
        }

        // ── hit testing ──────────────────────────────────────────────────────
        // Rows are painted, not controls, so the mouse is mapped back to an index by hand. Everything
        // works in CONTENT coordinates (scroll already applied) so hover and paint agree while scrolling.

        /// <summary>The row's own strip — NOT its grid, which is not part of the clickable row.</summary>
        private Rectangle RowBounds(int i)
            => new(0, RowTop(i), ClientSize.Width, RowHeight);

        /// <summary>The row strip plus whatever it expanded to: what has to be repainted when it opens.</summary>
        private Rectangle RowAndGridBounds(int i)
            => new(0, RowTop(i), ClientSize.Width, RowHeight + (i == _openRow ? ExtraH() : 0));

        private Rectangle FooterBounds()
            => new(0, Shown * RowHeight + ExtraH() + AutoScrollPosition.Y, ClientSize.Width, FooterH);

        /// <summary>The chevron that opens a row's achievements, at the far right of the strip.</summary>
        private Rectangle ChevronBounds(int i)
            => new(ClientSize.Width - _w.S(22) - ChevW, RowTop(i) + (RowHeight - ChevW) / 2, ChevW, ChevW);

        /// <summary>Which row's STRIP is at this y. A plain division stopped working once a row could
        /// carry a grid, so this walks — at most a hundred rows, on a mouse move.</summary>
        private int RowAt(int y)
        {
            int n = Shown;
            for (int i = 0; i < n; i++)
            {
                int top = RowTop(i);
                if (y >= top && y < top + RowHeight) return i;
            }
            return -1;
        }

        private bool FooterAt(int y)
        {
            if (!HasFooter) return false;
            var f = FooterBounds();
            return y >= f.Top && y < f.Bottom;
        }

        /// <summary>The achievement under the cursor inside the open row's grid, or -1.</summary>
        private int BadgeAt(Point p, out RaCacheAch? hit)
        {
            hit = null;
            if (_openRow < 0 || !_ach.TryGetValue(_openRow, out var data)) return -1;
            var list = data.achievements;
            if (list.Count == 0) return -1;
            int gridTop = RowTop(_openRow) + RowHeight + _w.S(6);
            int cols = GridCols(), step = BadgeSize + BadgeGap;
            int col = (p.X - TextLeft) / step, row = (p.Y - gridTop) / step;
            if (p.X < TextLeft || p.Y < gridTop || col < 0 || col >= cols || row < 0) return -1;
            // Inside the gap between two badges is not "on" either of them.
            if ((p.X - TextLeft) % step >= BadgeSize || (p.Y - gridTop) % step >= BadgeSize) return -1;
            int idx = row * cols + col;
            if (idx < 0 || idx >= list.Count) return -1;
            hit = list[idx];
            return hit.id;
        }

        /// <summary>Only a row matched to a game in the library reacts — the rest are just information.</summary>
        private bool Clickable(int i)
            => i >= 0 && Data != null && i < Data.recent.Count && Data.recent[i].localGameId != null;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = RowAt(e.Y);
            int chev = row >= 0 && ChevronBounds(row).Contains(e.Location) ? row : -1;
            // The chevron sits inside the row, so hovering it must not also light the row up as if the
            // click would navigate — it wouldn't.
            int i = (chev < 0 && Clickable(row)) ? row : -1;
            bool foot = FooterAt(e.Y);

            int badgeId = BadgeAt(e.Location, out var ach);
            if (badgeId != _hoverBadge)
            {
                _hoverBadge = badgeId;
                try
                {
                    _tip.SetToolTip(this, ach == null ? "" :
                        $"{ach.title}  ({ach.points} pts)\r\n{ach.description}"
                        + (ach.unlocked ? (ach.unlockedHardcore ? "\r\nUnlocked (hardcore)" : "\r\nUnlocked") : "\r\nLocked"));
                }
                catch { }
            }

            if (i == _hover && foot == _hoverFooter && chev == _hoverChevron) return;
            if (_hover >= 0) Invalidate(RowBounds(_hover));
            if (_hoverChevron >= 0) Invalidate(ChevronBounds(_hoverChevron));
            if (_hoverFooter) Invalidate(FooterBounds());
            _hover = i; _hoverFooter = foot; _hoverChevron = chev;
            if (_hover >= 0) Invalidate(RowBounds(_hover));
            if (_hoverChevron >= 0) Invalidate(ChevronBounds(_hoverChevron));
            if (_hoverFooter) Invalidate(FooterBounds());
            Cursor = (_hover >= 0 || _hoverFooter || _hoverChevron >= 0) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0 && !_hoverFooter && _hoverChevron < 0) return;
            if (_hover >= 0) Invalidate(RowBounds(_hover));
            if (_hoverChevron >= 0) Invalidate(ChevronBounds(_hoverChevron));
            if (_hoverFooter) Invalidate(FooterBounds());
            _hover = -1; _hoverFooter = false; _hoverChevron = -1;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (FooterAt(e.Y)) { Expand(); return; }
            int i = RowAt(e.Y);
            // The chevron first: it lives inside a row that would otherwise navigate away.
            if (i >= 0 && ChevronBounds(i).Contains(e.Location)) { ToggleAchievements(i); return; }
            if (!Clickable(i)) return;
            string id = Data!.recent[i].localGameId!;
            try { _w._openGame(id); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var p = Data;
            if (p == null || p.recent.Count == 0) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var title = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var body = new Font("Segoe UI", 9f);

            // The percentage stops short of the chevron instead of sharing its corner.
            int left = TextLeft, right = ClientSize.Width - _w.S(22) - ChevW - _w.S(14);
            // The scroll offset is added to every COORDINATE rather than pushed into the Graphics with
            // TranslateTransform. TextRenderer draws through GDI (DrawTextEx) and ignores the world
            // transform entirely, so a transform scrolled the thumbs and the boxes — which are GDI+ — and
            // left every line of text nailed where it was, the old entries showing through behind the new.
            int y = AutoScrollPosition.Y;
            int shown = Shown;
            for (int i = 0; i < shown; i++)
            {
                var r = p.recent[i];

                // Hovered row: the same rounded highlight a RELATED GAMES card uses, so the gesture reads
                // as the same gesture.
                if (i == _hover)
                {
                    var box = new Rectangle(_w.S(8), y + _w.S(2), Math.Max(1, ClientSize.Width - _w.S(18)), RowHeight - _w.S(6));
                    using var path = RoundedRect(box, _w.S(8));
                    using (var bg = new SolidBrush(Color.FromArgb(54, 54, 59))) g.FillPath(bg, path);
                    using (var bd = new Pen(Color.FromArgb(64, 64, 68))) g.DrawPath(bd, path);
                }

                DrawThumb(g, i, y);

                int ty = y + _w.S(6);
                string headline = string.IsNullOrEmpty(r.console) ? (r.title ?? "") : $"{r.title} ({r.console})";
                TextRenderer.DrawText(g, headline, title, new Point(left, ty), Fg, TextFormatFlags.NoPrefix);

                // The percentage hugs the right edge, on the headline's baseline.
                string pct = r.PercentComplete() + "% Complete";
                var pctSize = TextRenderer.MeasureText(pct, title);
                TextRenderer.DrawText(g, pct, title, new Point(right - pctSize.Width, ty), Fg, TextFormatFlags.NoPrefix);

                ty += TextRenderer.MeasureText(headline, title).Height + _w.S(2);
                TextRenderer.DrawText(g, r.LastPlayedLocal(), body, new Point(left, ty), Fg, TextFormatFlags.NoPrefix);

                ty += TextRenderer.MeasureText("Hg", body).Height + _w.S(2);
                string line = $"Earned {r.earned:N0} of {r.total:N0} achievements and {r.points:N0} of {r.possiblePoints:N0} points";
                TextRenderer.DrawText(g, line, body, new Point(left, ty), Fg, TextFormatFlags.NoPrefix);

                DrawChevron(g, i, y);

                y += RowHeight;
                if (i == _openRow) { DrawGrid(g, i, y, body); y += ExtraH(); }
            }

            if (HasFooter) DrawFooter(g, y, body);
        }

        /// <summary>The disclosure chevron: down when the row is closed, up when its grid is open. Drawn as
        /// two strokes rather than a glyph so it lines up on any DPI without depending on a font.</summary>
        private void DrawChevron(Graphics g, int i, int rowY)
        {
            var box = new Rectangle(ClientSize.Width - _w.S(22) - ChevW, rowY + (RowHeight - ChevW) / 2, ChevW, ChevW);
            bool hot = _hoverChevron == i;
            using (var path = RoundedRect(box, _w.S(5)))
            {
                using (var bg = new SolidBrush(hot ? Color.FromArgb(60, 60, 66) : Color.FromArgb(44, 44, 48)))
                    g.FillPath(bg, path);
                using (var bd = new Pen(Color.FromArgb(70, 70, 76))) g.DrawPath(bd, path);
            }
            int w = _w.S(9), h = _w.S(5);
            int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            bool open = i == _openRow;
            using var pen = new Pen(hot ? Color.White : Fg, Math.Max(1.4f, _w.S(2) * 0.8f));
            if (open)
            {
                g.DrawLine(pen, cx - w / 2, cy + h / 2, cx, cy - h / 2);
                g.DrawLine(pen, cx, cy - h / 2, cx + w / 2, cy + h / 2);
            }
            else
            {
                g.DrawLine(pen, cx - w / 2, cy - h / 2, cx, cy + h / 2);
                g.DrawLine(pen, cx, cy + h / 2, cx + w / 2, cy - h / 2);
            }
        }

        /// <summary>The open row's achievements: RA's own badge art, coloured for the ones this account
        /// unlocked and RA's greyed "_lock" variant for the rest — the same two-image convention the detail
        /// pane's card uses. An empty slot means the badge is still downloading.</summary>
        private void DrawGrid(Graphics g, int i, int top, Font font)
        {
            if (!_ach.TryGetValue(i, out var data))
            {
                TextRenderer.DrawText(g, "Loading achievements…", font, new Point(TextLeft, top + _w.S(8)),
                    SubFg, TextFormatFlags.NoPrefix);
                return;
            }
            var list = data.achievements;
            if (list.Count == 0)
            {
                TextRenderer.DrawText(g, "No achievements for this game.", font, new Point(TextLeft, top + _w.S(8)),
                    SubFg, TextFormatFlags.NoPrefix);
                return;
            }
            int cols = GridCols(), step = BadgeSize + BadgeGap;
            int y0 = top + _w.S(6);
            for (int k = 0; k < list.Count; k++)
            {
                var a = list[k];
                int x = TextLeft + (k % cols) * step, y = y0 + (k / cols) * step;
                var slot = new Rectangle(x, y, BadgeSize, BadgeSize);
                Image? img;
                lock (_badgeGate) _badges.TryGetValue(a.id, out img);
                if (img != null) g.DrawImage(img, slot);
                else using (var bg = new SolidBrush(Color.FromArgb(38, 38, 42))) g.FillRectangle(bg, slot);
                // The unlocked ones get a warm outline so a filled grid still reads at a glance; RA's own
                // widget leans on the grey art alone, which disappears at this size.
                using var pen = new Pen(a.unlocked ? Color.FromArgb(190, 160, 90) : Color.FromArgb(60, 60, 64));
                g.DrawRectangle(pen, slot);
            }
        }

        /// <summary>"Show more (N)" / "Show less", drawn as the last row of the list rather than as a
        /// button under it: it belongs to the list, scrolls with it, and needs no second control competing
        /// with the panel for the bottom edge.</summary>
        private void DrawFooter(Graphics g, int y, Font font)
        {
            string text = $"Show more ({Total - Collapsed})";
            var size = TextRenderer.MeasureText(text, font);
            int w = size.Width + _w.S(28), h = FooterH - _w.S(8);
            var box = new Rectangle((ClientSize.Width - w) / 2, y + _w.S(2), w, h);
            using (var path = RoundedRect(box, _w.S(6)))
            {
                using (var bg = new SolidBrush(_hoverFooter ? Color.FromArgb(54, 54, 59) : Color.FromArgb(44, 44, 48)))
                    g.FillPath(bg, path);
                using (var bd = new Pen(Color.FromArgb(64, 64, 68))) g.DrawPath(bd, path);
            }
            TextRenderer.DrawText(g, text, font, box, _hoverFooter ? Color.White : Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>The cover, letterboxed inside its slot so a tall box art and a wide arcade marquee
        /// both keep their shape. An empty slot is drawn as the frame alone — a row that is still loading
        /// and a row RA has no art for look the same, which is honest: neither has a picture to show.</summary>
        private void DrawThumb(Graphics g, int i, int rowY)
        {
            var slot = new Rectangle(ThumbX, rowY + (RowHeight - ThumbH) / 2, ThumbW, ThumbH);
            using (var bg = new SolidBrush(Color.FromArgb(38, 38, 42))) g.FillRectangle(bg, slot);
            _thumbs.TryGetValue(i, out var img);
            if (img != null)
            {
                double k = Math.Min((double)slot.Width / img.Width, (double)slot.Height / img.Height);
                int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, slot.X + (slot.Width - w) / 2, slot.Y + (slot.Height - h) / 2, w, h);
            }
            using var pen = new Pen(Color.FromArgb(64, 64, 68));
            g.DrawRectangle(pen, slot);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        // Scrolling has to repaint the WHOLE panel, not the strip Windows uncovers.
        //
        // An AutoScroll panel scrolls by blitting the pixels it already has and invalidating only the band
        // that came into view. That works for a panel made of child controls, which move with the blit.
        // This one is PAINTED, at content coordinates, into a double buffer that the blit never touches —
        // so the moved pixels stayed on screen under the newly painted rows and the old entries showed
        // through behind the new ones. Invalidating everything on every scroll costs one repaint of five
        // to a hundred text rows, which is nothing, and it is the only way the buffer and the screen agree.
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── dark tabs (same treatment as the other LiteBox windows) ──────────────

    private TabControl NewDarkTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(S(140), S(26)), Padding = new Point(S(8), S(4)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds,
                sel ? Color.White : SubFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        Controls.Add(tabs);
        tabs.BringToFront();
        return tabs;
    }

    private TabPage NewTabPage(TabControl tabs, string title)
    {
        var p = new TabPage(title) { BackColor = Bg, UseVisualStyleBackColor = false };
        tabs.TabPages.Add(p);
        return p;
    }

    /// <summary>Widen the two tabs until they very nearly fill the row.
    ///
    /// The strip BESIDE the tabs is comctl32's, painted in the SYSTEM theme — white on this one — and
    /// owner draw reaches the tab items and nothing else. Left alone, two default-width tabs in an
    /// 810-wide window leave that white across most of the row.
    ///
    /// Deliberately a few pixels SHORT rather than exact: overshoot turns the row into a scroller with
    /// arrows and one tab hidden, so a 6px sliver at the right edge is the safe side to err on.
    /// TabSizeMode.FillToRight is not the answer — it only distributes across WRAPPED rows.
    ///
    /// Driven from the FORM's SizeChanged, never the TabControl's Resize: writing ItemSize recreates
    /// the tab control's handle, which fires ITS Resize again — a loop that exhausts window handles and
    /// takes the whole process down with "Error creating window handle" (found the hard way).</summary>
    private void FitTabs(TabControl tabs)
    {
        int n = tabs.TabPages.Count;
        if (n == 0) return;
        int w = Math.Max(S(90), (tabs.ClientSize.Width - S(8)) / n);
        if (tabs.ItemSize.Width != w) tabs.ItemSize = new Size(w, tabs.ItemSize.Height);
    }

    /// <summary>Give the leaderboard's name column whatever the other two don't use, so the row — and
    /// the highlight on the user's own row — reaches the right edge instead of stopping short.</summary>
    private void FitBoard()
    {
        if (_board.Columns.Count < 3) return;
        int rest = _board.ClientSize.Width - _board.Columns[0].Width - _board.Columns[2].Width;
        int w = Math.Max(S(120), rest);
        if (_board.Columns[1].Width != w) _board.Columns[1].Width = w;
    }
}
