// Native virtual ListView for the game list (replaces FastObjectListView — its rendering was the
// scroll-lag cause; a native list scrolls smoothly).
//
// No owner-draw (native text rendering, the lightest per-paint path). Light theming (alternating
// rows + per-cell rating colour) is applied in RetrieveVirtualItem. View.Details + VirtualMode + native LVS_EX_DOUBLEBUFFER,
// same family as the smooth poster. Reimplements the OLV conveniences MainWindow needs:
// column model, sort by getter + direction, substring filter, selection by game identity,
// header click → sort, header right-click → column chooser, drag-reorder, INI persistence.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

/// <summary>One column of the game list. Mutable width/visible/order are persisted to the INI.</summary>
internal sealed class GameColumn
{
    public string Key;                       // stable INI identity — never localise
    public string Title;                     // header text
    public int Width;                        // current width (persisted)
    public bool Visible;                     // current visibility (persisted)
    public int SavedDisplayIndex = -1;       // persisted display position (-1 = use definition order)
    public HorizontalAlignment Align = HorizontalAlignment.Left;
    public Func<IGame, object> Sort;         // comparable value for ordering (may be null)
    public Func<IGame, string> Text;         // display text
    public Func<IGame, Color?> Fore;         // optional per-cell colour (unused in this no-styling build)
    /// <summary>This column absorbs whatever width the others don't use, so the list never
    /// leaves a dead gap before the detail pane regardless of DPI or saved column widths.
    /// Exactly one column should set this - naturally the one that benefits from extra room
    /// (Title: long names stop truncating), not just "whichever ends up last."</summary>
    public bool Stretch;

    internal ColumnHeader Header;            // the live native header (when visible)
    internal int FitWidth = -1;              // cached content-fit width (header + widest cell + pad); -1 = unmeasured
}

