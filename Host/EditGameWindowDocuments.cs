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
    // Internal: the thumb GC filters the Manuals tree with the SAME set when marking valid thumbs.
    internal static readonly HashSet<string> DocExts = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".cbz", ".cbr", ".zip", ".txt", ".htm", ".html", ".doc", ".docx" };
    private static readonly HashSet<string> DocImageExts = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private static readonly Color DocManagedColor = Color.FromArgb(120, 185, 130);   // green — in Manuals\<Platform>
    private static readonly Color DocExternalColor = Color.FromArgb(150, 152, 162);  // grey — referenced elsewhere
    private static readonly Color DocManualAccent = Color.FromArgb(235, 190, 70);    // gold — the single "Manual" slot

    private int DocCellW => S(150);
    private int DocCellH => S(196);
    private int DocThumbH => DocCellH - S(52);
    // Additional tiles carry a third caption line (the LABEL when it differs from the file name).
    private int DocCellHA => DocCellH + S(16);

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

        // Web download sources — same chips as the image/video editors. Purple "ExtendDB" = the extended
        // database, the ONLY offline-DB manual source (LaunchBox's own Metadata.db has no manual rows, so no
        // orange "LaunchBox DB" chip here — unlike the image pages); blue = EmuMovies (live). A manual is a
        // GameImages row Type='Manual', so it downloads exactly like an image; downloaded manuals land managed
        // (under Manuals\<Platform>\) with an ADS provenance stamp.
        int chipX = S(164);
        void AddChip(CheckBox c, int w) { c.SetBounds(chipX, S(8), w, S(26)); bar.Controls.Add(c); chipX += w + S(10); }
        int dbId0 = Safe(() => DocGame.LaunchBoxDbId) ?? -1;
        if (MediaApiBridge.ModuleActive && dbId0 > 0)   // Base module on + extended DB present
            AddChip(SourceChip("ExtendDB", WebPurple, _docShowWeb, on => { _docShowWeb = on; DocRefresh(); }), S(112));
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

        // ManualPath readout — the RAW stored field, always visible. La designation est un CHAMP ;
        // la page le dit telle quelle (valeur relative/absolue du XML), sans ouvrir Info. Rebati a
        // chaque DocRefresh, donc a jour apres chaque action. Vide = selection auto.
        string mpStored = Safe(() => DocGame.ManualPath) ?? "";
        bool mpSet = !string.IsNullOrWhiteSpace(mpStored);
        bool mpMissing = mpSet && !File.Exists(DocResolve(mpStored));
        var mpLbl = new Label
        {
            Text = "ManualPath :  " + (!mpSet ? "(not set — auto selection)" : mpStored + (mpMissing ? "   (missing)" : "")),
            ForeColor = !mpSet ? SubFg : mpMissing ? Color.FromArgb(200, 110, 100) : DocManualAccent,
            BackColor = Bg, Font = new Font("Segoe UI", 8.5f, mpSet ? FontStyle.Regular : FontStyle.Italic),
            AutoSize = false, AutoEllipsis = true,
        };
        mpLbl.SetBounds(S(12), y, DocAvailWidth(host), S(20));
        if (mpSet) new ToolTip().SetToolTip(mpLbl, DocResolve(mpStored));   // resolved absolute path on hover
        inner.Controls.Add(mpLbl); y += S(26);

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

        // ── Manuals — the whole COLLECTION, like the image thumbs ──
        // L ordre est celui du parcours de resolution (ManualsAll : priorite de region puis
        // alphabet) — l element 0 est le choix AUTO, exactement ce que l ecran de pause ouvrira.
        // La designation (<ManualPath>) se pose PAR-DESSUS : sa tuile est en or PLEIN ; le choix
        // auto est en or POINTILLE — la difference auto/designe se lit a la bordure et au badge.
        var manuals = DocManualsAll();
        string pinnedAbs = MediaResolver.Override(Safe(() => DocGame.ManualPath)) ?? "";
        string storedAbs = DocManualAbs();                       // resolved stored path, even when the file is gone
        string selectedManual = pinnedAbs.Length > 0 ? pinnedAbs : (manuals.Count > 0 ? manuals[0] : "");
        bool manualDesignated = pinnedAbs.Length > 0;

        var mh = new Label
        {
            Text = "━━  Manuals" + (selectedManual.Length == 0 ? ""
                   : manualDesignated ? "   ·   designated" : "   ·   auto — region priority picks (right-click one to set it)"),
            ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg,
        };
        mh.SetBounds(S(12), y, S(700), S(26)); inner.Controls.Add(mh); y += S(30);

        int mcols = Math.Max(1, DocAvailWidth(host) / DocCellW);
        int mx = S(16), mcol = 0; bool anyManual = false;
        void PlaceManual(Panel cell)
        {
            if (mcol == mcols) { mcol = 0; mx = S(16); y += DocCellH; }
            cell.Location = new Point(mx, y); inner.Controls.Add(cell);
            mx += DocCellW; mcol++; anyManual = true;
        }
        if (manualDesignated && !manuals.Any(p => DocPathEq(p, pinnedAbs)))
            PlaceManual(DocManualTile(pinnedAbs, selected: true, designated: true));      // designe hors collection (externe)
        else if (!manualDesignated && storedAbs.Length > 0)
            PlaceManual(DocManualTile(storedAbs, selected: false, designated: true));     // designe mais INTROUVABLE
        foreach (var p in manuals)
            PlaceManual(DocManualTile(p, DocPathEq(p, selectedManual), manualDesignated && DocPathEq(p, pinnedAbs)));
        if (!anyManual)
        {
            var none = new Label
            {
                Text = "No manual — use “＋ Add Document…” or download one below.", AutoSize = false,
                ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            };
            none.SetBounds(S(16), y + S(4), S(560), S(26)); inner.Controls.Add(none);
            y += S(34);
        }
        else y += DocCellH + S(12);

        // ── Additional documents (grid) ── — INTEGRALE, dans l ordre LaunchBox. Les entrees des
        // manuels telecharges y figurent donc aussi (le meme fichier apparait dans les deux
        // grilles) : c est la liste reelle du XML, sans filtrage d affichage.
        var docs = DocAdditional();
        var ah = new Label { Text = "━━  Additional documents", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg };
        ah.SetBounds(S(12), y, S(400), S(26)); inner.Controls.Add(ah); y += S(30);

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
                if (col == cols) { col = 0; x = S(16); y += DocCellHA; }
                var cell = DocTile(abs, app);
                cell.Location = new Point(x, y); inner.Controls.Add(cell);
                x += DocCellW; col++;
            }
            y += DocCellHA + S(8);
        }

        DocAppendWeb(inner, host, ref y);
    }

    private int DocAvailWidth(Panel host) => (host.ClientSize.Width > S(200) ? host.ClientSize.Width : S(900)) - S(24) - S(16);

    private void DocRefresh() { if (_docHost != null) DocPopulate(_docHost); }

    // ── Tiles ─────────────────────────────────────────────────────────────────
    /// <summary>Additional-document tile. Ligne 1 = nom de FICHIER, ligne 2 = etat ; le LABEL de
    /// l entree (app.Name) s affiche en ligne 3 quand il differe du nom de fichier — c est lui que
    /// LaunchBox montre dans ses listes, il merite d etre visible sans ouvrir le menu.</summary>
    private Panel DocTile(string absPath, HostAdditionalApplication? app)
    {
        var cell = new Panel { Size = new Size(DocCellW, DocCellHA), BackColor = Bg };
        bool exists = !string.IsNullOrEmpty(absPath) && File.Exists(absPath);
        bool managed = exists && DocIsManaged(absPath);
        // Border: DOTTED and coloured by the download source (blue = EmuMovies, purple = database)
        // when the file carries an :info origin; else the managed(green)/external(grey) style.
        Color? src = exists ? DocSourceColor(DocAdsOrigin(absPath)) : null;
        Color border = src ?? (managed ? DocManagedColor : DocExternalColor);
        DashStyle style = !exists ? DashStyle.Dash
                        : src != null ? DashStyle.Dot
                        : managed ? DashStyle.Solid : DashStyle.Dash;
        DocTileChrome(cell, absPath, border, style, exists, pt => DocMenu(absPath, app, exists, managed).Show(cell.Controls[0], pt));

        string fileName = string.IsNullOrEmpty(absPath) ? "(unset)" : Path.GetFileName(absPath);
        // La region d un document, quand on la connait (nom de dossier de la convention, sinon
        // l ADS), figure dans la ligne d etat — c est elle que l utilisateur cherche.
        string region = exists ? DocManualRegion(absPath) : "";
        string loc = !exists ? "missing" : (managed ? "managed" : "external");
        DocTileCaptions(cell, fileName,
            "Doc" + (region.Length > 0 ? "  ·  " + region : "") + $"  ·  {DocExtLabel(absPath)}  ·  {loc}",
            exists ? (managed ? DocManagedColor : DocExternalColor) : Color.FromArgb(200, 110, 100));

        string label = (app?.Name ?? "").Trim();
        bool labelDiff = label.Length > 0
            && !label.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            && !label.Equals(Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);
        if (labelDiff)
        {
            var lb = new Label { Text = label, ForeColor = Color.FromArgb(190, 195, 205), BackColor = Bg, Font = new Font("Segoe UI", 7.5f, FontStyle.Italic), AutoSize = false, AutoEllipsis = true };
            lb.SetBounds(S(4), DocThumbH + S(42), DocCellW - S(8), S(16));
            cell.Controls.Add(lb);
            new ToolTip().SetToolTip(lb, "Label:  " + label);
        }
        return cell;
    }

    /// <summary>Manual-collection tile. The border tells the mode apart at a glance: SOLID gold =
    /// designated (&lt;ManualPath&gt;), DOTTED gold = the auto pick (region priority); the badge repeats
    /// it ("Manual · main" / "Manual · auto"). Others keep the managed/external/source styles.</summary>
    private Panel DocManualTile(string absPath, bool selected, bool designated)
    {
        var cell = new Panel { Size = new Size(DocCellW, DocCellH), BackColor = Bg };
        bool exists = !string.IsNullOrEmpty(absPath) && File.Exists(absPath);
        bool managed = exists && DocIsManaged(absPath);
        var app = DocAppFor(absPath);   // l entree additionnelle du meme fichier — elle suit les operations

        Color? src = exists && !selected ? DocSourceColor(DocAdsOrigin(absPath)) : null;
        Color border = selected || designated ? DocManualAccent : (src ?? (managed ? DocManagedColor : DocExternalColor));
        DashStyle style = !exists ? DashStyle.Dash
                        : selected ? (designated ? DashStyle.Solid : DashStyle.Dot)
                        : src != null ? DashStyle.Dot
                        : managed ? DashStyle.Solid : DashStyle.Dash;
        DocTileChrome(cell, absPath, border, style, exists,
            pt => DocManualMenu(absPath, exists, managed, designated, app).Show(cell.Controls[0], pt));

        string tag = "Manual" + (designated ? " · main" : selected ? " · auto" : "");
        string region = DocManualRegion(absPath);
        string loc = !exists ? "missing" : (managed ? "managed" : "external");
        DocTileCaptions(cell, Path.GetFileName(absPath),
            tag + (region.Length > 0 ? "  ·  " + region : "") + $"  ·  {DocExtLabel(absPath)}  ·  {loc}",
            !exists ? Color.FromArgb(200, 110, 100) : selected || designated ? DocManualAccent : managed ? DocManagedColor : DocExternalColor);
        return cell;
    }

    private static string DocExtLabel(string absPath) => Path.GetExtension(absPath).TrimStart('.').ToUpperInvariant();

    /// <summary>Shared tile plumbing: border paint, thumbnail, left-open / right-menu wiring.</summary>
    private void DocTileChrome(Panel cell, string absPath, Color border, DashStyle style, bool exists, Action<Point> menu)
    {
        // Border on the CELL, AROUND the thumbnail — not on a panel the (fill-docked) PictureBox would cover.
        cell.Paint += (_, e) =>
        {
            using var pen = new Pen(border, S(2)) { DashStyle = style };
            e.Graphics.DrawRectangle(pen, S(4), S(4), DocCellW - S(8), DocThumbH);
        };
        var pic = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        pic.SetBounds(S(6), S(6), DocCellW - S(12), DocThumbH - S(4));
        cell.Controls.Add(pic);
        cell.Controls.SetChildIndex(pic, 0);   // the menus anchor on Controls[0]

        if (exists) DocLoadThumb(pic, absPath);
        else { var o = pic.Image; pic.Image = DocBadge(".missing", DocCellW - S(16), DocThumbH - S(8)); o?.Dispose(); }

        // LEFT opens the document; RIGHT opens the menu. (Control.Click fires for right-click too on some
        // controls, which was opening the file on right-click — gate on the button explicitly.)
        pic.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { if (exists) DocOpen(absPath); } else if (e.Button == MouseButtons.Right) menu(e.Location); };
    }

    private void DocTileCaptions(Panel cell, string display, string infoText, Color infoColor)
    {
        var name = new Label { Text = display, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        name.SetBounds(S(4), DocThumbH + S(6), DocCellW - S(8), S(16));
        cell.Controls.Add(name);
        var info = new Label { Text = infoText, ForeColor = infoColor, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false, AutoEllipsis = true };
        info.SetBounds(S(4), DocThumbH + S(24), DocCellW - S(8), S(16));
        cell.Controls.Add(info);
        new ToolTip().SetToolTip(info, infoText);   // the line often ellipsizes at this cell width
    }

    private ContextMenuStrip DocMenu(string absPath, HostAdditionalApplication? app, bool exists, bool managed)
    {
        var m = ThemedMenu();
        if (exists)
        {
            m.Items.Add(new ToolStripMenuItem("Open").WithClick(() => DocOpen(absPath)));
            m.Items.Add(new ToolStripMenuItem("Show in Explorer").WithClick(() => DocReveal(absPath)));
            m.Items.Add(new ToolStripMenuItem("Info…").WithClick(() => DocShowInfo(absPath, isManual: false)));
        }

        if (!_readOnly)
        {
            if (app != null)
            {
                m.Items.Add(new ToolStripSeparator());
                // Reorder — the additional-document order is the LaunchBox list order; swap with the adjacent one.
                var docs = DocAdditional();
                int idx = docs.FindIndex(d => ReferenceEquals(d.app, app));
                if (idx > 0) m.Items.Add(new ToolStripMenuItem("Move up").WithClick(() => { app.SwapPositionWith(docs[idx - 1].app); DocRefresh(); }));
                if (idx >= 0 && idx < docs.Count - 1) m.Items.Add(new ToolStripMenuItem("Move down").WithClick(() => { app.SwapPositionWith(docs[idx + 1].app); DocRefresh(); }));

                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem("Set as manual").WithClick(() => { DocPromote(app); DocRefresh(); }));
                m.Items.Add(new ToolStripMenuItem("Change label…").WithClick(() => { if (DocRename(app)) DocRefresh(); }));
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

    /// <summary>Right-click menu of a manual-collection tile. Region and number live in the PATH
    /// (folder / -NN), so changing them is moving/renaming the file — the additional-document entry
    /// and the designation follow it.</summary>
    private ContextMenuStrip DocManualMenu(string absPath, bool exists, bool managed, bool designated, HostAdditionalApplication? app)
    {
        var m = ThemedMenu();
        if (exists)
        {
            m.Items.Add(new ToolStripMenuItem("Open").WithClick(() => DocOpen(absPath)));
            m.Items.Add(new ToolStripMenuItem("Show in Explorer").WithClick(() => DocReveal(absPath)));
            m.Items.Add(new ToolStripMenuItem("Info…").WithClick(() => DocShowInfo(absPath, isManual: true)));
        }
        if (_readOnly) return m;

        m.Items.Add(new ToolStripSeparator());
        if (designated)
            m.Items.Add(new ToolStripMenuItem(exists ? "Clear designation (back to auto)" : "Unlink missing manual")
                .WithClick(() => { DocSetManual(""); DocRefresh(); }));
        else if (exists)
            m.Items.Add(new ToolStripMenuItem("Set as manual").WithClick(() => { DocDesignateManual(absPath); DocRefresh(); }));

        if (exists && managed)
        {
            // La region d un manuel EST son dossier : en changer, c est deplacer le fichier, sous
            // le nom conventionnel (numero libre du dossier cible).
            var mv = new ToolStripMenuItem("Move to region");
            string cur = DocManualRegion(absPath);
            foreach (var r in LbRegions.Fallback)
            {
                string rr = r;
                mv.DropDownItems.Add(new ToolStripMenuItem(rr) { Checked = string.Equals(rr, cur, StringComparison.OrdinalIgnoreCase) }
                    .WithClick(() => DocMoveManualToRegion(absPath, rr, app)));
            }
            m.Items.Add(mv);

            string sani = MediaResolver.Sanitize(Safe(() => DocGame.Title) ?? "");
            if (GameMediaRenamer.TryPlain(Path.GetFileNameWithoutExtension(absPath), sani, out int curNum, allowUnnumbered: true))
                m.Items.Add(new ToolStripMenuItem("Change number…").WithClick(() => DocChangeManualNumber(absPath, sani, curNum, app)));
        }

        if (exists && !managed && DocManualsDir().Length > 0)
        {
            m.Items.Add(new ToolStripMenuItem("Move into Manuals folder").WithClick(() => DocRelocateManual(absPath, move: true)));
            m.Items.Add(new ToolStripMenuItem("Copy into Manuals folder").WithClick(() => DocRelocateManual(absPath, move: false)));
        }

        if (exists && managed)
        {
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Delete file").WithClick(() => DocDeleteManualFile(absPath, app)));
        }
        return m;
    }

    /// <summary>TOUS les manuels du jeu, dans l ordre de resolution (element 0 = choix auto).</summary>
    private List<string> DocManualsAll()
    {
        string plat = Safe(() => DocGame.Platform) ?? "", title = Safe(() => DocGame.Title) ?? "";
        Guid.TryParse(Safe(() => DocGame.Id) ?? "", out var gid);
        if (plat.Length == 0 || title.Length == 0) return new();
        try { return MediaResolver.ManualsAll(plat, gid, title); } catch { return new(); }
    }

    /// <summary>L entree additionnelle qui pointe sur ce fichier, s il y en a une.</summary>
    private HostAdditionalApplication? DocAppFor(string absPath)
        => DocAdditional().FirstOrDefault(d => DocPathEq(d.abs, absPath)).app;

    private static bool DocPathEq(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>La region affichable d un document : le nom du dossier ou il vit quand il est sous
    /// Manuals\&lt;plat&gt;\ (la convention la met la — un rangement libre s affiche verbatim, c est
    /// honnete), sinon la region native de l ADS. "" quand on ne sait pas.</summary>
    private string DocManualRegion(string absPath)
    {
        try
        {
            string dir = DocManualsDir();
            string? parent = Path.GetDirectoryName(absPath);
            if (dir.Length > 0 && parent != null
                && Path.GetFullPath(absPath).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(parent) ?? "";
        }
        catch { }
        try { return ImageInfoBridge.ReadAny(absPath) is ImageInfo i ? (i.NativeRegion ?? "") : ""; }
        catch { return ""; }
    }

    /// <summary>Pose &lt;ManualPath&gt;. L ANCIEN manuel designe, quand il disparaitrait de la page
    /// (hors de la collection, pas deja un document additionnel), est conserve en document
    /// additionnel — remplacer le manuel ne cache jamais l ancien fichier.</summary>
    private void DocDesignateManual(string abs)
    {
        string old = DocManualAbs();
        if (!string.IsNullOrEmpty(old) && !DocPathEq(old, abs) && File.Exists(old)
            && !DocManualsAll().Any(p => DocPathEq(p, old))
            && !DocAdditional().Any(d => DocPathEq(d.abs, old)))
            DocAddAdditional(old, Path.GetFileNameWithoutExtension(old));
        DocSetManual(abs);
    }

    private void DocMoveManualToRegion(string absPath, string region, HostAdditionalApplication? app)
    {
        string root = DocLbRoot(), plat = Safe(() => DocGame.Platform) ?? "", title = Safe(() => DocGame.Title) ?? "";
        if (root.Length == 0 || plat.Length == 0 || title.Length == 0) return;
        try
        {
            if (string.Equals(Path.GetFullPath(Media.ManualLibrary.RegionDir(root, plat, region)),
                              Path.GetFullPath(Path.GetDirectoryName(absPath) ?? ""), StringComparison.OrdinalIgnoreCase))
                return;   // deja dans cette region
        }
        catch { }
        string dest = Media.ManualLibrary.FreeDestination(root, plat, title, region, Path.GetExtension(absPath));
        try { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Move(absPath, dest); }
        catch (Exception ex) { MessageBox.Show(this, "Move failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        DocManualFollow(absPath, dest, app, region);
        DocRefresh();
    }

    private void DocChangeManualNumber(string absPath, string sani, int curNum, HostAdditionalApplication? app)
    {
        string cur = curNum == GameMediaRenamer.Unnumbered ? "" : curNum.ToString();
        if (!DocPrompt("Change number", "Number (1–99, empty = bare name):", cur, out string v)) return;
        v = v.Trim();
        int n = GameMediaRenamer.Unnumbered;
        if (v.Length > 0 && (!int.TryParse(v, out n) || n < 1 || n > 99))
        { MessageBox.Show(this, "Enter a number between 1 and 99, or leave empty for the bare name.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (n == curNum) return;
        string dest = Path.Combine(Path.GetDirectoryName(absPath) ?? "",
            (n == GameMediaRenamer.Unnumbered ? sani : $"{sani}-{n:D2}") + Path.GetExtension(absPath));
        if (File.Exists(dest))
        { MessageBox.Show(this, "That name is taken in this folder:\n" + Path.GetFileName(dest), "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try { File.Move(absPath, dest); }
        catch (Exception ex) { MessageBox.Show(this, "Rename failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        DocManualFollow(absPath, dest, app, null);
        DocRefresh();
    }

    /// <summary>Apres un deplacement/renommage de manuel : l entree additionnelle et la designation
    /// suivent le fichier — un chemin stocke qui pointe dans le vide n aide personne.</summary>
    private void DocManualFollow(string oldAbs, string dest, HostAdditionalApplication? app, string? regionForName)
    {
        if (app != null)
        {
            try
            {
                app.ApplicationPath = DocStore(dest);
                // Seuls les libelles generes ("Manual (Region)…") sont recalcules — un nom saisi
                // par l utilisateur lui appartient.
                if ((app.Name ?? "").StartsWith("Manual (", StringComparison.Ordinal))
                    app.Name = DocRegionalName(dest, regionForName ?? DocManualRegion(dest));
            }
            catch { }
        }
        if (DocPathEq(DocManualAbs(), oldAbs)) DocSetManual(dest);
    }

    private void DocDeleteManualFile(string absPath, HostAdditionalApplication? app)
    {
        var res = MessageBox.Show(this,
            $"Delete this manual from disk?\n\n{Path.GetFileName(absPath)}"
            + (app != null ? "\n\nIts additional-document entry is removed too." : ""),
            "Delete manual", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (res != DialogResult.OK) return;
        try { File.Delete(absPath); } catch { }
        if (app != null) { try { DocGame.TryRemoveAdditionalApplication(app); } catch { } }
        if (DocPathEq(DocManualAbs(), absPath)) DocSetManual("");
        DocRefresh();
    }

    /// <summary>ADS provenance (:info origin / native region · :crc32 · file size) + path — mirrors the image/video Info.</summary>
    private void DocShowInfo(string absPath, bool isManual) => DocShowInfo(absPath, isManual ? "Manual" : "Additional document");

    /// <summary>Same dialog with a free kind label — the Music page reuses it ("Music").</summary>
    internal void DocShowInfo(string absPath, string kind)
    {
        var sb = new StringBuilder();
        sb.AppendLine(kind);
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

    /// <summary>A stored path (LB writes it relative to the LB root, or absolute) → absolute.
    /// Internal: the thumb GC resolves document paths with the SAME rule before keying.</summary>
    internal static string DocResolve(string? stored)
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

    // Every managed/local doc this game already has (the whole manual collection, the designation,
    // the additional documents), for owned-dedup of the web candidates.
    private IEnumerable<string> DocOwnedPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string m = DocManualAbs();
        if (!string.IsNullOrEmpty(m) && File.Exists(m) && seen.Add(m)) yield return m;
        foreach (var p in DocManualsAll()) if (File.Exists(p) && seen.Add(p)) yield return p;
        foreach (var (_, abs) in DocAdditional()) if (!string.IsNullOrEmpty(abs) && File.Exists(abs) && seen.Add(abs)) yield return abs;
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
        // L entree additionnelle RESTE (elle rend le document atteignable chez LaunchBox, et la
        // grille Manuals la masque deja quand le fichier y figure) ; l ancien manuel designe est
        // conserve par DocDesignateManual quand il disparaitrait de la page.
        DocDesignateManual(DocResolve(app.ApplicationPath));
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
        if (!DocPrompt("Change label", "Label:", cur, out string name)) return false;
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
            if (firstIsManual) { DocDesignateManual(dest); firstIsManual = false; }   // only the first picked becomes the manual
            else
            {
                // Chaque entree additionnelle porte un LABEL — demande a l ajout, nom de fichier
                // par defaut ; Annuler garde le defaut plutot que d abandonner l ajout.
                string label = Path.GetFileNameWithoutExtension(dest);
                DocPrompt("Document label", $"Label for {Path.GetFileName(dest)}:", label, out label);
                DocAddAdditional(dest, label);
            }
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

    /// <summary>Un manuel qui ENTRE dans la bibliotheque suit la convention, qu'il soit designe comme
    /// principal ou non : Manuals\&lt;plat&gt;\&lt;Region&gt;\&lt;NomJeu&gt;-NN.&lt;ext&gt;. L'ancien nommage
    /// « &lt;titre&gt;[.&lt;REGION&gt;] » a la racine n'etait detecte par personne — mesure — et ne tenait que
    /// par le chemin ecrit dans &lt;ManualPath&gt;. Aucune region a l'ajout local : "World".</summary>
    private string DocManualDest(string dir, string src)
    {
        string root = DocLbRoot(), plat = Safe(() => DocGame.Platform) ?? "";
        string title = Safe(() => DocGame.Title) ?? "";
        if (root.Length == 0 || plat.Length == 0 || title.Length == 0)
            return Path.Combine(dir, DocBaseName() + Path.GetExtension(src));
        return Media.ManualLibrary.FreeDestination(root, plat, title, null, Path.GetExtension(src));
    }

    /// <summary>Un document additionnel copié/déplacé dans la bibliothèque va À PLAT dans
    /// Manuals\&lt;plat&gt;\, au nom conventionnel &lt;titre&gt;-NN (numéro libre suivant) — pas de région
    /// pour les documents additionnels. Ainsi nommé il est aussi vu par la détection des manuels,
    /// c'est le choix : un document qu'on range là EST de la famille des manuels. L'ancien
    /// sous-dossier par-jeu ne sert plus qu'en repli quand la convention est impossible.</summary>
    private string DocAdditionalDest(string dir, string src)
    {
        string root = DocLbRoot(), plat = Safe(() => DocGame.Platform) ?? "";
        string title = Safe(() => DocGame.Title) ?? "";
        if (root.Length > 0 && plat.Length > 0 && title.Length > 0)
            return Media.ManualLibrary.FreeDestinationFlat(root, plat, title, Path.GetExtension(src));

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

    // ── LEGACY region codes ("<titre>.FR.pdf" at the platform root) — READ-ONLY support. Nothing
    // writes this shape anymore (the convention is the region FOLDER); DocBaseName still strips it
    // so the few existing files keep answering for their game.
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

    private static string DocStripRegion(string fileNameNoExt)
    {
        int dot = fileNameNoExt.LastIndexOf('.');
        return (dot > 0 && DocRegionCodeSet.Contains(fileNameNoExt.Substring(dot + 1))) ? fileNameNoExt.Substring(0, dot) : fileNameNoExt;
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
        // Chaque groupe de radios vit dans SON conteneur : poses a plat sur la fenetre, les cinq
        // boutons formeraient un seul groupe WinForms et se decocheraient mutuellement.
        var roleGrp = new Panel { Location = new Point(S(120), S(14)), Size = new Size(S(320), S(52)), BackColor = Bg };
        var rbAdd = new RadioButton { Text = count > 1 ? "Additional documents" : "Additional document", Location = new Point(0, 0), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = true };
        var rbMan = new RadioButton { Text = manualSet ? "Manual (replaces the current designation)" : "Manual (main)", Location = new Point(0, S(26)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        roleGrp.Controls.Add(rbAdd); roleGrp.Controls.Add(rbMan);
        f.Controls.Add(roleGrp);
        if (count > 1) { var hint = new Label { Text = "(the first file becomes the manual, the rest additional)", Location = new Point(S(120), S(68)), AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f) }; f.Controls.Add(hint); }

        RadioButton rbHere = null!, rbMove = null!, rbCopy = null!;
        if (canManage)
        {
            var lblP = new Label { Text = "Location:", Location = new Point(S(16), S(104)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            f.Controls.Add(lblP);
            var locGrp = new Panel { Location = new Point(S(120), S(102)), Size = new Size(S(320), S(78)), BackColor = Bg };
            rbHere = new RadioButton { Text = "Use the file where it is (external)", Location = new Point(0, 0), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            rbCopy = new RadioButton { Text = "Copy into Manuals\\" + (Safe(() => DocGame.Platform) ?? ""), Location = new Point(0, S(26)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = true };
            rbMove = new RadioButton { Text = "Move into Manuals\\" + (Safe(() => DocGame.Platform) ?? ""), Location = new Point(0, S(52)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            locGrp.Controls.Add(rbHere); locGrp.Controls.Add(rbCopy); locGrp.Controls.Add(rbMove);
            f.Controls.Add(locGrp);
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
        // Shell, or LB 14's Reader when the UseLbReaderForDocs ini key is on (Media/DocOpener —
        // the same opener the pause screen and the game context menu use).
        try { Media.DocOpener.Open(abs); }
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
            try { bmp = DocThumb(absPath, DocRenderDim); } catch { }   // fixed dim: DPI-independent cache key; the Zoom PictureBox scales down
            bmp ??= DocBadge(Path.GetExtension(absPath), maxW, maxH);
            try
            {
                if (pic.IsHandleCreated) pic.BeginInvoke(new Action(() => { if (!pic.IsDisposed) { var o = pic.Image; pic.Image = bmp; o?.Dispose(); } else bmp.Dispose(); }));
                else bmp.Dispose();
            }
            catch { bmp.Dispose(); }
        });
    }

    /// <summary>Ensure the document's cached preview EXISTS (render + save if missing) without keeping a
    /// bitmap — the bulk cache generator's entry point. True = cached / rendered / nothing to render
    /// (non-previewable type); false = the render failed for a renderable type.
    /// <paramref name="force"/> re-renders and replaces the cached preview ("Regenerate everything") —
    /// transactionally: a failed render leaves the old preview in place.</summary>
    internal static bool DocEnsureThumb(string absPath, bool force = false)
    {
        string ext = Path.GetExtension(absPath).ToLowerInvariant();
        if (ext is not (".pdf" or ".cbz" or ".zip" or ".txt" or ".docx")) return true;
        string? cache = DocThumbCachePath(absPath, DocRenderDim);
        if (cache == null) return false;               // source missing/unreadable
        if (!force && File.Exists(cache)) return true; // HIT
        Bitmap? bmp = null;
        try { bmp = DocThumb(absPath, DocRenderDim, force); return bmp != null; }
        catch { return false; }
        finally { bmp?.Dispose(); }
    }

    /// <summary>Real preview for a document (disk-cached), or null when the type has no preview (→ badge).
    /// <paramref name="force"/> skips the cache read and lets the fresh render REPLACE the cached file —
    /// only once it exists (tmp + move), so a failed render never costs the old preview.</summary>
    private static Bitmap? DocThumb(string absPath, int maxDim, bool force = false)
    {
        string ext = Path.GetExtension(absPath).ToLowerInvariant();
        bool renderable = ext is ".pdf" or ".cbz" or ".zip" or ".txt" or ".docx";
        if (!renderable) return null;

        string? cache = DocThumbCachePath(absPath, maxDim);
        if (!force && cache != null && File.Exists(cache))
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
            // Lazy callers keep first-writer-wins (overwrite:false); the force path replaces the old file.
            try { var tmp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp"; bmp.Save(tmp, ImageFormat.Png); try { File.Move(tmp, cache, force); } catch { try { File.Delete(tmp); } catch { } } } catch { }
        }
        return bmp;
    }

    /// <summary>Render dimension of the cached document previews. FIXED (not DPI-derived) so the cache
    /// key is stable across monitors/zoom levels and the thumb GC can compute it — the Zoom PictureBox
    /// scales the 512px render down to whatever the cell needs.</summary>
    internal const int DocRenderDim = 512;

    private static string? DocThumbCachePath(string absPath, int maxDim)
    {
        try
        {
            var fi = new FileInfo(absPath); if (!fi.Exists) return null;
            return Path.Combine(ThumbCache.DocFolder, DocThumbFileName(absPath, fi.Length, fi.LastWriteTimeUtc.Ticks, maxDim));
        }
        catch { return null; }
    }

    /// <summary>The exact cache FILENAME of a document preview given (path, size, mtime) — single source
    /// of truth for the doc- key, shared with the thumb GC's valid-set (which feeds sizes/dates from an
    /// Everything prefetch or one stat, never re-hashing here).</summary>
    internal static string DocThumbFileName(string absPath, long size, long modifiedTicks, int maxDim = DocRenderDim)
    {
        string key = absPath.ToLowerInvariant() + "|" + size + "|" + modifiedTicks + "|" + maxDim;
        using var md5 = MD5.Create();
        return "doc-" + Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".png";
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
        bool webOn = _docShowWeb && MediaApiBridge.ModuleActive && dbId > 0;
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
                // Explicitly the EXTENDED DB — the "ExtendDB" source's label must stay true even when
                // [Base] UseAsMainDb is off (WebDbPath() would then answer with LaunchBox's own DB,
                // which has no manual rows anyway).
                // Manual rows join the manual collection; Map / Press rows download as ADDITIONAL
                // documents only (they are not manuals — and must not look like one on disk).
                var rows = MetadataDb.DocumentsForGame(MetadataDb.ExtendedDbPath, dbId);
                int total = rows.Count;
                // EVERY document row is screenscraper / emumovies (there are no launchbox ones), and those need
                // ExtendDB's per-origin fetcher (screenscraper needs API credentials). Without it, only launchbox
                // rows are CDN-fetchable — so for documents that means NONE. Mirror the image editor: drop the
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
        var hdr = new Label { Text = "⬇  Download documents — a manual joins the collection above · a map/press kit becomes an additional document · right-click for options", ForeColor = SubFg, Font = new Font("Segoe UI", 9f, FontStyle.Italic), AutoSize = false, BackColor = Bg };
        hdr.SetBounds(S(12), y, DocAvailWidth(host), S(24)); inner.Controls.Add(hdr); y += S(30);

        if (dbNeedsExtend)
        {
            inner.Controls.Add(new Label { Text = "This game's database documents are ScreenScraper/EmuMovies — downloading them needs the ExtendDB plugin loaded (API credentials). Use the EmuMovies source, or load ExtendDB.", AutoSize = false, ForeColor = Color.FromArgb(220, 170, 90), BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(S(16), y, DocAvailWidth(host), S(34)) });
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

        // Une ligne Manual rejoint la collection (et peut EN PLUS etre designee) ; une ligne
        // Map / Press ne peut devenir qu un document additionnel.
        bool isManualRow = string.Equals(w.Type, "Manual", StringComparison.OrdinalIgnoreCase);
        void Menu(Point pt)
        {
            var m = ThemedMenu();
            if (isManualRow)
            {
                m.Items.Add(new ToolStripMenuItem("Download").WithClick(() => DocDownloadWeb(w, false)));
                m.Items.Add(new ToolStripMenuItem("Download and set as manual").WithClick(() => DocDownloadWeb(w, true)));
            }
            else
                m.Items.Add(new ToolStripMenuItem("Download as additional document").WithClick(() => DocDownloadWeb(w, false)));
            if (w.IsLaunchbox) m.Items.Add(new ToolStripMenuItem("Open in browser").WithClick(() => DocOpenUrl(w.Url)));
            m.Show(pic, pt);
        }
        pic.MouseUp += (_, e) => { if (_readOnly) return; if (e.Button == MouseButtons.Left) DocDownloadWeb(w, false); else if (e.Button == MouseButtons.Right) Menu(e.Location); };

        var cap = new Label { Text = (source == "emu" ? "EmuMovies" : "ExtendDB") + (isManualRow ? "" : "  ·  " + w.Type) + (string.IsNullOrEmpty(w.Region) ? "" : "  ·  " + w.Region), ForeColor = border, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, AutoEllipsis = true };
        cap.SetBounds(S(4), DocThumbH + S(6), DocCellW - S(8), S(16)); cell.Controls.Add(cap);
        var info = new Label { Text = "download  ·  " + (isManualRow ? "Manual" : w.Type) + "  ·  " + ext.TrimStart('.').ToUpperInvariant(), ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false, AutoEllipsis = true };
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

        string ext = DocWebExt(w);
        string bn = DocBaseName();
        bool isManualRow = string.Equals(w.Type, "Manual", StringComparison.OrdinalIgnoreCase);
        string dest;
        try
        {
            if (isManualRow)
            {
                // Le manuel telecharge suit la convention : Manuals\<plat>\<Region>\<NomJeu>-NN.<ext>,
                // la region venant du scraper. L ancien nommage « <titre>[.<REGION>] » a la racine
                // n etait detecte par personne — mesure sur LaunchBox — et ne tenait que par le
                // chemin ecrit dans <ManualPath>. Il ne remplace plus l ancien manuel : chaque region
                // a sa place, et c est la priorite qui departage a la LECTURE, pas un effacement a
                // l ecriture.
                string mtitle = Safe(() => g.Title) ?? "";
                dest = plat.Length > 0 && mtitle.Length > 0 && DocLbRoot().Length > 0
                    ? Media.ManualLibrary.FreeDestination(DocLbRoot(), plat, mtitle, w.Region, ext)
                    : Path.Combine(dir, bn + ext);
            }
            else
            {
                // Un plan / press kit n est PAS un manuel : la detection (nom de fichier, tous
                // dossiers confondus) prendrait un fichier au nom conventionnel pour le manuel du
                // jeu — chez LaunchBox comme chez nous. Donc sous-dossier par jeu et nom PARLANT,
                // jamais le nom du jeu nu.
                string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
                string? pref = DocPreferredName(w);
                if (pref != null && sani.Length > 0 && GameMediaRenamer.TryPlain(pref, sani, out _, allowUnnumbered: true))
                    pref = null;   // ce nom serait vu comme un manuel — le nom type prend le relais
                string fileBase = pref ?? $"{bn} ({w.Type}, {Media.ManualLibrary.NormalizeRegion(w.Region)})";
                string folder = Path.Combine(dir, bn);
                dest = Path.Combine(folder, fileBase + ext);
                int k = 1; while (File.Exists(dest)) dest = Path.Combine(folder, $"{fileBase}-{k++:D2}{ext}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            try { ImageAdsWriter.WriteForDownload(dest, w, dbId, plat); } catch { }   // ADS provenance — managed only
        }
        catch (Exception ex) { MessageBox.Show(this, "Save failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        // TOUT document telecharge devient un document additionnel, region dans le libelle : la
        // detection automatique n expose qu UN manuel (mesure sur LaunchBox), les autres regions
        // — et les plans / press kits, qu elle ignore — ne sont atteignables chez lui que par une
        // entree explicite. "Set as manual" pose EN PLUS ManualPath — designation de l utilisateur,
        // jamais un calcul de notre part.
        DocAddAdditional(dest, isManualRow ? DocRegionalName(dest, w.Region)
                                           : $"{w.Type} ({Media.ManualLibrary.NormalizeRegion(w.Region)})");
        if (asManual && isManualRow) DocDesignateManual(dest);
        DocRefresh();
    }

    /// <summary>Keep a web row's real filename when it has one (URL-decoded + made filesystem-safe), else null
    /// for a query-string API URL. Extension stripped — the caller adds the resolved one.</summary>
    /// <summary>Le nom d un document additionnel cree par telechargement : la region y figure,
    /// c est elle que l utilisateur cherche dans la liste. Le -NN du fichier distingue les
    /// doublons d une meme region.</summary>
    private static string DocRegionalName(string dest, string? region)
    {
        string r = Media.ManualLibrary.NormalizeRegion(region);
        string n = Path.GetFileNameWithoutExtension(dest);
        int dash = n.LastIndexOf('-');
        int num = dash > 0 && int.TryParse(n.Substring(dash + 1), out var k2) ? k2 : 1;
        return num > 1 ? $"Manual ({r}) #{num}" : $"Manual ({r})";
    }

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
