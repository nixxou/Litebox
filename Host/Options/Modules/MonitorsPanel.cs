// Monitor Profiles module config panel — Options ▸ Modules ▸ Monitor profiles.
//
// This is the ONLY editor for the feature. It started life as a standalone modal, and that was a
// mistake: the Tools shortcut and the module tab would have been two windows with identical content to
// keep in step. Tools ▸ Monitor Profiles ▸ Manage… now opens the options window straight on this tab.
//
// Two sub-tabs:
//   "Profiles"     the profile list + the four parts of the selected one. Complete.
//   "Assignments"  reserved for binding a profile to an emulator / a game / something else — the shape
//                  of that is not decided yet, so the tab announces itself and does nothing. It exists
//                  now so the layout the feature will grow into is visible from the start.
//
// LAYOUT IS CAPTURED, NOT COMPOSED. No drag-the-monitors canvas: the user arranges the desktop in
// Windows' own display settings (far better at it, and where they already know how) and presses
// "Capture Current Layout". Every value stored is therefore one Windows itself produced — and will
// accept back.
//
// Edits live on a deep copy of the store's list until the options window's Apply/OK calls our apply
// callback, so Cancel discards them. A profile with all four parts off is dropped on save (it would be
// a name that does nothing) — the list marks those, rather than letting them vanish silently.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Monitors;
using LbApiHost.Host.UiKit;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Options;

