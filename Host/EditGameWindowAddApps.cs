// Edit Game → "Additional Versions" / "Additional Apps" pages — LiteBox's replica of LaunchBox's
// split management of a game's <AdditionalApplication> records. Single-game only (multi shows a
// placeholder). BOTH kinds live in the SAME storage; LaunchBox routes each record to one page via
// its (obfuscated) AdditionalApplication.IsLikelyVersion(). The rule below was determined
// EMPIRICALLY against LaunchBox 13.28 — 15 controlled records injected into a test library, noting
// which page each landed on:
//
//   version  =  NOT an autorun hook  AND  ( launched through an emulator  OR  has a Version string )
//
//   • AutoRunBefore/AutoRunAfter=true  → always an App (even with full version metadata).
//   • UseEmulator=true                 → Version (metadata not required).
//   • UseEmulator=false + Version set  → Version (a direct-exe version, e.g. a Windows build).
//   • Everything else (bare rows, Region alone, UseDosBox alone) → App.
//
// Pages: a two-column list (Name / Path) + LB's buttons — Versions: Add / Edit / Delete / Make
// Default; Apps: Add / Edit / Delete. Dialogs mirror LB's: the App dialog is launch-only (autorun
// hooks, wait-for-exit); the Version dialog has Launch / Metadata / Game Saves tabs (per-version
// PlayCount/PlayTime/LastPlayed shown in Metadata; the saves tab scans THAT version's ROM through
// SaveManager.ScanApp). Make Default swaps the launch identity (path, command line, emulator,
// DOSBox flag) plus the version identity (Version, Region) between the game and the selected
// version; play statistics stay with their owner (the game's totals remain the game's).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Saves;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private Panel? _verPage, _appPage;
    private ListView? _verList, _appList;
    private Button? _verAdd, _verEdit, _verDel, _verDefault, _appAdd, _appEdit, _appDel;

    private IGame AppsGame => _editGames[0];

    /// <summary>LaunchBox's version-vs-app routing rule (see the file header for the derivation).</summary>
    internal static bool IsLikelyVersion(IAdditionalApplication a)
    {
        try
        {
            return !a.AutoRunBefore && !a.AutoRunAfter
                   && (a.UseEmulator || !string.IsNullOrWhiteSpace(a.Version));
        }
        catch { return false; }
    }

    // ── Pages ─────────────────────────────────────────────────────────────

    private Control BuildAdditionalVersionsPage()
    {
        _verPage = new Panel { BackColor = Bg };
        _verList = NewAppListView();
        _verList.DoubleClick += (_, _) => { var a = SelectedApp(_verList); if (a != null && ShowVersionDialog(a)) ReloadAddApps(); };
        _verList.SelectedIndexChanged += (_, _) => UpdateAddAppButtons();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        _verAdd = FooterBtn("Add Additional Version…", Color.FromArgb(60, 60, 72));
        _verEdit = FooterBtn("Edit Additional Version…", Color.FromArgb(60, 60, 72));
        _verDel = FooterBtn("Delete Additional Version", Color.FromArgb(60, 60, 72));
        _verDefault = FooterBtn("Make Default", Color.FromArgb(50, 110, 65));
        _verAdd.Click += (_, _) => { if (ShowVersionDialog(null)) ReloadAddApps(); };
        _verEdit.Click += (_, _) => { var a = SelectedApp(_verList); if (a != null && ShowVersionDialog(a)) ReloadAddApps(); };
        _verDel.Click += (_, _) => DeleteApp(SelectedApp(_verList), "version");
        _verDefault.Click += (_, _) => { var a = SelectedApp(_verList); if (a != null) MakeDefaultVersion(a); };
        bottom.Controls.AddRange(new Control[] { _verAdd, _verEdit, _verDel, _verDefault });
        bottom.Resize += (_, _) =>
        {
            _verAdd.SetBounds(S(6), S(8), S(196), S(30));
            _verEdit.SetBounds(S(208), S(8), S(196), S(30));
            _verDel.SetBounds(S(410), S(8), S(196), S(30));
            _verDefault.SetBounds(bottom.ClientSize.Width - S(146), S(8), S(140), S(30));
        };

        _verPage.Controls.Add(_verList);
        _verPage.Controls.Add(bottom);
        _verList.BringToFront();
        ReloadAddApps();
        return _verPage;
    }

    private Control BuildAdditionalAppsPage()
    {
        _appPage = new Panel { BackColor = Bg };

        // LB's explanatory header (verbatim).
        var blurb = new Label
        {
            Dock = DockStyle.Top, Height = S(44), BackColor = Bg, ForeColor = SubFg,
            Padding = new Padding(S(4), S(6), S(4), 0),
            Text = "Additional applications allow you to specify additional commands to run for your game.  "
                 + "These commands will then be available in the right-click menu for the game.",
        };

        _appList = NewAppListView();
        _appList.DoubleClick += (_, _) => { var a = SelectedApp(_appList); if (a != null && ShowAppDialog(a)) ReloadAddApps(); };
        _appList.SelectedIndexChanged += (_, _) => UpdateAddAppButtons();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        _appAdd = FooterBtn("Add Application…", Color.FromArgb(60, 60, 72));
        _appEdit = FooterBtn("Edit Application…", Color.FromArgb(60, 60, 72));
        _appDel = FooterBtn("Delete Application", Color.FromArgb(60, 60, 72));
        _appAdd.Click += (_, _) => { if (ShowAppDialog(null)) ReloadAddApps(); };
        _appEdit.Click += (_, _) => { var a = SelectedApp(_appList); if (a != null && ShowAppDialog(a)) ReloadAddApps(); };
        _appDel.Click += (_, _) => DeleteApp(SelectedApp(_appList), "application");
        bottom.Controls.AddRange(new Control[] { _appAdd, _appEdit, _appDel });
        bottom.Resize += (_, _) =>
        {
            _appAdd.SetBounds(S(6), S(8), S(170), S(30));
            _appEdit.SetBounds(S(182), S(8), S(170), S(30));
            _appDel.SetBounds(S(358), S(8), S(170), S(30));
        };

        _appPage.Controls.Add(_appList);
        _appPage.Controls.Add(blurb);
        _appPage.Controls.Add(bottom);
        _appList.BringToFront();
        ReloadAddApps();
        return _appPage;
    }

    private ListView NewAppListView()
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            HideSelection = false, OwnerDraw = true,
        };
        lv.Columns.Add("Name", S(200));
        lv.Columns.Add("Path", S(430));
        // Owner-drawn header (the default one is light); items keep the default renderer, which
        // respects the ListView's dark Back/ForeColor + selection.
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
        lv.Resize += (_, _) => { try { lv.Columns[1].Width = Math.Max(S(200), lv.ClientSize.Width - lv.Columns[0].Width - S(4)); } catch { } };
        return lv;
    }

    private static IAdditionalApplication? SelectedApp(ListView? lv)
        => lv?.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as IAdditionalApplication : null;

    private void ReloadAddAppsIfBuilt() { if (_verList != null || _appList != null) ReloadAddApps(); }

    private void ReloadAddApps()
    {
        IAdditionalApplication[] apps;
        try { apps = AppsGame.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { apps = Array.Empty<IAdditionalApplication>(); }
        // Documents (Edit Game → Documents tab; Section=="Document") are additional-application records too,
        // but IsLikelyVersion has no concept of them (its rule predates that tab) and routes every one into
        // the "Apps" bucket below — exclude them here so they're managed exclusively by the dedicated Documents
        // page instead of ALSO being editable/deletable/launchable from this generic one.
        apps = apps.Where(a => a is not Data.HostAdditionalApplication { IsDocument: true }).ToArray();
        // The CURRENT DEFAULT (the row twinning the game's own ROM) sorts first and is tagged, so a
        // Make Default is immediately readable; the rest keeps the Priority order.
        string gPath = Safe(() => AppsGame.ApplicationPath) ?? "";
        FillAppList(_verList,
            apps.Where(a => a != null && IsLikelyVersion(a))
                .OrderByDescending(a => AppPathEq(Safe(() => a.ApplicationPath) ?? "", gPath))
                .ThenBy(a => Safe(() => a.Priority)),
            defaultPath: gPath);
        FillAppList(_appList, apps.Where(a => a != null && !IsLikelyVersion(a)));
        UpdateAddAppButtons();
    }

    private static void FillAppList(ListView? lv, IEnumerable<IAdditionalApplication> apps, string defaultPath = "")
    {
        if (lv == null) return;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var a in apps)
        {
            string name = Safe(() => a.Name) ?? "", path = Safe(() => a.ApplicationPath) ?? "";
            bool isDefault = defaultPath.Length > 0 && AppPathEq(path, defaultPath);
            var it = new ListViewItem(isDefault ? "★ " + name : name) { Tag = a };
            if (isDefault) it.ForeColor = Color.FromArgb(120, 220, 130);   // same green as the saves "Active" pill
            it.SubItems.Add(path);
            lv.Items.Add(it);
        }
        lv.EndUpdate();
    }

    private void UpdateAddAppButtons()
    {
        bool w = !_readOnly;
        bool vSel = _verList?.SelectedItems.Count > 0, aSel = _appList?.SelectedItems.Count > 0;
        if (_verAdd != null) _verAdd.Enabled = w;
        if (_verEdit != null) _verEdit.Enabled = vSel;              // read-only may still VIEW
        if (_verDel != null) _verDel.Enabled = w && vSel;
        if (_verDefault != null) _verDefault.Enabled = w && vSel;
        if (_appAdd != null) _appAdd.Enabled = w;
        if (_appEdit != null) _appEdit.Enabled = aSel;
        if (_appDel != null) _appDel.Enabled = w && aSel;
    }

    private void DeleteApp(IAdditionalApplication? a, string kind)
    {
        if (a == null || _readOnly) return;
        string name = Safe(() => a.Name) ?? "";
        if (MessageBox.Show(this, $"Delete the additional {kind} \"{name}\"?", "Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { AppsGame.TryRemoveAdditionalApplication(a); } catch (Exception ex) { Console.WriteLine("[addapps] delete failed: " + ex.Message); }
        ReloadAddApps();
    }

    // ── Make Default ─────────────────────────────────────────────────────
    // LB's data model keeps a version ROW for EVERY version, INCLUDING the current default (a "twin"
    // row whose ApplicationPath equals the game's — the user's imported libraries all carry one). So
    // Make Default must NOT swap/overwrite the selected row (that made it vanish into a duplicate of
    // the old default). Instead:
    //   1. the selected version's launch identity (ROM, command line, emulator, DOSBox) + version
    //      identity (Version, Region) are COPIED to the game — its row stays untouched (it simply
    //      becomes the new default's twin);
    //   2. the OLD default stays reachable: if no other row already points at the game's previous
    //      ROM (no twin existed), a new version row is created carrying the old launch identity.
    // Play statistics are never moved — the game's totals stay the game's, each row keeps its own.
    private void MakeDefaultVersion(IAdditionalApplication a)
    {
        if (_readOnly) return;
        var g = AppsGame;
        string aName = Safe(() => a.Name) ?? "";
        if (MessageBox.Show(this,
                $"Make \"{aName}\" the default version?\n\nThe game will launch this version (its ROM file, command line, "
                + "emulator, DOSBox flag and Version/Region are copied to the game). The previous default stays available "
                + "in the versions list.",
                "Make Default", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SaveCurrent();   // persist any pending metadata-page edits BEFORE the reload below discards them

        try
        {
            string gPath = Safe(() => g.ApplicationPath) ?? "", gCmd = Safe(() => g.CommandLine) ?? "";
            string gEmu = Safe(() => g.EmulatorId) ?? "", gVer = Safe(() => g.Version) ?? "", gReg = Safe(() => g.Region) ?? "";
            bool gDos = false; try { gDos = g.UseDosBox; } catch { }
            bool gHasEmu = gEmu.Length > 0 && gEmu != Guid.Empty.ToString();

            // 1. Keep the old default reachable — create a row for it only when no twin already exists.
            IAdditionalApplication[] apps;
            try { apps = g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
            catch { apps = Array.Empty<IAdditionalApplication>(); }
            bool twinExists = apps.Any(x => x != null && !ReferenceEquals(x, a)
                                            && !string.Equals(Safe(() => x.Id), Safe(() => a.Id), StringComparison.OrdinalIgnoreCase)
                                            && AppPathEq(Safe(() => x.ApplicationPath) ?? "", gPath));
            if (!twinExists && gPath.Length > 0)
            {
                var d = g.AddNewAdditionalApplication();
                if (d != null)
                {
                    d.Name = gVer.Length > 0 ? $"Play {gVer} version…" : "Play previous version…";
                    d.ApplicationPath = gPath;
                    d.CommandLine = gCmd;
                    d.EmulatorId = gHasEmu ? gEmu : "";
                    d.UseEmulator = gHasEmu;
                    d.UseDosBox = gDos;
                    d.Version = gVer;
                    d.Region = gReg;
                    d.Priority = NextPriority(g);
                }
            }

            // 2. The selected version becomes the game's launch target; its row is left untouched.
            g.ApplicationPath = Safe(() => a.ApplicationPath) ?? "";
            g.CommandLine = Safe(() => a.CommandLine) ?? "";
            g.EmulatorId = (Safe(() => a.UseEmulator ? a.EmulatorId : "") ?? "");
            try { g.UseDosBox = a.UseDosBox; } catch { }
            g.Version = Safe(() => a.Version) ?? "";
            g.Region = Safe(() => a.Region) ?? "";
        }
        catch (Exception ex) { Console.WriteLine("[addapps] make-default failed: " + ex.Message); }

        ReloadAddApps();
        LoadMetadata();            // the game's own fields changed → refresh page + baselines
        ReloadGameSavesIfBuilt();  // the game's ApplicationPath changed → base-view saves change too
    }

    /// <summary>Path equality for twin detection: separators normalized, case-insensitive.</summary>
    private static bool AppPathEq(string x, string y)
        => string.Equals(x.Replace('/', '\\').Trim(), y.Replace('/', '\\').Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Shared dialog helpers ────────────────────────────────────────────

    private Label DlgCap(Control parent, string text, int x, int y)
    {
        var l = new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = SubFg, BackColor = Bg };
        parent.Controls.Add(l);
        return l;
    }

    private TextBox DlgTxt(Control parent, string value, int x, int y, int w)
    {
        var t = new TextBox { Text = value, Location = new Point(x, y), Width = w, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        parent.Controls.Add(t);
        return t;
    }

    private ComboBox DlgCbo(Control parent, string choiceKey, string value, int x, int y, int w)
    {
        var c = new ComboBox
        {
            Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        };
        try
        {
            var items = MetadataChoicesCache.Get(choiceKey, PluginHelper.DataManager);
            if (items.Length > 0) c.Items.AddRange(items);
        }
        catch { }
        c.Text = value;
        bool ac = false;   // lazy autocomplete — building the index at handle-creation freezes on big lists
        c.Enter += (_, _) =>
        {
            if (ac || c.Items.Count == 0) return; ac = true;
            c.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            c.AutoCompleteSource = AutoCompleteSource.ListItems;
        };
        parent.Controls.Add(c);
        return c;
    }

    private CheckBox DlgChk(Control parent, string text, bool value, int x, int y)
    {
        var cb = new CheckBox { Text = text, Checked = value, AutoSize = true, Location = new Point(x, y), ForeColor = Fg, BackColor = Bg };
        parent.Controls.Add(cb);
        return cb;
    }

    /// <summary>All selectable emulators (id, title), LB's hidden zero-GUID placeholder filtered out.</summary>
    private static List<(string id, string title)> EmulatorChoices()
    {
        var list = new List<(string id, string title)>();
        try
        {
            foreach (var e in PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>())
            {
                if (e == null) continue;
                string id = Safe(() => e.Id) ?? "";
                if (id.Length == 0 || id == Guid.Empty.ToString()) continue;
                string t = Safe(() => e.Title) ?? "";
                list.Add((id, t.Length > 0 ? t : id));
            }
        }
        catch { }
        list.Sort((x, y) => string.Compare(x.title, y.title, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Browse for a file; the result is stored LB-style — relative when under the LB root.</summary>
    private void BrowseInto(TextBox target, string title)
    {
        using var dlg = new OpenFileDialog { Title = title, CheckFileExists = false };
        try
        {
            string cur = target.Text.Trim();
            if (cur.Length > 0)
            {
                string abs = Path.IsPathRooted(cur) ? cur : Path.Combine(SaveManager.LbRoot, cur);
                var dir = Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            if (string.IsNullOrEmpty(dlg.InitialDirectory)) dlg.InitialDirectory = SaveManager.LbRoot;
        }
        catch { }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string p = dlg.FileName;
        try
        {
            string root = SaveManager.LbRoot.TrimEnd('\\');
            if (root.Length > 0 && p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(root.Length + 1);
        }
        catch { }
        target.Text = p;
    }

    // ── "Edit Application" dialog (Additional App — launch hooks only) ────

    private bool ShowAppDialog(IAdditionalApplication? app)
    {
        var g = AppsGame;
        using var f = NewDialog(app == null ? "Add Application" : "Edit Application", 680, 470);

        var tabs = NewDarkTabs(f);
        var launch = NewTabPage(tabs, "Launch");

        int x = S(16), w = S(610), y = S(14);
        DlgCap(launch, "Application Name:", x, y); y += S(20);
        var name = DlgTxt(launch, Safe(() => app?.Name) ?? "", x, y, w); y += S(34);
        DlgCap(launch, "Application Path:", x, y); y += S(20);
        var path = DlgTxt(launch, Safe(() => app?.ApplicationPath) ?? "", x, y, w - S(90));
        var browse = DlgBtn("Browse…", Color.FromArgb(60, 60, 72));
        browse.Location = new Point(x + w - S(82), y - S(2));
        browse.Click += (_, _) => BrowseInto(path, "Select the application");
        launch.Controls.Add(browse); y += S(34);
        DlgCap(launch, "Application Command-Line Parameters:", x, y); y += S(20);
        var cmd = DlgTxt(launch, Safe(() => app?.CommandLine) ?? "", x, y, w); y += S(40);

        var useDos = DlgChk(launch, "Use DOSBox", Safe(() => app?.UseDosBox) == true, x, y);
        var useEmu = DlgChk(launch, "Use Emulator:", Safe(() => app?.UseEmulator) == true, x + S(120), y);
        var emus = EmulatorChoices();
        var emuCbo = new ComboBox
        {
            Location = new Point(x + S(240), y - S(2)), Width = w - S(240), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Enabled = useEmu.Checked,
        };
        foreach (var e in emus) emuCbo.Items.Add(e.title);
        SelectEmulator(emuCbo, emus, Safe(() => app?.EmulatorId) ?? "");
        launch.Controls.Add(emuCbo); y += S(38);

        var before = DlgChk(launch, "Automatically Run Before Main Application", Safe(() => app?.AutoRunBefore) == true, x, y); y += S(28);
        var after = DlgChk(launch, "Automatically Run After Main Application", Safe(() => app?.AutoRunAfter) == true, x, y); y += S(28);
        var wait = DlgChk(launch, "Wait for Exit", Safe(() => app?.WaitForExit) == true, x, y);

        void SyncEnabled()
        {
            emuCbo.Enabled = useEmu.Checked;
            wait.Enabled = before.Checked || after.Checked;   // LB greys it unless the app is an autorun hook
        }
        useDos.CheckedChanged += (_, _) => { if (useDos.Checked) useEmu.Checked = false; SyncEnabled(); };
        useEmu.CheckedChanged += (_, _) => { if (useEmu.Checked) useDos.Checked = false; SyncEnabled(); };
        before.CheckedChanged += (_, _) => SyncEnabled();
        after.CheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();

        return RunAddAppDialog(f, app, () =>
        {
            var a = app ?? g.AddNewAdditionalApplication();
            if (a == null) return false;
            ApplyStr(v => a.Name = v, Safe(() => a.Name), name.Text.Trim());
            ApplyStr(v => a.ApplicationPath = v, Safe(() => a.ApplicationPath), path.Text.Trim());
            ApplyStr(v => a.CommandLine = v, Safe(() => a.CommandLine), cmd.Text.Trim());
            ApplyBool(v => a.UseDosBox = v, Safe(() => a.UseDosBox), useDos.Checked);
            ApplyBool(v => a.UseEmulator = v, Safe(() => a.UseEmulator), useEmu.Checked);
            ApplyStr(v => a.EmulatorId = v, Safe(() => a.EmulatorId), useEmu.Checked ? EmulatorIdAt(emuCbo, emus) : "");
            ApplyBool(v => a.AutoRunBefore = v, Safe(() => a.AutoRunBefore), before.Checked);
            ApplyBool(v => a.AutoRunAfter = v, Safe(() => a.AutoRunAfter), after.Checked);
            ApplyBool(v => a.WaitForExit = v, Safe(() => a.WaitForExit), wait.Checked && wait.Enabled);
            return true;
        });
    }

    // ── "Edit Additional Version" dialog (Launch / Metadata / Game Saves) ─

    private bool ShowVersionDialog(IAdditionalApplication? app)
    {
        var g = AppsGame;
        using var f = NewDialog(app == null ? "Add Additional Version" : "Edit Additional Version", 680, 540);

        var tabs = NewDarkTabs(f);
        var launch = NewTabPage(tabs, "Launch");
        var meta = NewTabPage(tabs, "Metadata");

        // ── Launch tab ──
        int x = S(16), w = S(610), y = S(14);
        DlgCap(launch, "Application Name:", x, y); y += S(20);
        var name = DlgTxt(launch, Safe(() => app?.Name) ?? "", x, y, w); y += S(34);
        DlgCap(launch, "ROM File:", x, y); y += S(20);
        var path = DlgTxt(launch, Safe(() => app?.ApplicationPath) ?? "", x, y, w - S(90));
        var browse = DlgBtn("Browse…", Color.FromArgb(60, 60, 72));
        browse.Location = new Point(x + w - S(82), y - S(2));
        browse.Click += (_, _) => BrowseInto(path, "Select the ROM file");
        launch.Controls.Add(browse); y += S(34);
        DlgCap(launch, "Application Command-Line Parameters:", x, y); y += S(20);
        var cmd = DlgTxt(launch, Safe(() => app?.CommandLine) ?? "", x, y, w); y += S(40);

        // New version: preselect the game's emulator (the overwhelmingly common case).
        bool initUseEmu = app != null ? Safe(() => app.UseEmulator) : GameHasEmulator(g);
        string initEmuId = app != null ? (Safe(() => app.EmulatorId) ?? "") : (Safe(() => g.EmulatorId) ?? "");
        var useDos = DlgChk(launch, "Use DOSBox", Safe(() => app?.UseDosBox) == true, x, y);
        var useEmu = DlgChk(launch, "Use Emulator:", initUseEmu, x + S(120), y);
        var emus = EmulatorChoices();
        var emuCbo = new ComboBox
        {
            Location = new Point(x + S(240), y - S(2)), Width = w - S(240), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Enabled = initUseEmu,
        };
        foreach (var e in emus) emuCbo.Items.Add(e.title);
        SelectEmulator(emuCbo, emus, initEmuId);
        launch.Controls.Add(emuCbo);
        useDos.CheckedChanged += (_, _) => { if (useDos.Checked) useEmu.Checked = false; };
        useEmu.CheckedChanged += (_, _) => { if (useEmu.Checked) useDos.Checked = false; emuCbo.Enabled = useEmu.Checked; };

        // ── Metadata tab ── (two columns, LB layout)
        int lx = S(16), lw = S(286), rx = S(330), rw = S(286);
        int ly = S(14), ry = S(14);
        DlgCap(meta, "Version:", lx, ly); ly += S(20);
        var version = DlgTxt(meta, Safe(() => app?.Version) ?? "", lx, ly, lw); ly += S(34);
        DlgCap(meta, "Region:", rx, ry); ry += S(20);
        var region = DlgCbo(meta, "Region", Safe(() => app?.Region) ?? "", rx, ry, rw); ry += S(34);
        DlgCap(meta, "Developer:", lx, ly); ly += S(20);
        var developer = DlgCbo(meta, "Developer", Safe(() => app?.Developer) ?? "", lx, ly, lw); ly += S(34);
        DlgCap(meta, "Publisher:", rx, ry); ry += S(20);
        var publisher = DlgCbo(meta, "Publisher", Safe(() => app?.Publisher) ?? "", rx, ry, rw); ry += S(34);
        DlgCap(meta, "Status:", lx, ly); ly += S(20);
        var status = DlgCbo(meta, "Status", Safe(() => app?.Status) ?? "", lx, ly, lw); ly += S(34);
        DlgCap(meta, "Last Played:", rx, ry); ry += S(20);
        var lastPlayed = DlgTxt(meta, FmtDate(Safe(() => app?.LastPlayed)), rx, ry, rw); ry += S(34);
        DlgCap(meta, "Disc:", lx, ly); ly += S(20);
        var disc = new NumericUpDown
        {
            Location = new Point(lx, ly), Width = S(80), Minimum = 0, Maximum = 999,
            Value = Math.Max(0, Math.Min(999, Safe(() => app?.Disc) ?? 0)),
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        meta.Controls.Add(disc);
        var sideA = DlgChk(meta, "Side A", Safe(() => app?.SideA) == true, lx + S(100), ly + S(2));
        var sideB = DlgChk(meta, "Side B", Safe(() => app?.SideB) == true, lx + S(180), ly + S(2));
        ly += S(34);
        DlgCap(meta, "Release Date:", rx, ry); ry += S(20);
        var release = DlgTxt(meta, FmtDate(Safe(() => app?.ReleaseDate)), rx, ry, rw); ry += S(40);
        var installed = DlgChk(meta, "Installed", Safe(() => app?.Installed) == true, lx, ly + S(6));
        int pc = Safe(() => app?.PlayCount) ?? 0, pt = Safe(() => app?.PlayTime) ?? 0;
        meta.Controls.Add(new Label
        {
            Text = $"Play Count(Time):   {pc} ({FmtDuration(pt)})",
            AutoSize = true, Location = new Point(rx, ry), ForeColor = SubFg, BackColor = Bg,
        });

        // ── Game Saves tab (existing versions only — a new one has no ROM to scan yet) ──
        // Full parity with the game's Game Saves page: the SAME SavesPane control (cards, status
        // dots, action menus, Backup History, Import buttons), scoped to THIS version's ROM.
        if (app != null)
        {
            var saves = NewTabPage(tabs, "Game Saves");
            var pane = new SavesPane(this);
            saves.Controls.Add(pane);
            pane.Rescan(g, app);   // scan fires when the tab first shows (handle-created deferral)
        }

        return RunAddAppDialog(f, app, () =>
        {
            var a = app ?? g.AddNewAdditionalApplication();
            if (a == null) return false;
            ApplyStr(v => a.Name = v, Safe(() => a.Name), name.Text.Trim());
            ApplyStr(v => a.ApplicationPath = v, Safe(() => a.ApplicationPath), path.Text.Trim());
            ApplyStr(v => a.CommandLine = v, Safe(() => a.CommandLine), cmd.Text.Trim());
            ApplyBool(v => a.UseDosBox = v, Safe(() => a.UseDosBox), useDos.Checked);
            ApplyBool(v => a.UseEmulator = v, Safe(() => a.UseEmulator), useEmu.Checked);
            ApplyStr(v => a.EmulatorId = v, Safe(() => a.EmulatorId), useEmu.Checked ? EmulatorIdAt(emuCbo, emus) : "");
            ApplyStr(v => a.Version = v, Safe(() => a.Version), version.Text.Trim());
            ApplyStr(v => a.Region = v, Safe(() => a.Region), region.Text.Trim());
            ApplyStr(v => a.Developer = v, Safe(() => a.Developer), developer.Text.Trim());
            ApplyStr(v => a.Publisher = v, Safe(() => a.Publisher), publisher.Text.Trim());
            ApplyStr(v => a.Status = v, Safe(() => a.Status), status.Text.Trim());
            try { var d = ParseDate(lastPlayed.Text); if (d != Safe(() => a.LastPlayed)) a.LastPlayed = d; } catch { }
            try { var d = ParseDate(release.Text); if (d != Safe(() => a.ReleaseDate)) a.ReleaseDate = d; } catch { }
            try { int? dv = disc.Value == 0 ? (int?)null : (int)disc.Value; if (dv != Safe(() => a.Disc)) a.Disc = dv; } catch { }
            ApplyBool(v => a.SideA = v, Safe(() => a.SideA), sideA.Checked);
            ApplyBool(v => a.SideB = v, Safe(() => a.SideB), sideB.Checked);
            try { bool? iv = installed.Checked ? true : (Safe(() => a.Installed) == null ? (bool?)null : false); if (iv != Safe(() => a.Installed)) a.Installed = iv; } catch { }
            if (app == null)   // new version → append at the end of the version list
                try { a.Priority = NextPriority(g); } catch { }
            return true;
        });
    }

    private static bool GameHasEmulator(IGame g)
    {
        string id = Safe(() => g.EmulatorId) ?? "";
        return id.Length > 0 && id != Guid.Empty.ToString();
    }

    private static int NextPriority(IGame g)
    {
        int max = 0;
        try { foreach (var a in g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>()) max = Math.Max(max, Safe(() => a.Priority)); }
        catch { }
        return max + 1;
    }

    private static void SelectEmulator(ComboBox cbo, List<(string id, string title)> emus, string emulatorId)
    {
        int ix = emus.FindIndex(e => string.Equals(e.id, emulatorId, StringComparison.OrdinalIgnoreCase));
        if (ix >= 0) cbo.SelectedIndex = ix;
        else if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
    }

    private static string EmulatorIdAt(ComboBox cbo, List<(string id, string title)> emus)
        => cbo.SelectedIndex >= 0 && cbo.SelectedIndex < emus.Count ? emus[cbo.SelectedIndex].id : "";

    private static void ApplyStr(Action<string> set, string? current, string value)
    { try { if (!string.Equals(current ?? "", value, StringComparison.Ordinal)) set(value); } catch { } }

    private static void ApplyBool(Action<bool> set, bool? current, bool value)
    { try { if (current != value) set(value); } catch { } }

    /// <summary>Shared dialog chrome: dark tabs already added by the caller; append the OK / Cancel
    /// footer, run modal, and invoke <paramref name="apply"/> on OK (read-only mode disables OK).</summary>
    private bool RunAddAppDialog(Form f, IAdditionalApplication? app, Func<bool> apply)
    {
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var ok = DlgBtn("✔ OK", Color.FromArgb(50, 110, 65));
        var cancel = DlgBtn("✘ Cancel", Color.FromArgb(70, 70, 82));
        ok.Enabled = !_readOnly;
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
            try { changed = apply(); }
            catch (Exception ex) { Console.WriteLine("[addapps] apply failed: " + ex.Message); }
            f.DialogResult = DialogResult.OK; f.Close();
        };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.ShowDialog(this);
        return changed;
    }

    private TabControl NewDarkTabs(Form f)
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(S(96), S(26)), Padding = new Point(S(8), S(4)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, f.Font, e.Bounds,
                sel ? Color.White : SubFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        f.Controls.Add(tabs);
        tabs.BringToFront();
        return tabs;
    }

    private TabPage NewTabPage(TabControl tabs, string title)
    {
        var p = new TabPage(title) { BackColor = Bg, UseVisualStyleBackColor = false };
        tabs.TabPages.Add(p);
        return p;
    }

}
