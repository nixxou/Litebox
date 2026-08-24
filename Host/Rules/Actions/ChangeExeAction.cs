// ChangeExe — swap the executable itself. A ROOTED new path replaces the exe outright; a RELATIVE
// one resolves against the ORIGINAL exe's directory, which is the original's clever default: point
// an emulator at its sibling build ("retroarch_debug.exe") without knowing where it is installed.
//
// BigBoxProfile also set EmulatorLauncher.WorkingDirExe to the new exe's folder; our Spawn already
// defaults the working directory to the exe it is given, so retargeting the exe carries the working
// directory for free — the reason RuleCmd carries the Exe at all.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class ChangeExeAction : IRuleAction
{
    public string Type => LaunchRule.TypeChangeExe;
    public string AddLabel => "Add: Change exe…";
    public string DialogTitle => "Change exe rule";

    public bool IsConfigured(LaunchRule r) => r.NewExe.Length > 0;

    public string Describe(LaunchRule r) => "Change Exe to " + r.NewExe;

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
    {
        try
        {
            string exe = Path.IsPathRooted(r.NewExe)
                ? r.NewExe
                : Path.Combine(Path.GetDirectoryName(cmd.Exe) ?? "", r.NewExe);
            return cmd with { Exe = exe };
        }
        catch { return cmd; }
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { Size = new Size(S(576), S(76)), BackColor = LiteBoxTheme.Bg };

        var cap = new Label
        {
            Text = "New executable (absolute, or relative to the ORIGINAL exe's folder):",
            AutoSize = true, Location = new Point(0, S(2)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        var text = new TextBox
        {
            Text = r.NewExe, Location = new Point(0, S(22)), Width = S(486),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        var browse = new Button
        {
            Text = "Browse…", Location = new Point(S(492), S(20)), Size = new Size(S(82), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) text.Text = dlg.FileName;
        };
        var hint = new Label
        {
            Text = "The working directory follows the new exe's folder, as BigBoxProfile did.",
            AutoSize = true, Location = new Point(0, S(52)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.AddRange(new Control[] { cap, text, browse, hint });

        return (body, S(76), () => r.NewExe = text.Text.Trim());
    }
}
