// Manage Game Controllers — the GameControllers.xml catalog CRUD (LB parity), extracted from the
// Edit Game window so the SAME window serves both entry points: Edit Game ▸ Controller Support ▸
// "Manage Game Controllers…" and Tools ▸ Manage ▸ "Manage Game Controllers…".
//
// List: Name / Category / Associated Games, with Add… / Edit… / Delete / Close carrying the same
// glyphs as the other Manage windows (LiteBoxForm.ActionButton). The editor dialog keeps LB's two
// tabs — Details (Unique Name / Category; AssociatedPlatforms preserved verbatim, its populated
// format isn't RE'd yet) and Games (the games associated with the controller, read-only).
//
// Writes go through ControllerCatalogStore (session-authoritative in-memory list; the op-log records
// a GameController whole-collection replace). Deleting a controller also strips its association rows
// from every game, then flushes when safe.
//
// <see cref="OnCatalogChanged"/> lets the Edit Game page rebuild its controller column and grid — the
// catalog it was showing may have just moved under it.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Controllers;

/// <summary>The GameControllerSupport level mapping, RE'd against LB 13.28 data: absent = empty cell,
/// 0 = Not Supported, 1 = Partial Support, 2 = Full Support, 3 = Required. Shared by this window and
/// the Edit Game controller pages — one source of truth for a mapping that was reverse-engineered.</summary>
internal static class ControllerSupport
{
    public static readonly string[] Display = { "(Empty)", "Not Supported", "Partial Support", "Full Support", "Required" };

    /// <summary>LB shows an EMPTY cell for "no support level"; "(Empty)" only exists as a dropdown choice.</summary>
    public static string ToDisplay(string? level)
        => int.TryParse(level, out var v) && v >= 0 && v <= 3 ? Display[v + 1] : "";

    public static string? ToLevel(string? display)
    {
        int i = Array.IndexOf(Display, (display ?? "").Trim());
        return i >= 1 ? (i - 1).ToString() : null;   // ""/"(Empty)"/unknown → no SupportLevel element
    }
}

