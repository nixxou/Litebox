// MonitorProfile — a launch rule that picks the monitor profile, with the same three answers the
// emulator page offers: an explicit "none", a saved profile, or inline custom settings (the SAME
// editor, reused), plus a per-rule NVAPI switch that opens the NVIDIA-writes gate for this launch
// even when the module's global toggle is off (and for the exit restore — see GpuColor.ScopeForce).
//
// PRIORITY (Mehdi's spec): the rule sits ABOVE every stored assignment — version, game, emulator —
// and BELOW the manual "run the next game as…" one-shot. A fired rule is configuration; the
// one-shot is someone at the machine right now.
//
// TIMING is the unusual part: the profile applies BEFORE the command line is even built (the
// desktop must settle before autoruns and the emulator start), while rules probe the BUILT line.
// So the launch pre-evaluates this rule on the PREVIEW walk — the same PreviewWithTrace the page
// preview runs, over the side-effect-free constructed line — and hands the last fired rule's
// decision to MonitorAssign.Resolve (see EvaluateRules below and HostServices). In the real
// pipeline walk that happens later, this action is deliberately a NO-OP: its effect was consumed
// pre-spawn, and the line itself is never touched.
//
// The rule is inert while the Monitor Profiles module is off — evaluation gates on it, the dialog
// and the description say so.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Monitors;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class MonitorProfileAction : IRuleAction
{
    public string Type => LaunchRule.TypeMonitorProfile;
    public string AddLabel => "Add: Monitor profile…";
    public string DialogTitle => "Monitor profile rule";

    public bool IsConfigured(LaunchRule r) => r.MonitorAssignData.Length > 0;

    public string Describe(LaunchRule r)
    {
        string what = r.MonitorAssignData.Equals("none", StringComparison.OrdinalIgnoreCase) ? "no monitor profile"
                    : r.MonitorAssignData.Equals("custom", StringComparison.OrdinalIgnoreCase) ? "custom settings"
                    : "profile " + (MonitorProfileStore.ById(r.MonitorAssignData)?.Name ?? $"<deleted {r.MonitorAssignData}>");
        return "Monitor profile: " + what
             + (r.MonitorNvapi ? " [NVAPI on]" : "")
             + (LbModules.On(LbModule.Monitors) ? "" : " (module OFF — inert)");
    }

    // The line is never touched; the decision is consumed pre-spawn (see the header).
    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => cmd;

    /// <summary>The launch-side evaluation: walks the rules on the PREVIEW channel over the built
    /// line and returns the LAST fired MonitorProfile rule's decision — Unset when none fired, the
    /// Monitors module is off, or there is nothing to say. Pure over its inputs (selftested).</summary>
    /// <summary>Selftests only: the module gate reads the options DB, absent in the harness.</summary>
    internal static bool TestBypassModuleGate;

    public static (Assignment Assign, MonitorProfile? Custom, bool Nvapi) EvaluateRules(
        List<LaunchRule> rules, string fullCommandLine)
    {
        if (!TestBypassModuleGate && !LbModules.On(LbModule.Monitors)) return (Assignment.Unset, null, false);
        if (!rules.Any(r => r.Type == LaunchRule.TypeMonitorProfile)) return (Assignment.Unset, null, false);

        RulePipeline.PreviewWithTrace(rules, fullCommandLine, out var trace);
        LaunchRule? winner = null;
        for (int i = 0; i < rules.Count && i < trace.Count; i++)
            if (rules[i].Type == LaunchRule.TypeMonitorProfile
                && trace[i].State == RulePipeline.TraceState.Fired)
                winner = rules[i];   // last fired wins — it supersedes, like everything downstream
        if (winner == null) return (Assignment.Unset, null, false);

        string raw = winner.MonitorAssignData;
        Assignment assign = raw.Equals("none", StringComparison.OrdinalIgnoreCase) ? new Assignment(AssignKind.None, "")
                          : raw.Equals("custom", StringComparison.OrdinalIgnoreCase) ? new Assignment(AssignKind.Custom, "")
                          : new Assignment(AssignKind.Profile, raw);
        MonitorProfile? custom = null;
        if (assign.Kind == AssignKind.Custom && !string.IsNullOrWhiteSpace(winner.MonitorCustomData))
            try { custom = JsonSerializer.Deserialize<MonitorProfile>(winner.MonitorCustomData); } catch { }
        return (assign, custom, winner.MonitorNvapi);
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    private const string NoProfile = "Do not use a monitor profile";
    private const string CustomItem = "Custom settings for this rule…";

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var body = new Panel { BackColor = LiteBoxTheme.Bg, Width = S(576) };
        int y = 0;

        Label Cap(string t, int lines = 1, Color? color = null)
        {
            var l = new Label
            {
                Text = t, AutoSize = false, Location = new Point(0, y + S(2)),
                Size = new Size(S(574), S(2 + 18 * lines)),
                ForeColor = color ?? LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            body.Controls.Add(l);
            y += S(6 + 18 * lines);
            return l;
        }

        Cap("Chooses the monitor profile for this launch. A fired rule SUPERSEDES the stored"
            + " assignments (version, game, emulator) but stays below a manual \"run the next game"
            + " as…\" one-shot. Applied before the emulator starts, restored when the game exits.", 3);
        if (!LbModules.On(LbModule.Monitors))
            Cap("The Monitor Profiles module is OFF — this rule is inert until it is enabled.",
                1, Color.FromArgb(220, 160, 90));

        var profiles = MonitorProfileStore.All();
        // Same contract as everywhere this form appears: a deleted profile keeps its (dangling)
        // selection visible instead of snapping to "Do not use" and rewriting the rule on save.
        if (r.MonitorAssignData.Length > 0
            && !r.MonitorAssignData.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !r.MonitorAssignData.Equals("custom", StringComparison.OrdinalIgnoreCase)
            && !profiles.Any(p => string.Equals(p.Id, r.MonitorAssignData, StringComparison.OrdinalIgnoreCase)))
            profiles.Add(new MonitorProfile { Id = r.MonitorAssignData, Name = "<deleted profile>  (missing)" });
        var combo = new ComboBox
        {
            Location = new Point(0, y), Width = S(420), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        combo.Items.Add(NoProfile);
        foreach (var p in profiles) combo.Items.Add(p.Name);
        combo.Items.Add(CustomItem);
        combo.SelectedIndex =
            r.MonitorAssignData.Equals("custom", StringComparison.OrdinalIgnoreCase) ? combo.Items.Count - 1
            : r.MonitorAssignData.Length > 0 && !r.MonitorAssignData.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, profiles.FindIndex(p => p.Id == r.MonitorAssignData) + 1)
            : 0;
        body.Controls.Add(combo);
        y += S(30);

        var nvapi = new CheckBox
        {
            Text = "Apply the NVIDIA (NVAPI) settings for this launch even if the global toggle is off",
            Checked = r.MonitorNvapi, AutoSize = true,
            Location = new Point(0, y), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
        };
        body.Controls.Add(nvapi);
        y += S(28);

        // The inline custom configuration — the emulator page's own editor, whole.
        MonitorProfile customProfile;
        try { customProfile = JsonSerializer.Deserialize<MonitorProfile>(r.MonitorCustomData) ?? new MonitorProfile { Name = "Custom" }; }
        catch { customProfile = new MonitorProfile { Name = "Custom" }; }
        // Two columns — the single-column page layout would push this dialog past the screen.
        var (customBox, applyCustom) = MonitorAssignPanel.BuildCustom(customProfile, dpiS, readOnly: false, twoColumns: true);
        customBox.Location = new Point(0, y);
        body.Controls.Add(customBox);
        int customY = y;

        void Sync()
        {
            bool showCustom = combo.SelectedIndex == combo.Items.Count - 1;
            customBox.Visible = showCustom;
            body.Height = showCustom ? customY + customBox.Height + S(8) : customY + S(4);
        }
        combo.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        return (body, body.Height, () =>
        {
            int ix = combo.SelectedIndex;
            if (ix == combo.Items.Count - 1)
            {
                applyCustom();   // controls → the profile object
                r.MonitorAssignData = "custom";
                r.MonitorCustomData = JsonSerializer.Serialize(customProfile);
            }
            else if (ix <= 0) r.MonitorAssignData = "none";
            else r.MonitorAssignData = profiles[ix - 1].Id;
            r.MonitorNvapi = nvapi.Checked;
        });
    }
}
