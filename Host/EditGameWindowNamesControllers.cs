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
//     SupportLevel mapping (RE'd against LB 13.28 data): absent = (Empty), 0 = Not Supported,
//     1 = Partial Support, 2 = Full Support, 3 = Required.
//     "Manage Game Controllers…" shows the catalog (name / category / associated-game count);
//     catalog EDITING is deferred — it lives in its own root file (GameControllers.xml), which has
//     no LiteBox write-back channel yet.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Saves;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private DataGridView? _anGrid;                       // Alternate Names
    private DataGridView? _csGrid;                       // Controller Support
    private List<(string id, string name, string category)>? _controllerCatalog;
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

        var catalog = ControllerCatalog();
        _csDisplayToId.Clear();
        var displays = new List<string>();
        foreach (var (id, name, category) in catalog)
        {
            string d = category.Length > 0 ? $"{name} ({category})" : name;
            if (_csDisplayToId.TryAdd(d, id)) displays.Add(d);
        }

        var ctlCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Controller", FillWeight = 480, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        ctlCol.Items.AddRange(displays.Cast<object>().ToArray());
        var supCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Support (Optional)", FillWeight = 340, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        supCol.Items.AddRange(SupportDisplay.Cast<object>().ToArray());
        grid.Columns.Add(ctlCol);
        grid.Columns.Add(supCol);
        // A cell may hold a value outside the column's items (unknown controller id, blank support) —
        // register it so the combo cell never DataErrors.
        grid.CellValidating += (_, e) =>
        {
            var col = e.ColumnIndex == 0 ? ctlCol : supCol;
            var v = e.FormattedValue as string ?? "";
            if (v.Length > 0 && !col.Items.Contains(v)) col.Items.Add(v);
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

    private List<(string id, string name, string category)> ControllerCatalog()
    {
        if (_controllerCatalog != null) return _controllerCatalog;
        var list = new List<(string, string, string)>();
        try
        {
            string f = Path.Combine(SaveManager.LbRoot, "Data", "GameControllers.xml");
            if (File.Exists(f))
                foreach (var e in XDocument.Load(f).Root?.Elements("GameController") ?? Enumerable.Empty<XElement>())
                {
                    string id = e.Element("Id")?.Value ?? "";
                    if (id.Length == 0) continue;
                    list.Add((id, e.Element("Name")?.Value ?? id, e.Element("Category")?.Value ?? ""));
                }
        }
        catch (Exception ex) { Console.WriteLine("[controllers] catalog load failed: " + ex.Message); }
        list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
        return _controllerCatalog = list;
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

    // ── Manage Game Controllers (catalog viewer — editing deferred, see header) ──

    private void ShowManageControllersDialog()
    {
        using var f = NewDialog("Manage Game Controllers", 640, 480);
        f.FormBorderStyle = FormBorderStyle.Sizable;
        f.MinimumSize = new Size(S(480), S(320));

        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, HideSelection = false,
            OwnerDraw = true,
        };
        lv.Columns.Add("Name", S(240));
        lv.Columns.Add("Category", S(140));
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
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var gm in Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
                foreach (var row in (gm as ILiteBoxGame)?.GetSubEntities("GameControllerSupport")
                                    ?? (IReadOnlyList<IReadOnlyDictionary<string, string>>)Array.Empty<IReadOnlyDictionary<string, string>>())
                    if (row.TryGetValue("ControllerId", out var cid) && cid.Length > 0)
                        counts[cid] = counts.TryGetValue(cid, out var n) ? n + 1 : 1;
        }
        catch { }
        foreach (var (id, name, category) in ControllerCatalog())
        {
            var it = new ListViewItem(name);
            it.SubItems.Add(category);
            it.SubItems.Add(counts.TryGetValue(id, out var n) ? n.ToString() : "0");
            lv.Items.Add(it);
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var hint = new Label
        {
            AutoSize = true, ForeColor = SubFg, BackColor = Bg, Location = new Point(S(8), S(14)),
            Text = "Catalog editing (add / edit / delete) is managed by LaunchBox for now.",
        };
        var close = DlgBtn("Close", Color.FromArgb(70, 70, 82));
        close.DialogResult = DialogResult.Cancel;
        bottom.Controls.Add(hint);
        bottom.Controls.Add(close);
        bottom.Resize += (_, _) => close.Location = new Point(bottom.ClientSize.Width - close.Width - S(10), S(8));
        f.Controls.Add(lv);
        f.Controls.Add(bottom);
        lv.BringToFront();
        f.CancelButton = close;
        f.ShowDialog(this);
    }

    private void ReloadNamesControllersIfBuilt()
    {
        if (IsMulti) return;
        if (_anGrid != null) LoadAlternateNames();
        if (_csGrid != null) LoadControllerSupport();
    }
}
