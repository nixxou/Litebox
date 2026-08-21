// A read-only multiline TextBox wearing a SlimScrollBar instead of the native 17px one.
//
// A TextBox is a native EDIT control, and ScrollBars.Vertical on one shows its bar PERMANENTLY — even over
// three lines of text that fit. Setting ScrollBars.None takes it away, but then nothing reports the scroll
// state either, so the overlay has to read it out of the control: EDIT counts in DISPLAY lines (wrapped
// ones included), which is what EM_GETLINECOUNT, EM_GETFIRSTVISIBLELINE and EM_LINESCROLL all speak.
//
// The bar cannot be a child of the TextBox — an EDIT does not host child windows sensibly — so this is a
// panel holding the two side by side, and the caller keeps talking to .Box exactly as before.

#nullable enable

using System.Runtime.InteropServices;

namespace LbApiHost.Host.UiKit;

internal sealed class SlimScrollTextBox : Panel
{
    private const int EM_GETLINECOUNT = 0x00BA, EM_LINESCROLL = 0x00B6, EM_GETFIRSTVISIBLELINE = 0x00CE;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    private readonly SlimScrollBar? _bar;

    /// <summary>The text box itself: everything the rest of the app does with it is unchanged.</summary>
    public TextBox Box { get; }

    private readonly bool _slim;

    public SlimScrollTextBox(bool slim = true)
    {
        _slim = slim;
        var box = new InnerBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            // Native: the bar shows permanently, which is the behaviour this class exists to replace.
            ScrollBars = slim ? ScrollBars.None : ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        };
        Box = box;
        Controls.Add(box);
        if (!slim) return;

        _bar = new SlimScrollBar(vertical: true);
        // This range is counted in display lines, so a notch is worth the system's line count directly.
        _bar.WheelStep = SystemInformation.MouseWheelScrollLines <= 0 ? 3 : SystemInformation.MouseWheelScrollLines;
        Controls.Add(_bar);
        _bar.BringToFront();
        _bar.ScrollTo += line =>
        {
            if (!box.IsHandleCreated) return;
            int first = (int)SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(line - first));
            Sync();
        };
        box.StateChanged += Sync;
        Resize += (_, _) => Sync();
    }

    /// <summary>Read the scroll state out of the EDIT control and hand it to the bar.</summary>
    public void Sync()
    {
        if (!_slim || _bar == null || Box == null || !Box.IsHandleCreated || Box.IsDisposed) return;
        try
        {
            int lineH = Math.Max(1, TextRenderer.MeasureText("Hg", Box.Font).Height);
            int visible = Math.Max(1, Box.ClientSize.Height / lineH);
            int total = (int)SendMessage(Box.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
            int first = (int)SendMessage(Box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            _bar.Edge = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            _bar.SetRange(visible, total, first);
            if (_bar.Visible) _bar.Reposition();
        }
        catch { }
    }

    // The EDIT tells nobody when it scrolls itself, so the wheel and the caret keys are picked up here.
    private sealed class InnerBox : TextBox
    {
        private const int WM_VSCROLL = 0x0115, WM_KEYDOWN = 0x0100, WM_SETTEXT = 0x000C, WM_SIZE = 0x0005;
        public event Action? StateChanged;

        /// <summary>False = leave the wheel to the control, which still has its native bar.</summary>
        public bool OwnWheel = true;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!OwnWheel) { base.OnMouseWheel(e); return; }
            // Driven by hand rather than left to the control: without WS_VSCROLL an EDIT is inconsistent
            // about the wheel, and doing it here means the bar always knows where it ended up.
            if (IsHandleCreated)
            {
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0) lines = 3;
                SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-(e.Delta / 120) * lines));
            }
            StateChanged?.Invoke();
            // Not calling base: the scroll above is the whole of it, and the default handler would
            // otherwise hand the wheel to the scrolling pane behind and move both at once.
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is WM_VSCROLL or WM_KEYDOWN or WM_SETTEXT or WM_SIZE) StateChanged?.Invoke();
        }
    }
}
