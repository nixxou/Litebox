// Edit Game → Media → Documents. LaunchBox splits this into two DISTINCT things and so do we:
//
//   • the MAIN MANUAL — a single file, stored as <ManualPath> on the <Game> (IGame.ManualPath).
//   • ADDITIONAL DOCUMENTS — <AdditionalApplication> records marked <Section>Document</Section> (the SDK
//     interface hides Section, so we read/write it via the concrete HostAdditionalApplication).
//
// LaunchBox references documents by ARBITRARY paths (relative to the LB root) and never copies them. We keep
// that compatibility BUT prefer a tidy <LB>\Manuals\<Platform>\ home: a file under it (with a document
// extension) is "managed" (solid border); anything else is "external" (dashed border). On add we offer
// Use-here / Move / Copy. When we store a path we write it RELATIVE when it's under the LB root (clean +
// portable, LB-style) and ABSOLUTE otherwise (no ..\..\..\ chains for external files).
//
// Thumbnails: PDF (bundled PDFium first-page render), CBZ/ZIP (first image), TXT (first lines), DOCX (first
// extracted text) get a real preview; DOC / HTML / CBR fall back to a type badge. Cached in cache\thumbs\docs.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;
using LbApiHost.Host.Integrations;
using LbApiHost.Host.Media;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private IGame DocGame => _editGames[0];

    // Extensions LaunchBox recognises as manuals/documents (mirrors ExtendDB's manual set).
    private static readonly HashSet<string> DocExts = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".cbz", ".cbr", ".zip", ".txt", ".htm", ".html", ".doc", ".docx" };
    private static readonly HashSet<string> DocImageExts = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private static readonly Color DocManagedColor = Color.FromArgb(120, 185, 130);   // green — in Manuals\<Platform>
    private static readonly Color DocExternalColor = Color.FromArgb(150, 152, 162);  // grey — referenced elsewhere
    private static readonly Color DocManualAccent = Color.FromArgb(235, 190, 70);    // gold — the single "Manual" slot

    private int DocCellW => S(150);
    private int DocCellH => S(196);
    private int DocThumbH => DocCellH - S(52);

    private Panel? _docHost;

    // Web download sources (per open-editor session).
    private bool _docShowWeb, _docShowEmu;
    private readonly Dictionary<string, List<EmuMoviesCatalog.EmuMedia>?> _docEmuCache = new(StringComparer.Ordinal);

    // ── Page ────────────────────────────────────────────────────────────────
    private Control BuildDocumentsPage()
    {
        var container = new Panel { BackColor = Bg, Dock = DockStyle.Fill };
        var host = new Panel { BackColor = Bg, AutoScroll = true, Dock = DockStyle.Fill };

        var bar = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Bg };
        var add = DlgBtn("＋ Add Document…", Color.FromArgb(45, 95, 60));
        add.AutoSize = false; add.SetBounds(S(4), S(6), S(150), S(28)); add.Enabled = !_readOnly;
        add.Click += (_, _) => DocAdd();
        bar.Controls.Add(add);

        // Web download sources — same chips as the image/video editors. Purple = the offline database (native +
        // ExtendDB), blue = EmuMovies (live). A manual is a GameImages row Type='Manual', so it downloads exactly
        // like an image; downloaded manuals land managed (under Manuals\<Platform>\) with an ADS provenance stamp.
        int chipX = S(164);
        void AddChip(CheckBox c, int w) { c.SetBounds(chipX, S(8), w, S(26)); bar.Controls.Add(c); chipX += w + S(10); }
        int dbId0 = Safe(() => DocGame.LaunchBoxDbId) ?? -1;
        if (MetadataDb.Available && dbId0 > 0)
            AddChip(SourceChip("Web (database)", WebPurple, _docShowWeb, on => { _docShowWeb = on; DocRefresh(); }), S(158));
        if (ImgEmuAvailable(DocGame))
            AddChip(SourceChip("EmuMovies", EmuBlue, _docShowEmu, on => { _docShowEmu = on; DocRefresh(); }), S(124));

        if (!PdfThumbnailer.Available)
            bar.Controls.Add(new Label { Text = "PDF thumbnails need pdfium (deploys on first launch).", AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Location = new Point(chipX + S(6), S(12)) });

        container.Controls.Add(host);
        container.Controls.Add(bar);
        DocPopulate(host);
        return container;
    }

    private void DocPopulate(Panel host)
    {
        _docHost = host;
        foreach (Control c in host.Controls) DocDisposePics(c);
        host.Controls.Clear();

        var inner = new Panel { BackColor = Bg, Location = Point.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
        host.Controls.Add(inner);
        int y = S(10);

        // Missing-file warning strip + one-click "unlink all missing".
        int missing = DocMissingCount();
        if (missing > 0 && !_readOnly)
        {
            var warn = new Label { Text = $"⚠  {missing} document(s) point to a file that no longer exists.", AutoSize = false, ForeColor = Color.FromArgb(220, 150, 90), BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(S(12), y, S(420), S(24)), TextAlign = ContentAlignment.MiddleLeft };
            inner.Controls.Add(warn);
            var unlink = DlgBtn("Unlink all missing", Color.FromArgb(120, 70, 50)); unlink.AutoSize = false; unlink.SetBounds(S(438), y, S(150), S(24));
            unlink.Click += (_, _) => DocUnlinkAllMissing();
            inner.Controls.Add(unlink);
            y += S(32);
        }

        // ── Manual (single slot) ──
        var mh = new Label { Text = "━━  Manual", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg };
        mh.SetBounds(S(12), y, S(400), S(26)); inner.Controls.Add(mh); y += S(30);

        string manual = DocManualAbs();
        if (!string.IsNullOrEmpty(manual))
        {
            var cell = DocTile(manual, null, isManual: true);
            cell.Location = new Point(S(16), y); inner.Controls.Add(cell);
        }
        else
        {
            var none = new Label
            {
                Text = "No manual set — use “＋ Add Document…” and choose “Set as Manual”.", AutoSize = false,
                ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            };
            none.SetBounds(S(16), y + S(4), S(560), S(26)); inner.Controls.Add(none);
        }
        y += DocCellH + S(12);

        // ── Additional documents (grid) ──
        var ah = new Label { Text = "━━  Additional documents", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg };
        ah.SetBounds(S(12), y, S(400), S(26)); inner.Controls.Add(ah); y += S(30);

        var docs = DocAdditional();
        if (docs.Count == 0)
        {
            var none = new Label
            {
                Text = "No additional documents.", AutoSize = false,
                ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            };
            none.SetBounds(S(16), y + S(4), S(400), S(26)); inner.Controls.Add(none);
            y += S(30);
        }
        else
        {
            int cols = Math.Max(1, (DocAvailWidth(host)) / DocCellW);
            int x = S(16), col = 0;
            foreach (var (app, abs) in docs)
            {
                if (col == cols) { col = 0; x = S(16); y += DocCellH; }
                var cell = DocTile(abs, app, isManual: false);
                cell.Location = new Point(x, y); inner.Controls.Add(cell);
                x += DocCellW; col++;
            }
            y += DocCellH + S(8);
        }

        DocAppendWeb(inner, host, ref y);
    }

    private int DocAvailWidth(Panel host) => (host.ClientSize.Width > S(200) ? host.ClientSize.Width : S(900)) - S(24) - S(16);

    private void DocRefresh() { if (_docHost != null) DocPopulate(_docHost); }

    // ── Tile ──────────────────────────────────────────────────────────────────
    private Panel DocTile(string absPath, HostAdditionalApplication? app, bool isManual)
    {
        var cell = new Panel { Size = new Size(DocCellW, DocCellH), BackColor = Bg };
        bool exists = !string.IsNullOrEmpty(absPath) && File.Exists(absPath);
        bool managed = exists && DocIsManaged(absPath);
        // Border: gold for the manual slot; else DOTTED and coloured by the download source (blue = EmuMovies,
        // purple = database) when the file carries an :info origin; else the managed(green)/external(grey) style.
        Color? src = exists && !isManual ? DocSourceColor(DocAdsOrigin(absPath)) : null;
        Color border = isManual ? DocManualAccent : (src ?? (managed ? DocManagedColor : DocExternalColor));
        DashStyle style = !exists ? DashStyle.Dash
                        : src != null ? DashStyle.Dot
                        : managed ? DashStyle.Solid : DashStyle.Dash;

        // Border on the CELL, AROUND the thumbnail — not on a panel the (fill-docked) PictureBox would cover.
        cell.Paint += (_, e) =>
        {
            using var pen = new Pen(border, S(2)) { DashStyle = style };
            e.Graphics.DrawRectangle(pen, S(4), S(4), DocCellW - S(8), DocThumbH);
        };
        var pic = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        pic.SetBounds(S(6), S(6), DocCellW - S(12), DocThumbH - S(4));
        cell.Controls.Add(pic);

        if (exists) DocLoadThumb(pic, absPath);
        else { var o = pic.Image; pic.Image = DocBadge(".missing", DocCellW - S(16), DocThumbH - S(8)); o?.Dispose(); }

        void OpenIt() { if (exists) DocOpen(absPath); }
        void Menu(Point pt) => DocMenu(absPath, app, isManual, exists, managed).Show(pic, pt);
        // LEFT opens the document; RIGHT opens the menu. (Control.Click fires for right-click too on some
        // controls, which was opening the file on right-click — gate on the button explicitly.)
        pic.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) OpenIt(); else if (e.Button == MouseButtons.Right) Menu(e.Location); };

        string fileName = string.IsNullOrEmpty(absPath) ? "(unset)" : Path.GetFileName(absPath);
        string display = isManual ? fileName : (app != null && !string.IsNullOrWhiteSpace(app.Name) ? app.Name : fileName);
        var name = new Label { Text = display, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        name.SetBounds(S(4), DocThumbH + S(6), DocCellW - S(8), S(16));
        cell.Controls.Add(name);

        string ext = Path.GetExtension(absPath).TrimStart('.').ToUpperInvariant();
        string tag = isManual ? "Manual" : "Doc";
        string loc = !exists ? "missing" : (managed ? "managed" : "external");
        var info = new Label { Text = $"{tag}  ·  {ext}  ·  {loc}", ForeColor = exists ? (managed ? DocManagedColor : DocExternalColor) : Color.FromArgb(200, 110, 100), BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false, AutoEllipsis = true };
        info.SetBounds(S(4), DocThumbH + S(24), DocCellW - S(8), S(16));
        cell.Controls.Add(info);

        return cell;
    }

    private ContextMenuStrip DocMenu(string absPath, HostAdditionalApplication? app, bool isManual, bool exists, bool managed)
    {
        var m = ThemedMenu();
        if (exists)
        {
            m.Items.Add(new ToolStripMenuItem("Open").WithClick(() => DocOpen(absPath)));
            m.Items.Add(new ToolStripMenuItem("Show in Explorer").WithClick(() => DocReveal(absPath)));
            m.Items.Add(new ToolStripMenuItem("Info…").WithClick(() => DocShowInfo(absPath, isManual)));
        }

        if (!_readOnly)
        {
            if (isManual)
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem("Replace…").WithClick(() => DocReplaceManual()));
                if (exists && !managed && DocManualsDir().Length > 0)
                {
                    m.Items.Add(new ToolStripMenuItem("Move into Manuals folder").WithClick(() => DocRelocateManual(absPath, move: true)));
                    m.Items.Add(new ToolStripMenuItem("Copy into Manuals folder").WithClick(() => DocRelocateManual(absPath, move: false)));
                }
                m.Items.Add(new ToolStripMenuItem(exists ? "Clear manual" : "Unlink missing manual").WithClick(() => { DocSetManual(""); DocRefresh(); }));
            }
            else if (app != null)
            {
                m.Items.Add(new ToolStripSeparator());
                // Reorder — the additional-document order is the LaunchBox list order; swap with the adjacent one.
                var docs = DocAdditional();
                int idx = docs.FindIndex(d => ReferenceEquals(d.app, app));
                if (idx > 0) m.Items.Add(new ToolStripMenuItem("Move up").WithClick(() => { app.SwapPositionWith(docs[idx - 1].app); DocRefresh(); }));
                if (idx >= 0 && idx < docs.Count - 1) m.Items.Add(new ToolStripMenuItem("Move down").WithClick(() => { app.SwapPositionWith(docs[idx + 1].app); DocRefresh(); }));

                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem("Promote to Manual").WithClick(() => { DocPromote(app); DocRefresh(); }));
                m.Items.Add(new ToolStripMenuItem("Rename…").WithClick(() => { if (DocRename(app)) DocRefresh(); }));
                if (exists && !managed && DocManualsDir().Length > 0)
                {
                    m.Items.Add(new ToolStripMenuItem("Move into Manuals folder").WithClick(() => DocRelocateApp(app, absPath, move: true)));
                    m.Items.Add(new ToolStripMenuItem("Copy into Manuals folder").WithClick(() => DocRelocateApp(app, absPath, move: false)));
                }
                m.Items.Add(new ToolStripSeparator());
                if (!exists) m.Items.Add(new ToolStripMenuItem("Unlink (remove reference)").WithClick(() => { try { DocGame.TryRemoveAdditionalApplication(app); } catch { } DocRefresh(); }));
                m.Items.Add(new ToolStripMenuItem("Delete document").WithClick(() => { DocDeleteApp(app); DocRefresh(); }));
            }
        }
        return m;
    }

    /// <summary>ADS provenance (:info origin / native region · :crc32 · file size) + path — mirrors the image/video Info.</summary>
    private void DocShowInfo(string absPath, bool isManual)
    {
        var sb = new StringBuilder();
        sb.AppendLine(isManual ? "Manual" : "Additional document");
        sb.AppendLine(absPath);
        sb.AppendLine();
        try { sb.AppendLine($"Size:  {new FileInfo(absPath).Length / 1024.0:0.#} KB"); } catch { }
        sb.AppendLine($"Location:  {(DocIsManaged(absPath) ? "managed (Manuals folder)" : "external (referenced in place)")}");
        sb.AppendLine();
        string crc = FileMetaStore.Read(absPath, FileMetaStore.StreamCrc32);
        var info = ImageInfoBridge.ReadAny(absPath);
        sb.AppendLine("── ADS metadata " + (ImageInfoBridge.Available ? "(via ExtendDB reader)" : "(native)") + " ──");
        sb.AppendLine($"CRC32 (:crc32):  {(string.IsNullOrEmpty(crc) ? "(none)" : crc)}");
        if (info is ImageInfo i)
        {
            sb.AppendLine($"Origin:  {(string.IsNullOrEmpty(i.Origin) ? "(none)" : i.Origin)}");
            sb.AppendLine($"Native region:  {(string.IsNullOrEmpty(i.NativeRegion) ? "(none)" : i.NativeRegion)}");
            sb.AppendLine($"Database Id:  {i.DatabaseId}");
            sb.AppendLine($"CRC32 (:info):  {i.Crc32}");
            sb.AppendLine($"Duplicate:  {i.Duplicate}");
            sb.AppendLine($"File type:  {(string.IsNullOrEmpty(i.FileType) ? "(none)" : i.FileType)}");
            sb.AppendLine($"File size:  {i.FileSize}");
            sb.AppendLine($"Source:  {(string.IsNullOrEmpty(i.OriginalUrl) ? "(none)" : i.OriginalUrl)}");
        }
        else sb.AppendLine("(:info):  (none)");
        MessageBox.Show(this, sb.ToString(), "Document info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Data / path helpers ─────────────────────────────────────────────────
    private static string DocLbRoot() => MediaResolver.LbRoot ?? "";
    private string DocManualsDir()
    {
        string root = DocLbRoot(); string plat = Safe(() => DocGame.Platform) ?? "";
        return (root.Length > 0 && plat.Length > 0) ? Path.Combine(root, "Manuals", plat) : "";
    }

    /// <summary>A stored path (LB writes it relative to the LB root, or absolute) → absolute.</summary>
    private static string DocResolve(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        try { return Path.IsPathRooted(stored) ? stored : (DocLbRoot().Length > 0 ? Path.GetFullPath(Path.Combine(DocLbRoot(), stored)) : stored); }
        catch { return stored; }
    }

    /// <summary>Absolute → stored form: RELATIVE to the LB root when under it (clean + portable), else ABSOLUTE
    /// (no ..\..\ chains for external files — the user's preference).</summary>
    private static string DocStore(string abs)
    {
        string root = DocLbRoot();
        if (root.Length == 0 || string.IsNullOrEmpty(abs)) return abs;
        try
        {
            string full = Path.GetFullPath(abs), rootFull = Path.GetFullPath(root);
            if (full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(rootFull, full);
        }
        catch { }
        return abs;
    }

    private bool DocIsManaged(string abs)
    {
        string dir = DocManualsDir();
        if (dir.Length == 0 || string.IsNullOrEmpty(abs)) return false;
        try { return abs.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && DocExts.Contains(Path.GetExtension(abs)); }
        catch { return false; }
    }

    private string DocManualAbs() => DocResolve(Safe(() => DocGame.ManualPath));
    private void DocSetManual(string abs) { try { DocGame.ManualPath = string.IsNullOrEmpty(abs) ? "" : DocStore(abs); } catch { } }

    private List<(HostAdditionalApplication app, string abs)> DocAdditional()
    {
        var list = new List<(HostAdditionalApplication, string)>();
        try
        {
            foreach (var a in DocGame.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                if (a is HostAdditionalApplication h && h.IsDocument)
                    list.Add((h, DocResolve(h.ApplicationPath)));
        }
        catch { }
        return list;
    }

    private void DocAddAdditional(string abs, string? name)
    {
        try
        {
            if (DocGame.AddNewAdditionalApplication() is HostAdditionalApplication h)
            {
                h.ApplicationPath = DocStore(abs);
                h.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(abs) : name;
                h.Section = HostAdditionalApplication.DocumentSection;
            }
        }
        catch (Exception ex) { Console.WriteLine("[docs] add additional failed: " + ex.Message); }
    }

    private int DocMissingCount()
    {
        int n = 0;
        string m = DocManualAbs(); if (!string.IsNullOrEmpty(m) && !File.Exists(m)) n++;
        foreach (var (_, abs) in DocAdditional()) if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) n++;
        return n;
    }

    private void DocUnlinkAllMissing()
    {
        string m = DocManualAbs(); if (!string.IsNullOrEmpty(m) && !File.Exists(m)) DocSetManual("");
        foreach (var (app, abs) in DocAdditional())
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) { try { DocGame.TryRemoveAdditionalApplication(app); } catch { } }
        DocRefresh();
    }

    // Every managed/local doc this game already has, for owned-dedup of the web candidates.
    private IEnumerable<string> DocOwnedPaths()
    {
        string m = DocManualAbs();
        if (!string.IsNullOrEmpty(m) && File.Exists(m)) yield return m;
        foreach (var (_, abs) in DocAdditional()) if (!string.IsNullOrEmpty(abs) && File.Exists(abs)) yield return abs;
    }

    // The ADS :info origin of a downloaded doc (null when never stamped — hand-added / external, which get no ADS).
    private static string? DocAdsOrigin(string path)
    {
        try { return ImageInfoBridge.ReadAny(path) is ImageInfo i ? i.Origin : null; }
        catch { return null; }
    }

    // Border colour by the source we downloaded from (from :info origin): EmuMovies blue, any database origin
    // (screenscraper / launchbox / …) purple. Null = never stamped (a file you added yourself).
    private static Color? DocSourceColor(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return null;
        if (origin.Contains("emumovies", StringComparison.OrdinalIgnoreCase)) return EmuBlue;
        return WebPurple;
    }

    private void DocPromote(HostAdditionalApplication app)
    {
        string newAbs = DocResolve(app.ApplicationPath);
        string oldManual = Safe(() => DocGame.ManualPath) ?? "";
        try { DocGame.TryRemoveAdditionalApplication(app); } catch { }
        if (!string.IsNullOrWhiteSpace(oldManual))   // demote the previous manual to an additional document
        {
            string oldAbs = DocResolve(oldManual);
            DocAddAdditional(oldAbs, Path.GetFileNameWithoutExtension(oldAbs));
        }
        DocSetManual(newAbs);
    }

    private void DocDeleteApp(HostAdditionalApplication app)
    {
        string abs = DocResolve(app.ApplicationPath);
        bool managed = DocIsManaged(abs);
        var res = MessageBox.Show(this,
            managed && File.Exists(abs)
                ? $"Remove this document?\n\n{Path.GetFileName(abs)}\n\nIt's in the Manuals folder — also delete the file from disk?"
                : $"Remove this document reference?\n\n{Path.GetFileName(abs)}",
            "Delete document", managed && File.Exists(abs) ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (res == DialogResult.Cancel || res == DialogResult.None) return;
        try { DocGame.TryRemoveAdditionalApplication(app); } catch { }
        if (res == DialogResult.Yes && managed) { try { File.Delete(abs); } catch { } }   // only offer file delete for managed
    }

    private bool DocRename(HostAdditionalApplication app)
    {
        string cur = app.Name ?? "";
        if (!DocPrompt("Rename document", "Name:", cur, out string name)) return false;
        try { app.Name = name; } catch { }
        return true;
    }

    // ── Add / relocate ────────────────────────────────────────────────────────
    private void DocAdd()
    {
        if (_readOnly) return;
        using var ofd = new OpenFileDialog
        {
            Title = "Add document(s)", Multiselect = true, CheckFileExists = true,
            Filter = "Documents (*.pdf;*.cbz;*.cbr;*.zip;*.txt;*.htm;*.html;*.doc;*.docx)|*.pdf;*.cbz;*.cbr;*.zip;*.txt;*.htm;*.html;*.doc;*.docx|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;

        bool manualSet = !string.IsNullOrWhiteSpace(Safe(() => DocGame.ManualPath));
        if (!DocAskAddOptions(ofd.FileNames.Length, manualSet, out bool asManual, out int place)) return;

        bool firstIsManual = asManual;
        foreach (var src in ofd.FileNames)
        {
            string dest = DocApplyPlacement(src, place, firstIsManual);
            if (string.IsNullOrEmpty(dest)) continue;
            if (firstIsManual) { DocSetManual(dest); firstIsManual = false; }   // only the first picked becomes the manual
            else DocAddAdditional(dest, null);
        }
        DocRefresh();
    }

    // place: 0 = use here · 1 = move · 2 = copy. Returns the final path to store (src on failure/use-here).
    // Managed layout: the MANUAL is <Manuals>\<Platform>\<base><ext> (base = game title, +guid on collision);
    // ADDITIONAL docs go into a per-game sub-folder <Manuals>\<Platform>\<base>\ KEEPING their original name.
    private string DocApplyPlacement(string src, int place, bool asManual)
    {
        if (place == 0) return src;
        string dir = DocManualsDir();
        if (dir.Length == 0) return src;
        try
        {
            string dest = asManual ? DocManualDest(dir, src) : DocAdditionalDest(dir, src);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) { try { File.Delete(dest); } catch { } }   // manual: replace; additional dest is already unique
            if (place == 2) File.Copy(src, dest, overwrite: true);
            else File.Move(src, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't place the document into the Manuals folder:\n" + ex.Message + "\n\nReferencing it in place instead.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return src;
        }
    }

    private string DocManualDest(string dir, string src) => Path.Combine(dir, DocBaseName() + Path.GetExtension(src));

    private string DocAdditionalDest(string dir, string src)
    {
        string folder = Path.Combine(dir, DocBaseName());
        string name = Path.GetFileName(src);
        string dest = Path.Combine(folder, name);
        string noext = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        int n = 1;
        while (File.Exists(dest)) dest = Path.Combine(folder, $"{noext}-{n++:D2}{ext}");   // keep original name; number a dup
        return dest;
    }

    /// <summary>The managed base name for this game (game title, or title.&lt;guid8&gt; on collision with a DIFFERENT
    /// game). The manual file is &lt;base&gt;[.&lt;REGION&gt;]&lt;ext&gt; and the additional sub-folder is &lt;base&gt;\ — both share
    /// this base. Reuses the base this game's existing managed docs already use.</summary>
    private string DocBaseName()
    {
        string dir = DocManualsDir();
        string sani = MediaResolver.Sanitize(Safe(() => DocGame.Title) ?? "");
        if (string.IsNullOrEmpty(sani)) sani = "manual";
        string guid = (Safe(() => DocGame.Id) ?? "").Replace("-", "");
        string guidForm = guid.Length >= 8 ? sani + "." + guid.Substring(0, 8) : sani;
        if (dir.Length == 0) return sani;

        // Prefer the additional sub-folder's name — it IS the base, verbatim (no region/ext to strip).
        foreach (var (_, abs) in DocAdditional())
        {
            if (!DocIsManaged(abs)) continue;
            string? parent = Path.GetDirectoryName(abs);
            try { if (parent != null && !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(parent); }
            catch { }
        }
        // Else derive it from the managed manual's filename, stripping a trailing region code (…\Foo.FR.pdf → Foo).
        string cur = DocManualAbs();
        if (DocIsManaged(cur)) return DocStripRegion(Path.GetFileNameWithoutExtension(cur));

        // No managed artifact yet: a plain-named file/folder present here belongs to another game → disambiguate.
        try
        {
            bool collide = Directory.Exists(Path.Combine(dir, sani))
                || DocExts.Any(e => File.Exists(Path.Combine(dir, sani + e)))
                || DocRegionCodeSet.Any(rc => DocExts.Any(e => File.Exists(Path.Combine(dir, sani + "." + rc + e))));
            return collide ? guidForm : sani;
        }
        catch { return sani; }
    }

    // ── Region codes (kept in the managed file name, e.g. Final Fantasy 7.FR.pdf) ──
    private static readonly Dictionary<string, string> DocRegionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["France"] = "FR", ["Japan"] = "JP", ["United States"] = "US", ["USA"] = "US", ["Spain"] = "ES",
        ["Germany"] = "DE", ["Europe"] = "EU", ["Italy"] = "IT", ["Australia"] = "AU", ["Netherlands"] = "NL",
        ["Sweden"] = "SE", ["Brazil"] = "BR", ["Korea"] = "KR", ["China"] = "CN", ["Russia"] = "RU",
        ["Asia"] = "AS", ["North America"] = "NA", ["United Kingdom"] = "UK", ["Canada"] = "CA", ["Finland"] = "FI",
        ["Norway"] = "NO", ["Denmark"] = "DK", ["Poland"] = "PL", ["Portugal"] = "PT", ["Greece"] = "GR",
        ["World"] = "", ["none"] = "", [""] = "",
    };
    private static readonly HashSet<string> DocRegionCodeSet =
        new(DocRegionCodes.Values.Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>Short region code for a manual filename, or "" when none/World. Falls back to the first two letters.</summary>
    private static string DocRegionCode(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return "";
        if (DocRegionCodes.TryGetValue(region.Trim(), out var c)) return c;
        var letters = new string(region.Where(char.IsLetter).ToArray());
        return letters.Length >= 2 ? letters.Substring(0, 2).ToUpperInvariant() : letters.ToUpperInvariant();
    }

    private static string DocStripRegion(string fileNameNoExt)
    {
        int dot = fileNameNoExt.LastIndexOf('.');
        return (dot > 0 && DocRegionCodeSet.Contains(fileNameNoExt.Substring(dot + 1))) ? fileNameNoExt.Substring(0, dot) : fileNameNoExt;
    }

    private void DocReplaceManual()
    {
        if (_readOnly) return;
        using var ofd = new OpenFileDialog
        {
            Title = "Set manual", CheckFileExists = true,
            Filter = "Documents (*.pdf;*.cbz;*.cbr;*.zip;*.txt;*.htm;*.html;*.doc;*.docx)|*.pdf;*.cbz;*.cbr;*.zip;*.txt;*.htm;*.html;*.doc;*.docx|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        if (!DocAskAddOptions(1, false, out _, out int place)) return;
        string dest = DocApplyPlacement(ofd.FileName, place, asManual: true);
        DocSetManual(dest);
        DocRefresh();
    }

    private void DocRelocateManual(string abs, bool move)
    {
        string dest = DocApplyPlacement(abs, move ? 1 : 2, asManual: true);
        if (!string.Equals(dest, abs, StringComparison.OrdinalIgnoreCase)) { DocSetManual(dest); DocRefresh(); }
    }

    private void DocRelocateApp(HostAdditionalApplication app, string abs, bool move)
    {
        string dest = DocApplyPlacement(abs, move ? 1 : 2, asManual: false);
        if (!string.Equals(dest, abs, StringComparison.OrdinalIgnoreCase)) { try { app.ApplicationPath = DocStore(dest); } catch { } DocRefresh(); }
    }

    /// <summary>Role (Manual vs Additional) + placement (use-here / move / copy) picker. Returns false on cancel.</summary>
    private bool DocAskAddOptions(int count, bool manualSet, out bool asManual, out int place)
    {
        asManual = false; place = 0;
        bool canManage = DocManualsDir().Length > 0;
        using var f = NewDialog("Add document", 460, canManage ? 280 : 210);

        var lblRole = new Label { Text = "Add as:", Location = new Point(S(16), S(16)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        f.Controls.Add(lblRole);
        var rbAdd = new RadioButton { Text = count > 1 ? "Additional documents" : "Additional document", Location = new Point(S(120), S(14)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = true };
        var rbMan = new RadioButton { Text = manualSet ? "Manual (replaces the current one)" : "Manual (main)", Location = new Point(S(120), S(40)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        f.Controls.Add(rbAdd); f.Controls.Add(rbMan);
        if (count > 1) { var hint = new Label { Text = "(the first file becomes the manual, the rest additional)", Location = new Point(S(120), S(64)), AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f) }; f.Controls.Add(hint); }

        RadioButton rbHere = null!, rbMove = null!, rbCopy = null!;
        if (canManage)
        {
            var lblP = new Label { Text = "Location:", Location = new Point(S(16), S(104)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            f.Controls.Add(lblP);
            rbHere = new RadioButton { Text = "Use the file where it is (external)", Location = new Point(S(120), S(102)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            rbCopy = new RadioButton { Text = "Copy into Manuals\\" + (Safe(() => DocGame.Platform) ?? ""), Location = new Point(S(120), S(128)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = true };
            rbMove = new RadioButton { Text = "Move into Manuals\\" + (Safe(() => DocGame.Platform) ?? ""), Location = new Point(S(120), S(154)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            f.Controls.Add(rbHere); f.Controls.Add(rbCopy); f.Controls.Add(rbMove);
        }

        bool ok = false;
        DialogButtons(f, out var okBtn, out var cancel);
        okBtn.Click += (_, _) => { ok = true; f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        if (f.ShowDialog(this) != DialogResult.OK || !ok) return false;

        asManual = rbMan.Checked;
        place = !canManage ? 0 : (rbHere.Checked ? 0 : rbMove.Checked ? 1 : 2);
        return true;
    }

    private bool DocPrompt(string title, string label, string initial, out string value)
    {
        value = initial;
        using var f = NewDialog(title, 440, 150);
        var lbl = new Label { Text = label, Location = new Point(S(14), S(18)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var tb = new TextBox { Location = new Point(S(14), S(42)), Width = S(400), Text = initial, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        f.Controls.Add(lbl); f.Controls.Add(tb);
        bool ok = false;
        DialogButtons(f, out var okBtn, out var cancel);
        okBtn.Click += (_, _) => { ok = true; f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        if (f.ShowDialog(this) != DialogResult.OK || !ok) return false;
        value = tb.Text.Trim();
        return true;
    }

    private void DocOpen(string abs)
    {
        try { Process.Start(new ProcessStartInfo { FileName = abs, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't open:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void DocReveal(string abs)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + abs + "\"", UseShellExecute = true }); }
        catch { }
    }

    // ── Thumbnails ────────────────────────────────────────────────────────────
    private void DocLoadThumb(PictureBox pic, string absPath)
    {
        if (!pic.IsHandleCreated) { void H(object? _, EventArgs __) { pic.HandleCreated -= H; DocLoadThumb(pic, absPath); } pic.HandleCreated += H; return; }
        int maxW = DocCellW - S(16), maxH = DocThumbH - S(8);
        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { bmp = DocThumb(absPath, Math.Max(maxW, maxH)); } catch { }
            bmp ??= DocBadge(Path.GetExtension(absPath), maxW, maxH);
            try
            {
                if (pic.IsHandleCreated) pic.BeginInvoke(new Action(() => { if (!pic.IsDisposed) { var o = pic.Image; pic.Image = bmp; o?.Dispose(); } else bmp.Dispose(); }));
                else bmp.Dispose();
            }
            catch { bmp.Dispose(); }
        });
    }

    /// <summary>Real preview for a document (disk-cached), or null when the type has no preview (→ badge).</summary>
    private Bitmap? DocThumb(string absPath, int maxDim)
    {
        string ext = Path.GetExtension(absPath).ToLowerInvariant();
        bool renderable = ext is ".pdf" or ".cbz" or ".zip" or ".txt" or ".docx";
        if (!renderable) return null;

        string? cache = DocThumbCachePath(absPath, maxDim);
        if (cache != null && File.Exists(cache))
        {
            try { using var ms = new MemoryStream(File.ReadAllBytes(cache)); return new Bitmap(Image.FromStream(ms)); }
            catch { try { File.Delete(cache); } catch { } }
        }

        Bitmap? bmp = ext switch
        {
            ".pdf" => PdfThumbnailer.RenderFirstPage(absPath, maxDim),
            ".cbz" or ".zip" => DocRenderComic(absPath, maxDim),
            ".txt" => DocRenderText(File.Exists(absPath) ? SafeReadLines(absPath, 40) : null, maxDim),
            ".docx" => DocRenderText(DocxText(absPath), maxDim),
            _ => null,
        };
        if (bmp != null && cache != null)
        {
            try { var tmp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp"; bmp.Save(tmp, ImageFormat.Png); try { File.Move(tmp, cache, false); } catch { File.Delete(tmp); } } catch { }
        }
        return bmp;
    }

    private static string? DocThumbCachePath(string absPath, int maxDim)
    {
        try
        {
            var fi = new FileInfo(absPath); if (!fi.Exists) return null;
            string key = absPath.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + maxDim;
            using var md5 = MD5.Create();
            return Path.Combine(ThumbCache.DocFolder, "doc-" + Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".png");
        }
        catch { return null; }
    }

    private static string[]? SafeReadLines(string path, int max)
    { try { return File.ReadLines(path).Take(max).ToArray(); } catch { return null; } }

    private static Bitmap? DocRenderComic(string path, int maxDim)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var img = zip.Entries.Where(e => DocImageExts.Contains(Path.GetExtension(e.Name)))
                                 .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (img == null) return null;
            using var s = img.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
            using var src = Image.FromStream(ms);
            double sc = Math.Min(1.0, Math.Min((double)maxDim / src.Width, (double)maxDim / src.Height));
            return new Bitmap(src, Math.Max(1, (int)(src.Width * sc)), Math.Max(1, (int)(src.Height * sc)));
        }
        catch { return null; }
    }

    private static string[]? DocxText(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml"); if (entry == null) return null;
            using var s = entry.Open(); using var sr = new StreamReader(s);
            string xml = sr.ReadToEnd();
            var sb = new StringBuilder();
            foreach (Match m in Regex.Matches(xml, @"<w:p\b|<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline))
            {
                if (m.Value.StartsWith("<w:p", StringComparison.Ordinal)) sb.Append('\n');
                else sb.Append(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
            }
            var lines = sb.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(40).ToArray();
            return lines.Length > 0 ? lines : null;
        }
        catch { return null; }
    }

    private static Bitmap? DocRenderText(string[]? lines, int maxDim)
    {
        if (lines == null || lines.Length == 0) return null;
        try
        {
            int w = (int)(maxDim * 0.77), h = maxDim;
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            using var f = new Font("Consolas", 6.2f);
            using var br = new SolidBrush(Color.FromArgb(30, 30, 30));
            float y = 4;
            foreach (var raw in lines)
            {
                string ln = raw.Length > 64 ? raw.Substring(0, 64) : raw;
                g.DrawString(ln, f, br, 4, y);
                y += f.Height; if (y > h - f.Height) break;
            }
            return bmp;
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, Color> DocTypeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Color.FromArgb(220, 90, 80), [".doc"] = Color.FromArgb(90, 140, 220), [".docx"] = Color.FromArgb(90, 140, 220),
        [".htm"] = Color.FromArgb(220, 150, 70), [".html"] = Color.FromArgb(220, 150, 70), [".txt"] = Color.FromArgb(160, 165, 175),
        [".cbz"] = Color.FromArgb(150, 120, 210), [".cbr"] = Color.FromArgb(150, 120, 210), [".zip"] = Color.FromArgb(150, 120, 210),
    };

    private Bitmap DocBadge(string ext, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(30, 30, 38));
        // A dog-eared page glyph.
        Color accent = ext == ".missing" ? Color.FromArgb(200, 110, 100) : (DocTypeColors.TryGetValue(ext, out var c) ? c : Color.FromArgb(150, 152, 162));
        int pw = (int)(w * 0.5), ph = (int)(h * 0.6), px = (w - pw) / 2, py = (int)(h * 0.16);
        int ear = (int)(pw * 0.28);
        using (var body = new SolidBrush(Color.FromArgb(52, 52, 62)))
        using (var pen = new Pen(accent, 2f))
        {
            var pts = new[] { new Point(px, py), new Point(px + pw - ear, py), new Point(px + pw, py + ear), new Point(px + pw, py + ph), new Point(px, py + ph) };
            g.FillPolygon(body, pts); g.DrawPolygon(pen, pts);
            g.DrawLines(pen, new[] { new Point(px + pw - ear, py), new Point(px + pw - ear, py + ear), new Point(px + pw, py + ear) });
        }
        string label = ext == ".missing" ? "?" : ext.TrimStart('.').ToUpperInvariant();
        using var lf = new Font("Segoe UI", Math.Max(6f, h * 0.11f), FontStyle.Bold);
        var sz = g.MeasureString(label, lf);
        using var tb = new SolidBrush(accent);
        g.DrawString(label, lf, tb, (w - sz.Width) / 2, py + ph - sz.Height - 4);
        return bmp;
    }

    private static void DocDisposePics(Control c)
    {
        if (c is PictureBox pb) { var im = pb.Image; pb.Image = null; try { im?.Dispose(); } catch { } }
        foreach (Control ch in c.Controls) DocDisposePics(ch);
    }

    // ── Web download sources (database + EmuMovies) ─────────────────────────────
    private void DocAppendWeb(Panel inner, Panel host, ref int y)
    {
        var g = DocGame;
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        bool webOn = _docShowWeb && MetadataDb.Available && dbId > 0;
        bool emuOn = _docShowEmu && ImgEmuAvailable(g);
        if (!webOn && !emuOn) return;

        // Both sources unify to WebImage → ONE download path (ImgFetchWebBytes + ImageAdsWriter).
        var cands = new List<(MetadataDb.WebImage w, string source)>();
        bool loading = false, dbNeedsExtend = false;

        // Skip anything we ALREADY own: the same multi-level check as the image/video editors — the ADS-recorded
        // CRC first, then the ADS FileSize, then the on-disk size for files with no ADS size (BuildEmuOwned/EmuOwns).
        var owned = BuildEmuOwned(DocOwnedPaths());

        if (webOn)
            try
            {
                var rows = MetadataDb.ManualsForGame(dbId);
                int total = rows.Count;
                // EVERY manual row is screenscraper / emumovies (there are no launchbox ones), and those need
                // ExtendDB's per-origin fetcher (screenscraper needs API credentials). Without it, only launchbox
                // rows are CDN-fetchable — so for manuals that means NONE. Mirror the image editor: drop the
                // un-fetchable rows and flag that ExtendDB is required. EmuMovies (below) stays ExtendDB-free.
                if (!MediaApiBridge.Available) rows = rows.Where(r => r.IsLaunchbox).ToList();
                foreach (var w in rows) if (!EmuOwns(owned, w.Crc32, w.FileSize)) cands.Add((w, "web"));
                if (rows.Count == 0 && total > 0) dbNeedsExtend = true;
            }
            catch { }

        if (emuOn)
        {
            string key = Safe(() => g.Id) ?? Safe(() => g.Title) ?? "";
            if (!_docEmuCache.TryGetValue(key, out var em)) { DocTriggerEmuFetch(g, key); loading = true; }
            else if (em == null) loading = true;
            else foreach (var m in em.Where(m => string.Equals(m.LbType, "Manual", StringComparison.OrdinalIgnoreCase)))
                 {
                     var w = ImgEmuToWeb(m, dbId);
                     if (!EmuOwns(owned, w.Crc32, w.FileSize)) cands.Add((w, "emu"));
                 }
        }

        if (cands.Count == 0 && !loading && !dbNeedsExtend) return;

        y += S(6);
        var hdr = new Label { Text = "⬇  Download a manual — left-click = as manual · right-click for options", ForeColor = SubFg, Font = new Font("Segoe UI", 9f, FontStyle.Italic), AutoSize = false, BackColor = Bg };
        hdr.SetBounds(S(12), y, S(700), S(24)); inner.Controls.Add(hdr); y += S(30);

        if (dbNeedsExtend)
        {
            inner.Controls.Add(new Label { Text = "This game's database manuals are ScreenScraper/EmuMovies — downloading them needs the ExtendDB plugin loaded (API credentials). Use the EmuMovies source, or load ExtendDB.", AutoSize = false, ForeColor = Color.FromArgb(220, 170, 90), BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(S(16), y, DocAvailWidth(host), S(34)) });
            y += S(38);
        }

        int cols = Math.Max(1, DocAvailWidth(host) / DocCellW);
        int x = S(16), col = 0;
        foreach (var (w, source) in cands)
        {
            if (col == cols) { col = 0; x = S(16); y += DocCellH; }
            var cell = DocWebTile(w, source);
            cell.Location = new Point(x, y); inner.Controls.Add(cell);
            x += DocCellW; col++;
        }
        if (cands.Count > 0) y += DocCellH;

        if (loading)
        {
            inner.Controls.Add(new Label { Text = "Querying EmuMovies…", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(S(16), y) });
            y += S(24);
        }
    }

    private Panel DocWebTile(MetadataDb.WebImage w, string source)
    {
        var cell = new Panel { Size = new Size(DocCellW, DocCellH), BackColor = Bg };
        Color border = source == "emu" ? EmuBlue : WebPurple;
        var frame = new Panel { BackColor = Color.FromArgb(18, 18, 24) };
        frame.SetBounds(S(4), S(4), DocCellW - S(8), DocThumbH);
        frame.Paint += (_, e) => { using var pen = new Pen(border, S(2)); e.Graphics.DrawRectangle(pen, 1, 1, frame.Width - 3, frame.Height - 3); };
        string ext = DocWebExt(w);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        pic.Image = DocBadge(ext, DocCellW - S(16), DocThumbH - S(8));
        frame.Controls.Add(pic); cell.Controls.Add(frame);

        bool asManualDefault = string.IsNullOrEmpty(DocManualAbs());
        void Menu(Point pt)
        {
            var m = ThemedMenu();
            m.Items.Add(new ToolStripMenuItem("Download as Manual").WithClick(() => DocDownloadWeb(w, true)));
            m.Items.Add(new ToolStripMenuItem("Download as additional document").WithClick(() => DocDownloadWeb(w, false)));
            if (w.IsLaunchbox) m.Items.Add(new ToolStripMenuItem("Open in browser").WithClick(() => DocOpenUrl(w.Url)));
            m.Show(pic, pt);
        }
        pic.MouseUp += (_, e) => { if (_readOnly) return; if (e.Button == MouseButtons.Left) DocDownloadWeb(w, asManualDefault); else if (e.Button == MouseButtons.Right) Menu(e.Location); };

        var cap = new Label { Text = (source == "emu" ? "EmuMovies" : "Database") + (string.IsNullOrEmpty(w.Region) ? "" : "  ·  " + w.Region), ForeColor = border, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, AutoEllipsis = true };
        cap.SetBounds(S(4), DocThumbH + S(6), DocCellW - S(8), S(16)); cell.Controls.Add(cap);
        var info = new Label { Text = "download  ·  " + ext.TrimStart('.').ToUpperInvariant(), ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false };
        info.SetBounds(S(4), DocThumbH + S(24), DocCellW - S(8), S(16)); cell.Controls.Add(info);
        return cell;
    }

    /// <summary>A REAL document extension for a web row. The FileName is often a URL (a ScreenScraper API call
    /// "…mediaManuelJeu.php?…filetype=pdf&lbname=sc.manuel-pdf-sc", or a URL-encoded EmuMovies path), so a naive
    /// Path.GetExtension gives garbage. Prefer an explicit filetype= in the URL, then FileType, then the real
    /// filename extension — and only if it's a known document extension; manuals default to PDF.</summary>
    private static string DocWebExt(MetadataDb.WebImage w)
    {
        string? cand = null;
        var m = Regex.Match(w.FileName ?? "", @"[?&]filetype=([A-Za-z0-9]{2,5})\b");
        if (m.Success) cand = "." + m.Groups[1].Value.ToLowerInvariant();
        if (cand == null && !string.IsNullOrEmpty(w.FileType))
            cand = (w.FileType.StartsWith(".", StringComparison.Ordinal) ? w.FileType : "." + w.FileType).ToLowerInvariant();
        if (cand == null) { var e = Path.GetExtension(w.FileName ?? ""); if (!string.IsNullOrEmpty(e)) cand = e.ToLowerInvariant(); }
        return (cand != null && DocExts.Contains(cand)) ? cand : ".pdf";
    }

    private void DocDownloadWeb(MetadataDb.WebImage w, bool asManual)
    {
        if (_readOnly) return;
        var g = DocGame;
        string plat = Safe(() => g.Platform) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        string dir = DocManualsDir();
        if (dir.Length == 0) { MessageBox.Show(this, "This game has no platform / id — can't store a managed manual.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        byte[]? bytes;
        UseWaitCursor = true;
        try { bytes = ImgFetchWebBytes(w); } catch { bytes = null; } finally { UseWaitCursor = false; }
        if (bytes == null || bytes.Length == 0) { MessageBox.Show(this, "Download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        // Clean name: <base>[.<REGION>]<ext>, never derived from the (URL) FileName. The additional variant goes
        // into the per-game sub-folder. Region kept when available (e.g. Final Fantasy 7.FR.pdf).
        string ext = DocWebExt(w);
        string rc = DocRegionCode(w.Region);
        string suffix = rc.Length > 0 ? "." + rc : "";
        string bn = DocBaseName();
        string dest;
        try
        {
            if (asManual)
            {
                // Replace any existing managed manual (even a different region) so we don't leave an orphan.
                string oldMan = DocManualAbs();
                if (DocIsManaged(oldMan) && File.Exists(oldMan)) { try { File.Delete(oldMan); } catch { } }
                dest = Path.Combine(dir, bn + suffix + ext);
                if (File.Exists(dest)) { try { File.Delete(dest); } catch { } }
            }
            else
            {
                // EmuMovies rows carry a real filename in the URL — KEEP it (URL-decoded, e.g.
                // "Earthworm%20Jim%20%28USA%29.pdf" → "Earthworm Jim (USA)"). ScreenScraper rows are a
                // query-string API call with no real name → fall back to <base>[.<REGION>]. Numbered on a clash.
                string folder = Path.Combine(dir, bn);
                string fileBase = DocPreferredName(w) ?? (bn + suffix);
                dest = Path.Combine(folder, fileBase + ext);
                int n = 1; while (File.Exists(dest)) dest = Path.Combine(folder, $"{fileBase}-{n++:D2}{ext}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            try { ImageAdsWriter.WriteForDownload(dest, w, dbId, plat); } catch { }   // ADS provenance — managed only
        }
        catch (Exception ex) { MessageBox.Show(this, "Save failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        if (asManual) DocSetManual(dest);
        else DocAddAdditional(dest, Path.GetFileNameWithoutExtension(dest));   // the clean file name, not the URL
        DocRefresh();
    }

    /// <summary>Keep a web row's real filename when it has one (URL-decoded + made filesystem-safe), else null
    /// for a query-string API URL. Extension stripped — the caller adds the resolved one.</summary>
    private static string? DocPreferredName(MetadataDb.WebImage w)
    {
        string fn = w.FileName ?? "";
        if (fn.Length == 0 || fn.Contains('?')) return null;
        string bn = System.Net.WebUtility.UrlDecode(Path.GetFileNameWithoutExtension(fn)) ?? "";
        foreach (var c in Path.GetInvalidFileNameChars()) bn = bn.Replace(c, ' ');
        bn = bn.Trim();
        return string.IsNullOrWhiteSpace(bn) ? null : bn;
    }

    private void DocTriggerEmuFetch(IGame g, string key)
    {
        _docEmuCache[key] = null;   // loading sentinel — a re-populate before the fetch lands won't re-trigger
        string romPath = Safe(() => g.ApplicationPath) ?? "";
        string title = Safe(() => g.Title) ?? "";
        string plat = Safe(() => g.Platform) ?? "";
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<EmuMoviesCatalog.EmuMedia> found = new();
            try { var api = EmuApi(); if (api != null) found = await EmuMoviesCatalog.ResolveForGameAsync(api, title, romPath, plat); }
            catch { }
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { _docEmuCache[key] = found; DocRefresh(); })); }
            catch { }
        });
    }

    private void DocOpenUrl(string url) { try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { } }
}
