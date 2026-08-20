// Base class for LiteBox's themed dialogs/windows. Centralizes the one DPI concern that
// still needs manual handling: pure chrome pixel dimensions with no text to derive a size
// from (button padding, panel margins, a nav column's width). Text itself never needs this
// - a Font's point-size already renders at the correct physical size for the current DPI
// via GDI+. AutoScaleMode is explicitly OFF here: this class scales chrome itself,
// deliberately, instead of leaning on WinForms' implicit whole-tree Font-based rescale
// (which only ever fires once, at first Show/Load, so it silently misses any control added
// to the tree afterward - e.g. a section panel built and attached long after the window was
// already shown). That mismatch was the root cause of the very first DPI bug found in this
// codebase (OptionsWindow's overlapping controls at 225% scaling); every LiteBox window
// should derive from this class instead of re-deriving DPI handling per-file.

#nullable enable

namespace LbApiHost.Host.UiKit;

internal class LiteBoxForm : Form
{
    /// <summary>Current-monitor scale factor (2.25 at 225%). Use only for chrome pixel
    /// dimensions (widths, margins, paddings) that have no text to derive a size from -
    /// never for anything text-driven, since Font/PreferredSize are already DPI-correct
    /// and scaling them again would double-count the DPI. Named DpiScale, not Scale -
    /// Control already declares a Scale(float) method, and shadowing it silently is a
    /// footgun for anyone who later calls .Scale(...) expecting the base behavior.</summary>
    protected readonly float DpiScale;

    protected LiteBoxForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        var _ = Handle;                 // force handle creation so DeviceDpi reflects the real monitor
        DpiScale = DeviceDpi / 96f;

        BackColor = LiteBoxTheme.Bg;
        ForeColor = LiteBoxTheme.Fg;
        Font = new Font("Segoe UI", 9.5f);
        ShowIcon = false; ShowInTaskbar = false;
        KeyPreview = true;
        // These windows keep MaximizeBox off for a clean title bar, which also kills the
        // double-click-to-maximize gesture. Give the gesture back (resizable windows only).
        TitleBarMaximize.Enable(this);
        // Readable checkboxes on the dark canvas, for whatever this window builds — now or later.
        ThemedCheckBox.StyleAll(this);
    }

    /// <summary>Scales a pure chrome pixel dimension for the current DPI. See <see cref="DpiScale"/>.</summary>
    protected int S(int px) => (int)Math.Round(px * DpiScale);

    /// <summary>A footer action button, optionally carrying one of the <see cref="MenuIcons"/> glyphs
    /// to its left (LaunchBox labels these buttons with the same icons as the matching menu entries).
    /// Sizes itself to its content so an icon — or a longer label — never crops the text; a missing
    /// icon simply yields a text-only button.</summary>
    protected Button ActionButton(string text, string? icon = null)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(S(96), S(28)),
            Padding = new Padding(S(10), S(4), S(12), S(4)),
            Margin = new Padding(0, 0, S(8), 0),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.CancelBtn, ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleRight,
        };
        btn.FlatAppearance.BorderSize = 0;
        if (icon != null) btn.Image = MenuIcons.Get(icon, S(16));
        return btn;
    }
}
