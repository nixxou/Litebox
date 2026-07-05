// LB "Game Progress" option pages — replicas of LaunchBox's Data → Game Progress Automation /
// Game Progress Organization, round-tripping the SAME Settings.xml fields (see Data\ProgressModel):
//   • Automation: the master switch + one target-value picker per rule (blank = rule skipped),
//     the playtime/inactivity thresholds and the "included manual values" list.
//   • Organization: the ordered Category/Value tree behind the Edit Game "Progress" combo —
//     move / add / remove / revert, persisted as the comma-joined ProgressPriorities.
// Changing either page re-marks the Edit Game combo cache dirty; enabling/keeping automation on
// triggers a background sweep on Apply so the library reflects the new rules immediately.

#nullable enable

using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class ProgressOptions
{
    public static void AddSections(OptionsWindow w, LbSettingsStore s, bool readOnly, float dpiS)
    {
        w.AddSection("LB · Game Progress Automation", BuildAutomationPanel(s, readOnly, dpiS, out var applyAuto),
            readOnly ? null : applyAuto);
        w.AddSection("LB · Game Progress Organization", BuildOrganizationPanel(s, readOnly, dpiS, out var applyOrg),
            readOnly ? null : applyOrg);
    }

    private static Color Bg => LiteBoxTheme.Bg;
    private static Color Fg => LiteBoxTheme.Fg;
    private static Color SubFg => LiteBoxTheme.SubFg;
    private static Color Field => LiteBoxTheme.Panel2;

    // ── Automation ─────────────────────────────────────────────────────────

    private static Control BuildAutomationPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(10)) };
        var org = ProgressModel.Values(s).ToArray();
        int x = S(14), wFull = S(560), y = S(12);

        var enable = new CheckBox
        {
            Text = "Automatic Progress Tracking", AutoSize = true, Location = new Point(x, y),
            ForeColor = Fg, BackColor = Bg, Checked = s.GetBool("EnableAutoProgressTracking"),
        };
        p.Controls.Add(enable); y += S(30);

        p.Controls.Add(new Label
        {
            Text = "Set Progress to the following for…  (leave blank to skip auto-setting)",
            AutoSize = true, Location = new Point(x, y), ForeColor = SubFg, BackColor = Bg,
        });
        y += S(26);

        ComboBox Value(string caption, string field, NumericUpDown? num = null, string? numField = null)
        {
            var cap = new Label { Text = caption, AutoSize = true, Location = new Point(x, y), ForeColor = Fg, BackColor = Bg };
            p.Controls.Add(cap);
            // The numeric threshold sits RIGHT-ALIGNED with the combo column's edge (not glued to the
            // caption, which crushed it on long labels).
            if (num != null) { num.Location = new Point(x + wFull - num.Width, y - S(3)); p.Controls.Add(num); }
            y += S(22);
            var c = new ComboBox
            {
                Location = new Point(x, y), Width = wFull, DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Text = s.Get(field),
            };
            c.Items.AddRange(org);
            c.Text = s.Get(field);
            p.Controls.Add(c);
            y += S(34);
            return c;
        }
        NumericUpDown Num(string field, int fallback)
        {
            var n = new NumericUpDown
            {
                Width = S(64), Minimum = 0, Maximum = 100000,
                BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            n.Value = int.TryParse(s.Get(field, fallback.ToString()), out var v) ? Math.Max(0, Math.Min(100000, v)) : fallback;
            return n;
        }

        var notStarted = Value("Games that don’t match any of the criteria below", "AutoProgressNotStartedValue");
        var minPlay = Num("AutoProgressMinPlaytime", 30);
        var playtime = Value("Games with a playtime greater than the following (in minutes)", "AutoProgressMinPlaytimeReachedValue", minPlay);
        var hasAch = Value("Games where you’ve earned at least one achievement (from any source)", "AutoProgressHasAchievementsValue");
        var pausePer = Num("AutoProgressPausePeriod", 30);
        var paused = Value("Games \"In Progress\" but inactive for the following (in days)", "AutoProgressPausedValue", pausePer);
        var beatenSc = Value("Games you’ve beaten with RetroAchievements in softcore mode", "AutoProgressBeatenSoftcoreValue");
        var beatenHc = Value("Games you’ve beaten with RetroAchievements in hardcore mode", "AutoProgressBeatenHardcoreValue");
        var completed = Value("Games where you’ve earned every achievement on RetroAchievements (softcore mode)", "AutoProgressCompletedValue");
        var mastered = Value("Games where you’ve earned every achievement from any source (RetroAchievements must be in hardcore mode)", "AutoProgressMasteredValue");

        p.Controls.Add(new Label
        {
            Text = "Games included in automation when Progress equals any of the following (semicolon separated)",
            AutoSize = true, Location = new Point(x, y), ForeColor = Fg, BackColor = Bg,
        });
        y += S(22);
        var included = new TextBox
        {
            Location = new Point(x, y), Width = wFull, Text = s.Get("AutoProgressIncludedValues"),
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        p.Controls.Add(included);

        if (readOnly) foreach (Control c in p.Controls) c.Enabled = false;

        apply = () =>
        {
            s.Set("EnableAutoProgressTracking", enable.Checked ? "true" : "false");
            s.Set("AutoProgressMinPlaytime", ((int)minPlay.Value).ToString());
            s.Set("AutoProgressPausePeriod", ((int)pausePer.Value).ToString());
            s.Set("AutoProgressNotStartedValue", notStarted.Text.Trim());
            s.Set("AutoProgressMinPlaytimeReachedValue", playtime.Text.Trim());
            s.Set("AutoProgressHasAchievementsValue", hasAch.Text.Trim());
            s.Set("AutoProgressPausedValue", paused.Text.Trim());
            s.Set("AutoProgressBeatenSoftcoreValue", beatenSc.Text.Trim());
            s.Set("AutoProgressBeatenHardcoreValue", beatenHc.Text.Trim());
            s.Set("AutoProgressCompletedValue", completed.Text.Trim());
            s.Set("AutoProgressMasteredValue", mastered.Text.Trim());
            s.Set("AutoProgressIncludedValues", included.Text.Trim());
            if (enable.Checked) ProgressAutomation.SweepAsync();   // reflect the new rules right away
        };
        return p;
    }

    // ── Organization ───────────────────────────────────────────────────────

    private static Control BuildOrganizationPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };

        var blurb = new Label
        {
            Dock = DockStyle.Top, Height = S(40), BackColor = Bg, ForeColor = SubFg,
            Text = "Tweak your Game Progress categories and values here. Reorder them to match your workflow, "
                 + "add new ones, and clean out anything you're not using. Your changes will appear throughout the UI.",
        };

        var tree = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = LiteBoxTheme.PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            HideSelection = false, ShowLines = false, ShowPlusMinus = true, FullRowSelect = true,
            ItemHeight = S(24), Indent = S(18),
        };

        void Rebuild(List<string> values)
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            foreach (var entry in values)
            {
                var (cat, val) = ProgressModel.Split(entry);
                TreeNode parent;
                if (cat.Length == 0) { tree.Nodes.Add(new TreeNode(val) { Tag = "value" }); continue; }
                parent = tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text == cat && Equals(n.Tag, "cat"))
                         ?? tree.Nodes[tree.Nodes.Add(new TreeNode(cat) { Tag = "cat" })];
                parent.Nodes.Add(new TreeNode(val) { Tag = "value" });
            }
            tree.ExpandAll();
            tree.EndUpdate();
        }
        Rebuild(ProgressModel.Values(s));

        // Flatten the tree back to the ordered "Category / Value" list.
        List<string> Flatten()
        {
            var list = new List<string>();
            foreach (TreeNode n in tree.Nodes)
            {
                if (Equals(n.Tag, "cat")) foreach (TreeNode c in n.Nodes) list.Add(n.Text + " / " + c.Text);
                else list.Add(n.Text);
            }
            return list;
        }

        // ── Buttons (right column, LB order) ──
        var buttons = new Panel { Dock = DockStyle.Right, Width = S(170), BackColor = Bg, Padding = new Padding(S(8), 0, 0, 0) };
        Button Btn(string text, int top)
        {
            var b = new Button
            {
                Text = text, Location = new Point(S(8), top), Size = new Size(S(156), S(28)),
                FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg,
                FlatAppearance = { BorderSize = 0 }, Enabled = !readOnly,
            };
            buttons.Controls.Add(b);
            return b;
        }
        var up = Btn("Move Selected Up", S(0));
        var down = Btn("Move Selected Down", S(34));
        var add = Btn("Add Item", S(68));
        var rem = Btn("Remove Selected Item", S(102));
        var revert = Btn("Revert to Default", S(136));

        void Move(int delta)
        {
            var n = tree.SelectedNode;
            if (n == null) return;
            var coll = n.Parent?.Nodes ?? tree.Nodes;
            int i = coll.IndexOf(n), j = i + delta;
            if (i < 0 || j < 0 || j >= coll.Count) return;
            coll.RemoveAt(i);
            coll.Insert(j, n);
            tree.SelectedNode = n;
            tree.ExpandAll();
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        add.Click += (_, _) =>
        {
            var sel = tree.SelectedNode;
            var cat = sel == null ? null : Equals(sel.Tag, "cat") ? sel : sel.Parent;
            string? txt = PromptText(p.FindForm(), dpiS, "Add Item",
                cat != null ? $"New value under \"{cat.Text}\":" : "New entry (\"Category / Value\"):");
            if (string.IsNullOrWhiteSpace(txt)) return;
            txt = txt.Trim().Replace(",", " ");           // commas are the storage separator
            if (cat != null && !txt.Contains(" / ")) cat.Nodes.Add(new TreeNode(txt) { Tag = "value" });
            else
            {
                var (c2, v2) = ProgressModel.Split(txt);
                if (c2.Length == 0) { tree.Nodes.Add(new TreeNode(v2) { Tag = "value" }); }
                else
                {
                    var parent = tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text == c2 && Equals(n.Tag, "cat"))
                                 ?? tree.Nodes[tree.Nodes.Add(new TreeNode(c2) { Tag = "cat" })];
                    parent.Nodes.Add(new TreeNode(v2) { Tag = "value" });
                }
            }
            tree.ExpandAll();
        };

        rem.Click += (_, _) =>
        {
            var n = tree.SelectedNode;
            if (n == null) return;
            string what = Equals(n.Tag, "cat") ? $"the category \"{n.Text}\" and its {n.Nodes.Count} value(s)" : $"\"{n.Text}\"";
            if (MessageBox.Show(p.FindForm(), $"Remove {what}?", "Remove Item",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            n.Remove();
        };

        revert.Click += (_, _) =>
        {
            if (MessageBox.Show(p.FindForm(), "Revert the Game Progress organization to LaunchBox's default list?",
                    "Revert to Default", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Rebuild(ProgressModel.DefaultPriorities.Split(',').Select(v => v.Trim()).ToList());
        };

        p.Controls.Add(tree);
        p.Controls.Add(buttons);
        p.Controls.Add(blurb);
        tree.BringToFront();

        apply = () =>
        {
            ProgressModel.SetValues(s, Flatten());
            MetadataChoicesCache.MarkFieldDirty("Progress");   // the Edit Game combo re-reads the organization
        };
        return p;
    }

    private static string? PromptText(IWin32Window? owner, float dpiS, string title, string label)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        using var f = new Form
        {
            Text = title, Size = new Size(S(460), S(170)), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowIcon = false, ShowInTaskbar = false, BackColor = Bg, ForeColor = Fg, Font = new Font("Segoe UI", 9.5f),
        };
        f.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(S(16), S(16)), ForeColor = Fg });
        var tb = new TextBox
        {
            Location = new Point(S(16), S(42)), Width = S(410),
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        f.Controls.Add(tb);
        var ok = new Button { Text = "OK", Location = new Point(S(16), S(84)), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 110, 65), ForeColor = Color.White, FlatAppearance = { BorderSize = 0 }, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(S(100), S(84)), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 70, 82), ForeColor = Color.White, FlatAppearance = { BorderSize = 0 }, DialogResult = DialogResult.Cancel };
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}
