// RetroAchievements options — the single RA config surface, hosted in the LB · Integrations →
// RetroAchievements tab (NOT a module, NOT a separate left-menu page). It owns the whole feature:
//
//   • Account: Username + API Key (LaunchBox Settings.xml) and Password (stored ENCRYPTED in
//     ra-panel.json — RA never keeps the password; we keep it only to auto-renew the session token).
//     Password and API Key are validated live (debounced) with a red/green marker. Two links go to
//     RA's register / API-key pages.
//   • Token auto-renewal: "renew every N days" — a background re-login rewrites Settings.xml's token
//     before it expires (LaunchBox never refreshes it). Fail-safe: a failed login never touches the
//     working token (see RaTokenRenew).
//   • Resolution engine config: the auto-update trigger, RAHasher override, catalogue refresh cadence,
//     the startup rolling refresh, and the per-platform grid (console mapping / Enabled / Refresh / Scan).
//
// The header stacks in a TableLayoutPanel so rows never overlap; the grid fills the rest and scrolls.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host;
using LbApiHost.Host.Data;
using LbApiHost.Host.Ra;

namespace LbApiHost.Host.Options;

internal static class RaPanel
{
    private const int ColPlat = 0, ColConsole = 1, ColId = 2, ColEnabled = 3, ColRefresh = 4, ColScan = 5;

