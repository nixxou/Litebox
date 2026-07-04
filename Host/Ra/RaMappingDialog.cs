// Platform → RA-console mapping editor for the LiteBox-native RA fallback. Opened from the RA options tab
// ("Platform mapping…"). Each LB platform shows its effective RAHasher console; the user can override it
// (e.g. map "Sega Model 2" → ARC) or set "(none)". Only platforms that DIFFER from the frozen hardlist are
// persisted (Core\ra-platform-overrides.json via RaPlatformMap.SaveOverrides) — so the hardlist still drives
// everything else. Its own window, so the table isn't cramped in the small tab.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Ra;

internal sealed class RaMappingDialog : LiteBoxForm
{
    private const string None = "(none)";

    private readonly DataGridView _grid;
    private readonly TextBox _filter;

    public RaMappingDialog(IEnumerable<string> platforms)
    {
        Text = "RetroAchievements — platform mapping";
        ClientSize = new Size(S(520), S(560));
        MinimumSize = new Size(S(420), S(360));
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 8.5f);
        MinimizeBox = false; MaximizeBox = false;

        var help = new Label
        {
            Dock = DockStyle.Top, Height = S(38), ForeColor = LiteBoxTheme.SubFg, Padding = new Padding(S(10), S(8), S(10), 0), AutoSize = false,
            Text = "Override how a platform maps to a RetroAchievements console (arcade boards, oddballs…). "
                 + "Leave a row on “(default: …)” to keep the built-in mapping; pick a console (or “(none)”) to force it.",
        };

        var filterHost = new Panel { Dock = DockStyle.Top, Height = S(30), Padding = new Padding(S(10), S(2), S(10), S(4)) };
        _filter = new TextBox { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle };
        _filter.TextChanged += (_, _) => ApplyFilter();
        var filterLbl = new Label { Dock = DockStyle.Left, Text = "Filter ", ForeColor = LiteBoxTheme.SubFg, AutoSize = true, Padding = new Padding(0, S(4), 0, 0) };
        filterHost.Controls.Add(_filter); filterHost.Controls.Add(filterLbl);

        var consoleKeys = RaPlatformMap.AllConsoleKeys().ToList();

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = LiteBoxTheme.Bg, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(60, 60, 64),
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = LiteBoxTheme.Panel2;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = LiteBoxTheme.Fg;
        _grid.DefaultCellStyle.BackColor = LiteBoxTheme.Bg; _grid.DefaultCellStyle.ForeColor = LiteBoxTheme.Fg;
        _grid.DefaultCellStyle.SelectionBackColor = LiteBoxTheme.Accent; _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.RowTemplate.Height = S(22);

        var colPlat = new DataGridViewTextBoxColumn { HeaderText = "Platform", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 62 };
        var colCons = new DataGridViewComboBoxColumn { HeaderText = "RA console", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 38, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
        _grid.Columns.Add(colPlat);
        _grid.Columns.Add(colCons);
        // Commit a combo pick immediately (so OK reads the latest value without leaving the cell).
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.DataError += (_, e) => { e.ThrowException = false; };

        // Per-row combo: 1st item "(default: <auto>)" = keep the built-in mapping; then "(none)" + every
        // console key. Picking anything but the default forces an override. (Per-CELL items because the
        // default label differs per platform.)
        var ov = RaPlatformMap.GetOverrides();
        var keySet = new HashSet<string>(consoleKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var name in (platforms ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string auto = RaPlatformMap.AutoKeyFor(name);
            string defItem = "(default: " + (string.IsNullOrEmpty(auto) ? "—" : auto) + ")";

            int i = _grid.Rows.Add();
            var cell = (DataGridViewComboBoxCell)_grid.Rows[i].Cells[1];   // accessing the row unshares it → per-cell Items
            cell.Items.Add(defItem);
            cell.Items.Add(None);
            foreach (var k in consoleKeys) cell.Items.Add(k);

            string sel = defItem;
            if (ov.TryGetValue(name, out var ovk)) sel = string.IsNullOrEmpty(ovk) ? None : (keySet.Contains(ovk) ? ovk : defItem);
            _grid.Rows[i].Cells[0].Value = name;
            cell.Value = sel;
        }

        var footer = new FooterBar();
        footer.AddButton("Cancel", LiteBoxTheme.CancelBtn, (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        footer.AddButton("Save", LiteBoxTheme.Ok, (_, _) => { Persist(); DialogResult = DialogResult.OK; Close(); });

        Controls.Add(_grid);
        Controls.Add(filterHost);
        Controls.Add(help);
        Controls.Add(footer);
        _grid.BringToFront();
    }

    private void ApplyFilter()
    {
        string q = (_filter.Text ?? "").Trim();
        try
        {
            _grid.CurrentCell = null;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                var name = r.Cells[0].Value as string ?? "";
                r.Visible = q.Length == 0 || name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { }
    }

    private void Persist()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _grid.Rows)
        {
            var name = r.Cells[0].Value as string;
            if (string.IsNullOrWhiteSpace(name)) continue;
            string v = r.Cells[1].Value as string ?? "";
            if (v.StartsWith("(default", StringComparison.OrdinalIgnoreCase)) continue;   // default → no override
            map[name] = (v == None) ? "" : v;                                             // "(none)" → none; else force the key
        }
        RaPlatformMap.SaveOverrides(map);
    }
}
