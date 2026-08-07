// The left panel's LaunchBox-style header: a rounded search field, a funnel button next to it for the
// advanced criteria, and the "group by" selector under them.
//
// All three are painted by hand rather than themed WinForms controls, for one reason each:
//   RoundedField     - a TextBox can't have rounded corners or a themed border; it gets hosted, borderless,
//                      inside a frame that draws both. The frame's fill colour doubles as the quick-filter
//                      tint (amber = transient, blue = deliberate), so the whole field changes colour, not
//                      just the strip of pixels the text sits on.
//   FilterGlyphButton- needs a third state ("a filter is in force") that a Button doesn't have. Lit + inset,
//                      in the same blue as a deliberate quick filter but stronger, so the two read as one
//                      family: blue = you are looking at a subset.
//   ThemedDropDown   - a native ComboBox can't be made taller without owner-draw, can't be rounded at all,
//                      and drags a system-coloured drop list with it. This is a label + chevron that opens a
//                      ToolStripDropDown, which the host's DarkRenderer already knows how to paint.

#nullable enable

using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LbApiHost.Host.UiKit;

/// <summary>Rounded, flat-painted input frame hosting exactly one child (the search TextBox). The child is
/// laid out with the frame's padding and vertically centred — a single-line TextBox has a font-driven height,
/// so Dock=Fill would pin it to the top instead of centring it.</summary>
internal sealed class RoundedField : Panel
{
    private Color _fill = LiteBoxTheme.Panel2;
    private Color _border = FieldBorder;

    public static readonly Color FieldBorder = Color.FromArgb(62, 63, 74);

    public RoundedField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = LiteBoxTheme.PanelC;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 8;

    /// <summary>Repaints the frame in a new fill/border pair (the quick-filter tint). The hosted TextBox's
    /// own BackColor is the caller's job — it has to match or the text sits in a differently-coloured box.</summary>
    public void SetFieldColors(Color fill, Color border)
    {
        if (_fill == fill && _border == border) return;
        _fill = fill; _border = border;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = Math.Max(0, ClientSize.Width - Padding.Horizontal);
        foreach (Control c in Controls)
            c.SetBounds(Padding.Left, Math.Max(0, (ClientSize.Height - c.Height) / 2), w, c.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var b = new SolidBrush(_fill);
        g.FillPath(b, path);
        using var p = new Pen(_border);
        g.DrawPath(p, path);
    }

    /// <summary>Rounded-rectangle path, clamped so a radius larger than the box degrades to a capsule
    /// instead of throwing.</summary>
    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var gp = new GraphicsPath();
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }
}

/// <summary>The funnel next to the search field. <see cref="Active"/> means an advanced filter is in force:
/// the button then stays lit in a strong blue with an inset edge, so "a filter is hiding games from you" is
/// visible without opening the dialog.</summary>
internal sealed class FilterGlyphButton : Control
{
    private bool _hover, _down, _active;

    public FilterGlyphButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // Control's own click plumbing fires Click for ANY button, so a right-click meant to CLEAR the
        // filter would open the dialog on the way. We raise Click ourselves, left button only.
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        BackColor = LiteBoxTheme.PanelC;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasDown = _down;
        if (_down) { _down = false; Invalidate(); }
        base.OnMouseUp(e);
        if (wasDown && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill, border, glyph;
        if (_active)
        {
            // Same hue as the deliberate quick-filter tint (30,62,86), pushed brighter: the button has to
            // carry the signal on its own, without the width of the search box to help it.
            fill   = _down ? Color.FromArgb(28, 72, 104) : _hover ? Color.FromArgb(48, 108, 154) : Color.FromArgb(38, 92, 132);
            border = Color.FromArgb(92, 166, 220);
            glyph  = Color.FromArgb(222, 240, 255);
        }
        else
        {
            fill   = _down ? Color.FromArgb(38, 39, 48) : _hover ? Color.FromArgb(58, 59, 70) : Color.FromArgb(48, 49, 59);
            border = RoundedField.FieldBorder;
            glyph  = _hover ? LiteBoxTheme.Fg : LiteBoxTheme.SubFg;
        }

        using (var path = RoundedField.Rounded(r, 8))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            using var p = new Pen(border);
            g.DrawPath(p, path);
        }

