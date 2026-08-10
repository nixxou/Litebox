// Parental module config panel — native to LiteBox.
//
// Four groups, built through ModulePanelKit for the shared dark look:
//   • Activation    — the single parental enable switch, the shared PIN (+ confirm, reuses Host/Data/BigBoxPin —
//                     BigBox's own parental PIN), and the two "allow while locked" toggles (star / favorite).
//   • Filter rules  — Whitelist/Blacklist mode + a rating-pattern list (wildcards * and ?) with Add/Remove.
//   • Hide platforms — a hide-when-LOCKED and a hide-when-UNLOCKED list, each a platform combo
//                     (from PluginHelper.DataManager) + Add/Remove. Applies everywhere parental applies; the
//                     native filter enforces hide-when-LOCKED only (hide-when-UNLOCKED deferred for vanilla LB/BB).
//   • Lock control  — the pop-up hotkey (captured live) + Clear.
//
// Removed pending the vanilla-LB/BB revamp (WS5/WS6): the BigBox write-mode combo, "force web hide all",
// and "require PIN to install". Their ParentalConfig fields survive (still read elsewhere) at their
// defaults (Block / false / false) — the panel just no longer exposes them.
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
        Cap(gAct, "One switch — when on, parental control applies everywhere: the LiteBox desktop, the web "
                + "clients, and vanilla LaunchBox / BigBox (via the native filter installed below). The PIN is "
                + "BigBox's own parental code — set or clear it below; BigBox boots locked when a PIN is set.", 12, y1, 690); y1 += 46;

        var chkEnabled = Chk(gAct, "Enable parental control", 12, y1, cfg.Enabled); y1 += 26;

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

        // The two "allow while locked" toggles — now govern all three surfaces (LiteBox desktop,
        // BigBox-web, LaunchBox-web), not just the web (see ParentalWebWriteGuard + the desktop guard).
        Cap(gAct, "While locked, a limited user may still be allowed to change these — on the LiteBox "
                + "desktop AND the BigBox / LaunchBox web clients:", 12, y1, 690); y1 += 22;
        var chkAllowRatings = Chk(gAct, "Allow a locked user to change star ratings", 12, y1, cfg.AllowLockedModifyRatings); y1 += 24;
        var chkAllowFavorites = Chk(gAct, "Allow a locked user to change favorites", 12, y1, cfg.AllowLockedModifyFavorites); y1 += 30;

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
        // Group 3 — Hide platforms
        // ═════════════════════════════════════════════════════════════════════
        var platformNames = LoadPlatformNames();

        var gHide = Group("Hide platforms", rootY);
        int y3 = 24;
        Cap(gHide, "Remove whole platforms / categories from view — everywhere parental applies (LiteBox desktop, "
                 + "the web clients, and vanilla LaunchBox / BigBox via the native filter). The LOCKED list applies "
                 + "while parental is locked; the UNLOCKED list applies otherwise.", 12, y3, 690); y3 += 48;

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
        Cap(gHide, "Note: LiteBox and the web clients enforce this list. The native filter for vanilla "
                 + "LaunchBox / BigBox does NOT yet apply hide-when-unlocked (deferred).", 12, y3, 690); y3 += 34;
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
        // Capture the combo. A low-level hook (installed only while focused) grabs the press BEFORE the
        // app-wide HostHotKeys message filter — otherwise F10/F11/F12 (and the current parental key) get
        // swallowed by that filter and can never be re-bound. KeyDown stays as the fallback if the hook fails.
        void ApplyKey(Keys keyData)
        {
            if (readOnly) return;
            var code = keyData & Keys.KeyCode;
            if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;   // wait for a real key
            if (code == Keys.Escape) { SetHotKey(Keys.None); return; }
            SetHotKey(keyData);   // includes modifier flags
        }
        hotKeyBox.KeyDown += (s, e) => { e.SuppressKeyPress = true; e.Handled = true; ApplyKey(e.KeyData); };
        KeyCaptureHook.OnFocus(hotKeyBox, ApplyKey);
        clearHot.Click += (_, _) => { if (!readOnly) SetHotKey(Keys.None); };
        y4 += 32;
        CloseGroup(gLock, y4);

        // ═════════════════════════════════════════════════════════════════════
        // Group 5 — Vanilla LaunchBox / BigBox (native filter install)
        // ═════════════════════════════════════════════════════════════════════
        var gNative = Group("Vanilla LaunchBox / BigBox", rootY);
        int y5 = 24;
        Cap(gNative, "Extend parental control to the real LaunchBox and BigBox apps by installing a native filter "
                + "(an ASI loader + a write-guard plugin). The read-filter never runs unless the write-guard is "
                + "present, so it cannot lose games. Takes effect on the next LaunchBox / BigBox launch.", 12, y5, 690); y5 += 46;
        var installBtn = Btn(gNative, "Install", 12, y5, 110);
        var uninstallBtn = Btn(gNative, "Uninstall", 130, y5, 110);
        var nativeStatus = Cap(gNative, "", 252, y5 + 4, 430);
        void RefreshNative()
        {
            bool inst = ParentalNativeInstall.IsInstalled;
            bool payload = ParentalNativeInstall.PayloadAvailable;
            // Supported on both net9 (LB 13.27) and net10 (13.28+): LiteBox ships a guard build per runtime and
            // deploys the one matching Core (self-healing on an LB upgrade — see ParentalNativeInstall).
            nativeStatus.Text = inst ? "Installed." : payload ? "Not installed." : "Payload missing — cannot install.";
            installBtn.Text = inst ? "Reinstall" : "Install";
            installBtn.Enabled = !readOnly && payload;
            uninstallBtn.Enabled = !readOnly && inst;
        }
        installBtn.Click += (_, _) =>
        {
            var (ok, msg) = ParentalNativeInstall.Install(); RefreshNative();
            MessageBox.Show(msg, "LiteBox — Parental Control", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        };
        uninstallBtn.Click += (_, _) =>
        {
            var (ok, msg) = ParentalNativeInstall.Uninstall(); RefreshNative();
            MessageBox.Show(msg, "LiteBox — Parental Control", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        };
        RefreshNative();
        y5 += 34;
        CloseGroup(gNative, y5);

        // ── Enable-dependent field states ───────────────────────────────────
        void SyncEnabled()
        {
            bool anyScope = chkEnabled.Checked;
            pin.Enabled = pinConfirm.Enabled = showPin.Enabled = pinAvailable && !readOnly && anyScope;
            pinNote.Text = !pinAvailable
                ? "BigBoxSettings.xml was not found (BigBox has never run on this install) — the PIN cannot be set here."
                : BigBoxPin.Current().Length > 0
                    ? "A PIN is already set. Leave both boxes blank to keep it, or type a new one (twice) to change it."
                    : anyScope ? "Set a PIN (required to enable parental control) and confirm it." : "No PIN is currently set.";
        }
        chkEnabled.CheckedChanged += (_, _) => SyncEnabled();

        // Prefill the PIN boxes with the current PIN (so leaving them keeps it visible when "Show" is on).
        try { if (pinAvailable) { var cur = BigBoxPin.Current(); pin.Text = cur; pinConfirm.Text = cur; } } catch { }
        SyncEnabled();

        // ── Apply ────────────────────────────────────────────────────────────
        void Apply()
        {
            if (readOnly) return;

            bool anyScope = chkEnabled.Checked;

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
                        if (!BigBoxPin.Set(p))
                        { Warn("The PIN could not be written to BigBoxSettings.xml — close LaunchBox / BigBox (or disable read-only) and try again."); return; }
                    }
                    // p empty + hasPin → keep the existing PIN unchanged.
                }
                else
                {
                    // Parental fully disabled → clear the shared PIN. A refused clear (LB running /
                    // read-only) is reported — a PIN the user believes gone but still set is worse.
                    if (BigBoxPin.Current().Length > 0 && !BigBoxPin.Set(""))
                    { Warn("The PIN could not be cleared from BigBoxSettings.xml — close LaunchBox / BigBox (or disable read-only) and try again."); return; }
                }
            }

            // Persist the config. ForceWebHideAll / BigBoxWriteMode / BlockInstallWhenLocked are no longer
            // exposed (removed pending the vanilla-LB/BB revamp) — left untouched at their loaded defaults.
            cfg.Enabled = anyScope;
            cfg.AllowLockedModifyRatings = chkAllowRatings.Checked;
            cfg.AllowLockedModifyFavorites = chkAllowFavorites.Checked;
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
