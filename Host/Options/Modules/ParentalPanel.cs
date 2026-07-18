// Parental module config panel — full parity with ExtendDB's "Parental control" tab, native to LiteBox.
//
// Four groups, built through ModulePanelKit for the shared ExtendDB-like dark look:
//   • Activation    — enable for LaunchBox / BigBox, the shared PIN (+ confirm, reuses Host/Data/BigBoxPin —
//                     BigBox's own parental PIN), BigBox write-mode, the two "allow while locked" toggles,
//                     force-web (hide ALL games), and the "require PIN to install store games" gate.
//   • Filter rules  — Whitelist/Blacklist mode + a rating-pattern list (wildcards * and ?) with Add/Remove.
//   • Hide platforms (BigBox) — a hide-when-LOCKED and a hide-when-UNLOCKED list, each a platform combo
//                     (from PluginHelper.DataManager) + Add/Remove.
//   • Lock control  — the pop-up hotkey (captured live) + Clear.
//
// Scalars persist to LiteBox.ini [Parental]; the three lists to parental-lists.json — both via
// ParentalConfig.Save(). The PIN persists through BigBoxPin.Set(). After a save the panel calls
// ParentalFilter.NotifyConfigChanged() so the host tree/list filters and the web frontends re-read at once.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Parental;
using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Host.Options;

internal static class ParentalPanel
{
    /// <summary>The panel shown while parental is LOCKED: no settings, just an Unlock path.</summary>
    private static (Control panel, Action? apply) LockedStub(float dpiS)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = ModulePanelKit.Root(dpiS);

        var head = ModulePanelKit.Header("Parental control — locked", dpiS);
        head.Location = new Point(S(8), S(12));
        root.Controls.Add(head);

        var cap = ModulePanelKit.Caption(
            "The parental filter is active and locked. Unlock it with the PIN to view or change these "
            + "settings (padlock button, hotkey, or the button below).", dpiS, 640);
        cap.Location = new Point(S(8), S(40));
        root.Controls.Add(cap);

        var btn = ModulePanelKit.Button("Unlock…", dpiS);
        btn.Location = new Point(S(8), S(84));
        var note = ModulePanelKit.Caption("", dpiS, 640);
        note.Location = new Point(S(8), S(116));
        root.Controls.Add(note);
        btn.Click += (_, _) =>
        {
            try { Media.ParentalBridge.ShowLockDialog(btn.FindForm()); } catch { }
            note.Text = ParentalFilter.Active
                ? "Still locked."
                : "Unlocked — close and reopen Options to edit the parental settings.";
        };
        root.Controls.Add(btn);

