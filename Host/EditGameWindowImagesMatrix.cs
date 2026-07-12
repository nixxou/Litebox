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
// "Show web images" is an explicit, batched action with a progress modal: for every EMPTY cell it looks up the
// offline metadata DB, picks the web image LaunchBox would use for that slot, downloads its thumbnail and
// paints the cell with a PURPLE border (= not owned). Clicking any cell opens that (game, category) in a modal
// reusing the normal category editor, so move/copy/delete/download all work there.

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

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private struct MxCell
    {
        public string? Path;                 // local ★★ pick (null = none locally)
        public int Count;                    // local images in the category
        public MetadataDb.WebImage? Web;     // web stand-in when the cell is empty and web is on
        public int WebCount;                 // web candidates available for the category
    }

    private DataGridView? _mxGrid;
    private List<string> _mxCats = new();
    private readonly object _mxLock = new();                             // _mxRows is touched from the UI thread (paint) AND the batch thread
    private readonly Dictionary<int, MxCell[]> _mxRows = new();          // row → cells (computed on demand)
    private readonly Dictionary<string, Image?> _mxThumbs = new(StringComparer.OrdinalIgnoreCase);  // key → thumb (null = failed)
    private readonly HashSet<string> _mxThumbLoading = new(StringComparer.OrdinalIgnoreCase);
    private bool _mxShowWeb;
    private Label? _mxStatus;

    // 3x taller rows: box art is portrait, so give height priority and keep the column narrow enough that 12
    // of them stay scannable. Landscape art (screenshots/backgrounds) aspect-fits inside the same box.
    private const int MxThumbW = 116, MxThumbH = 150, MxRowH = 174;
    private const int MxColW = 130;
    private static readonly Color MxWebColor = Color.FromArgb(150, 90, 200);   // purple = not owned (web)

    // ── Page ──────────────────────────────────────────────────────────────────
    private Control BuildImagesMatrixPage()
    {
        _mxCats = ImgRegroupements().ToList();
        _mxRows.Clear();

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        var bar = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Bg };
        var chkWeb = new CheckBox
        {
            Text = "Show web images (fill the gaps)", AutoSize = true, ForeColor = Color.FromArgb(190, 150, 230),
            BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Location = new Point(S(4), S(10)), Checked = false,
        };
        var btnAll = DlgBtn("⬇  Download all missing", Color.FromArgb(78, 52, 120));
        btnAll.AutoSize = false; btnAll.SetBounds(S(250), S(5), S(170), S(28)); btnAll.Enabled = !_readOnly;
        btnAll.Click += (_, _) => MxDownloadAllMissing();

        _mxStatus = new Label
        {
            Text = $"{_editGames.Count} games × {_mxCats.Count} categories", ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(S(432), S(12)),
        };
        bar.Controls.Add(chkWeb);
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

        grid.RowCount = _editGames.Count;
        _mxGrid = grid;

        root.Controls.Add(grid);   // Fill first …
        root.Controls.Add(bar);    // … Top last

        chkWeb.CheckedChanged += (_, _) =>
        {
            if (chkWeb.Checked) MxFillWeb(chkWeb);
            else
            {
                _mxShowWeb = false;
                MxInvalidateAllRows();   // recompute without the web stand-ins
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
            string cat = _mxCats[c];
            if (cacheUsable)
            {
                // Already ordered type → region → number, so [0] IS GetBestImageTypeFirst (the ★★ pick).
                var all = GameCacheBridge.AllImagesTypeFirst(plat, id, cat, 999);
                cells[c] = new MxCell { Path = all.Count > 0 ? all[0] : null, Count = all.Count };
            }
            else
            {
                var types = ImgTypesOf(cat);
                var ofCat = scan!.Where(f => types.Any(t => string.Equals(t, f.Type, StringComparison.OrdinalIgnoreCase))).ToList();
                cells[c] = new MxCell { Path = ImgLbSlotPick(ofCat, types), Count = ofCat.Count };
            }
        }

        MxApplyWeb(row, cells);   // keep the purple stand-ins on any recompute (else they'd vanish)
        lock (_mxLock) { _mxRows[row] = cells; }
        return cells;
    }

    /// <summary>
    /// Fills every still-EMPTY cell of a row with the web image LaunchBox would use for that slot. Called on
    /// every row (re)compute while the toggle is on — otherwise a row recomputed after closing the modal (even
    /// with Cancel) would come back with its purple cells blanked.
    /// </summary>
    private void MxApplyWeb(int row, MxCell[] cells)
    {
        if (!_mxShowWeb) return;
        var g = _editGames[row];
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        if (dbId <= 0) return;
        if (!cells.Any(c => c.Count == 0)) return;

        List<MetadataDb.WebImage> web;
        try { web = MetadataDb.ImagesForGame(dbId); } catch { return; }
        if (!MediaApiBridge.Available) web = web.Where(w => w.IsLaunchbox).ToList();

        for (int c = 0; c < _mxCats.Count; c++)
        {
            if (cells[c].Count > 0) { cells[c].Web = null; cells[c].WebCount = 0; continue; }
            var pick = MxWebSlotPick(web, ImgTypesOf(_mxCats[c]), out int cnt);
            cells[c].Web = pick;
            cells[c].WebCount = cnt;
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
                using var pen = new Pen(MxWebColor, S(2));
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
            using var bg = new SolidBrush(isWeb ? MxWebColor : Color.FromArgb(70, 74, 88));
            g.FillRectangle(bg, br);
            using var fg = new SolidBrush(Color.White);
            g.DrawString(txt, f, fg, br.X + (bw - sz.Width) / 2f, br.Y + S(1));
        }

        e.Handled = true;
    }

    /// <summary>Thumb for a cell, decoding it on a background thread the first time (the grid repaints the
    /// cell when it lands). Local images are read from disk; web ones are fetched through the normal
    /// per-origin fetcher. Null while loading / on failure.</summary>
    private Image? MxThumb(string key, MxCell cell, int row, int col)
    {
        if (_mxThumbs.TryGetValue(key, out var have)) return have;
        if (!_mxThumbLoading.Add(key)) return null;

        bool isWeb = cell.Path == null && cell.Web.HasValue;
        var web = cell.Web;
        string? path = cell.Path;

        System.Threading.Tasks.Task.Run(() =>
        {
            Image? thumb = null;
            try
            {
                byte[]? bytes = isWeb ? ImgFetchWebBytes(web!.Value)
                                      : (path != null && File.Exists(path) ? File.ReadAllBytes(path) : null);
                if (bytes != null && bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    using var src = Image.FromStream(ms);
                    int maxW = S(MxThumbW) * 2, maxH = S(MxThumbH) * 2;   // 2× for crisp scaling
                    double sc = Math.Min(1.0, Math.Min((double)maxW / src.Width, (double)maxH / src.Height));
                    thumb = new Bitmap(src, Math.Max(1, (int)(src.Width * sc)), Math.Max(1, (int)(src.Height * sc)));
                }
            }
            catch { thumb = null; }

            try
            {
                var grid = _mxGrid;
                if (grid == null || grid.IsDisposed || !grid.IsHandleCreated) { thumb?.Dispose(); return; }
                grid.BeginInvoke(new Action(() =>
                {
                    if (grid.IsDisposed) { thumb?.Dispose(); return; }
                    _mxThumbs[key] = thumb;
                    if (row < grid.RowCount && col < grid.ColumnCount) grid.InvalidateCell(col, row);
                }));
            }
            catch { thumb?.Dispose(); }
        });
        return null;
    }

    // ── "Show web images": explicit batch, with a progress modal ───────────────
    private void MxFillWeb(CheckBox chk)
    {
        if (!MetadataDb.Available)
        {
            MessageBox.Show(this, "The LaunchBox metadata database isn't available.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        _mxShowWeb = true;
        lock (_mxLock) _mxRows.Clear();   // rows recompute with the web stand-ins (MxApplyWeb)

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
        if (cell.Web.HasValue && !_readOnly)
            m.Items.Add(new ToolStripMenuItem($"⬇  Download this image  ({cell.WebCount} available)")
                .WithClick(() => MxDownloadCell(row, col)));
        m.Items.Add(new ToolStripMenuItem("🔍  Open this category…").WithClick(() => MxOpenCell(row, col)));
        m.Show(grid, grid.PointToClient(Cursor.Position));
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
        if (!MetadataDb.Available)
        {
            MessageBox.Show(this, "The LaunchBox metadata database isn't available.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Web stand-ins are what we download; make sure they're computed even if the toggle is off.
        bool wasShowing = _mxShowWeb;
        if (!_mxShowWeb) { _mxShowWeb = true; lock (_mxLock) _mxRows.Clear(); }

        var jobs = new List<(int row, MetadataDb.WebImage web)>();
        for (int row = 0; row < _editGames.Count; row++)
            foreach (var c in MxRow(row))
                if (c.Count == 0 && c.Web.HasValue) jobs.Add((row, c.Web.Value));

        if (jobs.Count == 0)
        {
            _mxShowWeb = wasShowing;
            MessageBox.Show(this, "Nothing to download — no empty category has a database image.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int games = jobs.Select(j => j.row).Distinct().Count();
        if (MessageBox.Show(this, $"Download {jobs.Count} image(s) across {games} game(s)?\n\nOne image per empty category — the one LaunchBox would use for that slot.",
                "Download all missing", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
        {
            _mxShowWeb = wasShowing;
            return;
        }

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

        _mxShowWeb = wasShowing;
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
        var typeSet = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        var ofCat = web.Where(w => typeSet.Contains(w.Type)).ToList();
        count = ofCat.Count;
        if (ofCat.Count == 0) return null;

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
                    if (string.Equals(reg, region, StringComparison.OrdinalIgnoreCase)) return w;
                }
            return ofType[0];   // its region isn't in the order at all — still the winning type
        }
        return ofCat[0];
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
        string cat = _mxCats[col - 1];
        var game = _editGames[row];
        var cell = MxRow(row)[col - 1];

        var prevGame = _imgGame;
        _imgGame = game;   // every Img* helper now operates on THIS game
        // Clicking a PURPLE cell means "there's nothing local here" — open the page with the web toggle already
        // on, otherwise it would show an empty category.
        _imgOpenWithWeb = cell.Path == null && cell.Web.HasValue;
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
            _imgOpenWithWeb = false;
            _imgGame = prevGame;
            _pages.Remove(cat);      // that page was built for another game — never cache it
        }

        // The modal may have added / moved / deleted files: recompute that row (and MxApplyWeb re-attaches the
        // purple stand-ins, so a cell you merely opened and cancelled doesn't come back blank).
        MxInvalidateRow(row);
    }
}
