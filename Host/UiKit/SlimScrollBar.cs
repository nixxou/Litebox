// A thin scrollbar, drawn by us, to replace the 17px slab Windows insists on.
//
// The width of a native scrollbar comes from SM_CXVSCROLL, a SYSTEM metric: it cannot be set per window,
// let alone per control. The only way to a thinner one is to take the native bar away and paint our own —
// which is what this does, reusing the trick PosterListView already uses to hide its own bar (strip the
// WS_*SCROLL style and keep stripping it from WM_NCCALCSIZE, because the control puts it straight back).
//
// It OVERLAYS the content rather than taking a column of layout. That is the whole point: a bar that takes
// layout space costs its full width forever, while an overlay costs 4px of covered pixels at rest and can
// widen to 12px under the pointer — wide enough to grab — without reflowing a single line of what is
// underneath. The hosts it is attached to leave a margin on that edge, so the covered pixels are padding.

#nullable enable

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.UiKit;

internal sealed class SlimScrollBar : Control
{
    public const int RestThickness = 3;
    public const int HoverThickness = 12;

    private readonly bool _vertical;
    private int _viewport, _content, _position;
    private bool _hover, _dragging;
    private int _dragOffset;

    /// <summary>Asked for a new scroll position (in the same units as the range fed to <see cref="SetRange"/>).</summary>
    public event Action<int>? ScrollTo;

    public SlimScrollBar(bool vertical)
    {
        _vertical = vertical;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Visible = false;
        EnsureWheelRouter();
    }

    /// <summary>The edge of the host this bar rides on, in the host's own coordinates. Kept as a field so
    /// the bar can widen toward the content on hover without the caller re-computing anything.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle Edge { get; set; }

    public void SetRange(int viewport, int content, int position)
    {
        // Nothing to scroll: a bar over content that fits is exactly what we set out to remove.
        bool needed = content > viewport && viewport > 0;
        if (!needed)
        {
            if (Visible) Visible = false;
            return;
        }
        int max = content - viewport;
        position = Math.Clamp(position, 0, max);
        bool changed = _viewport != viewport || _content != content || _position != position;
        _viewport = viewport; _content = content; _position = position;
        if (!Visible) Visible = true;
        if (changed) Invalidate();
    }

    // Darker than any panel it can sit over (#202128 for the side panes, #2D2D30 for the notes box), so
    // the expanded bar reads as a recess rather than a highlight.
    private static readonly Color TrackColour = Color.FromArgb(24, 25, 30);
    private static readonly Color ThumbRest = Color.FromArgb(104, 104, 112);
    private static readonly Color ThumbHover = Color.FromArgb(146, 146, 154);
    private static readonly Color ThumbDrag = Color.FromArgb(186, 186, 194);

    /// <summary>How far one wheel notch moves, in whatever unit the host feeds the range in — pixels for a
    /// scrolling pane, display lines for a text box. There is no unit-agnostic default worth having, so
    /// each host sets its own.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int WheelStep { get; set; } = 3;

    private int Thickness => _hover || _dragging ? HoverThickness : RestThickness;

    /// <summary>Place the bar along its edge at the current thickness. Called on every geometry change and
    /// whenever the pointer arrives or leaves, which is what makes the widening free of layout.</summary>
    public void Reposition()
    {
        var e = Edge;
        int t = Thickness;
        Bounds = _vertical
            ? new Rectangle(e.Right - t, e.Y, t, e.Height)
            : new Rectangle(e.X, e.Bottom - t, e.Width, t);
    }

    private (int pos, int len) Thumb()
    {
        int track = _vertical ? Height : Width;
        int max = Math.Max(1, _content - _viewport);
        int len = Math.Max(24, (int)((long)track * _viewport / Math.Max(1, _content)));
        len = Math.Min(len, track);
        int pos = (int)((long)(track - len) * _position / max);
        return (pos, len);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_content <= _viewport) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (pos, len) = Thumb();

        // At rest the track is invisible: only the thumb hints at where you are. Under the pointer a track
        // appears so the whole width reads as something you can click into — and it is DARKER than the
        // panels it sits over, the way the native dark scrollbar gutter used to be. A light wash there
        // read as a highlight rather than as a groove.
        if (_hover || _dragging)
            using (var tb = new SolidBrush(TrackColour))
                g.FillRectangle(tb, ClientRectangle);