internal sealed class GameListView : ListView
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lvi);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEM
    {
        public uint mask; public int iItem; public int iSubItem; public uint state; public uint stateMask;
        public IntPtr pszText; public int cchTextMax; public int iImage; public IntPtr lParam;
        public int iIndent; public int iGroupId; public uint cColumns; public IntPtr puColumns;
        public IntPtr piColFmt; public int iGroup;
    }

    private const int LVM_FIRST = 0x1000;
    private const int LVM_SETITEMSTATE = LVM_FIRST + 43;
    private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    private const int LVM_GETHEADER = LVM_FIRST + 31;
    private const int LVS_EX_DOUBLEBUFFER = 0x00010000;
    private const uint LVIS_FOCUSED = 1, LVIS_SELECTED = 2;
    private const int WM_CONTEXTMENU = 0x007B;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    private readonly List<GameColumn> _columns = new();
    private List<GameColumn> _visCols = new();          // logical (subitem) order == add order
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<GameColumn> AllColumns => _columns;

    private IGame[] _all = Array.Empty<IGame>();
    private IGame[] _view = Array.Empty<IGame>();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<IGame> VisibleGames => _view;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TotalCount => _all.Length;

    public Func<IGame, object> SortGetter;
    public bool SortAscending = true;
    public GameColumn SortGlyphColumn;
    public Func<IGame, bool> FilterPredicate;

    // Light theming applied in RetrieveVirtualItem (no owner-draw): alternating row bands,
    // per-cell colour (rating). Set by MainWindow; Striped off → flat rows.
    public bool Striped;
    public Color RowBack = SystemColors.Window;
    public Color RowAlt = SystemColors.Window;
    public Color RowFore = SystemColors.WindowText;

    public event Action ViewChanged;
    public event Action SelectionChangedGame;
    public event Action GameActivated;
    public event Action<IGame[], Point> GameRightClicked;
    public event Action<GameColumn> ColumnClicked;
    public event Action<Point> ColumnChooserRequested;

    private bool _selPending;

    public GameListView()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        MultiSelect = true;
        HideSelection = false;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        BorderStyle = BorderStyle.None;
        AllowColumnReorder = true;

        // Native ListView rows size themselves to max(Font.Height, SmallImageList.ImageSize.Height).
        // With no image list at all that's the cramped ~17px classic-Windows look; a taller BLANK image
        // list (no per-row icon is ever drawn) stretches the row height. A two-line height is one the
        // native control fills by WRAPPING long cell text onto a second line; a compact height keeps
        // text on one line (truncated). See ApplyRowHeight / the TwoLineRows option.
        ApplyRowHeight();

        RetrieveVirtualItem += OnRetrieveVirtualItem;
        ColumnClick += (_, e) => { if (e.Column >= 0 && e.Column < _visCols.Count) ColumnClicked?.Invoke(_visCols[e.Column]); };
        ItemActivate += (_, _) => GameActivated?.Invoke();
        MouseUp += OnMouseUpRight;
        SelectedIndexChanged += OnSelectedIndexChanged;
        HandleCreated += (_, _) => { EnableDoubleBuffer(); ThemeHeader(); ApplyRowHeight(); MeasureContentFits(); AutoFit(); };
        Resize += (_, _) => AutoFit();   // cheap: reuses cached content-fit widths, only recomputes the fill
        // Capture the user's BASE width during the DRAG (ColumnWidthChanging fires live, left button
        // held), NOT in ColumnWidthChanged. Programmatic width changes (AutoFit shrinking a column to
        // fit a list with shorter content; the native control re-laying-out when the scrollbar toggles)
        // ALSO raise these events, sometimes deferred past the _autoFitting window — mis-adopting one
        // as the base is what made a manually-widened column stay tiny after visiting a shorter list.
        // The mouse-button state cleanly separates a real drag from every programmatic/native change;
        // _autoFitting still guards the re-balance AutoFit triggers on the OTHER columns.
        ColumnWidthChanging += (_, e) =>
        {
            if (_autoFitting || Control.MouseButtons != MouseButtons.Left) return;
            if (e.ColumnIndex >= 0 && e.ColumnIndex < _visCols.Count)
                _visCols[e.ColumnIndex].Width = e.NewWidth;   // adopt the dragged width as the persisted base
        };
        ColumnWidthChanged += (_, _) => { if (!_autoFitting) AutoFit(); };   // re-balance after a drag / native change
    }

    // The list body picks up dark scrollbars/selection from ApplyDarkScroll (MainWindow) via
    // SetWindowTheme on ITS OWN handle - but the column header is a SEPARATE native child window
    // (SysHeader32, fetched via LVM_GETHEADER) that never got that treatment, so it stayed the
    // classic light Win32 bevel button style while every row below it went dark. That header/body
    // mismatch reads as "old Windows app" faster than almost anything else in this UI.
    private void ThemeHeader()
    {
        try
        {
            IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero) return;
            SetWindowTheme(header, "DarkMode_ItemsView", null);
            SetWindowPos(header, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { }
    }

    public GameColumn AddColumn(GameColumn c) { _columns.Add(c); return c; }

    public void RebuildColumns()
    {
        BeginUpdate();
        try
        {
            Columns.Clear();
            foreach (var c in _columns) c.Header = null;
            _visCols = _columns.Where(c => c.Visible)
                .Select((c, defIdx) => (c, key: c.SavedDisplayIndex >= 0 ? c.SavedDisplayIndex : 1000 + defIdx))
                .OrderBy(t => t.key).Select(t => t.c).ToList();
            foreach (var c in _visCols)
            {
                var h = new ColumnHeader { Text = HeaderText(c), Width = c.Width > 0 ? c.Width : 100, TextAlign = c.Align };
                Columns.Add(h);
                c.Header = h;
            }
        }
        finally { EndUpdate(); }
        MeasureContentFits();   // visible-column set changed → re-measure content fit
        AutoFit();
        Invalidate();
    }

    // ── Smart column fit — two rules that keep the list exactly as wide as its window ─────────
    // Persisted per-column widths never, by themselves, add up to the list's real width (different
    // DPI, hidden/shown columns, years-old sessions). Instead of chasing that with scaling math, the
    // width each column actually GETS is derived every time the list is shown or resized:
    //   • non-Stretch columns SHRINK to their content: width = min(userWidth, contentFit). A column
    //     is never wider than it needs, which frees space (A); userWidth acts as a MAX cap.
    //   • the one Stretch column (Title) GROWS to fill: width = max(userWidth, leftover). It eats the
    //     freed space but never drops below what the user set (B); userWidth acts as a MIN floor.
    // contentFit is measured once per list display (MeasureContentFits), cached on the column, and
    // reused by AutoFit on every resize so dragging the window edge stays cheap.
    private const int FitRowCap = 15000;   // above this many rows, skip the content scan (keep user widths)
    private const int FitCandidates = 8;   // longest-by-length cells kept per column as measure candidates
    private const int FitPad = 14;         // cell padding + a little slack so nothing truncates
    private const int MinCol = 24;         // never collapse a visible column below this
    private bool _autoFitting;             // true while WE assign header widths → ignore our own ColumnWidthChanged
    private static readonly Comparison<string> ByLenDesc = (a, b) => b.Length.CompareTo(a.Length);

    // Display option "Auto-fit column widths" (default on). Off → every column, Title included, keeps
    // exactly the width the user drags it to (classic manual sizing); the dead gap before the detail
    // pane may reappear, which is then the user's explicit choice.
    private bool _autoFitColumns = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoFitColumns
    {
        get => _autoFitColumns;
        set
        {
            if (_autoFitColumns == value) return;
            _autoFitColumns = value;
            if (!IsHandleCreated) return;
            if (value) { MeasureContentFits(); AutoFit(); }   // turned ON → measure + re-balance
            else RestoreBaseWidths();                         // turned OFF → back to the user's exact widths
        }
    }

    // Put every header back to its user base width (used when auto-fit is turned OFF).
    private void RestoreBaseWidths()
    {
        _autoFitting = true;
        try { foreach (var c in _visCols) if (c.Header != null) c.Header.Width = c.Width > 0 ? c.Width : 100; }
        finally { _autoFitting = false; }
    }

    // Display option "Two-line rows" (default on). On → rows are tall enough that the native ListView
    // WRAPS long cell text onto a second line. Off → compact single-line rows (long text truncates,
    // more games fit). It is the ROW HEIGHT that drives the native wrap; there is no per-column control
    // (the OS wraps every column or none), which is why this is a single global toggle.
    private bool _twoLineRows = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TwoLineRows
    {
        get => _twoLineRows;
        set { if (_twoLineRows == value) return; _twoLineRows = value; ApplyRowHeight(); }
    }

    private ImageList _rowSizer;   // a blank image list whose height sets the row height

    // Row height = one blank-image height. 30px fits two wrapped lines (the native control fills the
    // extra height by wrapping); 22px fits a single line (a second line has no room, so text truncates).
    // Re-applied on HandleCreated so the DPI scale is the real monitor's, not the pre-handle default.
    private void ApplyRowHeight()
    {
        float s = LiteBoxTheme.DpiScale(this);
        int h = (int)Math.Round((_twoLineRows ? 30 : 22) * s * _zoom);
        var old = _rowSizer;
        _rowSizer = new ImageList { ImageSize = new Size(1, Math.Max(1, h)), ColorDepth = ColorDepth.Depth32Bit };
        SmallImageList = _rowSizer;
        old?.Dispose();
    }

    // ── Zoom (Ctrl +/- and Ctrl-wheel over the game list; level owned + persisted by MainWindow) ──
    // Scales the FONT and the row height together, then re-measures content (text widths change with
    // the font) and re-fits the columns. baseFontPt is the un-zoomed point size, passed each call so
    // repeated steps never compound off an already-scaled font.
    private float _zoom = 1f;
    private float _baseFontPt = 9f;
    public void SetZoom(float zoom, float baseFontPt)
    {
        if (baseFontPt > 0) _baseFontPt = baseFontPt;
        _zoom = Math.Clamp(zoom, 0.5f, 2f);
        try { Font = new Font(Font.FontFamily, _baseFontPt * _zoom, Font.Style); } catch { }
        ApplyRowHeight();
        MeasureContentFits();
        AutoFit();
        Invalidate();
    }

    // Measure each non-Stretch column's content-fit width (widest of its header text + its cells) over
    // the CURRENT view, once. Proportional font: longest-by-chars ≠ widest-by-pixels, so keep the few
    // longest cells per column and MeasureText each rather than trusting a single candidate.
    private void MeasureContentFits()
    {
        if (!_autoFitColumns || !IsHandleCreated) return;
        var cols = new List<GameColumn>();
        foreach (var c in _visCols) if (!c.Stretch && c.Header != null) cols.Add(c);
        if (cols.Count == 0) return;

        var view = _view;
        if (view.Length > FitRowCap) { foreach (var c in cols) c.FitWidth = -1; return; }   // too big → no shrink

        var cand = new List<string>[cols.Count];
        for (int i = 0; i < cols.Count; i++) cand[i] = new List<string>(FitCandidates + 1);
        foreach (var g in view)
            for (int i = 0; i < cols.Count; i++)
            {
                string t = CellText(cols[i], g);
                if (string.IsNullOrEmpty(t)) continue;
                var list = cand[i];
                if (list.Count < FitCandidates) { list.Add(t); if (list.Count == FitCandidates) list.Sort(ByLenDesc); }
                else if (t.Length > list[FitCandidates - 1].Length) { list[FitCandidates - 1] = t; list.Sort(ByLenDesc); }
            }

        for (int i = 0; i < cols.Count; i++)
        {
            int max = TextRenderer.MeasureText(HeaderText(cols[i]), Font).Width;
            foreach (var t in cand[i]) { int w = TextRenderer.MeasureText(t, Font).Width; if (w > max) max = w; }
            cols[i].FitWidth = max + FitPad;
        }
    }

    // Apply the two rules from the cached FitWidth values. Cheap — safe to call on every resize.
    private void AutoFit()
    {
        if (!_autoFitColumns || _autoFitting || !IsHandleCreated) return;
        _autoFitting = true;
        try
        {
            GameColumn stretch = null;
            int othersWidth = 0;
            foreach (var c in _visCols)
            {
                if (c.Header == null) continue;
                if (c.Stretch) { stretch = c; continue; }
                int baseW = c.Width > 0 ? c.Width : 100;
                int w = c.FitWidth > 0 ? Math.Min(baseW, c.FitWidth) : baseW;   // A: shrink to content, capped at user width
                if (w < MinCol) w = MinCol;
                if (c.Header.Width != w) c.Header.Width = w;
                othersWidth += w;
            }
            if (stretch?.Header != null)
            {
                int baseW = stretch.Width > 0 ? stretch.Width : 100;
                int w = Math.Max(baseW, ClientSize.Width - othersWidth);        // B: fill leftover, floored at user width
                if (stretch.Header.Width != w) stretch.Header.Width = w;
            }
        }
        finally { _autoFitting = false; }
    }

    private string HeaderText(GameColumn c)
        => ReferenceEquals(c, SortGlyphColumn) ? c.Title + (SortAscending ? "  ▲" : "  ▼") : c.Title;

    private void RefreshHeaderGlyphs()
    {
        foreach (var c in _visCols) if (c.Header != null) c.Header.Text = HeaderText(c);
    }

    public void SetColumnVisible(GameColumn c, bool visible)
    {
        if (c == null || c.Visible == visible) return;
        SyncFromUi();
        c.Visible = visible;
        RebuildColumns();
    }

    public void SyncFromUi()
    {
        // Width is deliberately NOT read back from the live header here: the header width is the
        // AutoFit-COMPUTED value (shrunk/filled), not the user's base. The base is captured directly
        // on a user drag (ColumnWidthChanged), so it stays the pure user intent that gets persisted.
        foreach (var c in _visCols)
            if (c.Header != null)
                try { c.SavedDisplayIndex = c.Header.DisplayIndex; } catch { }
    }

    // A NEW array reference = a different game set (platform/node switch — MainWindow rebuilds
    // _current per node); the SAME reference = a re-sort / re-filter of the current set. The next
    // RebuildView uses this to decide whether to snap the scroll back to the top.
    private bool _setSwapped;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IGame[] Games
    {
        get => _all;
        set
        {
            var v = value ?? Array.Empty<IGame>();
            if (!ReferenceEquals(_all, v)) _setSwapped = true;
            _all = v;
        }
    }

    public void RebuildView()
    {
        var prev = SelectedGame;
        int prevLen = _view.Length;   // to detect a shrink (see the scroll-clamp below)
        IEnumerable<IGame> q = _all;
        if (FilterPredicate != null) q = q.Where(SafeFilter);
        List<IGame> list = SortGetter == null
            ? q.ToList()
            : (SortAscending ? q.OrderBy(SortGetter, ValueComparer.Instance)
                             : q.OrderByDescending(SortGetter, ValueComparer.Instance)).ToList();
        _view = list.ToArray();
        RefreshHeaderGlyphs();
        bool swapped = _setSwapped; _setSwapped = false;
        // A native virtual ListView carries TWO kinds of stale state across a content change:
        //   • its pixel scroll offset (both directions) — a same-or-larger platform came up
        //     parked mid-list, a smaller one past its end (blank);
        //   • worse, a stale ITEMS-AREA ORIGIN: rows painted shifted way down while the
        //     scrollbar sits at the top (user screenshots: first rows at the BOTTOM of the
        //     viewport, or a 1-row list rendering nothing at all). EnsureVisible can't fix
        //     that one — the control believes item 0 is already visible.
        // On a SET SWAP (new Games array = platform/node switch) zero VirtualListSize first:
        // the control drops its whole geometry (origin + scroll) and recomputes from scratch
        // for the new size. Same-set rebuilds (sort, search typing) keep the fast direct set.
        try
        {
            if (swapped && IsHandleCreated)
            {
                BeginUpdate();
                try { VirtualListSize = 0; VirtualListSize = _view.Length; }
                finally { EndUpdate(); }
            }
            else VirtualListSize = _view.Length;
        }
        catch { }
        MeasureContentFits();   // view content changed → re-fit columns (capped + cached, so cheap)
        AutoFit();
        try { SelectedIndices.Clear(); } catch { }
        int selIx = prev != null ? Array.IndexOf(_view, prev) : -1;
        if (selIx >= 0) SetSelectedAndFocused(selIx);
        // Scroll snap: on a swap, to the surviving selection or the top; on a same-set SHRINK
        // (search narrowing), back into range. EnsureVisible is a no-op when the target is
        // already visible, so a same-set same-size refresh keeps the user's scroll untouched.
        if (_view.Length > 0 && (swapped || _view.Length < prevLen))
        {
            try { EnsureVisible(selIx >= 0 ? selIx : 0); } catch { }
        }
        Invalidate();
        // The VirtualListSize=0 reset above is NOT always enough: the stale-origin state can
        // survive it (measured — SNES switch still painted row 0 ~800px down, scrollbar at
        // top). Post-layout self-check: once this message batch settles, ask the control
        // where item 0 actually sits; parked way below the header = corrupted geometry →
        // recreate the native handle (guaranteed full reset), restore selection, re-snap.
        if (swapped && _view.Length > 0 && IsHandleCreated)
        {
            int keepSel = selIx;
            try { BeginInvoke((Action)(() => VerifyOriginAfterSwap(keepSel))); } catch { }
        }
        ViewChanged?.Invoke();
    }

    private void VerifyOriginAfterSwap(int selIx)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated || _view.Length == 0) return;
            var r = GetItemRect(0, ItemBoundsPortion.Entire);
            // Healthy: item 0 right under the header (~header height), or scrolled above the
            // viewport (negative Top — a selection was re-snapped). Corrupted: hundreds of px
            // down while the scrollbar sits at the top.
            int rowH = _rowSizer?.ImageSize.Height ?? 30;
            int worstHealthyTop = rowH * 2 + 64;   // header + generous slack
            if (r.Top <= worstHealthyTop) return;
            Console.WriteLine($"[gamelist] stale items origin after swap (item0.Top={r.Top}, rowH={rowH}) — recreating handle");
            RecreateHandle();   // HandleCreated hook re-applies double-buffer/theme/row height/fit
            if (selIx >= 0) SetSelectedAndFocused(selIx);
            try { EnsureVisible(selIx >= 0 ? selIx : 0); } catch { }
            Invalidate();
        }
        catch { }
    }

    private bool SafeFilter(IGame g) { try { return FilterPredicate(g); } catch { return false; } }

    private void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        int n = _visCols.Count;
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Length)
        {
            // A virtual item MUST carry one subitem PER COLUMN — even the placeholder we hand back
            // for an index that briefly falls out of range. That happens during a platform switch:
            // VirtualListSize can still reflect the previous (larger) platform's count while _view
            // already points at the smaller new array, so a still-visible row index is momentarily
            // >= _view.Length. A single-subitem item here makes WinForms throw
            // "…needs a SubItem for each ListView column" the instant the native control asks for
            // column >= 1 (a forced header repaint — StretchColumn/ThemeHeader — makes it ask).
            var blank = new ListViewItem("");
            for (int i = 1; i < n; i++) blank.SubItems.Add("");
            e.Item = blank;
            return;
        }
        var g = _view[e.ItemIndex];
        var it = new ListViewItem(n > 0 ? CellText(_visCols[0], g) : "");
        for (int i = 1; i < n; i++) it.SubItems.Add(CellText(_visCols[i], g));

        // Theming (one-time, when the row scrolls into view — not per frame, so no scroll cost).
        Color bg = (Striped && (e.ItemIndex & 1) == 1) ? RowAlt : RowBack;
        it.UseItemStyleForSubItems = false;
        for (int i = 0; i < n && i < it.SubItems.Count; i++)
        {
            var si = it.SubItems[i];
            si.BackColor = bg;
            Color? f = null;
            if (_visCols[i].Fore != null) { try { f = _visCols[i].Fore(g); } catch { } }
            si.ForeColor = f ?? RowFore;
        }
        e.Item = it;
    }

    private static string CellText(GameColumn c, IGame g) { try { return c.Text?.Invoke(g) ?? ""; } catch { return ""; } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IGame SelectedGame
    {
        get
        {
            try { var s = SelectedIndices; if (s.Count > 0) { int i = s[0]; if (i >= 0 && i < _view.Length) return _view[i]; } }
            catch { }
            return null;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IGame[] SelectedGames
    {
        get
        {
            try { return SelectedIndices.Cast<int>().Where(i => i >= 0 && i < _view.Length).Select(i => _view[i]).ToArray(); }
            catch { return Array.Empty<IGame>(); }
        }
    }

    public void SelectGame(IGame g, bool focus)
    {
        int ix = Array.IndexOf(_view, g);
        if (ix < 0) return;
        SetSelectedAndFocused(ix);
        try { EnsureVisible(ix); } catch { }
        if (focus) { try { Focus(); } catch { } }
    }

    public void SelectFirst()
    {
        if (_view.Length == 0) return;
        SetSelectedAndFocused(0);
        try { EnsureVisible(0); } catch { }
    }

    public void RefreshGame(IGame g)
    {
        int ix = Array.IndexOf(_view, g);
        if (ix >= 0) { try { RedrawItems(ix, ix, false); } catch { } }
    }

    public IGame GameAt(int viewIndex) => viewIndex >= 0 && viewIndex < _view.Length ? _view[viewIndex] : null;

    private void SetSelectedAndFocused(int index)
    {
        if (!IsHandleCreated) return;
        var clear = new LVITEM { stateMask = LVIS_SELECTED | LVIS_FOCUSED, state = 0 };
        SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)(-1), ref clear);
        var set = new LVITEM { stateMask = LVIS_SELECTED | LVIS_FOCUSED, state = LVIS_SELECTED | LVIS_FOCUSED };
        SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)index, ref set);
    }

    // Select every row (Ctrl+A). Virtual-mode friendly: LVM_SETITEMSTATE with item index -1 targets
    // all items at once, so it costs nothing regardless of how many games the view holds.
    public void SelectAll()
    {
        if (!IsHandleCreated || _view.Length == 0) return;
        var all = new LVITEM { stateMask = LVIS_SELECTED, state = LVIS_SELECTED };
        SendMessage(Handle, LVM_SETITEMSTATE, (IntPtr)(-1), ref all);
    }

    private void OnSelectedIndexChanged(object sender, EventArgs e)
    {
        if (_selPending) return;
        _selPending = true;
        try { BeginInvoke((Action)(() => { _selPending = false; if (!IsDisposed) SelectionChangedGame?.Invoke(); })); }
        catch { _selPending = false; }
    }

    private void OnMouseUpRight(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = HitTest(e.Location);
        int ix = hit?.Item?.Index ?? -1;
        if (ix < 0 || ix >= _view.Length) return;
        if (!SelectedIndices.Contains(ix)) SetSelectedAndFocused(ix);
        var games = SelectedGames;
        if (games.Length > 0) GameRightClicked?.Invoke(games, PointToScreen(e.Location));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CONTEXTMENU && IsHandleCreated)
        {
            IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (m.WParam == header)
            {
                Point pt = ((int)m.LParam == -1) ? Cursor.Position
                    : new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                ColumnChooserRequested?.Invoke(pt);
                return;
            }
        }
        base.WndProc(ref m);
    }

    private void EnableDoubleBuffer()
    {
        try { SendMessage(Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER); } catch { }
    }

    private sealed class ValueComparer : IComparer<object>
    {
        public static readonly ValueComparer Instance = new();
        public int Compare(object a, object b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            if (a is string sa && b is string sb) return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (a is IComparable ca && a.GetType() == b.GetType()) { try { return ca.CompareTo(b); } catch { } }
            return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
