// Edit Game window — per-game editor modelled on LaunchBox's "Edit Game" dialog and heavily
// inspired by ExtendDB's Editgameform (the data-entry / "saisie" part), but re-homed onto the
// LiteBox data layer: every field writes through the HostGame SETTERS (SDK props + ILiteBoxFields),
// so each change lands in the GameStore op-log → persisted to the Platform XML, exactly like any
// other host write. Read-only mode disables inputs and never writes.
//
// Deliberately DIFFERENT from ExtendDB's editor:
//   • NO lock system (that stays ExtendDB-specific for now) — fields are plain, no 🔓 buttons.
//
// Multi-select (adapted from ExtendDB): the editor takes 1..N games. Each field shows the common value,
// or a "‹multiple values›" placeholder when they differ. Only fields the user actually TOUCHES are
// written, to ALL selected games — untouched fields keep each game's own value. (A "touched" set stands
// in for ExtendDB's read-only-until-double-click lock; it also avoids re-journalling unchanged fields.)
//
// Shell: left navigation TREE (full LB Edit-Game hierarchy), right content panel, bottom bar with
// OK / Cancel + ◄ ► (navigate the visible list; navigating saves first, like LB). Only the Metadata
// page has content today; every other node is present in the tree with a placeholder panel.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed class EditGameWindow : Form
{
    // Palette — matches OptionsWindow / EditEmulatorWindow.
    private static readonly Color Bg = Color.FromArgb(24, 24, 30);
    private static readonly Color PanelC = Color.FromArgb(34, 34, 42);
    private static readonly Color Field = Color.FromArgb(45, 45, 54);
    private static readonly Color Fg = Color.FromArgb(222, 222, 222);
    private static readonly Color SubFg = Color.FromArgb(150, 150, 165);
    private static readonly Color Accent = Color.FromArgb(0, 122, 204);
    private static readonly Color ModifiedColor = Color.FromArgb(235, 150, 135);   // slightly-red tint for a changed field

    // Layout constants (two-column grid).
    private const int Lx = 14, Lw = 92, LFx = 110, FW = 250;   // left column: label x/w, field x, field width
    private const int Rx = 380, Rw = 80, RFx = 462;            // right column: label x/w, field x
    private const int FullW = 602;                             // full-width field (Title / URLs)
    private const int RowH = 30, FieldH = 23;

    private readonly IReadOnlyList<IGame> _visible;
    private readonly bool _readOnly;
    private int _index;
    private IReadOnlyList<IGame> _editGames;   // the game(s) being edited (1 for single, N for multi)
    private bool IsMulti => _editGames.Count > 1;
    private bool _loading;                     // suppress dirty-tracking while LoadMetadata sets values
    private bool _restoring;                   // suppress dirty-tracking while a revert restores a value
    private const string Multi = "‹multiple values›";

    // Dirty-field tracking: baseline (loaded) value-string per control, a per-field ↺ revert button, and
    // the list of trackable controls. A field is "modified" when its current value differs from the
    // baseline — shown reddish with the ↺ button; on save it is written (to every edited game) UNLESS it
    // still holds the Multi placeholder. Reverting restores the baseline (so it never writes the placeholder).
    private readonly Dictionary<Control, string> _baseline = new();
    private readonly Dictionary<Control, Button> _revert = new();
    private readonly List<Control> _fields = new();
    private readonly ToolTip _tips = new();

    // Distinct library values that seed the editable combos (built once).
    private readonly Dictionary<string, string[]> _choices = new(StringComparer.Ordinal);

    // Shell.
    private readonly TreeView _tree;
    private readonly Panel _host;
    private readonly Label _titleBar;
    private readonly Button _prev, _next;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);

    // Metadata controls (kept so navigation just reloads values).
    private TextBox _title = null!, _releaseDate = null!, _lastPlayed = null!, _videoUrl = null!, _wikiUrl = null!, _version = null!;
    private ComboBox _rating = null!, _releaseType = null!, _genre = null!, _platform = null!, _developer = null!,
                     _publisher = null!, _series = null!, _region = null!, _playMode = null!, _status = null!,
                     _source = null!, _progress = null!;
    private NumericUpDown _maxPlayers = null!;
    private CheckBox _favorite = null!, _portable = null!, _installed = null!, _hide = null!, _broken = null!;
    private Label _dateAdded = null!, _dateModified = null!, _playCount = null!;
    private StarBar _starBar = null!;

    public static void Open(IReadOnlyList<IGame> games, IReadOnlyList<IGame> visible, bool readOnly, IWin32Window? owner)
    {
        if (games == null || games.Count == 0) return;
        using var w = new EditGameWindow(games, visible, readOnly);
        w.ShowDialog(owner);
    }

    private EditGameWindow(IReadOnlyList<IGame> games, IReadOnlyList<IGame> visible, bool readOnly)
    {
        _editGames = games;
        _visible = visible ?? Array.Empty<IGame>();
        _readOnly = readOnly;
        _index = games.Count > 1 ? -1 : IndexOf(games[0]);

        BuildChoices();

        Size = new Size(940, 660);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ShowIcon = false; ShowInTaskbar = false;
        MaximizeBox = false; MinimizeBox = false;
        KeyPreview = true;

        // ── Left navigation tree ─────────────────────────────────────────
        _tree = new TreeView
        {
            Dock = DockStyle.Left, Width = 210,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.None,
            HideSelection = false, FullRowSelect = true, ShowLines = false, ShowPlusMinus = true,
            ItemHeight = 26, DrawMode = TreeViewDrawMode.OwnerDrawText, Indent = 18,
            Font = new Font("Segoe UI", 9.5f),
        };
        _tree.DrawNode += OnDrawNode;
        _tree.AfterSelect += (_, e) => ShowPage(e.Node?.Tag as string ?? "Metadata");
        BuildTree();

        // ── Right: title bar + content host ──────────────────────────────
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _titleBar = new Label
        {
            Dock = DockStyle.Top, Height = 34, BackColor = PanelC, ForeColor = Fg,
            Text = "Metadata", TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        };
        _host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(6, 6, 6, 6) };
        right.Controls.Add(_host);
        right.Controls.Add(_titleBar);

        // ── Bottom bar: OK / Cancel + hint + ◄ ► ─────────────────────────
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = PanelC };
        var ok = FooterBtn("OK", Color.FromArgb(50, 110, 65));
        var cancel = FooterBtn("Cancel", Color.FromArgb(70, 70, 82));
        ok.Location = new Point(12, 9);
        cancel.Location = new Point(112, 9);
        ok.Click += (_, _) => { SaveCurrent(); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var hint = new Label
        {
            AutoSize = true, ForeColor = SubFg, BackColor = PanelC,
            Text = _readOnly ? "Read-only — changes are not saved"
                 : _editGames.Count > 1 ? $"Editing {_editGames.Count} games — only fields you change are applied to all"
                 : "Navigating will save immediately",
            Font = new Font("Segoe UI", 9f),
        };
        _prev = NavBtn("◄"); _next = NavBtn("►");
        _prev.Click += (_, _) => Navigate(-1);
        _next.Click += (_, _) => Navigate(+1);
        bottom.Controls.AddRange(new Control[] { ok, cancel, hint, _prev, _next });
        bottom.Resize += (_, _) =>
        {
            int r = bottom.ClientSize.Width - 12;
            _next.Left = r - _next.Width; _prev.Left = _next.Left - _prev.Width - 6;
            hint.Left = _prev.Left - hint.Width - 12; hint.Top = (bottom.Height - hint.Height) / 2;
        };

        Controls.Add(right);
        Controls.Add(_tree);
        Controls.Add(bottom);
        right.BringToFront();

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

        // Build + show the Metadata page, then load the current game.
        _pages["Metadata"] = BuildMetadataPage();
        LoadMetadata();
        _tree.SelectedNode = _tree.Nodes[0];   // Metadata
        ShowPage("Metadata");
        UpdateChrome();
        if (_readOnly) DisableInputs(_pages["Metadata"]);
    }

    // ── Navigation tree ──────────────────────────────────────────────────
    private void BuildTree()
    {
        TreeNode N(string text, string tag) => new(text) { Tag = tag };

        var metadata = N("Metadata", "Metadata");
        metadata.Nodes.AddRange(new[]
        {
            N("Notes", "Notes"),
            N("Custom Fields", "CustomFields"),
            N("Sort Title", "SortTitle"),
            N("Additional Apps", "AdditionalApps"),
            N("Alternate Names", "AlternateNames"),
            N("Controller Support", "ControllerSupport"),
            N("Game Saves", "GameSaves"),
        });

        var media = N("Media", "Media");
        media.Nodes.AddRange(new[]
        {
            N("Images", "Images"),
            N("Videos", "Videos"),
            N("3D Model Settings", "ModelSettings"),
        });

        var launching = N("Launching", "Launching");
        var dosbox = N("DOSBox", "DOSBox");
        dosbox.Nodes.Add(N("Mounts", "Mounts"));
        launching.Nodes.Add(dosbox);
        launching.Nodes.AddRange(new[]
        {
            N("Emulation", "Emulation"),
            N("Root Folder", "RootFolder"),
            N("Startup/Pause", "StartupPause"),
        });

        _tree.Nodes.AddRange(new[] { metadata, media, launching });
        _tree.ExpandAll();
    }

    private void OnDrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        // OwnerDrawText: the system draws the background (incl. selection) + the ± glyph; we draw
        // only the label — so we never paint over the expand glyph. Top nodes brighter than children.
        if (e.Node == null) { e.DrawDefault = true; return; }
        bool sel = (e.State & TreeNodeStates.Selected) != 0;
        var color = sel ? Color.White : (e.Node.Level == 0 ? Fg : SubFg);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, _tree.Font, e.Bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void ShowPage(string key)
    {
        _titleBar.Text = _tree.SelectedNode?.Text ?? key;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key == "Metadata" ? BuildMetadataPage() : Placeholder(_tree.SelectedNode?.Text ?? key);
            _pages[key] = page;
            if (_readOnly) DisableInputs(page);
        }
        _host.SuspendLayout();
        _host.Controls.Clear();
        page.Dock = DockStyle.Fill;
        _host.Controls.Add(page);
        _host.ResumeLayout();
    }

    private Control Placeholder(string title)
    {
        var p = new Panel { BackColor = Bg };
        var l = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            Text = $"{title}\n\n(not implemented yet)",
        };
        p.Controls.Add(l);
        return p;
    }

    // ── Metadata page ────────────────────────────────────────────────────
    private Control BuildMetadataPage()
    {
        var p = new Panel { BackColor = Bg, AutoScroll = true };
        int y = 10;

        // Title (full width) + a disabled "Search for Metadata" placeholder (scraping is ExtendDB's job).
        Cap("Title", Lx, y, Lw, p);
        _title = Txt("", LFx, y, FullW - 168, p);
        var search = MiniBtn("Search for Metadata", new Point(LFx + FullW - 158, y - 1), 158);
        search.Enabled = false;
        var tip = new ToolTip(); tip.SetToolTip(search, "Metadata scraping is provided by the ExtendDB plugin.");
        p.Controls.Add(search);
        y += RowH;

        // Release Date | Rating
        Cap("Release Date", Lx, y, Lw, p); _releaseDate = DateField(LFx, y, FW, p);
        Cap("Rating", Rx, y, Rw, p); _rating = Cbo("", _choices["Rating"], RFx, y, FW, p);
        y += RowH;

        // Release Type | Max Players
        Cap("Release Type", Lx, y, Lw, p); _releaseType = Cbo("", _choices["ReleaseType"], LFx, y, FW, p);
        Cap("Max Players", Rx, y, Rw, p); _maxPlayers = Num(0, 0, 64, RFx, y, 80, p);
        y += RowH;

        // Genre | Platform
        Cap("Genre", Lx, y, Lw, p); _genre = Cbo("", _choices["Genre"], LFx, y, FW, p);
        Cap("Platform", Rx, y, Rw, p); _platform = Cbo("", _choices["Platform"], RFx, y, FW, p);
        y += RowH;

        // Developer | Publisher
        Cap("Developer", Lx, y, Lw, p); _developer = Cbo("", _choices["Developer"], LFx, y, FW, p);
        Cap("Publisher", Rx, y, Rw, p); _publisher = Cbo("", _choices["Publisher"], RFx, y, FW, p);
        y += RowH;

        // Series | Region
        Cap("Series", Lx, y, Lw, p); _series = Cbo("", _choices["Series"], LFx, y, FW, p);
        Cap("Region", Rx, y, Rw, p); _region = Cbo("", _choices["Region"], RFx, y, FW, p);
        y += RowH;

        // Play Mode | Version
        Cap("Play Mode", Lx, y, Lw, p); _playMode = Cbo("", _choices["PlayMode"], LFx, y, FW, p);
        Cap("Version", Rx, y, Rw, p); _version = Txt("", RFx, y, FW, p);
        y += RowH;

        // Status | Source
        Cap("Status", Lx, y, Lw, p); _status = Cbo("", _choices["Status"], LFx, y, FW, p);
        Cap("Source", Rx, y, Rw, p); _source = Cbo("", _choices["Source"], RFx, y, FW, p);
        y += RowH;

        // Last Played | Progress
        Cap("Last Played", Lx, y, Lw, p); _lastPlayed = DateField(LFx, y, FW, p);
        Cap("Progress", Rx, y, Rw, p); _progress = Cbo("", _choices["Progress"], RFx, y, FW, p);
        y += RowH;

        // Video URL (full) | Wikipedia URL (full)
        Cap("Video URL", Lx, y, Lw, p); _videoUrl = Txt("", LFx, y, FullW, p); y += RowH;
        Cap("Wikipedia URL", Lx, y, Lw, p); _wikiUrl = Txt("", LFx, y, FullW, p); y += RowH;

        // Star rating — same widget language as the main window's hero panel: cyan = community/default
        // rating, yellow = the user's own rating. Click a star to set your rating; click the current
        // value again to clear it (reverts to the community rating shown in cyan).
        Cap("Star Rating", Lx, y, Lw, p);
        _starBar = new StarBar { Location = new Point(LFx, y - 2), ReadOnly = _readOnly };
        _starBar.Changed = () => OnField(_starBar);
        p.Controls.Add(_starBar); Track(_starBar, p);
        y += RowH + 6;

        // ── Read-only info + flags ───────────────────────────────────────
        _dateAdded = InfoLabel("Date Added:", Lx, y, p);
        var favorite = ChkBox("Favorite", RFx + 8, y); _favorite = favorite;
        var portable = ChkBox("Portable", RFx + 130, y); _portable = portable;
        y += 24;
        _dateModified = InfoLabel("Date Modified:", Lx, y, p);
        _installed = ChkBox("Installed", RFx + 8, y);
        _hide = ChkBox("Hide", RFx + 130, y);
        y += 24;
        _playCount = InfoLabel("Play Count(Time):", Lx, y, p);
        _broken = ChkBox("Broken", RFx + 8, y);
        foreach (var cb in new[] { _favorite, _portable, _installed, _hide, _broken }) p.Controls.Add(cb);

        return p;
    }

    // Star-rating widget mirroring MainWindow.HeroPanel: cyan when showing the community/default
    // rating, yellow once the user sets their own; empty stars are faint white; a numeric to the left.
    // Click a star to set the user rating; click the current value to clear it (back to community).
    private sealed class StarBar : Panel
    {
        private static readonly Color Community = Color.FromArgb(0x38, 0xD6, 0xE6);   // cyan (default/community)
        private static readonly Color User      = Color.FromArgb(0xF6, 0xC3, 0x44);   // yellow (user-set)
        private static readonly Color Empty     = Color.FromArgb(105, 255, 255, 255); // faint white
        private const int StarW = 22, NumW = 34;

        // Ratings are HALF-STAR precise (0, 0.5, 1, … 5) — StarRatingFloat is a float.
        private double _userValue;   // 0..5 in .5 steps — persisted (0 = not rated → show community)
        private double _community;   // community average (display fallback, drawn to its exact fraction)
        private double _hoverValue = -1;
        private readonly Rectangle[] _rects = new Rectangle[5];
        private Rectangle _clearRect;   // the ✕ hit-area (only present when a user rating is set)
        private bool _hoverClear;

        public bool ReadOnly;
        public Action? Changed;   // fired when the user rating changes (drives the parent's touched-set)

        public StarBar()
        {
            DoubleBuffered = true;
            Height = 26; Width = NumW + 5 * StarW + 34;   // room for the ✕ clear button
            BackColor = Bg;
        }

        public double UserValue => _userValue;
        public bool Dirty;   // reddish numeric when the user rating differs from its loaded value
        public void SetFrom(double userRating, double community)
        {
            _userValue = Math.Round(userRating * 2) / 2.0;   // snap the user rating to the nearest half
            _community = community;
            Invalidate();
        }
        public void SetUserValue(double v) { _userValue = Math.Round(v * 2) / 2.0; Invalidate(); }

        private bool IsUser => _userValue > 0;
        private double Effective => IsUser ? _userValue : _community;

        // The half-precise value under the mouse: left half of star i → i+0.5, right half → i+1; -1 outside.
        private double ValueAt(int mouseX)
        {
            for (int i = 0; i < 5; i++)
                if (mouseX >= _rects[i].X && mouseX < _rects[i].Right)
                    return mouseX < _rects[i].X + StarW / 2.0 ? i + 0.5 : i + 1;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var numFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var starFont = new Font("Segoe UI Symbol", 13f);
            var sfNum = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            var sfStar = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            bool hovering = !ReadOnly && _hoverValue > 0;
            double eff = hovering ? _hoverValue : Effective;
            var fill = hovering ? User : (IsUser ? User : Community);

            string num = eff > 0 ? eff.ToString("0.0", CultureInfo.InvariantCulture) : "—";
            using (var tb = new SolidBrush(Dirty ? ModifiedColor : Color.FromArgb(200, 200, 205)))
                g.DrawString(num, numFont, tb, new RectangleF(0, 0, NumW, Height), sfNum);

            int sx = NumW + 4;
            for (int i = 0; i < 5; i++)
            {
                _rects[i] = new Rectangle(sx + i * StarW, 0, StarW, Height);
                var cell = new RectangleF(sx + i * StarW, 0, StarW, Height);
                using (var be = new SolidBrush(Empty)) g.DrawString("★", starFont, be, cell, sfStar);   // empty base

                double frac = Math.Max(0, Math.Min(1, eff - i));   // 0, .5 (half) or partial for community
                if (frac > 0)
                {
                    var saved = g.Clip;
                    g.SetClip(new RectangleF(cell.X, cell.Y, (float)(StarW * frac), Height));
                    using (var bf = new SolidBrush(fill)) g.DrawString("★", starFont, bf, cell, sfStar);
                    g.Clip = saved;
                }
            }

            // Clear affordance — shown only when the user has set their own rating (not read-only):
            // removes it, reverting to the community rating (cyan). Reddens on hover.
            if (IsUser && !ReadOnly)
            {
                int cx = sx + 5 * StarW + 6;
                _clearRect = new Rectangle(cx, 0, 20, Height);
                using var cb = new SolidBrush(_hoverClear ? Color.FromArgb(235, 120, 120) : Color.FromArgb(140, 140, 150));
                g.DrawString("✕", numFont, cb, new RectangleF(cx, 0, 20, Height), sfStar);
            }
            else _clearRect = Rectangle.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (ReadOnly) return;
            bool oc = _clearRect.Contains(e.Location);
            double v = oc ? -1 : ValueAt(e.X);
            Cursor = (v > 0 || oc) ? Cursors.Hand : Cursors.Default;
            if (v != _hoverValue || oc != _hoverClear) { _hoverValue = v; _hoverClear = oc; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverValue != -1 || _hoverClear) { _hoverValue = -1; _hoverClear = false; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (ReadOnly) return;
            if (_clearRect.Contains(e.Location)) { if (_userValue > 0) { _userValue = 0; Invalidate(); Changed?.Invoke(); } return; }
            double v = ValueAt(e.X);
            if (v <= 0) return;
            _userValue = Math.Abs(_userValue - v) < 0.01 ? 0 : v;   // click the current value → clear (revert to community)
            Invalidate(); Changed?.Invoke();
        }
    }

    // ── Load / Save ──────────────────────────────────────────────────────
    // Loads the common value of each field across ALL edited games (or the "‹multiple values›"
    // placeholder when they differ). Touch-tracking is suppressed during the load.
    private void LoadMetadata()
    {
        _loading = true;
        try
        {
            SetText(_title, MergeStr(g => g.Title));
            SetText(_version, MergeStr(g => g.Version));
            SetText(_videoUrl, MergeStr(g => g.VideoUrl));
            SetText(_wikiUrl, MergeStr(g => g.WikipediaUrl));
            SetText(_releaseDate, MergeDate(g => g.ReleaseDate));
            SetText(_lastPlayed, MergeDate(g => g.LastPlayedDate));
            SetCombo(_rating, MergeStr(g => g.Rating));
            SetCombo(_releaseType, MergeStr(g => g.ReleaseType));
            SetCombo(_genre, MergeStr(g => g.GenresString));
            SetCombo(_platform, MergeStr(g => g.Platform));
            SetCombo(_developer, MergeStr(g => g.Developer));
            SetCombo(_publisher, MergeStr(g => g.Publisher));
            SetCombo(_series, MergeStr(g => g.Series));
            SetCombo(_region, MergeStr(g => g.Region));
            SetCombo(_playMode, MergeStr(g => g.PlayMode));
            SetCombo(_status, MergeStr(g => g.Status));
            SetCombo(_source, MergeStr(g => g.Source));
            SetCombo(_progress, MergeStr(g => g.Progress));

            _maxPlayers.Value = Clamp(MergeVal(g => g.MaxPlayers ?? 0) ?? 0, 0, 64);
            _starBar.SetFrom(MergeVal(g => (double)g.StarRatingFloat) ?? 0, MergeVal(g => (double)g.CommunityStarRating) ?? 0);

            SetCheck(_favorite, MergeVal(g => g.Favorite));
            SetCheck(_portable, MergeVal(g => g.Portable));
            SetCheck(_hide, MergeVal(g => g.Hide));
            SetCheck(_broken, MergeVal(g => g.Broken));
            SetCheck(_installed, MergeVal(g => g.Installed == true));

            if (IsMulti)
            {
                _dateAdded.Text = $"{_editGames.Count} games selected";
                _dateModified.Text = "";
                _playCount.Text = "";
            }
            else
            {
                var g0 = _editGames[0];
                _dateAdded.Text = "Date Added:   " + FmtDateTime(Safe(() => g0.DateAdded));
                _dateModified.Text = "Date Modified:   " + FmtDateTime(Safe(() => g0.DateModified));
                int pc = Safe(() => g0.PlayCount); int pt = Safe(() => g0.PlayTime);
                _playCount.Text = $"Play Count(Time):   {pc} ({FmtDuration(pt)})";
            }
        }
        finally
        {
            // Baseline = the just-loaded value of every field; then paint each field's dirty state
            // (all clean at this point → normal colour, ↺ hidden).
            foreach (var c in _fields) { _baseline[c] = ValueStr(c); RefreshFieldState(c); }
            _loading = false;
        }

        void SetText(TextBox t, string v) { t.Text = v; t.ForeColor = v == Multi ? SubFg : Fg; }
        void SetCombo(ComboBox c, string v) { c.Text = v; c.ForeColor = v == Multi ? SubFg : Fg; }
        void SetCheck(CheckBox cb, bool? merged)
        {
            cb.ThreeState = IsMulti;
            cb.CheckState = merged.HasValue ? (merged.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
        }
        string MergeStr(Func<IGame, string> get)
        {
            string? first = null;
            foreach (var g in _editGames) { var v = (Safe(() => get(g)) ?? "").Trim(); if (first == null) first = v; else if (first != v) return Multi; }
            return first ?? "";
        }
        string MergeDate(Func<IGame, DateTime?> get)
        {
            string? first = null;
            foreach (var g in _editGames) { var v = FmtDate(Safe(() => get(g))); if (first == null) first = v; else if (first != v) return Multi; }
            return first ?? "";
        }
        T? MergeVal<T>(Func<IGame, T> get) where T : struct
        {
            bool first = true; T acc = default;
            foreach (var g in _editGames) { T v = Safe(() => get(g)); if (first) { acc = v; first = false; } else if (!acc.Equals(v)) return null; }
            return first ? (T?)null : acc;
        }
    }

    // Writes ONLY the fields the user actually changed (current != loaded baseline), to EVERY edited game.
    // A field still holding the "‹multiple values›" placeholder is never written — so untouched (or
    // reverted) multi-value fields keep each game's own value.
    private void SaveCurrent()
    {
        if (_readOnly) return;
        foreach (var g in _editGames)
        {
            if (Writable(_title)) W(() => g.Title = _title.Text.Trim());
            if (Writable(_rating)) W(() => g.Rating = _rating.Text.Trim());
            if (Writable(_releaseType)) W(() => g.ReleaseType = _releaseType.Text.Trim());
            if (Writable(_genre)) W(() => g.GenresString = _genre.Text.Trim());
            if (Writable(_platform)) W(() => g.Platform = _platform.Text.Trim());
            if (Writable(_developer)) W(() => g.Developer = _developer.Text.Trim());
            if (Writable(_publisher)) W(() => g.Publisher = _publisher.Text.Trim());
            if (Writable(_series)) W(() => g.Series = _series.Text.Trim());
            if (Writable(_region)) W(() => g.Region = _region.Text.Trim());
            if (Writable(_playMode)) W(() => g.PlayMode = _playMode.Text.Trim());
            if (Writable(_version)) W(() => g.Version = _version.Text.Trim());
            if (Writable(_status)) W(() => g.Status = _status.Text.Trim());
            if (Writable(_source)) W(() => g.Source = _source.Text.Trim());
            if (Writable(_progress)) W(() => g.Progress = _progress.Text.Trim());
            if (Writable(_videoUrl)) W(() => g.VideoUrl = _videoUrl.Text.Trim());
            if (Writable(_wikiUrl)) W(() => g.WikipediaUrl = _wikiUrl.Text.Trim());
            if (Writable(_releaseDate)) W(() => g.ReleaseDate = ParseDate(_releaseDate.Text));
            if (Writable(_lastPlayed)) W(() => g.LastPlayedDate = ParseDate(_lastPlayed.Text));
            if (Writable(_maxPlayers)) { int v = (int)_maxPlayers.Value; int? mp = v <= 0 ? (int?)null : v; W(() => g.MaxPlayers = mp); }
            if (Writable(_starBar)) W(() => g.StarRatingFloat = (float)_starBar.UserValue);
            if (Writable(_favorite) && _favorite.CheckState != CheckState.Indeterminate) W(() => g.Favorite = _favorite.Checked);
            if (Writable(_portable) && _portable.CheckState != CheckState.Indeterminate) W(() => g.Portable = _portable.Checked);
            if (Writable(_hide) && _hide.CheckState != CheckState.Indeterminate) W(() => g.Hide = _hide.Checked);
            if (Writable(_broken) && _broken.CheckState != CheckState.Indeterminate) W(() => g.Broken = _broken.Checked);
            if (Writable(_installed) && _installed.CheckState != CheckState.Indeterminate) W(() => g.Installed = _installed.Checked);
        }

        bool Writable(Control c) => Modified(c) && !IsPlaceholder(c);
        static void W(Action a) { try { a(); } catch { } }
    }

    // Prev/Next walk the visible list — single-game mode only (multi edits the whole set at once).
    private void Navigate(int delta)
    {
        if (IsMulti || _visible.Count == 0) return;
        int ni = _index + delta;
        if (ni < 0 || ni >= _visible.Count) return;
        SaveCurrent();
        _index = ni;
        _editGames = new[] { _visible[_index] };
        LoadMetadata();
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        Text = IsMulti ? $"Edit {_editGames.Count} Games" : "Edit Game: " + (Safe(() => _editGames[0].Title) ?? "");
        bool nav = !IsMulti && _visible.Count > 1 && _index >= 0;
        _prev.Enabled = nav && _index > 0;
        _next.Enabled = nav && _index < _visible.Count - 1;
    }

    // ── Dirty-field tracking (reddish text + per-field ↺ revert) ─────────
    // Registers a control for dirty-tracking and gives it an overlay ↺ button (hidden until modified)
    // at its right edge, which restores the loaded value. Checkboxes register via _fields directly
    // (no ↺ — trivial to toggle back).
    private void Track(Control c, Panel p)
    {
        _fields.Add(c);
        var b = new Button
        {
            Text = "↺", Size = new Size(18, 18), Visible = false, TabStop = false, Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(92, 46, 42),          // dark-red fill → clearly reads as a button
            ForeColor = Color.FromArgb(255, 180, 165),
            Font = new Font("Segoe UI Symbol", 9.5f),        // symbol font so the ↺ renders (plain Segoe UI shows tofu)
            FlatAppearance = { BorderSize = 1, MouseOverBackColor = Color.FromArgb(120, 58, 52) },
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
        int cy = c.Top + Math.Max(0, (c.Height - 18) / 2);
        b.Location = c switch
        {
            ComboBox => new Point(c.Right - 36, cy),        // left of the dropdown arrow
            NumericUpDown => new Point(c.Right - 34, cy),   // left of the spin buttons
            StarBar => new Point(c.Right + 3, cy),          // just right of the star widget
            _ => new Point(c.Right - 18, cy),               // over a text box's right edge
        };
        b.Click += (_, _) => RevertField(c);
        _tips.SetToolTip(b, "Restore the original value");
        p.Controls.Add(b); b.BringToFront();
        _revert[c] = b;
    }

    private void OnField(Control c) { if (!_loading && !_restoring) RefreshFieldState(c); }

    private void RefreshFieldState(Control c)
    {
        bool mod = Modified(c);
        if (c is StarBar s) { s.Dirty = mod; s.Invalidate(); }
        else c.ForeColor = mod ? ModifiedColor : (IsPlaceholder(c) ? SubFg : Fg);
        if (_revert.TryGetValue(c, out var b)) b.Visible = mod && !_readOnly;
    }

    private void RevertField(Control c)
    {
        if (!_baseline.TryGetValue(c, out var b)) return;
        _restoring = true;
        try
        {
            switch (c)
            {
                case ComboBox cb: cb.Text = b; break;
                case TextBox t: t.Text = b; break;
                case NumericUpDown n: n.Value = decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? Math.Max(n.Minimum, Math.Min(n.Maximum, d)) : n.Minimum; break;
                case CheckBox ck: ck.CheckState = Enum.TryParse<CheckState>(b, out var cs) ? cs : CheckState.Unchecked; break;
                case StarBar s: s.SetUserValue(double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var uv) ? uv : 0); break;
            }
        }
        finally { _restoring = false; }
        RefreshFieldState(c);
    }

    private bool Modified(Control c) => _baseline.TryGetValue(c, out var b) && b != ValueStr(c);
    private bool IsPlaceholder(Control c) => (c is TextBox t && t.Text == Multi) || (c is ComboBox cb && cb.Text == Multi);
    private static string ValueStr(Control c) => c switch
    {
        ComboBox cb => cb.Text,
        TextBox t => t.Text,
        NumericUpDown n => n.Value.ToString(CultureInfo.InvariantCulture),
        CheckBox ck => ck.CheckState.ToString(),
        StarBar s => s.UserValue.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    private int IndexOf(IGame g)
    {
        string? id = Safe(() => g.Id);
        for (int i = 0; i < _visible.Count; i++)
            if (string.Equals(Safe(() => _visible[i].Id), id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── Distinct-value choice lists (one pass over the library) ──────────
    private void BuildChoices()
    {
        Sset genre = New(), dev = New(), pub = New(), series = New(), region = New(),
             playMode = New(), source = New(), status = New(), releaseType = New(), rating = New(), progress = New();
        // A couple of known LB sets merged in so common values are offered even on an empty library.
        foreach (var r in new[] { "E", "E10+", "T", "M", "AO", "RP" }) rating.Add(r);
        foreach (var r in new[] { "Full", "Demo", "Prototype", "Beta", "Homebrew", "Hack" }) releaseType.Add(r);
        foreach (var s in new[] { "Imperfect", "Playable", "Preservable", "Unplayable" }) status.Add(s);
        foreach (var s in new[] { "Not Started / Unplayed", "Playing / Progressing", "Beaten / Completed", "Mastered / 100%", "Abandoned" }) progress.Add(s);

        try
        {
            foreach (var g in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
            {
                AddMulti(genre, Safe(() => g.GenresString));
                AddMulti(dev, Safe(() => g.Developer));
                AddMulti(pub, Safe(() => g.Publisher));
                AddMulti(series, Safe(() => g.Series));
                AddMulti(playMode, Safe(() => g.PlayMode));
                Add(region, Safe(() => g.Region));
                Add(source, Safe(() => g.Source));
                Add(status, Safe(() => g.Status));
                Add(releaseType, Safe(() => g.ReleaseType));
                Add(rating, Safe(() => g.Rating));
                Add(progress, Safe(() => g.Progress));
            }
        }
        catch { }

        Sset platform = New();
        try { foreach (var pl in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>()) Add(platform, Safe(() => pl.Name)); }
        catch { }

        _choices["Genre"] = Arr(genre); _choices["Developer"] = Arr(dev); _choices["Publisher"] = Arr(pub);
        _choices["Series"] = Arr(series); _choices["Region"] = Arr(region); _choices["PlayMode"] = Arr(playMode);
        _choices["Source"] = Arr(source); _choices["Status"] = Arr(status); _choices["ReleaseType"] = Arr(releaseType);
        _choices["Rating"] = Arr(rating); _choices["Progress"] = Arr(progress); _choices["Platform"] = Arr(platform);

        static Sset New() => new(StringComparer.OrdinalIgnoreCase);
        static void Add(Sset s, string? v) { v = v?.Trim(); if (!string.IsNullOrEmpty(v)) s.Add(v!); }
        static void AddMulti(Sset s, string? v) { if (string.IsNullOrEmpty(v)) return; foreach (var part in v!.Split(';')) Add(s, part); }
        static string[] Arr(Sset s) => s.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private sealed class Sset : SortedSet<string> { public Sset(IComparer<string> c) : base(c) { } }

    // ── UI helpers ───────────────────────────────────────────────────────
    private Label Cap(string text, int x, int y, int w, Panel p)
    {
        var l = new Label { Text = text, Location = new Point(x, y + 3), Size = new Size(w, FieldH), ForeColor = SubFg, BackColor = Bg, TextAlign = ContentAlignment.MiddleLeft };
        p.Controls.Add(l); return l;
    }

    private TextBox Txt(string v, int x, int y, int w, Panel p)
    {
        var t = new TextBox { Text = v, Location = new Point(x, y), Width = w, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        t.TextChanged += (_, _) => OnField(t);
        p.Controls.Add(t); Track(t, p); return t;
    }

    private ComboBox Cbo(string v, string[] items, int x, int y, int w, Panel p)
    {
        var c = new ComboBox
        {
            Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        if (items is { Length: > 0 }) c.Items.AddRange(items);
        c.Text = v;
        c.TextChanged += (_, _) => OnField(c);
        p.Controls.Add(c); Track(c, p); return c;
    }

    private NumericUpDown Num(int v, int min, int max, int x, int y, int w, Panel p)
    {
        var n = new NumericUpDown { Location = new Point(x, y), Width = w, Minimum = min, Maximum = max, Value = Clamp(v, min, max), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        n.ValueChanged += (_, _) => OnField(n);
        p.Controls.Add(n); Track(n, p); return n;
    }

    private CheckBox ChkBox(string text, int x, int y)
    {
        var cb = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        cb.CheckStateChanged += (_, _) => OnField(cb);
        _fields.Add(cb);   // tracked for the reddish "modified" text + save (no ↺ button — a checkbox is trivial to toggle back)
        return cb;
    }

    private Label InfoLabel(string text, int x, int y, Panel p)
    {
        var l = new Label { Text = text, Location = new Point(x, y + 2), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
        p.Controls.Add(l); return l;
    }

    private TextBox DateField(int x, int y, int w, Panel p)
    {
        var t = new TextBox { Location = new Point(x, y), Width = w - 28, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        t.TextChanged += (_, _) => OnField(t);
        var b = MiniBtn("▦", new Point(x + w - 26, y - 1), 26);
        b.Click += (_, _) => { if (!_readOnly) ShowDatePopup(b, t); };
        p.Controls.Add(t); Track(t, p); p.Controls.Add(b);
        return t;
    }

    private void ShowDatePopup(Control anchor, TextBox target)
    {
        var pop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var cal = new MonthCalendar { MaxSelectionCount = 1 };
        if (ParseDate(target.Text) is DateTime d) try { cal.SetDate(d); } catch { }
        cal.DateSelected += (_, e) => { target.Text = e.Start.ToString("d", CultureInfo.CurrentCulture); pop.Close(); };
        pop.Controls.Add(cal);
        try { pop.Location = anchor.PointToScreen(new Point(0, anchor.Height)); } catch { }
        pop.Deactivate += (_, _) => pop.Close();
        pop.Show(this);
    }

    private Button MiniBtn(string text, Point loc, int w) => new()
    {
        Text = text, Location = loc, Size = new Size(w, FieldH + 2),
        FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f),
    };

    private static Button FooterBtn(string text, Color back) => new()
    {
        Text = text, Size = new Size(92, 28),
        FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
    };

    private Button NavBtn(string text) => new()
    {
        Text = text, Size = new Size(40, 28), Top = 9,
        FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 10f),
    };

    private static void DisableInputs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TextBox or ComboBox or NumericUpDown or CheckBox) c.Enabled = false;
            else if (c is Button b && b.Text != "▦") { /* keep buttons visible but inert handled by _readOnly */ }
            if (c.HasChildren) DisableInputs(c);
        }
    }

    // ── value helpers ────────────────────────────────────────────────────
    private static string FmtDate(DateTime? d) => d.HasValue ? d.Value.ToString("d", CultureInfo.CurrentCulture) : "";
    private static string FmtDateTime(DateTime d) => d == default ? "" : d.ToString("g", CultureInfo.CurrentCulture);
    private static string FmtDuration(int seconds)
    {
        if (seconds <= 0) return "0h 00m 00s";
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s";
    }
    private static DateTime? ParseDate(string s)
        => DateTime.TryParse((s ?? "").Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) ? d.Date : (DateTime?)null;
    private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
