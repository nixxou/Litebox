// Override-aware value controls. LaunchBox hides the fact that a per-emulator / per-game value is
// really an OVERRIDE of an inherited default — it just shows the number. LiteBox makes it explicit:
//   • while the value equals the inherited default it is shown MUTED with a "(hérité)" tag,
//   • the moment it differs it turns AMBER (this entity carries its own value) and a small ↺ button
//     appears that snaps it back to the default (⇒ the override is dropped and the value inherits
//     live again).
//
// "Override" is derived purely from value ≠ baseline — no separate stored flag — so dragging a slider
// back onto the default auto-clears it. The getter returns null when inheriting: callers persist that
// as an empty field, and the field-store removes the XML element (absent ⇒ inherit at launch).

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class OverrideUi
{
    /// <summary>Colour of an active override (differs from the inherited default).</summary>
    public static readonly Color OverrideColor = Color.FromArgb(240, 190, 90); // amber

    /// <summary>A trackbar that inherits <paramref name="baseline"/> until overridden. Adds a caption
    /// (amber when overridden, muted otherwise) and a ↺ reset-to-default button. Lays controls into
    /// <paramref name="parent"/> starting at <paramref name="y"/> and returns the next free Y plus a
    /// getter that yields null when the value should inherit (value == baseline).</summary>
    public static (int nextY, Func<int?> get) Slider(
        Panel parent, float s, int x, int y, int width,
        string caption, int baseline, int? current, int max, int step,
        Func<int, string> fmt, Color fg, Color subFg, Color bg, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * s);
        baseline = Math.Max(0, Math.Min(max, baseline));
        int effective = Math.Max(0, Math.Min(max, current ?? baseline));

        var cap = new Label { AutoSize = true, Location = new Point(x, y), BackColor = bg };
        var reset = new Button
        {
            AutoSize = true, Height = S(22), Location = new Point(x, y - S(2)),
            FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = OverrideColor,
            Padding = new Padding(S(5), 0, S(5), 0), TabStop = false, Font = new Font("Segoe UI", 8f),
        };
        reset.FlatAppearance.BorderColor = OverrideColor;
        reset.FlatAppearance.BorderSize = 1;
        var tip = new ToolTip();
        tip.SetToolTip(reset, "Retirer l'override — revenir au défaut hérité");

        var bar = new TrackBar
        {
            Location = new Point(x, y + S(22)), Width = width, Minimum = 0, Maximum = max,
            SmallChange = step, LargeChange = step, TickFrequency = Math.Max(1, max / 20),
            Value = effective, Enabled = !readOnly, BackColor = bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        parent.Controls.Add(cap);
        parent.Controls.Add(bar);
        parent.Controls.Add(reset);

        reset.Text = $"↻ défaut {fmt(baseline)}";   // the value it snaps back to
        void Refresh()
        {
            bool ov = bar.Value != baseline;
            cap.ForeColor = ov ? OverrideColor : subFg;
            cap.Text = ov
                ? $"{caption}{fmt(bar.Value)}"
                : $"{caption}{fmt(bar.Value)}   (hérité)";
            reset.Visible = ov && !readOnly;
            reset.Location = new Point(cap.Right + S(12), y - S(2));   // sit right after the caption text
        }
        bar.ValueChanged += (_, _) => Refresh();
        reset.Click += (_, _) => bar.Value = baseline;   // snapping to baseline auto-clears the override
        Refresh();

        return (y + S(22) + S(40), () => bar.Value == baseline ? (int?)null : bar.Value);
    }
}
