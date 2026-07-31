// "Root Game" — the one question a Combine has to ask: which of the selected games survives, and
// which ones become its versions. LaunchBox shows a single sentence and a dropdown; so does this.
//
// It is the last point where the operation can be called off, and the operation is destructive
// (every other game stops existing), so Cancel returns null and nothing happens.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class RootGamePicker
{
    /// <summary>The game the others fold into, or null if the user cancelled.</summary>
    public static IGame? Ask(IWin32Window? owner, IReadOnlyList<IGame> games)
    {
        if (games == null || games.Count < 2) return null;

        using var form = new Form
        {
            Text = "Root Game",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.Fg,
            ClientSize = new Size(430, 130),
        };
        float s = LiteBoxTheme.DpiScale(form);
        int S(int px) => (int)Math.Round(px * s);
        form.ClientSize = new Size(S(430), S(130));

        form.Controls.Add(new Label
        {
            Text = "The selected games will be combined into the root game you select below:",
            Location = new Point(S(12), S(14)), Size = new Size(S(406), S(34)),
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        });

        var combo = new ComboBox
        {
            Location = new Point(S(12), S(52)), Width = S(406),
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        // Titles alone would be ambiguous when the selection is several discs of one game, which is
        // the common case — the version distinguishes them.
        foreach (var g in games) combo.Items.Add(Describe(g));
        combo.SelectedIndex = 0;
        form.Controls.Add(combo);

        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Location = new Point(S(232), S(90)), Size = new Size(S(88), S(28)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(S(330), S(90)), Size = new Size(S(88), S(28)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK && combo.SelectedIndex >= 0
            ? games[combo.SelectedIndex]
            : null;
    }

    private static string Describe(IGame g)
    {
        string title = Safe(() => g.Title) ?? "";
        string version = Safe(() => g.Version) ?? "";
        return version.Trim().Length > 0 ? $"{title} {version.Trim()}" : title;
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
