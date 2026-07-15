// Edit Game (MULTI-selection) → Media → Videos: the video-coverage MATRIX — the video twin of the image
// matrix. One row per game, one column per LaunchBox video TYPE (Video Snap, Trailer, Theme Video, Recording,
// Marquee). Each cell shows the ★★ video for that (game, type) as a still frame (VideoThumbnailer, 20% in) plus
// a count badge.
//
// Gap filling works like the image matrix, with two ORDERED sources: "Show web videos" (purple, the extended
// DB) and "Show EmuMovies videos" (blue). Because the web sources only carry a generic "Video" (gameplay) and
// "VideoAdvert", a stand-in only ever lands in the "Video Snap" column ("Video") or "Trailer" ("VideoAdvert");
// the other three columns show owned coverage only. Whichever source you enable first fills the gaps first.
//
// The download path is the VIDEO one, NOT the image one: a database video goes through MediaApi.FetchForWizard
// (Steam fake-mp4 / HLS aware — an HLS-only trailer is stream-only and can't be saved), an EmuMovies video is a
// plain GET. Frames for stand-ins are decoded from the playable URL (MediaApiBridge.ListUrls → the real .m3u8
// for Steam) — expensive, so bounded workers + VideoThumbnailer's own disk cache, like the image matrix.

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
using LbApiHost.Host.Video;
using LbApiHost.Host.Integrations;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private struct MvCell
    {
        public string? Path;                 // local ★★ video (null = none locally)
        public int Count;                    // local videos in the column's type
        public MetadataDb.WebImage? Web;     // web/EmuMovies stand-in when the cell is empty and a source is on
    }

    private DataGridView? _mvGrid;
    private List<string> _mvCols = new();                                 // the video-type columns
    private readonly object _mvLock = new();
    private readonly Dictionary<int, MvCell[]> _mvRows = new();

    private bool _mvShowWeb, _mvShowEmu, _mvShowSteam, _mvShowYt;
    private readonly List<string> _mvSourceOrder = new();                 // "web" / "emu" / "steam" / "yt" — enable order = fill priority
    private readonly Dictionary<int, List<MetadataDb.WebImage>> _mvWebVideos = new();       // row → DB videos
    private readonly Dictionary<int, List<EmuMoviesCatalog.EmuMedia>> _mvEmuVideos = new();  // row → EmuMovies videos
    private readonly Dictionary<int, List<MetadataDb.WebImage>> _mvSteamVideos = new();      // row → Steam videos
    private readonly Dictionary<int, List<MetadataDb.WebImage>> _mvYtVideos = new();         // row → YouTube stand-in (first downloadable)
    private Label? _mvStatus;

    // Thumbnails: a web-video frame is a network fetch + VLC decode — heavy — so cache (LRU) and queue (bounded,
    // visible-first) exactly like the image matrix. VideoThumbnailer serializes decodes and disk-caches them, so
    // few workers suffice and scrolling back never re-decodes.
    private const int MvThumbCacheMax = 400;
    private const int MvQueueMax = 2000;
    private const int MvWorkerCount = 2;

    private readonly object _mvThumbLock = new();
    private readonly Dictionary<string, Image?> _mvThumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _mvLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _mvLruNodes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class MvJob { public string Key = ""; public string? Path; public MetadataDb.WebImage? Web; public int Row, Col; }
    private readonly object _mvQLock = new();
    private readonly Dictionary<string, MvJob> _mvPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MvJob> _mvQueue = new();
    private int _mvWorkers;
    private volatile int _mvVisFirst, _mvVisLast;

    private const int MvThumbW = 150, MvThumbH = 96, MvRowH = 116;
    private const int MvColW = 168;
    private static readonly Color MvWebColor = Color.FromArgb(150, 90, 200);   // purple = database stand-in
    private static readonly Color MvEmuColor = Color.FromArgb(90, 150, 220);   // blue = EmuMovies stand-in
    private static readonly Color MvSteamColor = Color.FromArgb(92, 172, 96);  // green = Steam stand-in
    private static readonly Color MvYtColor = Color.FromArgb(210, 66, 60);     // red = YouTube stand-in
    private static Color MvStandinColor(MetadataDb.WebImage w)
        => string.Equals(w.Origin, "lb-emumovies", StringComparison.OrdinalIgnoreCase) ? MvEmuColor
         : string.Equals(w.Origin, "lb-steam", StringComparison.OrdinalIgnoreCase) ? MvSteamColor
         : string.Equals(w.Origin, "youtube", StringComparison.OrdinalIgnoreCase) ? MvYtColor
         : MvWebColor;

    /// <summary>Which matrix column a web/EmuMovies video's LbType lands in (null = not placeable here).</summary>
    private static string? MvColumnFor(string lbType) => lbType switch
    {
        "Video" => "Video Snap",
        "VideoAdvert" => "Trailer",
        _ => null,
    };

    // ── Page ──────────────────────────────────────────────────────────────────
    private Control BuildVideosMatrixPage()
    {
        _mvCols = VidTypes().ToList();
        _mvRows.Clear();

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var bar = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Bg };

        var chkWeb = new CheckBox
        {
            Text = "Web (fill the gaps)", AutoSize = true, ForeColor = Color.FromArgb(190, 150, 230),
            BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Location = new Point(S(4), S(10)), Checked = false, Visible = VidWebAvailable,
        };
        CheckBox? chkEmu = null;
        bool emuUsable; try { emuUsable = EmuMoviesApi.FromLbSettings() != null; } catch { emuUsable = false; }
        if (emuUsable)
        {
            chkEmu = new CheckBox
            {
                Text = "EmuMovies", AutoSize = true, ForeColor = MvEmuColor,
                BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(S(228), S(10)), Checked = false,
            };
        }
        CheckBox? chkSteam = null;
        bool steamUsable = _editGames.Any(g => SteamCatalog.AppIdOf(Safe(() => g.ApplicationPath), Safe(() => g.LaunchBoxDbId) ?? -1) != null);
        if (steamUsable)
        {
            chkSteam = new CheckBox
            {
                Text = "Steam", AutoSize = true, ForeColor = MvSteamColor,
                BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(S(396), S(10)), Checked = false,
            };
        }
        // YouTube (red) — search-driven; per game we keep the FIRST result we can actually download. Always offered.
        var chkYt = new CheckBox
        {
            Text = "YouTube", AutoSize = true, ForeColor = MvYtColor,
            BackColor = Bg, Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Location = new Point(S(474), S(10)), Checked = false,
        };
        var btnAll = DlgBtn("⬇  Download all missing", Color.FromArgb(78, 52, 120));
        btnAll.AutoSize = false; btnAll.SetBounds(S(566), S(5), S(170), S(28)); btnAll.Enabled = !_readOnly;
        btnAll.Click += (_, _) => MvDownloadAllMissing();

        _mvStatus = new Label
        {
            Text = $"{_editGames.Count} games × {_mvCols.Count} video types", ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(S(746), S(12)),
        };
        bar.Controls.Add(chkWeb);
        if (chkEmu != null) bar.Controls.Add(chkEmu);
        if (chkSteam != null) bar.Controls.Add(chkSteam);
        bar.Controls.Add(chkYt);
        bar.Controls.Add(btnAll);
        bar.Controls.Add(_mvStatus);

        if (!VlcService.Available)
            bar.Controls.Add(new Label { Text = "⚠ libvlc missing — no thumbnails", ForeColor = Color.FromArgb(235, 180, 100), BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(720), S(24)) });

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AllowUserToOrderColumns = false, ReadOnly = true,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            BackgroundColor = Bg, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(55, 55, 66),
            ScrollBars = ScrollBars.Both, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EnableHeadersVisualStyles = false, Cursor = Cursors.Hand,
        };
        grid.DefaultCellStyle.BackColor = Bg; grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(48, 62, 88); grid.DefaultCellStyle.SelectionForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(34, 34, 44); grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        grid.ColumnHeadersHeight = S(30); grid.RowTemplate.Height = S(MvRowH);

        var colGame = new DataGridViewTextBoxColumn { Name = "Game", HeaderText = "Game", Frozen = true, Width = S(220), SortMode = DataGridViewColumnSortMode.NotSortable };
        colGame.DefaultCellStyle.Padding = new Padding(S(6), 0, 0, 0);
        grid.Columns.Add(colGame);
        foreach (var cat in _mvCols)
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = cat, HeaderText = cat, Width = S(MvColW), SortMode = DataGridViewColumnSortMode.NotSortable });

        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _editGames.Count) return;
            e.Value = e.ColumnIndex == 0 ? (Safe(() => _editGames[e.RowIndex].Title) ?? "(untitled)") : "";
        };
        grid.CellPainting += MvPaintCell;
        grid.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) MvOpenCell(e.RowIndex, e.ColumnIndex);
            else if (e.Button == MouseButtons.Right) MvCellMenu(grid, e.RowIndex, e.ColumnIndex);
        };
        grid.Scroll += (_, _) => MvUpdateVisible(grid);
        grid.Resize += (_, _) => MvUpdateVisible(grid);
        grid.HandleCreated += (_, _) => MvUpdateVisible(grid);
        grid.RowCount = _editGames.Count;
        _mvGrid = grid;
        MvUpdateVisible(grid);

        root.Controls.Add(grid);
        root.Controls.Add(bar);

        chkWeb.CheckedChanged += (_, _) =>
        {
            if (chkWeb.Checked) { _mvShowWeb = true; if (!_mvSourceOrder.Contains("web")) _mvSourceOrder.Add("web"); MvFillWeb(chkWeb); }
            else { _mvShowWeb = false; _mvSourceOrder.Remove("web"); MvInvalidateAll(); MvSetStatus($"{_editGames.Count} games × {_mvCols.Count} video types"); }
        };
        if (chkEmu != null) chkEmu.CheckedChanged += (_, _) =>
        {
            if (chkEmu.Checked) { _mvShowEmu = true; if (!_mvSourceOrder.Contains("emu")) _mvSourceOrder.Add("emu"); MvFillEmu(chkEmu); }
            else { _mvShowEmu = false; _mvSourceOrder.Remove("emu"); MvInvalidateAll(); MvSetStatus($"{_editGames.Count} games × {_mvCols.Count} video types"); }
        };
        if (chkSteam != null) chkSteam.CheckedChanged += (_, _) =>
        {
            if (chkSteam.Checked) { _mvShowSteam = true; if (!_mvSourceOrder.Contains("steam")) _mvSourceOrder.Add("steam"); MvFillSteam(chkSteam); }
            else { _mvShowSteam = false; _mvSourceOrder.Remove("steam"); MvInvalidateAll(); MvSetStatus($"{_editGames.Count} games × {_mvCols.Count} video types"); }
        };
        chkYt.CheckedChanged += (_, _) =>
        {
            if (chkYt.Checked)
            {
                if (!YtDlp.Available) { MessageBox.Show(this, "yt-dlp isn't installed. Open a game's Videos → YouTube and use the Download button (or the LB · Integrations → YT-DLP tab) first.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information); chkYt.Checked = false; return; }
                _mvShowYt = true; if (!_mvSourceOrder.Contains("yt")) _mvSourceOrder.Add("yt"); MvFillYt(chkYt);
            }
            else { _mvShowYt = false; _mvSourceOrder.Remove("yt"); MvInvalidateAll(); MvSetStatus($"{_editGames.Count} games × {_mvCols.Count} video types"); }
        };

        return root;
    }

    private void MvSetStatus(string s) { if (_mvStatus != null) _mvStatus.Text = s; }
    private void MvInvalidateAll() { lock (_mvLock) _mvRows.Clear(); _mvGrid?.Invalidate(); }
    private void MvInvalidateRow(int row) { lock (_mvLock) _mvRows.Remove(row); _mvGrid?.InvalidateRow(row); }

    // ── Row data ────────────────────────────────────────────────────────────
    private MvCell[] MvRow(int row)
    {
        lock (_mvLock) { if (_mvRows.TryGetValue(row, out var cached)) return cached; }

        var g = _editGames[row];
        var cells = new MvCell[_mvCols.Count];
        List<VidFile> scan;
        try { scan = VidScan(g); } catch { scan = new(); }

        for (int c = 0; c < _mvCols.Count; c++)
        {
            var ofType = scan.Where(v => string.Equals(v.Type, _mvCols[c], StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.NumVal).ToList();
            cells[c] = new MvCell { Path = ofType.Count > 0 ? ofType[0].Path : null, Count = ofType.Count };
        }

        MvApplyStandins(row, cells);
        lock (_mvLock) { _mvRows[row] = cells; }
        return cells;
    }

    private void MvApplyStandins(int row, MvCell[] cells)
    {
        for (int c = 0; c < cells.Length; c++) cells[c].Web = null;
        if (_mvSourceOrder.Count == 0 || !cells.Any(c => c.Count == 0)) return;

        var g = _editGames[row];
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;

        // Owned videos across ALL types (for dedup: a web video we already own anywhere shouldn't stand in).
        var owned = BuildEmuOwned(MvRow_LocalPaths(g));

        foreach (var src in _mvSourceOrder)
        {
            List<MetadataDb.WebImage> vids;
            if (src == "web")
            {
                _mvWebVideos.TryGetValue(row, out var wl);
                vids = wl ?? new();
            }
            else if (src == "emu")
            {
                _mvEmuVideos.TryGetValue(row, out var el);
                vids = (el ?? new List<EmuMoviesCatalog.EmuMedia>())
                    .Select(m => new MetadataDb.WebImage(dbId, m.Url, m.LbType, m.Region, m.Crc, "lb-emumovies", 0, m.Ext, m.FileSize)).ToList();
            }
            else if (src == "steam")
            {
                _mvSteamVideos.TryGetValue(row, out var sl);
                // Re-stamp as "lb-steam" (a LIVE download) so it's distinct from a DB "steam" row, both here and in
                // the ADS on download (green vs purple owned-border).
                vids = (sl ?? new List<MetadataDb.WebImage>())
                    .Select(w => new MetadataDb.WebImage(w.DatabaseId, w.FileName, w.Type, w.Region, w.Crc32, "lb-steam", w.Duplicate, w.FileType, w.FileSize)).ToList();
            }
            else // "yt"
            {
                _mvYtVideos.TryGetValue(row, out var yl);
                vids = yl ?? new();   // WebImage (origin=youtube, Type=Video → Video Snap); no CRC/size → never deduped
            }

            foreach (var w in vids)
            {
                if (EmuOwns(owned, w.Crc32, w.FileSize)) continue;
                string? col = MvColumnFor(w.Type);
                // Only the Video Snap slot is the target — the other type columns stay owned-only (still shown).
                if (col == null || !string.Equals(col, "Video Snap", StringComparison.OrdinalIgnoreCase)) continue;
                int ci = _mvCols.FindIndex(t => string.Equals(t, col, StringComparison.OrdinalIgnoreCase));
                if (ci < 0) continue;
                if (cells[ci].Count > 0 || cells[ci].Web != null) continue;   // owned or a prior source stood in
                cells[ci].Web = w;
            }
        }
    }

    private List<string> MvRow_LocalPaths(IGame g)
    {
        try { return VidScan(g).Select(v => v.Path).ToList(); } catch { return new(); }
    }

    // ── Painting ──────────────────────────────────────────────────────────────
    private void MvPaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 1 || e.RowIndex >= _editGames.Count) return;
        e.PaintBackground(e.CellBounds, true);
        var g = e.Graphics!;
        var cell = MvRow(e.RowIndex)[e.ColumnIndex - 1];

        bool isWeb = cell.Path == null && cell.Web.HasValue;
        string? key = cell.Path ?? (isWeb ? cell.Web!.Value.Key : null);
        if (key == null)
        {
            using var dim = new SolidBrush(Color.FromArgb(80, 80, 92));
            using var f = new Font("Segoe UI", 9f);
            g.DrawString("—", f, dim, e.CellBounds.X + e.CellBounds.Width / 2f - S(5), e.CellBounds.Y + e.CellBounds.Height / 2f - S(9));
            e.Handled = true; return;
        }

        var img = MvThumb(key, cell, e.RowIndex, e.ColumnIndex);
        int tw = S(MvThumbW), th = S(MvThumbH);
        var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - tw) / 2, e.CellBounds.Y + (e.CellBounds.Height - th) / 2, tw, th);
        if (img != null)
        {
            double scale = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
            int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
            var dst = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            g.DrawImage(img, dst);
            if (isWeb) { using var pen = new Pen(MvStandinColor(cell.Web!.Value), S(2)); g.DrawRectangle(pen, Rectangle.Inflate(dst, S(1), S(1))); }
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(30, 30, 38)); g.FillRectangle(b, box);
            using var f = new Font("Segoe UI", 7.5f); using var fg = new SolidBrush(Color.FromArgb(120, 126, 142));
            g.DrawString(isWeb ? "▶ …" : "▶", f, fg, box.X + box.Width / 2f - S(8), box.Y + box.Height / 2f - S(7));
        }

        int count = cell.Count;
        bool showBadge = count > 0 || isWeb;
        if (showBadge)
        {
            string txt = isWeb ? "web" : count.ToString();
            using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            var sz = g.MeasureString(txt, f);
            int bw = (int)Math.Max(sz.Width + S(6), S(16)), bh = S(14);
            var br = new Rectangle(e.CellBounds.Right - bw - S(4), e.CellBounds.Bottom - bh - S(3), bw, bh);
            using var bg = new SolidBrush(isWeb ? MvStandinColor(cell.Web!.Value) : Color.FromArgb(70, 74, 88));
            g.FillRectangle(bg, br);
            using var fg = new SolidBrush(Color.White); g.DrawString(txt, f, fg, br.X + (bw - sz.Width) / 2f, br.Y + S(1));
        }
        e.Handled = true;
    }

    private void MvUpdateVisible(DataGridView grid)
    {
        try { int first = Math.Max(0, grid.FirstDisplayedScrollingRowIndex); _mvVisFirst = first; _mvVisLast = first + Math.Max(1, grid.DisplayedRowCount(true)); }
        catch { }
    }

    // ── Thumbnails (bounded queue, visible-first; VideoThumbnailer serializes + disk-caches) ──
    private Image? MvThumb(string key, MvCell cell, int row, int col)
    {
        lock (_mvThumbLock) { if (_mvThumbs.TryGetValue(key, out var have)) { MvTouch(key); return have; } }
        if (!VlcService.Available) return null;
        lock (_mvQLock)
        {
            if (_mvPending.ContainsKey(key)) return null;
            var job = new MvJob { Key = key, Path = cell.Path, Web = cell.Web, Row = row, Col = col };
            _mvPending[key] = job; _mvQueue.Add(job);
            if (_mvQueue.Count > MvQueueMax)
            {
                int centre = (_mvVisFirst + _mvVisLast) / 2;
                _mvQueue.Sort((a, b) => Math.Abs(a.Row - centre).CompareTo(Math.Abs(b.Row - centre)));
                for (int i = _mvQueue.Count - 1; i >= MvQueueMax; i--) { _mvPending.Remove(_mvQueue[i].Key); _mvQueue.RemoveAt(i); }
            }
            while (_mvWorkers < MvWorkerCount) { _mvWorkers++; System.Threading.Tasks.Task.Run(MvWorkerLoop); }
        }
        return null;
    }

    private void MvWorkerLoop()
    {
        while (true)
        {
            MvJob job;
            lock (_mvQLock)
            {
                if (_mvQueue.Count == 0) { _mvWorkers--; return; }
                int centre = (_mvVisFirst + _mvVisLast) / 2, best = 0, bestD = int.MaxValue;
                for (int i = 0; i < _mvQueue.Count; i++) { int d = Math.Abs(_mvQueue[i].Row - centre); if (d < bestD) { bestD = d; best = i; } }
                job = _mvQueue[best]; _mvQueue.RemoveAt(best); _mvPending.Remove(job.Key);
            }

            Image? thumb = MvDecode(job);
            var grid = _mvGrid;
            if (grid == null || grid.IsDisposed || !grid.IsHandleCreated) { thumb?.Dispose(); lock (_mvQLock) { _mvWorkers--; } return; }
            try
            {
                grid.BeginInvoke(new Action(() =>
                {
                    if (grid.IsDisposed) { thumb?.Dispose(); return; }
                    MvStoreThumb(job.Key, thumb);
                    if (job.Row < grid.RowCount && job.Col < grid.ColumnCount) grid.InvalidateCell(job.Col, job.Row);
                }));
            }
            catch { thumb?.Dispose(); }
        }
    }

    private Image? MvDecode(MvJob job)
    {
        try
        {
            if (job.Path != null) return VideoThumbnailer.Get(job.Path);      // local: disk-cached
            if (!job.Web.HasValue) return null;
            var w = job.Web.Value;
            string key = "vm:" + w.Crc32 + ":" + w.FileName;
            if (string.Equals(w.Origin, "youtube", StringComparison.OrdinalIgnoreCase))
            {
                // Use the YouTube thumbnail JPG (cheap) rather than decoding a video frame (would need yt-dlp + VLC).
                var id = YouTubeCatalog.ExtractId(w.FileName);
                if (id == null) return null;
                var bytes = WebGetBytes(YtDlp.ThumbUrl(id), null);
                if (bytes == null || bytes.Length == 0) return null;
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                double scale = Math.Min(1.0, Math.Min((double)MvThumbW / tmp.Width, (double)MvThumbH / tmp.Height));
                return new Bitmap(tmp, new Size(Math.Max(1, (int)(tmp.Width * scale)), Math.Max(1, (int)(tmp.Height * scale))));
            }
            if (string.Equals(w.Origin, "lb-emumovies", StringComparison.OrdinalIgnoreCase))
                return VideoThumbnailer.GetFromUrl(w.FileName, EmuMoviesApi.MediaReferer, key);
            if (string.Equals(w.Origin, "lb-steam", StringComparison.OrdinalIgnoreCase))
                return VideoThumbnailer.GetFromUrl(w.FileName, SteamApi.Referer, key);   // live: FileName is a direct mp4
            // Database video: resolve the playable URL chain (turns a Steam .m3u8.mp4 into the real .m3u8).
            foreach (var s in MediaApiBridge.ListUrls(w))
            {
                var t = VideoThumbnailer.GetFromUrl(s.Url, s.Referer, key);
                if (t != null) return t;
            }
        }
        catch { }
        return null;
    }

    private void MvStoreThumb(string key, Image? thumb)
    {
        lock (_mvThumbLock)
        {
            _mvThumbs[key] = thumb; MvTouch(key);
            while (_mvLru.Count > MvThumbCacheMax)
            {
                var oldest = _mvLru.Last; if (oldest == null) break;
                _mvLru.RemoveLast(); _mvLruNodes.Remove(oldest.Value);
                if (_mvThumbs.TryGetValue(oldest.Value, out var img)) { _mvThumbs.Remove(oldest.Value); try { img?.Dispose(); } catch { } }
            }
        }
    }
    private void MvTouch(string key)
    {
        if (_mvLruNodes.TryGetValue(key, out var node)) { _mvLru.Remove(node); _mvLru.AddFirst(node); }
        else _mvLruNodes[key] = _mvLru.AddFirst(key);
    }

    // ── Fill batches ──────────────────────────────────────────────────────────
    // True when the game's Video Snap slot is already covered — owned OR filled by a higher-priority checked source.
    // Video Snap is the ONLY slot the matrix fills, so it alone decides whether a source needs querying for a game.
    private bool MvVideoSnapCovered(int row)
    {
        int vs = _mvCols.FindIndex(t => string.Equals(t, "Video Snap", StringComparison.OrdinalIgnoreCase));
        if (vs < 0) return false;
        var cells = MvRow(row);
        return cells[vs].Count > 0 || cells[vs].Web.HasValue;
    }

    private void MvFillWeb(CheckBox chk)
    {
        if (!VidWebAvailable) { MessageBox.Show(this, "The extended database isn't available.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information); chk.Checked = false; return; }
        MvFillBatch("Loading database videos…", (row, g, dbId, ct) =>
        {
            if (MvVideoSnapCovered(row)) { lock (_mvLock) _mvWebVideos[row] = new(); return 0; }   // already has a Video Snap → skip
            List<MetadataDb.WebImage> v; try { v = MetadataDb.VideosForGame(dbId); } catch { v = new(); }
            lock (_mvLock) _mvWebVideos[row] = v;
            return v.Count;
        });
    }

    private void MvFillEmu(CheckBox chk)
    {
        var api = EmuApi();
        if (api == null) { MessageBox.Show(this, "EmuMovies credentials aren't configured.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information); chk.Checked = false; return; }
        MvFillBatch("Querying EmuMovies…", (row, g, dbId, ct) =>
        {
            if (MvVideoSnapCovered(row)) { lock (_mvLock) _mvEmuVideos[row] = new(); return 0; }   // only query games that need a Video Snap
            var media = new List<EmuMoviesCatalog.EmuMedia>();
            string plat = Safe(() => g.Platform) ?? "";
            if (EmuMoviesCatalog.SupportsPlatform(plat))
            {
                try
                {
                    media = EmuMoviesCatalog.ResolveForGameAsync(api, Safe(() => g.Title) ?? "", Safe(() => g.ApplicationPath) ?? "", plat, ct)
                        .GetAwaiter().GetResult()
                        .Where(m => m.LbType == "Video" || m.LbType == "VideoAdvert").ToList();
                }
                catch { }
            }
            lock (_mvLock) _mvEmuVideos[row] = media;
            return media.Count;
        });
    }

    private void MvFillSteam(CheckBox chk)
    {
        MvFillBatch("Querying Steam…", (row, g, dbId, ct) =>
        {
            if (MvVideoSnapCovered(row)) { lock (_mvLock) _mvSteamVideos[row] = new(); return 0; }   // only query games that need a Video Snap
            List<MetadataDb.WebImage> v = new();
            try
            {
                v = SteamCatalog.ResolveForGameAsync(dbId, Safe(() => g.ApplicationPath) ?? "", ct).GetAwaiter().GetResult()
                    .Where(w => string.Equals(w.Type, "Video", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            catch { }
            lock (_mvLock) _mvSteamVideos[row] = v;
            return v.Count;
        });
    }

    private void MvFillYt(CheckBox chk)
    {
        var cfg = YtConfig.Load();
        var cookies = cfg.CookieBrowser;
        MvFillBatch("Querying YouTube…", (row, g, dbId, ct) =>
        {
            if (MvVideoSnapCovered(row)) { lock (_mvLock) _mvYtVideos[row] = new(); return 0; }   // only query games that need a Video Snap
            var list = new List<MetadataDb.WebImage>();
            try
            {
                var searches = YtExpandSearches(cfg.Searches, g);
                string? videoUrl = Safe(() => g.VideoUrl);
                string? gogId = Safe(() => (g as ILiteBoxGame)?.GetField("GogAppId"));
                var results = YouTubeCatalog.ResolveInitialAsync(videoUrl, gogId, searches, 12, cookies, ct).GetAwaiter().GetResult();
                // Keep the FIRST result we can actually download — verify one at a time and stop there (cap the
                // checks so an all-blocked game doesn't hammer YouTube).
                int checks = 0;
                foreach (var r in results)
                {
                    if (ct.IsCancellationRequested || checks >= 8) break;
                    checks++;
                    var v = YtDlp.VerifyAsync(new[] { r.Id }, cookies, ct).GetAwaiter().GetResult();
                    if (v.TryGetValue(r.Id, out var vd) && vd.Available)
                    {
                        list.Add(new MetadataDb.WebImage(dbId, r.WatchUrl, "Video", "", 0, "youtube", 0, "mp4", 0));
                        break;
                    }
                }
            }
            catch { }
            lock (_mvLock) _mvYtVideos[row] = list;
            return list.Count;
        });
    }

    private void MvFillBatch(string title, Func<int, IGame, int, CancellationToken, int> perGame)
    {
        using var dlg = NewDialog(title, 420, 150);
        var lbl = new Label { Text = "Preparing…", ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(380), S(20)) };
        var pb = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(380), S(18)), Minimum = 0, Maximum = Math.Max(1, _editGames.Count) };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(300), S(72), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);
        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => cts.Cancel(); dlg.FormClosing += (_, _) => cts.Cancel();

        void Ui(Action a) { try { if (!dlg.IsDisposed && dlg.IsHandleCreated) dlg.BeginInvoke(a); } catch { } }

        int found = 0;
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
                lock (_mvLock) _mvRows.Remove(row);   // recompute with the new stand-ins
                if (n > 0) found++;
                int f = found;
                Ui(() => { if (!dlg.IsDisposed) { pb.Value = Math.Min(pb.Maximum, cur); lbl.Text = $"{cur} / {_editGames.Count} games · {f} with videos"; } });
            }
            Ui(() => { if (!dlg.IsDisposed) dlg.Close(); });
        }, cts.Token);

        dlg.ShowDialog(this);
        cts.Cancel();
        MvSetStatus($"{_editGames.Count} games × {_mvCols.Count} video types");
        _mvGrid?.Invalidate();
    }

    // ── Cell actions ──────────────────────────────────────────────────────────
    private void MvCellMenu(DataGridView grid, int row, int col)
    {
        if (row < 0 || col < 1 || row >= _editGames.Count) return;
        var cell = MvRow(row)[col - 1];
        var m = ThemedMenu();
        if (cell.Path != null || cell.Web.HasValue)
            m.Items.Add(new ToolStripMenuItem(cell.Path != null ? "▶  Play" : "▶  Play (stream)").WithClick(() => MvPlayCell(row, col)));
        if (cell.Web.HasValue && !_readOnly)
            m.Items.Add(new ToolStripMenuItem("⬇  Download this video").WithClick(() => MvDownloadCell(row, col)));
        m.Items.Add(new ToolStripMenuItem("🎬  Open this game's videos…").WithClick(() => MvOpenCell(row, col)));
        m.Show(grid, grid.PointToClient(Cursor.Position));
    }

    /// <summary>Play the cell's video — the local ★★ file (in-window), or the web/EmuMovies stand-in streamed
    /// (EmuMovies = direct URL; database = the per-origin chain, so a Steam fake-mp4 plays as its real HLS).</summary>
    private void MvPlayCell(int row, int col)
    {
        var cell = MvRow(row)[col - 1];
        if (cell.Path != null)
        {
            if (VideoPlayerDialog.Play(this, cell.Path)) { _imgTouchedPlatforms.Add(Safe(() => _editGames[row].Platform) ?? ""); MvInvalidateRow(row); }
            return;
        }
        if (!cell.Web.HasValue) return;
        var w = cell.Web.Value;
        string title = $"{Safe(() => _editGames[row].Title)} — {_mvCols[col - 1]}";
        // YouTube plays in the in-app WebView2 player (or the browser), never libvlc — the FileName is a watch URL.
        if (string.Equals(w.Origin, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            var id = YouTubeCatalog.ExtractId(w.FileName) ?? "";
            try { if (LbApiHost.Host.Video.YouTubePlayerDialog.TryShow(this, id, w.FileName, title)) return; } catch { }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(w.FileName) { UseShellExecute = true }); } catch { }
            return;
        }
        List<VideoPlayerDialog.Source> srcs;
        if (string.Equals(w.Origin, "lb-emumovies", StringComparison.OrdinalIgnoreCase))
            srcs = new() { new VideoPlayerDialog.Source(w.FileName, EmuMoviesApi.MediaReferer) };
        else if (string.Equals(w.Origin, "lb-steam", StringComparison.OrdinalIgnoreCase))
        {
            UseWaitCursor = true;   // HLS-only trailers need a yt-dlp resolve (the movie_max.mp4 is a dead 404)
            string? url; try { url = VidSteamPlayUrl(_editGames[row], w); } finally { UseWaitCursor = false; }
            srcs = new() { new VideoPlayerDialog.Source(url ?? w.FileName, SteamApi.Referer) };
        }
        else srcs = MediaApiBridge.ListUrls(w).Select(c => new VideoPlayerDialog.Source(c.Url, c.Referer)).ToList();
        VideoPlayerDialog.PlayWeb(this, title, srcs);
    }

    private void MvDownloadCell(int row, int col)
    {
        var cell = MvRow(row)[col - 1];
        if (!cell.Web.HasValue || _readOnly) return;
        var prev = _imgGame; _imgGame = _editGames[row];
        try { MvDownloadOne(_editGames[row], cell.Web.Value, _mvCols[col - 1]); }
        finally { _imgGame = prev; }
        _imgTouchedPlatforms.Add(Safe(() => _editGames[row].Platform) ?? "");
        MvInvalidateRow(row);
    }

    private void MvOpenCell(int row, int col)
    {
        if (row < 0 || row >= _editGames.Count) return;
        var game = _editGames[row];
        var prevGame = _imgGame; _imgGame = game;
        // Mirror the grid's enabled sources so the game's video page opens showing the same stand-ins.
        bool prevWeb = _vidShowWeb, prevEmu = _vidShowEmu, prevSteam = _vidShowSteam, prevYt = _vidShowYt;
        _vidShowWeb = _mvShowWeb; _vidShowEmu = _mvShowEmu; _vidShowSteam = _mvShowSteam; _vidShowYt = _mvShowYt;
        try
        {
            using var dlg = NewDialog($"{Safe(() => game.Title)} — Videos", 940, 660);
            dlg.FormBorderStyle = FormBorderStyle.Sizable; dlg.MaximizeBox = true;
            var holder = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = Bg };
            var close = DlgBtn("Close", Color.FromArgb(45, 95, 60)); close.AutoSize = false; close.SetBounds(S(12), S(7), S(100), S(30));
            close.Click += (_, _) => dlg.Close();
            bottom.Controls.Add(close);
            var page = BuildVideosPage(); page.Dock = DockStyle.Fill;
            holder.Controls.Add(page);
            dlg.Controls.Add(holder); dlg.Controls.Add(bottom);
            dlg.ShowDialog(this);
        }
        finally
        {
            _vidShowWeb = prevWeb; _vidShowEmu = prevEmu; _vidShowSteam = prevSteam; _vidShowYt = prevYt;
            _imgGame = prevGame;
            _imgTouchedPlatforms.Add(Safe(() => game.Platform) ?? "");   // the modal may have added videos
        }
        MvInvalidateRow(row);
    }

    // ── Download EVERY missing cell ───────────────────────────────────────────
    private void MvDownloadAllMissing()
    {
        if (_readOnly) return;
        if (_mvSourceOrder.Count == 0)
        {
            MessageBox.Show(this, "Turn on \"Show web videos\" and/or \"Show EmuMovies videos\" first.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var jobs = new List<(int row, MetadataDb.WebImage web, string col)>();
        for (int row = 0; row < _editGames.Count; row++)
        {
            var cells = MvRow(row);
            for (int c = 0; c < cells.Length; c++)
                if (cells[c].Count == 0 && cells[c].Web.HasValue) jobs.Add((row, cells[c].Web!.Value, _mvCols[c]));
        }
        if (jobs.Count == 0) { MessageBox.Show(this, "Nothing to download — no empty video slot has a stand-in.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        int games = jobs.Select(j => j.row).Distinct().Count();
        int hls = jobs.Count(j => VidIsHlsRow(j.web));
        // With yt-dlp present, Steam HLS trailers ARE saved (via yt-dlp); only warn when it's missing.
        string note = (hls > 0 && !YtDlp.Available)
            ? $"\n\n{hls} of these are Steam HLS trailers — they'll be SKIPPED (install yt-dlp to save them)."
            : "";
        if (MessageBox.Show(this, $"Download {jobs.Count} video(s) across {games} game(s)?" + note,
                "Download all missing", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        using var dlg = NewDialog("Downloading videos…", 420, 150);
        var lbl = new Label { Text = "Starting…", ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(380), S(20)) };
        var pb = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(380), S(18)), Minimum = 0, Maximum = jobs.Count };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(300), S(72), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);
        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => cts.Cancel(); dlg.FormClosing += (_, _) => cts.Cancel();

        int ok = 0, fail = 0; var touched = new List<string>();
        System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (cts.IsCancellationRequested) break;
                var (row, web, col) = jobs[i];
                var g = _editGames[row];
                bool done1 = false;
                try { done1 = MvDownloadOne(g, web, col); } catch { }
                if (done1) { ok++; var p = Safe(() => g.Platform) ?? ""; if (!string.IsNullOrEmpty(p) && !touched.Contains(p)) touched.Add(p); }
                else fail++;
                int done = i + 1, o = ok, f = fail;
                try { if (!dlg.IsDisposed && dlg.IsHandleCreated) dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) { pb.Value = Math.Min(pb.Maximum, done); lbl.Text = $"{done} / {jobs.Count} · {o} saved" + (f > 0 ? $", {f} skipped/failed" : ""); } })); }
                catch { }
            }
            try { if (!dlg.IsDisposed && dlg.IsHandleCreated) dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) dlg.Close(); })); } catch { }
        }, cts.Token);

        dlg.ShowDialog(this); cts.Cancel();
        foreach (var p in touched) _imgTouchedPlatforms.Add(p);
        MvInvalidateAll();
        MessageBox.Show(this, $"Saved {ok} video(s)." + (fail > 0 ? $"\n{fail} skipped or failed (HLS / no source)." : ""), "LiteBox", MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>Download one video stand-in into <paramref name="targetType"/>'s folder, video-aware: EmuMovies
    /// = direct GET, database = FetchForWizard (skips a Steam HLS → returns false). Writes the ExtendDB ADS.</summary>
    private bool MvDownloadOne(IGame g, MetadataDb.WebImage w, string targetType)
    {
        try
        {
            string plat = Safe(() => g.Platform) ?? "";
            string idStr = Safe(() => g.Id) ?? "";
            int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
            if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return false;

            // YouTube: yt-dlp writes the file itself (FileName is the watch URL). Uses the configured quality/cookies.
            if (string.Equals(w.Origin, "youtube", StringComparison.OrdinalIgnoreCase))
            {
                string? ydir = MediaResolver.VideoTypeFolder(plat, targetType);
                if (string.IsNullOrEmpty(ydir)) return false;
                Directory.CreateDirectory(ydir);
                string ysani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
                string yprefix = ImgPrefix(plat, idStr, ysani, null, ydir);
                int ynum = ImgMaxNum(ydir, yprefix) + 1;
                string outNoExt = Path.Combine(ydir, $"{yprefix}-{ynum:D2}");
                var cfg = YtConfig.Load();
                var outcome = YtDlp.DownloadAsync(w.FileName, cfg.Quality, outNoExt, cfg.CookieBrowser).GetAwaiter().GetResult();
                if (outcome.Path == null || !File.Exists(outcome.Path)) return false;
                long yfs = 0; try { yfs = new FileInfo(outcome.Path).Length; } catch { }
                ImageAdsWriter.WriteForDownload(outcome.Path, new MetadataDb.WebImage(dbId, w.FileName, "Video", "", 0, "youtube", 0, "mp4", yfs), dbId, plat);
                return true;
            }

            // A Steam HLS trailer (".m3u8") can't be saved as bytes — hand it to yt-dlp (manifest + ffmpeg merge).
            if (VidIsHlsRow(w)) return VidDownloadHls(g, w, targetType);

            byte[]? bytes;
            if (string.Equals(w.Origin, "lb-emumovies", StringComparison.OrdinalIgnoreCase)) bytes = EmuFetchBytes(w.FileName);
            else if (string.Equals(w.Origin, "lb-steam", StringComparison.OrdinalIgnoreCase))
            {
                bytes = WebGetBytes(w.FileName, SteamApi.Referer);   // live Steam = direct mp4 (older trailers)
                if (bytes == null || bytes.Length == 0) return VidDownloadSteamHls(g, w, targetType);   // HLS-only → yt-dlp master
            }
            else bytes = MediaApiBridge.FetchBytes(w, plat);   // database video via the wizard: HLS-only Steam → null

            if (bytes == null || bytes.Length == 0) return false;
            // Guard against a mirror handing back an HLS manifest as "bytes".
            if (bytes.Length > 7 && System.Text.Encoding.ASCII.GetString(bytes, 0, 7) == "#EXTM3U") return false;

            string ext = ImageFileType.Extract(w.FileName);
            if (string.IsNullOrEmpty(ext)) ext = "mp4";
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}.{ext.TrimStart('.')}");
            File.WriteAllBytes(target, bytes);
            ImageAdsWriter.WriteForDownload(target, w, dbId, plat);
            return true;
        }
        catch { return false; }
    }
}
