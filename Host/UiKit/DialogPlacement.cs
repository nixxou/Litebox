// Where a MODELESS window opens. FormStartPosition.CenterParent is a ShowDialog-only courtesy:
// a form put up with Show(owner) ignores it and lets Windows pick the spot — the primary monitor,
// which is the wrong screen whenever LiteBox lives on a second one. Every modeless window that
// means "centre me on the app" goes through here instead. (FullscreenPlacement is the same idea
// for borderless viewers, which want a whole monitor rather than a spot on one.)

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class DialogPlacement
{
    /// <summary>Centre <paramref name="f"/> over <paramref name="owner"/>, clamped to that
    /// monitor's work area so a half-offscreen (or minimised) owner can't push the window out of
    /// sight. Call BEFORE Show — it switches the form to manual positioning.</summary>
    public static void CenterOnOwner(Form f, Form? owner)
    {
        if (f == null) return;
        f.StartPosition = FormStartPosition.Manual;
        try
        {
            if (owner == null || owner.IsDisposed) { f.StartPosition = FormStartPosition.CenterScreen; return; }
            var b = owner.WindowState == FormWindowState.Minimized ? owner.RestoreBounds : owner.Bounds;
            if (b.Width <= 0 || b.Height <= 0) { f.StartPosition = FormStartPosition.CenterScreen; return; }
            var wa = Screen.FromRectangle(b).WorkingArea;
            f.Location = new Point(
                Math.Max(wa.Left, Math.Min(b.Left + (b.Width - f.Width) / 2, wa.Right - f.Width)),
                Math.Max(wa.Top, Math.Min(b.Top + (b.Height - f.Height) / 2, wa.Bottom - f.Height)));
        }
        catch { f.StartPosition = FormStartPosition.CenterScreen; }
    }
}
