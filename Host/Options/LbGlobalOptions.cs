// LaunchBox GLOBAL options (LB parity — the Tools ▸ Options window), backed by
// LB\Data\Settings.xml through LbSettingsStore → op-log → scoped flush. These are
// LAUNCHBOX's settings: editing them here writes what LB will read on its next
// boot. Options LiteBox itself never reads carry NoImpact = true and render a red
// "No impact on LiteBox" note (the value still round-trips for LB's benefit).
//
// This first pass covers the simple sections. Deferred to later rounds:
//   - Save Management (big chantier, explicitly parked)
//   - Startup Applications / Game Progress / Related Games / Media priorities
//     (grids & trees — incl. the Related-Games mirror file: LB STRIPS unknown
//     tags from Settings.xml, verified empirically, so the mirror cannot live there)

#nullable enable

using LbApiHost.Host.Data;

namespace LbApiHost.Host.Options;

internal static class LbGlobalOptions
{
    /// <summary>Appends the LaunchBox-settings sections to an options window.
    /// <paramref name="readOnly"/> greys them out entirely.</summary>
    public static void AddSections(OptionsWindow w, LbSettingsStore s, bool readOnly)
    {
        if (!s.Loaded) return;   // no Settings.xml → nothing to edit

        OptionItem B(string label, string field, bool noImpact, string? help = null)
            => new(label, label, OptionKind.Bool)
            { Get = () => s.Get(field), Set = v => s.Set(field, v), NoImpact = noImpact, Help = help };

        // LB's language picker: display the native names, store the culture code
        // (same 16 entries as LB's own dropdown; codes from the Core\* satellite
        // folders — .NET culture fallback makes the specific form always safe).
        string[] langNames =
        {
            "Arabic", "Deutsch", "English", "Español", "Ελληνικά", "Français", "Italiano",
            "日本語", "한국어", "Nederlands", "Português do Brasil", "Русский", "Svenska",
            "Türkçe", "简体中文", "繁體中文",
        };
        string[] langCodes =
        {
            "ar-SA", "de-DE", "en-US", "es", "el-GR", "fr-FR", "it-IT",
            "ja-JP", "ko-KR", "nl-NL", "pt-BR", "ru-RU", "sv-SE",
            "tr-TR", "zh-Hans", "zh-Hant",
        };
        string GetLanguage()
        {
            var v = s.Get("Language", "en-US");
            if (Array.IndexOf(langCodes, v) >= 0) return v;
            // Tolerant match: "fr" or "fr-CA" in the file still selects Français.
            var two = v.Split('-')[0];
            foreach (var c in langCodes) if (c.Split('-')[0].Equals(two, StringComparison.OrdinalIgnoreCase)) return c;
            return "en-US";
        }

        w.AddSection("LB · General", new[]
        {
            OptionItem.Choice("g", "Language", langNames,
                GetLanguage, v => s.Set("Language", v),
                "LaunchBox UI language — applies to LaunchBox's next start.")
                .Values(langCodes).Tag(noImpact: true),
            B("Show splash screen during load", "ShowLaunchBoxSplashScreen", true),
            B("Allow deleting ROMs when deleting games", "AllowDeletingRoms", true),
            B("Share optional usage data", "EnableTelemetry", true),
            B("Minimize LaunchBox when launching games", "MinimizeOnGameLaunch", true,
                "Only when the startup screen is disabled."),
            B("Restore LaunchBox when exiting games", "RestoreOnGameExit", true,
                "Only when the startup screen is disabled."),
        }, readOnly);

        w.AddSection("LB · Startup Applications", BuildStartupAppsPanel(s, readOnly, out var applyStartupApps),
            readOnly ? null : applyStartupApps);

        w.AddSection("LB · System Tray", new[]
        {
            B("Enable System Tray", "EnableSystemTray", true),
            B("Minimize to System Tray", "MinimizeToSystemTray", true),
            B("Close to System Tray", "CloseToSystemTray", true),
            OptionItem.Toggle("t", "Show notification when sent to the system tray",
                () => !s.GetBool("DontSendTrayReminder"), v => s.SetBool("DontSendTrayReminder", !v))
                .Tag(noImpact: true),
        }, readOnly);

        w.AddSection("LB · Region Priorities", BuildRegionPrioritiesPanel(s, readOnly, out var applyRegions),
            readOnly ? null : applyRegions);

        w.AddSection("LB · Auto-Import Media", BuildAutoImportMediaPanel(s, readOnly, out var applyMedia),
            readOnly ? null : applyMedia);

        // All media priority lists live under ONE section with internal tabs
        // (LB has ~10 separate sub-pages; we fold them into tabs to cut clutter).
        w.AddSection("LB · Media Priorities", BuildMediaPrioritiesPanel(s, readOnly, out var applyPrio),
            readOnly ? null : applyPrio);

        // LB "Integrations" branch, tabbed. None drive LiteBox today (it doesn't run
        // these integrations) — they round-trip to Settings.xml for LaunchBox, and the
        // credentials sit here for a future LiteBox feature (notably RetroAchievements).
        w.AddSection("LB · Integrations", BuildIntegrationsPanel(s, readOnly, out var applyInteg),
            readOnly ? null : applyInteg);

        // LB "Gameplay" branch (Game Startup / Game Pause / Screen Capture), tabbed.
        // These DO drive LiteBox (startup/end/pause overlays + screenshot hotkey).
        w.AddSection("LB · Gameplay", BuildGameplayPanel(s, readOnly, out var applyGameplay),
            readOnly ? null : applyGameplay);
    }

