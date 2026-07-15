// Edit Game → "Alternate Names" and "Controller Support" pages — LB parity, single-game only.
//
// Both use the SAME editing pattern as the Custom Fields page (per the shared UI convention):
// a DataGridView whose LAST ROW IS ALWAYS EMPTY (AllowUserToAddRows) instead of an Add button;
// empty rows are simply dropped when the page saves (OK / navigation).
//
//   • Alternate Names — the game's <AlternateName> child entities (Name / Region), plus LB's
//     "Set Selected Name as Title" button (feeds the Metadata page's Title field, saved with it).
//   • Controller Support — the game's <GameControllerSupport> sub-entities: one row per controller
//     (picked from the Data\GameControllers.xml catalog) with an optional support level.
//     SupportLevel mapping (RE'd against LB 13.28 data): absent = empty cell, 0 = Not Supported,
//     1 = Partial Support, 2 = Full Support, 3 = Required.
//   • Manage Game Controllers — full catalog CRUD (LB parity): Add / Edit / Delete over
//     Data\GameControllers.xml through ControllerCatalogStore (session-authoritative in-memory list;
//     writes go through the op-log's GameController whole-collection replace). The editor dialog has
//     LB's Details tab (Unique Name / Category; AssociatedPlatforms is preserved verbatim — its
//     populated format isn't RE'd yet) and Games tab (the games associated with the controller).
//     Deleting a controller also removes its association rows from every game.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;
using LbApiHost.Host.Saves;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private DataGridView? _anGrid;                       // Alternate Names
    private DataGridView? _csGrid;                       // Controller Support
    private DataGridViewComboBoxColumn? _csCtlCol;
    private readonly Dictionary<string, string> _csDisplayToId = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SupportDisplay = { "(Empty)", "Not Supported", "Partial Support", "Full Support", "Required" };
    // LB shows an EMPTY cell for "no support level"; "(Empty)" only exists as a dropdown choice.
    private static string SupportToDisplay(string? level)
        => int.TryParse(level, out var v) && v >= 0 && v <= 3 ? SupportDisplay[v + 1] : "";
    private static string? SupportToLevel(string? display)
    {
        int i = Array.IndexOf(SupportDisplay, (display ?? "").Trim());
        return i >= 1 ? (i - 1).ToString() : null;   // ""/"(Empty)"/unknown → no SupportLevel element
    }

    // ── Shared dark grid (same look as the Custom Fields page) ────────────

    private DataGridView NewDarkGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Bg, GridColor = Color.FromArgb(60, 60, 70),
            BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false, RowHeadersVisible = false,
            AllowUserToAddRows = !_readOnly, AllowUserToDeleteRows = !_readOnly, ReadOnly = _readOnly,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = new Font("Segoe UI", 9.5f),
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = PanelC;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PanelC;
        grid.DefaultCellStyle.BackColor = Field;
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Accent;
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DataError += (_, e) => e.ThrowException = false;
        return grid;
    }

    // ── Alternate Names ────────────────────────────────────────────────────

    private Control BuildAlternateNamesPage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        var grid = NewDarkGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 500, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Region", FillWeight = 320, SortMode = DataGridViewColumnSortMode.NotSortable });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(40), BackColor = Bg, Padding = new Padding(0, S(6), 0, 0) };
        var asTitle = FooterBtn("Set Selected Name as Title", Color.FromArgb(60, 60, 72));
        asTitle.AutoSize = false;
        asTitle.Dock = DockStyle.Fill;
        asTitle.Enabled = !_readOnly;
        asTitle.Click += (_, _) =>
        {
            var row = grid.CurrentRow;
            string name = (row?.Cells[0].Value as string ?? "").Trim();
            if (name.Length == 0 || _title == null) return;
            _title.Text = name;   // lands in the Metadata Title field → saved with the page (dirty-tracked)
        };
        bottom.Controls.Add(asTitle);

        p.Controls.Add(grid);
        p.Controls.Add(bottom);
        grid.BringToFront();
        _anGrid = grid;
        LoadAlternateNames();
        return p;
    }

    private void LoadAlternateNames()
    {
        if (_anGrid == null || IsMulti) return;
        _anGrid.Rows.Clear();
        try
        {
            foreach (var a in AppsGame.GetAllAlternateNames() ?? Array.Empty<IAlternateName>())
                if (a != null) _anGrid.Rows.Add(Safe(() => a.Name) ?? "", Safe(() => a.Region) ?? "");
        }
        catch { }
    }

    private void SaveAlternateNames()
    {
        if (_readOnly || _anGrid == null || IsMulti) return;
        try { _anGrid.EndEdit(); } catch { }
        // Grid → intended list; EMPTY rows are dropped (the always-empty last row pattern).
        var intended = new List<(string name, string region)>();
        foreach (DataGridViewRow r in _anGrid.Rows)
        {
            if (r.IsNewRow) continue;
            string name = (r.Cells[0].Value as string ?? "").Trim();
            if (name.Length == 0) continue;
            intended.Add((name, (r.Cells[1].Value as string ?? "").Trim()));
        }
        var g = AppsGame;
        IAlternateName[] current;
        try { current = g.GetAllAlternateNames() ?? Array.Empty<IAlternateName>(); } catch { current = Array.Empty<IAlternateName>(); }
        bool same = current.Length == intended.Count
                    && current.Zip(intended).All(z => string.Equals(Safe(() => z.First.Name) ?? "", z.Second.name, StringComparison.Ordinal)
                                                   && string.Equals(Safe(() => z.First.Region) ?? "", z.Second.region, StringComparison.Ordinal));
        if (same) return;   // untouched → no ops
        try
        {
            foreach (var a in current) g.TryRemoveAlternateNames(a);
            foreach (var (name, region) in intended)
            {
                var a = g.AddNewAlternateName();
                if (a == null) continue;
                a.Name = name;
                a.Region = region;
            }
        }
        catch (Exception ex) { Console.WriteLine("[altnames] save failed: " + ex.Message); }
    }

    // ── Controller Support ────────────────────────────────────────────────

    private Control BuildControllerSupportPage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        var grid = NewDarkGrid();

        _csCtlCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Controller", FillWeight = 480, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var supCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Support (Optional)", FillWeight = 340, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        supCol.Items.AddRange(SupportDisplay.Cast<object>().ToArray());
        grid.Columns.Add(_csCtlCol);
        grid.Columns.Add(supCol);
        RefreshControllerColumn();
        // A cell may hold a value outside the column's items (unknown controller id, blank support) —
        // register it so the combo cell never DataErrors.
        grid.CellValidating += (_, e) =>
        {
            var col = e.ColumnIndex == 0 ? _csCtlCol : supCol;
            var v = e.FormattedValue as string ?? "";
            if (v.Length > 0 && col != null && !col.Items.Contains(v)) col.Items.Add(v);
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(40), BackColor = Bg, Padding = new Padding(0, S(6), 0, 0) };
        var del = FooterBtn("Delete Controller", Color.FromArgb(60, 60, 72));
        var manage = FooterBtn("Manage Game Controllers…", Color.FromArgb(60, 60, 72));
        del.AutoSize = false; manage.AutoSize = false;
        del.Enabled = !_readOnly;
        del.Click += (_, _) => { var r = grid.CurrentRow; if (r != null && !r.IsNewRow) grid.Rows.Remove(r); };
        manage.Click += (_, _) => ShowManageControllersDialog();
        bottom.Controls.AddRange(new Control[] { del, manage });
        bottom.Resize += (_, _) =>
        {
            del.SetBounds(0, S(6), S(180), S(28));
            manage.SetBounds(bottom.ClientSize.Width - S(220), S(6), S(220), S(28));
        };

        p.Controls.Add(grid);
        p.Controls.Add(bottom);
        grid.BringToFront();
        _csGrid = grid;
        LoadControllerSupport();
        return p;
    }

    /// <summary>(Re)builds the single-game grid's Controller column — see the shared overload below.</summary>
    private void RefreshControllerColumn() => RefreshControllerColumn(_csCtlCol, _csGrid);

    /// <summary>(Re)builds a Controller combo column's choices + the shared display↔id map from the catalog.
    /// <paramref name="gridToPreserveFrom"/> (optional): also keep any value already used by a cell in that
    /// grid (unknown ids shown raw) so those cells keep rendering — the single-game grid needs it (a game can
    /// carry a raw id no longer in the catalog); the multi-select matrix grid never has pre-existing raw
    /// values to preserve, so it passes null.</summary>
    private void RefreshControllerColumn(DataGridViewComboBoxColumn? col, DataGridView? gridToPreserveFrom)
    {
        if (col == null) return;
        _csDisplayToId.Clear();
        var displays = new List<string>();
        foreach (var r in ControllerCatalogStore.All())
        {
            string d = r.Category.Length > 0 ? $"{r.Name} ({r.Category})" : r.Name;
            if (_csDisplayToId.TryAdd(d, r.Id)) displays.Add(d);
        }
        if (gridToPreserveFrom != null)
        {
            // Keep any value already used by a grid cell (unknown ids shown raw) so cells keep rendering.
            var keep = new HashSet<string>(displays, StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow r in gridToPreserveFrom.Rows)
                if (!r.IsNewRow && r.Cells[0].Value is string v && v.Length > 0 && keep.Add(v)) displays.Add(v);
        }
        col.Items.Clear();
        col.Items.AddRange(displays.Cast<object>().ToArray());
    }

    private string ControllerDisplay(string id)
    {
        foreach (var kv in _csDisplayToId) if (string.Equals(kv.Value, id, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        _csDisplayToId[id] = id;   // unknown id → shown raw, still round-trips
        return id;
    }

    private void LoadControllerSupport()
    {
        if (_csGrid == null || IsMulti) return;
        _csGrid.Rows.Clear();
        try
        {
            foreach (var row in (AppsGame as ILiteBoxGame)?.GetSubEntities("GameControllerSupport")
                                ?? (IReadOnlyList<IReadOnlyDictionary<string, string>>)Array.Empty<IReadOnlyDictionary<string, string>>())
            {
                string cid = row.TryGetValue("ControllerId", out var c) ? c : "";
                if (cid.Length == 0) continue;
                string level = row.TryGetValue("SupportLevel", out var l) ? l : "";
                _csGrid.Rows.Add(ControllerDisplay(cid), SupportToDisplay(level));
            }
        }
        catch { }
    }

    private void SaveControllerSupport()
    {
        if (_readOnly || _csGrid == null || IsMulti) return;
        try { _csGrid.EndEdit(); } catch { }
        var g = AppsGame;
        string gid = Safe(() => g.Id) ?? "";
        var rows = new List<Dictionary<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _csGrid.Rows)
        {
            if (r.IsNewRow) continue;
            string display = (r.Cells[0].Value as string ?? "").Trim();
            if (display.Length == 0) continue;                                   // empty row → dropped
            string cid = _csDisplayToId.TryGetValue(display, out var id) ? id : display;
            if (!seen.Add(cid)) continue;                                        // duplicate controller → first wins
            var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["ControllerId"] = cid, ["GameId"] = gid };
            string? level = SupportToLevel(r.Cells[1].Value as string);
            if (level != null) row["SupportLevel"] = level;
            rows.Add(row);
        }

        // Only write when something actually changed (SetSubEntities replaces the whole collection).
        var lbg = g as ILiteBoxGame;
        if (lbg == null) return;
        List<IReadOnlyDictionary<string, string>> cur;
        try { cur = lbg.GetSubEntities("GameControllerSupport").ToList(); } catch { cur = new(); }
        bool same = cur.Count == rows.Count && cur.Zip(rows).All(z =>
            (z.First.TryGetValue("ControllerId", out var a) ? a : "") == z.Second["ControllerId"]
            && (z.First.TryGetValue("SupportLevel", out var b) ? b : "") == (z.Second.TryGetValue("SupportLevel", out var c) ? c : ""));
        if (same) return;
        try { lbg.SetSubEntities("GameControllerSupport", rows); }
        catch (Exception ex) { Console.WriteLine("[controllers] save failed: " + ex.Message); }
    }

    // ── Manage Game Controllers (catalog CRUD, LB parity) ─────────────────

    private void ShowManageControllersDialog()
    {
        using var f = NewDialog("Manage Game Controllers", 680, 500);
        f.FormBorderStyle = FormBorderStyle.Sizable;
        f.MinimumSize = new Size(S(520), S(340));

        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, HideSelection = false,
            OwnerDraw = true,
        };
        lv.Columns.Add("Name", S(250));
        lv.Columns.Add("Category", S(150));
        lv.Columns.Add("Associated Games", S(150));
        lv.DrawColumnHeader += (_, e) =>
        {
            using var b = new SolidBrush(Color.FromArgb(24, 24, 28));
            e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.Inflate(-S(4), 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", lv.Font, r, SubFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;

        // Association counts: one pass over the library's GameControllerSupport rows.
        var counts = ControllerAssociationCounts();

        void Reload()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var r in ControllerCatalogStore.All())
            {
                var it = new ListViewItem(r.Name) { Tag = r.Id };
                it.SubItems.Add(r.Category);
                it.SubItems.Add(counts.TryGetValue(r.Id, out var n) ? n.ToString() : "0");
                lv.Items.Add(it);
            }
            lv.EndUpdate();
        }
        Reload();

        string? SelectedId() => lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as string : null;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var add = DlgBtn("✚ Add…", Color.FromArgb(60, 60, 72));
        var edit = DlgBtn("✎ Edit…", Color.FromArgb(60, 60, 72));
        var del = DlgBtn("✖ Delete", Color.FromArgb(60, 60, 72));
        var close = DlgBtn("Close", Color.FromArgb(70, 70, 82));
        add.Enabled = edit.Enabled = del.Enabled = !_readOnly;
        close.DialogResult = DialogResult.Cancel;
        bottom.Controls.AddRange(new Control[] { add, edit, del, close });
        bottom.Resize += (_, _) =>
        {
            add.Location = new Point(S(8), S(8));
            edit.Location = new Point(add.Right + S(6), S(8));
            del.Location = new Point(edit.Right + S(6), S(8));
            close.Location = new Point(bottom.ClientSize.Width - close.Width - S(10), S(8));
        };

        add.Click += (_, _) => { if (ShowControllerEditor(f, null)) { Reload(); RefreshControllerColumn(); } };
        edit.Click += (_, _) =>
        {
            var id = SelectedId(); if (id == null) return;
            if (ShowControllerEditor(f, id)) { Reload(); RefreshControllerColumn(); }
        };
        lv.DoubleClick += (_, _) => { var id = SelectedId(); if (id != null && ShowControllerEditor(f, id)) { Reload(); RefreshControllerColumn(); } };
        del.Click += (_, _) =>
        {
            var id = SelectedId(); if (id == null) return;
            var rec = ControllerCatalogStore.All().FirstOrDefault(x => x.Id == id); if (rec == null) return;
            int n = counts.TryGetValue(id, out var c) ? c : 0;
            string extra = n > 0 ? $"\n\nIt is associated with {n} game(s); those associations are removed too." : "";
            if (MessageBox.Show(f, $"Delete the game controller \"{rec.Name}\"?{extra}", "Delete Controller",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (n > 0) RemoveControllerAssociations(id);
            ControllerCatalogStore.Remove(id);
            counts.Remove(id);
            Reload(); RefreshControllerColumn(); LoadControllerSupport();   // this game's page may have shown it
        };

        f.Controls.Add(lv);
        f.Controls.Add(bottom);
        lv.BringToFront();
        f.CancelButton = close;
        f.ShowDialog(this);
        try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { }   // catalog edits → disk when safe
    }

    private static Dictionary<string, int> ControllerAssociationCounts()
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

    private static void RemoveControllerAssociations(string controllerId)
    {
        try
        {
            foreach (var gm in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
            {
                var lbg = gm as ILiteBoxGame;
                if (lbg == null) continue;
                List<IReadOnlyDictionary<string, string>> rows;
                try { rows = lbg.GetSubEntities("GameControllerSupport").ToList(); } catch { continue; }
                if (rows.Count == 0) continue;
                var kept = rows.Where(r => !(r.TryGetValue("ControllerId", out var cid)
                                             && string.Equals(cid, controllerId, StringComparison.OrdinalIgnoreCase))).ToList();
                if (kept.Count != rows.Count) lbg.SetSubEntities("GameControllerSupport", kept);
            }
        }
        catch (Exception ex) { Console.WriteLine("[controllers] association cleanup failed: " + ex.Message); }
    }

    /// <summary>LB's "Edit Game Controller" dialog: Details (Unique Name / Category) + Games tab
    /// (associated games, read-only). Returns true when the catalog changed.</summary>
    private bool ShowControllerEditor(IWin32Window owner, string? id)
    {
        var existing = id == null ? null : ControllerCatalogStore.All().FirstOrDefault(x => x.Id == id);
        using var f = NewDialog(existing == null ? "Add Game Controller" : "Edit Game Controller", 620, 440);

        var tabs = NewDarkTabs(f);
        var details = NewTabPage(tabs, "Details");

        int x = S(140), w = S(420), y = S(18);
        Label Cap(string text, int cy)
        {
            var l = new Label { Text = text, AutoSize = true, Location = new Point(S(16), cy + S(3)), ForeColor = Fg, BackColor = Bg };
            details.Controls.Add(l);
            return l;
        }
        Cap("Unique Name:", y);
        var name = new TextBox { Location = new Point(x, y), Width = w, Text = existing?.Name ?? "", BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        details.Controls.Add(name); y += S(34);

        Cap("Category:", y);
        var category = new ComboBox
        {
            Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Text = existing?.Category ?? "",
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
                            it.SubItems.Add(SupportToDisplay(row.TryGetValue("SupportLevel", out var l) ? l : ""));
                            glv.Items.Add(it);
                        }
            }
            catch { }
            games.Controls.Add(glv);
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var ok = DlgBtn("✔ OK", Color.FromArgb(50, 110, 65));
        var cancel = DlgBtn("✘ Cancel", Color.FromArgb(70, 70, 82));
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
        f.ShowDialog(owner);
        return changed;
    }

    private void ReloadNamesControllersIfBuilt()
    {
        if (IsMulti) return;
        if (_anGrid != null) LoadAlternateNames();
        if (_csGrid != null) LoadControllerSupport();
    }

    // ── Controller Support (MULTI-select): aggregated view ────────────────
    // Truth = per game: controller → at most ONE support level. The model is a dictionary
    // (gameId → controllerId → level), so that invariant is STRUCTURALLY impossible to violate.
    // The grid is an aggregate: one row per DISTINCT (controller, support) pair with "N / total"
    // games — the same controller with two different supports shows as two rows. Any mutation can
    // merge/split/empty rows, so every operation ends with a full grid rebuild:
    //   • editing a row's controller/support moves that pair's games to the new pair (a game's
    //     other support for the same controller is removed by construction);
    //   • the always-empty last row adds a pair, applied to ALL selected games;
    //   • clicking the Games cell applies the pair to all games; "Select Games…" opens a
    //     checkbox list to choose exactly which games carry the pair;
    //   • Delete removes the pair from its games.
    // Nothing is written until OK (SaveControllerSupportMulti — only the games whose map changed).

    private Dictionary<string, Dictionary<string, string>>? _csmModel;    // gameId → controllerId → level ("" = none)
    private Dictionary<string, Dictionary<string, string>>? _csmLoaded;   // deep snapshot for change detection
    private DataGridView? _csmGrid;
    private DataGridViewComboBoxColumn? _csmCtlCol;
    private bool _csmRebuilding;

    private Control BuildControllerSupportMultiPage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        var blurb = new Label
        {
            Dock = DockStyle.Top, Height = S(34), BackColor = Bg, ForeColor = SubFg,
            Padding = new Padding(S(2), S(4), S(2), 0),
            Text = $"Aggregated over the {_editGames.Count} selected games — one row per controller/support pair. "
                 + "Click a row's Games cell (or Select Games…) to choose which games carry it.",
        };

        var grid = NewDarkGrid();
        _csmCtlCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Controller", FillWeight = 400, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var supCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Support (Optional)", FillWeight = 260, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        supCol.Items.AddRange(SupportDisplay.Cast<object>().ToArray());
        var cntCol = new DataGridViewTextBoxColumn { HeaderText = "Games", FillWeight = 110, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable };
        grid.Columns.Add(_csmCtlCol);
        grid.Columns.Add(supCol);
        grid.Columns.Add(cntCol);
        RefreshMultiControllerColumn();

        grid.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex > 1) return;
            var col = e.ColumnIndex == 0 ? _csmCtlCol! : supCol;
            var v = e.FormattedValue as string ?? "";
            if (v.Length > 0 && !col.Items.Contains(v)) col.Items.Add(v);
        };

        // Load the per-game model (+ deep snapshot for the save diff).
        _csmModel = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _editGames)
        {
            string gid = Safe(() => g.Id) ?? "";
            if (gid.Length == 0) continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in (g as ILiteBoxGame)?.GetSubEntities("GameControllerSupport")
                                    ?? (IReadOnlyList<IReadOnlyDictionary<string, string>>)Array.Empty<IReadOnlyDictionary<string, string>>())
                    if (row.TryGetValue("ControllerId", out var cid) && cid.Length > 0)
                        map[cid] = NormLevel(row.TryGetValue("SupportLevel", out var l) ? l : "");
            }
            catch { }
            _csmModel[gid] = map;
        }
        _csmLoaded = _csmModel.ToDictionary(kv => kv.Key,
            kv => new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        // Combo edits commit immediately (else CellValueChanged waits for the cell to be left).
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        { if (!_csmRebuilding && grid.IsCurrentCellDirty && grid.CurrentCell?.ColumnIndex <= 1) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (_csmRebuilding || e.RowIndex < 0 || e.ColumnIndex > 1) return;
            var row = grid.Rows[e.RowIndex];
            // Rebuilding from inside the edit pipeline is a reentrant no-no — defer to idle.
            BeginInvoke((Action)(() => OnMultiRowEdited(row)));
        };
        grid.CellClick += (_, e) =>
        {
            if (_csmRebuilding || e.RowIndex < 0 || e.ColumnIndex != 2) return;
            var row = grid.Rows[e.RowIndex];
            if (row.IsNewRow || row.Tag is not Tuple<string, string> pair) return;
            ShowMultiSelectGamesDialog(pair.Item1, pair.Item2);   // Games cell → pick the games (Check/Uncheck All inside)
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _readOnly || grid.IsCurrentCellInEditMode) return;
            e.Handled = true; e.SuppressKeyPress = true;
            DeleteMultiRow(grid.CurrentRow);
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(40), BackColor = Bg, Padding = new Padding(0, S(6), 0, 0) };
        var sel = FooterBtn("Select Games…", Color.FromArgb(60, 60, 72));
        var del = FooterBtn("Delete Controller", Color.FromArgb(60, 60, 72));
        var manage = FooterBtn("Manage Game Controllers…", Color.FromArgb(60, 60, 72));
        sel.AutoSize = del.AutoSize = manage.AutoSize = false;
        sel.Enabled = del.Enabled = !_readOnly;
        sel.Click += (_, _) =>
        {
            var row = grid.CurrentRow;
            if (row == null || row.IsNewRow || row.Tag is not Tuple<string, string> pair) return;
            ShowMultiSelectGamesDialog(pair.Item1, pair.Item2);
        };
        del.Click += (_, _) => DeleteMultiRow(grid.CurrentRow);
        manage.Click += (_, _) =>
        {
            ShowManageControllersDialog();
            PurgeMissingControllersFromModel();   // a deleted catalog controller lost its rows library-wide
            RefreshMultiControllerColumn();
            RebuildMultiRows();
        };
        bottom.Controls.AddRange(new Control[] { sel, del, manage });
        bottom.Resize += (_, _) =>
        {
            sel.SetBounds(0, S(6), S(150), S(28));
            del.SetBounds(S(156), S(6), S(170), S(28));
            manage.SetBounds(bottom.ClientSize.Width - S(220), S(6), S(220), S(28));
        };

        p.Controls.Add(grid);
        p.Controls.Add(blurb);
        p.Controls.Add(bottom);
        grid.BringToFront();
        _csmGrid = grid;
        RebuildMultiRows();
        return p;
    }

    private static string NormLevel(string l) => int.TryParse(l, out var v) && v is >= 0 and <= 3 ? v.ToString() : "";
    private static int LevelOrder(string lvl) => lvl.Length == 0 ? 0 : (int.TryParse(lvl, out var v) ? v + 1 : 9);

    private void RefreshMultiControllerColumn() => RefreshControllerColumn(_csmCtlCol, null);

    private IEnumerable<string> GamesWithPair(string ctl, string lvl)
        => _csmModel!.Where(kv => kv.Value.TryGetValue(ctl, out var l) && l == lvl).Select(kv => kv.Key);

    /// <summary>Sets controller→level on the given games. The per-game dictionary keys on the
    /// controller, so any OTHER support the game had for it is replaced — the invariant.</summary>
    private void AssignPair(string ctl, string lvl, IEnumerable<string> gids)
    { foreach (var gid in gids.ToList()) if (_csmModel!.TryGetValue(gid, out var map)) map[ctl] = lvl; }

    private void OnMultiRowEdited(DataGridViewRow row)
    {
        if (_csmModel == null || row.DataGridView == null) return;
        string display = (row.Cells[0].Value as string ?? "").Trim();
        if (display.Length == 0) return;                                     // new row not filled yet
        string ctl = _csDisplayToId.TryGetValue(display, out var id) ? id : display;
        string lvl = SupportToLevel(row.Cells[1].Value as string) ?? "";
        if (row.Tag is Tuple<string, string> old)
        {
            if (old.Item1 == ctl && old.Item2 == lvl) return;
            // Move this pair's games to the edited pair (same-controller conflicts die structurally).
            foreach (var gid in GamesWithPair(old.Item1, old.Item2).ToList())
            {
                _csmModel[gid].Remove(old.Item1);
                _csmModel[gid][ctl] = lvl;
            }
        }
        else
        {
            // NEW row: apply only to the games that DON'T have this controller yet — adding
            // "C / (empty)" next to an existing "C / Required" must never swallow the other
            // pair's games. When every game already has the controller, open Select Games so
            // the assignment is explicit instead of silently stealing.
            var lacking = _csmModel.Where(kv => !kv.Value.ContainsKey(ctl)).Select(kv => kv.Key).ToList();
            if (lacking.Count > 0) { AssignPair(ctl, lvl, lacking); RebuildMultiRows(); }
            else { RebuildMultiRows(); ShowMultiSelectGamesDialog(ctl, lvl); }
            return;
        }
        RebuildMultiRows();
    }

    private void DeleteMultiRow(DataGridViewRow? row)
    {
        if (_readOnly || row == null || row.IsNewRow || row.Tag is not Tuple<string, string> pair || _csmModel == null) return;
        foreach (var gid in GamesWithPair(pair.Item1, pair.Item2).ToList()) _csmModel[gid].Remove(pair.Item1);
        RebuildMultiRows();
    }

    private void RebuildMultiRows()
    {
        if (_csmGrid == null || _csmModel == null) return;
        _csmRebuilding = true;
        try
        {
            _csmGrid.Rows.Clear();
            int total = _csmModel.Count;
            var counts = new Dictionary<(string ctl, string lvl), int>();
            foreach (var map in _csmModel.Values)
                foreach (var kv in map)
                { var k = (kv.Key, kv.Value); counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1; }
            foreach (var kv in counts
                         .OrderBy(k => ControllerDisplay(k.Key.ctl), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(k => LevelOrder(k.Key.lvl)))
            {
                int i = _csmGrid.Rows.Add(ControllerDisplay(kv.Key.ctl), SupportToDisplay(kv.Key.lvl), $"{kv.Value} / {total}");
                var r = _csmGrid.Rows[i];
                r.Tag = Tuple.Create(kv.Key.ctl, kv.Key.lvl);
                r.Cells[2].ToolTipText = "Click to choose which games carry this controller/support";
            }
        }
        finally { _csmRebuilding = false; }
    }

    /// <summary>"Select Games…": exactly which of the selected games carry this pair. Checking a game
    /// assigns the pair (replacing its other support for the controller); unchecking removes it.</summary>
    private void ShowMultiSelectGamesDialog(string ctl, string lvl)
    {
        if (_csmModel == null) return;
        using var f = NewDialog($"Select Games — {ControllerDisplay(ctl)}"
                                + (SupportToDisplay(lvl).Length > 0 ? $" / {SupportToDisplay(lvl)}" : ""), 520, 470);
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill, BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            CheckOnClick = true, IntegralHeight = false,
        };
        var ordered = _editGames.Where(g => (Safe(() => g.Id) ?? "").Length > 0)
                                .OrderBy(g => Safe(() => g.Title) ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        // BeginUpdate: a multi-selection can be huge (whole-platform edits, 20K+ games) — filling
        // and mass-checking item by item with live repaints would crawl otherwise. The scrollbar
        // itself is native to CheckedListBox.
        list.BeginUpdate();
        foreach (var g in ordered)
        {
            string gid = Safe(() => g.Id) ?? "";
            bool has = _csmModel.TryGetValue(gid, out var map) && map.TryGetValue(ctl, out var l) && l == lvl;
            list.Items.Add(Safe(() => g.Title) ?? gid, has);
        }
        list.EndUpdate();

        var top = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Bg };
        var checkAll = DlgBtn("Check All", Color.FromArgb(60, 60, 72));
        var uncheckAll = DlgBtn("Uncheck All", Color.FromArgb(60, 60, 72));
        checkAll.Enabled = uncheckAll.Enabled = !_readOnly;
        void SetAll(bool on)
        {
            list.BeginUpdate();
            try { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, on); }
            finally { list.EndUpdate(); }
        }
        checkAll.Click += (_, _) => SetAll(true);
        uncheckAll.Click += (_, _) => SetAll(false);
        top.Controls.AddRange(new Control[] { checkAll, uncheckAll });
        top.Resize += (_, _) =>
        {
            checkAll.Location = new Point(S(0), S(6));
            uncheckAll.Location = new Point(checkAll.Right + S(8), S(6));
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var ok = DlgBtn("✔ OK", Color.FromArgb(50, 110, 65));
        var cancel = DlgBtn("✘ Cancel", Color.FromArgb(70, 70, 82));
        ok.Enabled = !_readOnly;
        cancel.DialogResult = DialogResult.Cancel;
        bottom.Controls.AddRange(new Control[] { ok, cancel });
        bottom.Resize += (_, _) =>
        {
            cancel.Location = new Point(bottom.ClientSize.Width - cancel.Width - S(12), S(8));
            ok.Location = new Point(cancel.Left - ok.Width - S(8), S(8));
        };
        ok.Click += (_, _) =>
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                string gid = Safe(() => ordered[i].Id) ?? "";
                if (!_csmModel.TryGetValue(gid, out var map)) continue;
                bool want = list.GetItemChecked(i);
                bool has = map.TryGetValue(ctl, out var l) && l == lvl;
                if (want) map[ctl] = lvl;                                    // assign (replaces another support)
                else if (has) map.Remove(ctl);                               // unchecked → drop the pair
            }
            f.DialogResult = DialogResult.OK; f.Close();
        };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.Controls.Add(list);
        f.Controls.Add(top);
        f.Controls.Add(bottom);
        list.BringToFront();
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) == DialogResult.OK) RebuildMultiRows();
    }

    private void PurgeMissingControllersFromModel()
    {
        if (_csmModel == null) return;
        var known = new HashSet<string>(ControllerCatalogStore.All().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var (gid, map) in _csmModel)
            foreach (var ctl in map.Keys.Where(c => !known.Contains(c)).ToList())
            {
                map.Remove(ctl);
                // The catalog delete already removed it library-wide → keep the snapshot in sync too.
                if (_csmLoaded != null && _csmLoaded.TryGetValue(gid, out var snap)) snap.Remove(ctl);
            }
    }

    private void SaveControllerSupportMulti()
    {
        if (_readOnly || !IsMulti || _csmModel == null || _csmLoaded == null) return;
        foreach (var g in _editGames)
        {
            string gid = Safe(() => g.Id) ?? "";
            if (!_csmModel.TryGetValue(gid, out var map)) continue;
            var was = _csmLoaded.TryGetValue(gid, out var w) ? w : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool same = map.Count == was.Count && map.All(kv => was.TryGetValue(kv.Key, out var l) && l == kv.Value);
            if (same) continue;
            var rows = map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv =>
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["ControllerId"] = kv.Key, ["GameId"] = gid };
                if (kv.Value.Length > 0) row["SupportLevel"] = kv.Value;
                return (IReadOnlyDictionary<string, string>)row;
            }).ToList();
            try { (g as ILiteBoxGame)?.SetSubEntities("GameControllerSupport", rows); }
            catch (Exception ex) { Console.WriteLine("[controllers] multi save failed: " + ex.Message); }
        }
    }
}

