// The shared dialog body of the two Replace actions (line / in-file) and the Manage Variables
// editor. One builder, a withFile switch — the field the modes do not share is the only difference,
// which is exactly why they became two actions instead of BigBoxProfile's one dialog where half the
// fields played dead depending on a radio.
//
// SANDBOXES everywhere (Mehdi: the subject is complex enough to demand them): the Replace dialog
// carries a full test line and shows the live result of the CURRENT settings — and the file mode
// adds a test-content box, so a config rewrite is rehearsed without touching any real file; the
// variables editor shows every variable's resolved value for the test line as you type. Nothing a
// sandbox computes ever writes anywhere.

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
    private const string SampleLine = @"emulator.exe -L ""cores\snes.dll"" ""C:\roms\game.zip""";

    public static (Control Body, int Height, Action Save) Build(LaunchRule r, float dpiS, bool withFile)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = (int)Math.Round(576 * dpiS) };
        int y = 0;

        Label Cap(string t, bool dim = true)
        {
            var l = new Label
            {
                Text = t, AutoSize = true, Location = new Point(0, y + S(2)),
                ForeColor = dim ? LiteBoxTheme.SubFg : LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(l);
            y += S(20);
            return l;
        }
        TextBox Field(string value, int width = 574, int lines = 1, bool readOnly = false)
        {
            var t = new TextBox
            {
                Text = value, Location = new Point(0, y), Width = S(width),
                BackColor = readOnly ? LiteBoxTheme.Bg : LiteBoxTheme.Panel2,
                ForeColor = readOnly ? LiteBoxTheme.SubFg : LiteBoxTheme.Fg,
                BorderStyle = BorderStyle.FixedSingle, ReadOnly = readOnly,
                Multiline = lines > 1, Height = lines > 1 ? S(14 * lines + 8) : S(23),
                ScrollBars = lines > 1 ? ScrollBars.Vertical : ScrollBars.None,
            };
            body.Controls.Add(t);
            y += (lines > 1 ? S(14 * lines + 8) : S(23)) + S(7);
            return t;
        }

        TextBox? file = null;
        if (withFile)
        {
            Cap("File to rewrite (variables allowed):");
            file = Field(r.TargetFile, 486);
            var browse = new Button
            {
                Text = "Browse…", Location = new Point(S(492), file.Top - S(1)), Size = new Size(S(82), S(25)),
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
        y += S(30);

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
        body.Controls.Add(manage);
        body.Controls.Add(varCount);
        y += S(32);

        Cap(withFile
            ? "ex: search \"fullscreen=0\" → \"fullscreen=1\"  ·  regex \"core=(\\w+)\" → \"core={CORE}\""
            : "ex: regex search \"disc(\\d)\" → replace \"d\\1\"  ·  literal \"-window\" → \"-fullscreen\"");

        // ── the sandbox — rehearse the CURRENT settings, write nothing, ever ──
        Cap("Sandbox — test line (exe included; feeds the variables and, without file, the replace):");
        var testLine = Field(SampleLine);
        TextBox? testContent = null;
        if (withFile)
        {
            Cap("Test file content (rehearses the rewrite — no real file is touched):");
            testContent = Field("fullscreen=0\r\nvsync=1", lines: 4);
        }
        Cap("Result:");
        var result = Field("", lines: withFile ? 4 : 1, readOnly: true);

        void Recalc()
        {
            try
            {
                var vars = RuleVariables.Parse(variablesData);
                var all = RuleArgs.SplitFull(testLine.Text);
                string exe = all.FirstOrDefault() ?? "";
                string args = RuleArgs.Join(all.Skip(1));
                string s2 = RuleVariables.Expand(search.Text, vars, exe, args);
                string r2 = RuleVariables.Expand(replace.Text, vars, exe, args);
                if (withFile)
                {
                    string outc = RuleVariables.DoReplace(testContent!.Text, s2, r2, regex.Checked, caseS.Checked, singleline: true);
                    result.Text = RuleVariables.Expand(outc, vars, exe, args);
                }
                else if (target!.SelectedIndex == 0)
                {
                    string outa = RuleArgs.Join(RuleArgs.Split(args)
                        .Select(a => RuleVariables.DoReplace(a, s2, r2, regex.Checked, caseS.Checked)));
                    result.Text = RuleArgs.Join(new[] { exe }) + " " + RuleVariables.Expand(outa, vars, exe, outa);
                }
                else
                {
                    string outc = RuleVariables.DoReplace(args, s2, r2, regex.Checked, caseS.Checked);
                    result.Text = RuleArgs.Join(new[] { exe }) + " " + RuleVariables.Expand(outc, vars, exe, outc);
                }
            }
            catch (Exception ex) { result.Text = "(invalid: " + ex.Message + ")"; }
        }
        search.TextChanged += (_, _) => Recalc();
        replace.TextChanged += (_, _) => Recalc();
        regex.CheckedChanged += (_, _) => Recalc();
        caseS.CheckedChanged += (_, _) => Recalc();
        if (target != null) target.SelectedIndexChanged += (_, _) => Recalc();
        testLine.TextChanged += (_, _) => Recalc();
        if (testContent != null) testContent.TextChanged += (_, _) => Recalc();
        manage.Click += (_, _) =>
        {
            using var dlg = new VariablesDialog(variablesData, dpiS, testLine.Text);
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK)
            {
                variablesData = dlg.VariablesData;
                varCount.Text = VarCountText(variablesData);
                Recalc();
            }
        };
        Recalc();

        body.Height = y;
        return (body, y, () =>
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
/// the right — and the sandbox below: a test line, and every variable's LIVE resolved value for it,
/// recomputed as you type. OK serializes, Cancel discards; the sandbox writes nothing.</summary>
internal sealed class VariablesDialog : LiteBoxForm
{
    public string VariablesData { get; private set; }

    private readonly List<RuleVariable> _vars;
    private readonly ListBox _list;
    private bool _binding;

    public VariablesDialog(string variablesData, float dpiS, string? sampleLine = null)
    {
        VariablesData = variablesData;
        _vars = RuleVariables.Parse(variablesData);

        Text = "Manage variables";
        ClientSize = new Size(S(620), S(452));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list = new ListBox
        {
            Location = new Point(S(14), S(14)), Size = new Size(S(180), S(226)),
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
        var add = Btn("Add", 14, 244);
        var remove = Btn("Remove", 108, 244);

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
        TextBox Field(int width = 392)
        {
            var t = new TextBox
            {
                Location = new Point(S(rx), y), Width = S(width),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(t);
            y += S(26);
            return t;
        }

        Cap("Token (as written in texts, e.g. {ROM}):");
        var name = Field();
        Cap("Source — cmd (exe+args) · arg (each argument, last match wins) · or a FILE path:");
        var source = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(S(rx), y), Width = S(304),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        source.Items.AddRange(new object[] { "cmd", "arg" });
        Controls.Add(source);
        var browseSrc = Btn("File…", rx + 312, 0, 80);
        browseSrc.Top = y;
        browseSrc.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) source.Text = dlg.FileName;
        };
        y += S(28);
        Cap("Regex to match on the source  (ex: core=(\\w+)  ·  (\\w+)\\.zip):");
        var pattern = Field();
        Cap("Value on match (\"\\1\"–\"\\9\" splice the groups  ·  ex: \\1):");
        var value = Field();
        Cap("Fallback when nothing matches:");
        var fallback = Field();

        // ── the sandbox: a test line, every variable's live value ──
        var sandCap = new Label
        {
            Text = "Sandbox — test line (exe included):", AutoSize = true,
            Location = new Point(S(14), S(280)), ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        Controls.Add(sandCap);
        var testLine = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(sampleLine) ? @"emulator.exe -L ""cores\snes.dll"" ""C:\roms\game.zip""" : sampleLine,
            Location = new Point(S(14), S(300)), Width = S(590),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(testLine);
        var resolved = new TextBox
        {
            Location = new Point(S(14), S(330)), Width = S(590), Height = S(76),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = LiteBoxTheme.Bg, ForeColor = LiteBoxTheme.SubFg, BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(resolved);

        void Sandbox()
        {
            try
            {
                var all = RuleArgs.SplitFull(testLine.Text);
                string exe = all.FirstOrDefault() ?? "";
                string args = RuleArgs.Join(all.Skip(1));
                resolved.Text = _vars.Count == 0
                    ? "(no variables yet)"
                    : string.Join("\r\n", _vars.Where(v => v.Name.Length > 0)
                        .Select(v => v.Name + " = " + RuleVariables.ResolveOne(v, exe, args)));
            }
            catch (Exception ex) { resolved.Text = "(invalid: " + ex.Message + ")"; }
        }

        void Reload(int select)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var v in _vars) _list.Items.Add(v.Name.Length > 0 ? v.Name : "(unnamed)");
            _list.EndUpdate();
            if (select >= 0 && select < _vars.Count) _list.SelectedIndex = select;
            Sandbox();
        }
        void Bind()
        {
            if (_binding) return;   // re-entry from Commit's guarded list rewrite — not a user selection
            _binding = true;
            var v = _list.SelectedIndex >= 0 && _list.SelectedIndex < _vars.Count ? _vars[_list.SelectedIndex] : null;
            name.Text = v?.Name ?? ""; source.Text = v?.Source ?? "cmd";
            pattern.Text = v?.Pattern ?? ""; value.Text = v?.Value ?? ""; fallback.Text = v?.Fallback ?? "";
            name.Enabled = source.Enabled = pattern.Enabled = value.Enabled = fallback.Enabled = browseSrc.Enabled = v != null;
            _binding = false;
        }
        void Commit()
        {
            if (_binding || _list.SelectedIndex < 0 || _list.SelectedIndex >= _vars.Count) return;
            int ix = _list.SelectedIndex;
            var v = _vars[ix];
            v.Name = name.Text.Trim(); v.Source = source.Text.Trim();
            v.Pattern = pattern.Text; v.Value = value.Text; v.Fallback = fallback.Text;
            // Rewriting the SELECTED item clears the selection for a beat → SelectedIndexChanged
            // fires with -1 → Bind() disables the fields — and disabling the focused textbox THROWS
            // THE FOCUS to the next control (the sandbox). Guard the rewrite and re-select silently
            // so typing in the token field keeps its caret. Only rewrite when the label changed.
            string label = v.Name.Length > 0 ? v.Name : "(unnamed)";
            if ((string)_list.Items[ix] != label)
            {
                _binding = true;
                _list.Items[ix] = label;
                _list.SelectedIndex = ix;
                _binding = false;
            }
            Sandbox();
        }
        name.TextChanged += (_, _) => Commit();
        source.TextChanged += (_, _) => Commit();
        pattern.TextChanged += (_, _) => Commit();
        value.TextChanged += (_, _) => Commit();
        fallback.TextChanged += (_, _) => Commit();
        testLine.TextChanged += (_, _) => Sandbox();
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
        ok.Location = new Point(S(410), S(414));
        ok.Click += (_, _) =>
        {
            VariablesData = RuleVariables.Serialize(_vars.Where(v => v.Name.Length > 0).ToList());
            DialogResult = DialogResult.OK; Close();
        };
        var cancel = ActionButton("Cancel", MenuIcons.Exit);
        cancel.Location = new Point(S(512), S(414));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(ok); Controls.Add(cancel);

        Reload(_vars.Count > 0 ? 0 : -1);
        Bind();
    }
}
