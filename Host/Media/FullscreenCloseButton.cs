#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LbApiHost.Host.Media;

/// <summary>Small close button shared by every fullscreen media viewer.</summary>
internal sealed class FullscreenCloseButton : Control
{
    private bool _hover;

    public FullscreenCloseButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        // A close chip never wants the focus. TabStop alone is NOT enough: the arrow-key dialog navigation
        // (Form.ProcessArrowKey → SelectNextControl) ignores TabStop and selects any SELECTABLE control —
        // which is exactly what stole ←/→ from the image viewer when this button was introduced.
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(32, 32);
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleName = "Close fullscreen";
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Color.Transparent;
        ApplyCircularRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyCircularRegion();
    }

    // The chip sits over a video (libvlc's own child HWND) or a 3D scene (an ElementHost), and WinForms'
    // "transparent" BackColor cannot show either of those through: it repaints the PARENT FORM's background
    // instead, which left a black square in the four corners outside the circle. Clipping the control to
    // the circle removes those pixels from the control altogether — the content behind shows through for
    // real, and corner clicks fall through to the video instead of hitting a dead zone.
    private void ApplyCircularRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Width, Height);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Fill PAST the clip (inflated by 1 px): the region already cuts the circle out, so an inset fill
        // would leave a ring of unpainted — therefore black — pixels inside it, and an anti-aliased fill
        // landing exactly on the clip would darken the edge. The border then stays fully inside the clip.
        using (var bg = new SolidBrush(_hover
                   ? Color.FromArgb(225, 58, 58, 64)
                   : Color.FromArgb(205, 28, 28, 32)))
            g.FillEllipse(bg, -1f, -1f, Width + 2f, Height + 2f);
        using (var border = new Pen(Color.FromArgb(_hover ? 190 : 105, Color.White), 1f))
            g.DrawEllipse(border, 0.75f, 0.75f, Width - 1.5f, Height - 1.5f);

        using var x = new Pen(_hover ? Color.White : Color.FromArgb(225, 225, 228), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        const float pad = 10.5f;
        g.DrawLine(x, pad, pad, Width - pad, Height - pad);
        g.DrawLine(x, Width - pad, pad, pad, Height - pad);
    }
}