internal static class MonitorsPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        var editor = new ProfilesEditor(dpiS, readOnly);

        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg };
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(ModulePanelKit.Sc(dpiS, 130), ModulePanelKit.Sc(dpiS, 25)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? ModulePanelKit.Field : ModulePanelKit.Panel);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                sel ? Color.White : ModulePanelKit.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        var generalPage = new TabPage("General") { BackColor = ModulePanelKit.Bg, UseVisualStyleBackColor = false };
        var (generalPanel, generalApply) = BuildGeneral(dpiS, readOnly);
        generalPage.Controls.Add(generalPanel);
        tabs.TabPages.Add(generalPage);

        var profilesPage = new TabPage("Profiles") { BackColor = ModulePanelKit.Bg, UseVisualStyleBackColor = false };
        profilesPage.Controls.Add(editor.Root);
        tabs.TabPages.Add(profilesPage);

        var assignPage = new TabPage("Assignments") { BackColor = ModulePanelKit.Bg, UseVisualStyleBackColor = false };
        assignPage.Controls.Add(BuildAssignments(dpiS, readOnly));
        tabs.TabPages.Add(assignPage);

        root.Controls.Add(tabs);
        // Profiles first: it is what the tab is for. General is one switch, and Assignments is a promise.
        tabs.SelectedTab = profilesPage;
        return (root, () => { generalApply(); editor.Apply(); });
    }

    /// <summary>Module-wide settings — one so far, and it is a gate rather than a preference.
    ///
    /// The web endpoints are OFF by default and depend on the Web module: an unauthenticated LAN URL that
    /// rearranges someone's desktop is not something to enable on their behalf. When the Web module is
    /// off the checkbox is greyed and says why, rather than silently doing nothing once ticked.</summary>
    private static (Control panel, Action apply) BuildGeneral(float dpiS, bool readOnly)
    {
        var p = ModulePanelKit.Root(dpiS);
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        int y = S(8);

        void Row(Control c, int h, int indent = 0) { c.Location = new Point(S(4 + indent), y); p.Controls.Add(c); y += h; }

        // ── Restore Original Layout hotkey ──
        // A global option, not a profile field: it undoes whichever profile is in force, so it belongs to
        // no single one of them.
        Row(ModulePanelKit.Header("Restore Original Layout", dpiS), S(30));

        Row(ModulePanelKit.Caption("A key that puts the desktop back the way it was before the first profile "
                                 + "was applied — the same thing the Tools menu entry does.", dpiS, 620), S(34), 18);

        var rkBox = new HotkeyCaptureBox(LiteBoxOptionsDb.GetGlobal(MonitorHotkeys.KeyRestore) ?? "")
        {
            Width = S(200), BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg,
            BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly,
        };
        Row(rkBox, S(30), 18);

        var rkGlobal = new CheckBox
        {
            Text = "Global hotkey (works even while a game is in front)",
            AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg,
            Checked = string.Equals(LiteBoxOptionsDb.GetGlobal(MonitorHotkeys.KeyRestoreGlobal), "true", StringComparison.OrdinalIgnoreCase),
            Enabled = !readOnly,
        };
        Row(rkGlobal, S(26), 18);

        Row(ModulePanelKit.Caption("Click the box and press a combination; Esc clears it. With \"global\", the "
                                 + "combination is taken from the whole system — no other application will "
                                 + "receive it again. This is the way OUT of a profile, so it is the one key "
                                 + "most worth having reachable from inside a game.", dpiS, 620), S(56), 18);

        var head = ModulePanelKit.Header("Web endpoints", dpiS);
        Row(head, S(40));

        bool webOn = LbModules.On(LbModule.Web);
        var box = new CheckBox
        {
            Text = "Activate web endpoints for Monitor Profiles",
            AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg,
            Enabled = webOn && !readOnly,
            Checked = string.Equals(LiteBoxOptionsDb.GetGlobal(MonitorsApi.OptionKey), "true", StringComparison.OrdinalIgnoreCase),
        };
        Row(box, S(26));

        Row(ModulePanelKit.Caption(
            "Adds three URLs to the embedded web server so a profile can be applied by calling a link — a "
            + "Stream Deck button, a phone bookmark, a home-automation scene when the projector comes on.", dpiS, 620), S(46), 18);

        int port = 8080;
        try { port = int.TryParse(LiteBoxConfig.LoadForExe().GetSec("Web", "Port"), out var pv) ? pv : 8080; } catch { }
        var urls = ModulePanelKit.Caption(
            $"http://<this-pc>:{port}/api/monitors                     what is available\n"
            + $"http://<this-pc>:{port}/api/monitors/apply?name=NAME     apply it\n"
            + $"http://<this-pc>:{port}/api/monitors/restore            back to the saved original", dpiS, 620);
        urls.Font = new Font("Consolas", 8.5f);
        Row(urls, S(56), 18);

        Row(ModulePanelKit.Caption(
            "Only profiles ticked \"Show in the Tools menu\" are listed or reachable — that box is also what "
            + "keeps one out of reach from the network. The server has no authentication, so treat these "
            + "like every other page it serves.", dpiS, 620), S(50), 18);

        if (!webOn)
        {
            var warn = ModulePanelKit.Caption("The Web module is off, so there is no server to add them to. "
                                            + "Enable it in the Modules tab first.", dpiS, 620);
            warn.ForeColor = LiteBoxTheme.Danger;
            Row(warn, S(34), 18);
        }

        return (p, () =>
        {
            if (readOnly) return;
            try { LiteBoxOptionsDb.SetGlobal(MonitorsApi.OptionKey, box.Checked ? "true" : null); } catch { }
            try
            {
                string hk = (rkBox.HotkeyValue ?? "").Trim();
                LiteBoxOptionsDb.SetGlobal(MonitorHotkeys.KeyRestore, hk.Length > 0 ? hk : null);
                LiteBoxOptionsDb.SetGlobal(MonitorHotkeys.KeyRestoreGlobal, rkGlobal.Checked ? "true" : null);
                // Re-register right away: a key the user just claimed should work without a restart, and
                // one they just released should stop being taken from the rest of the system.
                MonitorGlobalHotkeys.Refresh();
            }
            catch { }
        });
    }

    /// <summary>Assignments — every emulator, game and version that names a monitor profile, in one list
    /// you can prune.
    ///
    /// It exists because an assignment is invisible from anywhere else: it lives on the entity, and the
    /// only way to find one is to open that entity's editor and look. After a few months of "let me try
    /// this on that emulator", nobody remembers where they are. Here they are all listed — including the
    /// ones pointing at a profile that no longer exists, which is precisely the kind worth finding.
    ///
    /// Deletion is immediate and needs no OK: it removes an assignment, never an emulator or a game.</summary>
    private static Control BuildAssignments(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg, Padding = new Padding(S(12), S(10), S(12), S(8)) };

        var top = new Panel { Dock = DockStyle.Top, Height = S(70), BackColor = ModulePanelKit.Bg };
        var cap = ModulePanelKit.Caption("Everything that currently names a monitor profile. Removing an entry "
                                       + "only drops the assignment — the emulator, game or version is untouched.", dpiS, 640);
        cap.Location = new Point(0, 0);
        top.Controls.Add(cap);

        var kind = ModulePanelKit.Combo(dpiS, false, 220);
        kind.Items.AddRange(new object[] { "Emulators", "Games", "Versions" });
        kind.SelectedIndex = 0;
        kind.Location = new Point(0, S(38));
        top.Controls.Add(kind);

        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true,
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false,
        };
        list.Columns.Add("Name", S(340));
        list.Columns.Add("Monitor profile", S(260));

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ModulePanelKit.Bg,
            Padding = new Padding(0, S(8), 0, 0),
        };
        Button Btn(string t)
        {
            var b = new Button
            {
                Text = t, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(S(150), S(26)), Padding = new Padding(S(8), S(3), S(8), S(3)),
                Margin = new Padding(0, 0, S(8), 0), BackColor = ModulePanelKit.Field,
                ForeColor = ModulePanelKit.Fg, FlatStyle = FlatStyle.Flat, Enabled = !readOnly,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            return b;
        }
        var del = Btn("Remove selected");
        var delAll = Btn("Remove all in this list");
        bottom.Controls.Add(del);
        bottom.Controls.Add(delAll);

        string Scope() => kind.SelectedIndex switch
        {
            1 => LiteBoxOption.ScopeGame,
            2 => LiteBoxOption.ScopeVersion,
            _ => LiteBoxOption.ScopeEmulator,
        };

        var rows = new List<MonitorAssign.Row>();
        void Reload()
        {
            rows.Clear();
            rows.AddRange(MonitorAssign.All(Scope(), NameResolver(Scope())));
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rows) list.Items.Add(new ListViewItem(new[] { r.EntityName, r.What }));
            list.EndUpdate();
            del.Enabled = delAll.Enabled = !readOnly && rows.Count > 0;
        }

        kind.SelectedIndexChanged += (_, _) => Reload();
        del.Click += (_, _) =>
        {
            var picked = list.SelectedIndices.Cast<int>().Where(i => i >= 0 && i < rows.Count).Select(i => rows[i]).ToList();
            if (picked.Count == 0) return;
            foreach (var r in picked) MonitorAssign.Clear(r.Scope, r.EntityId);
            Reload();
        };
        delAll.Click += (_, _) =>
        {
            if (rows.Count == 0) return;
            if (MessageBox.Show(root, $"Remove all {rows.Count} assignment(s) in this list?", "Monitor Profiles",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var r in rows.ToList()) MonitorAssign.Clear(r.Scope, r.EntityId);
            Reload();
        };

        root.Controls.Add(list);
        root.Controls.Add(bottom);
        root.Controls.Add(top);
        list.BringToFront();
        Reload();
        return root;
    }

    /// <summary>Entity id to a display name. An id that resolves to nothing is left alone, so a dangling
    /// assignment still shows up as one — that is the kind this page exists to catch.</summary>
    private static Func<string, string?> NameResolver(string scope)
    {
        if (scope == LiteBoxOption.ScopeEmulator)
            return id =>
            {
                try
                {
                    var all = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllEmulators();
                    if (all != null)
                        foreach (var e in all)
                            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e.Title;
                }
                catch { }
                return null;
            };

        return id =>
        {
            try
            {
                var games = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllGames();
                if (games == null) return null;
                foreach (var g in games)
                {
                    if (scope == LiteBoxOption.ScopeGame)
                    {
                        if (string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) return g.Title;
                        continue;
                    }
                    var apps = g.GetAllAdditionalApplications();
                    if (apps == null) continue;
                    foreach (var a in apps)
                        if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) return g.Title + " — " + a.Name;
                }
            }
            catch { }
            return null;
        };
    }

    // ── the profiles editor ──────────────────────────────────────────────────

    private sealed class ProfilesEditor
    {
        private readonly float _dpi;
        private readonly bool _readOnly;
        private readonly List<MonitorProfile> _profiles;
        private readonly ListBox _list = new();
        private readonly CheckBox _public = new() { Text = "Show in the Tools menu", AutoSize = true };
        private readonly HotkeyCaptureBox _hotkey = new("");
        private readonly Label _hotkeyWarn = new();
        private readonly CheckBox _hotkeyGlobal = new() { Text = "Global hotkey (works even while a game is in front)", AutoSize = true };

        private readonly CheckBox _useLayout = new() { Text = "Monitor layout", AutoSize = true };
        private readonly Label _layoutInfo = new() { AutoSize = true };
        private readonly Button _capture = new();
        private readonly CheckBox _adapt = new() { Text = "Adapt to the monitors actually connected", AutoSize = true };

        private readonly CheckBox _usePreset = new() { Text = "Display mode", AutoSize = true };
        private readonly ComboBox _presetMonitor;
        private readonly ComboBox _presetRes;
        private readonly ComboBox _presetFreq;
        private readonly ComboBox _presetHdr;
        private readonly CheckBox _adjust = new() { Text = "Adjust to the closest supported value", AutoSize = true };
        private readonly ComboBox _presetRot;
        private readonly ComboBox _presetScale;
        private readonly ComboBox _presetZoom;
        private readonly ComboBox _extras;
        private readonly ComboBox _gpuFormat;
        private readonly ComboBox _gpuDepth;
        private readonly ComboBox _gpuRange;
        private readonly ComboBox _gpuVrr;
        private readonly ComboBox _gpuScaleMode;
        private readonly ComboBox _gpuScaleDev;
        private readonly CheckBox _gpuVibOn = new() { Text = "Set digital vibrance", AutoSize = true };
        private readonly NumericUpDown _gpuVib = new() { Minimum = 0, Maximum = 100, Increment = 1 };
        private readonly CheckBox _strict = new() { Text = "Match monitors strictly by port (no EDID fallback)", AutoSize = true };
        private readonly CheckBox _layoutDetails = new() { Text = "Details", AutoSize = true };
        private readonly CheckedListBox _disableList = new();
        private readonly CheckBox _useDisable = new() { Text = "Turn off specific monitors", AutoSize = true };
        private readonly CheckBox _makePrimary = new() { Text = "Make this monitor the main one", AutoSize = true };

        private readonly CheckBox _useAudio = new() { Text = "Sound card / volume", AutoSize = true };
        private readonly ComboBox _audioDevice;
        private readonly CheckBox _useVolume = new() { Text = "Set volume", AutoSize = true };
        private readonly NumericUpDown _volume = new() { Minimum = 0, Maximum = 100, Increment = 5 };

        private readonly CheckBox _solo = new() { Text = "Turn off every monitor except the primary one", AutoSize = true };

        private readonly Label _restoreInfo = new();
        private readonly Label _hdrHint = new();
        private Button _forget = null!;
        private List<MonitorInfo> _monitors;
        /// <summary>What each entry of the monitor combo MEANS, parallel to its items. Index 0 is "the
        /// main monitor" (empty identity). The list is rebuilt per profile because it may have to carry
        /// a screen that is not plugged in right now — see BuildMonitorChoices.</summary>
        private readonly List<MonitorChoice> _monitorChoices = new();
        private bool _loading;

        /// <summary>One entry of the monitor picker: the identity that gets SAVED, and a label that is
        /// purely informative. The display number and "disconnected" are never persisted — they describe
        /// the machine right now, and a profile must not be pinned to either.</summary>
        private sealed record MonitorChoice(string DevicePath, string FriendlyName, string Edid, int EdidProduct, string Label);
        private bool _reloading;
        private bool _loadingDisable;

        public Control Root { get; }

        private int S(int px) => ModulePanelKit.Sc(_dpi, px);

        public ProfilesEditor(float dpiS, bool readOnly)
        {
            _dpi = dpiS; _readOnly = readOnly;
            _profiles = MonitorProfileStore.All();
            _monitors = DisplayTargets.Enumerate();

            _presetMonitor = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _presetRes = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _presetFreq = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _presetHdr = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _presetRot = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _presetScale = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _presetZoom = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _extras = ModulePanelKit.Combo(dpiS, readOnly, 300);
            _gpuFormat = ModulePanelKit.Combo(dpiS, readOnly, 240);
            _gpuDepth = ModulePanelKit.Combo(dpiS, readOnly, 240);
            _gpuRange = ModulePanelKit.Combo(dpiS, readOnly, 240);
            _gpuVrr = ModulePanelKit.Combo(dpiS, readOnly, 240);
            _gpuScaleMode = ModulePanelKit.Combo(dpiS, readOnly, 240);
            _gpuScaleDev = ModulePanelKit.Combo(dpiS, readOnly, 240);
            _audioDevice = ModulePanelKit.Combo(dpiS, readOnly, 300);

            Root = BuildRoot();
            ReloadList();
            if (_list.Items.Count > 0) _list.SelectedIndex = 0; else Bind(null);
            ThemedCheckBox.StyleAll(Root);
        }

        public void Apply()
        {
            MonitorProfileStore.Save(_profiles);
            // The system registrations follow the saved list, not the edit buffer — a hotkey the user
            // cancelled out of must not stay claimed from the whole OS.
            MonitorGlobalHotkeys.Refresh();
        }

        private MonitorProfile? Current => _list.SelectedIndex >= 0 && _list.SelectedIndex < _profiles.Count
            ? _profiles[_list.SelectedIndex] : null;

        // ── layout ───────────────────────────────────────────────────────────

        private Control BuildRoot()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = ModulePanelKit.Bg, Padding = new Padding(S(12), S(10), S(8), S(8)),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(210)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            grid.Controls.Add(BuildLeft(), 0, 0);
            grid.Controls.Add(BuildRight(), 1, 0);
            return grid;
        }

        private Control BuildLeft()
        {
            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = ModulePanelKit.Bg,
                Margin = new Padding(0, 0, S(12), 0),
            };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _list.Dock = DockStyle.Fill;
            _list.BackColor = ModulePanelKit.Field;
            _list.ForeColor = ModulePanelKit.Fg;
            _list.BorderStyle = BorderStyle.None;
            _list.IntegralHeight = false;
            // Guarded: rewriting an item's text can bounce SelectedIndexChanged, and re-binding from the
            // model mid-edit is what used to un-tick a checkbox the instant it was ticked.
            _list.SelectedIndexChanged += (_, _) => { if (!_reloading) Bind(Current); };
            left.Controls.Add(_list, 0, 0);

            var btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ModulePanelKit.Bg,
                Padding = new Padding(0, S(6), 0, 0),
            };
            btns.Controls.Add(Btn("Add", () => AddProfile()));
            btns.Controls.Add(Btn("Duplicate", DuplicateProfile));
            btns.Controls.Add(Btn("Rename", RenameProfile));
            btns.Controls.Add(Btn("Delete", DeleteProfile));
            left.Controls.Add(btns, 0, 1);

            // Apply Now sits with the list, not in a footer: it acts on the SELECTED profile, and the
            // options window's own OK/Apply already owns the bottom of the screen.
            var applyRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ModulePanelKit.Bg,
                Padding = new Padding(0, S(8), 0, 0),
            };
            applyRow.Controls.Add(Btn("Apply Now", ApplyNow, wide: true));
            left.Controls.Add(applyRow, 0, 2);

            // The restore point, spelled out. It is taken once, before the first profile, and survives
            // restarts — so it can hold a desktop the user no longer thinks of as "original" without
            // anything on screen ever saying so.
            _restoreInfo.AutoSize = true;
            _restoreInfo.MaximumSize = new Size(S(200), 0);
            _restoreInfo.ForeColor = ModulePanelKit.Sub;
            _restoreInfo.Margin = new Padding(0, S(8), 0, S(4));
            applyRow.Controls.Add(_restoreInfo);
            _forget = Btn("Forget Saved Original", ForgetRestorePoint, wide: true);
            applyRow.Controls.Add(_forget);
            RefreshRestoreInfo();
            return left;
        }

        private Button Btn(string text, Action onClick, bool wide = false)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(S(wide ? 190 : 92), S(26)),
                Padding = new Padding(S(8), S(3), S(8), S(3)), Margin = new Padding(0, 0, S(6), S(6)),
                BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg,
                FlatStyle = FlatStyle.Flat, Enabled = !_readOnly,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            b.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Warn(ex.Message); } };
            return b;
        }

        /// <summary>The four sections in a vertical flow. Flow, not absolute positions: the captured
        /// layout block grows with the monitor count — a hand-computed y clipped the third screen on a
        /// 3-monitor desk, which is precisely the bug this shape prevents.</summary>
        private Control BuildRight()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = ModulePanelKit.Bg, Padding = new Padding(0, 0, S(6), S(8)),
            };
            void Add(Control c, int indent = 0, int gapAbove = 0)
            {
                c.Margin = new Padding(S(indent), S(gapAbove), 0, S(4));
                flow.Controls.Add(c);
            }
            Label Cap(string t) => ModulePanelKit.Caption(t, _dpi, maxWidth: 480);

            // Per-profile, above the parts: they describe how the profile is REACHED, not what it does.
            _public.CheckedChanged += (_, _) => Commit();
            Add(_public);
            Add(Cap("Unticking hides it from the menu and from the web endpoints. Its hotkey still works."), 18);
            Add(Cap("Hotkey"), 0, gapAbove: 6);
            _hotkey.Width = S(200);
            _hotkey.BackColor = ModulePanelKit.Field; _hotkey.ForeColor = ModulePanelKit.Fg;
            _hotkey.BorderStyle = BorderStyle.FixedSingle;
            _hotkey.ValueChanged += (_, _) => Commit();
            Add(_hotkey);
            _hotkeyWarn.AutoSize = true; _hotkeyWarn.MaximumSize = new Size(S(470), 0);
            _hotkeyWarn.ForeColor = LiteBoxTheme.Danger; _hotkeyWarn.Visible = false;
            Add(_hotkeyWarn);
            _hotkeyGlobal.CheckedChanged += (_, _) => Commit();
            Add(_hotkeyGlobal, 18, gapAbove: 2);
            Add(Cap("Click the box and press a combination; Esc clears it. Without \"global\", the key only "
                  + "works while LiteBox has focus. WITH it, the combination is taken from the whole system — "
                  + "no other application, game included, will ever receive it again."), 18);

            _useLayout.CheckedChanged += (_, _) => { Sync(); Commit(); };
            Add(_useLayout, 0, gapAbove: 12);
            Add(Cap("Position, resolution, refresh rate, rotation, zoom and HDR state of every monitor — captured as one set."), 18);
            _layoutInfo.Font = new Font("Consolas", 9f);        // columns line up; reads as data, not prose
            _layoutInfo.ForeColor = ModulePanelKit.Sub;
            Add(_layoutInfo, 18);
            _capture.Text = "Capture Current Layout";
            _capture.AutoSize = true; _capture.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _capture.Padding = new Padding(S(10), S(4), S(10), S(4));
            _capture.BackColor = ModulePanelKit.Field; _capture.ForeColor = ModulePanelKit.Fg;
            _capture.FlatStyle = FlatStyle.Flat;
            _capture.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 68);
            _capture.Click += (_, _) => { try { CaptureLayout(); } catch (Exception ex) { Warn(ex.Message); } };
            Add(_capture, 18);
            // "HDR —" means the profile has no opinion, so it leaves HDR alone. Said out loud, because a
            // layout captured before HDR was recorded looks complete and silently isn't.
            _hdrHint.Text = "\"HDR —\" means this profile was captured before HDR was recorded, so it leaves "
                          + "HDR untouched. Capture again to control it.";
            _hdrHint.AutoSize = true; _hdrHint.MaximumSize = new Size(S(470), 0);
            _hdrHint.ForeColor = ModulePanelKit.Sub; _hdrHint.Visible = false;
            Add(_hdrHint, 18);
            _layoutDetails.CheckedChanged += (_, _) => { _layoutInfo.Text = Describe(Current?.Layout, _layoutDetails.Checked); };
            Add(_layoutDetails, 18, gapAbove: 2);
            Add(Cap("Ticked: one block per monitor with its GPU, connector, identity and signal — what to "
                  + "look at when two identical screens or a wrong cable are suspected."), 36);

            Add(Cap("Monitors not in this profile"), 18, gapAbove: 4);
            _extras.Items.AddRange(new object[]
            {
                "Turn them off",
                "Leave them where they are",
                "Place them right of the profile",
                "Place them left of the profile",
                "Place them above the profile",
                "Place them below the profile",
            });
            _extras.SelectedIndexChanged += (_, _) => Commit();
            Add(_extras, 18);
            Add(Cap("What happens to a CONNECTED screen the layout does not mention. A separate question "
                  + "from the checkbox below, which is about a named screen being absent."), 36, 18);

            _strict.CheckedChanged += (_, _) => Commit();
            Add(_strict, 18, gapAbove: 4);
            Add(Cap("Off (default): a monitor whose port changed (new GPU, moved cable) is found again by "
                  + "its EDID identity. On: the exact port only — for a panel wired to TWO graphics cards "
                  + "at once, where one EDID covers two connections and the fallback could pick the wrong one."), 50, 18);

            _adapt.CheckedChanged += (_, _) => Commit();
            Add(_adapt, 18, gapAbove: 4);
            Add(Cap("Unplugged monitors are skipped instead of blocking the switch, and monitors that are "
                  + "connected but absent from the profile are left where they are instead of being turned off. "
                  + "Off: the profile refuses to apply unless every monitor matches."), 36);

            _usePreset.CheckedChanged += (_, _) => { Sync(); Commit(); };
            Add(_usePreset, 0, gapAbove: 10);
            Add(Cap("Changes ONE monitor's mode and/or its HDR state. The desktop arrangement is left untouched. "
                  + "Resolution may stay \"(leave unchanged)\" to force HDR or SDR on its own."), 18);
            Add(Cap("Monitor"), 18);
            _presetMonitor.SelectedIndexChanged += (_, _) => { if (!_loading) { FillResolutions(); FillFrequencies(); Commit(); } };
            Add(_presetMonitor, 18);
            _makePrimary.CheckedChanged += (_, _) => Commit();
            Add(_makePrimary, 18, gapAbove: 2);
            Add(Cap("Moves the desktop origin onto it; every other screen shifts to match. "
                  + "Greyed out for \"Main monitor\", which is primary by definition."), 36);
            Add(Cap("Resolution"), 18);
            _presetRes.SelectedIndexChanged += (_, _) => { if (!_loading) { FillFrequencies(); Commit(); } };
            Add(_presetRes, 18);
            Add(Cap("Refresh rate"), 18);
            _presetFreq.SelectedIndexChanged += (_, _) => Commit();
            Add(_presetFreq, 18);
            Add(Cap("HDR"), 18);
            _presetHdr.SelectedIndexChanged += (_, _) => Commit();
            Add(_presetHdr, 18);
            Add(Cap("Rotation"), 18);
            _presetRot.Items.AddRange(new object[] { "(leave unchanged)", "Landscape", "Portrait (90°)", "Landscape flipped (180°)", "Portrait flipped (270°)" });
            _presetRot.SelectedIndex = 0;
            _presetRot.SelectedIndexChanged += (_, _) => Commit();
            Add(_presetRot, 18);
            Add(Cap("Scaling below native (4:3 content on a 16:9 panel)"), 18);
            _presetScale.Items.AddRange(new object[] { "(leave unchanged)", "Driver default", "Stretch to fill", "Center (no stretch)" });
            _presetScale.SelectedIndex = 0;
            _presetScale.SelectedIndexChanged += (_, _) => Commit();
            Add(_presetScale, 18);
            Add(Cap("Windows zoom"), 18);
            _presetZoom.Items.Add("(leave unchanged)");
            foreach (var z in new[] { 100, 125, 150, 175, 200, 225, 250, 300 }) _presetZoom.Items.Add(z + "%");
            _presetZoom.SelectedIndex = 0;
            _presetZoom.SelectedIndexChanged += (_, _) => Commit();
            Add(_presetZoom, 18);

            Add(BuildGpuGroup(), 18, gapAbove: 6);

            _adjust.CheckedChanged += (_, _) => Commit();
            Add(_adjust, 18, gapAbove: 4);
            Add(Cap("The lists above are generic, so a mode may not exist on the screen this profile targets — "
                  + "it may be unplugged, or its driver may offer different modes than it does today. "
                  + "On: fall back to the closest one it really has. Off: refuse to apply and say which."), 36);

            _useAudio.CheckedChanged += (_, _) => { Sync(); Commit(); };
            Add(_useAudio, 0, gapAbove: 10);
            Add(Cap("Playback device"), 18);
            _audioDevice.SelectedIndexChanged += (_, _) => Commit();
            Add(_audioDevice, 18);
            _useVolume.CheckedChanged += (_, _) => { Sync(); Commit(); };
            Add(_useVolume, 18);
            _volume.ValueChanged += (_, _) => Commit();
            _volume.Width = S(80);
            _volume.BackColor = ModulePanelKit.Field; _volume.ForeColor = ModulePanelKit.Fg;
            _volume.BorderStyle = BorderStyle.None;
            Add(_volume, 36);

            _solo.CheckedChanged += (_, _) => Commit();
            Add(_solo, 0, gapAbove: 10);

            _useDisable.CheckedChanged += (_, _) => { Sync(); Commit(); };
            Add(_useDisable, 0, gapAbove: 6);
            Add(Cap("The precise form of the switch above: tick exactly the screens to switch off. The "
                  + "primary is never turned off this way."), 36, 18);
            _disableList.BackColor = ModulePanelKit.Field;
            _disableList.ForeColor = ModulePanelKit.Fg;
            _disableList.BorderStyle = BorderStyle.None;
            _disableList.CheckOnClick = true;
            _disableList.Width = S(320);
            _disableList.Height = S(70);
            // ItemCheck fires BEFORE the check state changes; committing after the message settles reads
            // the new state. The editor is not a Control, so the deferral goes through the list itself.
            _disableList.ItemCheck += (_, _) => { if (!_loading && !_loadingDisable) _disableList.BeginInvoke(new Action(Commit)); };
            Add(_disableList, 18);

            _presetHdr.Items.AddRange(new object[] { "Leave unchanged", "Force HDR on", "Force HDR off (SDR)" });

            _audioDevice.Items.Add("(leave unchanged)");
            foreach (var d in AudioEndpoints.Playback()) _audioDevice.Items.Add(d);

            BuildMonitorChoices(null);

            return flow;
        }

        /// <summary>The NVIDIA-only block of Display mode, framed in the vendor's dark green so its scope
        /// is visible at a glance: everything inside acts through the DRIVER, exists on no Windows API,
        /// and on a monitor driven by another GPU is skipped with the vendor named in the result.</summary>
        private Panel BuildGpuGroup()
        {
            var nvGreen = Color.FromArgb(24, 46, 24);       // dark NVIDIA-ish green, quiet on the dark canvas
            var nvBorder = Color.FromArgb(58, 110, 58);

            var box = new Panel
            {
                BackColor = nvGreen,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(10), S(8), S(10), S(8)),
                BorderStyle = BorderStyle.FixedSingle,
            };
            box.Paint += (_, e) =>
            {
                using var pen = new Pen(nvBorder);
                e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
            };

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = nvGreen,
            };
            void G(Control c, int indent = 0)
            {
                c.Margin = new Padding(S(indent), S(2), 0, S(2));
                if (c is Label l) l.BackColor = nvGreen;
                if (c is CheckBox cb) cb.BackColor = nvGreen;
                flow.Controls.Add(c);
            }
            Label GCap(string t)
            {
                var l = ModulePanelKit.Caption(t, _dpi, maxWidth: 440);
                l.BackColor = nvGreen;
                return l;
            }

            var head = new Label
            {
                Text = "NVIDIA output", AutoSize = true, BackColor = nvGreen,
                ForeColor = Color.FromArgb(140, 210, 140),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            };
            G(head);
            G(GCap("Driver-level settings — they exist on no Windows API. Applied only when this monitor "
                 + "is driven by an NVIDIA card; on any other GPU the profile skips them and says so."));

            G(GCap("Output color format"));
            _gpuFormat.Items.AddRange(new object[] { "(leave unchanged)", "RGB", "YCbCr444", "YCbCr422", "YCbCr420" });
            _gpuFormat.SelectedIndex = 0;
            _gpuFormat.SelectedIndexChanged += (_, _) => Commit();
            G(_gpuFormat, 12);

            G(GCap("Output color depth"));
            _gpuDepth.Items.AddRange(new object[] { "(leave unchanged)", "8 bpc", "10 bpc", "12 bpc" });
            _gpuDepth.SelectedIndex = 0;
            _gpuDepth.SelectedIndexChanged += (_, _) => Commit();
            G(_gpuDepth, 12);

            G(GCap("Output dynamic range"));
            _gpuRange.Items.AddRange(new object[] { "(leave unchanged)", "Full", "Limited" });
            _gpuRange.SelectedIndex = 0;
            _gpuRange.SelectedIndexChanged += (_, _) => Commit();
            G(_gpuRange, 12);

            G(GCap("Scaling: how below-native content fills the panel, and who does the work — the same "
                 + "Mode + Scaling Device pair as the NVIDIA app's per-display Scaling panel."));
            _gpuScaleMode.Items.AddRange(new object[] { "(leave unchanged)", "Aspect ratio", "Full-screen (stretch)", "No scaling (centered)", "Integer scaling" });
            _gpuScaleMode.SelectedIndex = 0;
            _gpuScaleMode.SelectedIndexChanged += (_, _) => { Sync(); Commit(); };
            G(_gpuScaleMode, 12);
            G(GCap("Scaling device"));
            _gpuScaleDev.Items.AddRange(new object[] { "Display", "GPU" });
            _gpuScaleDev.SelectedIndex = 0;
            _gpuScaleDev.SelectedIndexChanged += (_, _) => Commit();
            G(_gpuScaleDev, 12);

            G(GCap("G-Sync / VRR — DRIVER-WIDE, one value for the whole machine, not just this monitor. "
                 + "Put back by the game-exit and restore snapshots like everything else."));
            _gpuVrr.Items.AddRange(new object[] { "(leave unchanged)", "Off", "Fullscreen only", "Fullscreen and windowed" });
            _gpuVrr.SelectedIndex = 0;
            _gpuVrr.SelectedIndexChanged += (_, _) => Commit();
            G(_gpuVrr, 12);

            _gpuVibOn.CheckedChanged += (_, _) => { _gpuVib.Enabled = _gpuVibOn.Checked && _gpuVibOn.Enabled; Commit(); };
            G(_gpuVibOn);
            _gpuVib.BackColor = ModulePanelKit.Field; _gpuVib.ForeColor = ModulePanelKit.Fg;
            _gpuVib.BorderStyle = BorderStyle.None; _gpuVib.Width = S(70);
            _gpuVib.ValueChanged += (_, _) => Commit();
            G(_gpuVib, 24);
            G(GCap("NVIDIA's own scale (typically 0–63, default 50). Higher = more saturated."));

            box.Controls.Add(flow);
            return box;
        }

        // ── list actions ─────────────────────────────────────────────────────

        /// <summary>An all-off profile is dropped on save; say so in the list rather than letting it
        /// vanish silently.</summary>
        private static string Label(MonitorProfile p) => p.Name + (p.IsEmpty ? "   — empty" : "");

        private void ReloadList()
        {
            int keep = _list.SelectedIndex;
            _reloading = true;
            try
            {
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var p in _profiles) _list.Items.Add(Label(p));
                _list.EndUpdate();
                if (keep >= 0 && keep < _list.Items.Count) _list.SelectedIndex = keep;
            }
            finally { _reloading = false; }
        }

        /// <summary>Refresh ONLY the selected row's text. Commit runs on every keystroke and every
        /// checkbox; rebuilding the whole list there re-entered Bind and made each click cost four full
        /// display enumerations.</summary>
        private void RefreshCurrentLabel()
        {
            int ix = _list.SelectedIndex;
            if (ix < 0 || ix >= _profiles.Count) return;
            string text = Label(_profiles[ix]);
            if (string.Equals(_list.Items[ix] as string, text, StringComparison.Ordinal)) return;
            _reloading = true;
            try { _list.Items[ix] = text; _list.SelectedIndex = ix; }
            finally { _reloading = false; }
        }

        private void AddProfile()
        {
            var name = Prompt("New profile", "Name:", "Profile " + (_profiles.Count + 1));
            if (name == null) return;
            _profiles.Add(new MonitorProfile { Name = name });
            ReloadList();
            _list.SelectedIndex = _profiles.Count - 1;
        }

        private void DuplicateProfile()
        {
            var cur = Current;
            if (cur == null) return;
            var name = Prompt("Duplicate profile", "Name:", cur.Name + " (copy)");
            if (name == null) return;
            var copy = System.Text.Json.JsonSerializer.Deserialize<MonitorProfile>(
                System.Text.Json.JsonSerializer.Serialize(cur))!;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = name;
            _profiles.Add(copy);
            ReloadList();
            _list.SelectedIndex = _profiles.Count - 1;
        }

        private void RenameProfile()
        {
            var cur = Current;
            if (cur == null) return;
            var name = Prompt("Rename profile", "Name:", cur.Name);
            if (name == null) return;
            cur.Name = name;
            ReloadList();
        }

        private void DeleteProfile()
        {
            var cur = Current;
            if (cur == null) return;
            if (MessageBox.Show(Root, $"Delete \"{cur.Name}\"?", "Monitor Profiles",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int ix = _list.SelectedIndex;
            _profiles.RemoveAt(ix);
            ReloadList();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(ix, _list.Items.Count - 1);
            else Bind(null);
        }

        private void RefreshRestoreInfo()
        {
            string held = MonitorProfileApply.RestoreSummary();
            _restoreInfo.Text = held.Length > 0
                ? "Saved original:\n" + held
                : "Saved original: none yet — the next profile applied records the desktop first.";
            if (_forget != null) _forget.Enabled = !_readOnly && held.Length > 0;
        }

        private void ForgetRestorePoint()
        {
            if (MessageBox.Show(Root,
                    "Forget the saved original desktop?\n\n"
                    + "Nothing changes on screen. The next profile you apply will record the desktop as it is "
                    + "at that moment, and that becomes what \"Restore Original Layout\" goes back to.",
                    "Monitor Profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            MonitorProfileApply.Forget();
            RefreshRestoreInfo();
        }

        private void ApplyNow()
        {
            var cur = Current;
            if (cur == null) return;
            if (cur.IsEmpty) { Warn("Nothing is enabled in this profile."); return; }
            // Save first: applying a profile that only exists in this window's buffer would leave a
            // restore point referring to something the store has never heard of.
            MonitorProfileStore.Save(_profiles);
            var res = MonitorProfileApply.Apply(cur);
            RefreshRestoreInfo();
            Report(cur.Name + " — " + res.Message.Replace("\n", "  ·  "), res.Ok);
        }

        private void CaptureLayout()
        {
            var cur = Current;
            if (cur == null) return;
            var layout = DisplayTargets.Capture();
            if (layout == null) { Warn("Could not read the current display configuration."); return; }
            cur.Layout = layout;
            _layoutInfo.Text = Describe(layout, _layoutDetails.Checked);
            DisplayTargets.Invalidate();          // a capture is the moment to re-read the hardware
            _monitors = DisplayTargets.Enumerate();
            RefreshCurrentLabel();
        }

        /// <summary>Outcomes go to the notification centre, never to a message box. Questions (delete a
        /// profile, forget the saved original) keep their dialog — those need an answer, not a report.</summary>
        private static void Report(string message, bool ok)
        {
            if (ok) LiteBox.Notifications.NotificationCenter.Info(message, lifeSpanSeconds: 6);
            else LiteBox.Notifications.NotificationCenter.Error(message);
        }

        private static void Warn(string msg) => Report(msg, ok: false);

        // ── binding ──────────────────────────────────────────────────────────

        /// <summary>The layout, two renderings from one switch.
        ///
        /// Dense (default): one aligned line per monitor — enough for "is this the arrangement I think".
        /// Details: a block per monitor adding the GPU, the physical connector, the EDID identity, the
        /// DevicePath and the captured output signal — the things one actually needs when two identical
        /// panels or a wrong cable are suspected. Both live in the same label: a second window for this
        /// would be ceremony.</summary>
        private static string Describe(MonitorLayout? layout, bool details = false)
        {
            if (layout == null || layout.Paths.Count == 0)
                return "No layout captured yet — press the button below.";

            // Same-model monitors share a name; show the port that tells them apart, twins only.
            var dupNames = layout.Paths.GroupBy(r => r.Label).Where(g => g.Count() > 1)
                                       .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Name(LayoutPath r) => dupNames.Contains(r.Label) && r.PortId.Length > 0
                                       ? $"{r.Label} ({r.PortId})" : r.Label;

            var tag = new Dictionary<int, string>();
            foreach (var g in layout.Paths.Where(r => r.SourceGroup >= 0)
                                          .GroupBy(r => r.SourceGroup)
                                          .Where(g => g.Count() > 1))
                tag[g.Key] = "dup " + (char)('A' + tag.Count);

            var ordered = layout.Paths
                .Select((r, i) => (Rec: r, Order: r.SourceGroup >= 0 ? r.SourceGroup * 1000 : 1_000_000 + i))
                .OrderBy(x => x.Order).Select(x => x.Rec).ToList();

            if (details)
            {
                var blocks = new List<string>();
                foreach (var r in ordered)
                {
                    var live = DisplayTargets.ResolveTarget(r);
                    string gpu = "", conn = "";
                    if (live != null)
                    {
                        try { gpu = live.Adapter?.ToDisplayAdapter()?.DeviceName ?? ""; } catch { }
                        conn = DisplayTargets.ConnectorText(live);
                    }
                    var b = new System.Text.StringBuilder();
                    b.AppendLine($"{Name(r)}{(r.Primary ? "   primary" : "")}{(tag.TryGetValue(r.SourceGroup, out var t0) ? "   " + t0 : "")}");
                    b.AppendLine($"    mode     {r.Width}x{r.Height} @ {r.RefreshText}   at {r.X},{r.Y}   zoom {r.ZoomText}"
                                 + (r.Rotation is not ("" or "Identity") ? "   " + r.Rotation : "")
                                 + (r.OutputScaling is not ("" or "Default") ? "   scaling " + r.OutputScaling : ""));
                    b.AppendLine($"    output   HDR {HdrControl.Text(r.Hdr)}"
                                 + (r.ColorEncoding.Length > 0 ? $"   {r.ColorEncoding} {r.BitsPerChannel}bpc" : "")
                                 + (r.HasSignal ? $"   signal {r.SignalActiveWidth}x{r.SignalActiveHeight}/{r.SignalTotalWidth}x{r.SignalTotalHeight}" : ""));
                    if (r.GpuFormat.Length > 0 || r.GpuVibrance >= 0)
                        b.AppendLine($"    gpu      {r.GpuFormat} {r.GpuDepthBpc}bpc {r.GpuDynamicRange}"
                                     + (r.GpuVibrance >= 0 ? $"   vibrance {r.GpuVibrance}" : ""));
                    b.AppendLine($"    identity EDID {r.EdidManufacture}/{r.EdidProduct}"
                                 + (conn.Length > 0 ? $"   {conn}" : "")
                                 + (gpu.Length > 0 ? $"   {gpu}" : (live == null ? "   (not connected now)" : "")));
                    b.Append($"    path     {r.DevicePath}");
                    blocks.Add(b.ToString());
                }
                return string.Join(Environment.NewLine + Environment.NewLine, blocks);
            }

            int w = Math.Min(22, layout.Paths.Max(r => Name(r).Length));
            var seenPrimary = new HashSet<int>();
            return string.Join(Environment.NewLine, ordered.Select(r =>
            {
                bool showPrimary = r.Primary && (r.SourceGroup < 0 || seenPrimary.Add(r.SourceGroup));
                return Name(r).PadRight(w)
                     + $"  {r.Width}x{r.Height}".PadRight(13)
                     + r.RefreshText.PadLeft(9)
                     + $"   at {r.X},{r.Y}".PadRight(16)
                     + "  zoom " + r.ZoomText.PadRight(5)
                     + (r.Hdr != null ? "  HDR " + (r.Hdr.Value ? "on " : "off") : "  HDR — ")
                     + (tag.TryGetValue(r.SourceGroup, out var t) ? "  " + t : "")
                     + (showPrimary ? "  primary" : "")
                     + (r.Rotation is not ("" or "Identity") ? "  " + r.Rotation : "");
            }));
        }

        private void Bind(MonitorProfile? p)
        {
            _loading = true;
            try
            {
                bool has = p != null;
                _useLayout.Checked = has && p!.Layout != null;
                _usePreset.Checked = has && p!.Preset != null;
                _useAudio.Checked = has && p!.Audio != null;
                _useVolume.Checked = has && p!.Audio?.Volume != null;
                _solo.Checked = has && p!.SoloPrimary;
                _adapt.Checked = has && p!.AdaptToConnected;
                _volume.Value = p?.Audio?.Volume is int v ? Math.Clamp(v, 0, 100) : 50;
                _hdrHint.Visible = p?.Layout != null && p.Layout.Paths.Any(r => r.Hdr == null);

                BuildMonitorChoices(p?.Preset);
                _presetMonitor.SelectedIndex = 0;
                if (p?.Preset != null && !string.IsNullOrEmpty(p.Preset.DevicePath))
                {
                    int ix = _monitorChoices.FindIndex(c => string.Equals(c.DevicePath, p.Preset.DevicePath, StringComparison.OrdinalIgnoreCase));
                    if (ix >= 0) _presetMonitor.SelectedIndex = ix;
                }
                FillResolutions();
                if (p?.Preset is { HasMode: true }) SelectText(_presetRes, $"{p.Preset.Width} x {p.Preset.Height}");
                // Always refill: the combo handlers are inert while _loading, so without this an
                // untouched profile showed a resolution and an EMPTY refresh dropdown.
                FillFrequencies();

                if (p?.Preset is { HasMode: true, Frequency: > 0 } pm) SelectText(_presetFreq, pm.Frequency + " Hz");
                _presetHdr.SelectedIndex = p?.Preset?.Hdr switch { true => 1, false => 2, _ => 0 };
                _adjust.Checked = p?.Preset?.AdjustToClosest ?? true;
                _presetRot.SelectedIndex = IndexIn(RotValues, p?.Preset?.Rotation ?? "");
                _presetScale.SelectedIndex = IndexIn(ScaleValues, p?.Preset?.OutputScaling ?? "");
                _strict.Checked = p?.StrictMatch ?? false;
                var pf = p?.Preset;
                _gpuFormat.SelectedIndex = pf?.GpuFormat switch { "RGB" => 1, "YCbCr444" => 2, "YCbCr422" => 3, "YCbCr420" => 4, _ => 0 };
                _gpuDepth.SelectedIndex = pf?.GpuDepthBpc switch { 8 => 1, 10 => 2, 12 => 3, _ => 0 };
                _gpuRange.SelectedIndex = pf?.GpuDynamicRange switch { "Full" => 1, "Limited" => 2, _ => 0 };
                _gpuVibOn.Checked = pf is { GpuVibrance: >= 0 };
                _gpuVrr.SelectedIndex = pf?.GpuVrr switch { "off" => 1, "fullscreen" => 2, "always" => 3, _ => 0 };
                var (smi, sdi) = GpuScalingIndices(pf?.GpuScaling ?? "");
                _gpuScaleMode.SelectedIndex = smi;
                _gpuScaleDev.SelectedIndex = sdi;
                _gpuVib.Value = pf is { GpuVibrance: >= 0 } ? Math.Min(100, pf.GpuVibrance) : 50;
                var zl = ZoomLabelOf(p?.Preset?.DpiScale ?? "");
                _presetZoom.SelectedIndex = 0;
                if (zl.Length > 0) SelectText(_presetZoom, zl);
                _extras.SelectedIndex = (p?.Layout != null ? p.EffectiveExtras : MonitorProfile.ExtrasKeep) switch
                {
                    MonitorProfile.ExtrasOff => 0,
                    MonitorProfile.ExtrasRight => 2,
                    MonitorProfile.ExtrasLeft => 3,
                    MonitorProfile.ExtrasTop => 4,
                    MonitorProfile.ExtrasBottom => 5,
                    _ => 1,
                };
                _useDisable.Checked = p is { DisableMonitors.Count: > 0 };
                _loadingDisable = true;
                try
                {
                    _disableList.Items.Clear();
                    foreach (var m in _monitors)
                    {
                        bool on = p?.DisableMonitors.Any(d => string.Equals(d, m.DevicePath, StringComparison.OrdinalIgnoreCase)) ?? false;
                        _disableList.Items.Add(m.FriendlyName + (m.Primary ? "  (primary)" : ""), on);
                    }
                }
                finally { _loadingDisable = false; }
                _layoutInfo.Text = Describe(p?.Layout, _layoutDetails.Checked);
                _public.Checked = p?.Public ?? true;
                _hotkey.HotkeyValue = p?.Hotkey ?? "";
                _hotkeyGlobal.Checked = p?.HotkeyGlobal ?? false;
                _makePrimary.Checked = p?.Preset?.MakePrimary ?? false;

                _audioDevice.SelectedIndex = 0;
                if (p?.Audio != null && !string.IsNullOrEmpty(p.Audio.Device)) SelectText(_audioDevice, p.Audio.Device);

                Sync();
            }
            finally { _loading = false; }
        }

        private void Sync()
        {
            bool has = Current != null && !_readOnly;
            foreach (var c in new Control[] { _useLayout, _usePreset, _useAudio, _solo, _public, _hotkey }) c.Enabled = has;
            _hotkeyGlobal.Enabled = has && (_hotkey.HotkeyValue ?? "").Length > 0;
            _capture.Enabled = has && _useLayout.Checked;
            _adapt.Enabled = has && (_useLayout.Checked || _usePreset.Checked);
            _presetMonitor.Enabled = _presetRes.Enabled = _presetHdr.Enabled = has && _usePreset.Checked;
            _presetRot.Enabled = _presetScale.Enabled = _presetZoom.Enabled = has && _usePreset.Checked;
            _gpuFormat.Enabled = _gpuDepth.Enabled = _gpuRange.Enabled = _gpuVibOn.Enabled = has && _usePreset.Checked;
            _gpuVrr.Enabled = has && _usePreset.Checked;
            _gpuScaleMode.Enabled = has && _usePreset.Checked;
            _gpuScaleDev.Enabled = has && _usePreset.Checked && _gpuScaleMode.SelectedIndex is > 0 and < 4;
            _gpuVib.Enabled = has && _usePreset.Checked && _gpuVibOn.Checked;
            _strict.Enabled = has;
            _extras.Enabled = _layoutDetails.Enabled = has && _useLayout.Checked;
            _useDisable.Enabled = has;
            _disableList.Enabled = has && _useDisable.Checked;
            _adjust.Enabled = has && _usePreset.Checked;
            _makePrimary.Enabled = has && _usePreset.Checked && _presetMonitor.SelectedIndex > 0;
            // No resolution chosen = nothing to pick a rate for.
            _presetFreq.Enabled = has && _usePreset.Checked && ParseRes(_presetRes.SelectedItem as string).W > 0;
            _audioDevice.Enabled = has && _useAudio.Checked;
            _useVolume.Enabled = has && _useAudio.Checked;
            _volume.Enabled = has && _useAudio.Checked && _useVolume.Checked;
        }

        /// <summary>Push the controls back into the selected profile. The profile object IS the edit
        /// buffer; the options window's Apply writes the whole list at once.</summary>
        private void Commit()
        {
            if (_loading) return;
            var p = Current;
            if (p == null) return;

            if (!_useLayout.Checked) p.Layout = null;

            if (_usePreset.Checked)
            {
                var (w, h) = ParseRes(_presetRes.SelectedItem as string);
                int hz = ParseHz(_presetFreq.SelectedItem as string);
                bool? hdr = _presetHdr.SelectedIndex switch { 1 => true, 2 => false, _ => (bool?)null };
                int mi = _presetMonitor.SelectedIndex;
                var mon = mi > 0 && mi < _monitorChoices.Count ? _monitorChoices[mi] : null;

                var preset = new MonitorPreset
                {
                    DevicePath = mon?.DevicePath ?? "",
                    FriendlyName = mon?.FriendlyName ?? "",
                    EdidManufacture = mon?.Edid ?? "",
                    EdidProduct = mon?.EdidProduct ?? 0,
                    Width = w > 0 ? w : 0,
                    Height = w > 0 ? h : 0,
                    Frequency = w > 0 ? hz : 0,     // 0 = "(leave unchanged)" — any rate the screen likes
                    Hdr = hdr,
                    AdjustToClosest = _adjust.Checked,
                    // Meaningless for "Main monitor" — it already is the main one.
                    MakePrimary = _makePrimary.Checked && mon != null,
                    Rotation = RotValues[Math.Max(0, _presetRot.SelectedIndex)],
                    OutputScaling = ScaleValues[Math.Max(0, _presetScale.SelectedIndex)],
                    DpiScale = ZoomValueOf(_presetZoom.SelectedItem as string),
                    GpuFormat = _gpuFormat.SelectedIndex switch { 1 => "RGB", 2 => "YCbCr444", 3 => "YCbCr422", 4 => "YCbCr420", _ => "" },
                    GpuDepthBpc = _gpuDepth.SelectedIndex switch { 1 => 8, 2 => 10, 3 => 12, _ => 0 },
                    GpuDynamicRange = _gpuRange.SelectedIndex switch { 1 => "Full", 2 => "Limited", _ => "" },
                    GpuVibrance = _gpuVibOn.Checked ? (int)_gpuVib.Value : -1,
                    GpuVrr = _gpuVrr.SelectedIndex switch { 1 => "off", 2 => "fullscreen", 3 => "always", _ => "" },
                    GpuScaling = GpuScalingValue(_gpuScaleMode.SelectedIndex, _gpuScaleDev.SelectedIndex),
                };
                // Section ticked but nothing chosen inside = nothing to apply; keep it null so the
                // profile does not claim a part it will not act on.
                p.Preset = preset.IsEmpty ? null : preset;
            }
            else p.Preset = null;

            if (_useAudio.Checked)
            {
                p.Audio = new AudioPreset
                {
                    Device = _audioDevice.SelectedIndex > 0 ? (_audioDevice.SelectedItem as string ?? "") : "",
                    Volume = _useVolume.Checked ? (int)_volume.Value : null,
                };
                if (string.IsNullOrEmpty(p.Audio.Device) && p.Audio.Volume == null) p.Audio = null;
            }
            else p.Audio = null;

            p.SoloPrimary = _solo.Checked;
            p.AdaptToConnected = _adapt.Checked;
            p.StrictMatch = _strict.Checked;
            p.ExtrasPolicy = _extras.SelectedIndex switch
            {
                0 => MonitorProfile.ExtrasOff,
                2 => MonitorProfile.ExtrasRight,
                3 => MonitorProfile.ExtrasLeft,
                4 => MonitorProfile.ExtrasTop,
                5 => MonitorProfile.ExtrasBottom,
                _ => MonitorProfile.ExtrasKeep,
            };
            p.DisableMonitors = _useDisable.Checked
                ? _monitors.Where((m, i) => i < _disableList.Items.Count && _disableList.GetItemChecked(i) && !m.Primary)
                           .Select(m => m.DevicePath).ToList()
                : new List<string>();
            p.Public = _public.Checked;
            p.Hotkey = _hotkey.HotkeyValue ?? "";
            p.HotkeyGlobal = _hotkeyGlobal.Checked;

            var clashes = MonitorHotkeys.Conflicts(_profiles);
            _hotkeyWarn.Text = clashes.Count > 0 ? "Hotkey conflict: " + string.Join("; ", clashes) : "";
            _hotkeyWarn.Visible = clashes.Count > 0;
            Sync();
            RefreshCurrentLabel();
        }

        // ── mode combos ──────────────────────────────────────────────────────

        /// <summary>Fill the monitor picker: "the main monitor", then every attached screen, then — when
        /// the profile names one that is NOT attached — that screen too.
        ///
        /// That last part is the point. A profile aimed at the TV must keep saying so while the TV is
        /// off: dropping it from the list would lose the selection, and the next edit would silently
        /// rewrite the profile to target something else. What is saved is the identity (DevicePath +
        /// EDID); the "(Display 2)" / "(disconnected)" suffix is a description of this moment, never
        /// stored, so a profile is not pinned to a port number that changes with the cabling.</summary>
        private void BuildMonitorChoices(MonitorPreset? preset)
        {
            _monitorChoices.Clear();
            _presetMonitor.Items.Clear();

            void AddChoice(MonitorChoice c) { _monitorChoices.Add(c); _presetMonitor.Items.Add(c.Label); }

            AddChoice(new MonitorChoice("", "", "", 0, "Main monitor (whichever is primary)"));

            foreach (var m in _monitors)
            {
                string where = m.DisplayName.Length > 0 ? DisplayNumber(m.DisplayName) : "connected";
                AddChoice(new MonitorChoice(m.DevicePath, m.FriendlyName, m.EdidManufacture, m.EdidProduct,
                                            $"{m.FriendlyName}  ({where}{(m.Primary ? ", primary" : "")})"));
            }

            // The profile's own monitor, when it is not among the attached ones.
            if (preset != null && !string.IsNullOrEmpty(preset.DevicePath)
                && !_monitors.Any(m => string.Equals(m.DevicePath, preset.DevicePath, StringComparison.OrdinalIgnoreCase)))
            {
                string name = preset.FriendlyName.Length > 0 ? preset.FriendlyName : "Saved monitor";
                AddChoice(new MonitorChoice(preset.DevicePath, preset.FriendlyName, preset.EdidManufacture,
                                            preset.EdidProduct, $"{name}  (disconnected)"));
            }
        }

        /// <summary>"\\.\DISPLAY2" → "Display 2". Informative only.</summary>
        private static string DisplayNumber(string deviceName)
        {
            var digits = new string(deviceName.Where(char.IsDigit).ToArray());
            return digits.Length > 0 ? "Display " + digits : deviceName;
        }

        /// <summary>True while the preset targets "the main monitor" — the screen this profile is about
        /// to MAKE primary, which is usually not the one that is primary right now. Its capabilities are
        /// therefore unknowable here, so the generic catalogue is offered instead of some other panel's
        /// mode list dressed up as validated fact.</summary>
        private bool MainMonitorTargeted => _presetMonitor.SelectedIndex <= 0;

        private void FillResolutions()
        {
            _presetRes.Items.Clear();
            _presetFreq.Items.Clear();

            // First entry = do not touch the mode at all. A preset may legitimately carry only an HDR
            // choice, and forcing a resolution just to reach that switch would change something the user
            // never asked to change.
            _presetRes.Items.Add(LeaveUnchanged);

            // ALWAYS the generic catalogue, named monitor or not. Offering a live mode list looks more
            // precise and is less honest: the screen may be unplugged, and even attached its list moves
            // with driver settings (custom resolutions, scaling done on the GPU, a different cable). A
            // profile is a wish; "Adjust to the closest supported value" decides what happens when the
            // hardware disagrees, at the moment it can actually be asked.
            foreach (var label in ModeCatalog.ResolutionLabels()) _presetRes.Items.Add(label);
            _presetRes.SelectedIndex = 0;
        }

        private const string LeaveUnchanged = "(leave unchanged)";

        private void FillFrequencies()
        {
            _presetFreq.Items.Clear();
            var (w, h) = ParseRes(_presetRes.SelectedItem as string);
            if (w <= 0) return;                     // "(leave unchanged)" — no rate to offer

            // "(leave unchanged)" here means: keep the rate the screen is already running at that
            // resolution. Choosing a resolution should not silently drag the refresh rate to a default.
            _presetFreq.Items.Add(LeaveUnchanged);

            foreach (var label in ModeCatalog.RefreshLabels()) _presetFreq.Items.Add(label);
            _presetFreq.SelectedIndex = 0;
        }

        /// <summary>The Display the preset combos describe — the picked monitor, or the current primary
        /// for "Main monitor". Null when it is not attached, so the combos stay empty rather than
        /// offering another panel's modes.</summary>
        private WindowsDisplayAPI.Display? PresetDisplay()
        {
            int ix = _presetMonitor.SelectedIndex;
            if (ix <= 0 || ix >= _monitorChoices.Count) return DisplayTargets.ResolveDisplay("");
            return DisplayTargets.ResolveDisplay(_monitorChoices[ix].DevicePath);
        }

        /// <summary>(mode 1..3, device 0..1) to the NVAPI scaling value. "...ToClosest" = the display
        /// receives the low mode and scales it; "...ToNative" = the GPU outputs native and scales.</summary>
        private static string GpuScalingValue(int mode, int device) => (mode, device) switch
        {
            (1, 0) => "ToAspectScanOutToClosest",
            (1, 1) => "ToAspectScanOutToNative",
            (2, 0) => "ToClosest",
            (2, 1) => "ToNative",
            (3, 0) => "GPUScanOutToClosest",
            (3, 1) => "GPUScanOutToNative",
            // Integer scaling is GPU-only by nature — the device combo is greyed on it, like the NVIDIA app.
            (4, _) => GpuColor.IntegerScalingName,
            _ => "",
        };

        private static (int Mode, int Device) GpuScalingIndices(string v) => v switch
        {
            "ToAspectScanOutToClosest" => (1, 0),
            "ToAspectScanOutToNative" => (1, 1),
            "ToClosest" => (2, 0),
            "ToNative" => (2, 1),
            "GPUScanOutToClosest" => (3, 0),
            "GPUScanOutToNative" => (3, 1),
            GpuColor.IntegerScalingName => (4, 1),
            _ => (0, 0),
        };

        private static readonly string[] RotValues = { "", "Identity", "Rotate90", "Rotate180", "Rotate270" };
        private static readonly string[] ScaleValues = { "", "Default", "Stretch", "Center" };

        private static int IndexIn(string[] values, string v)
        {
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        private static string ZoomValueOf(string? label)
            => string.IsNullOrEmpty(label) || label!.StartsWith("(") ? ""
             : label == "100%" ? "Identity" : "Scale" + label.TrimEnd('%') + "Percent";

        private static string ZoomLabelOf(string stored)
            => stored.Length == 0 ? "" : LayoutPath.ZoomPercent(stored);

        private static (int W, int H) ParseRes(string? s)
        {
            if (string.IsNullOrEmpty(s) || s == LeaveUnchanged) return (0, 0);
            var parts = s.Split('x');
            return parts.Length == 2 && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h)
                ? (w, h) : (0, 0);
        }

        private static int ParseHz(string? s)
            => string.IsNullOrEmpty(s) ? 0 : (int.TryParse(s.Replace("Hz", "").Trim(), out int v) ? v : 0);

        /// <summary>Select a value, ADDING it when the list does not have it. A profile may hold a mode
        /// the catalogue never listed — an exotic resolution, a rate from a custom timing — and silently
        /// falling back to the first entry would rewrite the profile on the next edit.</summary>
        private static void SelectText(ComboBox box, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < box.Items.Count; i++)
                if (string.Equals(box.Items[i] as string, text, StringComparison.OrdinalIgnoreCase)) { box.SelectedIndex = i; return; }
            box.Items.Insert(1, text);      // after "(leave unchanged)"
            box.SelectedIndex = 1;
        }

        // ── name prompt ──────────────────────────────────────────────────────

        private string? Prompt(string title, string label, string value)
        {
            using var dlg = new NamePrompt(title, label, value);
            return dlg.ShowDialog(Root) == DialogResult.OK ? dlg.Value : null;
        }

        private sealed class NamePrompt : LiteBoxForm
        {
            private readonly TextBox _text = new();
            public string Value => _text.Text.Trim();

            public NamePrompt(string title, string label, string value)
            {
                Text = title;
                ClientSize = new Size(S(360), S(132));
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false; MaximizeBox = false;

                var lab = new Label { Text = label, AutoSize = true, Location = new Point(S(14), S(16)), ForeColor = LiteBoxTheme.SubFg };
                _text.Text = value;
                _text.Location = new Point(S(14), S(40));
                _text.Width = S(330);
                _text.BackColor = LiteBoxTheme.Panel2; _text.ForeColor = LiteBoxTheme.Fg;
                _text.BorderStyle = BorderStyle.FixedSingle;

                var ok = ActionButton("OK", MenuIcons.Add);
                ok.Location = new Point(S(150), S(82));
                ok.Click += (_, _) => { if (Value.Length > 0) { DialogResult = DialogResult.OK; Close(); } };
                var cancel = ActionButton("Cancel", MenuIcons.Exit);
                cancel.Location = new Point(S(250), S(82));
                cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

                AcceptButton = ok; CancelButton = cancel;
                Controls.AddRange(new Control[] { lab, _text, ok, cancel });
            }
        }
    }
}
