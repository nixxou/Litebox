// Builds a top-down stack of option rows (checkbox / textbox / combo, each with an
// optional help line) inside a FlowLayoutPanel. Every row's position comes from the
// PREVIOUS row's real PreferredSize at layout time - never a pre-computed Y offset - so it
// can't go stale regardless of DPI, font, or when the row is added to a form that's already
// been shown (WinForms' own Font-based AutoScale only ever runs once, at first Show/Load;
// content attached afterward - e.g. a lazily-built section panel - never gets touched by it).
// This is shared by any dialog that needs an OptionItem list, not just the global Options
// window - extracted so that bug class only needs fixing once.

#nullable enable

using LbApiHost.Host.Options;

namespace LbApiHost.Host.UiKit;

internal static class OptionRows
{
    public static (Control panel, Action apply) Build(IEnumerable<OptionItem> items, Func<int, int> S)
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
        };
        var applies = new List<Action>();
        int wrapWidth = S(620);

        foreach (var it in items)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = LiteBoxTheme.Bg, Margin = new Padding(S(4), S(4), S(4), S(12)),
            };

            switch (it.Kind)
            {
                case OptionKind.Bool:
                {
                    var cb = new CheckBox
                    {
                        Text = it.Label, AutoSize = true, MaximumSize = new Size(wrapWidth, 0),
                        ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
                        Checked = string.Equals(it.Get(), "true", StringComparison.OrdinalIgnoreCase),
                    };
                    row.Controls.Add(cb);
                    applies.Add(() => ApplyIfChanged(it, cb.Checked ? "true" : "false"));
                    break;
                }
                case OptionKind.Text:
                {
                    var lbl = new Label { Text = it.Label, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg };
                    var tb = new TextBox
                    {
                        Width = S(480), Margin = new Padding(0, S(4), 0, 0),
                        BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
                        Text = it.Get(),
                    };
                    row.Controls.Add(lbl); row.Controls.Add(tb);
                    applies.Add(() => ApplyIfChanged(it, tb.Text));
                    break;
                }
                case OptionKind.Path:
                {
                    // Textbox + Browse… on one line: the path stays typeable, the picker fills it in.
                    var lbl = new Label { Text = it.Label, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg };
                    var pathRow = new FlowLayoutPanel
                    {
                        FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.Bg,
                        Margin = new Padding(0, S(4), 0, 0),
                    };
                    var tb = new TextBox
                    {
                        Width = S(480), Margin = new Padding(0),
                        BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
                        Text = it.Get(),
                    };
                    var browse = new Button
                    {
                        Text = "Browse…", AutoSize = true, Margin = new Padding(S(6), 0, 0, 0),
                        FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
                        FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand,
                    };
                    browse.Click += (_, _) =>
                    {
                        using var dlg = new OpenFileDialog
                        {
                            Title = it.Label,
                            Filter = it.FileFilter ?? "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                            CheckFileExists = true,
                        };
                        try
                        {
                            var cur = tb.Text.Trim();
                            var dir = cur.Length > 0 ? System.IO.Path.GetDirectoryName(cur) : null;
                            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) dlg.InitialDirectory = dir;
                        }
                        catch { }
                        if (dlg.ShowDialog(tb.FindForm()) == DialogResult.OK) tb.Text = dlg.FileName;
                    };
                    pathRow.Controls.Add(tb); pathRow.Controls.Add(browse);
                    row.Controls.Add(lbl); row.Controls.Add(pathRow);
                    applies.Add(() => ApplyIfChanged(it, tb.Text));
                    break;
                }
                case OptionKind.Number:
                {
                    // Label beside a numeric spinner (digits only, clamped) — own left-to-right sub-flow.
                    var numRow = new FlowLayoutPanel
                    {
                        FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.Bg,
                    };
                    var lbl = new Label { Text = it.Label, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg, Margin = new Padding(0, S(6), S(12), 0) };
                    var nud = new NumericUpDown
                    {
                        Width = S(120), Margin = new Padding(0),
                        Minimum = it.NumMin, Maximum = it.NumMax, Increment = it.NumStep,
                        BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
                    };
                    decimal init = it.NumMin;
                    if (int.TryParse(it.Get(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var iv))
                        init = Math.Max(it.NumMin, Math.Min(it.NumMax, iv));
                    nud.Value = init;
                    numRow.Controls.Add(lbl); numRow.Controls.Add(nud);
                    row.Controls.Add(numRow);
                    applies.Add(() => ApplyIfChanged(it, ((int)nud.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                }
                case OptionKind.Choice:
                {
                    // Label beside the combo, so this pair needs its own left-to-right sub-flow.
                    var lblRow = new FlowLayoutPanel
                    {
                        FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.Bg,
                    };
                    var lbl = new Label { Text = it.Label, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg, Margin = new Padding(0, S(6), S(12), 0) };
                    var cmb = new ComboBox
                    {
                        Width = S(260), Margin = new Padding(0),
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
                    };
                    cmb.Items.AddRange(it.Choices);
                    // Grow (never shrink) to the widest entry: a self-explaining choice like
                    // "Follow LaunchBox — currently: LiteBox popups" is useless truncated to "LiteBox po…".
                    // Capped at the help text's wrap width so one long entry can't stretch the page.
                    foreach (var choice in it.Choices)
                    {
                        int need = TextRenderer.MeasureText(choice ?? "", cmb.Font).Width + S(30);   // + arrow & padding
                        if (need > cmb.Width) cmb.Width = Math.Min(need, wrapWidth);
                    }
                    // With ChoiceValues, the combo displays Choices[i] but storage speaks ChoiceValues[i].
                    var values = it.ChoiceValues is { } cv && cv.Length == it.Choices.Length ? cv : it.Choices;
                    var cur = it.Get();
                    int ix = Array.FindIndex(values, c => string.Equals(c, cur, StringComparison.OrdinalIgnoreCase));
                    cmb.SelectedIndex = ix >= 0 ? ix : (it.Choices.Length > 0 ? 0 : -1);
                    lblRow.Controls.Add(lbl); lblRow.Controls.Add(cmb);
                    row.Controls.Add(lblRow);
                    applies.Add(() => { if (cmb.SelectedIndex >= 0 && cmb.SelectedIndex < values.Length) ApplyIfChanged(it, values[cmb.SelectedIndex]); });
                    break;
                }
                case OptionKind.Button:
                {
                    var btn = new Button
                    {
                        Text = it.Label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Padding = new Padding(S(12), S(3), S(12), S(3)), Margin = new Padding(0, S(2), 0, S(2)),
                        FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
                        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9f),
                    };
                    var onClick = it.OnClick;
                    btn.Click += (_, _) => onClick?.Invoke();
                    row.Controls.Add(btn);
                    break;
                }
            }

            if (it.NoImpact)
            {
                var ni = new Label
                {
                    Text = "No impact on LiteBox", AutoSize = true, Margin = new Padding(S(18), S(2), 0, 0),
                    ForeColor = LiteBoxTheme.Danger, BackColor = LiteBoxTheme.Bg,
                    Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
                };
                row.Controls.Add(ni);
            }

            if (!string.IsNullOrEmpty(it.Help))
            {
                var help = new Label
                {
                    Text = it.Help, AutoSize = true, Margin = new Padding(S(18), S(2), 0, 0),
                    ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg, Font = new Font("Segoe UI", 8.25f),
                    MaximumSize = new Size(wrapWidth, 0),
                };
                row.Controls.Add(help);
            }

            root.Controls.Add(row);
        }

        return (root, () => { foreach (var a in applies) a(); });
    }

    private static void ApplyIfChanged(OptionItem it, string newValue)
    {
        if (string.Equals(it.Get(), newValue, StringComparison.Ordinal)) return;
        it.Set(newValue);
        try { it.ApplyLive?.Invoke(); } catch { }
    }
}
