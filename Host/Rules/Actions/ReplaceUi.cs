// The shared dialog body of the two Replace actions (line / in-file) and the Manage Variables
// editor. One builder, a withFile switch — the field the modes do not share is the only difference,
// which is exactly why they became two actions instead of BigBoxProfile's one dialog where half the
// fields played dead depending on a radio.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal static class ReplaceUi
{
    public static (Control Body, int Height, Action Save) Build(LaunchRule r, float dpiS, bool withFile)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        int height = S(withFile ? 240 : 186);
        var body = new Panel { Size = new Size(S(576), height), BackColor = LiteBoxTheme.Bg };
        int y = 0;

        Label Cap(string t)
        {
            var l = new Label
            {
                Text = t, AutoSize = true, Location = new Point(0, y + S(2)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(l);
            y += S(20);
            return l;
        }
        TextBox Field(string value, int width = 574)
        {
            var t = new TextBox
            {
                Text = value, Location = new Point(0, y), Width = S(width),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            body.Controls.Add(t);
            y += S(30);
            return t;
        }

        TextBox? file = null;
        if (withFile)
        {
            Cap("File to rewrite (variables allowed):");
            file = Field(r.TargetFile, 486);
            var browse = new Button
            {
                Text = "Browse…", Location = new Point(S(492), file.Top - S(2)), Size = new Size(S(82), S(25)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            browse.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            browse.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*" };
                if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) file!.Text = dlg.FileName;
            };
            body.Controls.Add(browse);
        }

        Cap("Search for:");
        var search = Field(r.Search);
        Cap("Replace with (\"\\1\"–\"\\9\" splice regex groups; variables allowed in both):");
        var replace = Field(r.ReplaceWith);

        var regex = new CheckBox
        {
            Text = "Regular expression", Checked = r.UseRegex, AutoSize = true,
            Location = new Point(0, y + S(2)), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        var caseS = new CheckBox
        {
            Text = "Case sensitive", Checked = r.CaseSensitive, AutoSize = true,
            Location = new Point(S(170), y + S(2)), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(regex);
        body.Controls.Add(caseS);

        ComboBox? target = null;
        if (!withFile)
        {
            target = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(320), y), Width = S(254),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            target.Items.AddRange(new object[] { "In each argument", "In the whole command line" });
            target.SelectedIndex = r.AsArg ? 0 : 1;
            body.Controls.Add(target);
        }
        y += S(32);

        var manage = new Button
        {
            Text = "Manage variables…", Location = new Point(0, y), Size = new Size(S(150), S(26)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        manage.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        string variablesData = r.VariablesData;   // edited through the dialog, saved with the rule
        var varCount = new Label
        {
            Text = VarCountText(variablesData), AutoSize = true, Location = new Point(S(158), y + S(5)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        manage.Click += (_, _) =>
        {
            using var dlg = new VariablesDialog(variablesData, dpiS);
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK)
            {
                variablesData = dlg.VariablesData;
                varCount.Text = VarCountText(variablesData);
            }
        };
        body.Controls.Add(manage);
        body.Controls.Add(varCount);

        return (body, height, () =>
        {
            r.Search = search.Text;
            r.ReplaceWith = replace.Text;
            r.UseRegex = regex.Checked;
            r.CaseSensitive = caseS.Checked;
            if (target != null) r.AsArg = target.SelectedIndex == 0;
            if (file != null) r.TargetFile = file.Text.Trim();
            r.VariablesData = variablesData;
        });
    }

    private static string VarCountText(string data)
    {
        int n = RuleVariables.Parse(data).Count;
        return n == 0 ? "no variables" : n == 1 ? "1 variable" : n + " variables";
    }
}

/// <summary>The Manage Variables editor: the list on the left, the selected variable's fields on
/// the right, edits committed to the buffer as you type. OK serializes, Cancel discards.</summary>
internal sealed class VariablesDialog : LiteBoxForm
{
    public string VariablesData { get; private set; }

    private readonly List<RuleVariable> _vars;
    private readonly ListBox _list;
    private bool _binding;

    public VariablesDialog(string variablesData, float dpiS)
    {
        VariablesData = variablesData;
        _vars = RuleVariables.Parse(variablesData);

        Text = "Manage variables";
        ClientSize = new Size(S(620), S(324));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list = new ListBox
        {
            Location = new Point(S(14), S(14)), Size = new Size(S(180), S(240)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
        };
        Controls.Add(_list);

        Button Btn(string t, int x, int yy, int w = 86)
        {
            var b = new Button
            {
                Text = t, Location = new Point(S(x), S(yy)), Size = new Size(S(w), S(26)),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            Controls.Add(b);
            return b;
        }
        var add = Btn("Add", 14, 258);
        var remove = Btn("Remove", 108, 258);

        int rx = 210, y = S(14);
        Label Cap(string t)
        {
            var l = new Label
            {
                Text = t, AutoSize = true, Location = new Point(S(rx), y),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            Controls.Add(l);
            y += S(18);
            return l;
        }
        TextBox Field()
        {
            var t = new TextBox
            {
                Location = new Point(S(rx), y), Width = S(392),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(t);
            y += S(26);
            return t;
        }

        Cap("Token (as written in texts, e.g. {ROM}):");
        var name = Field();
        Cap("Source:");
        var source = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(S(rx), y), Width = S(392),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        source.Items.AddRange(new object[] { "cmd", "arg" });
        Controls.Add(source);
        y += S(26);
        Cap("(cmd = exe + arguments · arg = each argument, last match wins · or type a FILE path)");
        Cap("Regex to match on the source:");
        var pattern = Field();
        Cap("Value on match (\"\\1\"–\"\\9\" splice the groups):");
        var value = Field();
        Cap("Fallback when nothing matches:");
        var fallback = Field();

        void Reload(int select)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var v in _vars) _list.Items.Add(v.Name.Length > 0 ? v.Name : "(unnamed)");
            _list.EndUpdate();
            if (select >= 0 && select < _vars.Count) _list.SelectedIndex = select;
        }
        void Bind()
        {
            _binding = true;
            var v = _list.SelectedIndex >= 0 && _list.SelectedIndex < _vars.Count ? _vars[_list.SelectedIndex] : null;
            name.Text = v?.Name ?? ""; source.Text = v?.Source ?? "cmd";
            pattern.Text = v?.Pattern ?? ""; value.Text = v?.Value ?? ""; fallback.Text = v?.Fallback ?? "";
            name.Enabled = source.Enabled = pattern.Enabled = value.Enabled = fallback.Enabled = v != null;
            _binding = false;
        }
        void Commit()
        {
            if (_binding || _list.SelectedIndex < 0 || _list.SelectedIndex >= _vars.Count) return;
            var v = _vars[_list.SelectedIndex];
            v.Name = name.Text.Trim(); v.Source = source.Text.Trim();
            v.Pattern = pattern.Text; v.Value = value.Text; v.Fallback = fallback.Text;
            int ix = _list.SelectedIndex;
            _list.Items[ix] = v.Name.Length > 0 ? v.Name : "(unnamed)";
        }
        name.TextChanged += (_, _) => Commit();
        source.TextChanged += (_, _) => Commit();
        pattern.TextChanged += (_, _) => Commit();
        value.TextChanged += (_, _) => Commit();
        fallback.TextChanged += (_, _) => Commit();
        _list.SelectedIndexChanged += (_, _) => Bind();

        add.Click += (_, _) =>
        {
            _vars.Add(new RuleVariable { Name = "{VAR" + (_vars.Count + 1) + "}" });
            Reload(_vars.Count - 1);
        };
        remove.Click += (_, _) =>
        {
            int ix = _list.SelectedIndex;
            if (ix < 0 || ix >= _vars.Count) return;
            _vars.RemoveAt(ix);
            Reload(Math.Min(ix, _vars.Count - 1));
            Bind();
        };

        var ok = ActionButton("OK", MenuIcons.Add);
        ok.Location = new Point(S(410), S(286));
        ok.Click += (_, _) =>
        {
            VariablesData = RuleVariables.Serialize(_vars.Where(v => v.Name.Length > 0).ToList());
            DialogResult = DialogResult.OK; Close();
        };
        var cancel = ActionButton("Cancel", MenuIcons.Exit);
        cancel.Location = new Point(S(512), S(286));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(ok); Controls.Add(cancel);

        Reload(_vars.Count > 0 ? 0 : -1);
        Bind();
    }
}
