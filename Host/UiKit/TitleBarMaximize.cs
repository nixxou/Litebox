// Double-clicking a title bar should maximize the window. Windows ties that gesture to the
// MaximizeBox capability, so a dialog that merely wants to hide the maximize BUTTON loses the
// gesture with it — two separate wishes, one flag. This restores the gesture and leaves the
// chrome alone.

#nullable enable

using System;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class TitleBarMaximize
{
    /// <summary>Make a double-click on <paramref name="f"/>'s title bar toggle maximize/restore,
    /// whatever MaximizeBox says. No-op for windows that cannot be resized (a fixed dialog
    /// maximized is a stretched dialog, not a bigger one).</summary>
    public static void Enable(Form f)
    {
        if (f == null) return;
        _ = new Hook(f);   // lives as long as the form's handle; nothing else needs to hold it
    }

    private sealed class Hook : NativeWindow
    {
        private const int WM_NCLBUTTONDBLCLK = 0x00A3, HTCAPTION = 2;
        private readonly Form _f;

        public Hook(Form f)
        {
            _f = f;
            if (f.IsHandleCreated) AssignHandle(f.Handle);
            f.HandleCreated += (_, _) => { try { ReleaseHandle(); AssignHandle(f.Handle); } catch { } };
            f.HandleDestroyed += (_, _) => { try { ReleaseHandle(); } catch { } };
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCLBUTTONDBLCLK && (int)m.WParam == HTCAPTION && Resizable())
            {
                _f.WindowState = _f.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
                return;   // swallowed: with MaximizeBox off the default does nothing anyway
            }
            base.WndProc(ref m);
        }

        private bool Resizable()
            => _f.FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow;
    }
}
