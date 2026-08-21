// The source tree wearing slim overlay bars instead of its two native ones.
//
// A TreeView is harder to fit than the panel and the text box were, for one reason: taking WS_*SCROLL away
// is what buys the space back, but a control stripped of the style no longer maintains reliable scroll
// info, so the range cannot simply be read out of GetScrollInfo the way it would be otherwise.
//
// Vertically that is worked around by not asking the control at all: a tree scrolls by ITEMS, and the item
// it starts at is TopNode, so counting visible nodes gives the range and the position outright, and setting
// TopNode moves it. That path is immune to whatever the native styles are doing.
//
// Horizontally there is no managed equivalent, so that one does go through the native scroll info and
// WM_HSCROLL — and hides itself if the control has stopped reporting a range.

#nullable enable

using System.Runtime.InteropServices;

namespace LbApiHost.Host.UiKit;

internal sealed class SlimScrollTreeHost : Panel
{
    private readonly SlimScrollBar _v, _h;

    /// <summary>The tree itself: every existing use of it is unchanged.</summary>
    public TreeView Tree { get; }

    private readonly bool _slim;

    public SlimScrollTreeHost(TreeView tree, bool slim = true)
    {
        _slim = slim;
        Tree = tree;
        tree.Dock = DockStyle.Fill;
        Controls.Add(tree);
        if (!slim) { _v = _h = null!; return; }

        _v = new SlimScrollBar(vertical: true) { WheelStep = WheelLines };
        _h = new SlimScrollBar(vertical: false) { WheelStep = 48 };
        Controls.Add(_v);
        Controls.Add(_h);
        _v.BringToFront();
        _h.BringToFront();

        _v.ScrollTo += ScrollToRow;
        _h.ScrollTo += x => { NativeHScroll(x); Sync(); };

        if (tree is Inner inner) { inner.StateChanged += Sync; inner.WheelRows += ScrollByRows; }
        tree.AfterExpand += (_, _) => Sync();
        tree.AfterCollapse += (_, _) => Sync();
        tree.AfterSelect += (_, _) => Sync();
        Resize += (_, _) => Sync();
    }

    private static int WheelLines => SystemInformation.MouseWheelScrollLines <= 0 ? 3 : SystemInformation.MouseWheelScrollLines;

    /// <summary>A tree with its native bars stripped, which keeps stripping them: the control puts the
    /// styles back whenever its content changes.</summary>
    public static TreeView NewTree(bool slim = true) => slim ? new Inner() : new TreeView();

    // ── vertical: counted in visible nodes ───────────────────────────────────
    private static int VisibleCount(TreeView t)
    {
        int n = 0;
        for (var node = t.Nodes.Count > 0 ? t.Nodes[0] : null; node != null; node = node.NextVisibleNode) n++;
        return n;
    }

    private static int IndexOfTop(TreeView t)
    {
        var top = t.TopNode;
        if (top == null) return 0;
        int i = 0;
        for (var node = t.Nodes.Count > 0 ? t.Nodes[0] : null; node != null; node = node.NextVisibleNode, i++)
            if (ReferenceEquals(node, top)) return i;
        return 0;
    }

    /// <summary>Move the view by a number of rows, clamped. This is what the wheel over the TREE ITSELF
    /// goes through: a tree stops scrolling on WM_MOUSEWHEEL once WS_VSCROLL is stripped — the same thing
    /// PosterListView records for the icon view — so the notch has to be turned into rows by hand. The
    /// pane on the right needs none of this: its scrolling is WinForms', not the control's.</summary>
    private void ScrollByRows(int rows)
    {
        try
        {
            int visibleRows = Math.Max(1, Tree.ClientSize.Height / Math.Max(1, Tree.ItemHeight));
            int max = Math.Max(0, VisibleCount(Tree) - visibleRows);
            ScrollToRow(Math.Clamp(IndexOfTop(Tree) + rows, 0, max));
        }
        catch { }
    }

    private void ScrollToRow(int row)
    {
        try
        {
            int i = 0;
            for (var node = Tree.Nodes.Count > 0 ? Tree.Nodes[0] : null; node != null; node = node.NextVisibleNode, i++)
                if (i == row) { Tree.TopNode = node; break; }
            Sync();
        }
        catch { }
    }

    // ── horizontal: the one place the native scroll info is still needed ──────
    [StructLayout(LayoutKind.Sequential)]
    private struct SCROLLINFO { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    private const int SIF_ALL = 0x17, SB_HORZ = 0, WM_HSCROLL = 0x0114, SB_THUMBPOSITION = 4;

    private bool NativeH(out int viewport, out int content, out int position)
    {
        viewport = content = position = 0;
        if (!Tree.IsHandleCreated) return false;
        var si = new SCROLLINFO { cbSize = Marshal.SizeOf<SCROLLINFO>(), fMask = SIF_ALL };
        if (!GetScrollInfo(Tree.Handle, SB_HORZ, ref si)) return false;
        viewport = si.nPage;
        content = si.nMax - si.nMin + 1;
        position = si.nPos;
        return viewport > 0 && content > viewport;
    }

    private void NativeHScroll(int x)
    {
        if (!Tree.IsHandleCreated) return;
        SendMessage(Tree.Handle, WM_HSCROLL, (IntPtr)((x << 16) | SB_THUMBPOSITION), IntPtr.Zero);
    }

    public void Sync()
    {
        if (!_slim || Tree == null || Tree.IsDisposed || !Tree.IsHandleCreated) return;
        try
        {
            var edge = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);

            int rowH = Math.Max(1, Tree.ItemHeight);
            int rows = Math.Max(1, Tree.ClientSize.Height / rowH);
            int vis = VisibleCount(Tree);
            _v.Edge = edge;
            _v.SetRange(rows, vis, IndexOfTop(Tree));
            if (_v.Visible) _v.Reposition();

            if (NativeH(out int hv, out int hc, out int hp))
            {
                _h.Edge = edge;
                _h.SetRange(hv, hc, hp);
                if (_h.Visible) _h.Reposition();
            }
            else _h.SetRange(0, 0, 0);   // no range reported: nothing to show
        }
        catch { }
    }

    private sealed class Inner : TreeView
    {
        private const int WM_NCCALCSIZE = 0x0083, WM_VSCROLL = 0x0115, WM_HSCROLL_I = 0x0114;
        private const int WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100, WM_SIZE = 0x0005;
        private const int GWL_STYLE = -16, WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

        public event Action? StateChanged;
        public event Action<int>? WheelRows;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            WheelRows?.Invoke(-notches * lines);
            if (e is HandledMouseEventArgs h) h.Handled = true;
            // No base call: the control would do nothing with it anyway, and the panes behind must not move.
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SlimScrollBar.HideNativeBars(this);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && IsHandleCreated)
            {
                int style = GetWindowLong(Handle, GWL_STYLE);
                int stripped = style & ~WS_VSCROLL & ~WS_HSCROLL;
                if (stripped != style) SetWindowLong(Handle, GWL_STYLE, stripped);
            }
            base.WndProc(ref m);
            if (m.Msg is WM_VSCROLL or WM_HSCROLL_I or WM_MOUSEWHEEL or WM_KEYDOWN or WM_SIZE) StateChanged?.Invoke();
        }
    }
}
