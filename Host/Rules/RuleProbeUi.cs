// The probe block UI — the filter/exclude controls nearly EVERY BigBoxProfile action carries, built
// once as a reusable component so each ported action gets the same, polished surface. The redesign
// (Mehdi-approved, on trial): the original's dependent checkboxes ("Multiple entries" ungreys "Must
// match all") become ONE mode dropdown — no invalid state can be expressed — and the Manage… button
// (BigBoxProfile's Manage_Items list editor) appears with the list modes. Storage is unchanged: the
// mode maps onto the same text + Comma + MatchAll fields the engine reads.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules;

/// <summary>One probe group ("Run only when…" / "Never when…"): mode combo, text field, Manage…,
/// and optionally the marker checkbox. Load from / save to a rule's filter or exclude side.</summary>
internal sealed class ProbeBlock
{
    public GroupBox Box { get; }

    private readonly ComboBox _mode;
    private readonly TextBox _text;
    private readonly Button _manage;
    private readonly CheckBox? _marker;
    private readonly float _dpiS;

    private int S(int px) => (int)Math.Round(px * _dpiS);

    /// <summary>Modes, in combo order: none (probe off), plain substring, ANY of a comma list,
    /// EVERY entry of a comma list. The wording differs between the filter and exclude sides but
    /// the mapping is the same: (comma, matchAll) = (false,false) / (true,false) / (true,true).</summary>
    public ProbeBlock(string title, bool exclude, bool withMarker, float dpiS, bool readOnly)
    {
        _dpiS = dpiS;
        Box = new GroupBox
        {
            Text = title, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Width = S(430), Height = S(withMarker ? 118 : 92),
        };

        _mode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(12), S(22)), Width = S(404),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            Enabled = !readOnly,
        };
        _mode.Items.AddRange(exclude
            ? new object[]
            {
                "Never blocked",
                "Blocked when the command line contains this text",
                "Blocked when it contains ANY entry of the list",
                "Blocked only when it contains EVERY entry of the list",
            }
            : new object[]
            {
                "Always",
                "Only when the command line contains this text",
                "Only when it contains ANY entry of the list",
                "Only when it contains EVERY entry of the list",
            });
        Box.Controls.Add(_mode);

        _text = new TextBox
        {
            Location = new Point(S(12), S(52)), Width = S(316),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            Enabled = !readOnly,
        };
        Box.Controls.Add(_text);

        _manage = new Button
        {
            Text = "Manage…", Location = new Point(S(334), S(50)), Size = new Size(S(82), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            Enabled = !readOnly,
        };
        _manage.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        _manage.Click += (_, _) =>
        {
            using var dlg = new ManageItemsDialog(_text.Text, dpiS);
            if (dlg.ShowDialog(Box.FindForm()) == DialogResult.OK) _text.Text = dlg.Value;
        };
        Box.Controls.Add(_manage);

        if (withMarker)
        {
            _marker = new CheckBox
            {
                Text = "Marker: strip arguments equal to an entry before launch",
                AutoSize = true, Location = new Point(S(12), S(86)),
                ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg, Enabled = !readOnly,
            };
            Box.Controls.Add(_marker);
        }

        _mode.SelectedIndexChanged += (_, _) => Sync();
    }

    private void Sync()
    {
        bool active = _mode.SelectedIndex > 0;
        bool listMode = _mode.SelectedIndex >= 2;
        _text.Enabled = active && _mode.Enabled;
        _manage.Enabled = listMode && _mode.Enabled;
        if (_marker != null) _marker.Enabled = active && _mode.Enabled;
    }

    public void Load(string text, bool comma, bool matchAll, bool marker = false)
    {
        _mode.SelectedIndex = text.Length == 0 ? 0 : comma ? (matchAll ? 3 : 2) : 1;
        _text.Text = text;
        if (_marker != null) _marker.Checked = marker;
        Sync();
    }

    /// <summary>The stored shape. Mode 0 saves an EMPTY text — the mode is authoritative, whatever
    /// still sits in the (disabled) field; the engine's "empty = probe off" contract does the rest.</summary>
    public (string Text, bool Comma, bool MatchAll, bool Marker) Save()
        => (_mode.SelectedIndex == 0 ? "" : _text.Text.Trim(),
            _mode.SelectedIndex >= 2,
            _mode.SelectedIndex == 3,
            _marker?.Checked ?? false);
}

/// <summary>BigBoxProfile's Manage_Items, native: the comma list edited as an actual list — add,
/// remove, reorder — and re-joined on OK. Entries containing commas cannot be expressed by the
/// storage format, so the field refuses them, which the free-text box silently could not.</summary>
internal sealed class ManageItemsDialog : LiteBoxForm
{
    public string Value { get; private set; } = "";

    public ManageItemsDialog(string commaList, float dpiS)
    {
        Text = "Manage entries";
        ClientSize = new Size(S(380), S(300));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        var input = new TextBox
        {
            Location = new Point(S(14), S(14)), Width = S(266),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        var list = new ListBox
        {
            Location = new Point(S(14), S(46)), Size = new Size(S(266), S(200)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
        };
        foreach (var e in commaList.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            list.Items.Add(e);

        Button Side(string t, int yy)
        {
            var b = new Button
            {
                Text = t, Location = new Point(S(292), S(yy)), Size = new Size(S(74), S(26)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            Controls.Add(b);
            return b;
        }
        var add = Side("Add", 13);
        var up = Side("▲", 46);
        var down = Side("▼", 76);
        var del = Side("Delete", 106);

        add.Click += (_, _) =>
        {
            string v = input.Text.Trim();
            if (v.Length == 0) return;
            if (v.Contains(','))
            {
                MessageBox.Show(this, "An entry cannot contain a comma — that is the list separator.",
                    "Manage entries", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            list.Items.Add(v);
            input.Clear();
            input.Focus();
        };
        input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { add.PerformClick(); e.Handled = e.SuppressKeyPress = true; } };
        void Move(int delta)
        {
            int ix = list.SelectedIndex, to = ix + delta;
            if (ix < 0 || to < 0 || to >= list.Items.Count) return;
            var item = list.Items[ix];
            list.Items.RemoveAt(ix);
            list.Items.Insert(to, item);
            list.SelectedIndex = to;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        del.Click += (_, _) => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };

        var ok = ActionButton("OK", MenuIcons.Add);
        ok.Location = new Point(S(170), S(262));
        ok.Click += (_, _) =>
        {
            Value = string.Join(", ", list.Items.Cast<object>().Select(o => o?.ToString() ?? ""));
            DialogResult = DialogResult.OK; Close();
        };
        var cancel = ActionButton("Cancel", MenuIcons.Exit);
        cancel.Location = new Point(S(272), S(262));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { input, list, ok, cancel });
    }
}
