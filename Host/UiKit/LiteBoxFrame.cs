// A titled panel with a blue "LiteBox-specific" border — the visual marker that the controls inside
// are LiteBox-only options that a real LaunchBox neither shows nor honours. Used to fold LiteBox's
// per-emulator / per-game options INTO the matching native tab (Startup Screen, Pause Screen, …)
// instead of a separate section, while still setting them clearly apart. Same look as the general
// Options window's "LiteBox-specific" group so the two read as one convention.
//
// Add child controls at coordinates relative to the returned panel; leave a little top margin so
// they clear the title label.

#nullable enable

using System.Drawing;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class LiteBoxFrame
{
    /// <summary>Blue accent of the LiteBox-specific frame (matches LbGlobalOptions).</summary>
    public static readonly Color Accent = Color.FromArgb(96, 156, 224);

    /// <summary>A bordered, titled panel at <paramref name="loc"/>/<paramref name="size"/>. The border
    /// is drawn just under the title so the caption sits on the top edge, LaunchBox-groupbox style.</summary>
    public static Panel Make(string title, Point loc, Size size, float s, Color bg)
    {
        int S(int px) => (int)System.Math.Round(px * s);
        var grp = new Panel { Location = loc, Size = size, BackColor = bg };
        grp.Paint += (_, e) =>
        {
            using var pen = new Pen(Accent, 1);
            e.Graphics.DrawRectangle(pen, 0, S(7), grp.Width - 1, grp.Height - S(8));
        };
        grp.Controls.Add(new Label
        {
            Text = " " + title + " ", AutoSize = true, ForeColor = Accent, BackColor = bg,
            Location = new Point(S(10), 0), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        });
        return grp;
    }
}
