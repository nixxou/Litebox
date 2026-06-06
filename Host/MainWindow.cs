// The host GUI — a LaunchBox-like 3-pane layout (dark themed):
//   LEFT   : source tree (All Games / Platforms / Playlists, incl. auto-playlists).
//   CENTER : sortable, searchable game LIST (FastObjectListView, columns). Default
//            order = CompareName (normalized title); a Sort combo + direction toggle
//            and column-click let the user re-order. NOT thumbnails (toggle = later).
//   RIGHT  : details of the selected game — clear logo + box art + metadata + notes.
// Double-click / Enter launches. Right-click → Play / Play With (emulators) /
// Play Version (additional apps) / plugin game menus.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using BrightIdeasSoftware;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;

namespace LbApiHost.Host;

internal sealed class MainWindow : Form
{
    // ── Theme ────────────────────────────────────────────────────────────────
    private static readonly Color Bg      = Color.FromArgb(30, 30, 30);
    private static readonly Color Panel   = Color.FromArgb(37, 37, 38);
    private static readonly Color Panel2  = Color.FromArgb(45, 45, 48);
    private static readonly Color Row2    = Color.FromArgb(34, 34, 36);
    private static readonly Color Fg      = Color.FromArgb(222, 222, 222);
    private static readonly Color SubFg   = Color.FromArgb(150, 150, 152);
    private static readonly Color Accent  = Color.FromArgb(0, 122, 204);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

    private readonly PluginRegistry _reg;
    private readonly IDataManager _dm;

    private readonly TreeListView _sources;
    private readonly FastObjectListView _games;
    private readonly ToolStripTextBox _search;
    private readonly ToolStripComboBox _sortCombo;
    private readonly ToolStripButton _dirBtn;
    private readonly ToolStripLabel _count;

    // right-hand details
    private readonly PictureBox _logo;
    private readonly PictureBox _art;
    private readonly Label _title;
    private readonly Label _meta;
    private readonly TextBox _notes;

    private IGame[] _current = Array.Empty<IGame>();
    private object _currentNode;   // selected tree node (for the right pane when no game is selected)
    private object _detailsShown;  // current right-pane subject (IGame or tree node)
    private int _detailsLoadToken; // guards async image loads against stale selections
    private bool _ascending = true;
    private OLVColumn[] _sortColumns;   // parallel to SortLabels
    private bool _suppressSort;

    private readonly LiteBoxConfig _cfg;

    // "Game running" overlay + during-game unload state.
    private DoubleBufferedPanel _overlay;
    private Image _overlayImg;
    private string _overlayText = "";
    private string _resumeGameId;

