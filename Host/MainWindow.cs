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
using LbApiHost.Host.Similar;
using LbApiHost.Host.Store;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host;

internal sealed partial class MainWindow : Form, IMessageFilter
{
    // ── Theme ────────────────────────────────────────────────────────────────
    // Bg/Panel/Panel2/Fg/SubFg/Accent are byte-for-byte the same palette as Host.UiKit.LiteBoxTheme -
    // referencing it here instead of a second copy of the same Color.FromArgb literals means a future
    // palette change only has one place to edit. Row2 has no LiteBoxTheme equivalent (a striped-row
    // shade specific to this list), so it stays local.
    private static readonly Color Bg      = LiteBoxTheme.Bg;
    private static readonly Color Panel   = LiteBoxTheme.PanelC;   // side panels (tree, detail) — #202128
    private static readonly Color Center  = LiteBoxTheme.Center;   // centre game-list column — #2A2B34
    private static readonly Color Panel2  = LiteBoxTheme.Panel2;
    private static readonly Color Row2    = Color.FromArgb(47, 48, 58);   // striped-row alt: a hair lighter than Center (#2A2B34)
    // Poster empty-tile placeholder: a hair lighter than Center so it blends into the zone (near-invisible).
    private static readonly Color PosterPlaceholder = Color.FromArgb(52, 53, 63);
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
    private GameListIndexBar _gameIndex;         // LB 14's Game List Index — group markers where the scrollbar was
    private bool _gameIndexOn;                   // the option's value — NOT _gameIndex.Visible, which is false while the form isn't shown yet
    private int _posterBaseX;                    // poster X as LayoutPoster placed it (before any overlay nudge)
    private int _posterGridRight;                // right edge of the last tile column, parent coords
    private int _posterCols;                     // column count LayoutPoster settled on (pinned during overlay squeezes)
    private int _posterGap;                      // inter-tile gap LayoutPoster settled on
    private bool _posterSqueezed;                // an overlay squeeze changed spacing/bounds — LayoutPoster restores
    // Poster (grid) view — a native virtual ListView mirroring the OLV's displayed (sorted+filtered)
    // order; owner-drawn box-art tiles. Toggled from VIEW ▸ Images View / List View.
    private PosterListView _poster;
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
    // ── How much memory the poster grid may keep ─────────────────────────────
    // The three caches below used to be capped by COUNT (1024 slots, 600 tiles, 600 thumbs), which
    // hides the fact that one element is 105 KB at zoom 1 and 420 KB at zoom 2 — the same caps then
    // mean 160 MB or 630 MB. They are capped by BYTES now, so the memory is what the user chose and
    // the zoom only changes how many elements fit inside it.
    //
    // The budgets below are exactly what the old counts cost at zoom 1, so level 3 is the behaviour
    // that shipped. The default is one notch above it: box art is the one thing here that costs a disk
    // read and a decode to rebuild, and holding a bit more of it is what makes scrolling back over
    // games already seen instant.
    private const long PosterSlotBudget0 = 1024L * 124 * 212 * 4;    // native slots / composited tiles
    private const long PosterThumbBudget0 = 600L * 124 * 174 * 4;    // decoded box art
    // Level 3 is the reference (1×). Below it the steps are steep — the point of levels 1 and 2 is to
    // give a small machine a real reduction, not a token one; above it they are geometric (×1.39 a
    // step) up to ten times the reference.
    private static readonly double[] PosterMemFactors =
        { 0.125, 0.35, 1.0, 1.4, 2.0, 2.9, 4.2, 6.2, 9.0, 13.0 };

    // Two ceilings that are NOT about memory, both measured:
    //   • an HBITMAP costs one GDI object and a process gets 10 000 — allocation failed at 9 994 in a
    //     standalone probe. The owner-draw tile cache holds one per tile, so it must stay well under.
    //     (The native image list holds ONE handle whatever its slot count, but grows its internal strip
    //     by reallocation, so several thousand slots is where filling it stops being free either way.)
    //   • a GDI+ Bitmap, the form the decoded thumbs are kept in, costs NO handle at all: 12 000 of
    //     them left the GDI count at 3. They are bounded by memory alone, so they get the high ceiling
    //     — and they are also the ones worth keeping, being the only ones that cost a disk read and a
    //     decode to rebuild.
    private const int PosterTileHardCap = 4096;
    private const int PosterThumbHardCap = 65536;

    /// <summary>1–10, default 4. Scales every poster image cache at once.</summary>
    private int PosterMemLevel => Math.Clamp(_cfg.GetInt("PosterImageMemoryLevel", 4), 1, 10);
    private double PosterMemFactor => PosterMemFactors[PosterMemLevel - 1];

    private long PosterSlotBytes => Math.Max(1L, (long)PCellW * (PImgH + PLabelH) * 4);
    private long PosterThumbBytes => Math.Max(1L, (long)PCellW * PArtH * 4);

    /// <summary>The two caps for a given level factor. Whatever the tiles cannot spend — they hit the
    /// hard ceiling long before the budget does at the top levels — is handed to the decoded images
    /// rather than left unused, which is why level 10 reaches its two gigabytes even though the tile
    /// count stops at 4096.</summary>
    private (int Tiles, int Thumbs) PosterCapsFor(double factor)
    {
        long tileBudget = (long)(PosterSlotBudget0 * factor);
        int tiles = (int)Math.Clamp(tileBudget / PosterSlotBytes, 96, PosterTileHardCap);
        long spill = Math.Max(0, tileBudget - (long)tiles * PosterSlotBytes);
        int thumbs = (int)Math.Clamp(((long)(PosterThumbBudget0 * factor) + spill) / PosterThumbBytes,
                                     64, PosterThumbHardCap);
        return (tiles, thumbs);
    }

    /// <summary>Recycled image-list slots (>> on-screen tiles). The floor keeps scrolling smooth at
    /// large zooms, where one tile is expensive: below ~96 the LRU would evict tiles still on screen.</summary>
    private int PosterSlotCap => PosterCapsFor(PosterMemFactor).Tiles;
    private int PosterTileCap => PosterSlotCap;     // the owner-draw renderer's equivalent

    private int PosterThumbCap => PosterCapsFor(PosterMemFactor).Thumbs;