    private static readonly Color Good = Color.FromArgb(120, 200, 140);
    private static readonly Color Bad = Color.FromArgb(222, 110, 110);

    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly, LbSettingsStore settings, LiteBoxConfig cfg)
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
            try { age = RaStore.CatalogueAgeHours(consoleId); } catch { age = double.MaxValue; }
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
            row.Tag = presetKey;
            row.Cells[ColPlat].Value = platform;

            var cell = (DataGridViewComboBoxCell)row.Cells[ColConsole];
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
                var overrides = RaPlatformMap.GetOverrides();
                foreach (var name in RaPanelActions.PlatformNames())
                {
                    string preset = RaPlatformMap.AutoKeyFor(name);
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
                SyncRowState(e.RowIndex);
            }
        };

        void RefreshOneRow(int rowIndex, int consoleId)
        {
            try { grid.Rows[rowIndex].Cells[ColRefresh].Value = "Refreshing…"; } catch { }
            Task.Run(() =>
            {
                try { RaCatalogEngine.RefreshOne(consoleId); } catch { }
                try
                {
                    if (grid.IsHandleCreated)
                        grid.BeginInvoke((Action)(() => { try { if (rowIndex < grid.Rows.Count) grid.Rows[rowIndex].Cells[ColRefresh].Value = RefreshText(consoleId); } catch { } }));
                }
                catch { }
            });
        }

        Populate();

        // ── Header (auto-sizing vertical stack — no overlaps) ────────────────────────────────────
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = Bg,
            Padding = new Padding(0, 0, 0, S(6)),
        };
        void AddHeaderRow(Control c) { c.Margin = new Padding(S(2), S(2), S(2), S(2)); top.Controls.Add(c); }

        AddHeaderRow(ModulePanelKit.Header("RetroAchievements", dpiS));
        AddHeaderRow(ModulePanelKit.Caption(
            "Computes the correct RetroAchievements hash per ROM / version with our RAHasher, keeps your "
            + "session token fresh, and resolves each game's set. Step 1 (username + password) powers in-game "
            + "unlocking; Step 2 (API key) matches sets and shows progress.", dpiS, maxWidth: 760));

        // Small labelled text field factory for the account block.
        Label Lbl(string t) => new() { Text = t, AutoSize = true, ForeColor = Fg, BackColor = Bg, Margin = new Padding(0, S(6), S(6), 0) };
        Label Status() => new() { AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(S(6), S(7), 0, 0), Text = "" };
        TextBox Tb(string val, int w, bool pwd = false) => new()
        {
            Text = val, Width = S(w), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f), UseSystemPasswordChar = pwd, Enabled = !readOnly, Margin = new Padding(0, S(3), 0, 0),
        };

        // Account grid: Username / Password (+ validity) / API Key (+ validity).
        var acct = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, BackColor = Bg };
        var user = Tb(settings.Get("RetroAchievementsUsername"), 240);
        var pwd = Tb(RaPanelConfig.PasswordClear, 240, pwd: true);
        var pwdStatus = Status();
        var key = Tb(settings.Get("RetroAchievementsApiKey"), 320);
        var keyStatus = Status();
        acct.Controls.Add(Lbl("Username"), 0, 0); acct.Controls.Add(user, 1, 0); acct.Controls.Add(new Label { Width = 0, Height = 0, Margin = new Padding(0) }, 2, 0);
        acct.Controls.Add(Lbl("Password"), 0, 1); acct.Controls.Add(pwd, 1, 1); acct.Controls.Add(pwdStatus, 2, 1);
        acct.Controls.Add(Lbl("API Key"), 0, 2); acct.Controls.Add(key, 1, 2); acct.Controls.Add(keyStatus, 2, 2);
        AddHeaderRow(acct);

        // ── Live, debounced validity (red/green). Values captured on the UI thread before the network call. ──
        Action WireValidity(TextBox field, Label status, Func<string, string, bool> check)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 800 };
            int seq = 0;
            void Restart()
            {
                timer.Stop();
                if (string.IsNullOrWhiteSpace(field.Text) || string.IsNullOrWhiteSpace(user.Text)) { status.Text = ""; return; }
                status.ForeColor = Sub; status.Text = "…";
                timer.Start();
            }
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                int mine = ++seq;
                string u = user.Text.Trim(), v = field.Text;
                status.ForeColor = Sub; status.Text = "checking…";
                Task.Run(() =>
                {
                    bool ok = false; try { ok = check(u, v); } catch { }
                    try
                    {
                        if (!field.IsDisposed && field.IsHandleCreated)
                            field.BeginInvoke((Action)(() =>
                            {
                                if (mine != seq) return;
                                status.ForeColor = ok ? Good : Bad;
                                status.Text = ok ? "● valid" : "● invalid";
                            }));
                    }
                    catch { }
                });
            };
            field.TextChanged += (_, _) => Restart();
            root.Disposed += (_, _) => { try { timer.Dispose(); } catch { } };
            return Restart;
        }
        var recheckPwd = WireValidity(pwd, pwdStatus, (u, p) => RaConnect.Login(u, p).Ok);
        var recheckKey = WireValidity(key, keyStatus, (u, k) => RaConnect.ValidateApiKey(u, k));
        user.TextChanged += (_, _) => { recheckPwd(); recheckKey(); };
        // Validate the pre-filled credentials on load — but only once the fields actually have a window handle.
        // At build time (before the RA tab is realised) they don't, so the debounced check's result would be
        // dropped (its BeginInvoke needs a handle) and the label would stay stuck on "checking…" until the user
        // edited the field. HandleCreated fires when the tab is first shown.
        void InitValidate(TextBox field, Action recheck)
        {
            if (string.IsNullOrWhiteSpace(field.Text)) return;
            if (field.IsHandleCreated) recheck();
            else field.HandleCreated += (_, _) => recheck();
        }
        InitValidate(pwd, recheckPwd);
        InitValidate(key, recheckKey);

        // ── Token status + renewal cadence + Renew now ──────────────────────────────────────────
        var tokenRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Bg };
        var lblTok = new Label { AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, S(8), S(4), 0) };
        void PaintTokenLabel()
        {
            var d = RaPanelConfig.TokenObtainedUtc;
            lblTok.Text = "Session token: " + (d.HasValue ? "obtained " + d.Value.ToLocalTime().ToString("yyyy-MM-dd") : "not yet renewed by LiteBox")
                          + "   ·   renew every";
        }
        PaintTokenLabel();
        var numRenew = new NumericUpDown
        {
            Minimum = 1, Maximum = 365, Value = RaPanelConfig.RenewEveryDays, Width = S(52),
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            Enabled = !readOnly, Margin = new Padding(0, S(5), S(4), 0),
        };
        var lblDays = new Label { Text = "days", AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, S(8), S(12), 0) };
        var btnRenew = ModulePanelKit.Button("Renew now", dpiS, readOnly);
        btnRenew.Margin = new Padding(0, S(2), 0, 0);
        var lblRenewMsg = new Label { AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(S(8), S(8), 0, 0), Text = "" };
        tokenRow.Controls.Add(lblTok); tokenRow.Controls.Add(numRenew); tokenRow.Controls.Add(lblDays);
        tokenRow.Controls.Add(btnRenew); tokenRow.Controls.Add(lblRenewMsg);
        AddHeaderRow(tokenRow);

        btnRenew.Click += (_, _) =>
        {
            if (readOnly) return;
            string u = user.Text.Trim();
            string p = pwd.Text.Length > 0 ? pwd.Text : RaPanelConfig.PasswordClear;
            if (u.Length == 0 || p.Length == 0)
            {
                lblRenewMsg.ForeColor = Bad; lblRenewMsg.Text = "● need username + password";
                return;
            }
            btnRenew.Enabled = false; lblRenewMsg.ForeColor = Sub; lblRenewMsg.Text = "renewing…";
            int days = (int)numRenew.Value;
            Task.Run(() =>
            {
                var r = RaConnect.Login(u, p);
                bool ok = r.Ok && !string.IsNullOrEmpty(r.Token) && RaTokenStore.Write(r.Token);
                if (ok) RaPanelConfig.SaveAuth(p, DateTime.UtcNow, days);   // persist password + fresh timestamp
                try
                {
                    if (!btnRenew.IsDisposed && btnRenew.IsHandleCreated)
                        btnRenew.BeginInvoke((Action)(() =>
                        {
                            // Keep the settings store in sync so a later journal flush (window close) writes
                            // the SAME fresh token, not the stale one it loaded.
                            if (ok) { try { settings.Set("RetroAchievementsToken", r.Token); } catch { } }
                            lblRenewMsg.ForeColor = ok ? Good : Bad;
                            lblRenewMsg.Text = ok ? "● token renewed" : "● failed — " + r.Error;
                            if (ok) PaintTokenLabel();
                            btnRenew.Enabled = true;
                        }));
                }
                catch { }
            });
        };

        // ── Register / retrieve links ───────────────────────────────────────────────────────────
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Bg };
        LinkLabel Link(string text, string url)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), LinkColor = ModulePanelKit.Accent, ActiveLinkColor = Color.White, Margin = new Padding(0, S(4), S(18), 0) };
            l.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            return l;
        }
        links.Controls.Add(Link("Register an account", "https://retroachievements.org/createaccount.php"));
        links.Controls.Add(Link("Retrieve your API key", "https://retroachievements.org/login"));
        AddHeaderRow(links);

        // ── Action bar: Refresh all / Scan all (+Force) / Clear + Auto-update ────────────────────
        var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Bg, Margin = new Padding(S(2), S(8), S(2), 0) };
        var btnRefreshAll = ModulePanelKit.Button("Refresh all", dpiS);
        var btnScanAll = ModulePanelKit.Button("Scan all", dpiS, readOnly);
        var btnScanAllForce = ModulePanelKit.Button("▾", dpiS, readOnly);
        var btnClear = ModulePanelKit.Button("Clear RA data", dpiS, readOnly);
        btnScanAll.Margin = new Padding(S(8), 0, 0, 0);
        btnScanAllForce.Margin = new Padding(0);
        btnClear.Margin = new Padding(S(8), 0, S(4), 0);
        var lblMode = new Label { Text = "Auto-update:", AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(S(18), S(7), S(4), 0) };
        var mode = ModulePanelKit.Combo(dpiS, readOnly, width: 108);
        mode.Margin = new Padding(0, S(3), 0, 0);
        mode.Items.AddRange(new object[] { "On select", "On launch" });
        mode.SelectedIndex = RaPanelConfig.Mode == RaPanelConfig.ModeOnLaunch ? 1 : 0;
        bar.Controls.Add(btnRefreshAll); bar.Controls.Add(btnScanAll); bar.Controls.Add(btnScanAllForce);
        bar.Controls.Add(btnClear); bar.Controls.Add(lblMode); bar.Controls.Add(mode);
        AddHeaderRow(bar);

        // ── Engine options: RAHasher override + catalogue refresh cadence ────────────────────────
        var opts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Bg };
        var lblHasher = new Label { Text = "RAHasher:", AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, S(7), S(4), 0) };
        var txtHasher = new TextBox
        {
            Width = S(280), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f), Text = RaPanelConfig.HasherPath,
            PlaceholderText = @"(default: ThirdParty\RetroAchievements)", Enabled = !readOnly, Margin = new Padding(0, S(4), 0, 0),
        };
        var btnBrowseHasher = ModulePanelKit.Button("…", dpiS, readOnly);
        btnBrowseHasher.Margin = new Padding(S(2), 0, 0, 0);
        btnBrowseHasher.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "Pick the RAHasher executable", Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*", CheckFileExists = true };
            try { if (txtHasher.Text.Trim() is { Length: > 0 } cur) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(cur); } catch { }
            if (dlg.ShowDialog(root.FindForm()) == DialogResult.OK) txtHasher.Text = dlg.FileName;
        };
        var lblRefresh = new Label { Text = "Catalogue refresh (h):", AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(S(18), S(7), S(4), 0) };
        var numRefresh = new NumericUpDown
        {
            Minimum = 1, Maximum = 168, Value = RaPanelConfig.RefreshHours, Width = S(56),
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            Enabled = !readOnly, Margin = new Padding(0, S(4), 0, 0),
        };
        opts.Controls.Add(lblHasher); opts.Controls.Add(txtHasher); opts.Controls.Add(btnBrowseHasher);
        opts.Controls.Add(lblRefresh); opts.Controls.Add(numRefresh);
        AddHeaderRow(opts);

        // (The old "startup rolling refresh" opt-in is gone: the catalogue heartbeat now always refreshes every
        //  absent console + up to 3 stale ones per tick — see RaCatalogEngine.RefreshDue.)

        // Config note: RA needs a Web API key + username in LaunchBox's Settings.xml.
        var note = new Label
        {
            AutoSize = true, BackColor = Bg, Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
            ForeColor = SafeConfigured() ? Sub : Color.FromArgb(210, 140, 90),
            Text = SafeConfigured()
                ? "RetroAchievements account detected (key + username in LaunchBox Settings)."
                : "No RetroAchievements key/username yet — hashing still works, but sets can't be matched to your account.",
            Margin = new Padding(S(2), S(6), 0, 0),
        };
        AddHeaderRow(note);

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

        // ── Apply ────────────────────────────────────────────────────────────────────────────────
        void Apply()
        {
            try
            {
                if (!readOnly)
                {
                    // Credentials: username + API key round-trip to Settings.xml; password stays LiteBox-side (encrypted).
                    if (user.Text != settings.Get("RetroAchievementsUsername")) settings.Set("RetroAchievementsUsername", user.Text.Trim());
                    if (key.Text != settings.Get("RetroAchievementsApiKey")) settings.Set("RetroAchievementsApiKey", key.Text.Trim());

                    string prevPwd = RaPanelConfig.PasswordClear;
                    bool pwdChanged = pwd.Text != prevPwd;
                    RaPanelConfig.SaveAuth(pwd.Text, null, (int)numRenew.Value);
                    if (pwdChanged && pwd.Text.Length > 0)
                    {
                        // New password → the token may be stale; make it due and kick a background renewal now.
                        // (The renewal reads the live read-only state itself — no mode to pass.)
                        RaPanelConfig.MarkTokenStale();
                        RaTokenRenew.MaybeRenewAsync();
                    }
                }

                string modeVal = mode.SelectedIndex == 1 ? RaPanelConfig.ModeOnLaunch : RaPanelConfig.ModeOnSelect;
                var overridesOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var enabledDiffs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow r in grid.Rows)
                {
                    string plat = (r.Cells[ColPlat].Value as string ?? "").Trim();
                    if (plat.Length == 0 || !seen.Add(plat)) continue;

                    string auto = r.Tag as string ?? "";
                    string key2 = ResolveKey(r);
                    if (!string.Equals(key2, auto, StringComparison.OrdinalIgnoreCase))
                        overridesOut[plat] = key2;

                    bool defaultEnabled = RaPanelActions.ConsoleIdForKey(key2) > 0;
                    bool en = r.Cells[ColEnabled].Value is bool bb && bb;
                    if (en != defaultEnabled) enabledDiffs[plat] = en;
                }
                RaPlatformMap.SaveOverrides(overridesOut);
                RaPanelConfig.Save(modeVal, enabledDiffs, txtHasher.Text, (int)numRefresh.Value);
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