    // ── LB "Gameplay" branch: the startup / pause / screen-capture options that
    //    LiteBox actually honours. Screen toggles + times + cosmetics round-trip to
    //    Settings.xml (LB-owned field names); the two HOTKEYS live in LiteBox.ini
    //    (combo-capable, unlike LB's single WPF-Key int). Theme pickers are omitted
    //    (LiteBox has no themes). Changes apply on the next game launch. ──
    private static Control BuildGameplayPanel(LbSettingsStore s, bool readOnly, out Action apply)
    {
        var Bg = Color.FromArgb(30, 30, 30);
        var Fg = Color.FromArgb(222, 222, 222);
        var Dim = Color.FromArgb(150, 150, 152);
        var Panel2 = Color.FromArgb(45, 45, 48);
        var applies = new List<Action>();
        var ini = LiteBoxConfig.LoadForExe();   // PauseHotkey / ScreenCaptureKey live here
        bool iniDirty = false;

        CheckBox Chk(string t, bool v, Point loc) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = v, Enabled = !readOnly };
        Label Lbl(string t, Point loc, Color? c = null) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = c ?? Fg, BackColor = Bg };
        TextBox Txt(string v, Point loc, int w) => new() { Text = v, Location = loc, Width = w, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly };
        HotkeyCaptureBox Hk(string v, Point loc, int w) => new(v) { Location = loc, Width = w, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly };
        void BindChk(CheckBox cb, string field) => applies.Add(() => { if (cb.Checked != s.GetBool(field)) s.SetBool(field, cb.Checked); });
        void BindTxt(TextBox tb, string field) => applies.Add(() => { if (tb.Text != s.Get(field)) s.Set(field, tb.Text); });
        void BindIniHk(HotkeyCaptureBox hb, string key) => applies.Add(() => { if (hb.HotkeyValue != ini.Get(key)) { ini.Set(key, hb.HotkeyValue); iniDirty = true; } });

        var tabs = NewDarkTabControl();
        TabPage Page(string t) { var p = new TabPage(t) { BackColor = Bg, Padding = new Padding(12) }; tabs.TabPages.Add(p); return p; }

        // ── Game Startup (governs the startup "NOW LOADING…" AND end "GAME OVER" screens) ──
        {
            var p = Page("Game Startup");
            var use = Chk("Use Game Startup Screen", s.GetBool("UseStartupScreen", true), new Point(4, 8));
            p.Controls.Add(use);
            p.Controls.Add(Lbl("(also shows the end “GAME OVER” screen)", new Point(28, 30), Dim));
            p.Controls.Add(Lbl("Minimum Startup Screen Display Time (ms)", new Point(4, 64)));
            var st = Txt(s.Get("MinimumStartupScreenDisplayTime", "1000"), new Point(320, 61), 90); p.Controls.Add(st);
            p.Controls.Add(Lbl("Minimum Shutdown Screen Display Time (ms)", new Point(4, 96)));
            var sh = Txt(s.Get("MinimumShutdownScreenDisplayTime", "1000"), new Point(320, 93), 90); p.Controls.Add(sh);
            var hc = Chk("Hide Mouse Cursor on Startup Screens", s.GetBool("HideMouseCursorOnStartupScreens", true), new Point(4, 128));
            p.Controls.Add(hc);
            BindChk(use, "UseStartupScreen"); BindTxt(st, "MinimumStartupScreenDisplayTime");
            BindTxt(sh, "MinimumShutdownScreenDisplayTime"); BindChk(hc, "HideMouseCursorOnStartupScreens");
        }

        // ── Game Pause ──
        {
            var p = Page("Game Pause");
            var use = Chk("Use Game Pause Screen", s.GetBool("UsePauseScreen", true), new Point(4, 8));
            p.Controls.Add(use);
            p.Controls.Add(Lbl("Pause Key", new Point(4, 40)));
            var pk = Hk(ini.Get("PauseHotkey", "Pause"), new Point(120, 37), 220); p.Controls.Add(pk);
            p.Controls.Add(Lbl("click, then press a key/combo", new Point(348, 40), Dim));
            var fade = Chk("Enable Fading", s.GetBool("PauseScreenFading", true), new Point(4, 76));
            var mute = Chk("Mute Audio During Transitions", s.GetBool("PauseScreenMuting", true), new Point(4, 102));
            p.Controls.AddRange(new Control[] { fade, mute });
            BindChk(use, "UsePauseScreen"); BindIniHk(pk, "PauseHotkey");
            BindChk(fade, "PauseScreenFading"); BindChk(mute, "PauseScreenMuting");
        }

        // ── Screen Capture ──
        {
            var p = Page("Screen Capture");
            p.Controls.Add(Lbl("Screen Capture Key", new Point(4, 12)));
            var sc = Hk(ini.Get("ScreenCaptureKey", ""), new Point(150, 9), 220); p.Controls.Add(sc);
            p.Controls.Add(Lbl("click, then press a key/combo  (empty = disabled)", new Point(378, 12), Dim));
            p.Controls.Add(Lbl("Saves a PNG of the game's monitor to <LB>\\Screenshots.", new Point(4, 44), Dim));
            BindIniHk(sc, "ScreenCaptureKey");
        }

        // Footer note: gameplay changes take effect on the next game launch.
        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 24, ForeColor = Dim, BackColor = Bg,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft,
            Text = "These options apply to the next game launch.",
        };
        var host = new Panel { BackColor = Bg };
        host.Controls.Add(tabs);
        host.Controls.Add(note);

        apply = () =>
        {
            foreach (var a in applies) a();
            if (iniDirty) { try { ini.Save(); } catch { } }
        };
        return host;
    }

    // ── Startup Applications grid (LB parity; LiteBox LAUNCHES the LaunchBox-
    //    flagged rows at its own boot — see StartupApps.LaunchAll) ────────────
    private static Control BuildStartupAppsPanel(LbSettingsStore s, bool readOnly, out Action apply)
    {
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 34, ForeColor = Color.FromArgb(150, 150, 152),
            Text = "Started when LaunchBox/Big Box launch — LiteBox starts the LaunchBox-flagged rows too.\nDelete key removes the selected row.",
            Font = new Font("Segoe UI", 8.25f),
        };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.FromArgb(37, 37, 38), BorderStyle = BorderStyle.None,
            AllowUserToAddRows = !readOnly, AllowUserToDeleteRows = !readOnly, ReadOnly = readOnly,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(222, 222, 222) },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(222, 222, 222),
                SelectionBackColor = Color.FromArgb(0, 122, 204), SelectionForeColor = Color.White,
            },
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Application Path", FillWeight = 40 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Command-Line Parameters", FillWeight = 28 });
        var startWith = new DataGridViewComboBoxColumn { HeaderText = "Start With?", FillWeight = 18, FlatStyle = FlatStyle.Flat };
        startWith.Items.AddRange("Both", "LaunchBox", "Big Box");
        grid.Columns.Add(startWith);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Allow Multiple Instances?", FillWeight = 14 });
        grid.DataError += (_, e) => e.ThrowException = false;

        foreach (var a in s.StartupApps)
        {
            string sw = a.StartWithLaunchBox && a.StartWithBigBox ? "Both" : (a.StartWithLaunchBox ? "LaunchBox" : "Big Box");
            int ri = grid.Rows.Add(a.ApplicationPath, a.CommandLine, sw, a.AllowMultipleInstances);
            // Carry the original app on the row so its Extra (unmodelled fields) survives
            // an edit/reorder — new rows have no Tag and start with empty Extra.
            grid.Rows[ri].Tag = a;
        }

        panel.Controls.Add(grid);
        panel.Controls.Add(hint);
        grid.BringToFront();

        apply = () =>
        {
            var list = new List<LbStartupApp>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string path = (row.Cells[0].Value?.ToString() ?? "").Trim();
                if (path.Length == 0) continue;
                string sw = row.Cells[2].Value?.ToString() ?? "Both";
                var app = (row.Tag as LbStartupApp)?.Clone() ?? new LbStartupApp();   // keep Extra of an existing row
                app.ApplicationPath = path;
                app.CommandLine = (row.Cells[1].Value?.ToString() ?? "").Trim();
                app.StartWithLaunchBox = sw is "Both" or "LaunchBox";
                app.StartWithBigBox = sw is "Both" or "Big Box";
                app.AllowMultipleInstances = row.Cells[3].Value is true;
                list.Add(app);
            }
            // Only log a replace op when something actually changed.
            var old = s.StartupApps;
            bool same = old.Count == list.Count && !old.Where((o, i) =>
                o.ApplicationPath != list[i].ApplicationPath || o.CommandLine != list[i].CommandLine ||
                o.StartWithLaunchBox != list[i].StartWithLaunchBox || o.StartWithBigBox != list[i].StartWithBigBox ||
                o.AllowMultipleInstances != list[i].AllowMultipleInstances).Any();
            if (!same) s.SetStartupApps(list);
        };
        return panel;
    }

    // ── Region Priorities (LB parity: checklist + Move Up/Down) ─────────────
    // LB's catalog order: 5 promoted regions then alphabetical. Stored field
    // RegionPriorities holds ONLY the checked regions, comma-joined, in order.
    private static readonly string[] _regionCatalog =
    {
        "World", "Europe", "North America", "Japan", "Asia",
        "Australia", "Brazil", "Canada", "China", "Finland", "France", "Germany",
        "Greece", "Holland", "Hong Kong", "Italy", "Korea", "The Netherlands",
        "Norway", "Oceania", "Russia", "South America", "Spain", "Sweden",
        "Thailand", "United Kingdom", "United States",
    };

    private static Control BuildRegionPrioritiesPanel(LbSettingsStore s, bool readOnly, out Action apply)
    {
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 24, ForeColor = Color.FromArgb(150, 150, 152),
            Text = "Regions to prioritize for imports and displayed images. Checked = used, in order.",
            Font = new Font("Segoe UI", 8.25f),
        };

        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(222, 222, 222),
            BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false, Enabled = !readOnly,
        };

        // Build display order: checked regions first (stored priority order),
        // then the remaining catalog regions in catalog order.
        var stored = s.Get("RegionPriorities")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => _regionCatalog.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var ordered = new List<string>(stored);
        foreach (var r in _regionCatalog)
            if (!ordered.Contains(r, StringComparer.OrdinalIgnoreCase)) ordered.Add(r);
        foreach (var r in ordered)
            list.Items.Add(r, stored.Contains(r, StringComparer.OrdinalIgnoreCase));

        var right = new Panel { Dock = DockStyle.Right, Width = 150, BackColor = Color.FromArgb(30, 30, 30) };
        var up = MoveBtn("Move Selected Up", 4);
        var down = MoveBtn("Move Selected Down", 38);
        up.Enabled = down.Enabled = !readOnly;
        void Move(int delta)
        {
            int i = list.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var item = list.Items[i];
            bool chk = list.GetItemChecked(i);
            list.Items.RemoveAt(i);
            list.Items.Insert(j, item);
            list.SetItemChecked(j, chk);
            list.SelectedIndex = j;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        right.Controls.Add(up); right.Controls.Add(down);

        panel.Controls.Add(list);
        panel.Controls.Add(right);
        panel.Controls.Add(hint);
        list.BringToFront();

        apply = () =>
        {
            var picked = new List<string>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i)) picked.Add(list.Items[i].ToString());
            var joined = string.Join(",", picked);
            if (joined != s.Get("RegionPriorities")) s.Set("RegionPriorities", joined);
        };
        return panel;
    }

    // ── Automatic Imports Media (LB parity) ────────────────────────────────
    // LB's hardcoded media catalog: ordered (Media Type, Image Group) — extracted
    // from LB's own Options grid (group empty where LB shows it blank). The per-type
    // Download toggle lives in Settings.xml <ImageTypeSettings>/UseInAutoImports;
    // the limit is AutoImportMediaLimit (0 = No Limit). LiteBox does no metadata
    // importing itself, so this is config we keep for LaunchBox's benefit.
    private static readonly (string type, string group)[] _mediaCatalog =
    {
        ("Box - Front", "Boxes"), ("Box - Front - Reconstructed", "Boxes"),
        ("Box - Back", "Box Back"), ("Box - Back - Reconstructed", "Box Back"),
        ("Box - 3D", "3D Boxes"), ("Box - Spine", ""), ("Box - Full", ""),
        ("Advertisement Flyer - Front", "Boxes"), ("Advertisement Flyer - Back", "Box Back"),
        ("Arcade - Cabinet", ""), ("Arcade - Circuit Board", ""), ("Arcade - Control Panel", ""),
        ("Arcade - Controls Information", ""), ("Arcade - Marquee", "Marquees"), ("Banner", "Marquees"),
        ("Cart - Front", "Carts"), ("Cart - Back", "Cart Back"), ("Cart - 3D", "3D Carts"),
        ("Clear Logo", ""), ("Disc", "Carts"),
        ("Fanart - Box - Front", "Boxes"), ("Fanart - Box - Back", "Box Back"),
        ("Fanart - Cart - Front", "Carts"), ("Fanart - Cart - Back", "Cart Back"),
        ("Fanart - Background", "Backgrounds"), ("Fanart - Disc", "Carts"),
        ("Screenshot - Gameplay", "Screenshots"), ("Screenshot - Game Title", "Screenshots"),
        ("Screenshot - Game Select", "Screenshots"), ("Screenshot - Game Over", "Screenshots"),
        ("Screenshot - High Scores", "Screenshots"),
        ("Steam Banner", "Boxes, Marquees"), ("Steam Poster", "Boxes"), ("Steam Screenshot", "Screenshots"),
        ("GOG Poster", "Boxes"), ("GOG Screenshot", "Screenshots"),
        // Store-specific types (rows 37-50) — same grid order as LaunchBox.
        ("Epic Games Background", "Backgrounds"), ("Epic Games Poster", "Boxes"), ("Epic Games Screenshot", "Screenshots"),
        ("Origin Background", "Backgrounds"), ("Origin Poster", "Boxes"), ("Origin Screenshot", "Screenshots"),
        ("Uplay Background", "Backgrounds"), ("Uplay Thumbnail", "Boxes"),
        ("Amazon Background", "Backgrounds"), ("Amazon Screenshot", "Screenshots"), ("Amazon Poster", "Boxes"),
        ("Icon", ""), ("Square", "Boxes"), ("Poster", "Boxes"),
    };

    private static Control BuildAutoImportMediaPanel(LbSettingsStore s, bool readOnly, out Action apply)
    {
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };

        var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(30, 30, 30) };
        top.Controls.Add(new Label
        {
            Text = "Image downloads limit (per image group) — 0 = No Limit:",
            Location = new Point(4, 6), AutoSize = true, ForeColor = Color.FromArgb(222, 222, 222),
        });
        var limit = new TextBox
        {
            Location = new Point(4, 26), Width = 120, Enabled = !readOnly,
            BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(222, 222, 222), BorderStyle = BorderStyle.FixedSingle,
            Text = s.Get("AutoImportMediaLimit", "0"),
        };
        top.Controls.Add(limit);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.FromArgb(37, 37, 38), BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(222, 222, 222) },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(222, 222, 222),
                SelectionBackColor = Color.FromArgb(0, 122, 204), SelectionForeColor = Color.White,
            },
        };
        var dl = new DataGridViewCheckBoxColumn { HeaderText = "Download", FillWeight = 12, ReadOnly = readOnly };
        var mt = new DataGridViewTextBoxColumn { HeaderText = "Media Type", FillWeight = 50, ReadOnly = true };
        var ig = new DataGridViewTextBoxColumn { HeaderText = "Image Group", FillWeight = 38, ReadOnly = true };
        grid.Columns.AddRange(dl, mt, ig);
        grid.DataError += (_, e) => e.ThrowException = false;

        // Current per-type Download state from Settings.xml (default false if absent).
        var byType = s.ImageTypes.ToDictionary(i => i.ImageType, i => i.UseInAutoImports, StringComparer.OrdinalIgnoreCase);
        // Display order = hardcoded catalog (the only place that knows order + group),
        // then any LIVE ImageTypeSettings type we don't know about, appended (group blank).
        // So a type a future LB version records still appears instead of vanishing.
        var rows = new List<(string type, string group)>(_mediaCatalog);
        var known = new HashSet<string>(_mediaCatalog.Select(c => c.type), StringComparer.OrdinalIgnoreCase);
        foreach (var it in s.ImageTypes)
            if (it.ImageType.Length > 0 && known.Add(it.ImageType)) rows.Add((it.ImageType, ""));
        foreach (var (type, group) in rows)
        {
            bool on = byType.TryGetValue(type, out var v) && v;
            int ri = grid.Rows.Add(on, type, group);
            grid.Rows[ri].Tag = type;
        }

        panel.Controls.Add(grid);
        panel.Controls.Add(top);
        grid.BringToFront();

        apply = () =>
        {
            // Limit (a plain Settings field).
            var lim = (limit.Text ?? "").Trim();
            if (lim.Length == 0) lim = "0";
            if (lim != s.Get("AutoImportMediaLimit", "0")) s.Set("AutoImportMediaLimit", lim);

            // Merge grid Download states into the FULL ImageTypeSettings collection,
            // preserving entries outside the grid (and each entry's IsDefault + Extra).
            var all = s.ImageTypes;                       // ordered, full (incl. non-grid types)
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++) idx[all[i].ImageType] = i;
            bool changed = false;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is not string type) continue;
                bool on = row.Cells[0].Value is true;
                if (idx.TryGetValue(type, out var i))
                {
                    if (all[i].UseInAutoImports != on) { all[i].UseInAutoImports = on; changed = true; }
                }
                else
                {
                    all.Add(new LbImageTypeSetting { ImageType = type, UseInAutoImports = on });
                    changed = true;
                }
            }
            if (changed) s.SetImageTypes(all);
        };
        return panel;
    }

    // Video media types (LB's Video Priorities catalog).
    private static readonly string[] _videoTypes = { "Theme Video", "Video Snap", "Recording", "Trailer", "Marquee" };

    // The 10 LB media priority lists (tab title, Settings field, default order CSV).
    private static readonly (string tab, string field, string defaults, bool video)[] _mediaPriorities =
    {
        ("Box Front", "FrontImageTypePriorities", "GOG Poster,Steam Poster,Epic Games Poster,Amazon Poster,Box - Front,Box - Front - Reconstructed,Advertisement Flyer - Front,Origin Poster,Uplay Thumbnail,Fanart - Box - Front,Poster,Square,Steam Banner", false),
        ("Box Back", "BackImageTypePriorities", "Box - Back,Box - Back - Reconstructed,Advertisement Flyer - Back,Fanart - Box - Back", false),
        ("3D Box", "Box3dImageTypePriorities", "Box - 3D", false),
        ("Cart Front", "CartFrontImageTypePriorities", "Cart - Front,Fanart - Cart - Front,Disc,Fanart - Disc", false),
        ("Cart Back", "CartBackImageTypePriorities", "Cart - Back,Fanart - Cart - Back", false),
        ("3D Cart", "Cart3dImageTypePriorities", "Cart - 3D", false),
        ("Background", "BackgroundImageTypePriorities", "Epic Games Background,Uplay Background,Origin Background,Amazon Background,Fanart - Background", false),
        ("Marquee", "MarqueeImageTypePriorities", "Arcade - Marquee,Banner,Steam Banner", false),
        ("Screenshot", "ScreenshotsImageTypePriorities", "Screenshot - Gameplay,Screenshot - Game Title,Screenshot - Game Select,Screenshot - High Scores,Screenshot - Game Over,Steam Screenshot,GOG Screenshot,Epic Games Screenshot,Origin Screenshot,Amazon Screenshot", false),
        ("Video", "VideoTypePriorities", "Theme Video,Video Snap,Recording,Trailer", true),
    };

    /// <summary>The full image-type universe = hardcoded catalog ∪ live ImageTypeSettings.
    /// Driving the lists off this (not the static catalog alone) means a type a future
    /// LB version records is offered, not silently missing.</summary>
    private static string[] ImageCatalog(LbSettingsStore s)
    {
        var list = _mediaCatalog.Select(c => c.type).ToList();
        var seen = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        foreach (var it in s.ImageTypes) if (it.ImageType.Length > 0 && seen.Add(it.ImageType)) list.Add(it.ImageType);
        return list.ToArray();
    }

    /// <summary>A dark, owner-drawn TabControl (the OS draws tabs light otherwise).</summary>
    private static TabControl NewDarkTabControl()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, Multiline = true,
            BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(222, 222, 222),
            SizeMode = TabSizeMode.Normal, Padding = new Point(14, 4),
        };
        tabs.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= tabs.TabPages.Count) return;
            bool sel = e.Index == tabs.SelectedIndex;
            using var bg = new SolidBrush(sel ? Color.FromArgb(0, 122, 204) : Color.FromArgb(45, 45, 48));
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                sel ? Color.White : Color.FromArgb(222, 222, 222),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        return tabs;
    }

    // One tabbed section hosting all media priority lists.
    private static Control BuildMediaPrioritiesPanel(LbSettingsStore s, bool readOnly, out Action apply)
    {
        var imgCatalog = ImageCatalog(s);
        var applies = new List<Action>();
        var tabs = NewDarkTabControl();
        foreach (var (tab, field, defaults, video) in _mediaPriorities)
        {
            var page = new TabPage(tab) { BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(2) };
            var panel = BuildPriorityPanel(s, field, video ? _videoTypes : imgCatalog, defaults, readOnly, out var ap);
            panel.Dock = DockStyle.Fill;
            page.Controls.Add(panel);
            tabs.TabPages.Add(page);
            applies.Add(ap);
        }
        apply = () => { foreach (var a in applies) a(); };
        return tabs;
    }

    // ── LB "Integrations" branch (tabbed). All NoImpact on LiteBox today; round-trip
    //    to Settings.xml for LaunchBox, credentials kept for future LiteBox use. ──
    private static Control BuildIntegrationsPanel(LbSettingsStore s, bool readOnly, out Action apply)
    {
        var Bg = Color.FromArgb(30, 30, 30);
        var Fg = Color.FromArgb(222, 222, 222);
        var Panel2 = Color.FromArgb(45, 45, 48);
        var applies = new List<Action>();

        CheckBox Chk(string t, bool v, Point loc) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = v, Enabled = !readOnly };
        Label Lbl(string t, Point loc) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg };
        TextBox Txt(string v, Point loc, int w, bool pwd = false) => new() { Text = v, Location = loc, Width = w, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly, UseSystemPasswordChar = pwd };
        Button Browse(Point loc, Action onClick)
        {
            var b = new Button { Text = "Browse…", Location = loc, Size = new Size(84, 24), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f), Enabled = !readOnly };
            b.Click += (_, _) => onClick();
            return b;
        }
        // Bind a checkbox to a bool field (optionally inverted) on apply.
        void BindChk(CheckBox cb, string field, bool invert = false)
            => applies.Add(() => { bool nv = invert ? !cb.Checked : cb.Checked; if (nv != s.GetBool(field)) s.SetBool(field, nv); });
        void BindTxt(TextBox tb, string field)
            => applies.Add(() => { if (tb.Text != s.Get(field)) s.Set(field, tb.Text); });

        var tabs = NewDarkTabControl();
        TabPage Page(string t) { var p = new TabPage(t) { BackColor = Bg, Padding = new Padding(12) }; tabs.TabPages.Add(p); return p; }

        // ── DOSBox ──
        {
            var p = Page("DOSBox");
            var c1 = Chk("Show all DOSBox commands", s.GetBool("ShowCommands"), new Point(4, 8));
            var c2 = Chk("Don't exit DOSBox when exiting games", !s.GetBool("ExitDosBox", true), new Point(4, 34));
            var c3 = Chk("Pause before each command", s.GetBool("PauseBeforeCommands"), new Point(4, 60));
            var c4 = Chk("Pause before exiting DOSBox", s.GetBool("PauseBeforeExit"), new Point(4, 86));
            p.Controls.AddRange(new Control[] { c1, c2, c3, c4 });
            BindChk(c1, "ShowCommands"); BindChk(c2, "ExitDosBox", invert: true); BindChk(c3, "PauseBeforeCommands"); BindChk(c4, "PauseBeforeExit");
        }

        // ── EmuMovies ──
        {
            var p = Page("EmuMovies");
            p.Controls.Add(Lbl("User ID", new Point(4, 8)));
            var user = Txt(s.Get("EmuMoviesUserId"), new Point(4, 28), 280); p.Controls.Add(user);
            p.Controls.Add(Lbl("Password", new Point(4, 60)));
            var pwd = Txt(s.Get("EmuMoviesPassword"), new Point(4, 80), 280, pwd: true); p.Controls.Add(pwd);
            BindTxt(user, "EmuMoviesUserId"); BindTxt(pwd, "EmuMoviesPassword");
        }

        // ── GOG ──
        {
            var p = Page("GOG");
            p.Controls.Add(Lbl("Profile Name", new Point(4, 8)));
            var prof = Txt(s.Get("GogProfileName"), new Point(4, 28), 280); p.Controls.Add(prof);
            var galaxy = Chk("Launch games through GOG Galaxy client (when possible)", s.GetBool("GogLaunchWithClient"), new Point(4, 64));
            p.Controls.Add(galaxy);
            BindTxt(prof, "GogProfileName"); BindChk(galaxy, "GogLaunchWithClient");
        }

        // ── LEDBlinky ──
        {
            var p = Page("LEDBlinky");
            var en = Chk("Enable LEDBlinky", s.GetBool("EnableLedBlinky"), new Point(4, 8));
            p.Controls.Add(en);
            p.Controls.Add(Lbl("Path to LEDBlinky.exe file", new Point(4, 38)));
            var path = Txt(s.Get("LedBlinkyPath"), new Point(4, 58), 480); p.Controls.Add(path);
            p.Controls.Add(Browse(new Point(490, 57), () => { using var d = new OpenFileDialog { Filter = "LEDBlinky (LEDBlinky.exe)|LEDBlinky.exe|Executables (*.exe)|*.exe" }; if (d.ShowDialog() == DialogResult.OK) path.Text = d.FileName; }));
            var ss = Chk("Don't start screensaver when entering attract mode", s.GetBool("LedBlinkyDontStartScreensaver"), new Point(4, 92));
            var adv = Chk("Use advanced logic for LEDBlinky filters lists in Big Box (primarily for addon devices)", s.GetBool("LedBlinkyUseAdvanced"), new Point(4, 118));
            p.Controls.AddRange(new Control[] { ss, adv });
            BindChk(en, "EnableLedBlinky"); BindTxt(path, "LedBlinkyPath"); BindChk(ss, "LedBlinkyDontStartScreensaver"); BindChk(adv, "LedBlinkyUseAdvanced");
        }

        // ── MAME ──
        {
            var p = Page("MAME");
            var dl = Chk("Download MAME Community Leaderboards from the LaunchBox Games Database", s.GetBool("DownloadMameCommunityHighScores"), new Point(4, 8));
            var ul = Chk("Upload Your MAME High Scores to the LaunchBox Games Database Community Leaderboards", s.GetBool("UploadMameCommunityHighScores"), new Point(4, 34));
            p.Controls.AddRange(new Control[] { dl, ul });
            BindChk(dl, "DownloadMameCommunityHighScores"); BindChk(ul, "UploadMameCommunityHighScores");
        }

        // ── RetroAchievements ── (the integration most likely to be wired into LiteBox later)
        {
            var p = Page("RetroAchievements");
            p.Controls.Add(Lbl("Username", new Point(4, 8)));
            var user = Txt(s.Get("RetroAchievementsUsername"), new Point(4, 28), 280); p.Controls.Add(user);
            p.Controls.Add(Lbl("Password", new Point(4, 60)));
            var pwd = Txt(s.Get("RetroAchievementsPassword"), new Point(4, 80), 280, pwd: true); p.Controls.Add(pwd);
            p.Controls.Add(Lbl("API Key", new Point(4, 112)));
            var key = Txt(s.Get("RetroAchievementsApiKey"), new Point(4, 132), 380); p.Controls.Add(key);
            // Username/API key/password round-trip; the login Token is LB-managed — never touched.
            BindTxt(user, "RetroAchievementsUsername"); BindTxt(pwd, "RetroAchievementsPassword"); BindTxt(key, "RetroAchievementsApiKey");
        }

        // ── Steam ──
        {
            var p = Page("Steam");
            p.Controls.Add(Lbl("Steam Custom URL  (https://steamcommunity.com/id/…)", new Point(4, 8)));
            var url = Txt(s.Get("SteamUserName"), new Point(4, 28), 360); p.Controls.Add(url);
            p.Controls.Add(Lbl("API Key", new Point(4, 60)));
            var key = Txt(s.Get("SteamApiKey"), new Point(4, 80), 380); p.Controls.Add(key);
            BindTxt(url, "SteamUserName"); BindTxt(key, "SteamApiKey");
        }

        // ── OBS Studio ──
        {
            var p = Page("OBS Studio");
            var auto = Chk("Automatically add OBS Studio recordings to LaunchBox games", s.GetBool("AutoAddObsRecordings"), new Point(4, 8));
            p.Controls.Add(auto);
            p.Controls.Add(Lbl("OBS Studio Video Recordings Folder", new Point(4, 38)));
            var folder = Txt(s.Get("ObsRecordingsFolder"), new Point(4, 58), 480); p.Controls.Add(folder);
            p.Controls.Add(Browse(new Point(490, 57), () => { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) folder.Text = d.SelectedPath; }));
            var ensure = Chk("Make sure OBS Studio is running before launching games", s.GetBool("StartObsWithGames"), new Point(4, 92));
            p.Controls.Add(ensure);
            p.Controls.Add(Lbl("OBS Studio Executable Path", new Point(4, 122)));
            var exe = Txt(s.Get("ObsExePath"), new Point(4, 142), 480); p.Controls.Add(exe);
            p.Controls.Add(Browse(new Point(490, 141), () => { using var d = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" }; if (d.ShowDialog() == DialogResult.OK) exe.Text = d.FileName; }));
            BindChk(auto, "AutoAddObsRecordings"); BindTxt(folder, "ObsRecordingsFolder"); BindChk(ensure, "StartObsWithGames"); BindTxt(exe, "ObsExePath");
        }

        var host = new Panel { BackColor = Bg };
        var note = new Label
        {
            Dock = DockStyle.Top, Height = 22,
            Text = "No impact on LiteBox yet — stored for LaunchBox (and reusable when LiteBox grows these features).",
            ForeColor = Color.FromArgb(225, 95, 95), BackColor = Bg,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
        };
        host.Controls.Add(tabs);
        host.Controls.Add(note);
        tabs.BringToFront();
        apply = () => { foreach (var a in applies) a(); };
        return host;
    }

    // ── Generic media priority list (checklist + Move Up/Down + Revert to Default) ──
    // Stores a CSV of the CHECKED types in priority order. Display = checked first
    // (stored order), then the remaining catalog types alphabetically (LB layout).
    // Tolerant of unknown types: a stored type not in the catalog is kept (checked)
    // and written back, so a future LB type is never dropped.
    private static Control BuildPriorityPanel(LbSettingsStore s, string field, string[] catalog, string defaults, bool readOnly, out Action apply)
    {
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(222, 222, 222),
            BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false, Enabled = !readOnly,
        };

        void Populate(IEnumerable<string> orderedChecked)
        {
            // Keep EVERY checked entry, even one absent from the catalog — an unknown
            // type stored by a future LB stays visible (checked) and round-trips.
            var chk = orderedChecked.Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Items.Clear();
            foreach (var t in chk) list.Items.Add(t, true);
            foreach (var t in catalog.Where(t => !chk.Contains(t, StringComparer.OrdinalIgnoreCase))
                                     .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                list.Items.Add(t, false);
        }
        Populate(s.Get(field).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));

        var right = new Panel { Dock = DockStyle.Right, Width = 150, BackColor = Color.FromArgb(30, 30, 30) };
        var up = MoveBtn("Move Selected Up", 4);
        var down = MoveBtn("Move Selected Down", 38);
        var revert = MoveBtn("Revert to Default", 76);
        up.Enabled = down.Enabled = revert.Enabled = !readOnly;
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var item = list.Items[i]; bool c = list.GetItemChecked(i);
            list.Items.RemoveAt(i); list.Items.Insert(j, item); list.SetItemChecked(j, c); list.SelectedIndex = j;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        revert.Click += (_, _) => Populate(defaults.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        right.Controls.Add(up); right.Controls.Add(down); right.Controls.Add(revert);

        panel.Controls.Add(list);
        panel.Controls.Add(right);
        list.BringToFront();

        apply = () =>
        {
            var picked = new List<string>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i)) picked.Add(list.Items[i].ToString());
            var joined = string.Join(",", picked);
            if (joined != s.Get(field)) s.Set(field, joined);
        };
        return panel;
    }

    private static Button MoveBtn(string text, int top) => new()
    {
        Text = text, Location = new Point(4, top), Size = new Size(142, 28),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 75), ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f),
    };

    // ── tiny fluent helpers (keep the table above readable) ─────────────────
    private static OptionItem Tag(this OptionItem it, bool noImpact) { it.NoImpact = noImpact; return it; }
    private static OptionItem Values(this OptionItem it, params string[] values) { it.ChoiceValues = values; return it; }
}