    // Tree node icons (Nostalgic Platform Icons media pack + drawn fallbacks).
    private readonly ImageList _treeIcons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(22, 22) };
    private readonly Dictionary<object, string> _nodeIconKey = new();

    /// <summary>Marker for the synthetic "All Games" tree root.</summary>
    private sealed class AllNode { public static readonly AllNode Instance = new(); }

    // Sort options (default first = CompareName). Each maps to an OLV column so
    // sorting goes THROUGH ObjectListView (otherwise SetObjects re-sorts and
    // clobbers a manual sort). "Name" uses a hidden CompareName column.
    private static readonly string[] SortLabels =
        { "Name", "Title", "Platform", "Year", "Rating", "Plays", "Date Added", "Last Played" };

    public MainWindow(PluginRegistry reg, IDataManager dm)
    {
        _reg = reg; _dm = dm;
        _cfg = LiteBoxConfig.LoadForExe();
        Text = "LiteBox";
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9f);

        _games = BuildGameList();

        _sources = BuildSourceTree();

        var details = BuildDetails(out _logo, out _art, out _title, out _meta, out _notes);

        var inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Bg, SplitterWidth = 4 };
        inner.Panel1.Controls.Add(_games);
        inner.Panel2.Controls.Add(details);

        var outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Bg, SplitterWidth = 4 };
        outer.Panel1.Controls.Add(_sources);
        outer.Panel2.Controls.Add(inner);
        Controls.Add(outer);

        // ── Top toolbar ──────────────────────────────────────────────────────
        var bar = new ToolStrip
        {
            Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Panel2, ForeColor = Fg, Renderer = new DarkRenderer(),
            Padding = new Padding(6, 2, 6, 2), ImageScalingSize = new Size(16, 16),
        };
        bar.Items.Add(new ToolStripLabel("Search:") { ForeColor = SubFg });
        _search = new ToolStripTextBox
        {
            AutoSize = false, Width = 240, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Panel, ForeColor = Fg,
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        bar.Items.Add(_search);
        bar.Items.Add(new ToolStripSeparator());

        bar.Items.Add(new ToolStripLabel("Sort:") { ForeColor = SubFg });
        _sortCombo = new ToolStripComboBox
        {
            AutoSize = false, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Panel, ForeColor = Fg,
        };
        foreach (var d in SortLabels) _sortCombo.Items.Add(d);
        _sortCombo.SelectedIndexChanged += (_, _) => { if (!_suppressSort) ApplySort(); };
        bar.Items.Add(_sortCombo);
        _dirBtn = new ToolStripButton("▲") { ForeColor = Fg, ToolTipText = "Ascending / Descending" };
        _dirBtn.Click += (_, _) => { _ascending = !_ascending; _dirBtn.Text = _ascending ? "▲" : "▼"; ApplySort(); };
        bar.Items.Add(_dirBtn);
        bar.Items.Add(new ToolStripSeparator());

        bar.Items.Add(new ToolStripButton("Thumbnails (soon)") { Enabled = false, ForeColor = SubFg });

        // Options (gear) — right aligned.
        var optBtn = new ToolStripDropDownButton("⚙")
        {
            ForeColor = Fg, ToolTipText = "Options", Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text, ShowDropDownArrow = false,
            Font = new Font("Segoe UI Symbol", 11f),
        };
        ((ToolStripDropDownMenu)optBtn.DropDown).Renderer = new DarkRenderer();
        optBtn.DropDown.BackColor = Panel2;
        optBtn.DropDown.ForeColor = Fg;
        var miScreen = new ToolStripMenuItem("Show \"game running\" screen on launch")
        { CheckOnClick = true, Checked = _cfg.ShowGameRunningScreen };
        miScreen.CheckedChanged += (_, _) => { _cfg.ShowGameRunningScreen = miScreen.Checked; _cfg.Save(); };
        var miUnload = new ToolStripMenuItem("Unload the list while a game runs")
        { CheckOnClick = true, Checked = _cfg.UnloadListDuringGame };
        miUnload.CheckedChanged += (_, _) => { _cfg.UnloadListDuringGame = miUnload.Checked; _cfg.Save(); };
        optBtn.DropDownItems.Add(miScreen);
        optBtn.DropDownItems.Add(miUnload);
        bar.Items.Add(optBtn);

        _count = new ToolStripLabel("") { ForeColor = SubFg, Alignment = ToolStripItemAlignment.Right };
        bar.Items.Add(_count);

        // React to game launch start/end (for the running screen + during-game unload).
        HostLaunch.GameStarted += OnGameStarted;
        HostLaunch.GameEnded += OnGameEnded;
        FormClosed += (_, _) =>
        {
            HostLaunch.GameStarted -= OnGameStarted;
            HostLaunch.GameEnded -= OnGameEnded;
            HostStateManager.SelectedGamesProvider = null;
        };

        // Expose the current selection to plugins via IStateManager (UI-thread safe).
        HostStateManager.SelectedGamesProvider = () =>
        {
            try
            {
                if (IsDisposed) return Array.Empty<IGame>();
                if (InvokeRequired) return (IGame[])Invoke((Func<IGame[]>)(() => _games.SelectedObjects.OfType<IGame>().ToArray()));
                return _games.SelectedObjects.OfType<IGame>().ToArray();
            }
            catch { return Array.Empty<IGame>(); }
        };

        // ── Top menu: system-menu plugins ────────────────────────────────────
        var menu = new MenuStrip { Dock = DockStyle.Top, BackColor = Panel2, ForeColor = Fg, Renderer = new DarkRenderer() };
        var pluginsMenu = new ToolStripMenuItem("Plugins");
        foreach (var m in _reg.SystemMenus)
        {
            string cap; bool show;
            try { cap = m.Caption ?? m.GetType().Name; show = m.ShowInLaunchBox; }
            catch { cap = m.GetType().Name; show = true; }
            if (!show) continue;
            var captured = m;
            var it = new ToolStripMenuItem(cap);
            it.Click += (_, _) => Safe(() => captured.OnSelected());
            pluginsMenu.DropDownItems.Add(it);
        }
        if (pluginsMenu.DropDownItems.Count == 0)
            pluginsMenu.DropDownItems.Add(new ToolStripMenuItem("(no plugin menu items)") { Enabled = false });
        menu.Items.Add(pluginsMenu);

        Controls.Add(bar);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _sortCombo.SelectedIndex = 0; // default = CompareName

        // Dark native scrollbars (Win10/11 explorer dark theme).
        DarkScroll(_games);
        DarkScroll(_sources);
        DarkScroll(_notes);

        Load += (_, _) =>
        {
            try { outer.SplitterDistance = 240; } catch { }
            try { inner.SplitterDistance = Math.Max(300, inner.Width - 380); } catch { }
            PopulateSources();
            try { ActiveControl = _games; _games.Focus(); } catch { }
        };
    }

    // ── Game list construction ───────────────────────────────────────────────
    private FastObjectListView BuildGameList()
    {
        var olv = new FastObjectListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Clickable, BackColor = Panel, ForeColor = Fg,
            BorderStyle = BorderStyle.None, RowHeight = 22, UseAlternatingBackColors = true,
            AlternateRowBackColor = Row2, GridLines = false, ShowGroups = false, UseFiltering = true,
        };
        olv.SelectedBackColor = Accent;
        olv.SelectedForeColor = Color.White;
        olv.HeaderFormatStyle = new HeaderFormatStyle();
        olv.HeaderFormatStyle.SetBackColor(Panel2);
        olv.HeaderFormatStyle.SetForeColor(Fg);

        OLVColumn Col(string title, int w, AspectGetterDelegate get, HorizontalAlignment align = HorizontalAlignment.Left)
            => new OLVColumn(title, null) { Width = w, AspectGetter = get, TextAlign = align, Sortable = true, Searchable = true };

        // Hidden sort-only columns (CompareName / dates) — usable as PrimarySortColumn.
        var cName = Col("Name", 0, r => CompareName((IGame)r)); cName.IsVisible = false; cName.Searchable = false;
        var cDateAdded = Col("Added", 0, r => Safe(() => ((IGame)r).DateAdded)); cDateAdded.IsVisible = false;
        var cLastPlayed = Col("Last", 0, r => Safe(() => (object)((IGame)r).LastPlayedDate)); cLastPlayed.IsVisible = false;

        var cTitle = Col("Title", 360, r => S(((IGame)r).Title));
        var cPlat  = Col("Platform", 150, r => S(((IGame)r).Platform));
        var cDev   = Col("Developer", 150, r => S(((IGame)r).Developer));
        var cGenre = Col("Genre", 140, r => S(((IGame)r).GenresString));
        var cYear  = Col("Year", 55, r => N(() => ((IGame)r).ReleaseYear), HorizontalAlignment.Right);
        var cRate  = Col("★", 55, r => N(() => (double?)((IGame)r).StarRatingFloat), HorizontalAlignment.Right);
        cRate.AspectToStringConverter = v => v is double d && d > 0 ? d.ToString("0.#") : "";
        var cFav   = Col("Fav", 45, r => Safe(() => ((IGame)r).Favorite), HorizontalAlignment.Center);
        cFav.AspectToStringConverter = v => v is bool b && b ? "★" : "";
        var cPlays = Col("Plays", 55, r => N(() => (int?)((IGame)r).PlayCount), HorizontalAlignment.Right);

        // AllColumns holds every column; RebuildColumns shows only the IsVisible ones.
        olv.AllColumns.AddRange(new[] { cName, cTitle, cPlat, cDev, cGenre, cYear, cRate, cFav, cPlays, cDateAdded, cLastPlayed });
        olv.RebuildColumns();

        // Parallel to SortLabels: Name, Title, Platform, Year, Rating, Plays, Date Added, Last Played.
        _sortColumns = new[] { cName, cTitle, cPlat, cYear, cRate, cPlays, cDateAdded, cLastPlayed };

        olv.SelectionChanged += (_, _) => OnGameSelectionChanged();
        olv.ItemActivate += (_, _) => LaunchSelected();
        olv.CellRightClick += OnCellRightClick;
        olv.AfterSorting += (_, e) =>
        {
            if (_sortColumns == null) return;
            _suppressSort = true;
            try
            {
                int i = Array.IndexOf(_sortColumns, e.ColumnToSort);
                if (i >= 0 && _sortCombo.SelectedIndex != i) _sortCombo.SelectedIndex = i;
                if (e.SortOrder != SortOrder.None) { _ascending = e.SortOrder == SortOrder.Ascending; _dirBtn.Text = _ascending ? "▲" : "▼"; }
            }
            finally { _suppressSort = false; }
        };
        return olv;
    }

    // ── Right details construction ───────────────────────────────────────────
    private Panel BuildDetails(out PictureBox logo, out PictureBox art, out Label title, out Label meta, out TextBox notes)
    {
        var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Panel, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        logo = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Panel };
        art = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Panel };
        title = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = 28, ForeColor = Fg, Font = new Font("Segoe UI Semibold", 12f), TextAlign = ContentAlignment.MiddleLeft };
        meta = new Label { Dock = DockStyle.Fill, AutoSize = false, ForeColor = SubFg, TextAlign = ContentAlignment.TopLeft };
        notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Panel2, ForeColor = Fg };

        tlp.Controls.Add(logo, 0, 0);
        tlp.Controls.Add(art, 0, 1);
        tlp.Controls.Add(title, 0, 2);
        tlp.Controls.Add(meta, 0, 3);
        tlp.Controls.Add(notes, 0, 4);
        return tlp;
    }

    // ── Sources (LaunchBox-native tree: categories ▸ platforms / playlists) ───
    private TreeListView BuildSourceTree()
    {
        var tlv = new TreeListView
        {
            Dock = DockStyle.Fill, BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, RowHeight = 26,
            ShowGroups = false, UseFiltering = false,
        };
        tlv.SelectedBackColor = Accent;
        tlv.SelectedForeColor = Color.White;
        var col = new OLVColumn("", null)
        {
            FillsFreeSpace = true,
            AspectGetter = x => x is AllNode ? "All Games" : HostPlatformCategory.NodeName(x),
            ImageGetter = x => _nodeIconKey.TryGetValue(x, out var k) ? (object)k : "fb_plat",
        };
        tlv.Columns.Add(col);
        tlv.SmallImageList = _treeIcons;
        tlv.CanExpandGetter = x => x is HostPlatformCategory c && c.Children.Count > 0;
        tlv.ChildrenGetter = x => (x is HostPlatformCategory c)
            ? (System.Collections.IEnumerable)c.Children : Array.Empty<object>();
        tlv.TreeColumnRenderer = new ChevronTreeRenderer();   // LaunchBox-style rotating chevron
        tlv.SelectionChanged += (_, _) => LoadNode(tlv.SelectedObject);
        return tlv;
    }

    private void PopulateSources()
    {
        var roots = new List<object> { AllNode.Instance };
        if (_dm is HostDataManagerXml hostDm) roots.AddRange(hostDm.RootNodes);
        else { try { roots.AddRange(_dm.GetAllPlatforms()); } catch { } }

        BuildTreeIcons(roots);
        _sources.Roots = roots;
        try { _sources.ExpandAll(); } catch { }
        _sources.SelectedObject = AllNode.Instance;   // → LoadNode(All)
    }

    // ── Tree icons (Nostalgic Platform Icons pack + drawn fallbacks) ─────────
    private void BuildTreeIcons(IEnumerable<object> roots)
    {
        _treeIcons.Images.Clear();
        _nodeIconKey.Clear();
        _treeIcons.Images.Add("fb_cat", GlyphCategory());
        _treeIcons.Images.Add("fb_play", GlyphPlaylist());
        _treeIcons.Images.Add("fb_plat", GlyphPlatform());

        string imagesRoot = MediaResolver.ImagesRoot;
        int counter = 0;
        void Walk(object node)
        {
            if (node == null || _nodeIconKey.ContainsKey(node)) return;
            _nodeIconKey[node] = ResolveIcon(node, imagesRoot, ref counter);
            if (node is HostPlatformCategory cat) foreach (var c in cat.Children) Walk(c);
        }
        foreach (var r in roots) Walk(r);
    }

    private string ResolveIcon(object node, string imagesRoot, ref int counter)
    {
        string sub, name, fallback;
        if (node is AllNode) { sub = "Playlists"; name = "All Games"; fallback = "fb_play"; }
        else if (node is IPlatformCategory c) { sub = "Platform Categories"; name = c.Name; fallback = "fb_cat"; }
        else if (node is IPlaylist pl) { sub = "Playlists"; name = pl.Name; fallback = "fb_play"; }
        else if (node is IPlatform p) { sub = "Platforms"; name = p.Name; fallback = "fb_plat"; }
        else return "fb_plat";

        string path = MediaResolver.PlatformIcon(imagesRoot, sub, name);
        var img = path == null ? null : LoadScaled(path, 22);
        if (img == null) return fallback;
        string key = "n" + counter++;
        _treeIcons.Images.Add(key, img);
        return key;
    }

    private static Image LoadScaled(string path, int size)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var src = Image.FromStream(ms);
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float ratio = Math.Min((float)size / src.Width, (float)size / src.Height);
            int w = Math.Max(1, (int)(src.Width * ratio)), h = Math.Max(1, (int)(src.Height * ratio));
            g.DrawImage(src, (size - w) / 2, (size - h) / 2, w, h);
            return bmp;
        }
        catch { return null; }
    }

    private static Image GlyphCategory()
    {
        var bmp = new Bitmap(22, 22);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Color.FromArgb(150, 150, 152));
        g.FillRectangle(b, 3, 8, 16, 9);          // body
        g.FillRectangle(b, 3, 6, 7, 3);           // tab
        return bmp;
    }
    private static Image GlyphPlaylist()
    {
        var bmp = new Bitmap(22, 22);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var p = new Pen(Color.FromArgb(150, 150, 152), 2f);
        g.DrawLine(p, 4, 7, 18, 7);
        g.DrawLine(p, 4, 11, 18, 11);
        g.DrawLine(p, 4, 15, 13, 15);
        return bmp;
    }
    private static Image GlyphPlatform()
    {
        var bmp = new Bitmap(22, 22);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var p = new Pen(Color.FromArgb(150, 150, 152), 2f);
        g.DrawRectangle(p, 4, 5, 14, 9);          // screen
        g.DrawLine(p, 8, 18, 14, 18);             // stand
        g.DrawLine(p, 11, 14, 11, 18);
        return bmp;
    }

    // OLV coalesces SelectionChanged (fires ~½s after SetObjects), so a node
    // click would otherwise clear the pane just after ShowNodeDetails filled it.
    // When nothing is selected in the list, keep showing the current node.
    private void OnGameSelectionChanged()
    {
        if (_games.SelectedObject is IGame g) ShowDetails(g);
        else if (!ReferenceEquals(_detailsShown, _currentNode)) ShowNodeDetails(_currentNode);
    }

    private void LoadNode(object node)
    {
        if (node == null) return;
        _currentNode = node;
        try
        {
            IEnumerable<IGame> src =
                  node is AllNode ? _dm.GetAllGames()
                : node is IPlatformCategory cat ? cat.GetAllGames(true, true)
                : node is IPlaylist pl ? pl.GetAllGames(true)
                : node is IPlatform p ? p.GetAllGames(true, true)
                : Array.Empty<IGame>();
            _current = (src ?? Array.Empty<IGame>()).ToArray();
        }
        catch { _current = Array.Empty<IGame>(); }

        ApplySort();              // fills the centre list (no game auto-selected)
        ShowNodeDetails(node);    // node info on the right
    }

    // ── Sort + filter ────────────────────────────────────────────────────────
    private void ApplySort()
    {
        if (_games == null || _sortColumns == null) return;
        int idx = _sortCombo != null && _sortCombo.SelectedIndex >= 0 ? _sortCombo.SelectedIndex : 0;
        var col = _sortColumns[Math.Min(idx, _sortColumns.Length - 1)];
        var order = _ascending ? SortOrder.Ascending : SortOrder.Descending;

        _games.PrimarySortColumn = col;
        _games.PrimarySortOrder = order;
        _games.SetObjects(_current);   // sorted through OLV by the chosen column's aspect
        _games.Sort(col, order);
        ApplyFilter();
        // No game auto-selected: the right pane shows the selected tree node's info
        // until the user clicks a game (then ShowDetails takes over).
    }

    private void ApplyFilter()
    {
        string txt = _search?.Text;
        _games.ModelFilter = string.IsNullOrWhiteSpace(txt)
            ? null
            : new ModelFilter(o =>
            {
                var g = (IGame)o;
                return Contains(S(g.Title), txt) || Contains(S(g.Platform), txt) || Contains(S(g.Developer), txt);
            });
        if (_count != null) _count.Text = $"{_games.GetItemCount()} / {_current.Length} games";
    }

    // ── Details rendering ────────────────────────────────────────────────────
    private void ShowDetails(IGame g)
    {
        _detailsShown = g;
        string logoPath = g == null ? null : Safe(() => g.ClearLogoImagePath);
        string artPath = g == null ? null
            : (Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f
             : Safe(() => g.Box3DImagePath) is { Length: > 0 } b ? b
             : Safe(() => g.ScreenshotImagePath));
        LoadImagesAsync(logoPath, artPath);

        if (g == null) { _title.Text = ""; _meta.Text = ""; _notes.Text = ""; return; }

        _title.Text = S(g.Title);

        var bits = new List<string>();
        void Add(string label, string val) { if (!string.IsNullOrWhiteSpace(val)) bits.Add($"{label}: {val}"); }
        Add("Platform", S(g.Platform));
        Add("Developer", S(g.Developer));
        Add("Publisher", S(g.Publisher));
        Add("Genre", S(g.GenresString));
        Add("Released", N(() => g.ReleaseYear)?.ToString());
        Add("Players", S(g.PlayMode));
        var rating = Safe(() => g.StarRatingFloat);
        if (rating > 0) Add("Rating", rating.ToString("0.#") + " ★");
        Add("Plays", Safe(() => g.PlayCount).ToString());
        var versions = Safe(() => g.GetAllAdditionalApplications()?.Length);
        if (versions > 0) Add("Versions", versions.ToString());
        _meta.Text = string.Join("    •    ", bits);

        _notes.Text = S(g.Notes).Replace("\n", "\r\n");
    }

    // Right pane when a TREE node (category / platform / playlist / All) is selected.
    private void ShowNodeDetails(object node)
    {
        _detailsShown = node;
        if (node == null || node is AllNode)
        {
            LoadImagesAsync(null, null);
            _title.Text = node is AllNode ? "All Games" : "";
            _meta.Text = node is AllNode ? $"Total Games: {_current.Length}" : "";
            _notes.Text = "";
            return;
        }

        LoadImagesAsync(NodeImage(node, clearLogo: true), NodeImage(node, clearLogo: false));
        _title.Text = HostPlatformCategory.NodeName(node);

        var bits = new List<string> { $"Total Games: {_current.Length}" };
        if (node is IPlatform p)
        {
            void Add(string l, string v) { if (!string.IsNullOrWhiteSpace(v)) bits.Add($"{l}: {v}"); }
            Add("Developer", Safe(() => p.Developer));
            Add("Manufacturer", Safe(() => p.Manufacturer));
            Add("Release", N(() => p.ReleaseDate?.Year)?.ToString());
        }
        _meta.Text = string.Join("    •    ", bits);
        _notes.Text = NodeNotes(node).Replace("\n", "\r\n");
    }

    private string NodeImage(object node, bool clearLogo)
    {
        try
        {
            if (node is IPlatform p)
                return clearLogo ? p.ClearLogoImagePath
                     : (NonEmpty(p.BannerImagePath) ?? NonEmpty(p.BackgroundImagePath) ?? p.DefaultBoxImagePath);
            if (node is IPlatformCategory c)
                return clearLogo ? c.ClearLogoImagePath : (NonEmpty(c.BannerImagePath) ?? c.BackgroundImagePath);
            if (node is IPlaylist pl)
                return clearLogo ? pl.ClearLogoImagePath
                     : (NonEmpty(pl.BannerImagePath) ?? NonEmpty(pl.BackgroundImagePath) ?? pl.DefaultBoxImagePath);
        }
        catch { }
        return null;
    }

    private static string NodeNotes(object node)
    {
        try
        {
            if (node is IPlatform p) return S(p.Notes);
            if (node is IPlatformCategory c) return S(c.Notes);
            if (node is IPlaylist pl) return S(pl.Notes);
        }
        catch { }
        return "";
    }

    private static string NonEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    // Decode the right-pane images OFF the UI thread so selecting a tree node
    // (or a game) never blocks the game-list paint — only the cheap text is set
    // synchronously. The token discards a stale decode if the selection changed
    // before it finished.
    private void LoadImagesAsync(string logoPath, string artPath)
    {
        int token = ++_detailsLoadToken;
        SetImage(_logo, null);   // drop the previous subject's art right away
        SetImage(_art, null);
        if (string.IsNullOrEmpty(logoPath) && string.IsNullOrEmpty(artPath)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var logo = LoadImage(logoPath);
            var art = LoadImage(artPath);
            void Apply()
            {
                if (IsDisposed || token != _detailsLoadToken) { logo?.Dispose(); art?.Dispose(); return; }
                var ol = _logo.Image; _logo.Image = logo; ol?.Dispose();
                var oa = _art.Image; _art.Image = art; oa?.Dispose();
            }
            try { if (!IsDisposed) BeginInvoke((Action)Apply); else { logo?.Dispose(); art?.Dispose(); } }
            catch { logo?.Dispose(); art?.Dispose(); }
        });
    }

    private static void SetImage(PictureBox pb, string path)
    {
        var old = pb.Image;
        pb.Image = LoadImage(path);
        old?.Dispose();
    }

    private static Image LoadImage(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var tmp = Image.FromStream(ms);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }

    // ── Game-running screen + during-game list unload ────────────────────────
    private void OnGameStarted(IGame g)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => OnGameStarted(g))); } catch { } return; }

        _resumeGameId = g != null ? Safe(() => g.Id) : null;

        if (_cfg.UnloadListDuringGame)
        {
            LoadImagesAsync(null, null);             // clears + invalidates any in-flight decode
            _games.SetObjects(Array.Empty<IGame>()); // free the OLV row index during the game
        }
        if (_cfg.ShowGameRunningScreen) ShowRunningOverlay(g);
    }

    private void OnGameEnded(IGame g)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => OnGameEnded(g))); } catch { } return; }

        HideRunningOverlay();

        if (_cfg.UnloadListDuringGame)
        {
            _games.SetObjects(_current);   // data already reloaded by HostLaunch
            ApplyFilter();
            IGame target = _resumeGameId == null ? null
                : _current.FirstOrDefault(x => string.Equals(Safe(() => x.Id), _resumeGameId, StringComparison.OrdinalIgnoreCase));
            if (target != null) { _games.SelectObject(target, true); _games.EnsureModelVisible(target); ShowDetails(target); }
            else if (_games.GetItemCount() > 0) { _games.SelectedIndex = 0; }
        }
    }

    private void ShowRunningOverlay(IGame g)
    {
        if (_overlay == null)
        {
            _overlay = new DoubleBufferedPanel { Dock = DockStyle.Fill };
            _overlay.Paint += PaintOverlay;
            Controls.Add(_overlay);
        }
        _overlayImg?.Dispose();
        string fan = g == null ? null
            : (Safe(() => g.BackgroundImagePath) is { Length: > 0 } bg ? bg : Safe(() => g.FrontImagePath));
        _overlayImg = LoadImage(fan);
        _overlayText = (_cfg.GameRunningText ?? "") + (g != null ? "\n\n" + S(Safe(() => g.Title)) : "");
        _overlay.Visible = true;
        _overlay.BringToFront();
        _overlay.Invalidate();
    }

    private void HideRunningOverlay()
    {
        if (_overlay != null) _overlay.Visible = false;
        if (_overlayImg != null) { _overlayImg.Dispose(); _overlayImg = null; }
    }

    private void PaintOverlay(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = _overlay.ClientRectangle;
        using (var b = new SolidBrush(_cfg.GameRunningColor)) g.FillRectangle(b, rect);
        if (_overlayImg != null) DrawCover(g, _overlayImg, rect);
        using (var scrim = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(scrim, rect);
        using (var f = new Font("Segoe UI Semibold", 22f))
        using (var tb = new SolidBrush(Color.White))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(_overlayText, f, tb, rect, sf);
    }

    private static void DrawCover(Graphics g, Image img, Rectangle rect)
    {
        if (img == null || rect.Width <= 0 || rect.Height <= 0) return;
        float ir = (float)img.Width / img.Height, rr = (float)rect.Width / rect.Height;
        int w, h;
        if (ir > rr) { h = rect.Height; w = (int)(h * ir); } else { w = rect.Width; h = (int)(w / ir); }
        int x = rect.X + (rect.Width - w) / 2, y = rect.Y + (rect.Height - h) / 2;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, x, y, w, h);
    }

    // ── Launch + context menu ────────────────────────────────────────────────
    private void LaunchSelected()
    {
        if (_games.SelectedObject is not IGame g) return;
        var emu = Safe(() => _dm.GetEmulatorById(g.EmulatorId));
        Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, emu, null));
    }

    private void OnCellRightClick(object sender, CellRightClickEventArgs e)
    {
        if (e.Model is IGame clicked && !_games.IsSelected(clicked))
            _games.SelectObject(clicked, true);
        var games = _games.SelectedObjects.OfType<IGame>().ToArray();
        if (games.Length == 0) return;
        var menu = BuildGameContextMenu(games);
        if (menu.Items.Count > 0) e.MenuStrip = menu;
    }

    private ContextMenuStrip BuildGameContextMenu(IGame[] games)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Panel2, ForeColor = Fg };

        var play = new ToolStripMenuItem("Play") { Font = new Font(Font, FontStyle.Bold) };
        play.Click += (_, _) => LaunchSelected();
        menu.Items.Add(play);

        // Play With… / Play Version… apply to a single selected game.
        if (games.Length == 1)
        {
            var g = games[0];

            var emus = SafeEmulatorsForPlatform(S(g.Platform));
            if (emus.Count > 0)
            {
                var pw = new ToolStripMenuItem("Play With");
                foreach (var e in emus)
                {
                    var ce = e;
                    var it = new ToolStripMenuItem(S(Safe(() => e.Title)));
                    it.Click += (_, _) => Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, ce, null));
                    pw.DropDownItems.Add(it);
                }
                menu.Items.Add(pw);
            }

            var apps = SafeAddApps(g);
            if (apps.Length > 0)
            {
                var pv = new ToolStripMenuItem("Play Version");
                foreach (var a in apps)
                {
                    var ca = a;
                    string cap = S(Safe(() => a.Name));
                    var it = new ToolStripMenuItem(cap.Length > 0 ? cap : "(version)");
                    it.Click += (_, _) => Safe(() =>
                    {
                        string emuId = !string.IsNullOrEmpty(Safe(() => ca.EmulatorId)) ? ca.EmulatorId : g.EmulatorId;
                        var emu = _dm.GetEmulatorById(emuId);
                        PluginHelper.LaunchBoxMainViewModel.PlayGame(g, ca, emu, null);
                    });
                    pv.DropDownItems.Add(it);
                }
                menu.Items.Add(pv);
            }

            // Configure (only if the game has a Configuration Application Path) —
            // works for emulated, DOSBox and plain PC games (Configure() is DOSBox-aware).
            if (!string.IsNullOrEmpty(S(Safe(() => g.ConfigurationPath))))
            {
                var cfg = new ToolStripMenuItem("Configure");
                cfg.Click += (_, _) => Safe(() => g.Configure());
                menu.Items.Add(cfg);
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        foreach (var gm in _reg.GameMenus)
        {
            bool valid, show; string cap;
            try
            {
                cap = gm.Caption;
                show = gm.ShowInLaunchBox;
                valid = games.Length == 1 ? gm.GetIsValidForGame(games[0])
                                          : (gm.SupportsMultipleGames && gm.GetIsValidForGames(games));
            }
            catch { continue; }
            if (!show || !valid) continue;

            var captured = gm; var gs = games;
            var it = new ToolStripMenuItem(cap);
            it.Click += (_, _) => Safe(() =>
            {
                if (gs.Length == 1) captured.OnSelected(gs[0]); else captured.OnSelected(gs);
            });
            menu.Items.Add(it);
        }

        foreach (var gmm in _reg.GameMultiMenus)
        {
            IEnumerable<IGameMenuItem> items;
            try { items = gmm.GetMenuItems(games); } catch { continue; }
            if (items == null) continue;
            foreach (var mi in items) menu.Items.Add(BuildGameMenuItem(mi, games));
        }
        return menu;
    }

    private ToolStripMenuItem BuildGameMenuItem(IGameMenuItem mi, IGame[] games)
    {
        string cap; bool enabled;
        try { cap = mi.Caption; enabled = mi.Enabled; } catch { cap = "?"; enabled = false; }
        var item = new ToolStripMenuItem(cap) { Enabled = enabled };

        IEnumerable<IGameMenuItem> children = null;
        try { children = mi.Children; } catch { }
        if (children != null)
            foreach (var c in children) item.DropDownItems.Add(BuildGameMenuItem(c, games));
        else
        {
            var captured = mi; var g = games;
            item.Click += (_, _) => Safe(() => captured.OnSelect(g));
        }
        return item;
    }

    // ── Safe wrappers / helpers ──────────────────────────────────────────────
    private IEnumerable<string> SafePlatforms()
    {
        try { return _dm.GetAllPlatforms().Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)); }
        catch { return Array.Empty<string>(); }
    }

    private List<IPlaylist> SafePlaylists()
    {
        try { return _dm.GetAllPlaylists()?.Where(p => p != null).ToList() ?? new List<IPlaylist>(); }
        catch { return new List<IPlaylist>(); }
    }

    private List<IEmulator> SafeEmulatorsForPlatform(string platform)
    {
        try
        {
            var all = _dm.GetAllEmulators() ?? Array.Empty<IEmulator>();
            var match = all.Where(e =>
            {
                try { return e.GetAllEmulatorPlatforms()?.Any(ep => string.Equals(ep.Platform, platform, StringComparison.OrdinalIgnoreCase)) == true; }
                catch { return false; }
            }).ToList();
            return match.Count > 0 ? match : all.ToList(); // fall back to all emulators
        }
        catch { return new List<IEmulator>(); }
    }

    private static IAdditionalApplication[] SafeAddApps(IGame g)
    {
        try { return g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { return Array.Empty<IAdditionalApplication>(); }
    }

    /// <summary>Normalized comparison name (default sort): SortTitle|Title, lower, no leading article, alnum only.</summary>
    private static string CompareName(IGame g)
    {
        string s = S(Safe(() => g.SortTitle));
        if (s.Length == 0) s = S(Safe(() => g.Title));
        s = s.ToLowerInvariant().Trim();
        foreach (var art in new[] { "the ", "a ", "an " })
            if (s.StartsWith(art, StringComparison.Ordinal)) { s = s.Substring(art.Length); break; }
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool Contains(string hay, string needle)
        => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string S(string s) => s ?? "";
    private static object N(Func<int?> f) { try { return f(); } catch { return null; } }
    private static object N(Func<double?> f) { try { return f(); } catch { return null; } }

    private static void DarkScroll(Control c)
    {
        void Apply() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Apply(); else c.HandleCreated += (_, _) => Apply();
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { MessageBox.Show(this, ex.ToString(), "Plugin error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

    // ── LaunchBox-style expansion chevron (▶ collapsed → ▼ expanded) ─────────
    // Replaces ObjectListView's +/- box. The right→down flip reads as the
    // small "rotation" LaunchBox shows when a category opens. Lines are off.
    private sealed class ChevronTreeRenderer : TreeListView.TreeRenderer
    {
        private static readonly Color GlyphColor = Color.FromArgb(180, 180, 182);

        public ChevronTreeRenderer() { IsShowLines = false; }

        protected override void DrawExpansionGlyph(Graphics g, Rectangle r, bool isExpanded)
        {
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int cx = r.Left + 9;                 // matches OLV's left-aligned glyph slot
            int cy = r.Top + r.Height / 2;
            const int s = 4;                     // chevron arm reach
            using var pen = new Pen(GlyphColor, 1.8f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            Point[] pts = isExpanded
                ? new[] { new Point(cx - s, cy - s / 2), new Point(cx, cy + s / 2), new Point(cx + s, cy - s / 2) } // ▼
                : new[] { new Point(cx - s / 2, cy - s), new Point(cx + s / 2, cy), new Point(cx - s / 2, cy + s) }; // ▶
            g.DrawLines(pen, pts);
            g.SmoothingMode = oldMode;
        }
    }

    // ── Double-buffered panel for the flicker-free overlay ───────────────────
    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
    }

    // ── Dark renderer for menus / toolbars ───────────────────────────────────
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? Fg : SubFg; base.OnRenderItemText(e); }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Accent;
        public override Color MenuItemSelectedGradientBegin => Accent;
        public override Color MenuItemSelectedGradientEnd => Accent;
        public override Color MenuItemBorder => Accent;
        public override Color MenuBorder => Panel2;
        public override Color ToolStripDropDownBackground => Panel2;
        public override Color ImageMarginGradientBegin => Panel2;
        public override Color ImageMarginGradientMiddle => Panel2;
        public override Color ImageMarginGradientEnd => Panel2;
        public override Color MenuStripGradientBegin => Panel2;
        public override Color MenuStripGradientEnd => Panel2;
        public override Color ToolStripGradientBegin => Panel2;
        public override Color ToolStripGradientMiddle => Panel2;
        public override Color ToolStripGradientEnd => Panel2;
        public override Color SeparatorDark => Color.FromArgb(60, 60, 62);
    }
}
