// The probe block UI — the filter/exclude controls nearly EVERY BigBoxProfile action carries, built
// once as a reusable component so each ported action gets the same, polished surface. Two Mehdi
// passes shaped it: the original's dependent checkboxes became ONE mode dropdown (no invalid state
// can be expressed), and the block now rests COLLAPSED as a single accent-blue summary line — the
// same reading grammar as the group headers in the rule list — expanding into the editor on click.
// The summary can say everything the block can hold: the mode dropdown IS the enumeration (4 states
// a side), entries are plain text, and the two flags append as "· marker" / "· group"; long lists
// ellipsize with the full text in a tooltip. Storage is unchanged: the mode maps onto the same
// text + Comma + MatchAll fields the engine reads.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules;

/// <summary>One probe group ("Run only when…" / "Never when…"): a one-line summary when collapsed,
/// the mode combo + text + Manage… (+ marker / group boxes) when expanded. Load from / save to a
/// rule's filter or exclude side; <see cref="LayoutChanged"/> lets the host dialog reflow.</summary>
internal sealed class ProbeBlock
{
    public GroupBox Box { get; }

    /// <summary>Raised when the block's height changed (expand / collapse) — reflow time.</summary>
    public event Action? LayoutChanged;

    private readonly Label _summary;
    private readonly ComboBox _mode;
    private readonly TextBox _text;
    private readonly Button _manage;
    private readonly CheckBox? _marker;
    private readonly CheckBox? _group;
    private readonly ToolTip _tip = new();
    private readonly bool _exclude;
    private readonly float _dpiS;
    private bool _expanded;

    private int S(int px) => (int)Math.Round(px * _dpiS);

    // The collapsed height follows the summary, which WRAPS when the condition is long (Mehdi:
    // several lines when needed) — measured up to four lines, ellipsized beyond.
    private int CollapsedHeight => S(24) + _summary.Height + S(8);
    private int ExpandedHeight => S(_marker != null ? 166 : 116);

    /// <summary>Modes, in combo order: none (probe off), plain substring, ANY of a comma list,
    /// EVERY entry of a comma list — (comma, matchAll) = (false,false) / (true,false) / (true,true).</summary>
    public ProbeBlock(string title, bool exclude, bool withMarker, float dpiS, bool readOnly, int width = 600)
    {
        _dpiS = dpiS;
        _exclude = exclude;
        Box = new GroupBox
        {
            Text = title, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Width = S(width),
        };

        // The collapsed face: the whole condition in one accent line, click to open the editor.
        _summary = new Label
        {
            Location = new Point(S(12), S(20)), Width = Box.Width - S(24), Height = S(20),
            AutoSize = false, AutoEllipsis = true, Cursor = Cursors.Hand,
            ForeColor = LiteBoxTheme.Accent, BackColor = LiteBoxTheme.Bg,
        };
        _summary.Click += (_, _) => SetExpanded(!_expanded);
        Box.Controls.Add(_summary);

        _mode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(12), S(44)), Width = Box.Width - S(28),
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
            Location = new Point(S(12), S(74)), Width = Box.Width - S(118),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            Enabled = !readOnly,
        };
        Box.Controls.Add(_text);

        _manage = new Button
        {
            Text = "Manage…", Location = new Point(Box.Width - S(98), S(72)), Size = new Size(S(82), S(25)),
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
                AutoSize = true, Location = new Point(S(12), S(108)),
                ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg, Enabled = !readOnly,
            };
            Box.Controls.Add(_marker);

            // Presentation + a commitment, not an engine switch (see LaunchRule.AsGroup).
            _group = new CheckBox
            {
                Text = "Group: rules sharing this condition form one branch (keep its arguments unmodified)",
                AutoSize = true, Location = new Point(S(12), S(134)),
                ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg, Enabled = !readOnly,
            };
            Box.Controls.Add(_group);

            _marker.CheckedChanged += (_, _) => RefreshSummary();
            _group.CheckedChanged += (_, _) => RefreshSummary();
        }

        _mode.SelectedIndexChanged += (_, _) => { Sync(); RefreshSummary(); };
        _text.TextChanged += (_, _) => RefreshSummary();

        SetExpanded(false);
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        foreach (Control c in Box.Controls)
            if (!ReferenceEquals(c, _summary))
                c.Visible = expanded;
        Box.Height = expanded ? ExpandedHeight : CollapsedHeight;
        if (expanded) Sync();
        LayoutChanged?.Invoke();
    }

    private void Sync()
    {
        bool active = _mode.SelectedIndex > 0;
        bool listMode = _mode.SelectedIndex >= 2;
        _text.Enabled = active && _mode.Enabled;
        _manage.Enabled = listMode && _mode.Enabled;
        if (_marker != null) _marker.Enabled = active && _mode.Enabled;
        if (_group != null) _group.Enabled = active && _mode.Enabled;
    }

    /// <summary>The one-line reading of the whole block — every expressible state has a sentence:
    /// the mode picks the frame, the entries fill it, the flags append. Ellipsized when long, with
    /// the full text as tooltip.</summary>
    private void RefreshSummary()
    {
        string text = _text.Text.Trim();
        string s = _mode.SelectedIndex switch
        {
            1 => (_exclude ? "Blocked when the line contains " : "Only when the line contains ") + Quote(text),
            2 => (_exclude ? "Blocked when it contains ANY of: " : "Only when it contains ANY of: ") + text,
            3 => (_exclude ? "Blocked only when it contains EVERY of: " : "Only when it contains EVERY of: ") + text,
            _ => _exclude ? "Never blocked" : "Always",
        };
        if (_marker is { Checked: true }) s += "  · marker";
        if (_group is { Checked: true }) s += "  · group";
        _summary.Text = s;
        _tip.SetToolTip(_summary, s + "\n(click to edit)");

        int measured = TextRenderer.MeasureText(s, _summary.Font,
            new Size(_summary.Width, int.MaxValue), TextFormatFlags.WordBreak).Height + S(2);
        int h = Math.Clamp(measured, S(20), S(72));
        if (_summary.Height != h)
        {
            _summary.Height = h;
            if (!_expanded)
            {
                Box.Height = CollapsedHeight;
                LayoutChanged?.Invoke();
            }
        }
    }

    private static string Quote(string t) => t.Length == 0 ? "…" : "\"" + t + "\"";

    public void Load(string text, bool comma, bool matchAll, bool marker = false, bool asGroup = false)
    {
        _mode.SelectedIndex = text.Length == 0 ? 0 : comma ? (matchAll ? 3 : 2) : 1;
        _text.Text = text;
        if (_marker != null) _marker.Checked = marker;
        if (_group != null) _group.Checked = asGroup;
        Sync();
        RefreshSummary();
    }

    /// <summary>The stored shape. Mode 0 saves an EMPTY text — the mode is authoritative, whatever
    /// still sits in the (disabled) field; the engine's "empty = probe off" contract does the rest.</summary>
    public (string Text, bool Comma, bool MatchAll, bool Marker, bool AsGroup) Save()
        => (_mode.SelectedIndex == 0 ? "" : _text.Text.Trim(),
            _mode.SelectedIndex >= 2,
            _mode.SelectedIndex == 3,
            _marker?.Checked ?? false,
            _group?.Checked ?? false);
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
