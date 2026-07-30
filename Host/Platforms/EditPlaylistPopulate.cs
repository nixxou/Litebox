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

    // Stable XML key <-> LaunchBox label maps. Unknown keys already present in a future LaunchBox version
    // are injected into the combo verbatim, so opening and applying the editor never destroys them.
    private static readonly (string key, string label)[] Fields =
    {
        ("Platform", "Platform"),
        ("Title", "Title"),
        ("Developer", "Developer"),
        ("Publisher", "Publisher"),
        ("Genre", "Genre"),
        ("PlayMode", "Play Mode"),
        ("Region", "Region"),
        ("Series", "Series"),
        ("Status", "Status"),
        ("Source", "Source"),
        ("Rating", "Rating"),
        ("ReleaseType", "Release Type"),
        ("MaxPlayers", "Max Players"),
        ("ReleaseYear", "Release Year"),
        ("Version", "Version"),
        ("StarRating", "Star Rating"),
        ("Favorite", "Favorite"),
        ("Completed", "Completed"),
        ("Broken", "Broken"),
        ("Hide", "Hidden"),
        ("Installed", "Installed"),
        ("PlayCount", "Play Count"),
        ("PlayTime", "Play Time"),
        ("DateAdded", "Date Added"),
        ("ReleaseDate", "Release Date"),
        ("LastPlayedDate", "Last Played Date"),
    };

    private static readonly (string key, string label)[] Comparisons =
    {
        ("EqualTo", "Is Equal To"),
        ("NotEqualTo", "Is Not Equal To"),
        ("Contains", "Contains"),
        ("DoesNotContain", "Does Not Contain"),
        ("StartsWith", "Starts With"),
        ("EndsWith", "Ends With"),
        ("IsEmpty", "Is Empty"),
        ("IsNotEmpty", "Is Not Empty"),
        ("GreaterThan", "Is Greater Than"),
        ("LessThan", "Is Less Than"),
    };

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

        var grid = NewGrid(readOnly, allowNewRows: !readOnly);
        grid.Dock = DockStyle.Fill;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        var fieldCol = new DataGridViewComboBoxColumn
        {
            Name = "Field", HeaderText = "Field", FillWeight = 28,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        };
        foreach (var x in Fields) fieldCol.Items.Add(x.label);
        var comparisonCol = new DataGridViewComboBoxColumn
        {
            Name = "Comparison", HeaderText = "Comparison", FillWeight = 28,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        };
        foreach (var x in Comparisons) comparisonCol.Items.Add(x.label);
        var valueCol = new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 44 };
        grid.Columns.AddRange(fieldCol, comparisonCol, valueCol);
        grid.DataError += (_, _) => { }; // forward-compatible combo values are inserted below; never crash on one

        try
        {
            foreach (var f in playlist.GetAllPlaylistFilters() ?? Array.Empty<IPlaylistFilter>())
            {
                string fieldKey = Safe(() => f.FieldKey) ?? "";
                if (fieldKey.Length == 0) continue;
                string cmpKey = Safe(() => f.ComparisonTypeKey) ?? "";
                string fieldLabel = LabelFor(Fields, fieldKey);
                string comparisonLabel = LabelFor(Comparisons, cmpKey);
                EnsureComboItem(fieldCol, fieldLabel);
                EnsureComboItem(comparisonCol, comparisonLabel);
                grid.Rows.Add(fieldLabel, comparisonLabel, Safe(() => f.Value) ?? "");
            }
        }
        catch { }

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

            var defs = new List<PlaylistFilterDef>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string fieldLabel = Convert.ToString(row.Cells["Field"].Value)?.Trim() ?? "";
                if (fieldLabel.Length == 0) continue;
                string comparisonLabel = Convert.ToString(row.Cells["Comparison"].Value)?.Trim() ?? "";
                string fieldKey = KeyFor(Fields, fieldLabel);
                string comparisonKey = comparisonLabel.Length == 0 ? "EqualTo" : KeyFor(Comparisons, comparisonLabel);
                defs.Add(new PlaylistFilterDef(fieldKey, comparisonKey,
                    Convert.ToString(row.Cells["Value"].Value) ?? ""));
            }
            playlist.ReplaceFilters(defs);
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
            if (readOnly || state.AutoPopulate) return;
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

    private static string LabelFor((string key, string label)[] map, string key)
        => map.FirstOrDefault(x => string.Equals(x.key, key, StringComparison.OrdinalIgnoreCase)).label ?? key;

    private static string KeyFor((string key, string label)[] map, string label)
        => map.FirstOrDefault(x => string.Equals(x.label, label, StringComparison.OrdinalIgnoreCase)).key ?? label;

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