internal sealed class ManageControllersWindow : LiteBoxForm
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color PanelC = LiteBoxTheme.PanelC;
    private static readonly Color Field = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;

    private readonly bool _readOnly;
    private readonly ListView _list;
    private readonly Dictionary<string, int> _counts;
    private readonly ToolTip _tips = new();

    /// <summary>Raised after every catalog change (add / edit / delete) so a hosting page can refresh.</summary>
    public event Action? OnCatalogChanged;

    public ManageControllersWindow(bool readOnly)
    {
        _readOnly = readOnly;
        _counts = AssociationCounts();

        Text = "Manage Game Controllers" + (readOnly ? "   [READ-ONLY]" : "");
        ClientSize = new Size(S(680), S(500));
        MinimumSize = new Size(S(520), S(340));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.None, HideSelection = false,
            OwnerDraw = true,
        };
        _list.Columns.Add("Name", S(250));
        _list.Columns.Add("Category", S(150));
        _list.Columns.Add("Associated Games", S(150));
        _list.DrawColumnHeader += (_, e) =>
        {
            using var b = new SolidBrush(Color.FromArgb(24, 24, 28));
            e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.Inflate(-S(4), 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", _list.Font, r, SubFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        _list.DrawItem += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;
        _list.DoubleClick += (_, _) => EditSelected();

        var footer = new Panel { Dock = DockStyle.Bottom, BackColor = LiteBoxTheme.PanelC, Height = S(44) };
        var leftGroup = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.PanelC,
            Padding = new Padding(S(12), S(8), 0, 0),
        };
        var rightGroup = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.PanelC,
            Padding = new Padding(0, S(8), S(12), 0),
        };

        var add = ActionButton("Add…", MenuIcons.Add);
        add.Enabled = !readOnly;
        if (readOnly) add.Text = "Add 🔒";
        add.Click += (_, _) => { if (ShowEditor(null)) Reload(); };
        var edit = ActionButton("Edit…", MenuIcons.Edit);
        edit.Click += (_, _) => EditSelected();
        var del = ActionButton("Delete", MenuIcons.Delete);
        del.Enabled = !readOnly;
        if (readOnly) del.Text = "Delete 🔒";
        del.Click += (_, _) => DeleteSelected();
        leftGroup.Controls.Add(add); leftGroup.Controls.Add(edit); leftGroup.Controls.Add(del);

        var close = ActionButton("Close", MenuIcons.Exit);
        close.DialogResult = DialogResult.Cancel;
        close.Click += (_, _) => Close();
        rightGroup.Controls.Add(close);
        CancelButton = close;

        footer.Controls.Add(leftGroup);
        footer.Controls.Add(rightGroup);

        Controls.Add(_list);
        Controls.Add(footer);
        _list.BringToFront();

        Reload();
        // Catalog edits reach disk when safe (LB/BB closed), like every other editor here.
        FormClosed += (_, _) => { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } };
    }

    private void Reload()
    {
        string? keep = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in ControllerCatalogStore.All())
        {
            var it = new ListViewItem(r.Name) { Tag = r.Id };
            it.SubItems.Add(r.Category);
            it.SubItems.Add(_counts.TryGetValue(r.Id, out var n) ? n.ToString() : "0");
            it.Selected = keep != null && string.Equals(keep, r.Id, StringComparison.OrdinalIgnoreCase);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
    }

    private string? SelectedId() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    private void EditSelected()
    {
        var id = SelectedId();
        if (id != null && ShowEditor(id)) Reload();
    }

    private void DeleteSelected()
    {
        var id = SelectedId(); if (id == null) return;
        var rec = ControllerCatalogStore.All().FirstOrDefault(x => x.Id == id); if (rec == null) return;
        int n = _counts.TryGetValue(id, out var c) ? c : 0;
        string extra = n > 0 ? $"\n\nIt is associated with {n} game(s); those associations are removed too." : "";
        if (MessageBox.Show(this, $"Delete the game controller \"{rec.Name}\"?{extra}", "Delete Controller",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (n > 0) RemoveAssociations(id);
        ControllerCatalogStore.Remove(id);
        _counts.Remove(id);
        Reload();
        OnCatalogChanged?.Invoke();
    }

    // ── the catalog editor (Details + Games, LB parity) ──────────────────────

    private bool ShowEditor(string? id)
    {
        var existing = id == null ? null : ControllerCatalogStore.All().FirstOrDefault(x => x.Id == id);
        using var f = NewDialog(existing == null ? "Add Game Controller" : "Edit Game Controller", 620, 440);

        var tabs = NewDarkTabs(f);
        var details = NewTabPage(tabs, "Details");

        int x = S(140), w = S(420), y = S(18);
        void Cap(string text, int cy)
            => details.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(S(16), cy + S(3)), ForeColor = Fg, BackColor = Bg });

        Cap("Unique Name:", y);
        var name = new TextBox { Location = new Point(x, y), Width = w, Text = existing?.Name ?? "", BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        details.Controls.Add(name); y += S(34);

        Cap("Category:", y);
        var category = new ComboBox
        {
            Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        };
        foreach (var c in ControllerCatalogStore.All().Select(r => r.Category).Where(c => c.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            category.Items.Add(c);
        category.Text = existing?.Category ?? "";
        details.Controls.Add(category); y += S(34);

        Cap("Associated Platform(s):", y);
        string rawPlatforms = existing != null && existing.Extra.TryGetValue("AssociatedPlatforms", out var ap) ? ap : "";
        var platforms = new TextBox
        {
            Location = new Point(x, y), Width = w, Text = rawPlatforms, ReadOnly = true,
            BackColor = Field, ForeColor = SubFg, BorderStyle = BorderStyle.FixedSingle,
        };
        _tips.SetToolTip(platforms, "Preserved as-is — LaunchBox's storage format for this field isn't reverse-engineered yet.");
        details.Controls.Add(platforms);

        // Games tab — the associated games (read-only), like LB's.
        if (existing != null)
        {
            var games = NewTabPage(tabs, "Games");
            var glv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, HideSelection = false,
                OwnerDraw = true,
            };
            glv.Columns.Add("Title", S(260));
            glv.Columns.Add("Platform", S(160));
            glv.Columns.Add("Support", S(120));
            glv.DrawColumnHeader += (_, e) =>
            {
                using var b = new SolidBrush(Color.FromArgb(24, 24, 28));
                e.Graphics.FillRectangle(b, e.Bounds);
                var r = e.Bounds; r.Inflate(-S(4), 0);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", glv.Font, r, SubFg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            glv.DrawItem += (_, e) => e.DrawDefault = true;
            glv.DrawSubItem += (_, e) => e.DrawDefault = true;
            try
            {
                foreach (var gm in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
                    foreach (var row in (gm as ILiteBoxGame)?.GetSubEntities("GameControllerSupport")
                                        ?? (IReadOnlyList<IReadOnlyDictionary<string, string>>)Array.Empty<IReadOnlyDictionary<string, string>>())
                        if (row.TryGetValue("ControllerId", out var cid) && string.Equals(cid, existing.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            var it = new ListViewItem(Safe(() => gm.Title) ?? "");
                            it.SubItems.Add(Safe(() => gm.Platform) ?? "");
                            it.SubItems.Add(ControllerSupport.ToDisplay(row.TryGetValue("SupportLevel", out var l) ? l : ""));
                            glv.Items.Add(it);
                        }
            }
            catch { }
            games.Controls.Add(glv);
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var ok = DlgBtn("OK", LiteBoxTheme.Ok);
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82));
        ok.Enabled = !_readOnly;
        cancel.DialogResult = DialogResult.Cancel;
        bottom.Controls.AddRange(new Control[] { ok, cancel });
        bottom.Resize += (_, _) =>
        {
            cancel.Location = new Point(bottom.ClientSize.Width - cancel.Width - S(12), S(8));
            ok.Location = new Point(cancel.Left - ok.Width - S(8), S(8));
        };
        f.Controls.Add(bottom);
        f.AcceptButton = ok;
        f.CancelButton = cancel;

        bool changed = false;
        ok.Click += (_, _) =>
        {
            string n = name.Text.Trim(), c = category.Text.Trim();
            if (n.Length == 0) { MessageBox.Show(f, "The controller needs a name.", "Game Controller", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            bool taken = ControllerCatalogStore.All().Any(r => !string.Equals(r.Id, existing?.Id, StringComparison.OrdinalIgnoreCase)
                                                            && string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase));
            if (taken) { MessageBox.Show(f, $"A controller named \"{n}\" already exists (the name must be unique).", "Game Controller", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try
            {
                if (existing == null) ControllerCatalogStore.AddNew(n, c);
                else ControllerCatalogStore.Update(existing.Id, n, c);
                changed = true;
            }
            catch (Exception ex) { Console.WriteLine("[controllers] catalog save failed: " + ex.Message); }
            f.DialogResult = DialogResult.OK; f.Close();
        };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.ShowDialog(this);
        if (changed) OnCatalogChanged?.Invoke();
        return changed;
    }

    // ── library-wide association bookkeeping ─────────────────────────────────

    /// <summary>One pass over the library's GameControllerSupport rows: controller id → game count.</summary>
    private static Dictionary<string, int> AssociationCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var gm in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
                foreach (var row in (gm as ILiteBoxGame)?.GetSubEntities("GameControllerSupport")
                                    ?? (IReadOnlyList<IReadOnlyDictionary<string, string>>)Array.Empty<IReadOnlyDictionary<string, string>>())
                    if (row.TryGetValue("ControllerId", out var cid) && cid.Length > 0)
                        counts[cid] = counts.TryGetValue(cid, out var n) ? n + 1 : 1;
        }
        catch { }
        return counts;
    }

    /// <summary>Strip a deleted controller's association rows from every game that referenced it.</summary>
    private static void RemoveAssociations(string controllerId)
    {
        try
        {
            foreach (var gm in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
            {
                var lbg = gm as ILiteBoxGame;
                if (lbg == null) continue;
                List<IReadOnlyDictionary<string, string>> rows;
                try { rows = lbg.GetSubEntities("GameControllerSupport").ToList(); } catch { continue; }
                var kept = rows.Where(r => !(r.TryGetValue("ControllerId", out var cid)
                                             && string.Equals(cid, controllerId, StringComparison.OrdinalIgnoreCase))).ToList();
                if (kept.Count == rows.Count) continue;
                try { lbg.SetSubEntities("GameControllerSupport", kept); } catch { }
            }
        }
        catch { }
    }

    // ── small local chrome helpers (the dialog is self-contained) ────────────

    private Form NewDialog(string title, int w, int h) => new()
    {
        Text = title, Size = new Size(S(w), S(h)), StartPosition = FormStartPosition.CenterParent,
        FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
        ShowIcon = false, ShowInTaskbar = false, BackColor = Bg, ForeColor = Fg, Font = new Font("Segoe UI", 9.5f),
    };

    private Button DlgBtn(string text, Color back)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Padding = new Padding(S(10), S(2), S(10), S(2)), FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = Color.White, Cursor = Cursors.Hand, Height = S(30),
            FlatAppearance = { BorderSize = 0 },
        };
        return b;
    }

    private TabControl NewDarkTabs(Form f)
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(S(96), S(26)), Padding = new Point(S(8), S(4)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, f.Font, e.Bounds,
                sel ? Color.White : SubFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        f.Controls.Add(tabs);
        tabs.BringToFront();
        return tabs;
    }

    private TabPage NewTabPage(TabControl tabs, string title)
    {
        var p = new TabPage(title) { BackColor = Bg, UseVisualStyleBackColor = false };
        tabs.TabPages.Add(p);
        return p;
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
