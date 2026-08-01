// "These games have manuals the destination does not. Which do you want to keep?"
//
// A combine folds several games into one, and only the root's documents survive by default —
// LaunchBox drops the rest without a word. Rather than reproduce that silently or override it
// silently, the ones that would be lost are offered.
//
// Only DISTINCT documents are offered. A manual the destination already has under the same file is
// not a choice, it is noise: showing it would invite someone to "keep" a second copy of what they
// already have, which is the opposite of helping.
//
// Nothing is pre-checked. The default outcome of clicking through is LaunchBox's behaviour, which
// is the right default for a dialog nobody reads — the ones who do read it get the choice.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Platforms;

internal static class CombineDocumentPicker
{
    /// <summary>The documents of <paramref name="absorbed"/> that <paramref name="root"/> does not
    /// already have, matched on the file they point at rather than on their name — the same manual
    /// filed under two labels is one manual.</summary>
    public static List<HostAdditionalApplication> Distinct(IReadOnlyList<IEnumerable<HostAdditionalApplication>> absorbed,
                                                           IEnumerable<HostAdditionalApplication> root)
    {
        var have = new HashSet<string>(
            root.Select(a => Norm(a.ApplicationPath)).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
        var result = new List<HostAdditionalApplication>();
        foreach (var group in absorbed)
            foreach (var doc in group)
            {
                string p = Norm(doc.ApplicationPath);
                if (p.Length == 0 || !have.Add(p)) continue;   // already there, or offered twice
                result.Add(doc);
            }
        return result;
    }

    private static string Norm(string? path)
        => (path ?? "").Replace('/', '\\').Trim().TrimEnd('\\');

    /// <summary>Shows the list and returns the ids to carry. An empty set means "keep none", which
    /// is also what Cancel means — there is no way to abort the combine from here, only to decline
    /// the extra.</summary>
    public static HashSet<string> Ask(IWin32Window? owner, IReadOnlyList<HostAdditionalApplication> candidates)
    {
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (candidates == null || candidates.Count == 0) return chosen;

        using var form = new Form
        {
            Text = "Documents",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.Fg,
        };
        float s = LiteBoxTheme.DpiScale(form);
        int S(int px) => (int)Math.Round(px * s);
        form.ClientSize = new Size(S(560), S(120 + Math.Min(candidates.Count, 10) * 20));

        form.Controls.Add(new Label
        {
            Text = candidates.Count == 1
                ? "The absorbed game has a document the destination does not. Keep it?"
                : $"The absorbed games have {candidates.Count} documents the destination does not. Keep them?",
            Location = new Point(S(12), S(12)), Size = new Size(S(536), S(34)),
            ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        });

        var list = new CheckedListBox
        {
            Location = new Point(S(12), S(48)),
            Size = new Size(S(536), form.ClientSize.Height - S(96)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
            BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false,
        };
        foreach (var doc in candidates) list.Items.Add(Describe(doc), false);
        form.Controls.Add(list);

        var all = new Button
        {
            Text = "Select all", Location = new Point(S(12), form.ClientSize.Height - S(40)),
            Size = new Size(S(96), S(28)), FlatStyle = FlatStyle.Flat,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        all.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        form.Controls.Add(all);

        var ok = new Button
        {
            Text = "Keep checked", DialogResult = DialogResult.OK,
            Location = new Point(S(330), form.ClientSize.Height - S(40)), Size = new Size(S(110), S(28)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        var none = new Button
        {
            Text = "Keep none", DialogResult = DialogResult.Cancel,
            Location = new Point(S(448), form.ClientSize.Height - S(40)), Size = new Size(S(100), S(28)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
        };
        form.Controls.Add(ok);
        form.Controls.Add(none);
        form.AcceptButton = ok;
        form.CancelButton = none;

        if (form.ShowDialog(owner) != DialogResult.OK) return chosen;
        foreach (int i in list.CheckedIndices) chosen.Add(candidates[i].Id ?? "");
        chosen.Remove("");
        return chosen;
    }

    private static string Describe(HostAdditionalApplication doc)
    {
        string name = (doc.Name ?? "").Trim();
        string file = "";
        try { file = Path.GetFileName(doc.ApplicationPath ?? ""); } catch { }
        return name.Length > 0 && !string.Equals(name, file, StringComparison.OrdinalIgnoreCase)
            ? $"{name}   ({file})" : file;
    }
}
