// Dual-handle range slider (low + high), themed for the filter dialog. Mirrors the web's year/rating
// sliders: values snap to Step; a handle at either extreme reads as "∞" via the Fmt formatter. Purely
// self-contained (owner-drawn); raises ValueChanged as either handle moves.

#nullable enable

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Search;

internal sealed class RangeSlider : Control
{
    private double _min, _max = 100, _step = 1, _low, _high = 100;
    private int _drag = -1;   // -1 none, 0 low, 1 high

    public event Action? ValueChanged;
    public Func<double, string> Fmt = v => v.ToString("0.#");

    public RangeSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 30;
    }

    public void Configure(double min, double max, double step, double low, double high, Func<double, string> fmt)
    {
        _min = min; _max = max; _step = step <= 0 ? 1 : step; Fmt = fmt;
        _low = Clamp(low); _high = Clamp(high);
        if (_low > _high) _low = _high;
        Invalidate();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Low { get => _low; set { _low = Math.Min(Clamp(value), _high); Invalidate(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double High { get => _high; set { _high = Math.Max(Clamp(value), _low); Invalidate(); } }

    private float Pad => Handle + 2;
    private const float Handle = 8f;
    private double Clamp(double v) => Math.Max(_min, Math.Min(_max, v));
    private double Snap(double v) => Clamp(Math.Round((v - _min) / _step) * _step + _min);

    private float XOf(double v)
    {
        float usable = Math.Max(1, Width - 2 * Pad);
        return Pad + (float)((v - _min) / (_max - _min)) * usable;
    }
    private double VOf(float x)
    {
        float usable = Math.Max(1, Width - 2 * Pad);
        return Snap(_min + (x - Pad) / usable * (_max - _min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Pick the nearer handle (ties → the one that leaves room to move).
        float dl = Math.Abs(e.X - XOf(_low)), dh = Math.Abs(e.X - XOf(_high));
        _drag = (dl < dh || (dl == dh && e.X < XOf(_low))) ? 0 : 1;
        DragTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0) DragTo(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = -1; }

    private void DragTo(int x)
    {
        double v = VOf(x);
        if (_drag == 0) _low = Math.Min(v, _high);
        else if (_drag == 1) _high = Math.Max(v, _low);
        else return;
        Invalidate();
        ValueChanged?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float cy = Height * 0.5f + 4;   // leave room for the value caption above
        float x0 = XOf(_low), x1 = XOf(_high);

        using (var track = new Pen(Color.FromArgb(70, 72, 84), 3f)) g.DrawLine(track, Pad, cy, Width - Pad, cy);
        using (var fill = new Pen(LiteBoxTheme.Accent, 3f)) g.DrawLine(fill, x0, cy, x1, cy);

        DrawHandle(g, x0, cy);
        DrawHandle(g, x1, cy);

        // Value caption (centred over each handle, ∞ at the extremes).
        using var cap = new Font("Segoe UI", 8.25f);
        var loStr = Fmt(_low); var hiStr = Fmt(_high);
        TextRenderer.DrawText(g, loStr, cap, new Rectangle((int)(x0 - 40), 0, 80, 14), LiteBoxTheme.SubFg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        if (Math.Abs(x1 - x0) > 30)
            TextRenderer.DrawText(g, hiStr, cap, new Rectangle((int)(x1 - 40), 0, 80, 14), LiteBoxTheme.SubFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
    }

    private static void DrawHandle(Graphics g, float x, float cy)
    {
        var r = new RectangleF(x - Handle, cy - Handle, Handle * 2, Handle * 2);
        using var fill = new SolidBrush(Color.FromArgb(230, 230, 235));
        using var ring = new Pen(LiteBoxTheme.Accent, 2f);
        g.FillEllipse(fill, r);
        g.DrawEllipse(ring, r);
    }
}
