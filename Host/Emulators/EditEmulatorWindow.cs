// Edit Emulator window — full per-emulator configuration for EVERY emulator,
// enriched by CAPABILITY when an integration plugin claims it (never by plugin
// name): version/update block when GetCurrentVersion answers, Dependency Files
// when GetBiosFilesForPlatform returns rows, core hints when IEmulatorWithCores.
//
// Sections (LB Edit Emulator parity, on the OptionsWindow shell):
//   Details · Associated Platforms · Dependency Files (plugin) · Startup Screen
//   · Pause Screen · 8 AutoHotkey script editors (Pause / Resume / Reset / Save
//   State / Load State / Swap Discs / Running / Exit).
//
// Writes go through the HostEmulator/HostEmulatorPlatform SETTERS (SDK props +
// ILiteBoxFields for the off-SDK pause toggles) — every change lands in the
// GameStore op-log exactly like any plugin write. Nothing is written before
// Apply/OK; in READ-ONLY mode every input is disabled and apply is a no-op.

#nullable enable

using System.IO;
using LbApiHost.Host.Options;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Emulators;

internal static class EditEmulatorWindow
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color Panel2 = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;
    private static readonly Color Good = Color.FromArgb(120, 220, 140);
    private static readonly Color Bad = Color.FromArgb(235, 120, 120);

    public static void Open(IEmulator emu, bool readOnly, IWin32Window? owner, string lbRoot)
    {
        string title = Safe(() => emu.Title) ?? "Emulator";
        using var w = new OptionsWindow($"Edit Emulator — {title}{(readOnly ? "   [READ-ONLY]" : "")}");
        float s = LiteBoxTheme.DpiScale(w);

        var (details, applyDetails) = BuildDetails(emu, lbRoot, s);
        w.AddSection("Details", details, applyDetails);

        var (plats, applyPlats) = BuildPlatforms(emu, readOnly, s);
        w.AddSection("Associated Platforms", plats, applyPlats);

        var deps = BuildDependencies(emu, lbRoot, s);
        if (deps != null) w.AddSection("Dependency Files", deps);

        var (startup, applyStartup) = BuildStartup(emu, s);
        w.AddSection("Startup Screen", startup, applyStartup);

        var (pause, applyPause) = BuildPause(emu, s);
        w.AddSection("Pause Screen", pause, applyPause);

        AddScript(w, emu, "Pause Script", e => e.PauseAutoHotkeyScript, (e, v) => e.PauseAutoHotkeyScript = v);
        AddScript(w, emu, "Resume Script", e => e.ResumeAutoHotkeyScript, (e, v) => e.ResumeAutoHotkeyScript = v);
        AddScript(w, emu, "Reset Game Script", e => e.ResetAutoHotkeyScript, (e, v) => e.ResetAutoHotkeyScript = v);
        AddScript(w, emu, "Save State Script", e => e.SaveStateAutoHotkeyScript, (e, v) => e.SaveStateAutoHotkeyScript = v);
        AddScript(w, emu, "Load State Script", e => e.LoadStateAutoHotkeyScript, (e, v) => e.LoadStateAutoHotkeyScript = v);
        AddScript(w, emu, "Swap Discs Script", e => e.SwapDiscsAutoHotkeyScript, (e, v) => e.SwapDiscsAutoHotkeyScript = v);
        AddScript(w, emu, "Running Script", e => e.AutoHotkeyScript, (e, v) => e.AutoHotkeyScript = v);
        AddScript(w, emu, "Exit Script", e => e.ExitAutoHotkeyScript, (e, v) => e.ExitAutoHotkeyScript = v);

        if (readOnly) DisableAllInputs(w);
        w.ShowDialog(owner);
    }

    // ── Details ────────────────────────────────────────────────────────
    private static (Control, Action) BuildDetails(IEmulator emu, string lbRoot, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = Bg, AutoScroll = true };

        var name = LabeledText(p, S(4), "Emulator Name:", Safe(() => emu.Title) ?? "", s: s);
        var path = LabeledText(p, S(64), "Application Path:", Safe(() => emu.ApplicationPath) ?? "", browse: true, lbRoot: lbRoot, s: s);
        var cmd = LabeledText(p, S(124), "Default Command-Line Parameters:", Safe(() => emu.CommandLine) ?? "", s: s);

        var noQuotes = Chk(p, new Point(S(8), S(188)), "Remove Quotes", Safe(() => emu.NoQuotes));
        var noSpace = Chk(p, new Point(S(300), S(188)), "Remove space before ROM", Safe(() => emu.NoSpace));
        var nameOnly = Chk(p, new Point(S(8), S(212)), "Remove file extension and folder path", Safe(() => emu.FileNameWithoutExtensionAndPath));
        var hideCon = Chk(p, new Point(S(8), S(236)), "Attempt to hide console window on startup/shutdown", Safe(() => emu.HideConsole));
        var extract = Chk(p, new Point(S(8), S(260)), "Extract ROM archives before running", Safe(() => emu.AutoExtract));

        // Live sample command (LB parity).
        var sampleLbl = new Label { Text = "Sample Command:", Location = new Point(S(8), S(292)), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
        var sample = new TextBox
        {
            Location = new Point(S(8), S(312)), Width = S(600), ReadOnly = true,
            BackColor = Panel2, ForeColor = SubFg, BorderStyle = BorderStyle.FixedSingle,
        };
        p.Controls.Add(sampleLbl); p.Controls.Add(sample);
        void RefreshSample()
        {
            string exe = System.IO.Path.GetFileName(path.Text.Trim());
            string rom = nameOnly.Checked ? "ROMFILE" : @"FULL\PATH\TO\ROM\FILE";
            if (!noQuotes.Checked) rom = "\"" + rom + "\"";
            string c = cmd.Text.Trim();
            sample.Text = exe + (c.Length > 0 ? " " + c : "") + (noSpace.Checked ? "" : " ") + rom;
        }
        path.TextChanged += (_, _) => RefreshSample();
        cmd.TextChanged += (_, _) => RefreshSample();
        foreach (var cb in new[] { noQuotes, noSpace, nameOnly }) cb.CheckedChanged += (_, _) => RefreshSample();
        RefreshSample();

        // Plugin block (capability-based): current version + Update / Reinstall.
        var plugin = EmuPlugins.ForEmulator(emu);
        if (plugin != null)
        {
            var verLbl = new Label { Text = "Current Version: …", Location = new Point(S(8), S(352)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            var updLbl = new Label { Text = "", Location = new Point(S(8), S(374)), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
            var updBtn = MiniBtn("Update", new Point(S(220), S(348)), s); updBtn.Enabled = false;
            var reBtn = MiniBtn("Reinstall", new Point(S(316), S(348)), s); reBtn.Enabled = false;
            p.Controls.Add(verLbl); p.Controls.Add(updLbl); p.Controls.Add(updBtn); p.Controls.Add(reBtn);

            string? latestId = null;
            System.Threading.Tasks.Task.Run(() =>
            {
                string? cur = EmuPlugins.CurrentVersion(emu);
                EmulatorControllerVersion? latest = null;
                try { latest = plugin.GetInstallableVersions()?.FirstOrDefault(); } catch { }
                try
                {
                    p.BeginInvoke((Action)(() =>
                    {
                        verLbl.Text = "Current Version: " + (cur ?? "unknown");
                        if (latest != null)
                        {
                            latestId = Safe(() => latest.Identifier);
                            string label = Safe(() => latest.Label) ?? latestId ?? "?";
                            bool upToDate = cur != null && (cur == latestId || cur == label);
                            updLbl.Text = upToDate ? "Up to date." : $"Latest available: {label}";
                            updBtn.Enabled = !upToDate && latestId != null;
                            reBtn.Enabled = latestId != null;
                        }
                        else updLbl.Text = "(no installable versions reported)";
                    }));
                }
                catch { }
            });

            void Install(string verb)
            {
                if (latestId == null) return;
                if (MessageBox.Show($"{verb} \"{Safe(() => emu.Title)}\" to version {latestId}?\nThe plugin will download and install it.",
                        verb + " Emulator", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                RunInstall(plugin, emu, latestId, p.FindForm());
            }
            updBtn.Click += (_, _) => Install("Update");
            reBtn.Click += (_, _) => Install("Reinstall");
        }

        return (p, Apply);

        void Apply()
        {
            Set(() => emu.Title = name.Text.Trim());
            Set(() => emu.ApplicationPath = path.Text.Trim());
            Set(() => emu.CommandLine = cmd.Text.Trim());
            Set(() => emu.NoQuotes = noQuotes.Checked);
            Set(() => emu.NoSpace = noSpace.Checked);
            Set(() => emu.FileNameWithoutExtensionAndPath = nameOnly.Checked);
            Set(() => emu.HideConsole = hideCon.Checked);
            Set(() => emu.AutoExtract = extract.Checked);
        }
    }

    /// <summary>InstallEmulator with a modal progress dialog (the plugin reports
    /// progress + honours cancellation). Shared with Manage Emulators' Update All.</summary>
    internal static void RunInstall(EmulatorPlugin plugin, IEmulator emu, string version, Form? owner)
    {
        bool cancelled = false;
        float s = owner != null ? LiteBoxTheme.DpiScale(owner) : 1f;
        int S(int px) => (int)Math.Round(px * s);
        var dlg = new Form
        {
            Text = "Installing…", Size = new Size(S(460), S(150)), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            BackColor = Bg, ForeColor = Fg, ControlBox = false,
        };
        var lbl = new Label { Location = new Point(S(14), S(14)), Size = new Size(S(420), S(36)), Text = "Starting…" };
        var bar = new ProgressBar { Location = new Point(S(14), S(56)), Size = new Size(S(420), S(18)), Style = ProgressBarStyle.Marquee };
        var cancel = MiniBtn("Cancel", new Point(S(338), S(84)), s);
        cancel.Click += (_, _) => { cancelled = true; cancel.Enabled = false; };
        dlg.Controls.AddRange(new Control[] { lbl, bar, cancel });

        System.Threading.Tasks.Task.Run(() =>
        {
            EmulatorInstallResponse? resp = null; Exception? err = null;
            try
            {
                var args = new InstallEmulatorArgs(
                    null,
                    (label, sub, prog) =>
                    {
                        try
                        {
                            dlg.BeginInvoke((Action)(() =>
                            {
                                lbl.Text = label + (string.IsNullOrEmpty(sub) ? "" : "\n" + sub);
                                if (prog.HasValue) { bar.Style = ProgressBarStyle.Continuous; bar.Value = Math.Max(0, Math.Min(100, (int)(prog.Value * 100))); }
                            }));
                        }
                        catch { }
                    },
                    () => cancelled,
                    emu,
                    version,
                    false);
                resp = plugin.InstallEmulator(args);
            }
            catch (Exception ex) { err = ex; }
            try
            {
                dlg.BeginInvoke((Action)(() =>
                {
                    dlg.Close();
                    string msg = err != null ? "Install failed: " + err.Message
                               : resp == null ? "Install returned nothing."
                               : (Safe(() => resp.Message) ?? "Done.");
                    MessageBox.Show(owner, msg, "Install Emulator", MessageBoxButtons.OK,
                        err != null ? MessageBoxIcon.Error : MessageBoxIcon.Information);
                }));
            }
            catch { }
        });
        dlg.ShowDialog(owner);
    }

    // ── Associated Platforms ───────────────────────────────────────────
    private static (Control, Action) BuildPlatforms(IEmulator emu, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = Bg };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Bg, GridColor = Color.FromArgb(60, 60, 64),
            BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
            AllowUserToAddRows = true, AllowUserToDeleteRows = true,
            // Fill (not None+fixed Width): fixed pixel column widths never scale for DPI and can
            // leave a dead gap or overflow relative to the grid's real width - the same class of
            // bug fixed in GameListView's column stretch. Fill (already used correctly by
            // RaMappingDialog's grid) sidesteps it entirely by always dividing the available width
            // proportionally, at any DPI, with no scaling math needed.
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Panel2;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(37, 37, 38);
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 122, 204);

        // Platform column = editable combo seeded with every known platform (the
        // library's + the ones already referenced by this emulator's rows); free
        // text stays allowed via EditingControlShowing → DropDown style.
        var platCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Associated Platform", FillWeight = 230,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
        };
        var known = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var pl in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>()) { var n = Safe(() => pl.Name); if (!string.IsNullOrEmpty(n)) known.Add(n!); } } catch { }
        foreach (var ep0 in emu.GetAllEmulatorPlatforms() ?? Array.Empty<IEmulatorPlatform>())
        { var n = Safe(() => ep0.Platform); if (!string.IsNullOrEmpty(n)) known.Add(n!); }
        foreach (var n in known) platCol.Items.Add(n);
        grid.Columns.Add(platCol);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Default Command-Line Parameters", FillWeight = 280 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Default", FillWeight = 64 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Extract ROMs", FillWeight = 90 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Use M3U", FillWeight = 70 });

        // Free-text platform entry: switch the editing combo to DropDown and accept
        // values not in Items (register them on validation so commit never DataErrors).
        grid.EditingControlShowing += (_, e) =>
        { if (grid.CurrentCell?.ColumnIndex == 0 && e.Control is ComboBox cb) { cb.DropDownStyle = ComboBoxStyle.DropDown; cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend; cb.AutoCompleteSource = AutoCompleteSource.ListItems; } };
        grid.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex != 0) return;
            var v = e.FormattedValue as string ?? "";
            if (v.Length > 0 && !platCol.Items.Contains(v)) platCol.Items.Add(v);
        };
        grid.DataError += (_, e) => e.ThrowException = false;

        // LB parity: only ONE emulator may be the default for a given platform. When the
        // user ticks "Default" for a platform another emulator already owns, warn (Yes =
        // steal it — the old owner is cleared on Apply; No = revert the tick). Skipped in
        // read-only (the grid is disabled and nothing is written).
        const int DefaultCol = 2;
        bool suppressDefault = false;
        if (!readOnly)
        {
            // Checkbox edits don't commit until focus leaves the cell — force an immediate
            // commit so CellValueChanged fires the moment the box is (un)ticked.
            grid.CurrentCellDirtyStateChanged += (_, _) =>
            { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (_, e) =>
            {
                if (suppressDefault || e.ColumnIndex != DefaultCol || e.RowIndex < 0) return;
                var row = grid.Rows[e.RowIndex];
                if (row.IsNewRow || row.Cells[DefaultCol].Value is not (true or CheckState.Checked)) return;
                string platform = (row.Cells[0].Value as string ?? "").Trim();
                var owner = OtherDefaultEmu(platform);
                if (owner == null) return;
                var ans = MessageBox.Show(p.FindForm(),
                    $"{owner} is already the default emulator for {platform}. Are you sure you wish to replace it with this one?",
                    "LiteBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ans != DialogResult.Yes)
                { suppressDefault = true; row.Cells[DefaultCol].Value = false; suppressDefault = false; }
            };
        }

        // The per-platform AutoExtract is NULLABLE in the data model (null =
        // inherit the emulator-level setting). The UI shows a plain on/off
        // checkbox like LB; the loaded null state is remembered per row so an
        // UNTOUCHED unchecked box keeps inheriting instead of forcing "false".
        var loadedExtract = new Dictionary<DataGridViewRow, bool?>();

        var rows = emu.GetAllEmulatorPlatforms()?.ToList() ?? new List<IEmulatorPlatform>();
        suppressDefault = true;   // initial fill must not trigger the "replace default?" prompt
        foreach (var ep in rows)
        {
            bool? ax = Safe(() => ep.AutoExtract);
            int i = grid.Rows.Add(Safe(() => ep.Platform) ?? "", Safe(() => ep.CommandLine) ?? "",
                                  Safe(() => ep.IsDefault), ax == true, Safe(() => ep.M3uDiscLoadEnabled));
            grid.Rows[i].Tag = ep;
            loadedExtract[grid.Rows[i]] = ax;
        }
        suppressDefault = false;

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = S(34), ForeColor = SubFg, BackColor = Bg,
            Text = "An unchecked, untouched Extract ROMs keeps inheriting the emulator-level setting.  New row at the bottom adds a platform; Delete removes the selected row.",
            Font = new Font("Segoe UI", 8.25f), Padding = new Padding(S(4), S(2), 0, 0),
        };
        p.Controls.Add(grid); p.Controls.Add(hint);

        return (p, Apply);

        void Apply()
        {
            var kept = new HashSet<IEmulatorPlatform>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string platform = (row.Cells[0].Value as string ?? "").Trim();
                if (platform.Length == 0) continue;
                var ep = row.Tag as IEmulatorPlatform;
                if (ep == null) { ep = Safe(() => emu.AddNewEmulatorPlatform()); if (ep == null) continue; row.Tag = ep; }
                kept.Add(ep);
                Set(() => ep.Platform = platform);
                Set(() => ep.CommandLine = (row.Cells[1].Value as string ?? "").Trim());
                bool wantDefault = row.Cells[2].Value is true or CheckState.Checked;
                Set(() => ep.IsDefault = wantDefault);
                // Enforce single-default-per-platform: clear the flag on any other emulator.
                if (!readOnly && wantDefault) ClearOtherDefaults(platform);
                // Two-state checkbox over a nullable field: an unchecked box that
                // LOADED as null (inherit) and was never checked stays null.
                bool isChecked = row.Cells[3].Value is true or CheckState.Checked;
                bool? loaded = loadedExtract.TryGetValue(row, out var lv) ? lv : null;
                bool? ax = isChecked ? true : (loaded == null ? (bool?)null : false);
                Set(() => ep.AutoExtract = ax);
                Set(() => ep.M3uDiscLoadEnabled = row.Cells[4].Value is true or CheckState.Checked);
            }
            // Rows removed in the grid → remove from the emulator.
            foreach (var ep in rows.Where(r => !kept.Contains(r)))
                Set(() => emu.TryRemoveEmulatorPlatform(ep));
        }

        // The title of ANOTHER emulator already flagged default for <paramref name="platform"/>
        // (null when none) — drives the "replace the default?" warning.
        string? OtherDefaultEmu(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return null;
            string? selfId = Safe(() => emu.Id);
            foreach (var oe in PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>())
            {
                if (oe == null || string.Equals(Safe(() => oe.Id), selfId, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var ep in oe.GetAllEmulatorPlatforms() ?? Array.Empty<IEmulatorPlatform>())
                    if (Safe(() => ep.IsDefault) && string.Equals(Safe(() => ep.Platform), platform, StringComparison.OrdinalIgnoreCase))
                        return Safe(() => oe.Title) ?? "Another emulator";
            }
            return null;
        }

        // Clear the default flag for <paramref name="platform"/> on every OTHER emulator,
        // routing through their setters so each change lands in the op-log.
        void ClearOtherDefaults(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return;
            string? selfId = Safe(() => emu.Id);
            foreach (var oe in PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>())
            {
                if (oe == null || string.Equals(Safe(() => oe.Id), selfId, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var ep in oe.GetAllEmulatorPlatforms() ?? Array.Empty<IEmulatorPlatform>())
                    if (Safe(() => ep.IsDefault) && string.Equals(Safe(() => ep.Platform), platform, StringComparison.OrdinalIgnoreCase))
                        Set(() => ep.IsDefault = false);
            }
        }
    }

    // ── Dependency Files (plugin capability) ───────────────────────────
    private static Control? BuildDependencies(IEmulator emu, string lbRoot, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var platforms = emu.GetAllEmulatorPlatforms()?.Select(ep => Safe(() => ep.Platform) ?? "").Where(name => name.Length > 0).Distinct().ToList()
                        ?? new List<string>();
        if (platforms.Count == 0 || EmuPlugins.ForEmulator(emu) == null) return null;
        // Show the section only when at least one platform reports files.
        if (!platforms.Any(pl => EmuPlugins.BiosFiles(emu, pl).Any())) return null;

        var p = new Panel { BackColor = Bg };
        var top = new Panel { Dock = DockStyle.Top, Height = S(36), BackColor = Bg };
        var lbl = new Label { Text = "Platform:", Location = new Point(S(4), S(9)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var cmb = new ComboBox
        {
            Location = new Point(S(70), S(5)), Width = S(320), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Panel2, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        };
        cmb.Items.AddRange(platforms.Cast<object>().ToArray());
        top.Controls.Add(lbl); top.Controls.Add(cmb);

        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            BackColor = Color.FromArgb(37, 37, 38), ForeColor = Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        list.Columns.Add("", S(30));
        list.Columns.Add("File", S(320));
        list.Columns.Add("Description", S(260));
        list.Columns.Add("Rule", S(160));

        string emuDir = "";
        try
        {
            var ap = Safe(() => emu.ApplicationPath) ?? "";
            if (!System.IO.Path.IsPathRooted(ap)) ap = System.IO.Path.GetFullPath(System.IO.Path.Combine(lbRoot, ap));
            emuDir = System.IO.Path.GetDirectoryName(ap) ?? "";
        }
        catch { }

        void Fill()
        {
            list.Items.Clear();
            if (cmb.SelectedItem is not string platform) return;
            foreach (var f in EmuPlugins.BiosFiles(emu, platform))
            {
                string rel = System.IO.Path.Combine((Safe(() => f.Location) ?? "").Trim('\\', '/'), Safe(() => f.FileName) ?? "");
                bool exists = false;
                try { exists = emuDir.Length > 0 && File.Exists(System.IO.Path.Combine(emuDir, rel)); } catch { }
                var grp = Safe(() => f.ApplicableGroup);
                string rule = grp is { IsGroupRequired: true, AllItemsRequired: false } ? "one of group: " + (Safe(() => grp.Description) ?? "?")
                            : grp is { IsGroupRequired: true, AllItemsRequired: true } ? "required (group)"
                            : Safe(() => f.Required) ? "required" : "optional";
                var item = new ListViewItem(new[] { exists ? "●" : "!", "\\" + rel, Safe(() => f.Description) ?? "", rule });
                item.UseItemStyleForSubItems = false;
                item.SubItems[0].ForeColor = exists ? Good : Bad;
                list.Items.Add(item);
            }
        }
        cmb.SelectedIndexChanged += (_, _) => Fill();
        cmb.SelectedIndex = 0;

        p.Controls.Add(list); p.Controls.Add(top);
        list.BringToFront();
        return p;
    }

    // ── Startup Screen ─────────────────────────────────────────────────
    private static (Control, Action) BuildStartup(IEmulator emu, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = Bg, AutoScroll = true };
        var use = Chk(p, new Point(S(8), S(6)), "Enable Game Startup Screen", Safe(() => emu.UseStartupScreen));
        var noShut = Chk(p, new Point(S(330), S(6)), "Enable Game Shutdown Screen", !Safe(() => emu.DisableShutdownScreen));
        var hideMouse = Chk(p, new Point(S(8), S(30)), "Hide Mouse Cursor During Game", Safe(() => emu.HideMouseCursorInGame));
        var aggressive = Chk(p, new Point(S(330), S(30)), "Aggressive Startup Window Hiding", Safe(() => emu.AggressiveWindowHiding));
        var hideAll = Chk(p, new Point(S(8), S(54)), "Hide All Windows that are not in Exclusive Fullscreen Mode", Safe(() => emu.HideAllNonExclusiveFullscreenWindows));

        var dlyLbl = new Label { Text = "Startup Load Delay (ms):", Location = new Point(S(8), S(88)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var dly = new NumericUpDown
        {
            Location = new Point(S(170), S(85)), Width = S(90), Minimum = 0, Maximum = 60000, Increment = 250,
            BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Value = Math.Max(0, Math.Min(60000, Safe(() => emu.StartupLoadDelay))),
        };
        var note = new Label
        {
            Text = "These settings round-trip to Emulators.xml and are honoured by LaunchBox.\nLiteBox does not implement startup/shutdown screens yet.",
            Location = new Point(S(8), S(122)), AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8.25f),
        };
        p.Controls.Add(dlyLbl); p.Controls.Add(dly); p.Controls.Add(note);

        return (p, () =>
        {
            Set(() => emu.UseStartupScreen = use.Checked);
            Set(() => emu.DisableShutdownScreen = !noShut.Checked);
            Set(() => emu.HideMouseCursorInGame = hideMouse.Checked);
            Set(() => emu.AggressiveWindowHiding = aggressive.Checked);
            Set(() => emu.HideAllNonExclusiveFullscreenWindows = hideAll.Checked);
            Set(() => emu.StartupLoadDelay = (int)dly.Value);
        });
    }

    // ── Pause Screen (off-SDK toggles via ILiteBoxFields) ──────────────
    private static (Control, Action) BuildPause(IEmulator emu, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = Bg };
        var lf = emu as ILiteBoxFields;
        bool GetB(string k, bool def) { var v = lf?.GetField(k); return string.IsNullOrEmpty(v) ? def : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); }

        var use = Chk(p, new Point(S(8), S(6)), "Enable Game Pause Screen", GetB("UsePauseScreen", true));
        var susp = Chk(p, new Point(S(8), S(30)), "Suspend Emulator Process While Paused", GetB("SuspendProcessOnPause", true));
        var force = Chk(p, new Point(S(8), S(54)), "Forceful Pause Screen Activation (enable this if the pause screen is not showing)", GetB("ForcefulPauseScreenActivation", true));

        return (p, () =>
        {
            if (lf == null) return;
            Set(() => lf.SetField("UsePauseScreen", use.Checked ? "true" : "false"));
            Set(() => lf.SetField("SuspendProcessOnPause", susp.Checked ? "true" : "false"));
            Set(() => lf.SetField("ForcefulPauseScreenActivation", force.Checked ? "true" : "false"));
        });
    }

    // ── Script sections ────────────────────────────────────────────────
    private static void AddScript(OptionsWindow w, IEmulator emu, string title,
        Func<IEmulator, string?> get, Action<IEmulator, string> set)
    {
        var p = new Panel { BackColor = Bg };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true,
            ScrollBars = ScrollBars.Both, WordWrap = false,
            BackColor = Color.FromArgb(37, 37, 38), ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f),
            Text = (Safe(() => get(emu)) ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        p.Controls.Add(tb);
        w.AddSection(title, p, () => Set(() => set(emu, tb.Text.Replace("\r\n", "\n"))));
    }

    // ── helpers ────────────────────────────────────────────────────────
    private static TextBox LabeledText(Panel p, int y, string label, string value, bool browse = false, string lbRoot = "", float s = 1f)
    {
        int S(int px) => (int)Math.Round(px * s);
        var lbl = new Label { Text = label, Location = new Point(S(8), y), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var tb = new TextBox
        {
            Location = new Point(S(8), y + S(20)), Width = browse ? S(510) : S(600),
            BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Text = value,
        };
        p.Controls.Add(lbl); p.Controls.Add(tb);
        if (browse)
        {
            var btn = MiniBtn("Browse…", new Point(S(526), y + S(18)), s);
            btn.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var sel = dlg.FileName;
                // Keep LB-relative when the exe sits under the LB root (LB convention).
                try
                {
                    if (lbRoot.Length > 0 && sel.StartsWith(lbRoot, StringComparison.OrdinalIgnoreCase))
                        sel = sel.Substring(lbRoot.Length).TrimStart('\\', '/');
                }
                catch { }
                tb.Text = sel;
            };
            p.Controls.Add(btn);
        }
        return tb;
    }

    private static CheckBox Chk(Panel p, Point loc, string text, bool val)
    {
        var cb = new CheckBox { Text = text, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = val };
        p.Controls.Add(cb);
        return cb;
    }

    private static Button MiniBtn(string text, Point loc, float s = 1f) => new()
    {
        Text = text, Location = loc, Size = new Size((int)Math.Round(88 * s), (int)Math.Round(26 * s)),
        FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f),
    };

    private static void DisableAllInputs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TextBox or CheckBox or ComboBox or NumericUpDown or DataGridView) c.Enabled = false;
            else if (c is Button b && b.Text != "Cancel" && b.Text != "OK") b.Enabled = false;
            if (c.HasChildren) DisableAllInputs(c);
        }
    }

    private static void Set(Action a) { try { a(); } catch (Exception ex) { Console.WriteLine("[editemu] write failed: " + ex.Message); } }
    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
