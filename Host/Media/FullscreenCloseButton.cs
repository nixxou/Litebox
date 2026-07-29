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
        Size = new Size(32, 32);
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleName = "Close fullscreen";
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Color.Transparent;
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

        var circle = new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f);
        using (var bg = new SolidBrush(_hover
                   ? Color.FromArgb(225, 58, 58, 64)
                   : Color.FromArgb(205, 28, 28, 32)))
            g.FillEllipse(bg, circle);
        using (var border = new Pen(Color.FromArgb(_hover ? 190 : 105, Color.White), 1f))
            g.DrawEllipse(border, circle);

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
