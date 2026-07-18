// ROM module config panel — the ROM-extractor (ArchiveMGS) settings, at FULL parity with the ExtendDB
// plugin's "RomExtractor" tab. Clean-room native LiteBox implementation: original code, no plugin /
// reflection / Harmony, built entirely on the existing LiteBox backends (RomConfig for the model,
// ArchiveRamDisk for the RAM-disk capability probe, ArchiveCacheDb / ArchiveCacheIndex for cache usage,
// PluginHelper.DataManager for the platform / emulator enumeration).
//
// Layout mirrors the ExtendDB config:
//   • Header — cache folder + Browse, Max size (GB) + live usage, the cache-if size band (MIN / MAX MB),
//     a "System capabilities" card (RAM-disk readiness + one conditional action), and the content-action
//     buttons Clear archive data / Manage cache… / Reset to default.
//   • Left "Platform / Emulator" cascade column — 3-colour state combos (Default / Customized /
//     Untouched-inherits) with "+" buttons to add a per-platform or per-emulator profile, inheritance
//     checkboxes, and a legend. Selecting a row binds the right fiche.
//   • Right "fiche" — edits the resolved ArchivePriorityRow: Copy-settings-from + Copy, Operation mode,
//     the per-mode option strip (Extract companions / other ROMs / Flatten / Output name / Companion exts /
//     Copy exts), Sub-folder scheme + a live path example, then File rules (ROM / ignored extensions, the
//     weighted Tag-priority grid, RA bonus) OR the Conversions grid, plus Texture pack and M3U-input cards.
//
// Persistence: the fiche is flushed into its bound row, then the scalar globals go to LiteBox.ini [Rom] and
// GlobalDefault + Priorities to rom-profiles.json via RomConfig.Save(), followed by RomConfig.Invalidate().
//
// Inheritance is modelled by ROW PRESENCE, exactly like the resolution cascade
// (exact (platform, emulator) → (platform, "All") → GlobalDefault):
//   • "Use default settings" (platform) checked ⇔ NO (platform, *) row. Unchecking seeds a (platform,"All")
//     row from GlobalDefault.
//   • "Use platform settings" (emulator) checked ⇔ NO (platform, emulator) row. Unchecking seeds one from
//     (platform,"All"). While inheriting, the fiche shows the resolved parent read-only.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Rom;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Options;

