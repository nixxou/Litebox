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
    /// <summary>(label, first row index) per group, in display order. Supplied by the owner so the
    /// bar never has to know how the list is sorted or filtered.</summary>
    public Func<IReadOnlyList<(string Label, int Index)>>? GroupsProvider;
    /// <summary>Rows in the current view — the denominator for every marker position.</summary>
    public Func<int>? RowCount;
    /// <summary>Scroll the list so this row lands at the top.</summary>
    public Action<int>? JumpToRow;
    /// <summary>The row currently at the top of the list, for the position indicator.</summary>
    public Func<int>? TopRow;

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

    private IReadOnlyList<(string Label, int Index)> _groups = Array.Empty<(string, int)>();
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
        TabStop = false;
        Cursor = Cursors.Hand;
        Width = MinW;
    }

    /// <summary>Re-read the groups from the owner and refit the width. Cheap enough to call on
    /// every view change (one pass over precomputed labels).</summary>
    public void RefreshGroups()
    {
        try
        {
            _groups = GroupsProvider?.Invoke() ?? Array.Empty<(string, int)>();
            _rows = Math.Max(0, RowCount?.Invoke() ?? 0);
        }
        catch { _groups = Array.Empty<(string, int)>(); _rows = 0; }
        FitWidth();
        Invalidate();
    }

    private void FitWidth()
    {
        int w = MinW;
        foreach (var (label, _) in _groups)
            w = Math.Max(w, TextRenderer.MeasureText(label, Font).Width + 8);
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

        // Current position: a soft bar behind the markers, so the index doubles as a scroll indicator.
        int top = -1;
        try { top = TopRow?.Invoke() ?? -1; } catch { }
        if (top >= 0)
        {
            using var pos = new SolidBrush(Color.FromArgb(36, LiteBoxTheme.Fg));
            g.FillRectangle(pos, 0, Math.Max(0, YOf(top) - 6), ClientSize.Width, 12);
        }

        using var tick = new SolidBrush(Color.FromArgb(150, LiteBoxTheme.SubFg));
        using var tickHot = new SolidBrush(LiteBoxTheme.Fg);

        int lineH = Font.Height;
        int cx = ClientSize.Width / 2;
        int lastLabelBottom = int.MinValue;
        for (int i = 0; i < _groups.Count; i++)
        {
            var (label, row) = _groups[i];
            int y = YOf(row);
            bool hot = i == _hover && _pointerIn;

            // A label needs its own slice of height; when the previous one already used it the
            // group keeps a tick — still hoverable and clickable, nothing dropped but the text.
            bool room = y - lineH / 2 >= lastLabelBottom + 1 && y + lineH / 2 <= ClientSize.Height;
            if (room || hot)
            {
                var r = new Rectangle(0, y - lineH / 2, ClientSize.Width, lineH);
                TextRenderer.DrawText(g, label, Font, r, hot ? LiteBoxTheme.Fg : LiteBoxTheme.SubFg,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (room) lastLabelBottom = y + lineH / 2;
            }
            else
            {
                int d = hot ? 5 : 3;
                g.FillEllipse(hot ? tickHot : tick, cx - d / 2f, y - d / 2f, d, d);
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
        JumpAt(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = GroupAt(e.Y);
        if (i != _hover)
        {
            _hover = i;
            string t = i >= 0 && i < _groups.Count ? _groups[i].Label : "";
            if (t != _tipText) { _tipText = t; if (t.Length > 0) _tip.SetToolTip(this, t); }
            Invalidate();
        }
        if (_dragging) JumpAt(e.Y);
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
