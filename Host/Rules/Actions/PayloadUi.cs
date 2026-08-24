// The payload-with-mode body Prefix and Suffix share — one text field and the argument/cmdline
// choice. Composition, not inheritance: each action stays its own file and calls this for its UI.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal static class PayloadUi
{
    /// <summary>caption + text field + "add it as" combo. <paramref name="save"/> receives
    /// (payload, asArg) — the trim rule is applied here (trimmed as ARGUMENT, verbatim as CMDLINE,
    /// an edge space being often the point there — BigBoxProfile's exact behaviour).</summary>
    public static (Control Body, int Height, Action Save) Build(
        string caption, string argItem, string cmdItem,
        string value, bool asArg, float dpiS, Action<string, bool> save)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { Size = new Size(S(576), S(86)), BackColor = LiteBoxTheme.Bg };

        var cap = new Label
        {
            Text = caption, AutoSize = true, Location = new Point(0, S(2)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        var text = new TextBox
        {
            Text = value, Location = new Point(0, S(22)), Width = S(574),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        var capAs = new Label
        {
            Text = "Add it as:", AutoSize = true, Location = new Point(0, S(58)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        var mode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(72), S(54)), Width = S(502),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        mode.Items.AddRange(new object[] { argItem, cmdItem });
        mode.SelectedIndex = asArg ? 0 : 1;
        body.Controls.AddRange(new Control[] { cap, text, capAs, mode });

        return (body, S(86), () =>
        {
            bool arg = mode.SelectedIndex == 0;
            save(arg ? text.Text.Trim() : text.Text, arg);
        });
    }
}
