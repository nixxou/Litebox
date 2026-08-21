// An AutoScroll panel that keeps its scrolling but gives up its native scrollbars, so a SlimScrollBar can
// be laid over it instead. Two things are needed and neither is optional:
//
//   * the WS_*SCROLL styles have to be stripped from WM_NCCALCSIZE as well as once at startup, because
//     ScrollableControl puts them back on every layout pass;
//   * the panel has to say when its scroll position moved, since the wheel changes it without any of the
//     usual scrollbar notifications ever being raised.

#nullable enable

using System.Runtime.InteropServices;

namespace LbApiHost.Host.UiKit;

internal sealed class SlimScrollPanel : Panel
{
    private const int WM_NCCALCSIZE = 0x0083, WM_VSCROLL = 0x0115, WM_HSCROLL = 0x0114, WM_MOUSEWHEEL = 0x020A;
    private const int GWL_STYLE = -16, WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    /// <summary>The scroll position, the range, or both have just moved.</summary>
    public event Action? ScrollStateChanged;

    /// <summary>False keeps the native scrollbars, and the panel behaves exactly like the plain Panel it
    /// replaced. Set before the handle exists; it decides whether the styles are stripped at all.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SlimBars { get; set; } = true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (SlimBars) SlimScrollBar.HideNativeBars(this);
    }

    protected override void WndProc(ref Message m)
    {
        // Before the frame is measured: without this the bar reappears the moment anything relays out.
        if (SlimBars && m.Msg == WM_NCCALCSIZE && IsHandleCreated)
        {
            int style = GetWindowLong(Handle, GWL_STYLE);
            int stripped = style & ~WS_VSCROLL & ~WS_HSCROLL;
            if (stripped != style) SetWindowLong(Handle, GWL_STYLE, stripped);
        }
        base.WndProc(ref m);
        if (m.Msg is WM_VSCROLL or WM_HSCROLL or WM_MOUSEWHEEL) ScrollStateChanged?.Invoke();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        ScrollStateChanged?.Invoke();
    }
}
