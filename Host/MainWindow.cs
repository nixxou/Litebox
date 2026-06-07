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
    private static readonly Color UserRating = Color.FromArgb(255, 196, 0);   // amber: user-set rating
    private static readonly Color CommRating = Color.FromArgb(150, 150, 152); // grey: community rating

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

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
    private List<object> _treeRoots;   // tree roots (incl. AllNode) — for key lookup on restore
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
        { "Name", "Title", "Platform", "Developer", "Year", "Rating", "Plays", "Date Added", "Date Modified", "Last Played" };

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
        RestoreWindowState();   // size / position / maximized from the INI (overrides the defaults)

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

        // Persist layout / window / selection once, at close (not per change).
        FormClosing += (_, _) => { try { SaveAll(); } catch { } };

        // Bring the window back on-screen if a monitor is unplugged while running.
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }

        // React to game launch start/end (for the running screen + during-game unload).
        HostLaunch.GameStarted += OnGameStarted;
        HostLaunch.GameEnded += OnGameEnded;
        FormClosed += (_, _) =>
        {
            HostLaunch.GameStarted -= OnGameStarted;
            HostLaunch.GameEnded -= OnGameEnded;
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
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
            RestoreColumnLayout();   // order / width / shown-hidden from the INI
            RestoreSort();           // last sort column + direction
            PopulateSources();       // build the tree
            RestoreSelection();      // last category + game
            try { ActiveControl = _games; _games.Focus(); } catch { }
        };
        // Final dark-scrollbar pass once everything (data, columns) is in place.
        Shown += (_, _) => { ApplyDarkScroll(_games); ApplyDarkScroll(_sources); ApplyDarkScroll(_notes); };
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
            AllowColumnReorder = true,              // drag to reorder (persisted on close)
            SelectColumnsOnRightClick = true,       // right-click header → show/hide columns
            UseCellFormatEvents = true,             // per-cell colouring (user vs community rating)
        };
        olv.UseExplorerTheme = false;   // stop OLV forcing the light "explorer" scrollbars
        olv.SelectedBackColor = Accent;
        olv.SelectedForeColor = Color.White;
        olv.HeaderFormatStyle = new HeaderFormatStyle();
        olv.HeaderFormatStyle.SetBackColor(Panel2);
        olv.HeaderFormatStyle.SetForeColor(Fg);

        // key = stable INI identity (.Tag); never localise it.
        OLVColumn Col(string key, string title, int w, AspectGetterDelegate get,
                      HorizontalAlignment align = HorizontalAlignment.Left, bool visible = true)
            => new OLVColumn(title, null) { Tag = key, Width = w, AspectGetter = get, TextAlign = align,
                                            Sortable = true, Searchable = true, IsVisible = visible };

        static string Check(object v) => v is bool b && b ? "✓" : "";

        // Hidden sort-only key column (normalized title) — the default sort.
        var cName = Col("name", "Name", 0, r => CompareName((IGame)r), visible: false); cName.Searchable = false;

        var cTitle = Col("title", "Title", 320, r => S(((IGame)r).Title));
        var cPlat  = Col("platform", "Platform", 150, r => S(((IGame)r).Platform));
        var cDev   = Col("developer", "Developer", 150, r => S(((IGame)r).Developer));
        var cPub   = Col("publisher", "Publisher", 150, r => S(((IGame)r).Publisher), visible: false);
        var cGenre = Col("genre", "Genre", 140, r => S(((IGame)r).GenresString));
        var cSeries= Col("series", "Series", 130, r => S(((IGame)r).Series), visible: false);
        var cRegion= Col("region", "Region", 90, r => S(((IGame)r).Region), visible: false);
        var cMode  = Col("playmode", "Play Mode", 110, r => S(((IGame)r).PlayMode), visible: false);
        var cVer   = Col("version", "Version", 90, r => S(((IGame)r).Version), visible: false);
        var cStatus= Col("status", "Status", 90, r => S(((IGame)r).Status), visible: false);
        var cSource= Col("source", "Source", 110, r => S(((IGame)r).Source), visible: false);
        var cYear  = Col("year", "Year", 55, r => N(() => ((IGame)r).ReleaseYear), HorizontalAlignment.Right);
        var cRel   = Col("releasedate", "Release Date", 100, r => Safe(() => (object)((IGame)r).ReleaseDate), HorizontalAlignment.Right, visible: false);
        cRel.AspectToStringConverter = v => v is DateTime d ? d.ToString("yyyy-MM-dd") : "";

        // Effective rating: user (StarRatingFloat) if set, else community — like
        // launchbox-web / bigbox-web. Coloured per-cell in FormatCell below.
        var cRate  = Col("rating", "Rating", 70, r => N(() => (double?)((IGame)r).CommunityOrLocalStarRating), HorizontalAlignment.Right);
        cRate.AspectToStringConverter = v => v is double d && d > 0 ? d.ToString("0.#") + " ★" : "";
        var cEsrb  = Col("esrb", "ESRB", 70, r => S(((IGame)r).Rating), visible: false);
        var cComm  = Col("community", "Community", 80, r => N(() => (double?)((IGame)r).CommunityStarRating), HorizontalAlignment.Right, visible: false);
        cComm.AspectToStringConverter = v => v is double d && d > 0 ? d.ToString("0.#") : "";
        var cVotes = Col("votes", "Votes", 60, r => N(() => (int?)((IGame)r).CommunityStarRatingTotalVotes), HorizontalAlignment.Right, visible: false);

        var cFav   = Col("fav", "Fav", 45, r => Safe(() => ((IGame)r).Favorite), HorizontalAlignment.Center);
        cFav.AspectToStringConverter = v => v is bool b && b ? "★" : "";