        // The 1px inset is only affordable once the bar is wide enough to spare it. At rest it left a thumb
        // ONE pixel across, which the rounding then antialiased into a washed-out smear — measured at
        // #494952 where the thumb colour is #68687 0. The horizontal bar, being a thin line along the
        // bottom edge, was invisible outright.
        int inset = Thickness >= 6 ? 1 : 0;
        var r = _vertical
            ? new Rectangle(inset, pos, Math.Max(1, Width - inset * 2), len)
            : new Rectangle(pos, inset, len, Math.Max(1, Height - inset * 2));
        int radius = Math.Max(1, (_vertical ? r.Width : r.Height) / 2);
        var colour = _dragging ? ThumbDrag : _hover ? ThumbHover : ThumbRest;
        using var brush = new SolidBrush(colour);
        using var path = Rounded(r, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true; Reposition(); Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_dragging) { _hover = false; Reposition(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _content > _viewport)
        {
            var (pos, len) = Thumb();
            int at = _vertical ? e.Y : e.X;
            if (at >= pos && at < pos + len) { _dragging = true; _dragOffset = at - pos; }
            else
            {
                // Clicking the track jumps a page, the way a native bar does.
                int page = Math.Max(1, _viewport - 24);
                Request(_position + (at < pos ? -page : page));
            }
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Wheel(e.Delta);
        if (e is HandledMouseEventArgs h) h.Handled = true;
        // No base call: the notch is spent here, and the pane behind must not move on the same gesture.
    }

    private void Wheel(int delta)
    {
        if (_content <= _viewport) return;
        int notches = delta / 120;
        if (notches == 0) notches = Math.Sign(delta);
        Request(_position - notches * Math.Max(1, WheelStep));
    }

    // ── Getting the wheel here at all ────────────────────────────────────────
    // Windows sends WM_MOUSEWHEEL to the control with the KEYBOARD FOCUS, not to the one under the pointer.
    // A scrollbar never takes focus, so overriding OnMouseWheel is not enough on its own: the notch went to
    // whatever had focus and the bar under the pointer never heard about it — which is why it could be
    // dragged but not wheeled, dragging going through the control's own mouse messages.
    //
    // A message filter is the standard cure: it sees the notch before anyone handles it, asks the OS which
    // window is actually under the pointer, and hands it over when that window is one of ours. Installed
    // once, for every bar in the process.
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private const int WM_MOUSEWHEEL = 0x020A;
    private static bool _routerInstalled;

    private static void EnsureWheelRouter()
    {
        if (_routerInstalled) return;
        _routerInstalled = true;
        try { Application.AddMessageFilter(new WheelRouter()); } catch { }
    }

    private sealed class WheelRouter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            long lp = m.LParam.ToInt64();
            var pt = new POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };   // SCREEN coords
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            if (FromHandle(hwnd) is not SlimScrollBar bar || !bar.Visible) return false;
            bar.Wheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
            return true;   // spent here: nothing behind should move on the same notch
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            int track = _vertical ? Height : Width;
            var (_, len) = Thumb();
            int span = Math.Max(1, track - len);
            int at = Math.Clamp((_vertical ? e.Y : e.X) - _dragOffset, 0, span);
            Request((int)((long)at * (_content - _viewport) / span));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            if (!ClientRectangle.Contains(e.Location)) _hover = false;
            Reposition(); Invalidate();
        }
        base.OnMouseUp(e);
    }

    private void Request(int position)
    {
        int max = Math.Max(0, _content - _viewport);
        position = Math.Clamp(position, 0, max);
        if (position == _position) return;
        _position = position;
        Invalidate();
        ScrollTo?.Invoke(position);
    }

    // ── Taking the native bars away ──────────────────────────────────────────
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr h, int bar, bool show);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const int GWL_STYLE = -16, WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000;
    private const int SB_BOTH = 3;
    private const uint SWP_FRAME = 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0010;   // FRAMECHANGED|NOSIZE|NOMOVE|NOZORDER|NOACTIVATE

    /// <summary>Strip both native bars from a control and force the frame to be recomputed without them.
    /// Has to be re-run after anything that rebuilds the frame — a handle recreation, SetWindowTheme — which
    /// is why the hosts below call it again from their own WM_NCCALCSIZE.</summary>
    public static void HideNativeBars(Control c)
    {
        if (c == null || !c.IsHandleCreated) return;
        try
        {
            ShowScrollBar(c.Handle, SB_BOTH, false);
            int style = GetWindowLong(c.Handle, GWL_STYLE);
            int stripped = style & ~WS_VSCROLL & ~WS_HSCROLL;
            if (stripped != style) SetWindowLong(c.Handle, GWL_STYLE, stripped);
            SetWindowPos(c.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAME);
        }
        catch { }
    }
}
