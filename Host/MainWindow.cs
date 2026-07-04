// The host GUI — a LaunchBox-like 3-pane layout (dark themed):
//   LEFT   : source tree (All Games / Platforms / Playlists, incl. auto-playlists).
//   CENTER : sortable, searchable game LIST (native GameListView, columns). Default
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
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Ra;
using LbApiHost.Host.Store;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host;

internal sealed class MainWindow : Form
{
    // ── Theme ────────────────────────────────────────────────────────────────
    // Bg/Panel/Panel2/Fg/SubFg/Accent are byte-for-byte the same palette as Host.UiKit.LiteBoxTheme -
    // referencing it here instead of a second copy of the same Color.FromArgb literals means a future
    // palette change only has one place to edit. Row2 has no LiteBoxTheme equivalent (a striped-row
    // shade specific to this list), so it stays local.
    private static readonly Color Bg      = LiteBoxTheme.Bg;
    private static readonly Color Panel   = LiteBoxTheme.PanelC;
    private static readonly Color Panel2  = LiteBoxTheme.Panel2;
    private static readonly Color Row2    = Color.FromArgb(34, 34, 36);
    private static readonly Color Fg      = LiteBoxTheme.Fg;
    private static readonly Color SubFg   = LiteBoxTheme.SubFg;
    private static readonly Color Accent  = LiteBoxTheme.Accent;
    private static readonly Color UserRating = Color.FromArgb(255, 196, 0);   // amber: user-set rating
    private static readonly Color CommRating = Color.FromArgb(150, 150, 152); // grey: community rating

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    // Caption (title-bar) colouring — Windows 11 (build 22000+) only; a harmless no-op error on Win10
    // (there the warning banner below the caption is the visible cue instead).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);   // COLORREF = 0x00BBGGRR

    // Forced read-only because another LiteBox instance is already running (only one may write the
    // LB XMLs / op-log). Drives the warning caption + banner and the locked options. See InstanceGuard.
    private readonly bool _secondInstance;
    private static readonly Color WarnBg = Color.FromArgb(170, 60, 0);   // warning amber/orange

    private readonly PluginRegistry _reg;
    private readonly IDataManager _dm;

    private readonly TreeView _sources;
    private readonly Dictionary<object, TreeNode> _treeNodeMap = new();   // node object → its TreeNode (selection restore)
    private readonly GameListView _games;
    // Poster (grid) view — a native virtual ListView mirroring the OLV's displayed (sorted+filtered)
    // order; owner-drawn box-art tiles. Toggled from the toolbar (list ⇄ poster).
    private ListView _poster;
    private ToolStripButton _posterBtn;
    private bool _posterMode;
    private readonly Dictionary<Guid, Image> _posterBmp = new();   // decoded box thumbs (visible-ish)
    private readonly Queue<Guid> _posterBmpOrder = new();          // FIFO eviction order
    // Native image-list slot pool: each game's fully composited tile (box + title + developer, baked
    // once) lives in a Win32 HIMAGELIST and is drawn by the NATIVE control during scroll — no managed
    // per-tile paint at all (that owner-draw repaint storm is what froze a held scroll). Slots recycle
    // LRU so memory stays bounded; a tile is (re)built only when its item is retrieved or its thumb
    // finishes loading, never per frame.
    private IntPtr _himl;                                           // native HIMAGELIST (ILC_COLOR32)
    private readonly Dictionary<Guid, int> _slotOf = new();         // game id -> imagelist slot
    private readonly List<Guid> _slotId = new();                    // slot -> game id (for LRU eviction)
    private int _slotCount;                                         // slots populated so far (<= cap)
    private readonly LinkedList<int> _slotLru = new();              // front = MRU, back = LRU
    private readonly Dictionary<int, LinkedListNode<int>> _slotNode = new();
    private const int PosterSlotCap = 1024;                         // recycled slots (>> on-screen tiles)
    // Legacy owner-draw renderer (opt-in via PosterOwnerDraw; needs a restart to switch). Kept as an
    // alternative to the native image list: it owner-draws each tile (custom rounded selection + hover
    // grow) but repaints managed per tile, so a held scroll in a huge view can stutter.
    private bool _posterOwnerDraw;
    private ImageList _posterGeom;                                  // empty; only its ImageSize drives the tile geometry
    private int _posterHot = -1;                                    // hovered tile index (for the hover grow)
    private readonly Dictionary<Guid, IntPtr> _posterTileHbm = new();   // GDI HBITMAP per composited tile (fast BitBlt)
    private readonly Queue<Guid> _posterTileOrder = new();
    private SolidBrush _panelBrush;                                 // cached bg brush (no per-tile alloc)
    private IntPtr _posterMemDC;                                    // shared memory DC for BitBlt/StretchBlt
    // Poster thumb loading: a small BOUNDED worker pool draining a LIFO deque (newest/visible tiles
    // first), with BATCHED completion (one UI marshal drains all ready thumbs). Replaces the per-tile
    // Task.Run that, on a fast scroll, spawned hundreds of parallel decodes whose individual BeginInvoke
    // completions flooded — and froze — the UI thread until the key was released.
    private readonly LinkedList<(IGame g, Guid id)> _posterReq = new();  // pending requests (front = newest)
    private readonly HashSet<Guid> _posterPending = new();               // queued/loading/awaiting-apply (dedup)
    private readonly Queue<(IGame g, Guid id, Image img)> _posterDone = new();   // loaded, awaiting batched apply
    private readonly object _posterQLock = new();                        // guards _posterReq/_posterPending/_posterDone/workers
    private int _posterActiveWorkers;
    private bool _posterDrainPending;              // coalesces the batched apply+invalidate
    private static readonly int PosterMaxWorkers = Math.Max(1, Math.Min(3, Environment.ProcessorCount - 1));
    private const int PosterReqCap = 64;           // cap pending requests; drop oldest (scrolled-past) beyond this
    private const int PCellW = 124, PImgH = 174, PLabelH = 38, PGap = 14;
    private readonly ToolStripTextBox _search;
    private readonly ToolStripComboBox _sortCombo;
    private readonly ToolStripButton _dirBtn;
    private readonly ToolStripLabel _count;
    private ToolStripLabel _extDbInd;      // "ExtendDB present" indicator (toolbar, before the count)
    private ToolStripLabel _parentalInd;   // parental-control padlock indicator (toolbar, before the count)
    private Image _padlockClosed, _padlockOpen;
    // Platforms whose games parental control must hide from the list (a platform directly listed,
    // or any platform sitting under a hidden category). Recomputed with the tree. See ParentalBridge.
    private readonly HashSet<string> _parentalHiddenPlatforms = new(StringComparer.OrdinalIgnoreCase);

    // right-hand details
    private readonly HeroPanel _hero;            // fanart + clear logo (pulse) + rating + heart
    private readonly MediaPanel _media;          // main media (box → screenshots, click to switch)
    private readonly MediaStrip _strip;          // clickable mini-thumbnails under the main media (slim custom scrollbar)
    private SplitContainer _outerSplit;          // left tree | (middle list + right details) — % persisted
    private SplitContainer _innerSplit;          // middle list | right details — % persisted
    private Panel _detailHost;                    // scroll viewport hosting the detail grid (scrollbar when content overflows)
    private LaunchButtons _launchButtons;         // Play / Version / ROM group docked at the pane bottom
    private TableLayoutPanel _detailGrid;        // detail layout — sized by RelayoutDetail (fills viewport, or taller → scrolls)
    private double _mediaAspect = 16.0 / 9.0;    // reserved main-media area aspect (16:9 default, 2:3 poster option)
    private List<string> _mediaItems;            // current game's media sources (box first, then screenshots)
    private int _mediaSel;                        // selected media index
    private System.Windows.Forms.Timer _mediaTimer;   // 0.5s debounce: build strip + upgrade main to full
    private readonly MetaCard _meta;             // title + platform + expandable game fields (or node text)
    private readonly VndbCard _vndb;             // expandable box of coloured VNDB tags (content/tech/ero)
    private RetroAchievementsCard _raCard;       // expandable RetroAchievements box (LiteBox-native, from the raid)
    private StoreAchievementsCard _storeAchCard; // expandable store-achievements box (GOG today; from galaxy-2.0.db)
    private readonly TextBox _notes;
    private static bool _metaExpanded;           // remembered expand state of the platform meta card (session + INI)
    private static bool _vndbExpanded;           // remembered expand state of the VNDB tags box (session + INI)
    private static bool _raExpanded;             // remembered expand state of the RetroAchievements box (session + INI)
    private static bool _storeAchExpanded;       // remembered expand state of the store-achievements box (session + INI)
    private readonly Dictionary<string, Image> _platIconCache = new(StringComparer.OrdinalIgnoreCase);

    private IGame[] _current = Array.Empty<IGame>();
    private IGame _heroGame;        // game currently shown in the hero (for rate/favorite clicks)
    private long _lastStoreSyncTick;   // debounce for the focus-regained store re-sync (Environment.TickCount64)
    private System.Windows.Forms.Timer _storePollTimer;   // active install-state poll while a store game is selected
    private volatile bool _storeLostFocus;       // LiteBox lost the foreground since the current store launch
    private volatile bool _storeRegainedFocus;   // …and has since regained it (store running-screen exit signal)
    private volatile bool _gameRunning;          // a game is launching/running → pause store status refresh
    private System.Windows.Forms.Timer _fanartTimer;                       // 0.5s debounce before fanart fade-in
    private readonly Dictionary<string, string> _fanartPick = new();       // node/game key -> chosen fanart src (stable per session)
    private object _currentNode;   // selected tree node (for the right pane when no game is selected)
    private System.Windows.Forms.ComboBox _viewCombo;               // left-panel "group by" selector
    private SourceView _currentView = SourceViews.ById(null);       // current grouping (default Platform Category)
    private bool _suppressViewEvent;                                // guard combo SelectedIndexChanged during sync
    private List<object> _treeRoots;   // tree roots (incl. AllNode) — for key lookup on restore
    private object _detailsShown;  // current right-pane subject (IGame or tree node)
    private int _detailsLoadToken; // guards async image loads against stale selections
    // Serialized, latest-wins detail loader (replaces the per-selection parallel loads that flooded
    // the UI thread and froze the list while an arrow key was held).
    private IGame _detailWant;             // latest game whose detail is wanted (guarded by _detailLock)
    private bool _detailRunning;           // a loader task is currently active (guarded by _detailLock)
    private readonly object _detailLock = new();
    private volatile bool _closing;        // form is closing → the loader bails before its blocking Invoke
    private bool _ascending = true;
    private string[] _sortKeys;          // parallel to SortLabels (column keys; "name" = CompareName)
    private string _curSortKey = "name"; // current sort key (header click can pick a non-combo column)
    private bool _suppressSort;

