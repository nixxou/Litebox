// Run commands as administrator — BigBoxProfile's ExecutePrePostCmdAsAdmin on OUR rails: the
// original kept a "|||" command list, a single before/after switch, and ran each command through a
// per-command scheduled task it registered on the fly; ours runs them through the ONE
// LiteBox_AdminLaunch no-UAC bridge (fallback: per-command UAC runas when the task is not
// installed). Two additions the original lacked: an optional WAIT per command (a mount or a
// driver load must finish before the emulator starts — BBP fired and forgot with a 100 ms nap),
// and the rule-variables expansion every other action already enjoys.
//
// Only the COMPANION programs are elevated — the emulator spawn stays normal, so the whole screen
// system (startup cover, SmartCapture, pause, game-over) keeps working. That is this rule's whole
// advantage over "Run as administrator".

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class AdminCmdAction : IRuleAction
{
    private const string Tag = "rules";

    public string Type => LaunchRule.TypeAdminCmd;
    public string AddLabel => "Add: Run commands as admin…";
    public string DialogTitle => "Run commands as admin rule";

    public bool IsConfigured(LaunchRule r) => SplitCmds(r.AdminCmds).Count > 0;

    public string Describe(LaunchRule r)
    {
        int n = SplitCmds(r.AdminCmds).Count;
        return $"Run {n} command{(n > 1 ? "s" : "")} as ADMIN {(r.AdminOnStart ? "before launch" : "at exit")}"
             + (r.AdminWait ? " (waited)" : "");
    }

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => cmd;   // the line is never touched

    public void ExecuteBefore(LaunchRule r, RuleCmd cmd)
    {
        var commands = ParseCommands(r, cmd.Exe, cmd.Args);
        if (commands.Count == 0) return;
        if (r.AdminOnStart) RunAll(commands, r.AdminWait, "before");
        else RulePipeline.RegisterAfterLaunch(() => RunAll(commands, r.AdminWait, "exit"));
    }

    /// <summary>The command list, variables expanded against the CURRENT line, split into
    /// (exe, args) pairs. Pure — the selftests pin it without elevating anything.</summary>
    internal static List<(string Exe, string Args)> ParseCommands(LaunchRule r, string exe, string args)
    {
        var result = new List<(string, string)>();
        var vars = RuleVariables.Parse(r.VariablesData);
        foreach (var raw in SplitCmds(r.AdminCmds))
        {
            string expanded = RuleVariables.Expand(raw, vars, exe, args);
            var parts = RuleArgs.SplitFull(expanded);
            if (parts.Length == 0) continue;
            result.Add((parts[0], RuleArgs.Join(parts.Skip(1))));
        }
        return result;
    }

    private static List<string> SplitCmds(string joined)
        => joined.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    private static void RunAll(List<(string Exe, string Args)> commands, bool wait, string slot)
    {
        foreach (var (exe, args) in commands)
        {
            try
            {
                int pid = AdminLaunch.SpawnViaTask(exe, args, SafeDir(exe), hideConsole: false);
                if (pid > 0)
                {
                    LbLog.Info(Tag, $"AdminCmd ({slot}): \"{exe}\" {args} → pid {pid}" + (wait ? " (waiting)" : ""));
                    if (wait) AdminLaunch.WaitForPid(pid);
                    else System.Threading.Thread.Sleep(100);   // the original's spacing
                    continue;
                }
                // Bridge unavailable → per-command UAC runas (works, prompts).
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                { UseShellExecute = true, Verb = "runas", WorkingDirectory = SafeDir(exe) ?? "" };
                using var p = System.Diagnostics.Process.Start(psi);
                LbLog.Info(Tag, $"AdminCmd ({slot}): \"{exe}\" {args} via UAC runas");
                if (wait) p?.WaitForExit();
            }
            catch (Exception ex) { LbLog.Warn(Tag, $"AdminCmd ({slot}): \"{exe}\" failed ({ex.Message})"); }
        }
    }

    private static string? SafeDir(string exe)
    {
        try { var d = Path.GetDirectoryName(exe); return string.IsNullOrEmpty(d) ? null : d; }
        catch { return null; }
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
        int y = 0;

        body.Controls.Add(new Label
        {
            Text = "Runs each command ELEVATED (through the no-UAC task) — the EMULATOR itself stays"
                 + " normal, so the whole screen system keeps working. One command per entry"
                 + " (\"|||\"-separated, Manage…); variables allowed. Typical use: a driver/service"
                 + " companion, a mount, an elevated config write.",
            AutoSize = false, Location = new Point(0, S(2)), Size = new Size(S(574), S(52)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });
        y += S(56);

        var cmds = new TextBox
        {
            Text = r.AdminCmds, Location = new Point(0, y), Width = S(486),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(cmds);
        var manage = new Button
        {
            Text = "Manage…", Location = new Point(S(492), y - S(1)), Size = new Size(S(82), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        manage.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        manage.Click += (_, _) =>
        {
            using var dlg = new ManageItemsDialog(cmds.Text, dpiS, "|||");
            if (dlg.ShowDialog(body.FindForm()) == DialogResult.OK) cmds.Text = dlg.Value;
        };
        body.Controls.Add(manage);
        y += S(32);

        var onStart = new RadioButton
        {
            Text = "Run BEFORE the emulator starts", Checked = r.AdminOnStart, AutoSize = true,
            Location = new Point(0, y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        var onExit = new RadioButton
        {
            Text = "Run at game EXIT", Checked = !r.AdminOnStart, AutoSize = true,
            Location = new Point(S(250), y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(onStart); body.Controls.Add(onExit);
        y += S(26);

        var wait = new CheckBox
        {
            Text = "Wait for each command to finish (a mount/driver must be ready before the game)",
            Checked = r.AdminWait, AutoSize = true,
            Location = new Point(0, y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(wait);
        y += S(30);

        AdminTaskRow.Build(body, 0, y, dpiS);
        y += S(32);

        body.Height = y;
        return (body, y, () =>
        {
            r.AdminCmds = cmds.Text.Trim();
            r.AdminOnStart = onStart.Checked;
            r.AdminWait = wait.Checked;
        });
    }
}
