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
            "BigBoxProfile's probes & actions, native. An ordered list of rules attached to an EMULATOR "
            + "rewrites the command line right before the game spawns — each rule guarded by filters on "
            + "the command line. Per-game targeting: a marker argument in the game's custom command-line "
            + "parameters routes rules and is stripped before the emulator ever sees it.", dpiS, 620), S(64), 18);

        Row(ModulePanelKit.Caption(
            "Rules are edited on the emulator: Edit Emulator ▸ Launch Rules. Store games are out of "
            + "scope by construction — they have neither an emulator nor a command line to rewrite.", dpiS, 620), S(48), 18);

        Row(ModulePanelKit.Caption(
            "Actions are ported from BigBoxProfile one by one, each verified against the original before "
            + "the next lands. Available today: Prefix and Suffix (prepend / append), Change exe, Change rom path (relocation through prioritized folders), Replace (search/replace on the line, literal or regex, with an extraction-variables system), and Replace in file (rewrite a config file right before launch), Create file (write a file — path and content take variables), and HID device detector (scans the plugged devices through the same libraries emulators read and appends one argument per matched device).", dpiS, 620), S(48), 18);

        return p;
    }

    private static Control BuildAssignments(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg, Padding = new Padding(S(10)) };

        // Emulator-only since Mehdi's cut — no scope combo: one list, the emulators carrying rules.
        var top = new Panel { Dock = DockStyle.Top, Height = S(30), BackColor = ModulePanelKit.Bg };
        var topCap = ModulePanelKit.Caption("Emulators carrying launch rules — double-click opens the emulator on its Launch Rules page.", dpiS, 620);
        topCap.Location = new Point(0, S(4));
        top.Controls.Add(topCap);

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

        string Scope() => LiteBoxOption.ScopeEmulator;

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
            SyncSel();
        }
        // Export/Import act on ONE entity — greyed unless exactly one row is selected (Mehdi's cut:
        // the exchange grain is the entity, here as on its own Launch Rules page).
        void SyncSel()
        {
            bool one = list.SelectedIndices.Count == 1;
            export.Enabled = one;
            import_.Enabled = one && !readOnly;
        }
        list.SelectedIndexChanged += (_, _) => SyncSel();
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

        LaunchRuleStore.Row? Selected()
            => list.SelectedIndices.Count == 1 && list.SelectedIndices[0] < rows.Count
                ? rows[list.SelectedIndices[0]] : null;

        var jsonPretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        export.Click += (_, _) =>
        {
            if (Selected() is not { } row) return;
            try
            {
                var rules = LaunchRuleStore.Get(row.Scope, row.EntityId);
                using var dlg = new SaveFileDialog
                {
                    Filter = "Launch rules (*.json)|*.json",
                    FileName = "launch-rules-" + string.Join("_", row.EntityName.Split(System.IO.Path.GetInvalidFileNameChars())) + ".json",
                };
                if (dlg.ShowDialog(root.FindForm()) != DialogResult.OK) return;
                System.IO.File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(rules, jsonPretty));
            }
            catch (Exception ex)
            {
                MessageBox.Show(root, "Export failed: " + ex.Message, "Launch rules", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        import_.Click += (_, _) =>
        {
            if (Selected() is not { } row) return;
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
                    MessageBox.Show(root, "This file does not hold a rule list in the format Export produces.",
                        "Launch rules", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // This page edits the STORE directly (no buffer window around it), so it confirms.
                if (MessageBox.Show(root,
                        $"Replace the rules of \"{row.EntityName}\" with the {incoming.Count} imported one(s)?",
                        "Launch rules", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                LaunchRuleStore.Set(row.Scope, row.EntityId, incoming);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(root, "Import failed: " + ex.Message, "Launch rules", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        // Double-click = straight to the entity's own Launch Rules page. Emulators for now — the
        // game and version windows will follow once the emulator loop is polished (Mehdi's order).
        list.DoubleClick += (_, _) =>
        {
            if (Selected() is not { } row || row.Scope != LiteBoxOption.ScopeEmulator) return;
            var emu = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllEmulators()
                ?.FirstOrDefault(e => string.Equals(e.Id, row.EntityId, StringComparison.OrdinalIgnoreCase));
            if (emu == null) return;
            Emulators.EditEmulatorWindow.Open(emu, readOnly, root.FindForm(),
                Media.MediaResolver.LbRoot ?? "", openSection: "Launch Rules");
            Reload();   // the window may have changed this very list
        };

        root.Controls.Add(list);
        root.Controls.Add(bottom);
        root.Controls.Add(top);
        Reload();
        return root;
    }
}