    private readonly LiteBoxConfig _cfg;
    private static bool _useImageCache = true;   // option: use the degraded thumb cache for UI images

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
        // LEDBlinky reads its enable flag + exe path live from LB Settings.xml.
        if (_dm is HostDataManagerXml hdmLed) LedBlinky.Bind(hdmLed.LbSettings);
        _cfg = LiteBoxConfig.LoadForExe();
        _secondInstance = InstanceGuard.AnotherInstanceRunning;
        _useImageCache = _cfg.UseImageCache;
        _posterOwnerDraw = _cfg.GetBool("PosterOwnerDraw", false);   // legacy poster renderer (vs native image list)
        _metaExpanded = _cfg.GetBool("MetaExpanded", false);
        _vndbExpanded = _cfg.GetBool("VndbExpanded", false);
        _raExpanded = _cfg.GetBool("RaExpanded", false);
        _storeAchExpanded = _cfg.GetBool("StoreAchExpanded", false);
        Text = _secondInstance
            ? "LiteBox — READ-ONLY (another instance is open — changes won't be saved)"
            : "LiteBox";
        try { using var ico = typeof(MainWindow).Assembly.GetManifestResourceStream("LbApiHost.litebox.ico"); if (ico != null) Icon = new Icon(ico); } catch { }
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9f);
        RestoreWindowState();   // size / position / maximized from the INI (overrides the defaults)

        // Reconcile GOG/Steam install state before building the list — LiteBox runs without
        // LaunchBox.exe, so this is what flips Installed / sets the GOG .lnk ApplicationPath.
        StoreTrace.Log("==== LiteBox boot — initial store sync ====");
        if ((_dm as HostDataManagerXml)?.SyncStoreInstallStates() > 0)
            (_dm as HostDataManagerXml)?.FlushIfSafe();   // persist the correction now (no-op if LB is running)
        _lastStoreSyncTick = Environment.TickCount64;
        // Re-reconcile whenever LiteBox regains focus — the user installs/uninstalls in GOG Galaxy /
        // Steam (another window) and comes back; this picks it up live, no restart needed (debounced).
        Activated += (_, _) => OnActivatedStoreResync();
        Deactivate += (_, _) => _storeLostFocus = true;   // store running-screen: track foreground loss
        // While a GOG/Steam game is selected, actively poll the client install-state so an
        // uninstall (or a delayed DB write the focus check missed) flips the button within ~1.5s.
        _storePollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _storePollTimer.Tick += (_, _) => StorePollTick();

        _games = BuildGameList();

        _sources = BuildSourceTree();
        LogParentalState("boot (after tree)");

        var details = BuildDetails(out _hero, out _media, out _strip, out _meta, out _vndb, out _notes);
        _hero.RateClicked = v => RateHeroGame(v);
        _hero.FavClicked = () => ToggleHeroFavorite();
        _meta.ExpandedChanged = OnMetaExpandedToggled;
        _vndb.ExpandedChanged = OnVndbExpandedToggled;

        // Scroll viewport: the detail grid normally fills it (notes absorbs the slack); when the
        // content needs more than fits (e.g. the meta box expanded, or a short pane), the grid grows
        // taller than the viewport and a vertical scrollbar appears — its width is reserved so it
        // never overlaps the content (RelayoutDetail).
        _detailHost = new Panel { Dock = DockStyle.Fill, BackColor = Panel, AutoScroll = true };
        _detailHost.Controls.Add(details);
        _detailHost.Resize += (_, _) => RelayoutDetail();

        _poster = BuildPoster();

        var inner = new ThemedSplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Bg, SplitterWidth = 4 };
        inner.Panel1.BackColor = Panel;       // shows in the side margins around the centred poster grid
        inner.Panel1.Controls.Add(_poster);   // hidden until poster mode; same cell as the list
        inner.Panel1.Controls.Add(_games);
        // Launch buttons docked at the bottom of the details pane (always visible,
        // outside the scrolling detail grid). _detailHost (Fill) is added FIRST so
        // the bottom panel reserves its space and the grid fills the rest.
        inner.Panel2.Controls.Add(_detailHost);
        _launchButtons = new LaunchButtons(
            (g, app, emu) => Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, app, emu, null)),
            StoreLaunch,   // GOG/Steam: running screen + exit watch
            g => (_dm as HostDataManagerXml)?.GetLastLaunch(Safe(() => g.Id)));   // launch-button initial selection fallback (no ExtendDB)
        inner.Panel2.Controls.Add(_launchButtons);
        inner.Panel1.Resize += (_, _) => LayoutPoster();   // keep the poster grid centred on resize

        var outer = new ThemedSplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Bg, SplitterWidth = 4 };
        // Left panel = a "group by" ComboBox (top) above the source tree (fill). A TableLayoutPanel gives a
        // deterministic top-strip + fill split (no docking z-order guessing).
        _viewCombo = BuildViewCombo();
        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = Panel, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        leftPanel.Controls.Add(_viewCombo, 0, 0);
        leftPanel.Controls.Add(_sources, 0, 1);
        outer.Panel1.Controls.Add(leftPanel);
        outer.Panel2.Controls.Add(inner);
        Controls.Add(outer);
        _outerSplit = outer; _innerSplit = inner;   // for splitter % persistence

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

        // Label shows the view you'd switch TO: "Poster View" while in list mode, "List View" while in
        // poster mode (kept in sync by SetPosterMode). Default mode is list → start as "Poster View".
        var posterBtn = new ToolStripButton("Poster View") { ForeColor = Fg, CheckOnClick = true, ToolTipText = "Toggle list / poster view" };
        posterBtn.CheckedChanged += (_, _) => SetPosterMode(posterBtn.Checked);
        _posterBtn = posterBtn;
        bar.Items.Add(posterBtn);
        bar.Items.Add(new ToolStripSeparator());
        var genBtn = new ToolStripButton("Generate Image Cache")
        { ForeColor = Fg, ToolTipText = "Pre-generate the cached thumbnails (logo, box, screenshot) for every game" };
        genBtn.Click += (_, _) => GenerateAllCachedImages();
        bar.Items.Add(genBtn);

        // Options (gear) — right aligned. Opens the sectioned options window
        // (Host/Options): the old per-toggle dropdown grew past what a menu
        // can carry, and the window's option model lets each setting migrate
        // to its final storage (INI vs LB Settings/emulator/game) later
        // without touching the UI.
        var optBtn = new ToolStripButton("⚙")
        {
            ForeColor = Fg, ToolTipText = "Options", Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Segoe UI Symbol", 11f),
        };
        if (_secondInstance)   // options locked while a 2nd instance forces read-only
        {
            optBtn.Enabled = false;
            optBtn.ToolTipText = "Options locked — another LiteBox instance is open (read-only)";
        }
        optBtn.Click += (_, _) =>
        {
            using var w = BuildOptionsWindow();
            w.ShowDialog(this);
            // Scoped flush: the LB-settings ops go to Settings.xml right away
            // (when safe); LiteBox INI options were already saved by ApplyFinished.
            (_dm as HostDataManagerXml)?.FlushLbSettingsIfSafe();
        };
        bar.Items.Add(optBtn);

        // Manage Emulators (full per-emulator config; read-only honours the lock).
        var emusBtn = new ToolStripButton("Emulators") { ForeColor = Fg, ToolTipText = "Manage Emulators" };
        emusBtn.Click += (_, _) =>
        {
            bool ro = (_dm as HostDataManagerXml)?.ReadOnly ?? true;
            using var w = new Emulators.ManageEmulatorsWindow(ro, LbApiHost.Host.Media.MediaResolver.LbRoot ?? "");
            w.ShowDialog(this);
            // Opportunistic SCOPED flush: only the Emulators.xml ops go to disk now
            // (when safe — LB/BB closed); game/playlist ops stay pending until the
            // close-time flush. Matches the natural "I closed the editor, it's
            // saved" expectation without committing unrelated half-done edits.
            (_dm as HostDataManagerXml)?.FlushEmulatorsIfSafe();
        };
        bar.Items.Add(emusBtn);

        _count = new ToolStripLabel("") { ForeColor = SubFg, Alignment = ToolStripItemAlignment.Right };
        bar.Items.Add(_count);

        // ExtendDB / parental-control indicators, just to the left of the game count.
        // Right-aligned items lay out right→left in add order, so adding these AFTER _count
        // puts them on its left (the padlock nearest the count, "ExtendDB" further left).
        _parentalInd = new ToolStripLabel("")
        {
            Alignment = ToolStripItemAlignment.Right, Visible = false,
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
        };
        bar.Items.Add(_parentalInd);
        _extDbInd = new ToolStripLabel("ExtendDB")
        {
            ForeColor = Accent, Alignment = ToolStripItemAlignment.Right, Visible = false,
            ToolTipText = "ExtendDB plugin detected — its metadata & media cache power this view",
        };
        bar.Items.Add(_extDbInd);

        // Persist layout / window / selection once, at close (not per change).
        // _closing lets the serialized detail loader bail before its blocking Invoke once the pump ends.
        FormClosing += (_, _) => { _closing = true; LedBlinky.FrontendQuit(); try { SaveAll(); } catch { } };

        // Bring the window back on-screen if a monitor is unplugged while running.
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }

        // React to game launch start/end (for the running screen + during-game unload).
        HostLaunch.GameStarted += OnGameStarted;
        HostLaunch.GameEnded += OnGameEnded;
        // React to ExtendDB parental lock/unlock so the tree, list and padlock re-sync live.
        ParentalBridge.StateChanged += OnParentalStateChanged;
        // App-wide ExtendDB hotkeys (kiosk F10/F11 + parental key) — ExtendDB's own WPF-input
        // hotkeys don't fire in a WinForms host, so the host captures them. See HostHotKeys.
        HostHotKeys.Install(this);
        FormClosed += (_, _) =>
        {
            HostLaunch.GameStarted -= OnGameStarted;
            HostLaunch.GameEnded -= OnGameEnded;
            ParentalBridge.StateChanged -= OnParentalStateChanged;
            HostHotKeys.Uninstall();
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
            HostStateManager.SelectedGamesProvider = null;
        };

        // Expose the current selection to plugins via IStateManager (UI-thread safe).
        HostStateManager.SelectedGamesProvider = () =>
        {
            try
            {
                if (IsDisposed) return Array.Empty<IGame>();
                if (InvokeRequired) return (IGame[])Invoke((Func<IGame[]>)(() => _games.SelectedGames));
                return _games.SelectedGames;
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

        // Second-instance warning: a coloured banner at the very top of the client area (added last →
        // docks closest to the caption). On Win11 the caption itself is also tinted (OnHandleCreated);
        // on Win10 the caption colour API is a no-op so this banner is the visible cue.
        if (_secondInstance)
        {
            var warn = new Label
            {
                Dock = DockStyle.Top, Height = 26, AutoSize = false,
                BackColor = WarnBg, ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(8, 0, 8, 0),
                Text = "⚠  READ-ONLY — another LiteBox instance is already open. " +
                       "Your changes won't be saved and the options are locked.",
            };
            Controls.Add(warn);
        }

        _sortCombo.SelectedIndex = 0; // default = CompareName

        // Dark native scrollbars (Win10/11 explorer dark theme).
        DarkScroll(_games);
        DarkScroll(_sources);
        DarkScroll(_notes);
        DarkScroll(_detailHost);   // the detail pane's overflow scrollbar
        // _strip uses its own slim custom scrollbar (no native scrollbar to theme).

        Load += (_, _) =>
        {
            // Pane widths: restore the saved fractions (left tree, middle list) so the 3 panes scale
            // proportionally with the window; fall back to fixed defaults on first run. outer first
            // (sets inner's available width), then inner.
            int leftPm = _cfg.GetInt("SplitLeftPermille", 0);
            int midPm = _cfg.GetInt("SplitMidPermille", 0);
            float dpiS = LiteBoxTheme.DpiScale(this);
            if (leftPm > 0) SetSplitFraction(outer, leftPm / 1000.0);
            else try { outer.SplitterDistance = (int)Math.Round(240 * dpiS); } catch { }
            if (midPm > 0) SetSplitFraction(inner, midPm / 1000.0);
            else try { inner.SplitterDistance = Math.Max((int)Math.Round(300 * dpiS), inner.Width - (int)Math.Round(380 * dpiS)); } catch { }
            RestoreColumnLayout();   // order / width / shown-hidden from the INI
            RestoreSort();           // last sort column + direction
            _currentView = SourceViews.ById(_cfg.Get("GroupView"));   // restore the saved grouping…
            SyncViewCombo();                                          // …reflect it in the combo (no rebuild)
            PopulateSources();       // build the tree
            RestoreSelection();      // last category + game
            RefreshExtendDbIndicators();   // ExtendDB-present + parental padlock
            try { ActiveControl = _games; _games.Focus(); } catch { }
            if (_cfg.GetBool("PosterMode", false)) _posterBtn.Checked = true;   // → SetPosterMode(true)
            LedBlinky.FrontendStart();   // "1" — the front-end is up (LEDBlinky FE-active animation, etc.)
        };
        // Final dark-scrollbar pass once everything (data, columns) is in place.
        Shown += (_, _) =>
        {
            ApplyDarkScroll(_games); ApplyDarkScroll(_sources); ApplyDarkScroll(_notes); ApplyDarkScroll(_detailHost); RelayoutDetail();
            // RA native-fallback rolling refresh (opt-in) — after the window is up, on idle so it never
            // delays the first paint. Gated internally (checkbox + ExtendDB-not-handling-RA + creds set).
            try
            {
                BeginInvoke((Action)(() => RaStartupRefresh.RunIfEnabled(
                    _dm, _cfg.RaStartupRollingRefresh,
                    () => { try { BeginInvoke((Action)(() => (_dm as HostDataManagerXml)?.FlushIfSafe())); } catch { } })));
            }
            catch { }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_secondInstance) return;
        try   // tint the native caption (Win11+); silently ignored on Win10
        {
            int cap = ColorRef(WarnBg), txt = ColorRef(Color.White);
            DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));
            DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref txt, sizeof(int));
        }
        catch { }
    }

    // ── Game list construction (native ListView — smooth scroll, dark themed) ──
    private GameListView BuildGameList()
    {
        var lv = new GameListView
        {
            Dock = DockStyle.Fill, Font = Font, BackColor = Panel, ForeColor = Fg,
            Striped = true, RowBack = Panel, RowAlt = Row2, RowFore = Fg,
        };

        // key = stable INI identity; never localise it. sort = comparable value; text = displayed
        // string; fore = optional per-cell colour (rating). visible = default visibility.
        GameColumn Col(string key, string title, int w, Func<IGame, object> sort, Func<IGame, string> text,
                       HorizontalAlignment align = HorizontalAlignment.Left, bool visible = true, Func<IGame, Color?> fore = null, bool stretch = false)
            => lv.AddColumn(new GameColumn { Key = key, Title = title, Width = w, Visible = visible, Align = align, Sort = sort, Text = text, Fore = fore, Stretch = stretch });

        // DPI-scaled default width, so a fresh install's columns are proportioned sensibly at any
        // scaling factor. Named DpiW, not "W" - a bare one-letter name sits right next to the
        // pre-existing S(string) null-coalescing helper used in the very same Col(...) calls below,
        // and the two are easy to confuse (or swap) at a glance since both take one short argument.
        int DpiW(int px) => (int)Math.Round(px * LiteBoxTheme.DpiScale(this));

        static string DateStr(object v) => v is DateTime d && d != default ? d.ToString("yyyy-MM-dd") : "";

        // Sort the Title column by the article-stripped compare name (LaunchBox-style: "The Legend
        // of Zelda" sorts under L), while still DISPLAYING the full title.
        Col("title", "Title", DpiW(320), g => CompareName(g), g => S(Safe(() => g.Title)), stretch: true);
        Col("platform", "Platform", DpiW(150), g => S(Safe(() => g.Platform)), g => S(Safe(() => g.Platform)));
        Col("developer", "Developer", DpiW(150), g => S(Safe(() => g.Developer)), g => S(Safe(() => g.Developer)));
        Col("publisher", "Publisher", DpiW(150), g => S(Safe(() => g.Publisher)), g => S(Safe(() => g.Publisher)), visible: false);
        Col("genre", "Genre", DpiW(140), g => S(Safe(() => g.GenresString)), g => S(Safe(() => g.GenresString)));
        Col("series", "Series", DpiW(130), g => S(Safe(() => g.Series)), g => S(Safe(() => g.Series)), visible: false);
        Col("region", "Region", DpiW(90), g => S(Safe(() => g.Region)), g => S(Safe(() => g.Region)), visible: false);
        Col("playmode", "Play Mode", DpiW(110), g => S(Safe(() => g.PlayMode)), g => S(Safe(() => g.PlayMode)), visible: false);
        Col("version", "Version", DpiW(90), g => S(Safe(() => g.Version)), g => S(Safe(() => g.Version)), visible: false);
        Col("status", "Status", DpiW(90), g => S(Safe(() => g.Status)), g => S(Safe(() => g.Status)), visible: false);
        Col("source", "Source", DpiW(110), g => S(Safe(() => g.Source)), g => S(Safe(() => g.Source)), visible: false);
        Col("year", "Year", DpiW(55), g => N(() => g.ReleaseYear), g => N(() => g.ReleaseYear)?.ToString() ?? "", HorizontalAlignment.Right);
        Col("releasedate", "Release Date", DpiW(100), g => Safe(() => (object)g.ReleaseDate), g => DateStr(Safe(() => (object)g.ReleaseDate)), HorizontalAlignment.Right, visible: false);
        // Effective rating: user (StarRatingFloat) if set, else community. Coloured per-cell: user amber, community grey.
        Col("rating", "Rating", DpiW(70), g => N(() => (double?)g.CommunityOrLocalStarRating),
            g => { var d = Safe(() => g.CommunityOrLocalStarRating); return d > 0 ? d.ToString("0.#") + " ★" : ""; }, HorizontalAlignment.Right,
            fore: g => Safe(() => g.CommunityOrLocalStarRating) > 0 ? (Safe(() => g.StarRatingFloat) > 0 ? UserRating : CommRating) : (Color?)null);
        Col("esrb", "ESRB", DpiW(70), g => S(Safe(() => g.Rating)), g => S(Safe(() => g.Rating)), visible: false);
        Col("community", "Community", DpiW(80), g => N(() => (double?)g.CommunityStarRating),
            g => { var d = Safe(() => g.CommunityStarRating); return d > 0 ? d.ToString("0.#") : ""; }, HorizontalAlignment.Right, visible: false);
        Col("votes", "Votes", DpiW(60), g => N(() => (int?)g.CommunityStarRatingTotalVotes),
            g => N(() => (int?)g.CommunityStarRatingTotalVotes)?.ToString() ?? "", HorizontalAlignment.Right, visible: false);
        Col("fav", "Fav", DpiW(45), g => Safe(() => (object)g.Favorite), g => Safe(() => g.Favorite) ? "★" : "", HorizontalAlignment.Center);
#pragma warning disable CS0618 // IGame.Completed is marked obsolete by the SDK but is still the Completed flag
        Col("completed", "Done", DpiW(50), g => Safe(() => (object)g.Completed), g => Safe(() => g.Completed) ? "✓" : "", HorizontalAlignment.Center, visible: false);
#pragma warning restore CS0618
        Col("broken", "Broken", DpiW(55), g => Safe(() => (object)g.Broken), g => Safe(() => g.Broken) ? "✓" : "", HorizontalAlignment.Center, visible: false);
        Col("portable", "Portable", DpiW(60), g => Safe(() => (object)g.Portable), g => Safe(() => g.Portable) ? "✓" : "", HorizontalAlignment.Center, visible: false);
        Col("installed", "Installed", DpiW(60), g => Safe(() => (object)g.Installed), g => Safe(() => g.Installed == true) ? "✓" : "", HorizontalAlignment.Center, visible: false);
        Col("players", "Players", DpiW(60), g => N(() => g.MaxPlayers), g => N(() => g.MaxPlayers)?.ToString() ?? "", HorizontalAlignment.Right, visible: false);
        Col("plays", "Plays", DpiW(55), g => N(() => (int?)g.PlayCount), g => { var p = Safe(() => g.PlayCount); return p > 0 ? p.ToString() : ""; }, HorizontalAlignment.Right);
        Col("playtime", "Play Time", DpiW(80), g => Safe(() => (object)g.PlayTime), g => FormatPlayTime(Safe(() => g.PlayTime)), HorizontalAlignment.Right, visible: false);
        Col("dateadded", "Date Added", DpiW(100), g => Safe(() => (object)g.DateAdded), g => DateStr(Safe(() => (object)g.DateAdded)), HorizontalAlignment.Right, visible: false);
        Col("datemodified", "Date Modified", DpiW(110), g => Safe(() => (object)g.DateModified), g => DateStr(Safe(() => (object)g.DateModified)), HorizontalAlignment.Right, visible: false);
        Col("lastplayed", "Last Played", DpiW(100), g => Safe(() => (object)g.LastPlayedDate), g => DateStr(Safe(() => (object)g.LastPlayedDate)), HorizontalAlignment.Right, visible: false);
        Col("dbid", "DB Id", DpiW(70), g => N(() => g.LaunchBoxDbId), g => N(() => g.LaunchBoxDbId)?.ToString() ?? "", HorizontalAlignment.Right, visible: false);
        Col("apppath", "Application Path", DpiW(300), g => S(Safe(() => g.ApplicationPath)), g => S(Safe(() => g.ApplicationPath)), visible: false);
        Col("rahash", "RA Hash", DpiW(240), g => g is HostGame hg ? hg.RetroAchievementsHash : "", g => g is HostGame hg ? hg.RetroAchievementsHash : "", visible: false);

        lv.RebuildColumns();

        _sortKeys = new[] { "name", "title", "platform", "developer", "year", "rating", "plays", "dateadded", "datemodified", "lastplayed" };

        lv.SelectionChangedGame += OnGameSelectionChanged;
        lv.GameActivated += LaunchSelected;
        lv.GameRightClicked += OnGameRightClicked;
        lv.ColumnClicked += OnHeaderColumnClicked;
        lv.ColumnChooserRequested += ShowColumnChooser;
        lv.ViewChanged += OnViewChanged;
        lv.SearchForVirtualItem += OnTypeAheadSearch;   // type-to-jump (compare-name prefix)
        return lv;
    }

    private void OnViewChanged()
    {
        if (_count != null) _count.Text = $"{_games.VisibleGames.Count} / {_games.TotalCount} games";
        if (_posterMode) RefreshPoster();
    }

    // ── ExtendDB / parental indicators ─────────────────────────────────────────
    // Reflect ExtendDB's presence and parental-control state into the toolbar:
    //   • "ExtendDB" label — shown whenever the plugin is loaded.
    //   • padlock — shown when parental control is CONFIGURED; closed (amber) when the
    //     session is locked (restrictions enforced), open (grey) when unlocked. Mirrors
    //     launchbox-web's lock indicator. Hidden entirely when parental is not configured.
    private void RefreshExtendDbIndicators()
    {
        if (_extDbInd == null || _parentalInd == null) return;

        bool ext = false;
        try { ext = GameCacheBridge.ExtendDbPresent; } catch { }
        _extDbInd.Visible = ext;

        bool show = false, locked = false;
        try { show = ParentalBridge.Enabled; locked = ParentalBridge.Locked; } catch { }
        _parentalInd.Visible = show;
        if (show)
        {
            _padlockClosed ??= GlyphPadlock(true);
            _padlockOpen ??= GlyphPadlock(false);
            _parentalInd.Image = locked ? _padlockClosed : _padlockOpen;
            _parentalInd.ToolTipText = locked
                ? "Parental control ACTIVE (locked) — restricted categories and games are hidden"
                : "Parental control unlocked";
        }
    }

    // ExtendDB parental lock/unlock fired (rare under LiteBox, but keep it live): refresh the
    // snapshot, rebuild the tree (hidden nodes drop), re-filter the list and update the padlock.
    private void OnParentalStateChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)OnParentalStateChanged); } catch { } return; }
        try
        {
            ParentalBridge.Refresh();
            object keep = _currentNode;
            if (keep != null && keep is not AllNode && ParentalHidesNode(keep)) keep = AllNode.Instance;
            PopulateSources();                                  // recomputes hidden set + drops hidden nodes
            _currentNode = null;                                // force the re-fill (LoadNode guards same-node)
            object sel = keep ?? AllNode.Instance;
            if (_treeNodeMap.TryGetValue(sel, out var tn)) _sources.SelectedNode = tn;   // may fire AfterSelect → LoadNode
            LoadNode(sel);                                      // guaranteed re-fill (no-op if the line above already did)
            RefreshExtendDbIndicators();
        }
        catch { }
    }

    // Diagnostic: snapshot the parental-control state (boot + on demand) into litebox-store.log.
    private void LogParentalState(string when)
    {
        try
        {
            StoreTrace.Log($"PARENTAL [{when}] present={ParentalBridge.Present} enabled={ParentalBridge.Enabled} " +
                           $"locked={ParentalBridge.Locked} active={ParentalBridge.Active} forceAll={ParentalBridge.ForceAll} " +
                           $"hiddenPlatforms=[{string.Join(", ", _parentalHiddenPlatforms)}]");
        }
        catch (Exception ex) { StoreTrace.Log("PARENTAL EX: " + ex.Message); }
    }

    // True when a tree node (platform / category / playlist) must be hidden by parental control.
    private bool ParentalHidesNode(object n)
    {
        try { return ParentalBridge.Active && ParentalBridge.IsNameHidden(HostPlatformCategory.NodeName(n)); }
        catch { return false; }
    }

    // True when a game must be hidden from the list: force-all, a hidden platform/category, or a
    // disallowed ESRB rating. Loading-vs-display only — the game stays in memory, just not shown.
    private bool ParentalHidesGame(IGame g)
    {
        if (!ParentalBridge.Active) return false;
        if (ParentalBridge.ForceAll) return true;
        string plat = S(Safe(() => g.Platform));
        if (plat.Length > 0 && _parentalHiddenPlatforms.Contains(plat)) return true;
        return !ParentalBridge.IsRatingAllowed(S(Safe(() => g.Rating)));
    }

    // Expand the parental hide-list into the concrete set of platform names whose games must be
    // hidden: a platform listed directly, OR any platform under a hidden category. Built with the
    // tree (roots known) so the per-game filter is a plain HashSet lookup.
    private void RecomputeParentalHiddenPlatforms()
    {
        _parentalHiddenPlatforms.Clear();
        if (!ParentalBridge.Active || _treeRoots == null) return;

        void Walk(object n, bool inherited)
        {
            bool hidden = inherited || ParentalBridge.IsNameHidden(HostPlatformCategory.NodeName(n));
            if (n is IPlatform p)
            {
                if (hidden && !string.IsNullOrEmpty(p.Name)) _parentalHiddenPlatforms.Add(p.Name);
            }
            else if (n is HostPlatformCategory c)
            {
                foreach (var ch in c.Children) Walk(ch, hidden);
            }
        }
        foreach (var r in _treeRoots) { if (r is AllNode) continue; Walk(r, false); }
    }

    private void OnGameRightClicked(IGame[] games, Point screen)
    {
        if (games == null || games.Length == 0) return;
        var menu = BuildGameContextMenu(games);
        if (menu.Items.Count > 0) menu.Show(screen);
    }

    private void OnHeaderColumnClicked(GameColumn col)
    {
        if (col == null) return;
        bool same = string.Equals(_curSortKey, col.Key, StringComparison.OrdinalIgnoreCase);
        _ascending = same ? !_ascending : true;
        _dirBtn.Text = _ascending ? "▲" : "▼";
        int idx = Array.IndexOf(_sortKeys, col.Key);
        if (idx >= 0) { _suppressSort = true; try { _sortCombo.SelectedIndex = idx; } finally { _suppressSort = false; } }
        DoSort(col.Key, _ascending);
    }

    private void ShowColumnChooser(Point screen)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Panel2, ForeColor = Fg };
        foreach (var c in _games.AllColumns)
        {
            var cc = c;
            var it = new ToolStripMenuItem(c.Title) { CheckOnClick = true, Checked = c.Visible };
            it.CheckedChanged += (_, _) =>
            {
                if (!it.Checked && _games.AllColumns.Count(x => x.Visible) <= 1) { it.Checked = true; return; }
                _games.SetColumnVisible(cc, it.Checked);
            };
            menu.Items.Add(it);
        }
        menu.Show(screen);
    }

    private Func<IGame, object> SortGetterFor(string key)
    {
        if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)) return g => CompareName(g);
        var col = _games.AllColumns.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return col?.Sort ?? (g => CompareName(g));
    }

    // ── Right details construction ───────────────────────────────────────────
    private Panel BuildDetails(out HeroPanel hero, out MediaPanel media, out MediaStrip strip,
                               out MetaCard meta, out VndbCard vndb, out TextBox notes)
    {
        // Reserved main-media aspect (width/height): 16:9 by default, or poster 2:3 (INI option).
        _mediaAspect = _cfg.Use169ForMainScreenshot ? (16.0 / 9.0) : (2.0 / 3.0);

        var tlp = new TableLayoutPanel { BackColor = Panel, ColumnCount = 1, RowCount = 8, Padding = new Padding(12) };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));   // hero: fanart + logo + rating/heart
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));   // main media (sized from pane width → _mediaAspect)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));    // mini-thumbnail strip + slim scrollbar (reserved)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));    // meta card (title + platform + expandable fields, wraps)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // VNDB tags box (0 when none; expandable)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // RetroAchievements box (0 when no raid; expandable)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // store achievements box (0 when not a GOG game; expandable)
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // notes (fills the rest)
        _detailGrid = tlp;

        hero = new HeroPanel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        media = new MediaPanel { Dock = DockStyle.Fill, BackColor = Panel };
        strip = new MediaStrip { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 4, 0, 4) };
        meta = new MetaCard { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        vndb = new VndbCard { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        _raCard = new RetroAchievementsCard { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        _raCard.ExpandedChanged = OnRaExpandedToggled;
        _raCard.LayoutChanged = RelayoutDetail;
        _storeAchCard = new StoreAchievementsCard { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        _storeAchCard.ExpandedChanged = OnStoreAchExpandedToggled;
        _storeAchCard.LayoutChanged = RelayoutDetail;
        notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Panel2, ForeColor = Fg };

        tlp.Controls.Add(hero, 0, 0);
        tlp.Controls.Add(media, 0, 1);
        tlp.Controls.Add(strip, 0, 2);
        tlp.Controls.Add(meta, 0, 3);
        tlp.Controls.Add(vndb, 0, 4);
        tlp.Controls.Add(_raCard, 0, 5);
        tlp.Controls.Add(_storeAchCard, 0, 6);
        tlp.Controls.Add(notes, 0, 7);
        return tlp;
    }

    // Minimum height reserved for the notes box before the whole pane starts scrolling.
    private const int MinNotesH = 60;

    // Strip row height: 72 for a game's media thumbs (92×52 + slim bar), 104 for a
    // node's recent-game box thumbs (64×92 portrait, slightly bigger per UX ask).
    private int _stripRowH = 72;

    // Shared tooltip (recent-game thumbs show the game title on hover).
    private readonly ToolTip _tips = new();

    // Lay out the detail grid inside its scroll viewport. The media area fills the pane width
    // (height = width / aspect, capped to part of the viewport) and the meta card is measured
    // from its wrapped content. If hero + media + strip + meta + a minimum notes box fits the
    // viewport, the grid fills it (notes absorbs the slack — no scrollbar). Otherwise the grid
    // grows taller than the viewport and a vertical scrollbar appears; the grid width is reduced
    // by the scrollbar width so it never overlaps the content.
    private bool _inRelayout;
    private void RelayoutDetail()
    {
        var host = _detailHost; var tlp = _detailGrid;
        if (host == null || tlp == null || tlp.RowStyles.Count < 8 || _inRelayout) return;
        _inRelayout = true;
        try { RelayoutDetailCore(host, tlp); }
        finally { _inRelayout = false; }
    }

    private void RelayoutDetailCore(Panel host, TableLayoutPanel tlp)
    {
        int sbw = SystemInformation.VerticalScrollBarWidth;
        int hsbh = SystemInformation.HorizontalScrollBarHeight;
        int fullW = host.ClientSize.Width + (host.VerticalScroll.Visible ? sbw : 0);     // width with NO vertical scrollbar
        int viewH = host.ClientSize.Height + (host.HorizontalScroll.Visible ? hsbh : 0); // height with NO horizontal scrollbar
        if (fullW < 80 || viewH < 80) return;
        int padH = tlp.Padding.Horizontal, padV = tlp.Padding.Vertical;

        // Minimum content height for a given grid width (media capped to the viewport).
        int MinContent(int gridW, out int mediaH, out int metaH, out int vndbH, out int raH, out int storeH)
        {
            int colW = Math.Max(20, gridW - padH);
            mediaH = (int)Math.Round(colW / _mediaAspect);
            int cap = (int)(viewH * 0.62);
            if (cap > 100 && mediaH > cap) mediaH = cap;
            if (mediaH < 90) mediaH = 90;
            metaH = _meta.HeightForWidth(colW);
            vndbH = _vndb.HeightForWidth(colW);
            raH = _raCard?.HeightForWidth(colW) ?? 0;
            storeH = _storeAchCard?.HeightForWidth(colW) ?? 0;
            return padV + 158 + mediaH + _stripRowH + metaH + vndbH + raH + storeH + MinNotesH;
        }

        bool overflow = MinContent(fullW, out _, out _, out _, out _, out _) > viewH;
        int wantW = overflow ? Math.Max(80, fullW - sbw) : fullW;
        int minContent = MinContent(wantW, out int media, out int meta, out int vndb, out int ra, out int store);

        var rsMedia = tlp.RowStyles[1];
        if (rsMedia.SizeType != SizeType.Absolute || Math.Abs(rsMedia.Height - media) > 0.5) { rsMedia.SizeType = SizeType.Absolute; rsMedia.Height = media; }
        var rsStrip = tlp.RowStyles[2];
        if (rsStrip.SizeType != SizeType.Absolute || Math.Abs(rsStrip.Height - _stripRowH) > 0.5) { rsStrip.SizeType = SizeType.Absolute; rsStrip.Height = _stripRowH; }
        var rsMeta = tlp.RowStyles[3];
        if (rsMeta.SizeType != SizeType.Absolute || Math.Abs(rsMeta.Height - meta) > 0.5) { rsMeta.SizeType = SizeType.Absolute; rsMeta.Height = meta; }
        var rsVndb = tlp.RowStyles[4];
        if (rsVndb.SizeType != SizeType.Absolute || Math.Abs(rsVndb.Height - vndb) > 0.5) { rsVndb.SizeType = SizeType.Absolute; rsVndb.Height = vndb; }
        var rsRa = tlp.RowStyles[5];
        if (rsRa.SizeType != SizeType.Absolute || Math.Abs(rsRa.Height - ra) > 0.5) { rsRa.SizeType = SizeType.Absolute; rsRa.Height = ra; }
        var rsStore = tlp.RowStyles[6];
        if (rsStore.SizeType != SizeType.Absolute || Math.Abs(rsStore.Height - store) > 0.5) { rsStore.SizeType = SizeType.Absolute; rsStore.Height = store; }

        // Drive the scroll range, then size the grid to EXACTLY the width the meta/vndb were measured
        // at (wantW). Using host.ClientSize.Width here is unsafe right after changing AutoScrollMinSize:
        // it can still report the previous item's scrollbar state, so the card would render narrower
        // than it was measured → an extra wrapped line overflows the box. wantW already accounts for
        // the scrollbar, and equals the settled client width in both cases.
        host.AutoScrollMinSize = new Size(0, overflow ? minContent : 0);
        int gridW = wantW;
        int gridH = overflow ? minContent : viewH;
        if (tlp.Bounds != new Rectangle(0, 0, gridW, gridH))
            tlp.Bounds = new Rectangle(0, 0, gridW, gridH);
    }

    private void OnMetaExpandedToggled()
    {
        _metaExpanded = _meta.Expanded;   // remember for the next game (and persisted at close)
        RelayoutDetail();
    }

    private void OnVndbExpandedToggled()
    {
        _vndbExpanded = _vndb.Expanded;
        RelayoutDetail();
    }

    private void OnRaExpandedToggled()
    {
        _raExpanded = _raCard.Expanded;
        RelayoutDetail();
    }

    private void OnStoreAchExpandedToggled()
    {
        _storeAchExpanded = _storeAchCard.Expanded;
        RelayoutDetail();
    }

    // Split a GenresString into the plain LB genres and the VNDB tags. VNDB tags are appended
    // to the genre field (same as launchbox-web) as "vndb-cont / X", "vndb-tech / Y",
    // "vndb-ero / Z"; type 0 = content, 1 = tech, 2 = ero. Returned tags are grouped by type.
    private static (string genres, List<(string name, int type)> vndb) ParseGenres(string genresString)
    {
        var reg = new List<string>();
        var cont = new List<string>(); var tech = new List<string>(); var ero = new List<string>();
        if (!string.IsNullOrEmpty(genresString))
        {
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            static string Clean(string s, int n) => s.Substring(n).Trim().TrimStart('/').Trim();
            foreach (var part in genresString.Split(';'))
            {
                var s = part.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("vndb-cont", OIC)) { var t = Clean(s, 9); if (t.Length > 0) cont.Add(t); }
                else if (s.StartsWith("vndb-tech", OIC)) { var t = Clean(s, 9); if (t.Length > 0) tech.Add(t); }
                else if (s.StartsWith("vndb-ero", OIC)) { var t = Clean(s, 8); if (t.Length > 0) ero.Add(t); }
                else reg.Add(s);
            }
        }
        var vndb = new List<(string, int)>();
        foreach (var c in cont) vndb.Add((c, 0));
        foreach (var t in tech) vndb.Add((t, 1));
        foreach (var e in ero) vndb.Add((e, 2));
        return (string.Join("; ", reg), vndb);
    }

    // Small platform icon (Nostalgic Platform Icons pack) for the meta pill; cached per platform.
    private Image PlatformIconImage(string platform)
    {
        if (string.IsNullOrEmpty(platform)) return null;
        if (_platIconCache.TryGetValue(platform, out var img)) return img;
        Image res = null;
        try
        {
            var path = MediaResolver.PlatformIcon(MediaResolver.ImagesRoot, "Platforms", platform);
            if (path != null) res = LoadScaled(path, 18);
        }
        catch { }
        _platIconCache[platform] = res;   // cache even null to avoid repeated disk probes
        return res;
    }

    // ── Sources (LaunchBox-native tree: categories ▸ platforms / playlists) ───
    // Native TreeView. The modern rotating chevrons + dark selection/scrollbars come from the
    // "DarkMode_Explorer" visual style (applied by ApplyDarkScroll), so no custom renderer is needed.
    private TreeView BuildSourceTree()
    {
        // Row height/indent scaled for DPI, and bumped up from the classic-Windows-Explorer-tree
        // density (was a hardcoded, unscaled 26px) to the roomier spacing modern Windows apps use.
        float s = LiteBoxTheme.DpiScale(this);
        var tv = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.None,
            FullRowSelect = true, ShowLines = false, ShowPlusMinus = true, ShowRootLines = true,
            HideSelection = false, ItemHeight = (int)Math.Round(32 * s), Indent = (int)Math.Round(20 * s),
            ImageList = _treeIcons,
        };
        tv.AfterSelect += (_, e) => { if (e.Node?.Tag != null) LoadNode(e.Node.Tag); };
        return tv;
    }

    // ── "Group by" view selector (above the source tree) ─────────────────────
    private System.Windows.Forms.ComboBox BuildViewCombo()
    {
        var cb = new System.Windows.Forms.ComboBox
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Fg,
        };
        foreach (var v in SourceViews.All) cb.Items.Add(v.Label);
        cb.SelectedIndexChanged += (_, _) => OnGroupViewChanged();
        return cb;
    }

    // Reflect _currentView in the combo WITHOUT triggering a rebuild (used while restoring the saved view).
    private void SyncViewCombo()
    {
        if (_viewCombo == null) return;
        int idx = Array.FindIndex(SourceViews.All, v => v.Id == _currentView.Id);
        _suppressViewEvent = true;
        try { _viewCombo.SelectedIndex = Math.Max(0, idx); }
        finally { _suppressViewEvent = false; }
    }

    // User picked a grouping → persist it, rebuild the tree, land on "All".
    private void OnGroupViewChanged()
    {
        if (_suppressViewEvent || _viewCombo == null) return;
        int idx = _viewCombo.SelectedIndex;
        if (idx < 0 || idx >= SourceViews.All.Length) return;
        _currentView = SourceViews.All[idx];
        _cfg.Set("GroupView", _currentView.Id); _cfg.Save();
        _currentNode = null;                 // let LoadNode re-run for the new view's "All"
        PopulateSources();
        if (_treeNodeMap.TryGetValue(AllNode.Instance, out var tn)) { _sources.SelectedNode = tn; try { tn.EnsureVisible(); } catch { } }
        LoadNode(AllNode.Instance);
    }

    private IReadOnlyList<IGame> SafeAllGames()
    {
        try { return (_dm?.GetAllGames() ?? Array.Empty<IGame>()).ToList(); }
        catch { return Array.Empty<IGame>(); }
    }

    private void PopulateSources()
    {
        var roots = new List<object> { AllNode.Instance };
        try { roots.AddRange(_currentView.BuildRoots(_dm, SafeAllGames())); }
        catch { if (_dm is HostDataManagerXml hostDm) roots.AddRange(hostDm.RootNodes); }

        _treeRoots = roots;
        RecomputeParentalHiddenPlatforms();   // expand the hide-list before building the tree / filtering
        BuildTreeIcons(roots);
        _treeNodeMap.Clear();
        _sources.BeginUpdate();
        try
        {
            _sources.Nodes.Clear();
            foreach (var r in roots)
            {
                if (r is not AllNode && ParentalHidesNode(r)) continue;   // parental: drop hidden categories/platforms
                _sources.Nodes.Add(BuildTreeNode(r));
            }
            // Collapsed by default — restoring the saved selection (RestoreSelection)
            // auto-expands just the path to the selected node.
            _sources.CollapseAll();
        }
        finally { _sources.EndUpdate(); }
        // Selection (saved category/game) is restored by RestoreSelection().
    }

    // Build a TreeNode for a source object (Tag = the object), recursing into category children.
    private TreeNode BuildTreeNode(object obj)
    {
        string text = obj is AllNode ? "All Games" : obj is GroupNode gn ? gn.Label : (HostPlatformCategory.NodeName(obj) ?? "");
        string imgKey = _nodeIconKey.TryGetValue(obj, out var k) ? k : "fb_plat";
        var tn = new TreeNode(text) { Tag = obj, ImageKey = imgKey, SelectedImageKey = imgKey };
        _treeNodeMap[obj] = tn;
        if (obj is HostPlatformCategory c)
            foreach (var child in c.Children)
            {
                if (ParentalHidesNode(child)) continue;   // parental: drop hidden child categories/platforms
                tn.Nodes.Add(BuildTreeNode(child));
            }
        else if (obj is GroupNode gc && gc.Children != null)   // 2-level dynamic view (Progress: bucket ▸ leaf)
            foreach (var child in gc.Children) tn.Nodes.Add(BuildTreeNode(child));
        return tn;
    }

    // ── Persistence (human-readable INI, written once at close) ──────────────
    private void SaveAll()
    {
        SaveColumnLayout();
        SaveWindowState();
        _cfg.Set("GroupView", _currentView?.Id ?? SourceViews.DefaultId);
        _cfg.Set("LastCategory", NodeKey(_currentNode) ?? "*");
        var g = _games.SelectedGame;
        _cfg.Set("LastGame", g != null ? S(Safe(() => g.Id)) : "");
        string sortKey = (_sortKeys != null && _sortCombo.SelectedIndex >= 0 && _sortCombo.SelectedIndex < _sortKeys.Length)
                         ? _sortKeys[_sortCombo.SelectedIndex] : "name";
        _cfg.Set("SortColumn", sortKey);
        _cfg.SetBool("SortAsc", _ascending);
        _cfg.SetBool("MetaExpanded", _metaExpanded);
        _cfg.SetBool("VndbExpanded", _vndbExpanded);
        _cfg.SetBool("RaExpanded", _raExpanded);
        _cfg.SetBool("StoreAchExpanded", _storeAchExpanded);
        SaveSplitters();
        _cfg.Save();
    }

    // Pane widths persisted as a fraction (per-mille) of each splitter's width, so they restore
    // proportionally regardless of the window size at next launch.
    private void SaveSplitters()
    {
        int Permille(SplitContainer sc) => sc != null && sc.Width > 0
            ? Math.Max(0, Math.Min(1000, (int)Math.Round(sc.SplitterDistance * 1000.0 / sc.Width))) : 0;
        int left = Permille(_outerSplit), mid = Permille(_innerSplit);
        if (left > 0) _cfg.SetInt("SplitLeftPermille", left);
        if (mid > 0) _cfg.SetInt("SplitMidPermille", mid);
    }

    private static void SetSplitFraction(SplitContainer sc, double frac)
    {
        if (sc == null || sc.Width <= 0) return;
        int min = sc.Panel1MinSize;
        int max = sc.Width - sc.Panel2MinSize - sc.SplitterWidth;
        if (max <= min) return;
        int d = Math.Max(min, Math.Min(max, (int)Math.Round(frac * sc.Width)));
        try { sc.SplitterDistance = d; } catch { }
    }

    // Col.<key> = <width>,<visible 0/1>,<displayIndex or -1>
    private void SaveColumnLayout()
    {
        _games.SyncFromUi();
        foreach (var c in _games.AllColumns)
        {
            int di = c.Visible ? c.SavedDisplayIndex : -1;
            // The Stretch column's width is runtime-computed (GameListView.StretchColumn fills
            // whatever the others don't use) and gets overwritten on every resize/rebuild anyway -
            // persisting it would just save whatever transient size the window happened to be at
            // closing time (e.g. a degenerate near-zero width if closed while minimized), which is
            // never a real user preference. Save 0 so RestoreColumnLayout's "w > 0" check skips it.
            int w = c.Stretch ? 0 : c.Width;
            _cfg.Set("Col." + c.Key, $"{w},{(c.Visible ? 1 : 0)},{di}");
        }
    }

    private void RestoreColumnLayout()
    {
        foreach (var c in _games.AllColumns)
        {
            var v = _cfg.Get("Col." + c.Key);
            if (string.IsNullOrEmpty(v)) continue;   // no saved entry → keep the column's defaults
            var p = v.Split(',');
            if (p.Length >= 1 && int.TryParse(p[0], out var w) && w > 0) c.Width = w;
            if (p.Length >= 2) c.Visible = p[1] == "1";
            c.SavedDisplayIndex = (p.Length >= 3 && int.TryParse(p[2], out var d) && d >= 0) ? d : -1;
        }
        try { _games.RebuildColumns(); } catch { }   // applies visibility + saved display order
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
            if (_cfg.GetBool("WinMax", false))
            {
                // This runs from the constructor, before the form has a real handle or has been
                // resolved to an actual monitor. Maximizing THIS early can resolve against stale
                // screen metrics rather than the current monitor's real work area, producing a
                // "maximized" window that visibly doesn't fill the screen. Load (not Shown) is
                // early enough to fix that - the handle exists and Bounds is already applied by
                // then - and, critically, it fires BEFORE the separate Load handler below that
                // restores the splitter/pane fractions from their saved permille. That handler
                // computes each pane's pixel width from the CURRENT container Width, so the
                // window must already be at its final (maximized) size when it runs, or the
                // panes end up sized for the small pre-maximize window instead.
                Load += (_, _) => { if (WindowState != FormWindowState.Maximized) WindowState = FormWindowState.Maximized; };
            }
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
    // ── Global options window ────────────────────────────────────────────────
    // Sections + option bindings (Host/Options). Storage today = LiteBox.ini;
    // the Pause options are slated to migrate to the LB-wide settings layer
    // (LB-compatible) — only their Get/Set bindings will change.
    // Options → Plugins : a checkbox per folder under <LB>\Plugins. Replaces
    // whitelist.txt. Default (never configured) = every present folder checked.
    // Changes are written to LiteBox.ini and take effect on the next start
    // (plugins load once at boot), so we warn on Apply when the set changed.
    private (Control panel, Action apply) BuildPluginsSection()
    {
        var Bg   = Color.FromArgb(30, 30, 30);
        var Fg   = Color.FromArgb(222, 222, 222);
        var SubFg = Color.FromArgb(150, 150, 152);
        var Warn = Color.FromArgb(225, 175, 95);

        var panel = new Panel { BackColor = Bg, Dock = DockStyle.Fill };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Bg, Padding = new Padding(0, 6, 0, 0),
        };

        var note = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 52, ForeColor = Warn, BackColor = Bg,
            Padding = new Padding(2, 2, 2, 8), Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            Text = "Plugins to load (subfolders of " + (HostBoot.PluginsRoot ?? @"<LB>\Plugins") + ").\r\n"
                 + "Changes apply on the next LiteBox restart.",
        };

        string root = HostBoot.PluginsRoot ?? "";
        var folders = HostBoot.ListPluginFolders(root);
        var enabled = _cfg.GetEnabledPluginsOrNull();          // null ⇒ all (never configured)
        bool defaultAll = enabled == null;
        var enabledSet = new HashSet<string>(enabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        var checks = new List<CheckBox>();
        if (folders.Count == 0)
        {
            flow.Controls.Add(new Label { AutoSize = true, ForeColor = SubFg, Margin = new Padding(2, 6, 2, 2),
                Text = "No plugin folders found in " + root });
        }
        foreach (var f in folders)
        {
            var cb = new CheckBox
            {
                Text = f, AutoSize = true, ForeColor = Fg, Margin = new Padding(2, 5, 2, 5),
                Checked = defaultAll || enabledSet.Contains(f),
            };
            checks.Add(cb);
            flow.Controls.Add(cb);
        }

        panel.Controls.Add(flow);
        panel.Controls.Add(note);   // Dock=Top → sits above the Fill flow

        var initial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cb in checks) if (cb.Checked) initial.Add(cb.Text);

        Action apply = () =>
        {
            var sel = new List<string>();
            foreach (var cb in checks) if (cb.Checked) sel.Add(cb.Text);
            _cfg.SetEnabledPlugins(sel);   // persisted by OptionsWindow.ApplyFinished → _cfg.Save()

            var now = new HashSet<string>(sel, StringComparer.OrdinalIgnoreCase);
            if (!now.SetEquals(initial))
            {
                MessageBox.Show(this,
                    "The enabled plugins have changed.\nRestart LiteBox to apply.",
                    "LiteBox — Plugins", MessageBoxButtons.OK, MessageBoxIcon.Information);
                initial.Clear(); initial.UnionWith(now);   // don't repeat on a 2nd Apply
            }
        };
        return (panel, apply);
    }

    // ── RetroAchievements scan — data-side helpers ──────────────────────────────────────────────
    // The UI (platform picker + Lite/Full buttons) lives in the LB · Integrations → RetroAchievements
    // tab (LbGlobalOptions); MainWindow only provides the platform list + the scan launcher (they need
    // the data manager), handed over via Options.RaScanHook. RunRaScan enumerates on the UI thread,
    // shows the modal progress dialog, then flushes.

    private System.Collections.Generic.IEnumerable<string> RaPlatformNamesSorted()
    {
        var names = new System.Collections.Generic.List<string>();
        try { foreach (var p in _dm.GetAllPlatforms() ?? Array.Empty<IPlatform>()) { try { if (!string.IsNullOrEmpty(p?.Name)) names.Add(p.Name); } catch { } } } catch { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private System.Collections.Generic.List<IGame> RaGatherGames(string sel)
    {
        var list = new System.Collections.Generic.List<IGame>();
        try
        {
            foreach (var p in _dm.GetAllPlatforms() ?? Array.Empty<IPlatform>())
            {
                if (p == null) continue;
                string name = null; try { name = p.Name; } catch { }
                if (!string.IsNullOrEmpty(sel) && sel != Options.RaScanHook.AllPlatforms && !string.Equals(name, sel, StringComparison.OrdinalIgnoreCase)) continue;
                IGame[] gs = null; try { gs = p.GetAllGames(true, true); } catch { }
                if (gs != null) foreach (var g in gs) if (g != null) list.Add(g);
            }
        }
        catch { }
        return list;
    }

    private void RunRaScan(string sel, bool full)
    {
        try
        {
            var games = RaGatherGames(sel);
            if (games.Count == 0) { MessageBox.Show(this, "No games found for this selection.", "RetroAchievements"); return; }
            using var f = new Ra.RaScanProgress(games, full, string.IsNullOrEmpty(sel) ? Options.RaScanHook.AllPlatforms : sel);
            f.ShowDialog(this);
            (_dm as HostDataManagerXml)?.FlushIfSafe();
        }
        catch (Exception ex) { MessageBox.Show(this, "Scan failed: " + ex.Message, "RetroAchievements"); }
    }

    private Options.OptionsWindow BuildOptionsWindow()
    {
        var w = new Options.OptionsWindow("LiteBox — Options");
        w.ApplyFinished = () => _cfg.Save();

        w.AddSection("General", new[]
        {
            Options.OptionItem.Toggle("General", "Read-only (never write to the LaunchBox files)",
                () => _cfg.ReadOnly, v => _cfg.ReadOnly = v,
                "When on, every editor that writes to the LaunchBox XMLs stays locked. LiteBox.ini itself is always writable.",
                applyLive: () => { if (_dm is HostDataManagerXml hdm) hdm.ReadOnly = _cfg.ReadOnly; }),
            Options.OptionItem.Toggle("General", "Show \"game running\" screen on launch",
                () => _cfg.ShowGameRunningScreen, v => _cfg.ShowGameRunningScreen = v),
            Options.OptionItem.Toggle("General", "Unload the game list while a game runs",
                () => _cfg.UnloadListDuringGame, v => _cfg.UnloadListDuringGame = v,
                "Frees the list's memory during the game and reloads it on exit."),
            Options.OptionItem.Toggle("General", "Store games: use window-focus exit fallback",
                () => _cfg.StoreExitFocusFallback, v => _cfg.StoreExitFocusFallback = v,
                "Off (default): a GOG/Steam/Epic game's exit is detected only from its install-folder "
                + "process — robust, works on a 2nd monitor. On: also fall back to the window-focus signal "
                + "when no install-folder process is ever seen (older, flakier). Applies to the next launch."),
            Options.OptionItem.Toggle("General", "Store games: close the store client on game exit",
                () => _cfg.KillStoreLauncherAfterGame, v => _cfg.KillStoreLauncherAfterGame = v,
                "Off (default): the GOG/Steam/Epic/Ubisoft client stays open after a store game exits. "
                + "On: close the store client when the game exits — but only the instance LiteBox started "
                + "(a client you already had running is left alone). Applies to the next launch."),
            Options.OptionItem.Toggle("General", "Store games: close the client even if it was already running",
                () => _cfg.KillStoreLauncherEvenIfPreRunning, v => _cfg.KillStoreLauncherEvenIfPreRunning = v,
                "Only matters when 'close the store client on game exit' is on. Off (default): leave a client "
                + "you already had open before the launch. On: close it too (kill ALL of that store's client "
                + "processes, not just the one LiteBox started)."),
        });

        var (pluginsPanel, applyPlugins) = BuildPluginsSection();
        w.AddSection("Plugins", pluginsPanel, applyPlugins);

        w.AddSection("Display", new[]
        {
            Options.OptionItem.Toggle("Display", "Use 16:9 for the main media (else poster ratio)",
                () => _cfg.Use169ForMainScreenshot, v => _cfg.Use169ForMainScreenshot = v,
                applyLive: () => { _mediaAspect = _cfg.Use169ForMainScreenshot ? (16.0 / 9.0) : (2.0 / 3.0); RelayoutDetail(); }),
            Options.OptionItem.Toggle("Display", "Use the image cache (degraded thumbnails)",
                () => _cfg.UseImageCache, v => _cfg.UseImageCache = v,
                applyLive: () => _useImageCache = _cfg.UseImageCache),
            Options.OptionItem.Toggle("Display", "Poster grid: legacy owner-draw rendering (needs restart)",
                () => _cfg.GetBool("PosterOwnerDraw", false), v => _cfg.SetBool("PosterOwnerDraw", v),
                "Off (default): the poster grid uses a native image list — the control scrolls and draws the "
                + "tiles itself, so a held arrow key stays smooth even in huge views. On: the previous owner-draw "
                + "renderer (custom rounded selection + hover grow, but can stutter on a long held scroll). "
                + "Takes effect after restarting LiteBox."),
            Options.OptionItem.Toggle("Display", "Use game cache (when ExtendDB absent)",
                () => _cfg.UseGameCache, v => _cfg.UseGameCache = v,
                "Builds an in-memory media cache (Everything-backed) when the ExtendDB plugin isn't loaded.",
                applyLive: ApplyGameCacheOption),
            Options.OptionItem.Toggle("Display", "Unload the game cache while a game runs",
                () => _cfg.UnloadGameCacheDuringGame, v => _cfg.UnloadGameCacheDuringGame = v),
        });

        w.AddSection("Pause screen", new[]
        {
            Options.OptionItem.Toggle("Pause", "Enable pause screens",
                () => _cfg.GetBool("PauseEnabled", true), v => _cfg.SetBool("PauseEnabled", v),
                "Master switch. Each emulator (and each game) can still opt out individually."),
            Options.OptionItem.Text("Pause", "Pause hotkey",
                () => _cfg.Get("PauseHotkey", "Pause"), v => _cfg.Set("PauseHotkey", v),
                "Global hotkey opening the pause screen, e.g. Pause, F12, Ctrl+F12, Ctrl+Shift+P. Applies to the next launch."),
            Options.OptionItem.Choice("Pause", "Pause mode", new[] { "legacy", "advanced" },
                () => _cfg.Get("PauseMode", "legacy"), v => _cfg.Set("PauseMode", v),
                "legacy = LaunchBox-style native overlay. advanced = LiteBox WebView mode (not implemented yet — falls back to legacy)."),
        });

        // LiteBox-local caches — a maintenance button (always enabled, even in read-only: it only
        // touches LiteBox's own Core cache folders, never the LaunchBox files).
        w.AddSection("Caches", BuildCachesSection());

        // LaunchBox GLOBAL settings (Settings.xml, write-back via the op-log +
        // scoped flush after the window closes). Greyed out in read-only mode.
        if (_dm is HostDataManagerXml hdm2)
        {
            // Hand the RA scan over to the LB · Integrations → RetroAchievements tab. Greyed out (Available
            // = false) when ExtendDB is resolving RA itself — it takes over and the manual scan is disabled.
            var raScan = new Options.RaScanHook
            {
                Available = !Media.RomBridge.RaActive,
                Configured = Ra.RaService.Configured,
                Platforms = RaPlatformNamesSorted,
                Run = RunRaScan,
                RollingRefresh = _cfg.RaStartupRollingRefresh,
                SetRollingRefresh = v => { _cfg.RaStartupRollingRefresh = v; _cfg.Save(); },
                OpenMapping = () => { try { using var d = new Ra.RaMappingDialog(RaPlatformNamesSorted()); d.ShowDialog(this); } catch (Exception ex) { Console.WriteLine("[ra-lite] mapping dialog: " + ex.Message); } },
            };
            Options.LbGlobalOptions.AddSections(w, hdm2.LbSettings, hdm2.ReadOnly, raScan);
        }

        // Danger zone — full self-uninstall. Last section.
        w.AddSection("Uninstall LiteBox", BuildUninstallSection());

        return w;
    }

    // ── LiteBox cache maintenance (Options → Caches) ─────────────────────────────────────────
    // Achievement caches LiteBox keeps under Core\litebox\ : the normalised JSON (ra-cache / store-ach-cache)
    // and the downloaded badge images (ra-badges / store-ach-badges).
    private static readonly string[] _achCacheDirs = { "ra-cache", "ra-badges", "store-ach-cache", "store-ach-badges" };

    private static (int files, long bytes) AchCacheSize()
    {
        int f = 0; long b = 0;
        foreach (var d in _achCacheDirs)
        {
            try
            {
                var dir = Path.Combine(LiteBoxPaths.Data, d);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                { f++; try { b += new FileInfo(file).Length; } catch { } }
            }
            catch { }
        }
        return (f, b);
    }

    private static void ClearAchCache()
    {
        foreach (var d in _achCacheDirs)
            try { var dir = Path.Combine(LiteBoxPaths.Data, d); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
    }

    private Control BuildCachesSection()
    {
        var p = new Panel { BackColor = Bg, AutoScroll = true };
        var title = new Label { Text = "Achievements cache", Location = new Point(4, 8), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
        var desc = new Label
        {
            Text = "RetroAchievements + GOG/Steam achievement data and downloaded badge images "
                 + "(Core\\ra-cache, ra-badges, store-ach-cache, store-ach-badges). Clearing forces a fresh "
                 + "fetch the next time you view a game.",
            Location = new Point(4, 32), AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f),
        };
        var size = new Label { Location = new Point(4, 84), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var btn = new Button
        {
            Text = "Clear achievements cache", Location = new Point(4, 108), Size = new Size(210, 28),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 },
            Font = new Font("Segoe UI", 9f),
        };
        void RefreshSize()
        {
            var (f, b) = AchCacheSize();
            size.Text = f == 0 ? "Cache is empty." : $"Currently cached: {f} file(s), {b / (1024.0 * 1024.0):0.0} MB.";
            btn.Enabled = f > 0;
        }
        btn.Click += (_, _) =>
        {
            var (f, b) = AchCacheSize();
            if (f == 0) return;
            if (MessageBox.Show(p.FindForm(),
                    $"Delete {f} cached achievement file(s) ({b / (1024.0 * 1024.0):0.0} MB)?\n\n"
                    + "RetroAchievements and GOG/Steam achievements will be re-fetched the next time you view a game.",
                    "Clear achievements cache", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ClearAchCache();
            RefreshSize();
            // Re-load the detail panels for the current selection so the effect shows immediately.
            try { var g = _games?.SelectedGame; if (g != null) ScheduleMedia(g); } catch { }
        };
        RefreshSize();
        p.Controls.Add(title); p.Controls.Add(desc); p.Controls.Add(size); p.Controls.Add(btn);
        return p;
    }

    // Full self-uninstall (Options → Uninstall LiteBox). Red button + confirmation → detached .bat.
    private Control BuildUninstallSection()
    {
        var p = new Panel { BackColor = Bg, AutoScroll = true };
        var title = new Label { Text = "Uninstall LiteBox", Location = new Point(4, 8), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
        var desc = new Label
        {
            Text = "Removes LiteBox completely: LiteBox.exe (Core + root re-launcher), the Core\\litebox\\ data "
                 + "folder, and ThirdParty\\Steam. The ExtendDB plugin and the ThirdParty tools it shares are "
                 + "left untouched unless you tick a box below.",
            Location = new Point(4, 32), AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f),
        };
        var cbThumbs = new CheckBox { Text = "Also delete the shared thumbnail cache (Plugins\\ExtendDB\\cache\\thumbs)", Location = new Point(4, 92), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 8.5f) };
        var cbTp = new CheckBox { Text = "Also remove the shared ThirdParty tools (Everything, ImageMagick, RAHasher)", Location = new Point(4, 116), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 8.5f) };
        var shareNote = new Label { Text = "Both are shared with ExtendDB, which re-creates them on its next run.", Location = new Point(22, 140), AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f) };
        var btn = new Button
        {
            Text = "Uninstall LiteBox", Location = new Point(4, 172), Size = new Size(210, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(150, 40, 40), ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        };
        btn.Click += (_, _) =>
        {
            string extra = (cbThumbs.Checked ? "\n  • the shared thumbnail cache" : "")
                         + (cbTp.Checked ? "\n  • the shared ThirdParty tools (Everything/ImageMagick/RAHasher)" : "");
            if (MessageBox.Show(p.FindForm(),
                    "Uninstall LiteBox now?\n\nLiteBox will close and delete itself. This cannot be undone." + extra,
                    "Uninstall LiteBox", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try { Install.Uninstaller.RunSelfUninstall(cbThumbs.Checked, cbTp.Checked); }   // launches the bat + exits
            catch (Exception ex) { MessageBox.Show(p.FindForm(), "Uninstall failed to start: " + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        p.Controls.Add(title); p.Controls.Add(desc); p.Controls.Add(cbThumbs); p.Controls.Add(cbTp); p.Controls.Add(shareNote); p.Controls.Add(btn);
        return p;
    }

    /// <summary>Live-apply for the "Use game cache" toggle (same behaviour the old
    /// gear menu item had): build or release the host cache, ExtendDB preferred.</summary>
    private void ApplyGameCacheOption()
    {
        bool enable = _cfg.UseGameCache && !LbApiHost.Host.Media.GameCacheBridge.ExtendDbPresent;
        if (enable && !LbApiHost.Host.Gc.HostGameCache.Enabled)
        {
            LbApiHost.Host.Gc.HostGameCache.Enabled = true;
            try { LbApiHost.Host.Media.EverythingSupport.Init(LbApiHost.Host.Media.MediaResolver.LbRoot); } catch { }
            LbApiHost.Host.Gc.HostGameCache.Build();
        }
        else if (!enable && LbApiHost.Host.Gc.HostGameCache.Enabled)
        {
            LbApiHost.Host.Gc.HostGameCache.Enabled = false;
            LbApiHost.Host.Gc.HostGameCache.ClearForMemory();
        }
    }

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
        for (int i = 0; i < _sortKeys.Length; i++)
            if (string.Equals(_sortKeys[i], key, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        _curSortKey = _sortKeys[idx];
        _suppressSort = true;
        try { _sortCombo.SelectedIndex = idx; } finally { _suppressSort = false; }
    }

    private void RestoreSelection()
    {
        object node = AllNode.Instance;
        var savedCat = _cfg.Get("LastCategory");
        if (!string.IsNullOrEmpty(savedCat)) node = FindNodeByKey(savedCat) ?? AllNode.Instance;
        // The saved category may now be parental-hidden (no TreeNode built for it): don't reopen it.
        if (node is not AllNode && ParentalHidesNode(node)) node = AllNode.Instance;

        // Select the node visually (AfterSelect → LoadNode); the explicit LoadNode below is then a
        // no-op via its guard, but kept so a node with no TreeNode still fills the list.
        if (_treeNodeMap.TryGetValue(node, out var tn)) { _sources.SelectedNode = tn; try { tn.EnsureVisible(); } catch { } }
        LoadNode(node);                   // synchronous fill (so the saved game can be selected right after)

        var savedGame = _cfg.Get("LastGame");
        if (!string.IsNullOrEmpty(savedGame))
        {
            var g = _current.FirstOrDefault(x => string.Equals(Safe(() => x.Id), savedGame, StringComparison.OrdinalIgnoreCase));
            if (g != null) { _games.SelectGame(g, true); ShowDetails(g); }
        }
    }

    /// <summary>
    /// Navigates to / selects the game whose IGame.Id is <paramref name="gameId"/>.
    /// If the game isn't in the currently-loaded list, jumps to the "All" node first
    /// (so any owned game is reachable regardless of the current tree filter), then
    /// selects it and shows its details. Returns false when the id is unknown.
    /// Called by HostGameNavBridge for ExtendDB's Similar-Games viewer.
    /// </summary>
    public bool SelectGameById(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;

        IGame game = null;
        try
        {
            var all = _dm?.GetAllGames();
            if (all != null)
                game = all.FirstOrDefault(x => string.Equals(Safe(() => x.Id), gameId, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (game == null) return false;

        try
        {
            if (Array.IndexOf(_current, game) < 0)
            {
                // Not in the current view → switch to "All" (mirrors RestoreSelection:
                // visual select + a direct synchronous LoadNode so the list is filled).
                if (_treeNodeMap.TryGetValue(AllNode.Instance, out var tn))
                {
                    _sources.SelectedNode = tn;
                    try { tn.EnsureVisible(); } catch { }
                }
                LoadNode(AllNode.Instance);
            }
            _games.SelectGame(game, true);
            ShowDetails(game);
            try { Activate(); BringToFront(); } catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Stable key for a tree node, persisted as LastCategory.</summary>
    private static string NodeKey(object node)
    {
        if (node is AllNode) return "*";
        if (node is GroupNode gn) return "G:" + gn.Label;
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
            else if (n is GroupNode gn && gn.Children != null) foreach (var ch in gn.Children) stack.Push(ch);
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
            else if (node is GroupNode gn && gn.Children != null) foreach (var c in gn.Children) Walk(c);
        }
        foreach (var r in roots) Walk(r);
    }

    private string ResolveIcon(object node, string imagesRoot, ref int counter)
    {
        string sub, fallback; string[] names;
        if (node is AllNode) { sub = "Playlists"; names = new[] { "All Games" }; fallback = "fb_play"; }
        // The Nostalgic pack names files after the LEAF (NestedName "Atari Classics"), not the full nested
        // Name ("Arcade Atari Classics") — try NestedName first, then Name.
        else if (node is IPlatformCategory c) { sub = "Platform Categories"; names = IconNames(c.NestedName, c.Name); fallback = "fb_cat"; }
        else if (node is IPlaylist pl) { sub = "Playlists"; names = IconNames(pl.NestedName, pl.Name); fallback = "fb_play"; }
        else if (node is IPlatform p) { sub = "Platforms"; names = IconNames(p.Name); fallback = "fb_plat"; }
        else if (node is GroupNode) return "fb_cat";   // Publisher/Region/Year/… nodes: neutral folder glyph
        else return "fb_plat";

        string path = MediaResolver.PlatformIcon(imagesRoot, sub, names);
        var img = path == null ? null : LoadScaled(path, 22);
        if (img == null) return fallback;
        string key = "n" + counter++;
        _treeIcons.Images.Add(key, img);
        return key;
    }

    // Distinct, non-empty icon-file candidates in priority order (NestedName leaf first, then full Name).
    private static string[] IconNames(params string[] xs)
    {
        var list = new List<string>();
        foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x) && !list.Contains(x)) list.Add(x);
        return list.ToArray();
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

    // Toolbar padlock for the parental indicator. closed = locked (amber, shackle down on both
    // legs); open = unlocked (grey, one leg lifted). 16×16 to match the toolbar ImageScalingSize.
    private static Image GlyphPadlock(bool closed)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color col = closed ? Color.FromArgb(255, 196, 0) : Color.FromArgb(150, 150, 152);   // amber locked / grey unlocked
        using var pen = new Pen(col, 1.6f);
        using var br = new SolidBrush(col);
        if (closed)
        {
            g.DrawArc(pen, 4.5f, 2f, 7, 8, 180, 180);     // shackle: top half-circle
            g.DrawLine(pen, 4.5f, 6f, 4.5f, 8.5f);        // left leg into body
            g.DrawLine(pen, 11.5f, 6f, 11.5f, 8.5f);      // right leg into body
        }
        else
        {
            g.DrawArc(pen, 5.5f, 1f, 7, 8, 150, 180);     // shackle lifted/open
            g.DrawLine(pen, 11.7f, 4.6f, 11.7f, 8.5f);    // only the right leg meets the body
        }
        g.FillRectangle(br, 3, 8, 10, 7);                 // body
        using var hole = new SolidBrush(Color.FromArgb(30, 30, 30));
        g.FillEllipse(hole, 7, 10, 2, 2);                 // keyhole
        return bmp;
    }

    // OLV coalesces SelectionChanged (fires ~½s after SetObjects), so a node
    // click would otherwise clear the pane just after ShowNodeDetails filled it.
    // When nothing is selected in the list, keep showing the current node.
    private void OnGameSelectionChanged()
    {
        // Hand the selection to the serialized loader instead of loading on the UI thread (or spawning
        // one parallel load per row — which floods the UI thread with image-decode continuations and
        // freezes the list while an arrow key is held). The loader shows the base thumb tracking the
        // scroll (one image at a time, latest-wins) and lands the full detail pane once it settles.
        if (_games.SelectedGame is IGame g) { RequestDetail(g); LedBlinky.GameSelect(g); }   // "9" — highlight → light this game's controls
        else if (!ReferenceEquals(_detailsShown, _currentNode)) ShowNodeDetails(_currentNode);
    }

    private void LoadNode(object node)
    {
        // Guard re-selecting the already-loaded node — also stops the coalesced
        // tree SelectionChanged from re-loading (and clobbering a restored game
        // selection) right after RestoreSelection called LoadNode directly.
        if (node == null || ReferenceEquals(node, _currentNode)) return;
        _currentNode = node;
        // LEDBlinky list-change "7 <emu>" (arcade → "MAME"). Real platforms only for now; playlists /
        // groups / All are the unresolved 7-vs-8 case — see LedBlinky.ListChange.
        try { if (node is IPlatform lbPlat) LedBlinky.ListChange(lbPlat.Name); } catch { }
        try
        {
            IEnumerable<IGame> src =
                  node is AllNode ? _dm.GetAllGames()
                : node is GroupNode gn ? gn.Games
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
        if (_games == null || _sortKeys == null) return;
        int idx = _sortCombo != null && _sortCombo.SelectedIndex >= 0 ? _sortCombo.SelectedIndex : 0;
        DoSort(_sortKeys[Math.Min(idx, _sortKeys.Length - 1)], _ascending);
    }

    private void DoSort(string key, bool asc)
    {
        if (_games == null) return;
        _curSortKey = key;
        _games.SortGetter = SortGetterFor(key);
        _games.SortAscending = asc;
        _games.SortGlyphColumn = _games.AllColumns.FirstOrDefault(c =>
            c.Visible && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        ApplyFilter();   // sets the filter predicate + rebuilds the view (single pass)
    }

    private void ApplyFilter()
    {
        if (_games == null) return;
        _games.Games = _current;
        string txt = _search?.Text;
        bool hasTxt = !string.IsNullOrWhiteSpace(txt);
        bool parental = ParentalBridge.Active;   // hide restricted games (kept in memory, just not shown)
        _games.FilterPredicate = (!hasTxt && !parental)
            ? (Func<IGame, bool>)null
            : g =>
            {
                if (parental && ParentalHidesGame(g)) return false;
                if (!hasTxt) return true;
                return Contains(S(Safe(() => g.Title)), txt) || Contains(S(Safe(() => g.Platform)), txt) || Contains(S(Safe(() => g.Developer)), txt);
            };
        _games.RebuildView();   // count + poster updated via ViewChanged
    }

    // ── Poster (grid) view ────────────────────────────────────────────────────
    // A native virtual ListView in LargeIcon view. Each game's tile (box-art "contain" + title +
    // developer, or a grey phantom for missing art) is composited ONCE into a Win32 image list and the
    // NATIVE control renders + scrolls it — no managed per-tile paint, so a held scroll stays smooth.
    // Tiles are built lazily on item retrieval / thumb load; image-list slots recycle LRU.
    private ListView BuildPoster()
    {
        bool od = _posterOwnerDraw;
        if (od) _posterGeom = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(PCellW, PImgH + PLabelH) };
        else _himl = ImageList_Create(PCellW, PImgH + PLabelH, ILC_COLOR32, 0, 64);
        var lv = new ListView
        {
            // NOT docked: LayoutPoster gives it a left margin of (leftover/2) and extends it to the
            // panel's right edge — so icons (left-aligned) start at the centred position, the empty
            // slack falls on the right, and the vertical scrollbar stays at the right edge.
            Dock = DockStyle.None, View = View.LargeIcon, VirtualMode = true, OwnerDraw = od,
            BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.None, MultiSelect = false,
            Visible = false, HideSelection = false, Scrollable = true,
            LargeImageList = od ? _posterGeom : null,
        };
        if (od)
        {
            lv.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem("");   // data comes from DrawPosterItem
            lv.DrawItem += DrawPosterItem;
            lv.MouseMove += OnPosterMouseMove;
            lv.MouseLeave += (_, _) => { if (_posterHot != -1) { int o = _posterHot; _posterHot = -1; InvalidatePosterItem(o); } };
        }
        else
        {
            lv.RetrieveVirtualItem += OnPosterRetrieveItem;   // native: each item carries its image-list slot
        }
        lv.SearchForVirtualItem += OnTypeAheadSearch;   // type-to-jump (compare-name prefix)
        lv.SelectedIndexChanged += (_, _) => OnPosterSelectionChanged();
        lv.ItemActivate += (_, _) => LaunchSelected();
        lv.MouseUp += OnPosterMouseUp;   // right-click → same game context menu as the list
        lv.HandleCreated += (_, _) =>
        {
            if (!od) SendMessage(lv.Handle, LVM_SETIMAGELIST, (IntPtr)LVSIL_NORMAL, _himl);   // our native list (WinForms left null)
            SetIconSpacing(lv, PCellW + PGap, PImgH + PLabelH + PGap);
            EnableListViewDoubleBuffer(lv);
        };
        return lv;
    }

    // Provide the (virtual) item: just an image-list slot — the composited tile carries box + text.
    private void OnPosterRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        int slot = -1;
        var model = PosterModel(e.ItemIndex);
        if (model != null && Guid.TryParse(S(Safe(() => model.Id)), out var id)) slot = SlotFor(model, id);
        e.Item = new ListViewItem("") { ImageIndex = slot };
    }

    private IGame PosterModel(int displayIndex)
    {
        try { return _games.GameAt(displayIndex); }
        catch { return null; }
    }

    private void RefreshPoster()
    {
        if (_poster == null) return;
        int n = 0; try { n = _games.VisibleGames.Count; } catch { }
        try
        {
            // Native mode: drop the virtual item cache. A re-sort/filter changes which game sits at each
            // index, so the control must re-request each item's ImageIndex (slots are keyed by game id,
            // not by index). Reassigning VirtualListSize invalidates that cache (toggle via 0 when the
            // count is unchanged). Owner-draw reads the model per paint, so it just needs the count.
            if (!_posterOwnerDraw && _poster.VirtualListSize == n && n > 0) _poster.VirtualListSize = 0;
            if (_poster.VirtualListSize != n) _poster.VirtualListSize = n;
        }
        catch { }
        LayoutPoster();   // item count changed → vertical scrollbar may toggle → re-layout
        _poster.Invalidate();
    }

    // Position the poster ListView so the icon grid looks centred while the scrollbar stays at the
    // right edge: left margin = leftover/2, width extends to the panel's right edge. Icons left-align
    // → start at the centred position; the slack falls on the right; the scrollbar is at the far right.
    private void LayoutPoster()
    {
        if (_poster == null || !_posterMode) return;
        var parent = _poster.Parent; if (parent == null) return;
        int pw = parent.ClientSize.Width, ph = parent.ClientSize.Height;
        if (pw <= 0 || ph <= 0) return;
        int strideX = PCellW + PGap, strideY = PImgH + PLabelH + PGap;
        int count = _poster.VirtualListSize;
        int sbw = SystemInformation.VerticalScrollBarWidth;

        int cols0 = Math.Max(1, pw / strideX);
        int rows = (count + cols0 - 1) / cols0;
        bool scroll = (long)rows * strideY > ph;        // would the grid overflow vertically?
        int effW = pw - (scroll ? sbw : 0);             // width usable for columns
        int cols = Math.Max(1, effW / strideX);
        int gridW = cols * strideX;
        int left = Math.Max(0, (effW - gridW) / 2);     // shift right by half the slack
        var b = new Rectangle(left, 0, pw - left, ph);  // extend to the right edge (scrollbar there)
        if (_poster.Bounds != b) _poster.Bounds = b;
    }

    private void SetPosterMode(bool on)
    {
        if (_posterMode == on || _poster == null) return;
        _posterMode = on;
        if (_posterBtn != null) _posterBtn.Text = on ? "List View" : "Poster View";   // label = the view you'd switch TO
        _cfg.SetBool("PosterMode", on); _cfg.Save();
        if (on)
        {
            RefreshPoster();
            _games.Visible = false;          // hide the list behind: the poster's left margin would reveal it
            _poster.Visible = true; _poster.BringToFront();
            LayoutPoster();
            try { ApplyDarkScroll(_poster); } catch { }
            try { if (_poster.IsHandleCreated) EnableListViewDoubleBuffer(_poster); } catch { }   // SetWindowTheme can clear ex-styles
            try { ActiveControl = _poster; _poster.Focus(); } catch { }
        }
        else
        {
            _poster.Visible = false; _games.Visible = true; _games.BringToFront();
            try { ActiveControl = _games; _games.Focus(); } catch { }
        }
    }

    private void OnPosterSelectionChanged()
    {
        if (_poster.SelectedIndices.Count == 0) return;
        var m = PosterModel(_poster.SelectedIndices[0]);
        // focus:false — keep keyboard focus on the poster (SelectGame(...,true) would steal it to the
        // hidden list, freezing poster arrow navigation after the first move). Selection still updates
        // _games → OnGameSelectionChanged → ShowDetails + persists LastGame.
        if (m != null) _games.SelectGame(m, false);
    }

    private void OnPosterMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = _poster.GetItemAt(e.X, e.Y);
        if (item == null) return;
        if (!item.Selected) { _poster.SelectedIndices.Clear(); _poster.SelectedIndices.Add(item.Index); }
        var m = PosterModel(item.Index);
        if (m == null) return;
        var menu = BuildGameContextMenu(new[] { m });   // Play / Play With / Play Version + plugin game menus
        if (menu.Items.Count > 0) menu.Show(_poster, e.Location);
    }

    // ── Native image list (comctl32): the control draws + scrolls the tiles itself; we only build each
    // tile bitmap once and hand its slot to the control. ImageList_Replace updates one slot IN PLACE (no
    // handle recreate), unlike the WinForms ImageList. ──────────
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern IntPtr ImageList_Create(int cx, int cy, int flags, int cInitial, int cGrow);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern int ImageList_Add(IntPtr himl, IntPtr hbmImage, IntPtr hbmMask);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool ImageList_Replace(IntPtr himl, int i, IntPtr hbmImage, IntPtr hbmMask);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool ImageList_Destroy(IntPtr himl);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hgdiobj);
    private const int ILC_COLOR32 = 0x20, LVM_SETIMAGELIST = 0x1000 + 3, LVSIL_NORMAL = 0;

    // The image-list slot for a game, building + interning its tile on first use (slots recycle LRU).
    private int SlotFor(IGame model, Guid id)
    {
        if (_slotOf.TryGetValue(id, out int slot)) { TouchSlot(slot); return slot; }
        IntPtr hbm = BuildTileHbm(model, id);
        if (hbm == IntPtr.Zero) return -1;
        if (_slotCount < PosterSlotCap)
        {
            slot = ImageList_Add(_himl, hbm, IntPtr.Zero);
            DeleteObject(hbm);
            if (slot < 0) return -1;
            _slotId.Add(id);                 // slot == _slotId.Count - 1
            _slotCount = _slotId.Count;
        }
        else
        {
            slot = EvictLru();               // far from the on-screen window (cap >> visible) → safe to reuse
            ImageList_Replace(_himl, slot, hbm, IntPtr.Zero);
            DeleteObject(hbm);
            _slotOf.Remove(_slotId[slot]);
            _slotId[slot] = id;
        }
        _slotOf[id] = slot;
        TouchSlot(slot);
        return slot;
    }

    private void TouchSlot(int slot)
    {
        if (_slotNode.TryGetValue(slot, out var node)) _slotLru.Remove(node);
        _slotNode[slot] = _slotLru.AddFirst(slot);   // front = most-recently used
    }

    private int EvictLru()
    {
        int slot = _slotLru.Last.Value;              // back = least-recently used
        _slotLru.RemoveLast();
        _slotNode.Remove(slot);
        return slot;
    }

    // Rebuild + replace a game's tile after its thumb finished loading (no-op if it has no live slot).
    private void RefreshSlot(IGame model, Guid id)
    {
        if (model == null || !_slotOf.TryGetValue(id, out int slot)) return;
        IntPtr hbm = BuildTileHbm(model, id);
        if (hbm == IntPtr.Zero) return;
        ImageList_Replace(_himl, slot, hbm, IntPtr.Zero);
        DeleteObject(hbm);
    }

    // Composite a tile (box image or phantom + title + developer) into a 24bpp GDI bitmap and return its
    // HBITMAP (IntPtr.Zero on failure). 24bpp = no alpha channel, so GDI text renders opaque (a 32bpp
    // ARGB tile would lose the text pixels' alpha and the image list would draw them transparent). The
    // caller adds/replaces it into the image list, then frees the HBITMAP (the list keeps its own copy).
    private IntPtr BuildTileHbm(IGame model, Guid id)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            using var tile = new Bitmap(PCellW, PImgH + PLabelH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var tg = Graphics.FromImage(tile))
            {
                tg.Clear(Panel);
                var imgArea = new Rectangle(0, 0, PCellW, PImgH);
                var img = PosterThumbSync(model, id);         // sync decode if the thumb is on disk; else null + async
                if (img != null)
                {
                    int ix = imgArea.X + (imgArea.Width - img.Width) / 2, iy = imgArea.Bottom - img.Height;
                    tg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    tg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    tg.DrawImage(img, ix, iy, img.Width, img.Height);
                }
                else
                {
                    tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    int pw = (int)(PCellW * 0.78f), ph = (int)(PImgH * 0.92f);
                    var ph_r = new Rectangle((PCellW - pw) / 2, imgArea.Bottom - ph, pw, ph);
                    using var pb = new SolidBrush(Color.FromArgb(65, 67, 75));
                    using var pp = RoundRect(ph_r, 10);
                    tg.FillPath(pb, pp);
                }
                var title = S(Safe(() => model.Title));
                var dev = S(Safe(() => model.Developer));
                var tRect = new Rectangle(0, PImgH + 3, PCellW, 17);
                var dRect = new Rectangle(0, PImgH + 19, PCellW, 15);
                TextRenderer.DrawText(tg, title, Font, tRect, Fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (!string.IsNullOrEmpty(dev))
                    TextRenderer.DrawText(tg, dev, Font, dRect, SubFg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            hbm = tile.GetHbitmap();
        }
        catch { }
        return hbm;
    }

    // ── Legacy owner-draw renderer (opt-in) ───────────────────────────────────
    // GDI BitBlt: copying a prepared tile via GDI is ~10× faster than GDI+ DrawImage. (DeleteObject is
    // declared in the native block above.)
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int cx1, int cy1, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    private const int SRCCOPY = 0x00CC0020, HALFTONE = 4;

    private void DrawPosterItem(object sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics; var b = e.Bounds;
        _panelBrush ??= new SolidBrush(Panel);
        g.FillRectangle(_panelBrush, b);                  // gaps around the tile
        var model = PosterModel(e.ItemIndex);
        if (model == null) return;
        if (!Guid.TryParse(S(Safe(() => model.Id)), out var id)) return;

        bool selected = e.Item != null && e.Item.Selected;
        bool hot = e.ItemIndex == _posterHot;
        int cellX = b.X + (b.Width - PCellW) / 2;
        int cellTop = b.Y + 4;

        IntPtr hbm = GetPosterTileHbm(model, id);   // composited ONCE + cached; the hot path is just a blit
        int th = PImgH + PLabelH;
        if (selected || hot)
        {
            var cardRect = new Rectangle(cellX - 6, cellTop - 4, PCellW + 12, th + 8);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var hl = new SolidBrush(Color.FromArgb(selected ? 26 : 12, 255, 255, 255)))
            using (var path = RoundRect(cardRect, 8))
                g.FillPath(hl, path);
            if (hbm != IntPtr.Zero)   // grow the selected/hot tile 1.045× (StretchBlt, one tile)
            {
                int sw = (int)(PCellW * 1.045f), sh = (int)(th * 1.045f);
                BlitTile(g, hbm, cellX + (PCellW - sw) / 2, cellTop + (th - sh) / 2, sw, sh);
            }
        }
        else if (hbm != IntPtr.Zero)
        {
            BlitTile(g, hbm, cellX, cellTop, PCellW, th);   // fast native 1:1 copy — the hot path
        }
    }

    private void BlitTile(Graphics g, IntPtr hbm, int x, int y, int w, int h)
    {
        if (_posterMemDC == IntPtr.Zero) _posterMemDC = CreateCompatibleDC(IntPtr.Zero);
        IntPtr hdc = g.GetHdc();
        try
        {
            IntPtr oldObj = SelectObject(_posterMemDC, hbm);
            if (w == PCellW && h == PImgH + PLabelH) BitBlt(hdc, x, y, w, h, _posterMemDC, 0, 0, SRCCOPY);
            else { SetStretchBltMode(hdc, HALFTONE); StretchBlt(hdc, x, y, w, h, _posterMemDC, 0, 0, PCellW, PImgH + PLabelH, SRCCOPY); }
            SelectObject(_posterMemDC, oldObj);
        }
        finally { g.ReleaseHdc(hdc); }
    }

    // Composite a tile into a 32bpp GDI bitmap ONCE; painted by BitBlt. Cached by id; rebuilt when the
    // box thumb arrives (DrainPosterDone drops the stale tile). Returns the cached HBITMAP (Zero on fail).
    private IntPtr GetPosterTileHbm(IGame model, Guid id)
    {
        if (_posterTileHbm.TryGetValue(id, out var cachedHbm)) return cachedHbm;
        IntPtr hbm = BuildTileHbm(model, id);
        _posterTileHbm[id] = hbm;
        _posterTileOrder.Enqueue(id);
        while (_posterTileOrder.Count > 600)
        {
            var old = _posterTileOrder.Dequeue();
            if (_posterTileHbm.TryGetValue(old, out var oh)) { if (oh != IntPtr.Zero) DeleteObject(oh); _posterTileHbm.Remove(old); }
        }
        return hbm;
    }

    // Drop a cached composited tile so it rebuilds (e.g. once its box thumb finishes loading).
    private void InvalidatePosterTile(Guid id)
    {
        if (_posterTileHbm.TryGetValue(id, out var h)) { if (h != IntPtr.Zero) DeleteObject(h); _posterTileHbm.Remove(id); }
    }

    private void OnPosterMouseMove(object sender, MouseEventArgs e)
    {
        int idx = _poster.GetItemAt(e.X, e.Y)?.Index ?? -1;
        if (idx != _posterHot)
        {
            int old = _posterHot; _posterHot = idx;
            InvalidatePosterItem(old); InvalidatePosterItem(idx);
        }
    }

    private void InvalidatePosterItem(int index)
    {
        if (index < 0 || _poster == null || !_poster.Visible) return;
        try { if (index < _poster.VirtualListSize) _poster.RedrawItems(index, index, false); } catch { }
    }

    // Decoded box thumb for a tile. When the thumb file is ALREADY on disk (the common case once browsed
    // once) it decodes it SYNCHRONOUSLY here — so the tile is composited once WITH its image and never
    // triggers an async load → DrainPosterDone Invalidate. That async path is what produced the full-grid
    // repaint storm that froze a held scroll in big views. Only a genuinely uncached thumb falls back to async.
    private Image PosterThumbSync(IGame model, Guid id)
    {
        if (_posterBmp.TryGetValue(id, out var bmp)) return bmp;   // already decoded (may be a null sentinel)
        if (_useImageCache)
        {
            string src = DetailSource(model, "Front", () =>
                  Safe(() => model.FrontImagePath) is { Length: > 0 } f ? f : Safe(() => model.Box3DImagePath));
            string cachedFile = string.IsNullOrEmpty(src) ? null
                : ThumbCache.GetCachedOnly(src, ThumbCache.DefaultMaxDim, keepAlpha: false);   // instant: no Magick
            if (cachedFile != null)
            {
                Image img = null;
                try { using var raw = LoadImage(cachedFile); if (raw != null) img = ScaleContain(raw, PCellW, PImgH); } catch { }
                _posterBmp[id] = img;             // cache (same 600-cap eviction as the async path)
                _posterBmpOrder.Enqueue(id);
                while (_posterBmpOrder.Count > 600)
                {
                    var old = _posterBmpOrder.Dequeue();
                    if (_posterBmp.TryGetValue(old, out var ob)) { ob?.Dispose(); _posterBmp.Remove(old); }
                }
                return img;
            }
        }
        QueuePosterThumb(model, id);   // not on disk → async (phantom now, fills in later)
        return null;
    }

    // Request a background thumb load (dedup + LIFO so the newest/visible tiles load first; bounded so a
    // fast scroll never piles up unbounded work). Called from BuildTileHbm on the UI thread.
    private void QueuePosterThumb(IGame model, Guid id)
    {
        bool spawn = false;
        lock (_posterQLock)
        {
            if (!_posterPending.Add(id)) return;             // already queued / loading / awaiting apply
            _posterReq.AddFirst((model, id));                // LIFO: newest at the front
            while (_posterReq.Count > PosterReqCap)          // drop oldest (already scrolled past)
            {
                var stale = _posterReq.Last.Value;
                _posterReq.RemoveLast();
                _posterPending.Remove(stale.id);
            }
            if (_posterActiveWorkers < PosterMaxWorkers) { _posterActiveWorkers++; spawn = true; }
        }
        if (spawn) System.Threading.Tasks.Task.Run(PosterLoadWorker);
    }

    // One pool worker: pop the newest request, decode+scale it (the expensive part) off the UI thread,
    // hand the result to the batched drain, repeat until the queue empties.
    private void PosterLoadWorker()
    {
        while (true)
        {
            IGame model; Guid id;
            lock (_posterQLock)
            {
                if (_posterReq.Count == 0 || IsDisposed || _closing) { _posterActiveWorkers--; return; }
                var node = _posterReq.First.Value;           // LIFO pop
                _posterReq.RemoveFirst();
                model = node.g; id = node.id;
            }

            Image img = null;
            try
            {
                string src = DetailSource(model, "Front", () =>
                      Safe(() => model.FrontImagePath) is { Length: > 0 } f ? f : Safe(() => model.Box3DImagePath));
                if (!string.IsNullOrEmpty(src))
                {
                    // Poster grid: load the SMALL cached thumb ONLY — never the full-res original.
                    // LoadThumbOrFull serves the full original on a cache MISS, so a cold "All games"
                    // scroll would decode hundreds of multi-megapixel bitmaps onto the Large Object Heap
                    // → back-to-back Gen2 GCs that suspend the UI thread → the grid freezes until the key
                    // is released. GetOrCreate makes the 360px thumb via Magick (native downscale, no
                    // managed LOH) on first use — bounded by THIS pool — and the 2nd pass is a cache HIT.
                    string thumb = _useImageCache ? ThumbCache.GetOrCreate(src, ThumbCache.DefaultMaxDim, keepAlpha: false) : null;
                    using var raw = LoadImage(thumb ?? src);   // small thumb — or the original only if the cache/Magick is unavailable
                    if (raw != null) img = ScaleContain(raw, PCellW, PImgH);   // pre-size to the cell once
                }
            }
            catch { img = null; }

            lock (_posterQLock) { _posterDone.Enqueue((model, id, img)); }
            RequestPosterDrain();
        }
    }

    // Coalesce the apply: ONE UI marshal drains ALL ready thumbs, so N completing loads cost ~1
    // BeginInvoke instead of N — this is what stops a fast scroll from flooding/starving the UI thread.
    private void RequestPosterDrain()
    {
        lock (_posterQLock) { if (_posterDrainPending) return; _posterDrainPending = true; }
        try
        {
            if (!IsDisposed && !_closing && IsHandleCreated) BeginInvoke((Action)DrainPosterDone);
            else lock (_posterQLock) { _posterDrainPending = false; }
        }
        catch { lock (_posterQLock) { _posterDrainPending = false; } }
    }

    private void DrainPosterDone()
    {
        lock (_posterQLock) { _posterDrainPending = false; }
        bool any = false;
        while (true)
        {
            (IGame g, Guid id, Image img) item;
            lock (_posterQLock) { if (_posterDone.Count == 0) break; item = _posterDone.Dequeue(); _posterPending.Remove(item.id); }
            if (IsDisposed) { item.img?.Dispose(); continue; }
            if (_posterBmp.TryGetValue(item.id, out var prev) && !ReferenceEquals(prev, item.img)) prev?.Dispose();
            _posterBmp[item.id] = item.img;     // null = "no art" sentinel (draw phantom, don't retry)
            _posterBmpOrder.Enqueue(item.id);
            if (_posterOwnerDraw) InvalidatePosterTile(item.id);   // drop stale tile → DrawPosterItem rebuilds
            else RefreshSlot(item.g, item.id);                     // native: rebuild the (phantom) slot in place
            any = true;
        }
        while (_posterBmpOrder.Count > 600)
        {
            var old = _posterBmpOrder.Dequeue();
            if (_posterBmp.TryGetValue(old, out var ob)) { ob?.Dispose(); _posterBmp.Remove(old); }
        }
        if (any && _posterMode && _poster != null && _poster.Visible) _poster.Invalidate();
    }

    // Scale src to the largest size that fits (maxW × maxH) keeping aspect — done ONCE (bicubic) so
    // the poster can blit it 1:1 at scroll time instead of resampling every frame.
    private static Image ScaleContain(Image src, int maxW, int maxH)
    {
        try
        {
            float ir = (float)src.Width / src.Height, ar = (float)maxW / maxH;
            int w, h;
            if (ir > ar) { w = maxW; h = Math.Max(1, (int)Math.Round(maxW / ir)); }
            else { h = maxH; w = Math.Max(1, (int)Math.Round(maxH * ir)); }
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, w, h);
            return bmp;
        }
        catch { return null; }   // caller disposes the source thumb → never hand it back; phantom instead
    }

    // ── Win32: per-tile spacing (gap) for the LargeIcon poster grid ───────────
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private static void SetIconSpacing(ListView lv, int cx, int cy)
    {
        const int LVM_FIRST = 0x1000, LVM_SETICONSPACING = LVM_FIRST + 53;
        try { SendMessage(lv.Handle, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF))); } catch { }
    }

    // Native double-buffering for the owner-drawn ListView (kills the hover flicker; works with
    // virtual mode + the dark explorer theme, unlike WinForms' DoubleBuffered on a native control).
    private static void EnableListViewDoubleBuffer(ListView lv)
    {
        const int LVM_FIRST = 0x1000, LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54, LVS_EX_DOUBLEBUFFER = 0x00010000;
        try { SendMessage(lv.Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER); } catch { }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = Math.Max(2, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Focus-regained: re-reconcile store install state (debounced) so an install/uninstall done in
    // GOG Galaxy / Steam while LiteBox stayed open is reflected without a restart. Persists the change
    // when safe and rebuilds the current game's Install/Play button — only if something actually changed.
    // Store running-screen exit signal: once LiteBox has lost the foreground (game took over) and then
    // regained it, the game is considered closed (the process watcher reads this for short launches).
    private void StoreLaunch(IGame g)
    {
        _storeLostFocus = false;
        _storeRegainedFocus = false;
        // Exit detection: install-folder process by default. The window-focus
        // fallback is opt-in (StoreExitFocusFallback) — pass no focus callback
        // when it's off so the watcher relies purely on the game process.
        Func<bool> regained = _cfg.StoreExitFocusFallback ? (() => _storeRegainedFocus) : (Func<bool>)null;
        try { HostLaunch.LaunchStore(g, regained, _cfg.KillStoreLauncherAfterGame, _cfg.KillStoreLauncherEvenIfPreRunning); } catch { }
    }

    private void OnActivatedStoreResync()
    {
        if (_storeLostFocus) _storeRegainedFocus = true;   // foreground came back after the game took it
        if (_gameRunning) return;                          // don't re-sync store status while a game runs
        try
        {
            if (_dm is not HostDataManagerXml hdm) return;
            long now = Environment.TickCount64;
            if (now - _lastStoreSyncTick < 1500) { StoreTrace.Log("activated: skipped (debounce)"); return; }   // ignore rapid re-activations
            _lastStoreSyncTick = now;
            StoreTrace.Log($"activated: re-sync (sel='{S(_heroGame?.Title)}')");
            int changed = hdm.SyncStoreInstallStates();
            if (changed <= 0) return;
            hdm.FlushIfSafe();   // persist Installed / ApplicationPath now (no-op if LaunchBox is running)
            if (_heroGame != null && StoreSupport.KindOf(_heroGame) != StoreKind.None)
            {
                _launchButtons?.ShowFor(_heroGame, SafeEmulatorsForPlatform(S(_heroGame.Platform)), SafeAddApps(_heroGame));
                StoreTrace.Log($"activated: rebuilt button for '{S(_heroGame.Title)}' installed={Safe(() => _heroGame.Installed) == true}");
            }
        }
        catch (Exception ex) { StoreTrace.Log("activated EX: " + ex.Message); }
    }

    // Run the poll only while a store game is the current subject (cheap: the whole reconcile is
    // ~15-25ms; the read is skipped when minimized). Stop it for non-store games / tree nodes.
    private void SetStorePoll(bool on)
    {
        if (_storePollTimer == null) return;
        if (on) { if (!_storePollTimer.Enabled) { _storePollTimer.Start(); StoreTrace.Log($"poll START (sel='{S(_heroGame?.Title)}')"); } }
        else if (_storePollTimer.Enabled) { _storePollTimer.Stop(); StoreTrace.Log("poll STOP"); }
    }

    private void StorePollTick()
    {
        try
        {
            if (_gameRunning) return;   // paused during a game launch/run
            if (WindowState == FormWindowState.Minimized) { StoreTrace.Log("poll tick: skipped (minimized)"); return; }
            var g = _heroGame;
            if (g == null || StoreSupport.KindOf(g) == StoreKind.None) { StoreTrace.Log("poll tick: not a store game → stop"); SetStorePoll(false); return; }
            if (_dm is not HostDataManagerXml hdm) return;
            long now = Environment.TickCount64;
            if (now - _lastStoreSyncTick < 1000) { StoreTrace.Log("poll tick: skipped (debounce)"); return; }   // de-dupe vs the focus-regained sync
            _lastStoreSyncTick = now;
            bool before = Safe(() => g.Installed) == true;
            StoreTrace.Log($"poll tick: sel='{S(g.Title)}' kind={StoreSupport.KindOf(g)} installedBefore={before}");
            int changed = hdm.SyncStoreInstallStates(quiet: true);
            bool after = Safe(() => g.Installed) == true;
            StoreTrace.Log($"poll tick: changed={changed} installedAfter={after}");
            if (changed <= 0) return;
            hdm.FlushIfSafe();
            if (_heroGame != null && StoreSupport.KindOf(_heroGame) != StoreKind.None)
            {
                _launchButtons?.ShowFor(_heroGame, SafeEmulatorsForPlatform(S(_heroGame.Platform)), SafeAddApps(_heroGame));
                StoreTrace.Log($"poll tick: REBUILT button for '{S(_heroGame.Title)}' installed={Safe(() => _heroGame.Installed) == true}");
            }
        }
        catch (Exception ex) { StoreTrace.Log("poll tick EX: " + ex.Message); }
    }

    // ── Details rendering ────────────────────────────────────────────────────
    // Direct path (thumb click, restore-on-launch): load the box async and build the pane now.
    // The keyboard/selection path goes through RequestDetail → the serialized loader instead.
    private void ShowDetails(IGame g)
    {
        _detailsShown = g;
        _heroGame = g;
        if (g == null)
        {
            _hero.SetNode("");
            LoadImagesAsync(null, null);
            ScheduleFanart(null, null);
            ClearStrip();
            _meta.Clear(); _vndb.Clear(); _raCard?.HidePanel(); _notes.Text = ""; RelayoutDetail();
            _launchButtons?.HideGame();
            SetStorePoll(false);
            return;
        }

        // Source selection like launchbox-web/bigbox-web: ClearLogo regroupement for the
        // logo, Front for the box (GameCache → same file → shared cache; IO fallback).
        // SetGame (carrying the title fallback) runs BEFORE the async logo load so a game
        // with no clear logo shows its title as text — with the same pulse.
        var (logoSrc, artSrc) = DetailImageSources(g);
        SetHeroGame(g);
        LoadImagesAsync(logoSrc, artSrc);
        PopulateDetailMeta(g);
    }

    // The box + clear-logo source files for a game (same resolution launchbox-web/bigbox-web use).
    private (string logoSrc, string artSrc) DetailImageSources(IGame g)
    {
        string logoSrc = DetailSource(g, "ClearLogo", () => Safe(() => g.ClearLogoImagePath));
        string artSrc = DetailSource(g, "Front", () =>
              Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f
            : Safe(() => g.Box3DImagePath) is { Length: > 0 } b ? b
            : Safe(() => g.ScreenshotImagePath));
        return (logoSrc, artSrc);
    }

    // Hero card title/rating/favorite (cheap — property reads only).
    private void SetHeroGame(IGame g)
    {
        double eff = Safe(() => g.CommunityOrLocalStarRating);
        _hero.SetGame(S(g.Title), eff, Safe(() => g.StarRatingFloat) > 0, Safe(() => g.Favorite));
    }

    // The settle-time detail pane: fanart + thumb-strip schedules, metadata rows, notes, launch
    // buttons, store poll, parental trace. The hero title + main box image are applied by the caller.
    private void PopulateDetailMeta(IGame g)
    {
        ScheduleFanart(g, null);
        ScheduleMedia(g);   // 0.5s later: build the thumb strip + upgrade the main to full

        // Title + platform live in the card; the rest are the expandable rows.
        var rows = new List<(string, string)>();
        void R(string label, string val) { if (!string.IsNullOrWhiteSpace(val)) rows.Add((label, val)); }
        var (plainGenres, vndbTags) = ParseGenres(S(g.GenresString));
        R("Developer", S(g.Developer));
        R("Publisher", S(g.Publisher));
        R("Genre", plainGenres);   // non-VNDB genres only; VNDB tags go to the box below
        R("Released", N(() => g.ReleaseYear)?.ToString());
        R("Players", S(g.PlayMode));
        var rating = Safe(() => g.StarRatingFloat);
        if (rating > 0) R("Rating", rating.ToString("0.#") + " ★");
        var plays = Safe(() => g.PlayCount);
        if (plays > 0) R("Plays", plays.ToString());
        R("Play Time", FormatPlayTime(Safe(() => g.PlayTime)));
        var versions = Safe(() => g.GetAllAdditionalApplications()?.Length);
        if (versions > 0) R("Versions", versions.ToString());
        R("File", Path.GetFileName(S(Safe(() => g.ApplicationPath))));
        _meta.ShowGame(S(g.Title), S(g.Platform), PlatformIconImage(S(g.Platform)), rows);
        _meta.Expanded = _metaExpanded;   // honour the remembered expand state
        _vndb.SetTags(vndbTags);
        _vndb.Expanded = _vndbExpanded;
        _raCard?.HidePanel();   // clean slate at selection — the debounced ScheduleMedia tick (re)fills it from the raid
        _storeAchCard?.HidePanel();   // same: refilled from the store (GOG) at the debounced tick
        // New game → scroll the detail pane to the top BEFORE relaying out. RelayoutDetailCore positions
        // the grid at an absolute (0,0), so it must start from an unscrolled panel; otherwise a tall
        // previous game (e.g. a big achievements grid) leaves a scroll offset and the grid is mispositioned.
        if (_detailHost != null) { try { _detailHost.AutoScrollPosition = new Point(0, 0); } catch { } }
        RelayoutDetail();

        _notes.Text = S(g.Notes).Replace("\n", "\r\n");

        // Launch buttons (Play / Version / ROM) — reuses the same SDK enumeration
        // as the right-click menu; the ROM tier lights up only when ExtendDB is loaded.
        _launchButtons?.ShowFor(g, SafeEmulatorsForPlatform(S(g.Platform)), SafeAddApps(g));
        SetStorePoll(StoreSupport.KindOf(g) != StoreKind.None);   // poll only for GOG/Steam games

        // Diagnostic: why is this game visible under parental control?
        try
        {
            string plat = S(Safe(() => g.Platform));
            StoreTrace.Log($"DETAIL '{S(g.Title)}' plat='{plat}' rating='{S(Safe(() => g.Rating))}' " +
                           $"active={ParentalBridge.Active} ratingAllowed={ParentalBridge.IsRatingAllowed(S(Safe(() => g.Rating)))} " +
                           $"platHidden={(plat.Length > 0 && _parentalHiddenPlatforms.Contains(plat))} hidesGame={ParentalHidesGame(g)}");
        }
        catch { }
    }

    // ── Serialized, latest-wins detail loader ─────────────────────────────────
    // A selection change (keyboard or mouse) hands its game here. A SINGLE background task loads the
    // box thumb one image at a time — never in parallel — and always converges on the latest selection.
    // While an arrow key is held, the base thumb tracks the scroll (transit: image + title only, cheap);
    // when the selection settles (no newer one arrived while the last image loaded) the full pane lands.
    // Applying with a blocking Invoke self-paces the loop to the UI's paint rate, so it can never queue
    // up a backlog of paints that starves keyboard input (the original "scrolls then freezes" bug).
    private void RequestDetail(IGame g)
    {
        if (g == null) return;
        bool start = false;
        lock (_detailLock) { _detailWant = g; if (!_detailRunning) { _detailRunning = true; start = true; } }
        if (start) System.Threading.Tasks.Task.Run(DetailLoop);
    }

    private void DetailLoop()
    {
        while (true)
        {
            IGame g;
            lock (_detailLock) { g = _detailWant; }
            if (g == null || IsDisposed || _closing) { lock (_detailLock) { _detailRunning = false; } return; }

            // Load the box + clear logo on THIS thread — the "load one, wait for it, then the next".
            var (logoSrc, artSrc) = DetailImageSources(g);
            Image logo = LoadThumbOrFull(logoSrc, keepAlpha: true);
            Image art = LoadThumbOrFull(artSrc, keepAlpha: false);

            // Settled = no newer selection arrived while this image loaded. Decided under the lock so a
            // selection landing right now is not lost: it keeps _detailRunning true and we loop to it.
            bool settled;
            lock (_detailLock) { settled = ReferenceEquals(_detailWant, g); if (settled) _detailRunning = false; }

            try
            {
                if (!IsDisposed && !_closing && IsHandleCreated)
                    Invoke((Action)(() =>
                    {
                        if (IsDisposed || !ReferenceEquals(_games.SelectedGame, g)) { logo?.Dispose(); art?.Dispose(); return; }
                        if (settled) ApplyDetails(g, logo, art);   // landed → full pane
                        else ApplyImageTransit(g, logo, art);      // scrolled past → base thumb + title only
                    }));
                else { logo?.Dispose(); art?.Dispose(); }
            }
            catch { logo?.Dispose(); art?.Dispose(); }

            if (settled) return;   // a later selection restarts the loop via RequestDetail
        }
    }

    // Settle: the selection landed here. Images are already decoded (on the loader thread) → applied
    // directly (no re-load, no SetImage(null) flash) and the full pane is built.
    private void ApplyDetails(IGame g, Image logo, Image art)
    {
        _detailsShown = g;
        _heroGame = g;
        ++_detailsLoadToken;        // invalidate any async load/fanart still in flight from a prior detail
        SetHeroGame(g);             // title (text fallback) before the logo
        _hero.SetLogo(logo);
        _media.SetImage(art);
        PopulateDetailMeta(g);
    }

    // Transit: a game merely scrolled past. Update only the base thumb + title/logo (cheap) so images
    // track the scroll; the heavy pane (metadata, buttons, fanart, strip) waits for the settle above.
    private void ApplyImageTransit(IGame g, Image logo, Image art)
    {
        _detailsShown = g;
        _heroGame = g;
        ++_detailsLoadToken;        // cancel a previous settle's fanart/strip still loading, mid-scroll
        SetHeroGame(g);
        _hero.SetLogo(logo);
        _media.SetImage(art);
    }

    // Right pane when a TREE node (category / platform / playlist / All) is selected.
    private void ShowNodeDetails(object node)
    {
        _detailsShown = node;
        _heroGame = null;
        _launchButtons?.HideGame();   // launch group is game-only
        SetStorePoll(false);
        if (node == null || node is AllNode)
        {
            _hero.SetNode(node is AllNode ? "All Games" : "");   // no rating/heart for a node
            LoadImagesAsync(null, null);
            ScheduleFanart(null, node);   // AllNode → default fanart; null → empty pane (no fanart)
            if (node is AllNode) _meta.ShowNode("All Games", new List<string> { $"Total Games: {_current.Length}" });
            else _meta.Clear();
            _vndb.Clear();
            _raCard?.HidePanel();
            _notes.Text = "";
            PopulateNodeRecentStrip(node is AllNode);   // recently played of the node (empty pane → clears)
            RelayoutDetail();
            return;
        }

        _hero.SetNode(HostPlatformCategory.NodeName(node) ?? "");
        LoadImagesAsync(NodeImage(node, clearLogo: true), NodeImage(node, clearLogo: false));
        ScheduleFanart(null, node);
        PopulateNodeRecentStrip(true);   // recent-game box thumbs under the main media

        var bits = new List<string> { $"Total Games: {_current.Length}" };
        if (node is IPlatform p)
        {
            void Add(string l, string v) { if (!string.IsNullOrWhiteSpace(v)) bits.Add($"{l}: {v}"); }
            Add("Developer", Safe(() => p.Developer));
            Add("Manufacturer", Safe(() => p.Manufacturer));
            Add("Release", N(() => p.ReleaseDate?.Year)?.ToString());
        }
        _meta.ShowNode(HostPlatformCategory.NodeName(node) ?? "", bits);
        _vndb.Clear();
        _raCard?.HidePanel();
        RelayoutDetail();
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

    // ── Hero fanart (random background, fades in after ~0.5s) ────────────────
    private static readonly Random _rng = new();

    // launchbox-web's schedulePosterFanart: ~0.5s debounce, then a random background
    // fades in faintly behind the logo. The details token discards a stale load.
    private void ScheduleFanart(IGame g, object node)
    {
        if (_fanartTimer != null) { _fanartTimer.Stop(); _fanartTimer.Dispose(); _fanartTimer = null; }
        // Leaving the previous selection → fade its fanart out now (the new one fades
        // in ~0.5s later once resolved/loaded). Matches launchbox-web's fade-out.
        _hero.FadeOutFanart();
        int token = _detailsLoadToken;
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        _fanartTimer = t;
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            if (ReferenceEquals(_fanartTimer, t)) _fanartTimer = null;
            if (IsDisposed || token != _detailsLoadToken) return;
            // A "subject" is a game or any node (incl. All Games) — only the truly empty pane
            // (no game, no node) shows no fanart. A subject without its own background falls
            // back to the embedded default.
            bool haveSubject = g != null || node != null;
            string src = ResolveFanartSrc(g, node);
            if (string.IsNullOrEmpty(src) && !haveSubject) { _hero.FadeOutFanart(); return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                var img = !string.IsNullOrEmpty(src) ? LoadThumbOrFull(src, keepAlpha: false)   // degraded jpg → light faint bg
                                                     : LoadDefaultFanart();                      // no background → embedded default
                if (img == null && haveSubject) img = LoadDefaultFanart();   // load failed → still try the default
                if (img == null) { try { if (!IsDisposed && token == _detailsLoadToken) BeginInvoke((Action)(() => { if (!IsDisposed && token == _detailsLoadToken) _hero.FadeOutFanart(); })); } catch { } return; }
                try
                {
                    if (!IsDisposed && token == _detailsLoadToken)
                        BeginInvoke((Action)(() => { if (!IsDisposed && token == _detailsLoadToken) _hero.SetFanart(img); else img.Dispose(); }));
                    else img.Dispose();
                }
                catch { img.Dispose(); }
            });
        };
        t.Start();
    }

    // A random background (Background regroupement, else screenshots) for a game —
    // stable per session so revisiting shows the same one; node background otherwise.
    private string ResolveFanartSrc(IGame g, object node)
    {
        try
        {
            if (g != null)
            {
                string key = "G:" + S(Safe(() => g.Id));
                if (_fanartPick.TryGetValue(key, out var cached)) return cached;
                var list = new List<string>();
                string plat = Safe(() => g.Platform);
                if (!string.IsNullOrEmpty(plat) && GameCacheBridge.Ready(plat) && Guid.TryParse(S(Safe(() => g.Id)), out var id))
                {
                    list = GameCacheBridge.AllImagesTypeFirst(plat, id, "Background", 12);
                    if (list.Count == 0) list = GameCacheBridge.AllImagesTypeFirst(plat, id, "Screenshots", 12);
                }
                if (list.Count == 0)
                {
                    var bg = Safe(() => g.BackgroundImagePath); if (!string.IsNullOrEmpty(bg)) list.Add(bg);
                    if (list.Count == 0) { var sh = Safe(() => g.ScreenshotImagePath); if (!string.IsNullOrEmpty(sh)) list.Add(sh); }
                }
                if (list.Count == 0) return null;
                string pick = list[_rng.Next(list.Count)];
                _fanartPick[key] = pick;
                return pick;
            }
            if (node != null && node is not AllNode)
                return NonEmpty(NodeImage(node, clearLogo: false));
        }
        catch { }
        return null;
    }

    // Embedded fallback fanart (defaultFanart.jpg) — used when the selected game/node has no
    // background of its own. Bytes are cached once; each call returns a FRESH Bitmap because
    // HeroPanel.SetFanart takes ownership and disposes it on fade-out.
    private static byte[] _defaultFanartBytes;
    private static byte[] DefaultFanartBytes()
    {
        if (_defaultFanartBytes != null) return _defaultFanartBytes;
        try
        {
            var asm = typeof(MainWindow).Assembly;
            string name = "LbApiHost.defaultFanart.jpg";
            if (Array.IndexOf(asm.GetManifestResourceNames(), name) < 0)
                name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("defaultFanart.jpg", StringComparison.OrdinalIgnoreCase));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                    if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); _defaultFanartBytes = ms.ToArray(); }
        }
        catch { }
        return _defaultFanartBytes ?? Array.Empty<byte>();
    }
    private static Image LoadDefaultFanart()
    {
        var b = DefaultFanartBytes();
        if (b.Length == 0) return null;
        try { using var ms = new MemoryStream(b); using var tmp = Image.FromStream(ms); return new Bitmap(tmp); }
        catch { return null; }
    }

    // Hero interactivity: click a star → set the user rating; click the heart → toggle
    // favorite. Both persist (DataManager.Save) and refresh the list row's cells.
    private void RateHeroGame(int value)
    {
        var g = _heroGame; if (g == null) return;
        Safe(() => g.StarRatingFloat = value);   // → journal (deferred, gated); no immediate XML write
        _hero.SetRating(value, isUser: true);
        Safe(() => _games.RefreshGame(g));
    }

    private void ToggleHeroFavorite()
    {
        var g = _heroGame; if (g == null) return;
        bool nv = !Safe(() => g.Favorite);
        Safe(() => g.Favorite = nv);             // → journal
        _hero.SetFavorite(nv);
        Safe(() => _games.RefreshGame(g));
    }

    // ── Main media (16:9) + mini-thumbnail strip ─────────────────────────────
    // Like launchbox-web's media carousel, but the main starts on the BOX (Front),
    // not a screenshot — in the default list view we don't already see the box. The
    // strip + the degraded→full upgrade of the main happen ~0.5s after selection.
    private void ScheduleMedia(IGame g)
    {
        if (_mediaTimer != null) { _mediaTimer.Stop(); _mediaTimer.Dispose(); _mediaTimer = null; }
        if (_stripRowH != 72) { _stripRowH = 72; RelayoutDetail(); }   // back from a node's taller recent strip
        ClearStrip();   // reserve the strip space, empty, until the deferred load
        int token = _detailsLoadToken;
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        _mediaTimer = t;
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            if (ReferenceEquals(_mediaTimer, t)) _mediaTimer = null;
            if (IsDisposed || token != _detailsLoadToken) return;
            // RA detail panel at the debounced detail-load (not on every selection). LoadRaPanel first runs
            // the plugin's on-select hash/raid heal BLOCKING (so a never-hashed game gets its raid written
            // BEFORE we display from it — fixes the "leave and come back" symptom), then fetches + shows the
            // achievements. No-op without the plugin / RA module / OnSelect mode. Backgrounded inside.
            try { LoadRaPanel(g, token); } catch { }
            try { LoadStoreAchPanel(g, token); } catch { }
            var items = BuildMediaList(g);
            _mediaItems = items; _mediaSel = items.Count > 0 ? 0 : -1;
            if (items.Count > 0) SetMainMedia(items[0], full: true, token);   // upgrade box: degraded → full
            PopulateStrip(items, token);
        };
        t.Start();
    }

    // Loads + shows the RetroAchievements detail card for a game, at the debounced detail-load (NOT on every
    // selection). PURE LiteBox: reads the raid + median commitments from the <Game> XML (GetField), then
    // fetches/caches achievements via the public RA Web API (RaService) — no ExtendDB needed at display time.
    // A fresh cache shows instantly; otherwise a brief "loading" box, the fetch on a bg thread, then the data.
    private void LoadRaPanel(IGame g, int token)
    {
        if (_raCard == null) return;
        var (xmlBeat, xmlMaster) = RaFields.ReadMedians(g);   // fallback only — live medians come from the API
        // Played-since-cached invalidates the cache (your unlock progress changed). Read on the UI thread.
        DateTime lastPlayedUtc;
        try { var lp = g.LastPlayedDate; lastPlayedUtc = lp.HasValue ? lp.Value.ToUniversalTime() : DateTime.MinValue; }
        catch { lastPlayedUtc = DateTime.MinValue; }

        // Live medians (GetGameProgression, cached) take priority; the game XML is the fallback.
        void ShowWith(RaGameCache c)
        {
            _raCard.Show(c, c.beatMin > 0 ? c.beatMin : xmlBeat, c.masterMin > 0 ? c.masterMin : xmlMaster);
            _raCard.Expanded = _raExpanded;
        }

        // Optimistic first paint: raid already on the game + a cache → show now; known raid w/o cache →
        // "loading"; nothing known yet → stay hidden and reveal only if the heal resolves a raid (no
        // loading→hide flicker for non-RA games).
        int raid0 = RaFields.Raid(g);
        RaGameCache cached0 = raid0 > 0 ? RaService.ReadCache(raid0) : null;
        if (cached0 != null) ShowWith(cached0);
        else if (raid0 > 0) _raCard.ShowLoading();
        else _raCard.HidePanel();

        System.Threading.Tasks.Task.Run(() =>
        {
            // 1) Make sure the plugin's on-select hash/raid heal has actually RUN (BLOCKING) so a never-
            //    hashed game gets its raid written BEFORE we read it. No-op without the plugin / RA module /
            //    OnSelect mode. (Slow first time — hashes the ROM — hence off the UI thread.)
            try
            {
                if (Media.RomBridge.RaActive) Media.RomBridge.HealRaSync(g);   // ExtendDB present + RA module on → it owns the hash/raid
                else RaResolveLite.Resolve(g);                                 // ExtendDB absent / RA module off → LiteBox-native fallback
            }
            catch { }
            if (IsDisposed || token != _detailsLoadToken) return;

            // 2) read the now-resolved raid (on the UI thread, matching the host's data-access pattern)
            int raid = 0;
            try { Invoke(new Action(() => raid = RaFields.Raid(g))); } catch { return; }
            if (token != _detailsLoadToken) return;
            if (raid <= 0 || !RaService.Configured)
            {
                try { BeginInvoke(new Action(() => { if (token == _detailsLoadToken) _raCard.HidePanel(); })); } catch { }
                return;
            }

            // 3) fetch/cache achievements + medians (refetch if stale or played since cached), then show
            RaGameCache data = null;
            try { data = RaService.EnsureAndRead(raid, lastPlayedUtc); }
            catch (Exception ex) { Console.WriteLine("[ra] fetch failed: " + ex.Message); }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (token != _detailsLoadToken) return;   // selection moved on
                    if (data != null)
                    {
                        ShowWith(data);
                        RaXmlWriter.Write(g, data);   // persist medians/beaten/cached-date to the <Game> XML (op-log)
                    }
                    else if (cached0 == null) _raCard.HidePanel();
                }));
            }
            catch { }
        });
    }

    // Loads + shows the store-achievements card at the debounced detail-load. PURE LiteBox, per store:
    //   GOG   → Galaxy's LOCAL galaxy-2.0.db (GogAchievements; full detail, whole owned library).
    //   Steam → the Steamworks helper for the private unlock state + localized names, enriched with web
    //           icons/rarity via the API key (SteamAchievements). No public profile needed.
    // Cached per app id, refreshed when played since cached (the RA card's freshness rule). Non-store
    // games (or store games without an id) hide the card.
    private void LoadStoreAchPanel(IGame g, int token)
    {
        if (_storeAchCard == null) return;
        string source = Safe(() => g.Source) ?? "";
        string gogId = Safe(() => (g as ILiteBoxGame)?.GetField("GogAppId")) ?? "";
        string steamId = Safe(() => StoreSupport.SteamAppId(g.ApplicationPath)) ?? "";

        string title;
        Func<StoreAchCache?> readCache;
        Func<DateTime, StoreAchCache?> ensure;
        if (source.Equals("GOG", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(gogId))
        {
            title = "GOG Achievements";
            readCache = () => GogAchievements.ReadCache(gogId);
            ensure = lp => GogAchievements.EnsureAndRead(gogId, lp);
        }
        else if (source.Equals("Steam", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(steamId))
        {
            title = "Steam Achievements";
            readCache = () => SteamAchievements.ReadCache(steamId);
            ensure = lp => SteamAchievements.EnsureAndRead(steamId, lp);
        }
        else { _storeAchCard.HidePanel(); return; }

        // Played-since-cached invalidates the cache (your unlock progress changed). Read on the UI thread.
        DateTime lastPlayedUtc;
        try { var lp = g.LastPlayedDate; lastPlayedUtc = lp.HasValue ? lp.Value.ToUniversalTime() : DateTime.MinValue; }
        catch { lastPlayedUtc = DateTime.MinValue; }

        void ShowWith(StoreAchCache c)
        {
            _storeAchCard.Title = title;
            _storeAchCard.Show(c);
            _storeAchCard.Expanded = _storeAchExpanded;
        }

        // Optimistic first paint from cache: show it if it has achievements, hide if it cached "none"
        // (total 0), else a brief "loading" until the live read lands.
        var cached0 = readCache();
        if (cached0 != null) { if (cached0.total > 0) ShowWith(cached0); else _storeAchCard.HidePanel(); }
        else _storeAchCard.ShowLoading();

        System.Threading.Tasks.Task.Run(() =>
        {
            StoreAchCache data = null;
            try { data = ensure(lastPlayedUtc); }
            catch (Exception ex) { Console.WriteLine("[storeach] fetch failed: " + ex.Message); }
            if (IsDisposed || token != _detailsLoadToken) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (token != _detailsLoadToken) return;   // selection moved on
                    // Fetch landed: show when it has achievements, otherwise hide (0 = this game has none).
                    if (data != null) { if (data.total > 0) ShowWith(data); else _storeAchCard.HidePanel(); }
                    // Fetch failed (null): keep a non-empty cache already shown; otherwise hide (never stay on "loading").
                    else if (!(cached0 != null && cached0.total > 0)) _storeAchCard.HidePanel();
                }));
            }
            catch { }
        });
    }

    // Sets the main media. NOTE: single extension point — a future video item would be
    // detected here and hosted in the 16:9 zone instead of an image.
    private void SetMainMedia(string src, bool full, int token)
    {
        if (_mediaItems != null) { int ix = _mediaItems.FindIndex(s => string.Equals(s, src, StringComparison.OrdinalIgnoreCase)); if (ix >= 0) _mediaSel = ix; }
        HighlightStrip();
        if (string.IsNullOrEmpty(src)) { _media.SetImage(null); return; }
        System.Threading.Tasks.Task.Run(() =>
        {
            var img = full ? LoadImage(src) : LoadThumbOrFull(src, keepAlpha: false);
            try
            {
                if (!IsDisposed && token == _detailsLoadToken)
                    BeginInvoke((Action)(() => { if (!IsDisposed && token == _detailsLoadToken) _media.SetImage(img); else img?.Dispose(); }));
                else img?.Dispose();
            }
            catch { img?.Dispose(); }
        });
    }

    // Media sources for a game, in display order: the box (the main image) first, then
    // the title screenshot(s), the gameplay screenshots, and finally the fanart — each
    // with the normal type/region priority. The lists are resolved IO-side
    // (MediaResolver) so they're identical whether or not ExtendDB's GameCache is active;
    // only the box uses the cache-or-IO "best" pick. NOTE: single extension point — a
    // future video item would be inserted into this order.
    private const int MaxMediaItems = 24;
    private static List<string> BuildMediaList(IGame g)
    {
        var items = new List<string>();
        void Add(string s)
        {
            if (items.Count >= MaxMediaItems) return;
            if (!string.IsNullOrEmpty(s) && !items.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) items.Add(s);
        }
        void AddAll(IEnumerable<string> ss) { if (ss != null) foreach (var s in ss) Add(s); }

        // 1. The box = the main image (best Front; cache when ready, else IO).
        Add(DetailSource(g, "Front", () =>
              Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f : Safe(() => g.Box3DImagePath)));

        string plat = Safe(() => g.Platform);
        string title = S(Safe(() => g.Title));
        if (!string.IsNullOrEmpty(plat) && Guid.TryParse(S(Safe(() => g.Id)), out var id))
        {
            // 2. title screen → 3. gameplay → 4. fanart, all with type/region priority (IO, cache-independent).
            AddAll(MediaResolver.AllOfType(plat, id, title, "Screenshot - Game Title"));
            AddAll(MediaResolver.AllOfType(plat, id, title, "Screenshot - Gameplay"));
            AddAll(MediaResolver.AllOfType(plat, id, title, "Fanart - Background"));
        }
        else
        {
            Add(Safe(() => g.ScreenshotImagePath));
            Add(Safe(() => g.BackgroundImagePath));
        }
        return items;
    }

    private void PopulateStrip(List<string> items, int token)
    {
        if (token != _detailsLoadToken) return;
        ClearStrip();
        foreach (var src in items)
        {
            var captured = src;
            var th = new MediaThumb
            {
                Width = 92, Height = 52, BackColor = Panel,
                Margin = new Padding(0, 0, 6, 0), Cursor = Cursors.Hand,
            };
            th.Click += (_, _) => SetMainMedia(captured, full: true, _detailsLoadToken);
            th.MouseWheel += (_, e) => _strip.WheelScroll(e.Delta);   // wheel over a thumb scrolls the strip
            _strip.Flow.Controls.Add(th);
            System.Threading.Tasks.Task.Run(() =>
            {
                var img = LoadThumbOrFull(captured, keepAlpha: false);
                try
                {
                    if (!IsDisposed && token == _detailsLoadToken)
                        BeginInvoke((Action)(() => { if (!th.IsDisposed && token == _detailsLoadToken) th.SetImage(img); else img?.Dispose(); }));
                    else img?.Dispose();
                }
                catch { img?.Dispose(); }
            });
        }
        _strip.UpdateScroll();
        HighlightStrip();
    }

    private void ClearStrip()
    {
        foreach (Control c in _strip.Flow.Controls) c.Dispose();   // MediaThumb disposes its own image
        _strip.Flow.Controls.Clear();
        _strip.ResetScroll();
    }

    // ── Node "recent games" strip ─────────────────────────────────────────────
    // Under a node's main media (platform / category / playlist / All), show the
    // degraded box thumbs of the node's most recently PLAYED games (up to 7),
    // slightly bigger than the game screenshot thumbs (portrait 64×92 in a 104px
    // row). Clicking a thumb selects that game (list + details). Empty when the
    // node has no played game yet (the row collapses back to the game height on
    // the next game selection via ScheduleMedia).
    private const int NodeRecentMax = 7;
    private void PopulateNodeRecentStrip(bool show)
    {
        ClearStrip();
        int wantH = 72;
        var recent = new List<IGame>();
        if (show)
        {
            recent = _current
                .Select(g => (g, ts: Safe(() => g.LastPlayedDate) ?? DateTime.MinValue))
                .Where(x => x.ts > DateTime.MinValue)
                .OrderByDescending(x => x.ts)
                .Take(NodeRecentMax)
                .Select(x => x.g)
                .ToList();
            if (recent.Count > 0) wantH = 104;
        }
        if (_stripRowH != wantH) { _stripRowH = wantH; RelayoutDetail(); }
        if (recent.Count == 0) return;

        int token = _detailsLoadToken;   // fresh — LoadImagesAsync just bumped it
        foreach (var g in recent)
        {
            var captured = g;
            var th = new MediaThumb
            {
                Width = 64, Height = 92, BackColor = Panel,
                Margin = new Padding(0, 0, 6, 0), Cursor = Cursors.Hand,
            };
            try { _tips.SetToolTip(th, S(Safe(() => captured.Title))); } catch { }
            th.Click += (_, _) => { _games.SelectGame(captured, true); ShowDetails(captured); };
            th.MouseWheel += (_, e) => _strip.WheelScroll(e.Delta);
            _strip.Flow.Controls.Add(th);
            var src = DetailSource(captured, "Front", () =>
                  Safe(() => captured.FrontImagePath) is { Length: > 0 } f ? f
                : Safe(() => captured.Box3DImagePath) is { Length: > 0 } b ? b
                : Safe(() => captured.ScreenshotImagePath));
            if (string.IsNullOrEmpty(src)) continue;
            System.Threading.Tasks.Task.Run(() =>
            {
                var img = LoadThumbOrFull(src, keepAlpha: false);   // degraded thumb cache
                try
                {
                    if (!IsDisposed && token == _detailsLoadToken)
                        BeginInvoke((Action)(() => { if (!th.IsDisposed && token == _detailsLoadToken) th.SetImage(img); else img?.Dispose(); }));
                    else img?.Dispose();
                }
                catch { img?.Dispose(); }
            });
        }
        _strip.UpdateScroll();
    }

    // Highlight the selected mini-thumb: a thin white border (no blue fill on the empty parts).
    private void HighlightStrip()
    {
        var ctrls = _strip.Flow.Controls;
        for (int i = 0; i < ctrls.Count; i++)
            if (ctrls[i] is MediaThumb th) th.Selected = (i == _mediaSel);
        if (_mediaSel >= 0 && _mediaSel < ctrls.Count) _strip.ScrollIntoView(ctrls[_mediaSel]);
    }

    // ── Bulk cache pre-generation ────────────────────────────────────────────
    // The 3 cached thumbnails per game, picked the SAME way as the detail pane /
    // the web: clear logo (WebP), box "Front" (JPEG), main screenshot (JPEG).
    private static string[] ResolveCacheSources(IGame g)
    {
        if (g == null) return null;
        string logo = DetailSource(g, "ClearLogo", () => Safe(() => g.ClearLogoImagePath));
        string box = DetailSource(g, "Front", () =>
              Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f
            : Safe(() => g.Box3DImagePath) is { Length: > 0 } b ? b
            : Safe(() => g.ScreenshotImagePath));
        string shot = DetailSource(g, "Screenshots", () =>
              Safe(() => g.ScreenshotImagePath) is { Length: > 0 } s ? s
            : Safe(() => g.BackgroundImagePath));
        return new[] { logo, box, shot };
    }

    private void GenerateAllCachedImages()
    {
        var games = Safe(() => _dm.GetAllGames()) ?? Array.Empty<IGame>();
        if (games.Length == 0) return;
        using var dlg = new GenerateCacheForm(games, ResolveCacheSources);
        dlg.ShowDialog(this);
    }

    // Generate/load the right-pane images OFF the UI thread (degraded thumbs from the
    // shared cache: logo=WebP w/ alpha, art=JPEG), so selecting a node/game never blocks
    // the game-list paint — only the cheap text is set synchronously. The token discards
    // a stale load if the selection changed before it finished. Args are SOURCE paths.
    private void LoadImagesAsync(string logoSrc, string artSrc)
    {
        int token = ++_detailsLoadToken;
        _media.SetImage(null);
        // No logo source at all → settle now so the title-text fallback shows.
        if (string.IsNullOrEmpty(logoSrc) && string.IsNullOrEmpty(artSrc)) { _hero.SetLogo(null); return; }
        System.Threading.Tasks.Task.Run(() =>
        {
            var logo = LoadThumbOrFull(logoSrc, keepAlpha: true);   // clear logo → WebP/alpha
            var art = LoadThumbOrFull(artSrc, keepAlpha: false);    // main media (box) DEGRADED, instant
            void Apply()
            {
                if (IsDisposed || token != _detailsLoadToken) { logo?.Dispose(); art?.Dispose(); return; }
                _hero.SetLogo(logo);                       // hero owns + pulses the logo
                _media.SetImage(art);                      // degraded box now; upgraded to full after 0.5s
            }
            try { if (!IsDisposed) BeginInvoke((Action)Apply); else { logo?.Dispose(); art?.Dispose(); } }
            catch { logo?.Dispose(); art?.Dispose(); }
        });
    }

    // Cache HIT → the degraded thumbnail (light). MISS → show the FULL original right
    // away (one decode, no wait) and queue the thumb generation in the background, so a
    // fast browse never stalls on Magick and the thumb is ready (HIT) next time.
    private static Image LoadThumbOrFull(string src, bool keepAlpha)
    {
        if (string.IsNullOrEmpty(src)) return null;
        if (!_useImageCache) return LoadImage(src);   // option off → full original, no cache
        var cached = ThumbCache.GetCachedOnly(src, ThumbCache.DefaultMaxDim, keepAlpha);
        if (cached != null) return LoadImage(cached);
        ThumbCache.EnqueueGenerate(src, ThumbCache.DefaultMaxDim, keepAlpha);   // background, for next time
        return LoadImage(src);                                                  // full original, now
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

        // LEDBlinky: select the launched game then Game Start — mirrors LaunchBox ("9" before "3",
        // since "3" is argument-less and lights whatever the last "9" selected).
        LedBlinky.GameSelect(g);
        LedBlinky.GameStart();

        _resumeGameId = g != null ? Safe(() => g.Id) : null;
        _gameRunning = true;
        SetStorePoll(false);   // pause the install-state poll while the game runs (client DB may be mid-write)

        if (_cfg.UnloadListDuringGame)
        {
            LoadImagesAsync(null, null);             // clears + invalidates any in-flight decode
            _games.Games = Array.Empty<IGame>();     // free the row index during the game
            _games.RebuildView();
        }
        if (_cfg.ShowGameRunningScreen) ShowRunningOverlay(g);
    }

    private void OnGameEnded(IGame g)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => OnGameEnded(g))); } catch { } return; }

        LedBlinky.GameStop();   // "4" — fire before the list re-selects a game (which would send a "9")

        _gameRunning = false;   // game over → store status refresh may resume

        HideRunningOverlay();

        if (_cfg.UnloadListDuringGame)
        {
            ApplyFilter();   // _current already reloaded by HostLaunch → Games = _current + rebuild
            IGame target = _resumeGameId == null ? null
                : _current.FirstOrDefault(x => string.Equals(Safe(() => x.Id), _resumeGameId, StringComparison.OrdinalIgnoreCase));
            if (target != null) { _games.SelectGame(target, true); ShowDetails(target); }
            else if (_games.VisibleGames.Count > 0) { _games.SelectFirst(); }
        }
        // Resume the poll if a store game is the current subject (covers UnloadListDuringGame off too).
        SetStorePoll(_heroGame != null && StoreSupport.KindOf(_heroGame) != StoreKind.None);
    }

    private void ShowRunningOverlay(IGame g)
    {
        if (_overlay == null)
        {
            _overlay = new DoubleBufferedPanel { Dock = DockStyle.Fill, Cursor = Cursors.Default };
            _overlay.Paint += PaintOverlay;
            // Manual escape hatch: DOUBLE-click to dismiss if the overlay ever
            // lingers (e.g. a game whose process can't be detected). A single
            // click — typically just bringing LiteBox back to the foreground
            // while the game is still running — must NOT dismiss it.
            _overlay.DoubleClick += (_, _) => HideRunningOverlay();
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
        if (_games.SelectedGame is not IGame g) return;
        var emu = Safe(() => _dm.GetEmulatorById(g.EmulatorId));
        Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, emu, null));
    }

    private ContextMenuStrip BuildGameContextMenu(IGame[] games)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Panel2, ForeColor = Fg };

        // Single-game items (Play / Play With / Play Version / Configure). Everything except Edit is single-only.
        if (games.Length == 1)
        {
            var g = games[0];

            var play = new ToolStripMenuItem("Play") { Font = new Font(Font, FontStyle.Bold) };
            play.Click += (_, _) => LaunchSelected();
            menu.Items.Add(play);

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

        // Edit — single OR multiple: opens the metadata editor for every selected game (the only item
        // that isn't single-only). ◄► walk the visible list when a single game is selected.
        {
            var gs = games;
            var edit = new ToolStripMenuItem(gs.Length > 1 ? $"Edit {gs.Length} Games…" : "Edit…");
            edit.Click += (_, _) => OpenEditGame(gs);
            menu.Items.Add(edit);
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

    // Opens the metadata editor (EditGameWindow) for the selected game(s) — single or multiple. The
    // visible list is passed so the ◄► arrows can walk it in single mode. Honours read-only mode.
    // Refreshes the list + detail on close.
    private void OpenEditGame(IGame[] games)
    {
        if (games == null || games.Length == 0) return;
        try
        {
            bool ro = (_dm as HostDataManagerXml)?.ReadOnly ?? false;
            EditGameWindow.Open(games, _games.VisibleGames, ro, this);
            try { _games.RebuildView(); } catch { }
            RequestDetail(games[0]);
        }
        catch (Exception ex) { Console.WriteLine("[editgame] open failed: " + ex); }
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

    // ── Type-to-jump (incremental search) ─────────────────────────────────────
    // Native virtual-ListView type-ahead: the control accumulates the typed keys (with its own
    // timeout) and raises SearchForVirtualItem; we answer with the index of the first game whose
    // compare-name (article-stripped, alnum, lower) starts with what was typed — so "secre" jumps to
    // "Secret of Mana", consistent with the name sort. Setting e.Index makes the ListView select +
    // scroll to it natively. Shared by the list AND the poster (both mirror _games.VisibleGames).
    private void OnTypeAheadSearch(object sender, SearchForVirtualItemEventArgs e)
    {
        try
        {
            if (!e.IsTextSearch) return;
            string needle = NormalizeTypeAhead(e.Text);
            if (needle.Length == 0) return;
            var view = _games.VisibleGames;
            int n = view.Count;
            if (n == 0) return;
            int start = (e.StartIndex >= 0 && e.StartIndex < n) ? e.StartIndex : 0;
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;                       // wrap so repeating a letter cycles
                if (CompareName(view[i]).StartsWith(needle, StringComparison.Ordinal)) { e.Index = i; return; }
            }
        }
        catch { }
    }

    // Reduce typed text to the same shape as CompareName (lower, letters+digits only) so a prefix
    // matches: spaces/punctuation typed by the user are dropped ("secret o" → "secreto").
    private static string NormalizeTypeAhead(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

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

    // Metadata card under the media: a rounded box holding the title and the platform
    // (icon + name + a rotating chevron, like the source tree). Clicking it expands the
    // remaining fields, one "Label: value" per row. EVERYTHING word-wraps to as many lines
    // as needed (title and every field, collapsed or expanded) — nothing is cut off. Tree
    // nodes reuse it (title + plain wrapped lines, no chevron). Height is measured from the
    // wrapped content at the current width; the expand state lives in MainWindow.
    private sealed class MetaCard : Panel
    {
        private enum Mode { None, Game, Text }
        private Mode _mode = Mode.None;
        private string _title = "", _platform = "";
        private Image _icon;                                   // not owned (cached by MainWindow)
        private (string label, string value)[] _rows = Array.Empty<(string, string)>();
        private string[] _lines = Array.Empty<string>();
        private bool _expanded;
        private readonly Font _titleFont = new Font("Segoe UI Semibold", 12f);

        private const int Pad = 10, Gap = 6, IconSz = 18, ChevW = 16, VMargin = 4;   // VMargin = breathing room above/below the box
        // Wrap to multiple lines; only a single over-long word gets ellipsised as a last resort.
        private const TextFormatFlags Wrap =
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.WordEllipsis | TextFormatFlags.Left | TextFormatFlags.Top;

        public Action ExpandedChanged;

        public MetaCard()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded
        {
            get => _expanded;
            set { if (_expanded != value) { _expanded = value; Invalidate(); } }
        }

        public int DesiredHeight => HeightForWidth(ClientSize.Width);

        // Wrapped height for a given card width (used to lay out the row before the control
        // has that width — measurement must not depend on the control's current bounds).
        public int HeightForWidth(int cardWidth)
        {
            if (_mode == Mode.None) return 0;
            if (cardWidth < 40) return 64;   // not laid out yet → fallback
            return LayoutContent(null, cardWidth) + Pad + VMargin;   // content end + bottom pad + bottom margin
        }

        public void ShowGame(string title, string platform, Image icon, List<(string, string)> rows)
        {
            _mode = Mode.Game; _title = title ?? ""; _platform = platform ?? ""; _icon = icon;
            _rows = (rows ?? new List<(string, string)>()).ToArray();
            Invalidate();
        }

        public void ShowNode(string title, List<string> lines)
        {
            _mode = Mode.Text; _title = title ?? ""; _icon = null; _platform = "";
            _lines = (lines ?? new List<string>()).ToArray();
            Invalidate();
        }

        public void Clear()
        {
            _mode = Mode.None; _icon = null; _title = "";
            _rows = Array.Empty<(string, string)>(); _lines = Array.Empty<string>();
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_mode == Mode.Game) { _expanded = !_expanded; Invalidate(); ExpandedChanged?.Invoke(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _mode == Mode.Game ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_mode == Mode.None) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var box = new Rectangle(0, VMargin, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 2 * VMargin - 1));
            using (var path = Rounded(box, 8))
            {
                using var bg = new SolidBrush(Color.FromArgb(46, 46, 50)); g.FillPath(bg, path);
                using var bd = new Pen(Color.FromArgb(64, 64, 68)); g.DrawPath(bd, path);
            }
            LayoutContent(g, ClientSize.Width);
        }

        // Lays out (and draws when g != null) the title, platform row and fields/lines, all
        // word-wrapped, for a given card width. Returns the y just past the last element.
        private int LayoutContent(Graphics g, int cardWidth)
        {
            int innerW = Math.Max(20, cardWidth - 2 * Pad);
            int x = Pad, y = VMargin + Pad;   // start below the top margin + box padding

            if (!string.IsNullOrEmpty(_title))
                y += DrawWrapped(g, _title, _titleFont, Fg, x, y, innerW);

            if (_mode == Mode.Game)
            {
                y += Gap;
                int chevCol = ChevW + 2;
                int nameX = x + IconSz + 7;
                int nameW = Math.Max(10, innerW - IconSz - 7 - chevCol);
                string name = string.IsNullOrEmpty(_platform) ? "Unknown platform" : _platform;
                if (g != null)
                {
                    if (_icon != null) g.DrawImage(_icon, x, y, IconSz, IconSz);
                    DrawChevron(g, x + innerW - ChevW / 2, y + IconSz / 2, _expanded);
                }
                int nameH = DrawWrapped(g, name, Font, Fg, nameX, y, nameW);
                y += Math.Max(IconSz, nameH);

                if (_expanded && _rows.Length > 0)
                {
                    y += 4;
                    foreach (var (label, value) in _rows)
                    {
                        string lbl = label + ":  ";
                        var lblSz = TextRenderer.MeasureText(lbl, Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding);
                        if (g != null)
                            TextRenderer.DrawText(g, lbl, Font, new Rectangle(x, y, lblSz.Width, lblSz.Height),
                                SubFg, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
                        int vx = x + lblSz.Width;
                        int vh = DrawWrapped(g, value, Font, Fg, vx, y, Math.Max(20, innerW - lblSz.Width));
                        y += Math.Max(lblSz.Height, vh) + 3;
                    }
                }
            }
            else // node text
            {
                y += Gap;
                foreach (var line in _lines)
                    y += DrawWrapped(g, line, Font, SubFg, x, y, innerW) + 2;
            }

            return y;
        }

        // Measures (g == null) or draws a word-wrapped block; returns its height.
        private static int DrawWrapped(Graphics g, string text, Font font, Color color, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var size = TextRenderer.MeasureText(text, font, new Size(w, int.MaxValue), Wrap);
            if (g != null)
                TextRenderer.DrawText(g, text, font, new Rectangle(x, y, w, size.Height), color, Wrap);
            return size.Height;
        }

        private static void DrawChevron(Graphics g, int cx, int cy, bool expanded)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int s = 4;
            using var pen = new Pen(Color.FromArgb(180, 180, 182), 1.8f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            Point[] pts = expanded
                ? new[] { new Point(cx - s, cy - s / 2), new Point(cx, cy + s / 2), new Point(cx + s, cy - s / 2) }
                : new[] { new Point(cx - s / 2, cy - s), new Point(cx + s / 2, cy), new Point(cx - s / 2, cy + s) };
            g.DrawLines(pen, pts);
            g.SmoothingMode = old;
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing) { if (disposing) _titleFont.Dispose(); base.Dispose(disposing); }
    }

    // Box of VNDB tags shown under the meta card (only when the game has any). Tags are small
    // coloured pills grouped by type — content (blue), tech (teal), ero (rose) — matching the
    // launchbox-web colours. Collapsed shows only the first line of pills plus a chevron; clicking
    // expands to all pills (wrapped). Empty (no tags) → zero height. Mirrors MetaCard's box style.
    private sealed class VndbCard : Panel
    {
        private (string name, int type)[] _tags = Array.Empty<(string, int)>();
        private bool _expanded, _expandable;
        public Action ExpandedChanged;

        private const int Pad = 10, VMargin = 4, PillH = 20, PadX = 9, GapX = 6, GapY = 6, ChevW = 16;
        // 0 = content (blue), 1 = tech (teal), 2 = ero (rose) — same hues as the web badges.
        private static readonly Color[] PillBg = { Color.FromArgb(26, 26, 42), Color.FromArgb(26, 32, 32), Color.FromArgb(42, 10, 26) };
        private static readonly Color[] PillFg = { Color.FromArgb(128, 144, 208), Color.FromArgb(96, 176, 160), Color.FromArgb(240, 112, 138) };
        private static readonly Color[] PillBd = { Color.FromArgb(42, 48, 96), Color.FromArgb(42, 64, 64), Color.FromArgb(90, 16, 48) };

        public VndbCard()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded { get => _expanded; set { if (_expanded != value) { _expanded = value; Invalidate(); } } }

        public void SetTags(List<(string, int)> tags) { _tags = (tags ?? new List<(string, int)>()).ToArray(); Invalidate(); }
        public void Clear() { _tags = Array.Empty<(string, int)>(); Invalidate(); }

        public int DesiredHeight => HeightForWidth(ClientSize.Width);
        public int HeightForWidth(int cardWidth)
        {
            if (_tags.Length == 0) return 0;
            if (cardWidth < 40) return PillH + 2 * Pad + 2 * VMargin;
            return LayoutPills(null, cardWidth) + Pad + VMargin;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_expandable) { _expanded = !_expanded; Invalidate(); ExpandedChanged?.Invoke(); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _expandable ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_tags.Length == 0) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var box = new Rectangle(0, VMargin, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 2 * VMargin - 1));
            using (var path = Rounded(box, 8))
            {
                using var bg = new SolidBrush(Color.FromArgb(46, 46, 50)); g.FillPath(bg, path);
                using var bd = new Pen(Color.FromArgb(64, 64, 68)); g.DrawPath(bd, path);
            }
            LayoutPills(g, ClientSize.Width);
        }

        // Lay out (and draw when g != null) the tag pills for a given card width. Collapsed = first
        // line only + chevron; expanded = wrapped. Sets _expandable. Returns bottom y.
        private int LayoutPills(Graphics g, int cardWidth)
        {
            int innerW = Math.Max(20, cardWidth - 2 * Pad);
            _expandable = !FitsOneLine(innerW);
            bool collapsed = _expandable && !_expanded;
            int chev = _expandable ? (ChevW + 4) : 0;
            int x0 = Pad, top = VMargin + Pad;
            int x = x0, y = top, curRight = x0 + innerW - chev;   // first line reserves chevron when expandable
            int fullRight = x0 + innerW;
            for (int i = 0; i < _tags.Length; i++)
            {
                var ts = TextRenderer.MeasureText(_tags[i].name, Font, new Size(int.MaxValue, PillH), TextFormatFlags.NoPadding);
                int pw = ts.Width + 2 * PadX;
                if (x > x0 && x + pw > curRight)
                {
                    if (collapsed) break;                 // collapsed → only the first line
                    x = x0; y += PillH + GapY; curRight = fullRight;
                }
                if (pw > curRight - x0) pw = curRight - x0;   // clamp an over-wide single pill
                if (g != null) DrawPill(g, new Rectangle(x, y, pw, PillH), _tags[i].name, _tags[i].type);
                x += pw + GapX;
            }
            if (_expandable && g != null)
                DrawChevron(g, x0 + innerW - ChevW / 2, top + PillH / 2, _expanded);
            return y + PillH;
        }

        private bool FitsOneLine(int innerW)
        {
            int x = 0;
            foreach (var (name, _) in _tags)
            {
                var ts = TextRenderer.MeasureText(name, Font, new Size(int.MaxValue, PillH), TextFormatFlags.NoPadding);
                int pw = ts.Width + 2 * PadX;
                if (x > 0) x += GapX;
                if (x > 0 && x + pw > innerW) return false;
                x += pw;
            }
            return true;
        }

        private void DrawPill(Graphics g, Rectangle r, string name, int type)
        {
            int t = (type >= 0 && type <= 2) ? type : 0;
            using (var path = Rounded(r, PillH / 2))
            {
                using var bg = new SolidBrush(PillBg[t]); g.FillPath(bg, path);
                using var bd = new Pen(PillBd[t]); g.DrawPath(bd, path);
            }
            TextRenderer.DrawText(g, name, Font, r, PillFg[t],
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private static void DrawChevron(Graphics g, int cx, int cy, bool expanded)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int s = 4;
            using var pen = new Pen(Color.FromArgb(180, 180, 182), 1.8f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            Point[] pts = expanded
                ? new[] { new Point(cx - s, cy - s / 2), new Point(cx, cy + s / 2), new Point(cx + s, cy - s / 2) }
                : new[] { new Point(cx - s / 2, cy - s), new Point(cx + s / 2, cy), new Point(cx - s / 2, cy + s) };
            g.DrawLines(pen, pts);
            g.SmoothingMode = old;
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(2, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // A mini-thumbnail in the media strip: owner-drawn so the image keeps its aspect on a
    // transparent (pane) background, and the selected one gets a thin white border (no blue
    // selection fill bleeding onto the letterbox area).
    private sealed class MediaThumb : Panel
    {
        private Image _img;
        private bool _selected;
        public MediaThumb()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }
        public void SetImage(Image img) { var old = _img; _img = img; if (!ReferenceEquals(old, img)) old?.Dispose(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var rect = ClientRectangle;
            if (_img != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float ir = (float)_img.Width / _img.Height, ar = (float)rect.Width / Math.Max(1, rect.Height);
                int iw, ih;
                if (ir > ar) { iw = rect.Width; ih = (int)(iw / ir); } else { ih = rect.Height; iw = (int)(ih * ir); }
                g.DrawImage(_img, rect.X + (rect.Width - iw) / 2, rect.Y + (rect.Height - ih) / 2, Math.Max(1, iw), Math.Max(1, ih));
            }
            if (_selected)
            {
                using var p = new Pen(Color.White, 2f);
                g.DrawRectangle(p, 1, 1, rect.Width - 2, rect.Height - 2);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) _img?.Dispose(); base.Dispose(disposing); }
    }

    // Horizontal viewport for the thumbnail strip with a SLIM custom scrollbar. The native
    // FlowLayoutPanel scrollbar (~17px) overlapped the 52px thumbs in the row and isn't
    // resizable; here the thumbs live in an inner auto-sized FlowLayoutPanel we offset
    // horizontally (Flow.Left = -scroll), and a thin bar (~7px) is drawn/dragged at the
    // bottom. Mouse wheel scrolls too (forwarded from the thumbs).
    private sealed class MediaStrip : Panel
    {
        public readonly FlowLayoutPanel Flow;
        private int _scrollX;
        private const int BarH = 7;          // bar footprint — ~50% of the native ~14-17px
        private bool _dragging, _hoverBar;

        public MediaStrip()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(0, 0), Margin = new Padding(0),
            };
            Controls.Add(Flow);
            Flow.SizeChanged += (_, _) => Relayout();
            Flow.MouseWheel += (_, e) => WheelScroll(e.Delta);
        }

        private int ContentW => Flow.PreferredSize.Width;
        private int MaxScroll => Math.Max(0, ContentW - ClientSize.Width);
        private bool NeedBar => MaxScroll > 0;
        private int TrackTop => ClientSize.Height - BarH;

        public void ResetScroll() { _scrollX = 0; Relayout(); }
        public void UpdateScroll() => Relayout();
        public void WheelScroll(int delta) => SetScroll(_scrollX - Math.Sign(delta) * 64);

        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); Flow.BackColor = BackColor; }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); WheelScroll(e.Delta); }

        private void SetScroll(int x)
        {
            x = Math.Max(0, Math.Min(MaxScroll, x));
            if (x == _scrollX) return;
            _scrollX = x; Relayout();
        }

        private void Relayout()
        {
            if (_scrollX > MaxScroll) _scrollX = MaxScroll;
            Flow.Top = Math.Max(0, (ClientSize.Height - BarH - Flow.PreferredSize.Height) / 2);
            Flow.Left = -_scrollX;
            Invalidate();
        }

        // Reveal a thumbnail (child of Flow; its Left is in content coords).
        public void ScrollIntoView(Control c)
        {
            if (c == null || c.Parent != Flow) return;
            int vis = c.Left - _scrollX;
            if (vis < 0) SetScroll(c.Left);
            else if (vis + c.Width > ClientSize.Width) SetScroll(c.Left + c.Width - ClientSize.Width);
        }

        private void JumpToMouse(int mouseX)
        {
            int vw = ClientSize.Width;
            int thumbW = Math.Max(24, (int)((long)vw * vw / Math.Max(1, ContentW)));
            int travel = Math.Max(1, vw - thumbW);
            int x = Math.Max(0, Math.Min(travel, mouseX - thumbW / 2));
            SetScroll((int)((long)MaxScroll * x / travel));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (NeedBar && e.Y >= TrackTop) { _dragging = true; Capture = true; JumpToMouse(e.X); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hb = NeedBar && e.Y >= TrackTop;
            if (hb != _hoverBar) { _hoverBar = hb; Invalidate(); }
            if (_dragging) JumpToMouse(e.X);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging) { _dragging = false; Capture = false; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverBar) { _hoverBar = false; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (!NeedBar) return;
            int vw = ClientSize.Width, y = TrackTop + 1, h = BarH - 2;
            int thumbW = Math.Max(24, (int)((long)vw * vw / ContentW));
            int travel = Math.Max(1, vw - thumbW);
            int thumbX = MaxScroll > 0 ? (int)((long)travel * _scrollX / MaxScroll) : 0;
            using (var tb = new SolidBrush(Color.FromArgb(42, 42, 46))) g.FillRectangle(tb, 0, y, vw, h);
            var col = (_dragging || _hoverBar) ? Color.FromArgb(125, 125, 130) : Color.FromArgb(82, 82, 88);
            using (var b = new SolidBrush(col)) g.FillRectangle(b, thumbX, y, thumbW, h);
        }
    }

    private sealed class MediaPanel : Panel
    {
        private Image _img;
        public MediaPanel()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        public void SetImage(Image img) { var old = _img; _img = img; if (!ReferenceEquals(old, img)) old?.Dispose(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);   // transparent: the panel IS the reserved area; letterbox = pane background, no dark box
            if (_img == null) return;
            var rect = ClientRectangle;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float ir = (float)_img.Width / _img.Height, ar = (float)rect.Width / Math.Max(1, rect.Height);
            int iw, ih;
            if (ir > ar) { iw = rect.Width; ih = (int)(iw / ir); } else { ih = rect.Height; iw = (int)(ih * ir); }
            g.DrawImage(_img, rect.X + (rect.Width - iw) / 2, rect.Y + (rect.Height - ih) / 2, Math.Max(1, iw), Math.Max(1, ih));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _img?.Dispose();
            base.Dispose(disposing);
        }
    }

    // ── Hero panel: fanart bg + clear logo (pulse) + rating + heart ──────────
    // launchbox-web-style top of the detail pane. Owner-drawn so the fanart can sit
    // faintly behind a transparent clear logo (WinForms child controls can't do that).
    private sealed class HeroPanel : Panel
    {
        private static readonly Color StarCommunity = Color.FromArgb(0x38, 0xD6, 0xE6);  // cyan
        private static readonly Color StarUser      = Color.FromArgb(0xF6, 0xC3, 0x44);  // yellow
        private static readonly Color StarEmpty     = Color.FromArgb(78, 255, 255, 255); // ~0.30 white
        private static readonly Color HeartOn       = Color.FromArgb(0xFF, 0x4A, 0x4A);  // red
        private static readonly Color HeartOff      = Color.FromArgb(140, 200, 200, 205);
        private static readonly Color BoxBg         = Color.FromArgb(179, 45, 45, 50);   // ~0.70

        private Image _logo, _fanart;
        private string _logoText;       // fallback shown (with the same pulse) when there's no clear logo
        private bool _logoReady;        // logo load settled (image set, or confirmed none → show text)
        private bool _isGame, _favorite, _ratingIsUser;
        private double _rating;
        private double _fanartAlpha, _fanartTarget;
        private float _logoScale = 1f, _pulseT = 1f;
        private int _hoverStar = -1; private bool _hoverHeart;

        private readonly System.Windows.Forms.Timer _fade = new() { Interval = 16 };
        private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 16 };
        private readonly Rectangle[] _starRects = new Rectangle[5];
        private Rectangle _heartRect;

        public Action<int> RateClicked;   // 1..5
        public Action FavClicked;

        public HeroPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            _fade.Tick += (_, _) => StepFade();
            _pulse.Tick += (_, _) => StepPulse();
        }

        public void SetGame(string title, double rating, bool isUser, bool favorite)
        { _isGame = true; _logoText = title; ClearLogoImage(); _rating = rating; _ratingIsUser = isUser; _favorite = favorite; Invalidate(); }
        public void SetNode(string title)
        { _isGame = false; _logoText = title; ClearLogoImage(); _rating = 0; _favorite = false; _ratingIsUser = false; Invalidate(); }
        public void SetRating(double rating, bool isUser) { _rating = rating; _ratingIsUser = isUser; Invalidate(); }
        public void SetFavorite(bool fav) { _favorite = fav; Invalidate(); }

        // Clear the logo image WITHOUT pulsing (subject changed; the real content —
        // image or text fallback — arrives via SetLogo and pulses then).
        private void ClearLogoImage()
        {
            var old = _logo; _logo = null; old?.Dispose();
            _logoReady = false; _pulse.Stop(); _logoScale = 1f;
        }

        // Final logo content: the image if non-null, else the text fallback. Pulses on appear.
        public void SetLogo(Image img)
        {
            var old = _logo; _logo = img; if (!ReferenceEquals(old, img)) old?.Dispose();
            _logoReady = true;
            _pulseT = 0f; _pulse.Start();   // pulse on appear (image OR text fallback)
            Invalidate();
        }

        public void SetFanart(Image img)
        {
            var old = _fanart; _fanart = img; if (!ReferenceEquals(old, img)) old?.Dispose();
            _fanartAlpha = 0; _fanartTarget = img != null ? 0.28 : 0; _fade.Start(); Invalidate();
        }
        public void FadeOutFanart() { _fanartTarget = 0; _fade.Start(); }

        private void StepFade()
        {
            // Fade-in quick (~100ms), fade-out gentler (~370ms) — asymmetric like the web,
            // and short enough to finish within the 0.5s before the next fanart fades in.
            double step = _fanartAlpha < _fanartTarget ? 0.045 : 0.012;
            if (_fanartAlpha < _fanartTarget) _fanartAlpha = Math.Min(_fanartTarget, _fanartAlpha + step);
            else _fanartAlpha = Math.Max(_fanartTarget, _fanartAlpha - step);
            if (Math.Abs(_fanartAlpha - _fanartTarget) < 0.001)
            {
                _fanartAlpha = _fanartTarget; _fade.Stop();
                if (_fanartTarget == 0 && _fanart != null) { _fanart.Dispose(); _fanart = null; }
            }
            Invalidate();
        }
        private void StepPulse()
        {
            _pulseT += 0.06f;
            if (_pulseT >= 1f) { _pulseT = 1f; _logoScale = 1f; _pulse.Stop(); }
            else _logoScale = 1f + 0.06f * (float)Math.Sin(_pulseT * Math.PI);   // 1 → 1.06 → 1
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Panel);
            var rect = ClientRectangle;
            bool haveFan = _fanart != null && _fanartAlpha > 0.01;
            if (haveFan)
            {
                DrawCoverAlpha(g, _fanart, rect, (float)_fanartAlpha);
                using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(1, rect.Height)),
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0),
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical);
                g.FillRectangle(grad, rect);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            int bottomBar = _isGame ? 30 : 6;
            var logoArea = new Rectangle(10, 8, rect.Width - 20, rect.Height - 8 - bottomBar);
            if (logoArea.Height > 8)
            {
                if (_logo != null) DrawLogo(g, _logo, logoArea, _logoScale);
                else if (_logoReady && !string.IsNullOrEmpty(_logoText)) DrawLogoText(g, _logoText, logoArea, _logoScale);
            }
            if (_isGame) DrawRatingAndHeart(g, rect);
        }

        private static void DrawCoverAlpha(Graphics g, Image img, Rectangle rect, float alpha)
        {
            float ir = (float)img.Width / img.Height, rr = (float)rect.Width / Math.Max(1, rect.Height);
            int w, h;
            if (ir > rr) { h = rect.Height; w = (int)(h * ir); } else { w = rect.Width; h = (int)(w / ir); }
            int x = rect.X + (rect.Width - w) / 2, y = rect.Y + (rect.Height - h) / 2;
            var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(cm);
            g.DrawImage(img, new Rectangle(x, y, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
        }

        private static void DrawLogo(Graphics g, Image img, Rectangle area, float scale)
        {
            int maxH = Math.Min(84, area.Height);
            float ratio = Math.Min((float)area.Width / img.Width, (float)maxH / img.Height);
            int w = Math.Max(1, (int)(img.Width * ratio * scale)), h = Math.Max(1, (int)(img.Height * ratio * scale));
            int x = area.X + (area.Width - w) / 2, y = area.Y + (area.Height - h) / 2;
            // Drop shadow: the logo's alpha silhouette in semi-transparent black, offset.
            var shadow = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new float[]{0,0,0,0,0}, new float[]{0,0,0,0,0}, new float[]{0,0,0,0,0},
                new float[]{0,0,0,0.5f,0}, new float[]{0,0,0,0,1},
            });
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(shadow);
                g.DrawImage(img, new Rectangle(x + 2, y + 3, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
            g.DrawImage(img, x, y, w, h);
        }

        // Text fallback when a game/node has no clear logo — bold, ~0.85 white, centered,
        // drop-shadow + the same pulse scale (mirrors launchbox-web's .ps-logo-text).
        private static void DrawLogoText(Graphics g, string text, Rectangle area, float scale)
        {
            using var f = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
            using var sf = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            var st = g.Save();
            float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f;
            g.TranslateTransform(cx, cy); g.ScaleTransform(scale, scale); g.TranslateTransform(-cx, -cy);
            using (var sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                g.DrawString(text, f, sh, new RectangleF(area.X + 1.5f, area.Y + 2.5f, area.Width, area.Height), sf);
            using (var tb = new SolidBrush(Color.FromArgb(217, 255, 255, 255)))   // ~0.85 white
                g.DrawString(text, f, tb, new RectangleF(area.X, area.Y, area.Width, area.Height), sf);
            g.Restore(st);
        }

        private void DrawRatingAndHeart(Graphics g, Rectangle rect)
        {
            using var numFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var starFont = new Font("Segoe UI Symbol", 11f);
            using var heartFont = new Font("Segoe UI Symbol", 12f);

            int boxH = 22, y = rect.Bottom - boxH - 6, x = 10, pad = 7, starW = 15;
            string num = _rating > 0 ? _rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "—";
            var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            int numW = (int)Math.Ceiling(g.MeasureString(num, numFont).Width) + 2;
            int boxW = pad + numW + 4 + 5 * starW + pad;
            var box = new Rectangle(x, y, boxW, boxH);
            using (var b = new SolidBrush(BoxBg)) FillRound(g, box, 4, b);

            using (var tb = new SolidBrush(Color.White))
                g.DrawString(num, numFont, tb, new RectangleF(x + pad, y, numW, boxH), sf);

            int filled = (int)Math.Round(_rating);
            var fillColor = _ratingIsUser ? StarUser : StarCommunity;
            int sx = x + pad + numW + 4;
            for (int i = 0; i < 5; i++)
            {
                _starRects[i] = new Rectangle(sx + i * starW, y, starW, boxH);
                Color c = _hoverStar >= 0 ? (i <= _hoverStar ? StarUser : StarEmpty)
                                          : (i < filled ? fillColor : StarEmpty);
                using var b = new SolidBrush(c);
                g.DrawString("★", starFont, b, new RectangleF(sx + i * starW, y, starW, boxH), sf);
            }

            // Heart, to the right of the rating box.
            int hx = box.Right + 8;
            _heartRect = new Rectangle(hx, y, boxH, boxH);
            using (var b = new SolidBrush(BoxBg)) FillRound(g, _heartRect, 4, b);
            using (var hb = new SolidBrush(_favorite ? HeartOn : HeartOff))
            using (var hsf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("♥", heartFont, hb, _heartRect, hsf);
        }

        private static void FillRound(Graphics g, Rectangle r, int radius, Brush b)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(b, path);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (_isGame)
            {
                for (int i = 0; i < 5; i++)
                    if (_starRects[i].Contains(e.Location)) { RateClicked?.Invoke(i + 1); return; }
                if (_heartRect.Contains(e.Location)) { FavClicked?.Invoke(); return; }
            }
            base.OnMouseClick(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isGame)
            {
                int hs = -1;
                for (int i = 0; i < 5; i++) if (_starRects[i].Contains(e.Location)) { hs = i; break; }
                bool oh = _heartRect.Contains(e.Location);
                Cursor = (hs >= 0 || oh) ? Cursors.Hand : Cursors.Default;
                if (hs != _hoverStar || oh != _hoverHeart) { _hoverStar = hs; _hoverHeart = oh; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverStar != -1 || _hoverHeart) { _hoverStar = -1; _hoverHeart = false; Invalidate(); }
            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fade.Dispose(); _pulse.Dispose(); _logo?.Dispose(); _fanart?.Dispose(); }
            base.Dispose(disposing);
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
            // Ensure DoubleClick is raised (the running-overlay's escape gesture).
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        }
    }

    // ── Bulk image-cache generation modal ────────────────────────────────────
    // Generates the per-game cached thumbnails in parallel (≤ min(4, cores)) with a
    // progress bar keyed on the number of games. Cancellable; idempotent (cache HITs
    // are skipped instantly, so re-running only fills the gaps).
    private sealed class GenerateCacheForm : Form
    {
        private readonly IGame[] _games;
        private readonly Func<IGame, string[]> _resolve;
        private readonly ProgressBar _bar;
        private readonly Label _label;
        private readonly Button _cancel;
        private readonly System.Threading.CancellationTokenSource _cts = new();
        private int _done;
        private readonly float _s;
        private int S(int px) => (int)Math.Round(px * _s);

        public GenerateCacheForm(IGame[] games, Func<IGame, string[]> resolve)
        {
            _games = games; _resolve = resolve;
            _s = DeviceDpi / 96f;
            Text = "Generate Image Cache";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ControlBox = false;
            ClientSize = new Size(S(452), S(116));
            BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9f);

            _label = new Label { Location = new Point(S(16), S(14)), Size = new Size(S(420), S(20)), ForeColor = Fg,
                                 Text = $"Preparing…  0 / {games.Length}" };
            _bar = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(420), S(18)),
                                     Minimum = 0, Maximum = Math.Max(1, games.Length), Style = ProgressBarStyle.Continuous };
            _cancel = new Button { Location = new Point(S(346), S(78)), Size = new Size(S(90), S(26)), Text = "Cancel",
                                   FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
            _cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 72);
            _cancel.Click += (_, _) => { try { _cts.Cancel(); } catch { } _cancel.Enabled = false; _cancel.Text = "Cancelling…"; };
            Controls.Add(_label); Controls.Add(_bar); Controls.Add(_cancel);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            int dop = Math.Min(4, Math.Max(1, Environment.ProcessorCount));
            System.Threading.Tasks.Task.Run(() => RunGeneration(dop));
        }

        private void RunGeneration(int dop)
        {
            try
            {
                System.Threading.Tasks.Parallel.ForEach(_games,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = _cts.Token },
                    g =>
                    {
                        try
                        {
                            var s = _resolve(g);
                            if (s != null)
                            {
                                if (!string.IsNullOrEmpty(s[0])) ThumbCache.GetOrCreate(s[0], ThumbCache.DefaultMaxDim, keepAlpha: true);
                                if (!string.IsNullOrEmpty(s[1])) ThumbCache.GetOrCreate(s[1], ThumbCache.DefaultMaxDim, keepAlpha: false);
                                if (!string.IsNullOrEmpty(s[2])) ThumbCache.GetOrCreate(s[2], ThumbCache.DefaultMaxDim, keepAlpha: false);
                            }
                        }
                        catch { }
                        Report(System.Threading.Interlocked.Increment(ref _done));
                    });
            }
            catch (OperationCanceledException) { }
            catch { }
            Finish();
        }

        private void Report(int n)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (IsDisposed) return; _bar.Value = Math.Min(_bar.Maximum, n); _label.Text = $"Generating cached images…  {n} / {_games.Length}"; })); }
            catch { }
        }

        private void Finish()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } })); }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _cts.Cancel(); } catch { } _cts.Dispose(); }
            base.Dispose(disposing);
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
