// The shared "Launch Rules" editor page — one control for the three attachment points (Edit Emulator
// section, Edit Game page, Edit Additional Version tab), like MonitorAssignPanel is for profiles.
// The list is an edit buffer: nothing touches the store until the window's own apply runs.
//
// V1 offers ONE action type (Prefix — see Host\Rules\LaunchRules.cs for why), but the page is the
// family's: ordered list, Add split by type, Edit/Remove/Up/Down. Each new ported action adds a menu
// entry and a dialog, nothing else.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Options;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules;

internal static class LaunchRulesPanel
{
    public static (Control panel, Action apply) Build(string scope, string entityId, float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = ModulePanelKit.Root(dpiS);
        int y = S(8);
        void Row(Control c, int h, int indent = 0) { c.Location = new Point(S(4 + indent), y); root.Controls.Add(c); y += h; }

        var rules = LaunchRuleStore.Get(scope, entityId);

        string level = scope == Data.LiteBoxOption.ScopeVersion ? "version"
                     : scope == Data.LiteBoxOption.ScopeGame ? "game" : "emulator";
        Row(ModulePanelKit.Caption(
            $"Rules run IN ORDER against the command line right before the game spawns, each seeing the "
            + $"previous one's result. The most specific level with rules wins whole: version, then game, "
            + $"then emulator — rules here {(level == "emulator" ? "apply unless the game or version has its own" : $"replace the {(level == "version" ? "game's and emulator's" : "emulator's")}")}.",
            dpiS, 620), S(48));

        // ── the live preview — BigBoxProfile's "Exemple Command IN / Emulator Command OUT" pair.
        // The IN line is free text (exe included) so any scenario can be tried, markers included;
        // the OUT recomputes on every keystroke and every change to the list, through each rule's
        // EXAMPLE treatment (see RulePipeline.PreviewExample) plus the final marker pass.
        Row(ModulePanelKit.Caption("Example command IN:", dpiS, 620), S(18));
        var exIn = new TextBox
        {
            Text = DefaultExampleIn(scope, entityId), Width = S(560),
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
            Enabled = !readOnly,
        };
        Row(exIn, S(28));
        Row(ModulePanelKit.Caption("Command OUT:", dpiS, 620), S(18));
        var exOut = new TextBox
        {
            ReadOnly = true, Width = S(560),
            BackColor = ModulePanelKit.Bg, ForeColor = ModulePanelKit.Sub, BorderStyle = BorderStyle.FixedSingle,
        };
        Row(exOut, S(28));

        var list = new ListBox
        {
            Width = S(560), Height = S(150),
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg,
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
        };
        void Recalc()
        {
            try { exOut.Text = RulePipeline.PreviewExample(rules, exIn.Text); }
            catch { exOut.Text = exIn.Text; }
        }
        void Reload(int select = -1)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rules) list.Items.Add(r.Describe());
            list.EndUpdate();
            if (select >= 0 && select < list.Items.Count) list.SelectedIndex = select;
            Recalc();
        }
        exIn.TextChanged += (_, _) => Recalc();
        Reload(rules.Count > 0 ? 0 : -1);
        Row(list, S(156));

        var bar = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ModulePanelKit.Bg,
        };
        Button Btn(string t)
        {
            var b = new Button
            {
                Text = t, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(S(86), S(26)), Padding = new Padding(S(6), S(2), S(6), S(2)),
                Margin = new Padding(0, 0, S(6), 0), BackColor = ModulePanelKit.Field,
                ForeColor = ModulePanelKit.Fg, FlatStyle = FlatStyle.Flat, Enabled = !readOnly,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            bar.Controls.Add(b);
            return b;
        }

        var add = Btn("Add: Prefix…");
        var edit = Btn("Edit…");
        var remove = Btn("Remove");
        var up = Btn("▲");
        var down = Btn("▼");
        Row(bar, S(36));

        LaunchRule? Current() => list.SelectedIndex >= 0 && list.SelectedIndex < rules.Count
            ? rules[list.SelectedIndex] : null;

        void EditRule(LaunchRule rule, bool isNew)
        {
            using var dlg = new PrefixRuleDialog(rule, dpiS);
            if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
            if (isNew) rules.Add(rule);
            Reload(isNew ? rules.Count - 1 : list.SelectedIndex);
        }

        add.Click += (_, _) => EditRule(new LaunchRule { Type = LaunchRule.TypePrefix }, isNew: true);
        edit.Click += (_, _) => { if (Current() is { } r) EditRule(r, isNew: false); };
        if (!readOnly) list.DoubleClick += (_, _) => { if (Current() is { } r) EditRule(r, isNew: false); };
        remove.Click += (_, _) =>
        {
            int ix = list.SelectedIndex;
            if (ix < 0 || ix >= rules.Count) return;
            rules.RemoveAt(ix);
            Reload(Math.Min(ix, rules.Count - 1));
        };
        void Move(int delta)
        {
            int ix = list.SelectedIndex;
            int to = ix + delta;
            if (ix < 0 || ix >= rules.Count || to < 0 || to >= rules.Count) return;
            (rules[ix], rules[to]) = (rules[to], rules[ix]);
            Reload(to);
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        return (root, () =>
        {
            if (readOnly) return;
            try { LaunchRuleStore.Set(scope, entityId, rules); } catch { }
        });
    }

    /// <summary>The preview's default IN line — BigBoxProfile seeded it with the emulator's exe name
    /// plus a sample rom path. The entity's real exe when it can be found cheaply, a placeholder
    /// otherwise; free text either way.</summary>
    private static string DefaultExampleIn(string scope, string entityId)
    {
        string exe = "emulator.exe";
        try
        {
            if (scope == Data.LiteBoxOption.ScopeEmulator)
            {
                var e = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllEmulators()
                    ?.FirstOrDefault(x => string.Equals(x.Id, entityId, StringComparison.OrdinalIgnoreCase));
                var p = e?.ApplicationPath;
                if (!string.IsNullOrWhiteSpace(p)) exe = System.IO.Path.GetFileName(p);
            }
        }
        catch { }
        return exe + " \"C:\\MyRomDir\\MyRom.bin\"";
    }
    /// <summary>The Prefix editor — grouped, with the shared probe blocks (mode dropdowns + Manage…).
    /// Same stored shape as BigBoxProfile's Prefix_Config; only the surface changed.</summary>
    private sealed class PrefixRuleDialog : LiteBoxForm
    {
        public PrefixRuleDialog(LaunchRule rule, float dpiS)
        {
            Text = "Prefix rule";
            ClientSize = new Size(S(462), S(496));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            int y = S(12);

            var enabled = new CheckBox
            {
                Text = "Rule enabled", Checked = rule.Enabled, AutoSize = true,
                Location = new Point(S(16), y), ForeColor = LiteBoxTheme.Fg, BackColor = BackColor,
            };
            Controls.Add(enabled);
            y += S(30);

            // ── Action ──
            var actBox = new GroupBox
            {
                Text = "Action", ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
                Location = new Point(S(16), y), Width = S(430), Height = S(112),
            };
            var capPrefix = new Label
            {
                Text = "Prefix to add:", AutoSize = true, Location = new Point(S(12), S(22)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            var prefix = new TextBox
            {
                Text = rule.Prefix, Location = new Point(S(12), S(42)), Width = S(404),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            var capAs = new Label
            {
                Text = "Add it as:", AutoSize = true, Location = new Point(S(12), S(78)),
                ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            };
            var asMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(84), S(74)), Width = S(332),
                BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
            };
            asMode.Items.AddRange(new object[]
            {
                "One argument (the text becomes a single token)",
                "Raw command-line text (may carry several arguments)",
            });
            asMode.SelectedIndex = rule.AsArg ? 0 : 1;
            actBox.Controls.AddRange(new Control[] { capPrefix, prefix, capAs, asMode });
            Controls.Add(actBox);
            y += S(122);

            // ── the shared probe blocks ──
            var when = new ProbeBlock("Run only when…", exclude: false, withMarker: true, dpiS, readOnly: false);
            when.Box.Location = new Point(S(16), y);
            when.Load(rule.Filter, rule.CommaFilter, rule.MatchAllFilter, rule.RemoveFilter);
            Controls.Add(when.Box);
            y += when.Box.Height + S(10);

            var never = new ProbeBlock("Never when…", exclude: true, withMarker: false, dpiS, readOnly: false);
            never.Box.Location = new Point(S(16), y);
            never.Load(rule.Exclude, rule.CommaExclude, rule.MatchAllExclude);
            Controls.Add(never.Box);
            y += never.Box.Height + S(14);

            var ok = ActionButton("OK", MenuIcons.Add);
            ok.Location = new Point(S(252), y);
            ok.Click += (_, _) =>
            {
                // An empty prefix is allowed — the rule saves NON-CONFIGURED and is skipped at launch,
                // BigBoxProfile's add-now-configure-later workflow.
                rule.Enabled = enabled.Checked;
                bool asArg = asMode.SelectedIndex == 0;
                // As ARGUMENT the token is trimmed (BigBoxProfile trims too); as CMDLINE the text is
                // kept verbatim — a trailing space before the rest of the line is often the point.
                rule.Prefix = asArg ? prefix.Text.Trim() : prefix.Text;
                rule.AsArg = asArg;
                (rule.Filter, rule.CommaFilter, rule.MatchAllFilter, rule.RemoveFilter) = when.Save();
                (rule.Exclude, rule.CommaExclude, rule.MatchAllExclude, _) = never.Save();
                DialogResult = DialogResult.OK; Close();
            };
            var cancel = ActionButton("Cancel", MenuIcons.Exit);
            cancel.Location = new Point(S(354), y);
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(ok); Controls.Add(cancel);
            ClientSize = new Size(S(462), y + S(40));
            ThemedCheckBox.StyleAll(this);
        }
    }
}
