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
            Text = "Matching launches spawn the emulator ELEVATED (a UAC prompt appears). Use the"
                 + " probes below to scope it — e.g. a marker argument in the game's custom"
                 + " command-line parameters for the few titles that need it (TeknoParrot…).",
            AutoSize = false, Location = new Point(0, S(2)), Size = new Size(S(574), S(52)),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
        });
        body.Controls.Add(new Label
        {
            Text = "WARNING: the whole screen system is disabled for elevated launches — startup"
                 + " screen, FPS detection, pause screen and hotkeys, game-over screen. Windows"
                 + " forbids a normal process from watching or controlling an elevated one."
                 + " Exit detection and play time keep working.",
            AutoSize = false, Location = new Point(0, S(58)), Size = new Size(S(574), S(66)),
            ForeColor = Color.FromArgb(220, 160, 90), BackColor = LiteBoxTheme.Bg,
        });
        int h = S(128);
        body.Height = h;
        return (body, h, () => { });
    }
}
