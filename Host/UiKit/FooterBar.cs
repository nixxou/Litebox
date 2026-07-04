// Bottom action bar (OK / Apply / Cancel, or similar). The bar sizes itself from its own
// buttons' real PreferredSize instead of a hand-picked pixel Height - the previous
// OptionsWindow footer used a fixed "Height = 46"-style guess that clipped the buttons at
// high DPI once the button padding/font grew past what 46px could hold. A FlowLayoutPanel
// with AutoSize on the non-docked axis can't go out of sync with its own content that way.

#nullable enable

namespace LbApiHost.Host.UiKit;

internal sealed class FooterBar : FlowLayoutPanel
{
    public FooterBar()
    {
        Dock = DockStyle.Bottom;
        BackColor = LiteBoxTheme.PanelC;
        FlowDirection = FlowDirection.RightToLeft;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        int s = DeviceDpi;
        Padding = new Padding((int)Math.Round(14 * s / 96f), (int)Math.Round(10 * s / 96f), (int)Math.Round(14 * s / 96f), (int)Math.Round(10 * s / 96f));
    }

    /// <summary>Adds a button. Call in RIGHT-TO-LEFT visual order (rightmost button first)
    /// since this bar flows right-to-left - e.g. AddButton("Cancel", ...) then
    /// AddButton("Apply", ...) then AddButton("OK", ...) renders as [OK][Apply][Cancel].</summary>
    public Button AddButton(string text, Color back, EventHandler onClick)
    {
        float s = DeviceDpi / 96f;
        var btn = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding((int)Math.Round(16 * s), (int)Math.Round(4 * s), (int)Math.Round(16 * s), (int)Math.Round(4 * s)),
            Margin = new Padding((int)Math.Round(8 * s), 0, 0, 0),
            FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += onClick;
        Controls.Add(btn);
        return btn;
    }
}
