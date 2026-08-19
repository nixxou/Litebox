// The game list INDEX — LaunchBox 14's "Game List Index" rebuilt for LiteBox's native list. A
// narrow strip standing where the list's scrollbar was (the owner hides it), showing one marker per
// group of the CURRENT sort: click or drag to jump straight to a group.
//
// Grouping follows the sort, not the alphabet: sorted by Title the groups are letters, sorted by
// Genre or Play Mode they are the values themselves ("Fighter / 2D", "(None)", …) — same rule as LB.
//
// A label is drawn only where there is vertical room for its text; a group squeezed out keeps its
// marker as a small tick, and stays hoverable and clickable. That is exactly why LaunchBox's own
// index can skip a thin letter (a J with two games) on a crowded list — nothing is missing, the
// letter just has no room to spell itself, and the tooltip still names it.
//
// The dark backdrop appears only while the index is being used (hover / drag), like LB's. With
// AlwaysShow off the strip keeps a slim collapsed width and stays blank until the pointer arrives,
// then widens to fit its labels while in use.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host;

internal sealed class GameListIndexBar : Control
{
    /// <summary>One entry per group, in display order: the text to draw, the full value for the
    /// tooltip, the group's first row, and whether this group may SPELL its text (only the first
    /// group of each first-segment family does — the rest stay dots whatever the room). Supplied by
    /// the owner so the bar never has to know how the list is sorted or filtered.</summary>
    public Func<IReadOnlyList<(string Label, string Tip, int Index, bool Spell)>>? GroupsProvider;
    /// <summary>Rows in the current view — the denominator for every marker position.</summary>
    public Func<int>? RowCount;
    /// <summary>Scroll the list so this row lands at the top.</summary>
    public Action<int>? JumpToRow;
    /// <summary>The row currently at the top of the list, for the position indicator.</summary>
    public Func<int>? TopRow;
    /// <summary>Rows one viewport holds. The thumb's range is rows − page — that is what lets a
    /// drag place the view anywhere, ends included, exactly like a scrollbar.</summary>
    public Func<int>? PageRows;

    /// <summary>On: markers stay painted and the strip keeps its fitted width. Off: a slim blank
    /// strip until the pointer reaches it — LaunchBox's "Always show the Game List Index" toggle.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlwaysShow
    {
        get => _alwaysShow;
        set { if (_alwaysShow == value) return; _alwaysShow = value; FitWidth(); Invalidate(); }
    }
    private bool _alwaysShow = true;

