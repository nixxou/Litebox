// Options ▸ Modules ▸ Launch rules — the module's own page: what the pipeline is, where rules are
// edited, and the Assignments listing (which emulators / games / versions carry rules), the same
// housekeeping surface the Monitor Profiles module has. Rules themselves are edited on the entity
// windows — this page never duplicates that editor.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Rules;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class RulesPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        TabPage Page(string title, Control c)
        {
            var pg = new TabPage(title) { BackColor = ModulePanelKit.Bg };
            c.Dock = DockStyle.Fill;
            pg.Controls.Add(c);
            tabs.TabPages.Add(pg);
            return pg;
        }

        Page("General", BuildGeneral(dpiS));
        Page("Assignments", BuildAssignments(dpiS, readOnly));

        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg };
        root.Controls.Add(tabs);
        return (root, null);
    }

    private static Control BuildGeneral(float dpiS)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var p = ModulePanelKit.Root(dpiS);
        int y = S(8);
        void Row(Control c, int h, int indent = 0) { c.Location = new Point(S(4 + indent), y); p.Controls.Add(c); y += h; }

        Row(ModulePanelKit.Header("Launch rules", dpiS), S(30));
        Row(ModulePanelKit.Caption(
            "BigBoxProfile's probes & actions, native. An ordered list of rules attached to an emulator, a "
            + "game or an additional version rewrites the command line right before the game spawns — each "
            + "rule guarded by filters on the command line, including marker arguments that route rules and "
            + "are stripped before the emulator ever sees them.", dpiS, 620), S(64), 18);

        Row(ModulePanelKit.Caption(
            "Rules are edited on the entity itself: Edit Emulator ▸ Launch Rules, Edit Game ▸ Launching ▸ "
            + "Launch Rules, or the additional version's dialog. Resolution is EXCLUSIVE — the most specific "
            + "level that has any enabled rule provides the whole pipeline: version, then game, then "
            + "emulator.", dpiS, 620), S(64), 18);

        Row(ModulePanelKit.Caption(
            "Actions are ported from BigBoxProfile one by one, each verified against the original before "
            + "the next lands. Available today: Prefix (prepend to the argument list or the command line).", dpiS, 620), S(48), 18);

        return p;
    }

    private static Control BuildAssignments(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg, Padding = new Padding(S(10)) };

        var kind = ModulePanelKit.Combo(dpiS, readOnly: false, 220);
        kind.Items.AddRange(new object[] { "Emulators", "Games", "Additional versions" });
        kind.SelectedIndex = 0;

        var top = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = ModulePanelKit.Bg };
        kind.Location = new Point(0, S(4));
        top.Controls.Add(kind);

        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        list.Columns.Add("Entity", S(240));
        list.Columns.Add("Rules", S(330));

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ModulePanelKit.Bg,
            Padding = new Padding(0, S(8), 0, 0),
        };
        Button Btn(string t)
        {
            var b = new Button
            {
                Text = t, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(S(150), S(26)), Padding = new Padding(S(8), S(3), S(8), S(3)),
                Margin = new Padding(0, 0, S(8), 0), BackColor = ModulePanelKit.Field,
                ForeColor = ModulePanelKit.Fg, FlatStyle = FlatStyle.Flat, Enabled = !readOnly,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            bottom.Controls.Add(b);
            return b;
        }
        var del = Btn("Remove selected");
        var delAll = Btn("Remove all in this list");
        var export = Btn("Export all\u2026");
        var import_ = Btn("Import\u2026");
        export.Enabled = true;   // exporting is read-only, allowed even in read-only mode

        string Scope() => kind.SelectedIndex switch
        {
            1 => LiteBoxOption.ScopeGame,
            2 => LiteBoxOption.ScopeVersion,
            _ => LiteBoxOption.ScopeEmulator,
        };

        var rows = new List<LaunchRuleStore.Row>();
        void Reload()
        {
            rows.Clear();
            rows.AddRange(LaunchRuleStore.All(Scope(), MonitorsPanel.NameResolver(Scope())));
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rows) list.Items.Add(new ListViewItem(new[] { r.EntityName, r.What }));
            list.EndUpdate();
            del.Enabled = delAll.Enabled = !readOnly && rows.Count > 0;
        }
        kind.SelectedIndexChanged += (_, _) => Reload();
        del.Click += (_, _) =>
        {
            var picked = list.SelectedIndices.Cast<int>().Where(i => i >= 0 && i < rows.Count).Select(i => rows[i]).ToList();
            foreach (var r in picked) LaunchRuleStore.Clear(r.Scope, r.EntityId);
            if (picked.Count > 0) Reload();
        };
        delAll.Click += (_, _) =>
        {
            if (rows.Count == 0) return;
            if (MessageBox.Show(root, $"Remove all {rows.Count} rule list(s) in this list?", "Launch rules",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var r in rows.ToList()) LaunchRuleStore.Clear(r.Scope, r.EntityId);
            Reload();
        };

        // ── whole-set export / import. The file wraps the stored JSON plus entity names; import
        // matches by id, then by unique name (ids mean nothing on another install), REPLACES the
        // rules of what it matches, and says exactly what it will do before doing it.
        var jsonPretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        export.Click += (_, _) =>
        {
            try
            {
                var set = LaunchRuleStore.ExportAll(MonitorsPanel.NameResolver);
                if (set.Entries.Count == 0)
                {
                    MessageBox.Show(root, "There are no rules to export.", "Launch rules",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                using var dlg = new SaveFileDialog
                {
                    Filter = "Launch rules (*.json)|*.json", FileName = "litebox-launch-rules.json",
                };
                if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
                System.IO.File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(set, jsonPretty));
                MessageBox.Show(root, $"Exported {set.Entries.Count} rule list(s).", "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(root, "Export failed: " + ex.Message, "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        import_.Click += (_, _) =>
        {
            try
            {
                using var dlg = new OpenFileDialog { Filter = "Launch rules (*.json)|*.json|All files (*.*)|*.*" };
                if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
                var set = System.Text.Json.JsonSerializer.Deserialize<LaunchRuleStore.RuleSetExport>(
                    System.IO.File.ReadAllText(dlg.FileName));
                if (set == null || set.Format != "LiteBoxLaunchRules" || set.Entries.Count == 0)
                {
                    MessageBox.Show(root, "This file is not a LiteBox launch-rules export.", "Launch rules",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var dm = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager;
                string? EmuByName(string name)
                {
                    var hits = dm?.GetAllEmulators()?.Where(e => string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase)).ToList();
                    return hits is { Count: 1 } ? hits[0].Id : null;
                }
                string? GameByName(string name)
                {
                    var hits = dm?.GetAllGames()?.Where(g => string.Equals(g.Title, name, StringComparison.OrdinalIgnoreCase)).ToList();
                    return hits is { Count: 1 } ? hits[0].Id : null;
                }
                bool Exists(string scope, string id) =>
                    MonitorsPanel.NameResolver(scope)(id) != null;

                var plan = LaunchRuleStore.PlanImport(set, EmuByName, GameByName, Exists);
                int byId = plan.Count(x => x.How == "matched by id");
                int byName = plan.Count(x => x.How == "matched by name");
                var missed = plan.Where(x => x.TargetId == null).ToList();
                string msg = $"Import {plan.Count} rule list(s): {byId} matched by id, {byName} by name"
                    + (missed.Count > 0
                        ? $", {missed.Count} NOT matched (skipped):\n  "
                          + string.Join("\n  ", missed.Take(8).Select(x => $"{x.Entry.Scope} \"{(x.Entry.EntityName.Length > 0 ? x.Entry.EntityName : x.Entry.EntityId)}\" — {x.How}"))
                          + (missed.Count > 8 ? $"\n  … and {missed.Count - 8} more" : "")
                        : ".")
                    + "\n\nExisting rules on the matched entities will be REPLACED. Continue?";
                if (MessageBox.Show(root, msg, "Launch rules",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

                int done = LaunchRuleStore.ApplyImport(plan);
                MessageBox.Show(root, $"Imported {done} rule list(s).", "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(root, "Import failed: " + ex.Message, "Launch rules",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        root.Controls.Add(list);
        root.Controls.Add(bottom);
        root.Controls.Add(top);
        Reload();
        return root;
    }
}
