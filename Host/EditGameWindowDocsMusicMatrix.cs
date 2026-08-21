// Edit Game (MULTI-selection) → Media → Documents / Music: batch download, the way the image and video
// matrices do it — one row per game, the sources you enable fill the gaps, one button downloads them all.
//
// These two media are SINGLE-SLOT, unlike images and videos: a game has one manual and one music track, so
// there is nothing to lay out in a grid of types. One state column per game says it all, and the page stays
// text-only — no thumbnails to decode, no worker pool.
//
// Documents means MANUALS, and only manuals. The document table also carries maps and press kits, and those
// are NOT manuals: LaunchBox exposes a single manual slot, and a map filed there would be opened as one. So a
// game whose only offer is a map or a press kit is left alone — deliberately skipped rather than filled with
// the wrong thing. Region is honoured: the candidates are ranked by the region priorities, same order the
// image picker uses, so a library that prefers North America gets the North American manual when there is one.
//
// A downloaded manual is DESIGNATED (ManualPath) as well as filed as an additional document — that is what
// "download the manuals for these games" means. Music likewise becomes the game's music.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Integrations;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private enum DmKind { Manual, Music }

    private struct DmCell
    {
        public string? Local;                 // owned file (null = nothing on disk)
        public MetadataDb.WebImage? Web;      // stand-in when empty and a source is on
        public string? Src;                   // "web" / "emu"
    }

    /// <summary>Everything ONE page owns. This used to hang off the window as five fields, and the two
    /// pages — built once each, then kept by ShowPage's cache — took turns overwriting it: after a look at
    /// Music, coming back to Documents left a grid headed "Manual" resolving, downloading and DESIGNATING
    /// music. The header is fixed when the page is built; the behaviour read the mutable field. One state
    /// per page, handed to everything that acts on it, and the kind cannot drift from the label again.</summary>
    private sealed class DmState
    {
        public DmState(DmKind kind) { Kind = kind; }

        public readonly DmKind Kind;
        public bool IsManual => Kind == DmKind.Manual;
        public string Noun => Kind == DmKind.Manual ? "manual" : "music";

        public DataGridView? Grid;
        public Label? Status;
        /// <summary>Per ROW — and per page: the same game answers differently for a manual and for music.</summary>
        public readonly Dictionary<int, DmCell> Cells = new();
        /// <summary>Enable order = fill priority, like the other matrices.</summary>
        public readonly List<string> SourceOrder = new();
    }

    private static readonly Color DmOwnedColor = Color.FromArgb(120, 185, 130);
    private static readonly Color DmWebColor = Color.FromArgb(190, 150, 230);
    private static readonly Color DmEmuColor = Color.FromArgb(90, 150, 220);

    private Control BuildDocumentsMatrixPage() => BuildDmPage(DmKind.Manual);
    private Control BuildMusicMatrixPage() => BuildDmPage(DmKind.Music);

    private Control BuildDmPage(DmKind kind)
    {
        var st = new DmState(kind);

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var bar = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Bg };

        // ExtendDB carries every document/music row (there are no LaunchBox ones), and its rows need the
        // per-origin fetcher — without the module they cannot be downloaded at all, so the source is not
        // offered rather than being offered and failing on every game.
        var chkWeb = new CheckBox
        {
            Text = "ExtendDB (fill the gaps)", AutoSize = true, ForeColor = DmWebColor, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Location = new Point(S(4), S(10)), Enabled = MediaApiBridge.ModuleActive,
        };
        if (!MediaApiBridge.ModuleActive) chkWeb.Text += " — needs the ExtendDB module";
        chkWeb.CheckedChanged += (_, _) =>
        {
            if (chkWeb.Checked) st.SourceOrder.Add("web"); else st.SourceOrder.Remove("web");
            DmInvalidateAll(st);
        };

        CheckBox? chkEmu = null;
        bool emuUsable; try { emuUsable = EmuMoviesApi.FromLbSettings() != null; } catch { emuUsable = false; }
        if (emuUsable)
        {
            chkEmu = new CheckBox
            {
                Text = "EmuMovies", AutoSize = true, ForeColor = DmEmuColor, BackColor = Bg,
                Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(S(210), S(10)),
            };
            chkEmu.CheckedChanged += (_, _) =>
            {
                if (chkEmu.Checked)
                {
                    st.SourceOrder.Add("emu");
                    // One resolve per game, on the shared batch runner the image matrix already uses — its
                    // results land in the same per-row cache. That one IS shared on purpose: it holds every
                    // medium the game offers, and each page filters it by type, so visiting both queries once.
                    MxFillEmu(chkEmu);
                    DmInvalidateAll(st);
                }
                else { st.SourceOrder.Remove("emu"); DmInvalidateAll(st); }
            };
        }

        var btnAll = DlgBtn($"⬇  Download all missing", Color.FromArgb(78, 52, 120));
        btnAll.AutoSize = false; btnAll.SetBounds(S(310), S(5), S(170), S(28)); btnAll.Enabled = !_readOnly;
        btnAll.Click += (_, _) => DmDownloadAllMissing(st);

        st.Status = new Label
        {
            Text = $"{_editGames.Count} games", ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(S(492), S(12)),
        };

        bar.Controls.Add(chkWeb);
        if (chkEmu != null) bar.Controls.Add(chkEmu);
        bar.Controls.Add(btnAll);
        bar.Controls.Add(st.Status);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AllowUserToOrderColumns = false, ReadOnly = true,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            BackgroundColor = Bg, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(55, 55, 66),
            ScrollBars = ScrollBars.Both, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EnableHeadersVisualStyles = false,
        };
        grid.DefaultCellStyle.BackColor = Bg;
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(48, 62, 88);
        grid.DefaultCellStyle.SelectionForeColor = Fg;
        grid.DefaultCellStyle.Padding = new Padding(S(6), 0, 0, 0);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(34, 34, 44);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        grid.ColumnHeadersHeight = S(30);
        grid.RowTemplate.Height = S(26);

        grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Game", HeaderText = "Game", Frozen = true, Width = S(300), SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "State", HeaderText = st.IsManual ? "Manual" : "Music",
            Width = S(520), SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _editGames.Count) return;
            if (e.ColumnIndex == 0) { e.Value = Safe(() => _editGames[e.RowIndex].Title) ?? ""; return; }
            var c = DmRow(st, e.RowIndex);
            e.Value = c.Local != null ? "✓  " + Path.GetFileName(c.Local)
                    : c.Web.HasValue ? $"⬇  {(c.Src == "emu" ? "EmuMovies" : "ExtendDB")}  ·  {ManualLibrary.NormalizeRegion(c.Web.Value.Region)}"
                                       + $"  ·  {(st.IsManual ? DocWebExt(c.Web.Value) : MusWebExt(c.Web.Value)).TrimStart('.').ToUpperInvariant()}"
                    : "—";
        };
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _editGames.Count || e.ColumnIndex != 1) return;
            var c = DmRow(st, e.RowIndex);
            e.CellStyle.ForeColor = c.Local != null ? DmOwnedColor
                                  : c.Web.HasValue ? (c.Src == "emu" ? DmEmuColor : DmWebColor)
                                  : SubFg;
        };

        grid.RowCount = _editGames.Count;
        st.Grid = grid;

        root.Controls.Add(grid);
        root.Controls.Add(bar);
        DmSetStatus(st);

        // Probe (env LB_DM_PROBE=1 for the database source, =emu for EmuMovies): turn that source on and report
        // what each row resolved to — the rules that matter here are about WHICH row wins (type, then region),
        // and those are invisible in a screenshot until something is downloaded.
        string probe = Environment.GetEnvironmentVariable("LB_DM_PROBE") ?? "";
        if (probe == "1" || probe == "emu")
        {
            BeginInvoke((Action)(() =>
            {
                bool emuProbe = probe == "emu";
                if (emuProbe) { if (chkEmu == null) { Console.WriteLine("[dm] no EmuMovies credentials"); return; } chkEmu.Checked = true; }
                else chkWeb.Checked = true;
                Console.WriteLine($"[dm] === {(st.IsManual ? "Documents/Manual" : "Music")} · {_editGames.Count} games · source={(emuProbe ? "EmuMovies" : "ExtendDB")} ===");
                for (int i = 0; i < _editGames.Count; i++)
                {
                    var g = _editGames[i];
                    int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
                    string offered;
                    if (emuProbe)
                    {
                        List<EmuMoviesCatalog.EmuMedia>? em;
                        lock (_mxLock) _mxEmuMedia.TryGetValue(i, out em);
                        offered = em == null || em.Count == 0 ? "(none)"
                            : string.Join(", ", em.GroupBy(m => m.LbType).Select(gr => $"{gr.Key}×{gr.Count()}"));
                    }
                    else
                    {
                        var all = dbId > 0 ? (st.IsManual
                                    ? MetadataDb.DocumentsForGame(MetadataDb.ExtendedDbPath, dbId)
                                    : MetadataDb.MusicForGame(MetadataDb.ExtendedDbPath, dbId))
                                : new List<MetadataDb.WebImage>();
                        offered = all.Count == 0 ? "(none)"
                            : string.Join(", ", all.GroupBy(r => r.Type).Select(gr => $"{gr.Key}×{gr.Count()}"));
                    }
                    var c = DmRow(st, i);
                    string verdict = c.Local != null ? "OWNED"
                                   : c.Web.HasValue ? $"take {c.Web.Value.Type} [{ManualLibrary.NormalizeRegion(c.Web.Value.Region)}] {c.Web.Value.FileName}"
                                   : "SKIP";
                    Console.WriteLine($"[dm] {Safe(() => g.Title),-38} db={dbId,-7} offers: {offered,-40} -> {verdict}");
                }
                Console.WriteLine("[dm] done");
            }));
        }
        return root;
    }

    private void DmSetStatus(DmState st)
    {
        if (st.Status == null) return;
        int owned = 0, fillable = 0;
        for (int i = 0; i < _editGames.Count; i++)
        {
            var c = DmRow(st, i);
            if (c.Local != null) owned++;
            else if (c.Web.HasValue) fillable++;
        }
        st.Status.Text = $"{_editGames.Count} games · {owned} with a {st.Noun}"
                       + (fillable > 0 ? $" · {fillable} fillable from the enabled source(s)" : "");
    }

    // Its OWN grid and label — this used to reach for whichever page had been built last, so a download
    // left the visible one showing rows it had already replaced.
    private void DmInvalidateAll(DmState st)
    {
        st.Cells.Clear();
        try { st.Grid?.Invalidate(); } catch { }
        DmSetStatus(st);
    }

    /// <summary>What one game has, and what could fill it. The local answer is LaunchBox's own — the same
    /// path the game's menu opens — so "owned" here means exactly what the rest of the app shows.</summary>
    private DmCell DmRow(DmState st, int row)
    {
        if (st.Cells.TryGetValue(row, out var cached)) return cached;
        var g = _editGames[row];
        var cell = new DmCell();

        string local = st.IsManual ? Safe(() => g.GetManualPath()) ?? "" : Safe(() => g.GetMusicPath()) ?? "";
        if (local.Length > 0 && File.Exists(local)) cell.Local = local;

        if (cell.Local == null && st.SourceOrder.Count > 0)
        {
            int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
            foreach (var src in st.SourceOrder)
            {
                if (cell.Web.HasValue) break;
                if (src == "web")
                {
                    if (dbId <= 0 || !MediaApiBridge.Available) continue;
                    List<MetadataDb.WebImage> rows;
                    try
                    {
                        rows = st.IsManual
                             ? MetadataDb.DocumentsForGame(MetadataDb.ExtendedDbPath, dbId)
                             : MetadataDb.MusicForGame(MetadataDb.ExtendedDbPath, dbId);
                    }
                    catch { rows = new(); }
                    // The type filter IS the skip rule: a map or a press kit is not a manual, and a game
                    // that only has those keeps its empty slot instead of being handed the wrong document.
                    var pick = MxWebSlotPick(rows, new List<string> { st.IsManual ? "Manual" : "Music" }, out _);
                    if (pick != null) { cell.Web = pick; cell.Src = "web"; }
                }
                else   // "emu"
                {
                    List<EmuMoviesCatalog.EmuMedia>? media;
                    lock (_mxLock) _mxEmuMedia.TryGetValue(row, out media);
                    if (media == null) continue;
                    // EmuMedia is a STRUCT — FirstOrDefault hands back a BLANK one when nothing matches, and
                    // it is never null. Order the real candidates instead (type filter + region priority,
                    // the same rule the image grid uses), so "no manual / no music" stays an empty slot.
                    string want = st.IsManual ? "Manual" : "Music";
                    var ordered = MxEmuSlotOrder(media, dbId, new List<string> { want }, out _);
                    if (ordered.Count > 0) { cell.Web = ordered[0]; cell.Src = "emu"; }
                }
            }
        }

        st.Cells[row] = cell;
        return cell;
    }

    // ── Batch ─────────────────────────────────────────────────────────────────
    private void DmDownloadAllMissing(DmState st)
    {
        if (_readOnly) return;
        if (st.SourceOrder.Count == 0)
        { MessageBox.Show(this, "Turn on a source first — ExtendDB and/or EmuMovies.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        var jobs = new List<(int row, MetadataDb.WebImage web)>();
        for (int row = 0; row < _editGames.Count; row++)
        {
            var c = DmRow(st, row);
            if (c.Local == null && c.Web.HasValue) jobs.Add((row, c.Web.Value));
        }
        if (jobs.Count == 0)
        {
            MessageBox.Show(this, $"Nothing to download — every selected game either has a {st.Noun} already, or the enabled source(s) offer none."
                + (st.IsManual ? "\n\nMaps and press kits are not manuals: a game whose only offer is one of those is skipped." : ""),
                "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"Download {jobs.Count} {st.Noun}(s)?\n\nEach one is filed for its game and set as its {st.Noun}.",
                "Download all missing", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        using var dlg = NewDialog($"Downloading {st.Noun}s…", 420, 150);
        var lbl = new Label { Text = "Starting…", ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(380), S(20)) };
        var pb = new ProgressBar { Location = new Point(S(16), S(42)), Size = new Size(S(380), S(18)), Minimum = 0, Maximum = jobs.Count };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(300), S(72), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);
        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => cts.Cancel();
        dlg.FormClosing += (_, _) => cts.Cancel();

        int ok = 0, fail = 0;
        System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (cts.IsCancellationRequested) break;
                var (row, web) = jobs[i];
                bool done = false;
                try { done = DmDownloadOne(st, _editGames[row], web); } catch { }
                if (done) ok++; else fail++;
                int n = i + 1, o = ok, f = fail;
                try
                {
                    if (!dlg.IsDisposed && dlg.IsHandleCreated)
                        dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) { pb.Value = Math.Min(pb.Maximum, n); lbl.Text = $"{n} / {jobs.Count} · {o} saved" + (f > 0 ? $", {f} failed" : ""); } }));
                }
                catch { }
            }
            try { if (!dlg.IsDisposed && dlg.IsHandleCreated) dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) dlg.Close(); })); } catch { }
        }, cts.Token);

        dlg.ShowDialog(this);
        cts.Cancel();
        DmInvalidateAll(st);
        MessageBox.Show(this, $"Saved {ok} {st.Noun}(s)." + (fail > 0 ? $"\n{fail} failed." : ""),
                        "LiteBox", MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>Fetch one stand-in and file it for THIS game — the single-game pages' write rules, per game
    /// rather than per window: managed location, provenance stamped, designated as the game's manual/music.</summary>
    private bool DmDownloadOne(DmState st, IGame g, MetadataDb.WebImage w)
    {
        string plat = Safe(() => g.Platform) ?? "", title = Safe(() => g.Title) ?? "";
        string root = DocLbRoot();
        if (root.Length == 0 || plat.Length == 0 || title.Length == 0) return false;
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;

        // ImgFetchWebBytes resolves the per-origin chain against the CURRENT game's platform, so point it
        // at this row's game for the call — the same swap the image matrix does before a per-row fetch.
        byte[]? bytes;
        var prev = _imgGame;
        try { _imgGame = g; bytes = ImgFetchWebBytes(w); }
        catch { bytes = null; }
        finally { _imgGame = prev; }
        if (bytes == null || bytes.Length == 0) return false;

        try
        {
            string dest = st.IsManual
                ? ManualLibrary.FreeDestination(root, plat, title, w.Region, DocWebExt(w))
                : ManualLibrary.FreeMusicDestination(root, plat, title, MusWebExt(w));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            try { ImageAdsWriter.WriteForDownload(dest, w, dbId, plat); } catch { }

            if (st.IsManual)
            {
                // Filed AND designated: the additional-document entry is what makes other regions reachable
                // at all (LaunchBox's detection exposes one manual), ManualPath is the designation itself.
                DmAddDocument(g, dest, DocRegionalName(dest, w.Region));
                try { g.ManualPath = DocStore(dest); } catch { }
            }
            else try { g.MusicPath = DocStore(dest); } catch { }
            return true;
        }
        catch { return false; }
    }

    private static void DmAddDocument(IGame g, string abs, string name)
    {
        try
        {
            if (g.AddNewAdditionalApplication() is HostAdditionalApplication h)
            {
                h.ApplicationPath = DocStore(abs);
                h.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(abs) : name;
                h.Section = HostAdditionalApplication.DocumentSection;
            }
        }
        catch (Exception ex) { Console.WriteLine("[docs] add additional failed: " + ex.Message); }
    }
}
