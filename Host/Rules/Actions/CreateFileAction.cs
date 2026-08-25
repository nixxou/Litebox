// CreateFile — write a file right before the launch (a per-game config, a flag file a script
// watches, a controller profile). The original was File.WriteAllText in a silent catch, path and
// content verbatim; ours adds what it deserved: VARIABLES in both the path and the content
// (Mehdi's ask — "{ROM}.cfg" is the whole point), the missing directory created, the write logged,
// the failure named. Overwrite is unconditional and the file STAYS after the game, both as in the
// original — this is config poking, not state to restore.
//
// A side-effect action: Apply is identity, the work lives in ExecuteBefore — real channel only,
// the preview never writes, and the dialog's sandbox shows the expanded path and content instead.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class CreateFileAction : IRuleAction
{
    public string Type => LaunchRule.TypeCreateFile;
    public string AddLabel => "Add: Create file…";
    public string DialogTitle => "Create file rule";

    public bool IsConfigured(LaunchRule r) => r.TargetFile.Length > 0;

    public string Describe(LaunchRule r)
        => "Create file " + r.TargetFile
           + (r.FileContent.Length > 0 ? $" ({r.FileContent.Length} chars)" : " (empty)");

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => cmd;

    public void ExecuteBefore(LaunchRule r, RuleCmd cmd)
    {
        try
        {
            var vars = RuleVariables.Parse(r.VariablesData);
            string file = RuleVariables.Expand(r.TargetFile, vars, cmd.Exe, cmd.Args);
            string content = RuleVariables.Expand(r.FileContent, vars, cmd.Exe, cmd.Args);
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(file, content);
            LbLog.Info("rules", $"CreateFile: \"{file}\" written ({content.Length} chars)");
        }
        catch (Exception ex) { LbLog.Warn("rules", $"CreateFile failed ({ex.Message}) — nothing written"); }
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
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
                AcceptsReturn = lines > 1,
            };
            body.Controls.Add(t);
            y += (lines > 1 ? S(14 * lines + 8) : S(23)) + S(7);
            return t;
        }

        Cap("File to write (variables allowed — ex: C:\\emu\\config\\{ROM}.cfg; overwritten, kept after the game):");
        var file = Field(r.TargetFile, 486);
        var browse = new Button
        {
            Text = "Browse…", Location = new Point(S(492), file.Top - S(1)), Size = new Size(S(82), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        browse.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "All files (*.*)|*.*", OverwritePrompt = false };
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) file.Text = dlg.FileName;
        };
        body.Controls.Add(browse);

        Cap("Content (variables allowed):");
        var content = Field(r.FileContent, lines: 5);

        var manage = new Button
        {
            Text = "Manage variables…", Location = new Point(0, y), Size = new Size(S(150), S(26)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        manage.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        string variablesData = r.VariablesData;
        body.Controls.Add(manage);
        y += S(32);

        // ── the sandbox: the expanded path and content for a test line — written nowhere ──
        Cap("Sandbox — test line (exe included; feeds the variables):");
        var testLine = Field(@"emulator.exe -L ""cores\snes.dll"" ""C:\roms\game.zip""");
        Cap("Would write:");
        var result = Field("", lines: 5, readOnly: true);

        void Recalc()
        {
            try
            {
                var vars = RuleVariables.Parse(variablesData);
                var all = RuleArgs.SplitFull(testLine.Text);
                string exe = all.FirstOrDefault() ?? "";
                string args = RuleArgs.Join(all.Skip(1));
                string path = RuleVariables.Expand(file.Text, vars, exe, args);
                string body2 = RuleVariables.Expand(content.Text, vars, exe, args);
                result.Text = "→ " + path + "\r\n" + body2;
            }
            catch (Exception ex) { result.Text = "(invalid: " + ex.Message + ")"; }
        }
        file.TextChanged += (_, _) => Recalc();
        content.TextChanged += (_, _) => Recalc();
        testLine.TextChanged += (_, _) => Recalc();
        manage.Click += (_, _) =>
        {
            using var dlg = new VariablesDialog(variablesData, dpiS, testLine.Text);
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) { variablesData = dlg.VariablesData; Recalc(); }
        };
        Recalc();

        body.Height = y;
        return (body, y, () =>
        {
            r.TargetFile = file.Text.Trim();
            r.FileContent = content.Text;
            r.VariablesData = variablesData;
        });
    }
}