// ── The game-controller catalog (Data\GameControllers.xml) ─────────────────────
// Session-authoritative in-memory list: loaded once from the file, mutated by the Manage dialog,
// persisted through the op-log's "GameController" whole-collection replace (flushed when safe —
// so reads NEVER go back to the possibly-stale file within a session). Unmodelled elements
// (AssociatedPlatforms, future fields) are preserved verbatim per controller.

internal sealed class ControllerRec
{
    public string Id = "", Name = "", Category = "";
    public Dictionary<string, string> Extra = new(StringComparer.Ordinal);   // element → raw inner XML
}

internal static class ControllerCatalogStore
{
    private static readonly object _lock = new();
    private static List<ControllerRec>? _list;

    private static string FilePath => Path.Combine(Saves.SaveManager.LbRoot, "Data", "GameControllers.xml");

    public static List<ControllerRec> All()
    {
        lock (_lock)
        {
            Load();
            return _list!.Select(Clone).ToList();
        }
    }

    public static ControllerRec AddNew(string name, string category)
    {
        lock (_lock)
        {
            Load();
            var r = new ControllerRec { Id = Guid.NewGuid().ToString(), Name = name, Category = category };
            r.Extra["AssociatedPlatforms"] = "";   // LB always writes the (empty) element
            _list!.Add(r);
            Sort();
            Persist();
            return Clone(r);
        }
    }

