// RetroAchievements module config panel — LiteBox-native parity with ExtendDB's "RetroAchievements" tab.
//
// Blocks LB's scan-on-select hashing and resolves each game's RA set by hashing the actual ROM with our own
// RAHasher (per ROM / per version). This page drives that resolver's configuration:
//
//   • a header action bar — Refresh all (re-pull every mapped console's catalogue), Scan all (resolve every
//     enabled platform's ROMs), Clear RA data (wipe the per-game hash/id, keep the catalogue), and the
//     Auto-update trigger (On select / On launch);
//   • a per-platform grid — Platform · RA console (per-cell combo leading with "default: <preset>") · ID
//     (click → the console's games page on retroachievements.org) · Enabled · Refresh (+ catalogue age) ·
//     Scan (that platform's ROMs).
//
// Backends reused verbatim: RaPlatformMap (preset + override mapping), RaCatalogLite (catalogue refresh +
// age), RaScanProgress (the modal scan), plus RaPanelSupport (this feature's additive config plumbing +
// the scan launcher, since MainWindow.RunRaScan is private). Built with ModulePanelKit for the shared look.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Ra;

namespace LbApiHost.Host.Options;

internal static class RaPanel
{
    private const int ColPlat = 0, ColConsole = 1, ColId = 2, ColEnabled = 3, ColRefresh = 4, ColScan = 5;

    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var Bg = ModulePanelKit.Bg; var Fg = ModulePanelKit.Fg; var Sub = ModulePanelKit.Sub; var Field = ModulePanelKit.Field;

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(16), S(12), S(16), S(10)) };

        // ── Grid (fills the remaining space; scrolls internally) ─────────────────────────────────
        var grid = ModulePanelKit.Grid(dpiS, readOnly);
        grid.Dock = DockStyle.Fill;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        grid.AllowUserToResizeColumns = true;

        var colPlat = new DataGridViewTextBoxColumn { HeaderText = "Platform", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 40 };
        var colConsole = new DataGridViewComboBoxColumn
        {
            HeaderText = "RetroAchievements console", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 32,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, ReadOnly = readOnly,
        };
        var colId = new DataGridViewLinkColumn
        {
            HeaderText = "ID", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = S(48),
            LinkColor = ModulePanelKit.Accent, ActiveLinkColor = Color.White, VisitedLinkColor = ModulePanelKit.Accent,
            TrackVisitedState = false, ToolTipText = "Open this console's games page on retroachievements.org",
        };
        var colEnabled = new DataGridViewCheckBoxColumn { HeaderText = "Enabled", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = S(66), ReadOnly = readOnly };
        var colRefresh = new DataGridViewButtonColumn
        {
            HeaderText = "Refresh", UseColumnTextForButtonValue = false, FlatStyle = FlatStyle.Flat,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = S(140),
            ToolTipText = "Force a catalogue refresh for this console now",
        };
        var colScan = new DataGridViewButtonColumn
        {
            HeaderText = "Scan", UseColumnTextForButtonValue = false, FlatStyle = FlatStyle.Flat,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = S(96),
            ToolTipText = "Hash this platform's ROMs now and store their RetroAchievements hash/id",
        };
        foreach (var bc in new[] { colRefresh, colScan })
        {
            bc.DefaultCellStyle.BackColor = Field; bc.DefaultCellStyle.ForeColor = Fg;
            bc.DefaultCellStyle.SelectionBackColor = Field; bc.DefaultCellStyle.SelectionForeColor = Fg;
            bc.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        grid.Columns.AddRange(colPlat, colConsole, colId, colEnabled, colRefresh, colScan);
        grid.DataError += (_, e) => e.ThrowException = false;   // swallow combo "value not valid" noise

        var allKeys = RaPlatformMap.AllConsoleKeys().ToList();
        bool building = false;

        // Effective RAHasher key from a row's console cell: "default: …" → the row's preset (Tag);
        // "(none)" → ""; else the chosen key itself.
        string ResolveKey(DataGridViewRow row)
        {
            var v = row.Cells[ColConsole].Value as string ?? "";
            if (v.StartsWith("default", StringComparison.OrdinalIgnoreCase)) return row.Tag as string ?? "";
            if (v == "(none)") return "";
            return v;
        }

        string RefreshText(int consoleId)
        {
            if (consoleId <= 0) return "";
            double age;
            try { age = RaCatalogLite.CacheAgeHours(consoleId); } catch { age = double.MaxValue; }
            if (double.IsInfinity(age) || age == double.MaxValue) return "Refresh · never";
            if (age < 1) return "Refresh · <1h";
            if (age < 48) return $"Refresh · {(int)Math.Round(age)}h";
            return $"Refresh · {(int)Math.Round(age / 24.0)}d";
        }

        void SyncRowState(int rowIndex)
        {
            var row = grid.Rows[rowIndex];
            int id = RaPanelActions.ConsoleIdForKey(ResolveKey(row));
            row.Cells[ColId].Value = id > 0 ? id.ToString() : "";
            row.Cells[ColRefresh].Value = RefreshText(id);
            row.Cells[ColScan].Value = id > 0 ? "Scan" : "";
            var en = (DataGridViewCheckBoxCell)row.Cells[ColEnabled];
            if (id <= 0) { en.Value = false; en.ReadOnly = true; }
            else en.ReadOnly = readOnly;
        }

        void AddRow(string platform, string presetKey, string? overrideKey, bool hasOverride)
        {
            int idx = grid.Rows.Add();
            var row = grid.Rows[idx];
            row.Tag = presetKey;   // resolves the "default" item back to a key
            row.Cells[ColPlat].Value = platform;

            var cell = (DataGridViewComboBoxCell)row.Cells[ColConsole];   // touching the row unshares it → per-cell Items
            string defItem = "default:  " + (string.IsNullOrEmpty(presetKey) ? "(none)" : presetKey);
            cell.Items.Clear();
            cell.Items.Add(defItem);
            cell.Items.Add("(none)");
            foreach (var k in allKeys) cell.Items.Add(k);

            string sel = defItem;
            if (hasOverride)
                sel = string.IsNullOrEmpty(overrideKey) ? "(none)" : (allKeys.Contains(overrideKey, StringComparer.OrdinalIgnoreCase) ? overrideKey! : defItem);
            cell.Value = sel;

            string effKey = hasOverride ? (overrideKey ?? "") : presetKey;
            bool defaultEnabled = RaPanelActions.ConsoleIdForKey(effKey) > 0;
            ((DataGridViewCheckBoxCell)row.Cells[ColEnabled]).Value = RaPanelConfig.IsEnabled(platform, defaultEnabled);
            SyncRowState(idx);
        }

        void Populate()
        {
            building = true;
            try
            {
                grid.Rows.Clear();
                var overrides = RaPlatformMap.GetOverrides();   // name → key ("" = explicit none)
                foreach (var name in RaPanelActions.PlatformNames())
                {
                    string preset = RaPlatformMap.AutoKeyFor(name);   // "" when the hardlist doesn't map it
                    if (overrides.TryGetValue(name, out var ovk)) AddRow(name, preset, ovk ?? "", true);
                    else AddRow(name, preset, null, false);
                }
            }
            catch { }
            finally { building = false; }
        }

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (building || e.RowIndex < 0) return;
            if (e.ColumnIndex == ColConsole) SyncRowState(e.RowIndex);
        };
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            if (e.ColumnIndex == ColId)
            {
                if (int.TryParse(row.Cells[ColId].Value as string, out var id)) RaPanelActions.OpenConsoleGames(id);
            }
            else if (e.ColumnIndex == ColRefresh)
            {
                int id = RaPanelActions.ConsoleIdForKey(ResolveKey(row));
                if (id > 0) RefreshOneRow(e.RowIndex, id);
            }
            else if (e.ColumnIndex == ColScan)
            {
                if (readOnly) return;
                int id = RaPanelActions.ConsoleIdForKey(ResolveKey(row));
                if (id <= 0) return;
                string plat = row.Cells[ColPlat].Value as string ?? "";
                if (string.IsNullOrEmpty(plat)) return;
                if (!RaPanelActions.RunScan(root.FindForm(), plat, full: false))
                    MessageBox.Show(root.FindForm(), "No games found for this platform.", "RetroAchievements");
                SyncRowState(e.RowIndex);   // the scan may have refreshed the catalogue
            }
        };

        // Per-row Refresh runs on a background thread (a first catalogue pull can take a while) with a live
        // "Refreshing…" label, then re-reads the age.
        void RefreshOneRow(int rowIndex, int consoleId)
        {
            try { grid.Rows[rowIndex].Cells[ColRefresh].Value = "Refreshing…"; } catch { }
            Task.Run(() =>
            {
                try { RaCatalogEngine.RefreshOne(consoleId); } catch { }   // engine: guards + store + IGame sync
                try
                {
                    if (grid.IsHandleCreated)
                        grid.BeginInvoke((Action)(() => { try { if (rowIndex < grid.Rows.Count) grid.Rows[rowIndex].Cells[ColRefresh].Value = RefreshText(consoleId); } catch { } }));
                }
                catch { }
            });
        }

        Populate();

        // ── Header + action bar (fixed height, docked above the grid) ────────────────────────────
        var top = new Panel { Dock = DockStyle.Top, Height = S(150), BackColor = Bg };

        var head = ModulePanelKit.Header("RetroAchievements", dpiS); head.Location = new Point(S(2), S(2)); top.Controls.Add(head);
        var cap = ModulePanelKit.Caption(
            "Computes the correct RetroAchievements hash per ROM / version with our RAHasher — archives are "
            + "hashed in memory (no unpacking), every entry is remembered, and the launched entry corrects the "
            + "game at launch. Each platform's console leads with \"default: …\" (the built-in preset) — pick "
            + "another to force it, or click an ID for its games page.",
            dpiS, maxWidth: 720);
        cap.Location = new Point(S(2), S(26)); top.Controls.Add(cap);

        var bar = new FlowLayoutPanel
        {
            Location = new Point(S(2), S(88)), AutoSize = true, BackColor = Bg,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        var btnRefreshAll = ModulePanelKit.Button("Refresh all", dpiS);
        var btnScanAll = ModulePanelKit.Button("Scan all", dpiS, readOnly);
        var btnScanAllForce = ModulePanelKit.Button("▾", dpiS, readOnly);
        var btnClear = ModulePanelKit.Button("Clear RA data", dpiS, readOnly);
        btnScanAll.Margin = new Padding(S(8), 0, 0, 0);
        btnScanAllForce.Margin = new Padding(0);
        btnClear.Margin = new Padding(S(8), 0, S(4), 0);

        var lblMode = ModulePanelKit.Caption("Auto-update:", dpiS); lblMode.Margin = new Padding(S(18), S(7), S(4), 0);
        var mode = ModulePanelKit.Combo(dpiS, readOnly, width: 108);
        mode.Margin = new Padding(0, S(3), 0, 0);
        mode.Items.AddRange(new object[] { "On select", "On launch" });
        mode.SelectedIndex = RaPanelConfig.Mode == RaPanelConfig.ModeOnLaunch ? 1 : 0;

        bar.Controls.Add(btnRefreshAll);
        bar.Controls.Add(btnScanAll);
        bar.Controls.Add(btnScanAllForce);
        bar.Controls.Add(btnClear);
        bar.Controls.Add(lblMode);
        bar.Controls.Add(mode);
        top.Controls.Add(bar);

        // Config note: RA needs a Web API key + username in LaunchBox's Settings.xml.
        var note = new Label
        {
            AutoSize = true, Location = new Point(S(2), S(126)), BackColor = Bg,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
            ForeColor = SafeConfigured() ? Sub : Color.FromArgb(210, 140, 90),
            Text = SafeConfigured()
                ? "RetroAchievements account detected (key + username in LaunchBox Settings)."
                : "No RetroAchievements key/username in LaunchBox Settings — hashing still works, but sets can't be matched to your account.",
        };
        top.Controls.Add(note);

        // ── Action-bar handlers ──────────────────────────────────────────────────────────────────
        btnRefreshAll.Click += (_, _) =>
        {
            var ids = new HashSet<int>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (!(r.Cells[ColEnabled].Value is bool en && en)) continue;
                int id = RaPanelActions.ConsoleIdForKey(ResolveKey(r));
                if (id > 0) ids.Add(id);
            }
            if (ids.Count == 0) { MessageBox.Show(root.FindForm(), "No enabled, mapped platforms to refresh.", "RetroAchievements"); return; }
            btnRefreshAll.Enabled = false; btnRefreshAll.Text = "Refreshing…";
            Task.Run(() =>
            {
                int ok = RaPanelActions.RefreshConsoles(ids);
                try
                {
                    if (grid.IsHandleCreated)
                        grid.BeginInvoke((Action)(() =>
                        {
                            for (int i = 0; i < grid.Rows.Count; i++) SyncRowState(i);
                            btnRefreshAll.Text = $"Refresh all  ✓ {ok}/{ids.Count}";
                            btnRefreshAll.Enabled = true;
                        }));
                }
                catch { }
            });
        };
        btnScanAll.Click += (_, _) =>
        {
            if (readOnly) return;
            if (!RaPanelActions.RunScan(root.FindForm(), null, full: false))
                MessageBox.Show(root.FindForm(), "No games found in the enabled platforms.", "RetroAchievements");
            for (int i = 0; i < grid.Rows.Count; i++) SyncRowState(i);
        };
        btnScanAllForce.Click += (_, _) =>
        {
            if (readOnly) return;
            if (MessageBox.Show(root.FindForm(),
                    "Force a FULL rescan of every enabled platform? This recomputes the hash of every game (slow) — "
                    + "use it to pick up sets that appeared in RetroAchievements after a game was first resolved.",
                    "RetroAchievements — force rescan", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (!RaPanelActions.RunScan(root.FindForm(), null, full: true))
                MessageBox.Show(root.FindForm(), "No games found in the enabled platforms.", "RetroAchievements");
            for (int i = 0; i < grid.Rows.Count; i++) SyncRowState(i);
        };
        btnClear.Click += (_, _) =>
        {
            if (readOnly) return;
            if (MessageBox.Show(root.FindForm(),
                    "Clear all RetroAchievements game data?\n\nWipes the RetroAchievements hash + id on every game. "
                    + "The downloaded catalogue is KEPT. Games re-resolve on the next scan / select.",
                    "RetroAchievements — Clear RA data", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            btnClear.Enabled = false; btnClear.Text = "Clearing…";
            Task.Run(() =>
            {
                int n = RaPanelActions.ClearRaData();
                try
                {
                    if (grid.IsHandleCreated)
                        grid.BeginInvoke((Action)(() => { btnClear.Text = $"Clear RA data  ✓ ({n})"; btnClear.Enabled = true; }));
                }
                catch { }
            });
        };

        root.Controls.Add(grid);
        root.Controls.Add(top);
        grid.BringToFront();

        // ── Apply: persist mapping overrides (RaPlatformMap) + the panel's mode/enabled diffs ─────
        void Apply()
        {
            try
            {
                string modeVal = mode.SelectedIndex == 1 ? RaPanelConfig.ModeOnLaunch : RaPanelConfig.ModeOnSelect;

                var overridesOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var enabledDiffs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow r in grid.Rows)
                {
                    string plat = (r.Cells[ColPlat].Value as string ?? "").Trim();
                    if (plat.Length == 0 || !seen.Add(plat)) continue;

                    string auto = r.Tag as string ?? "";
                    string key = ResolveKey(r);
                    if (!string.Equals(key, auto, StringComparison.OrdinalIgnoreCase))
                        overridesOut[plat] = key;   // diff from the preset → store (incl. "" = explicit none)

                    bool defaultEnabled = RaPanelActions.ConsoleIdForKey(key) > 0;
                    bool en = r.Cells[ColEnabled].Value is bool bb && bb;
                    if (en != defaultEnabled) enabledDiffs[plat] = en;
                }
                RaPlatformMap.SaveOverrides(overridesOut);
                RaPanelConfig.Save(modeVal, enabledDiffs);
            }
            catch { }
        }

        return (root, Apply);
    }

    private static bool SafeConfigured()
    {
        try { return RaService.Configured; } catch { return false; }
    }
}
