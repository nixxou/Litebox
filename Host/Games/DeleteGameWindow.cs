// The delete confirmation — a real window, not a MessageBox, because the question has more than one
// answer: the entry always goes, the media are a choice, kind by kind, with the file count of each.
//
//   [x] Delete the game entry          ← always on, shown disabled: it is what the command IS
//   [ ] Images (12)
//   [ ] Videos (2)
//   [ ] Manuals (1)
//
//   [Cancel]            [Delete Selection]  [Delete Everything]
//
// Only kinds with at least one file get a line, and the counts already exclude anything another game
// would also resolve (GameMediaDeleter decides) — nothing offered here is a file we would refuse to
// delete afterwards.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Games;

internal sealed class DeleteGameWindow : LiteBoxForm
{
    private readonly List<(CheckBox box, GameMediaDeleter.MediaGroup group)> _rows = new();

    /// <summary>Files the user agreed to delete. Empty when only the entry goes.</summary>
    public List<string> Files { get; } = new();

    private DeleteGameWindow(IGame[] games, GameMediaDeleter.Plan plan)
    {
        Text = games.Length == 1 ? "Delete Game" : "Delete Games";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        int w = S(430), pad = S(16), y = pad;

        var head = new Label
        {
            AutoSize = false, Left = pad, Top = y, Width = w - pad * 2,
            ForeColor = LiteBoxTheme.Fg, Font = new Font(Font, FontStyle.Bold),
            Text = games.Length == 1 ? $"Delete “{Title1(games[0])}” ?" : $"Delete these {games.Length} games?",
            Height = S(22),
        };
        Controls.Add(head);
        y += head.Height + S(4);

        // Naming them is only useful while they can be read: past ten the list stops being a list
        // and becomes a wall, so it ends in a count.
        const int MaxNamed = 10;
        if (games.Length > 1)
        {
            int lines = Math.Min(MaxNamed, games.Length) + (games.Length > MaxNamed ? 1 : 0);
            var list = new Label
            {
                AutoSize = false, Left = pad, Top = y, Width = w - pad * 2, Height = S(16) * lines + S(6),
                ForeColor = LiteBoxTheme.SubFg,
                Text = string.Join("\n", games.Take(MaxNamed).Select(g => "• " + Title1(g)))
                     + (games.Length > MaxNamed ? $"\n… and {games.Length - MaxNamed:N0} more" : ""),
            };
            Controls.Add(list);
            y += list.Height + S(4);
        }

        // The entry itself: shown so the dialog states its whole effect, disabled because it is not
        // a choice — this command deletes the game.
        // AutoCheck off rather than Enabled off: a disabled control paints in the system's grey,
        // which on this background is barely readable — and the line is there to be READ.
        var entry = new CheckBox
        {
            Text = games.Length == 1 ? "Delete the game entry" : $"Delete the {games.Length} game entries",
            Checked = true, AutoCheck = false, AutoSize = true, TabStop = false,
            Left = pad, Top = y, ForeColor = LiteBoxTheme.SubFg,
        };
        Controls.Add(entry);
        y += S(26);

        foreach (var grp in plan.Groups)
        {
            var cb = new CheckBox
            {
                Text = $"{grp.Label} ({grp.Count})", AutoSize = true,
                Left = pad + S(4), Top = y, ForeColor = LiteBoxTheme.Fg,
            };
            Controls.Add(cb);
            _rows.Add((cb, grp));
            y += S(24);
        }

        y += S(4);
        var note = new Label
        {
            AutoSize = false, Left = pad, Top = y, Width = w - pad * 2,
            Height = plan.Skipped || plan.Shared > 0 ? S(62) : S(46),
            ForeColor = LiteBoxTheme.SubFg,
            Text = plan.Skipped
                 ? $"The game cache isn't up for these platforms, so finding the media of {games.Length:N0} games "
                 + "would mean scanning the disk game by game. The files are left on disk — delete a smaller "
                 + "selection to be offered them. ROMs and save games are never touched."
                 : (plan.Shared > 0
                        ? $"Files another game also uses are never deleted — {plan.Shared} left out of the counts above.\n"
                        : "")
                 + "Only files inside LaunchBox's own media folders count: manuals and music kept elsewhere "
                 + "are references and stay put. ROMs and save games are never touched.",
        };
        Controls.Add(note);
        y += note.Height + S(8);

        // Buttons, right-aligned: Cancel · Delete Selection ▾. "Delete everything" is NOT a button
        // sitting under the mouse — it hides behind the caret, the way the detail pane's Play does,
        // so the irreversible answer takes a deliberate second click.
        int bh = S(28), bx = w - pad;
        var buttons = new List<Button>();

        if (plan.HasMedia)
        {
            var caret = MakeButton("▾", Color.FromArgb(96, 42, 42), S(26), bh);
            var more = new ContextMenuStrip { BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg };
            int total = plan.AllFiles.Count();
            var everything = new ToolStripMenuItem($"Delete everything  ({total:N0} files)");
            everything.Click += (_, _) => { Files.AddRange(plan.AllFiles); Accept(); };
            more.Items.Add(everything);
            caret.Click += (_, _) => more.Show(caret, new Point(0, caret.Height));
            buttons.Add(caret);

            var sel = MakeButton("Delete Selection", Color.FromArgb(118, 52, 52), S(130), bh);
            sel.Click += (_, _) =>
            {
                foreach (var (box, grp) in _rows) if (box.Checked) Files.AddRange(grp.Files);
                Accept();
            };
            buttons.Add(sel);
        }
        else
        {
            var del = MakeButton("Delete", Color.FromArgb(118, 52, 52), S(100), bh);
            del.Click += (_, _) => Accept();
            buttons.Add(del);
        }

        var cancel = MakeButton("Cancel", LiteBoxTheme.CancelBtn, S(90), bh);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Add(cancel);

        // Right to left. The caret is placed first and the button that follows it sits FLUSH — the
        // two read as one split control, not as two buttons that happen to be neighbours.
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            bx -= b.Width;
            b.Left = bx; b.Top = y;
            bx -= (i == 0 && plan.HasMedia) ? 0 : S(8);
            Controls.Add(b);
        }
        CancelButton = cancel;

        ClientSize = new Size(w, y + bh + pad);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    private void Accept() { DialogResult = DialogResult.OK; Close(); }

    private Button MakeButton(string text, Color back, int width, int height) => new()
    {
        Text = text, Width = width, Height = height,
        FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 },
    };

    private static string Title1(IGame g) { try { return g.Title ?? ""; } catch { return ""; } }

    /// <summary>Asks. Returns the files to delete alongside the games, or null when cancelled.</summary>
    public static List<string>? Ask(IWin32Window owner, IGame[] games, GameMediaDeleter.Plan plan)
    {
        using var w = new DeleteGameWindow(games, plan);
        return w.ShowDialog(owner) == DialogResult.OK ? w.Files : null;
    }
}