    /// <summary>What the caps add up to right now — shown in the option's help so the level means
    /// something concrete rather than a number between 1 and 10.</summary>
    private string PosterMemEstimate()
    {
        var (t, th) = PosterCapsFor(PosterMemFactor);
        long bytes = (long)t * PosterSlotBytes + (long)th * PosterThumbBytes;
        return $"{bytes / (1024 * 1024)} MB at the current zoom ({t} tiles + {th} images)";
    }
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
    // Base poster tile geometry at zoom 100%; the live values scale with the central-panel zoom so a
    // zoomed grid fits more/fewer posters per row (PCellW+PGap is the horizontal stride LayoutPoster uses).
    private const int PCellW0 = 124, PImgH0 = 174, PLabelH0 = 38, PGap0 = 14;
    private double _zoom = 1.0;                                       // central-panel zoom 0.5–2.0 (saved as ZoomPercent)
    private int PCellW  => (int)Math.Round(PCellW0  * _zoom);
    /// <summary>The ART box — what the box thumbs are scaled to fit. Stays put whatever the badges do,
    /// so switching a badge placement never resizes the artwork.</summary>
    private int PArtH   => (int)Math.Round(PImgH0   * _zoom);
    /// <summary>The image ZONE: the art box plus, for the "just above the art" placement, a reserved
    /// band on top. The art is bottom-anchored in the zone, so that band is empty space above it —
    /// which is what makes that placement work even for a poster that fills the art box.</summary>
    private int PImgH   => PArtH + (BadgeBandAbove ? PBadgeBand : 0);
    // Badges sitting UNDER the developer line grow the label band instead. Every tile in the grid must
    // be the same size, so the reserved row count is the most-badged game of the current view
    // (RecomputePosterBadgeRows) — usually 1, 2 for a well-documented arcade game.
    private int PLabelH => (int)Math.Round(PLabelH0 * _zoom) + (BadgeBandBelow ? PBadgeBand : 0);
    private int PBadgeCell => Math.Max(6, PZ(14) * PosterBadgeScalePct / 100);
    private int PBadgeBand => _posterBadgeRows * PBadgeCell;
    private bool BadgeBandAbove => _posterBadgeRows > 0 && PosterBadgePlacement == PlaceAboveArt;
    private bool BadgeBandBelow => _posterBadgeRows > 0 && PosterBadgePlacement == PlaceUnderDev;
    private int _posterBadgeRows;
    private int PGap    => (int)Math.Round(PGap0    * _zoom);
    private int PZ(int px) => (int)Math.Round(px * _zoom);           // scale a tile-internal offset by zoom
    private Font _posterTileFont;                                    // MainWindow.Font × zoom, for tile title/dev text
    private TextBox _search;                                          // quick search — left panel header (borderless, hosted in _searchWrap)
    private RoundedField _searchWrap;                                 // the rounded frame around it (carries the quick-filter tint)
    // Debounces the search box: ApplyFilter → RebuildView → MeasureContentFits re-scans every row of the
    // (possibly ~15000-row) view to re-fit non-stretch columns, otherwise on EVERY keystroke — wasted CPU +
    // input latency near that library size. 150ms feels instant once typing pauses, yet collapses a fast
    // typist's whole word into one measure pass.
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 150 };
    private FilterGlyphButton _filterBtn;                             // advanced search filter (dialog + active indicator)
    private Search.FilterCriteria _filter;                            // null = no advanced filter
    private ToolStripLabel _extDbInd;      // "ExtendDB present" indicator (menu bar, left of the padlock)
    private ToolStripLabel _parentalInd;   // parental-control padlock indicator (menu bar, left of the bell)
    private Image _padlockClosed, _padlockOpen;
    // Platforms whose games parental control must hide from the list (a platform directly listed,
    // or any platform sitting under a hidden category). Recomputed with the tree. See ParentalBridge.
    private readonly HashSet<string> _parentalHiddenPlatforms = new(StringComparer.OrdinalIgnoreCase);

    // right-hand details
    private readonly HeroPanel _hero;            // fanart + clear logo (pulse) + rating + heart
    private Model3d.Model3dBlock _media3d;       // 3D model overlay INSIDE the main media box (a media-list item)
    private Video.VideoBlock _mediaVideo;        // video overlay in the same box (libvlc surface + hover controls)
    private int _videoThumbToken;                // cancels the deferred video-thumb worker when the selection moves
    private readonly MediaPanel _media;          // main media (box → screenshots, click to switch)
    private readonly MediaStrip _strip;          // clickable mini-thumbnails under the main media (slim custom scrollbar)
    private SplitContainer _outerSplit;          // left tree | (middle list + right details) — % persisted
    private SplitContainer _innerSplit;          // middle list | right details — % persisted
    private Panel _detailHost;                    // scroll viewport hosting the detail grid (scrollbar when content overflows)
    private LaunchButtons _launchButtons;         // Play / Version / ROM group docked at the pane bottom
    private TableLayoutPanel _detailGrid;        // detail layout — sized by RelayoutDetail (fills viewport, or taller → scrolls)
    private double _mediaAspect = 16.0 / 9.0;    // reserved main-media area aspect (16:9 default, 2:3 poster option)
    private List<string> _mediaItems;            // current game's media sources (box first, then screenshots)
    private IGame _mediaItemsGame;                // the game _mediaItems belongs to (fullscreen-viewer guard)
    private int _mediaSel;                        // selected media index
    private System.Windows.Forms.Timer _mediaTimer;   // 0.5s debounce: build strip + upgrade main to full
    private readonly MetaCard _meta;             // title + platform + expandable game fields (or node text)
    private readonly VndbCard _vndb;             // expandable box of coloured VNDB tags (content/tech/ero)
    private RetroAchievementsCard _raCard;       // expandable RetroAchievements box (LiteBox-native, from the raid)
    private StoreAchievementsCard _storeAchCard; // expandable store-achievements box (GOG today; from galaxy-2.0.db)
    private DetailTabStrip _detailTabs;          // compact OVERVIEW | RELATED GAMES strip (game mode only)
    private RelatedGamesPanel _related;          // Related tab content (suggester cards; fills the pane)
    private Mame.HighScoresPanel _highScores;    // HIGH SCORES tab content (MAME leaderboards; game mode, MAME only)
    private bool _hsTabShown;                     // whether the HIGH SCORES tab is currently in the strip (MAME game)
    private static int _detailTabSel;            // 0 = Overview, 1 = Related — remembered across selections (session)
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
    private ThemedDropDown _viewCombo;                              // left-panel "group by" selector
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
    private string _curSortKey = "title";
    private string _sessionSortKey = "title";
    private bool _sessionAscending = true;
    private bool _nodeForcesSort;
    private string _sortedNodeKey;         // node the current sort was activated for (navigation vs refresh)
    private Dictionary<string, int> _manualOrder;
    private readonly DeferredGameSort _deferredKioskSort = new();
    private TitleSortNormalization _titleSortNormalization;

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

    // Arrange By is session-scoped; playlist SortBy can temporarily override it without changing
    // the session choice restored when the user leaves that playlist.
    public MainWindow(PluginRegistry reg, IDataManager dm)
    {
        _reg = reg; _dm = dm;
        // LEDBlinky reads its enable flag + exe path live from LB Settings.xml.
        if (_dm is HostDataManagerXml hdmLed) LedBlinky.Bind(hdmLed.LbSettings);
        // MAME leaderboard toggles (download gates the HIGH SCORES tab; upload gates the auto-submit) read live too.
        if (_dm is HostDataManagerXml hdmMame) Mame.MameOptions.Bind(hdmMame.LbSettings);
        // Quels jeux savent produire un high score : lu une fois des hiscore.dat installés (MAME et FBNeo).
        // En tâche de fond — c'est de l'E/S pure, et personne ne pose la question avant qu'un jeu soit sélectionné.
        System.Threading.Tasks.Task.Run(() => { try { _ = Mame.HiscoreDat.Count; } catch { } });
        _cfg = LiteBoxConfig.LoadForExe();
        _titleSortNormalization = _cfg.TitleSortNormalizationMode;
        // The 3D snapshot reads OUR live config: an option applied in the Options window is then visible to
        // the 3D paths as soon as the snapshot is invalidated, without waiting for the ini file write.
        Model3d.Model3dOptions.Source = () => _cfg;
        _secondInstance = InstanceGuard.AnotherInstanceRunning;
        _useImageCache = _cfg.UseImageCache;
        _posterOwnerDraw = _cfg.GetBool("PosterOwnerDraw", false);   // legacy poster renderer (vs native image list)
        _zoom = Math.Clamp(_cfg.GetInt("ZoomPercent", 100) / 100.0, 0.5, 2.0);   // read BEFORE BuildPoster so its image list is sized for the saved zoom
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
        _hero.ProgressClicked = r => Safe(() => ShowProgressMenu(r));
        _hero.EditClicked = () => Safe(() => { if (_heroBadgeGame != null) OpenEditGame(new[] { _heroBadgeGame }); });
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
        WireBadges();   // list strip source + the events that repaint badges + the background pass

        var inner = new ThemedSplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Bg, SplitterWidth = 4 };
        inner.Panel1.BackColor = Center;      // centre column — #2A2B34 (shows in the poster-grid side margins too)
        inner.Panel1.Controls.Add(_poster);   // hidden until poster mode; same cell as the list
        inner.Panel1.Controls.Add(_games);
        // The Game List Index stands where the scrollbar was. NOT docked: it places itself on the
        // panel's right edge, and the views make room via Panel1.Padding — so a hover expansion is
        // a pure overlay and never reflows the grid or the columns under the pointer.
        _gameIndex = new GameListIndexBar
        {
            BackColor = Center, Font = Font,
            // Providers pick the ACTIVE view: the poster mirrors the list's displayed order
            // (same index space), so the groups are shared and only the scroll plumbing switches.
            GroupsProvider = ComputeIndexGroups,
            RowCount = () => _games.VisibleGames.Count,
            JumpToRow = r => { if (_posterMode) _poster.ScrollItemToTop(r); else _games.ScrollRowToTop(r); },
            SelectRow = r =>
            {
                if (_posterMode) _poster.SelectRange(r, r);   // fires SelectedIndexChanged → detail pane follows
                else if (_games.GameAt(r) is { } g) _games.SelectGame(g, focus: false);
            },
            TopRow = () => _posterMode ? _poster.FirstVisibleIndex : _games.TopRowIndex,
            PageRows = () => _posterMode ? _poster.ItemsPerPage : _games.RowsPerPage,
            // Free positioning + the wheel go through the view's own scroller: the poster grid
            // moves by PIXELS there, which is what keeps a drag continuous on a short list.
            ScrollToFraction = f => { if (_posterMode) _poster.ScrollToFraction(f); else _games.ScrollToFraction(f); },
            ScrollFraction = () => _posterMode ? _poster.ScrollFraction : _games.ScrollFraction,
            ScrollLines = n => { if (_posterMode) _poster.ScrollLines(n); else _games.ScrollLines(n); },
        };
        inner.Panel1.Controls.Add(_gameIndex);
        _gameIndex.BringToFront();   // above both views — its hover expansion overlays them
        _games.ViewChanged += () => { if (_gameIndex.Visible) _gameIndex.RefreshGroups(); };
        _games.Scrolled += () => { if (_gameIndex.Visible) _gameIndex.Invalidate(); };   // keep the thumb honest
        _poster.Scrolled += () => { if (_gameIndex.Visible) _gameIndex.Invalidate(); };
        _gameIndex.ReservedWidthChanged += ApplyGameListIndexRoom;   // labels refit → the views re-pad
        // The hover expansion overlays the grid; if it would COVER the last tile column, slide the
        // whole grid left into its own centring slack (a pure translation: the column count — and
        // with it the drag mapping — never changes). Bar Resize = display width change.
        _gameIndex.Resize += (_, _) => NudgePosterForOverlay();
        // The wheel over the strip, whatever state it is in. Windows delivers WM_MOUSEWHEEL to the
        // FOCUSED control — which is neither the strip nor, necessarily, the list — so relying on
        // the strip's own handler left it a dead zone, most visibly while COLLAPSED (a 14px sliver
        // that owns no scrolling of its own). A thread filter catches the wheel wherever it landed
        // and applies it to the active view whenever the pointer is over the strip's lane.
        _wheelFilter = new IndexWheelFilter(this);
        Application.AddMessageFilter(_wheelFilter);
        FormClosed += (_, _) => { try { Application.RemoveMessageFilter(_wheelFilter); } catch { } };
        ApplyGameListIndexOptions();
        // Layout ran while the form was invisible (Control.Visible lies until Show), so the poster
        // may have measured a barless width — one refresh once everything is really on screen.
        Shown += (_, _) => { try { ApplyGameListIndexOptions(); } catch { } };
        // Launch buttons docked at the bottom of the details pane (always visible,
        // outside the scrolling detail grid). _detailHost (Fill) is added FIRST so
        // the bottom panel reserves its space and the grid fills the rest.
        inner.Panel2.Controls.Add(_detailHost);
        _launchButtons = new LaunchButtons(
            (g, app, emu) => Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, app, emu, null)),
            StoreLaunch,   // GOG/Steam: running screen + exit watch
            g => (_dm as HostDataManagerXml)?.GetLastLaunchFull(Safe(() => g.Id)),    // launch-button initial selection fallback (no ExtendDB): emu + version + last archive ROM
            id => (_dm as HostDataManagerXml)?.ClearLastLaunch(id));              // ↺ reset button cancels the LiteBox history row
        inner.Panel2.Controls.Add(_launchButtons);
        inner.Panel1.Resize += (_, _) => LayoutPoster();   // keep the poster grid centred on resize

        var outer = new ThemedSplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Bg, SplitterWidth = 4 };
        // Left panel, LaunchBox layout: search field + advanced-filter funnel on top, the "group by"
        // selector under them, then the source tree (fill). A TableLayoutPanel gives a deterministic
        // strip + strip + fill split (no docking z-order guessing).
        var searchRow = BuildSearchRow();
        _viewCombo = BuildViewCombo();
        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            BackColor = Panel, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        // The single column has to be declared, and declared as Percent: a TableLayoutPanel fills any
        // ColumnStyle it was not given with an AutoSize one, and an AutoSize column sizes itself on its
        // children rather than on the panel. The rows were spelled out and the column was not, so the
        // strip sized itself to what it wanted and overflowed to the right when the pane was dragged
        // narrower — taking the funnel with it, since it sits at the right end of the search row while
        // the search box holds the percent column beside it. The button did not shrink or hide; it was
        // simply outside the panel being painted.
        leftPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        leftPanel.Controls.Add(searchRow, 0, 0);
        leftPanel.Controls.Add(_viewCombo, 0, 1);
        leftPanel.Controls.Add(_sources, 0, 2);
        outer.Panel1.Controls.Add(leftPanel);
        outer.Panel2.Controls.Add(inner);
        Controls.Add(outer);
        _outerSplit = outer; _innerSplit = inner;   // for splitter % persistence

        // The second bar is gone. Everything it carried has a home in the LaunchBox-shaped menu above it —
        // Arrange By and Image Group as top-level entries, the list/poster switch and Generate Image Cache
        // under VIEW, Options / Emulators / Plugins under TOOLS, the game count as the bar's status label,
        // and the two session indicators (padlock, ExtendDB) beside the bell. See MainWindowMainMenu.cs.

        // Poster IMAGE GROUP — which regroupement the tiles show. Restored HERE because BuildMainMenu's
        // Image Group entries read _posterGroup to stamp their check marks, and it builds below.
        _posterGroup = _cfg.Get("PosterImageGroup", null) ?? "Front";

        // Persist layout / window / selection once, at close (not per change).
        // _closing lets the serialized detail loader bail before its blocking Invoke once the pump ends.
        FormClosing += (_, _) => { _closing = true; try { Application.RemoveMessageFilter(this); } catch { } try { Media.GameMusicPlayer.Stop(); } catch { } LedBlinky.FrontendQuit(); try { SaveAll(); } catch { } };

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

        // Notifications get somewhere to draw: popups in this monitor's bottom-right corner + the bell in
        // the menu bar. Before BuildMainMenu, which creates the bell itself. (Detached on FormClosed.)
        Notifications.NotificationUi.Attach(this);

        // ── Top menu: the LaunchBox-shaped bar (see MainWindowMainMenu.cs) ───
        var menu = BuildMainMenu();

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

        // Dark native scrollbars (Win10/11 explorer dark theme).
        DarkScroll(_games);
        DarkScroll(_sources);
        DarkScroll(_notes);
        DarkScroll(_detailHost);   // the detail pane's overflow scrollbar
        DarkScroll(_related.ScrollHost);   // the Related tab's internal card list
        // _strip uses its own slim custom scrollbar (no native scrollbar to theme).

        Load += (_, _) =>
        {
            // Pane widths: restore the saved fractions (left tree, middle list) so the 3 panes scale
            // proportionally with the window; fall back to fixed defaults on first run. outer first
            // (sets inner's available width), then inner.
            int leftPm = _cfg.GetInt("SplitLeftPermille", 0);
            int midPm = _cfg.GetInt("SplitMidPermille", 0);
            float dpiS = LiteBoxTheme.DpiScale(this);
            // A floor for the left pane, because WinForms' default is 25px and the funnel lives in a
            // fixed-width column at the right of the search row: past a certain narrowness it is the
            // button that falls off the edge, silently, while the search box keeps its own space.
            //
            // 85 is the point where the row still HOLDS rather than the point where it is comfortable:
            // 16 of padding + 36 for the funnel and its gap, leaving 33 for the field — which stays
            // typable only because RoundedField now yields its insets instead of eating that 33. The
            // "Platform Category" selector below truncates well before this; that is a legibility
            // matter, and the tree underneath is what the pane is for.
            //
            // Set BEFORE restoring the saved fraction, since SetSplitFraction clamps against it: an INI
            // written when there was no floor is corrected on the way in rather than reproduced.
            try { outer.Panel1MinSize = (int)Math.Round(85 * dpiS); } catch { }
            if (leftPm > 0) SetSplitFraction(outer, leftPm / 1000.0);
            else try { outer.SplitterDistance = (int)Math.Round(240 * dpiS); } catch { }
            if (midPm > 0) SetSplitFraction(inner, midPm / 1000.0);
            else try { inner.SplitterDistance = Math.Max((int)Math.Round(300 * dpiS), inner.Width - (int)Math.Round(380 * dpiS)); } catch { }
            RestoreColumnLayout();   // order / width / shown-hidden from the INI
            _games.AutoFitColumns = _cfg.GetBool("AutoFitColumns", true);   // Display option (default on)
            _games.TwoLineRows = _cfg.GetBool("TwoLineRows", true);         // Display option (default on)
            if (Math.Abs(_zoom - 1.0) > 0.001) _games.SetZoom((float)_zoom, 9f);   // apply saved central-panel zoom to the list (poster already built at this zoom)
            Application.AddMessageFilter(this);   // enable Ctrl-wheel zoom over the central panel
            // Arrange By is deliberately session-only. Every process starts at Title ascending.
            _currentView = SourceViews.ById(_cfg.Get("GroupView"));   // restore the saved grouping…
            SyncViewCombo();                                          // …reflect it in the combo (no rebuild)
            PopulateSources();       // build the tree
            RestoreSelection();      // last category + game
            RefreshExtendDbIndicators();   // ExtendDB-present + parental padlock
            try { ActiveControl = _games; _games.Focus(); } catch { }
            if (_cfg.GetBool("PosterMode", false)) SetPosterMode(true);   // restore the saved view
            LedBlinky.FrontendStart();   // "1" — the front-end is up (LEDBlinky FE-active animation, etc.)
        };
        // Final dark-scrollbar pass once everything (data, columns) is in place.
        Shown += (_, _) =>
        {
            ApplyDarkScroll(_games); ApplyDarkScroll(_sources); ApplyDarkScroll(_notes); ApplyDarkScroll(_detailHost); ApplyDarkScroll(_related.ScrollHost); RelayoutDetail();
            // Why is THIS game's 3D model what it is: the slots it resolved, the key they produce, and
            // whether the cached GLB still answers to that key. Reading it beats guessing at a stale bake.
            var m3dInfo = Environment.GetEnvironmentVariable("LB_MODEL3D_INFO");
            if (!string.IsNullOrEmpty(m3dInfo))
            {
                try
                {
                    var g = FindGameForCli(m3dInfo);
                    if (g == null) Console.WriteLine($"[m3d] game not found: \"{m3dInfo}\"");
                    else
                    {
                        var idn = Model3d.Model3dCache.Resolve(g);
                        if (idn == null) Console.WriteLine("[m3d] Resolve returned null (no platform/title/id)");
                        else
                        {
                            Console.WriteLine($"[m3d] {Safe(() => g.Title)} · hasArt={idn.HasArt} key={idn.Key}");
                            Console.WriteLine($"[m3d]   front = {idn.Art.Front ?? "(none)"}");
                            Console.WriteLine($"[m3d]   back  = {idn.Art.Back ?? "(none)"}");
                            Console.WriteLine($"[m3d]   spine = {idn.Art.Spine ?? "(none)"}");
                            Console.WriteLine($"[m3d]   logo  = {idn.Art.Logo ?? "(none)"}");
                            Console.WriteLine($"[m3d]   full  = {idn.Art.Full ?? "(none)"}  fullScan={idn.Art.FullScan}");
                            Console.WriteLine($"[m3d]   glb   = {idn.GlbPath} exists={System.IO.File.Exists(idn.GlbPath)}");
                            Console.WriteLine($"[m3d]   current={Model3d.Model3dCache.IsCurrent(idn)}");
                            var info = Model3d.GlbFile.ReadInfo(idn.GlbPath);
                            Console.WriteLine(info == null ? "[m3d]   stored: (no header)"
                                : $"[m3d]   stored: baker={info.BakerVersion} (now {Model3d.Model3dCache.BakerVersion}) key={info.Key}");
                            // LB_MODEL3D_BAKE=1: do what the detail pane now does with a stale model —
                            // re-bake it — and report whether that actually brings the slot up to date.
                            if (Environment.GetEnvironmentVariable("LB_MODEL3D_BAKE") == "1")
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                string? baked = Model3d.Model3dCache.Ensure(g);
                                sw.Stop();
                                var after = Model3d.Model3dCache.Resolve(g);
                                Console.WriteLine($"[m3d]   Ensure -> {(baked == null ? "null" : "ok")} in {sw.ElapsedMilliseconds} ms; "
                                    + $"current now={(after != null && Model3d.Model3dCache.IsCurrent(after))}");
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("[m3d] " + ex.Message); }
                BeginInvoke((Action)(() => { Console.WriteLine("[m3d] done"); Close(); }));
            }
            // LB_IMGDL_TEST="<game>|<regroupement>": resolve the ExtendDB stand-in for that slot and try
            // to download it, with LB_IMGDL_DIAG tracing on — reproduces "this one image won't come down"
            // without driving the matrix by hand.
            var dlTest = Environment.GetEnvironmentVariable("LB_IMGDL_TEST");
            if (!string.IsNullOrEmpty(dlTest))
            {
                Environment.SetEnvironmentVariable("LB_IMGDL_DIAG", "1");
                var parts = dlTest.Split('|');
                string wanted = parts[0], cat = parts.Length > 1 ? parts[1] : "Front";
                var g = FindGameForCli(wanted);
                if (g == null) Console.WriteLine($"[imgdl] game not found: \"{wanted}\"");
                else
                {
                    Console.WriteLine($"[imgdl] === {Safe(() => g.Title)} · {cat} ===");
                    try { EditGameWindow.DiagDownloadSlot(g, cat, this); }
                    catch (Exception ex) { Console.WriteLine("[imgdl] driver: " + ex); }
                }
                Console.WriteLine("[imgdl] done");
                BeginInvoke((Action)Close);
            }
            if (Environment.GetEnvironmentVariable("LB_WHEEL_SELFTEST") == "1") RunWheelSelfTest();
            var idxDiag = Environment.GetEnvironmentVariable("LB_INDEX_DIAG");
            if (!string.IsNullOrEmpty(idxDiag)) RunIndexDiag(idxDiag);
            // Hands-free UI drivers (diagnostics): --edit-game/--edit-page and --options — see HostBoot.
            if (!string.IsNullOrEmpty(HostBoot.AutoEditGame))
            {
                try
                {
                    var g = FindGameForCli(HostBoot.AutoEditGame);
                    if (g != null)
                    {
                        Console.WriteLine($"[edit-game] opening \"{Safe(() => g.Title)}\" page={HostBoot.AutoEditPage ?? "(default)"}");
                        BeginInvoke((Action)(() => EditGameWindow.Open(new[] { g }, Array.Empty<IGame>(), false, this, HostBoot.AutoEditPage)));
                    }
                    else Console.WriteLine($"[edit-game] game not found: \"{HostBoot.AutoEditGame}\"");
                }
                catch (Exception ex) { Console.WriteLine("[edit-game] " + ex.Message); }
            }
            else if (!string.IsNullOrEmpty(HostBoot.AutoEditEmu))
            {
                try
                {
                    var key = HostBoot.AutoEditEmu;
                    var emu = (_dm.GetAllEmulators() ?? Array.Empty<IEmulator>()).FirstOrDefault(e =>
                        string.Equals(Safe(() => e.Id), key, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Safe(() => e.Title), key, StringComparison.OrdinalIgnoreCase)
                        || (Safe(() => e.Title) ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (emu != null)
                    {
                        Console.WriteLine($"[edit-emu] opening \"{Safe(() => emu.Title)}\"");
                        BeginInvoke((Action)(() => Emulators.EditEmulatorWindow.Open(emu, false, this, LbApiHost.Host.Media.MediaResolver.LbRoot ?? "")));
                    }
                    else Console.WriteLine($"[edit-emu] emulator not found: \"{key}\"");
                }
                catch (Exception ex) { Console.WriteLine("[edit-emu] " + ex.Message); }
            }
            else if (HostBoot.AutoGenCache != null)
            {
                BeginInvoke((Action)(() => GenCacheSelfTest(HostBoot.AutoGenCache)));
            }
            else if (HostBoot.AutoOptions != null)
            {
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        // The launch-with-arguments path is precisely how a second host gets to exist next
                        // to a running one, and this one asks for the options window by name. Same bar as
                        // the menu item and the padlock: that window saves, this instance may not.
                        if (_secondInstance)
                        {
                            Console.WriteLine("[options] another LiteBox instance is open → options not shown (read-only)");
                            return;
                        }
                        using var w = BuildOptionsWindow();
                        if (HostBoot.AutoOptions.Length > 0 && !w.SelectSection(HostBoot.AutoOptions))
                            Console.WriteLine($"[options] section not found: \"{HostBoot.AutoOptions}\"");
                        w.ShowDialog(this);
                        (_dm as HostDataManagerXml)?.FlushLbSettingsIfSafe();
                    }
                    catch (Exception ex) { Console.WriteLine("[options] " + ex.Message); }
                }));
            }
            else if (HostBoot.NotifyDemo)
            {
                // --notify-demo: one of each shape, so the popup stack, the wrapping, the action buttons
                // and the bell badge can be eyeballed without hunting for something that notifies.
                BeginInvoke((Action)(() =>
                {
                    LiteBox.Notifications.NotificationCenter.Info("This is a LiteBox notification.");
                    LiteBox.Notifications.NotificationCenter.Error(
                        "Something went wrong, and this message is long enough to prove that the card grows "
                        + "to fit its text instead of cropping it.");
                    LiteBox.Notifications.NotificationCenter.Input("Notification with actions.", new[]
                    {
                        new KeyValuePair<string, Action>("Say hi",
                            () => LiteBox.Notifications.NotificationCenter.Info("Hi.")),
                        new KeyValuePair<string, Action>("Raise an error",
                            () => LiteBox.Notifications.NotificationCenter.Error("This is an error notification.")),
                    });
                    // …and the LaunchBox-compatibility layer, reached exactly as a plugin reaches it.
                    Notifications.LaunchBoxShim.SelfTest();
                }));
            }
            else if (!string.IsNullOrEmpty(HostBoot.AutoPlay))
            {
                try
                {
                    var g = FindGameForCli(HostBoot.AutoPlay);
                    if (g != null)
                    {
                        Console.WriteLine($"[play] launching \"{Safe(() => g.Title)}\"{(HostLaunch.DryRun ? " (dry)" : "")}");
                        BeginInvoke((Action)(() => Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, null, null))));
                    }
                    else Console.WriteLine($"[play] game not found: \"{HostBoot.AutoPlay}\"");
                }
                catch (Exception ex) { Console.WriteLine("[play] " + ex.Message); }
            }
            else if (!string.IsNullOrEmpty(HostBoot.DupCycle))
            {
                // Hands-free dup-filter lifecycle test: cold build (CNN session may create) → Suspend
                // (what a game launch does) → build during suspension (cached verdicts / hint, NO session)
                // → Resume → warm build (pure cache hits). Pair with --debug to see the [dedup] trace.
                var g = FindGameForCli(HostBoot.DupCycle);
                if (g == null) Console.WriteLine($"[dup-cycle] game not found: \"{HostBoot.DupCycle}\"");
                else new System.Threading.Thread(() =>
                {
                    try
                    {
                        Console.WriteLine($"[dup-cycle] game \"{Safe(() => g.Title)}\" — 1: COLD build (both views)");
                        var l1 = BuildMediaList(g, poster: false); var p1 = BuildMediaList(g, poster: true);
                        Console.WriteLine($"[dup-cycle] cold: list={l1.Count} poster={p1.Count}");
                        Console.WriteLine("[dup-cycle] 2: SUSPEND (simulated game launch) + build during suspension");
                        Media.Dedup.DedupEngine.Suspend();
                        var l2 = BuildMediaList(g, poster: false);
                        Console.WriteLine($"[dup-cycle] suspended build: list={l2.Count}");
                        Console.WriteLine("[dup-cycle] 3: RESUME + warm build");
                        Media.Dedup.DedupEngine.Resume();
                        var l3 = BuildMediaList(g, poster: false);
                        Console.WriteLine($"[dup-cycle] warm build: list={l3.Count}");
                        Console.WriteLine("[dup-cycle] done");
                    }
                    catch (Exception ex) { Console.WriteLine("[dup-cycle] " + ex.Message); }
                })
                { IsBackground = true, Name = "dup-cycle" }.Start();
            }
            // Automatic Progress Tracking sweep (LB parity) — background, opt-in (LiteBox option) and
            // gated internally on the Settings.xml master switch; local data only (play time, last
            // played, RA cache). Off by default: the on-select / on-exit triggers cover normal use.
            if (_cfg.ProgressSweepOnBoot)
                try { Data.ProgressAutomation.SweepAsync(); } catch { }
            // RA catalogue heartbeat (engine P1): first tick +20s then every 30 min. On each idle tick it
            // refreshes EVERY absent (never-pulled) console + up to 3 stale (past-TTL) ones, most-overdue
            // first — so it also serves the old "startup rolling refresh" role, no opt-in needed.
            try { RaCatalogEngine.Start(); } catch { }
            // RA session-token auto-renewal: if the user stored their RA password, re-login and rewrite
            // the expiring token in Settings.xml when it's due. Skipped in read-only; fail-safe on error.
            // CanWrite is a LIVE delegate (not a boot latch): toggling read-only in the options is seen
            // by the very next heartbeat firing, no restart needed.
            try
            {
                var dmRef = _dm;
                Ra.RaTokenRenew.CanWrite = () => !(dmRef is HostDataManagerXml roDm) || !roDm.ReadOnly;
                Ra.RaTokenRenew.MaybeRenewAsync();
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
            Dock = DockStyle.Fill, Font = Font, BackColor = Center, ForeColor = Fg,   // centre column — #2A2B34
            Striped = true, RowBack = Center, RowAlt = Row2, RowFore = Fg,
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
        // Effective year (explicit ReleaseYear, else the year inside ReleaseDate) — the column now
        // SHOWS what Arrange By "Release Date Year" and the web clients SORT on, instead of leaving
        // a blank cell for every game dated only by a full ReleaseDate.
        Col("year", "Year", DpiW(55), g => GameSortCatalog.EffectiveYear(g),
            g => GameSortCatalog.EffectiveYear(g)?.ToString() ?? "", HorizontalAlignment.Right);
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
        // Three states, because Installed is a user checkbox and "never set" is not "not installed":
        // ✓ ticked · ✕ explicitly unticked · blank = untouched. Same distinction the sort key makes.
        Col("installed", "Installed", DpiW(60), g => Safe(() => (object)g.Installed),
            g => Safe(() => g.Installed) is bool b ? (b ? "✓" : "✕") : "", HorizontalAlignment.Center, visible: false);
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

        lv.SelectionChangedGame += OnGameSelectionChanged;
        lv.GameActivated += LaunchSelected;
        lv.GameRightClicked += OnGameRightClicked;
        lv.ColumnClicked += OnHeaderColumnClicked;
        lv.ColumnChooserRequested += ShowColumnChooser;
        lv.ViewChanged += OnViewChanged;
        lv.SearchForVirtualItem += OnTypeAheadSearch;   // type-to-jump (compare-name prefix)
        lv.KeyPress += OnGameListKeyPress;              // hors tri Titre : la frappe FILTRE
        lv.KeyDown += OnGameListKeyDown;                // Retour arrière / Échap sur ce filtre
        return lv;
    }

    private void OnViewChanged()
    {
        UpdateMenuStatus();   // "Displaying N of M total games." in the menu bar
        // The badge band the tiles reserve is sized on the most-badged game of the VIEW, so a node or
        // filter change can move it — and moving it changes every tile's height.
        if (RecomputePosterBadgeRows()) { try { RebuildPosterGeometry(); } catch { } }
        if (_posterMode) RefreshPoster();
    }

    // ── ExtendDB / parental indicators ─────────────────────────────────────────
    // Reflect ExtendDB's presence and parental-control state into the menu bar:
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
            // Locking is a session-only toggle (ParentalFilter.SetLocked writes nothing), so it stays
            // available in a read-only second instance — hiding the restricted games is exactly what one
            // might have come to look at. The SETTINGS are a different matter: that window saves, so it is
            // barred here as it is under Tools ▸ Options, and the tip must stop offering the gesture.
            _parentalInd.ToolTipText = (locked
                ? "Parental control ACTIVE (locked) — restricted categories and games are hidden"
                : "Parental control unlocked")
                + "\nClick to " + (locked ? "unlock (PIN)" : "lock")
                + (_secondInstance
                    ? " · right-click for the lock · settings locked (another LiteBox instance is open)"
                    : " · double-click for the settings · right-click for both");
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
        if (ParentalBridge.IsGameBlocked(g)) return true;   // per-game "requires parental" flag (row bit)
        if (ParentalBridge.HideUninstalled && Safe(() => g.Installed) == false) return true;   // hide not-installed games
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
        SelectSort(SortKeyForColumn(col.Key));
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
        if (string.Equals(key, GameSortCatalog.Manual, StringComparison.OrdinalIgnoreCase))
            return g => _manualOrder != null && _manualOrder.TryGetValue(S(Safe(() => g.Id)), out var i) ? i : int.MaxValue;
        if (GameSortCatalog.IsStandard(key) || key.StartsWith(GameSortCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var getter = GameSortCatalog.Getter(key, _titleSortNormalization);
            return g => getter(g);
        }
        // Extra LiteBox columns that are not in LaunchBox's canonical Arrange By list
        // retain their native header sort instead of silently falling back to Title.
        var col = _games.AllColumns.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return col?.Sort ?? (g => CompareName(g));
    }

    private static string SortKeyForColumn(string key) => key?.ToLowerInvariant() switch
    {
        "fav" => "favorite",
        "players" => "maxplayers",
        "plays" => "playcount",
        "dbid" => "launchboxid",
        "year" => "releaseyear",
        "esrb" => "rating",
        "rating" => "starrating",
        _ => key?.ToLowerInvariant() ?? "title",
    };

    private static string ColumnKeyForSort(string key) => key?.ToLowerInvariant() switch
    {
        "favorite" => "fav",
        "maxplayers" => "players",
        "playcount" => "plays",
        "launchboxid" => "dbid",
        "releaseyear" => "year",
        "rating" => "esrb",
        "starrating" => "rating",
        _ => key,
    };

    // ── Right details construction ───────────────────────────────────────────
    private Panel BuildDetails(out HeroPanel hero, out MediaPanel media, out MediaStrip strip,
                               out MetaCard meta, out VndbCard vndb, out TextBox notes)
    {
        // Reserved main-media aspect (width/height): 16:9 by default, or poster 2:3 (INI option).
        _mediaAspect = _cfg.Use169ForMainScreenshot ? (16.0 / 9.0) : (2.0 / 3.0);

        var tlp = new TableLayoutPanel { BackColor = Panel, ColumnCount = 1, RowCount = 11, Padding = new Padding(12) };
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));   // hero: fanart + logo + rating/heart
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));   // main media (sized from pane width → _mediaAspect)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));    // mini-thumbnail strip + slim scrollbar (reserved)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // OVERVIEW | RELATED GAMES tab strip (0 for tree nodes)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));    // meta card (title + platform + expandable fields, wraps)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // VNDB tags box (0 when none; expandable)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // RetroAchievements box (0 when no raid; expandable)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // store achievements box (0 when not a GOG game; expandable)
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // notes (fills the rest in Overview mode)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // Related panel (fills instead of rows 4-8 in Related mode)
        tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // HIGH SCORES panel (MAME leaderboards; fills instead of rows 4-8 in that mode)
        _detailGrid = tlp;

        hero = new HeroPanel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        media = new MediaPanel { Dock = DockStyle.Fill, BackColor = Panel };
        // 3D model OVERLAY: lives INSIDE the main media box and covers it while the current media item
        // is the 3D sentinel (thumb PNG first, live viewport swapped in — the proven Model3dBlock flow).
        // VISIBILITY IS CONTENT-DRIVEN: the overlay only appears once its cover PNG is actually set
        // (HasContent) — showing it eagerly covered the instant image with an EMPTY panel for the
        // background hop of the thumb load, which read as the jacket blinking out at post-load time.
        _media3d = new Model3d.Model3dBlock { Dock = DockStyle.Fill, BackColor = Panel, Visible = false };
        _media3d.ContentChanged = () =>
        {
            _media3d.Visible = _media3d.HasContent;
            if (_media3d.HasContent) _media3d.BringToFront();
        };
        media.Controls.Add(_media3d);
        // Video OVERLAY: same content-driven visibility as the 3D one — it appears when a video sentinel
        // becomes the main media, and hosts libvlc's render surface + the hover controls.
        _mediaVideo = new Video.VideoBlock { Dock = DockStyle.Fill, Visible = false };
        _mediaVideo.ContentChanged = () =>
        {
            _mediaVideo.Visible = _mediaVideo.HasContent;
            if (_mediaVideo.HasContent) _mediaVideo.BringToFront();
        };
        _mediaVideo.FullscreenRequested = OpenFullscreenVideo;
        media.Controls.Add(_mediaVideo);
        // Fullscreen viewers (LB parity): double-click an image → the image viewer; the 3D overlay's
        // badge or a double-click → the fullscreen model (reloaded at source texture resolution).
        media.DoubleClicked = OpenFullscreenImage;
        _media3d.ExpandClicked = () => { if (_detailsShown is IGame gfs) OpenFullscreen3d(gfs); };
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

        // Compact OVERVIEW | RELATED GAMES switch (web-theme parity) + the Related content panel.
        _detailTabs = new DetailTabStrip(primary: true) { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 0, 6) };
        _detailTabs.SetTabs("OVERVIEW", "RELATED GAMES");
        _detailTabs.Selected = _detailTabSel;
        _detailTabs.SelectedChanged = OnDetailTabChanged;
        _related = new RelatedGamesPanel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0) };
        _related.CandidateFilter = RelatedCandidateAllowed;
        _related.LocalArtResolver = RelatedLocalArt;
        _related.OpenLocalGame = id => { try { SelectGameById(id); } catch { } };

        tlp.Controls.Add(hero, 0, 0);
        tlp.Controls.Add(media, 0, 1);
        tlp.Controls.Add(strip, 0, 2);
        tlp.Controls.Add(_detailTabs, 0, 3);
        tlp.Controls.Add(meta, 0, 4);
        tlp.Controls.Add(vndb, 0, 5);
        tlp.Controls.Add(_raCard, 0, 6);
        tlp.Controls.Add(_storeAchCard, 0, 7);
        tlp.Controls.Add(notes, 0, 8);
        tlp.Controls.Add(_related, 0, 9);
        _highScores = new Mame.HighScoresPanel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0) };
        tlp.Controls.Add(_highScores, 0, 10);
        // After an auto-submit, drop the stale board and reload if the HIGH SCORES tab is showing this rom.
        Mame.MameHighScoreSubmit.Submitted += rom =>
        {
            try { if (IsHandleCreated) BeginInvoke((Action)(() => _highScores?.InvalidateRom(rom, _detailTabSel == 2))); }
            catch { }
        };
        return tlp;
    }

    /// <summary>Tab flip: remember, reset the pane scroll, lazily run the suggester on first Related view.</summary>
    private void OnDetailTabChanged(int idx)
    {
        _detailTabSel = idx;
        if (idx == 1) _related?.EnsureLoaded();
        if (idx == 2) _highScores?.EnsureLoaded();   // MAME leaderboards fetched only on first flip to the tab
        if (_detailHost != null) { try { _detailHost.AutoScrollPosition = new Point(0, 0); } catch { } }
        RelayoutDetail();
    }

    /// <summary>Parental gate for suggester candidates — the desktop mirror of RelatedProvider's web filter.</summary>
    private bool RelatedCandidateAllowed(CandidateGame c)
    {
        if (!ParentalBridge.Active) return true;
        if (c.IsLocal && ParentalBridge.IsGameBlocked(c.Id)) return false;   // per-game "requires parental" flag
        if (!ParentalBridge.IsRatingAllowed(c.Rating ?? "")) return false;
        var plat = c.Platform ?? "";
        if (plat.Length > 0 && _parentalHiddenPlatforms.Contains(plat)) return false;
        return true;
    }

    /// <summary>Box-art path for a LOCAL suggestion card — the same source chain as the detail pane.
    /// Called off the UI thread by the related panel's background run.</summary>
    private string RelatedLocalArt(string gameId)
    {
        var g = Safe(() => _dm?.GetGameById(gameId));
        return g == null ? null : DetailImageSources(g, allow3d: false).artSrc;   // cards need a real file
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
        if (host == null || tlp == null || tlp.RowStyles.Count < 11 || _inRelayout) return;
        _inRelayout = true;
        try { RelayoutDetailCore(host, tlp); }
        finally { _inRelayout = false; }
    }

    // Tab strip height (game mode) and the minimum Related-panel height before the pane scrolls.
    private const int DetailTabsH = 30;
    private const int MinRelatedH = 240;

    private void RelayoutDetailCore(Panel host, TableLayoutPanel tlp)
    {
        int sbw = SystemInformation.VerticalScrollBarWidth;
        int hsbh = SystemInformation.HorizontalScrollBarHeight;
        int fullW = host.ClientSize.Width + (host.VerticalScroll.Visible ? sbw : 0);     // width with NO vertical scrollbar
        int viewH = host.ClientSize.Height + (host.HorizontalScroll.Visible ? hsbh : 0); // height with NO horizontal scrollbar
        if (fullW < 80 || viewH < 80) return;
        int padH = tlp.Padding.Horizontal, padV = tlp.Padding.Vertical;

        // Tab strip shows only for a game (nodes keep the classic pane); Related mode swaps the
        // overview rows (meta/vndb/ra/store/notes) for the related panel, which then absorbs the slack.
        bool gameMode = _detailsShown is IGame;
        int tabH = gameMode ? DetailTabsH : 0;
        bool relatedMode = gameMode && _detailTabSel == 1;
        bool highScoresMode = gameMode && _detailTabSel == 2;   // HIGH SCORES tab: swaps overview rows for the leaderboards panel

        // Minimum content height for a given grid width (media capped to the viewport).
        int MinContent(int gridW, out int mediaH, out int metaH, out int vndbH, out int raH, out int storeH)
        {
            int colW = Math.Max(20, gridW - padH);
            mediaH = (int)Math.Round(colW / _mediaAspect);
            int cap = (int)(viewH * 0.62);
            if (cap > 100 && mediaH > cap) mediaH = cap;
            if (mediaH < 90) mediaH = 90;
            if (relatedMode || highScoresMode)
            {
                metaH = vndbH = raH = storeH = 0;
                return padV + 158 + mediaH + _stripRowH + tabH + MinRelatedH;
            }
            metaH = _meta.HeightForWidth(colW);
            vndbH = _vndb.HeightForWidth(colW);
            raH = _raCard?.HeightForWidth(colW) ?? 0;
            storeH = _storeAchCard?.HeightForWidth(colW) ?? 0;
            return padV + 158 + mediaH + _stripRowH + tabH + metaH + vndbH + raH + storeH + MinNotesH;
        }

        bool overflow = MinContent(fullW, out _, out _, out _, out _, out _) > viewH;
        int wantW = overflow ? Math.Max(80, fullW - sbw) : fullW;
        int minContent = MinContent(wantW, out int media, out int meta, out int vndb, out int ra, out int store);

        var rsMedia = tlp.RowStyles[1];
        if (rsMedia.SizeType != SizeType.Absolute || Math.Abs(rsMedia.Height - media) > 0.5) { rsMedia.SizeType = SizeType.Absolute; rsMedia.Height = media; }
        var rsStrip = tlp.RowStyles[2];
        if (rsStrip.SizeType != SizeType.Absolute || Math.Abs(rsStrip.Height - _stripRowH) > 0.5) { rsStrip.SizeType = SizeType.Absolute; rsStrip.Height = _stripRowH; }
        var rsTabs = tlp.RowStyles[3];
        if (rsTabs.SizeType != SizeType.Absolute || Math.Abs(rsTabs.Height - tabH) > 0.5) { rsTabs.SizeType = SizeType.Absolute; rsTabs.Height = tabH; }
        var rsMeta = tlp.RowStyles[4];
        if (rsMeta.SizeType != SizeType.Absolute || Math.Abs(rsMeta.Height - meta) > 0.5) { rsMeta.SizeType = SizeType.Absolute; rsMeta.Height = meta; }
        var rsVndb = tlp.RowStyles[5];
        if (rsVndb.SizeType != SizeType.Absolute || Math.Abs(rsVndb.Height - vndb) > 0.5) { rsVndb.SizeType = SizeType.Absolute; rsVndb.Height = vndb; }
        var rsRa = tlp.RowStyles[6];
        if (rsRa.SizeType != SizeType.Absolute || Math.Abs(rsRa.Height - ra) > 0.5) { rsRa.SizeType = SizeType.Absolute; rsRa.Height = ra; }
        var rsStore = tlp.RowStyles[7];
        if (rsStore.SizeType != SizeType.Absolute || Math.Abs(rsStore.Height - store) > 0.5) { rsStore.SizeType = SizeType.Absolute; rsStore.Height = store; }

        // Notes vs Related: exactly one of the two absorbs the leftover space (Percent 100); the
        // other collapses. In Related overflow, the related panel gets its fixed minimum instead.
        var rsNotes = tlp.RowStyles[8];
        var rsRelated = tlp.RowStyles[9];
        var rsHs = tlp.RowStyles[10];
        void Collapse(RowStyle rs) { if (rs.SizeType != SizeType.Absolute || rs.Height != 0) { rs.SizeType = SizeType.Absolute; rs.Height = 0; } }
        void FillPane(RowStyle rs)   // like Related: fixed minimum on overflow, else absorbs the slack
        {
            if (overflow) { if (rs.SizeType != SizeType.Absolute || Math.Abs(rs.Height - MinRelatedH) > 0.5) { rs.SizeType = SizeType.Absolute; rs.Height = MinRelatedH; } }
            else if (rs.SizeType != SizeType.Percent) { rs.SizeType = SizeType.Percent; rs.Height = 100; }
        }
        if (relatedMode) { Collapse(rsNotes); FillPane(rsRelated); Collapse(rsHs); }
        else if (highScoresMode) { Collapse(rsNotes); Collapse(rsRelated); FillPane(rsHs); }
        else
        {
            if (rsNotes.SizeType != SizeType.Percent) { rsNotes.SizeType = SizeType.Percent; rsNotes.Height = 100; }
            Collapse(rsRelated); Collapse(rsHs);
        }

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
    // Multi-select is hand-rolled (native TreeView is single-select): Ctrl+click toggles, Shift+click selects
    // a visible range; extra nodes are painted with the selection colour. The list shows the UNION of the
    // selected nodes' games (deduped); right-click on a HOMOGENEOUS selection offers multi-edit.
    private readonly List<TreeNode> _multiSel = new();
    private TreeNode _multiAnchor;
    private static readonly object MultiUnionSentinel = new();

    private void PaintMultiSel(TreeView tv)
    {
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                bool sel = _multiSel.Count > 1 && _multiSel.Contains(n);
                n.BackColor = sel ? Color.FromArgb(60, 90, 130) : Color.Empty;
                n.ForeColor = sel ? Color.White : Color.Empty;
                Walk(n.Nodes);
            }
        }
        try { tv.BeginUpdate(); Walk(tv.Nodes); } finally { tv.EndUpdate(); }
    }

    private static List<TreeNode> VisibleRange(TreeView tv, TreeNode a, TreeNode b)
    {
        var visible = new List<TreeNode>();
        for (var n = tv.Nodes.Count > 0 ? tv.Nodes[0] : null; n != null; n = n.NextVisibleNode) visible.Add(n);
        int ia = visible.IndexOf(a), ib = visible.IndexOf(b);
        if (ia < 0 || ib < 0) return new List<TreeNode> { b };
        return visible.GetRange(Math.Min(ia, ib), Math.Abs(ib - ia) + 1);
    }

    private void LoadUnion()
    {
        var tags = _multiSel.Select(n => n.Tag).Where(t => t != null).Distinct().ToList();
        if (tags.Count <= 1) { if (tags.Count == 1) LoadNode(tags[0]); return; }
        _currentNode = MultiUnionSentinel;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var union = new List<IGame>();
        foreach (var t in tags)
        {
            IEnumerable<IGame> src;
            try
            {
                src = t is AllNode ? _dm.GetAllGames()
                    : t is GroupNode gn ? gn.Games
                    : t is IPlatformCategory cat ? cat.GetAllGames(true, true)
                    : t is IPlaylist pl ? pl.GetAllGames(true)
                    : t is IPlatform p ? p.GetAllGames(true, true)
                    : Array.Empty<IGame>();
            }
            catch { src = Array.Empty<IGame>(); }
            foreach (var g in src ?? Array.Empty<IGame>())
            {
                string id = S(Safe(() => g.Id));
                if (id.Length == 0 || seen.Add(id)) union.Add(g);
            }
        }
        _current = union.ToArray();
        ClearTransientTypedFilter();
        // A union is not a playlist: it must drop any order the previously selected playlist
        // imposed. Without this, leaving a Manual playlist for a multi-selection kept both the
        // "Manual" label and that playlist's ranks, so every game outside it tied at int.MaxValue
        // and the union came out unsorted.
        ActivateNodeSort(_currentNode, NodeKeyForUnion(tags));
        ApplySort();
        try { ShowNodeDetails(_multiSel[_multiSel.Count - 1].Tag); } catch { }
    }

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
        tv.AfterSelect += (_, e) =>
        {
            // Plain selection (keyboard / simple click) resets the multi-selection; with Ctrl/Shift held the
            // NodeMouseClick handler below owns the set and the union load.
            if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0)
            {
                _multiSel.Clear();
                if (e.Node != null) { _multiSel.Add(e.Node); _multiAnchor = e.Node; }
                PaintMultiSel(tv);
                if (e.Node?.Tag != null) LoadNode(e.Node.Tag);
            }
        };
        tv.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || e.Node == null) return;
            bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
            if (!ctrl && !shift)
            {
                // Plain click is handled by AfterSelect — EXCEPT when re-clicking the natively-selected node
                // while a multi-selection is active (AfterSelect won't fire): collapse the set here.
                if (_multiSel.Count > 1)
                {
                    _multiSel.Clear(); _multiSel.Add(e.Node); _multiAnchor = e.Node;
                    PaintMultiSel(tv);
                    if (e.Node.Tag != null) LoadNode(e.Node.Tag);
                }
                return;
            }
            if (ctrl)
            {
                if (!_multiSel.Remove(e.Node)) _multiSel.Add(e.Node);
                if (_multiSel.Count == 0) _multiSel.Add(e.Node);
                _multiAnchor = e.Node;
            }
            else if (_multiAnchor != null)
            {
                _multiSel.Clear();
                _multiSel.AddRange(VisibleRange(tv, _multiAnchor, e.Node));
            }
            else { _multiSel.Add(e.Node); _multiAnchor = e.Node; }
            PaintMultiSel(tv);
            if (_multiSel.Count > 1) LoadUnion();
            else if (_multiSel.Count == 1 && _multiSel[0].Tag != null) LoadNode(_multiSel[0].Tag);
        };
        // After an editor closes: re-read Parents.xml into the in-memory hierarchy and rebuild the source
        // tree (renames / parent-membership changes show immediately), keeping the edited node selected.
        void RefreshAfterEdit(object keep)
        {
            try
            {
                (_dm as HostDataManagerXml)?.ReloadHierarchy();
                PopulateSources();
                if (_treeNodeMap.TryGetValue(keep, out var tn2)) { _sources.SelectedNode = tn2; try { tn2.EnsureVisible(); } catch { } }
            }
            catch (Exception ex) { Console.WriteLine("[editnode] refresh: " + ex.Message); }
        }
        tv.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.Node?.Tag == null) return;
            // Limited mode: the source-tree context menu is entirely admin (edit / delete / copy / paste
            // platforms, categories, playlists), so it is suppressed wholesale — select + launch only.
            if (Media.ParentalBridge.Active) return;
            var tag = e.Node.Tag;
            bool ro = (_dm as HostDataManagerXml)?.ReadOnly ?? false;
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Panel2, ForeColor = Fg };

            // Right-click INSIDE a multi-selection → homogeneous multi-edit; mixed types get no edit entry.
            if (_multiSel.Count > 1 && _multiSel.Contains(e.Node))
            {
                var tags = _multiSel.Select(n2 => n2.Tag).Where(t => t != null).Distinct().ToList();
                void AfterDelete(bool did) { if (!did) return; _currentNode = null; RefreshAfterEdit(AllNode.Instance); }
                if (tags.Count > 1 && tags.All(t => t is IPlatform))
                {
                    var list = tags.Cast<IPlatform>().ToList();
                    var it = new ToolStripMenuItem($"Edit {list.Count} Platforms…");
                    it.Click += (_, _) => { try { Platforms.MultiEditWindow.OpenPlatforms(list, ro, this); } catch (Exception ex) { Console.WriteLine("[multiedit] " + ex.Message); } RefreshAfterEdit(tags[0]); };
                    menu.Items.Add(it);
                    if (!ro)
                    {
                        var del = new ToolStripMenuItem($"Delete {list.Count} Platforms…");
                        del.Click += (_, _) => { try { AfterDelete(Platforms.NodeDeleter.DeletePlatforms(list, _dm as HostDataManagerXml, this)); } catch (Exception ex) { Console.WriteLine("[delete] " + ex.Message); } };
                        menu.Items.Add(del);
                    }
                }
                else if (tags.Count > 1 && tags.All(t => t is HostPlatformCategory))
                {
                    var list = tags.Cast<HostPlatformCategory>().ToList();
                    var it = new ToolStripMenuItem($"Edit {list.Count} Categories…");
                    it.Click += (_, _) => { try { Platforms.MultiEditWindow.OpenCategories(list, ro, this); } catch (Exception ex) { Console.WriteLine("[multiedit] " + ex.Message); } RefreshAfterEdit(tags[0]); };
                    menu.Items.Add(it);
                    if (!ro)
                    {
                        var del = new ToolStripMenuItem($"Delete {list.Count} Categories…");
                        del.Click += (_, _) => { try { AfterDelete(Platforms.NodeDeleter.DeleteCategories(list, _dm as HostDataManagerXml, this)); } catch (Exception ex) { Console.WriteLine("[delete] " + ex.Message); } };
                        menu.Items.Add(del);
                    }
                }
                else if (tags.Count > 1 && tags.All(t => t is Data.HostPlaylist))
                {
                    var list = tags.Cast<Data.HostPlaylist>().ToList();
                    var it = new ToolStripMenuItem($"Edit {list.Count} Playlists…");
                    it.Click += (_, _) => { try { Platforms.MultiEditWindow.OpenPlaylists(list, ro, this); } catch (Exception ex) { Console.WriteLine("[multiedit] " + ex.Message); } RefreshAfterEdit(tags[0]); };
                    menu.Items.Add(it);
                    // Copier n'écrit rien : proposé même en lecture seule.
                    var cp = new ToolStripMenuItem($"Copy {list.Count} Playlists");
                    cp.Click += (_, _) => { try { Platforms.PlaylistCopier.Copy(list, _dm as HostDataManagerXml, this); } catch (Exception ex) { Console.WriteLine("[plcopy] " + ex.Message); } };
                    menu.Items.Add(cp);
                    if (!ro)
                    {
                        var del = new ToolStripMenuItem($"Delete {list.Count} Playlists…");
                        del.Click += (_, _) => { try { AfterDelete(Platforms.NodeDeleter.DeletePlaylists(list, _dm as HostDataManagerXml, this)); } catch (Exception ex) { Console.WriteLine("[delete] " + ex.Message); } };
                        menu.Items.Add(del);
                    }
                }
                if (menu.Items.Count > 0) menu.Show(tv, e.Location);
                return;
            }

            // "Paste" n'apparaît que si on sait où atterrir : la plateforme la plus proche EN REMONTANT
            // depuis le nœud cliqué (lui-même s'il en est une). Une catégorie hors de toute plateforme
            // n'en propose pas — une entrée grisée sans explication serait pire que pas d'entrée.
            void AddPasteItem(object destNode)
            {
                if (ro || Platforms.PlaylistCopier.ClipboardCount == 0) return;
                var destPlat = Platforms.PlaylistCopier.ResolveDestPlatform(e.Node);
                if (destPlat == null) return;
                int n = Platforms.PlaylistCopier.ClipboardCount;
                var it = new ToolStripMenuItem(n == 1 ? "Paste Playlist" : $"Paste {n} Playlists");
                it.Click += (_, _) =>
                {
                    try
                    {
                        var made = Platforms.PlaylistCopier.Paste(destNode, destPlat, _dm as HostDataManagerXml, this);
                        if (made.Count > 0) RefreshAfterEdit(made[0]);
                    }
                    catch (Exception ex) { Console.WriteLine("[plcopy] paste: " + ex.Message); }
                };
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(it);
            }

            if (tag is IPlatform plat)
            {
                tv.SelectedNode = e.Node;
                var edit = new ToolStripMenuItem("Edit Platform…");
                edit.Click += (_, _) =>
                {
                    try { Platforms.EditPlatformWindow.Open(plat, ro, this, MediaResolver.LbRoot ?? ""); } catch (Exception ex) { Console.WriteLine("[editplat] " + ex.Message); }
                    RefreshAfterEdit(plat);
                };
                menu.Items.Add(edit);
                // "Documents" submenu (below Edit, LB-style) — only when the platform has at least one document.
                try
                {
                    var docs = Platforms.EditPlatformDocuments.GetDocuments(plat.Name);
                    if (docs.Count > 0)
                    {
                        var docMenu = new ToolStripMenuItem("Documents");
                        docMenu.DropDown.Renderer = new DarkRenderer();
                        docMenu.DropDown.BackColor = Panel2; docMenu.DropDown.ForeColor = Fg;
                        foreach (var (dn, abs) in docs)
                        {
                            var it = new ToolStripMenuItem(dn) { ToolTipText = abs };
                            it.Click += (_, _) =>
                            {
                                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(abs) { UseShellExecute = true }); }
                                catch (Exception ex) { Console.WriteLine("[platdoc] " + ex.Message); }
                            };
                            docMenu.DropDownItems.Add(it);
                        }
                        menu.Items.Add(docMenu);
                    }
                }
                catch { }
                if (!ro)
                {
                    var del = new ToolStripMenuItem("Delete Platform…");
                    del.Click += (_, _) => { try { if (Platforms.NodeDeleter.DeletePlatforms(new List<IPlatform> { plat }, _dm as HostDataManagerXml, this)) { _currentNode = null; RefreshAfterEdit(AllNode.Instance); } } catch (Exception ex) { Console.WriteLine("[delete] " + ex.Message); } };
                    menu.Items.Add(del);
                }
                AddPasteItem(plat);
            }
            else if (tag is HostPlatformCategory cat)
            {
                tv.SelectedNode = e.Node;
                var edit = new ToolStripMenuItem("Edit Category…");
                edit.Click += (_, _) =>
                {
                    try { Platforms.EditCategoryWindow.Open(cat, ro, this); } catch (Exception ex) { Console.WriteLine("[editcat] " + ex.Message); }
                    RefreshAfterEdit(cat);
                };
                menu.Items.Add(edit);
                if (!ro)
                {
                    var del = new ToolStripMenuItem("Delete Category…");
                    del.Click += (_, _) => { try { if (Platforms.NodeDeleter.DeleteCategories(new List<HostPlatformCategory> { cat }, _dm as HostDataManagerXml, this)) { _currentNode = null; RefreshAfterEdit(AllNode.Instance); } } catch (Exception ex) { Console.WriteLine("[delete] " + ex.Message); } };
                    menu.Items.Add(del);
                }
                AddPasteItem(cat);
            }
            else if (tag is Data.HostPlaylist pl)
            {
                tv.SelectedNode = e.Node;
                var edit = new ToolStripMenuItem("Edit Playlist…");
                edit.Click += (_, _) =>
                {
                    try { Platforms.EditPlaylistWindow.Open(pl, ro, this); } catch (Exception ex) { Console.WriteLine("[editpl] " + ex.Message); }
                    RefreshAfterEdit(pl);
                };
                menu.Items.Add(edit);
                var cpOne = new ToolStripMenuItem("Copy Playlist");
                cpOne.Click += (_, _) => { try { Platforms.PlaylistCopier.Copy(new List<Data.HostPlaylist> { pl }, _dm as HostDataManagerXml, this); } catch (Exception ex) { Console.WriteLine("[plcopy] " + ex.Message); } };
                menu.Items.Add(cpOne);
                if (!ro)
                {
                    var del = new ToolStripMenuItem("Delete Playlist…");
                    del.Click += (_, _) => { try { if (Platforms.NodeDeleter.DeletePlaylists(new List<Data.HostPlaylist> { pl }, _dm as HostDataManagerXml, this)) { _currentNode = null; RefreshAfterEdit(AllNode.Instance); } } catch (Exception ex) { Console.WriteLine("[delete] " + ex.Message); } };
                    menu.Items.Add(del);
                }
            }
            else return;

            menu.Show(tv, e.Location);
        };
        return tv;
    }

    // ── Left panel header: quick search + advanced-filter funnel ─────────────
    // The search box sits above the tree it filters, with the funnel beside it, instead of on the
    // toolbar: that's where LaunchBox puts it, and it puts the two filtering controls — the text one
    // and the criteria one — next to each other rather than at opposite ends of the window.
    private Control BuildSearchRow()
    {
        float s = LiteBoxTheme.DpiScale(this);
        int h = (int)Math.Round(30 * s), gap = (int)Math.Round(6 * s), pad = (int)Math.Round(8 * s);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Panel2, ForeColor = Fg,
            Font = new Font("Segoe UI", 10f), PlaceholderText = "Search",
        };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); ReflectQuickFilter(); };
        // Éditer le champ à la main sort le filtre du régime transitoire : il ne doit plus
        // disparaître en changeant de plateforme.
        _search.KeyDown += (_, _) => _typedFilterIsTransient = false;

        _searchWrap = new RoundedField
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Panel,
            Padding = new Padding((int)Math.Round(11 * s), 0, (int)Math.Round(8 * s), 0),
        };
        _searchWrap.Controls.Add(_search);

        _filterBtn = new FilterGlyphButton { Dock = DockStyle.Fill, Margin = new Padding(gap, 0, 0, 0), BackColor = Panel };
        _filterBtn.Click += (_, _) => OpenFilterDialog();
        _filterBtn.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ClearAdvancedFilter(); };
        _tips.SetToolTip(_filterBtn, "Advanced search filter");

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = h,
            BackColor = Panel, Padding = Padding.Empty,
            Margin = new Padding(pad, pad, pad, (int)Math.Round(6 * s)),
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, h + gap));   // funnel = square, + its left gap
        row.Controls.Add(_searchWrap, 0, 0);
        row.Controls.Add(_filterBtn, 1, 0);
        return row;
    }

    // ── "Group by" view selector (above the source tree) ─────────────────────
    private ThemedDropDown BuildViewCombo()
    {
        float s = LiteBoxTheme.DpiScale(this);
        var cb = new ThemedDropDown
        {
            Dock = DockStyle.Top, Height = (int)Math.Round(32 * s), BackColor = Panel,
            ForeColor = Fg, Font = new Font("Segoe UI", 9.75f),
            Margin = new Padding((int)Math.Round(8 * s), 0, (int)Math.Round(8 * s), (int)Math.Round(8 * s)),
            MenuRenderer = new DarkRenderer(),
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
        _multiSel.Clear(); _multiAnchor = null;   // tree nodes are recreated — drop the multi-selection
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

    // Build a TreeNode for a source object (Tag = the object), recursing into category AND platform children
    // (LB allows playlists/categories nested under a platform). Multi-parent means the same object can appear
    // at several places; `path` guards against Parents.xml cycles on the current branch.
    private TreeNode BuildTreeNode(object obj) => BuildTreeNode(obj, new HashSet<object>());
    private TreeNode BuildTreeNode(object obj, HashSet<object> path)
    {
        string text = obj is AllNode ? "All Games" : obj is GroupNode gn ? gn.Label : (HostPlatformCategory.NodeName(obj) ?? "");
        string imgKey = _nodeIconKey.TryGetValue(obj, out var k) ? k : "fb_plat";
        var tn = new TreeNode(text) { Tag = obj, ImageKey = imgKey, SelectedImageKey = imgKey };
        _treeNodeMap[obj] = tn;
        path.Add(obj);
        System.Collections.Generic.IEnumerable<object> kids =
            obj is HostPlatformCategory c ? (System.Collections.Generic.IEnumerable<object>)c.Children
            : obj is Data.HostPlatform hp ? hp.TreeChildren
            : obj is GroupNode gc && gc.Children != null ? gc.Children
            : Array.Empty<object>();
        foreach (var child in kids)
        {
            if (path.Contains(child)) continue;                              // cycle guard
            if (obj is not GroupNode && ParentalHidesNode(child)) continue;  // parental: drop hidden children
            tn.Nodes.Add(BuildTreeNode(child, path));
        }
        path.Remove(obj);
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
            // c.Width is the user's BASE width (their drag intent), NOT the AutoFit-computed header
            // width — for the Stretch column (Title) it's the fill FLOOR, for the others the shrink
            // CAP — so it's a real preference worth persisting for every column.
            _cfg.Set("Col." + c.Key, $"{c.Width},{(c.Visible ? 1 : 0)},{di}");
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
    // Options → Plugins : a checkbox per folder under <LB>\Plugins, stored as
    // LiteBox.ini EnabledPlugins. Default (never configured) = every present folder checked.
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
            Text = "Plugins to load (subfolders of " + (HostBoot.PluginsRoot ?? @"<LB>\Plugins")
                 + (HostBoot.SystemPluginsRoot != null ? @", plus LB 14's System\Plugins" : "") + ").\r\n"
                 + "Changes apply on the next LiteBox restart.",
        };

        string root = HostBoot.PluginsRoot ?? "";
        var folders = HostBoot.ListPluginFolders(root);
        var enabled = _cfg.GetEnabledPluginsOrNull();          // null ⇒ all (never configured)
        bool defaultAll = enabled == null;
        var enabledSet = new HashSet<string>(enabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        // LB 14 system plugins (<LB>\System\Plugins — the integration plugins LB moved out of Plugins\).
        // Same persisted names as pre-14, listed in their own group below. A name present in BOTH roots
        // is shown once, there: the system copy is the one discovery actually loads.
        var sysFolders = HostBoot.SystemPluginsRoot != null
            ? HostBoot.ListPluginFolders(HostBoot.SystemPluginsRoot) : new List<string>();
        var sysSet = new HashSet<string>(sysFolders, StringComparer.OrdinalIgnoreCase);

        var checks = new List<CheckBox>();
        if (folders.Count == 0 && sysFolders.Count == 0)
        {
            flow.Controls.Add(new Label { AutoSize = true, ForeColor = SubFg, Margin = new Padding(2, 6, 2, 2),
                Text = "No plugin folders found in " + root });
        }
        foreach (var f in folders)
        {
            if (sysSet.Contains(f)) continue;   // stale pre-14 leftover — the System\Plugins copy loads
            // ExtendDB is integrated into LiteBox → shown greyed, never loaded, never part of the enabled set.
            if (HostBoot.IntegrateExtendDb && HostBoot.IsExtendDb(f))
            {
                flow.Controls.Add(new CheckBox
                {
                    Text = f + "   —  integrated into LiteBox", AutoSize = true, ForeColor = SubFg,
                    Enabled = false, Checked = false, Margin = new Padding(2, 5, 2, 0),
                });
                flow.Controls.Add(new Label
                {
                    Text = "Its functionality is now built into LiteBox, so the plugin is not loaded.",
                    AutoSize = true, ForeColor = SubFg, Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                    Margin = new Padding(24, 0, 2, 6),
                });
                continue;
            }
            // Our companion parental plugin: deployed for vanilla LaunchBox/BigBox by Options → Parental →
            // Install, never loaded under LiteBox. Shown greyed so it isn't mistaken for a togglable plugin.
            if (HostBoot.IsNativeParental(f))
            {
                flow.Controls.Add(new CheckBox
                {
                    Text = f + "   —  LiteBox parental (vanilla LaunchBox / BigBox)", AutoSize = true, ForeColor = SubFg,
                    Enabled = false, Checked = false, Margin = new Padding(2, 5, 2, 0),
                });
                flow.Controls.Add(new Label
                {
                    Text = "Managed by Options → Parental → Install; enforces parental control inside vanilla LaunchBox/BigBox and is never loaded under LiteBox.",
                    AutoSize = true, ForeColor = SubFg, Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                    Margin = new Padding(24, 0, 2, 6),
                });
                continue;
            }
            var cb = new CheckBox
            {
                Text = f, AutoSize = true, ForeColor = Fg, Margin = new Padding(2, 5, 2, 5),
                Checked = defaultAll || enabledSet.Contains(f),
            };
            checks.Add(cb);
            flow.Controls.Add(cb);
        }

        if (sysFolders.Count > 0)
        {
            flow.Controls.Add(new Label
            {
                Text = @"LaunchBox 14 plugins (System\Plugins) — installed and updated by LaunchBox:",
                AutoSize = true, ForeColor = SubFg, Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                Margin = new Padding(2, 12, 2, 2),
            });
            foreach (var f in sysFolders)
            {
                // Text must stay the exact folder name — apply() persists cb.Text into EnabledPlugins=.
                var cb = new CheckBox
                {
                    Text = f, AutoSize = true, ForeColor = Fg, Margin = new Padding(2, 5, 2, 5),
                    Checked = defaultAll || enabledSet.Contains(f),
                };
                checks.Add(cb);
                flow.Controls.Add(cb);
            }
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

    // RetroAchievements scanning now lives entirely in the RA options page (RaPanel + RaPanelActions,
    // which use PluginHelper.DataManager directly), so MainWindow no longer hosts a scan launcher.

    // Display options, organised into internal tabs (General / Middle · List /
    // Middle · Poster / Right panel). The Right panel tab stacks its OptionItems (16:9, load delay) over
    // the media-layout editor (immediate image per view + the ordered post-load image list).
    private void BuildDisplaySection(Options.OptionsWindow w)
    {
        float dpi = LiteBoxTheme.DpiScale(this);
        int RS(int px) => (int)Math.Round(px * dpi);

        var general = new[]
        {
            Options.OptionItem.Choice("Display", "Title sort normalization",
                new[] { "without", "simple", "advanced" },
                () => TitleSortNormalizer.ConfigValue(_cfg.TitleSortNormalizationMode),
                v => _cfg.TitleSortNormalizationMode = TitleSortNormalizer.Parse(v),
                "without: use the raw Title and ignore Sort Title. simple: use Sort Title when set, then remove "
                + "a leading The/A/An and punctuation. advanced: use Sort Title when set, then remove bracket "
                + "annotations and articles, separate punctuation, and convert Roman numerals II-VIII.",
                applyLive: ApplyTitleSortNormalization),
            // The pair binds LB 14's OWN Settings.xml keys, so LiteBox and LaunchBox edit the same
            // switch. Against a pre-14 LB the keys are ProblemKeys-routed to LiteBox's DB (a 13.x
            // rewrite would strip the unknown names) — same value, different shelf.
            Options.OptionItem.Toggle("Display", "Game list index (replaces the scrollbars)",
                () => LbSettings?.GetBool("UseArrangeScrollBar", true) ?? true,
                v => LbSettings?.SetBool("UseArrangeScrollBar", v),
                "LaunchBox's Game List Index (its 'Show Game List Index' option — the setting is shared): "
                + "the list AND poster scrollbars become an index of the current Arrange By groups — letters when "
                + "sorted by Title, the values themselves for any other sort. Click or drag it to jump "
                + "straight to a group; a group too thin to spell its label keeps a dot marker (hover "
                + "names it).",
                applyLive: ApplyGameListIndexOptions),
            Options.OptionItem.Toggle("Display", "Game list index: mini bar when retracted",
                () => _cfg.GetBool("GameListIndexMini", true), v => _cfg.SetBool("GameListIndexMini", v),
                "Only matters with 'always show' OFF. On (default): the retracted strip still shows a "
                + "slim dots-only index (its sliver of space stays reserved either way); hovering it "
                + "unfolds the full bar over the content. Off: the retracted strip stays blank.",
                applyLive: ApplyGameListIndexOptions),
            Options.OptionItem.Toggle("Display", "Game list index: always show the markers",
                () => LbSettings?.GetBool("AlwaysShowArrangeScrollBar", true) ?? true,
                v => LbSettings?.SetBool("AlwaysShowArrangeScrollBar", v),
                "LB's 'Always show the Game List Index' (shared setting). On (default): the markers stay "
                + "visible and the strip keeps its room. Off: a slim blank strip until the pointer reaches "
                + "the list's right edge. The dark background only ever appears while the index is being "
                + "used, either way.",
                applyLive: ApplyGameListIndexOptions),
            Options.OptionItem.Toggle("Display", "Use the image cache (degraded thumbnails)",
                () => _cfg.UseImageCache, v => _cfg.UseImageCache = v,
                applyLive: () => _useImageCache = _cfg.UseImageCache),
            Options.OptionItem.Toggle("Display", "Use game cache (when ExtendDB absent)",
                () => _cfg.UseGameCache, v => _cfg.UseGameCache = v,
                "Builds an in-memory media cache (Everything-backed) when the ExtendDB plugin isn't loaded.",
                applyLive: ApplyGameCacheOption),
            Options.OptionItem.Toggle("Display", "Unload the game cache while a game runs",
                () => _cfg.UnloadGameCacheDuringGame, v => _cfg.UnloadGameCacheDuringGame = v),
            Options.OptionItem.Action("Display", "Edit colours…", ShowColorEditor,
                "Customise the shared LiteBox palette. Takes full effect after restarting LiteBox."),
            // ── 3D model validity ──
            // Lives HERE and not in the right-panel tab: it gates far more than that pane — the media list,
            // the instant path's key index, the detail overlay, the fullscreen viewer AND whether a model is
            // baked at all (selection + bulk Generate Media Cache). Flat globals → LiteBox.ini like the rest
            // of this tab. applyLive drops the cached snapshot (the values are read per resolved game) and
            // recomputes the key index, since the eligible-game set just moved.
            Options.OptionItem.Toggle("Display", "3D model: also require a Box - Back scan",
                () => _cfg.Model3dRequireBack, v => _cfg.Model3dRequireBack = v,
                "The FRONT is always required — without it the case wears LaunchBox's 'NoImage' placeholder. "
                + "Tick this to only consider a model worth showing (and baking) when the game also has a back scan.",
                applyLive: Refresh3dValidity),
            Options.OptionItem.Toggle("Display", "3D model: also require a Box - Spine scan",
                () => _cfg.Model3dRequireSpine, v => _cfg.Model3dRequireSpine = v,
                "Same idea for the spine scan — the piece that makes the case's edge real rather than flat colour.",
                applyLive: Refresh3dValidity),
            Options.OptionItem.Choice("Display", "3D model: when both extra scans are required",
                new[] { "either one is enough", "need both" },
                () => _cfg.Model3dRequireBothScans ? "need both" : "either one is enough",
                v => _cfg.Model3dRequireBothScans = v == "need both",
                "Only matters when Back AND Spine are both ticked above.",
                applyLive: Refresh3dValidity),
            Options.OptionItem.Toggle("Display", "3D model: a Box - Full scan alone is enough",
                () => _cfg.Model3dAcceptFullScan, v => _cfg.Model3dAcceptFullScan = v,
                "A full scan composes the whole case by itself, so it satisfies the rule on its own. Counts only "
                + "for games where full-scan mode actually applies — that mode is set per platform and per game, "
                + "so this stays available whatever the global setting says.",
                applyLive: Refresh3dValidity),
            AchievementPointsOption(),
        };

        Options.OptionItem[] midList =
        [
            Options.OptionItem.Toggle("Display", "Auto-fit column widths to content",
                () => _cfg.GetBool("AutoFitColumns", true), v => _cfg.SetBool("AutoFitColumns", v),
                "On (default): the non-Title columns shrink to fit their content and the Title column grows to "
                + "fill the leftover space (never below the width you set for it). Off: every column, Title "
                + "included, keeps exactly the width you drag it to — classic manual sizing.",
                applyLive: () => _games.AutoFitColumns = _cfg.GetBool("AutoFitColumns", true)),
            Options.OptionItem.Toggle("Display", "Two-line rows (wrap long cell text)",
                () => _cfg.GetBool("TwoLineRows", true), v => _cfg.SetBool("TwoLineRows", v),
                "On (default): rows wrap long cell text onto a second line. Off: compact single-line rows, "
                + "truncated with an ellipsis, more games on screen.",
                applyLive: () => _games.TwoLineRows = _cfg.GetBool("TwoLineRows", true)),
            .. BadgeListOptions(),
        ];

        Options.OptionItem[] midPoster =
        [
            Options.OptionItem.Toggle("Display", "Poster grid: legacy owner-draw rendering (needs restart)",
                () => _cfg.GetBool("PosterOwnerDraw", false), v => _cfg.SetBool("PosterOwnerDraw", v),
                "Off (default): native image-list grid (smooth held-scroll). On: the previous owner-draw "
                + "renderer (rounded selection + hover grow, but can stutter). Takes effect after restart."),
            Options.OptionItem.Number("Display", "Poster grid: image memory allocation (1-10)",
                () => PosterMemLevel, v => _cfg.SetInt("PosterImageMemoryLevel", Math.Clamp(v, 1, 10)),
                min: 1, max: 10, step: 1,
                help: "How much memory the grid may spend keeping decoded box art and composited tiles, so "
                + "that scrolling back over games you have already seen redraws instead of re-reading the "
                + "disk. 4 is the default; 3 is what LiteBox kept before this setting existed, 10 goes all the "
                + "way to about 2 GB and 1 down to an eighth of the default. The budget is in BYTES, so "
                + "zooming in makes each tile bigger and simply fits fewer of them — it no longer multiplies "
                + "the memory by four. Currently: " + PosterMemEstimate(),
                applyLive: () => { RebuildPosterGeometry(); if (_posterMode) { RefreshPoster(); LayoutPoster(); } }),
            .. BadgePosterOptions(),
        ];

        // Right panel tab = OptionItems (16:9 + load delay) on top, media-layout editor below.
        Options.OptionItem[] rightOpts =
        [
            Options.OptionItem.Toggle("Display", "Use 16:9 for the main media (else poster ratio)",
                () => _cfg.Use169ForMainScreenshot, v => _cfg.Use169ForMainScreenshot = v,
                // Display only: the bake aspect is a constant, so this cannot change a single model. It
                // used to trigger a full key rebuild — seconds of work that provably produced the same
                // index. What it DOES need is the 3D options snapshot dropped, which nothing did.
                applyLive: () => { _mediaAspect = _cfg.Use169ForMainScreenshot ? (16.0 / 9.0) : (2.0 / 3.0); RelayoutDetail(); Model3d.Model3dOptions.Invalidate(); }),
            Options.OptionItem.Toggle("Display", "Videos: play automatically when selected",
                () => _cfg.VideoAutoplay, v => _cfg.VideoAutoplay = v,
                "On: a video starts as soon as it becomes the main media. Off (default): its still frame is "
                + "shown with a ▶ and playback waits for a click. Controls (play/pause, seek, mute) appear "
                + "when the mouse is over the media zone either way.",
                applyLive: () => { if (_mediaVideo != null) _mediaVideo.Autoplay = _cfg.VideoAutoplay; }),
            Options.OptionItem.Toggle("Display", "Videos: …with sound",
                () => _cfg.VideoAutoplaySound, v => _cfg.VideoAutoplaySound = v,
                "Only affects AUTOPLAY: off (default) an automatically started video is muted — a list that "
                + "talks while you scroll is unbearable. A video you start by CLICKING always has sound.",
                applyLive: () => { if (_mediaVideo != null) _mediaVideo.AutoplaySound = _cfg.VideoAutoplaySound; }),
            .. BadgeHeroOptions(),
            Options.OptionItem.Number("Display", "Detail load delay (ms)",
                () => _cfg.DetailLoadDelayMs, v => _cfg.DetailLoadDelayMs = v,
                min: 0, max: 5000, step: 50,
                help: "How long after selecting a game the deferred right-pane parts load (thumbnail strip, "
                + "full-res box, RA/store panels, fanart). Default 300 ms; 0 = immediate. Applies next selection."),
        ];
        var (rightRows, rightApply) = UiKit.OptionRows.Build(rightOpts, RS);
        var mediaPanel = new Media.MediaLayoutPanel();
        var rightTab = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = LiteBoxTheme.Bg };
        // Proportional, not a fixed 150px: that fixed height silently hid every option past the
        // fourth behind a scrollbar in a box too short to look scrollable.
        rightTab.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));
        rightTab.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        ((Control)rightRows).Dock = DockStyle.Fill;
        mediaPanel.Dock = DockStyle.Fill;
        rightTab.Controls.Add((Control)rightRows, 0, 0);
        rightTab.Controls.Add(mediaPanel, 0, 1);

        w.AddTabbedSection("Display", new (string, object, Action?)[]
        {
            ("General", general, null),
            ("Middle · List", midList, null),
            ("Middle · Poster", midPoster, null),
            ("Right panel", rightTab, () => { rightApply(); mediaPanel.Apply(); }),
        });
    }

    private void ApplyTitleSortNormalization()
    {
        _titleSortNormalization = _cfg.TitleSortNormalizationMode;
        if (string.Equals(_curSortKey, "title", StringComparison.OrdinalIgnoreCase)) ApplySort();
    }

    // A 3D-validity knob changed: drop the cached snapshot (Model3dOptions reads the LIVE _cfg, so the new
    // value is visible immediately, before the ini file is even written) and recompute the key index — the
    // set of games that may have a model just changed.
    private void Refresh3dValidity()
    {
        // These knobs decide whether a model is worth SHOWING, not what it contains — they are
        // deliberately outside the manifest so that tightening the rule never re-bakes. Dropping the
        // snapshot is therefore the whole job; no cached file becomes wrong.
        Model3d.Model3dOptions.Invalidate();
    }

    /// <param name="moduleTab">When set, the Modules section opens on that module's own tab (the parental
    /// padlock jumps straight to Parental). The caller still picks the SECTION via SelectSection.</param>
    private Options.OptionsWindow BuildOptionsWindow(Modules.LbModule? moduleTab = null)
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
            Options.OptionItem.Toggle("General", "Progress automation: sweep the whole library at startup",
                () => _cfg.ProgressSweepOnBoot, v => _cfg.ProgressSweepOnBoot = v,
                "Runs the Game Progress automation rules over EVERY game in the background right after "
                + "startup. Off (default): games are re-evaluated when selected and when a game exits — "
                + "the sweep is only useful to catch up a whole library at once. Needs 'Automatic Progress "
                + "Tracking' enabled in LB · Game Progress Automation."),
            Options.OptionItem.Toggle("General", "Progress automation: re-evaluate a game when selected",
                () => _cfg.ProgressApplyOnSelect, v => _cfg.ProgressApplyOnSelect = v,
                "Re-runs the Game Progress automation rules for a game while its details pane loads "
                + "(background, cheap). On by default. Needs 'Automatic Progress Tracking' enabled in "
                + "LB · Game Progress Automation."),
        });

        Notifications.NotificationOptions.Add(w, _cfg);

        var (pluginsPanel, applyPlugins) = BuildPluginsSection();
        w.AddSection("Plugins", pluginsPanel, applyPlugins);

        BuildDisplaySection(w);

        // (The standalone "Pause screen" section was merged into LB · Gameplay → Game Pause:
        //  "Use Game Pause Screen" is the master switch, "Pause Key" the hotkey, "Pause mode" the
        //  legacy/advanced choice — the old PauseEnabled ini key was dead, the hotkey duplicated.)

        // ExtendDB features, folded natively into LiteBox — enable/disable each + its own settings. LiteBox-own
        // state (litebox-options.db), so editable even in LB read-only mode, like the Caches maintenance below.
        {
            var (modPanel, modApply) = Options.ModulesOptions.Build(LiteBoxTheme.DpiScale(this), readOnly: false, moduleTab);
            w.AddSection("Modules", modPanel, modApply);
        }

        // RetroAchievements now lives entirely in the LB · Integrations → RetroAchievements tab (built
        // below by LbGlobalOptions/RaPanel): credentials + token auto-renewal + per-ROM hashing + grid.
        // No separate section here.

        // Similar Games — a standalone feature, NOT one of the modules; its own section (stub for now, filled by
        // the parallel port). LiteBox-own state, so editable even in LB read-only mode.
        {
            var (simPanel, simApply) = Options.SimilarOptions.Build(LiteBoxTheme.DpiScale(this), readOnly: false);
            w.AddSection("Similar Games", simPanel, simApply);
        }

        // LiteBox-local caches — a maintenance button (always enabled, even in read-only: it only
        // touches LiteBox's own Core cache folders, never the LaunchBox files).
        w.AddSection("Caches", BuildCachesSection());

        // LaunchBox GLOBAL settings (Settings.xml, write-back via the op-log +
        // scoped flush after the window closes). Greyed out in read-only mode.
        if (_dm is HostDataManagerXml hdm2)
        {
            Options.LbGlobalOptions.AddSections(w, hdm2.LbSettings, hdm2.ReadOnly, _cfg);   // share _cfg so ApplyFinished's Save persists the panel's LiteBox.ini edits (incl. the RA tab)
            // LB 14's Reader (provider, defaults, keyboard/controller mappings) — edits LaunchBox's
            // OWN Reader database, so both apps stay in sync. Self-hides on pre-14 installs.
            Options.ReaderOptions.AddSections(w, hdm2.ReadOnly, LiteBoxTheme.DpiScale(this));
        }

        // Danger zone — full self-uninstall. Last section.
        w.AddSection("Uninstall LiteBox", BuildUninstallSection());

        return w;
    }

    // ── LiteBox data & cache maintenance (Options → Caches) — see Host/Data/DataMaintenance for the catalog.

    // Options → Colors: swatch + hex + system color picker + per-color reset, for the shared theme.
    // Edits the live LiteBoxTheme immediately (so a newly-opened dialog reflects it); the section's
    // apply (LiteBoxTheme.Save) persists it. Already-built windows — the main window especially — pick
    // the new palette up on the next launch, hence the "restart" note.
    // Colour editor as its OWN modal dialog (opened from Display → "Edit colours…"). A section panel
    // hosted inside the Options window's nested AutoScroll host failed to create its window handle; a
    // standalone top-level dialog sidesteps that. One button per colour (the button IS the swatch):
    // left-click = picker, right-click = reset that colour. OK saves; Cancel restores the snapshot.
    private void ShowColorEditor()
    {
        float sc = LiteBoxTheme.DpiScale(this);
        int Z(int px) => (int)Math.Round(px * sc);
        static Color Ink(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140 ? Color.Black : Color.White;

        var swatches = LiteBoxTheme.Swatches;
        var snapshot = new Color[swatches.Length];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = swatches[i].Get();

        var form = new Form
        {
            Text = "LiteBox — Colours", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, ShowIcon = false, BackColor = Bg, ForeColor = Fg, Font = new Font("Segoe UI", 9f),
            ClientSize = new Size(Z(380), Z(44) + swatches.Length * Z(34) + Z(50)),
        };

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(Z(12), Z(10), Z(12), Z(6)),
        };
        var buttons = new Button[swatches.Length];
        void Style(int i) { var sw = swatches[i]; var b = buttons[i]; b.BackColor = sw.Get(); b.ForeColor = Ink(sw.Get()); b.Text = $"{sw.Name}    {LiteBoxTheme.ToHex(sw.Get())}"; }
        for (int i = 0; i < swatches.Length; i++)
        {
            int ix = i; var sw = swatches[i];
            var b = new Button
            {
                Size = new Size(Z(344), Z(30)), Margin = new Padding(0, Z(2), 0, Z(2)),
                FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Z(10), 0, 0, 0),
                Font = new Font("Segoe UI", 9f), FlatAppearance = { BorderColor = Color.FromArgb(80, 80, 84), BorderSize = 1 },
            };
            buttons[i] = b; Style(i);
            b.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { Color = sw.Get(), FullOpen = true, AnyColor = true };
                if (dlg.ShowDialog(form) == DialogResult.OK) { sw.Set(dlg.Color); Style(ix); }
            };
            b.MouseUp += (_, me) => { if (me.Button == MouseButtons.Right) { sw.Set(sw.Default); Style(ix); } };
            list.Controls.Add(b);
        }

        var footer = new Panel { Dock = DockStyle.Bottom, Height = Z(46), BackColor = Panel };
        var ok = new Button { Text = "OK", Size = new Size(Z(84), Z(28)), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Ok, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        var cancel = new Button { Text = "Cancel", Size = new Size(Z(84), Z(28)), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.CancelBtn, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        var resetAll = new Button { Text = "Reset all", Size = new Size(Z(90), Z(28)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        ok.Click += (_, _) => { LiteBoxTheme.Save(_cfg); form.DialogResult = DialogResult.OK; form.Close(); };
        cancel.Click += (_, _) => { for (int i = 0; i < snapshot.Length; i++) swatches[i].Set(snapshot[i]); form.DialogResult = DialogResult.Cancel; form.Close(); };
        resetAll.Click += (_, _) => { LiteBoxTheme.ResetAll(); for (int i = 0; i < buttons.Length; i++) Style(i); };
        footer.Controls.AddRange(new Control[] { resetAll, cancel, ok });
        void LayoutFooter()
        {
            int r = footer.ClientSize.Width - Z(12), y = (footer.Height - ok.Height) / 2;
            ok.Location = new Point(r - ok.Width, y);
            cancel.Location = new Point(ok.Left - cancel.Width - Z(8), y);
            resetAll.Location = new Point(Z(12), y);
        }
        footer.Resize += (_, _) => LayoutFooter();
        LayoutFooter();

        form.Controls.Add(list);
        form.Controls.Add(footer);
        form.AcceptButton = ok; form.CancelButton = cancel;
        try { form.ShowDialog((Form?)Form.ActiveForm ?? this); } finally { form.Dispose(); }
    }

    // Options → Caches: a full inventory of everything LiteBox writes under Core\litebox\, driven by the
    // DataMaintenance catalog. Cache dirs / logs / state clear immediately; a database is scheduled for the
    // next restart (it is open now); config + essential dirs are info-only.
    private Control BuildCachesSection()
    {
        float dpiS = LiteBoxTheme.DpiScale(this);
        int S(int px) => (int)Math.Round(px * dpiS);
        var mono = new Font("Consolas", 8f);
        var dim = Color.FromArgb(120, 120, 124);

        var p = new Panel { BackColor = Bg, AutoScroll = true };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg, Padding = new Padding(S(4), S(8), S(4), S(8)),
        };

        static string Human(long b) =>
            b <= 0 ? "0 B" :
            b < 1024 ? $"{b} B" :
            b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" :
            b < 1024L * 1024 * 1024 ? $"{b / (1024.0 * 1024):0.0} MB" :
            $"{b / (1024.0 * 1024 * 1024):0.00} GB";

        Label Header(string t) => new() { Text = t, AutoSize = true, ForeColor = Fg, BackColor = Bg,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, S(14), 0, S(4)) };
        Label Sub(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(S(660), 0),
            ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, S(6)) };
        Button MkBtn(string text) => new() { Text = text, AutoSize = false, Size = new Size(S(150), S(26)),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 },
            Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0) };

        flow.Controls.Add(new Label { Text = "Data & caches", AutoSize = true, ForeColor = Fg, BackColor = Bg,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(2)) });
        flow.Controls.Add(new Label { Text = LiteBoxPaths.Data, AutoSize = true, ForeColor = dim, BackColor = Bg,
            Font = mono, Margin = new Padding(0, 0, 0, S(2)) });

        // ── Automatic cache cleaning opt-outs (read by ThumbGc / ThumbCache at the NEXT launch) ──
        flow.Controls.Add(Header("Automatic cache cleaning"));
        flow.Controls.Add(Sub("Background cleaners that run once per launch, after the game cache is ready. "
            + "Unchecking disables that cleaner (takes effect at the next LiteBox start)."));
        void AddClean(string label, string key)
        {
            var cb = new CheckBox { Text = label, AutoSize = true, Checked = _cfg.GetBool(key, true),
                                    ForeColor = Fg, BackColor = Bg, Margin = new Padding(S(6), 0, 0, S(2)) };
            cb.CheckedChanged += (_, _) => { try { _cfg.SetBool(key, cb.Checked); _cfg.Save(); } catch { } };
            flow.Controls.Add(cb);
        }
        AddClean("Image thumbnails (unused / stale entries)", "CleanThumbsImages");
        AddClean("Video thumbnails", "CleanThumbsVideo");
        AddClean("Document thumbnails", "CleanThumbsDocs");
        AddClean("Web-image previews (unused for 30 days)", "CleanThumbsWebImg");
        AddClean("Related-games thumbnails (junk files)", "CleanThumbsRelated");
        AddClean("Size-budget sweep (500 MB cap on the thumbs tree)", "CleanThumbsBudget");
        AddClean("3D box models (stale bakes / removed games)", "CleanModel3d");
        AddClean("Options DB (rows of removed games / emulators / platforms)", "CleanOptionsDb");

        // ── Thumbnail format: global transparent container + per-regroupement policy ──
        flow.Controls.Add(Header("Thumbnail image format"));
        flow.Controls.Add(Sub("Transparent thumbnails are stored as PNG (decodes natively — fast to scroll) "
            + "or WebP (smaller on disk, but only decodes through Magick, which stutters on scroll). "
            + "Switching re-generates transparent thumbnails and the automatic cleaner removes the old ones."));
        var alphaRow = new Panel { Width = S(360), Height = S(26), BackColor = Bg, Margin = new Padding(S(6), 0, 0, S(4)) };
        alphaRow.Controls.Add(new Label { Text = "Transparent format", AutoSize = false, Size = new Size(S(180), S(22)),
            Location = new Point(0, S(3)), ForeColor = Fg, BackColor = Bg });
        var alphaCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(184), 0),
            Size = new Size(S(140), S(22)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        alphaCombo.Items.AddRange(new object[] { "PNG (fast)", "WebP (small)" });
        alphaCombo.SelectedIndex = ThumbCache.AlphaExt() == ".webp" ? 1 : 0;
        alphaCombo.SelectedIndexChanged += (_, _) =>
        {
            try { _cfg.Set("ThumbAlphaFormat", alphaCombo.SelectedIndex == 1 ? "webp" : "png"); _cfg.Save();
                  ThumbCache.InvalidateAlphaFormat(); } catch { }
        };
        alphaRow.Controls.Add(alphaCombo);
        flow.Controls.Add(alphaRow);

        flow.Controls.Add(Sub("Per image type: Auto (JPEG for photos, transparent format only when the image "
            + "really has transparency), JPEG (always, opaque), or Transparent (always keep alpha)."));
        var fmtCombos = new Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase);
        void SaveFormats()
        {
            var alpha = new List<string>(); var jpg = new List<string>();
            foreach (var (key, _) in CacheRegroupements)
                if (fmtCombos.TryGetValue(key, out var cb))
                    switch (cb.SelectedIndex) { case 1: jpg.Add(key); break; case 2: alpha.Add(key); break; }
            try { _cfg.Set("ThumbAlphaRegroupements", string.Join(",", alpha));
                  _cfg.Set("ThumbJpgRegroupements", string.Join(",", jpg)); _cfg.Save();
                  ThumbCache.InvalidateFormatCache(); } catch { }
        }
        foreach (var (key, title) in CacheRegroupements)
        {
            var row = new Panel { Width = S(360), Height = S(26), BackColor = Bg, Margin = new Padding(S(6), 0, 0, S(2)) };
            row.Controls.Add(new Label { Text = title, AutoSize = false, Size = new Size(S(180), S(22)),
                Location = new Point(0, S(3)), ForeColor = Fg, BackColor = Bg });
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(184), 0),
                Size = new Size(S(140), S(22)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
            combo.Items.AddRange(new object[] { "Auto", "JPEG", "Transparent" });
            combo.SelectedIndex = ThumbCache.FormatFor(key) switch
            { ThumbCache.ThumbFormat.Jpg => 1, ThumbCache.ThumbFormat.Png => 2, _ => 0 };
            combo.SelectedIndexChanged += (_, _) => SaveFormats();
            fmtCombos[key] = combo;
            row.Controls.Add(combo);
            flow.Controls.Add(row);
        }

        // ── Duplicate detection: re-evaluate every game's prevent-duplicates results ──
        flow.Controls.Add(Header("Duplicate detection"));
        flow.Controls.Add(Sub("Recomputes the \"prevent duplicate images\" results for EVERY game and rewrites the per-image "
            + "caches (:lb.dupcheck ADS). Run it after changing the engine/threshold, or to pre-compute everything so game "
            + "selection never pays the first-visit cost. Needs the option enabled (Display → Right panel)."));
        var dupRow = new Panel { Width = S(690), Height = S(30), BackColor = Bg, Margin = new Padding(S(6), 0, 0, S(4)) };
        var dupBtn = MkBtn("Update duplicates"); dupBtn.Location = new Point(0, 0);
        var dupClean = MkBtn("Clean dupcheck keys"); dupClean.Location = new Point(S(156), 0);
        var dupStop = MkBtn("Stop"); dupStop.Location = new Point(S(312), 0); dupStop.Enabled = false;
        var dupLbl = new Label { AutoSize = true, ForeColor = SubFg, BackColor = Bg, Location = new Point(S(474), S(5)) };
        dupRow.Controls.Add(dupBtn); dupRow.Controls.Add(dupClean); dupRow.Controls.Add(dupStop); dupRow.Controls.Add(dupLbl);
        flow.Controls.Add(dupRow);
        bool dupCancel = false;
        dupStop.Click += (_, _) => dupCancel = true;

        // Clean: walk every game's image files and DELETE their stored dup-check data (the :lb.dupcheck
        // stream and/or the .ads sidecar). Next selections recompute from scratch (if the option is on).
        dupClean.Click += (_, _) =>
        {
            IGame[] games;
            try { games = _dm.GetAllGames(); } catch { games = Array.Empty<IGame>(); }
            if (MessageBox.Show(p.FindForm(),
                    $"Remove the stored duplicate-check data (:lb.dupcheck) from the images of all {games.Length} games?\n\n"
                    + "The images themselves are untouched. Results are recomputed on the next visits when "
                    + "\"Prevent duplicate images\" is enabled.",
                    "Clean dupcheck keys", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            dupCancel = false; dupBtn.Enabled = false; dupClean.Enabled = false; dupStop.Enabled = true;
            dupLbl.Text = $"0/{games.Length} games";
            System.Threading.Tasks.Task.Run(() =>
            {
                int done = 0, removed = 0;
                foreach (var gg in games)
                {
                    if (dupCancel) break;
                    try
                    {
                        string plat2 = Safe(() => gg.Platform) ?? "";
                        string title2 = Safe(() => gg.Title) ?? "";
                        if (plat2.Length > 0 && Guid.TryParse(Safe(() => gg.Id) ?? "", out var id2))
                            foreach (var (path2, _, _) in MediaResolver.AllImageFiles(plat2, id2, title2))
                                if (Media.DupCheckAds.Delete(path2)) removed++;
                    }
                    catch { }
                    done++;
                    if (done % 10 == 0 || done == games.Length)
                    {
                        int d = done, k = removed;
                        try { BeginInvoke(new Action(() => { if (!dupLbl.IsDisposed) dupLbl.Text = $"{d}/{games.Length} games — {k} key(s) removed"; })); } catch { }
                    }
                }
                // BEFORE the UI update, deliberately: the label below lives in the Options window, so the
                // IsDisposed guard silently drops the result when that window was closed — which is exactly
                // when you most need to be told. Manual button only; nothing automatic runs this pass.
                LiteBox.Notifications.NotificationCenter.Info(dupCancel
                    ? $"Dup-check cleanup stopped at {done}/{games.Length} games — {removed} key(s) removed."
                    : $"Dup-check keys cleaned — {removed} key(s) removed across {games.Length} games.");
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (dupLbl.IsDisposed) return;
                        dupLbl.Text = (dupCancel ? $"stopped at {done}/{games.Length}" : $"done — {games.Length} games") + $" — {removed} key(s) removed";
                        dupBtn.Enabled = true; dupClean.Enabled = true; dupStop.Enabled = false;
                    }));
                }
                catch { }
            });
        };
        dupBtn.Click += (_, _) =>
        {
            if (!Media.MediaLayout.Current.PreventDuplicates)
            {
                MessageBox.Show(p.FindForm(), "Enable \"Prevent duplicate images\" first (Display → Right panel), then Apply — "
                    + "this pass rewrites the caches for the engine/threshold configured there.",
                    "Duplicate detection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            IGame[] games;
            try { games = _dm.GetAllGames(); } catch { games = Array.Empty<IGame>(); }
            dupCancel = false; dupBtn.Enabled = false; dupClean.Enabled = false; dupStop.Enabled = true;
            dupLbl.Text = $"0/{games.Length} games";
            System.Threading.Tasks.Task.Run(() =>
            {
                int done = 0;
                foreach (var gg in games)
                {
                    if (dupCancel) break;
                    // Both views, force = recompute + rewrite even valid cached results.
                    try { BuildMediaList(gg, poster: false, forceDup: true); BuildMediaList(gg, poster: true, forceDup: true); } catch { }
                    done++;
                    if (done % 5 == 0 || done == games.Length)
                    {
                        int d = done;
                        try { BeginInvoke(new Action(() => { if (!dupLbl.IsDisposed) dupLbl.Text = $"{d}/{games.Length} games"; })); } catch { }
                    }
                }
                // Same reasoning as the cleanup pass above: notify off the UI thread first, so closing the
                // Options window can't swallow the one signal that this long pass is over.
                LiteBox.Notifications.NotificationCenter.Info(dupCancel
                    ? $"Duplicate detection stopped at {done}/{games.Length} games."
                    : $"Duplicate detection finished — {games.Length} games.");
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (dupLbl.IsDisposed) return;
                        dupLbl.Text = dupCancel ? $"stopped at {done}/{games.Length}" : $"done — {games.Length} games";
                        dupBtn.Enabled = true; dupClean.Enabled = true; dupStop.Enabled = false;
                    }));
                }
                catch { }
            });
        };

        // ── 3D box models: the GLB cache (Core\litebox\cache\3d) ──
        flow.Controls.Add(Header("3D box models"));
        flow.Controls.Add(Sub("Baked 3D case models (one GLB per game, thumb-first). Models are (re)baked on selection or in "
            + "bulk via Tools → Generate Media Cache. \"Clean stale\" removes bakes whose game/art/settings changed; "
            + "the automatic cleaner (above) does the same once per launch."));
        var m3dRow = new Panel { Width = S(690), Height = S(30), BackColor = Bg, Margin = new Padding(S(6), 0, 0, S(4)) };
        var m3dClean = MkBtn("Clean stale models"); m3dClean.Location = new Point(0, 0);
        var m3dWipe = MkBtn("Delete all"); m3dWipe.Location = new Point(S(156), 0);
        var m3dLbl = new Label { AutoSize = true, ForeColor = SubFg, BackColor = Bg, Location = new Point(S(318), S(5)) };
        m3dRow.Controls.Add(m3dClean); m3dRow.Controls.Add(m3dWipe); m3dRow.Controls.Add(m3dLbl);
        flow.Controls.Add(m3dRow);
        void M3dStats(string? suffix = null)
        {
            var (files, bytes) = Model3d.Model3dCache.Stats();
            m3dLbl.Text = $"{files} model(s), {bytes / (1024.0 * 1024.0):0.#} MB" + (suffix != null ? " — " + suffix : "");
        }
        M3dStats();
        m3dClean.Click += (_, _) =>
        {
            m3dClean.Enabled = false; m3dWipe.Enabled = false;
            System.Threading.Tasks.Task.Run(() =>
            {
                IGame[] games;
                try { games = _dm.GetAllGames(); } catch { games = Array.Empty<IGame>(); }
                var (kept, deletedN) = Model3d.Model3dCache.SweepStale(games);
                Model3d.Model3dKeyIndex.Refresh();
                try { BeginInvoke(new Action(() => { if (m3dLbl.IsDisposed) return; M3dStats($"{deletedN} stale deleted, {kept} kept"); m3dClean.Enabled = true; m3dWipe.Enabled = true; })); } catch { }
            });
        };
        m3dWipe.Click += (_, _) =>
        {
            if (MessageBox.Show(p.FindForm(), "Delete ALL baked 3D models? They are re-baked on the next visits.",
                    "3D box models", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int n = Model3d.Model3dCache.CleanAll();
            M3dStats($"{n} deleted");
        };

        var cacheRefreshers = new List<Action>();

        void AddRow(DataMaintenance.Item it)
        {
            var row = new Panel { Width = S(690), Height = S(60), BackColor = Panel, Margin = new Padding(0, 0, 0, S(4)) };
            var name = new Label { AutoSize = true, ForeColor = Fg, BackColor = Panel,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold), Location = new Point(S(8), S(6)) };
            var role = new Label { Text = it.Role, AutoSize = true, MaximumSize = new Size(S(520), 0),
                ForeColor = SubFg, BackColor = Panel, Font = new Font("Segoe UI", 8.25f), Location = new Point(S(8), S(26)) };
            var path = new Label { Text = it.FullPath, AutoSize = false, Size = new Size(S(672), S(15)), AutoEllipsis = true,
                ForeColor = dim, BackColor = Panel, Font = mono, Location = new Point(S(8), S(43)) };
            row.Controls.Add(name); row.Controls.Add(role); row.Controls.Add(path);

            void Refresh()
            {
                var (f, b) = DataMaintenance.SizeOf(it);
                string sz = it.IsDir ? $"{f} file(s), {Human(b)}" : (f == 0 ? "absent" : Human(b));
                name.Text = $"{it.Name}      —      {sz}";
            }
            Refresh();

            switch (it.Action)
            {
                case DataMaintenance.ActionType.None:
                    row.Controls.Add(new Label {
                        Text = it.Kind == DataMaintenance.Kind.ConfigFile ? "settings — kept" : "essential — kept",
                        AutoSize = true, ForeColor = dim, BackColor = Panel,
                        Font = new Font("Segoe UI", 8f, FontStyle.Italic), Location = new Point(S(566), S(9)) });
                    break;

                case DataMaintenance.ActionType.ClearDirNow:
                {
                    var b = MkBtn("Clear"); b.Location = new Point(S(532), S(6));
                    b.Click += (_, _) =>
                    {
                        var (f, by) = DataMaintenance.SizeOf(it);
                        if (f == 0) return;
                        if (MessageBox.Show(p.FindForm(), $"Clear {it.Name}  ({f} file(s), {Human(by)})?\n\n{it.Role}",
                            "Clear cache", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        DataMaintenance.ClearDir(it); Refresh();
                        try { var g = _games?.SelectedGame; if (g != null) ScheduleMedia(g); } catch { }
                    };
                    row.Controls.Add(b);
                    cacheRefreshers.Add(Refresh);
                    break;
                }

                case DataMaintenance.ActionType.DeleteFileNow:
                {
                    bool isLog = it.Kind == DataMaintenance.Kind.Log;
                    var b = MkBtn(isLog ? "Delete" : "Reset"); b.Location = new Point(S(532), S(6));
                    b.Click += (_, _) =>
                    {
                        var (f, by) = DataMaintenance.SizeOf(it);
                        if (f == 0) return;
                        string q = isLog ? $"Delete {it.Name}  ({Human(by)})?" : $"Reset {it.Name}?\n\n{it.Role}";
                        if (MessageBox.Show(p.FindForm(), q, "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        DataMaintenance.DeleteFile(it); Refresh();
                    };
                    row.Controls.Add(b);
                    break;
                }

                case DataMaintenance.ActionType.ResetDbOnRestart:
                {
                    var b = MkBtn(""); b.Location = new Point(S(532), S(6));
                    void Style()
                    {
                        bool sched = DataMaintenance.IsScheduled(it);
                        b.Text = sched ? "Scheduled ✓ (undo)" : "Delete on restart";
                        b.BackColor = sched ? LiteBoxTheme.Accent : Panel2;
                        b.ForeColor = sched ? Color.White : Fg;
                    }
                    Style();
                    b.Click += (_, _) =>
                    {
                        if (!DataMaintenance.IsScheduled(it))
                        {
                            string warn = it.Warning != null ? "\n\n⚠  " + it.Warning : "";
                            if (MessageBox.Show(p.FindForm(),
                                $"Delete {it.Name} on the next restart?\n\n{it.Role}{warn}\n\nIt is recreated empty at the next boot.",
                                "Schedule database reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        }
                        DataMaintenance.ToggleScheduled(it); Style();
                    };
                    row.Controls.Add(b);
                    break;
                }
            }
            flow.Controls.Add(row);
        }

        // Cache directories + global clear-all.
        flow.Controls.Add(Header("Cache directories"));
        flow.Controls.Add(Sub("Rebuildable caches — safe to clear anytime; they refill on demand."));
        var clearAll = MkBtn("Clear ALL caches"); clearAll.Width = S(180); clearAll.Margin = new Padding(0, 0, 0, S(6));
        clearAll.Click += (_, _) =>
        {
            long tot = DataMaintenance.Of(DataMaintenance.Kind.CacheDir).Sum(i => DataMaintenance.SizeOf(i).bytes);
            if (MessageBox.Show(p.FindForm(),
                $"Clear ALL cache directories  ({Human(tot)})?\n\nThumbnails, downloads, badges, browser cache… all refill on demand.",
                "Clear all caches", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var i in DataMaintenance.Of(DataMaintenance.Kind.CacheDir)) DataMaintenance.ClearDir(i);
            foreach (var r in cacheRefreshers) r();
            try { var g = _games?.SelectedGame; if (g != null) ScheduleMedia(g); } catch { }
        };
        flow.Controls.Add(clearAll);
        foreach (var it in DataMaintenance.Of(DataMaintenance.Kind.CacheDir)) AddRow(it);

        flow.Controls.Add(Header("Databases"));
        flow.Controls.Add(Sub("Deleting a database is scheduled for the next restart (it is open now) and recreated empty at boot."));
        foreach (var it in DataMaintenance.Of(DataMaintenance.Kind.Database)) AddRow(it);

        flow.Controls.Add(Header("Logs"));
        foreach (var it in DataMaintenance.Of(DataMaintenance.Kind.Log)) AddRow(it);

        flow.Controls.Add(Header("State"));
        foreach (var it in DataMaintenance.Of(DataMaintenance.Kind.StateFile)) AddRow(it);

        flow.Controls.Add(Header("Configuration  (kept — managed in their own options pages)"));
        foreach (var it in DataMaintenance.Of(DataMaintenance.Kind.ConfigFile)) AddRow(it);

        flow.Controls.Add(Header("Essential  (kept — not caches)"));
        foreach (var it in DataMaintenance.Of(DataMaintenance.Kind.EssentialDir)) AddRow(it);

        p.Controls.Add(flow);
        return p;
    }

    // Full self-uninstall (Options → Uninstall LiteBox). Red button + confirmation → detached .bat.
    private Control BuildUninstallSection()
    {
        // FlowLayoutPanel (TopDown), not fixed Locations: `desc` wraps to however many lines its actual text
        // needs (MaximumSize.Width forces the wrap, AutoSize grows the height to fit) — a fixed Y for cbThumbs
        // below it baked in an assumed height that the real wrapped text (3 lines at normal DPI, more at higher
        // DPI / larger fonts) exceeds, so it silently overlapped the description. Same bug class as the original
        // OptionsWindow overlap; same fix (derive layout from live PreferredSize) + DPI-scale the pixel sizes.
        float dpiS = LiteBoxTheme.DpiScale(this);
        int S(int px) => (int)Math.Round(px * dpiS);

        var p = new Panel { BackColor = Bg, AutoScroll = true };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg, Padding = new Padding(S(4), S(8), S(4), 0),
        };
        var title = new Label { Text = "Uninstall LiteBox", AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(8)) };
        var desc = new Label
        {
            Text = "Removes LiteBox completely: LiteBox.exe (Core + root re-launcher), the whole Core\\litebox\\ data "
                 + "folder (databases, caches, config, logs), and the LiteBox-only ThirdParty natives "
                 + "(Steam, Pdfium, RomExtractor). The ExtendDB plugin is never touched; the ThirdParty tools "
                 + "shared with it are left in place unless you tick the box below.",
            AutoSize = true, MaximumSize = new Size(S(560), 0), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, S(16)),
        };
        var cbTp = new CheckBox { Text = "Also remove the shared ThirdParty tools (Everything, ImageMagick, RAHasher)", AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, S(4)) };
        var shareNote = new Label { Text = "Shared with ExtendDB, which re-creates them on its next run; their folders are only removed if left empty.", AutoSize = true, MaximumSize = new Size(S(540), 0), ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f), Margin = new Padding(S(18), 0, 0, S(32)) };
        var btn = new Button
        {
            Text = "Uninstall LiteBox", AutoSize = false, Size = new Size(S(210), S(32)), Margin = new Padding(0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(150, 40, 40), ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        };
        btn.Click += (_, _) =>
        {
            string extra = cbTp.Checked ? "\n  • the shared ThirdParty tools (Everything/ImageMagick/RAHasher)" : "";
            if (MessageBox.Show(p.FindForm(),
                    "Uninstall LiteBox now?\n\nLiteBox will close and delete itself. This cannot be undone." + extra,
                    "Uninstall LiteBox", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try { Install.Uninstaller.RunSelfUninstall(cbTp.Checked); }   // launches the bat + exits
            catch (Exception ex) { MessageBox.Show(p.FindForm(), "Uninstall failed to start: " + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        flow.Controls.Add(title); flow.Controls.Add(desc); flow.Controls.Add(cbTp); flow.Controls.Add(shareNote); flow.Controls.Add(btn);
        p.Controls.Add(flow);
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

    // Menu-bar padlock for the parental indicator. closed = locked (amber, shackle down on both
    // legs); open = unlocked (grey, one leg lifted). 16×16, the bar's icon size.
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

        ClearTransientTypedFilter();   // la frappe dans la liste ne survit pas au changement de noeud
        ActivateNodeSort(node);
        ApplySort();              // fills the centre list (no game auto-selected)
        ShowNodeDetails(node);    // node info on the right
    }

    // ── Sort + filter ────────────────────────────────────────────────────────
    /// <summary>Distinct key for a multi-selection, so switching between two different unions
    /// counts as navigating rather than as a refresh of the same node.</summary>
    private static string NodeKeyForUnion(IEnumerable<object> tags)
        => "U:" + string.Join("|", tags.Select(t => NodeKey(t) ?? "?").OrderBy(x => x, StringComparer.Ordinal));

    private void ActivateNodeSort(object node, string nodeKeyOverride = null)
    {
        _nodeForcesSort = false;
        _manualOrder = null;
        _curSortKey = _sessionSortKey;
        _ascending = _sessionAscending;
        // A staged kiosk order is consumed when the user NAVIGATES, not when the current node is
        // merely rebuilt (a refresh after an edit, a return from a game, a parental reload). Those
        // rebuilds can happen while the kiosk is still on screen, and re-sorting the list behind it
        // is exactly the wasted work the deferral exists to avoid.
        string nodeKey = nodeKeyOverride ?? NodeKey(node) ?? "*";
        if (!string.Equals(nodeKey, _sortedNodeKey, StringComparison.Ordinal))
        {
            _sortedNodeKey = nodeKey;
            _deferredKioskSort.AppliedOnNodeLoad();
        }

        if (node is not IPlaylist playlist) return;
        bool auto = Safe(() => playlist.AutoPopulate);
        if (!auto)
        {
            // Manual can be selected contextually even when SortBy is Default. Build its order
            // for every non-auto playlist. Equal ManualOrder values retain PlaylistGame/XML order.
            _manualOrder = GameSortCatalog.ManualRanks(
                Safe(() => playlist.GetAllPlaylistGames()) ?? Array.Empty<IPlaylistGame>());
        }
        var configured = GameSortCatalog.Parse(
            Safe(() => playlist.SortBy),
            GameSortCatalog.CustomFieldNames(Safe(() => _dm.GetAllGames()) ?? Array.Empty<IGame>()));

        // Manual has no stable meaning for an auto-populated playlist: LaunchBox falls back to Default.
        if (configured == GameSortCatalog.Manual && auto) return;
        if (configured == GameSortCatalog.Default) return;

        _nodeForcesSort = true;
        _curSortKey = configured;
        _ascending = true;
    }

    /// <param name="localOnly">The order was imposed by an action that is NOT a sort choice — a
    /// letter jump needing alphabetical order. It applies here and now, but must not become the
    /// session order: leaving the node restores whatever the user had actually picked.</param>
    private void SelectSort(string key, bool? ascending = null, bool localOnly = false)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "title";
        bool same = string.Equals(_curSortKey, key, StringComparison.OrdinalIgnoreCase);
        _ascending = ascending ?? (same ? !_ascending : true);
        _curSortKey = key;
        // Manual only has meaning inside the current non-auto playlist. Selecting it from a
        // Default playlist must not replace the session sort restored on the next platform.
        bool updatesSession = !localOnly && GameSortCatalog.UpdatesSession(_nodeForcesSort, key);
        _deferredKioskSort.DesktopSelection(
            ref _sessionSortKey,
            ref _sessionAscending,
            updatesSession,
            key,
            _ascending);
        DoSort(key, _ascending);
    }

    /// <summary>The process-wide order exposed to the embedded LB/BB kiosk.
    /// Playlist overrides and contextual Manual order are deliberately excluded.</summary>
    internal (string Key, string Dir) KioskSessionSort()
        => (_sessionSortKey, _sessionAscending ? "asc" : "desc");

    /// <summary>Apply a global order chosen in an embedded web kiosk. The desktop's
    /// current configured playlist keeps its local override, but the new session
    /// order is ready when returning to a platform or Default playlist.</summary>
    internal void ApplyKioskSessionSort(string key, string dir)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)(() => ApplyKioskSessionSort(key, dir))); } catch { }
            return;
        }

        key = (key ?? "").Trim();
        var customNames = GameSortCatalog.CustomFieldNames(
            Safe(() => _dm.GetAllGames()) ?? Array.Empty<IGame>());
        if (key.StartsWith(GameSortCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = key.Substring(GameSortCatalog.CustomPrefix.Length);
            var canonical = customNames.FirstOrDefault(x =>
                string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            key = canonical == null ? "title" : GameSortCatalog.CustomPrefix + canonical;
        }
        else if (!GameSortCatalog.IsStandard(key)
                 && key.ToLowerInvariant() is not ("community" or "votes" or "completed" or "broken" or "apppath" or "rahash"))
        {
            key = GameSortCatalog.Parse(key, customNames);
            if (key is GameSortCatalog.Default or GameSortCatalog.Manual) key = "title";
        }

        key = key.StartsWith(GameSortCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? key
            : key.ToLowerInvariant();
        _deferredKioskSort.Stage(
            ref _sessionSortKey,
            ref _sessionAscending,
            key,
            !string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase));
        // Do not touch _curSortKey, the ListView, filters, or poster grid here.
        // ActivateNodeSort consumes this staged session order on the next real node load.
    }

    /// <summary>Fills a drop-down with the sort catalog — shared by the menu bar's two Arrange By
    /// entries. Always rebuilt rather than cached: the entries
    /// depend on the current node (a manual playlist adds "Manual"), on the active extra column, and
    /// on the custom fields the library actually uses.</summary>
    private void PopulateArrangeItems(ToolStripItemCollection into)
    {
        void Add(string key, string label)
        {
            bool active = string.Equals(_curSortKey, key, StringComparison.OrdinalIgnoreCase);
            // LaunchBox ticks the active field; we put the DIRECTION arrow in that same icon margin
            // instead (re-picking the active field reverses it) — it says strictly more than a tick.
            var item = new ToolStripMenuItem(label)
            {
                Tag = key,
                ForeColor = Fg,
                Image = active ? SortArrowImage(_ascending) : null,
            };
            item.Click += (_, _) => SelectSort(key);
            into.Add(item);
        }

        if (_currentNode is IPlaylist pl && !Safe(() => pl.AutoPopulate))
        {
            Add(GameSortCatalog.Manual, "Manual");
            into.Add(new ToolStripSeparator());
        }

        bool activeIsExtraColumn = !GameSortCatalog.IsStandard(_curSortKey)
            && !string.Equals(_curSortKey, GameSortCatalog.Manual, StringComparison.OrdinalIgnoreCase)
            && !_curSortKey.StartsWith(GameSortCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase);
        if (activeIsExtraColumn)
        {
            var activeColumn = _games.AllColumns.FirstOrDefault(c =>
                string.Equals(c.Key, _curSortKey, StringComparison.OrdinalIgnoreCase));
            if (activeColumn != null)
            {
                Add(_curSortKey, activeColumn.Title);
                into.Add(new ToolStripSeparator());
            }
        }
        foreach (var d in GameSortCatalog.Standard) Add(d.Key, d.Label);

        // LaunchBox exposes custom-field sorts globally, even when the current node
        // happens not to contain a game using that field.
        var custom = GameSortCatalog.CustomFieldNames(Safe(() => _dm.GetAllGames()) ?? Array.Empty<IGame>());
        if (custom.Length > 0)
        {
            into.Add(new ToolStripSeparator());
            foreach (var name in custom) Add(GameSortCatalog.CustomPrefix + name, name);
        }
    }

    // The active sort's direction, drawn in the item's ICON margin (where a tick would sit). Two
    // cached 16px bitmaps — a filled triangle reads cleaner at that size than the ▲/▼ text glyphs
    // a label would spell it with.
    private static readonly Dictionary<bool, Image> _sortArrowCache = new();

    private static Image SortArrowImage(bool ascending)
    {
        lock (_sortArrowCache)
        {
            if (_sortArrowCache.TryGetValue(ascending, out var cached)) return cached;
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var pts = ascending
                    ? new[] { new Point(8, 4), new Point(13, 11), new Point(3, 11) }
                    : new[] { new Point(3, 5), new Point(13, 5), new Point(8, 12) };
                using var brush = new SolidBrush(Accent);
                g.FillPolygon(brush, pts);
            }
            _sortArrowCache[ascending] = bmp;
            return bmp;
        }
    }

    private void ApplySort()
    {
        if (_games == null) return;
        DoSort(_curSortKey, _ascending);
    }

    private void DoSort(string key, bool asc)
    {
        if (_games == null) return;
        _curSortKey = key;
        _games.SortGetter = SortGetterFor(key);
        // Ties are broken by the title key, ascending, exactly like the web clients — otherwise a
        // low-cardinality sort (Genre, Favorite, Region…) leaves each block in raw XML order here
        // and in alphabetical order there. Skipped for Title (same key) and for Manual, whose ranks
        // are already unique and whose ties must stay in playlist order.
        bool titleTie = !string.Equals(key, "title", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(key, GameSortCatalog.Manual, StringComparison.OrdinalIgnoreCase);
        _games.SortTieGetter = titleTie ? g => CompareName(g) : null;
        _games.SortAscending = asc;
        var columnKey = ColumnKeyForSort(key);
        _games.SortGlyphColumn = _games.AllColumns.FirstOrDefault(c =>
            c.Visible && string.Equals(c.Key, columnKey, StringComparison.OrdinalIgnoreCase));
        ApplyFilter();   // sets the filter predicate + rebuilds the view (single pass)
    }

    // ── Game List Index (LB 14) ───────────────────────────────────────────────
    /// <summary>One marker per group of the CURRENT sort, over the displayed view. Sorted by Title
    /// the groups are letters; any other sort groups by its displayed value. Only the FIRST group
    /// of each first-segment family is eligible to spell its text ("Action; Adventure" spells
    /// "Action"; the following "Action; Casual" keeps a dot) — LaunchBox's rule; the tooltip still
    /// names every group in full.</summary>
    private static readonly char[] FamilyCuts = { ';', '/' };

    private IReadOnlyList<(string Label, string Tip, int Index, bool Spell)> ComputeIndexGroups()
    {
        var view = _games?.VisibleGames;
        if (view == null || view.Count == 0) return Array.Empty<(string, string, int, bool)>();
        var label = IndexLabelFor(_curSortKey);
        var groups = new List<(string, string, int, bool)>();
        string last = null, lastFamily = null;
        for (int i = 0; i < view.Count; i++)
        {
            string l = label(view[i]) ?? "";
            if (string.Equals(l, last, StringComparison.Ordinal)) continue;
            // Family = the first value, and only its LEFT half when it is itself compound: the
            // arcade genres "Fighter / Versus", "Fighter / 2D", … all spell a single "Fighter".
            int cut = l.IndexOfAny(FamilyCuts);
            string family = (cut < 0 ? l : l[..cut]).Trim();
            bool spell = !string.Equals(family, lastFamily, StringComparison.OrdinalIgnoreCase);
            groups.Add((family, l, i, spell));
            last = l; lastFamily = family;
        }
        // A near-unique sort (full dates, DB ids) makes one group per row; thin what the bar has to
        // hold so hover search and paint stay O(small). Every kept marker still jumps exactly.
        if (groups.Count > 300)
        {
            var thin = new List<(string, string, int, bool)>(300);
            for (int i = 0; i < 300; i++) thin.Add(groups[(int)((long)i * groups.Count / 300)]);
            groups = thin;
        }
        return groups;
    }

    private Func<IGame, string> IndexLabelFor(string key)
    {
        bool alpha = string.IsNullOrEmpty(key)
            || string.Equals(key, "title", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, GameSortCatalog.Default, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, GameSortCatalog.Manual, StringComparison.OrdinalIgnoreCase);
        if (!alpha)
        {
            // The group label is what the sort column DISPLAYS (so Genre groups read "Fighter / 2D",
            // not a raw value), falling back to the sort getter for keys without a column.
            // VNDB tags ride inside GenresString as "vndb-…" segments (see ParseGenres); the index
            // groups on the REAL genres only, so a game's tag soup never becomes a family.
            static string Clean(string t)
            {
                if (string.IsNullOrEmpty(t)) return "(None)";
                if (t.IndexOf("vndb-", StringComparison.OrdinalIgnoreCase) >= 0)
                    t = ParseGenres(t).genres;
                return string.IsNullOrEmpty(t) ? "(None)" : t;
            }
            var colKey = ColumnKeyForSort(key);
            var col = _games.AllColumns.FirstOrDefault(c => string.Equals(c.Key, colKey, StringComparison.OrdinalIgnoreCase));
            if (col?.Text != null)
                return g => Clean(Safe(() => col.Text(g)));
            var getter = SortGetterFor(key);
            if (getter != null)
                return g => Clean(Safe(() => getter(g))?.ToString());
        }
        return g =>
        {
            string t = CompareName(g);
            char c = t?.Length > 0 ? char.ToUpperInvariant(t[0]) : '#';
            return char.IsLetter(c) ? c.ToString() : "#";   // digits and symbols share LB's # bucket
        };
    }

    private void ApplyGameListIndexOptions()
    {
        if (_gameIndex == null) return;
        // LB 14's own keys (shared Settings.xml on 14+, ProblemKeys-routed to our DB before that).
        bool on = LbSettings?.GetBool("UseArrangeScrollBar", true) ?? true;
        _gameIndexOn = on;
        _games.HideVScroll = on;
        if (_poster != null) _poster.HideVScroll = on;   // the poster grid loses its scrollbar too
        _gameIndex.AlwaysShow = LbSettings?.GetBool("AlwaysShowArrangeScrollBar", true) ?? true;
        _gameIndex.MiniWhenCollapsed = _cfg.GetBool("GameListIndexMini", true);
        _gameIndex.Visible = on;
        if (on) _gameIndex.RefreshGroups();
        ApplyGameListIndexRoom();
    }

    /// <summary>Give the strip its RESERVED width (constant per mode): the list frees it through
    /// the panel's right padding, the poster through LayoutPoster. Hover expansion overlays and
    /// never comes through here.</summary>
    private void ApplyGameListIndexRoom()
    {
        if (_gameIndex == null) return;
        int room = _gameIndexOn ? _gameIndex.ReservedWidth : 0;
        var parent = _gameIndex.Parent;
        if (parent != null && parent.Padding.Right != room)
            parent.Padding = new Padding(0, 0, room, 0);
        LayoutPoster();
    }

    private void ApplyFilter()
    {
        if (_games == null) return;
        _games.Games = _current;
        string txt = _search?.Text;
        bool hasTxt = !string.IsNullOrWhiteSpace(txt);
        bool parental = ParentalBridge.Active;   // hide restricted games (kept in memory, just not shown)
        var filt = _filter;                       // advanced dialog criteria (null = none)
        // A TRANSIENT filter (typing in the list, or a letter picked from a rail) narrows to titles
        // BEGINNING with what was typed; a deliberate search in the box finds it anywhere. Same
        // rule and same two title forms as both web clients — see GameTextFilter.
        bool prefix = _typedFilterIsTransient;
        var hide = HideGamesFilterOrNull();       // View ▸ Hide Games — LB's Settings.xml rules
        _games.FilterPredicate = (!hasTxt && !parental && filt == null && hide == null)
            ? (Func<IGame, bool>)null
            : g =>
            {
                if (hide != null && !hide(g)) return false;
                if (parental && ParentalHidesGame(g)) return false;
                if (hasTxt && !GameTextFilter.Matches(g, txt, prefix)) return false;
                if (filt != null && !filt.Matches(g)) return false;   // AND the advanced criteria
                return true;
            };
        _games.RebuildView();   // count + poster updated via ViewChanged
    }

    // ── Advanced search filter (dialog + indicator) ───────────────────────────
    private void OpenFilterDialog()
    {
        Search.FilterCriteria.ResetCaches();   // le catalogue de manettes a pu changer depuis la dernière ouverture
        using var dlg = new Search.FilterDialog(_filter ?? new Search.FilterCriteria(), ComputeFacets(), Search.SearchHistory.Load());
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
        var c = dlg.Result;
        _filter = (c.IsActive || c.SortBy != "alpha") ? c : null;   // keep only if it does something
        if (c.IsActive) Search.SearchHistory.Add(c);

        // The filter's "Order by" is a one-shot: drive the list's own sort so the user can still re-sort after.
        string sortKey = c.SortBy switch { "year" => "releaseyear", "rating" => "starrating", "lastplayed" => "lastplayed", _ => null };
        if (sortKey != null)
        {
            SelectSort(sortKey, false);   // sets SortGetter + calls ApplyFilter (predicate applied too)
        }
        else ApplyFilter();

        UpdateFilterIndicator();
    }

    private void ClearAdvancedFilter()
    {
        if (_filter == null) return;
        _filter = null;
        ApplyFilter();
        UpdateFilterIndicator();
    }

    private void UpdateFilterIndicator()
    {
        if (_filterBtn == null) return;
        bool active = _filter != null && _filter.IsActive;
        // Lit + inset, in the same blue family as a deliberate quick filter (see ReflectQuickFilter) but
        // stronger — the button is small, so it needs the extra contrast to carry the same message.
        _filterBtn.Active = active;
        _tips.SetToolTip(_filterBtn, active ? "Filter active — click to edit, right-click to clear" : "Advanced search filter");
    }

    // Les valeurs proposées par le dialogue de filtre.
    //
    // Elles viennent de TOUTE la bibliothèque, pas du nœud affiché. LaunchBox, lui, ne propose que les
    // valeurs du nœud courant — il peut se le permettre, son filtre meurt quand on change de nœud. Le
    // nôtre survit, c'est même sa raison d'être : borner les choix au nœud courant rendrait « tous mes
    // jeux japonais, toutes plateformes confondues » impossible à exprimer.
    private Search.FilterFacets ComputeFacets()
    {
        var f = new Search.FilterFacets();
        var games = Safe(() => _dm?.GetAllGames()) ?? _current ?? Array.Empty<IGame>();
        // Manettes : seules celles qu'au moins UN jeu référence avec un vrai support (niveau ≠ 0 =
        // « Not Supported »). Le catalogue entier proposerait des options qui garantissent zéro
        // résultat — exactement ce que la règle « valeurs présentes dans la bibliothèque » interdit.
        var usedControllerIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
        {
            foreach (var x in S(Safe(() => g.GenresString)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) f.Genres.Add(x);
            f.Add(f.Publishers, Safe(() => g.Publisher));
            f.Add(f.Developers, Safe(() => g.Developer));
            f.Add(f.ReleaseTypes, Safe(() => g.ReleaseType));
            f.Add(f.Platforms, Safe(() => g.Platform));
            f.Add(f.Statuses, Safe(() => g.Status));
            f.Add(f.Progresses, Safe(() => g.Progress));
            f.Add(f.Esrb, Safe(() => g.Rating));
            f.AddTokens(f.Regions, Safe(() => g.Region));
            f.AddTokens(f.PlayModes, Safe(() => g.PlayMode));
            int? mp = Safe(() => g.MaxPlayers); if (mp is > 0 and <= 32) f.MaxPlayers.Add(mp.Value);
            if (g is LbApiHost.ILiteBoxGame lb)
                try
                {
                    foreach (var row in lb.GetSubEntities("GameControllerSupport"))
                        if (Search.FilterCriteria.RowSupportsController(row)
                            && row.TryGetValue("ControllerId", out var cid) && !string.IsNullOrEmpty(cid))
                            usedControllerIds.Add(cid);
                }
                catch { }
        }
        try
        {
            foreach (var r in ControllerCatalogStore.All())
                if (usedControllerIds.Contains(r.Id ?? "")) f.Add(f.Controllers, r.Name);
        }
        catch { }
        return f;
    }

    // ── Poster (grid) view ────────────────────────────────────────────────────
    // A native virtual ListView in LargeIcon view. Each game's tile (box-art "contain" + title +
    // developer, or a grey phantom for missing art) is composited ONCE into a Win32 image list and the
    // NATIVE control renders + scrolls it — no managed per-tile paint, so a held scroll stays smooth.
    // Tiles are built lazily on item retrieval / thumb load; image-list slots recycle LRU.
    // The poster is a VIRTUAL ListView, where comctl32's Shift range-selection anchor is unreliable
    // (LVM_SETSELECTIONMARK is ignored) — Shift+click / Shift+arrow ranged from index 0 and over-selected.
    // So we own the Shift range: track our anchor, and on a Shift interaction re-select exactly [anchor..target].
    // Plain / Ctrl clicks and plain navigation stay native (they were fine); we only note the new anchor after.
    internal sealed class PosterListView : ListView   // internal: --selftest-selection drives it headless
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lvi);

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask; public int iItem; public int iSubItem; public uint state; public uint stateMask;
            public IntPtr pszText; public int cchTextMax; public int iImage; public IntPtr lParam;
            public int iIndent; public int iGroupId; public uint cColumns; public IntPtr puColumns;
            public IntPtr piColFmt; public int iGroup;
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int LVM_FIRST = 0x1000;
        private const int LVM_SETITEMSTATE = LVM_FIRST + 43;
        private const int LVM_GETNEXTITEM = LVM_FIRST + 12;
        private const int LVNI_FOCUSED = 0x0001;
        private const int LVNI_SELECTED = 0x0002;
        private const uint LVIS_FOCUSED = 1, LVIS_SELECTED = 2;
        private const int WM_LBUTTONDOWN = 0x0201, WM_KEYDOWN = 0x0100;

        private int _anchor = -1;

        // ── Game List Index support (grid flavour of GameListView's helpers) ──────────
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref PT pt);
        private const int LVM_SCROLL_P = LVM_FIRST + 20, LVM_GETORIGIN_P = LVM_FIRST + 41;
        private const int WM_NCCALCSIZE_P = 0x83, WM_MOUSEWHEEL_P = 0x020A, WM_VSCROLL_P = 0x0115;
        private const int GWL_STYLE_P = -16, WS_VSCROLL_P = 0x00200000, SB_VERT_P = 1;

        /// <summary>Current vertical scroll offset (LVM_GETORIGIN — valid in icon views).</summary>
        private int ScrollTop()
        {
            var pt = new PT();
            SendMessage(Handle, LVM_GETORIGIN_P, IntPtr.Zero, ref pt);
            return pt.Y;
        }

        private bool _hideVScroll;
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool HideVScroll
        {
            get => _hideVScroll;
            set
            {
                if (_hideVScroll == value) return;
                _hideVScroll = value;
                ReassertScrollHiding();
            }
        }

        /// <summary>Re-hide the scrollbar after anything that redraws the frame (SetWindowTheme,
        /// handle recreation): ShowScrollBar + a frame-changed pass so WM_NCCALCSIZE strips the
        /// style again.</summary>
        public void ReassertScrollHiding()
        {
            if (!_hideVScroll || !IsHandleCreated) return;
            try
            {
                ShowScrollBar(Handle, SB_VERT_P, false);
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }
        }

        /// <summary>The viewport moved — lets the index bar keep its thumb honest.</summary>
        public event Action Scrolled;

        /// <summary>How many columns comctl ACTUALLY wrapped — its items-per-row test has an
        /// internal margin no width formula predicts, so the layout measures instead of guessing.</summary>
        public int NativeColumns => Grid().cols;

        /// <summary>Columns per row and the row stride in px, measured off the native geometry
        /// (item 0 vs the first item that wraps). (1, big) for a single-row grid. CACHED per
        /// (count, client width): the index bar reads this on every thumb repaint while
        /// scrolling, and the raw scan is up to 256 GetItemRect messages.</summary>
        private (int cols, int rowH) _gridCache = (0, 0);
        private (int n, int w, int itemH) _gridCacheKey = (-1, -1, -1);

        /// <summary>Drop the measured-grid cache — call after anything that re-tiles without
        /// changing count or width (icon spacing pushes).</summary>
        public void InvalidateGridCache() => _gridCacheKey = (-1, -1, -1);

        private (int cols, int rowH) Grid()
        {
            int n = VirtualListSize;
            if (n <= 0) return (1, 1);
            // The item's own height joins the key: a spacing push that forgets to invalidate would
            // otherwise leave a stale stride in place, and NOTHING downstream can tell — every
            // position simply reads a few percent off (a 226px stride against a real 240px one put
            // the index thumb a hundred games past where the eye was, on a 3000-game library).
            var r0 = GetItemRect(0);
            var key = (n, ClientSize.Width, r0.Height);
            if (key == _gridCacheKey) return _gridCache;
            int cols = Math.Max(1, n);
            for (int i = 1; i < Math.Min(n, 256); i++)
                if (GetItemRect(i).Y > r0.Y) { cols = i; break; }
            int rowH = int.MaxValue;
            if (cols < n)
            {
                // Stride over the LONGEST baseline available rather than the next row alone: one
                // adjacent reading taken mid-relayout would skew every row index that follows,
                // while a first-to-last measurement averages the grid as it actually stands.
                int lastRowStart = (n - 1) / cols * cols;
                int rows = lastRowStart / cols;
                rowH = rows > 0 ? (GetItemRect(lastRowStart).Y - r0.Y) / rows
                                : Math.Max(1, GetItemRect(cols).Y - r0.Y);
            }
            _gridCacheKey = key;
            _gridCache = (cols, rowH);
            return _gridCache;
        }

        /// <summary>Index of the first item of the topmost visible grid row.</summary>
        public int FirstVisibleIndex
        {
            get
            {
                if (!IsHandleCreated || VirtualListSize == 0) return 0;
                try
                {
                    var (cols, rowH) = Grid();
                    if (rowH <= 0 || rowH == int.MaxValue) return 0;
                    return Math.Clamp(Math.Max(0, (ScrollTop() + rowH / 2) / rowH) * cols, 0, VirtualListSize - 1);
                }
                catch { return 0; }
            }
        }

        /// <summary>Items one viewport holds (whole grid rows).</summary>
        public int ItemsPerPage
        {
            get
            {
                if (!IsHandleCreated || VirtualListSize == 0) return 1;
                try
                {
                    var (cols, rowH) = Grid();
                    if (rowH <= 0 || rowH == int.MaxValue) return Math.Max(1, VirtualListSize);
                    return Math.Max(1, ClientSize.Height / rowH) * cols;
                }
                catch { return 1; }
            }
        }

        /// <summary>Pixel-scroll so <paramref name="index"/>'s grid row lands at the top (the
        /// index bar's jump). LVM_SCROLL takes pixels in icon view.</summary>
        public void ScrollItemToTop(int index)
        {
            if (!IsHandleCreated || VirtualListSize == 0) return;
            index = Math.Clamp(index, 0, VirtualListSize - 1);
            try
            {
                var (_, rowH) = Grid();
                if (rowH == int.MaxValue) return;   // single row — nowhere to scroll
                // Both rects live in the CURRENT client space; the grid's top margin in that space
                // is item 0's Y plus the scroll already applied. Scrolling by the difference puts
                // this item's row exactly where row 0 sits when the grid is unscrolled.
                int margin = GetItemRect(0).Y + ScrollTop();
                int dy = GetItemRect(index).Y - margin;
                SendMessage(Handle, LVM_SCROLL_P, IntPtr.Zero, (IntPtr)dy);
            }
            catch { }
        }

        /// <summary>Diagnostic: the raw grid geometry behind FirstVisibleIndex / ScrollFraction.</summary>
        public string DiagGeom(int probe)
        {
            try
            {
                var (cols, rowH) = Grid();
                probe = Math.Clamp(probe, 0, Math.Max(0, VirtualListSize - 1));
                var r0 = GetItemRect(0);
                var rp = GetItemRect(probe);
                return $"n={VirtualListSize} cols={cols} rowH={rowH} scrollTop={ScrollTop()} clientH={ClientSize.Height} "
                     + $"maxScroll={MaxScroll()} item0.Y={r0.Y} item0.H={r0.Height} item{probe}.Y={rp.Y} "
                     + $"firstVisible={FirstVisibleIndex} itemsPerPage={ItemsPerPage} frac={ScrollFraction:0.0000}";
            }
            catch (Exception ex) { return "DiagGeom: " + ex.Message; }
        }

        /// <summary>Total scrollable pixels — content height beyond one viewport.</summary>
        private int MaxScroll()
        {
            var (cols, rowH) = Grid();
            if (rowH <= 0 || rowH == int.MaxValue) return 0;
            int rows = (VirtualListSize + cols - 1) / cols;
            int margin = GetItemRect(0).Y + ScrollTop();   // the grid's top inset, scroll-independent
            return Math.Max(0, margin + rows * rowH - ClientSize.Height);
        }

        /// <summary>Scroll by N wheel lines — a "line" is one grid ROW here (negative = up). The
        /// index bar drives the wheel through this instead of forwarding WM_MOUSEWHEEL, which a
        /// list view with its scrollbar style stripped is free to ignore.</summary>
        public void ScrollLines(int lines)
        {
            if (!IsHandleCreated || VirtualListSize == 0 || lines == 0) return;
            try
            {
                var (_, rowH) = Grid();
                if (rowH <= 0 || rowH == int.MaxValue) return;
                SendMessage(Handle, LVM_SCROLL_P, IntPtr.Zero, (IntPtr)(lines * rowH));
            }
            catch { }
        }

        /// <summary>Park the viewport at a fraction (0..1) of its scroll range, in PIXELS — what
        /// lets an index drag stop between two grid rows. Mapping the drag to an item index and
        /// scrolling that row to the top made a short grid move in visible notches: five item
        /// indices share one row, and every row snapped to the top of the view.</summary>
        public void ScrollToFraction(double f)
        {
            if (!IsHandleCreated || VirtualListSize == 0) return;
            try
            {
                int target = (int)Math.Round(Math.Clamp(f, 0, 1) * MaxScroll());
                SendMessage(Handle, LVM_SCROLL_P, IntPtr.Zero, (IntPtr)(target - ScrollTop()));
            }
            catch { }
        }

        /// <summary>Where the viewport sits in its scroll range (0..1) — drives the index thumb, so
        /// the thumb tracks a pixel-precise drag instead of jumping row to row.</summary>
        public double ScrollFraction
        {
            get
            {
                if (!IsHandleCreated || VirtualListSize == 0) return 0;
                try { int max = MaxScroll(); return max <= 0 ? 0 : Math.Clamp(ScrollTop() / (double)max, 0, 1); }
                catch { return 0; }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (_hideVScroll && m.Msg == WM_NCCALCSIZE_P && IsHandleCreated)
            {
                int style = GetWindowLong(Handle, GWL_STYLE_P);
                if ((style & WS_VSCROLL_P) != 0) SetWindowLong(Handle, GWL_STYLE_P, style & ~WS_VSCROLL_P);
            }
            // The wheel, by hand. comctl's ICON view refuses to scroll on WM_MOUSEWHEEL once
            // WS_VSCROLL is stripped (probe-verified: the message moves the report list but leaves
            // the grid exactly where it was), so the poster was wheel-dead over its whole surface
            // — the index strip merely made it visible. Scroll by whole grid rows and swallow it.
            if (m.Msg == WM_MOUSEWHEEL_P)
            {
                int notches = unchecked((short)((long)m.WParam >> 16)) / 120;
                if (notches != 0)
                {
                    int lines = SystemInformation.MouseWheelScrollLines;
                    if (lines <= 0) lines = 3;   // "one screen at a time" (-1) — the usual default
                    ScrollLines(-notches * lines);
                    Scrolled?.Invoke();
                    return;
                }
            }
            if (m.Msg is WM_MOUSEWHEEL_P or WM_VSCROLL_P or WM_KEYDOWN)
                Scrolled?.Invoke();
            if (VirtualListSize > 0 && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_KEYDOWN))
            {
                bool shift = (ModifierKeys & Keys.Shift) != 0;
                bool ctrl = (ModifierKeys & Keys.Control) != 0;

                if (m.Msg == WM_LBUTTONDOWN)
                {
                    int lp = unchecked((int)m.LParam.ToInt64());
                    int idx = GetItemAt((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF))?.Index ?? -1;
                    if (idx >= 0 && shift && !ctrl)
                    {
                        // Do NOT let the native handler run — its virtual-mode Shift range is wrong AND would
                        // flash the wide selection before we correct it. We know the clicked item, so select
                        // exactly [anchor..idx] ourselves and take focus.
                        SelectRange(_anchor >= 0 ? _anchor : idx, idx);
                        if (!Focused) Focus();
                        return;
                    }
                    base.WndProc(ref m);
                    if (idx >= 0 && !shift) _anchor = idx;   // plain / ctrl → new anchor
                    return;
                }

                // WM_KEYDOWN: only take over grid navigation; everything else is native.
                var key = (Keys)unchecked((int)m.WParam.ToInt64());
                bool nav = key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                                or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown;
                if (nav)
                {
                    if (shift && !ctrl)
                    {
                        // Native knows the grid geometry, so let it MOVE the focus — but wrapped in
                        // Begin/EndUpdate so its (wrong) intermediate selection never paints; we then set the
                        // correct range from the anchor and paint once.
                        BeginUpdate();
                        try
                        {
                            base.WndProc(ref m);
                            int f = (int)SendMessage(Handle, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_FOCUSED);
                            if (f >= 0) SelectRange(_anchor >= 0 ? _anchor : f, f);
                        }
                        finally { EndUpdate(); }
                        return;
                    }
                    base.WndProc(ref m);
                    int nf = (int)SendMessage(Handle, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_FOCUSED);
                    if (nf >= 0) _anchor = nf;   // plain nav → new anchor
                    return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>Select every tile in one message (Ctrl+A). Index -1 targets all items, so this costs the
        /// same whether the view holds ten games or ten thousand — the list view has done it this way all
        /// along, the poster simply had no Ctrl+A wired to it.</summary>
        public void SelectAllItems()
        {
            if (!IsHandleCreated || VirtualListSize == 0) return;
            var all = new LVITEM { stateMask = LVIS_SELECTED, state = LVIS_SELECTED };
            SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)(-1), ref all);
            _anchor = 0;   // a later Shift+click extends from the top, as it does in the list
        }

        /// <summary>Deselect everything in one message — same reason as <see cref="SelectAllItems"/>:
        /// index -1 means "all items", so it does not depend on how many are currently selected.</summary>
        public void ClearSelection()
        {
            if (!IsHandleCreated) return;
            var none = new LVITEM { stateMask = LVIS_SELECTED, state = 0 };
            SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)(-1), ref none);
        }

        /// <summary>Every selected index, walked ONCE through the native control.
        ///
        /// Not <see cref="ListView.SelectedIndices"/>: in virtual mode its indexer keeps no cursor, so
        /// asking for element i replays i LVM_GETNEXTITEM messages from the start, and enumerating the
        /// whole selection costs O(n²). Selecting a few thousand tiles by Shift+click then froze the UI
        /// for as long as it took to make millions of round trips. LVM_GETNEXTITEM already means "the
        /// selected item AFTER this one", so carrying the cursor forward makes the walk linear.</summary>
        public List<int> SelectedIndicesFast()
        {
            var res = new List<int>();
            if (!IsHandleCreated) return res;
            for (int i = (int)SendMessage(Handle, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED);
                 i >= 0;
                 i = (int)SendMessage(Handle, LVM_GETNEXTITEM, (IntPtr)i, (IntPtr)LVNI_SELECTED))
                res.Add(i);
            return res;
        }

        // Select exactly [min(a,b)..max(a,b)], clearing the rest; focus the moving end. Anchor unchanged.
        // Wrapped in Begin/EndUpdate so the clear-then-set sequence paints once, not as a flash of nothing.
        internal void SelectRange(int a, int b)   // internal: measured by --selftest-selection
        {
            if (!IsHandleCreated) return;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            BeginUpdate();
            try
            {
                var clear = new LVITEM { stateMask = LVIS_SELECTED, state = 0 };
                SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)(-1), ref clear);   // -1 = all items
                var sel = new LVITEM { stateMask = LVIS_SELECTED, state = LVIS_SELECTED };
                for (int i = lo; i <= hi; i++) SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)i, ref sel);
                var foc = new LVITEM { stateMask = LVIS_FOCUSED, state = LVIS_FOCUSED };
                SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)b, ref foc);
            }
            finally { EndUpdate(); }
        }
    }

    private PosterListView BuildPoster()
    {
        bool od = _posterOwnerDraw;
        if (od) _posterGeom = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(PCellW, PImgH + PLabelH) };
        else _himl = ImageList_Create(PCellW, PImgH + PLabelH, ILC_COLOR32, 0, 64);   // 32bpp: matches the screen depth → direct (fast) blit on scroll
        var lv = new PosterListView
        {
            // NOT docked: LayoutPoster gives it a left margin of (leftover/2) and extends it to the
            // panel's right edge — so icons (left-aligned) start at the centred position, the empty
            // slack falls on the right, and the vertical scrollbar stays at the right edge.
            Dock = DockStyle.None, View = View.LargeIcon, VirtualMode = true, OwnerDraw = od,
            BackColor = Center, ForeColor = Fg, BorderStyle = BorderStyle.None, MultiSelect = true,
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
        lv.KeyPress += OnGameListKeyPress;              // hors tri Titre : la frappe FILTRE
        lv.KeyDown += OnGameListKeyDown;                // Retour arrière / Échap sur ce filtre
        lv.SelectedIndexChanged += (_, _) => OnPosterSelectionChanged();
        lv.ItemActivate += (_, _) => LaunchSelected();
        lv.MouseUp += OnPosterMouseUp;   // right-click → same game context menu as the list
        lv.HandleCreated += (_, _) =>
        {
            if (!od) SendMessage(lv.Handle, LVM_SETIMAGELIST, (IntPtr)LVSIL_NORMAL, _himl);   // our native list (WinForms left null)
            SetIconSpacing(lv, PCellW + PGap, PImgH + PLabelH + PGap);
            lv.InvalidateGridCache();   // every spacing push re-tiles: no measured stride survives it
            EnableListViewDoubleBuffer(lv);
        };
        return lv;
    }

    // Provide the (virtual) item: just an image-list slot — the composited tile carries box + text.
    //
    // Nothing is built once the window is on its way out. WinForms walks EVERY virtual item when the
    // native window dies (ReleaseUiaProvider, on WM_DESTROY, asks the collection for each index), and a
    // virtual list keeps no item cache — every one of those asks lands here. Building a tile then means
    // loading and decoding the whole library's box art AFTER the window is off screen, on the very UI
    // thread the exit path joins: 15 s of ghost process and ~700 MB of churn on a 5000-game library,
    // with nothing left to draw it on. The item WinForms gets back is released on the spot and never
    // owned an accessibility object, so that walk frees nothing either way — only the tile is wasted.
    // Hand back the blank item (never null: the Items[i] path throws on a null virtual item) and skip
    // the build.
    private void OnPosterRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        int slot = -1;
        if (!_closing && !Disposing && !IsDisposed)
        {
            var model = PosterModel(e.ItemIndex);
            if (model != null && Guid.TryParse(S(Safe(() => model.Id)), out var id)) slot = SlotFor(model, id);
        }
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
        int prevN = 0; try { prevN = _poster.VirtualListSize; } catch { }   // to detect a shrink
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
        // Same shrink guard as the list view: a smaller count leaves the native scroll parked past the
        // new end (blank grid), so snap back to the top when the item count dropped.
        if (n > 0 && n < prevN) { try { _poster.EnsureVisible(0); } catch { } }
        _poster.Invalidate();
    }

    // Position the poster ListView so the icon grid looks centred while the scrollbar stays at the
    // right edge: left margin = leftover/2, width extends to the panel's right edge. Icons left-align
    // → start at the centred position; the slack falls on the right; the scrollbar is at the far right.
    private void LayoutPoster()
    {
        if (_poster == null || !_posterMode) return;
        var parent = _poster.Parent; if (parent == null) return;
        // The index strip is DOCKED but the poster is not (Dock.None, centred by hand), so the
        // grid must subtract the strip's width itself or it slides underneath.
        int barW = _gameIndexOn && _gameIndex != null ? _gameIndex.ReservedWidth : 0;
        int pw = parent.ClientSize.Width - barW, ph = parent.ClientSize.Height;
        if (pw <= 0 || ph <= 0) return;
        int strideY = PImgH + PLabelH + PGap;
        int count = _poster.VirtualListSize;
        // With the index on there IS no native scrollbar — reserving its width would just skew
        // the centring.
        int sbw = barW > 0 ? 0 : SystemInformation.VerticalScrollBarWidth;

        int cols0 = Math.Max(1, pw / (PCellW + PGap));
        int rows = (count + cols0 - 1) / cols0;
        bool scroll = (long)rows * strideY > ph;        // would the grid overflow vertically?
        int effW = pw - (scroll ? sbw : 0);             // width usable for columns

        // The inter-tile gap is ELASTIC: when squeezing it (down to a floor) buys one or more
        // extra columns, spend the margin on tiles instead of empty centring slack — the index
        // strip's reserved width stops costing a whole column. With room to spare the gap stays
        // nominal. (The trailing gap doesn't need to fit: the last column ends at its tiles.)
        int minGap = 1;   // Mehdi's call: in the worst case the tiles may all but touch
        // comctl's items-per-row test anticipates a vertical scrollbar even when the style is
        // stripped (wraps on roughly clientW − (scrollbar + edge)), and cols/gap must agree
        // INCLUDING the trailing gap — both learned live from dead-lane regressions.
        int wrapMargin = SystemInformation.VerticalScrollBarWidth + 4;
        int usable = Math.Max(PCellW, effW - wrapMargin);
        int Fit(int w, int g) => Math.Max(1, Math.Max(PCellW, w - wrapMargin) / (PCellW + g));
        // NOMINAL gap and balanced margins by default. The gap squeezes (down to 1px) ONLY when
        // the index strip's reserved width actually COSTS columns against a strip-less layout —
        // max-packing unconditionally crammed 1px gaps everywhere and ate the left margin.
        int colsNoStrip = Fit(parent.ClientSize.Width - (barW > 0 && scroll ? SystemInformation.VerticalScrollBarWidth : 0), PGap);
        int cols = Fit(effW, PGap);
        if (cols < colsNoStrip) cols = Math.Min(colsNoStrip, Fit(effW, minGap));
        int gap = Math.Min(PGap, Math.Max(minGap, (usable - cols * PCellW) / cols));
        int strideX = PCellW + gap;
        ApplyPosterIconSpacing(strideX, strideY);
        int gridW = cols * strideX;
        // Centre on the VISUAL extent (no trailing gap), bounded so the wrap keeps its columns.
        int left = Math.Max(0, (effW - (gridW - gap)) / 2);
        left = Math.Max(0, Math.Min(left, pw - gridW - wrapMargin));
        _poster.Bounds = new Rectangle(left, 0, pw - left, ph);   // extend to the right edge (scrollbar there)
        // comctl's items-per-row test carries an INTERNAL margin no formula sees — measure what it
        // actually wrapped and, when a column we counted on jumped away, hand the slack back as
        // width until it returns. Converges in a step or two; without it the missing column left a
        // dead lane on the right (seen live at two window widths).
        for (int i = 0; i < 3 && left > 0; i++)
        {
            int actual = _poster.NativeColumns;
            if (actual >= cols) break;
            left = Math.Max(0, left - (cols - actual) * strideX / 2 - 4);
            _poster.Bounds = new Rectangle(left, 0, pw - left, ph);
        }
        _posterBaseX = left;
        _posterGridRight = left + gridW - gap;          // last tile's right edge, no trailing gap
        _posterCols = cols;
        _posterGap = gap;
        _posterSqueezed = false;
        NudgePosterForOverlay();   // the bar may already be expanded over the fresh layout
    }

    private int _posterSpacingX;   // last icon spacing pushed to the native grid (0 = never)

    /// <summary>Push the elastic horizontal spacing to the native list when it changed. The
    /// vertical stride stays nominal — only the inter-column margin breathes.</summary>
    private void ApplyPosterIconSpacing(int cx, int cy)
    {
        if (_posterSpacingX == cx || _poster == null || !_poster.IsHandleCreated) return;
        _posterSpacingX = cx;
        try { SetIconSpacing(_poster, cx, cy); _poster.InvalidateGridCache(); _poster.Invalidate(); } catch { }
    }

    /// <summary>While the index strip is EXPANDED over the grid (hover/drag in auto-hide mode), a
    /// tile column can end up underneath it. Two escapes, in order, both keeping the COLUMN COUNT
    /// pinned so the drag mapping never moves: slide the grid left into its centring slack, then
    /// SQUEEZE the inter-tile gap (down to 1px) to shorten the row itself. Only when both run out
    /// does the last column stay partially covered. Collapse restores the laid-out state.</summary>
    private void NudgePosterForOverlay()
    {
        if (_poster == null || !_posterMode || _gameIndex == null) return;
        if (!(_gameIndexOn && _gameIndex.Visible)) return;
        bool expanded = _gameIndex.Width > _gameIndex.ReservedWidth + 2;
        if (!expanded)
        {
            // Collapsed again: undo the squeeze via a full relayout, or just the translation.
            if (_posterSqueezed) LayoutPoster();
            else if (_poster.Left != _posterBaseX) _poster.Left = _posterBaseX;
            return;
        }

        var parent = _poster.Parent; if (parent == null) return;
        int avail = _gameIndex.Left;   // the grid must end before the expanded strip
        int cols = Math.Max(1, _posterCols);

        // 1) translation covers it? extent at the LAID-OUT gap, trailing gap excluded.
        int extentBase = cols * PCellW + (cols - 1) * _posterGap;
        if (_posterBaseX + extentBase <= avail || extentBase <= avail)
        {
            // A previous squeeze is undone by a full relayout, whose own trailing Nudge re-places
            // everything with fresh numbers — continuing here would reuse stale ones.
            if (_posterSqueezed) { LayoutPoster(); return; }
            int lx = Math.Max(0, Math.Min(_posterBaseX, avail - extentBase));
            if (_poster.Left != lx) _poster.Left = lx;
            return;
        }

        // 2) squeeze: the tightest gap that clears the strip, floored at 1px — cols stays pinned.
        int gap = cols <= 1 ? _posterGap
                : Math.Clamp((avail - cols * PCellW) / Math.Max(1, cols - 1), 1, _posterGap);
        int stride = PCellW + gap;
        int extent = cols * PCellW + (cols - 1) * gap;
        int left = Math.Max(0, Math.Min(_posterBaseX, avail - extent));
        ApplyPosterIconSpacing(stride, PImgH + PLabelH + PGap);
        // Width chosen so comctl keeps EXACTLY cols columns at this stride (its wrap anticipates a
        // scrollbar; NativeColumns verifies and hands back more width if a column still dropped).
        int wrapMargin = SystemInformation.VerticalScrollBarWidth + 4;
        int width = Math.Min(parent.ClientSize.Width - left, cols * stride + wrapMargin + 8);
        _poster.Bounds = new Rectangle(left, 0, width, parent.ClientSize.Height);
        for (int i = 0; i < 2; i++)
        {
            int actual = _poster.NativeColumns;
            if (actual >= cols || left + width >= parent.ClientSize.Width) break;
            width = Math.Min(parent.ClientSize.Width - left, width + stride / 2);
            _poster.Bounds = new Rectangle(left, 0, width, parent.ClientSize.Height);
        }
        _posterSqueezed = true;
    }

    // ── Central-panel zoom (Ctrl +/- , Ctrl-wheel, Ctrl-0) ────────────────────────────────────
    // One level (0.5–2.0, 25% steps) drives BOTH views: the detail list scales its font + row height
    // + re-fits its columns; the poster grid scales its tile geometry so more/fewer posters fit a row.
    // Persisted as ZoomPercent; only fires when the central panel (list or poster) has focus.
    private bool CentralPanelHasFocus()
        => (_games != null && _games.Focused) || (_poster != null && _poster.Focused);

    private void ChangeZoom(int steps)
        => ApplyZoomLevel(Math.Round((_zoom + steps * 0.25) / 0.25) * 0.25);

    private void ApplyZoomLevel(double z)
    {
        z = Math.Clamp(z, 0.5, 2.0);
        if (Math.Abs(z - _zoom) < 0.001) return;
        _zoom = z;
        _cfg.SetInt("ZoomPercent", (int)Math.Round(_zoom * 100)); _cfg.Save();
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        try { _games?.SetZoom((float)_zoom, 9f); } catch { }
        try { RebuildPosterGeometry(); } catch { }
        if (_posterMode) { try { RefreshPoster(); LayoutPoster(); } catch { } }
        else { try { _poster?.Invalidate(); } catch { } }
    }

    // The poster tiles + decoded thumbs are all sized for the PREVIOUS zoom, and the native image list
    // has fixed-size slots — so a zoom change means: free every cached tile/thumb, reset the slot
    // bookkeeping, and recreate the image list at the new cell size. Tiles then rebuild lazily (at the
    // new geometry) on the next retrieval. Guarded/try-wrapped: it must never take the app down.
    private void RebuildPosterGeometry()
    {
        if (_poster == null) return;
        foreach (var h in _posterTileHbm.Values) if (h != IntPtr.Zero) DeleteObject(h);
        _posterTileHbm.Clear(); _posterTileOrder.Clear();
        foreach (var img in _posterBmp.Values) img?.Dispose();
        _posterBmp.Clear();
        // The FIFO must reset WITH the dictionary it evicts for: ids left behind inflate its count, and
        // the eviction loop then throws out FRESH thumbs until the stale ids drain through.
        _posterBmpOrder.Clear();
        // Queued and ready work belongs to the OLD geometry/group — a decode landing after this rebuild
        // would insert a thumb sized for the previous cell (or resolved for the previous image group)
        // into the fresh caches. Dropped here; pending ids go with it so tiles re-queue cleanly. (A
        // worker mid-decode can still land ≤ PosterMaxWorkers stale entries — rare, evicts normally.)
        lock (_posterQLock)
        {
            _posterReq.Clear(); _posterPending.Clear();
            while (_posterDone.Count > 0) { var (_, _, img) = _posterDone.Dequeue(); img?.Dispose(); }
        }
        _slotOf.Clear(); _slotId.Clear(); _slotCount = 0; _slotLru.Clear(); _slotNode.Clear();
        _posterTileFont?.Dispose(); _posterTileFont = null;

        if (_posterOwnerDraw)
        {
            var oldGeom = _posterGeom;
            int cw = Math.Min(256, PCellW), ch = Math.Min(256, PImgH + PLabelH);   // managed ImageList caps at 256
            _posterGeom = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(cw, ch) };
            try { if (_poster.IsHandleCreated) _poster.LargeImageList = _posterGeom; } catch { }
            oldGeom?.Dispose();
        }
        else
        {
            var oldHiml = _himl;
            _himl = ImageList_Create(PCellW, PImgH + PLabelH, ILC_COLOR32, 0, 64);   // 32bpp: matches the screen depth → direct (fast) blit on scroll
            try { if (_poster.IsHandleCreated) SendMessage(_poster.Handle, LVM_SETIMAGELIST, (IntPtr)LVSIL_NORMAL, _himl); } catch { }
            if (oldHiml != IntPtr.Zero) ImageList_Destroy(oldHiml);
        }
        try { if (_poster.IsHandleCreated) { SetIconSpacing(_poster, PCellW + PGap, PImgH + PLabelH + PGap); _poster.InvalidateGridCache(); } } catch { }
        _posterSpacingX = PCellW + PGap;   // keep the elastic-gap cache honest (LayoutPoster re-squeezes right after)
        // The whole ladder, at this geometry: the one line that says what the slider actually buys.
        var ladder = new System.Text.StringBuilder();
        for (int lv = 1; lv <= 10; lv++)
        {
            var (t, th) = PosterCapsFor(PosterMemFactors[lv - 1]);
            long bytes = (long)t * PosterSlotBytes + (long)th * PosterThumbBytes;
            ladder.Append(lv == PosterMemLevel ? " [" : " ").Append(lv).Append('=')
                  .Append(bytes / (1024 * 1024)).Append(lv == PosterMemLevel ? "MB]" : "MB");
        }
        Console.WriteLine($"[poster] geometry {PCellW}x{PImgH + PLabelH} (zoom {_zoom:0.00}), "
            + $"memory level {PosterMemLevel} -> {PosterMemEstimate()}");
        Console.WriteLine($"[poster] memory ladder:{ladder}");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // A clicked WinForms button retains keyboard focus, so Space used to "click" the inline video's
        // fullscreen button again. While a video occupies the media box, Space belongs to play/pause
        // regardless of which non-text child currently has focus. Fullscreen owns its own Space handler.
        if (keyData == Keys.Space && _mediaVideo is { Visible: true, HasContent: true }
                                  && !FocusedControlAcceptsText())
        {
            _mediaVideo.TogglePlayPause();
            return true;
        }

        if ((keyData & Keys.Control) == Keys.Control)
        {
            var k = keyData & Keys.KeyCode;
            // Ctrl+0 → reset zoom to 100%. Ungated on purpose: it's a harmless global reset, and
            // requiring central-panel keyboard focus made it miss right after a Ctrl-wheel zoom (the
            // wheel leaves focus wherever it was, so the list usually isn't keyboard-focused then).
            if (k == Keys.D0 || k == Keys.NumPad0) { ApplyZoomLevel(1.0); return true; }
            // Ctrl+A → select every game, in whichever central view has the keyboard. The poster used to be
            // left out on the assumption that the native control handled it: a virtual-mode ListView does
            // not, so the key simply did nothing there.
            if (k == Keys.A)
            {
                if (_posterMode && _poster != null && _poster.Focused) { _poster.SelectAllItems(); OnPosterSelectionChanged(); return true; }
                if (_games != null && _games.Focused) { _games.SelectAll(); return true; }
            }
            if (CentralPanelHasFocus())
            {
                switch (k)
                {
                    case Keys.Oemplus: case Keys.Add:       ChangeZoom(+1); return true;
                    case Keys.OemMinus: case Keys.Subtract: ChangeZoom(-1); return true;
                }
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool FocusedControlAcceptsText()
    {
        Control? focused = FindFocusedControl(this);
        return focused is TextBoxBase { ReadOnly: false }
            || focused is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList };

        static Control? FindFocusedControl(Control root)
        {
            if (root.Focused) return root;
            foreach (Control child in root.Controls)
                if (child.ContainsFocus) return FindFocusedControl(child) ?? child;
            return null;
        }
    }

    // Ctrl-wheel over (or with focus in) the central panel zooms instead of scrolling. This filter
    // catches WM_MOUSEWHEEL before the native list scrolls it; returning true swallows the scroll.
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL || (ModifierKeys & Keys.Control) != Keys.Control) return false;
        bool central =
            (_games  != null && (_games.Focused  || (_games.IsHandleCreated  && m.HWnd == _games.Handle))) ||
            (_poster != null && (_poster.Focused || (_poster.IsHandleCreated && m.HWnd == _poster.Handle)));
        if (!central) return false;
        int delta = unchecked((short)((long)m.WParam >> 16));
        ChangeZoom(delta > 0 ? +1 : -1);
        return true;
    }

    private IndexWheelFilter _wheelFilter;

    /// <summary>Diagnostic (env LB_WHEEL_SELFTEST=1): exercises the index strip's wheel path in
    /// BOTH views without injected input — UIPI blocks that against an elevated LiteBox, so a
    /// synthetic wheel from outside never arrives. Parks our own cursor on the strip, hands the
    /// filter a fabricated WM_MOUSEWHEEL, and reports whether the view actually moved.</summary>
    private async void RunWheelSelfTest()
    {
        async System.Threading.Tasks.Task Settle(int ms)
        { for (int i = 0; i < Math.Max(1, ms / 50); i++) { Application.DoEvents(); await System.Threading.Tasks.Task.Delay(50); } }

        await Settle(2500);
        foreach (bool poster in new[] { false, true })
        {
            try { SetPosterMode(poster); } catch (Exception ex) { Console.WriteLine("[wheel] mode: " + ex.Message); }
            await Settle(1800);
            string mode = poster ? "poster" : "list";
            var bar = _gameIndex;
            int Pos() { try { return poster ? _poster.FirstVisibleIndex : _games.TopRowIndex; } catch { return -1; } }
            Console.WriteLine($"[wheel] --- {mode}: indexOn={_gameIndexOn} visible={bar?.Visible} barW={bar?.Width} reserved={bar?.ReservedWidth}");

            int p0 = Pos();
            try { if (poster) _poster.ScrollLines(3); else _games.ScrollLines(3); }
            catch (Exception ex) { Console.WriteLine("[wheel] ScrollLines threw: " + ex.Message); }
            await Settle(700);
            Console.WriteLine($"[wheel] {mode}: ScrollLines(3) {p0} -> {Pos()}");

            // Does the VIEW scroll on a wheel message of its own? Both views run with WS_VSCROLL
            // stripped, and a list view in that state may simply refuse to scroll — which would
            // make the wheel dead over the grid itself, not just over the strip.
            Control view = poster ? _poster : (Control)_games;
            int pw = Pos();
            var vc = view.PointToScreen(new Point(view.Width / 2, view.Height / 2));
            try { SendMessage(view.Handle, 0x020A, (IntPtr)(-120 << 16), (IntPtr)((vc.Y << 16) | (vc.X & 0xFFFF))); } catch { }
            await Settle(700);
            Console.WriteLine($"[wheel] {mode}: native WM_MOUSEWHEEL on the view {pw} -> {Pos()}");

            if (bar == null) continue;
            try { Cursor.Position = bar.PointToScreen(new Point(Math.Max(1, bar.Width / 2), bar.Height / 2)); } catch { }
            await Settle(500);
            int p1 = Pos();
            bool handled = false;
            var msg = Message.Create(bar.Handle, 0x020A, (IntPtr)(-120 << 16), IntPtr.Zero);
            try { handled = _wheelFilter.PreFilterMessage(ref msg); }
            catch (Exception ex) { Console.WriteLine("[wheel] filter threw: " + ex.Message); }
            await Settle(700);
            Console.WriteLine($"[wheel] {mode}: filter handled={handled}  {p1} -> {Pos()}");
        }
        Console.WriteLine("[wheel] done");
        try { Close(); } catch { }
    }

    /// <summary>Diagnostic (env LB_INDEX_DIAG=&lt;letter&gt;): parks the poster so that the named
    /// group's FIRST game sits on the last visible row — the moment the eye says "we are just
    /// reaching N" — then dumps the thumb's position against every marker's. Measures the two
    /// scales the strip juggles instead of guessing at them from a screenshot.</summary>
    private async void RunIndexDiag(string letter)
    {
        async System.Threading.Tasks.Task Settle(int ms)
        { for (int i = 0; i < Math.Max(1, ms / 50); i++) { Application.DoEvents(); await System.Threading.Tasks.Task.Delay(50); } }

        await Settle(3000);
        try { SetPosterMode(true); } catch { }
        await Settle(2000);
        try
        {
            var groups = ComputeIndexGroups();
            int gi = -1;
            for (int i = 0; i < groups.Count; i++)
                if ((groups[i].Label ?? "").StartsWith(letter, StringComparison.OrdinalIgnoreCase)) { gi = i; break; }
            if (gi < 0) { Console.WriteLine($"[index] no group starting with '{letter}'"); Close(); return; }
            int cols = _poster.NativeColumns, page = _poster.ItemsPerPage;
            int target = groups[gi].Index;
            _poster.ScrollItemToTop(Math.Max(0, target - (page - cols)));   // that game on the LAST visible row
            await Settle(1200);
            Console.WriteLine($"[index] target '{groups[gi].Label}' idx={target} asked ScrollItemToTop({Math.Max(0, target - (page - cols))})");
            Console.WriteLine("[index] geom " + _poster.DiagGeom(target));
            Console.Write(_gameIndex.DiagDump());
        }
        catch (Exception ex) { Console.WriteLine("[index] " + ex.Message); }
        Console.WriteLine("[index] done");
        try { Close(); } catch { }
    }

    /// <summary>Routes a wheel whose pointer sits over the Game List Index strip to the active
    /// view. The strip's LANE is used, not the control's bounds: collapsed it is a 14px sliver, and
    /// the wheel must work there exactly as it does over the expanded labels.</summary>
    private sealed class IndexWheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly MainWindow _w;
        public IndexWheelFilter(MainWindow w) => _w = w;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            var bar = _w._gameIndex;
            if (bar == null || !_w._gameIndexOn || !bar.IsHandleCreated) return false;
            // A modal dialog of ours is up: its own scrolling wins, never the list behind it.
            if (Form.ActiveForm != _w) return false;
            var host = bar.Parent;
            if (host == null || !host.IsHandleCreated) return false;
            Point p;
            try { p = host.PointToClient(Cursor.Position); } catch { return false; }
            if (!host.ClientRectangle.Contains(p)) return false;
            int lane = Math.Max(bar.Width, bar.ReservedWidth);   // expanded width, else the sliver
            if (p.X < host.ClientSize.Width - lane) return false;

            int notches = unchecked((short)((long)m.WParam >> 16)) / 120;
            if (notches == 0) return false;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;   // "one screen at a time" (-1) — treat as the usual default
            try
            {
                if (_w._posterMode) _w._poster.ScrollLines(-notches * lines);
                else _w._games.ScrollLines(-notches * lines);
            }
            catch { }
            try { bar.Invalidate(); } catch { }   // the position indicator moved
            return true;                          // handled — never dispatched twice
        }
    }

    private void SetPosterMode(bool on)
    {
        if (_posterMode == on || _poster == null) return;
        _posterMode = on;
        try { ApplyGameListIndexOptions(); } catch { }   // re-aim the index's scroll plumbing at the new view
        try { SyncViewSwitchChecks(); } catch { }   // menu bar: Images View / List View check marks
        _cfg.SetBool("PosterMode", on); _cfg.Save();
        if (on)
        {
            RefreshPoster();
            _games.Visible = false;          // hide the list behind: the poster's left margin would reveal it
            _poster.Visible = true; _poster.BringToFront();
            _gameIndex?.BringToFront();   // the strip stays above whichever view is frontmost
            LayoutPoster();
            try { ApplyDarkScroll(_poster); } catch { }
            try { _poster.ReassertScrollHiding(); } catch { }   // the theme pass can resurface the bar
            try { if (_poster.IsHandleCreated) EnableListViewDoubleBuffer(_poster); } catch { }   // SetWindowTheme can clear ex-styles
            try { ActiveControl = _poster; _poster.Focus(); } catch { }
        }
        else
        {
            _poster.Visible = false; _games.Visible = true; _games.BringToFront();
            _gameIndex?.BringToFront();   // above the list too — else the hover expansion unfolds UNDERNEATH it
            try { ActiveControl = _games; _games.Focus(); } catch { }
            // Leaving the poster RELEASES its image memory outright (user decision): a list-mode session
            // should not keep hundreds of MB of tiles idle for a view that is not on screen. Same full
            // drop as a zoom change — coming back is a cold rebuild, tiles refill lazily as they show.
            // The list view itself holds no box art (its only images are the badge strips).
            try { RebuildPosterGeometry(); } catch { }
        }
    }

    /// <summary>Pick the image type the poster tiles show. One state, two surfaces: both Image Group
    /// menus are re-stamped, then the tile caches are rebuilt (the zoom pattern) and the poster
    /// repainted when it is the live view.</summary>
    private void SelectPosterGroup(string key)
    {
        _posterGroup = key;
        try { _cfg.Set("PosterImageGroup", key); _cfg.Save(); } catch { }
        try { SyncImageGroupChecks(); } catch { }
        try { RebuildPosterGeometry(); } catch { }
        if (_posterMode) { try { RefreshPoster(); LayoutPoster(); } catch { } }
    }

    private bool _posterSyncPending;

    private void OnPosterSelectionChanged()
    {
        if (_poster.SelectedIndices.Count == 0) return;
        // Mirror the poster selection into the hidden list AFTER the native control finishes — doing it
        // synchronously (mid mouse/key handling) manipulated another control's state during range selection.
        // Coalesced: a Shift range fires many changes, we mirror once when it settles. (Range selection itself
        // is handled correctly by PosterListView, which owns the Shift anchor.)
        if (_posterSyncPending) return;
        _posterSyncPending = true;
        try { BeginInvoke((Action)(() => { _posterSyncPending = false; MirrorPosterToList(); })); }
        catch { _posterSyncPending = false; }
    }

    // focus:false — keep keyboard focus on the poster (SelectGame(...,true) would steal it to the hidden
    // list, freezing poster arrow navigation). The list selection still drives ShowDetails + LastGame and
    // feeds SelectedGamesProvider (so edits / plugin menus see the whole poster selection).
    private void MirrorPosterToList()
    {
        if (_poster == null) return;
        var idx = _poster.SelectedIndicesFast();   // one walk, reused below — see SelectedIndicesFast
        if (idx.Count == 0) return;
        if (idx.Count == 1) { var m = PosterModel(idx[0]); if (m != null) _games.SelectGame(m, false); }
        else { var games = GamesAt(idx); if (games.Count > 0) _games.SelectGames(games, false); }
    }


    // The games behind the poster's current selection, in display order.
    private List<IGame> PosterSelectedGames() => _poster == null ? new List<IGame>() : GamesAt(_poster.SelectedIndicesFast());

    private List<IGame> GamesAt(List<int> indices)
    {
        var list = new List<IGame>(indices.Count);
        try { foreach (int i in indices) { var m = PosterModel(i); if (m != null) list.Add(m); } }
        catch { }
        return list;
    }

    private void OnPosterMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = _poster.GetItemAt(e.X, e.Y);
        if (item == null) return;
        // Match the list's right-click: if the clicked tile isn't part of the selection, select just it;
        // otherwise keep the whole multi-selection and act on all of it (Play / Edit / plugin menus).
        if (!item.Selected) { _poster.ClearSelection(); _poster.SelectedIndices.Add(item.Index); }
        var games = PosterSelectedGames();
        if (games.Count == 0) return;
        var menu = BuildGameContextMenu(games.ToArray());
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
    private const int ILC_COLOR32 = 0x20, ILC_COLOR24 = 0x18, LVM_SETIMAGELIST = 0x1000 + 3, LVSIL_NORMAL = 0;

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
                tg.Clear(Center);   // match the centre-column zone (#2A2B34), not the side panels
                var imgArea = new Rectangle(0, 0, PCellW, PImgH);
                // The art is bottom-anchored inside imgArea and is usually NARROWER and SHORTER than it
                // (a tall box in a 124×174 cell leaves empty band(s)). artRect is where the pixels
                // actually are — the badge placements that talk about "the image" need that, not the area.
                Rectangle artRect;
                var img = PosterThumbSync(model, id);         // sync decode if the thumb is on disk; else null + async
                if (img != null)
                {
                    int ix = imgArea.X + (imgArea.Width - img.Width) / 2, iy = imgArea.Bottom - img.Height;
                    tg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    tg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    tg.DrawImage(img, ix, iy, img.Width, img.Height);
                    artRect = new Rectangle(ix, iy, img.Width, img.Height);
                }
                else
                {
                    // No art → a barely-there placeholder that blends into the zone (a hair lighter than Center).
                    tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    int pw = (int)(PCellW * 0.78f), ph = (int)(PArtH * 0.92f);
                    var ph_r = new Rectangle((PCellW - pw) / 2, imgArea.Bottom - ph, pw, ph);
                    using var pb = new SolidBrush(PosterPlaceholder);
                    using var pp = RoundRect(ph_r, 10);
                    tg.FillPath(pb, pp);
                    artRect = ph_r;   // the placeholder stands in for the art
                }
                var title = S(Safe(() => model.Title));
                var dev = S(Safe(() => model.Developer));
                var tRect = new Rectangle(0, PImgH + PZ(3), PCellW, PZ(17));
                var dRect = new Rectangle(0, PImgH + PZ(19), PCellW, PZ(15));
                var tileFont = _posterTileFont ??= new Font(Font.FontFamily, Font.Size * (float)_zoom, Font.Style);
                TextRenderer.DrawText(tg, title, tileFont, tRect, Fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (!string.IsNullOrEmpty(dev))
                    TextRenderer.DrawText(tg, dev, tileFont, dRect, SubFg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                DrawTileBadges(tg, model, artRect);
            }
            hbm = tile.GetHbitmap();
        }
        catch { }
        return hbm;
    }

    // ── Poster tile badges ───────────────────────────────────────────────────
    // Baked into the tile bitmap, which is composited once and cached — so badges cost nothing while
    // scrolling. Four placements and three alignments (Options ▸ Display).
    //
    // Three of the placements are relative to THE ART, not to the tile: box art is bottom-anchored and
    // usually narrower and shorter than the cell, so "at the top of the image" has to mean the top of
    // the pixels — putting it at the top of the CELL would hang the badges in empty space above a
    // short poster. BuildTileHbm hands over that rectangle (artRect); the placeholder stands in for it
    // when a game has no art at all.
    private const string PlaceUnderDev = "under the developer";
    private const string PlaceArtBottom = "over the art, at its bottom";
    private const string PlaceArtTop = "over the art, at its top";
    private const string PlaceAboveArt = "just above the art";
    private static readonly string[] PosterPlacements = { PlaceUnderDev, PlaceArtBottom, PlaceArtTop, PlaceAboveArt };
    private static readonly string[] PosterAligns = { "centred", "left", "right" };

    private string PosterBadgePlacement
    {
        get
        {
            var v = _cfg.Get("BadgesPosterPlacement");
            if (!string.IsNullOrEmpty(v) && Array.IndexOf(PosterPlacements, v) >= 0) return v;
            // Migrates the first shipped setting (a plain overlay on/off); anything else gets the
            // default placement.
            return _cfg.GetBool("BadgesPosterOverlay", false) ? PlaceArtBottom : PlaceAboveArt;
        }
    }
    private string PosterBadgeAlign
    {
        get { var v = _cfg.Get("BadgesPosterAlign"); return Array.IndexOf(PosterAligns, v) >= 0 ? v : "right"; }
    }

    private int ListBadgeOpacityPct => Math.Clamp(_cfg.GetInt("BadgesListOpacityPct", 80), 5, 100);
    private int PosterBadgeOpacityPct => Math.Clamp(_cfg.GetInt("BadgesPosterOpacityPct", 80), 5, 100);
    private int ListBadgeScalePct => Math.Clamp(_cfg.GetInt("BadgesListScalePct", 100), 25, 200);
    private int PosterBadgeScalePct => Math.Clamp(_cfg.GetInt("BadgesPosterScalePct", 100), 25, 200);

    private void DrawTileBadges(Graphics g, IGame model, Rectangle artRect)
    {
        if (!Badges.BadgeSettings.ShowBadges) return;
        var hits = Badges.BadgeEngine.VisibleCached(model);
        if (hits.Count == 0) return;

        string placement = PosterBadgePlacement;
        bool onArt = placement == PlaceArtBottom || placement == PlaceArtTop;
        int cell = PBadgeCell;
        // Row capacity: the art's own width for the two placements drawn ON the art (an extra row
        // just stacks over it), the whole tile for the two drawn OUTSIDE it — those two have a band
        // reserved by RecomputePosterBadgeRows, which counts with the tile width, and the two must
        // agree or a row would spill onto the art / into the title.
        int capacityW = onArt ? artRect.Width : PCellW;
        int cols = Math.Max(1, Math.Max(capacityW, cell) / cell);
        int rows = Math.Max(1, (hits.Count + cols - 1) / cols);
        int bandH = rows * cell;
        // Horizontal alignment is relative to the ART for every art-relative placement (including the
        // one drawn above it — badges belong to the picture, not to the cell).
        var reference = placement == PlaceUnderDev ? new Rectangle(0, 0, PCellW, 0) : artRect;

        int top;
        bool veiled = false;
        switch (placement)
        {
            case PlaceArtBottom: top = Math.Max(artRect.Y, artRect.Bottom - bandH); veiled = true; break;
            case PlaceArtTop: top = artRect.Y; veiled = true; break;
            case PlaceAboveArt: top = Math.Max(0, artRect.Y - bandH); break;   // the reserved band
            default: top = PImgH + PZ(19) + PZ(15) + PZ(1); break;             // under the developer line
        }

        if (veiled)
        {
            using var veil = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillRectangle(veil, new Rectangle(reference.X, top, reference.Width,
                                                Math.Min(bandH, Math.Max(0, artRect.Bottom - top))));
        }

        string align = PosterBadgeAlign;
        int used = Math.Min(hits.Count, rows * cols);
        for (int i = 0; i < used; i++)
        {
            var img = Badges.BadgeImages.Get(hits[i].Image, cell, hits[i].Tint, PosterBadgeOpacityPct);
            if (img == null) continue;
            int row = i / cols, col = i % cols;
            int rowCount = Math.Min(cols, hits.Count - row * cols);       // each row aligns its own run
            int rowW = rowCount * cell;
            int originX = align switch
            {
                "left" => reference.X,
                "right" => reference.Right - rowW,
                _ => reference.X + (reference.Width - rowW) / 2,
            };
            originX = Math.Max(0, Math.Min(originX, PCellW - rowW));   // a row wider than the art stays in the tile
            int x = originX + col * cell + (cell - img.Width) / 2;
            int y = top + row * cell + (cell - img.Height) / 2;
            g.DrawImage(img, x, y, img.Width, img.Height);
        }
    }

    /// <summary>How many badge rows the tiles must reserve for the current view — for the two
    /// placements drawn OUTSIDE the art (under the developer, just above the art); the two drawn on
    /// top of it need no room. Returns true when the value changed: every tile's height just moved,
    /// so the geometry has to be rebuilt.</summary>
    private bool RecomputePosterBadgeRows()
    {
        int rows = 0;
        var place = PosterBadgePlacement;
        if (Badges.BadgeSettings.ShowBadges && (place == PlaceUnderDev || place == PlaceAboveArt))
        {
            int cols = Math.Max(1, PCellW / PBadgeCell);
            int max = 0;
            try
            {
                foreach (var g in _games.VisibleGames)
                {
                    int n = Badges.BadgeEngine.VisibleCached(g).Count;
                    if (n > max) max = n;
                }
            }
            catch { }
            rows = (max + cols - 1) / cols;
        }
        if (rows == _posterBadgeRows) return false;
        _posterBadgeRows = rows;
        return true;
    }

    // Badges changed (menu toggle, background pass, placement option): the tiles have them baked in,
    // so every cached tile must go — and the geometry too when the reserved band changed.
    private void DropPosterTiles()
    {
        RecomputePosterBadgeRows();
        try { RebuildPosterGeometry(); } catch { }
        if (_posterMode) { try { RefreshPoster(); LayoutPoster(); } catch { } }
        else { try { _poster?.Invalidate(); } catch { } }
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
        _panelBrush ??= new SolidBrush(Center);           // gaps around the tile — match the centre zone
        g.FillRectangle(_panelBrush, b);
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
        while (_posterTileOrder.Count > PosterTileCap)
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

    // Drop EVERY cached poster layer for these games so the next paint re-resolves and re-decodes their
    // art. The editor adds and removes FILES (image downloads, deletions) that no store notification can
    // see — the same blindness the badge RecomputeNow above OpenEditGame covers — and the poster pipeline
    // caches at three levels on top of the resolution: the decoded thumb (_posterBmp, where null is a
    // "no art, don't retry" SENTINEL — a game that had no image would keep its phantom forever), the
    // composited tile (owner-draw HBITMAP), and the native image-list slot. ThumbCache needs nothing:
    // its key includes the source file's size, so a new or replaced file re-keys by construction.
    private void InvalidatePosterArt(IReadOnlyList<IGame> games)
    {
        if (games == null || games.Count == 0) return;
        foreach (var g in games)
        {
            if (!Guid.TryParse(S(Safe(() => g.Id)), out var id)) continue;
            lock (_posterQLock) _posterPending.Remove(id);   // a queued stale load must not block the fresh one
            if (_posterBmp.TryGetValue(id, out var bmp)) { bmp?.Dispose(); _posterBmp.Remove(id); }
            if (_posterOwnerDraw) InvalidatePosterTile(id);   // next DrawPosterItem recomposites
            else RefreshSlot(g, id);                          // native: rebuild the live slot in place
        }
        if (_posterMode && _poster is { Visible: true }) _poster.Invalidate();
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
    // The tile's source image for the SELECTED image group ("Front" keeps its Box3D fallback chain).
    private string _posterGroup;
    private string PosterSrc(IGame model) =>
        string.Equals(_posterGroup, "Front", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_posterGroup)
            ? DetailSource(model, "Front", () => Safe(() => model.FrontImagePath) is { Length: > 0 } f ? f : Safe(() => model.Box3DImagePath))
            : CacheSourceFor(model, _posterGroup);
    private ThumbCache.ThumbFormat PosterFmt => ThumbCache.FormatFor(_posterGroup ?? "Front");

    private Image PosterThumbSync(IGame model, Guid id)
    {
        if (_posterBmp.TryGetValue(id, out var bmp)) return bmp;   // already decoded (may be a null sentinel)
        if (_useImageCache)
        {
            string src = PosterSrc(model);
            string cachedFile = string.IsNullOrEmpty(src) ? null
                : ThumbCache.GetCachedOnly(src, PosterFmt);   // instant: no Magick
            if (cachedFile != null)
            {
                Image img = null;
                try { using var raw = LoadImage(cachedFile); if (raw != null) img = ScaleContain(raw, PCellW, PArtH); } catch { }
                _posterBmp[id] = img;             // cache (same byte-budget eviction as the async path)
                _posterBmpOrder.Enqueue(id);
                while (_posterBmpOrder.Count > PosterThumbCap)
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
                string src = PosterSrc(model);
                if (!string.IsNullOrEmpty(src))
                {
                    // Poster grid: load the SMALL cached thumb ONLY — never the full-res original.
                    // LoadThumbOrFull serves the full original on a cache MISS, so a cold "All games"
                    // scroll would decode hundreds of multi-megapixel bitmaps onto the Large Object Heap
                    // → back-to-back Gen2 GCs that suspend the UI thread → the grid freezes until the key
                    // is released. GetOrCreate makes the 360px thumb via Magick (native downscale, no
                    // managed LOH) on first use — bounded by THIS pool — and the 2nd pass is a cache HIT.
                    string thumb = _useImageCache ? ThumbCache.GetOrCreate(src, PosterFmt) : null;
                    using var raw = LoadImage(thumb ?? src);   // small thumb — or the original only if the cache/Magick is unavailable
                    if (raw != null) img = ScaleContain(raw, PCellW, PArtH);   // pre-size to the art box once
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
        while (_posterBmpOrder.Count > PosterThumbCap)
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
        // Read the store options FRESH from disk (not the cached _cfg, which can lag behind an options
        // save) so a just-ticked "close the store client on exit" actually applies to this launch.
        var cfgNow = LiteBoxConfig.LoadForExe();
        // Exit detection: install-folder process by default. The window-focus
        // fallback is opt-in (StoreExitFocusFallback) — pass no focus callback
        // when it's off so the watcher relies purely on the game process.
        Func<bool> regained = cfgNow.StoreExitFocusFallback ? (() => _storeRegainedFocus) : (Func<bool>)null;
        try { HostLaunch.LaunchStore(g, regained, cfgNow.KillStoreLauncherAfterGame, cfgNow.KillStoreLauncherEvenIfPreRunning); } catch { }
    }

    private void OnActivatedStoreResync()
    {
        if (_storeLostFocus) _storeRegainedFocus = true;   // foreground came back after the game took it
        // Options-db hot cache: reload if the db file changed on disk (ExtendDB under a simultaneously
        // running real LB writes it directly). One stat when nothing changed.
        try { Data.LiteBoxOptionsDb.RevalidateHotCache(); } catch { }
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
                _launchButtons?.ShowFor(_heroGame, SafeEmulatorsForPlatform(S(_heroGame.Platform), _heroGame), SafeAddApps(_heroGame));
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
                _launchButtons?.ShowFor(_heroGame, SafeEmulatorsForPlatform(S(_heroGame.Platform), _heroGame), SafeAddApps(_heroGame));
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
            _meta.Clear(); _vndb.Clear(); _raCard?.HidePanel(); _notes.Text = ""; _related?.ClearAll(); _highScores?.ClearAll(); RelayoutDetail();
            _launchButtons?.HideGame();
            SetStorePoll(false);
            UpdateGameMusic(null);
            return;
        }

        // Source selection like launchbox-web/bigbox-web: ClearLogo regroupement for the
        // logo, Front for the box (GameCache → same file → shared cache; IO fallback).
        // SetGame (carrying the title fallback) runs BEFORE the async logo load so a game
        // with no clear logo shows its title as text — with the same pulse.
        var (logoSrc, artSrc) = DetailImageSources(g);
        SetHeroGame(g);
        Hide3dOverlay();   // instant is ALWAYS a flat image (3D immediate = the baked PNG via the loaders)
        HideVideoOverlay();   // …and a video must never keep playing over the next game
        LoadImagesAsync(logoSrc, artSrc);
        PopulateDetailMeta(g);
        UpdateGameMusic(g);   // direct path (thumb click / restore) — same music rules as the settle

        // Automatic Progress Tracking, "on select" flavor (option): re-evaluate this game while its
        // detail data is being composed — off the UI thread, and cheap (RAM + at most one cached-RA
        // read). Gated on the option AND (inside the engine) on the LB master switch.
        if (_cfg.ProgressApplyOnSelect)
        {
            var pg = g;
            Task.Run(() => { try { Data.ProgressAutomation.ApplyToGame(pg); } catch { } });
        }
    }

    // The box + clear-logo source files for a game (same resolution launchbox-web/bigbox-web use).
    // allow3d=false (related cards, callers that need a real FILE) resolves the 3D immediate straight
    // to its fallback family instead of the sentinel.
    private (string logoSrc, string artSrc) DetailImageSources(IGame g, bool allow3d = true)
    {
        string logoSrc = DetailSource(g, "ClearLogo", () => Safe(() => g.ClearLogoImagePath));
        // Immediate main image family is configurable PER VIEW (Options → Display → Right panel).
        string fam = _posterMode ? Media.MediaLayout.Current.ImmediatePoster : Media.MediaLayout.Current.ImmediateList;
        if (string.IsNullOrEmpty(fam)) fam = "Front";
        // "3D Model" immediate: the baked snapshot PNG ONLY (no bake, no viewport at instant time).
        // Not bakeable / not baked yet → the configured fallback family takes over.
        if (string.Equals(fam, Media.Media3dItem.FamilyKey, StringComparison.OrdinalIgnoreCase))
        {
            if (allow3d)
                try
                {
                    // O(1) set lookup only — NEVER Model3dCache.Resolve here: this path runs for every
                    // game a fast scroll transits, and Resolve's art-slot IO froze the transit loader.
                    // Presence is all this needs, and the file is named after the game, so there is no
                    // second branch and nothing to be ready for. Whether that model is still CURRENT is
                    // settled later, off the transit path, by whoever actually loads it.
                    string gid = S(Safe(() => g.Id));
                    if (Model3d.Model3dKeyIndex.HasModel(gid))
                        return (logoSrc, Media.Media3dItem.For(Path.Combine(Model3d.Model3dCache.Dir, gid + ".glb")));
                }
                catch (Exception ex) { Console.WriteLine("[media3d] instant lookup failed: " + ex.Message); }
            fam = Media.MediaLayout.Current.Immediate3dFallback;
            if (string.IsNullOrEmpty(fam) || string.Equals(fam, Media.Media3dItem.FamilyKey, StringComparison.OrdinalIgnoreCase)) fam = "Front";
        }
        string artSrc = CacheSourceFor(g, fam);
        if (string.IsNullOrEmpty(artSrc))   // family had nothing → the old Front→Box3D→Screenshot fallback
            artSrc = Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f
                   : Safe(() => g.Box3DImagePath) is { Length: > 0 } b ? b
                   : Safe(() => g.ScreenshotImagePath);
        return (logoSrc, artSrc);
    }

    // Hero card title/rating/favorite (cheap — property reads only).
    private void SetHeroGame(IGame g)
    {
        double eff = Safe(() => g.CommunityOrLocalStarRating);
        _hero.SetGame(S(g.Title), eff, Safe(() => g.StarRatingFloat) > 0, Safe(() => g.Favorite));
        _heroBadgeGame = g;
        RefreshHeroBadges();
    }

    // ── Hero badges ──────────────────────────────────────────────────────────
    // The status strip in the hero's top-right corner. The badge SET comes from the shared engine
    // cache (computed once for the whole library), so this only turns names into pre-scaled bitmaps.
    // Rebuilt on selection, on a Badges-menu toggle, and when the background pass publishes.
    private IGame _heroBadgeGame;

    // Hero badge display options (Options ▸ Display ▸ right panel). The hero is the detail view, not
    // the browsing grid, so by default it keeps showing badges even with LaunchBox's "Show Badges"
    // off — that setting is about the list. Unticking the first option makes it follow along.
    private bool HeroBadgesAlways => _cfg.GetBool("BadgesHeroAlways", true);
    private int HeroBadgeScalePct => Math.Clamp(_cfg.GetInt("BadgesHeroScalePct", 100), 25, 200);
    private int HeroBadgeOpacityPct => Math.Clamp(_cfg.GetInt("BadgesHeroOpacityPct", 80), 5, 100);
    private static readonly string[] BadgeScales =
        { "25%", "50%", "75%", "100%", "125%", "150%", "175%", "200%" };

    private void RefreshHeroBadges()
    {
        var g = _heroBadgeGame;
        if (g == null || (!Badges.BadgeSettings.ShowBadges && !HeroBadgesAlways))
        { _hero.SetBadges(Array.Empty<(Image, string)>()); _hero.SetProgress(null, null); return; }

        var list = new List<(Image, string)>();
        int px = Math.Max(6, (int)Math.Round(20 * LiteBoxTheme.DpiScale(this) * HeroBadgeScalePct / 100.0));
        int opacity = HeroBadgeOpacityPct;
        foreach (var hit in Badges.BadgeEngine.VisibleHero(g))
        {
            // Progress leaves the strip — it becomes the button at the bottom-right (SetHeroProgress).
            if (string.Equals(hit.Id, "Progress", StringComparison.OrdinalIgnoreCase)) continue;
            var img = Badges.BadgeImages.Get(hit.Image, px, hit.Tint, opacity);
            if (img != null) list.Add((img, hit.Tip));
        }
        _hero.SetBadges(list);
        SetHeroProgress(g, px, opacity);
    }

    // The progress button's face: the game's own state badge, or the pack's generic Progress marker
    // (dimmed) when nothing is set — the button has to be there for a game WITHOUT progress, since
    // that is exactly when you want to set one. Hidden entirely when the Progress badge is disabled
    // in View ▸ Badges, so the menu still governs it.
    private void SetHeroProgress(IGame g, int px, int opacity)
    {
        if (!Badges.BadgeSettings.IsEnabledHero("Progress")) { _hero.SetProgress(null, null); return; }
        string value = S(Safe(() => g.Progress));
        Image img = null;
        if (value.Length > 0)
        {
            var hit = Badges.BadgeEngine.VisibleHero(g)
                .FirstOrDefault(h => string.Equals(h.Id, "Progress", StringComparison.OrdinalIgnoreCase));
            if (hit.Image != null) img = Badges.BadgeImages.Get(hit.Image, px, hit.Tint, opacity);
        }
        img ??= Badges.BadgeImages.Get("Progress", px, Badges.BadgeTint.None, Math.Min(opacity, 45));
        _hero.SetProgress(img, value.Length > 0 ? value : "Progress: not set — click to choose");
    }

    // Click on that button: LaunchBox's own progress organization, as a menu. It opens to the LEFT of
    // the button (AboveLeft) because the button sits at the right edge of the detail pane, which is
    // itself at the right edge of the window — a menu growing rightwards would run off the screen.
    private void ShowProgressMenu(Rectangle buttonRect)
    {
        var g = _heroBadgeGame;
        if (g == null) return;
        if ((_dm as HostDataManagerXml)?.ReadOnly ?? true)
        {
            MessageBox.Show(this, "The library is open read-only.", "Progress",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string current = S(Safe(() => g.Progress));
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Panel2, ForeColor = Fg };

        var none = new ToolStripMenuItem("(none)") { Checked = current.Length == 0 };
        none.Click += (_, _) => Safe(() => ApplyProgress(g, ""));
        menu.Items.Add(none);
        menu.Items.Add(new ToolStripSeparator());

        string lastCategory = null;
        foreach (var value in Safe(() => MetadataChoicesCache.Get("Progress", _dm)) ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            // The organization is "Category / Value" — a separator between categories keeps a long
            // list readable, exactly as the Options page groups them.
            var (category, _) = Data.ProgressModel.Split(value);
            if (lastCategory != null && !string.Equals(category, lastCategory, StringComparison.OrdinalIgnoreCase))
                menu.Items.Add(new ToolStripSeparator());
            lastCategory = category;

            var it = new ToolStripMenuItem(value)
            { Checked = string.Equals(value, current, StringComparison.OrdinalIgnoreCase) };
            var captured = value;
            it.Click += (_, _) => Safe(() => ApplyProgress(g, captured));
            menu.Items.Add(it);
        }

        // Dispose AFTER the close finishes, never inside the event: WinForms keeps touching the
        // drop-down while it closes (SetVisibleCore re-creates its handle), so disposing here throws
        // ObjectDisposedException right after the item's own click handler has run.
        menu.Closed += (_, _) =>
        {
            try { BeginInvoke((Action)(() => { try { menu.Dispose(); } catch { } })); } catch { }
        };
        // Anchored to the hero, opening UP-LEFT: the button sits at the right edge of the detail
        // pane, itself at the right edge of the window, so a menu growing rightwards would run off
        // the screen. (Control-relative overload — the screen-point one shows nothing here.)
        menu.Show(_hero, new Point(buttonRect.Right, buttonRect.Top), ToolStripDropDownDirection.AboveLeft);
    }

    private void ApplyProgress(IGame g, string value)
    {
        Safe(() => g.Progress = value ?? "");           // → journal
        Badges.BadgeEngine.Invalidate(g);               // its Progress badge just changed
        MetadataChoicesCache.MarkFieldDirty("Progress");// a new custom value would join the choices
        RefreshHeroBadges();
        Safe(() => _games.RefreshGame(g));
        _games?.RefreshBadges();
        if (Guid.TryParse(S(Safe(() => g.Id)), out var id)) InvalidatePosterTile(id);
        if (_posterMode) { try { _poster?.Invalidate(); } catch { } }
    }

    // ── Badge display options, built once and shown twice ────────────────────
    // Options ▸ Display keeps them in the tab of the surface they affect; the Manage Badges window
    // gathers all three under its Display tab. Same OptionItems, same storage, same applyLive — so
    // whichever window you use, the other one shows the change next time it opens.

    /// <summary>A badge size or opacity just changed. Both are BAKED into the cached bitmaps, so every
    /// entry for the value we are leaving is dead — and nothing else would ever reclaim them (the cache
    /// is keyed (name, height, tint, opacity) and only ever grew). Dropping them costs a re-decode of
    /// the pack's ~44 images, a few milliseconds.
    ///
    /// All three surfaces are refreshed, not just the one whose option moved: the hero HOLDS Image
    /// references handed out by the cache, so they must be re-fetched before anything paints again.</summary>
    private void RescaleBadgeIcons(Action after = null)
    {
        Badges.BadgeImages.DropScaled();
        RefreshHeroBadges();
        if (_games != null) { _games.BadgeCellScale = ListBadgeScalePct; _games.RefreshBadges(); }
        try { after?.Invoke(); } catch { }
    }

    internal Options.OptionItem[] BadgeHeroOptions() => new[]
    {
            Options.OptionItem.Toggle("Display", "Badges: show them here even when \"Show Badges\" is off",
                () => _cfg.GetBool("BadgesHeroAlways", true), v => _cfg.SetBool("BadgesHeroAlways", v),
                "On (default): the detail pane always shows the selected game's badges — LaunchBox's "
                + "\"Show Badges\" (View ▸ Badges) governs the game LIST and the poster tiles, not this "
                + "corner. Off: this strip follows that setting too. The per-badge toggles apply either way.",
                applyLive: RefreshHeroBadges),
            Options.OptionItem.Choice("Display", "Badges: size", BadgeScales,
                () => HeroBadgeScalePct + "%",
                v => _cfg.SetInt("BadgesHeroScalePct", int.TryParse(v.TrimEnd('%'), out var p) ? p : 100),
                "Scales the badge strip in the detail pane. 100% ≈ 20 px at 100% DPI.",
                applyLive: () => RescaleBadgeIcons()),
            Options.OptionItem.Number("Display", "Badges: opacity (%)",
                () => HeroBadgeOpacityPct, v => _cfg.SetInt("BadgesHeroOpacityPct", v),
                min: 5, max: 100, step: 5,
                help: "How opaque the badges are over the fanart. 100 = solid; 80 by default.",
                applyLive: () => RescaleBadgeIcons()),
    };

    internal Options.OptionItem[] BadgeListOptions() => new[]
    {
            Options.OptionItem.Choice("Display", "Badges: size", BadgeScales,
                () => ListBadgeScalePct + "%",
                v => _cfg.SetInt("BadgesListScalePct", int.TryParse(v.TrimEnd('%'), out var p) ? p : 100),
                "Scales the badge strip drawn before each title. A badge can never be taller than the "
                + "row it rides in, so past a point this only stops growing — raise \"Two-line rows\" or "
                + "the zoom for bigger badges.",
                applyLive: () => RescaleBadgeIcons()),
            Options.OptionItem.Number("Display", "Badges: opacity (%)",
                () => ListBadgeOpacityPct, v => _cfg.SetInt("BadgesListOpacityPct", v),
                min: 5, max: 100, step: 5,
                help: "How opaque the badge strip drawn before each title is. 100 = solid; 80 by default. "
                + "Only when \"Show Badges\" is on.",
                applyLive: () => RescaleBadgeIcons()),
            Options.OptionItem.Choice("Display", "Badges: when to work out which apply",
                new[] { "after loading (background)", "during loading" },
                () => _cfg.GetBool("BadgesComputeAtLoad", false) ? "during loading" : "after loading (background)",
                v => _cfg.SetBool("BadgesComputeAtLoad", v == "during loading"),
                "Badges are worked out ONCE for the whole library and kept — the list, the tiles and the "
                + "detail pane then just draw them. Default: the pass runs in the background once the window "
                + "is up, and badges appear platform by platform. \"During loading\" does it before the window "
                + "shows, so the first list is already complete, at the cost of a longer start. Takes effect "
                + "at the next start."),
    };

    internal Options.OptionItem[] BadgePosterOptions() => new[]
    {
            Options.OptionItem.Choice("Display", "Poster grid: where badges go",
                PosterPlacements,
                () => PosterBadgePlacement,
                v => _cfg.Set("BadgesPosterPlacement", v),
                "Only when \"Show Badges\" is on. Default: just above the art. Under the developer: nothing covers the box art, "
                + "but the tiles get taller — enough for the most-badged game in view. The other three follow "
                + "THE ART, which is bottom-anchored and usually smaller than the cell: over its bottom edge "
                + "(above the title), over its top edge, or just above it in the empty space a short poster "
                + "leaves — that last one falls back onto the art's top edge when the art fills the cell. The "
                + "three art placements keep the tile's height.",
                applyLive: DropPosterTiles),
            Options.OptionItem.Choice("Display", "Poster grid: badge size", BadgeScales,
                () => PosterBadgeScalePct + "%",
                v => _cfg.SetInt("BadgesPosterScalePct", int.TryParse(v.TrimEnd('%'), out var p) ? p : 100),
                "Scales the badges baked into the tiles. With the two placements that reserve room "
                + "(under the developer, just above the art) the tiles grow with them.",
                applyLive: () => RescaleBadgeIcons(DropPosterTiles)),
            Options.OptionItem.Number("Display", "Poster grid: badge opacity (%)",
                () => PosterBadgeOpacityPct, v => _cfg.SetInt("BadgesPosterOpacityPct", v),
                min: 5, max: 100, step: 5,
                help: "How opaque the badges baked into the tiles are — worth lowering when they sit on "
                + "the art. 100 = solid; 80 by default.",
                applyLive: () => RescaleBadgeIcons(DropPosterTiles)),
            Options.OptionItem.Choice("Display", "Poster grid: badge alignment",
                PosterAligns,
                () => PosterBadgeAlign,
                v => _cfg.Set("BadgesPosterAlign", v),
                "Where the badge row sits horizontally — inside the art for the three art placements, across "
                + "the tile when they are under the developer. Right by default.",
                applyLive: DropPosterTiles),
    };

    // Everything the badge surfaces need, in one place: the list's strip source, the two events that
    // can change what is drawn, and the background pass that fills the engine's cache.
    private void WireBadges()
    {
        // Per displayed row the list reads one int — the combination id, which lives in the game's
        // store row. It never evaluates: before the pass lands, rows simply have no badges and repaint
        // when it publishes. The icons are asked for only when a combination has no strip yet.
        _games.BadgeCellScale = ListBadgeScalePct;
        _games.BadgeComboOf = g => Badges.BadgeSettings.ShowBadges ? Badges.BadgeEngine.Combo(g) : 0;
        _games.BadgeMaxSlots = () => Badges.BadgeSettings.ShowBadges ? Badges.BadgeEngine.MaxEnabledSlots : 0;
        _games.BadgeStripImages = (combo, cell) =>
        {
            var hits = Badges.BadgeEngine.Table.Materialize(combo, filtered: true);
            if (hits.Length == 0 || cell <= 0) return Array.Empty<Image>();
            int opacity = ListBadgeOpacityPct;
            var imgs = new List<Image>(hits.Length);
            foreach (var h in hits)
            {
                var img = Badges.BadgeImages.Get(h.Image, cell, h.Tint, opacity);
                if (img != null) imgs.Add(img);
            }
            return imgs;
        };

        void Repaint()
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    RefreshHeroBadges();
                    _games?.RefreshBadges();
                    DropPosterTiles();     // tiles bake their badges in — they must be re-composited
                }));
            }
            catch { }
        }
        Badges.BadgeSettings.Changed += Repaint;   // a Badges-menu toggle
        Badges.BadgeEngine.Changed += Repaint;     // the background pass publishing a platform

        // A handful of games changed under us (an edit, a store sync, a plugin write). Repainting
        // EVERYTHING for that would drop every composited tile and rebuild every strip — so those
        // games are repainted one by one, and only a genuinely new badge combination forces the
        // strips to be rebuilt.
        Badges.BadgeWatch.Recomputed += ids =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    foreach (var id in ids)
                    {
                        var g = GameById(id);
                        if (g != null) Safe(() => _games.RefreshGame(g));
                        // Tiles bake their badges in, and the two poster renderers keep them in
                        // different places: owner-draw caches an HBITMAP per game (drop it and the
                        // next paint rebuilds), the native one holds a slot in the image list that
                        // has to be REWRITTEN. Doing only the first left the native tiles showing the
                        // badges the game had before the edit.
                        if (_posterOwnerDraw) InvalidatePosterTile(id);
                        else if (g != null) Safe(() => RefreshSlot(g, id));
                    }
                    _games?.RefreshBadgesIfCombinationsChanged();
                    if (_posterMode) { try { _poster?.Invalidate(); } catch { } }
                    if (ids.Any(i => Guid.TryParse(S(Safe(() => _heroBadgeGame?.Id)), out var h) && h == i))
                        RefreshHeroBadges();
                }));
            }
            catch { }
        };
        Badges.BadgeWatch.Start(GameById);

        // "At load" runs the pass now (the first list is already badged, boot is a bit longer);
        // "after load" — the default — lets the window come up first.
        if (_cfg.GetBool("BadgesComputeAtLoad", false)) StartBadgePass();
        else Shown += (_, _) => StartBadgePass();
    }

    /// <summary>The live IGame for an id — the badge watcher hands out ids, the predicates need the
    /// object. Looks in the current view first (the common case), then the whole library.</summary>
    private IGame GameById(Guid id)
    {
        try
        {
            foreach (var g in _games.VisibleGames)
                if (Guid.TryParse(S(Safe(() => g.Id)), out var x) && x == id) return g;
            foreach (var g in _dm?.GetAllGames() ?? Array.Empty<IGame>())
                if (Guid.TryParse(S(Safe(() => g.Id)), out var x) && x == id) return g;
        }
        catch { }
        return null;
    }

    private void StartBadgePass()
        => Badges.BadgeEngine.StartPass(() =>
        {
            try { return (_dm?.GetAllGames() ?? Array.Empty<IGame>()).ToList(); }
            catch { return Array.Empty<IGame>(); }
        });

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
        _related?.ShowFor(g, _detailTabSel == 1);   // Related tab: recompute now if visible, else lazily on flip
        // HIGH SCORES tab: present only for MAME games. Rebuild the strip when its presence flips, clamping the
        // active tab if it vanished. ShowFor recomputes now only when HIGH SCORES is the visible tab, else lazily.
        // HIGH SCORES tab shows only when the download option is enabled (LB parity: leaderboards are fetched
        // only when the user opted in on the MAME Integrations tab) ET quand le jeu peut vraiment produire un
        // score — sa rom doit être déclarée par un hiscore.dat installé, sans quoi l'onglet promettrait un
        // classement pour un jeu dont l'émulateur n'écrira jamais de .hi.
        bool mame = Mame.MameLeaderboards.HasHiscoreSupport(g) && Mame.MameOptions.DownloadEnabled;
        if (mame != _hsTabShown)
        {
            _hsTabShown = mame;
            _detailTabs.SetTabs(mame ? new[] { "OVERVIEW", "RELATED GAMES", "HIGH SCORES" } : new[] { "OVERVIEW", "RELATED GAMES" });
            if (_detailTabSel >= (mame ? 3 : 2)) { _detailTabSel = 0; _detailTabs.Selected = 0; }
        }
        _highScores?.ShowFor(g, _detailTabSel == 2);
        // New game → scroll the detail pane to the top BEFORE relaying out. RelayoutDetailCore positions
        // the grid at an absolute (0,0), so it must start from an unscrolled panel; otherwise a tall
        // previous game (e.g. a big achievements grid) leaves a scroll offset and the grid is mispositioned.
        if (_detailHost != null) { try { _detailHost.AutoScrollPosition = new Point(0, 0); } catch { } }
        RelayoutDetail();

        _notes.Text = S(g.Notes).Replace("\n", "\r\n");

        // Launch buttons (Play / Version / ROM) — reuses the same SDK enumeration
        // as the right-click menu; the ROM tier lights up only when ExtendDB is loaded.
        _launchButtons?.ShowFor(g, SafeEmulatorsForPlatform(S(g.Platform), g), SafeAddApps(g));
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
        // LongRunning: this loop lives as long as selections keep arriving — on a pool thread it both
        // hogged a pool slot AND could be starved by other blocked pool work (the 3D bake waits).
        if (start) System.Threading.Tasks.Task.Factory.StartNew(DetailLoop,
            System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
            System.Threading.Tasks.TaskScheduler.Default);
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
                        bool art3d = Media.Media3dItem.Is(artSrc);   // 3D snapshot → fit-to-height, not letterbox
                        if (settled) ApplyDetails(g, logo, art, art3d);   // landed → full pane
                        else ApplyImageTransit(g, logo, art, art3d);      // scrolled past → base thumb + title only
                    }));
                else { logo?.Dispose(); art?.Dispose(); }
            }
            catch { logo?.Dispose(); art?.Dispose(); }

            if (settled) return;   // a later selection restarts the loop via RequestDetail
        }
    }

    // Settle: the selection landed here. Images are already decoded (on the loader thread) → applied
    // directly (no re-load, no SetImage(null) flash) and the full pane is built.
    private void ApplyDetails(IGame g, Image logo, Image art, bool art3d = false)
    {
        _detailsShown = g;
        _heroGame = g;
        ++_detailsLoadToken;        // invalidate any async load/fanart still in flight from a prior detail
        SetHeroGame(g);             // title (text fallback) before the logo
        _hero.SetLogo(logo);
        Hide3dOverlay();            // a flat image takes the box — the previous game's 3D must not linger
        HideVideoOverlay();         // idem for a playing video (it would also keep decoding)
        _media.SetImage(art, art3d);
        PopulateDetailMeta(g);
        UpdateGameMusic(g);         // the selection settled here → its music (View ▸ Media rules)
    }

    // Transit: a game merely scrolled past. Update only the base thumb + title/logo (cheap) so images
    // track the scroll; the heavy pane (metadata, buttons, fanart, strip) waits for the settle above.
    private void ApplyImageTransit(IGame g, Image logo, Image art, bool art3d = false)
    {
        _detailsShown = g;
        _heroGame = g;
        ++_detailsLoadToken;        // cancel a previous settle's fanart/strip still loading, mid-scroll
        SetHeroGame(g);
        _hero.SetLogo(logo);
        Hide3dOverlay();            // scrolling past: the previous game's 3D must not cover the new thumb
        HideVideoOverlay();
        _media.SetImage(art, art3d);
    }

    // Right pane when a TREE node (category / platform / playlist / All) is selected.
    private void ShowNodeDetails(object node)
    {
        _detailsShown = node;
        _heroGame = null;
        Hide3dOverlay();              // 3D media overlay is game-only
        HideVideoOverlay();           // video too
        UpdateGameMusic(null);        // music too — a tree node has none
        _launchButtons?.HideGame();   // launch group is game-only
        _related?.ClearAll();         // tab strip + related list are game-only
        _highScores?.ClearAll();      // MAME leaderboards are game-only too
        if (_hsTabShown) { _hsTabShown = false; _detailTabs.SetTabs("OVERVIEW", "RELATED GAMES"); if (_detailTabSel >= 2) { _detailTabSel = 0; _detailTabs.Selected = 0; } }
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
        var t = new System.Windows.Forms.Timer { Interval = DetailLoadInterval };
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
        // Parental: a locked user can't set ratings unless "allow star ratings while locked" is on.
        if (ParentalBridge.DesktopMutationBlocked("rating")) return;
        Safe(() => g.StarRatingFloat = value);   // → journal (deferred, gated); no immediate XML write
        _hero.SetRating(value, isUser: true);
        Safe(() => _games.RefreshGame(g));
    }

    private void ToggleHeroFavorite()
    {
        var g = _heroGame; if (g == null) return;
        // Parental: a locked user can't toggle favorite unless "allow favorites while locked" is on.
        if (ParentalBridge.DesktopMutationBlocked("favorite")) return;
        bool nv = !Safe(() => g.Favorite);
        Safe(() => g.Favorite = nv);             // → journal
        _hero.SetFavorite(nv);
        RefreshHeroBadges();                     // the Favorite badge follows the heart
        Safe(() => _games.RefreshGame(g));
    }

    // Settle delay for BOTH deferred-detail timers (media strip + fanart). One user-set value
    // (DetailLoadDelayMs, Display options); a WinForms Timer needs Interval ≥ 1, so 0 ms clamps to 1
    // (fires on the next message cycle — effectively "immediately" but still off the selection call).
    private int DetailLoadInterval => Math.Max(1, _cfg.DetailLoadDelayMs);

    // ── Main media (16:9) + mini-thumbnail strip ─────────────────────────────
    // Like launchbox-web's media carousel, but the main starts on the BOX (Front),
    // not a screenshot — in the default list view we don't already see the box. The
    // strip + the degraded→full upgrade of the main happen after DetailLoadDelayMs (default 0.5s).
    private void ScheduleMedia(IGame g)
    {
        if (_mediaTimer != null) { _mediaTimer.Stop(); _mediaTimer.Dispose(); _mediaTimer = null; }
        if (_stripRowH != 72) { _stripRowH = 72; RelayoutDetail(); }   // back from a node's taller recent strip
        ClearStrip();   // reserve the strip space, empty, until the deferred load
        int token = _detailsLoadToken;
        var t = new System.Windows.Forms.Timer { Interval = DetailLoadInterval };
        _mediaTimer = t;
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            if (ReferenceEquals(_mediaTimer, t)) _mediaTimer = null;
            if (IsDisposed || token != _detailsLoadToken) return;
            // RA detail panel at the debounced detail-load (not on every selection). LoadRaPanel first runs
            // the plugin's on-select hash/raid heal BLOCKING (so a never-hashed game gets its raid written
            // BEFORE we display from it — fixes the "leave and come back" symptom), then fetches + shows the
            // achievements. No-op without the plugin / OnSelect mode. Backgrounded inside.
            try { LoadRaPanel(g, token); } catch { }
            try { LoadStoreAchPanel(g, token); } catch { }
            // Build the media list OFF the UI thread: with the prevent-duplicates filter on, a game's first
            // visit decodes/embeds images (CNN can take a second+); cached visits stay near-instant. The
            // token guard drops the result if the selection moved on meanwhile.
            bool posterNow = _posterMode;
            System.Threading.Tasks.Task.Run(() =>
            {
                List<string> items;
                try { items = BuildMediaList(g, posterNow); } catch { items = new List<string>(); }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || token != _detailsLoadToken) return;
                        _mediaItems = items; _mediaItemsGame = g; _mediaSel = items.Count > 0 ? 0 : -1;
                        if (items.Count > 0) SetMainMedia(items[0], full: true, token);   // upgrade box: degraded → full
                        // The main media is settled: music now knows whether a sounded video took the audio.
                        UpdateGameMusic(g, mainIsVideo: items.Count > 0 && Media.MediaVideoItem.Is(items[0]));
                        PopulateStrip(items, token);
                        try { Kick3dBake(g, items, token); } catch { }   // GLB missing → bake at settle, then refresh the 3D tile
                        try { KickVideoThumbs(items, token); } catch { } // extract missing video frames, one by one, cancellable

                    }));
                }
                catch { }   // window closed mid-build
            });
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
            //    hashed game gets its raid written BEFORE we read it. No-op without the plugin /
            //    OnSelect mode. (Slow first time — hashes the ROM — hence off the UI thread.)
            try
            {
                string raPlat = null; try { raPlat = g.Platform; } catch { }
                if (RaPlatformState.ShouldAutoResolveOnSelect(raPlat))
                {
                    // Live progress in the RA card while an unparsed archive is hashed on select (fills the
                    // ROM picker's per-entry column). Only REVEAL the bar once it's genuinely slow — more than
                    // 1s elapsed AND still under 30% done — so fast (cartridge) parses never flash it. Once
                    // shown it keeps updating. Reporter cleared in finally; UI update is token-guarded.
                    long hashStart = Environment.TickCount64;
                    bool barShown = false;
                    Ra.RaHasherLite.ArcProgress = (done, total) =>
                    {
                        try
                        {
                            if (!barShown)
                            {
                                if (Environment.TickCount64 - hashStart < 1000) return;         // <1s → don't flash
                                if (total > 0 && (long)done * 100 >= (long)total * 30) return;   // already ≥30% → will finish fast
                                barShown = true;
                            }
                            if (!IsDisposed && IsHandleCreated)
                                BeginInvoke(new Action(() => { if (token == _detailsLoadToken) _raCard?.ShowHashing(done, total); }));
                        }
                        catch { }
                    };
                    try { RaResolveLite.Resolve(g, fillPickerWhenResolved: true); }   // per-ROM RAHasher resolution + parse an unparsed archive for the picker column
                    finally { Ra.RaHasherLite.ArcProgress = null; }
                }
                // else: this platform is RA-disabled OR the auto-update trigger is "On launch" → don't re-hash on select; the panel still shows whatever raid is already stored.
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

    // ── 3D media overlay (the 3D sentinel item shown in the main box) ─────────────────────────────
    // Show = the proven Model3dBlock flow (GLB thumb PNG immediately, live viewport swapped in behind —
    // bakes on the STA worker when the GLB is missing). Hide = leaving to a normal image or a node.
    private void Show3dOverlay(IGame g)
    {
        if (_media3d == null) return;
        Console.WriteLine("[media3d] overlay showfor: " + S(Safe(() => g.Title)));
        _media3d.BringToFront();
        _media3d.ShowFor(g);   // the overlay turns visible via ContentChanged, WITH its PNG already set
    }

    private void Hide3dOverlay()
    {
        if (_media3d == null || (!_media3d.Visible && !_media3d.HasContent)) return;
        _media3d.Clear();
        _media3d.Visible = false;
    }

    // ── deferred video still frames ──────────────────────────────────────────────
    // Extracting a frame costs hundreds of ms per video (VideoThumbnailer decodes 20 % in), so it must
    // never sit on the display path: the strip shows BLACK tiles and this worker fills them in afterwards,
    // one video at a time, on its own thread.
    //
    // Cancellation is the point (user's requirement): leaving the game must not keep the machine busy —
    // but nothing is aborted mid-decode either. The token is checked BETWEEN videos, so the current
    // extraction finishes (its result is still worth caching) and the queue simply stops there.
    private void KickVideoThumbs(List<string> items, int token)
    {
        var pending = items.Where(Media.MediaVideoItem.Is)
                           .Where(i => !Video.VideoThumbnailer.IsCached(Media.MediaVideoItem.PathOf(i)))
                           .ToList();
        if (pending.Count == 0) return;
        int mine = ++_videoThumbToken;
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            foreach (var item in pending)
            {
                if (mine != _videoThumbToken || token != _detailsLoadToken || IsDisposed) return;   // moved on: stop here
                // A game is running: do NOT start another decode. The token above is bumped from the UI
                // thread (OnGameStarted), which may be marshalled and land AFTER the launch already released
                // libvlc — a decode slipping through that window would re-create the instance we just gave
                // back to the game, and burn CPU behind it. HostLaunch.GameRunning is set synchronously ON
                // the launching thread, before any of that, so it closes the window. The queue simply
                // resumes when the pane is rebuilt after the game (selection restored → ScheduleMedia).
                if (HostLaunch.GameRunning) return;
                string path = Media.MediaVideoItem.PathOf(item);
                try { using (var img = Video.VideoThumbnailer.Get(path)) { } } catch { }   // decode + disk-cache
                if (mine != _videoThumbToken || token != _detailsLoadToken || IsDisposed) return;
                // Landed: refresh this tile, and the main box when this video is the selected item.
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((Action)(() =>
                        {
                            if (mine != _videoThumbToken || token != _detailsLoadToken || IsDisposed) return;
                            RefreshItemTile(item, token);
                            if (_mediaItems != null && _mediaSel >= 0 && _mediaSel < _mediaItems.Count
                                && string.Equals(_mediaItems[_mediaSel], item, StringComparison.OrdinalIgnoreCase))
                                _mediaVideo?.SetStillFor(path, Media.MediaVideoItem.CachedThumb(item));
                        }));
                }
                catch { }
            }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>Re-load one strip tile's image (a deferred thumbnail landed).</summary>
    private void RefreshItemTile(string item, int token)
    {
        if (_mediaItems == null || token != _detailsLoadToken) return;
        int ix = _mediaItems.FindIndex(s => string.Equals(s, item, StringComparison.OrdinalIgnoreCase));
        if (ix < 0 || ix >= _strip.Flow.Controls.Count) return;
        if (_strip.Flow.Controls[ix] is MediaThumb th) th.SetImage(LoadThumbOrFull(item, keepAlpha: false));
    }

    // ── video overlay (a video sentinel is the current main media) ───────────────
    private void ShowVideoOverlay(string item)
    {
        if (_mediaVideo == null) return;
        _mediaVideo.Autoplay = _cfg.VideoAutoplay;
        _mediaVideo.AutoplaySound = _cfg.VideoAutoplaySound;
        _mediaVideo.BringToFront();
        // The still frame only if it is ALREADY extracted — otherwise black + ▶, and the deferred pass
        // hands it over when it lands (SetStillFor).
        _mediaVideo.ShowFor(Media.MediaVideoItem.PathOf(item), Media.MediaVideoItem.CachedThumb(item));
    }

    private void HideVideoOverlay()
    {
        if (_mediaVideo == null || (!_mediaVideo.Visible && !_mediaVideo.HasContent)) return;
        _mediaVideo.Clear();
        _mediaVideo.Visible = false;
    }

    // ── fullscreen viewers (LB parity) ───────────────────────────────────────
    // Double-click the main image → fullscreen image viewer over the game's IMAGE items only (the 3D
    // sentinel — and any future video item — is filtered out; navigation is image-to-image).
    private void OpenFullscreenImage()
    {
        if (_detailsShown is not IGame g || !ReferenceEquals(g, _mediaItemsGame)) return;   // node / mid-transit
        var items = _mediaItems;
        if (items == null) return;
        // IMAGES ONLY, as specified: navigation skips the 3D model AND the videos. (Videos slipped in when
        // they became media items — their sentinel isn't a 3D one, so the old filter let them through and the
        // viewer would have paged onto a video's still frame as if it were a picture.)
        var imgs = items.Where(s => !Media.Media3dItem.Is(s) && !Media.MediaVideoItem.Is(s)).ToList();
        if (imgs.Count == 0) return;
        string cur = _mediaSel >= 0 && _mediaSel < items.Count ? items[_mediaSel] : imgs[0];
        int start = imgs.FindIndex(s => string.Equals(s, cur, StringComparison.OrdinalIgnoreCase));
        using var v = new Media.FullscreenImageViewer(imgs, Math.Max(0, start), LoadImage);
        v.ShowDialog(this);
    }

    // The 3D overlay's ⤢ badge → fullscreen model viewer (cached GLB instantly, then the same model
    // rebuilt live with source-resolution textures).
    private void OpenFullscreen3d(IGame g)
    {
        using var v = new Model3d.Model3dFullscreen(g);
        v.ShowDialog(this);
    }

    // The video hover bar's ⤢ button → fullscreen player. The inline one is PAUSED for the duration so
    // the two never decode (or talk) at once, and the fullscreen picks up at its exact position.
    private void OpenFullscreenVideo()
    {
        if (_mediaVideo == null || _mediaItems == null) return;
        string item = _mediaSel >= 0 && _mediaSel < _mediaItems.Count ? _mediaItems[_mediaSel] : null;
        if (!Media.MediaVideoItem.Is(item)) return;
        long at = _mediaVideo.PositionMs;
        _mediaVideo.PauseIfPlaying();
        using var v = new Video.VideoFullscreen(Media.MediaVideoItem.PathOf(item),
                                                Media.MediaVideoItem.CachedThumb(item), _cfg.VideoAutoplaySound, at);
        v.ShowDialog(this);
        _mediaVideo.ResumeAt(v.ExitPositionMs, v.ContinuePlaying, v.ExitEnded);
    }

    private void OpenFullscreenFromThumb(string item)
    {
        if (_detailsShown is not IGame g || _mediaItems == null) return;
        SetMainMedia(item, full: true, _detailsLoadToken);

        if (Media.Media3dItem.Is(item))
            OpenFullscreen3d(g);
        else if (Media.MediaVideoItem.Is(item))
            OpenFullscreenVideo();
        else
            OpenFullscreenImage();
    }

    // Sets the main media. NOTE: single extension point — a future video item would be
    // detected here and hosted in the 16:9 zone instead of an image.
    private void SetMainMedia(string src, bool full, int token)
    {
        if (_mediaItems != null) { int ix = _mediaItems.FindIndex(s => string.Equals(s, src, StringComparison.OrdinalIgnoreCase)); if (ix >= 0) _mediaSel = ix; }
        HighlightStrip();
        // The 3D sentinel: the overlay takes the box over (PNG first, viewport behind). Any other
        // item hides it back to the plain image panel.
        if (Media.Media3dItem.Is(src))
        {
            HideVideoOverlay();
            if (_detailsShown is IGame g3) Show3dOverlay(g3);
            return;
        }
        Hide3dOverlay();
        // A video takes the main zone over: still frame + ▶, and playback when Autoplay is on.
        if (Media.MediaVideoItem.Is(src))
        {
            ShowVideoOverlay(src);
            return;
        }
        HideVideoOverlay();
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
    // Config-driven (Options → Display → Right panel). Each MediaEntry names a FAMILY or an EXACT LB type
    // plus a count; entries are taken in order (= priority), deduped, capped at MaxMediaItems. entry[0]'s
    // first image is the main box the delay upgrades to full-res. Default layout == the old hard-coded list.
    private static List<string> BuildMediaList(IGame g, bool poster, bool forceDup = false)
    {
        var items = new List<string>();
        var dupAccepted = new List<string>();    // the accepted REAL images (sentinels excluded both ways)
        Media.MediaDupFilter dupFilter = null;   // set below once the game is identified
        // `sentinel` = a 3D model or a video item: both BYPASS the dup filter in both directions (a 3D
        // snapshot is a render of the front, a video frame isn't an image of the game's art — comparing
        // would evict the real thing, and neither is a decodable file for later candidates).
        bool Add(string s, bool sentinel = false)
        {
            if (items.Count >= MaxMediaItems) return false;
            if (string.IsNullOrEmpty(s) || items.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) return false;
            // Prevent-duplicates filter: a visually-duplicate candidate is skipped — it doesn't consume the
            // entry budget, so the next candidate takes its place. Cached per image in the :lb.dupcheck ADS.
            if (!sentinel && dupFilter != null && dupFilter.IsDup(s, dupAccepted)) return false;
            items.Add(s);
            if (!sentinel) dupAccepted.Add(s);
            return true;
        }

        string plat = Safe(() => g.Platform);
        string title = S(Safe(() => g.Title));
        bool haveId = !string.IsNullOrEmpty(plat) && Guid.TryParse(S(Safe(() => g.Id)), out var id);
        Guid.TryParse(S(Safe(() => g.Id)), out var gid);
        string gameReg = S(Safe(() => g.Region));   // used by entries flagged "game region first"

        if (haveId) dupFilter = Media.MediaDupFilter.For(Media.MediaLayout.Current, poster, plat, gid, title, forceDup);

        var layout = Media.MediaLayout.Current.PostLoadFor(poster);
        var contrib = new int[layout.Count];   // images each entry actually added (for cumulative counting)
        for (int ei = 0; ei < layout.Count; ei++)
        {
            var e = layout[ei];
            // Cumulative: the target counts the images already added by the N entries directly above, so
            // this entry only tops up to reach e.Count. Non-cumulative: take up to e.Count from this entry.
            int budget = e.Count;
            if (e.Cumulative)
            {
                int depth = Math.Max(0, e.CumulativeDepth);
                int above = 0;
                for (int k = Math.Max(0, ei - depth); k < ei; k++) above += contrib[k];
                budget = Math.Max(0, e.Count - above);
            }
            // The 3D pseudo-family: at most ONE item, present only when the model CAN exist (front art,
            // or a full scan in full-scan mode — Model3dCache.Resolve's HasArt). The GLB may not be
            // baked yet: the sentinel still goes in, the settle-time bake fills it (Kick3dBake).
            if (!e.ExactType && string.Equals(e.Sel, Media.Media3dItem.FamilyKey, StringComparison.OrdinalIgnoreCase))
            {
                if (budget > 0)
                    try
                    {
                        // "COULD this game have a model", not "does it already have one". The item added
                        // here is the sentinel the block bakes from, so gating it on the file existing
                        // means nothing is ever baked. This runs in the post-load pipeline, off the
                        // transit path, which is exactly where the one Resolve that answers it belongs.
                        string? glbPath = Model3d.Model3dCache.Resolve(g) is { HasArt: true } idn3d
                            ? idn3d.GlbPath : null;
                        if (glbPath != null && Add(Media.Media3dItem.For(glbPath), sentinel: true)) contrib[ei]++;
                    }
                    catch { }
                continue;
            }
            // The VIDEO pseudo-family: "Video" takes every type (main, Trailer, Theme, Marquee,
            // Recordings — in that order), "Video:<SubDir>" just one. Items ride as sentinels; their
            // still frames are fetched AFTER the post-load delay (KickVideoThumbs), never here.
            if (Media.MediaVideoItem.IsSelector(e.Sel))
            {
                if (budget > 0)
                    try
                    {
                        int taken0 = 0;
                        foreach (var vp in Media.MediaVideoItem.Resolve(g, Media.MediaVideoItem.SubDirOf(e.Sel)))
                        {
                            if (taken0 >= budget) break;
                            if (Add(Media.MediaVideoItem.For(vp), sentinel: true)) { taken0++; contrib[ei]++; }
                        }
                    }
                    catch { }
                continue;
            }
            int taken = 0;
            foreach (var path in ResolveMediaEntry(g, e, plat, title, gid, haveId, gameReg))
            {
                if (taken >= budget) break;
                if (Add(path)) { taken++; contrib[ei]++; }
                if (items.Count >= MaxMediaItems) return items;
            }
        }
        if (items.Count == 0)   // no id / empty layout → minimal IGame fallback
        {
            Add(Safe(() => g.FrontImagePath)); Add(Safe(() => g.ScreenshotImagePath)); Add(Safe(() => g.BackgroundImagePath));
        }
        return items;
    }

    // The ordered candidate paths for one layout entry (auto selection: LB type→region→number).
    private static IEnumerable<string> ResolveMediaEntry(IGame g, Media.MediaEntry e, string plat, string title, Guid id, bool haveId, string? gameRegion)
    {
        string? region = e.IgnoreGameRegion ? null : gameRegion;   // default = prefer the game's own region (LB-identical); checked = ignore it
        if (e.ExactType)
            return haveId ? MediaResolver.AllOfType(plat, id, title, e.Sel, region, e.AllRegions) : Array.Empty<string>();

        // Family: the best pick first (cache-or-IO, keeps the IGame fallback), then all files of its LB types.
        var list = new List<string>();
        var best = CacheSourceFor(g, e.Sel);
        if (!string.IsNullOrEmpty(best)) list.Add(best);
        if (haveId && Gc.SettingsWatcher.GetImageRegroupementPriorities().TryGetValue(e.Sel, out var types))
            foreach (var t in types) list.AddRange(MediaResolver.AllOfType(plat, id, title, t, region, e.AllRegions));
        return list;
    }

    // The post-load list may carry a 3D sentinel whose GLB isn't baked yet (its strip tile shows only
    // the badge, the main box nothing). Bake it at settle on the STA worker, then refresh the tile —
    // and the main box when the 3D item is the selected one. Idempotent with the overlay's own
    // bake-on-miss (Ensure re-checks existence inside the STA job).
    private void Kick3dBake(IGame g, List<string> items, int token)
    {
        string it = items.FirstOrDefault(Media.Media3dItem.Is);
        if (it == null) return;
        string glb = Media.Media3dItem.GlbPath(it);
        bool exists; try { exists = File.Exists(glb); } catch { exists = false; }
        // Present is not current: art added since the bake (or changed model settings, or a newer baker)
        // leaves a stale GLB in the game's slot, and stopping at File.Exists showed it forever — only a
        // bulk regenerate ever caught up. One header read says which, and a stale one falls through to
        // the re-bake below exactly like a missing one.
        if (exists)
        {
            bool current = true;
            try { var idn = Model3d.Model3dCache.Resolve(g); current = idn == null || Model3d.Model3dCache.IsCurrent(idn); } catch { }
            if (current) return;
        }
        // LongRunning: Ensure BLOCKS on the serialized STA bake queue — parking pool threads there
        // during fast scrolling starved the pool and froze the transit image loader. The stale-check
        // also drops the bake entirely once the selection has moved on.
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            // stillWanted is re-checked INSIDE the STA job, right before the expensive bake: drop it when the
            // selection moved on, and also when a GAME LAUNCHED — a bake is minutes of CPU we owe the game,
            // and the queue naturally refills when the pane is rebuilt after the exit.
            if (Model3d.Model3dCache.Ensure(g, stillWanted: () => token == _detailsLoadToken && !HostLaunch.GameRunning) == null) return;
            try
            {
                if (IsDisposed || token != _detailsLoadToken) return;
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || token != _detailsLoadToken || _mediaItems == null) return;
                    int ix = _mediaItems.IndexOf(it);
                    if (ix < 0) return;
                    if (ix < _strip.Flow.Controls.Count && _strip.Flow.Controls[ix] is MediaThumb th)
                        th.SetImage(Media.Media3dItem.Thumb(it));
                    if (_mediaSel == ix) SetMainMedia(it, full: true, token);   // overlay now hits the fresh GLB
                }));
            }
            catch { }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
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
                Badge3d = Media.Media3dItem.Is(src),   // little "3D" tag, bottom-right of the tile
                BadgePlay = Media.MediaVideoItem.Is(src),   // ▶ over video tiles (drawn, never baked into the cached frame)
            };
            th.Click += (_, _) => SetMainMedia(captured, full: true, _detailsLoadToken);
            th.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) OpenFullscreenFromThumb(captured);
            };
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
    internal static string[] ResolveCacheSources(IGame g)   // internal: --thumbtest replays this headless
    {
        if (g == null) return null;
        return new[] { CacheSourceFor(g, "ClearLogo"), CacheSourceFor(g, "Front"), CacheSourceFor(g, "Screenshots") };
    }

    /// <summary>Every image regroupement offerable in the bulk cache generator, in display order — the
    /// SAME list drives the selection modal, the generation phases and the thumb GC's valid-set (a thumb
    /// the generator can produce must be a thumb the GC marks, or it would sweep it after 48 h).</summary>
    internal static readonly (string Key, string Title)[] CacheRegroupements =
    {
        ("ClearLogo", "Clear logos"),
        ("Front", "Box fronts"),
        ("Back", "Box backs"),
        ("Box3d", "3D boxes"),
        ("BoxSpine", "Box spines"),
        ("BoxFull", "Box full scans"),
        ("CartFront", "Cart fronts"),
        ("CartBack", "Cart backs"),
        ("Cart3d", "3D carts"),
        ("Screenshots", "Screenshots"),
        ("Background", "Backgrounds"),
        ("Marquee", "Marquees"),
    };

    /// <summary>The source image the UI would thumb-cache for (game, regroupement): the game cache's ★★
    /// pick first, then the IGame path fallback of that regroupement (the classic three keep their longer
    /// chains — detail-pane parity). Null when the game simply has no such image.</summary>
    internal static string CacheSourceFor(IGame g, string regroupement) => regroupement switch
    {
        "ClearLogo" => DetailSource(g, "ClearLogo", () => Safe(() => g.ClearLogoImagePath)),
        "Front" => DetailSource(g, "Front", () =>
              Safe(() => g.FrontImagePath) is { Length: > 0 } f ? f
            : Safe(() => g.Box3DImagePath) is { Length: > 0 } b ? b
            : Safe(() => g.ScreenshotImagePath)),
        "Screenshots" => DetailSource(g, "Screenshots", () =>
              Safe(() => g.ScreenshotImagePath) is { Length: > 0 } s ? s
            : Safe(() => g.BackgroundImagePath)),
        "Back" => DetailSource(g, "Back", () => Safe(() => g.BackImagePath)),
        "Box3d" => DetailSource(g, "Box3d", () => Safe(() => g.Box3DImagePath)),
        "CartFront" => DetailSource(g, "CartFront", () => Safe(() => g.CartFrontImagePath)),
        "CartBack" => DetailSource(g, "CartBack", () => Safe(() => g.CartBackImagePath)),
        "Cart3d" => DetailSource(g, "Cart3d", () => Safe(() => g.Cart3DImagePath)),
        "Background" => DetailSource(g, "Background", () => Safe(() => g.BackgroundImagePath)),
        "Marquee" => DetailSource(g, "Marquee", () => Safe(() => g.MarqueeImagePath)),
        _ => DetailSource(g, regroupement, () => null),   // BoxSpine / BoxFull: cache-only (no IGame property)
    };

    private GenerateCacheProgressForm? _genCacheLive;   // restore the running generation instead of double-launching

    private void GenerateAllCachedImages() => GenerateCachedImages(null);

    /// <summary>The Generate Image Cache flow (options dialog → phased progress run).
    /// <paramref name="only"/> restricts the run to those games (the menu's selected-games entry);
    /// null = the whole library (the all-games entry).</summary>
    private void GenerateCachedImages(IGame[] only)
    {
        if (Media.ParentalBridge.Active) return;   // limited mode: no image-cache generation
        if (_genCacheLive is { IsDisposed: false } live) { try { live.RestoreFromMinimized(); } catch { } return; }
        var games = only ?? Safe(() => _dm.GetAllGames()) ?? Array.Empty<IGame>();
        if (games.Length == 0) return;

        // The previous run's selection is re-proposed (GenCacheSelection csv in LiteBox.ini).
        var saved = new HashSet<string>(
            (_cfg.Get("GenCacheSelection", null) ?? "Front,Screenshots")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        using var opts = new GenerateCacheOptionsForm(saved);
        if (opts.ShowDialog(this) != DialogResult.OK) return;

        var chosen = opts.SelectedRegroupements;
        var csvParts = new List<string>(chosen);
        if (opts.Videos) csvParts.Add("videos");
        if (opts.Docs) csvParts.Add("docs");
        if (opts.Models3d) csvParts.Add("models3d");
        if (opts.Dup) csvParts.Add("dup");
        try { _cfg.Set("GenCacheSelection", string.Join(",", csvParts)); _cfg.Save(); } catch { }

        // The WHAT is remembered; the HOW is not — a forced rebuild is a one-shot gesture (the red entry
        // under the split button), never the state the next run opens in.
        bool force = opts.Force;
        var phases = BuildCachePhases(chosen, opts.Videos, opts.Docs, opts.Models3d, opts.Dup, force);
        if (phases.Count == 0) return;

        var dlg = new GenerateCacheProgressForm(phases, games);
        _genCacheLive = dlg;
        int total = games.Length;
        dlg.FormClosed += (_, _) =>
        {
            _genCacheLive = null;
            // Completion notification, wired HERE rather than inside the form: --gencache drives the very
            // same form headlessly (GenCacheSelfTest) and exits the app when it closes — that run must stay
            // silent. This call site is the menu one, i.e. the only manual trigger.
            // Worth notifying even though the dialog was on screen: it can be MINIMIZED while it works,
            // and on success it simply closes without ever stating a result.
            if (dlg.WasCancelled)
                LiteBox.Notifications.NotificationCenter.Info("Media cache generation stopped.");
            else if (dlg.FailedCount > 0)
                LiteBox.Notifications.NotificationCenter.Error(
                    $"Media cache generated for {total} game(s) — {dlg.FailedCount} thumbnail(s) failed (see litebox-debug.log).");
            else
                LiteBox.Notifications.NotificationCenter.Info(
                    (force ? "Media cache regenerated — " : "Media cache generated — ") + $"{total} game(s).");
        };
        dlg.ShowPseudoModal(this);
    }

    // One image REGROUPEMENT per phase (ClearLogo → webp/alpha, everything else → jpg).
    // Failures are counted only when a source EXISTS and still would not generate (Magick trouble).
    // force = the "Regenerate everything" run: the cached entry is dropped and rebuilt instead of hit.
    private static CachePhase ImagePhase(string title, string regroupement, bool force) => new(title, Math.Min(4, Math.Max(1, Environment.ProcessorCount)), g =>
    {
        string src = CacheSourceFor(g, regroupement);
        if (string.IsNullOrEmpty(src)) return 0;
        var fmt = ThumbCache.FormatFor(regroupement);
        string made = force ? ThumbCache.Rebuild(src, fmt) : ThumbCache.GetOrCreate(src, fmt);
        return made == null && File.Exists(src) ? 1 : 0;
    });

    // Every video of the game (cache-first, IGame fallback) — frame-extracted unless already cached.
    private static int VideoWork(IGame g, bool force)
    {
        int fail = 0;
        foreach (var p in VideoPathsOf(g))
        {
            try
            {
                if (force) { if (!Video.VideoThumbnailer.Regenerate(p)) fail++; continue; }
                if (Video.VideoThumbnailer.IsCached(p)) continue;
                using var img = Video.VideoThumbnailer.Get(p);
                if (img == null) fail++;
            }
            catch { fail++; }
        }
        return fail;
    }

    private static List<string> VideoPathsOf(IGame g)
    {
        var res = new List<string>();
        try
        {
            string plat = g.Platform;
            if (!string.IsNullOrEmpty(plat) && Gc.HostGameCache.Ready(plat) && Guid.TryParse(g.Id, out var id))
                foreach (var v in Gc.HostGameCache.AllVideoRefs(plat, id))
                    if (v?.FullPath is { Length: > 0 } p) res.Add(p);
            if (res.Count == 0)
            {
                var p = Safe(() => g.GetVideoPath(false));
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) res.Add(p);
            }
        }
        catch { }
        return res;
    }

    // Every document of the game (AdditionalApplication Section=Document), rendered at DocRenderDim.
    private static int DocWork(IGame g, bool force)
    {
        int fail = 0;
        try
        {
            foreach (var a in g.GetAllAdditionalApplications() ?? Array.Empty<Unbroken.LaunchBox.Plugins.Data.IAdditionalApplication>())
            {
                if (a is not Data.HostAdditionalApplication { IsDocument: true } h) continue;
                string abs = EditGameWindow.DocResolve(h.ApplicationPath);
                if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) continue;
                if (!EditGameWindow.DocEnsureThumb(abs, force)) fail++;
            }
        }
        catch { }
        return fail;
    }

    /// <summary>One step of the bulk cache generation: a title, its parallelism, and the per-game worker
    /// (returns the number of FAILURES for that game). The progress dialog runs the phases in order.</summary>
    internal sealed record CachePhase(string Title, int Dop, Func<IGame, int> Work);

    /// <param name="force">"Regenerate everything": each phase drops the cached entry before rebuilding it.
    /// The default run is missing-only — every worker is a cache HIT for anything already there.
    /// The dup phase is the exception: force changes nothing for it (see below).</param>
    private List<CachePhase> BuildCachePhases(ISet<string> regroupements, bool videos, bool docs, bool models3d = false, bool dup = false, bool force = false)
    {
        var phases = new List<CachePhase>();
        foreach (var (key, title) in CacheRegroupements)
            if (regroupements.Contains(key)) phases.Add(ImagePhase(title, key, force));
        if (videos) phases.Add(new CachePhase("Video thumbnails", 1, g => VideoWork(g, force)));
        if (docs) phases.Add(new CachePhase("Document thumbnails", 1, g => DocWork(g, force)));
        // Pre-compute the missing dup-check results (both views), the same walk as the Options → Caches
        // "Update duplicates" pass minus its forceDup — a valid (ctx,par) record cannot be stale, so even
        // a forced run only fills the gaps. Sequential like that pass (one CNN/GPU engine). Gated on the
        // option: with it off the filter is null and the walk would be a silent no-op.
        bool dupOn = false;
        try { dupOn = Media.MediaLayout.Current.PreventDuplicates; } catch { }
        if (dup && dupOn) phases.Add(new CachePhase("Duplicate detection", 1, g =>
        {
            try { BuildMediaList(g, poster: false); BuildMediaList(g, poster: true); } catch { }
            return 0;   // the filter fails OPEN and persists nothing — there is no failure to count
        }));
        // Bakes run on the STA worker POOL (Model3dBaker.WorkerCount) — feed it as many blocked callers.
        // A game with no case art is a skip, not a failure.
        if (models3d) phases.Add(new CachePhase("3D box models", Model3d.Model3dBaker.WorkerCount, g =>
        {
            var idn = Model3d.Model3dCache.Resolve(g);
            if (idn == null || !idn.HasArt) return 0;
            // force flows INTO Ensure (re-bake even a current model) rather than deleting the slot first:
            // GlbFile.Write is tmp+move, so a failed bake leaves the old GLB instead of an empty slot.
            return Model3d.Model3dCache.Ensure(g, force: force) == null ? 1 : 0;
        }));
        return phases;
    }

    // --gencache driver: runs the REAL progress form pseudo-modally, verifies the owner block, drives the
    // Minimize button (verifies the unblock), waits for completion and exits. Prints [gencache] lines.
    private void GenCacheSelfTest(string csv)
    {
        try
        {
            var sel = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool Has(string k) => sel.Contains(k, StringComparer.OrdinalIgnoreCase) || sel.Contains("all", StringComparer.OrdinalIgnoreCase);
            var games = Safe(() => _dm.GetAllGames()) ?? Array.Empty<IGame>();
            var regs = new HashSet<string>(CacheRegroupements.Select(r => r.Key).Where(Has), StringComparer.OrdinalIgnoreCase);
            // legacy aliases from the pre-regroupement driver csv
            if (Has("logos")) regs.Add("ClearLogo");
            if (Has("fronts")) regs.Add("Front");
            if (Has("shots")) regs.Add("Screenshots");
            // "force" in the csv drives the same rebuild-everything run as the dialog's red entry.
            bool force = sel.Contains("force", StringComparer.OrdinalIgnoreCase);
            var phases = BuildCachePhases(regs, Has("videos"), Has("docs"), Has("models3d"), Has("dup"), force);
            Console.WriteLine($"[gencache] phases=[{string.Join(", ", phases.Select(p => p.Title))}] games={games.Length} force={force}");
            if (phases.Count == 0 || games.Length == 0) { Application.Exit(); return; }

            var dlg = new GenerateCacheProgressForm(phases, games);
            _genCacheLive = dlg;
            dlg.FormClosed += (_, _) =>
            {
                Console.WriteLine($"[gencache] finished: failed={dlg.FailedCount}");
                _genCacheLive = null;
                BeginInvoke((Action)Application.Exit);
            };
            dlg.ShowPseudoModal(this);
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (_, _) =>
            {
                t.Stop(); t.Dispose();
                Console.WriteLine($"[gencache] pseudo-modal: owner input-enabled={GenerateCacheProgressForm.IsWindowEnabled(Handle)} (expect False)");
                dlg.DriveMinimize();
                Console.WriteLine($"[gencache] after minimize: owner input-enabled={GenerateCacheProgressForm.IsWindowEnabled(Handle)} (expect True), state={dlg.WindowState}");
            };
            t.Start();
        }
        catch (Exception ex) { Console.WriteLine("[gencache] " + ex.Message); Application.Exit(); }
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
                _media.SetImage(art, Media.Media3dItem.Is(artSrc));   // degraded box now; upgraded to full after 0.5s
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
        if (Media.Media3dItem.Is(src)) return Media.Media3dItem.Thumb(src);   // 3D sentinel → the GLB's baked PNG (never ThumbCache)
        // Video sentinel → the ALREADY-EXTRACTED frame only. Extraction costs hundreds of ms; a missing
        // frame stays black here and is fetched after the post-load delay (KickVideoThumbs).
        if (Media.MediaVideoItem.Is(src)) return Media.MediaVideoItem.CachedThumb(src);
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
            if (Media.Media3dItem.Is(path)) return Media.Media3dItem.Thumb(path);   // 3D sentinel → baked PNG
            if (Media.MediaVideoItem.Is(path)) return Media.MediaVideoItem.CachedThumb(path);   // video → cached frame only
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
        // A game is starting: stop the video (libvlc is about to be released for the game's RAM) and make
        // the deferred video-thumb worker stand down — it decodes with the very instance being disposed.
        HideVideoOverlay();
        _videoThumbToken++;
        // Tear down the web kiosk (frees the WebView2 process) while the game runs; recreated + deep-linked
        // back to this game on exit. Mirrors ExtendDB's full teardown.
        try { Web.Kiosk.WebKioskWindow.SuspendForGameLaunch(g != null ? Safe(() => g.Id) : null, g != null ? Safe(() => g.Platform) : null); } catch { }
        SetStorePoll(false);   // pause the install-state poll while the game runs (client DB may be mid-write)
        CandidateProvider.ReleaseMemory();   // drop the suggester's candidate pool — that RAM belongs to the game now
        // RA launch correction (engine P4): align the IGame hash/raid with the entry that actually
        // launched (Select-ROM pick / version), store-first, raid-only guard. Fire-and-forget.
        if (g != null)
        {
            var launched = g;
            Task.Run(() => { try { Ra.RaLaunchCorrect.OnGameLaunched(launched, _dm as HostDataManagerXml); } catch { } });
        }

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

        // Achievements earned in that session move the points in the menu bar (and the profile window,
        // if it's open). Background, best-effort, and a no-op for a game without an RA id.
        RefreshAchievementPointsAfterGame(g);

        HideRunningOverlay();
        // Bring the web kiosk back (to the exact page it was on) now the game has exited.
        try { Web.Kiosk.WebKioskWindow.RestoreAfterGameLaunch(); } catch { }

        if (_cfg.UnloadListDuringGame)
        {
            ApplyFilter();   // _current already reloaded by HostLaunch → Games = _current + rebuild
            IGame target = _resumeGameId == null ? null
                : _current.FirstOrDefault(x => string.Equals(Safe(() => x.Id), _resumeGameId, StringComparison.OrdinalIgnoreCase));
            // focus:false — re-selecting the resumed game must NOT activate the LiteBox
            // window: with the ExtendDB web kiosk as the frontend, its re-shown window
            // owns the post-game focus (the ForceFrontendFocusOnShutdown option and the
            // end-screen close govern who ends up in front, not this bookkeeping).
            if (target != null) { _games.SelectGame(target, false); ShowDetails(target); }
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

    /// <summary>Rebuilds the tree and the current list after a change that added or removed GAMES —
    /// combining and expanding both do. Mirrors the local RefreshAfterEdit used by the tree menu,
    /// which is a local function and therefore out of reach from here.</summary>
    private void ReloadAfterGameChange()
    {
        try
        {
            (_dm as HostDataManagerXml)?.ReloadHierarchy();
            object keep = _currentNode ?? AllNode.Instance;
            PopulateSources();
            _currentNode = null;                 // force the re-fill: LoadNode skips a same-node call
            LoadNode(keep);
        }
        catch (Exception ex) { Console.WriteLine("[gamemenu] refresh: " + ex.Message); }
    }

    /// <summary>Asks which game the others fold into, then combines. Destructive — the absorbed
    /// games stop existing as games — so the outcome is reported once it is done.</summary>
    private void CombineSelectedGames(IGame[] games)
    {
        if (Media.ParentalBridge.Active) return;   // limited mode
        if (_dm is not HostDataManagerXml dm || games.Length < 2) return;
        var root = Platforms.RootGamePicker.Ask(this, games);
        if (root == null) return;
        // Same reasoning as Expand: the loss is LaunchBox's, but it is not going to be silent here.
        if (MessageBox.Show(this,
                $"Combine {games.Length} games into \"{S(Safe(() => root.Title))}\"?\n\n"
                + "The absorbed games stop existing: their ID, database ID, title and manuals are "
                + "lost, and so is any field a version cannot hold (genre, notes, rating…).\n\n"
                + "Media are pooled when the games are the same database entry; anything left with "
                + "nothing pointing at it is deleted. Save games and save states are kept.",
                "Combine", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        // Documents have to be picked BEFORE the combine: the games that own them are about to stop
        // existing. Only the ones the destination does not already have are worth a question.
        var others = games.Where(g => !ReferenceEquals(g, root)).ToArray();
        var offer = Platforms.CombineDocumentPicker.Distinct(
            others.Select(g => Games.GameCombiner.DocumentsOf(g)).ToList(),
            Games.GameCombiner.DocumentsOf(root));
        var keep = Platforms.CombineDocumentPicker.Ask(this, offer);

        var outcome = Games.GameCombiner.Run(games, root, dm, keep);
        if (outcome.Absorbed <= 0) return;
        try { dm.FlushIfSafe(); } catch { }
        ReloadAfterGameChange();


        string media = outcome.MediaMoved > 0 || outcome.MediaSkipped > 0 || outcome.MediaDeleted > 0
            ? $"\n\n{outcome.MediaMoved} media file(s) pooled, {outcome.MediaSkipped} already present or too "
              + $"similar, {outcome.MediaDeleted} orphan(s) deleted."
            : "";
        MessageBox.Show(this, $"{outcome.Absorbed} game(s) combined into \"{S(Safe(() => root.Title))}\".{media}",
            "Combine", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExpandSelectedGames(IGame[] games)
    {
        if (Media.ParentalBridge.Active) return;   // limited mode
        if (_dm is not HostDataManagerXml dm) return;
        // LaunchBox's wording, plus a line it does not have. Reproducing a data loss is a defensible
        // choice; reproducing it in silence is not.
        if (MessageBox.Show(this,
                "Additional application ROMs in the selected games will be expanded out into "
                + "separate games. Are you sure you want to continue?\n\n"
                + "The restored games get a new ID and a title derived from their file name; their "
                + "database ID is not recovered. Save games and save states are kept.",
                "LiteBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        int n = games.Sum(g => Games.GameCombiner.Expand(g, dm));
        if (n <= 0) return;
        try { dm.FlushIfSafe(); } catch { }
        ReloadAfterGameChange();
    }

    // Opens the metadata editor (EditGameWindow) for the selected game(s) — single or multiple. The
    // visible list is passed so the ◄► arrows can walk it in single mode. Honours read-only mode.
    // Refreshes the list + detail on close.
    private void OpenEditGame(IGame[] games)
    {
        if (games == null || games.Length == 0) return;
        if (Media.ParentalBridge.Active) return;   // limited mode: the editor is an admin surface
        try
        {
            bool ro = (_dm as HostDataManagerXml)?.ReadOnly ?? false;
            EditGameWindow.Open(games, _games.VisibleGames, ro, this);
            // Safety net: the editor's pages also add and remove FILES (manuals, images) and future
            // metadata downloads will too — no store notification can see those, so the edited games
            // are re-evaluated wholesale on the way out.
            Badges.BadgeWatch.RecomputeNow(games);
            InvalidatePosterArt(games);   // same blindness, poster side: drop their cached thumbs/tiles
            try { _games.RebuildView(); } catch { }   // preserves the list's multi-selection (see GameListView)
            if (_posterMode) RestorePosterSelection(games);   // its RefreshPoster (via ViewChanged) dropped the poster's
            RequestDetail(games[0]);
        }
        catch (Exception ex) { Console.WriteLine("[editgame] open failed: " + ex); }
    }

    // Re-select these games in the poster grid (display order == VisibleGames). Needed because RefreshPoster
    // resets VirtualListSize, which clears the native selection — so an edit that ran on a multi-selection
    // would otherwise leave the poster blank-selected.
    private void RestorePosterSelection(IReadOnlyList<IGame> games)
    {
        if (_poster == null || games == null || games.Count == 0) return;
        try
        {
            var view = _games.VisibleGames;
            var pos = new Dictionary<IGame, int>(view.Count);
            for (int i = 0; i < view.Count; i++) pos[view[i]] = i;
            _poster.ClearSelection();
            foreach (var g in games) if (pos.TryGetValue(g, out var ix)) _poster.SelectedIndices.Add(ix);
            if (_poster.SelectedIndices.Count > 0) { try { _poster.EnsureVisible(_poster.SelectedIndices[0]); } catch { } }
        }
        catch { }
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

    private List<IEmulator> SafeEmulatorsForPlatform(string platform, IGame game = null)
    {
        try
        {
            // LB's hidden zero-GUID "unassigned" placeholder lives in Emulators.xml but is never
            // shown by LB — filter it everywhere (same as ManageEmulatorsWindow / the Edit combos).
            var all = (_dm.GetAllEmulators() ?? Array.Empty<IEmulator>()).Where(e =>
            {
                try { var id = e?.Id; return !string.IsNullOrEmpty(id) && id != Guid.Empty.ToString(); }
                catch { return false; }
            }).ToList();
            var match = all.Where(e =>
            {
                try { return e.GetAllEmulatorPlatforms()?.Any(ep => string.Equals(ep.Platform, platform, StringComparison.OrdinalIgnoreCase)) == true; }
                catch { return false; }
            }).ToList();
            if (match.Count > 0) return match;
            // No emulator configured for the platform: don't dump the whole catalog into the picker.
            // Keep only the emulator(s) the game / its versions actually reference (data-mismatch
            // protection — the Play default must stay resolvable); a pure exe game gets an empty
            // list and the launch buttons show plain "Launch <exe>" with no caret.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var gid = Safe(() => game?.EmulatorId);
            if (!string.IsNullOrEmpty(gid)) wanted.Add(gid);
            if (game != null)
                foreach (var a in SafeAddApps(game))
                {
                    var vid = Safe(() => a.UseEmulator ? a.EmulatorId : null);
                    if (!string.IsNullOrEmpty(vid)) wanted.Add(vid);
                }
            return wanted.Count == 0 ? new List<IEmulator>()
                                     : all.Where(e => wanted.Contains(Safe(() => e.Id) ?? "")).ToList();
        }
        catch { return new List<IEmulator>(); }
    }

    /// <summary>Additional applications a user could actually LAUNCH — every consumer here (the Play ▾ version
    /// dropdown, the right-click "Play Version" submenu, the per-version emulator picker) means "playable
    /// version," not "every additional-application record." Documents (Edit Game → Documents tab;
    /// Section=="Document") are additional-application records too, but they're manuals/guides to open, not
    /// versions of the game — excluded here so they don't show up as a bogus playable "version" that would try
    /// to launch the document file as if it were the game. (LaunchBox itself surfaces them under Media →
    /// Additional Documents, never as a version, so this matches native behaviour.) Same for LB 14's
    /// Links (Section=="Link"): URLs to open in a browser, never a playable version.</summary>
    private static IAdditionalApplication[] SafeAddApps(IGame g)
    {
        try
        {
            var all = g.GetAllAdditionalApplications();
            if (all == null) return Array.Empty<IAdditionalApplication>();
            return all.Where(a => a is not Data.HostAdditionalApplication { IsNonLaunchable: true }).ToArray();
        }
        catch { return Array.Empty<IAdditionalApplication>(); }
    }

    /// <summary>Configured title-sort key, shared by the list and poster views.</summary>
    private string CompareName(IGame g) => TitleSortNormalizer.Normalize(g, _titleSortNormalization);

    private static bool Contains(string hay, string needle)
        => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    // ── Type-to-jump (incremental search) ─────────────────────────────────────
    // Native virtual-ListView type-ahead: the control accumulates the typed keys (with its own
    // timeout) and raises SearchForVirtualItem; we answer with the index of the first game whose
    // configured title-sort key starts with the typed text normalized through the same mode.
    // Setting e.Index makes the ListView select + scroll to it natively. Shared by the list AND the
    // poster (both mirror _games.VisibleGames).
    private void OnTypeAheadSearch(object sender, SearchForVirtualItemEventArgs e)
    {
        try
        {
            if (!e.IsTextSearch) return;
            string needle = NormalizeTypeAhead(e.Text);
            if (needle.Length == 0) return;

            // Out of Title A→Z order, jumping is meaningless — "the first game starting with S"
            // sits wherever that other order put it. Typing then FILTERS instead (see
            // OnGameListKeyPress), and the characters never reach this handler.
            if (!IsTitleOrderForJump()) return;

            var view = _games.VisibleGames;
            int n = view.Count;
            if (n == 0) return;
            int start = (e.StartIndex >= 0 && e.StartIndex < n) ? e.StartIndex : 0;
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;                       // wrap so repeating a letter cycles
                if (CompareName(view[i]).StartsWith(needle, StringComparison.OrdinalIgnoreCase)) { e.Index = i; return; }
            }
        }
        catch { }
    }

    private bool IsTitleOrderForJump()
        => string.Equals(_curSortKey, "title", StringComparison.OrdinalIgnoreCase) && _ascending;

    // ── Typing in the game list ───────────────────────────────────────────────
    // Two behaviours, decided by the current order, because only one of them makes sense in each:
    //
    //   Title A→Z  → JUMP. The native type-ahead handles it (OnTypeAheadSearch).
    //   Any other  → FILTER. The typed text goes into the left panel's Search box, which already filters
    //                over Title/Platform/Developer and already ANDs with the advanced criteria.
    //                Reusing it means no second filter state, no second indicator, and the text
    //                stays visible and editable where the user expects to find it.
    //
    // A filter fed this way is TRANSIENT: Escape drops it and so does changing node. A search the
    // user typed into the box themselves is left alone — they put it there deliberately.
    private bool _typedFilterIsTransient;

    private void OnGameListKeyPress(object sender, KeyPressEventArgs e)
    {
        if (IsTitleOrderForJump()) return;                       // let the native jump have it
        if (char.IsControl(e.KeyChar)) return;                   // Backspace/Escape: OnGameListKeyDown
        AppendToTypedFilter(e.KeyChar.ToString());
        e.Handled = true;   // swallow it, or the control ALSO runs its incremental search
    }

    private void OnGameListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (string.IsNullOrEmpty(_search?.Text)) return;
            SetTypedFilter("", transient: false);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode != Keys.Back || IsTitleOrderForJump()) return;
        // Backspace edits the query one character at a time; Escape drops the whole thing. Fixing a
        // keystroke and abandoning a search are different intentions, so they get different keys.
        string cur = _search?.Text ?? "";
        if (cur.Length == 0) return;
        SetTypedFilter(cur.Substring(0, cur.Length - 1), transient: true);
        e.Handled = e.SuppressKeyPress = true;
    }

    private void AppendToTypedFilter(string s)
        => SetTypedFilter((_search?.Text ?? "") + s, transient: true);

    private void SetTypedFilter(string text, bool transient)
    {
        if (_search == null) return;
        _typedFilterIsTransient = transient && text.Length > 0;
        if (_search.Text == text) { ReflectQuickFilter(); return; }
        _search.Text = text;            // TextChanged → debounce → ApplyFilter
        try { _search.SelectionStart = text.Length; } catch { }
        ReflectQuickFilter();
    }

    // A "TEMP" badge pinned inside the Search box's right edge. EM_SETMARGINS reserves the space so
    // the caret and the text never slide under it; the badge itself is a Label child of the hosted
    // TextBox. Fail-soft throughout: if any of it misbehaves, the tint alone still says everything
    // the badge would have.
    private const int EM_SETMARGINS = 0xD3;
    private const int EC_RIGHTMARGIN = 0x2;
    private Label _searchBadge;

    private void EnsureSearchBadge()
    {
        if (_searchBadge != null || _search == null) return;
        try
        {
            var host = _search;
            _searchBadge = new Label
            {
                Text = "TEMP",
                AutoSize = false,
                Visible = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 6.75f, FontStyle.Bold),
                ForeColor = Color.FromArgb(24, 18, 8),
                BackColor = Color.FromArgb(226, 152, 58),
                Cursor = Cursors.Default,
            };
            host.Controls.Add(_searchBadge);
            host.Resize += (_, _) => LayoutSearchBadge();
            // Clicking the badge is the obvious way to ask "get rid of this".
            _searchBadge.Click += (_, _) => SetTypedFilter("", transient: false);
            LayoutSearchBadge();
        }
        catch { _searchBadge = null; }
    }

    private void LayoutSearchBadge()
    {
        if (_searchBadge?.Parent is not TextBox host) return;
        try
        {
            int w = 38, h = Math.Max(12, host.ClientSize.Height - 4);
            _searchBadge.Bounds = new Rectangle(host.ClientSize.Width - w - 2, (host.ClientSize.Height - h) / 2, w, h);
            int margin = _searchBadge.Visible ? w + 4 : 0;
            SendMessage(host.Handle, EM_SETMARGINS, (IntPtr)EC_RIGHTMARGIN, (IntPtr)(margin << 16));
        }
        catch { }
    }

    /// <summary>Makes an active quick filter unmistakable, and says which KIND it is:
    ///
    ///   orange + TEMP badge — produced by typing in the game list. Transient: it goes away on its
    ///                         own when you leave the node, so it is worth flagging as not durable.
    ///   blue                — typed into the box by hand. Stays until cleared.
    ///
    /// The box is always on screen, so this replaces any separate banner.</summary>
    private void ReflectQuickFilter()
    {
        if (_search == null) return;
        bool active = !string.IsNullOrWhiteSpace(_search.Text);
        bool temp = active && _typedFilterIsTransient;
        var back = !active ? Panel2
                 : temp ? Color.FromArgb(74, 51, 20)     // ambre sombre — filtre éphémère
                        : Color.FromArgb(30, 62, 86);    // bleu — recherche délibérée
        var fore = !active ? Fg : temp ? Color.FromArgb(255, 206, 140) : Color.White;
        var edge = !active ? RoundedField.FieldBorder
                 : temp ? Color.FromArgb(168, 116, 46)
                        : Color.FromArgb(70, 132, 178);
        if (_search.BackColor != back) _search.BackColor = back;
        if (_search.ForeColor != fore) _search.ForeColor = fore;
        _searchWrap?.SetFieldColors(back, edge);   // the frame carries the tint too, not just the text strip

        EnsureSearchBadge();
        if (_searchBadge != null && _searchBadge.Visible != temp)
        {
            _searchBadge.Visible = temp;
            LayoutSearchBadge();
        }
    }

    /// <summary>Drops a filter that typing into the list produced. A search the user typed into the
    /// box stays — leaving a node is not a reason to discard what they deliberately searched for.</summary>
    private void ClearTransientTypedFilter()
    {
        if (!_typedFilterIsTransient) { ReflectQuickFilter(); return; }
        _typedFilterIsTransient = false;
        if (_search != null && _search.Text.Length > 0) _search.Text = "";
        ReflectQuickFilter();
    }

    // Reduce typed text through the configured mode as well: simple drops punctuation/spaces,
    // advanced retains normalized word boundaries, and without keeps the text as entered.
    private string NormalizeTypeAhead(string s)
        => TitleSortNormalizer.Normalize(s ?? "", "", _titleSortNormalization);

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

    /// <summary>Game lookup for the CLI drivers: id exact → title exact → first title containing the
    /// key (all case-insensitive).</summary>
    private static IGame FindGameForCli(string key)
    {
        var all = PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>();
        var byId = all.FirstOrDefault(x => string.Equals(Safe(() => x.Id), key, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;
        var exact = all.FirstOrDefault(x => string.Equals(Safe(() => x.Title), key, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return all.FirstOrDefault(x => (Safe(() => x.Title) ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
    }

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
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }
        /// <summary>Little "3D" tag in the bottom-right corner — marks the 3D-model media item.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Badge3d { get; set; }
        /// <summary>Centred ▶ — marks a video item. Painted here, never baked into the cached frame.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool BadgePlay { get; set; }
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
            if (BadgePlay)
            {
                // Centred play glyph — the tile may still be black (frame not extracted yet), which is
                // exactly when this matters most: it says "this is a video", not "this is broken".
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int r = Math.Max(9, Math.Min(rect.Width, rect.Height) / 5);
                int cx = rect.Width / 2, cy = rect.Height / 2;
                using (var bg = new SolidBrush(Color.FromArgb(150, 12, 12, 16)))
                    g.FillEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
                using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.4f))
                    g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
                using var tri = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                int t = (int)(r * 0.55);
                g.FillPolygon(tri, new[] { new Point(cx - t / 2, cy - t), new Point(cx - t / 2, cy + t), new Point(cx + t, cy) });
            }
            if (Badge3d)
            {
                // Overlay drawn at paint time (never baked into the cached PNG — the main box must stay clean).
                using var f = new Font("Segoe UI Semibold", 7f);
                var sz = g.MeasureString("3D", f);
                var br = new Rectangle(rect.Right - (int)sz.Width - 8, rect.Bottom - (int)sz.Height - 5,
                                       (int)sz.Width + 5, (int)sz.Height + 2);
                using var bg = new SolidBrush(Color.FromArgb(190, 20, 20, 24));
                g.FillRectangle(bg, br);
                using var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f);
                g.DrawRectangle(pen, br.X, br.Y, br.Width - 1, br.Height - 1);
                using var txt = new SolidBrush(Color.White);
                g.DrawString("3D", f, txt, br.X + 2, br.Y);
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
        // A 3D snapshot is a WIDE canvas with the model fitted vertically and empty sides: drawing it
        // "contain" (the right rule for a photo) letterboxes it into a tiny model as soon as the box is
        // narrower than the bake — the poster-ratio defect. Those are drawn FIT TO HEIGHT and centred
        // instead, so the surplus width is cropped, exactly like the 3D overlay's SnapshotBox.
        private bool _fitHeight;
        /// <summary>Double-click on a displayed image → the fullscreen viewer (LB parity). The 3D item
        /// never lands here: its overlay covers this panel and has its own ⤢ badge.</summary>
        public Action DoubleClicked;
        public MediaPanel()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        }
        public void SetImage(Image img, bool fitHeight = false)
        {
            var old = _img; _img = img; if (!ReferenceEquals(old, img)) old?.Dispose();
            _fitHeight = fitHeight;
            Cursor = _img != null ? Cursors.Hand : Cursors.Default;   // hover hints "click me" (LB parity)
            Invalidate();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_img != null && e.Button == MouseButtons.Left) DoubleClicked?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);   // transparent: the panel IS the reserved area; letterbox = pane background, no dark box
            if (_img == null) return;
            var rect = ClientRectangle;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (_fitHeight && _img.Height > 0 && rect.Height > 0)
            {
                int w = Math.Max(1, (int)Math.Round(_img.Width * (double)rect.Height / _img.Height));
                g.DrawImage(_img, rect.X + (rect.Width - w) / 2, rect.Y, w, rect.Height);
                return;
            }
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
        private static readonly Color BoxBgHover    = Color.FromArgb(214, 70, 70, 78);   // the same plate, lit

        private Image _logo, _fanart;
        private string _logoText;       // fallback shown (with the same pulse) when there's no clear logo
        private bool _logoReady;        // logo load settled (image set, or confirmed none → show text)
        private bool _isGame, _favorite, _ratingIsUser;
        // Badge strip, top-left. Images are CACHE-OWNED by BadgeImages — drawn, never disposed here.
        private (Image img, string tip)[] _badges = Array.Empty<(Image, string)>();
        private Rectangle[] _badgeRects = Array.Empty<Rectangle>();
        private int _hoverBadge = -1;
        private readonly ToolTip _badgeTip = new() { ShowAlways = true, InitialDelay = 350, ReshowDelay = 120 };
        // Progress is pulled OUT of the strip: it is the one badge the user can set, so it gets its
        // own button at the bottom-right — the heart's twin at the other end of that row.
        private Image _progressImg;
        private string _progressTip;
        private Rectangle _progressRect;
        private bool _hoverProgress;
        // Edit button, left of the progress one: same plate, the menu set's pencil at 80% so it reads
        // as a discreet affordance rather than a sixth badge.
        private Rectangle _editRect;
        private bool _hoverEdit;
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
        /// <summary>The progress button was clicked; the argument is the button in this panel's
        /// CLIENT coordinates, so the caller can hang a menu off its left edge with the
        /// control-relative Show overload.</summary>
        public Action<Rectangle> ProgressClicked;
        /// <summary>The edit button was clicked — opens Edit Game for the shown game.</summary>
        public Action EditClicked;

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
        { _isGame = false; _logoText = title; ClearLogoImage(); _rating = 0; _favorite = false; _ratingIsUser = false; SetBadges(null); SetProgress(null, null); Invalidate(); }
        public void SetRating(double rating, bool isUser) { _rating = rating; _ratingIsUser = isUser; Invalidate(); }
        public void SetFavorite(bool fav) { _favorite = fav; Invalidate(); }

        /// <summary>The status badges drawn in the top-left corner. LaunchBox shows these on the
        /// detail side whether or not "Show Badges" is on — that setting governs the game LIST — so
        /// the caller passes whatever the enabled set says applies, and an empty list draws nothing.</summary>
        public void SetBadges(IReadOnlyList<(Image img, string tip)> badges)
        {
            _badges = badges is { Count: > 0 } ? badges.ToArray() : Array.Empty<(Image, string)>();
            _badgeRects = new Rectangle[_badges.Length];
            _hoverBadge = -1;
            _badgeTip.SetToolTip(this, null);
            Invalidate();
        }

        /// <summary>The progress button, bottom-right. A null image hides it (badge disabled, or no
        /// pack); the caller passes the game's own state image, or the pack's generic Progress marker
        /// when the game has no progress set — the button stays there either way, because it is how
        /// you SET one.</summary>
        public void SetProgress(Image img, string tip)
        {
            _progressImg = img; _progressTip = tip;
            _hoverProgress = false; _progressRect = Rectangle.Empty;
            _hoverEdit = false; _editRect = Rectangle.Empty;
            Invalidate();
        }

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
            if (_isGame) { DrawBadges(g, rect); DrawRatingAndHeart(g, rect); }
        }

        // The badge strip: one row in the top-right corner, over a rounded plate (the same BoxBg the
        // rating box uses) so the icons stay readable on a bright fanart. Badge art is not uniform in
        // width — the pack's keyboard is wider than tall, its wheel taller than wide — so each image
        // is drawn at its own size, vertically centred in a fixed-height row, and the plate is sized
        // from the total. It grows LEFTWARD from the right edge, so adding a badge never moves the
        // ones already there. Rects are kept for the hover tooltip.
        private void DrawBadges(Graphics g, Rectangle rect)
        {
            if (_badges.Length == 0) return;
            float s = LiteBoxTheme.DpiScale(this);
            int gap = (int)Math.Round(4 * s), pad = (int)Math.Round(5 * s);
            // Row height follows the ART, not a constant: the size option scales the images the
            // caller hands over, and the plate has to grow with them.
            int h = 0;
            foreach (var (img, _) in _badges) if (img != null && img.Height > h) h = img.Height;
            if (h <= 0) return;

            int total = 0;
            foreach (var (img, _) in _badges) total += (img?.Width ?? 0) + gap;
            if (total <= 0) return;
            total -= gap;

            var plate = new Rectangle(Math.Max(10, rect.Right - 10 - (total + pad * 2)), 8,
                                      total + pad * 2, h + pad * 2);
            int x = plate.X + pad, y = plate.Y + pad;
            using (var pb = new SolidBrush(BoxBg)) FillRound(g, plate, 4, pb);

            for (int i = 0; i < _badges.Length; i++)
            {
                var img = _badges[i].img;
                if (img == null) { _badgeRects[i] = Rectangle.Empty; continue; }
                var r = new Rectangle(x, y + (h - img.Height) / 2, img.Width, img.Height);
                g.DrawImage(img, r);
                // Hit rect covers the full row height, so a short badge (the keyboard) is as easy to
                // hover as a tall one.
                _badgeRects[i] = new Rectangle(x, y, img.Width, h);
                x += img.Width + gap;
            }
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

            // Edit + progress buttons — same box language as the heart, at the OTHER end of the row:
            // they are controls, not badges, so they never join the strip up top. Edit sits to the
            // left of progress, and takes its place at the edge when progress is hidden.
            int rightX = rect.Right - 10 - boxH;
            _editRect = new Rectangle(_progressImg != null ? rightX - boxH - 6 : rightX, y, boxH, boxH);
            using (var b = new SolidBrush(_hoverEdit ? BoxBgHover : BoxBg)) FillRound(g, _editRect, 4, b);
            var pencil = UiKit.MenuIcons.Get(UiKit.MenuIcons.Edit, boxH - 6);
            if (pencil != null)
            {
                var pr = new Rectangle(_editRect.X + (boxH - pencil.Width) / 2,
                                       _editRect.Y + (boxH - pencil.Height) / 2, pencil.Width, pencil.Height);
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.8f };
                using var ia = new System.Drawing.Imaging.ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(pencil, pr, 0, 0, pencil.Width, pencil.Height, GraphicsUnit.Pixel, ia);
            }

            if (_progressImg != null)
            {
                _progressRect = new Rectangle(rightX, y, boxH, boxH);
                using (var b = new SolidBrush(_hoverProgress ? BoxBgHover : BoxBg))
                    FillRound(g, _progressRect, 4, b);
                int side = boxH - 6;
                float ratio = Math.Min(side / (float)_progressImg.Width, side / (float)_progressImg.Height);
                int w = Math.Max(1, (int)(_progressImg.Width * ratio)), h = Math.Max(1, (int)(_progressImg.Height * ratio));
                g.DrawImage(_progressImg, _progressRect.X + (boxH - w) / 2, _progressRect.Y + (boxH - h) / 2, w, h);
            }
            else _progressRect = Rectangle.Empty;
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

        // The progress menu opens on mouse UP, not down. The button sits low in the window, so the
        // drop-down has to be clamped back DOWN over the cursor — and a menu opened on mouse-down
        // then receives the release as a click on whichever item landed under the pointer, silently
        // setting a progress the user never picked. Opening on the release consumes it.
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isGame && e.Button == MouseButtons.Left)
            {
                if (!_progressRect.IsEmpty && _progressRect.Contains(e.Location))
                { ProgressClicked?.Invoke(_progressRect); return; }
                if (!_editRect.IsEmpty && _editRect.Contains(e.Location)) { EditClicked?.Invoke(); return; }
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isGame)
            {
                int hs = -1;
                for (int i = 0; i < 5; i++) if (_starRects[i].Contains(e.Location)) { hs = i; break; }
                bool oh = _heartRect.Contains(e.Location);
                bool op = !_progressRect.IsEmpty && _progressRect.Contains(e.Location);
                bool oe = !_editRect.IsEmpty && _editRect.Contains(e.Location);
                Cursor = (hs >= 0 || oh || op || oe) ? Cursors.Hand : Cursors.Default;
                if (hs != _hoverStar || oh != _hoverHeart || op != _hoverProgress || oe != _hoverEdit)
                { _hoverStar = hs; _hoverHeart = oh; _hoverProgress = op; _hoverEdit = oe; Invalidate(); }
                UpdateBadgeHover(e.Location);
            }
            base.OnMouseMove(e);
        }

        // The tooltip text is re-set only when the hovered badge CHANGES: setting it on every mouse
        // move would restart the popup timer and the tip would never appear.
        private void UpdateBadgeHover(Point p)
        {
            int hb = -1;
            for (int i = 0; i < _badgeRects.Length; i++)
                if (_badgeRects[i].Contains(p)) { hb = i; break; }
            // The progress button shares the tooltip: index -2 marks it, so moving between it and the
            // strip still re-arms the popup.
            if (hb < 0 && !_progressRect.IsEmpty && _progressRect.Contains(p)) hb = -2;
            if (hb < 0 && !_editRect.IsEmpty && _editRect.Contains(p)) hb = -3;
            if (hb == _hoverBadge) return;
            _hoverBadge = hb;
            _badgeTip.SetToolTip(this, hb == -3 ? "Edit this game..." : hb == -2 ? _progressTip
                                     : hb >= 0 ? _badges[hb].tip : null);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverStar != -1 || _hoverHeart || _hoverProgress || _hoverEdit)
            { _hoverStar = -1; _hoverHeart = false; _hoverProgress = false; _hoverEdit = false; Invalidate(); }
            if (_hoverBadge != -1) { _hoverBadge = -1; _badgeTip.SetToolTip(this, null); }
            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fade.Dispose(); _pulse.Dispose(); _logo?.Dispose(); _fanart?.Dispose(); _badgeTip.Dispose(); }
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

    // ── Bulk cache generation (selection modal + two-bar progress) ───────────
    // The options modal picks WHICH caches to generate (per image regroupement, videos, documents);
    // the progress form runs the CachePhase list with two bars — phase-level and per-game — and is
    // pseudo-modal: shown non-modal with the owner disabled, so its Minimize button can hand control
    // back (owner re-enabled, window to the taskbar) while generation keeps running in the background.

    private sealed class GenerateCacheOptionsForm : Form
    {
        public ISet<string> SelectedRegroupements =>
            _regs.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        public bool Videos => _video.Checked;
        public bool Docs => _doc.Checked;
        public bool Models3d => _m3d.Checked;
        public bool Dup => _dup.Checked;

        /// <summary>True when the run was started from the split button's red entry: every selected cache
        /// is DROPPED and rebuilt. The plain button only fills what is missing.</summary>
        public bool Force { get; private set; }

        private readonly Dictionary<string, CheckBox> _regs = new(StringComparer.OrdinalIgnoreCase);
        private readonly CheckBox _video, _doc, _m3d, _dup;
        private readonly float _s;
        private int S(int px) => (int)Math.Round(px * _s);

        /// <param name="initial">Pre-checked entries: regroupement keys + "videos"/"docs" — the saved
        /// selection of the previous run.</param>
        public GenerateCacheOptionsForm(ISet<string> initial)
        {
            _s = DeviceDpi / 96f;
            Text = "Generate Media Cache";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9f);

            CheckBox Cb(string text, int x, int y, bool check, int w = 158) =>
                new() { Text = text, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(22)), Checked = check, ForeColor = Fg };

            var header = new Label { Text = "Image thumbnails", Location = new Point(S(16), S(12)), Size = new Size(S(300), S(20)),
                                     ForeColor = Fg, Font = new Font("Segoe UI Semibold", 9f) };
            Controls.Add(header);

            // Every regroupement, two columns; checked = the previous run's saved selection.
            int i = 0, rows = (CacheRegroupements.Length + 1) / 2;
            foreach (var (key, title) in CacheRegroupements)
            {
                var cb = Cb(title, 32 + (i / rows) * 170, 34 + (i % rows) * 24, initial.Contains(key));
                _regs[key] = cb;
                Controls.Add(cb);
                i++;
            }
            int yAfter = 34 + rows * 24 + 12;

            _video = Cb("Video thumbnails", 16, yAfter, initial.Contains("videos"), 320);
            _doc = Cb("Document thumbnails", 16, yAfter + 24, initial.Contains("docs"), 320);
            _m3d = Cb("3D box models (GLB cache)", 16, yAfter + 48, initial.Contains("models3d"), 320);
            // Duplicate detection: pre-compute the missing :lb.dupcheck results so game selection never
            // pays the first-visit cost. Missing-only in BOTH run modes — the (ctx,par) key self-invalidates
            // (file set, order, sizes, engine, threshold, version all in it), so a valid record can never be
            // stale and a forced recompute would only redo the CNN work for the same verdicts. The real
            // rewrite hammer stays on Options → Caches → "Update duplicates". Needs the option enabled.
            bool dupOn = false;
            try { dupOn = Media.MediaLayout.Current.PreventDuplicates; } catch { }
            _dup = Cb(dupOn ? "Duplicate detection (missing results)"
                            : "Duplicate detection (enable it in Display first)",
                      16, yAfter + 72, dupOn && initial.Contains("dup"), 340);
            _dup.Enabled = dupOn;
            if (!dupOn) _dup.ForeColor = SubFg;
            Controls.Add(_video); Controls.Add(_doc); Controls.Add(_m3d); Controls.Add(_dup);

            // Split button: the safe run is the button itself ("Generate missing" — every worker is a cache
            // HIT for what is already there), the destructive one lives behind the arrow, in Danger red, so
            // rebuilding a whole library takes a second deliberate gesture.
            int yBtn = yAfter + 24 * 4 + 16;
            var ok = new Button { Text = "Generate missing", Location = new Point(S(108), S(yBtn)), Size = new Size(S(132), S(28)),
                                  FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, DialogResult = DialogResult.OK };
            var drop = new Button { Text = "▾", Location = new Point(S(240), S(yBtn)), Size = new Size(S(24), S(28)),
                                    FlatStyle = FlatStyle.Flat, BackColor = ControlPaint.Dark(Accent, 0.04f), ForeColor = Color.White,
                                    TabStop = false };
            var cancel = new Button { Text = "Cancel", Location = new Point(S(272), S(yBtn)), Size = new Size(S(90), S(28)),
                                      FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, DialogResult = DialogResult.Cancel };
            ok.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 72);
            drop.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 72);
            cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 72);

            var menu = new ContextMenuStrip { Renderer = new DangerRenderer(), BackColor = LiteBoxTheme.Danger,
                                              ForeColor = Color.White, ShowImageMargin = false };
            var all = new ToolStripMenuItem("Regenerate everything")
            {
                AutoSize = false,
                Size = new Size(S(156), S(30)),               // as wide as the button + its arrow: one piece
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 9f),
                ToolTipText = "Delete the cached media of the ticked families and build them again from the source.\n"
                            + "Duplicate detection stays fill-missing-only (its results can't go stale) — "
                            + "use Options → Caches → \"Update duplicates\" to force a rewrite.",
            };
            all.Click += (_, _) => { Force = true; DialogResult = DialogResult.OK; };
            menu.Items.Add(all);
            drop.Click += (_, _) => menu.Show(ok, new Point(0, ok.Height));
            Disposed += (_, _) => menu.Dispose();

            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(ok); Controls.Add(drop); Controls.Add(cancel);
            ClientSize = new Size(S(380), S(yBtn + 42));
        }

        /// <summary>The dropped panel is an EXTENSION of the button, not a normal menu: it paints itself in
        /// Danger red (the shared DarkRenderer forces Panel2/Fg on every item, which would flatten exactly
        /// the signal this entry exists to carry).</summary>
        private sealed class DangerRenderer : ToolStripProfessionalRenderer
        {
            public DangerRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var c = e.Item.Selected ? ControlPaint.Light(LiteBoxTheme.Danger, 0.25f) : LiteBoxTheme.Danger;
                using var b = new SolidBrush(c);
                e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var b = new SolidBrush(LiteBoxTheme.Danger);
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            { e.TextColor = Color.White; base.OnRenderItemText(e); }
        }
    }

    private sealed class GenerateCacheProgressForm : Form
    {
        private readonly List<CachePhase> _phases;
        private readonly IGame[] _games;
        private IDisposable? _job;   // user-initiated job registration (suspends launch-time unloading)
        private readonly ProgressBar _phaseBar, _itemBar;
        private readonly Label _phaseLabel, _itemLabel;
        private readonly Button _minBtn, _cancel;
        private readonly System.Threading.CancellationTokenSource _cts = new();
        private Form _blockedOwner;                     // non-null while pseudo-modal (owner disabled)
        private int _failed;
        private readonly float _s;
        private int S(int px) => (int)Math.Round(px * _s);

        public GenerateCacheProgressForm(List<CachePhase> phases, IGame[] games)
        {
            _phases = phases; _games = games;
            _s = DeviceDpi / 96f;
            Text = "Generate Media Cache";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ControlBox = false;
            ClientSize = new Size(S(452), S(168));
            BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9f);

            _phaseLabel = new Label { Location = new Point(S(16), S(12)), Size = new Size(S(420), S(20)), ForeColor = Fg, Text = "Preparing…" };
            _phaseBar = new ProgressBar { Location = new Point(S(16), S(36)), Size = new Size(S(420), S(14)),
                                          Minimum = 0, Maximum = Math.Max(1, phases.Count), Style = ProgressBarStyle.Continuous };
            _itemLabel = new Label { Location = new Point(S(16), S(60)), Size = new Size(S(420), S(20)), ForeColor = Fg, Text = "" };
            _itemBar = new ProgressBar { Location = new Point(S(16), S(84)), Size = new Size(S(420), S(18)),
                                         Minimum = 0, Maximum = Math.Max(1, games.Length), Style = ProgressBarStyle.Continuous };
            _minBtn = new Button { Location = new Point(S(238), S(126)), Size = new Size(S(100), S(26)), Text = "Minimize",
                                   FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
            _cancel = new Button { Location = new Point(S(346), S(126)), Size = new Size(S(90), S(26)), Text = "Cancel",
                                   FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
            _minBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 72);
            _cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 72);
            _minBtn.Click += (_, _) => MinimizeUnblock();
            _cancel.Click += (_, _) => { try { _cts.Cancel(); } catch { } _cancel.Enabled = false; _cancel.Text = "Cancelling…"; };
            Controls.AddRange(new Control[] { _phaseLabel, _phaseBar, _itemLabel, _itemBar, _minBtn, _cancel });
        }

        // Win32 EnableWindow — the SAME mechanism the real modal loop uses: it blocks input on the whole
        // window WITHOUT flipping Control.Enabled on the children, so the game list does not repaint in
        // the washed-out "disabled" look (which Form.Enabled = false caused).
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool IsWindowEnabled(IntPtr hWnd);

        /// <summary>Show non-modal but with the owner input-DISABLED (Win32-level, like ShowDialog) —
        /// the Minimize button can lift the block while the work carries on.</summary>
        public void ShowPseudoModal(Form owner)
        {
            _blockedOwner = owner;
            // Modeless: CenterParent would be ignored and Windows would drop this on the primary
            // monitor, not the one LiteBox is on. Place it ourselves.
            DialogPlacement.CenterOnOwner(this, owner);
            try { EnableWindow(owner.Handle, false); } catch { }
            Show(owner);
        }

        // Minimize → give the owner back and drop to the taskbar; generation keeps running.
        private void MinimizeUnblock()
        {
            Unblock();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Minimized;
        }

        internal int FailedCount => _failed;
        /// <summary>True when the run was stopped by the user (Cancel / closing the window) rather than
        /// finishing. Read by the MENU call site to word its completion notification.</summary>
        internal bool WasCancelled => _cts.IsCancellationRequested;
        internal void DriveMinimize() => MinimizeUnblock();   // --gencache driver

        public void RestoreFromMinimized()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            try { Activate(); } catch { }
        }

        private void Unblock()
        {
            var o = _blockedOwner; _blockedOwner = null;
            if (o is { IsDisposed: false }) { try { EnableWindow(o.Handle, true); } catch { } }
        }

        // Workers only bump these counters — NEVER post to the UI. A per-item BeginInvoke floods the
        // message queue when items are cache HITs (microseconds each → tens of thousands of posts/s):
        // the queue starves paint/input and the window looks frozen, as if the work ran on the UI thread.
        // A 100 ms UI timer samples the counters instead: bounded, smooth, and phase-atomic.
        private volatile int _curPhase = -1;     // index of the running phase (-1 = not started)
        private int _curDone;                    // items finished in the running phase (Interlocked)

        private System.Windows.Forms.Timer _uiTimer;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _uiTimer.Tick += (_, _) => PaintProgress();
            _uiTimer.Start();
            // Register as a USER-INITIATED job for its whole lifetime: launching a game while this runs
            // must not free the game cache / libvlc / CNN session out from under it (BackgroundJobs).
            _job = BackgroundJobs.Enter("Generate Media Cache");
            System.Threading.Tasks.Task.Run(RunGeneration);
        }

        private void PaintProgress()
        {
            int p = _curPhase;
            if (p < 0 || p >= _phases.Count) return;
            int done = System.Threading.Volatile.Read(ref _curDone);
            _phaseLabel.Text = $"Step {p + 1} / {_phases.Count} — {_phases[p].Title}";
            _phaseBar.Value = Math.Min(_phaseBar.Maximum, p);
            _itemBar.Value = Math.Min(_itemBar.Maximum, done);
            _itemLabel.Text = $"{done} / {_games.Length}";
        }

        private void RunGeneration()
        {
            for (int p = 0; p < _phases.Count; p++)
            {
                if (_cts.IsCancellationRequested) break;
                var phase = _phases[p];
                System.Threading.Interlocked.Exchange(ref _curDone, 0);
                _curPhase = p;
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(_games,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = phase.Dop, CancellationToken = _cts.Token },
                        g =>
                        {
                            try { System.Threading.Interlocked.Add(ref _failed, phase.Work(g)); }
                            catch { }
                            System.Threading.Interlocked.Increment(ref _curDone);
                        });
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
            Finish();
        }

        private void Finish()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    try { _uiTimer?.Stop(); _uiTimer?.Dispose(); } catch { }
                    try { _curPhase = _phases.Count - 1; PaintProgress(); _phaseBar.Value = _phaseBar.Maximum; } catch { }
                    Unblock();
                    int failed = _failed;
                    if (failed > 0 && !_cts.IsCancellationRequested)
                    {
                        if (WindowState == FormWindowState.Minimized) RestoreFromMinimized();
                        MessageBox.Show(this,
                            failed + " thumbnail(s) could not be generated.\n\nCheck litebox-debug.log — a common cause is Magick.NET " +
                            "missing from Core (Magick.NET-Q16-AnyCPU.dll + Magick.NET.Core.dll next to LiteBox.exe).",
                            "Generate Media Cache", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    Close();
                }));
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Unblock();                                   // never leave the main window disabled
            // End the job registration on EVERY exit path (finished, cancelled, closed) — Dispose is
            // idempotent, so the safety net below can also fire without double-counting.
            try { _job?.Dispose(); _job = null; } catch { }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _cts.Cancel(); } catch { }
                _cts.Dispose();
                try { _job?.Dispose(); _job = null; } catch { }   // safety net: never leave the job registered
            }
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