    public static void Update(string id, string name, string category)
    {
        lock (_lock)
        {
            Load();
            var r = _list!.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (r == null) return;
            r.Name = name;
            r.Category = category;
            Sort();
            Persist();
        }
    }

    public static void Remove(string id)
    {
        lock (_lock)
        {
            Load();
            if (_list!.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0) Persist();
        }
    }

    private static void Load()
    {
        if (_list != null) return;
        var list = new List<ControllerRec>();
        try
        {
            if (File.Exists(FilePath))
                foreach (var e in XDocument.Load(FilePath).Root?.Elements("GameController") ?? Enumerable.Empty<XElement>())
                {
                    var r = new ControllerRec();
                    foreach (var c in e.Elements())
                        switch (c.Name.LocalName)
                        {
                            case "Id": r.Id = c.Value; break;
                            case "Name": r.Name = c.Value; break;
                            case "Category": r.Category = c.Value; break;
                            default: r.Extra[c.Name.LocalName] = string.Concat(c.Nodes()); break;   // raw inner XML
                        }
                    if (r.Id.Length > 0) list.Add(r);
                }
        }
        catch (Exception ex) { Console.WriteLine("[controllers] catalog load failed: " + ex.Message); }
        _list = list;
        Sort();
    }

    private static void Sort() => _list!.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    private static ControllerRec Clone(ControllerRec r)
        => new() { Id = r.Id, Name = r.Name, Category = r.Category, Extra = new Dictionary<string, string>(r.Extra, StringComparer.Ordinal) };

    private static void Persist()
    {
        var rows = _list!.Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal) { ["Id"] = r.Id, ["Name"] = r.Name, ["Category"] = r.Category };
            foreach (var kv in r.Extra) d.TryAdd(kv.Key, kv.Value);
            d.TryAdd("AssociatedPlatforms", "");
            return d;
        }).ToList();
        try { (PluginHelper.DataManager as HostDataManagerXml)?.ReplaceGameControllerCatalog(JsonSerializer.Serialize(rows)); }
        catch (Exception ex) { Console.WriteLine("[controllers] catalog persist failed: " + ex.Message); }
    }
}
