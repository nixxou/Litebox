// Options → Similar Games — STUB. "Similar Games" is a standalone feature, NOT one of the LbModules; it gets
// its own top-level options SECTION (registered in MainWindow next to "Modules"). A parallel agent replaces
// this body with the real settings UI; for now it returns a placeholder + a no-op apply so the section renders.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class SimilarOptions
{
    public static (Control panel, Action apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg;
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(10)) };

        p.Controls.Add(new Label
        {
            Text = "Similar Games", AutoSize = true, ForeColor = Fg, BackColor = Bg,
            Location = new Point(S(4), S(6)), Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        });
        p.Controls.Add(new Label
        {
            Text = "Suggest games similar to the one in view, from the extended database.",
            AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Sub, BackColor = Bg,
            Location = new Point(S(4), S(32)), Font = new Font("Segoe UI", 8.5f),
        });
        p.Controls.Add(new Label
        {
            Text = "These settings will appear here as the feature is ported into LiteBox.",
            AutoSize = true, ForeColor = Sub, BackColor = Bg,
            Location = new Point(S(4), S(72)), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
        });

        void Apply() { if (readOnly) return; }
        return (p, Apply);
    }
}
