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
using LbApiHost.Host.Store;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

/// <summary>Lets MainWindow hand the LiteBox-native RetroAchievements scan to the LB · Integrations →
/// RetroAchievements tab (the scan needs the data manager, which lives in MainWindow). When
/// <see cref="Available"/> is false the scan controls are greyed — RA is being resolved elsewhere
/// (taken over). Null hook ⇒ no scan controls.</summary>
internal sealed class RaScanHook
{
    public const string AllPlatforms = "(All platforms)";
    public bool Available;                                              // false → grey out (taken over)
    public bool Configured;                                            // RA key/username present
    public System.Func<System.Collections.Generic.IEnumerable<string>>? Platforms;
    public System.Action<string, bool>? Run;                          // (platform, full)
    public bool RollingRefresh;                                        // startup rolling-refresh checkbox state
    public System.Action<bool>? SetRollingRefresh;                    // persist the checkbox (apply-live)
    public System.Action? OpenMapping;                                // opens the platform → RA-console editor
}

internal static class LbGlobalOptions
{
    /// <summary>Appends the LaunchBox-settings sections to an options window.
    /// <paramref name="readOnly"/> greys them out entirely.</summary>
    public static void AddSections(OptionsWindow w, LbSettingsStore s, bool readOnly, RaScanHook? raScan = null)
    {
        if (!s.Loaded) return;   // no Settings.xml → nothing to edit
        // Computed once and threaded into every Build*Panel below - each panel's own local
        // S(int) wraps this into every pixel dimension. Same DPI-scale-factor idea as the rest
        // of the app; this file just has a lot of manually-positioned custom panels to cover.
        float dpiS = LiteBoxTheme.DpiScale(w);

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

        w.AddSection("LB · Startup Applications", BuildStartupAppsPanel(s, readOnly, dpiS, out var applyStartupApps),
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

        w.AddSection("LB · Region Priorities", BuildRegionPrioritiesPanel(s, readOnly, dpiS, out var applyRegions),
            readOnly ? null : applyRegions);

        w.AddSection("LB · Auto-Import Media", BuildAutoImportMediaPanel(s, readOnly, dpiS, out var applyMedia),
            readOnly ? null : applyMedia);

        // All media priority lists live under ONE section with internal tabs
        // (LB has ~10 separate sub-pages; we fold them into tabs to cut clutter).
        w.AddSection("LB · Media Priorities", BuildMediaPrioritiesPanel(s, readOnly, dpiS, out var applyPrio),
            readOnly ? null : applyPrio);

        // LB "Integrations" branch, tabbed. None drive LiteBox today (it doesn't run
        // these integrations) — they round-trip to Settings.xml for LaunchBox, and the
        // credentials sit here for a future LiteBox feature (notably RetroAchievements).
        w.AddSection("LB · Integrations", BuildIntegrationsPanel(s, readOnly, dpiS, out var applyInteg, raScan),
            readOnly ? null : applyInteg);

        // LB "Gameplay" branch (Game Startup / Game Pause / Screen Capture), tabbed.
        // These DO drive LiteBox (startup/end/pause overlays + screenshot hotkey).
        w.AddSection("LB · Gameplay", BuildGameplayPanel(s, readOnly, dpiS, out var applyGameplay),
            readOnly ? null : applyGameplay);
    }

    // ── LB "Gameplay" branch: the startup / pause / screen-capture options that
    //    LiteBox actually honours. Screen toggles + times + cosmetics round-trip to
    //    Settings.xml (LB-owned field names); the two HOTKEYS live in LiteBox.ini
    //    (combo-capable, unlike LB's single WPF-Key int). Theme pickers are omitted
    //    (LiteBox has no themes). Changes apply on the next game launch. ──
    private static Control BuildGameplayPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg;
        var Fg = LiteBoxTheme.Fg;
        var Dim = LiteBoxTheme.SubFg;
        var Panel2 = LiteBoxTheme.Panel2;
        var applies = new List<Action>();
        var ini = LiteBoxConfig.LoadForExe();   // PauseHotkey / ScreenCaptureKey live here
        bool iniDirty = false;

        CheckBox Chk(string t, bool v, Point loc) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = v, Enabled = !readOnly };
        Label Lbl(string t, Point loc, Color? c = null) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = c ?? Fg, BackColor = Bg };
        TextBox Txt(string v, Point loc, int w) => new() { Text = v, Location = loc, Width = S(w), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly };
        HotkeyCaptureBox Hk(string v, Point loc, int w) => new(v) { Location = loc, Width = S(w), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly };
        void BindChk(CheckBox cb, string field) => applies.Add(() => { if (cb.Checked != s.GetBool(field)) s.SetBool(field, cb.Checked); });
        void BindTxt(TextBox tb, string field) => applies.Add(() => { if (tb.Text != s.Get(field)) s.Set(field, tb.Text); });
        void BindIniHk(HotkeyCaptureBox hb, string key) => applies.Add(() => { if (hb.HotkeyValue != ini.Get(key)) { ini.Set(key, hb.HotkeyValue); iniDirty = true; } });

        var tabs = NewDarkTabControl(dpiS);
        TabPage Page(string t) { var p = new TabPage(t) { BackColor = Bg, Padding = new Padding(S(12)) }; tabs.TabPages.Add(p); return p; }

        // ── Game Startup (governs the startup "NOW LOADING…" AND end "GAME OVER" screens) ──
        {
            var p = Page("Game Startup");
            var use = Chk("Use Game Startup Screen", s.GetBool("UseStartupScreen", true), new Point(S(4), S(8)));
            p.Controls.Add(use);
            p.Controls.Add(Lbl("(also shows the end “GAME OVER” screen)", new Point(S(28), S(30)), Dim));
            p.Controls.Add(Lbl("Minimum Startup Screen Display Time (ms)", new Point(S(4), S(64))));
            var st = Txt(s.Get("MinimumStartupScreenDisplayTime", "1000"), new Point(S(320), S(61)), 90); p.Controls.Add(st);
            p.Controls.Add(Lbl("Minimum Shutdown Screen Display Time (ms)", new Point(S(4), S(96))));
            var sh = Txt(s.Get("MinimumShutdownScreenDisplayTime", "1000"), new Point(S(320), S(93)), 90); p.Controls.Add(sh);
            var hc = Chk("Hide Mouse Cursor on Startup Screens", s.GetBool("HideMouseCursorOnStartupScreens", true), new Point(S(4), S(128)));
            p.Controls.Add(hc);
            BindChk(use, "UseStartupScreen"); BindTxt(st, "MinimumStartupScreenDisplayTime");
            BindTxt(sh, "MinimumShutdownScreenDisplayTime"); BindChk(hc, "HideMouseCursorOnStartupScreens");
        }

        // ── Game Pause ──
        {
            var p = Page("Game Pause");
            var use = Chk("Use Game Pause Screen", s.GetBool("UsePauseScreen", true), new Point(S(4), S(8)));
            p.Controls.Add(use);
            p.Controls.Add(Lbl("Pause Key", new Point(S(4), S(40))));
            var pk = Hk(ini.Get("PauseHotkey", "Pause"), new Point(S(120), S(37)), 220); p.Controls.Add(pk);
            p.Controls.Add(Lbl("click, then press a key/combo", new Point(S(348), S(40)), Dim));
            var fade = Chk("Enable Fading", s.GetBool("PauseScreenFading", true), new Point(S(4), S(76)));
            var mute = Chk("Mute Audio During Transitions", s.GetBool("PauseScreenMuting", true), new Point(S(4), S(102)));
            p.Controls.AddRange(new Control[] { fade, mute });
            BindChk(use, "UsePauseScreen"); BindIniHk(pk, "PauseHotkey");
            BindChk(fade, "PauseScreenFading"); BindChk(mute, "PauseScreenMuting");
        }

        // ── Screen Capture ──
        {
            var p = Page("Screen Capture");
            p.Controls.Add(Lbl("Screen Capture Key", new Point(S(4), S(12))));
            var sc = Hk(ini.Get("ScreenCaptureKey", ""), new Point(S(150), S(9)), 220); p.Controls.Add(sc);
            p.Controls.Add(Lbl("click, then press a key/combo  (empty = disabled)", new Point(S(378), S(12)), Dim));
            p.Controls.Add(Lbl("Saves a PNG of the game's monitor to <LB>\\Screenshots.", new Point(S(4), S(44)), Dim));
            BindIniHk(sc, "ScreenCaptureKey");
        }

        // Footer note: gameplay changes take effect on the next game launch.
        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = S(24), ForeColor = Dim, BackColor = Bg,
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
    private static Control BuildStartupAppsPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = S(34), ForeColor = Color.FromArgb(150, 150, 152),
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

    private static Control BuildRegionPrioritiesPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = S(24), ForeColor = Color.FromArgb(150, 150, 152),
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

        var right = new Panel { Dock = DockStyle.Right, Width = S(150), BackColor = Color.FromArgb(30, 30, 30) };
        var up = MoveBtn("Move Selected Up", 4, dpiS);
        var down = MoveBtn("Move Selected Down", 38, dpiS);
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

    private static Control BuildAutoImportMediaPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var panel = new Panel { BackColor = Color.FromArgb(30, 30, 30) };

        var top = new Panel { Dock = DockStyle.Top, Height = S(52), BackColor = Color.FromArgb(30, 30, 30) };
        top.Controls.Add(new Label
        {
            Text = "Image downloads limit (per image group) — 0 = No Limit:",
            Location = new Point(S(4), S(6)), AutoSize = true, ForeColor = Color.FromArgb(222, 222, 222),
        });
        var limit = new TextBox
        {
            Location = new Point(S(4), S(26)), Width = S(120), Enabled = !readOnly,
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
    private static TabControl NewDarkTabControl(float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, Multiline = true,
            BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(222, 222, 222),
            SizeMode = TabSizeMode.Normal, Padding = new Point(S(14), S(4)),
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
    private static Control BuildMediaPrioritiesPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var imgCatalog = ImageCatalog(s);
        var applies = new List<Action>();
        var tabs = NewDarkTabControl(dpiS);
        foreach (var (tab, field, defaults, video) in _mediaPriorities)
        {
            var page = new TabPage(tab) { BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(S(2)) };
            var panel = BuildPriorityPanel(s, field, video ? _videoTypes : imgCatalog, defaults, readOnly, dpiS, out var ap);
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
    private static Control BuildIntegrationsPanel(LbSettingsStore s, bool readOnly, float dpiS, out Action apply, RaScanHook? raScan = null)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = Color.FromArgb(30, 30, 30);
        var Fg = Color.FromArgb(222, 222, 222);
        var Panel2 = Color.FromArgb(45, 45, 48);
        var applies = new List<Action>();

        CheckBox Chk(string t, bool v, Point loc) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = v, Enabled = !readOnly };
        Label Lbl(string t, Point loc) => new() { Text = t, Location = loc, AutoSize = true, ForeColor = Fg, BackColor = Bg };
        TextBox Txt(string v, Point loc, int w, bool pwd = false) => new() { Text = v, Location = loc, Width = S(w), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly, UseSystemPasswordChar = pwd };
        Button Browse(Point loc, Action onClick)
        {
            var b = new Button { Text = "Browse…", Location = loc, Size = new Size(S(84), S(24)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f), Enabled = !readOnly };
            b.Click += (_, _) => onClick();
            return b;
        }
        // Bind a checkbox to a bool field (optionally inverted) on apply.
        void BindChk(CheckBox cb, string field, bool invert = false)
            => applies.Add(() => { bool nv = invert ? !cb.Checked : cb.Checked; if (nv != s.GetBool(field)) s.SetBool(field, nv); });
        void BindTxt(TextBox tb, string field)
            => applies.Add(() => { if (tb.Text != s.Get(field)) s.Set(field, tb.Text); });

        var tabs = NewDarkTabControl(dpiS);
        TabPage Page(string t) { var p = new TabPage(t) { BackColor = Bg, Padding = new Padding(12) }; tabs.TabPages.Add(p); return p; }

        // ── DOSBox ──
        {
            var p = Page("DOSBox");
            var c1 = Chk("Show all DOSBox commands", s.GetBool("ShowCommands"), new Point(S(4), S(8)));
            var c2 = Chk("Don't exit DOSBox when exiting games", !s.GetBool("ExitDosBox", true), new Point(S(4), S(34)));
            var c3 = Chk("Pause before each command", s.GetBool("PauseBeforeCommands"), new Point(S(4), S(60)));
            var c4 = Chk("Pause before exiting DOSBox", s.GetBool("PauseBeforeExit"), new Point(S(4), S(86)));
            p.Controls.AddRange(new Control[] { c1, c2, c3, c4 });
            BindChk(c1, "ShowCommands"); BindChk(c2, "ExitDosBox", invert: true); BindChk(c3, "PauseBeforeCommands"); BindChk(c4, "PauseBeforeExit");
        }

        // ── EmuMovies ──
        {
            var p = Page("EmuMovies");
            p.Controls.Add(Lbl("User ID", new Point(S(4), S(8))));
            var user = Txt(s.Get("EmuMoviesUserId"), new Point(S(4), S(28)), 280); p.Controls.Add(user);
            p.Controls.Add(Lbl("Password", new Point(S(4), S(60))));
            var pwd = Txt(s.Get("EmuMoviesPassword"), new Point(S(4), S(80)), 280, pwd: true); p.Controls.Add(pwd);
            BindTxt(user, "EmuMoviesUserId"); BindTxt(pwd, "EmuMoviesPassword");
        }

        // ── GOG ── (credentials round-trip + a LOCAL diagnostics panel: galaxy-2.0.db is the achievements
        //    source (client need not run), plus Galaxy client status and library stats — all offline.)
        {
            var p = Page("GOG");
            var Good = Color.FromArgb(120, 200, 140);
            var Bad = Color.FromArgb(222, 110, 110);
            var Warn = Color.FromArgb(222, 175, 90);
            var Sub = Color.FromArgb(150, 150, 152);
            var LinkC = Color.FromArgb(120, 170, 230);

            LinkLabel Link(string text, Point loc, Func<string?> target)
            {
                var ll = new LinkLabel { Text = text, Location = loc, AutoSize = true, BackColor = Bg, LinkColor = LinkC, ActiveLinkColor = Color.FromArgb(150, 190, 240), Font = new Font("Segoe UI", 8.5f) };
                ll.LinkClicked += (_, _) => { var t = target(); if (!string.IsNullOrEmpty(t)) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t) { UseShellExecute = true }); } catch { } };
                return ll;
            }
            Label Stat(Point loc, int w = 540) => new() { Location = loc, AutoSize = false, Size = new Size(S(w), S(20)), ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.75f) };

            p.Controls.Add(Lbl("Profile Name", new Point(S(4), S(8))));
            var prof = Txt(s.Get("GogProfileName"), new Point(S(4), S(28)), 280); p.Controls.Add(prof);
            p.Controls.Add(Link("Open profile ↗", new Point(S(292), S(31)), () => GogDiagnostics.ProfileUrl(prof.Text)));
            BindTxt(prof, "GogProfileName");
            // NB no "Launch games through GOG Galaxy client" toggle: LiteBox always launches GOG games via
            // the Galaxy shortcut regardless, so the LB setting has no effect here.

            p.Controls.Add(new Label { Text = "Status", Location = new Point(S(4), S(64)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) });
            var dbLbl = Stat(new Point(S(4), S(88)));
            var clientLbl = Stat(new Point(S(4), S(112)));
            p.Controls.Add(dbLbl); p.Controls.Add(clientLbl);
            var explain = new Label { Location = new Point(S(4), S(136)), AutoSize = false, Size = new Size(S(540), S(34)), ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.25f) };
            p.Controls.Add(explain);
            var statsLbl = Stat(new Point(S(4), S(174)));
            p.Controls.Add(statsLbl);
            var reBtn = new Button { Text = "Re-check", Location = new Point(S(4), S(202)), Size = new Size(S(96), S(26)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f) };
            p.Controls.Add(reBtn);

            void Refresh()
            {
                reBtn.Enabled = false;
                dbLbl.Text = "Achievements source: …"; dbLbl.ForeColor = Sub;
                clientLbl.Text = "GOG Galaxy client: …"; clientLbl.ForeColor = Sub;
                statsLbl.Text = "Library: …"; explain.Text = "";
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        bool db = GogDiagnostics.DbPresent();
                        var client = GogDiagnostics.ClientStatus();
                        int lb = GogDiagnostics.LbGogGameCount();
                        var (owned, installed) = GogDiagnostics.LibraryCounts();
                        if (!p.IsHandleCreated) return;
                        p.BeginInvoke((Action)(() =>
                        {
                            if (db) { dbLbl.Text = "Achievements source (galaxy-2.0.db): found ✓ — read locally, Galaxy can stay closed."; dbLbl.ForeColor = Good; }
                            else { dbLbl.Text = "Achievements source (galaxy-2.0.db): not found ✗ — GOG achievements unavailable."; dbLbl.ForeColor = Bad; }

                            switch (client)
                            {
                                case GogDiagnostics.GalaxyState.Running:
                                    clientLbl.Text = "GOG Galaxy client: running ✓"; clientLbl.ForeColor = Good; break;
                                case GogDiagnostics.GalaxyState.Installed:
                                    clientLbl.Text = "GOG Galaxy client: installed, not running (not required for achievements)"; clientLbl.ForeColor = Warn; break;
                                default:
                                    clientLbl.Text = "GOG Galaxy client: not installed"; clientLbl.ForeColor = Sub; break;
                            }

                            explain.Text = db
                                ? "GOG achievements are read directly from Galaxy's local database — the client does NOT need to be running (Galaxy just needs to have synced your library at least once)."
                                : "Install GOG Galaxy and sign in once so it syncs your library; LiteBox then reads achievements from its local database (offline afterwards).";

                            string o = owned >= 0 ? owned.ToString() : "?";
                            string inst = installed >= 0 ? installed.ToString() : "?";
                            statsLbl.Text = $"Library:  {o} owned  ·  {lb} in LaunchBox  ·  {inst} installed on this PC";
                            statsLbl.ForeColor = Fg;
                            reBtn.Enabled = true;
                        }));
                    }
                    catch { }
                });
            }
            reBtn.Click += (_, _) => Refresh();
            bool checkedOnce = false;
            tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == p && !checkedOnce) { checkedOnce = true; Refresh(); } };
        }

        // ── LEDBlinky ──
        {
            var p = Page("LEDBlinky");
            var en = Chk("Enable LEDBlinky", s.GetBool("EnableLedBlinky"), new Point(S(4), S(8)));
            p.Controls.Add(en);
            p.Controls.Add(Lbl("Path to LEDBlinky.exe file", new Point(S(4), S(38))));
            var path = Txt(s.Get("LedBlinkyPath"), new Point(S(4), S(58)), 480); p.Controls.Add(path);
            p.Controls.Add(Browse(new Point(S(490), S(57)), () => { using var d = new OpenFileDialog { Filter = "LEDBlinky (LEDBlinky.exe)|LEDBlinky.exe|Executables (*.exe)|*.exe" }; if (d.ShowDialog() == DialogResult.OK) path.Text = d.FileName; }));
            var ss = Chk("Don't start screensaver when entering attract mode", s.GetBool("LedBlinkyDontStartScreensaver"), new Point(S(4), S(92)));
            var adv = Chk("Use advanced logic for LEDBlinky filters lists in Big Box (primarily for addon devices)", s.GetBool("LedBlinkyUseAdvanced"), new Point(S(4), S(118)));
            p.Controls.AddRange(new Control[] { ss, adv });
            BindChk(en, "EnableLedBlinky"); BindTxt(path, "LedBlinkyPath"); BindChk(ss, "LedBlinkyDontStartScreensaver"); BindChk(adv, "LedBlinkyUseAdvanced");
        }

        // ── MAME ──
        {
            var p = Page("MAME");
            var dl = Chk("Download MAME Community Leaderboards from the LaunchBox Games Database", s.GetBool("DownloadMameCommunityHighScores"), new Point(S(4), S(8)));
            var ul = Chk("Upload Your MAME High Scores to the LaunchBox Games Database Community Leaderboards", s.GetBool("UploadMameCommunityHighScores"), new Point(S(4), S(34)));
            p.Controls.AddRange(new Control[] { dl, ul });
            BindChk(dl, "DownloadMameCommunityHighScores"); BindChk(ul, "UploadMameCommunityHighScores");
        }

        // ── RetroAchievements ── (the integration most likely to be wired into LiteBox later)
        {
            var p = Page("RetroAchievements");
            p.Controls.Add(Lbl("Username", new Point(S(4), S(8))));
            var user = Txt(s.Get("RetroAchievementsUsername"), new Point(S(4), S(28)), 280); p.Controls.Add(user);
            p.Controls.Add(Lbl("Password", new Point(S(4), S(60))));
            var pwd = Txt(s.Get("RetroAchievementsPassword"), new Point(S(4), S(80)), 280, pwd: true); p.Controls.Add(pwd);
            p.Controls.Add(Lbl("API Key", new Point(S(4), S(112))));
            var key = Txt(s.Get("RetroAchievementsApiKey"), new Point(S(4), S(132)), 380); p.Controls.Add(key);
            // Username/API key/password round-trip; the login Token is LB-managed — never touched.
            BindTxt(user, "RetroAchievementsUsername"); BindTxt(pwd, "RetroAchievementsPassword"); BindTxt(key, "RetroAchievementsApiKey");

            // ── Scan (LiteBox resolution of hash/raid) — greyed out when it's taken over elsewhere. ──
            if (raScan != null)
            {
                var Sub = Color.FromArgb(150, 150, 152);
                int y = S(178);
                p.Controls.Add(new Label { Text = "Scan", Location = new Point(S(4), y), AutoSize = true, ForeColor = Fg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) });
                y += S(24);
                bool en = raScan.Available && !readOnly;
                if (!raScan.Available)
                {
                    p.Controls.Add(new Label { Text = "RetroAchievements is currently taken over — manual scanning is disabled here.", Location = new Point(S(4), y), AutoSize = true, MaximumSize = new Size(S(520), S(0)), ForeColor = Sub });
                    y += S(24);
                }
                else if (!raScan.Configured)
                {
                    p.Controls.Add(new Label { Text = "⚠  No username / API key above — hashes are computed but raids won't resolve.", Location = new Point(S(4), y), AutoSize = true, MaximumSize = new Size(S(520), S(0)), ForeColor = Color.FromArgb(222, 175, 90) });
                    y += S(24);
                }
                p.Controls.Add(Lbl("Platform", new Point(S(4), y))); y += S(20);
                var combo = new ComboBox { Location = new Point(S(4), y), Width = S(300), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Panel2, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Enabled = en };
                combo.Items.Add(RaScanHook.AllPlatforms);
                var plats = raScan.Platforms?.Invoke();
                if (plats != null) foreach (var n in plats) combo.Items.Add(n);
                combo.SelectedIndex = 0;
                p.Controls.Add(combo); y += S(34);
                Button ScanBtn(string t, int x)
                {
                    var b = new Button { Text = t, Location = new Point(S(x), y), Size = new Size(S(90), S(26)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, Enabled = en, Font = new Font("Segoe UI", 8.5f) };
                    b.FlatAppearance.BorderSize = 0;
                    return b;
                }
                var lite = ScanBtn("Lite scan", 4);
                var full = ScanBtn("Full scan", 100);
                lite.Click += (_, _) => raScan.Run?.Invoke(combo.SelectedItem as string, false);
                full.Click += (_, _) => raScan.Run?.Invoke(combo.SelectedItem as string, true);
                var mapBtn = new Button { Text = "Platform mapping…", Location = new Point(S(196), y), Size = new Size(S(132), S(26)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, Enabled = raScan.Available, Font = new Font("Segoe UI", 8.5f) };
                mapBtn.FlatAppearance.BorderSize = 0;
                mapBtn.Click += (_, _) => raScan.OpenMapping?.Invoke();
                p.Controls.Add(lite); p.Controls.Add(full); p.Controls.Add(mapBtn); y += S(36);
                var roll = new CheckBox { Text = "Refresh up to 3 stale platform catalogues at startup (rolling background update)", Location = new Point(S(4), y), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = raScan.RollingRefresh, Enabled = en };
                roll.CheckedChanged += (_, _) => raScan.SetRollingRefresh?.Invoke(roll.Checked);
                p.Controls.Add(roll); y += S(28);
                p.Controls.Add(new Label { Text = "Lite: only games with no hash yet.   ·   Full: recompute all (picks up a raid added to RA later).", Location = new Point(S(4), y), AutoSize = true, MaximumSize = new Size(S(540), S(0)), ForeColor = Sub });
            }
        }

        // ── Steam ── (credentials round-trip to Settings.xml + a live diagnostics panel: key validity,
        //    profile link, local client detection, "Game details" public status, and library stats.)
        {
            var p = Page("Steam");
            var Good = Color.FromArgb(120, 200, 140);
            var Bad = Color.FromArgb(222, 110, 110);
            var Warn = Color.FromArgb(222, 175, 90);
            var Sub = Color.FromArgb(150, 150, 152);
            var LinkC = Color.FromArgb(120, 170, 230);

            LinkLabel Link(string text, Point loc, Func<string?> target)
            {
                var ll = new LinkLabel { Text = text, Location = loc, AutoSize = true, BackColor = Bg, LinkColor = LinkC, ActiveLinkColor = Color.FromArgb(150, 190, 240), Font = new Font("Segoe UI", 8.5f) };
                ll.LinkClicked += (_, _) => { var t = target(); if (!string.IsNullOrEmpty(t)) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t) { UseShellExecute = true }); } catch { } };
                return ll;
            }
            Label Stat(Point loc, int w = 520) => new() { Location = loc, AutoSize = false, Size = new Size(S(w), S(20)), ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.75f) };

            p.Controls.Add(Lbl("Steam Custom URL  (https://steamcommunity.com/id/…)  — vanity name or 64-bit id", new Point(S(4), S(8))));
            var url = Txt(s.Get("SteamUserName"), new Point(S(4), S(28)), 300); p.Controls.Add(url);
            p.Controls.Add(Link("Open profile ↗", new Point(S(312), S(31)), () => SteamDiagnostics.ProfileUrl(url.Text)));

            p.Controls.Add(Lbl("API Key", new Point(S(4), S(60))));
            var keyStatus = new Label { Location = new Point(S(70), S(60)), AutoSize = true, ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.75f) };
            p.Controls.Add(keyStatus);
            var key = Txt(s.Get("SteamApiKey"), new Point(S(4), S(80)), 380); p.Controls.Add(key);
            BindTxt(url, "SteamUserName"); BindTxt(key, "SteamApiKey");

            p.Controls.Add(new Label { Text = "Status", Location = new Point(S(4), S(120)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) });
            var clientLbl = Stat(new Point(S(4), S(144)));
            var publicLbl = Stat(new Point(S(4), S(168)));
            p.Controls.Add(clientLbl); p.Controls.Add(publicLbl);
            p.Controls.Add(Link("Set “Game details” privacy ↗", new Point(S(4), S(190)), () => SteamDiagnostics.PrivacyUrl(url.Text)));
            var explain = new Label { Location = new Point(S(4), S(212)), AutoSize = false, Size = new Size(S(540), S(46)), ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.25f) };
            p.Controls.Add(explain);
            var statsLbl = Stat(new Point(S(4), S(264)));
            p.Controls.Add(statsLbl);
            var reBtn = new Button { Text = "Re-check", Location = new Point(S(4), S(292)), Size = new Size(S(96), S(26)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f) };
            p.Controls.Add(reBtn);

            void Refresh()
            {
                string k = key.Text.Trim(), u = url.Text.Trim();
                reBtn.Enabled = false;
                keyStatus.Text = "checking…"; keyStatus.ForeColor = Sub;
                clientLbl.Text = "Steam client: …"; clientLbl.ForeColor = Sub;
                publicLbl.Text = "Web achievements (Game details): …"; publicLbl.ForeColor = Sub;
                statsLbl.Text = "Library: …"; explain.Text = "";
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var client = SteamDiagnostics.ClientStatus();
                        int lb = SteamDiagnostics.LbSteamGameCount();
                        int inst = SteamDiagnostics.InstalledCount();
                        SteamDiagnostics.Probe pr;
                        using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
                            pr = SteamDiagnostics.Run(k, u, cts.Token);
                        if (!p.IsHandleCreated) return;
                        p.BeginInvoke((Action)(() =>
                        {
                            // API key
                            if (k.Length == 0) { keyStatus.Text = "— no key set"; keyStatus.ForeColor = Sub; }
                            else if (pr.KeyValid) { keyStatus.Text = "✓ valid"; keyStatus.ForeColor = Good; }
                            else { keyStatus.Text = "✗ invalid / rejected"; keyStatus.ForeColor = Bad; }

                            // local client (3 levels)
                            switch (client)
                            {
                                case SteamDiagnostics.ClientState.Running:
                                    clientLbl.Text = "Steam client: running ✓"; clientLbl.ForeColor = Good; break;
                                case SteamDiagnostics.ClientState.Installed:
                                    clientLbl.Text = "Steam client: installed, not running (launch it for client-based achievements)"; clientLbl.ForeColor = Warn; break;
                                default:
                                    clientLbl.Text = "Steam client: not installed"; clientLbl.ForeColor = Sub; break;
                            }

                            // Game-details public → web path
                            if (pr.GameDetailsPublic == true)
                            {
                                publicLbl.Text = "Web achievements: profile is Public ✓ — works even with Steam closed.";
                                publicLbl.ForeColor = Good;
                                explain.Text = "“Game details” is public, so LiteBox reads your Steam achievements over the web — no need to keep the Steam client running.";
                            }
                            else if (pr.GameDetailsPublic == false)
                            {
                                publicLbl.Text = "Web achievements: profile is Private ✗ — falling back to the Steam client.";
                                publicLbl.ForeColor = Warn;
                                explain.Text = "“Game details” is private, so LiteBox reads achievements from the running Steam client instead — the client must stay open. Set “Game details” to Public (link above) to use the web path (Steam can then be closed).";
                            }
                            else
                            {
                                publicLbl.Text = "Web achievements (Game details): unknown";
                                publicLbl.ForeColor = Sub;
                                explain.Text = pr.Note != null ? "(" + pr.Note + ")" : "Couldn't determine the profile's achievement visibility.";
                            }

                            // library stats
                            string owned = pr.OwnedCount >= 0 ? pr.OwnedCount.ToString() : "?";
                            string installed = inst >= 0 ? inst.ToString() : "?";
                            statsLbl.Text = $"Library:  {owned} owned  ·  {lb} in LaunchBox  ·  {installed} installed on this PC";
                            statsLbl.ForeColor = Fg;
                            reBtn.Enabled = true;
                        }));
                    }
                    catch { }
                });
            }
            reBtn.Click += (_, _) => Refresh();
            // Probe only when the user actually opens the Steam tab (no network unless they look).
            bool checkedOnce = false;
            tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == p && !checkedOnce) { checkedOnce = true; Refresh(); } };
        }

        // ── OBS Studio ──
        {
            var p = Page("OBS Studio");
            var auto = Chk("Automatically add OBS Studio recordings to LaunchBox games", s.GetBool("AutoAddObsRecordings"), new Point(S(4), S(8)));
            p.Controls.Add(auto);
            p.Controls.Add(Lbl("OBS Studio Video Recordings Folder", new Point(S(4), S(38))));
            var folder = Txt(s.Get("ObsRecordingsFolder"), new Point(S(4), S(58)), 480); p.Controls.Add(folder);
            p.Controls.Add(Browse(new Point(S(490), S(57)), () => { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) folder.Text = d.SelectedPath; }));
            var ensure = Chk("Make sure OBS Studio is running before launching games", s.GetBool("StartObsWithGames"), new Point(S(4), S(92)));
            p.Controls.Add(ensure);
            p.Controls.Add(Lbl("OBS Studio Executable Path", new Point(S(4), S(122))));
            var exe = Txt(s.Get("ObsExePath"), new Point(S(4), S(142)), 480); p.Controls.Add(exe);
            p.Controls.Add(Browse(new Point(S(490), S(141)), () => { using var d = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" }; if (d.ShowDialog() == DialogResult.OK) exe.Text = d.FileName; }));
            BindChk(auto, "AutoAddObsRecordings"); BindTxt(folder, "ObsRecordingsFolder"); BindChk(ensure, "StartObsWithGames"); BindTxt(exe, "ObsExePath");
        }

        apply = () => { foreach (var a in applies) a(); };
        return tabs;
    }

    // ── Generic media priority list (checklist + Move Up/Down + Revert to Default) ──
    // Stores a CSV of the CHECKED types in priority order. Display = checked first
    // (stored order), then the remaining catalog types alphabetically (LB layout).
    // Tolerant of unknown types: a stored type not in the catalog is kept (checked)
    // and written back, so a future LB type is never dropped.
    private static Control BuildPriorityPanel(LbSettingsStore s, string field, string[] catalog, string defaults, bool readOnly, float dpiS, out Action apply)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
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

        var right = new Panel { Dock = DockStyle.Right, Width = S(150), BackColor = Color.FromArgb(30, 30, 30) };
        var up = MoveBtn("Move Selected Up", 4, dpiS);
        var down = MoveBtn("Move Selected Down", 38, dpiS);
        var revert = MoveBtn("Revert to Default", 76, dpiS);
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

    private static Button MoveBtn(string text, int top, float dpiS) => new()
    {
        Text = text, Location = new Point((int)Math.Round(4 * dpiS), (int)Math.Round(top * dpiS)), Size = new Size((int)Math.Round(142 * dpiS), (int)Math.Round(28 * dpiS)),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 75), ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f),
    };

    // ── tiny fluent helpers (keep the table above readable) ─────────────────
    private static OptionItem Tag(this OptionItem it, bool noImpact) { it.NoImpact = noImpact; return it; }
    private static OptionItem Values(this OptionItem it, params string[] values) { it.ChoiceValues = values; return it; }
}
