// Manage Badges — the window the quick menu (View ▸ Badges) can't be: which badges show, in WHICH
// ORDER, plus the user's own badges, plus the display options gathered in one place.
//
// Two tabs:
//   • Badges — a family filter (All / Game Attributes / Storefronts / Controller Support / Custom)
//     on the left, the badges on the right with their icon, their global priority and a checkbox.
//     Move Up / Move Down reorder, Add/Edit/Delete manage custom badges.
//   • Display — the very OptionItems Options ▸ Display shows (hero, list, tiles), handed in by the
//     caller so both windows edit the same settings with the same live-apply.
//
// THE ORDERING RULE, which is the subtle part: priorities are ONE global list, but the user reorders
// while looking at a FILTERED view. "Move up" therefore moves a badge in front of the badge above it
// IN THAT VIEW — jumping over whatever the filter hides — and every priority is renumbered. Without
// that, pressing Up on the 5th Storefront badge would swap it with a hidden Controller badge and look
// like nothing happened. BadgeSettings.Move owns the rule; this window just hands it the visible ids.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Badges;

internal sealed class ManageBadgesWindow : LiteBoxForm
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color PanelC = LiteBoxTheme.PanelC;
    private static readonly Color Field = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;

    private const string FamilyAll = "All";
    private static readonly (string label, BadgeGroup? group)[] Families =
    {
        (FamilyAll, null),
        ("Game Attributes", BadgeGroup.GameAttributes),
        ("Storefronts", BadgeGroup.Storefronts),
        ("Controller Support", BadgeGroup.ControllerSupport),
        ("Custom Badges", BadgeGroup.Custom),
    };

    private readonly bool _readOnly;
    private readonly Func<IReadOnlyList<IGame>> _games;
    private readonly ListBox _familyList;
    private readonly ListView _badgeList;
    private readonly ImageList _icons;
    private readonly Button _up, _down, _add, _edit, _del;
    private bool _syncing;
    private bool _allowCheck;   // the last click landed on the checkbox (or the space bar was used)

    public ManageBadgesWindow(bool readOnly, Func<IReadOnlyList<IGame>> games,
                              Func<Options.OptionItem[]> displayOptions)
    {
        _readOnly = readOnly;
        _games = games;

        Text = "Manage Badges" + (readOnly ? "   [READ-ONLY]" : "");
        ClientSize = new Size(S(880), S(560));
        MinimumSize = new Size(S(700), S(420));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        var tabs = NewDarkTabs();
        var badgesTab = NewTabPage(tabs, "Badges");
        var displayTab = NewTabPage(tabs, "Display");

        // ── Badges tab ───────────────────────────────────────────────────────
        _icons = new ImageList { ImageSize = new Size(S(24), S(24)), ColorDepth = ColorDepth.Depth32Bit };

        _familyList = new ListBox
        {
            Dock = DockStyle.Left, Width = S(180), BackColor = PanelC, ForeColor = Fg,
            BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = S(24),
        };
        foreach (var (label, _) in Families) _familyList.Items.Add(label);
        _familyList.SelectedIndex = 0;
        _familyList.SelectedIndexChanged += (_, _) => Reload();

        _badgeList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            CheckBoxes = true, HideSelection = false, BorderStyle = BorderStyle.None,
            BackColor = PanelC, ForeColor = Fg, SmallImageList = _icons,
        };
        // Column 0 carries the checkbox AND the item image (the native list draws both there and
        // nowhere else), so the priority number gets a column of its own rather than fighting them
        // for the same 40 pixels.
        _badgeList.Columns.Add("", S(46));
        _badgeList.Columns.Add("#", S(40), HorizontalAlignment.Right);
        _badgeList.Columns.Add("Badge", S(230));
        _badgeList.Columns.Add("Family", S(150));
        _badgeList.Columns.Add("Condition", S(330));
        // A checked ListView flips the box on ANY click on the row — so double-clicking a badge to
        // edit it would enable it on the way in and disable it on the way out, or leave it inverted.
        // Nothing but the box itself (or the space bar) may change the state.
        _badgeList.MouseDown += (_, e) =>
        {
            var hit = _badgeList.HitTest(e.Location);
            _allowCheck = hit.Item != null && e.X <= hit.Item.Bounds.Left + S(20);
        };
        _badgeList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) _allowCheck = true; };
        _badgeList.ItemCheck += (_, e) => { if (!_syncing && !_allowCheck) e.NewValue = e.CurrentValue; };
        _badgeList.ItemChecked += (_, e) =>
        {
            if (_syncing || e.Item?.Tag is not string id) return;
            BadgeSettings.SetEnabled(id, e.Item.Checked);
        };
        _badgeList.SelectedIndexChanged += (_, _) => SyncButtons();
        _badgeList.DoubleClick += (_, _) => EditSelected();

        var split = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        split.Controls.Add(_badgeList);
        split.Controls.Add(_familyList);
        badgesTab.Controls.Add(split);

        var footer = new Panel { Dock = DockStyle.Bottom, BackColor = PanelC, Height = S(44) };
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = PanelC,
            Padding = new Padding(S(12), S(8), 0, 0),
        };
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = PanelC,
            Padding = new Padding(0, S(8), S(12), 0),
        };

        _up = ActionButton("Move Up");
        _up.Click += (_, _) => Move(up: true);
        _down = ActionButton("Move Down");
        _down.Click += (_, _) => Move(up: false);
        _add = ActionButton("Add Custom…", MenuIcons.Add);
        _add.Enabled = !readOnly;
        _add.Click += (_, _) => AddCustom();
        _edit = ActionButton("Edit…", MenuIcons.Edit);
        _edit.Click += (_, _) => EditSelected();
        _del = ActionButton("Delete", MenuIcons.Delete);
        _del.Enabled = !readOnly;
        _del.Click += (_, _) => DeleteSelected();
        var folder = ActionButton("Images Folder", MenuIcons.OpenImagesFolder);
        folder.Click += (_, _) => OpenImagesFolder();
        var close = ActionButton("Close");
        close.Click += (_, _) => Close();

        foreach (var b in new[] { _up, _down, _add, _edit, _del }) left.Controls.Add(b);
        right.Controls.Add(close);
        right.Controls.Add(folder);
        footer.Controls.Add(left);
        footer.Controls.Add(right);
        badgesTab.Controls.Add(footer);

        // ── Display tab ──────────────────────────────────────────────────────
        var (rows, apply) = OptionRows.Build(displayOptions(), S);
        ((Control)rows).Dock = DockStyle.Fill;
        displayTab.Controls.Add((Control)rows);
        FormClosed += (_, _) => { try { apply(); } catch { } };

        Reload();
        AcceptButton = close;
    }

    /// <summary>Opens the window (modeless is wrong here — the main window's badges change under it,
    /// and the caller wants to know when it's done).</summary>
    public static void Open(IWin32Window owner, bool readOnly, Func<IReadOnlyList<IGame>> games,
                            Func<Options.OptionItem[]> displayOptions)
    {
        using var w = new ManageBadgesWindow(readOnly, games, displayOptions);
        w.ShowDialog(owner);
    }

    // ── list plumbing ────────────────────────────────────────────────────────

    private BadgeGroup? SelectedFamily
        => _familyList.SelectedIndex >= 0 ? Families[_familyList.SelectedIndex].group : null;

    private List<BadgeDef> Shown()
    {
        var fam = SelectedFamily;
        var all = BadgeCatalog.All.OrderBy(b => BadgeSettings.OrderIndex(b.Id)).ToList();
        return fam == null ? all : all.Where(b => b.Group == fam.Value).ToList();
    }

    private void Reload()
    {
        string? keep = _badgeList.SelectedItems.Count > 0 ? _badgeList.SelectedItems[0].Tag as string : null;
        _syncing = true;
        try
        {
            _badgeList.BeginUpdate();
            _badgeList.Items.Clear();
            _icons.Images.Clear();

            foreach (var def in Shown())
            {
                int prio = BadgeSettings.OrderIndex(def.Id) + 1;
                var it = new ListViewItem("") { Tag = def.Id, Checked = BadgeSettings.IsEnabled(def.Id) };
                it.SubItems.Add(prio.ToString());
                it.SubItems.Add(def.Label);
                it.SubItems.Add(FamilyLabel(def.Group));
                it.SubItems.Add(Describe(def));

                // The badge's own art, at list size — a name alone doesn't tell you which badge it is.
                var img = BadgeImages.Get(ImageNameFor(def), S(24));
                if (img != null) { _icons.Images.Add(def.Id, img); it.ImageKey = def.Id; }
                _badgeList.Items.Add(it);
            }
        }
        finally { _badgeList.EndUpdate(); _syncing = false; }

        if (keep != null)
            foreach (ListViewItem it in _badgeList.Items)
                if ((it.Tag as string) == keep) { it.Selected = true; it.EnsureVisible(); break; }
        SyncButtons();
    }

    private static string FamilyLabel(BadgeGroup g) => g switch
    {
        BadgeGroup.GameAttributes => "Game Attributes",
        BadgeGroup.Storefronts => "Storefronts",
        BadgeGroup.ControllerSupport => "Controller Support",
        _ => "Custom",
    };

    // The Progress badge's image depends on the game; the list shows the pack's generic marker.
    private static string ImageNameFor(BadgeDef def) => def.Id;

    // Only custom badges have a condition worth spelling out; repeating a built-in's own name here
    // would just be noise in every row.
    private string Describe(BadgeDef def)
    {
        if (def.Group != BadgeGroup.Custom) return "";
        var custom = BadgeCustomStore.ById(def.Id);
        if (custom == null || custom.Rules.Count == 0) return "(no condition — never shows)";
        return string.Join("  ·  ", custom.Rules.Select(RuleText));
    }

    // The stored keys are LaunchBox's ("EqualTo"); the user picked LABELS ("Is Equal To"), so that is
    // what the summary shows.
    private static string RuleText(BadgeRule r)
    {
        var field = PlaylistFilterCatalog.Find(r.Field);
        var cmp = PlaylistFilterCatalog.FindComparison(r.Comparison, field?.Kind ?? PlaylistFieldKind.Text);
        string value = cmp is { UsesValue: false } ? "" : r.Value;
        return $"{field?.Label ?? r.Field} {cmp?.Label ?? r.Comparison} {value}".Trim();
    }

    private void SyncButtons()
    {
        var def = SelectedDef();
        bool custom = def?.Group == BadgeGroup.Custom;
        _up.Enabled = def != null && _badgeList.SelectedIndices.Count > 0 && _badgeList.SelectedIndices[0] > 0;
        _down.Enabled = def != null && _badgeList.SelectedIndices.Count > 0
                        && _badgeList.SelectedIndices[0] < _badgeList.Items.Count - 1;
        _edit.Enabled = custom && !_readOnly;
        _del.Enabled = custom && !_readOnly;
    }

    private BadgeDef? SelectedDef()
    {
        if (_badgeList.SelectedItems.Count == 0) return null;
        var id = _badgeList.SelectedItems[0].Tag as string;
        return id == null ? null : BadgeCatalog.All.FirstOrDefault(b => b.Id == id);
    }

    // ── actions ──────────────────────────────────────────────────────────────

    private void Move(bool up)
    {
        var def = SelectedDef();
        if (def == null) return;
        // The ids the user can SEE right now — that is what a step means to them.
        BadgeSettings.Move(def.Id, up, Shown().Select(b => b.Id).ToList());
        Reload();
    }

    private void AddCustom()
    {
        var badge = new BadgeCustom { Name = "New badge" };
        if (CustomBadgeDialog.Edit(this, badge, _games, DpiScale) != DialogResult.OK) return;
        BadgeCustomStore.Save(badge);
        BadgeSettings.Reset();          // the order list gains an entry
        // Ticked on arrival: you do not define a badge in order to keep it hidden. Explicit rather
        // than relying on "absent from the disabled list", so a leftover entry from a badge that once
        // carried the same id cannot silently keep the new one dark.
        BadgeSettings.SetEnabled(badge.Id, true);
        BadgeEngine.InvalidateAll();    // its rules have never been evaluated
        Reload();
    }

    private void EditSelected()
    {
        var def = SelectedDef();
        if (def == null || def.Group != BadgeGroup.Custom || _readOnly) return;
        var badge = BadgeCustomStore.ById(def.Id);
        if (badge == null) return;
        if (CustomBadgeDialog.Edit(this, badge, _games, DpiScale) != DialogResult.OK) return;
        BadgeCustomStore.Save(badge);
        BadgeEngine.InvalidateAll();
        Reload();
    }

    private void DeleteSelected()
    {
        var def = SelectedDef();
        if (def == null || def.Group != BadgeGroup.Custom || _readOnly) return;
        var r = MessageBox.Show(this,
            $"Delete the custom badge \"{def.Label}\"?\n\nIts image file stays in the badge pack folder.",
            "Delete badge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        BadgeCustomStore.Delete(def.Id);
        BadgeSettings.Reset();
        BadgeEngine.InvalidateAll();
        Reload();
    }

    private void OpenImagesFolder()
    {
        var dir = SelectedFamily == BadgeGroup.Custom ? BadgeCustomStore.ImageFolder : BadgeImages.PacksRoot;
        if (string.IsNullOrEmpty(dir)) return;
        try { System.IO.Directory.CreateDirectory(dir); } catch { }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open " + dir + "\n\n" + ex.Message, "Badges",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── dark tabs (same treatment as the other Manage windows) ───────────────

    private TabControl NewDarkTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(S(110), S(26)), Padding = new Point(S(8), S(4)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds,
                sel ? Color.White : SubFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        Controls.Add(tabs);
        tabs.BringToFront();
        return tabs;
    }

    private TabPage NewTabPage(TabControl tabs, string title)
    {
        var p = new TabPage(title) { BackColor = Bg, UseVisualStyleBackColor = false };
        tabs.TabPages.Add(p);
        return p;
    }
}
