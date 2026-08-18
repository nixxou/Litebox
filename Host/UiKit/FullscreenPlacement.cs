// Where a borderless fullscreen viewer opens. Maximised + Manual lands on whatever monitor Windows
// feels like (in practice the primary one), which is wrong when LiteBox — and the LaunchBox it
// accompanies — live on a second screen. Every fullscreen viewer goes through here instead.

#nullable enable

using System.Drawing;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class FullscreenPlacement
{
    /// <summary>Cover the monitor the app is actually on: the owner window's, else the active form's,
    /// else the first open form's. Bounds are set explicitly (rather than WindowState.Maximized) so the
    /// choice of monitor is ours and not the window manager's.</summary>
    public static void OnAppScreen(Form f)
    {
        Control? anchor = f.Owner;
        if (anchor == null || anchor.IsDisposed) anchor = Form.ActiveForm;
        if (anchor == null || anchor.IsDisposed)
            foreach (Form open in Application.OpenForms)
                if (open != f && !open.IsDisposed && open.Visible) { anchor = open; break; }
        var screen = anchor != null && anchor.IsHandleCreated ? Screen.FromControl(anchor) : Screen.PrimaryScreen;
        f.StartPosition = FormStartPosition.Manual;
        f.WindowState = FormWindowState.Normal;
        f.Bounds = screen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
    }
}