internal static class RomPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        var ed = new Editor(dpiS, readOnly);
        return (ed.Root, ed.Apply);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  The whole editor. Kept as an instance so the cascade / inheritance logic
    //  reads like the ExtendDB panel (fields + methods) instead of a wall of
    //  captured closures.
    // ═══════════════════════════════════════════════════════════════════════
    private sealed class Editor
    {
        // ── DPI + palette ──────────────────────────────────────────────────
        private readonly float _s;
        private readonly bool _ro;
        private int Sc(int px) => (int)Math.Round(px * _s);

        private static Color Bg => ModulePanelKit.Bg;
        private static Color PanelBg => ModulePanelKit.Panel;
        private static Color Field => ModulePanelKit.Field;
        private static Color Fg => ModulePanelKit.Fg;
        private static Color Sub => ModulePanelKit.Sub;
        private static Color Accent => ModulePanelKit.Accent;
        private static readonly Color Good = Color.FromArgb(86, 186, 120);
        private static readonly Color Warn = Color.FromArgb(220, 170, 90);
        private static Color Danger => LiteBoxTheme.Danger;

        private static readonly Font Font9 = new("Segoe UI", 9f);
        private static readonly Font Font9B = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font Font85 = new("Segoe UI", 8.5f);
        private static readonly Font Font13B = new("Segoe UI Semibold", 13f);

        public readonly Panel Root;

        // ── Header controls ────────────────────────────────────────────────
        private TextBox _tbCachePath = null!;
        private NumericUpDown _numMaxGb = null!, _numMinMb = null!, _numMaxMb = null!;
        private Label _lblUsage = null!, _capDot = null!, _capLbl = null!;
        private Button _capBtn = null!;
        private long _cacheUsedBytes = -1;

        // ── Cascade column ─────────────────────────────────────────────────
        private RomStateCombo _cmbPlat = null!, _cmbEmu = null!;
        private Button _btnAddEmu = null!;
        private Label _lblEmu = null!;
        private CheckBox _chkPlatDefault = null!, _chkEmuDefault = null!;

        // ── Fiche controls ─────────────────────────────────────────────────
        private GroupBox _fiche = null!;
        private Panel _ficheInner = null!;
        private ComboBox _cmbCopyFrom = null!, _cmbMode = null!, _cmbSub = null!, _cmbOut = null!;
        private CheckBox _chkCompanions = null!, _chkOtherRoms = null!, _chkConvertAfter = null!, _chkFlatten = null!;
        private TextBox _tbCompExt = null!, _tbCopyExt = null!, _tbRomExt = null!, _tbIgnExt = null!, _tbTexExt = null!, _tbTexPath = null!;
        private Label _lblOutHint = null!, _lblCopyHint = null!, _lblConvHint = null!, _lblSub = null!, _lblSig = null!, _lblSubEx = null!, _lblSubTmp = null!, _lblRamMb = null!, _lblRamNa = null!;
        private Panel _modeOptions = null!;
        private GroupBox _grpRules = null!, _grpConvert = null!, _grpTexture = null!, _grpM3u = null!;
        private DataGridView _grid = null!, _convGrid = null!;
        private CheckBox _chkRam = null!, _chkTexture = null!, _chkM3u = null!;
        private NumericUpDown _numRam = null!, _numRaBonus = null!;

        // ── Working data (clones; written back on Apply) ───────────────────
        private ArchivePriorityRow _global = new() { Platform = "All", Emulator = "All" };
        private readonly List<ArchivePriorityRow> _profiles = new();
        private ArchivePriorityRow? _target;
        private bool _editable;
        private bool _loading;
        private bool _ramReady;

        public Editor(float dpiS, bool readOnly)
        {
            _s = dpiS; _ro = readOnly;
            Root = ModulePanelKit.Root(dpiS);
            Root.AutoScroll = true;

            var canvas = new Panel { Location = new Point(0, 0), Size = new Size(Sc(1240), Sc(660)), BackColor = Bg };
            Root.Controls.Add(canvas);

            BuildHeader(canvas);
            BuildCascade(canvas);
            BuildFiche(canvas);

            LoadFromConfig();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  HEADER
        // ═══════════════════════════════════════════════════════════════════
        private void BuildHeader(Panel host)
        {
            var h = new Panel { Location = new Point(Sc(4), Sc(2)), Size = new Size(Sc(1228), Sc(118)), BackColor = PanelBg };
            h.Paint += (s, e) => { using var pen = new Pen(Field); e.Graphics.DrawRectangle(pen, 0, 0, h.Width - 1, h.Height - 1); };

            var title = new Label { Text = "ROM extractor", AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Font = Font13B, Location = new Point(Sc(16), Sc(8)) };

            var lblCache = Lbl("Cache folder", 18, 46);
            _tbCachePath = Tb(112, 43, 340);
            var btnBrowse = Btn("Browse…", 460, 42, null);
            btnBrowse.Click += (s, e) =>
            {
                try
                {
                    using var d = new FolderBrowserDialog { Description = "Choose the ROM extractor cache folder" };
                    try { if (!string.IsNullOrWhiteSpace(_tbCachePath.Text) && Directory.Exists(_tbCachePath.Text)) d.SelectedPath = _tbCachePath.Text; } catch { }
                    if (d.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(d.SelectedPath)) _tbCachePath.Text = d.SelectedPath;
                }
                catch { }
            };

            var lblSize = Lbl("Max size", 560, 46);
            _numMaxGb = Num(50, 1, 100000, 624, 43, 66);
            var lblGb = Lbl("GB", 694, 46, dim: true);
            _lblUsage = Lbl("", 722, 47, dim: true); _lblUsage.Font = Font85; _lblUsage.AutoSize = true;
            _numMaxGb.ValueChanged += (s, e) => UpdateUsageLabel();

            var lblMin = Lbl("Cache if ≥", 18, 82);
            _numMinMb = Num(100, 0, 1000000, 92, 79, 66);
            var lblAnd = Lbl("MB  and  <", 164, 82);
            _numMaxMb = Num(8000, 0, 1000000, 240, 79, 72);
            var lblUnit = Lbl(@"MB   (outside this range → \tmp each launch)", 322, 82, dim: true);

            // System-capabilities card (RAM-disk readiness) pinned top-right.
            var caps = BuildCapsBox();
            caps.Location = new Point(Sc(940), Sc(8));

            // Content actions (bottom-right).
            var btnReset = Btn("Reset to default", 0, 80, null);
            btnReset.Location = new Point(Sc(1228) - btnReset.Width - Sc(16), Sc(80));
            btnReset.Click += (s, e) => DoReset();
            var btnManage = Btn("Manage cache…", 0, 80, null);
            btnManage.Location = new Point(btnReset.Left - btnManage.Width - Sc(8), Sc(80));
            btnManage.Click += (s, e) => DoManageCache();
            var btnClear = Btn("Clear archive data", 0, 80, null);
            btnClear.Location = new Point(btnManage.Left - btnClear.Width - Sc(8), Sc(80));
            btnClear.Click += (s, e) => DoClearArchiveData();
            var btnExts = Btn("Extensions…", 0, 80, null);
            btnExts.Location = new Point(btnClear.Left - btnExts.Width - Sc(8), Sc(80));
            btnExts.Click += (s, e) => DoEditGlobalExtensions();

            h.Controls.AddRange(new Control[]
            {
                title, lblCache, _tbCachePath, btnBrowse, lblSize, _numMaxGb, lblGb, _lblUsage,
                lblMin, _numMinMb, lblAnd, _numMaxMb, lblUnit, caps, btnReset, btnManage, btnClear, btnExts,
            });
            host.Controls.Add(h);
        }

        /// <summary>Modal editor for the three GLOBAL extension lists (archive triggers / disc images /
        /// metadata-ignored) — previously ini-only. Saved immediately (globals, not part of the fiche).</summary>
        private void DoEditGlobalExtensions()
        {
            var c = RomConfig.Instance;
            using var dlg = new Form
            {
                Text = "Global extension lists", FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowIcon = false, ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Sc(530), Sc(196)),
                BackColor = PanelBg,
            };
            Label L(string t, int y)
            {
                var l = new Label { Text = t, AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Font = Font9, Location = new Point(Sc(14), Sc(y)) };
                dlg.Controls.Add(l); return l;
            }
            TextBox T(string v, int y)
            {
                var tb = new TextBox
                {
                    Text = v, Location = new Point(Sc(160), Sc(y - 3)), Width = Sc(350),
                    BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = Font9, Enabled = !_ro,
                };
                dlg.Controls.Add(tb); return tb;
            }
            L("Archives:", 18); var tbArc = T(c.ArchiveExtensions ?? "", 18);
            L("Disc images:", 52); var tbDisc = T(c.DiscImageExtensions ?? "", 52);
            L("Metadata (ignored):", 86); var tbMeta = T(c.MetadataExtensions ?? "", 86);
            var note = new Label
            {
                Text = "Comma-separated, no dots. Archives trigger extraction; disc images trigger convert/copy; metadata files are never playable.",
                AutoSize = false, Size = new Size(Sc(500), Sc(32)), ForeColor = Sub, Font = Font85, BackColor = Color.Transparent,
                Location = new Point(Sc(14), Sc(114)),
            };
            dlg.Controls.Add(note);
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = Font9, Location = new Point(Sc(340), Sc(154)), Width = Sc(80), Enabled = !_ro };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, ForeColor = Fg, BackColor = Field, Font = Font9, Location = new Point(Sc(428), Sc(154)), Width = Sc(80) };
            dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;

            if (dlg.ShowDialog(Root.FindForm()) == DialogResult.OK && !_ro)
            {
                c.ArchiveExtensions = tbArc.Text.Trim();
                c.DiscImageExtensions = tbDisc.Text.Trim();
                c.MetadataExtensions = tbMeta.Text.Trim();
                c.Save();
                RomConfig.Invalidate();
            }
        }

        private Panel BuildCapsBox()
        {
            var box = new Panel { Size = new Size(Sc(276), Sc(60)), BackColor = Field };
            box.Paint += (s, e) => { using var pen = new Pen(Sub); e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1); };
            var cap = new Label { Text = "System capabilities", AutoSize = true, ForeColor = Sub, BackColor = Color.Transparent, Font = Font85, Location = new Point(Sc(10), Sc(6)) };
            _capDot = new Label { Text = "●", AutoSize = true, ForeColor = Danger, BackColor = Color.Transparent, Font = Font9, Location = new Point(Sc(10), Sc(28)) };
            _capLbl = new Label { Text = "", AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Font = Font9, Location = new Point(Sc(28), Sc(28)) };
            _capBtn = new Button { Text = "Install", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = Font9, Location = new Point(Sc(186), Sc(26)), Enabled = !_ro };
            _capBtn.FlatAppearance.BorderColor = Accent;
            _capBtn.Click += (s, e) => OnCapsButton();
            box.Controls.AddRange(new Control[] { cap, _capDot, _capLbl, _capBtn });
            return box;
        }

        private void RefreshCaps()
        {
            bool driver = false, task = false;
            try { driver = ArchiveRamDisk.IsDriverInstalled(); task = driver && ArchiveRamDisk.IsTaskInstalled(); } catch { }
            _ramReady = driver && task;
            _capDot.ForeColor = _ramReady ? Good : Danger;
            _capLbl.Text = _ramReady ? "RAM disk: ready" : "RAM disk: unavailable";
            if (_ro) { _capBtn.Visible = false; }
            else if (!driver) { _capBtn.Text = "Get ImDisk…"; _capBtn.Visible = true; }
            else if (!task) { _capBtn.Text = "Install"; _capBtn.Visible = true; }
            else _capBtn.Visible = false;
        }

        private void OnCapsButton()
        {
            if (_ro) return;
            bool driver = false;
            try { driver = ArchiveRamDisk.IsDriverInstalled(); } catch { }
            if (!driver)
            {
                try { Process.Start(new ProcessStartInfo("https://sourceforge.net/projects/imdisk-toolkit/") { UseShellExecute = true }); } catch { }
                return;
            }
            try
            {
                if (ArchiveRamDisk.InstallTask()) RefreshCaps();
                else MessageBox.Show("Could not register the RAM-disk task (UAC declined or ImDisk missing).", "ROM extractor");
            }
            catch (Exception ex) { MessageBox.Show("RAM-disk task install failed: " + ex.Message, "ROM extractor"); }
            RefreshRamRow();
        }

        // Live cache occupancy, computed off the UI thread (a cold / large cache never hangs the window).
        private void RefreshCacheUsage()
        {
            string root = (_tbCachePath.Text ?? "").Trim();
            _lblUsage.Text = "computing…";
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { _cacheUsedBytes = 0; UpdateUsageLabel(); return; }
            Task.Run(() =>
            {
                long used;
                try { ArchiveCacheIndex.Reconcile(root); used = ArchiveCacheIndex.TotalBytes(root); }
                catch { used = -1; }
                try
                {
                    if (Root.IsHandleCreated && !Root.IsDisposed)
                        Root.BeginInvoke(new Action(() => { _cacheUsedBytes = used; UpdateUsageLabel(); }));
                }
                catch { }
            });
        }

        private void UpdateUsageLabel()
        {
            if (_lblUsage == null) return;
            if (_cacheUsedBytes < 0) { _lblUsage.Text = ""; return; }
            double usedGb = _cacheUsedBytes / (1024.0 * 1024.0 * 1024.0);
            double maxGb = (double)_numMaxGb.Value;
            double freePct = maxGb > 0 ? Math.Max(0, Math.Min(100, (maxGb - usedGb) / maxGb * 100.0)) : 0;
            _lblUsage.Text = $"used {usedGb:0.0} / {maxGb:0} GB · {freePct:0}% free";
            _lblUsage.ForeColor = usedGb >= maxGb ? Danger : (freePct < 10 ? Warn : Sub);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CASCADE COLUMN
        // ═══════════════════════════════════════════════════════════════════
        private void BuildCascade(Panel host)
        {
            var left = new Panel { Location = new Point(Sc(4), Sc(128)), Size = new Size(Sc(300), Sc(500)), BackColor = PanelBg };
            left.Paint += (s, e) => { using var pen = new Pen(Field); e.Graphics.DrawRectangle(pen, 0, 0, left.Width - 1, left.Height - 1); };

            var lblPlat = Lbl("Platform", 14, 14, bold: true);
            _cmbPlat = new RomStateCombo { Location = new Point(Sc(14), Sc(40)), Width = Sc(210) };
            _cmbPlat.SelectedIndexChanged += (s, e) => { if (!_loading) OnPlatformChanged(); };
            var btnAddPlat = Btn("+", 232, 39, null); btnAddPlat.Width = Sc(40); btnAddPlat.Click += (s, e) => AddPlatform();
            _chkPlatDefault = Chk("Use default settings", 16, 74);
            _chkPlatDefault.CheckedChanged += (s, e) => { if (!_loading) OnUsePlatformDefault(); };

            _lblEmu = Lbl("Emulator", 14, 116, bold: true);
            _cmbEmu = new RomStateCombo { Location = new Point(Sc(14), Sc(142)), Width = Sc(210) };
            _cmbEmu.SelectedIndexChanged += (s, e) => { if (!_loading) OnEmulatorChanged(); };
            _btnAddEmu = Btn("+", 232, 141, null); _btnAddEmu.Width = Sc(40); _btnAddEmu.Click += (s, e) => AddEmulator();
            _chkEmuDefault = Chk("Use platform settings", 16, 176);
            _chkEmuDefault.CheckedChanged += (s, e) => { if (!_loading) OnUseEmulatorDefault(); };

            var leg = new GroupBox { Text = "Legend", ForeColor = Sub, BackColor = Color.Transparent, Font = Font9B, Location = new Point(Sc(14), Sc(220)), Size = new Size(Sc(272), Sc(110)) };
            leg.Controls.Add(LegendRow("◆  Default", Accent, 26));
            leg.Controls.Add(LegendRow("●  Customized", Good, 52));
            leg.Controls.Add(LegendRow("○  Untouched (inherits)", Sub, 78));

            var cascade = Lbl("Emulator inherits from platform, which\ninherits from the default profile.", 16, 344, dim: true); cascade.Font = Font85;

            left.Controls.AddRange(new Control[] { lblPlat, _cmbPlat, btnAddPlat, _chkPlatDefault, _lblEmu, _cmbEmu, _btnAddEmu, _chkEmuDefault, leg, cascade });
            host.Controls.Add(left);
        }

        private Label LegendRow(string text, Color c, int y)
        {
            var l = Lbl(text, 16, y); l.ForeColor = c; return l;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  FICHE
        // ═══════════════════════════════════════════════════════════════════
        private void BuildFiche(Panel host)
        {
            _fiche = new GroupBox { Text = "Profile settings", ForeColor = Fg, BackColor = Bg, Font = Font9B, Location = new Point(Sc(312), Sc(128)), Size = new Size(Sc(920), Sc(500)) };
            _ficheInner = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(Sc(12), Sc(20), Sc(12), Sc(12)), BackColor = PanelBg };

            var lblCopy = Lbl("Copy settings from", 12, 10);
            _cmbCopyFrom = Combo(150, 7, 230);
            var btnCopy = Btn("Copy", 388, 6, Accent); btnCopy.Click += (s, e) => DoCopyFrom();

            _chkRam = Chk("Put in RAM disk if size ≤", 12, 44);
            _numRam = Num(2000, 1, 1000000, 200, 41, 72);
            _lblRamMb = Lbl("MB", 278, 44, dim: true);
            _lblRamNa = Lbl("(RAM disk unavailable)", 312, 46, dim: true); _lblRamNa.Font = Font85;
            _chkRam.CheckedChanged += (s, e) => RefreshRamRow();

            var lblMode = Lbl("Operation mode", 12, 78, bold: true);
            _cmbMode = Combo(150, 75, 320);
            _cmbMode.Items.AddRange(new object[]
            {
                "smartextract — extract the selected file(s)",
                "copy — copy the file locally (no extraction)",
                "convert — convert (chd → iso/cue, etc.)",
                "do-nothing — leave as-is, launch directly",
            });
            _cmbMode.SelectedIndexChanged += (s, e) => { if (!_loading) UpdateModeOptions(); };

            // Per-mode option strip (all controls built once, toggled per mode).
            _modeOptions = new Panel { Location = new Point(Sc(12), Sc(108)), Size = new Size(Sc(452), Sc(166)), BackColor = Field };
            BuildModeStrip();

            _lblSub = Lbl("Sub-folder (after the signature)", 12, 282);
            _cmbSub = Combo(216, 279, 150);
            _cmbSub.Items.AddRange(new object[] { "None", "Game title", "Platform", "Emulator", "Platform code" });
            _cmbSub.SelectedIndexChanged += (s, e) => { if (!_loading) RefreshExample(); };
            _lblSig = Lbl("MD5(8) signature is always inserted first.", 12, 308, dim: true); _lblSig.Font = Font85;
            _lblSubEx = Lbl("", 12, 328, dim: true); _lblSubEx.Font = Font85; _lblSubEx.AutoSize = true;
            _lblSubTmp = Lbl("", 12, 372, dim: true); _lblSubTmp.Font = Font85; _lblSubTmp.ForeColor = Warn; _lblSubTmp.AutoSize = true;

            _grpRules = new GroupBox { Text = "File rules", ForeColor = Sub, BackColor = Color.Transparent, Font = Font9B, Location = new Point(Sc(478), Sc(4)), Size = new Size(Sc(408), Sc(320)) };
            BuildRules(_grpRules);
            _grpConvert = new GroupBox { Text = "Conversions (input format → output)", ForeColor = Sub, BackColor = Color.Transparent, Font = Font9B, Location = new Point(Sc(478), Sc(4)), Size = new Size(Sc(408), Sc(320)), Visible = false };
            BuildConvert(_grpConvert);
            _grpTexture = new GroupBox { Text = "Texture pack", ForeColor = Sub, BackColor = Color.Transparent, Font = Font9B, Location = new Point(Sc(12), Sc(388)), Size = new Size(Sc(452), Sc(132)) };
            BuildTexture(_grpTexture);
            _grpM3u = new GroupBox { Text = "M3U support (input)", ForeColor = Sub, BackColor = Color.Transparent, Font = Font9B, Location = new Point(Sc(478), Sc(388)), Size = new Size(Sc(408), Sc(132)) };
            BuildM3u(_grpM3u);

            _ficheInner.Controls.AddRange(new Control[]
            {
                lblCopy, _cmbCopyFrom, btnCopy, _chkRam, _numRam, _lblRamMb, _lblRamNa,
                lblMode, _cmbMode, _modeOptions, _lblSub, _cmbSub, _lblSig, _lblSubEx, _lblSubTmp,
                _grpRules, _grpConvert, _grpTexture, _grpM3u,
            });
            _fiche.Controls.Add(_ficheInner);
            host.Controls.Add(_fiche);
        }

        private void BuildModeStrip()
        {
            _chkCompanions = Chk("Extract companions (non-ROM, non-ignored, e.g. .pcm)", 10, 4); _chkCompanions.BackColor = Color.Transparent;
            _chkOtherRoms = Chk("Also extract other ROMs (= full extraction)", 10, 28); _chkOtherRoms.BackColor = Color.Transparent;
            _chkOtherRoms.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                if (_chkOtherRoms.Checked) _chkCompanions.Checked = true;
                ApplyConvertAfterUi();
            };
            var lblOut = Lbl("Output name", 10, 58); lblOut.BackColor = Color.Transparent; lblOut.Name = "lblOut";
            _cmbOut = Combo(110, 55, 150);
            _cmbOut.Items.AddRange(new object[] { "Keep original", "Game title" });
            _lblOutHint = Lbl("", 266, 59, dim: true); _lblOutHint.Font = Font85; _lblOutHint.BackColor = Color.Transparent;
            _cmbOut.SelectedIndexChanged += (s, e) =>
            {
                _lblOutHint.Text = _cmbOut.SelectedIndex == 1 ? @"→ \tmp (name not shared)" : "";
                if (!_loading) RefreshExample();
            };
            _chkConvertAfter = Chk("Follow up with image convert (per Conversions →)", 10, 84); _chkConvertAfter.BackColor = Color.Transparent;
            _chkConvertAfter.CheckedChanged += (s, e) => { ApplyConvertAfterUi(); if (!_loading) RefreshExample(); };
            _chkFlatten = Chk("Flatten extraction (off = preserve the archive tree)", 10, 108); _chkFlatten.BackColor = Color.Transparent;
            var lblComp = Lbl("Companion exts", 10, 136); lblComp.BackColor = Color.Transparent; lblComp.Name = "lblComp";
            _tbCompExt = Tb(130, 133, 130);

            // copy-mode controls (share the strip; toggled by mode)
            var lblCopyExt = Lbl("Extensions", 10, 12); lblCopyExt.BackColor = Color.Transparent; lblCopyExt.Name = "lblCopyExt";
            _tbCopyExt = Tb(110, 9, 250);
            _lblCopyHint = Lbl("Copies these files to the cache — no extraction or selection.", 10, 42, dim: true); _lblCopyHint.Font = Font85; _lblCopyHint.BackColor = Color.Transparent;

            // convert / do-nothing hint
            _lblConvHint = Lbl("", 10, 16, dim: true); _lblConvHint.Font = Font85; _lblConvHint.BackColor = Color.Transparent; _lblConvHint.MaximumSize = new Size(Sc(430), 0);

            _modeOptions.Controls.AddRange(new Control[]
            {
                _chkCompanions, _chkOtherRoms, lblOut, _cmbOut, _lblOutHint, _chkConvertAfter, _chkFlatten, lblComp, _tbCompExt,
                lblCopyExt, _tbCopyExt, _lblCopyHint, _lblConvHint,
            });
        }

        // Toggle the per-mode option strip + the right-hand panel (File rules vs Conversions).
        private void UpdateModeOptions()
        {
            int idx = _cmbMode.SelectedIndex; if (idx < 0) idx = 0;

            var smart = new Control[] { _chkCompanions, _chkOtherRoms, _chkConvertAfter, _chkFlatten, _cmbOut, _lblOutHint, _tbCompExt };
            foreach (var c in smart) c.Visible = idx == 0;
            foreach (var n in new[] { "lblOut", "lblComp" }) foreach (Control c in _modeOptions.Controls) if (c.Name == n) c.Visible = idx == 0;

            bool copy = idx == 1;
            _tbCopyExt.Visible = copy; _lblCopyHint.Visible = copy;
            foreach (Control c in _modeOptions.Controls) if (c.Name == "lblCopyExt") c.Visible = copy;

            _lblConvHint.Visible = idx == 2 || idx == 3;
            _lblConvHint.Text = idx == 2
                ? "Define the conversions in the grid on the right. An empty output = leave that format untouched."
                : (idx == 3 ? "No action: the file is passed as-is to the emulator (no extraction, no cache)." : "");

            bool usesCache = idx != 3;
            foreach (var c in new Control[] { _lblSub, _cmbSub, _lblSig, _lblSubEx, _lblSubTmp }) c.Visible = usesCache;

            _grpRules.Visible = idx == 0;
            _grpConvert.Visible = idx == 2;
            ApplyConvertAfterUi();
            if (!_loading) RefreshExample();
        }

        // Reconciles the smartextract "Follow up with image convert" toggle: forces companions ON and swaps
        // the right panel between File-rules and Conversions (they share the region).
        private void ApplyConvertAfterUi()
        {
            if (_cmbMode.SelectedIndex != 0) return;
            bool conv = _chkConvertAfter.Checked;
            bool other = _chkOtherRoms.Checked;
            if (conv) _chkCompanions.Checked = true;
            _chkCompanions.Enabled = !_ro && !(conv || other);
            _grpRules.Visible = !conv;
            _grpConvert.Visible = conv;
        }

        private void RefreshRamRow()
        {
            _chkRam.Enabled = !_ro && _ramReady;
            _numRam.Enabled = !_ro && _ramReady && _chkRam.Checked;
            _lblRamMb.Enabled = _ramReady;
            _lblRamNa.Visible = !_ramReady;
        }

        private void RefreshExample()
        {
            if (_lblSubEx == null) return;
            string root = string.IsNullOrWhiteSpace(_tbCachePath.Text) ? @"D:\Emulation\romcache" : _tbCachePath.Text.Trim();
            if (root.Length > 48) root = root[..22] + "…" + root[^22..];
            const string romFull = @"Super Mario World 2 [Europe][En][Rev 2][!].sfc";
            const string sig = "3F9C1A77";

            int mode = _cmbMode.SelectedIndex; if (mode < 0) mode = 0;
            bool outputTitle = mode == 0 && _cmbOut.SelectedIndex == 1;

            string sub = (_cmbSub.SelectedIndex) switch
            {
                1 => "Yoshi's Island",
                2 => "Super Nintendo Entertainment System",
                3 => "Snes9x",
                4 => "SNES",
                _ => "",
            };
            string subSeg = sub.Length > 0 ? "\\" + sub : "";
            string file = mode == 2 ? "(output per the conversions grid)" : (outputTitle ? "Yoshi's Island.sfc" : romFull);
            string tmpSeg = outputTitle ? @"\tmp" : "";
            _lblSubEx.Text = $"e.g.   {root}{tmpSeg}\\\n         {sig}{subSeg}\\\n         {file}";
            _lblSubTmp.Text = outputTitle
                ? "→ in \\tmp because Output name = Game title (not shared)."
                : "\\tmp is used instead if extraction is outside the size range.";
        }

        // ── File-rules group ───────────────────────────────────────────────
        private void BuildRules(GroupBox g)
        {
            var romLbl = Lbl("ROM extensions", 14, 26); romLbl.BackColor = Color.Transparent;
            _tbRomExt = Tb(132, 23, 258);
            var ignLbl = Lbl("Ignored extensions", 14, 56); ignLbl.BackColor = Color.Transparent;
            _tbIgnExt = Tb(132, 53, 258);
            var note = Lbl("ROM = playable candidates · ignored = metadata. Everything\nelse = companions (pulled with the pick when the option is on).", 14, 82, dim: true); note.Font = Font85; note.BackColor = Color.Transparent;

            var prioLbl = Lbl("Tag priority (score = sum of weights)", 14, 118, bold: true); prioLbl.BackColor = Color.Transparent;
            _grid = ModulePanelKit.Grid(_s, _ro);
            _grid.Location = new Point(Sc(14), Sc(140)); _grid.Size = new Size(Sc(280), Sc(130));
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.Columns.Add("tag", "Tag / pattern");
            _grid.Columns.Add("w", "Weight");
            _grid.Columns[0].Width = Sc(200); _grid.Columns[1].Width = Sc(64);

            var add = Btn("Add", 300, 140, null); add.Width = Sc(90); add.Click += (s, e) => { if (!_ro) _grid.Rows.Add("(tag)", "0"); };
            var del = Btn("Remove", 300, 172, null); del.Width = Sc(90);
            del.Click += (s, e) => { if (!_ro && _grid.CurrentRow != null && !_grid.CurrentRow.IsNewRow) _grid.Rows.Remove(_grid.CurrentRow); };
            var whint = Lbl("Score = sum.\nNegative = avoid.\n(no hard exclude:\na lone (Proto)\nstill launches)", 300, 204, dim: true); whint.Font = Font85; whint.BackColor = Color.Transparent;

            var raLbl = Lbl("Extra bonus for games with RetroAchievements", 14, 282); raLbl.BackColor = Color.Transparent;
            _numRaBonus = Num(10000, 0, 1000000, 300, 279, 90);

            g.Controls.AddRange(new Control[] { romLbl, _tbRomExt, ignLbl, _tbIgnExt, note, prioLbl, _grid, add, del, whint, raLbl, _numRaBonus });
        }

        // ── Conversions grid ───────────────────────────────────────────────
        private void BuildConvert(GroupBox g)
        {
            var note = Lbl("One rule per row: input format → output (e.g. chd → iso, iso → rvz).\nLeave output empty to pass the format through untouched.", 14, 26, dim: true); note.Font = Font85; note.BackColor = Color.Transparent;
            _convGrid = ModulePanelKit.Grid(_s, _ro);
            _convGrid.Location = new Point(Sc(14), Sc(64)); _convGrid.Size = new Size(Sc(376), Sc(200));
            _convGrid.Columns.Add("in", "Input format");
            _convGrid.Columns.Add("out", "Output format");

            var add = Btn("Add", 14, 272, null); add.Width = Sc(90); add.Click += (s, e) => { if (!_ro) _convGrid.Rows.Add("iso", "chd"); };
            var del = Btn("Remove", 112, 272, null); del.Width = Sc(90);
            del.Click += (s, e) => { if (!_ro && _convGrid.CurrentRow != null && !_convGrid.CurrentRow.IsNewRow) _convGrid.Rows.Remove(_convGrid.CurrentRow); };

            g.Controls.AddRange(new Control[] { note, _convGrid, add, del });
        }

        // ── Texture-pack group ─────────────────────────────────────────────
        private void BuildTexture(GroupBox g)
        {
            _chkTexture = Chk("Enable texture packs for this profile", 14, 22); _chkTexture.BackColor = Color.Transparent;
            var lblExt = Lbl("Extension(s)", 14, 52); lblExt.BackColor = Color.Transparent;
            _tbTexExt = Tb(130, 49, 120);
            var lblPath = Lbl("Extraction path", 14, 80); lblPath.BackColor = Color.Transparent;
            _tbTexPath = Tb(130, 77, 190);
            var btnBrowse = Btn("Browse…", 328, 76, null);
            btnBrowse.Click += (s, e) => { try { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) _tbTexPath.Text = d.SelectedPath; } catch { } };
            var hint = Lbl("Tokens: {EmuDir}, {GameId}, {GameTitle}.", 14, 106, dim: true); hint.Font = Font85; hint.BackColor = Color.Transparent;

            void Toggle() { bool on = _chkTexture.Checked && !_ro; foreach (var c in new Control[] { lblExt, _tbTexExt, lblPath, _tbTexPath, btnBrowse }) c.Enabled = on; }
            _chkTexture.CheckedChanged += (s, e) => Toggle();
            Toggle();

            g.Controls.AddRange(new Control[] { _chkTexture, lblExt, _tbTexExt, lblPath, _tbTexPath, btnBrowse, hint });
        }

        // ── M3U-input group ────────────────────────────────────────────────
        private void BuildM3u(GroupBox g)
        {
            _chkM3u = Chk("M3U input support", 14, 22); _chkM3u.BackColor = Color.Transparent;
            var txt = Lbl(
                "When the emulator is launched with an .m3u, each listed file is inspected;\n"
                + "if at least one needs processing (extract, convert…) every file is processed\n"
                + "and a rewritten .m3u replaces the original. Otherwise it passes through\n"
                + "unchanged. (Never generated for a multi-disc archive.)",
                14, 48, dim: true); txt.Font = Font85; txt.BackColor = Color.Transparent;
            g.Controls.AddRange(new Control[] { _chkM3u, txt });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONTENT ACTIONS
        // ═══════════════════════════════════════════════════════════════════
        private void DoManageCache()
        {
            string root = (_tbCachePath.Text ?? "").Trim();
            try
            {
                if (string.IsNullOrEmpty(root)) { MessageBox.Show("No cache folder set.", "ROM extractor"); return; }
                Directory.CreateDirectory(root);
                Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Could not open the cache folder: " + ex.Message, "ROM extractor"); }
            RefreshCacheUsage();
        }

        private void DoClearArchiveData()
        {
            if (_ro) return;
            string root = (_tbCachePath.Text ?? "").Trim();
            if (MessageBox.Show(
                    "Clear the extraction cache?\n\nDeletes every cached extraction / conversion under the cache folder and empties the cache index. Archives re-extract on next launch. Your settings and profiles are kept.",
                    "Clear archive data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            long freed = 0;
            try { foreach (var e in ArchiveCacheDb.CacheEntries()) freed += ArchiveCacheDb.DeleteCache(root, e.Signature); }
            catch (Exception ex) { MessageBox.Show("Clear failed: " + ex.Message, "ROM extractor"); }
            double mb = freed / (1024.0 * 1024.0);
            MessageBox.Show($"Extraction cache cleared. Freed {mb:0.0} MB.", "ROM extractor");
            RefreshCacheUsage();
        }

        private void DoReset()
        {
            if (_ro) return;
            if (MessageBox.Show(
                    "Reset every ROM-extractor setting to the shipped defaults?\n\nRemoves all customized platform / emulator profiles and restores the Default profile, cache band and extension lists. The cache folder is kept.",
                    "Reset to default", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string keepPath = (_tbCachePath.Text ?? "").Trim();
            RomConfig.ResetToDefaults();
            var fresh = RomConfig.Instance;
            if (keepPath.Length > 0) fresh.CachePath = keepPath;   // reset keeps the folder, like ExtendDB
            LoadFromConfig();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  LOAD / SAVE
        // ═══════════════════════════════════════════════════════════════════
        private void LoadFromConfig()
        {
            _loading = true;
            var mgs = RomConfig.Instance;

            _tbCachePath.Text = mgs.CachePath ?? "";
            _numMaxGb.Value = Clamp(mgs.CacheMaxGb, 1, 100000);
            _numMinMb.Value = Clamp(mgs.CacheMinMb, 0, 1000000);
            _numMaxMb.Value = Clamp(mgs.CacheMaxMb, 0, 1000000);

            _global = Clone(mgs.GlobalDefault ?? new ArchivePriorityRow { Platform = "All", Emulator = "All" });
            _profiles.Clear();
            foreach (var r in mgs.Priorities) _profiles.Add(Clone(r));

            RefreshCaps();
            RefreshCacheUsage();
            RefreshPlatformCombo(null);

            _loading = false;
            OnPlatformChanged();
        }

        public void Apply()
        {
            if (_ro) return;
            if (_editable && _target != null) SaveFicheToRow(_target);

            var c = RomConfig.Instance;
            var path = (_tbCachePath.Text ?? "").Trim();
            if (path.Length > 0) c.CachePath = path;
            c.CacheMaxGb = (int)_numMaxGb.Value;
            c.CacheMinMb = (int)_numMinMb.Value;
            c.CacheMaxMb = (int)_numMaxMb.Value;
            c.GlobalDefault = _global;
            c.Priorities = _profiles.Where(r => !string.IsNullOrWhiteSpace(r.Platform)).ToList();

            c.Save();
            RomConfig.Invalidate();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CASCADE LOGIC
        // ═══════════════════════════════════════════════════════════════════
        private void RefreshPlatformCombo(string? selectName)
        {
            bool was = _loading; _loading = true;
            _cmbPlat.Items.Clear();
            _cmbPlat.Items.Add(new RomStateItem("Default (all platforms)", RomCfgState.Default));
            var names = new SortedSet<string>(AllPlatformNames(), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(selectName)) names.Add(selectName);
            foreach (var name in names)
            {
                bool custom = _profiles.Any(r => Eq(r.Platform, name));
                _cmbPlat.Items.Add(new RomStateItem(name, custom ? RomCfgState.Customized : RomCfgState.Untouched));
            }
            SelectByName(_cmbPlat, selectName);
            RefreshCopyFrom();
            _loading = was;
        }

        private void RefreshEmulatorCombo(string platform, string? selectName)
        {
            bool was = _loading; _loading = true;
            _cmbEmu.Items.Clear();
            _cmbEmu.Items.Add(new RomStateItem("Default (platform settings)", RomCfgState.Default));
            var names = new SortedSet<string>(EmulatorNamesFor(platform), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(selectName) && !Eq(selectName, "All")) names.Add(selectName);
            foreach (var name in names)
            {
                bool custom = _profiles.Any(r => Eq(r.Platform, platform) && Eq(r.Emulator, name));
                _cmbEmu.Items.Add(new RomStateItem(name, custom ? RomCfgState.Customized : RomCfgState.Untouched));
            }
            SelectByName(_cmbEmu, Eq(selectName ?? "", "All") ? null : selectName);
            _loading = was;
        }

        private static void SelectByName(ComboBox c, string? name)
        {
            if (string.IsNullOrEmpty(name)) { if (c.Items.Count > 0) c.SelectedIndex = 0; return; }
            for (int i = 0; i < c.Items.Count; i++)
                if (Eq((c.Items[i] as RomStateItem)?.Text ?? "", name)) { c.SelectedIndex = i; return; }
            if (c.Items.Count > 0) c.SelectedIndex = 0;
        }

        private void RefreshCopyFrom()
        {
            bool was = _loading; _loading = true;
            _cmbCopyFrom.Items.Clear();
            _cmbCopyFrom.Items.Add("(choose a source…)");
            _cmbCopyFrom.Items.Add("Default (global)");
            foreach (var r in _profiles)
                _cmbCopyFrom.Items.Add(r.Platform + " / " + (string.IsNullOrEmpty(r.Emulator) ? "All" : r.Emulator));
            _cmbCopyFrom.Items.Add("— presets —");
            _cmbCopyFrom.Items.Add("Preset · GoodSet");
            _cmbCopyFrom.Items.Add("Preset · HackSet");
            _cmbCopyFrom.SelectedIndex = 0;
            _loading = was;
        }

        private string SelPlatform() => (_cmbPlat.SelectedItem as RomStateItem)?.Text ?? "";
        private string SelEmulator() => (_cmbEmu.SelectedItem as RomStateItem)?.Text ?? "";
        private bool PlatformIsGlobal() => _cmbPlat.SelectedIndex <= 0;
        private bool EmulatorIsDefault() => _cmbEmu.SelectedIndex <= 0;

        private void OnPlatformChanged()
        {
            if (_editable && _target != null) SaveFicheToRow(_target);

            bool global = PlatformIsGlobal();
            _lblEmu.Visible = _cmbEmu.Visible = _btnAddEmu.Visible = _chkEmuDefault.Visible = _chkPlatDefault.Visible = !global;

            if (global) { BindTarget(_global, true, "Profile settings (Default — all platforms)"); return; }

            string plat = SelPlatform();
            RefreshEmulatorCombo(plat, null);
            UpdatePlatformCheckbox(plat);
            ResolveAndBind();
        }

        private void OnEmulatorChanged()
        {
            if (_editable && _target != null) SaveFicheToRow(_target);
            UpdateEmulatorCheckbox();
            ResolveAndBind();
        }

        private void UpdatePlatformCheckbox(string plat)
        {
            bool was = _loading; _loading = true;
            bool custom = _profiles.Any(r => Eq(r.Platform, plat));
            _chkPlatDefault.Checked = !custom;
            _cmbEmu.Enabled = _btnAddEmu.Enabled = _chkEmuDefault.Enabled = custom && !_ro;
            _loading = was;
        }

        private void UpdateEmulatorCheckbox()
        {
            bool was = _loading; _loading = true;
            if (EmulatorIsDefault()) { _chkEmuDefault.Checked = false; _chkEmuDefault.Enabled = false; }
            else
            {
                string plat = SelPlatform(), emu = SelEmulator();
                bool custom = _profiles.Any(r => Eq(r.Platform, plat) && Eq(r.Emulator, emu));
                _chkEmuDefault.Enabled = !_ro;
                _chkEmuDefault.Checked = !custom;
            }
            _loading = was;
        }

        private void ResolveAndBind()
        {
            const string inheritDefault = "Inherited — uncheck « Use default settings » to edit";
            const string inheritPlatform = "Inherited — uncheck « Use platform settings » to edit";

            if (PlatformIsGlobal()) { BindTarget(_global, true, "Profile settings (Default — all platforms)"); return; }
            string plat = SelPlatform();
            bool platCustom = _profiles.Any(r => Eq(r.Platform, plat));
            if (!platCustom) { BindTarget(_global, false, inheritDefault); return; }

            var platRow = FindRow(plat, "All");
            if (EmulatorIsDefault()) { BindTarget(platRow ?? _global, platRow != null, $"Profile settings ({plat} · Default)"); return; }

            string emu = SelEmulator();
            var emuRow = FindRow(plat, emu);
            if (emuRow != null) BindTarget(emuRow, true, $"Profile settings ({plat} · {emu})");
            else BindTarget(platRow ?? _global, false, inheritPlatform);
        }

        private void BindTarget(ArchivePriorityRow row, bool editable, string caption)
        {
            _target = row; _editable = editable;
            LoadRowToFiche(row, caption);
            _ficheInner.Enabled = editable && !_ro;
        }

        private void OnUsePlatformDefault()
        {
            string plat = SelPlatform(); if (string.IsNullOrEmpty(plat)) return;
            if (_chkPlatDefault.Checked) _profiles.RemoveAll(r => Eq(r.Platform, plat));
            else EnsureRow(plat, "All", _global);
            RefreshPlatformCombo(plat);
            RefreshEmulatorCombo(plat, null);
            UpdatePlatformCheckbox(plat);
            ResolveAndBind();
        }

        private void OnUseEmulatorDefault()
        {
            string plat = SelPlatform(), emu = SelEmulator();
            if (string.IsNullOrEmpty(plat) || EmulatorIsDefault()) return;
            if (_chkEmuDefault.Checked) _profiles.RemoveAll(r => Eq(r.Platform, plat) && Eq(r.Emulator, emu));
            else EnsureRow(plat, emu, FindRow(plat, "All") ?? _global);
            RefreshPlatformCombo(plat);
            RefreshEmulatorCombo(plat, emu);
            UpdateEmulatorCheckbox();
            ResolveAndBind();
        }

        private void AddPlatform()
        {
            if (_ro) return;
            string? plat = Prompt("Platform name (exact LaunchBox platform):");
            if (string.IsNullOrWhiteSpace(plat)) return;
            plat = plat.Trim();
            EnsureRow(plat, "All", _global);
            RefreshPlatformCombo(plat);
            OnPlatformChanged();
        }

        private void AddEmulator()
        {
            if (_ro) return;
            string plat = SelPlatform();
            if (string.IsNullOrEmpty(plat) || PlatformIsGlobal()) { MessageBox.Show("Select a platform first.", "ROM extractor"); return; }
            string? emu = Prompt("Emulator title:");
            if (string.IsNullOrWhiteSpace(emu)) return;
            emu = emu.Trim();
            EnsureRow(plat, "All", _global);
            EnsureRow(plat, emu, FindRow(plat, "All"));
            RefreshPlatformCombo(plat);
            UpdatePlatformCheckbox(plat);
            RefreshEmulatorCombo(plat, emu);
            OnEmulatorChanged();
        }

        private void DoCopyFrom()
        {
            if (!_editable || _target == null) return;
            int idx = _cmbCopyFrom.SelectedIndex;
            string sel = _cmbCopyFrom.SelectedItem as string ?? "";
            if (idx <= 0 || sel == "— presets —") return;

            ArchivePriorityRow? src = null;
            if (sel == "Preset · GoodSet") src = PresetGoodSet();
            else if (sel == "Preset · HackSet") src = PresetHackSet();
            else if (sel == "Default (global)") src = _global;
            else
            {
                int rowIdx = idx - 2;   // 0=(choose) 1=Default(global) then profiles
                if (rowIdx >= 0 && rowIdx < _profiles.Count) src = _profiles[rowIdx];
            }
            if (src == null || ReferenceEquals(src, _target)) return;
            _target.CopyFrom(src);
            LoadRowToFiche(_target, _fiche.Text);
        }

        private static ArchivePriorityRow PresetGoodSet() => new()
        {
            TagWeights = new()
            {
                new() { Tag = "(Europe)", Weight = 40 }, new() { Tag = "(World)", Weight = 35 },
                new() { Tag = "(USA)", Weight = 30 }, new() { Tag = "(Japan)", Weight = 10 },
                new() { Tag = "[!]", Weight = 10 }, new() { Tag = "(Beta)", Weight = -30 },
                new() { Tag = "(Proto)", Weight = -30 }, new() { Tag = "(Demo)", Weight = -20 },
                new() { Tag = "[b]", Weight = -100 }, new() { Tag = "[h]", Weight = -50 },
            },
        };

        private static ArchivePriorityRow PresetHackSet() => new()
        {
            TagWeights = new()
            {
                new() { Tag = "[h]", Weight = 40 }, new() { Tag = "[T+", Weight = 40 },
                new() { Tag = "(Europe)", Weight = 20 }, new() { Tag = "(USA)", Weight = 15 },
                new() { Tag = "[!]", Weight = 5 },
            },
        };

        // ── Fiche <-> row binding ──────────────────────────────────────────
        private void LoadRowToFiche(ArchivePriorityRow r, string caption)
        {
            _loading = true;
            _fiche.Text = caption;

            _cmbMode.SelectedIndex = ClampIdx((int)r.Mode, _cmbMode);
            UpdateModeOptions();
            _chkCompanions.Checked = r.ExtractCompanions;
            _chkOtherRoms.Checked = r.ExtractOtherRoms;
            _chkFlatten.Checked = r.FlattenExtraction;
            _tbCompExt.Text = r.CompanionExtensions ?? "bin";
            _chkConvertAfter.Checked = r.ConvertAfterExtract;
            _cmbOut.SelectedIndex = ClampIdx((int)r.OutputName, _cmbOut);
            _tbCopyExt.Text = r.CopyExtensions ?? "";
            ApplyConvertAfterUi();

            _cmbSub.SelectedIndex = ClampIdx((int)r.SubDirScheme, _cmbSub);
            _tbRomExt.Text = r.RomExtensions ?? "";
            _tbIgnExt.Text = r.IgnoredExtensions ?? "";

            LoadWeightsGrid(r.TagWeights);
            _numRaBonus.Value = Clamp(r.RetroAchievementsBonus, 0, 1000000);
            LoadConversionsGrid(r.Conversions);

            _chkRam.Checked = r.RamDiskEnabled; _numRam.Value = Clamp(r.RamDiskMaxMb, 1, 1000000);
            RefreshRamRow();
            _chkTexture.Checked = r.TextureEnabled; _tbTexExt.Text = r.TextureExtensions ?? ""; _tbTexPath.Text = r.TextureExtractPath ?? "";
            _chkM3u.Checked = r.M3uInput;

            _loading = false;
            RefreshExample();
        }

        private void SaveFicheToRow(ArchivePriorityRow r)
        {
            r.Mode = (ArchiveMode)Math.Max(0, _cmbMode.SelectedIndex);
            r.ExtractCompanions = _chkCompanions.Checked;
            r.ExtractOtherRoms = _chkOtherRoms.Checked;
            r.FlattenExtraction = _chkFlatten.Checked;
            r.CompanionExtensions = (_tbCompExt.Text ?? "").Trim();
            r.ConvertAfterExtract = _chkConvertAfter.Checked;
            r.OutputName = (OutputNameMode)Math.Max(0, _cmbOut.SelectedIndex);
            r.CopyExtensions = (_tbCopyExt.Text ?? "").Trim();
            r.SubDirScheme = (CacheSubDirScheme)Math.Max(0, _cmbSub.SelectedIndex);
            r.RomExtensions = (_tbRomExt.Text ?? "").Trim();
            r.IgnoredExtensions = (_tbIgnExt.Text ?? "").Trim();
            r.TagWeights = ReadWeightsGrid();
            r.RetroAchievementsBonus = (int)_numRaBonus.Value;
            r.Conversions = ReadConversionsGrid();
            r.RamDiskEnabled = _chkRam.Checked; r.RamDiskMaxMb = (int)_numRam.Value;
            r.TextureEnabled = _chkTexture.Checked; r.TextureExtensions = (_tbTexExt.Text ?? "").Trim(); r.TextureExtractPath = (_tbTexPath.Text ?? "").Trim();
            r.M3uInput = _chkM3u.Checked;
        }

        private void LoadWeightsGrid(List<TagWeight>? weights)
        {
            _grid.Rows.Clear();
            foreach (var w in weights ?? new()) _grid.Rows.Add(w.Tag, (w.Weight >= 0 ? "+" : "") + w.Weight);
        }

        private List<TagWeight> ReadWeightsGrid()
        {
            var list = new List<TagWeight>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                var tag = (row.Cells[0].Value as string ?? "").Trim();
                var wraw = (row.Cells[1].Value as string ?? "").Trim().Replace("+", "");
                if (tag.Length == 0) continue;
                if (int.TryParse(wraw, out var w)) list.Add(new TagWeight { Tag = tag, Weight = w });
            }
            return list;
        }

        private void LoadConversionsGrid(List<ConvertRule>? conv)
        {
            _convGrid.Rows.Clear();
            foreach (var c in conv ?? new())
            {
                if (string.IsNullOrWhiteSpace(c.Input)) continue;
                _convGrid.Rows.Add(c.Input.Trim(), (c.Output ?? "").Trim());
            }
        }

        private List<ConvertRule> ReadConversionsGrid()
        {
            var list = new List<ConvertRule>();
            foreach (DataGridViewRow row in _convGrid.Rows)
            {
                if (row.IsNewRow) continue;
                var inp = (row.Cells[0].Value as string ?? "").Trim();
                var outp = (row.Cells[1].Value as string ?? "").Trim();
                if (inp.Length == 0) continue;
                list.Add(new ConvertRule { Input = inp, Output = outp });
            }
            return list;
        }

        // ── Row + enumeration helpers ──────────────────────────────────────
        private ArchivePriorityRow? FindRow(string plat, string emu) => _profiles.FirstOrDefault(r => Eq(r.Platform, plat) && Eq(r.Emulator, emu));

        private ArchivePriorityRow EnsureRow(string plat, string emu, ArchivePriorityRow? seed)
        {
            var existing = FindRow(plat, emu);
            if (existing != null) return existing;
            var r = new ArchivePriorityRow { Platform = plat, Emulator = string.IsNullOrEmpty(emu) ? "All" : emu };
            if (seed != null) r.CopyFrom(seed);
            _profiles.Add(r);
            return r;
        }

        private static ArchivePriorityRow Clone(ArchivePriorityRow s)
        {
            var r = new ArchivePriorityRow { Platform = s.Platform, Emulator = s.Emulator };
            r.CopyFrom(s);
            return r;
        }

        private IEnumerable<string> AllPlatformNames()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>())
                    if (!string.IsNullOrWhiteSpace(p?.Name)) set.Add(p!.Name);
            }
            catch { }
            foreach (var r in _profiles) if (!string.IsNullOrWhiteSpace(r.Platform)) set.Add(r.Platform);
            return set;
        }

        private IEnumerable<string> EmulatorNamesFor(string platform)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>())
                {
                    if (string.IsNullOrWhiteSpace(e?.Title)) continue;
                    var eps = e!.GetAllEmulatorPlatforms() ?? Array.Empty<IEmulatorPlatform>();
                    if (eps.Any(ep => Eq(ep?.Platform ?? "", platform))) set.Add(e.Title);
                }
            }
            catch { }
            foreach (var r in _profiles)
                if (Eq(r.Platform, platform) && !Eq(r.Emulator, "All") && !string.IsNullOrWhiteSpace(r.Emulator)) set.Add(r.Emulator);
            return set;
        }

        // ── Misc ───────────────────────────────────────────────────────────
        private static bool Eq(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        private static decimal Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
        private static int ClampIdx(int v, ComboBox c) => c.Items.Count == 0 ? -1 : Math.Max(0, Math.Min(c.Items.Count - 1, v));

        private string? Prompt(string label)
        {
            using var f = new Form { Text = "ROM extractor", BackColor = Bg, ForeColor = Fg, Font = Font9, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, Size = new Size(Sc(440), Sc(160)), MaximizeBox = false, MinimizeBox = false };
            var l = new Label { Text = label, AutoSize = true, Location = new Point(Sc(14), Sc(16)), ForeColor = Fg, BackColor = Bg };
            var t = new TextBox { Location = new Point(Sc(14), Sc(44)), Width = Sc(400), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
            var ok = Btn("OK", 228, 84, Accent); ok.DialogResult = DialogResult.OK;
            var ca = Btn("Cancel", 324, 84, null); ca.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { l, t, ok, ca }); f.AcceptButton = ok; f.CancelButton = ca;
            return f.ShowDialog(Root.FindForm() ?? (IWin32Window)Root) == DialogResult.OK ? t.Text : null;
        }

        // ── Control factories ──────────────────────────────────────────────
        private Label Lbl(string text, int x, int y, bool bold = false, bool dim = false) => new()
        {
            Text = text, AutoSize = true, ForeColor = dim ? Sub : Fg, BackColor = Color.Transparent,
            Font = bold ? Font9B : Font9, Location = new Point(Sc(x), Sc(y)),
        };

        private CheckBox Chk(string text, int x, int y) => new()
        {
            Text = text, AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Font = Font9,
            Location = new Point(Sc(x), Sc(y)), Enabled = !_ro,
        };

        private TextBox Tb(int x, int y, int w) => new()
        {
            Location = new Point(Sc(x), Sc(y)), Width = Sc(w), BackColor = Field, ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, Font = Font9, ReadOnly = _ro,
        };

        private ComboBox Combo(int x, int y, int w)
        {
            var c = ModulePanelKit.Combo(_s, _ro, w);
            c.Location = new Point(Sc(x), Sc(y));
            return c;
        }

        private NumericUpDown Num(int v, int lo, int hi, int x, int y, int w) => new()
        {
            Minimum = lo, Maximum = hi, Value = Clamp(v, lo, hi), Location = new Point(Sc(x), Sc(y)), Width = Sc(w),
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = Font9, Enabled = !_ro,
        };

        private Button Btn(string text, int x, int y, Color? accent)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Font9,
                BackColor = accent ?? Field, ForeColor = accent.HasValue ? Color.White : Fg,
                Location = new Point(Sc(x), Sc(y)), Enabled = !_ro,
            };
            b.FlatAppearance.BorderColor = accent ?? Field;
            return b;
        }
    }

    // ── 3-colour state combo (Default / Customized / Untouched) ─────────────
    private enum RomCfgState { Default, Customized, Untouched }

    private sealed class RomStateItem
    {
        public string Text;
        public RomCfgState State;
        public RomStateItem(string t, RomCfgState s) { Text = t; State = s; }
        public override string ToString() => Text;
    }

    private sealed class RomStateCombo : ComboBox
    {
        public RomStateCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = ModulePanelKit.Field; ForeColor = ModulePanelKit.Fg;
            Font = new Font("Segoe UI", 9f);
            ItemHeight = 22;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0) return;
            var item = Items[e.Index] as RomStateItem;
            var text = item?.Text ?? Items[e.Index]?.ToString() ?? "";
            var good = Color.FromArgb(86, 186, 120);
            Color c = item == null ? ModulePanelKit.Fg : item.State switch
            {
                RomCfgState.Default => ModulePanelKit.Accent,
                RomCfgState.Customized => good,
                _ => ModulePanelKit.Sub,
            };
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? ModulePanelKit.Accent : ModulePanelKit.Field);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var fg = new SolidBrush(sel ? Color.White : c);
            var prefix = item?.State switch
            {
                RomCfgState.Default => "◆ ",
                RomCfgState.Customized => "● ",
                RomCfgState.Untouched => "○ ",
                _ => "",
            };
            e.Graphics.DrawString(prefix + text, Font, fg, e.Bounds.X + 3, e.Bounds.Y + 3);
        }
    }
}
