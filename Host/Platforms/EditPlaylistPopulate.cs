// Edit Playlist -> Auto-Populate / Games.
//
// LaunchBox stores auto-populate rules as root-level <PlaylistFilter> rows. Different fields are ANDed,
// repeated rules for the same field are ORed. Manual membership is stored as ordered <PlaylistGame> rows;
// the "Missing?" column is only a computed warning that GetActualGame() could not resolve the cached row.

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

namespace LbApiHost.Host.Platforms;

internal sealed class PlaylistEditorState
{
    private bool _autoPopulate;
    public event Action<bool>? AutoPopulateChanged;
    public event Action? AutoRulesApplied;

    public PlaylistEditorState(bool autoPopulate) => _autoPopulate = autoPopulate;

    public bool AutoPopulate
    {
        get => _autoPopulate;
        set
        {
            if (_autoPopulate == value) return;
            _autoPopulate = value;
            AutoPopulateChanged?.Invoke(value);
        }
    }

    public void RulesApplied() => AutoRulesApplied?.Invoke();
}

internal static class EditPlaylistPopulate
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color Panel2 = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;

    // The field list and the per-type comparison lists live in PlaylistFilterCatalog; this file only
    // renders them. Custom fields are merged INTO the alphabetical field list (LaunchBox does not
    // group them apart here, unlike Arrange By).
    private const string UnusedValue = "(Unused)";

    // ── Multi-selection ──────────────────────────────────────────────────────────────────────
    // Editing several playlists shows only what they have in COMMON and writes back as a
    // DIFFERENCE, so each keeps whatever the grid never displayed. The merging and the write-back
    // live in PlaylistMultiEdit — UI-free, and covered by --selftest-game-sort.

    /// <summary>Auto-Populate for a selection: tri-state checkbox, and only the rules every
    /// selected AUTO playlist has. A manual playlist has no rules to share, so intersecting with it
    /// would always come out empty — the grid describes the auto ones.</summary>
    public static (Control panel, Action apply) BuildAutoPopulateMulti(
        IReadOnlyList<HostPlaylist> playlists, bool readOnly, float dpiScale)
    {
        int S(int px) => (int)Math.Round(px * dpiScale);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };

        var enabled = new CheckBox
        {
            Dock = DockStyle.Top, Height = S(34), Text = "Auto-Populate these Playlists",
            ThreeState = true, Enabled = !readOnly, ForeColor = Fg, BackColor = Bg,
            Padding = new Padding(S(2), 0, 0, 0),
        };
        var mergedAuto = PlaylistMultiEdit.Merge(playlists, p => p.AutoPopulate);
        enabled.CheckState = mergedAuto.HasValue
            ? (mergedAuto.Value ? CheckState.Checked : CheckState.Unchecked)
            : CheckState.Indeterminate;

        var autos = playlists.Where(p => Safe(() => p.AutoPopulate)).ToList();
        var shownBefore = PlaylistMultiEdit.CommonFilters(autos);

        var info = new Label
        {
            Dock = DockStyle.Top, Height = S(40), ForeColor = SubFg, BackColor = Bg,
            Text = autos.Count == 0
                ? "No auto-populated playlist in the selection."
                : $"{shownBefore.Count} rule(s) shared by the {autos.Count} auto-populated playlist(s). "
                  + "Rules belonging to a single playlist are not listed, and are left untouched.",
        };

        var (grid, readRows) = BuildRuleGrid(shownBefore, readOnly || autos.Count == 0, dpiScale);
        grid.Dock = DockStyle.Fill;

        root.Controls.Add(grid);
        root.Controls.Add(info);
        root.Controls.Add(enabled);
        grid.BringToFront();

        void Apply()
        {
            if (readOnly) return;
            // Indeterminate means "leave each playlist as it is".
            if (enabled.CheckState != CheckState.Indeterminate)
                foreach (var p in playlists) try { p.AutoPopulate = enabled.Checked; } catch { }
            if (autos.Count == 0) return;
            PlaylistMultiEdit.ApplyFilterDifference(autos, shownBefore, readRows());
        }
        return (root, Apply);
    }

    /// <summary>Games for a selection: the intersection, read-only except for Delete — and Delete
    /// only when every selected playlist is manual, since an auto one's membership comes from its
    /// rules and would be restored the moment they are evaluated again.</summary>
    public static (Control panel, Action apply) BuildGamesMulti(
        IReadOnlyList<HostPlaylist> playlists, bool readOnly, float dpiScale)
    {
        int S(int px) => (int)Math.Round(px * dpiScale);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };

        bool allManual = playlists.All(p => !Safe(() => p.AutoPopulate));
        // No Up/Down/Top/Bottom on purpose: the manual order belongs to each playlist separately,
        // so there is no shared sequence a move could act on.
        var side = new Panel { Dock = DockStyle.Right, Width = S(92), BackColor = Bg, Padding = new Padding(S(10), 0, 0, 0) };
        var delete = new Button
        {
            Text = "Delete", Location = new Point(S(10), 0), Size = new Size(S(76), S(29)),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = LiteBoxTheme.Danger,
            FlatAppearance = { BorderSize = 0 }, Enabled = false,
        };
        side.Controls.Add(delete);

        var grid = NewGrid(readOnly: true, allowNewRows: false);
        grid.Dock = DockStyle.Fill;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Title", FillWeight = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Platform", HeaderText = "Platform", FillWeight = 40 });

        var common = PlaylistMultiEdit.CommonGameIds(playlists);
        int hidden = Math.Max(0, PlaylistMultiEdit.UnionGameCount(playlists) - common.Count);
        var resolved = new Dictionary<string, IGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in playlists)
            foreach (var g in Safe(() => p.GetAllGames(false)) ?? Array.Empty<IGame>())
            {
                var id = Safe(() => g.Id) ?? "";
                if (id.Length > 0) resolved[id] = g;
            }
        foreach (var id in common)
        {
            resolved.TryGetValue(id, out var game);
            int i = grid.Rows.Add(Safe(() => game.Title) ?? "", Safe(() => game.Platform) ?? "");
            grid.Rows[i].Tag = id;
        }

        var info = new Label
        {
            Dock = DockStyle.Top, Height = S(40), ForeColor = SubFg, BackColor = Bg,
            Text = $"{common.Count} game(s) present in all {playlists.Count} playlists."
                 + (hidden > 0 ? $"   {hidden} hidden — not present in every selected playlist." : "")
                 + (allManual ? "" : "   Delete is unavailable: the selection contains an auto-populated playlist."),
        };

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void UpdateButtons() => delete.Enabled = !readOnly && allManual && grid.SelectedRows.Count > 0;
        grid.SelectionChanged += (_, _) => UpdateButtons();

        void DeleteSelected()
        {
            if (!delete.Enabled || grid.SelectedRows.Count == 0) return;
            int at = grid.SelectedRows[0].Index;
            if (grid.Rows[at].Tag is string id) removed.Add(id);
            grid.Rows.RemoveAt(at);
            if (grid.Rows.Count > 0)
            {
                grid.ClearSelection();
                grid.Rows[Math.Min(at, grid.Rows.Count - 1)].Selected = true;
            }
            UpdateButtons();
        }
        delete.Click += (_, _) => DeleteSelected();
        grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; } };
        if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
        UpdateButtons();

        root.Controls.Add(grid);
        root.Controls.Add(side);
        root.Controls.Add(info);
        grid.BringToFront();

        void Apply()
        {
            if (readOnly || !allManual || removed.Count == 0) return;
            PlaylistMultiEdit.RemoveGames(playlists, removed);
        }
        return (root, Apply);
    }

    /// <summary>The rule grid on its own — the same control for one playlist and for a selection.
    /// Returns it with a reader that turns its rows back into rules, so the caller decides what to
    /// do with them (replace, for one playlist; a difference, for a selection).</summary>
    internal static (DataGridView grid, Func<List<PlaylistFilterDef>> readRows) BuildRuleGrid(
        IEnumerable<PlaylistFilterDef> initial, bool readOnly, float dpiScale)
    {
        var customNames = GameSortCatalog.CustomFieldNames(
            Safe(() => PluginHelper.DataManager.GetAllGames()) ?? Array.Empty<IGame>());
        var fields = PlaylistFilterCatalog.Fields(customNames);

        var grid = NewGrid(readOnly, allowNewRows: !readOnly);
        grid.Dock = DockStyle.Fill;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        var fieldCol = new DataGridViewComboBoxColumn
        {
            Name = "Field", HeaderText = "Field", FillWeight = 28,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        };
        foreach (var f in fields) fieldCol.Items.Add(f.Label);
        // The Comparison column holds the UNION of every type's vocabulary, because a
        // DataGridViewComboBoxColumn shares its item list across rows. Each row is then narrowed to
        // the comparisons its own field accepts, in EditingControlShowing below.
        var comparisonCol = new DataGridViewComboBoxColumn
        {
            Name = "Comparison", HeaderText = "Comparison", FillWeight = 28,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        };
        foreach (var label in AllComparisonLabels()) comparisonCol.Items.Add(label);
        var valueCol = new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 44 };
        grid.Columns.AddRange(fieldCol, comparisonCol, valueCol);
        grid.DataError += (_, _) => { }; // forward-compatible combo values are inserted below; never crash on one

        PlaylistFilterField FieldOfRow(DataGridViewRow row)
        {
            string label = Convert.ToString(row.Cells["Field"].Value)?.Trim() ?? "";
            return fields.FirstOrDefault(f => f.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
                   ?? PlaylistFilterCatalog.Find(label, customNames);
        }

        // Is True / Is False / Is Empty / Is Not Empty carry no operand. LaunchBox greys the cell
        // and writes "(Unused)" in it; the value is not persisted for those rules.
        void SyncValueCell(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow) return;
            var field = FieldOfRow(row);
            var cmpLabel = Convert.ToString(row.Cells["Comparison"].Value)?.Trim() ?? "";
            var cmp = PlaylistFilterCatalog.FindComparison(cmpLabel, field?.Kind ?? PlaylistFieldKind.Text);
            var cell = row.Cells["Value"];
            bool uses = cmp == null || cmp.UsesValue;
            cell.ReadOnly = readOnly || !uses;
            cell.Style.ForeColor = uses ? Fg : SubFg;
            if (!uses) cell.Value = UnusedValue;
            else if (Convert.ToString(cell.Value) == UnusedValue) cell.Value = "";
        }

        try
        {
            foreach (var f in initial ?? Enumerable.Empty<PlaylistFilterDef>())
            {
                string fieldKey = Safe(() => f.FieldKey) ?? "";
                if (fieldKey.Length == 0) continue;
                var field = PlaylistFilterCatalog.Find(fieldKey, customNames);
                string fieldLabel = field?.Label ?? fieldKey;
                var cmp = PlaylistFilterCatalog.FindComparison(
                    Safe(() => f.ComparisonTypeKey) ?? "", field?.Kind ?? PlaylistFieldKind.Text);
                string comparisonLabel = cmp?.Label ?? (Safe(() => f.ComparisonTypeKey) ?? "");
                EnsureComboItem(fieldCol, fieldLabel);
                EnsureComboItem(comparisonCol, comparisonLabel);
                int added = grid.Rows.Add(fieldLabel, comparisonLabel, Safe(() => f.Value) ?? "");
                SyncValueCell(grid.Rows[added]);
            }
        }
        catch { }

        // Narrow the dropdown to the comparisons this row's field type accepts, at the moment it
        // opens — the column's own Items list is shared by every row and cannot do it.
        grid.EditingControlShowing += (_, e) =>
        {
            if (grid.CurrentCell?.OwningColumn?.Name != "Comparison") return;
            if (e.Control is not ComboBox combo) return;
            var field = FieldOfRow(grid.Rows[grid.CurrentCell.RowIndex]);
            var allowed = PlaylistFilterCatalog.Comparisons(field?.Kind ?? PlaylistFieldKind.Text);
            combo.Items.Clear();
            foreach (var c in allowed) combo.Items.Add(c.Label);
            var current = Convert.ToString(grid.CurrentCell.Value)?.Trim() ?? "";
            if (current.Length > 0 && !combo.Items.Contains(current)) combo.Items.Add(current);
            combo.SelectedItem = current;
        };

        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var row = grid.Rows[e.RowIndex];
            string changed = grid.Columns[e.ColumnIndex].Name;
            if (changed == "Field")
            {
                // The new field may not accept the comparison the row carried — reset to the first
                // one its type offers rather than leaving an impossible pair behind.
                var field = FieldOfRow(row);
                var allowed = PlaylistFilterCatalog.Comparisons(field?.Kind ?? PlaylistFieldKind.Text);
                var cmpLabel = Convert.ToString(row.Cells["Comparison"].Value)?.Trim() ?? "";
                if (!allowed.Any(c => c.Label.Equals(cmpLabel, StringComparison.OrdinalIgnoreCase)))
                    row.Cells["Comparison"].Value = allowed[0].Label;
            }
            if (changed is "Field" or "Comparison") SyncValueCell(row);
        };
        // A combo commits on selection change, otherwise CellValueChanged only fires on cell exit.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell?.OwningColumn is DataGridViewComboBoxColumn)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        void RemoveCurrent()
        {
            if (readOnly || grid.CurrentCell == null) return;
            var row = grid.Rows[grid.CurrentCell.RowIndex];
            if (!row.IsNewRow) grid.Rows.Remove(row);
        }
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || grid.IsCurrentCellInEditMode) return;
            RemoveCurrent();
            e.Handled = true;
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Remove Rule", null, (_, _) => RemoveCurrent());
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || grid.Rows[e.RowIndex].IsNewRow) return;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            menu.Show(Cursor.Position);
        };


        List<PlaylistFilterDef> ReadRows()
        {
            var defs = new List<PlaylistFilterDef>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string fieldLabel = Convert.ToString(row.Cells["Field"].Value)?.Trim() ?? "";
                if (fieldLabel.Length == 0) continue;
                var field = FieldOfRow(row);
                // A label the catalog does not know came from the file (a field a newer LaunchBox
                // added). Write it back verbatim rather than mangling it into something else.
                string fieldKey = field?.Key ?? fieldLabel;

                string comparisonLabel = Convert.ToString(row.Cells["Comparison"].Value)?.Trim() ?? "";
                var cmp = PlaylistFilterCatalog.FindComparison(comparisonLabel, field?.Kind ?? PlaylistFieldKind.Text);
                string comparisonKey = cmp?.Key ?? (comparisonLabel.Length == 0 ? "EqualTo" : comparisonLabel);

                string value = Convert.ToString(row.Cells["Value"].Value) ?? "";
                if (cmp != null && !cmp.UsesValue) value = "";   // never persist the "(Unused)" placeholder
                defs.Add(new PlaylistFilterDef(fieldKey, comparisonKey, value));
            }
            return defs;
        }
        return (grid, ReadRows);
    }

    public static (Control panel, Action apply) BuildAutoPopulate(
        HostPlaylist playlist, bool readOnly, float dpiScale, PlaylistEditorState state)
    {
        int S(int px) => (int)Math.Round(px * dpiScale);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };
        var enabled = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = S(38),
            Text = "Auto-Populate this Playlist",
            Checked = state.AutoPopulate,
            Enabled = !readOnly,
            ForeColor = Fg,
            BackColor = Bg,
            Padding = new Padding(S(2), 0, 0, 0),
        };

        var (grid, readRows) = BuildRuleGrid(PlaylistMultiEdit.FiltersOf(playlist), readOnly, dpiScale);
        grid.Dock = DockStyle.Fill;

        void ShowGrid(bool on)
        {
            grid.Visible = on;
            state.AutoPopulate = on;
        }
        enabled.CheckedChanged += (_, _) => ShowGrid(enabled.Checked);
        grid.Visible = enabled.Checked;

        root.Controls.Add(grid);
        root.Controls.Add(enabled);
        grid.BringToFront();

        void Apply()
        {
            if (readOnly) return;
            state.AutoPopulate = enabled.Checked;
            if (playlist.AutoPopulate != enabled.Checked) playlist.AutoPopulate = enabled.Checked;

            playlist.ReplaceFilters(readRows());
            state.RulesApplied(); // an already-open Games section refreshes its auto-populated preview
        }
        return (root, Apply);
    }

    public static (Control panel, Action apply) BuildGames(
        HostPlaylist playlist, bool readOnly, float dpiScale, PlaylistEditorState state)
    {
        int S(int px) => (int)Math.Round(px * dpiScale);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };
        var side = new Panel
        {
            Dock = DockStyle.Right, Width = S(92), BackColor = Bg,
            Padding = new Padding(S(10), 0, 0, 0),
        };
        Button SideButton(string text, int top) => new()
        {
            Text = text,
            Location = new Point(S(10), S(top)),
            Size = new Size(S(76), S(29)),
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel2,
            ForeColor = Fg,
            FlatAppearance = { BorderSize = 0 },
        };
        var up = SideButton("Up", 0);
        var down = SideButton("Down", 36);
        var top = SideButton("Top", 82);
        var bottom = SideButton("Bottom", 118);
        var delete = SideButton("Delete", 164);
        delete.ForeColor = LiteBoxTheme.Danger;
        side.Controls.AddRange(new Control[] { up, down, top, bottom, delete });

        var grid = NewGrid(readOnly: true, allowNewRows: false);
        grid.Dock = DockStyle.Fill;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Title", FillWeight = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Platform", HeaderText = "Platform", FillWeight = 32 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Missing", HeaderText = "Missing?", FillWeight = 13,
            ReadOnly = true, ThreeState = false,
        });

        var manualRows = (playlist.GetAllPlaylistGames() ?? Array.Empty<IPlaylistGame>())
            .OfType<HostPlaylistGame>()
            .OrderBy(g => g.ManualOrderValue)
            .ToList();
        bool showingAuto = state.AutoPopulate;
        bool populated = false;

        void CaptureManual()
        {
            if (showingAuto) return;
            manualRows = grid.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow && r.Tag is HostPlaylistGame)
                .Select(r => (HostPlaylistGame)r.Tag!)
                .ToList();
        }

        void Populate(bool auto)
        {
            if (populated && !showingAuto) CaptureManual();
            showingAuto = auto;
            populated = true;
            grid.Rows.Clear();
            if (auto)
            {
                IGame[] games;
                try { games = playlist.GetAllGames(false) ?? Array.Empty<IGame>(); }
                catch { games = Array.Empty<IGame>(); }
                foreach (var game in games)
                {
                    int i = grid.Rows.Add(Safe(() => game.Title) ?? "", Safe(() => game.Platform) ?? "", false);
                    grid.Rows[i].Tag = game;
                }
            }
            else
            {
                foreach (var pg in manualRows)
                {
                    IGame? actual = null;
                    try { actual = pg.GetActualGame(); } catch { }
                    bool missing = actual == null;
                    string title = actual != null ? (Safe(() => actual.Title) ?? "") : (pg.GameTitleValue ?? "");
                    string platform = actual != null ? (Safe(() => actual.Platform) ?? "") : (pg.GamePlatformValue ?? "");
                    int i = grid.Rows.Add(title, platform, missing);
                    grid.Rows[i].Tag = pg;
                    if (missing) grid.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(220, 155, 105);
                }
            }
            if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
            UpdateButtons();
        }

        int SelectedIndex()
            => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Index
             : grid.CurrentCell != null ? grid.CurrentCell.RowIndex : -1;

        void Select(int index)
        {
            if (index < 0 || index >= grid.Rows.Count) return;
            grid.ClearSelection();
            grid.Rows[index].Selected = true;
            grid.CurrentCell = grid.Rows[index].Cells[0];
        }

        void MoveTo(int destination)
        {
            if (readOnly || showingAuto) return;
            int source = SelectedIndex();
            if (source < 0 || source >= grid.Rows.Count) return;
            destination = Math.Max(0, Math.Min(destination, grid.Rows.Count - 1));
            if (source == destination) return;
            var row = grid.Rows[source];
            grid.Rows.RemoveAt(source);
            grid.Rows.Insert(destination, row);
            Select(destination);
            CaptureManual();
            UpdateButtons();
        }

        void DeleteSelected()
        {
            if (readOnly || showingAuto) return;
            int i = SelectedIndex();
            if (i < 0 || i >= grid.Rows.Count) return;
            grid.Rows.RemoveAt(i);
            CaptureManual();
            if (grid.Rows.Count > 0) Select(Math.Min(i, grid.Rows.Count - 1));
            UpdateButtons();
        }

        void UpdateButtons()
        {
            int i = SelectedIndex();
            bool editable = !readOnly && !showingAuto && i >= 0;
            up.Enabled = editable && i > 0;
            top.Enabled = editable && i > 0;
            down.Enabled = editable && i < grid.Rows.Count - 1;
            bottom.Enabled = editable && i < grid.Rows.Count - 1;
            delete.Enabled = editable;
        }

        up.Click += (_, _) => MoveTo(SelectedIndex() - 1);
        down.Click += (_, _) => MoveTo(SelectedIndex() + 1);
        top.Click += (_, _) => MoveTo(0);
        bottom.Click += (_, _) => MoveTo(grid.Rows.Count - 1);
        delete.Click += (_, _) => DeleteSelected();
        grid.SelectionChanged += (_, _) => UpdateButtons();
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            DeleteSelected();
            e.Handled = true;
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Remove from Playlist", null, (_, _) => DeleteSelected());
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            Select(e.RowIndex);
            if (!showingAuto && !readOnly) menu.Show(Cursor.Position);
        };

        state.AutoPopulateChanged += Populate;
        state.AutoRulesApplied += () => { if (showingAuto) Populate(true); };
        Populate(state.AutoPopulate);

        root.Controls.Add(grid);
        root.Controls.Add(side);
        grid.BringToFront();

        void Apply()
        {
            if (readOnly) return;
            // The manual list is written back even when Auto-Populate is ON. LaunchBox keeps the
            // <PlaylistGame> rows either way, and dropping them here silently discarded any
            // reorder or removal the user made before ticking the box.
            CaptureManual();
            playlist.ReplaceGames(manualRows);
        }
        return (root, Apply);
    }

    private static DataGridView NewGrid(bool readOnly, bool allowNewRows)
    {
        var grid = new DataGridView
        {
            BackgroundColor = Panel2,
            ForeColor = Fg,
            GridColor = Color.FromArgb(70, 70, 74),
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToResizeRows = false,
            AllowUserToAddRows = allowNewRows,
            AllowUserToDeleteRows = !readOnly,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ReadOnly = readOnly,
            EditMode = DataGridViewEditMode.EditOnEnter,
            EnableHeadersVisualStyles = false,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(24, 24, 28);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.BackColor = Panel2;
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 90, 130);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(
            Math.Min(255, Panel2.R + 4), Math.Min(255, Panel2.G + 4), Math.Min(255, Panel2.B + 4));
        return grid;
    }

    /// <summary>Every comparison label across the three type vocabularies, deduplicated — the shared
    /// item list a DataGridViewComboBoxColumn needs so that no row's stored value is rejected.</summary>
    private static string[] AllComparisonLabels()
        => new[] { PlaylistFieldKind.Text, PlaylistFieldKind.Number, PlaylistFieldKind.Bool }
            .SelectMany(PlaylistFilterCatalog.Comparisons)
            .Select(c => c.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void EnsureComboItem(DataGridViewComboBoxColumn column, string value)
    {
        if (!string.IsNullOrEmpty(value) && !column.Items.Contains(value)) column.Items.Add(value);
    }

    private static T? Safe<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