#pragma warning disable CS0618 // IGame.Completed is marked obsolete by the SDK but is still the Completed flag
        var cDone  = Col("completed", "Done", 50, r => Safe(() => ((IGame)r).Completed), HorizontalAlignment.Center, visible: false);
#pragma warning restore CS0618
        cDone.AspectToStringConverter = Check;
        var cBroken= Col("broken", "Broken", 55, r => Safe(() => ((IGame)r).Broken), HorizontalAlignment.Center, visible: false);
        cBroken.AspectToStringConverter = Check;
        var cPort  = Col("portable", "Portable", 60, r => Safe(() => ((IGame)r).Portable), HorizontalAlignment.Center, visible: false);
        cPort.AspectToStringConverter = Check;
        var cInst  = Col("installed", "Installed", 60, r => Safe(() => (object)((IGame)r).Installed), HorizontalAlignment.Center, visible: false);
        cInst.AspectToStringConverter = v => v is bool b && b ? "✓" : "";
        var cPlayers = Col("players", "Players", 60, r => N(() => ((IGame)r).MaxPlayers), HorizontalAlignment.Right, visible: false);
        var cPlays = Col("plays", "Plays", 55, r => N(() => (int?)((IGame)r).PlayCount), HorizontalAlignment.Right);
        var cTime  = Col("playtime", "Play Time", 80, r => Safe(() => (object)((IGame)r).PlayTime), HorizontalAlignment.Right, visible: false);
        cTime.AspectToStringConverter = v => v is int s ? FormatPlayTime(s) : "";
        var cAdded = Col("dateadded", "Date Added", 100, r => Safe(() => (object)((IGame)r).DateAdded), HorizontalAlignment.Right, visible: false);
        cAdded.AspectToStringConverter = v => v is DateTime d && d != default ? d.ToString("yyyy-MM-dd") : "";
        var cMod   = Col("datemodified", "Date Modified", 110, r => Safe(() => (object)((IGame)r).DateModified), HorizontalAlignment.Right, visible: false);
        cMod.AspectToStringConverter = v => v is DateTime d && d != default ? d.ToString("yyyy-MM-dd") : "";
        var cLast  = Col("lastplayed", "Last Played", 100, r => Safe(() => (object)((IGame)r).LastPlayedDate), HorizontalAlignment.Right, visible: false);
        cLast.AspectToStringConverter = v => v is DateTime d ? d.ToString("yyyy-MM-dd") : "";
        var cDbId  = Col("dbid", "DB Id", 70, r => N(() => ((IGame)r).LaunchBoxDbId), HorizontalAlignment.Right, visible: false);
        var cAppPath = Col("apppath", "Application Path", 300, r => S(((IGame)r).ApplicationPath), visible: false);
        // Debug: RetroAchievements ROM hash (host-internal field, not an IGame member).
        var cRaHash = Col("rahash", "RA Hash", 240, r => r is HostGame hg ? hg.RetroAchievementsHash : "", visible: false);

        // AllColumns holds every column; RebuildColumns shows only the IsVisible ones.
        // Order here = default display order (visible first), then optional columns.
        olv.AllColumns.AddRange(new[]
        {
            cName, cTitle, cPlat, cDev, cGenre, cYear, cRate, cFav, cPlays,
            cPub, cSeries, cRegion, cMode, cVer, cStatus, cSource, cRel, cEsrb, cComm, cVotes,
            cDone, cBroken, cPort, cInst, cPlayers, cTime, cAdded, cMod, cLast, cDbId, cAppPath, cRaHash,
        });
        olv.RebuildColumns();

        // Parallel to SortLabels (see SortLabels): each entry maps to a column.
        _sortColumns = new[] { cName, cTitle, cPlat, cDev, cYear, cRate, cPlays, cAdded, cMod, cLast };

        // Per-cell rating colour: user rating in amber, community in muted grey.
        olv.FormatCell += (_, e) =>
        {
            if (ReferenceEquals(e.Column, cRate) && !e.Item.Selected && e.Model is IGame g
                && Safe(() => g.CommunityOrLocalStarRating) > 0)
                e.SubItem.ForeColor = Safe(() => g.StarRatingFloat) > 0 ? UserRating : CommRating;
        };

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
            ShowGroups = false, UseFiltering = false, UseExplorerTheme = false,
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

        _treeRoots = roots;
        BuildTreeIcons(roots);
        _sources.Roots = roots;
        try { _sources.ExpandAll(); } catch { }
        // Selection (saved category/game) is restored by RestoreSelection().
    }

    // ── Persistence (human-readable INI, written once at close) ──────────────
    private static string ColKey(OLVColumn c) => c?.Tag as string;

    private void SaveAll()
    {
        SaveColumnLayout();
        SaveWindowState();
        _cfg.Set("LastCategory", NodeKey(_currentNode) ?? "*");
        var g = _games.SelectedObject as IGame;
        _cfg.Set("LastGame", g != null ? S(Safe(() => g.Id)) : "");
        var sc = (_sortColumns != null && _sortCombo.SelectedIndex >= 0 && _sortCombo.SelectedIndex < _sortColumns.Length)
                 ? _sortColumns[_sortCombo.SelectedIndex] : null;
        _cfg.Set("SortColumn", ColKey(sc) ?? "name");
        _cfg.SetBool("SortAsc", _ascending);
        _cfg.Save();
    }

    // Col.<key> = <width>,<visible 0/1>,<displayIndex or -1>
    private void SaveColumnLayout()
    {
        foreach (var c in _games.AllColumns)
        {
            var key = ColKey(c); if (key == null) continue;
            int di = c.IsVisible ? c.DisplayIndex : -1;
            _cfg.Set("Col." + key, $"{c.Width},{(c.IsVisible ? 1 : 0)},{di}");
        }
    }

    private void RestoreColumnLayout()
    {
        var visible = new List<(OLVColumn col, int di)>();
        foreach (var c in _games.AllColumns)
        {
            var key = ColKey(c); if (key == null) continue;
            var v = _cfg.Get("Col." + key);
            if (string.IsNullOrEmpty(v))
            {
                if (c.IsVisible) visible.Add((c, int.MaxValue)); // no saved entry → keep default, order last
                continue;
            }
            var p = v.Split(',');
            if (p.Length >= 1 && int.TryParse(p[0], out var w) && w > 0) c.Width = w;
            if (p.Length >= 2) c.IsVisible = p[1] == "1";
            int di = (p.Length >= 3 && int.TryParse(p[2], out var d) && d >= 0) ? d : int.MaxValue;
            if (c.IsVisible) visible.Add((c, di));
        }
        try
        {
            _games.RebuildColumns();   // applies IsVisible (builds the Columns collection)
            // RebuildColumns keeps each column's stale DisplayIndex, so set the
            // display order explicitly. OrderBy is stable → equal/MaxValue di keep
            // their AllColumns order.
            var ordered = visible.OrderBy(o => o.di).Select(o => o.col).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        }
        catch { }
    }

    private void SaveWindowState()
    {
        // RestoreBounds reports {-1,-1} for a window never min/maximized, so use
        // Bounds when Normal and RestoreBounds only when maximized/minimized.
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (b.Width >= 200 && b.Height >= 200)
        {
            _cfg.SetInt("WinX", b.X); _cfg.SetInt("WinY", b.Y);
            _cfg.SetInt("WinW", b.Width); _cfg.SetInt("WinH", b.Height);
        }
        _cfg.SetBool("WinMax", WindowState == FormWindowState.Maximized);
    }

    private void RestoreWindowState()
    {
        int w = _cfg.GetInt("WinW", 0), h = _cfg.GetInt("WinH", 0);
        var rect = new Rectangle(_cfg.GetInt("WinX", 0), _cfg.GetInt("WinY", 0), w, h);
        // Safety: a monitor may have been unplugged since last run, leaving the
        // saved bounds off-screen. Only honour them when the title bar is reachable
        // on a CURRENT screen; otherwise keep the ctor defaults (centered, default
        // size) — i.e. reset to the default position/size.
        if (w >= 400 && h >= 300 && IsBoundsUsable(rect))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = rect;
            if (_cfg.GetBool("WinMax", false)) WindowState = FormWindowState.Maximized;
        }
    }

    // Usable only if a grabbable strip of the title bar lands on some current
    // screen's working area (so the user can actually see and move the window).
    private static bool IsBoundsUsable(Rectangle r)
    {
        if (r.Width < 200 || r.Height < 150) return false;
        var caption = new Rectangle(r.Left, r.Top, r.Width, 30);
        foreach (var sc in Screen.AllScreens)
        {
            var i = Rectangle.Intersect(sc.WorkingArea, caption);
            if (i.Width >= 120 && i.Height >= 8) return true;
        }
        return false;
    }

    // Monitor unplugged at runtime → if the window ended up off-screen, bring it
    // back: normalize, clamp to the primary working area and recenter on it.
    private void OnDisplaySettingsChanged(object sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => OnDisplaySettingsChanged(sender, e))); } catch { } return; }
        if (WindowState == FormWindowState.Minimized) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (IsBoundsUsable(b)) return;
        var ps = Screen.PrimaryScreen; if (ps == null) return;
        var wa = ps.WorkingArea;
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        Size = new Size(Math.Max(400, Math.Min(Width, wa.Width - 40)), Math.Max(300, Math.Min(Height, wa.Height - 40)));
        Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2), wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    private void RestoreSort()
    {
        _ascending = _cfg.GetBool("SortAsc", true);
        if (_dirBtn != null) _dirBtn.Text = _ascending ? "▲" : "▼";
        var key = _cfg.Get("SortColumn", "name");
        int idx = 0;
        for (int i = 0; i < _sortColumns.Length; i++)
            if (string.Equals(ColKey(_sortColumns[i]), key, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        _suppressSort = true;
        try { _sortCombo.SelectedIndex = idx; } finally { _suppressSort = false; }
    }

    private void RestoreSelection()
    {
        object node = AllNode.Instance;
        var savedCat = _cfg.Get("LastCategory");
        if (!string.IsNullOrEmpty(savedCat)) node = FindNodeByKey(savedCat) ?? AllNode.Instance;

        _sources.SelectedObject = node;   // visual; the coalesced event is a no-op via LoadNode's guard
        LoadNode(node);                   // synchronous fill (so the saved game can be selected right after)
        try { _sources.EnsureModelVisible(node); } catch { }

        var savedGame = _cfg.Get("LastGame");
        if (!string.IsNullOrEmpty(savedGame))
        {
            var g = _current.FirstOrDefault(x => string.Equals(Safe(() => x.Id), savedGame, StringComparison.OrdinalIgnoreCase));
            if (g != null) { _games.SelectObject(g, true); _games.EnsureModelVisible(g); ShowDetails(g); }
        }
    }

    /// <summary>Stable key for a tree node, persisted as LastCategory.</summary>
    private static string NodeKey(object node)
    {
        if (node is AllNode) return "*";
        if (node is IPlatformCategory c) return "C:" + c.Name;
        if (node is IPlaylist pl) return "L:" + (!string.IsNullOrEmpty(pl.PlaylistId) ? pl.PlaylistId : pl.Name);
        if (node is IPlatform p) return "P:" + p.Name;
        return null;
    }

    private object FindNodeByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key == "*") return AllNode.Instance;
        foreach (var n in EnumerateTreeNodes())
            if (string.Equals(NodeKey(n), key, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    private IEnumerable<object> EnumerateTreeNodes()
    {
        if (_treeRoots == null) yield break;
        var stack = new Stack<object>(_treeRoots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is AllNode) continue;
            yield return n;
            if (n is HostPlatformCategory cat) foreach (var ch in cat.Children) stack.Push(ch);
        }
    }

    private static string FormatPlayTime(int seconds)
    {
        if (seconds <= 0) return "";
        int h = seconds / 3600, m = (seconds % 3600) / 60;
        return h > 0 ? $"{h}h {m:00}m" : (m > 0 ? $"{m}m" : "<1m");
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
            using var src = LoadImage(path);   // WebP-aware (Magick) + GDI+ for the rest
            if (src == null) return null;
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
        // Guard re-selecting the already-loaded node — also stops the coalesced
        // tree SelectionChanged from re-loading (and clobbering a restored game
        // selection) right after RestoreSelection called LoadNode directly.
        if (node == null || ReferenceEquals(node, _currentNode)) return;
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
        // Same source selection as launchbox-web/bigbox-web: ClearLogo regroupement
        // for the logo, Front for the box art (via GameCache when ExtendDB is loaded
        // → same file → shared thumb cache; IO fallback otherwise).
        string logoSrc = g == null ? null : DetailSource(g, "ClearLogo", () => Safe(() => g.ClearLogoImagePath));
        string artSrc = g == null ? null
            : DetailSource(g, "Front", () =>
                  Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f
                : Safe(() => g.Box3DImagePath) is { Length: > 0 } b ? b
                : Safe(() => g.ScreenshotImagePath));
        LoadImagesAsync(logoSrc, artSrc);

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

    // Generate/load the right-pane images OFF the UI thread (degraded thumbs from the
    // shared cache: logo=WebP w/ alpha, art=JPEG), so selecting a node/game never blocks
    // the game-list paint — only the cheap text is set synchronously. The token discards
    // a stale load if the selection changed before it finished. Args are SOURCE paths.
    private void LoadImagesAsync(string logoSrc, string artSrc)
    {
        int token = ++_detailsLoadToken;
        SetImage(_logo, null);   // drop the previous subject's art right away
        SetImage(_art, null);
        if (string.IsNullOrEmpty(logoSrc) && string.IsNullOrEmpty(artSrc)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var logo = LoadThumbOrFull(logoSrc, keepAlpha: true);   // clear logo → WebP/alpha
            var art = LoadThumbOrFull(artSrc, keepAlpha: false);    // box art → JPEG
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

    // Degraded thumbnail from the shared ExtendDB cache; falls back to the full original
    // when no thumb can be made (e.g. Magick absent in standalone). LoadImage handles the
    // WebP the logo tier produces.
    private static Image LoadThumbOrFull(string src, bool keepAlpha)
    {
        if (string.IsNullOrEmpty(src)) return null;
        string thumb = null;
        try { thumb = ThumbCache.GetOrCreate(src, ThumbCache.DefaultMaxDim, keepAlpha); } catch { }
        return LoadImage(thumb ?? src);
    }

    // Picks the SAME source image launchbox-web/bigbox-web would (GameCache regroupement)
    // when ExtendDB is loaded — so the resolved file, and thus the shared thumb-cache key,
    // matches. Falls back to LiteBox's IO resolution when the cache isn't available.
    private static string DetailSource(IGame g, string regroupement, Func<string> ioFallback)
    {
        try
        {
            string plat = g.Platform;
            if (!string.IsNullOrEmpty(plat) && GameCacheBridge.Ready(plat) && Guid.TryParse(g.Id, out var id))
            {
                var p = GameCacheBridge.BestImageTypeFirst(plat, id, regroupement);
                if (!string.IsNullOrEmpty(p)) return p;
            }
        }
        catch { }
        return ioFallback();
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
            // GDI+ can't decode WebP (clear logos) → route those through Magick.NET.
            if (IsWebp(bytes)) return LoadWebp(bytes);
            using var ms = new MemoryStream(bytes);
            using var tmp = Image.FromStream(ms);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }

    private static bool IsWebp(byte[] b)
        => b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                          && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    // Optional dependency: Magick.NET is loaded by ExtendDB at runtime. The try/catch
    // here (not inside DecodeWebpMagick) absorbs the assembly-not-found that would be
    // thrown when JITing DecodeWebpMagick if Magick.NET is absent (standalone, no
    // ExtendDB) → WebP logos simply don't render instead of crashing.
    private static Image LoadWebp(byte[] bytes)
    {
        try { return DecodeWebpMagick(bytes); }
        catch { return null; }
    }

    private static Image DecodeWebpMagick(byte[] bytes)
    {
        using var img = new ImageMagick.MagickImage(bytes);
        // Png32 preserves the alpha channel so the transparent clear-logo background
        // survives into the GDI+ Bitmap (the PictureBox draws over the dark panel).
        var png = img.ToByteArray(ImageMagick.MagickFormat.Png32);
        using var ms = new MemoryStream(png);
        using var tmp = Image.FromStream(ms);
        return new Bitmap(tmp);
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
        // Defer via BeginInvoke so it runs AFTER the control's own OnHandleCreated
        // theming. Re-fires on every handle recreation (e.g. column show/hide).
        c.HandleCreated += (_, _) => { try { c.BeginInvoke((Action)(() => ApplyDarkScroll(c))); } catch { } };
        if (c.IsHandleCreated) { try { c.BeginInvoke((Action)(() => ApplyDarkScroll(c))); } catch { } }
    }

    private static void ApplyDarkScroll(Control c)
    {
        if (c == null || !c.IsHandleCreated) return;
        try
        {
            SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            // ObjectListView/ListView don't repaint their non-client scrollbars on a
            // bare SetWindowTheme — force a frame-changed so the dark bars are drawn.
            SetWindowPos(c.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { }
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