        return (root, null);
    }

    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        // PIN gate (plugin parity): while parental is ACTIVE (configured + locked) the config surface
        // is protected — otherwise a locked user could read/change the rules, the lists or the PIN.
        if (ParentalFilter.Active && ParentalFilter.HasPin)
            return LockedStub(dpiS);

        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var Bg = ModulePanelKit.Bg; var Fg = ModulePanelKit.Fg; var Sub = ModulePanelKit.Sub; var Field = ModulePanelKit.Field;

        var root = ModulePanelKit.Root(dpiS);
        var cfg = ParentalConfig.Instance;

        const int GroupWidth = 720;   // logical px; scaled by S()
        int rootY = 6;                // running Y cursor in the root, logical px

        // A group with a running inner-Y cursor. Caller adds children at (x, InnerY), then Close() sizes it.
        GroupBox Group(string title, int y)
        {
            var g = ModulePanelKit.Group(title, dpiS);
            g.Location = new Point(S(4), S(y));
            g.Width = S(GroupWidth);
            root.Controls.Add(g);
            return g;
        }
        void CloseGroup(GroupBox g, int innerHeight)
        {
            g.Height = S(innerHeight);
            rootY += innerHeight + 10;   // logical gap between groups
        }

        // ── Themed helpers (kit colors) ─────────────────────────────────────
        Label Cap(GroupBox g, string t, int x, int y, int w = 680)
        {
            var l = new Label { Text = t, AutoSize = true, MaximumSize = new Size(S(w), 0), ForeColor = Sub, BackColor = Bg, Location = new Point(S(x), S(y)), Font = new Font("Segoe UI", 8.5f) };
            g.Controls.Add(l); return l;
        }
        CheckBox Chk(GroupBox g, string t, int x, int y, bool val)
        {
            var c = ModulePanelKit.Check(t, dpiS, val, readOnly);
            c.Location = new Point(S(x), S(y));
            g.Controls.Add(c); return c;
        }
        Button Btn(GroupBox g, string t, int x, int y, int w = 90)
        {
            var b = ModulePanelKit.Button(t, dpiS, readOnly);
            b.AutoSize = false; b.Width = S(w); b.Height = S(24);
            b.Location = new Point(S(x), S(y));
            g.Controls.Add(b); return b;
        }
        ListBox List(GroupBox g, int x, int y, int w, int h)
        {
            var lb = new ListBox
            {
                Location = new Point(S(x), S(y)), Width = S(w), Height = S(h),
                BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f), IntegralHeight = false,
            };
            g.Controls.Add(lb); return lb;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Group 1 — Activation
        // ═════════════════════════════════════════════════════════════════════
        var gAct = Group("Activation", rootY);
        int y1 = 24;
        Cap(gAct, "Enable the parental filter for this LiteBox host (desktop + embedded web). The PIN is BigBox's "
                + "own parental code (BigBox boots locked when it is set) — but filtering INSIDE vanilla "
                + "LaunchBox/BigBox stays the ExtendDB plugin's job; LiteBox only manages the shared PIN.", 12, y1, 690); y1 += 46;

        var chkLaunchBox = Chk(gAct, "Enable Parental Control for LaunchBox", 12, y1, cfg.LaunchBoxEnabled); y1 += 26;

        // PIN + confirm (BigBoxPin) — shared credential.
        bool pinAvailable = BigBoxPin.Available;
        Cap(gAct, "PIN", 12, y1); Cap(gAct, "Confirm PIN", 160, y1); y1 += 18;
        var pin = ModulePanelKit.TextField(dpiS, readOnly || !pinAvailable, password: true, width: 120);
        pin.Location = new Point(S(12), S(y1)); pin.MaxLength = 8; gAct.Controls.Add(pin);
        var pinConfirm = ModulePanelKit.TextField(dpiS, readOnly || !pinAvailable, password: true, width: 120);
        pinConfirm.Location = new Point(S(160), S(y1)); pinConfirm.MaxLength = 8; gAct.Controls.Add(pinConfirm);
        var showPin = new CheckBox { Text = "Show", AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(300), S(y1 + 2)), Font = new Font("Segoe UI", 8.5f) };
        showPin.CheckedChanged += (_, _) => { pin.UseSystemPasswordChar = pinConfirm.UseSystemPasswordChar = !showPin.Checked; };
        gAct.Controls.Add(showPin); y1 += 30;
        var pinNote = Cap(gAct, "", 12, y1, 690); pinNote.Font = new Font("Segoe UI", 8.5f, FontStyle.Italic); y1 += 30;

        // Digits-only PIN entry (the web keypad / BigBox pop-up only enter digits).
        void DigitsOnly(object? s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; }
        pin.KeyPress += DigitsOnly; pinConfirm.KeyPress += DigitsOnly;

        var chkBigBox = Chk(gAct, "Enable for BigBox", 12, y1, cfg.BigBoxEnabled); y1 += 26;

        // BigBox write-mode.
        var lblWrite = Cap(gAct, "BigBox write-guard when a filtered library is saved", 12, y1); y1 += 18;
        var cmbWrite = ModulePanelKit.Combo(dpiS, readOnly, width: 260);
        cmbWrite.Location = new Point(S(12), S(y1));
        cmbWrite.Items.AddRange(new object[] { "Block writes (safe — never rewrite the library)", "Merge the filtered subset back in" });
        cmbWrite.SelectedIndex = cfg.BigBoxWriteMode == ParentalWriteMode.Merge ? 1 : 0;
        ThemeCombo(cmbWrite); gAct.Controls.Add(cmbWrite); y1 += 32;

        var chkAllowRatings = Chk(gAct, "Allow a locked user to change star ratings (web)", 12, y1, cfg.AllowLockedModifyRatings); y1 += 24;
        var chkAllowFavorites = Chk(gAct, "Allow a locked user to change favorites (web)", 12, y1, cfg.AllowLockedModifyFavorites); y1 += 24;
        var chkForceWeb = Chk(gAct, "Force Web — hide ALL games while locked (LaunchBox)", 12, y1, cfg.ForceWebHideAll); y1 += 20;
        Cap(gAct, "Supersedes the rules: an empty desktop list stops game media loading into RAM.", 30, y1, 690); y1 += 26;
        var chkBlockInstall = Chk(gAct, "Require the PIN to install store games while locked", 12, y1, cfg.BlockInstallWhenLocked); y1 += 30;

        CloseGroup(gAct, y1);

        // ═════════════════════════════════════════════════════════════════════
        // Group 2 — Filter rules
        // ═════════════════════════════════════════════════════════════════════
        var gRules = Group("Filter rules", rootY);
        int y2 = 24;
        Cap(gRules, "Mode", 12, y2); y2 += 18;
        var cmbMode = ModulePanelKit.Combo(dpiS, readOnly, width: 200);
        cmbMode.Location = new Point(S(12), S(y2));
        cmbMode.Items.AddRange(new object[] { "Whitelist (show only matching ratings)", "Blacklist (hide matching ratings)" });
        cmbMode.SelectedIndex = cfg.Mode == ParentalMode.Blacklist ? 1 : 0;
        ThemeCombo(cmbMode); gRules.Controls.Add(cmbMode); y2 += 34;

        Cap(gRules, "Rating patterns — wildcards * (any run) and ? (one char), matched against a game's rating (e.g. \"PEGI 18\", \"M\", \"Adult*\").", 12, y2, 690); y2 += 34;
        var lstRules = List(gRules, 12, y2, 360, 120);
        foreach (var r in cfg.Rules) if (!string.IsNullOrWhiteSpace(r)) lstRules.Items.Add(r.Trim());
        var newRule = ModulePanelKit.TextField(dpiS, readOnly, width: 200);
        newRule.Location = new Point(S(384), S(y2)); gRules.Controls.Add(newRule);
        var addRule = Btn(gRules, "Add", 384, y2 + 30);
        var removeRule = Btn(gRules, "Remove", 480, y2 + 30);

        void DoAddRule()
        {
            var v = (newRule.Text ?? "").Trim();
            if (v.Length == 0) return;
            foreach (var it in lstRules.Items) if (string.Equals(it?.ToString(), v, StringComparison.OrdinalIgnoreCase)) { lstRules.SelectedItem = it; newRule.Clear(); return; }
            lstRules.SelectedIndex = lstRules.Items.Add(v); newRule.Clear(); newRule.Focus();
        }
        addRule.Click += (_, _) => DoAddRule();
        removeRule.Click += (_, _) => RemoveSelected(lstRules);
        newRule.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoAddRule(); } };
        y2 += 130;
        CloseGroup(gRules, y2);

        // ═════════════════════════════════════════════════════════════════════
        // Group 3 — Hide platforms (BigBox)
        // ═════════════════════════════════════════════════════════════════════
        var platformNames = LoadPlatformNames();

        var gHide = Group("Hide platforms (BigBox)", rootY);
        int y3 = 24;
        Cap(gHide, "Remove platforms / categories from the BigBox filter page. The LOCKED list applies while parental is locked; the UNLOCKED list applies otherwise.", 12, y3, 690); y3 += 34;

        // Locked list.
        Cap(gHide, "Hide when LOCKED", 12, y3); y3 += 18;
        var cmbHideOn = ModulePanelKit.Combo(dpiS, readOnly, width: 300);
        cmbHideOn.Location = new Point(S(12), S(y3));
        foreach (var n in platformNames) cmbHideOn.Items.Add(n);
        if (cmbHideOn.Items.Count > 0) cmbHideOn.SelectedIndex = 0;
        ThemeCombo(cmbHideOn); gHide.Controls.Add(cmbHideOn);
        var addOn = Btn(gHide, "Add", 324, y3 - 1);
        var removeOn = Btn(gHide, "Remove", 420, y3 - 1); y3 += 30;
        var lstHideOn = List(gHide, 12, y3, 500, 90);
        foreach (var n in cfg.HiddenPlatformsBigBoxOn) if (!string.IsNullOrWhiteSpace(n)) lstHideOn.Items.Add(n.Trim());
        addOn.Click += (_, _) => AddFromCombo(cmbHideOn, lstHideOn);
        removeOn.Click += (_, _) => RemoveSelected(lstHideOn);
        y3 += 100;

        // Unlocked list.
        Cap(gHide, "Hide when UNLOCKED", 12, y3); y3 += 18;
        var cmbHideOff = ModulePanelKit.Combo(dpiS, readOnly, width: 300);
        cmbHideOff.Location = new Point(S(12), S(y3));
        foreach (var n in platformNames) cmbHideOff.Items.Add(n);
        if (cmbHideOff.Items.Count > 0) cmbHideOff.SelectedIndex = 0;
        ThemeCombo(cmbHideOff); gHide.Controls.Add(cmbHideOff);
        var addOff = Btn(gHide, "Add", 324, y3 - 1);
        var removeOff = Btn(gHide, "Remove", 420, y3 - 1); y3 += 30;
        var lstHideOff = List(gHide, 12, y3, 500, 90);
        foreach (var n in cfg.HiddenPlatformsBigBoxOff) if (!string.IsNullOrWhiteSpace(n)) lstHideOff.Items.Add(n.Trim());
        addOff.Click += (_, _) => AddFromCombo(cmbHideOff, lstHideOff);
        removeOff.Click += (_, _) => RemoveSelected(lstHideOff);
        y3 += 100;
        CloseGroup(gHide, y3);

        // ═════════════════════════════════════════════════════════════════════
        // Group 4 — Lock control
        // ═════════════════════════════════════════════════════════════════════
        var gLock = Group("Lock control", rootY);
        int y4 = 24;
        Cap(gLock, "A global hotkey that pops the lock/unlock dialog. Click the box and press the key combo; Esc clears it.", 12, y4, 690); y4 += 34;
        Cap(gLock, "Hotkey", 12, y4); y4 += 18;
        var hotKeyBox = ModulePanelKit.TextField(dpiS, readOnly: true, width: 220);
        hotKeyBox.Location = new Point(S(12), S(y4)); hotKeyBox.TabStop = !readOnly;
        gLock.Controls.Add(hotKeyBox);
        var clearHot = Btn(gLock, "Clear", 244, y4 - 1, 80);

        Keys hotKey = unchecked((Keys)cfg.HotKey);
        void SetHotKey(Keys k)
        {
            hotKey = k;
            hotKeyBox.Text = k == Keys.None ? "(none)" : new KeysConverter().ConvertToString(k) ?? "(none)";
        }
        SetHotKey(hotKey);
        hotKeyBox.KeyDown += (s, e) =>
        {
            e.SuppressKeyPress = true; e.Handled = true;
            if (readOnly) return;
            var code = e.KeyCode;
            if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;   // wait for a real key
            if (code == Keys.Escape) { SetHotKey(Keys.None); return; }
            SetHotKey(e.KeyData);   // includes modifier flags
        };
        clearHot.Click += (_, _) => { if (!readOnly) SetHotKey(Keys.None); };
        y4 += 32;
        CloseGroup(gLock, y4);

        // ── Enable-dependent field states ───────────────────────────────────
        void SyncEnabled()
        {
            bool anyScope = chkLaunchBox.Checked || chkBigBox.Checked;
            pin.Enabled = pinConfirm.Enabled = showPin.Enabled = pinAvailable && !readOnly && anyScope;
            bool lb = chkLaunchBox.Checked;
            chkForceWeb.Enabled = !readOnly && lb;
            if (!lb) chkForceWeb.Checked = false;
            bool bb = chkBigBox.Checked;
            lblWrite.Enabled = cmbWrite.Enabled = !readOnly && bb;
            pinNote.Text = !pinAvailable
                ? "BigBoxSettings.xml was not found (BigBox has never run on this install) — the PIN cannot be set here."
                : BigBoxPin.Current().Length > 0
                    ? "A PIN is already set. Leave both boxes blank to keep it, or type a new one (twice) to change it."
                    : anyScope ? "Set a PIN (required to enable parental control) and confirm it." : "No PIN is currently set.";
        }
        chkLaunchBox.CheckedChanged += (_, _) => SyncEnabled();
        chkBigBox.CheckedChanged += (_, _) => SyncEnabled();

        // Prefill the PIN boxes with the current PIN (so leaving them keeps it visible when "Show" is on).
        try { if (pinAvailable) { var cur = BigBoxPin.Current(); pin.Text = cur; pinConfirm.Text = cur; } } catch { }
        SyncEnabled();

        // ── Apply ────────────────────────────────────────────────────────────
        void Apply()
        {
            if (readOnly) return;

            bool lbOn = chkLaunchBox.Checked, bbOn = chkBigBox.Checked, anyScope = lbOn || bbOn;

            // PIN validation (mirrors ExtendDB): required on first enable, must match confirm when set.
            if (pinAvailable)
            {
                string p = (pin.Text ?? "").Trim(), c = (pinConfirm.Text ?? "").Trim();
                bool hasPin = BigBoxPin.Current().Length > 0;
                if (anyScope)
                {
                    if (!hasPin && p.Length == 0) { Warn("Enabling parental control requires a PIN. Enter a PIN and confirm it."); return; }
                    if (p.Length > 0)
                    {
                        if (p.Length != 4 || !p.All(char.IsAsciiDigit)) { Warn("BigBox PINs are exactly 4 digits."); return; }
                        if (p != c) { Warn("The PIN and its confirmation do not match."); return; }
                        BigBoxPin.Set(p);
                    }
                    // p empty + hasPin → keep the existing PIN unchanged.
                }
                else
                {
                    // Parental fully disabled → clear the shared PIN.
                    BigBoxPin.Set("");
                }
            }

            // Persist the config.
            cfg.LaunchBoxEnabled = lbOn;
            cfg.BigBoxEnabled = bbOn;
            cfg.ForceWebHideAll = lbOn && chkForceWeb.Checked;
            cfg.BigBoxWriteMode = cmbWrite.SelectedIndex == 1 ? ParentalWriteMode.Merge : ParentalWriteMode.Block;
            cfg.AllowLockedModifyRatings = chkAllowRatings.Checked;
            cfg.AllowLockedModifyFavorites = chkAllowFavorites.Checked;
            cfg.BlockInstallWhenLocked = chkBlockInstall.Checked;
            cfg.Mode = cmbMode.SelectedIndex == 1 ? ParentalMode.Blacklist : ParentalMode.Whitelist;
            cfg.HotKey = (int)hotKey;
            cfg.Rules = lstRules.Items.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => s.Trim().Length > 0).Select(s => s.Trim()).ToList();
            cfg.HiddenPlatformsBigBoxOn = lstHideOn.Items.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => s.Trim().Length > 0).Select(s => s.Trim()).ToList();
            cfg.HiddenPlatformsBigBoxOff = lstHideOff.Items.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => s.Trim().Length > 0).Select(s => s.Trim()).ToList();
            cfg.Save();

            // Re-read + re-apply everywhere (host tree/list filters, web frontends).
            ParentalFilter.NotifyConfigChanged();
        }

        return (root, Apply);
    }

    // ── Static helpers ──────────────────────────────────────────────────────

    /// <summary>Owner-draws a flat dark DropDownList combo so its dropdown items stay readable
    /// (a flat dark combo otherwise renders its open list black-on-black).</summary>
    private static void ThemeCombo(ComboBox combo)
    {
        if (combo.DropDownStyle != ComboBoxStyle.DropDownList) return;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += (sender, e) =>
        {
            var cb = (ComboBox)sender!;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color back = selected ? ModulePanelKit.Accent : ModulePanelKit.Field;
            Color fore = cb.Enabled ? ModulePanelKit.Fg : ModulePanelKit.Sub;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, e.Bounds, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
    }

    private static void AddFromCombo(ComboBox combo, ListBox list)
    {
        var name = combo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var it in list.Items) if (string.Equals(it?.ToString(), name, StringComparison.OrdinalIgnoreCase)) { list.SelectedItem = it; return; }
        list.SelectedIndex = list.Items.Add(name);
    }

    private static void RemoveSelected(ListBox list)
    {
        int idx = list.SelectedIndex;
        if (idx < 0) return;
        list.Items.RemoveAt(idx);
        if (list.Items.Count > 0) list.SelectedIndex = Math.Min(idx, list.Items.Count - 1);
    }

    /// <summary>The union of every platform and platform-category name in the library, sorted
    /// case-insensitively. Empty when the DataManager can't be reached.</summary>
    private static List<string> LoadPlatformNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dm = PluginHelper.DataManager;
            if (dm != null)
            {
                foreach (var p in dm.GetAllPlatforms() ?? Array.Empty<Unbroken.LaunchBox.Plugins.Data.IPlatform>())
                    if (!string.IsNullOrWhiteSpace(p?.Name)) names.Add(p!.Name);
                foreach (var c in dm.GetAllPlatformCategories() ?? Array.Empty<Unbroken.LaunchBox.Plugins.Data.IPlatformCategory>())
                    if (!string.IsNullOrWhiteSpace(c?.Name)) names.Add(c!.Name);
            }
        }
        catch { }
        return names.ToList();
    }

    private static void Warn(string message)
        => MessageBox.Show(message, "LiteBox — Parental Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
