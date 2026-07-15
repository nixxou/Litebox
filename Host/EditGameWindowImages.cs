// Edit Game → Media → Images. A faithful reproduction of ExtendDB's Editgameform image editor, re-homed on
// LiteBox: the "Images" tree node has one CHILD per image REGROUPEMENT (Front, Back, Background, Screenshots,
// Marquee, Box3d, CartFront, CartBack, Cart3d, ClearLogo, BoxSpine, BoxFull — from Gc.SettingsWatcher, same
// map as ExtendDB). Each regroupement page shows its images grouped by TYPE then REGION as a thumbnail grid;
// each thumbnail has a number, a right-click menu (Delete, Move/Copy To Type/Region, Delete all except this,
// Set Number, Enable/Disable GUID naming, Info), and — ONLY when ExtendDB is loaded — a lock button + a
// per-regroupement "Lock All" (via ImageLockBridge → ExtendDB.Utility.ImageLockAds). Clicking a thumbnail
// opens it fullscreen.
//
// File handling: target folder = the platform's image-type folder (+ region sub-folder); filename =
// `{sani}[.{guid}]-{NN:D2}.ext` — GUID form when the source is already GUID, when the target folder already
// holds GUID files for this game, OR when another game on the platform shares the sanitized title; NN =
// max-on-disk + 1. Disk is the source of truth (the page always enumerates disk). We remember the touched
// platform(s) and fire ONE de-duplicated media-cache rebuild each on close (OnFormClosed) — GameCacheBridge
// dispatches to ExtendDB's GameCache or our HostGameCache port. Single-game only (multi shows a placeholder).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Integrations;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    /// <summary>The game every image page operates on. Normally the single edited game; the multi-game media
    /// matrix re-points it at a row's game while that row's category modal is open (see ImgOpenCategoryModal).</summary>
    private IGame ImgGame => _imgGame ?? _editGames[0];
    private IGame? _imgGame;

    private readonly HashSet<string> _imgTouchedPlatforms = new(StringComparer.OrdinalIgnoreCase);
    // Lock buttons per regroupement, for "Lock All" to re-style them all at once.
    private readonly Dictionary<string, List<(Button btn, string path)>> _imgLockBtns = new();

    // ── Multi-select mode (category grid): long-press a thumbnail to enter it ──
    private bool _imgSelMode;
    private int _imgSelKind;   // which set the current selection is scoped to: 0 none · 1 local · 2 web
    private readonly HashSet<string> _imgSel = new(StringComparer.OrdinalIgnoreCase);      // local paths
    private readonly HashSet<string> _imgWebSel = new(StringComparer.OrdinalIgnoreCase);   // web-image keys (WebImage.Key)
    private readonly Dictionary<string, CheckBox> _imgCellChk = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _imgWebChk = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MetadataDb.WebImage> _imgWebByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _imgWebSource = new(StringComparer.OrdinalIgnoreCase);   // key → "web"/"emu"/"steam"
    private List<string> _imgCatAllPaths = new();
    private List<string> _imgCatAllWebKeys = new();
    private string _imgCurRegroupement = "";
    private Panel? _imgActionBar;
    private Label? _imgSelCount;
    private readonly List<Control> _imgLocalBtns = new();   // shown when the selection is LOCAL
    private Control? _imgDownloadBtn;                        // shown when the selection is WEB
    private System.Windows.Forms.Timer? _imgLongPressTimer;
    private int _imgPressKind;       // 1 local · 2 web
    private string? _imgPressKey;    // local path or web url
    private bool _imgSuppressClick;
    private bool _imgShowWeb;        // the "show web images" toggle (per category page)
    private bool _imgOpenWithWeb;    // set by the matrix: open the next category page with the web toggle ON
    private bool _imgOpenWithEmu;    // …and/or the EmuMovies toggle ON (mirrors the grid's enabled sources)
    private bool _imgOpenWithSteam;  // …and/or the Steam toggle ON
    private System.Net.Http.HttpClient? _imgHttp;

    /// <summary>Winner of its image TYPE (one per type) — see <see cref="ImgLbPicks"/>.</summary>
    private HashSet<string> _imgLbPicks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The ONE image LaunchBox actually displays for this regroupement — see <see cref="ImgLbSlotPick"/>.</summary>
    private string? _imgLbSlotPick;

    private static readonly Color LbSlotColor = Color.FromArgb(235, 190, 70);    // gold — the image LB really shows
    private static readonly Color LbTypeColor = Color.FromArgb(120, 126, 142);   // neutral steel — wins its type only

    // Web-database source colour (purple). The live sources have their own: EmuBlue / SteamGreen / YtRed.
    private static readonly Color WebPurple = Color.FromArgb(190, 150, 230);

    private static Color BlendColor(Color a, Color b, float t)
        => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// A source-toggle "chip" for the media editors (Web / EmuMovies / Steam / YouTube rows). The plain flat
    /// checkbox glyph washed out to grey on the dark canvas — checked vs unchecked was indistinguishable. This
    /// reads unambiguously: FILLED with a muted tint of the source colour (+ a leading ☑) when ON, hollow with a
    /// coloured outline (+ ☐) when OFF. Reinforces the "border colour = source" convention used by the tiles.
    /// </summary>
    private CheckBox SourceChip(string label, Color color, bool initial, Action<bool> onChanged)
    {
        var chk = new CheckBox
        {
            Checked = initial, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f), TabStop = false, UseVisualStyleBackColor = false,
        };
        chk.FlatAppearance.BorderColor = color;
        chk.FlatAppearance.BorderSize = 1;
        var onBack = BlendColor(color, Bg, 0.62f);                       // toward Bg → a muted tint, not a garish fill
        chk.FlatAppearance.CheckedBackColor = onBack;
        chk.FlatAppearance.MouseOverBackColor = BlendColor(color, Bg, 0.80f);
        void Restyle()
        {
            chk.BackColor = chk.Checked ? onBack : Bg;
            chk.ForeColor = chk.Checked ? Color.White : BlendColor(color, SubFg, 0.5f);
            chk.Text = (chk.Checked ? "☑  " : "☐  ") + label;
        }
        Restyle();
        chk.CheckedChanged += (_, _) => { Restyle(); onChanged(chk.Checked); };
        return chk;
    }

    /// <summary>
    /// The image LaunchBox picks for each image TYPE among <paramref name="files"/>: the first region in the
    /// canonical order (user RegionPriorities → LaunchBox's hard-coded fallback → root last) that has an image
    /// of that type, then the lowest number within it. Mirrors GamesDb/GameCache selection — see LbRegions.
    /// </summary>
    private static HashSet<string> ImgLbPicks(List<ImgFile> files)
    {
        var picks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = LbRegions.Order(LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities());
        foreach (var type in files.Select(f => f.Type).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ofType = files.Where(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var region in order)
            {
                var inRegion = ofType.Where(f => string.Equals(f.Region, region, StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(f => f.NumVal).ToList();
                if (inRegion.Count == 0) continue;
                picks.Add(inRegion[0].Path);   // first region that has one wins
                break;
            }
        }
        return picks;
    }

    /// <summary>
    /// The SINGLE image LaunchBox actually displays for a regroupement (slot). Mirrors GameCache's
    /// GetBestImageTypeFirst: the image TYPE is the dominant axis, so the first type of the regroupement (in
    /// its priority order) that has any image wins outright — region and number only break ties INSIDE that
    /// type. So the winner of a lower-ranked type (e.g. "Screenshot - Game Title") is never displayed when a
    /// higher-ranked one (e.g. "Screenshot - Gameplay") has an image. Null when the slot has no image.
    /// </summary>
    private static string? ImgLbSlotPick(List<ImgFile> files, List<string> types)
    {
        var order = LbRegions.Order(LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities());
        foreach (var type in types)   // already in the regroupement's priority order
        {
            var ofType = files.Where(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ofType.Count == 0) continue;
            foreach (var region in order)
            {
                var inRegion = ofType.Where(f => string.Equals(f.Region, region, StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(f => f.NumVal).ToList();
                if (inRegion.Count > 0) return inRegion[0].Path;
            }
        }
        return null;
    }

    private Panel ImgBuildActionBar()
    {
        // Tall enough for the buttons to WRAP onto a second row instead of hiding behind a horizontal
        // scrollbar (there can be ~10 of them: Move/Copy × Type/Region + Delete + Lock/Unlock + Download +
        // Select all + Done).
        var bar = new Panel { Dock = DockStyle.Top, Height = S(80), BackColor = Color.FromArgb(30, 30, 40), Visible = false };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true, Padding = new Padding(S(4)), BackColor = Color.Transparent };
        _imgSelCount = new Label { Text = "0 selected", ForeColor = Fg, BackColor = Color.Transparent, AutoSize = false, Width = S(84), Height = S(30), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(S(3), S(3), S(3), S(3)) };
        flow.Controls.Add(_imgSelCount);

        _imgLocalBtns.Clear();
        Button MenuBtn(string text, Action<ContextMenuStrip> fill)
        {
            var b = DlgBtn(text, Color.FromArgb(60, 60, 72)); b.Margin = new Padding(S(3));
            b.Click += (_, _) => { if (_imgSel.Count == 0) return; var mn = ThemedMenu(); fill(mn); mn.Show(b, new Point(0, b.Height)); };
            return b;
        }
        void AddLocal(Control c) { flow.Controls.Add(c); _imgLocalBtns.Add(c); }
        AddLocal(MenuBtn("Move To Type ▾", mn => { foreach (var t in MediaResolver.ImageTypeNames()) { string tt = t; mn.Items.Add(new ToolStripMenuItem(tt).WithClick(() => ImgBulkTransfer(tt, null, copy: false))); } }));
        AddLocal(MenuBtn("Move To Region ▾", mn => { foreach (var r in ImgMenuRegions()) { string rr = r; mn.Items.Add(new ToolStripMenuItem(rr == "none" ? "No Region" : rr).WithClick(() => ImgBulkTransfer(null, rr, copy: false))); } }));
        AddLocal(MenuBtn("Copy To Type ▾", mn => { foreach (var t in MediaResolver.ImageTypeNames()) { string tt = t; mn.Items.Add(new ToolStripMenuItem(tt).WithClick(() => ImgBulkTransfer(tt, null, copy: true))); } }));
        AddLocal(MenuBtn("Copy To Region ▾", mn => { foreach (var r in ImgMenuRegions()) { string rr = r; mn.Items.Add(new ToolStripMenuItem(rr == "none" ? "No Region" : rr).WithClick(() => ImgBulkTransfer(null, rr, copy: true))); } }));
        { var del = DlgBtn("Delete", Color.FromArgb(120, 50, 45)); del.Margin = new Padding(S(3)); del.Click += (_, _) => ImgBulkDelete(); AddLocal(del); }
        if (ImageLockBridge.Available)
        {
            var lk = DlgBtn("Lock", Color.FromArgb(92, 78, 30)); lk.Margin = new Padding(S(3)); lk.Click += (_, _) => ImgBulkLock(true); AddLocal(lk);
            var ul = DlgBtn("Unlock", Color.FromArgb(60, 60, 72)); ul.Margin = new Padding(S(3)); ul.Click += (_, _) => ImgBulkLock(false); AddLocal(ul);
        }

        var dl = DlgBtn("⬇ Download selected", Color.FromArgb(78, 52, 120)); dl.Margin = new Padding(S(3)); dl.Click += (_, _) => ImgDownloadSelected(); flow.Controls.Add(dl); _imgDownloadBtn = dl;

        var selAll = DlgBtn("Select all", Color.FromArgb(50, 60, 72)); selAll.Margin = new Padding(S(3)); selAll.Click += (_, _) => ImgSelectAllCurrent(); flow.Controls.Add(selAll);
        var done = DlgBtn("Done", Color.FromArgb(45, 95, 60)); done.Margin = new Padding(S(3)); done.Click += (_, _) => ImgExitSelectMode(); flow.Controls.Add(done);

        bar.Controls.Add(flow);
        _imgActionBar = bar;
        return bar;
    }

    private void ImgEnterSelectMode(int kind)
    {
        if (_imgSelMode && _imgSelKind != kind) ImgClearSelection();   // switching set → drop the other
        _imgSelMode = true; _imgSelKind = kind;
        if (_imgActionBar != null) _imgActionBar.Visible = true;
        foreach (var b in _imgLocalBtns) b.Visible = kind == 1;         // local ops vs …
        if (_imgDownloadBtn != null) _imgDownloadBtn.Visible = kind == 2;   // … download (web)
        foreach (var chk in _imgCellChk.Values) chk.Visible = kind == 1;    // checkboxes on the pressed set ONLY
        foreach (var chk in _imgWebChk.Values) chk.Visible = kind == 2;
        ImgUpdateSelCount();
    }

    private void ImgClearSelection()
    {
        _imgSel.Clear(); _imgWebSel.Clear();
        foreach (var chk in _imgCellChk.Values) { chk.Checked = false; if (chk.Parent != null) chk.Parent.BackColor = Bg; }
        foreach (var chk in _imgWebChk.Values) chk.Checked = false;
    }

    private void ImgExitSelectMode()
    {
        _imgSelMode = false; _imgSelKind = 0;
        ImgClearSelection();
        if (_imgActionBar != null) _imgActionBar.Visible = false;
        foreach (var chk in _imgCellChk.Values) chk.Visible = false;
        foreach (var chk in _imgWebChk.Values) chk.Visible = false;
    }

    private void ImgToggleSel(string path) => ImgSetSel(path, !_imgSel.Contains(path));
    private void ImgSetSel(string path, bool sel)
    {
        if (sel) _imgSel.Add(path); else _imgSel.Remove(path);
        if (_imgCellChk.TryGetValue(path, out var chk)) { if (chk.Checked != sel) chk.Checked = sel; if (chk.Parent != null) chk.Parent.BackColor = sel ? Color.FromArgb(40, 60, 90) : Bg; }
        ImgUpdateSelCount();
    }

    private void ImgWebToggle(string url) => ImgWebSet(url, !_imgWebSel.Contains(url));
    private void ImgWebSet(string url, bool sel)
    {
        if (sel) _imgWebSel.Add(url); else _imgWebSel.Remove(url);
        if (_imgWebChk.TryGetValue(url, out var chk) && chk.Checked != sel) chk.Checked = sel;
        ImgUpdateSelCount();
    }

    private void ImgUpdateSelCount() { if (_imgSelCount != null) _imgSelCount.Text = $"{(_imgSelKind == 2 ? _imgWebSel.Count : _imgSel.Count)} selected"; }

    private void ImgSelectAllCurrent()
    {
        if (_imgSelKind == 2) foreach (var k in _imgCatAllWebKeys) ImgWebSet(k, true);
        else foreach (var p in _imgCatAllPaths) ImgSetSel(p, true);
    }

    private void ImgSelectSection(List<string> paths)
    {
        bool allSel = paths.Count > 0 && paths.All(_imgSel.Contains);
        foreach (var p in paths) ImgSetSel(p, !allSel);
    }

    private void ImgStartLongPress()
    {
        ImgStopLongPress();
        _imgLongPressTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _imgLongPressTimer.Tick += (_, _) =>
        {
            ImgStopLongPress();
            _imgSuppressClick = true;                 // eat the Click that follows the release
            if (_imgPressKind == 2) { ImgEnterSelectMode(2); if (_imgPressKey != null) ImgWebSet(_imgPressKey, true); }
            else { ImgEnterSelectMode(1); if (_imgPressKey != null) ImgSetSel(_imgPressKey, true); }
        };
        _imgLongPressTimer.Start();
    }

    private void ImgStopLongPress() { try { _imgLongPressTimer?.Stop(); _imgLongPressTimer?.Dispose(); } catch { } _imgLongPressTimer = null; }

    // ── Bulk operations (subset of the per-image menu, applied to the selection) ──
    private void ImgBulkTransfer(string? targetType, string? targetRegion, bool copy)
    {
        if (_readOnly || _imgSel.Count == 0) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        var sel = ImgScan(g).Where(f => _imgSel.Contains(f.Path)).ToList();
        foreach (var img in sel) ImgDoTransfer(g, img, targetType ?? img.Type, targetRegion ?? img.Region, copy);
        _imgSel.Clear(); _imgSelMode = false;
        ImgAfterOp(plat);
    }

    private void ImgBulkDelete()
    {
        if (_readOnly || _imgSel.Count == 0) return;
        if (MessageBox.Show(this, $"Delete {_imgSel.Count} selected image(s) from disk?\n\nThis cannot be undone.",
                "Delete selected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        int fail = 0;
        foreach (var p in _imgSel.ToList()) { try { File.Delete(p); } catch { fail++; } }
        _imgSel.Clear(); _imgSelMode = false;
        ImgAfterOp(Safe(() => ImgGame.Platform) ?? "");
        if (fail > 0) MessageBox.Show(this, $"{fail} file(s) couldn't be deleted (locked / in use).", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ImgBulkLock(bool doLock)
    {
        if (!ImageLockBridge.Available || _imgSel.Count == 0) return;
        foreach (var p in _imgSel.ToList()) { if (doLock) ImageLockBridge.Lock(p); else ImageLockBridge.Unlock(p); }
        _imgSelMode = false; _imgSel.Clear();
        ImgAfterOp(Safe(() => ImgGame.Platform) ?? "");   // rebuild → lock glyphs refresh
    }

    private int ImgPadX => S(12);
    private int ImgPadY => S(10);
    private int ImgCellW => S(156);
    /// <summary>Thumbnail + two caption lines (#num / lock, then the ADS provenance) — see ImgCell.</summary>
    private int ImgCellH => S(168);
    private int ImgThumbH => ImgCellH - S(48);

    // ── ADS provenance caption (shared by the image AND video cells) ──────────
    // ":info" is where a downloaded file records WHERE it came from — the origin (launchbox / screenscraper /
    // steam / emumovies) and the region the source database gave it, which is NOT always the region folder the
    // file ended up in. Both are otherwise invisible; putting them under the thumbnail costs one small ADS read
    // per cell. Empty for a file that was never stamped (hand-copied, or downloaded before LiteBox wrote ADS).
    private static readonly Color ProvFg = Color.FromArgb(132, 128, 158);

    private static string AdsProvenance(string path)
    {
        var info = ImageInfoBridge.ReadAny(path);
        if (info is not ImageInfo i) return "";
        string o = (i.Origin ?? "").Trim();
        string r = (i.NativeRegion ?? "").Trim();
        if (o.Length == 0 && r.Length == 0) return "";
        if (o.Length == 0) return r;
        return r.Length == 0 ? o : o + "  ·  " + r;
    }

    private Label ProvLabel(string path, int x, int y, int w)
    {
        string text = AdsProvenance(path);
        var l = new Label
        {
            Text = text, ForeColor = ProvFg, BackColor = Bg, Font = new Font("Segoe UI", 7.5f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        l.SetBounds(x, y, w, S(16));
        if (text.Length > 0) new ToolTip().SetToolTip(l, "From the file's ExtendDB metadata (:info) — origin · native region");
        return l;
    }

    private readonly struct ImgFile
    {
        public readonly string Path, Type, Region, NumText;
        public readonly int NumVal;
        public readonly bool HasGuid;
        public ImgFile(string p, string t, string r, int nv, string nt, bool hg)
        { Path = p; Type = t; Region = r; NumVal = nv; NumText = nt; HasGuid = hg; }
    }

    // ── Regroupement (category) helpers ──────────────────────────────────────
    private static IReadOnlyList<string> ImgRegroupements()
        => LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities().Keys.ToList();
    private static bool ImgIsRegroupement(string key)
        => LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities().ContainsKey(key);
    private static List<string> ImgTypesOf(string regroupement)
        => LbApiHost.Host.Gc.SettingsWatcher.GetImageRegroupementPriorities().TryGetValue(regroupement, out var t)
            ? t : new List<string>();

    // ── "Images" parent node: the LaunchBox-style single-image navigator (browse ALL the game's images) ──
    private PictureBox? _imgNavPic;
    private Label? _imgNavCounter, _imgNavType;
    private Button? _imgNavRemove, _imgNavRemoveAll;
    private List<ImgFile> _imgNavList = new();
    private int _imgNavIdx;

    private Control BuildImagesPage()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(38)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(48)));

        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 4, RowCount = 1, Margin = new Padding(0), Padding = new Padding(S(2)) };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(60)));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(60)));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68f));
        var prev = DlgBtn("<", Color.FromArgb(60, 60, 72)); prev.AutoSize = false; prev.Dock = DockStyle.Fill; prev.Margin = new Padding(S(2));
        var next = DlgBtn(">", Color.FromArgb(60, 60, 72)); next.AutoSize = false; next.Dock = DockStyle.Fill; next.Margin = new Padding(S(2));
        _imgNavCounter = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Fg, BackColor = Field, Margin = new Padding(S(2)) };
        _imgNavType = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Fg, BackColor = Field, Margin = new Padding(S(2)), Cursor = Cursors.Hand };
        prev.Click += (_, _) => ImgNavShow(_imgNavIdx - 1);
        next.Click += (_, _) => ImgNavShow(_imgNavIdx + 1);
        _imgNavType.Click += (_, _) => ImgNavCategoryMenu();
        nav.Controls.Add(prev, 0, 0); nav.Controls.Add(next, 1, 0); nav.Controls.Add(_imgNavCounter, 2, 0); nav.Controls.Add(_imgNavType, 3, 0);

        _imgNavPic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Bg, Margin = new Padding(S(4)), Cursor = Cursors.Hand };
        _imgNavPic.Click += (_, _) => { if (_imgNavList.Count > 0) ShowImageFullscreenPath(_imgNavList[_imgNavIdx].Path); };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(S(4)) };
        var add = DlgBtn("Add Image…", Color.FromArgb(45, 95, 60)); add.Margin = new Padding(S(3));
        _imgNavRemove = DlgBtn("Remove Image", Color.FromArgb(120, 50, 45)); _imgNavRemove.Margin = new Padding(S(3));
        _imgNavRemoveAll = DlgBtn("Remove All Images", Color.FromArgb(120, 50, 45)); _imgNavRemoveAll.Margin = new Padding(S(3));
        var dl = DlgBtn("Download Media…", Color.FromArgb(60, 60, 72)); dl.Margin = new Padding(S(3));
        add.Enabled = !_readOnly; _imgNavRemove.Enabled = false; _imgNavRemoveAll.Enabled = false;
        add.Click += (_, _) => ImgNavAdd();
        _imgNavRemove.Click += (_, _) => ImgNavRemove();
        _imgNavRemoveAll.Click += (_, _) => ImgNavRemoveAll();
        dl.Click += (_, _) => MessageBox.Show(this, "Download Media is not implemented yet.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
        bar.Controls.AddRange(new Control[] { add, _imgNavRemove, _imgNavRemoveAll, dl });

        root.Controls.Add(nav, 0, 0);
        root.Controls.Add(_imgNavPic, 0, 1);
        root.Controls.Add(bar, 0, 2);
        ImgNavRefresh();
        return root;
    }

    private void ImgNavRefresh()
    {
        if (_imgNavPic == null || IsMulti) return;
        _imgNavList = ImgScan(ImgGame);
        ImgNavShow(0);
    }

    private void ImgNavShow(int idx)
    {
        if (_imgNavPic == null) return;
        int n = _imgNavList.Count;
        _imgNavRemove!.Enabled = n > 0 && !_readOnly;
        _imgNavRemoveAll!.Enabled = n > 0 && !_readOnly;
        if (n == 0)
        {
            _imgNavIdx = 0; var o = _imgNavPic.Image; _imgNavPic.Image = null; o?.Dispose();
            _imgNavCounter!.Text = "0 / 0"; _imgNavType!.Text = "(no images)"; return;
        }
        _imgNavIdx = Math.Max(0, Math.Min(n - 1, idx));
        var img = _imgNavList[_imgNavIdx];
        _imgNavCounter!.Text = $"{_imgNavIdx + 1} / {n}";
        _imgNavType!.Text = img.Region == "none" ? img.Type : $"{img.Type}  ·  {img.Region}";
        var old = _imgNavPic.Image;
        try { using var ms = new MemoryStream(File.ReadAllBytes(img.Path)); using var tmp = Image.FromStream(ms); _imgNavPic.Image = new Bitmap(tmp); }
        catch { _imgNavPic.Image = null; }
        old?.Dispose();
    }

    private void ImgNavAdd()
    {
        if (_readOnly || IsMulti || _imgNavPic == null) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? ""; string idStr = Safe(() => g.Id) ?? "";
        if (string.IsNullOrEmpty(idStr) || string.IsNullOrEmpty(plat)) { MessageBox.Show(this, "This game has no platform / id.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");

        // Pick TYPE + REGION first (seeded from the image on screen), THEN the file(s).
        string defType = _imgNavList.Count > 0 ? _imgNavList[_imgNavIdx].Type : "Box - Front";
        string defRegion = _imgNavList.Count > 0 ? _imgNavList[_imgNavIdx].Region : "World";
        if (!ImgPickTypeRegion(defType, defRegion, out string type, out string region)) return;
        if (!ImgCopyFilesInto(g, plat, idStr, sani, type, region, out string? lastTarget)) return;

        ImgAfterOp(plat);
        int at = lastTarget == null ? -1 : _imgNavList.FindIndex(f => string.Equals(f.Path, lastTarget, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) ImgNavShow(at);
    }

    /// <summary>Add image(s) to a specific CATEGORY page: the Type dropdown is restricted to that regroupement's
    /// types (Box - Front, Fanart - Box - Front, …), defaulting to the first — mirrors the Videos page's Add.</summary>
    private void ImgAddToCategory(string regroupement)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? ""; string idStr = Safe(() => g.Id) ?? "";
        if (string.IsNullOrEmpty(idStr) || string.IsNullOrEmpty(plat)) { MessageBox.Show(this, "This game has no platform / id.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        var types = ImgTypesOf(regroupement);
        if (types.Count == 0) return;
        if (!ImgPickTypeRegion(types[0], "World", out string type, out string region, types)) return;
        if (!ImgCopyFilesInto(g, plat, idStr, sani, type, region, out _)) return;
        ImgAfterOp(plat);   // rebuilds the current category page (or the matrix modal, via ImgAfterOp's hook)
    }

    /// <summary>Prompt for image file(s) and copy them into the type/region folder (GUID-aware naming, next
    /// number on disk). Returns true if at least one landed; <paramref name="lastTarget"/> = the last copied path.</summary>
    private bool ImgCopyFilesInto(IGame g, string plat, string idStr, string sani, string type, string region, out string? lastTarget)
    {
        lastTarget = null;
        string? baseFolder = MediaResolver.TypeFolder(plat, type);
        if (string.IsNullOrEmpty(baseFolder)) { MessageBox.Show(this, "Couldn't resolve the image folder.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        string folder = ImgSearchDir(baseFolder, region);

        using var ofd = new OpenFileDialog { Title = "Add image(s)", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true, Multiselect = true };
        if (ofd.ShowDialog(this) != DialogResult.OK) return false;

        int ok = 0;
        try { Directory.CreateDirectory(folder); } catch { }
        foreach (var file in ofd.FileNames)
        {
            try
            {
                string prefix = ImgPrefix(plat, idStr, sani, file, folder);
                int num = ImgMaxNum(folder, prefix) + 1;
                string target = Path.Combine(folder, $"{prefix}-{num:D2}{Path.GetExtension(file)}");
                File.Copy(file, target, overwrite: false);
                lastTarget = target; ok++;
            }
            catch (Exception ex) { MessageBox.Show(this, "Add image failed:\n" + Path.GetFileName(file) + "\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        return ok > 0;
    }

    /// <summary>Type + Region picker (before the file dialog), seeded with the current image's slot. When
    /// <paramref name="allowedTypes"/> is given, the Type dropdown is restricted to those (used by a category
    /// page's Add button, which only offers the types of that regroupement).</summary>
    private bool ImgPickTypeRegion(string defType, string defRegion, out string type, out string region, IReadOnlyList<string>? allowedTypes = null)
    {
        type = defType; region = defRegion;
        using var f = NewDialog("Image type & region", 440, 210);
        var lblT = new Label { Text = "Type:", Location = new Point(S(14), S(18)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var cboT = new ComboBox { Location = new Point(S(110), S(15)), Width = S(304), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        var typeList = (allowedTypes != null && allowedTypes.Count > 0) ? allowedTypes : MediaResolver.ImageTypeNames();
        foreach (var t in typeList) cboT.Items.Add(t);
        if (cboT.Items.Count > 0) { int di = cboT.Items.IndexOf(defType); cboT.SelectedIndex = di >= 0 ? di : 0; }

        var lblR = new Label { Text = "Region:", Location = new Point(S(14), S(58)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var cboR = new ComboBox { Location = new Point(S(110), S(55)), Width = S(304), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        foreach (var r in ImgMenuRegions()) cboR.Items.Add(r == "none" ? "No Region" : r);
        int dr = ImgMenuRegions().FindIndex(r => string.Equals(r, defRegion, StringComparison.OrdinalIgnoreCase));
        cboR.SelectedIndex = dr >= 0 ? dr : 0;

        bool okd = false;
        DialogButtons(f, out var ok, out var cancel);
        ok.Click += (_, _) => { okd = true; f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.Controls.Add(lblT); f.Controls.Add(cboT); f.Controls.Add(lblR); f.Controls.Add(cboR);
        if (f.ShowDialog(this) != DialogResult.OK || !okd) return false;

        type = cboT.SelectedItem as string ?? defType;
        var regions = ImgMenuRegions();
        region = (cboR.SelectedIndex >= 0 && cboR.SelectedIndex < regions.Count) ? regions[cboR.SelectedIndex] : defRegion;
        return true;
    }

    private string? ImgPickType(string defaultType)
    {
        using var f = NewDialog("Image type", 440, 160);
        var lbl = new Label { Text = "Add as image type:", Location = new Point(S(14), S(16)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var cbo = new ComboBox { Location = new Point(S(14), S(42)), Width = S(400), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        foreach (var t in MediaResolver.ImageTypeNames()) cbo.Items.Add(t);
        if (cbo.Items.Count > 0) { int di = cbo.Items.IndexOf(defaultType); cbo.SelectedIndex = di >= 0 ? di : 0; }
        string? chosen = null;
        var bottom = DialogButtons(f, out var ok, out var cancel);
        ok.Click += (_, _) => { chosen = cbo.SelectedItem as string; f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.Controls.Add(lbl); f.Controls.Add(cbo);
        return f.ShowDialog(this) == DialogResult.OK ? chosen : null;
    }

    /// <summary>Click the category label on the navigator → move the CURRENT image to another type.</summary>
    private void ImgNavCategoryMenu()
    {
        if (_imgNavType == null || _imgNavList.Count == 0 || _readOnly) return;
        var menu = ThemedMenu();
        string curType = _imgNavList[_imgNavIdx].Type;
        foreach (var name in MediaResolver.ImageTypeNames())
        {
            string type = name;
            var it = new ToolStripMenuItem(type) { Checked = string.Equals(type, curType, StringComparison.OrdinalIgnoreCase) };
            it.Click += (_, _) => { var img = _imgNavList[_imgNavIdx]; ImgTransfer(img, type, img.Region, copy: false); };
            menu.Items.Add(it);
        }
        menu.Show(_imgNavType, new Point(0, _imgNavType.Height));
    }

    private void ImgNavRemove()
    {
        if (_readOnly || _imgNavList.Count == 0 || _imgNavPic == null) return;
        var img = _imgNavList[_imgNavIdx];
        if (MessageBox.Show(this, $"Delete this image from disk?\n\n{img.Type}\n{Path.GetFileName(img.Path)}\n\nThis cannot be undone.",
                "Remove Image", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        int keep = _imgNavIdx;
        var old = _imgNavPic.Image; _imgNavPic.Image = null; old?.Dispose();
        try { File.Delete(img.Path); }
        catch (Exception ex) { MessageBox.Show(this, "Delete failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        ImgAfterOp(Safe(() => ImgGame.Platform) ?? "");
        ImgNavShow(Math.Min(keep, _imgNavList.Count - 1));
    }

    private void ImgNavRemoveAll()
    {
        if (_readOnly || _imgNavList.Count == 0 || _imgNavPic == null) return;
        int n = _imgNavList.Count;
        if (MessageBox.Show(this, $"Delete ALL {n} image(s) for this game from disk?\n\nThis cannot be undone.",
                "Remove All Images", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var old = _imgNavPic.Image; _imgNavPic.Image = null; old?.Dispose();
        int fail = 0;
        foreach (var f in _imgNavList.ToList()) { try { File.Delete(f.Path); } catch { fail++; } }
        ImgAfterOp(Safe(() => ImgGame.Platform) ?? "");
        if (fail > 0) MessageBox.Show(this, $"{fail} file(s) couldn't be deleted.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ── Category page ─────────────────────────────────────────────────────────
    private Control BuildImageCategoryPage(string regroupement)
    {
        _imgCurRegroupement = regroupement;
        _imgSelMode = false; _imgSel.Clear(); _imgWebSel.Clear(); _imgSelKind = 0;
        if (_imgModalHolder != null)
        {
            // Matrix modal: mirror the GRID's enabled sources exactly (one-shot) so the same stand-ins show, and
            // rebuild the check-order to match. Isolated from the tree state — the matrix save/restores it.
            _imgShowWeb = _imgOpenWithWeb;
            _imgShowEmu = _imgOpenWithEmu;
            _imgShowSteam = _imgOpenWithSteam;
            _imgSourceOrder.Clear();
            foreach (var s in new[] { "web", "emu", "steam" })
                if ((s == "web" && _imgShowWeb) || (s == "emu" && _imgShowEmu) || (s == "steam" && _imgShowSteam))
                    _imgSourceOrder.Add(s);
        }
        else
        {
            // Tree navigation: the chosen sources (and their check-order) PERSIST across categories for the whole
            // edit session — the user asked once, we don't make them re-tick on every category. Cheap: the
            // EmuMovies/Steam fetch caches are keyed per game, so leaving a live source on costs one query total,
            // reused across every category. The matrix's open-with flags only ever force a source ON, never off.
            if (_imgOpenWithWeb)   { _imgShowWeb = true;   ImgSourceToggle("web", true); }
            if (_imgOpenWithEmu)   { _imgShowEmu = true;   ImgSourceToggle("emu", true); }
            if (_imgOpenWithSteam) { _imgShowSteam = true; ImgSourceToggle("steam", true); }
        }
        var container = new Panel { BackColor = Bg, Dock = DockStyle.Fill };
        var grid = new Panel { BackColor = Bg, AutoScroll = true, Dock = DockStyle.Fill };
        var bar = ImgBuildActionBar();       // docked TOP, hidden until multi-select is on
        container.Controls.Add(grid);        // Fill added first (back) …
        container.Controls.Add(bar);         // … Top added last so it claims the top edge
        ImgPopulateCategory(regroupement, grid);
        return container;
    }

    private void ImgPopulateCategory(string regroupement, Panel host)
    {
        foreach (Control c in host.Controls) ImgDisposePics(c);
        host.Controls.Clear();
        _imgLockBtns[regroupement] = new List<(Button, string)>();
        _imgCellChk.Clear();
        _imgWebChk.Clear();
        _imgWebByKey.Clear();
        _imgWebSource.Clear();
        _imgCatAllWebKeys = new List<string>();

        var g = ImgGame;
        var all = ImgScan(g);
        var types = ImgTypesOf(regroupement);
        var imgs = all.Where(f => types.Any(t => string.Equals(t, f.Type, StringComparison.OrdinalIgnoreCase))).ToList();
        _imgCatAllPaths = imgs.Select(i => i.Path).ToList();
        _imgLbPicks = ImgLbPicks(imgs);                 // winner of each image TYPE
        _imgLbSlotPick = ImgLbSlotPick(imgs, types);    // the ONE image LaunchBox actually displays here

        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        bool webAvail = MetadataDb.Available && dbId > 0;

        var inner = new Panel { BackColor = Bg, Location = Point.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
        host.Controls.Add(inner);
        int y = ImgPadY;

        // Top bar (same row as Lock All): a green "＋ Add" that only offers THIS category's types (like the Videos
        // page's Add), then the source toggles — filled-when-on chips (see SourceChip). Purple = the offline
        // metadata DB, blue = EmuMovies (live), green = Steam (live). Only shown when that source is applicable.
        int chipX = ImgPadX + (imgs.Count > 0 && ImageLockBridge.Available ? S(122) : 0);
        void AddBar(Control c, int w) { c.SetBounds(chipX, y, w, S(26)); inner.Controls.Add(c); chipX += w + S(10); }

        var addBtn = DlgBtn("＋ Add", Color.FromArgb(45, 95, 60)); addBtn.AutoSize = false; addBtn.Enabled = !_readOnly;
        addBtn.Click += (_, _) => ImgAddToCategory(regroupement);
        AddBar(addBtn, S(92));

        if (webAvail)
            AddBar(SourceChip("Web (database)", WebPurple, _imgShowWeb, on =>
                { _imgShowWeb = on; ImgSourceToggle("web", on); ImgInvalidateCachedCategories(regroupement); ImgPopulateCategory(regroupement, host); }), S(158));
        if (ImgEmuAvailable(g))
            AddBar(SourceChip("EmuMovies", EmuBlue, _imgShowEmu, on =>
                { _imgShowEmu = on; ImgSourceToggle("emu", on); ImgInvalidateCachedCategories(regroupement); ImgPopulateCategory(regroupement, host); }), S(124));
        if (ImgSteamAvailable(g))
            AddBar(SourceChip("Steam", SteamGreen, _imgShowSteam, on =>
                { _imgShowSteam = on; ImgSourceToggle("steam", on); ImgInvalidateCachedCategories(regroupement); ImgPopulateCategory(regroupement, host); }), S(100));

        // "No images" only when the category is empty AND NEITHER web source will fill it. Without the emu
        // clause an EmuMovies-only view (web off, EmuMovies on) short-circuited here and never rendered.
        if (imgs.Count == 0 && !(webAvail && _imgShowWeb) && !(_imgShowEmu && ImgEmuAvailable(g)) && !(_imgShowSteam && ImgSteamAvailable(g)))
        {
            var none = new Label
            {
                Text = $"No images for {regroupement}", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 11f, FontStyle.Italic),
            };
            none.SetBounds(ImgPadX, y + S(34), S(500), S(30));
            inner.Controls.Add(none);
            return;
        }

        // Lock All — ExtendDB only.
        if (ImageLockBridge.Available && imgs.Count > 0)
        {
            var paths = imgs.Select(i => i.Path).ToList();
            int locked = paths.Count(ImageLockBridge.IsLocked);
            bool maj = paths.Count > 0 && locked > paths.Count / 2;
            var lockList = _imgLockBtns[regroupement];
            var btnAll = new Button
            {
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f), Cursor = Cursors.Hand, TabStop = false,
                ForeColor = Fg, BackColor = maj ? Color.FromArgb(92, 78, 30) : Color.FromArgb(60, 60, 72),
                Text = maj ? "🔓 Unlock All" : "🔒 Lock All",
            };
            btnAll.FlatAppearance.BorderSize = 0;
            btnAll.SetBounds(ImgPadX, y, S(110), S(26));
            bool state = maj;
            btnAll.Click += (_, _) =>
            {
                bool willLock = !state;
                foreach (var p in paths) { if (willLock) ImageLockBridge.Lock(p); else ImageLockBridge.Unlock(p); }
                foreach (var (b, _) in lockList) ImgStyleLockBtn(b, willLock);
                state = willLock;
                btnAll.Text = willLock ? "🔓 Unlock All" : "🔒 Lock All";
                btnAll.BackColor = willLock ? Color.FromArgb(92, 78, 30) : Color.FromArgb(60, 60, 72);
            };
            inner.Controls.Add(btnAll);
        }
        y += S(38);

        var regionOrder = ImgRegionOrder(all);
        foreach (var type in types)
        {
            int typeHeaderY = y;
            y += S(28);
            bool typeHas = false;
            foreach (var region in regionOrder)
            {
                var cells = imgs.Where(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(i.Region, region, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(i => i.NumVal).ToList();
                if (cells.Count == 0) continue;
                typeHas = true;
                var sectionPaths = cells.Select(c => c.Path).ToList();
                var rl = new Label
                {
                    Text = (region == "none" ? "No Region" : region) + "   (click row to select)", ForeColor = Color.FromArgb(140, 160, 190),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic), AutoSize = false, BackColor = Bg, Cursor = Cursors.Hand,
                };
                rl.Click += (_, _) => { if (_imgSelMode) ImgSelectSection(sectionPaths); };
                rl.SetBounds(ImgPadX + S(8), y, S(400), S(20));
                inner.Controls.Add(rl);
                y += S(24);
                int x = ImgPadX + S(8);
                foreach (var img in cells) { var cell = ImgCell(img, regroupement); cell.Location = new Point(x, y); inner.Controls.Add(cell); x += ImgCellW; }
                y += ImgCellH;
            }
            if (typeHas)
            {
                var th = new Label
                {
                    Text = $"━━  {type}", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    AutoSize = false, BackColor = Bg,
                };
                th.SetBounds(ImgPadX, typeHeaderY, S(600), S(26));
                inner.Controls.Add(th);
            }
            else y = typeHeaderY;
        }

        ImgAppendMergedWeb(g, dbId, imgs, regroupement, types, host, inner, ref y);
    }

    // Track the order the user turns web sources on, so the merged view interleaves them by check-order (like the
    // multi-select grid's fill priority). "web" = database (purple) · "emu" = EmuMovies (blue) · "steam" (green).
    private readonly List<string> _imgSourceOrder = new();
    private void ImgSourceToggle(string src, bool on)
    {
        if (on) { if (!_imgSourceOrder.Contains(src)) _imgSourceOrder.Add(src); }
        else _imgSourceOrder.Remove(src);
    }

    // The source toggles persist across categories, but ShowPage CACHES each category page — so an already-visited
    // page would keep the check state it was built with, going stale after you re-toggle on another one. Drop the
    // other cached category pages here so they rebuild with the current selection on next visit. Skipped inside the
    // matrix modal (its pages aren't cached and its state is save/restored, so it must not disturb the tree cache).
    private void ImgInvalidateCachedCategories(string except)
    {
        if (_imgModalHolder != null) return;
        foreach (var key in _pages.Keys.Where(k => ImgIsRegroupement(k)
                     && !string.Equals(k, except, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var pg = _pages[key];
            _pages.Remove(key);
            try { ImgDisposePics(pg); pg.Dispose(); } catch { }
        }
    }

    // ── Merged "images you don't own": database + EmuMovies + Steam in ONE flow, grouped by image type only ────
    // No per-source section headers — the tile border colour already says the origin (purple/blue/green). Within
    // a type, sources appear in the order the user checked them (_imgSourceOrder). Async sources (EmuMovies /
    // Steam) show a compact "Querying…" note and re-populate when their fetch lands (same cache model as before).
    private void ImgAppendMergedWeb(IGame g, int dbId, List<ImgFile> catImgs, string regroupement, List<string> types, Panel host, Panel inner, ref int y)
    {
        bool webOn   = _imgShowWeb   && MetadataDb.Available && dbId > 0;
        bool emuOn   = _imgShowEmu   && ImgEmuAvailable(g);
        bool steamOn = _imgShowSteam && ImgSteamAvailable(g);
        if (!webOn && !emuOn && !steamOn) return;

        // Active sources in check-order; append any that predate the order list in a stable fallback order.
        bool On(string s) => (s == "web" && webOn) || (s == "emu" && emuOn) || (s == "steam" && steamOn);
        var order = _imgSourceOrder.Where(On).ToList();
        foreach (var s in new[] { "web", "emu", "steam" }) if (On(s) && !order.Contains(s)) order.Add(s);

        var typeSet = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        string emuKey = Safe(() => g.Id) ?? Safe(() => g.Title) ?? "";
        string steamKey = "steam:" + (Safe(() => g.Id) ?? Safe(() => g.Title) ?? "");

        var entries = new List<(string type, int rank, Panel cell)>();
        var loading = new List<string>();

        for (int rank = 0; rank < order.Count; rank++)
        {
            switch (order[rank])
            {
                case "web":
                    foreach (var w in ImgWebCandidates(dbId, catImgs, typeSet))
                    {
                        ImgRegisterWeb(w, "web");
                        entries.Add((w.Type, rank, ImgWebCell(w)));
                    }
                    break;

                case "emu":
                    if (!_imgEmuCache.TryGetValue(emuKey, out var em)) { ImgTriggerEmuFetch(g, emuKey, regroupement, host); loading.Add("EmuMovies"); }
                    else if (em == null) loading.Add("EmuMovies");
                    else
                    {
                        var owned = BuildEmuOwned(catImgs.Select(f => f.Path));
                        foreach (var m in em.Where(m => typeSet.Contains(m.LbType) && !EmuOwns(owned, m.Crc, m.FileSize)))
                        {
                            var wi = ImgEmuToWeb(m, dbId);          // the same WebImage the download path expects
                            ImgRegisterWeb(wi, "emu");
                            entries.Add((m.LbType, rank, ImgEmuCell(m, wi.Key)));
                        }
                    }
                    break;

                case "steam":
                    if (!_imgSteamCache.TryGetValue(steamKey, out var st)) { ImgTriggerSteamFetch(g, dbId, steamKey, regroupement, host); loading.Add("Steam"); }
                    else if (st == null) loading.Add("Steam");
                    else
                        foreach (var w in st.Where(w => typeSet.Contains(w.Type)))
                        {
                            ImgRegisterWeb(w, "steam");
                            entries.Add((w.Type, rank, ImgSteamCell(w)));
                        }
                    break;
            }
        }

        if (entries.Count == 0 && loading.Count == 0) return;

        y += S(12);
        var hdr = new Label
        {
            Text = "⬇  Images you don't own — left-click preview · right-click download · long-press to multi-select  (border colour = source)",
            ForeColor = SubFg, Font = new Font("Segoe UI", 9f, FontStyle.Italic), AutoSize = false, BackColor = Bg,
        };
        hdr.SetBounds(ImgPadX, y, S(780), S(24)); inner.Controls.Add(hdr); y += S(30);

        // Wrap tiles to the visible width so a merged (multi-source) type row never forces horizontal scrolling.
        int avail = (host.ClientSize.Width > S(200) ? host.ClientSize.Width : S(1100)) - (ImgPadX + S(8)) - S(24);
        int cols = Math.Max(1, avail / ImgCellW);

        foreach (var type in types)
        {
            var cells = entries.Where(e => string.Equals(e.type, type, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(e => e.rank).Select(e => e.cell).ToList();   // OrderBy is stable → same-source order preserved
            if (cells.Count == 0) continue;
            var th = new Label { Text = $"━━  {type}", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg };
            th.SetBounds(ImgPadX, y, S(600), S(26)); inner.Controls.Add(th); y += S(30);
            int x = ImgPadX + S(8), col = 0;
            foreach (var cell in cells)
            {
                if (col == cols) { col = 0; x = ImgPadX + S(8); y += ImgCellH; }
                cell.Location = new Point(x, y); inner.Controls.Add(cell); x += ImgCellW; col++;
            }
            y += ImgCellH;
        }

        if (loading.Count > 0)
        {
            inner.Controls.Add(new Label { Text = "Querying " + string.Join(", ", loading) + "…", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(ImgPadX + S(4), y) });
            y += S(26);
        }
    }

    // Register a stand-in from ANY web source into the shared selection model (key → WebImage / source / all-keys),
    // so long-press select + batch download work uniformly across database, EmuMovies and Steam tiles.
    private void ImgRegisterWeb(MetadataDb.WebImage w, string source)
    {
        _imgWebByKey[w.Key] = w;
        _imgWebSource[w.Key] = source;
        _imgCatAllWebKeys.Add(w.Key);
    }

    /// <summary>EmuMovies media → the WebImage the download path (ImgDownloadOne / ImageAdsWriter) expects.</summary>
    private static MetadataDb.WebImage ImgEmuToWeb(EmuMoviesCatalog.EmuMedia m, int dbId)
        => new MetadataDb.WebImage(dbId, m.Url, m.LbType, m.Region, m.Crc, "emumovies", 0, m.Ext, m.FileSize);

    /// <summary>The per-tile selection checkbox shared by all three web sources (shown only in web select mode).</summary>
    private CheckBox ImgWebSelCheckbox(string key)
    {
        var chk = new CheckBox
        {
            AutoCheck = false, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 30, 55), ForeColor = Fg,
            Size = new Size(S(18), S(18)), Location = new Point(S(8), S(8)), TabStop = false,
            Visible = _imgSelMode && _imgSelKind == 2, Checked = _imgWebSel.Contains(key),
        };
        chk.Click += (_, _) => ImgWebToggle(key);
        _imgWebChk[key] = chk;
        return chk;
    }

    /// <summary>Database web stand-ins for this category (owned-by-CRC filtered; non-launchbox rows dropped when
    /// ExtendDB's per-origin fetcher isn't reachable, as they have no plain CDN URL).</summary>
    private List<MetadataDb.WebImage> ImgWebCandidates(int dbId, List<ImgFile> catImgs, HashSet<string> typeSet)
    {
        var owned = new HashSet<uint>();
        foreach (var f in catImgs) { var c = CrcBridge.Crc(f.Path); if (c != 0) owned.Add(c); }
        List<MetadataDb.WebImage> cands;
        try { cands = MetadataDb.ImagesForGame(dbId); } catch { cands = new List<MetadataDb.WebImage>(); }
        var web = cands.Where(w => typeSet.Contains(w.Type) && !owned.Contains(unchecked((uint)w.Crc32))).ToList();
        if (!MediaApiBridge.Available) web = web.Where(x => x.IsLaunchbox).ToList();
        return web;
    }

    private void ImgTriggerEmuFetch(IGame g, string idKey, string regroupement, Panel host)
    {
        _imgEmuCache[idKey] = null;   // loading sentinel — a re-populate before the fetch lands won't re-trigger
        string romPath = Safe(() => g.ApplicationPath) ?? "";
        string title = Safe(() => g.Title) ?? "";
        string plat = Safe(() => g.Platform) ?? "";
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<EmuMoviesCatalog.EmuMedia> found = new();
            try { var api = EmuApi(); if (api != null) found = await EmuMoviesCatalog.ResolveForGameAsync(api, title, romPath, plat); }
            catch { }
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => { _imgEmuCache[idKey] = found; if (_imgCurRegroupement == regroupement) ImgPopulateCategory(regroupement, host); }));
            }
            catch { }
        });
    }

    private void ImgTriggerSteamFetch(IGame g, int dbId, string idKey, string regroupement, Panel host)
    {
        _imgSteamCache[idKey] = null;
        string appPath = Safe(() => g.ApplicationPath) ?? "";
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<MetadataDb.WebImage> found = new();
            try { found = await SteamCatalog.ResolveForGameAsync(dbId, appPath); } catch { }
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => { _imgSteamCache[idKey] = found; if (_imgCurRegroupement == regroupement) ImgPopulateCategory(regroupement, host); }));
            }
            catch { }
        });
    }

    // ── Steam images (green) — LIVE, appdetails, only for games with a Steam appid ──
    private static readonly Color SteamGreen = Color.FromArgb(92, 172, 96);
    private bool _imgShowSteam;
    private readonly Dictionary<string, List<MetadataDb.WebImage>?> _imgSteamCache = new(StringComparer.Ordinal);

    private bool ImgSteamAvailable(IGame g)
    {
        try { return SteamCatalog.AppIdOf(Safe(() => g.ApplicationPath) ?? "", Safe(() => g.LaunchBoxDbId) ?? -1) != null; }
        catch { return false; }
    }

    private Panel ImgSteamCell(MetadataDb.WebImage w)
    {
        var cell = new Panel { Size = new Size(ImgCellW, ImgCellH), BackColor = Bg };
        var frame = new Panel { BackColor = SteamGreen, Padding = new Padding(S(2)) };
        frame.SetBounds(S(4), S(4), ImgCellW - S(8), ImgThumbH);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        ImgLoadThumbWeb(pic, w);   // origin=steam → direct GET inside ImgFetchWebBytes
        var wi = w;
        string key = w.Key;
        pic.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _imgPressKind = 2; _imgPressKey = key; ImgStartLongPress(); } };
        pic.MouseUp += (_, e) =>
        {
            ImgStopLongPress();
            if (e.Button == MouseButtons.Right)
            {
                var menu = ThemedMenu();
                menu.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => ImgDownloadSteam(wi)));
                menu.Items.Add(new ToolStripMenuItem("🔍  View fullscreen").WithClick(() => ShowImageFullscreenWeb(wi)));
                menu.Show(pic, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (_imgSuppressClick) { _imgSuppressClick = false; return; }
            if (_imgSelMode) ImgWebToggle(key);
            else ShowImageFullscreenWeb(wi);
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        var chk = ImgWebSelCheckbox(key);
        cell.Controls.Add(chk); chk.BringToFront();

        var cap = new Label { Text = "Steam", ForeColor = SteamGreen, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        cap.SetBounds(S(4), ImgCellH - S(42), ImgCellW - S(8), S(20));
        cell.Controls.Add(cap);
        return cell;
    }

    private void ImgDownloadSteam(MetadataDb.WebImage w)
    {
        if (_readOnly) return;
        var g = ImgGame;
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        UseWaitCursor = true;
        bool ok;
        try { ok = ImgDownloadOne(g, w, dbId, Safe(() => g.Platform) ?? ""); }   // origin=steam → direct GET + ADS
        finally { UseWaitCursor = false; }
        if (ok) ImgAfterOp(Safe(() => g.Platform) ?? "");
        else MessageBox.Show(this, "The Steam download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ── EmuMovies images (blue) — LIVE, the user's own account ────────────────
    private bool _imgShowEmu;
    private readonly Dictionary<string, List<EmuMoviesCatalog.EmuMedia>?> _imgEmuCache = new(StringComparer.Ordinal);

    private bool ImgEmuAvailable(IGame g)
    {
        try { return EmuMoviesCatalog.SupportsPlatform(Safe(() => g.Platform) ?? "") && EmuMoviesApi.FromLbSettings() != null; }
        catch { return false; }
    }

    private Panel ImgEmuCell(EmuMoviesCatalog.EmuMedia m, string key)
    {
        var cell = new Panel { Size = new Size(ImgCellW, ImgCellH), BackColor = Bg };
        var frame = new Panel { BackColor = EmuBlue, Padding = new Padding(S(2)) };   // blue = EmuMovies
        frame.SetBounds(S(4), S(4), ImgCellW - S(8), ImgThumbH);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        ImgLoadThumbEmu(pic, m);
        var mi = m;
        pic.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _imgPressKind = 2; _imgPressKey = key; ImgStartLongPress(); } };
        pic.MouseUp += (_, e) =>
        {
            ImgStopLongPress();
            if (e.Button == MouseButtons.Right)
            {
                var menu = ThemedMenu();
                menu.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => ImgDownloadEmu(mi)));
                menu.Items.Add(new ToolStripMenuItem("🔍  View fullscreen").WithClick(() => ImgViewEmuFullscreen(mi)));
                menu.Show(pic, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (_imgSuppressClick) { _imgSuppressClick = false; return; }
            if (_imgSelMode) ImgWebToggle(key);
            else ImgViewEmuFullscreen(mi);   // left = preview, like the purple tiles
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        var chk = ImgWebSelCheckbox(key);
        cell.Controls.Add(chk); chk.BringToFront();

        var cap = new Label { Text = "EmuMovies · " + (string.IsNullOrEmpty(m.Region) ? "World" : m.Region), ForeColor = EmuBlue, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        cap.SetBounds(S(4), ImgCellH - S(42), ImgCellW - S(8), S(20));
        cell.Controls.Add(cap);
        cell.Controls.Add(ProvLabel_Static(m.MediaType, S(4), ImgCellH - S(22), ImgCellW - S(8)));
        return cell;
    }

    private Label ProvLabel_Static(string text, int x, int y, int w)
    {
        var l = new Label { Text = text, ForeColor = ProvFg, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        l.SetBounds(x, y, w, S(16));
        return l;
    }

    /// <summary>Direct GET of an EmuMovies media URL (no auth, just the Referer). Null on failure.</summary>
    private byte[]? EmuFetchBytes(string url) => WebGetBytes(url, EmuMoviesApi.MediaReferer);

    /// <summary>Direct GET of a public media URL with a Referer (EmuMovies / Steam CDNs). Null on failure.</summary>
    private byte[]? WebGetBytes(string url, string? referer)
    {
        try
        {
            _imgHttp ??= NewHttp();
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
            using var resp = _imgHttp.Send(req);
            return resp.IsSuccessStatusCode ? resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() : null;
        }
        catch { return null; }
    }

    private void ImgViewEmuFullscreen(EmuMoviesCatalog.EmuMedia m)
    {
        byte[]? bytes = null;
        UseWaitCursor = true;
        try { bytes = EmuFetchBytes(m.Url); } catch { } finally { UseWaitCursor = false; }
        ShowImageFullscreenBytes(bytes);
    }

    private void ImgLoadThumbEmu(PictureBox pic, EmuMoviesCatalog.EmuMedia m)
    {
        // Same deferral as ImgLoadThumbWeb: the modal builds tiles before it's shown (no handle yet).
        if (!pic.IsHandleCreated)
        {
            void OnCreated(object? _, EventArgs __) { pic.HandleCreated -= OnCreated; ImgLoadThumbEmu(pic, m); }
            pic.HandleCreated += OnCreated;
            return;
        }
        var mi = m;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var bytes = EmuFetchBytes(mi.Url);
                if (bytes == null) return;
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                const int maxDim = 320;
                double scale = Math.Min(1.0, (double)maxDim / Math.Max(tmp.Width, tmp.Height));
                var bmp = new Bitmap(tmp, new Size(Math.Max(1, (int)(tmp.Width * scale)), Math.Max(1, (int)(tmp.Height * scale))));
                try { if (pic.IsHandleCreated) pic.BeginInvoke(new Action(() => { if (!pic.IsDisposed) { var o = pic.Image; pic.Image = bmp; o?.Dispose(); } else bmp.Dispose(); })); else bmp.Dispose(); }
                catch { bmp.Dispose(); }
            }
            catch { }
        });
    }

    private void ImgDownloadEmu(EmuMoviesCatalog.EmuMedia m)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return;

        byte[]? bytes = null;
        UseWaitCursor = true;
        try { bytes = EmuFetchBytes(m.Url); } catch { } finally { UseWaitCursor = false; }

        if (bytes == null || bytes.Length == 0) { MessageBox.Show(this, "The EmuMovies download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        try
        {
            string ext = string.IsNullOrEmpty(m.Ext) ? "png" : m.Ext;
            string region = string.IsNullOrEmpty(m.Region) ? "World" : m.Region;
            string? baseFolder = MediaResolver.TypeFolder(plat, m.LbType);
            if (string.IsNullOrEmpty(baseFolder)) return;
            string dir = ImgSearchDir(baseFolder, region);
            Directory.CreateDirectory(dir);
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}.{ext.TrimStart('.')}");
            File.WriteAllBytes(target, bytes);

            var wi = new MetadataDb.WebImage(dbId, m.Url, m.LbType, m.Region, m.Crc, "emumovies", 0, m.Ext, m.FileSize);
            ImageAdsWriter.WriteForDownload(target, wi, dbId, plat);
            ImgAfterOp(plat);
        }
        catch (Exception ex) { MessageBox.Show(this, "Download failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ── Web images (from the offline LaunchBox metadata DB) ───────────────────
    // Candidate stand-ins are computed by ImgWebCandidates and rendered by the merged ImgAppendMergedWeb (no
    // standalone section any more). A web image is hidden when already OWNED — matched by CRC32 exactly the way
    // ExtendDB dedups downloads (base-LB GameImages carries only CRC32, so it's the sole key). CRCs are computed
    // (via CrcBridge: ":crc32" ADS first, computed on miss) ONLY for this category's local images, never the
    // whole game — the candidates are of these same types anyway.
    private Panel ImgWebCell(MetadataDb.WebImage w)
    {
        var cell = new Panel { Size = new Size(ImgCellW, ImgCellH), BackColor = Bg };
        var frame = new Panel { BackColor = Color.FromArgb(150, 90, 200), Padding = new Padding(S(2)) };   // purple = not owned
        frame.SetBounds(S(4), S(4), ImgCellW - S(8), ImgThumbH);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        ImgLoadThumbWeb(pic, w);
        string key = w.Key;
        var wi = w;
        pic.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _imgPressKind = 2; _imgPressKey = key; ImgStartLongPress(); } };
        pic.MouseUp += (_, e) =>
        {
            ImgStopLongPress();
            if (e.Button == MouseButtons.Right)
            {
                var m = ThemedMenu();
                m.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => ImgDownloadWebList(new List<MetadataDb.WebImage> { wi })));
                m.Items.Add(new ToolStripMenuItem("🔍  View fullscreen").WithClick(() => ShowImageFullscreenWeb(wi)));
                m.Show(pic, e.Location);
                return;   // right = menu only
            }
            if (e.Button != MouseButtons.Left) return;
            if (_imgSuppressClick) { _imgSuppressClick = false; return; }
            if (_imgSelMode) ImgWebToggle(key);
            else ShowImageFullscreenWeb(wi);   // left = fullscreen
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        var chk = ImgWebSelCheckbox(key);
        cell.Controls.Add(chk); chk.BringToFront();

        // Caption: the same two lines the owned cells show (origin, then region) — so the tile you're about to
        // download already reads like the file it will become. The values come from the DB row, not from ADS
        // (nothing is stamped yet); ImageAdsWriter writes exactly these into :info on download.
        var cap = new Label
        {
            Text = w.IsLaunchbox ? "web" : "web · " + w.Origin, ForeColor = Color.FromArgb(190, 150, 230),
            BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        cap.SetBounds(S(4), ImgCellH - S(42), ImgCellW - S(8), S(20));
        cell.Controls.Add(cap);

        var rgn = new Label
        {
            Text = string.IsNullOrEmpty(w.Region) ? "World" : w.Region,   // GamesDb: a blank region IS World
            ForeColor = ProvFg, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        rgn.SetBounds(S(4), ImgCellH - S(22), ImgCellW - S(8), S(16));
        cell.Controls.Add(rgn);
        return cell;
    }

    /// <summary>Bytes for a web image — through ExtendDB's per-origin wizard fetcher when the Extended DB
    /// module is on (correct for screenscraper/steam/mirror/…), else a direct launchbox-CDN GET. Blocking.</summary>
    private byte[]? ImgFetchWebBytes(MetadataDb.WebImage w)
    {
        try
        {
            // A LIVE EmuMovies / Steam stand-in carries a full public CDN URL as its FileName and needs no auth
            // (just the Referer) — a plain GET, independent of the extended DB.
            if (string.Equals(w.Origin, "emumovies", StringComparison.OrdinalIgnoreCase))
                return WebGetBytes(w.FileName, EmuMoviesApi.MediaReferer);
            if (string.Equals(w.Origin, "steam", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(w.FileName, UriKind.Absolute, out _))
                return WebGetBytes(w.FileName, SteamApi.Referer);

            // Per-origin wizard fetch when the extended-DB module is on, or (defensively) for any non-launchbox
            // row while ExtendDB's fetcher is reachable — those have no valid CDN URL. Launchbox rows always
            // fall back to the CDN.
            bool nonLb = !w.IsLaunchbox;
            if (MediaApiBridge.UseWizardPath || (nonLb && MediaApiBridge.Available))
            {
                var b = MediaApiBridge.FetchBytes(w, Safe(() => ImgGame.Platform) ?? "");
                if (b != null && b.Length > 0) return b;
                if (nonLb) return null;            // no CDN for non-launchbox origins
            }
            _imgHttp ??= NewHttp();
            return _imgHttp.GetByteArrayAsync(w.Url).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    // ── Web-image preview disk cache: <LB>\Plugins\ExtendDB\cache\thumbs\webimg ───────────────────────────────
    // A web stand-in has no thumbnail endpoint — the CDN only serves the full-size file — so fetching one is
    // expensive. We persist the DOWNSCALED preview keyed by WebImage.Key (Origin|Type|Region|Duplicate|FileName,
    // immutable for a given stand-in), so re-navigating / re-toggling never re-downloads. The SAME key + folder
    // is used by the multi-select grid (MxDecode), so a preview fetched on either side is a hit for the other.
    private const int ImgWebPreviewDim = ThumbCache.DefaultMaxDim;   // 360 — same convention as every other thumb

    private static string? ImgWebThumbPath(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
            return Path.Combine(ThumbCache.WebImgFolder, Convert.ToHexString(hash).ToLowerInvariant() + ".jpg");
        }
        catch { return null; }
    }

    /// <summary>Decode arbitrary image bytes and re-encode a ≤360 px JPEG preview (byte[]), or null on failure.
    /// JPEG (not PNG) to match the grid's existing format and stay compact — previews sit on a dark tile, so the
    /// lost alpha of a transparent logo just blends into the background, same as the grid already does.</summary>
    private static byte[]? ImgEncodeWebPreview(byte[]? full)
    {
        if (full == null || full.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(full);
            using var src = Image.FromStream(ms);
            double scale = Math.Min(1.0, (double)ImgWebPreviewDim / Math.Max(src.Width, src.Height));
            int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
            using var bmp = new Bitmap(src, w, h);
            using var outMs = new MemoryStream();
            bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
            return outMs.ToArray();
        }
        catch { return null; }
    }

    private static void ImgSaveThumbAtomic(string path, byte[] bytes)
    {
        try
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: false); }
            catch { try { File.Delete(tmp); } catch { } }   // another cell won the race → keep theirs
        }
        catch { }
    }

    /// <summary>The ≤360 px preview bytes for a web stand-in: from the webimg disk cache when present, else
    /// fetched from the CDN, re-encoded, and cached. Null on failure. Safe to call off the UI thread.</summary>
    private byte[]? ImgWebPreviewBytes(MetadataDb.WebImage w)
    {
        string? path = ImgWebThumbPath(w.Key);
        if (path != null && File.Exists(path))
        {
            try { return File.ReadAllBytes(path); } catch { try { File.Delete(path); } catch { } }   // corrupt → refetch
        }
        var preview = ImgEncodeWebPreview(ImgFetchWebBytes(w));
        if (preview != null && path != null) ImgSaveThumbAtomic(path, preview);
        return preview;
    }

    private void ImgLoadThumbWeb(PictureBox pic, MetadataDb.WebImage w)
    {
        // In the matrix modal the tiles are built BEFORE the dialog is shown, so the PictureBox has no window
        // handle yet and BeginInvoke (below) would drop the decoded frame. Defer the load until the handle
        // exists — fires immediately in the already-shown single-game page, and on Show inside the modal.
        if (!pic.IsHandleCreated)
        {
            void OnCreated(object? _, EventArgs __) { pic.HandleCreated -= OnCreated; ImgLoadThumbWeb(pic, w); }
            pic.HandleCreated += OnCreated;
            return;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var preview = ImgWebPreviewBytes(w);   // disk cache → else fetch + cache
                if (preview == null || preview.Length == 0) return;
                using var ms = new MemoryStream(preview);
                using var tmp = Image.FromStream(ms);
                var bmp = new Bitmap(tmp);              // detach from the stream; already ≤360 px (PictureBox zoom-fits)
                try
                {
                    if (pic.IsHandleCreated) pic.BeginInvoke(new Action(() => { if (!pic.IsDisposed) { var o = pic.Image; pic.Image = bmp; o?.Dispose(); } else bmp.Dispose(); }));
                    else bmp.Dispose();
                }
                catch { bmp.Dispose(); }
            }
            catch { }
        });
    }

    // ── Download web images → disk (+ ExtendDB-format ADS) ────────────────────
    private void ImgDownloadSelected()
    {
        if (_imgWebSel.Count == 0) return;
        // Download grouped BY SOURCE, in the order the source checkboxes were turned on (_imgSourceOrder), then in
        // on-screen order within each source — so a mixed selection lands database → EmuMovies → Steam (or whatever
        // order the user enabled them in), not hash-set order.
        int SrcRank(string k)
        {
            var s = _imgWebSource.TryGetValue(k, out var v) ? v : "web";
            int i = _imgSourceOrder.IndexOf(s);
            return i < 0 ? int.MaxValue : i;
        }
        var picks = _imgWebSel.Where(_imgWebByKey.ContainsKey)
                              .OrderBy(SrcRank)
                              .ThenBy(k => _imgCatAllWebKeys.IndexOf(k))
                              .Select(k => _imgWebByKey[k]).ToList();
        ImgDownloadWebList(picks);
    }

    private void ImgDownloadWebList(List<MetadataDb.WebImage> picks)
    {
        if (_readOnly || picks == null || picks.Count == 0) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        int ok = 0, fail = 0;
        UseWaitCursor = true;
        try { foreach (var w in picks) { if (ImgDownloadOne(g, w, dbId, plat)) ok++; else fail++; } }
        finally { UseWaitCursor = false; }
        if (_imgSelMode) ImgExitSelectMode();
        ImgAfterOp(plat);
        MessageBox.Show(this, $"Downloaded {ok} image(s)." + (fail > 0 ? $"\n{fail} failed." : ""),
            "LiteBox", MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private bool ImgDownloadOne(IGame g, MetadataDb.WebImage w, int dbId, string plat)
    {
        try
        {
            string idStr = Safe(() => g.Id) ?? "";
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            // GamesDb: a blank DB region IS "World", and LaunchBox stores World images under <type>\World\
            // (this library has 14k files there) — NOT at the type-folder root. The root is only for images
            // that genuinely carry no region.
            string region = string.IsNullOrEmpty(w.Region) ? "World" : w.Region;
            string? baseFolder = MediaResolver.TypeFolder(plat, w.Type);
            if (string.IsNullOrEmpty(baseFolder)) return false;
            string dir = ImgSearchDir(baseFolder, region);
            Directory.CreateDirectory(dir);
            // Extension the ExtendDB way (ExtractFileType): "filetype=" URL param first, else last dot minus
            // query — the FileName is a source URL for non-launchbox origins, so its tail is unreliable.
            string ext = ImageFileType.Extract(w.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            byte[]? bytes = ImgFetchWebBytes(w);   // ExtendDB per-origin fetcher when the module is on, else CDN
            if (bytes == null || bytes.Length == 0) return false;
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}{ext}");
            File.WriteAllBytes(target, bytes);
            ImageAdsWriter.WriteForDownload(target, w, dbId, plat);   // ":crc32" + ":info" in ExtendDB format
            return true;
        }
        catch { return false; }
    }

    private static System.Net.Http.HttpClient NewHttp()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try { h.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBox/1.0"); } catch { }
        return h;
    }

    // ── Fullscreen viewer for a web image ─────────────────────────────────────
    private void ShowImageFullscreenWeb(MetadataDb.WebImage w)
    {
        byte[]? bytes = null;
        UseWaitCursor = true;
        try { bytes = ImgFetchWebBytes(w); } catch { } finally { UseWaitCursor = false; }
        ShowImageFullscreenBytes(bytes);
    }

    private void ShowImageFullscreenBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) { MessageBox.Show(this, "Couldn't load the image.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var scr = Screen.FromControl(this) ?? Screen.PrimaryScreen;
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Bounds = scr?.Bounds ?? new Rectangle(0, 0, 1280, 720), BackColor = Color.Black,
            ShowInTaskbar = false, KeyPreview = true, TopMost = true,
        };
        var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Hand };
        try { using var ms = new MemoryStream(bytes); using var tmp = Image.FromStream(ms); pb.Image = new Bitmap(tmp); } catch { }
        pb.Click += (_, _) => f.Close();
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.FormClosed += (_, _) => { try { pb.Image?.Dispose(); } catch { } };
        f.Controls.Add(pb);
        f.ShowDialog(this);
    }

    private Panel ImgCell(ImgFile img, string regroupement)
    {
        var cell = new Panel { Size = new Size(ImgCellW, ImgCellH), BackColor = Bg };
        bool isSlotPick = _imgLbSlotPick != null && string.Equals(_imgLbSlotPick, img.Path, StringComparison.OrdinalIgnoreCase);
        bool isTypePick = !isSlotPick && _imgLbPicks.Contains(img.Path);
        var pic = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand,
        };
        var picBounds = new Rectangle(S(4), S(4), ImgCellW - S(8), ImgThumbH);
        if (isSlotPick || isTypePick)
        {
            // Gold + thick = the image LaunchBox actually displays for this slot. Neutral + thin = merely the
            // winner of its own image type (a lower-ranked type never gets displayed — see ImgLbSlotPick).
            var frame = new Panel
            {
                BackColor = isSlotPick ? LbSlotColor : LbTypeColor,
                Padding = new Padding(isSlotPick ? S(3) : S(2)),
            };
            frame.Bounds = picBounds;
            pic.Dock = DockStyle.Fill;
            frame.Controls.Add(pic);
            cell.Controls.Add(frame);
        }
        else pic.Bounds = picBounds;
        ImgLoadThumb(pic, img.Path);
        pic.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _imgPressKind = 1; _imgPressKey = img.Path; ImgStartLongPress(); } };
        pic.MouseUp += (_, e) =>
        {
            ImgStopLongPress();
            if (e.Button == MouseButtons.Right) { ImgContextMenu(img, regroupement).Show(pic, e.Location); return; }   // right = menu only
            if (e.Button != MouseButtons.Left) return;
            if (_imgSuppressClick) { _imgSuppressClick = false; return; }   // eat the click after a long-press
            if (_imgSelMode) ImgToggleSel(img.Path);
            else ShowImageFullscreenPath(img.Path);   // left = fullscreen
        };
        if (!isSlotPick && !isTypePick) cell.Controls.Add(pic);   // otherwise pic already lives inside the frame

        if (isSlotPick || isTypePick)
        {
            int w = isSlotPick ? S(44) : S(36);
            var badge = new Label
            {
                Text = isSlotPick ? "★★ LB" : "★ LB",
                ForeColor = isSlotPick ? Color.FromArgb(30, 25, 5) : Color.FromArgb(235, 238, 245),
                BackColor = isSlotPick ? LbSlotColor : LbTypeColor,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(w, S(16)), Location = new Point(ImgCellW - w - S(6), S(8)),
            };
            cell.Controls.Add(badge); badge.BringToFront();
            new ToolTip().SetToolTip(badge, isSlotPick
                ? "LaunchBox DISPLAYS this image for this slot (its image type outranks the others here)"
                : "Best image of its own type — but LaunchBox displays a higher-ranked type instead");
        }

        // Multi-select checkbox (top-left) — hidden until select mode is on.
        var chk = new CheckBox
        {
            AutoCheck = false, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(24, 24, 30), ForeColor = Fg,
            Size = new Size(S(18), S(18)), Location = new Point(S(6), S(6)), TabStop = false,
            Visible = _imgSelMode, Checked = _imgSel.Contains(img.Path),
        };
        chk.Click += (_, _) => ImgToggleSel(img.Path);
        cell.Controls.Add(chk); chk.BringToFront();
        _imgCellChk[img.Path] = chk;
        if (chk.Checked) cell.BackColor = Color.FromArgb(40, 60, 90);

        var num = new Label
        {
            Text = $"#{img.NumVal}" + (img.HasGuid ? "  [G]" : ""), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        };
        num.SetBounds(S(4), ImgCellH - S(42), ImgCellW - (ImageLockBridge.Available ? S(34) : S(8)), S(20));
        cell.Controls.Add(num);

        cell.Controls.Add(ProvLabel(img.Path, S(4), ImgCellH - S(22), ImgCellW - S(8)));   // origin · native region (ADS)

        if (ImageLockBridge.Available)
        {
            bool locked = ImageLockBridge.IsLocked(img.Path);
            var lb = new Button { FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Symbol", 8f), Cursor = Cursors.Hand, TabStop = false };
            lb.FlatAppearance.BorderSize = 0;
            lb.SetBounds(ImgCellW - S(28), ImgCellH - S(42), S(22), S(20));
            ImgStyleLockBtn(lb, locked);
            string p = img.Path;
            lb.Click += (_, _) => ImgStyleLockBtn(lb, ImageLockBridge.Toggle(p));
            cell.Controls.Add(lb);
            _imgLockBtns[regroupement].Add((lb, img.Path));
        }
        return cell;
    }

    private static void ImgStyleLockBtn(Button b, bool locked)
    {
        b.Text = locked ? "🔒" : "🔓";
        b.BackColor = locked ? Color.FromArgb(92, 78, 30) : Color.FromArgb(48, 48, 60);
        b.ForeColor = locked ? Color.FromArgb(230, 205, 90) : Color.FromArgb(160, 160, 180);
    }

    private void ImgLoadThumb(PictureBox pic, string path)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));   // read fully → no file lock
            using var tmp = Image.FromStream(ms);
            const int maxDim = 320;
            double scale = Math.Min(1.0, (double)maxDim / Math.Max(tmp.Width, tmp.Height));
            var sz = new Size(Math.Max(1, (int)(tmp.Width * scale)), Math.Max(1, (int)(tmp.Height * scale)));
            pic.Image = new Bitmap(tmp, sz);
        }
        catch { pic.Image = null; }
    }

    private static void ImgDisposePics(Control c)
    {
        if (c is PictureBox pb) { var im = pb.Image; pb.Image = null; try { im?.Dispose(); } catch { } }   // detach BEFORE dispose
        foreach (Control ch in c.Controls) ImgDisposePics(ch);
    }

    // ── Disk scan ─────────────────────────────────────────────────────────────
    private List<ImgFile> ImgScan(IGame g)
    {
        var list = new List<ImgFile>();
        string plat = Safe(() => g.Platform) ?? "";
        string idLower = (Safe(() => g.Id) ?? "").ToLowerInvariant();
        Guid.TryParse(Safe(() => g.Id) ?? "", out var id);
        string title = Safe(() => g.Title) ?? "";
        List<(string path, string type, string region)> files;
        try { files = MediaResolver.AllImageFiles(plat, id, title); } catch { files = new(); }
        foreach (var (path, type, region) in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string nl = name.ToLowerInvariant();
            bool hasGuid = !string.IsNullOrEmpty(idLower) && nl.Contains($".{idLower}-");
            int numVal = 0; string numText = "";
            int dash = nl.LastIndexOf('-');
            if (dash >= 0 && dash < nl.Length - 1 && int.TryParse(nl.Substring(dash + 1), out var n)) { numVal = n; numText = name.Substring(dash + 1); }
            list.Add(new ImgFile(path, type, string.IsNullOrEmpty(region) ? "none" : region, numVal, numText, hasGuid));
        }
        return list;
    }

    private static List<string> ImgRegionOrder(List<ImgFile> all)
    {
        // Rows: root first (display convention), then the canonical LB order (priorities → LB's fallback),
        // then anything else this game happens to have. Rows with no image are skipped by the caller.
        // Built from the ORIGINAL-cased sources — LbRegions.Order lower-cases (it feeds the cache lookups),
        // and these strings are shown as the row labels.
        var order = new List<string> { "none" };
        var seen = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        foreach (var r in LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities())
            if (!string.IsNullOrWhiteSpace(r) && seen.Add(r.Trim())) order.Add(r.Trim());
        foreach (var r in LbRegions.Fallback)
            if (seen.Add(r)) order.Add(r);
        foreach (var r in all.Select(i => i.Region).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            if (seen.Add(r)) order.Add(r);
        return order;
    }

    private static List<string> ImgMenuRegions()
    {
        var order = new List<string> { "none" };
        order.AddRange(LbApiHost.Host.Gc.SettingsWatcher.GetRegionPriorities().Where(r => !r.Equals("none", StringComparison.OrdinalIgnoreCase)));
        return order;
    }

    // ── Context menu ──────────────────────────────────────────────────────────
    private ContextMenuStrip ImgContextMenu(ImgFile img, string regroupement)
    {
        var m = ThemedMenu();
        ToolStripMenuItem MI(string text, Action click) { var it = new ToolStripMenuItem(text); it.Click += (_, _) => click(); return it; }

        m.Items.Add(MI("🗑  Delete Image", () => ImgDeleteOne(img)));
        m.Items.Add(new ToolStripSeparator());

        var moveType = new ToolStripMenuItem("Move To Type");
        foreach (var t in ImgTypesOf(regroupement)) { string tt = t; moveType.DropDownItems.Add(new ToolStripMenuItem(tt) { Checked = string.Equals(tt, img.Type, StringComparison.OrdinalIgnoreCase) }.WithClick(() => ImgTransfer(img, tt, img.Region, copy: false))); }
        m.Items.Add(moveType);

        var moveRegion = new ToolStripMenuItem("Move To Region");
        foreach (var r in ImgMenuRegions()) { string rr = r; moveRegion.DropDownItems.Add(new ToolStripMenuItem(rr == "none" ? "No Region" : rr) { Checked = string.Equals(rr, img.Region, StringComparison.OrdinalIgnoreCase) }.WithClick(() => ImgTransfer(img, img.Type, rr, copy: false))); }
        m.Items.Add(moveRegion);

        var copyType = new ToolStripMenuItem("Copy To Type");
        foreach (var t in ImgTypesOf(regroupement)) { string tt = t; copyType.DropDownItems.Add(new ToolStripMenuItem(tt).WithClick(() => ImgTransfer(img, tt, img.Region, copy: true))); }
        m.Items.Add(copyType);

        var copyRegion = new ToolStripMenuItem("Copy To Region");
        foreach (var r in ImgMenuRegions()) { string rr = r; copyRegion.DropDownItems.Add(new ToolStripMenuItem(rr == "none" ? "No Region" : rr).WithClick(() => ImgTransfer(img, img.Type, rr, copy: true))); }
        m.Items.Add(copyRegion);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("🗑  Delete all except this", () => ImgDeleteAllExcept(img, regroupement)));
        m.Items.Add(MI("Set Number…", () => ImgSetNumber(img)));
        m.Items.Add(MI(img.HasGuid ? "Remove GUID naming" : "Enable GUID naming", () => ImgToggleGuid(img)));
        m.Items.Add(MI("ℹ  Info", () => ImgInfo(img)));
        return m;
    }

    // ── Operations ────────────────────────────────────────────────────────────
    /// <summary>The file op alone (copy/move to a type+region, correct naming) — no confirm, no page rebuild.
    /// Returns true on success. Shared by the single-image menu and the bulk (multi-select) actions.</summary>
    private bool ImgDoTransfer(IGame g, ImgFile img, string targetType, string targetRegion, bool copy)
    {
        if (!copy && string.Equals(img.Type, targetType, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(img.Region, targetRegion, StringComparison.OrdinalIgnoreCase)) return false;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        string? baseFolder = MediaResolver.TypeFolder(plat, targetType);
        if (string.IsNullOrEmpty(baseFolder)) return false;
        try
        {
            string dir = ImgSearchDir(baseFolder, targetRegion);
            Directory.CreateDirectory(dir);
            string prefix = ImgPrefix(plat, idStr, sani, img.Path, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}{Path.GetExtension(img.Path)}");
            if (copy) File.Copy(img.Path, target, overwrite: false);
            else File.Move(img.Path, target, overwrite: false);
            // Defensive: on a same-volume move the source MUST be gone. If some File.Move interception left it
            // behind (behaving like a copy), remove it so a "move" is always a real move.
            if (!copy && File.Exists(img.Path) && File.Exists(target) &&
                !string.Equals(Path.GetFullPath(img.Path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(img.Path); } catch { }
            }
            return true;
        }
        catch { return false; }
    }

    private void ImgTransfer(ImgFile img, string targetType, string targetRegion, bool copy)
    {
        if (_readOnly) return;
        var g = ImgGame;
        if (ImgDoTransfer(g, img, targetType, targetRegion, copy)) ImgAfterOp(Safe(() => g.Platform) ?? "");
    }

    private void ImgDeleteOne(ImgFile img)
    {
        if (_readOnly) return;
        if (MessageBox.Show(this, $"Delete this image from disk?\n\n{img.Type} · {(img.Region == "none" ? "No Region" : img.Region)}\n{Path.GetFileName(img.Path)}\n\nThis cannot be undone.",
                "Delete Image", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { File.Delete(img.Path); }
        catch (Exception ex) { MessageBox.Show(this, "Delete failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        ImgAfterOp(Safe(() => ImgGame.Platform) ?? "");
    }

    private void ImgDeleteAllExcept(ImgFile keep, string regroupement)
    {
        if (_readOnly) return;
        var types = ImgTypesOf(regroupement);
        var victims = ImgScan(ImgGame)
            .Where(f => types.Any(t => string.Equals(t, f.Type, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !string.Equals(f.Path, keep.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (victims.Count == 0) return;
        if (MessageBox.Show(this, $"This will delete {victims.Count} image(s) from the \"{regroupement}\" slot.\n\nThis cannot be undone.",
                "Delete all except this", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        int fail = 0;
        foreach (var v in victims) { try { File.Delete(v.Path); } catch { fail++; } }
        ImgAfterOp(Safe(() => ImgGame.Platform) ?? "");
        if (fail > 0) MessageBox.Show(this, $"{fail} file(s) couldn't be deleted (locked / in use).", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ImgToggleGuid(ImgFile img)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string idStr = Safe(() => g.Id) ?? "";
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        string dir = Path.GetDirectoryName(img.Path) ?? "";
        string ext = Path.GetExtension(img.Path);
        try
        {
            string prefix = img.HasGuid ? sani : $"{sani}.{idStr}";   // toggled form
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}{ext}");
            File.Move(img.Path, target, overwrite: false);
            ImgAfterOp(Safe(() => g.Platform) ?? "");
        }
        catch (Exception ex) { MessageBox.Show(this, "Toggle GUID failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ImgSetNumber(ImgFile img)
    {
        if (_readOnly) return;
        int? target = ImgPromptNumber(img.NumVal);
        if (target == null) return;
        var g = ImgGame;
        string idStr = Safe(() => g.Id) ?? "";
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        string dir = Path.GetDirectoryName(img.Path) ?? "";
        string ext = Path.GetExtension(img.Path);
        string prefix = img.HasGuid ? $"{sani}.{idStr}" : sani;
        string prefixLower = prefix.ToLowerInvariant();
        try
        {
            string temp = img.Path + ".litebox-tmp";
            File.Move(img.Path, temp, overwrite: true);
            // Cascade: bump every file whose number >= target, highest first (avoid clobber).
            var toShift = new List<(string path, int num)>();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (!name.StartsWith(prefixLower)) continue;
                int d = name.LastIndexOf('-'); if (d < 0 || d >= name.Length - 1) continue;
                if (int.TryParse(name.Substring(d + 1), out int n) && n >= target.Value) toShift.Add((f, n));
            }
            toShift.Sort((a, b) => b.num.CompareTo(a.num));
            foreach (var (path, num) in toShift)
            {
                string fe = Path.GetExtension(path);
                string on = Path.GetFileNameWithoutExtension(path);
                int d = on.LastIndexOf('-');
                string np = Path.Combine(dir, $"{on.Substring(0, d)}-{(num + 1):D2}{fe}");
                if (path != np) File.Move(path, np, overwrite: true);
            }
            File.Move(temp, Path.Combine(dir, $"{prefix}-{target:D2}{ext}"), overwrite: true);
            ImgAfterOp(Safe(() => g.Platform) ?? "");
        }
        catch (Exception ex) { MessageBox.Show(this, "Set number failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ImgInfo(ImgFile img)
    {
        string dims = "?", size = "?";
        try { size = $"{new FileInfo(img.Path).Length / 1024.0:0.#} KB"; } catch { }
        try { using var ms = new MemoryStream(File.ReadAllBytes(img.Path)); using var bmp = Image.FromStream(ms); dims = $"{bmp.Width} × {bmp.Height}"; } catch { }
        string locked = ImageLockBridge.Available ? (ImageLockBridge.IsLocked(img.Path) ? "Yes" : "No") : "(ExtendDB not loaded)";

        string text =
            $"Type:  {img.Type}\nRegion:  {(img.Region == "none" ? "No Region" : img.Region)}\nNumber:  #{img.NumVal}\n" +
            $"GUID naming:  {(img.HasGuid ? "Yes" : "No")}\nLocked:  {locked}\nDimensions:  {dims}\nSize:  {size}\n";

        // ADS metadata (:crc32 + :info) — the ExtendDB-format provenance. When ExtendDB is loaded, :info is
        // read through its own reader (so a populated block PROVES ExtendDB re-reads what LiteBox wrote).
        string crc32 = FileMetaStore.Read(img.Path, FileMetaStore.StreamCrc32);
        var info = ImageInfoBridge.ReadAny(img.Path);
        text += "\n── ADS metadata " + (ImageInfoBridge.Available ? "(via ExtendDB reader)" : "(native)") + " ──\n";
        text += $"CRC32 (:crc32):  {(string.IsNullOrEmpty(crc32) ? "(none)" : crc32)}\n";
        if (info is ImageInfo i)
        {
            text +=
                $"Origin:  {(string.IsNullOrEmpty(i.Origin) ? "(none)" : i.Origin)}\n" +
                $"Database Id:  {i.DatabaseId}\n" +
                $"CRC32 (:info):  {i.Crc32}\n" +
                $"Duplicate:  {i.Duplicate}\n" +
                $"File type:  {(string.IsNullOrEmpty(i.FileType) ? "(none)" : i.FileType)}\n" +
                $"Native region:  {(string.IsNullOrEmpty(i.NativeRegion) ? "(none)" : i.NativeRegion)}\n" +
                $"Stored dims:  {i.SizeX} × {i.SizeY}\n" +
                $"File size:  {i.FileSize}\n" +
                $"Source:  {(string.IsNullOrEmpty(i.OriginalUrl) ? "(none)" : i.OriginalUrl)}\n";
        }
        else text += "(:info):  (none)\n";

        text += $"\n{img.Path}";
        MessageBox.Show(this, text, "Image info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private int? ImgPromptNumber(int current)
    {
        using var f = NewDialog("Set number", 320, 150);
        var lbl = new Label { Text = "New number (1–999):", Location = new Point(S(14), S(16)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var tb = new TextBox { Location = new Point(S(14), S(42)), Width = S(120), Text = current.ToString(), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        tb.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        int? result = null;
        var bottom = DialogButtons(f, out var ok, out var cancel);
        ok.Click += (_, _) => { if (int.TryParse(tb.Text, out var n) && n >= 1 && n <= 999) { result = n; f.DialogResult = DialogResult.OK; f.Close(); } };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.Controls.Add(lbl); f.Controls.Add(tb);
        return f.ShowDialog(this) == DialogResult.OK ? result : null;
    }

    // ── Fullscreen viewer (click a thumbnail) ────────────────────────────────
    private void ShowImageFullscreenPath(string path)
    {
        var scr = Screen.FromControl(this) ?? Screen.PrimaryScreen;
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Bounds = scr?.Bounds ?? new Rectangle(0, 0, 1280, 720), BackColor = Color.Black,
            ShowInTaskbar = false, KeyPreview = true, TopMost = true,
        };
        var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Hand };
        try { using var ms = new MemoryStream(File.ReadAllBytes(path)); using var tmp = Image.FromStream(ms); pb.Image = new Bitmap(tmp); } catch { }
        pb.Click += (_, _) => f.Close();
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.FormClosed += (_, _) => { try { pb.Image?.Dispose(); } catch { } };
        f.Controls.Add(pb);
        f.ShowDialog(this);
    }

    // ── After an op: remember the platform + rebuild the visible category ─────
    private void ImgAfterOp(string plat)
    {
        if (!string.IsNullOrEmpty(plat)) _imgTouchedPlatforms.Add(plat);

        // The media matrix opens a category in a MODAL: the tree's selected node is still "Images", so refresh
        // the modal's page rather than the (non-existent) tree page.
        if (_imgModalHolder != null && _imgModalCat != null) { ImgModalRefresh(); return; }

        string? cur = _tree.SelectedNode?.Tag?.ToString();
        // A move can shift an image to ANOTHER category, so drop every OTHER regroupement page from the cache
        // (they rebuild on next visit) and refresh whatever image view is showing now.
        foreach (var r in ImgRegroupements()) if (r != cur) _pages.Remove(r);
        if (cur == "Images") ImgNavRefresh();                                         // the LaunchBox navigator
        else if (cur != null && ImgIsRegroupement(cur)) { _pages.Remove(cur); ShowPage(cur); }   // a category grid
    }

    /// <summary>Called from Navigate() on a game switch: drop the (now wrong-game) category pages and rebuild
    /// whichever image node is showing.</summary>
    private void ReloadImagesIfBuilt()
    {
        bool any = _pages.Remove("Images");
        foreach (var r in ImgRegroupements()) any |= _pages.Remove(r);
        string? cur = _tree.SelectedNode?.Tag?.ToString();
        if (any && cur != null && (cur == "Images" || ImgIsRegroupement(cur))) ShowPage(cur);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // One de-duplicated media-cache rebuild per touched platform (ExtendDB's GameCache OR our HostGameCache
        // port — GameCacheBridge dispatches). Non-blocking; if ExtendDB's watcher already queued it, no-op.
        foreach (var plat in _imgTouchedPlatforms)
        {
            try { var p = PluginHelper.DataManager?.GetPlatformByName(plat); if (p != null) GameCacheBridge.RebuildPlatform(p); }
            catch { }
        }
        _imgTouchedPlatforms.Clear();
        ImgStopLongPress();
        try { _imgqConn?.Dispose(); } catch { } _imgqConn = null;   // close the Image Query in-memory SQLite
        try { _imgHttp?.Dispose(); } catch { } _imgHttp = null;      // close the web-image HTTP client

        // Matrix: drop the pending fetches (the workers exit once the queue drains / the grid is gone) and
        // free the decoded thumbnails — up to MxThumbCacheMax bitmaps would otherwise linger.
        lock (_mxQLock) { _mxQueue.Clear(); _mxPending.Clear(); }
        lock (_mxThumbLock)
        {
            foreach (var img in _mxThumbs.Values) { try { img?.Dispose(); } catch { } }
            _mxThumbs.Clear(); _mxLru.Clear(); _mxLruNodes.Clear();
        }
        VidDisposeThumbs();   // video frames: shared by the video pages, freed once, here

        base.OnFormClosed(e);
    }

    // ── File-naming helpers (mirror ExtendDB's Editgameform) ──────────────────
    private static string ImgSearchDir(string baseFolder, string region)
        => (string.IsNullOrEmpty(region) || region.Equals("none", StringComparison.OrdinalIgnoreCase)) ? baseFolder : Path.Combine(baseFolder, region);

    /// <summary>The filename prefix (before -NN): GUID form when the source is already GUID, when the target
    /// folder already holds GUID files for this game, OR when another game on the platform shares the
    /// sanitized title (a legacy name would then be ambiguous). Else the plain sanitized title.</summary>
    private string ImgPrefix(string plat, string idStr, string sani, string? sourcePath, string targetDir)
    {
        string guidMarker = $".{idStr.ToLowerInvariant()}-";
        if (!string.IsNullOrEmpty(sourcePath) && Path.GetFileNameWithoutExtension(sourcePath).ToLowerInvariant().Contains(guidMarker))
            return $"{sani}.{idStr}";
        try
        {
            if (Directory.Exists(targetDir) &&
                Directory.EnumerateFiles(targetDir).Any(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(guidMarker)))
                return $"{sani}.{idStr}";
        }
        catch { }
        if (ImgNameCollision(plat, idStr, sani)) return $"{sani}.{idStr}";
        return sani;
    }

    private bool ImgNameCollision(string plat, string idStr, string sani)
    {
        try
        {
            foreach (var g in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
            {
                if (!string.Equals(Safe(() => g.Platform), plat, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Safe(() => g.Id), idStr, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(MediaResolver.Sanitize(Safe(() => g.Title) ?? ""), sani, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static int ImgMaxNum(string dir, string prefix)
    {
        int max = 0;
        if (!Directory.Exists(dir)) return max;
        string pl = prefix.ToLowerInvariant();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (!name.StartsWith(pl)) continue;
                int dash = name.LastIndexOf('-');
                if (dash < 0 || dash >= name.Length - 1) continue;
                if (int.TryParse(name.Substring(dash + 1), out int n) && n > max) max = n;
            }
        }
        catch { }
        return max;
    }

    // ── Themed dark context menu ──────────────────────────────────────────────
    private ContextMenuStrip ThemedMenu() => new() { Renderer = new ImgMenuRenderer(), BackColor = PanelC, ForeColor = Fg };

    private sealed class ImgMenuRenderer : ToolStripProfessionalRenderer
    {
        public ImgMenuRenderer() : base(new ImgMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? Fg : SubFg; base.OnRenderItemText(e); }
    }

    private sealed class ImgMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Accent;
        public override Color MenuItemSelectedGradientBegin => Accent;
        public override Color MenuItemSelectedGradientEnd => Accent;
        public override Color MenuItemBorder => Accent;
        public override Color MenuBorder => PanelC;
        public override Color ToolStripDropDownBackground => PanelC;
        public override Color ImageMarginGradientBegin => PanelC;
        public override Color ImageMarginGradientMiddle => PanelC;
        public override Color ImageMarginGradientEnd => PanelC;
        public override Color SeparatorDark => Color.FromArgb(60, 60, 62);
    }
}

internal static class ImgMenuItemExt
{
    /// <summary>Fluent Click wiring so the submenu builders stay one-liners.</summary>
    public static System.Windows.Forms.ToolStripMenuItem WithClick(this System.Windows.Forms.ToolStripMenuItem it, System.Action onClick)
    { it.Click += (_, _) => onClick(); return it; }
}
