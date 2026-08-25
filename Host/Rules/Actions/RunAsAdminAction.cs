// Run as administrator — the emulator-level twin of the per-game "Run as ADMINISTRATOR" checkbox
// (Edit Game ▸ Launching ▸ Startup/Pause): a fired rule makes the launch spawn ELEVATED through
// UAC (ShellExecute "runas"), and the whole screen system goes dark for that launch — startup
// cover, SmartCapture FPS detection, pause screen and its hotkeys, game-over screen. That is a
// WINDOWS wall, not a choice: a medium-IL process cannot capture (WGC CreateForWindow), hook
// (UIPI eats WH_KEYBOARD_LL while an elevated window has focus) or suspend (PROCESS_SUSPEND_RESUME
// denied) a high-IL one — and half-working overlays would be worse than none (Mehdi's call).
// Exit detection and play time survive (SYNCHRONIZE is granted).
//
// Like MonitorProfile, the decision is needed BEFORE the line exists (the screens arm before the
// spawn), so it is pre-evaluated on the PREVIEW walk — the branching bus works (a marker argument
// in one game's custom parameters can route this rule). The action itself never touches the line.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class RunAsAdminAction : IRuleAction
{
    public string Type => LaunchRule.TypeRunAsAdmin;
    public string AddLabel => "Add: Run as administrator…";
    public string DialogTitle => "Run as administrator rule";

    public bool IsConfigured(LaunchRule r) => true;   // the rule IS the configuration

    public string Describe(LaunchRule r) => "Run the emulator as ADMINISTRATOR (screens disabled)";

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => cmd;

    /// <summary>The launch-side pre-evaluation: true when any enabled RunAsAdmin rule FIRES on the
    /// preview walk over the built line. Pure over its inputs (selftested).</summary>
    public static bool EvaluateRules(List<LaunchRule> rules, string fullCommandLine)
    {
        if (!rules.Any(r => r.Type == LaunchRule.TypeRunAsAdmin)) return false;
        RulePipeline.PreviewWithTrace(rules, fullCommandLine, out var trace);
        for (int i = 0; i < rules.Count && i < trace.Count; i++)
            if (rules[i].Type == LaunchRule.TypeRunAsAdmin
                && trace[i].State == RulePipeline.TraceState.Fired)
                return true;
        return false;
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
        body.Controls.Add(new Label
        {
            Text = "Matching launches spawn the emulator ELEVATED. Use the probes below to scope it"
                 + " — e.g. a marker argument in the game's custom command-line parameters for the"
                 + " few titles that need it (TeknoParrot…).",
            AutoSize = false, Location = new Point(0, S(2)), Size = new Size(S(574), S(38)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });
        body.Controls.Add(new Label
        {
            Text = "WARNING: the whole screen system is disabled for elevated launches — startup"
                 + " screen, FPS detection, pause screen and hotkeys, game-over screen. Windows"
                 + " forbids a normal process from watching or controlling an elevated one."
                 + " Exit detection and play time keep working.",
            AutoSize = false, Location = new Point(0, S(44)), Size = new Size(S(574), S(66)),
            ForeColor = Color.FromArgb(220, 160, 90), BackColor = LiteBoxTheme.Bg,
        });
        AdminTaskRow.Build(body, 0, S(114), dpiS);
        int h = S(148);
        body.Height = h;
        return (body, h, () => { });
    }
}

/// <summary>The shared "no-UAC elevated task" status + Install/Remove row — used by the rule dialog
/// and the per-game Launching page. One UAC at install; without the task, launches fall back to a
/// per-launch UAC prompt.</summary>
internal static class AdminTaskRow
{
    public static void Build(Control parent, int x, int y, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var status = new Label
        {
            AutoSize = true, Location = new Point(x, y + S(5)),
            BackColor = parent.BackColor,
        };
        var install = new Button
        {
            Location = new Point(x + S(320), y), Size = new Size(S(200), S(25)),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        install.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        void Sync()
        {
            bool on = AdminLaunch.IsTaskInstalled();
            status.Text = on ? "No-UAC task: installed (silent elevated launches)"
                             : "No-UAC task: NOT installed — each launch will UAC-prompt";
            status.ForeColor = on ? Color.FromArgb(120, 190, 120) : Color.FromArgb(220, 160, 90);
            install.Text = on ? "Remove elevated task…" : "Install elevated task… (one UAC)";
        }
        install.Click += (_, _) =>
        {
            var form = parent.FindForm();
            if (form != null) form.Cursor = Cursors.WaitCursor;
            try
            {
                if (AdminLaunch.IsTaskInstalled()) AdminLaunch.UninstallTask();
                else AdminLaunch.InstallTask();
            }
            finally { if (form != null) form.Cursor = Cursors.Default; Sync(); }
        };
        Sync();
        parent.Controls.Add(status);
        parent.Controls.Add(install);
    }
}