        // "Enfoncé": a dark inner edge, drawn whenever the button is held OR latched on by an active filter.
        if (_active || _down)
        {
            using var inner = RoundedField.Rounded(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), 7);
            using var ip = new Pen(Color.FromArgb(80, 0, 0, 0));
            g.DrawPath(ip, inner);
        }

        DrawFunnel(g, glyph);
    }

    private void DrawFunnel(Graphics g, Color color)
    {
        float w = Math.Max(8f, Math.Min(Width, Height) * 0.46f);
        float h = w * 0.94f;
        float x = (Width - w) / 2f, y = (Height - h) / 2f;
        var pts = new[]
        {
            new PointF(x,             y),
            new PointF(x + w,         y),
            new PointF(x + w * 0.60f, y + h * 0.46f),
            new PointF(x + w * 0.60f, y + h),
            new PointF(x + w * 0.40f, y + h * 0.82f),
            new PointF(x + w * 0.40f, y + h * 0.46f),
        };
        using var b = new SolidBrush(color);
        g.FillPolygon(b, pts);
    }
}

/// <summary>A DropDownList combo the theme can actually control: painted label + chevron, with the list
/// itself a <see cref="ContextMenuStrip"/> so the host's dark ToolStrip renderer paints it. Exposes only the
/// slice of ComboBox this needs — Items, SelectedIndex, SelectedIndexChanged.</summary>
internal sealed class ThemedDropDown : Control
{
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false, ShowCheckMargin = true };
    private int _selected = -1;
    private bool _hover, _open;

    public ThemedDropDown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = LiteBoxTheme.PanelC;
        Cursor = Cursors.Hand;
        _menu.Closed += (_, _) => { _open = false; Invalidate(); };
    }

    public List<string> Items { get; } = new();

    /// <summary>The renderer the drop list paints with (the host's DarkRenderer).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ToolStripRenderer MenuRenderer { set { if (value != null) _menu.Renderer = value; } }

    public event EventHandler? SelectedIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int v = value < 0 || value >= Items.Count ? -1 : value;
            if (v == _selected) return;
            _selected = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { Focus(); ShowMenu(); }
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Down or Keys.Up or Keys.Space ? true : base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Down or Keys.Space or Keys.Enter) { ShowMenu(); e.Handled = true; }
    }

    private void ShowMenu()
    {
        if (Items.Count == 0) return;
        _menu.Items.Clear();
        for (int i = 0; i < Items.Count; i++)
        {
            int idx = i;
            var mi = new ToolStripMenuItem(Items[i]) { Checked = i == _selected };
            mi.Click += (_, _) => SelectedIndex = idx;
            _menu.Items.Add(mi);
        }
        _menu.MinimumSize = new Size(Width, 0);
        _open = true;
        Invalidate();
        _menu.Show(this, new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill = _open ? Color.FromArgb(58, 59, 70) : _hover ? Color.FromArgb(52, 53, 63) : LiteBoxTheme.Panel2;
        using (var path = RoundedField.Rounded(r, 8))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            using var p = new Pen(_open ? LiteBoxTheme.Accent : RoundedField.FieldBorder);
            g.DrawPath(p, path);
        }

        float s = LiteBoxTheme.DpiScale(this);
        int padL = (int)Math.Round(11 * s), chevW = (int)Math.Round(24 * s);
        string txt = _selected >= 0 && _selected < Items.Count ? Items[_selected] : "";
        TextRenderer.DrawText(g, txt, Font,
            new Rectangle(padL, 0, Math.Max(0, Width - padL - chevW), Height), LiteBoxTheme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        float cx = Width - chevW / 2f - 2f, cy = Height / 2f, half = Math.Max(3f, 4f * s);
        using var cp = new Pen(_hover || _open ? LiteBoxTheme.Fg : LiteBoxTheme.SubFg, Math.Max(1.4f, 1.5f * s))
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(cp, new[]
        {
            new PointF(cx - half, cy - half * 0.45f),
            new PointF(cx,        cy + half * 0.55f),
            new PointF(cx + half, cy - half * 0.45f),
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
