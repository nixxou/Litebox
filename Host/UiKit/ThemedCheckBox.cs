// A checkbox you can actually read on LiteBox's dark canvas. Windows draws the classic glyph as a pale
// square with a dark tick; on this background the tick washes out and checked looks like unchecked —
// the very complaint that produced EditGameWindowImages' SourceChip for the source toggles. This paints
// the glyph square itself: accent-filled with a white tick when on, hollow outline when off, and a
// filled core for the indeterminate (three-state) middle.
//
// It repaints OVER the native glyph rather than taking the control over, so text, layout, auto-size,
// focus and hit-testing all stay exactly as WinForms arranged them.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class ThemedCheckBox
{
    // Styled once, whatever the caller does: a page can be swept, re-swept on a rebuild, and still
    // paint once per checkbox. (Weak keys: a disposed control is not kept alive by this table.)
    private static readonly ConditionalWeakTable<CheckBox, object> _done = new();

    /// <summary>Repaint this checkbox's glyph in the theme's colours. Chips (Appearance.Button) are
    /// left alone — they already say their state with a filled background.</summary>
    public static void Style(CheckBox cb)
    {
        if (cb == null || cb.Appearance == Appearance.Button) return;
        if (_done.TryGetValue(cb, out _)) return;
        _done.Add(cb, cb);
        cb.Paint += (s, e) => Draw((CheckBox)s!, e.Graphics);
        // The glyph carries the state, so a state change must repaint it.
        cb.CheckStateChanged += (s, _) => ((CheckBox)s!).Invalidate();
    }

    /// <summary>Style every checkbox under <paramref name="root"/>, now and as more arrive — the
    /// windows that need this build their pages lazily, long after they are first shown.</summary>
    public static void StyleAll(Control? root)
    {
        if (root == null) return;
        if (root is CheckBox cb) Style(cb);
        foreach (Control c in root.Controls) StyleAll(c);
        if (_hooked.TryGetValue(root, out _)) return;   // one ControlAdded hook per container, ever
        _hooked.Add(root, root);
        root.ControlAdded += (_, e) => StyleAll(e.Control);
    }

    private static readonly ConditionalWeakTable<Control, object> _hooked = new();

    private static void Draw(CheckBox cb, Graphics g)
    {
        int size = Math.Max(12, (int)Math.Round(13 * cb.DeviceDpi / 96.0));
        // Where WinForms puts the glyph for the alignments actually used here (left/right, middle).
        bool right = cb.CheckAlign is ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight;
        int x = right ? cb.Width - size - 1 : 0;
        int y = cb.CheckAlign is ContentAlignment.TopLeft or ContentAlignment.TopRight ? 1
              : cb.CheckAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomRight ? cb.Height - size - 1
              : (cb.Height - size) / 2;
        var box = new Rectangle(x, y, size, size);

        bool on = cb.CheckState != CheckState.Unchecked;
        var fill = !cb.Enabled ? LiteBoxTheme.Panel2
                 : on ? LiteBoxTheme.Accent : LiteBoxTheme.Panel2;
        var edge = !cb.Enabled ? LiteBoxTheme.SubFg
                 : on ? LiteBoxTheme.Accent : Color.FromArgb(150, LiteBoxTheme.SubFg);

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, box);
        using (var p = new Pen(edge)) g.DrawRectangle(p, box);

        if (cb.CheckState == CheckState.Checked)
        {
            using var tick = new Pen(Color.White, Math.Max(1.6f, size / 7f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(tick, new[]
            {
                new PointF(box.Left + size * 0.24f, box.Top + size * 0.52f),
                new PointF(box.Left + size * 0.44f, box.Top + size * 0.72f),
                new PointF(box.Left + size * 0.78f, box.Top + size * 0.28f),
            });
        }
        else if (cb.CheckState == CheckState.Indeterminate)
        {
            // "Mixed" across a multi-selection: a core, clearly neither empty nor a tick.
            using var b = new SolidBrush(Color.White);
            g.FillRectangle(b, Rectangle.Inflate(box, -(int)(size * 0.3), -(int)(size * 0.3)));
        }
        g.SmoothingMode = old;
    }
}