    private IReadOnlyList<(string Label, string Tip, int Index, bool Spell)> _groups
        = Array.Empty<(string, string, int, bool)>();
    private int _rows;
    private int _hover = -1;          // group under the pointer (-1 none)
    private bool _dragging;
    private bool _pointerIn;
    private int _fit = CollapsedW;    // width the current labels need
    private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 120, ReshowDelay = 60 };
    private string _tipText = "";

    private const int CollapsedW = 14;
    private const int MinW = 30;
    private const int MaxW = 220;
    private const int Pad = 6;

    public GameListIndexBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // No double-click semantics: the second press of a fast pair must be an ordinary MouseDown
        // (another jump / grab), not a WM_LBUTTONDBLCLK that the bar has no handler for.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        TabStop = false;   // pointer stays the plain arrow, like LB's index
        Width = MinW;
    }

    /// <summary>Re-read the groups from the owner and refit the width. Cheap enough to call on
    /// every view change (one pass over precomputed labels).</summary>
    public void RefreshGroups()
    {
        try
        {
            _groups = GroupsProvider?.Invoke() ?? Array.Empty<(string, string, int, bool)>();
            _rows = Math.Max(0, RowCount?.Invoke() ?? 0);
        }
        catch { _groups = Array.Empty<(string, string, int, bool)>(); _rows = 0; }
        FitWidth();
        Invalidate();
    }

    private void FitWidth()
    {
        int w = MinW;
        foreach (var (label, _, _, spell) in _groups)
            if (spell)
                w = Math.Max(w, TextRenderer.MeasureText(label, Font).Width + DotRail + 8);
        _fit = Math.Min(w, MaxW);
        int want = _alwaysShow || _pointerIn || _dragging ? _fit : CollapsedW;
        if (want != Width) Width = want;
    }

    private bool MarkersVisible => _alwaysShow || _pointerIn || _dragging;
    private bool InUse => _dragging || (_pointerIn && _hover >= 0);

    // ── geometry ──────────────────────────────────────────────────────────────
    private int YOf(int rowIndex)
    {
        int h = Math.Max(1, ClientSize.Height - Pad * 2);
        double f = _rows <= 1 ? 0 : Math.Clamp(rowIndex / (double)(_rows - 1), 0, 1);
        return Pad + (int)Math.Round(f * (h - 1));
    }

    private int GroupAt(int y)
    {
        if (_groups.Count == 0) return -1;
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < _groups.Count; i++)
        {
            int d = Math.Abs(YOf(_groups[i].Index) - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private const int DotRail = 9;   // the dots' lane along the right edge; text ends before it

    /// <summary>The thumb's centre, on the SCROLL scale: top / (rows − page), so it reaches both
    /// ends of the strip. Group markers live on the row scale — the two agree at the top and drift
    /// a little toward the bottom, the same tension every scrollbar-with-marks has.</summary>
    private int ThumbY()
    {
        int top = -1, page = 1;
        try { top = TopRow?.Invoke() ?? -1; page = Math.Max(1, PageRows?.Invoke() ?? 1); } catch { }
        if (top < 0) return -1;
        int maxTop = Math.Max(0, _rows - page);
        if (maxTop == 0) return -1;   // everything fits — no scroll position to show
        int h = Math.Max(1, ClientSize.Height - Pad * 2);
        double f = Math.Clamp(top / (double)maxTop, 0, 1);
        return Pad + (int)Math.Round(f * (h - 1));
    }

    /// <summary>Continuous drag: map the pointer straight to a top row on the scroll scale — the
    /// thumb parks anywhere, between two markers included, like LaunchBox's.</summary>
    private void DragTo(int y)
    {
        if (_rows <= 0) return;
        int page = 1;
        try { page = Math.Max(1, PageRows?.Invoke() ?? 1); } catch { }
        int maxTop = Math.Max(0, _rows - page);
        int h = Math.Max(1, ClientSize.Height - Pad * 2);
        double f = Math.Clamp((y - Pad) / (double)(h - 1), 0, 1);
        try { JumpToRow?.Invoke((int)Math.Round(f * maxTop)); } catch { }
        Invalidate();
    }

    // ── paint ─────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        // The dark backdrop only while the index is in use — LB's rule, so the strip never sits as
        // a permanent dark band beside the list.
        if (InUse)
            using (var bg = new SolidBrush(LiteBoxTheme.PanelC))
                g.FillRectangle(bg, ClientRectangle);

        if (!MarkersVisible || _groups.Count == 0 || _rows == 0) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Current position: a thin accent line, LB's look — the drag handle for free scrolling.
        int ty = ThumbY();
        if (ty >= 0)
        {
            using var pos = new SolidBrush(_dragging ? LiteBoxTheme.Accent
                                                     : Color.FromArgb(210, LiteBoxTheme.Accent));
            g.FillRectangle(pos, 2, ty - 1, ClientSize.Width - 4, 3);
        }

        using var tick = new SolidBrush(Color.FromArgb(150, LiteBoxTheme.SubFg));
        using var tickHot = new SolidBrush(LiteBoxTheme.Fg);

        // Dots form a rail along the RIGHT edge; each label ends a small gap BEFORE the rail so
        // text never crushes into the dots — LB's alignment.
        int lineH = Font.Height;
        int rx = ClientSize.Width - 4;

        // Pass 1 — which spell-eligible labels actually get room. NOT first-come-first-served: a
        // label that lacks room but whose family holds ≥50% more games than the one occupying the
        // slice EVICTS it (cascading upward while it keeps out-weighing), so a three-game family
        // can't silence the three-hundred-game one right under it.
        var placed = new List<(int gi, int top, int bottom, int weight)>();
        for (int i = 0; i < _groups.Count; i++)
        {
            if (!_groups[i].Spell) continue;
            int next = _rows;
            for (int j = i + 1; j < _groups.Count; j++)
                if (_groups[j].Spell) { next = _groups[j].Index; break; }
            int weight = Math.Max(1, next - _groups[i].Index);
            int y = YOf(_groups[i].Index);
            int top = y - lineH / 2, bottom = y + lineH / 2;
            if (top < 0 || bottom > ClientSize.Height) continue;
            while (placed.Count > 0 && top <= placed[^1].bottom
                   && (long)weight * 2 > (long)placed[^1].weight * 3)
                placed.RemoveAt(placed.Count - 1);
            if (placed.Count == 0 || top > placed[^1].bottom)
                placed.Add((i, top, bottom, weight));
        }
        var spelled = new HashSet<int>();
        foreach (var p in placed) spelled.Add(p.gi);

        // Pass 2 — draw: the chosen labels as text, everything else as the dot rail. The hovered
        // group always shows its text (a transient emphasis, allowed to overlap).
        for (int i = 0; i < _groups.Count; i++)
        {
            var (label, _, row, spell) = _groups[i];
            int y = YOf(row);
            bool hot = i == _hover && _pointerIn;
            if (spelled.Contains(i) || (hot && spell))
            {
                var r = new Rectangle(0, y - lineH / 2, rx - DotRail, lineH);
                TextRenderer.DrawText(g, label, Font, r, hot ? LiteBoxTheme.Fg : LiteBoxTheme.SubFg,
                                      TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            else
            {
                int d = hot ? 5 : 3;
                g.FillEllipse(hot ? tickHot : tick, rx - d, y - d / 2f, d, d);
            }
        }
    }

    // ── input ─────────────────────────────────────────────────────────────────
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        // A quick SECOND press is not another grab: it means "put the thumb right here" — the
        // pointer position becomes the scroll position, no grab offset, no group snap.
        if (e.Clicks >= 2)
        {
            _grabOffset = 0;
            DragTo(e.Y);
            return;
        }
        // Pressing NEAR the thumb (not just dead on the 3px line) grabs it where it stands — no
        // snap, so a drag can start from a position between two markers without the list lurching
        // first. Pressing farther away is a jump to the nearest group (the letter you aimed at),
        // and the drag goes on from there.
        int ty = ThumbY();
        bool onThumb = ty >= 0 && Math.Abs(e.Y - ty) <= GrabZone;
        _grabOffset = onThumb ? e.Y - ty : 0;
        if (!onThumb) JumpAt(e.Y);
        Invalidate();
    }
    private int _grabOffset;             // pointer-to-thumb offset while dragging (0 = jumped)
    private const int GrabZone = 18;     // px around the 3px thumb line that count as grabbing it —
                                         // wider than the line (nobody aims at 3px), 26 was too greedy

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = GroupAt(e.Y);
        if (i != _hover)
        {
            _hover = i;
            string t = i >= 0 && i < _groups.Count ? _groups[i].Tip : "";
            if (t != _tipText) { _tipText = t; if (t.Length > 0) _tip.SetToolTip(this, t); }
            Invalidate();
        }
        // Any movement while pressed is a CONTINUOUS drag: the pointer maps straight to a scroll
        // position, markers or not — the thumb parks wherever it is let go.
        if (_dragging) DragTo(e.Y - _grabOffset);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        FitWidth();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _pointerIn = true;
        RefreshGroups();   // groups may be stale (scroll/sort since last hover) — one cheap pass
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        if (!_dragging) { _pointerIn = false; FitWidth(); }
        Invalidate();
    }

    /// <summary>The wheel belongs to the list beside the strip — the index is a jump control, not a
    /// scrollbar, and swallowing the wheel would make it a dead zone at the list's edge.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var list = FindList();
        if (list == null) { base.OnMouseWheel(e); return; }
        SendMessage(list.Handle, 0x020A /*WM_MOUSEWHEEL*/,
            (IntPtr)(e.Delta << 16),
            (IntPtr)((Cursor.Position.Y << 16) | (Cursor.Position.X & 0xFFFF)));
        Invalidate();   // the position indicator moved
    }

    private Control? FindList()
    {
        if (Parent == null) return null;
        foreach (Control c in Parent.Controls)
            if (c is ListView lv && lv.Visible) return lv;
        return null;
    }

    private void JumpAt(int y)
    {
        int i = GroupAt(y);
        if (i < 0 || i >= _groups.Count) return;
        try { JumpToRow?.Invoke(_groups[i].Index); } catch { }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
