// Edit Game (MULTI-selection) → Media → Images: the media-coverage MATRIX.
//
// One row per game, one column per image regroupement (Front, Back, Background, Screenshots, …). Each cell
// shows the image LaunchBox actually DISPLAYS for that slot — i.e. GetBestImageTypeFirst, the "★★" pick —
// plus a badge with how many images that game has in the category. At a glance you see who is missing what.
//
// Data comes from the GAME CACHE, not the disk: GameCacheBridge.AllImagesTypeFirst() returns the regroupement's
// images already ordered type → region → number, so its FIRST element IS the ★★ pick and its Count is the
// badge — one cache read per cell, zero I/O. We only fall back to a disk scan when the cache can't answer for
// that platform (not ready / dirty), exactly like MediaResolver.Image does.
//
// Thumbnails are decoded lazily on a background thread (the grid is VirtualMode, so only visible cells are
// ever painted) and memoised, so the table opens instantly and fills in.
//
// The database sources are explicit, batched actions with a progress modal: for every EMPTY cell they look up
// the chosen offline DB — "LaunchBox DB" (orange, LaunchBox's own Metadata.db) or "ExtendDB" (purple, the
// extended DB; only offered when the Base module is on and the DB is present) — pick the web image LaunchBox
// would use for that slot, download its thumbnail and paint the cell with a border in the SOURCE's colour
// (= not owned). Clicking any cell opens that (game, category) in a modal reusing the normal category editor,
// so move/copy/delete/download all work there.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Integrations;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private struct MxCell
    {
        public string? Path;                 // local ★★ pick (null = none locally)
        public int Count;                    // local images in the category
        public MetadataDb.WebImage? Web;     // web stand-in when the cell is empty and a source is on
        public int WebCount;                 // web candidates available for the category
        public string? WebSrc;               // which SOURCE won the cell: "lbdb"/"web"/"emu"/"steam"
        // Runners-up, only when the column's budget is above one — the cell still SHOWS a single
        // stand-in (there is room for one thumbnail), but "Download all" takes these too. Null at a
        // budget of 1, which is the ordinary case, so no memory is spent on a 3000-game selection.
        public List<MetadataDb.WebImage>? WebMore;
    }

    private DataGridView? _mxGrid;
    private List<string> _mxCats = new();                                // one entry per column: its HEADER
    /// <summary>Per column, what it actually reads: the regroupement it belongs to (which is what a
    /// click opens and what the stand-in sources are asked for) and, when the column is a SPLIT of that
    /// family, the types it keeps. Null types = the whole regroupement, the ordinary case.</summary>
    private List<(string Cat, List<string>? Types)> _mxSpec = new();
    private readonly object _mxLock = new();                             // _mxRows is touched from the UI thread (paint) AND the batch thread
    private readonly Dictionary<int, MxCell[]> _mxRows = new();          // row → cells (computed on demand)
    // Thumbnails. A 3000-game selection addresses ~35k cells, so BOTH the cache and the fetch queue must be
    // bounded or we blow up memory and starve the thread pool:
    //   • LRU-capped cache (an unbounded one would hold gigabytes of decoded bitmaps);
    //   • a small semaphore, because a web thumb is a BLOCKING network call on a pool thread — hundreds at
    //     once starve the pool and nothing ever lands (which is exactly what happened);
    //   • and a visibility check taken WHEN THE SLOT IS GRANTED, so cells scrolled far away are dropped
    //     instead of being fetched for nothing.
    private const int MxThumbCacheMax = 600;    // decoded bitmaps kept in RAM (35k would be gigabytes)
    private const int MxQueueMax = 3000;        // pending fetches kept (the farthest from the viewport are shed)
    private const int MxWorkerCount = 4;        // a web thumb is a BLOCKING download: hundreds at once starve the pool

    private readonly object _mxThumbLock = new();
    private readonly Dictionary<string, Image?> _mxThumbs = new(StringComparer.OrdinalIgnoreCase);  // key → thumb (null = failed)
    private readonly LinkedList<string> _mxLru = new();                                             // most-recent first
    private readonly Dictionary<string, LinkedListNode<string>> _mxLruNodes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class MxJob
    {
        public string Key = "";
        public string? Path;                  // local image
        public MetadataDb.WebImage? Web;      // web stand-in
        public int Row, Col;
    }

    private readonly object _mxQLock = new();
    private readonly Dictionary<string, MxJob> _mxPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MxJob> _mxQueue = new();
    private int _mxWorkers;
    private volatile int _mxVisFirst, _mxVisLast;   // visible row window, kept up to date on the UI thread

    private bool _mxShowLbDb;   // "LaunchBox DB" — LaunchBox's own Metadata.db, explicit (orange)
    private bool _mxShowWeb;    // "ExtendDB" — the extended DB, explicit (purple)
    private bool _mxShowEmu;
    private bool _mxShowSteam;
    // Sources that fill gaps, in the ORDER the user enabled them = fill priority. A cell is filled by the first
    // source (in this order) that has a candidate for it; a later source only fills what's STILL empty. So the
    // checkbox click order chooses which of LaunchBox DB / ExtendDB / EmuMovies / Steam wins each gap.
    private readonly List<string> _mxSourceOrder = new();                 // "lbdb" / "web" (extended DB) / "emu" / "steam"
    private readonly Dictionary<int, List<EmuMoviesCatalog.EmuMedia>> _mxEmuMedia = new();   // row → resolved EmuMovies media
    private readonly Dictionary<int, List<MetadataDb.WebImage>> _mxSteamMedia = new();       // row → resolved Steam media
    private Label? _mxStatus;

    // 3x taller rows: box art is portrait, so give height priority and keep the column narrow enough that 12
    // of them stay scannable. Landscape art (screenshots/backgrounds) aspect-fits inside the same box.
    private const int MxThumbW = 116, MxThumbH = 150, MxRowH = 174;
    private const int MxColW = 130;
    private static readonly Color MxWebColor = Color.FromArgb(150, 90, 200);    // purple = ExtendDB (extended DB) stand-in
    private static readonly Color MxLbDbColor = Color.FromArgb(225, 165, 80);   // orange = LaunchBox-DB stand-in
    private static readonly Color MxEmuColor = Color.FromArgb(90, 150, 220);    // blue = EmuMovies stand-in
    private static readonly Color MxSteamColor = Color.FromArgb(92, 172, 96);   // green = Steam stand-in

    /// <summary>The stand-in's border/badge colour by SOURCE (the two DB sources are distinguished by which
    /// checkbox fetched the row, recorded in <see cref="MxCell.WebSrc"/>; origin still colours emu/steam).</summary>
    private static Color MxStandinColor(MetadataDb.WebImage w, string? src)
        => string.Equals(src, "lbdb", StringComparison.Ordinal) ? MxLbDbColor
         : string.Equals(src, "web", StringComparison.Ordinal) ? MxWebColor
         : string.Equals(w.Origin, "emumovies", StringComparison.OrdinalIgnoreCase) ? MxEmuColor
         : string.Equals(w.Origin, "steam", StringComparison.OrdinalIgnoreCase) ? MxSteamColor
         : MxWebColor;

    // ── Page ──────────────────────────────────────────────────────────────────
    private Control BuildImagesMatrixPage()
    {
        MxBuildColumns();
        _mxRows.Clear();

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        var bar = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Bg };
        // TWO independent database sources: orange "LaunchBox DB" = LaunchBox's own Metadata.db, purple
        // "ExtendDB" = the extended DB (only when the Base module is on AND the DB is downloaded). Check order =
        // fill priority, exactly like the EmuMovies/Steam sources.
        var chkLbDb = new CheckBox
        {
            Text = "LaunchBox DB", AutoSize = true, ForeColor = MxLbDbColor,
            BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Location = new Point(S(4), S(10)), Checked = false,
        };
        CheckBox? chkExt = null;
        if (MediaApiBridge.ModuleActive)   // Base module on + extended DB present
        {
            chkExt = new CheckBox
            {
                Text = "ExtendDB", AutoSize = true, ForeColor = Color.FromArgb(190, 150, 230),
                BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(S(124), S(10)), Checked = false,
            };
        }
        // Blue EmuMovies "fill the gaps" — a SECOND source. Whichever of the two you check first fills the gaps
        // first; the other only fills what's still empty (see _mxSourceOrder). Only when EmuMovies is usable.
        CheckBox? chkEmu = null;
        bool emuUsable;
        try { emuUsable = EmuMoviesApi.FromLbSettings() != null; } catch { emuUsable = false; }
        if (emuUsable)
        {
            chkEmu = new CheckBox
            {
                Text = "EmuMovies", AutoSize = true, ForeColor = MxEmuColor,
                BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(S(228), S(10)), Checked = false,
            };
        }
        // Green Steam "fill the gaps" — a THIRD source, only when some selected game has a Steam appid.
        CheckBox? chkSteam = null;
        bool steamUsable = _editGames.Any(g => SteamCatalog.AppIdOf(Safe(() => g.ApplicationPath), Safe(() => g.LaunchBoxDbId) ?? -1) != null);
        if (steamUsable)
        {
            chkSteam = new CheckBox
            {
                Text = "Steam", AutoSize = true, ForeColor = MxSteamColor,
                BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(S(396), S(10)), Checked = false,
            };
        }
        var btnAll = DlgBtn("⬇  Download all missing", Color.FromArgb(78, 52, 120));
        btnAll.AutoSize = false; btnAll.SetBounds(S(540), S(5), S(170), S(28)); btnAll.Enabled = !_readOnly;
        btnAll.Click += (_, _) => MxDownloadAllMissing();

        _mxStatus = new Label
        {
            Text = $"{_editGames.Count} games × {_mxCats.Count} categories", ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(S(720), S(12)),
        };
        bar.Controls.Add(chkLbDb);
        if (chkExt != null) bar.Controls.Add(chkExt);
        if (chkEmu != null) bar.Controls.Add(chkEmu);
        if (chkSteam != null) bar.Controls.Add(chkSteam);
        bar.Controls.Add(btnAll);
        bar.Controls.Add(_mxStatus);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AllowUserToOrderColumns = false, ReadOnly = true,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            BackgroundColor = Bg, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(55, 55, 66),
            ScrollBars = ScrollBars.Both, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EnableHeadersVisualStyles = false, Cursor = Cursors.Hand,
        };
        grid.DefaultCellStyle.BackColor = Bg;
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(48, 62, 88);
        grid.DefaultCellStyle.SelectionForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(34, 34, 44);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        // Room on the right of every header for its budget spin box (a grid child, see MxBuildCountBoxes):
        // without it a long header runs under the box instead of ellipsising before it.
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(0, 0, S(52), 0);
        grid.ColumnHeadersHeight = S(30);
        grid.RowTemplate.Height = S(MxRowH);

        var colGame = new DataGridViewTextBoxColumn
        {
            Name = "Game", HeaderText = "Game", Frozen = true, Width = S(220), SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        colGame.DefaultCellStyle.Padding = new Padding(S(6), 0, 0, 0);
        grid.Columns.Add(colGame);
        foreach (var cat in _mxCats)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = cat, HeaderText = cat, Width = S(MxColW), SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _editGames.Count) return;
            e.Value = e.ColumnIndex == 0 ? (Safe(() => _editGames[e.RowIndex].Title) ?? "(untitled)") : "";
        };
        grid.CellPainting += MxPaintCell;
        grid.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) MxOpenCell(e.RowIndex, e.ColumnIndex);
            else if (e.Button == MouseButtons.Right) MxCellMenu(grid, e.RowIndex, e.ColumnIndex);
        };

        // Keep the visible-row window fresh: the fetch workers use it to always serve what's on screen first.
        // The header spin boxes ride along: they are grid children, so every horizontal move has to carry them.
        grid.Scroll += (_, _) => { MxUpdateVisible(grid); MxLayoutCountBoxes(); };
        grid.Resize += (_, _) => { MxUpdateVisible(grid); MxLayoutCountBoxes(); };
        grid.HandleCreated += (_, _) => { MxUpdateVisible(grid); MxLayoutCountBoxes(); };
        grid.ColumnWidthChanged += (_, _) => MxLayoutCountBoxes();

        grid.RowCount = _editGames.Count;
        _mxGrid = grid;
        MxUpdateVisible(grid);
        MxBuildCountBoxes(grid);

        // Footer: split the Screenshots column per type. A global LiteBox option, not a per-window one —
        // whoever wants their screenshots apart wants it every time, so the state outlives the window.
        var foot = new Panel { Dock = DockStyle.Bottom, Height = S(28), BackColor = Bg };
        var chkSplit = new CheckBox
        {
            Text = "Expand screenshots by type", AutoSize = true, ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Location = new Point(S(4), S(4)),
        };
        try { chkSplit.Checked = LiteBoxConfig.LoadForExe().ImagesMatrixExpandScreenshots; } catch { }
        chkSplit.CheckedChanged += (_, _) =>
        {
            try { var cfg = LiteBoxConfig.LoadForExe(); cfg.ImagesMatrixExpandScreenshots = chkSplit.Checked; cfg.Save(); } catch { }
            // Rebuild the page: the COLUMNS change, and every cached row was computed against the old
            // ones. Dropping the cached page is what makes ShowPage build a fresh matrix.
            try { _pages.Remove("Images"); ShowPage("Images"); } catch { }
        };
        foot.Controls.Add(chkSplit);

        root.Controls.Add(grid);   // Fill first …
        root.Controls.Add(foot);   // … then the footer …
        root.Controls.Add(bar);    // … Top last

        chkLbDb.CheckedChanged += (_, _) =>
        {
            if (chkLbDb.Checked)
            {
                _mxShowLbDb = true;
                if (!_mxSourceOrder.Contains("lbdb")) _mxSourceOrder.Add("lbdb");   // enabled now → lowest priority
                MxFillDb(chkLbDb, "lbdb");
            }
            else
            {
                _mxShowLbDb = false;
                _mxSourceOrder.Remove("lbdb");
                MxInvalidateAllRows();
                MxSetStatus($"{_editGames.Count} games × {_mxCats.Count} categories");
            }
        };

        if (chkExt != null) chkExt.CheckedChanged += (_, _) =>
        {
            if (chkExt.Checked)
            {
                _mxShowWeb = true;
                if (!_mxSourceOrder.Contains("web")) _mxSourceOrder.Add("web");
                MxFillDb(chkExt, "web");
            }
            else
            {
                _mxShowWeb = false;
                _mxSourceOrder.Remove("web");
                MxInvalidateAllRows();
                MxSetStatus($"{_editGames.Count} games × {_mxCats.Count} categories");
            }
        };

        if (chkEmu != null) chkEmu.CheckedChanged += (_, _) =>
        {
            if (chkEmu.Checked)
            {
                _mxShowEmu = true;
                if (!_mxSourceOrder.Contains("emu")) _mxSourceOrder.Add("emu");
                MxFillEmu(chkEmu);
            }
            else
            {
                _mxShowEmu = false;
                _mxSourceOrder.Remove("emu");
                MxInvalidateAllRows();
                MxSetStatus($"{_editGames.Count} games × {_mxCats.Count} categories");
            }
        };

        if (chkSteam != null) chkSteam.CheckedChanged += (_, _) =>
        {
            if (chkSteam.Checked)
            {
                _mxShowSteam = true;
                if (!_mxSourceOrder.Contains("steam")) _mxSourceOrder.Add("steam");
                MxFillSteam(chkSteam);
            }
            else
            {
                _mxShowSteam = false;
                _mxSourceOrder.Remove("steam");
                MxInvalidateAllRows();
                MxSetStatus($"{_editGames.Count} games × {_mxCats.Count} categories");
            }
        };

        return root;
    }

    private void MxSetStatus(string s) { if (_mxStatus != null) _mxStatus.Text = s; }

    // ── Row data (game cache first, disk when the cache can't be trusted) ─────
    //
    // The GameCache is only rebuilt when the Edit window closes (the deliberate "dirty + deferred rebuild"
    // model). So as soon as WE touched a platform — a download / move / delete — its cache is STALE and would
    // still report the old images. In that case read the disk for that row instead, so the grid updates
    // immediately after a download.
    /// <summary>The columns, from the image regroupements — with the SCREENSHOT family optionally split
    /// into "Game Title", "Game Over" and "Other Screenshot" (everything else configured in that family).
    /// The split is presentation only: each part still belongs to its regroupement, so clicking a cell
    /// opens the same category editor and the database sources answer for the same slot.</summary>
    private void MxBuildColumns()
    {
        _mxCats = new List<string>();
        _mxSpec = new List<(string, List<string>?)>();
        bool expand = false;
        try { expand = LiteBoxConfig.LoadForExe().ImagesMatrixExpandScreenshots; } catch { }

        foreach (var cat in MxRegroupementOrder())
        {
            var types = ImgTypesOf(cat);
            // The family is found by NAME (LB's own "Screenshots"), and the two named parts by the type
            // they end with — so a library that renamed or reordered its types still splits correctly,
            // and one that has neither simply keeps a single "Other Screenshot" column.
            bool isShots = cat.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!expand || !isShots || types.Count == 0)
            {
                _mxCats.Add(cat);
                _mxSpec.Add((cat, null));
                continue;
            }
            static bool Is(string type, string tail) => type.TrimEnd().EndsWith(tail, StringComparison.OrdinalIgnoreCase);
            var title = types.Where(t => Is(t, "Game Title")).ToList();
            var over = types.Where(t => Is(t, "Game Over")).ToList();
            var other = types.Where(t => !Is(t, "Game Title") && !Is(t, "Game Over")).ToList();
            void Part(string header, List<string> ts)
            { if (ts.Count == 0) return; _mxCats.Add(header); _mxSpec.Add((cat, ts)); }
            Part("Game Title", title);
            Part("Game Over", over);
            Part("Other Screenshot", other);
        }
        MxLoadCounts();
    }

    /// <summary>The regroupements in COLUMN order: LaunchBox's own, with the two the eye expects beside
    /// their sibling moved there — Clear Logo right after Back, Box Spine right after Marquee.</summary>
    private static List<string> MxRegroupementOrder()
    {
        var list = ImgRegroupements().ToList();
        void MoveAfter(string what, string after)
        {
            int from = list.FindIndex(c => string.Equals(c, what, StringComparison.OrdinalIgnoreCase));
            int to = list.FindIndex(c => string.Equals(c, after, StringComparison.OrdinalIgnoreCase));
            if (from < 0 || to < 0 || from == to + 1) return;
            var v = list[from];
            list.RemoveAt(from);
            to = list.FindIndex(c => string.Equals(c, after, StringComparison.OrdinalIgnoreCase));   // shifted by the removal
            list.Insert(to + 1, v);
        }
        MoveAfter("ClearLogo", "Back");
        MoveAfter("BoxSpine", "Marquee");
        return list;
    }

    // ── Per-column image budget ───────────────────────────────────────────────
    //
    // How many images that column is allowed to bring in: the number of stand-ins it may show and the
    // number "Download all" may fetch per empty cell. ZERO means the column shows LOCAL images only and
    // downloads nothing — the way to say "I don't collect these". Stored globally, one key per COLUMN
    // header, which is what keeps "Screenshots" and "Other Screenshot" apart when the split is on.
    private readonly List<int> _mxCounts = new();
    private readonly List<NumericUpDown> _mxCountBoxes = new();

    /// <summary>Default budget for a column: one image, except the slots most libraries don't collect.</summary>
    private static int MxDefaultCount(string header)
        => header is "BoxFull" or "Box3d" or "CartFront" or "CartBack" or "Cart3d" ? 0 : 1;

    private static string MxCountKey(string header)
        => "ImagesMatrixCount." + new string(header.Where(char.IsLetterOrDigit).ToArray());

    private void MxLoadCounts()
    {
        _mxCounts.Clear();
        LiteBoxConfig? cfg = null;
        try { cfg = LiteBoxConfig.LoadForExe(); } catch { }
        foreach (var h in _mxCats)
        {
            int def = MxDefaultCount(h);
            int v = def;
            try { if (cfg != null) v = cfg.GetInt(MxCountKey(h), def); } catch { }
            _mxCounts.Add(Math.Max(0, Math.Min(99, v)));
        }
    }

    private int MxCountOf(int col) => col >= 0 && col < _mxCounts.Count ? _mxCounts[col] : 1;

    /// <summary>One spin box per column, floated over the RIGHT of its header. A DataGridView has no
    /// notion of a control in a header, so these are children of the grid itself, kept in place by
    /// <see cref="MxLayoutCountBoxes"/> on every scroll, resize and column-width change.</summary>
    private void MxBuildCountBoxes(DataGridView grid)
    {
        foreach (var n in _mxCountBoxes) { try { grid.Controls.Remove(n); n.Dispose(); } catch { } }
        _mxCountBoxes.Clear();

        for (int i = 0; i < _mxCats.Count; i++)
        {
            int col = i;
            var n = new NumericUpDown
            {
                Minimum = 0, Maximum = 99, Value = MxCountOf(col),
                Width = S(44), Height = S(20),
                BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 8f), TextAlign = HorizontalAlignment.Center,
                Enabled = !_readOnly,
            };
            _tips.SetToolTip(n, "Images to bring in for this column — stand-ins shown and downloaded per game.\n0 = local images only: nothing shown, nothing downloaded.");
            n.ValueChanged += (_, _) =>
            {
                int v = (int)n.Value;
                if (col >= _mxCounts.Count || _mxCounts[col] == v) return;
                _mxCounts[col] = v;
                try { var cfg = LiteBoxConfig.LoadForExe(); cfg.SetInt(MxCountKey(_mxCats[col]), v); cfg.Save(); } catch { }
                MxInvalidateAllRows();   // the stand-ins for that column change (0 wipes them)
            };
            _mxCountBoxes.Add(n);
            grid.Controls.Add(n);
        }
        MxLayoutCountBoxes();
    }

    private void MxLayoutCountBoxes()
    {
        var grid = _mxGrid;
        if (grid == null || !grid.IsHandleCreated) return;
        for (int i = 0; i < _mxCountBoxes.Count; i++)
        {
            var n = _mxCountBoxes[i];
            Rectangle r;
            try { r = grid.GetColumnDisplayRectangle(i + 1, true); } catch { r = Rectangle.Empty; }   // +1: the frozen Game column
            // Width 0 = scrolled out of sight; a sliver too narrow to hold the box is hidden as well,
            // otherwise it would overlap its neighbour's header.
            if (r.Width < n.Width + S(8)) { n.Visible = false; continue; }
            n.Visible = true;
            n.Location = new Point(r.Right - n.Width - S(4), Math.Max(0, (grid.ColumnHeadersHeight - n.Height) / 2));
            n.BringToFront();
        }
    }

    /// <summary>Diagnostic driver (MainWindow's LB_IMGDL_TEST): resolve the ExtendDB stand-in for one
    /// game + regroupement and run the REAL download on it, tracing every gate. Opens no window — a
    /// failure that only happens on one image is far easier to read here than through the matrix.</summary>
    internal static void DiagDownloadSlot(IGame g, string regroupement, Form owner)
    {
        var w = new EditGameWindow(new[] { g }, Array.Empty<IGame>(), false);
        try
        {
            int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
            string plat = Safe(() => g.Platform) ?? "";
            Console.WriteLine($"[imgdl] dbId={dbId} platform={plat}");
            Console.WriteLine($"[imgdl] extendedDb={MetadataDb.ExtendedDbPath ?? "(none)"} moduleActive={MediaApiBridge.ModuleActive}");
            if (dbId <= 0) { Console.WriteLine("[imgdl] no LaunchBox database id — the DB sources have nothing to look up"); return; }

            var all = MetadataDb.ImagesForGame(MetadataDb.ExtendedDbPath, dbId);
            Console.WriteLine($"[imgdl] ExtendDB rows for this game: {all.Count}");
            var types = ImgTypesOf(regroupement);
            var ordered = MxWebSlotOrder(all, types, out int cnt);
            Console.WriteLine($"[imgdl] candidates for \"{regroupement}\": {cnt}");
            foreach (var c in ordered.Take(5))
                Console.WriteLine($"[imgdl]   type={c.Type} region={(string.IsNullOrEmpty(c.Region) ? "(blank)" : c.Region)} origin={c.Origin} isLb={c.IsLaunchbox} file={c.FileName}");
            if (ordered.Count == 0) return;

            w._imgGame = g;   // ImgFetchWebBytes reads the platform off this
            bool ok = w.ImgDownloadOne(g, ordered[0], dbId, plat);
            Console.WriteLine($"[imgdl] RESULT: {(ok ? "downloaded" : "FAILED")}");
        }
        finally { try { w.Dispose(); } catch { } }
    }

    /// <summary>Which type FOLDER a path sits in, among <paramref name="types"/> — the folder itself, or
    /// its parent when the file lives in a region sub-folder. String work only: the matrix reads its
    /// paths from the game cache and must not go back to the disk to learn what they are.</summary>
    private static bool MxPathIsOfType(string path, HashSet<string> types)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir == null) return false;
            if (types.Contains(Path.GetFileName(dir))) return true;
            string? up = Path.GetDirectoryName(dir);
            return up != null && types.Contains(Path.GetFileName(up));
        }
        catch { return false; }
    }

    private MxCell[] MxRow(int row)
    {
        lock (_mxLock) { if (_mxRows.TryGetValue(row, out var cached)) return cached; }

        var g = _editGames[row];
        string plat = Safe(() => g.Platform) ?? "";
        Guid.TryParse(Safe(() => g.Id) ?? "", out var id);
        var cells = new MxCell[_mxCats.Count];

        bool cacheUsable = GameCacheBridge.Ready(plat) && !_imgTouchedPlatforms.Contains(plat);
        List<ImgFile>? scan = cacheUsable ? null : ImgScan(g);   // one disk scan for the whole row

        for (int c = 0; c < _mxCats.Count; c++)
        {
            var (cat, only) = _mxSpec[c];
            if (cacheUsable)
            {
                // Already ordered type → region → number, so [0] IS GetBestImageTypeFirst (the ★★ pick).
                // A split column keeps that order and just drops the types it isn't about, so its own
                // first entry is the ★★ pick for the slice.
                var all = GameCacheBridge.AllImagesTypeFirst(plat, id, cat, 999);
                if (only != null)
                {
                    var keep = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
                    all = all.Where(p => MxPathIsOfType(p, keep)).ToList();
                }
                cells[c] = new MxCell { Path = all.Count > 0 ? all[0] : null, Count = all.Count };
            }
            else
            {
                var types = only ?? ImgTypesOf(cat);
                var ofCat = scan!.Where(f => types.Any(t => string.Equals(t, f.Type, StringComparison.OrdinalIgnoreCase))).ToList();
                cells[c] = new MxCell { Path = ImgLbSlotPick(ofCat, types), Count = ofCat.Count };
            }
        }

        MxApplyStandins(row, cells);   // keep the stand-ins on any recompute (else they'd vanish)
        lock (_mxLock) { _mxRows[row] = cells; }
        return cells;
    }

    /// <summary>
    /// Fills every still-EMPTY cell with a web stand-in, consulting the enabled sources IN THE ORDER the user
    /// turned them on (_mxSourceOrder): the first source that has a candidate for a cell wins it, a later source
    /// only fills what's still empty. So the checkbox click order is the source priority. Re-run on every row
    /// (re)compute — otherwise a row rebuilt after closing the modal would come back with its stand-ins blanked.
    /// </summary>
    private void MxApplyStandins(int row, MxCell[] cells)
    {
        for (int c = 0; c < cells.Length; c++) { cells[c].Web = null; cells[c].WebCount = 0; cells[c].WebSrc = null; }   // reset
        if (_mxSourceOrder.Count == 0 || !cells.Any(c => c.Count == 0)) return;

        var g = _editGames[row];
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        List<MetadataDb.WebImage>? lbList = null;                  // lazily loaded on first "lbdb" use
        List<MetadataDb.WebImage>? webList = null;                 // lazily loaded on first "web" (extended DB) use
        List<EmuMoviesCatalog.EmuMedia>? emuList = null;
        List<MetadataDb.WebImage>? steamList = null;

        // NOTE dedup between the two DB sources is structural here: a later source only ever fills cells the
        // earlier one had ZERO candidates for (per MxWebSlotPick), so no row can appear twice in the grid.
        List<MetadataDb.WebImage> LoadDb(string? db)
        {
            try
            {
                var l = MetadataDb.ImagesForGame(db, dbId);
                if (!MediaApiBridge.Available) l = l.Where(w => w.IsLaunchbox).ToList();   // no per-origin fetcher → CDN-only rows
                return l;
            }
            catch { return new(); }
        }

        foreach (var src in _mxSourceOrder)
        {
            for (int c = 0; c < _mxCats.Count; c++)
            {
                if (cells[c].Count > 0 || cells[c].Web != null) continue;   // owned, or a prior source already stood in
                int budget = MxCountOf(c);
                if (budget <= 0) continue;                                   // "I don't collect these": local only
                var types = _mxSpec[c].Types ?? ImgTypesOf(_mxSpec[c].Cat);   // a split column asks for ITS types only

                // Take the slot's image plus as many runners-up as the column's budget allows.
                void Fill(List<MetadataDb.WebImage> ordered, int cnt, string source)
                {
                    if (ordered.Count == 0) return;
                    cells[c].Web = ordered[0];
                    cells[c].WebCount = cnt;
                    cells[c].WebSrc = source;
                    cells[c].WebMore = budget > 1 && ordered.Count > 1
                        ? ordered.Skip(1).Take(budget - 1).ToList() : null;
                }

                if (src == "lbdb")
                {
                    if (dbId <= 0) continue;
                    lbList ??= LoadDb(MetadataDb.LaunchBoxDbPath());
                    Fill(MxWebSlotOrder(lbList, types, out int cnt), cnt, "lbdb");
                }
                else if (src == "web")
                {
                    if (dbId <= 0) continue;
                    webList ??= LoadDb(MetadataDb.ExtendedDbPath);
                    Fill(MxWebSlotOrder(webList, types, out int cnt), cnt, "web");
                }
                else if (src == "emu")
                {
                    if (emuList == null) { lock (_mxLock) _mxEmuMedia.TryGetValue(row, out emuList); emuList ??= new(); }
                    Fill(MxEmuSlotOrder(emuList, dbId, types, out int cnt), cnt, "emu");
                }
                else // "steam"
                {
                    if (steamList == null) { lock (_mxLock) _mxSteamMedia.TryGetValue(row, out steamList); steamList ??= new(); }
                    Fill(MxWebSlotOrder(steamList, types, out int cnt), cnt, "steam");   // Steam media is already WebImage
                }
            }
        }
    }

    // ── Painting ──────────────────────────────────────────────────────────────
    private void MxPaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 1 || e.RowIndex >= _editGames.Count) return;   // headers + game column: default

        e.PaintBackground(e.CellBounds, true);
        var g = e.Graphics!;
        var cell = MxRow(e.RowIndex)[e.ColumnIndex - 1];

        bool isWeb = cell.Path == null && cell.Web.HasValue;
        string? key = cell.Path ?? (isWeb ? cell.Web!.Value.Key : null);

        if (key == null)
        {
            // Nothing at all — a dash, so an empty cell reads as "missing", not "not loaded yet".
            using var dim = new SolidBrush(Color.FromArgb(80, 80, 92));
            using var f = new Font("Segoe UI", 9f);
            g.DrawString("—", f, dim, e.CellBounds.X + e.CellBounds.Width / 2f - S(5), e.CellBounds.Y + e.CellBounds.Height / 2f - S(9));
            e.Handled = true;
            return;
        }

        var img = MxThumb(key, cell, e.RowIndex, e.ColumnIndex);
        int tw = S(MxThumbW), th = S(MxThumbH);
        var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - tw) / 2, e.CellBounds.Y + (e.CellBounds.Height - th) / 2, tw, th);

        if (img != null)
        {
            // Fit, preserving aspect.
            double scale = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
            int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
            var dst = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            g.DrawImage(img, dst);
            if (isWeb)
            {
                using var pen = new Pen(MxStandinColor(cell.Web!.Value, cell.WebSrc), S(2));
                g.DrawRectangle(pen, Rectangle.Inflate(dst, S(1), S(1)));
            }
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(30, 30, 38));
            g.FillRectangle(b, box);
        }

        // Count badge (bottom-right of the cell).
        int count = isWeb ? cell.WebCount : cell.Count;
        if (count > 0)
        {
            string txt = count.ToString();
            using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            var sz = g.MeasureString(txt, f);
            int bw = (int)Math.Max(sz.Width + S(6), S(16)), bh = S(14);
            var br = new Rectangle(e.CellBounds.Right - bw - S(4), e.CellBounds.Bottom - bh - S(3), bw, bh);
            using var bg = new SolidBrush(isWeb ? MxStandinColor(cell.Web!.Value, cell.WebSrc) : Color.FromArgb(70, 74, 88));
            g.FillRectangle(bg, br);
            using var fg = new SolidBrush(Color.White);
            g.DrawString(txt, f, fg, br.X + (bw - sz.Width) / 2f, br.Y + S(1));
        }

        e.Handled = true;
    }

    /// <summary>Track the rows currently on screen, so queued fetches for rows we've scrolled past get dropped.</summary>
    private void MxUpdateVisible(DataGridView grid)
    {
        try
        {
            int first = Math.Max(0, grid.FirstDisplayedScrollingRowIndex);
            _mxVisFirst = first;
            _mxVisLast = first + Math.Max(1, grid.DisplayedRowCount(true));
        }
        catch { }
    }

    /// <summary>Thumb for a cell. Cached ones come back instantly; the rest are QUEUED (never dropped) and
    /// decoded by a few background workers that always serve the rows nearest the viewport first. Null while
    /// it's on its way.</summary>
    private Image? MxThumb(string key, MxCell cell, int row, int col)
    {
        lock (_mxThumbLock)
        {
            if (_mxThumbs.TryGetValue(key, out var have)) { MxTouch(key); return have; }
        }

        lock (_mxQLock)
        {
            if (_mxPending.ContainsKey(key)) return null;   // already queued
            var job = new MxJob { Key = key, Path = cell.Path, Web = cell.Web, Row = row, Col = col };
            _mxPending[key] = job;
            _mxQueue.Add(job);

            // Keep the queue bounded: shed the entries FARTHEST from what we're looking at. They aren't lost —
            // scrolling back re-queues them, and by then the disk cache usually answers without a download.
            if (_mxQueue.Count > MxQueueMax)
            {
                int centre = (_mxVisFirst + _mxVisLast) / 2;
                _mxQueue.Sort((a, b) => Math.Abs(a.Row - centre).CompareTo(Math.Abs(b.Row - centre)));
                for (int i = _mxQueue.Count - 1; i >= MxQueueMax; i--)
                {
                    _mxPending.Remove(_mxQueue[i].Key);
                    _mxQueue.RemoveAt(i);
                }
            }

            while (_mxWorkers < MxWorkerCount)
            {
                _mxWorkers++;
                System.Threading.Tasks.Task.Run(MxWorkerLoop);
            }
        }
        return null;
    }

    private void MxWorkerLoop()
    {
        while (true)
        {
            MxJob job;
            lock (_mxQLock)
            {
                if (_mxQueue.Count == 0) { _mxWorkers--; return; }

                // Highest priority = nearest the rows on screen, so what you're looking at never waits behind
                // what you've scrolled past (which still loads, just later).
                int centre = (_mxVisFirst + _mxVisLast) / 2;
                int best = 0, bestD = int.MaxValue;
                for (int i = 0; i < _mxQueue.Count; i++)
                {
                    int d = Math.Abs(_mxQueue[i].Row - centre);
                    if (d < bestD) { bestD = d; best = i; }
                }
                job = _mxQueue[best];
                _mxQueue.RemoveAt(best);
                _mxPending.Remove(job.Key);
            }

            Image? thumb = MxDecode(job);

            var grid = _mxGrid;
            if (grid == null || grid.IsDisposed || !grid.IsHandleCreated)
            {
                thumb?.Dispose();
                lock (_mxQLock) { _mxWorkers--; }
                return;
            }
            try
            {
                grid.BeginInvoke(new Action(() =>
                {
                    if (grid.IsDisposed) { thumb?.Dispose(); return; }
                    MxStoreThumb(job.Key, thumb);
                    if (job.Row < grid.RowCount && job.Col < grid.ColumnCount) grid.InvalidateCell(job.Col, job.Row);
                }));
            }
            catch { thumb?.Dispose(); }
        }
    }

    /// <summary>
    /// Produce a cell's thumbnail. A LOCAL image is just decoded from disk. A WEB one has no thumbnail endpoint
    /// — the CDN only serves the full-size file — so the download is expensive and we persist the DOWNSCALED
    /// preview under the shared webimg cache (thumbs\webimg, keyed by WebImage.Key): scrolling back never
    /// re-downloads, and the single-game editor's tiles hit the exact same files (see ImgWebPreviewBytes).
    /// </summary>
    private Image? MxDecode(MxJob job)
    {
        try
        {
            bool isWeb = job.Path == null && job.Web.HasValue;

            if (isWeb)
                return MxScale(ImgWebPreviewBytes(job.Web!.Value));   // ≤360 preview (cache→else fetch+cache) → grid-cell bitmap

            if (job.Path != null && File.Exists(job.Path))
                return MxScale(File.ReadAllBytes(job.Path));   // local: cheap, no disk cache needed
        }
        catch { }
        return null;
    }

    private Image? MxScale(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            int maxW = S(MxThumbW), maxH = S(MxThumbH);
            double sc = Math.Min(1.0, Math.Min((double)maxW / src.Width, (double)maxH / src.Height));
            return new Bitmap(src, Math.Max(1, (int)(src.Width * sc)), Math.Max(1, (int)(src.Height * sc)));
        }
        catch { return null; }
    }

    /// <summary>Insert a decoded thumb, evicting the least-recently-painted ones past the cap.</summary>
    private void MxStoreThumb(string key, Image? thumb)
    {
        lock (_mxThumbLock)
        {
            _mxThumbs[key] = thumb;
            MxTouch(key);

            while (_mxLru.Count > MxThumbCacheMax)
            {
                var oldest = _mxLru.Last;
                if (oldest == null) break;
                _mxLru.RemoveLast();
                _mxLruNodes.Remove(oldest.Value);
                if (_mxThumbs.TryGetValue(oldest.Value, out var img))
                {
                    _mxThumbs.Remove(oldest.Value);
                    try { img?.Dispose(); } catch { }
                }
            }
        }
    }

    /// <summary>Mark a key most-recently-used. Caller holds _mxThumbLock.</summary>
    private void MxTouch(string key)
    {
        if (_mxLruNodes.TryGetValue(key, out var node)) { _mxLru.Remove(node); _mxLru.AddFirst(node); }
        else _mxLruNodes[key] = _mxLru.AddFirst(key);
    }

    // ── "Fill the gaps" from a DATABASE source (LaunchBox DB or ExtendDB): explicit batch, progress modal ─────
    private void MxFillDb(CheckBox chk, string src)
    {
        bool avail = src == "lbdb" ? MetadataDb.LaunchBoxDbPath() != null : MediaApiBridge.ModuleActive;
        if (!avail)
        {
            MessageBox.Show(this, src == "lbdb" ? "The LaunchBox metadata database isn't available."
                                                : "The extended database isn't available.",
                "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            chk.Checked = false;
            return;
        }

        using var dlg = NewDialog("Fetching web images…", 420, 150);
        var lbl = new Label { Text = "Preparing…", ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(380), S(20)) };
        var pb = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(380), S(18)), Minimum = 0, Maximum = Math.Max(1, _editGames.Count) };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(300), S(72), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);

        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => cts.Cancel();
        dlg.FormClosing += (_, _) => cts.Cancel();

        lock (_mxLock) _mxRows.Clear();   // rows recompute with the stand-ins (MxApplyStandins; the handler set the flag)

        int filled = 0;
        System.Threading.Tasks.Task.Run(() =>
        {
            for (int row = 0; row < _editGames.Count; row++)
            {
                if (cts.IsCancellationRequested) break;
                var cells = MxRow(row);                       // computes + applies web
                filled += cells.Count(c => c.Web.HasValue);

                int done = row + 1, f = filled;
                try
                {
                    if (!dlg.IsDisposed && dlg.IsHandleCreated)
                        dlg.BeginInvoke(new Action(() =>
                        {
                            if (dlg.IsDisposed) return;
                            pb.Value = Math.Min(pb.Maximum, done);
                            lbl.Text = $"{done} / {_editGames.Count} games · {f} gap(s) filled";
                        }));
                }
                catch { }
            }
            try
            {
                if (!dlg.IsDisposed && dlg.IsHandleCreated)
                    dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) dlg.Close(); }));
            }
            catch { }
        }, cts.Token);

        dlg.ShowDialog(this);
        cts.Cancel();

        MxSetStatus($"{_editGames.Count} games × {_mxCats.Count} categories · {filled} gap(s) fillable from the database");
        _mxGrid?.Invalidate();   // thumbs stream in as they download
    }

    private void MxInvalidateAllRows()
    {
        lock (_mxLock) _mxRows.Clear();
        _mxGrid?.Invalidate();
    }

    private void MxInvalidateRow(int row)
    {
        lock (_mxLock) _mxRows.Remove(row);
        _mxGrid?.InvalidateRow(row);
    }

    // ── Right-click a cell: download straight from the grid ───────────────────
    private void MxCellMenu(DataGridView grid, int row, int col)
    {
        if (row < 0 || col < 1 || row >= _editGames.Count) return;
        var cell = MxRow(row)[col - 1];

        var m = ThemedMenu();
        if (cell.Path != null || cell.Web.HasValue)
            m.Items.Add(new ToolStripMenuItem("🔍  View fullscreen").WithClick(() => MxViewCell(row, col)));
        if (cell.Web.HasValue && !_readOnly)
            m.Items.Add(new ToolStripMenuItem($"⬇  Download this image  ({cell.WebCount} available)")
                .WithClick(() => MxDownloadCell(row, col)));
        m.Items.Add(new ToolStripMenuItem("🗂  Open this category…").WithClick(() => MxOpenCell(row, col)));
        m.Show(grid, grid.PointToClient(Cursor.Position));
    }

    /// <summary>Preview the cell's image fullscreen — the local ★★ pick, or the web/EmuMovies stand-in.</summary>
    private void MxViewCell(int row, int col)
    {
        var cell = MxRow(row)[col - 1];
        if (cell.Path != null) { ShowImageFullscreenPath(cell.Path); return; }
        if (!cell.Web.HasValue) return;
        var prev = _imgGame; _imgGame = _editGames[row];   // ImgFetchWebBytes needs the right game's platform
        try { ShowImageFullscreenWeb(cell.Web.Value); } finally { _imgGame = prev; }
    }

    /// <summary>Download the web stand-in of one cell, in place, without opening the modal.</summary>
    private void MxDownloadCell(int row, int col)
    {
        var cell = MxRow(row)[col - 1];
        if (!cell.Web.HasValue || _readOnly) return;

        var prev = _imgGame;
        _imgGame = _editGames[row];   // ImgDownloadWebList operates on ImgGame
        try { ImgDownloadWebList(new List<MetadataDb.WebImage> { cell.Web.Value }); }
        finally { _imgGame = prev; }

        MxInvalidateRow(row);   // the platform is now "touched" → the row re-reads the DISK, not the stale cache
    }

    // ── Download EVERY missing cell across the whole selection ────────────────
    private void MxDownloadAllMissing()
    {
        if (_readOnly) return;
        if (_mxSourceOrder.Count == 0)
        {
            MessageBox.Show(this, "Turn on a source first (LaunchBox DB, ExtendDB, EmuMovies or Steam) — those are the stand-ins that get downloaded.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Download the stand-ins currently filling the gaps — whatever source won each cell (purple or blue),
        // per the enabled sources and their order.
        // One job per image: the slot's stand-in, plus the runners-up a column whose budget is above one
        // asked to bring in. A column at zero contributed no stand-in at all, so it adds nothing here.
        var jobs = new List<(int row, MetadataDb.WebImage web)>();
        for (int row = 0; row < _editGames.Count; row++)
            foreach (var c in MxRow(row))
            {
                if (c.Count != 0 || !c.Web.HasValue) continue;
                jobs.Add((row, c.Web.Value));
                if (c.WebMore != null) foreach (var extra in c.WebMore) jobs.Add((row, extra));
            }

        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "Nothing to download — no empty category has a stand-in from the enabled source(s).", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int games = jobs.Select(j => j.row).Distinct().Count();
        int emuN = jobs.Count(j => string.Equals(j.web.Origin, "emumovies", StringComparison.OrdinalIgnoreCase));
        string mix = emuN == 0 ? "" : emuN == jobs.Count ? " (all EmuMovies)" : $" ({jobs.Count - emuN} database, {emuN} EmuMovies)";
        if (MessageBox.Show(this, $"Download {jobs.Count} image(s){mix} across {games} game(s)?\n\nPer empty category, as many as that column's number allows — starting with the one LaunchBox would use for the slot.",
                "Download all missing", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            return;

        using var dlg = NewDialog("Downloading…", 420, 150);
        var lbl = new Label { Text = "Starting…", ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(380), S(20)) };
        var pb = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(380), S(18)), Minimum = 0, Maximum = jobs.Count };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(300), S(72), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);

        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => cts.Cancel();
        dlg.FormClosing += (_, _) => cts.Cancel();

        int ok = 0, fail = 0;
        // ImgDownloadOne is the LOW-LEVEL write (no ImgAfterOp), so it does NOT mark the platform dirty.
        // Collect them here and merge on the UI thread — _imgTouchedPlatforms is read by MxRow while the grid
        // repaints behind this modal, so it must not be mutated from the worker.
        var touchedPlats = new List<string>();

        System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (cts.IsCancellationRequested) break;
                var (row, web) = jobs[i];
                var g = _editGames[row];
                string plat = Safe(() => g.Platform) ?? "";
                int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;

                if (ImgDownloadOne(g, web, dbId, plat))
                {
                    ok++;
                    if (!string.IsNullOrEmpty(plat) && !touchedPlats.Contains(plat)) touchedPlats.Add(plat);
                }
                else fail++;

                int done = i + 1, o = ok, f = fail;
                try
                {
                    if (!dlg.IsDisposed && dlg.IsHandleCreated)
                        dlg.BeginInvoke(new Action(() =>
                        {
                            if (dlg.IsDisposed) return;
                            pb.Value = Math.Min(pb.Maximum, done);
                            lbl.Text = $"{done} / {jobs.Count} · {o} downloaded" + (f > 0 ? $", {f} failed" : "");
                        }));
                }
                catch { }
            }
            try
            {
                if (!dlg.IsDisposed && dlg.IsHandleCreated)
                    dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) dlg.Close(); }));
            }
            catch { }
        }, cts.Token);

        dlg.ShowDialog(this);
        cts.Cancel();

        // Mark the platforms dirty (UI thread): triggers the deferred GameCache rebuild on close AND makes
        // MxRow read the disk instead of the now-stale cache.
        foreach (var p in touchedPlats) _imgTouchedPlatforms.Add(p);

        MxInvalidateAllRows();   // touched platforms → rows now re-read the disk
        MxSetStatus($"{ok} image(s) downloaded" + (fail > 0 ? $", {fail} failed" : ""));
        MessageBox.Show(this, $"Downloaded {ok} image(s)." + (fail > 0 ? $"\n{fail} failed." : ""),
            "LiteBox", MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>The web image LaunchBox would use for a slot: first TYPE of the regroupement that has a web
    /// candidate (type is the dominant axis), then the canonical region order. Also reports how many web
    /// candidates the whole category has.</summary>
    private static MetadataDb.WebImage? MxWebSlotPick(List<MetadataDb.WebImage> web, List<string> types, out int count)
    {
        var ordered = MxWebSlotOrder(web, types, out count);
        return ordered.Count > 0 ? ordered[0] : null;
    }

    /// <summary>The category's web candidates in the order LaunchBox would pick them (type priority, then
    /// region priority) — [0] is the slot's image, the rest are the runners-up a column with a budget
    /// above one downloads next.</summary>
    private static List<MetadataDb.WebImage> MxWebSlotOrder(List<MetadataDb.WebImage> web, List<string> types, out int count)
    {
        var typeSet = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        var ofCat = web.Where(w => typeSet.Contains(w.Type)).ToList();
        count = ofCat.Count;
        var res = new List<MetadataDb.WebImage>(ofCat.Count);
        if (ofCat.Count == 0) return res;

        var order = LbRegions.Order(LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities());
        foreach (var type in types)   // regroupement priority order
        {
            var ofType = ofCat.Where(w => string.Equals(w.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ofType.Count == 0) continue;
            foreach (var region in order)
                foreach (var w in ofType)
                {
                    // GamesDb: a blank DB region IS "World" (not the root). Mapping it to "none" would rank it
                    // dead last instead of at World's place in the priority list.
                    var reg = string.IsNullOrEmpty(w.Region) ? "World" : w.Region;
                    if (string.Equals(reg, region, StringComparison.OrdinalIgnoreCase)) res.Add(w);
                }
            foreach (var w in ofType) if (!res.Contains(w)) res.Add(w);   // regions outside the order, still this type
        }
        foreach (var w in ofCat) if (!res.Contains(w)) res.Add(w);        // types outside the priority list
        return res;
    }

    /// <summary>The EmuMovies image LaunchBox would use for a slot (same type-then-region rule as the database
    /// pick), returned as a WebImage stand-in (origin=emumovies, FileName=the full media URL) so it flows
    /// through the same paint / fetch / download path as the purple one.</summary>
    private MetadataDb.WebImage? MxEmuSlotPick(List<EmuMoviesCatalog.EmuMedia> emu, int dbId, List<string> types, out int count)
    {
        var ordered = MxEmuSlotOrder(emu, dbId, types, out count);
        return ordered.Count > 0 ? ordered[0] : null;
    }

    /// <summary>The EmuMovies candidates for the slot, best first — same type-then-region rule as the
    /// database sources, listed rather than reduced so a column budget above one has runners-up.</summary>
    private List<MetadataDb.WebImage> MxEmuSlotOrder(List<EmuMoviesCatalog.EmuMedia> emu, int dbId, List<string> types, out int count)
    {
        var typeSet = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        var ofCat = emu.Where(m => typeSet.Contains(m.LbType)).ToList();
        count = ofCat.Count;
        var res = new List<MetadataDb.WebImage>(ofCat.Count);
        if (ofCat.Count == 0) return res;

        var order = LbRegions.Order(LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities());
        var taken = new List<EmuMoviesCatalog.EmuMedia>();
        void Take(EmuMoviesCatalog.EmuMedia m)
        {
            if (taken.Contains(m)) return;
            taken.Add(m);
            res.Add(new MetadataDb.WebImage(dbId, m.Url, m.LbType, m.Region, m.Crc, "emumovies", 0, m.Ext, m.FileSize));
        }
        foreach (var type in types)
        {
            var ofType = ofCat.Where(m => string.Equals(m.LbType, type, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ofType.Count == 0) continue;
            foreach (var region in order)
                foreach (var m in ofType)
                    if (string.Equals(string.IsNullOrEmpty(m.Region) ? "World" : m.Region, region, StringComparison.OrdinalIgnoreCase)) Take(m);
            foreach (var m in ofType) Take(m);
        }
        foreach (var m in ofCat) Take(m);
        return res;
    }

    // ── "Show EmuMovies images": resolve each game live, then fill gaps ─────────
    private void MxFillEmu(CheckBox chk)
    {
        var api = EmuApi();
        if (api == null)
        {
            MessageBox.Show(this, "EmuMovies credentials aren't configured.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            chk.Checked = false;
            return;
        }
        MxFillBatch("Querying EmuMovies…", "EmuMovies", (row, g, dbId, ct) =>
        {
            string plat = Safe(() => g.Platform) ?? "";
            List<EmuMoviesCatalog.EmuMedia> media = new();
            if (EmuMoviesCatalog.SupportsPlatform(plat))
            {
                try { media = EmuMoviesCatalog.ResolveForGameAsync(api, Safe(() => g.Title) ?? "", Safe(() => g.ApplicationPath) ?? "", plat, ct).GetAwaiter().GetResult(); }
                catch { }
            }
            lock (_mxLock) _mxEmuMedia[row] = media;
            return media.Count;
        });
    }

    // ── "Show Steam images": one appdetails call per Steam game, then fill gaps ──
    private void MxFillSteam(CheckBox chk)
    {
        MxFillBatch("Querying Steam…", "Steam", (row, g, dbId, ct) =>
        {
            List<MetadataDb.WebImage> media = new();
            try { media = SteamCatalog.ResolveForGameAsync(dbId, Safe(() => g.ApplicationPath) ?? "", ct).GetAwaiter().GetResult(); }
            catch { }
            lock (_mxLock) _mxSteamMedia[row] = media;
            return media.Count;
        });
    }

    // Shared "resolve one live source per game, fill gaps" batch (EmuMovies / Steam). perGame stores its stand-ins
    // and returns how many it found. Progress shows the game being queried BEFORE the call — so a slow / retrying
    // source (Steam's rate limit) advances visibly instead of sitting on "Preparing…".
    private void MxFillBatch(string title, string source, Func<int, IGame, int, CancellationToken, int> perGame)
    {
        using var dlg = NewDialog(title, 420, 150);
        var lbl = new Label { Text = "Preparing…", ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(380), S(20)) };
        var pb = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(380), S(18)), Minimum = 0, Maximum = Math.Max(1, _editGames.Count) };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(300), S(72), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);

        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => cts.Cancel();
        dlg.FormClosing += (_, _) => cts.Cancel();

        void Ui(Action a) { try { if (!dlg.IsDisposed && dlg.IsHandleCreated) dlg.BeginInvoke(a); } catch { } }

        int matched = 0;
        // Start on Shown so the handle exists — otherwise the very first (pre-call) update is dropped and the
        // dialog sits on "Preparing…" for the whole first, possibly slow/retrying, query.
        dlg.Shown += (_, _) => System.Threading.Tasks.Task.Run(() =>
        {
            for (int row = 0; row < _editGames.Count; row++)
            {
                if (cts.IsCancellationRequested) break;
                var g = _editGames[row];
                int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
                int cur = row + 1; string gt = Safe(() => g.Title) ?? $"game {cur}";
                Ui(() => { if (!dlg.IsDisposed) { pb.Value = Math.Min(pb.Maximum, cur - 1); lbl.Text = $"{cur} / {_editGames.Count} · {gt}"; } });
                int n = 0; try { n = perGame(row, g, dbId, cts.Token); } catch { }
                lock (_mxLock) _mxRows.Remove(row);   // recompute with the new stand-ins
                if (n > 0) matched++;
                int mm = matched;
                Ui(() => { if (!dlg.IsDisposed) { pb.Value = Math.Min(pb.Maximum, cur); lbl.Text = $"{cur} / {_editGames.Count} games · {source} matched {mm}"; } });
            }
            Ui(() => { if (!dlg.IsDisposed) dlg.Close(); });
        }, cts.Token);

        dlg.ShowDialog(this);
        cts.Cancel();
        MxSetStatus($"{_editGames.Count} games × {_mxCats.Count} categories · {source} matched {matched} game(s)");
        _mxGrid?.Invalidate();
    }

    // ── Click a cell → that (game, category) in a modal, reusing the real editor ─
    //
    // The category editor rebuilds itself through ImgAfterOp, which normally targets the TREE's selected node.
    // Inside the modal that node is "Images" (the matrix), so we park the modal's host panel + category here
    // and ImgAfterOp refreshes THAT instead (see ImgModalRefresh / the hook in ImgAfterOp).
    private Panel? _imgModalHolder;
    private string? _imgModalCat;

    private void ImgModalRefresh()
    {
        if (_imgModalHolder == null || _imgModalCat == null) return;
        foreach (Control c in _imgModalHolder.Controls) ImgDisposePics(c);
        _imgModalHolder.Controls.Clear();
        var page = BuildImageCategoryPage(_imgModalCat);
        page.Dock = DockStyle.Fill;
        _imgModalHolder.Controls.Add(page);
    }

    private void MxOpenCell(int row, int col)
    {
        if (row < 0 || col < 1 || row >= _editGames.Count) return;
        string cat = _mxSpec[col - 1].Cat;   // a split column opens its FAMILY's editor, all types shown
        var game = _editGames[row];
        var cell = MxRow(row)[col - 1];

        var prevGame = _imgGame;
        _imgGame = game;   // every Img* helper now operates on THIS game
        // The single-game tree persists its source toggles + check-order across categories; the modal mirrors the
        // GRID instead (below), so snapshot the tree state and restore it after — the modal must not pollute it.
        bool prevLbDb = _imgShowLbDb, prevWeb = _imgShowWeb, prevEmu = _imgShowEmu, prevSteam = _imgShowSteam;
        var prevOrder = new List<string>(_imgSourceOrder);
        // Open the category page mirroring the GRID's enabled sources, so the same orange/purple/blue stand-ins
        // you see in the grid are there in the modal (and the chips match). A clicked stand-in cell also forces
        // its own source on (redundant — the grid had it on to show the cell — but explicit).
        string cellSrc = cell.Web.HasValue ? (cell.WebSrc ?? "") : "";
        bool cellIsEmu = cellSrc == "emu";
        bool cellIsSteam = cellSrc == "steam";
        bool cellIsLbDb = cellSrc == "lbdb";
        bool cellIsWeb = cell.Web.HasValue && !cellIsEmu && !cellIsSteam && !cellIsLbDb;
        _imgOpenWithLbDb = _mxShowLbDb || cellIsLbDb;
        _imgOpenWithWeb = _mxShowWeb || cellIsWeb;
        _imgOpenWithEmu = _mxShowEmu || cellIsEmu;
        _imgOpenWithSteam = _mxShowSteam || cellIsSteam;

        // Hand the modal the media the grid ALREADY resolved for this game, so its category page shows it
        // instantly instead of re-querying. Keyed the same way the single-game sections key their caches.
        string gk = Safe(() => game.Id) ?? Safe(() => game.Title) ?? "";
        lock (_mxLock)
        {
            if (_imgOpenWithEmu && _mxEmuMedia.TryGetValue(row, out var em)) _imgEmuCache[gk] = em;
            if (_imgOpenWithSteam && _mxSteamMedia.TryGetValue(row, out var sm)) _imgSteamCache["steam:" + gk] = sm;
        }
        try
        {
            using var dlg = NewDialog($"{Safe(() => game.Title)} — {cat}", 940, 660);
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = true;

            var holder = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = Bg };
            var close = DlgBtn("Close", Color.FromArgb(45, 95, 60)); close.AutoSize = false; close.SetBounds(S(12), S(7), S(100), S(30));
            close.Click += (_, _) => dlg.Close();
            bottom.Controls.Add(close);
            dlg.Controls.Add(holder);   // Fill first …
            dlg.Controls.Add(bottom);   // … Bottom last

            _imgModalHolder = holder;
            _imgModalCat = cat;
            ImgModalRefresh();          // builds the full category editor: move/copy/delete/web/download
            dlg.ShowDialog(this);
        }
        finally
        {
            _imgModalHolder = null;
            _imgModalCat = null;
            _imgOpenWithLbDb = false;
            _imgOpenWithWeb = false;
            _imgOpenWithEmu = false;
            _imgOpenWithSteam = false;
            _imgShowLbDb = prevLbDb; _imgShowWeb = prevWeb; _imgShowEmu = prevEmu; _imgShowSteam = prevSteam;
            _imgSourceOrder.Clear(); _imgSourceOrder.AddRange(prevOrder);
            _imgGame = prevGame;
            _pages.Remove(cat);      // that page was built for another game — never cache it
        }

        // The modal may have added / moved / deleted files: recompute that row (and MxApplyWeb re-attaches the
        // purple stand-ins, so a cell you merely opened and cancelled doesn't come back blank).
        MxInvalidateRow(row);
    }
}
