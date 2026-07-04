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

        // Native ListView rows size themselves to max(Font.Height, SmallImageList.ImageSize.Height) -
        // with no image list at all, that's just the cramped ~17px the classic-Windows-app look comes
        // from. No per-row icons are drawn here (ImageIndex is never set), so this list's images are
        // blank - it exists purely to stretch row height to something roomier, DPI-scaled like
        // everything else in this pass.
        float s = LiteBoxTheme.DpiScale(this);
        SmallImageList = new ImageList { ImageSize = new Size(1, (int)Math.Round(30 * s)), ColorDepth = ColorDepth.Depth32Bit };

        RetrieveVirtualItem += OnRetrieveVirtualItem;
        ColumnClick += (_, e) => { if (e.Column >= 0 && e.Column < _visCols.Count) ColumnClicked?.Invoke(_visCols[e.Column]); };
        ItemActivate += (_, _) => GameActivated?.Invoke();
        MouseUp += OnMouseUpRight;
        SelectedIndexChanged += OnSelectedIndexChanged;
        HandleCreated += (_, _) => { EnableDoubleBuffer(); ThemeHeader(); StretchColumn(); };
        Resize += (_, _) => StretchColumn();
        // Skip re-stretching when the event is the STRETCH column's own header changing width -
        // otherwise a user dragging Title's border fires this, StretchColumn() recomputes leftover
        // space (which excludes Title from its own sum), gets the same answer as before the drag,
        // and snaps the column right back - the column becomes impossible to resize by hand. A drag
        // on any OTHER column still re-stretches Title to absorb the new leftover space, and a later
        // window Resize still re-asserts the fill (so a manual narrowing doesn't leave a dead gap
        // once the window changes size again).
        ColumnWidthChanged += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && e.ColumnIndex < _visCols.Count && _visCols[e.ColumnIndex].Stretch) return;
            StretchColumn();
        };
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
        StretchColumn();
        Invalidate();
    }

    // Column widths are independent, persisted numbers (INI Col.<key> entries from years-old
    // sessions, unscaled for whatever DPI the app happens to run at now) - they will never, by
    // themselves, reliably add up to the list's actual width. Rather than chase that with more
    // scaling math, make the designated Stretch column (Title) self-heal: it always eats
    // whatever width the others didn't use, so there's never a dead gap before the detail pane,
    // at any DPI, any saved column set, any resize. Deliberately NOT "whichever column ends up
    // last" - that used to be Plays (a small numeric count with no reason to be wide), which
    // just moved the confusing dead-looking gap into a column instead of removing it.
    private bool _stretchingColumn;

    private void StretchColumn()
    {
        if (_stretchingColumn || !IsHandleCreated) return;
        var target = _visCols.FirstOrDefault(c => c.Stretch);
        if (target?.Header == null) return;
        int othersWidth = 0;
        foreach (var c in _visCols) if (c != target) othersWidth += c.Header?.Width ?? 0;
        int minWidth = 60;
        int w = Math.Max(minWidth, ClientSize.Width - othersWidth);
        if (target.Header.Width == w) return;
        _stretchingColumn = true;
        try { target.Header.Width = w; } finally { _stretchingColumn = false; }
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
        foreach (var c in _visCols)
            if (c.Header != null)
            {
                try { c.Width = c.Header.Width; } catch { }
                try { c.SavedDisplayIndex = c.Header.DisplayIndex; } catch { }
            }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IGame[] Games { get => _all; set => _all = value ?? Array.Empty<IGame>(); }

    public void RebuildView()
    {
        var prev = SelectedGame;
        IEnumerable<IGame> q = _all;
        if (FilterPredicate != null) q = q.Where(SafeFilter);
        List<IGame> list = SortGetter == null
            ? q.ToList()
            : (SortAscending ? q.OrderBy(SortGetter, ValueComparer.Instance)
                             : q.OrderByDescending(SortGetter, ValueComparer.Instance)).ToList();
        _view = list.ToArray();
        RefreshHeaderGlyphs();
        try { VirtualListSize = _view.Length; } catch { }
        try { SelectedIndices.Clear(); } catch { }
        if (prev != null) { int ix = Array.IndexOf(_view, prev); if (ix >= 0) SetSelectedAndFocused(ix); }
        Invalidate();
        ViewChanged?.Invoke();
    }

    private bool SafeFilter(IGame g) { try { return FilterPredicate(g); } catch { return false; } }

    private void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Length) { e.Item = new ListViewItem(""); return; }
        var g = _view[e.ItemIndex];
        int n = _visCols.Count;
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
