// A plain SplitContainer's splitter is an invisible-until-you-stumble-on-it gray bar with no
// feedback at all - you only find it by accidentally dragging it. This subclass highlights the
// splitter in the app's accent color on hover (and while actively dragging), the same
// discoverability cue modern split-pane UIs (VS Code, Windows Terminal, VS itself) give you.

#nullable enable

namespace LbApiHost.Host.UiKit;

internal sealed class ThemedSplitContainer : SplitContainer
{
    private bool _hover;
    private bool _dragging;

    public ThemedSplitContainer()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        SplitterMoving += (_, _) => { _dragging = true; Invalidate(SplitterRectangle); };
        SplitterMoved += (_, _) => { _dragging = false; Invalidate(SplitterRectangle); };
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Only the SplitContainer's own (uncovered-by-child-panels) area ever gets mouse events
        // here - i.e. exactly the splitter gap - so no extra hit-testing is needed.
        if (!_hover) { _hover = true; Invalidate(SplitterRectangle); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(SplitterRectangle); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var b = new SolidBrush(_hover || _dragging ? LiteBoxTheme.Accent : LiteBoxTheme.PanelC);
        e.Graphics.FillRectangle(b, SplitterRectangle);
    }
}
