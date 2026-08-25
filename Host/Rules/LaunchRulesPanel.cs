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
            Text = DefaultExampleIn(scope, entityId), Width = S(470), Location = new Point(0, 0),
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
            Enabled = !readOnly,
        };
        // Another random game of this emulator, through the same real-launch construction. Only the
        // emulator scope has anything to re-roll — a game or version IS its own example.
        var reroll = new Button
        {
            Text = "New example", Location = new Point(S(478), 0), Size = new Size(S(82), S(25)),
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, FlatStyle = FlatStyle.Flat,
            Enabled = !readOnly && scope == Data.LiteBoxOption.ScopeEmulator,
        };
        reroll.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
        reroll.Click += (_, _) => exIn.Text = DefaultExampleIn(scope, entityId);
        var inRow = new Panel { Size = new Size(S(562), S(27)), BackColor = ModulePanelKit.Bg };
        inRow.Controls.Add(exIn);
        inRow.Controls.Add(reroll);
        Row(inRow, S(30));
        Row(ModulePanelKit.Caption("Command OUT:", dpiS, 620), S(18));
        var exOut = new TextBox
        {
            ReadOnly = true, Width = S(560),
            BackColor = ModulePanelKit.Bg, ForeColor = ModulePanelKit.Sub, BorderStyle = BorderStyle.FixedSingle,
        };
        Row(exOut, S(28));

        // ── the two faces of the same flat list. The group view is a DERIVED, display-only lens
        // (spec: launch-rules groups design) — folding is opt-in per rule (the Group anchor box),
        // nesting is EVERY-refinement with the delta shown, and the flat ORDER stays the truth: the
        // tree is built by walking the list with a stack, so reading top-to-bottom is still reading
        // the execution. Editing always addresses the underlying flat index (rule nodes carry it).
        var groupView = new CheckBox
        {
            Text = "Group view (display only — the flat list is what runs, in its order)",
            AutoSize = true, Checked = true,
            ForeColor = ModulePanelKit.Sub, BackColor = ModulePanelKit.Bg,
        };
        Row(groupView, S(24));

        var list = new ListBox
        {
            Width = S(560), Height = S(150),
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg,
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
        };
        var tree = SlimScrollTreeHost.NewTree();
        tree.BackColor = ModulePanelKit.Field;
        tree.ForeColor = ModulePanelKit.Fg;
        tree.BorderStyle = BorderStyle.FixedSingle;
        tree.ShowLines = false;
        // No expand glyphs: their bottom lands one indent short and reads as a broken hierarchy
        // (Mehdi). The accent colour alone says "group" — and a group folds on double-click, the
        // TreeView's glyphless default. (An earlier cut cancelled BeforeCollapse, burying the
        // function with the glyph; only the glyph was ever the problem.)
        tree.ShowPlusMinus = false;
        tree.ShowRootLines = false;
        tree.FullRowSelect = true;
        tree.HideSelection = false;
        var treeHost = new SlimScrollTreeHost(tree) { Width = S(560), Height = S(150) };

        // The ONE per-rule indicator, BigBoxProfile's own: red = nothing configured. A per-rule
        // "would it fire for the example line" trace was tried and DROPPED (Mehdi): future actions
        // will hinge on AHK scripts, hardware, files — things no example line can simulate — and an
        // indicator that is right only most of the time misleads with confidence. The preview's
        // global OUT is the only behavioural feedback, exactly like the original.
        Color RuleColor(int i)
            => i >= 0 && i < rules.Count && !rules[i].IsConfigured ? LiteBoxTheme.Danger : ModulePanelKit.Fg;

        void RebuildTree(int selectIndex)
        {
            // The fold state belongs to the user, the rebuild does not: collapsed headers are
            // remembered by their full path and re-collapsed after the walk re-creates them.
            var collapsed = new HashSet<string>();
            void Collect(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag == null && !n.IsExpanded && n.Nodes.Count > 0) collapsed.Add(n.FullPath);
                    Collect(n.Nodes);
                }
            }
            Collect(tree.Nodes);

            tree.BeginUpdate();
            tree.Nodes.Clear();
            // The stack holds the open group chain (signature + its node). A rule whose signature
            // equals the top joins it; one that strictly refines it opens a child; anything else
            // (other signature, or no anchor) closes back to where it fits. Contiguity is the rule:
            // scattered same-signature runs become separate group instances — never wrong, the view
            // simply mirrors the order that will execute.
            var stack = new List<(ProbeSignature Sig, TreeNode Node)>();
            TreeNode? selected = null;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                var sig = ProbeSignature.Of(r);
                if (sig == null)
                {
                    stack.Clear();
                    var n0 = new TreeNode(r.Describe()) { Tag = i, ForeColor = RuleColor(i) };
                    tree.Nodes.Add(n0);
                    if (i == selectIndex) selected = n0;
                    continue;
                }
                while (stack.Count > 0 && !sig.Equals(stack[^1].Sig) && !sig.Refines(stack[^1].Sig))
                    stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0 || !sig.Equals(stack[^1].Sig))
                {
                    var parent = stack.Count > 0 ? stack[^1] : ((ProbeSignature, TreeNode)?)null;
                    var header = new TreeNode(sig.Label(parent?.Item1))
                    {
                        Tag = null, ForeColor = ModulePanelKit.Accent,
                    };
                    if (parent != null) parent.Value.Item2.Nodes.Add(header);
                    else tree.Nodes.Add(header);
                    stack.Add((sig, header));
                }
                var top = stack[^1].Node;
                var node = new TreeNode(DescribeAction(r)) { Tag = i, ForeColor = RuleColor(i) };
                top.Nodes.Add(node);
                if (i == selectIndex) selected = node;
            }
            tree.ExpandAll();
            void Refold(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    Refold(n.Nodes);
                    if (n.Tag == null && collapsed.Contains(n.FullPath)) n.Collapse(ignoreChildren: false);
                }
            }
            Refold(tree.Nodes);
            tree.EndUpdate();
            if (selected != null) { tree.SelectedNode = selected; selected.EnsureVisible(); }
        }

        int SelectedFlatIndex()
            => groupView.Checked
                ? tree.SelectedNode?.Tag is int ix ? ix : -1
                : list.SelectedIndex;

        void Recalc()
        {
            try { exOut.Text = RulePipeline.PreviewExample(rules, exIn.Text); }
            catch { exOut.Text = exIn.Text; }
        }
        void Reload(int select = -1)
        {
            Recalc();
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < rules.Count; i++) list.Items.Add(rules[i].Describe());
            list.EndUpdate();
            if (select >= 0 && select < list.Items.Count) list.SelectedIndex = select;
            RebuildTree(select);
            list.Visible = !groupView.Checked;
            treeHost.Visible = groupView.Checked;
        }
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? Color.FromArgb(60, 60, 66) : ModulePanelKit.Field);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var fg = new SolidBrush(RuleColor(e.Index));
            e.Graphics.DrawString(list.Items[e.Index]?.ToString() ?? "", list.Font, fg,
                e.Bounds.X + 2, e.Bounds.Y + 1);
        };

        exIn.TextChanged += (_, _) => Recalc();
        groupView.CheckedChanged += (_, _) => Reload(SelectedFlatIndex());
        Reload(rules.Count > 0 ? 0 : -1);
        Row(list, 0);
        treeHost.Location = list.Location;
        root.Controls.Add(treeHost);
        y += S(156);

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

        // ONE Add button; the action types live in its dropdown — ten actions as ten buttons ate
        // the whole bar. The registry is still the only list that grows: the menu is built from it.
        var add = Btn("Add…");
        var addMenu = new ContextMenuStrip { BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg };
        foreach (var action in Actions.RuleActions.All)
        {
            var a = action;
            string label = a.AddLabel.StartsWith("Add: ", StringComparison.Ordinal) ? a.AddLabel.Substring(5) : a.AddLabel;
            addMenu.Items.Add(label, null, (_, _) => EditRule(new LaunchRule { Type = a.Type }, isNew: true));
        }
        add.Click += (_, _) => addMenu.Show(add, new Point(0, add.Height));
        var edit = Btn("Edit…");
        var remove = Btn("Remove");
        var up = Btn("▲");
        var down = Btn("▼");
        var export = Btn("Export all\u2026");
        var import_ = Btn("Import\u2026");
        Row(bar, S(36));

        // ── stretch with the window: the fields and the list use whatever room the host gives —
        // long real-launch command lines are the whole point of the preview, and the rule list is
        // where a busy pipeline lives. Width tracks the client area, the list also takes the free
        // height, and the button bar rides just under it.
        void Relayout()
        {
            int w = Math.Max(S(420), root.ClientSize.Width - S(40));
            inRow.Width = w;
            exIn.Width = w - S(96);
            reroll.Left = w - S(86);
            exOut.Width = w;
            list.Width = w;
            list.Height = Math.Max(S(120), root.ClientSize.Height - list.Top - bar.Height - S(30));
            treeHost.Location = list.Location;
            treeHost.Size = list.Size;
            bar.Location = new Point(bar.Left, list.Top + list.Height + S(8));
        }
        root.Resize += (_, _) => Relayout();
        Relayout();

        LaunchRule? Current() => SelectedFlatIndex() is var fi && fi >= 0 && fi < rules.Count
            ? rules[fi] : null;

        void EditRule(LaunchRule rule, bool isNew)
        {
            var action = Actions.RuleActions.ByType(rule.Type);
            if (action == null) return;   // a rule from a newer build: shown red, not editable here
            using var dlg = new RuleDialog(rule, action, dpiS);
            if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
            if (isNew) rules.Add(rule);
            Reload(isNew ? rules.Count - 1 : SelectedFlatIndex());
        }

        edit.Click += (_, _) => { if (Current() is { } r) EditRule(r, isNew: false); };
        if (!readOnly)
        {
            list.DoubleClick += (_, _) => { if (Current() is { } r) EditRule(r, isNew: false); };
            tree.NodeMouseDoubleClick += (_, e) => { if (e.Node?.Tag is int && Current() is { } r) EditRule(r, isNew: false); };
        }
        remove.Click += (_, _) =>
        {
            int ix = SelectedFlatIndex();
            if (ix < 0 || ix >= rules.Count) return;
            rules.RemoveAt(ix);
            Reload(Math.Min(ix, rules.Count - 1));
        };
        void Move(int delta)
        {
            int ix = SelectedFlatIndex();
            int to = ix + delta;
            if (ix < 0 || ix >= rules.Count || to < 0 || to >= rules.Count) return;
            (rules[ix], rules[to]) = (rules[to], rules[ix]);
            Reload(to);
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        // ── copy / paste, right-click, ONE RULE at a time (Mehdi's cut: the clipboard grain is
        // the line; the list grain belongs to Export all / Import). The format stays the stored
        // JSON object, so a rule travels to any other entity's page — but an array is refused,
        // pasting a whole export by accident duplicated pipelines wholesale.
        var jsonPretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

        void DoCopy()
        {
            int ix = SelectedFlatIndex();
            if (ix < 0 || ix >= rules.Count) return;
            try { Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(rules[ix], jsonPretty)); } catch { }
        }
        LaunchRule? ClipboardRule()
        {
            try
            {
                string txt = Clipboard.GetText() ?? "";
                if (txt.Trim().Length == 0 || txt.TrimStart().StartsWith("[")) return null;
                return System.Text.Json.JsonSerializer.Deserialize<LaunchRule>(txt);
            }
            catch { return null; }
        }
        void DoPaste(bool before)
        {
            if (readOnly) return;
            var rule = ClipboardRule();
            if (rule == null)
            {
                MessageBox.Show(root.FindForm(),
                    "The clipboard does not hold ONE rule in the JSON format Copy produces (a rule list is not pasteable — use Import for that).",
                    "Launch rules", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int at = SelectedFlatIndex();
            int insert = at >= 0 && at < rules.Count ? (before ? at : at + 1) : rules.Count;
            rules.Insert(insert, rule);
            Reload(insert);
        }

        var menu = new ContextMenuStrip { BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg };
        var miCopy = menu.Items.Add("Copy");
        var miPasteBefore = menu.Items.Add("Paste before");
        var miPasteAfter = menu.Items.Add("Paste after");
        miCopy.Click += (_, _) => DoCopy();
        miPasteBefore.Click += (_, _) => DoPaste(before: true);
        miPasteAfter.Click += (_, _) => DoPaste(before: false);
        menu.Opening += (_, _) =>
        {
            int ix = SelectedFlatIndex();
            bool onRule = ix >= 0 && ix < rules.Count;
            bool pastable = !readOnly && ClipboardRule() != null;
            miCopy.Enabled = onRule;
            miPasteBefore.Enabled = pastable && onRule;
            miPasteAfter.Enabled = pastable;   // no selection = append at the end
        };
        list.ContextMenuStrip = menu;
        tree.ContextMenuStrip = menu;
        // Right-click aims: the row/node under the cursor becomes the selection first.
        list.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int ix = list.IndexFromPoint(e.Location);
            if (ix >= 0) list.SelectedIndex = ix;
        };
        tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };

        // ── export / import, INDIVIDUAL — this entity's list, in the stored format (a bare rule
        // array, the exact thing Copy-all puts on the clipboard). Import lands in the edit buffer
        // like any other change: nothing reaches the database before OK/Apply, Cancel undoes it.
        export.Click += (_, _) =>
        {
            if (rules.Count == 0)
            {
                MessageBox.Show(root.FindForm(), "There is no rule to export.", "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using var dlg = new SaveFileDialog
                {
                    Filter = "Launch rules (*.json)|*.json", FileName = "launch-rules.json",
                };
                if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
                System.IO.File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(rules, jsonPretty));
            }
            catch (Exception ex)
            {
                MessageBox.Show(root.FindForm(), "Export failed: " + ex.Message, "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        import_.Click += (_, _) =>
        {
            try
            {
                using var dlg = new OpenFileDialog { Filter = "Launch rules (*.json)|*.json|All files (*.*)|*.*" };
                if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
                string txt = System.IO.File.ReadAllText(dlg.FileName);
                var incoming = txt.TrimStart().StartsWith("[")
                    ? System.Text.Json.JsonSerializer.Deserialize<List<LaunchRule>>(txt)
                    : new List<LaunchRule> { System.Text.Json.JsonSerializer.Deserialize<LaunchRule>(txt)! };
                if (incoming == null || incoming.Count == 0 || incoming.Any(r => r == null))
                {
                    MessageBox.Show(root.FindForm(), "This file does not hold a rule list in the format Export produces.",
                        "Launch rules", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (rules.Count > 0)
                {
                    var res = MessageBox.Show(root.FindForm(),
                        $"Replace the {rules.Count} current rule(s) with the {incoming.Count} imported one(s)?\n\nNo appends them instead.",
                        "Launch rules", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (res == DialogResult.Cancel) return;
                    if (res == DialogResult.Yes) rules.Clear();
                }
                rules.AddRange(incoming);
                Reload(rules.Count - incoming.Count);
            }
            catch (Exception ex)
            {
                MessageBox.Show(root.FindForm(), "Import failed: " + ex.Message, "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        void CtrlCv(object? _, KeyEventArgs e)
        {
            if (!e.Control || readOnly) return;
            if (e.KeyCode == Keys.C) { DoCopy(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.V) { DoPaste(before: false); e.Handled = e.SuppressKeyPress = true; }
        }
        list.KeyDown += CtrlCv;
        tree.KeyDown += CtrlCv;

        return (root, () =>
        {
            if (readOnly) return;
            try { LaunchRuleStore.Set(scope, entityId, rules); } catch { }
        });
    }

    /// <summary>The preview's default IN line — the very line a real launch of the entity would
    /// spawn, built by HostLaunch.PreviewCommandLine (per-platform command line, the game's custom
    /// parameters, the integration plugin's NormalizeCommandLine, variable expansion, ROM-token
    /// flags), minus only the side-effectful steps (archive extraction, PrepareForLaunch). The game:
    /// a game page uses its own, a version its own, an emulator draws one of ITS games at random —
    /// the "New example" button redraws. Placeholder only when nothing real can be found.</summary>
    private static string DefaultExampleIn(string scope, string entityId)
    {
        try
        {
            // Emulator-only since Mehdi's cut — the page has one attachment point, so the example
            // is always "one of THIS emulator's games", redrawn by the New button.
            var dm = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager;
            Unbroken.LaunchBox.Plugins.Data.IGame? game = null;
            var mine = dm?.GetAllGames()?.Where(g => string.Equals(g.EmulatorId, entityId, StringComparison.OrdinalIgnoreCase)
                                                     && !string.IsNullOrWhiteSpace(g.ApplicationPath)).ToList();
            if (mine is { Count: > 0 }) game = mine[Random.Shared.Next(mine.Count)];
            if (game == null) return @"emulator.exe ""FULL\PATH\TO\ROM\FILE""";

            string? emuId = entityId;
            var emu = string.IsNullOrEmpty(emuId) ? null
                : dm?.GetAllEmulators()?.FirstOrDefault(x => string.Equals(x.Id, emuId, StringComparison.OrdinalIgnoreCase));

            return HostLaunch.PreviewCommandLine(game, null, emu) ?? @"emulator.exe ""FULL\PATH\TO\ROM\FILE""";
        }
        catch { return @"emulator.exe ""FULL\PATH\TO\ROM\FILE"""; }
    }

    /// <summary>A rule as shown INSIDE its group: the action alone — the condition lives on the
    /// header. Only the parts the header does not carry remain (exclude, marker, disabled).</summary>
    private static string DescribeAction(LaunchRule r)
    {
        if (!r.IsConfigured) return r.Type + " => NOT CONFIGURED";
        string d = Actions.RuleActions.ByType(r.Type)?.Describe(r) ?? r.Type;
        if (r.RemoveFilter) d += " [remove marker]";
        if (!r.Enabled) d = "(disabled) " + d;
        return d;
    }

    /// <summary>The rule editor SHELL — enabled box, the Action group whose BODY the action itself
    /// provides (IRuleAction.BuildActionUi), the two shared probe blocks, and the reflow. What a
    /// rule dialog IS lives here once; what each action ASKS lives in its own file.</summary>
    private sealed class RuleDialog : LiteBoxForm
    {
        public RuleDialog(LaunchRule rule, Actions.IRuleAction action, float dpiS)
        {
            // WIDE on purpose: the collapsed probe summaries are sentences, and a sentence needs a
            // line (Mehdi). Height follows the blocks as they expand, collapse, or wrap.
            Text = action.DialogTitle;
            ClientSize = new Size(S(640), S(400));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            var enabled = new CheckBox
            {
                Text = "Rule enabled", Checked = rule.Enabled, AutoSize = true,
                Location = new Point(S(16), S(12)), ForeColor = LiteBoxTheme.Fg, BackColor = BackColor,
            };
            Controls.Add(enabled);

            var (body, bodyHeight, saveAction) = action.BuildActionUi(rule, dpiS);
            var actBox = new GroupBox
            {
                Text = "Action", ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
                Location = new Point(S(16), S(42)),
                Width = Math.Max(S(600), body.Width + S(24)),   // wide bodies (script editors) widen the dialog
                Height = bodyHeight + S(30),
            };
            body.Location = new Point(S(12), S(20));
            actBox.Controls.Add(body);
            Controls.Add(actBox);

            var when = new ProbeBlock("Run only when…", exclude: false, withMarker: true, dpiS, readOnly: false);
            when.Load(rule.Filter, rule.CommaFilter, rule.MatchAllFilter, rule.RemoveFilter, rule.AsGroup);
            Controls.Add(when.Box);

            var never = new ProbeBlock("Never when…", exclude: true, withMarker: false, dpiS, readOnly: false);
            never.Load(rule.Exclude, rule.CommaExclude, rule.MatchAllExclude);
            Controls.Add(never.Box);

            var ok = ActionButton("OK", MenuIcons.Add);
            ok.Click += (_, _) =>
            {
                // An unconfigured save is allowed — the rule shows NOT CONFIGURED and is skipped,
                // BigBoxProfile's add-now-configure-later workflow.
                rule.Enabled = enabled.Checked;
                saveAction();
                (rule.Filter, rule.CommaFilter, rule.MatchAllFilter, rule.RemoveFilter, rule.AsGroup) = when.Save();
                (rule.Exclude, rule.CommaExclude, rule.MatchAllExclude, _, _) = never.Save();
                DialogResult = DialogResult.OK; Close();
            };
            var cancel = ActionButton("Cancel", MenuIcons.Exit);
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(ok); Controls.Add(cancel);

            void Reflow()
            {
                int width = Math.Max(S(640), actBox.Right + S(16));
                when.Box.Location = new Point(S(16), actBox.Bottom + S(10));
                never.Box.Location = new Point(S(16), when.Box.Bottom + S(10));
                int by = never.Box.Bottom + S(14);
                ok.Location = new Point(width - S(210), by);
                cancel.Location = new Point(width - S(108), by);
                ClientSize = new Size(width, by + S(40));
            }
            when.LayoutChanged += Reflow;
            never.LayoutChanged += Reflow;
            // An action body may grow/shrink after build (MonitorProfile shows its custom editor on
            // demand) — follow it, same contract as the probe blocks.
            body.SizeChanged += (_, _) => { actBox.Height = body.Height + S(30); Reflow(); };
            Reflow();
            ThemedCheckBox.StyleAll(this);
        }
    }
}
