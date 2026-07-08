// Reusable per-entity LiteBox GAMEPLAY overrides editor — the LiteBox-only options (no LaunchBox
// field) rendered as tri-states over the global default, for one scope (emulator OR game). Same
// options as the global "LiteBox-Options" tab, but each is "Use global (<value>)" / an explicit
// override, stored under scope=entity in litebox-options.db (so LB never sees them and the resolution
// game → emulator → global — GameplaySettings — picks the tightest set at launch).
//
// Used by Edit Emulator (a dedicated "LiteBox" section) and Edit Game (a per-game overrides modal),
// so both surfaces share one implementation. Web-return timing is deliberately absent: it is a
// process-global behaviour (when the kiosk reappears), not a per-entity setting.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Options;   // HotkeyCaptureBox

namespace LbApiHost.Host.Gameplay;

internal static class LiteBoxGameplayEditor
{
    /// <summary>Builds the per-entity LiteBox gameplay overrides panel + a save action (call on OK).
    /// The panel is fixed-height (~520px scaled) and AutoScrolls.</summary>
    public static (Panel panel, Action save) Build(string scope, string entityId, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = bg, AutoScroll = true };
        string what = scope == LiteBoxOption.ScopeGame ? "game" : "emulator";

        Label Lab(string t, int y) => new() { Text = t, Location = new Point(S(12), S(y + 3)), AutoSize = true, ForeColor = fg, BackColor = bg };
        ComboBox Cbo(int y, int w) => new() { Location = new Point(S(280), S(y)), Width = S(w), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = panel2, ForeColor = fg, FlatStyle = FlatStyle.Flat, Enabled = !readOnly };

        p.Controls.Add(new Label { Text = $"These LiteBox-only options override the global defaults for this {what} only. " +
            "\"Use global\" inherits; stored separately from LaunchBox.", Location = new Point(S(12), S(6)), AutoSize = true,
            ForeColor = subFg, BackColor = bg, Font = new Font("Segoe UI", 8.25f) });

        // 1. Keep startup/end screens on top (bool tri-state).
        p.Controls.Add(Lab("Keep startup/end screens on top:", 40));
        var stayGlobal = GameplaySettings.StartupStayOnTop();
        var stayCbo = Cbo(38, 200);
        stayCbo.Items.AddRange(new object[] { $"Use global ({(stayGlobal ? "On" : "Off")})", "On", "Off" });
        var stayOv = LiteBoxOption.GetOverride(scope, entityId, "StartupStayOnTop");
        stayCbo.SelectedIndex = string.IsNullOrEmpty(stayOv) ? 0 : (string.Equals(stayOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        p.Controls.Add(stayCbo);

        // 2. Exit / end screen early (int tri-state: Use global / Disabled / Custom ms).
        p.Controls.Add(Lab("Show exit/end screen early:", 72));
        var eagerGlobal = GameplaySettings.ExitScreenEagerMsGlobal();
        var eagerCbo = Cbo(70, 170);
        eagerCbo.Items.AddRange(new object[] { $"Use global ({(eagerGlobal < 0 ? "Off" : eagerGlobal + " ms")})", "Disabled", "Custom…" });
        var eagerOv = LiteBoxOption.GetOverride(scope, entityId, "ExitScreenEagerMs");
        int eagerOvNum = int.TryParse(eagerOv, out var _ev) ? _ev : int.MinValue;
        eagerCbo.SelectedIndex = string.IsNullOrEmpty(eagerOv) ? 0 : (eagerOvNum >= 0 ? 2 : 1);
        p.Controls.Add(eagerCbo);
        var eagerMs = new NumericUpDown { Location = new Point(S(458), S(70)), Width = S(70), Minimum = 0, Maximum = 10000, Increment = 100, BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Value = eagerOvNum >= 0 ? Math.Min(10000, eagerOvNum) : Math.Max(0, Math.Min(10000, eagerGlobal)), Visible = eagerCbo.SelectedIndex == 2, Enabled = !readOnly };
        p.Controls.Add(eagerMs);
        var eagerMsLbl = new Label { Text = "ms", Location = new Point(S(534), S(73)), AutoSize = true, ForeColor = subFg, BackColor = bg, Visible = eagerCbo.SelectedIndex == 2 };
        p.Controls.Add(eagerMsLbl);
        eagerCbo.SelectedIndexChanged += (_, _) => { eagerMs.Visible = eagerMsLbl.Visible = eagerCbo.SelectedIndex == 2; };

        // string tri-state (Use global / Disabled / Custom + capture box) for a hotkey option.
        (ComboBox cbo, HotkeyCaptureBox box, Func<string?> value) HotkeyTri(int y, string optKey)
        {
            var glob = optKey == "PauseHotkey" ? GameplaySettings.PauseKey() : GameplaySettings.ScreenCaptureKey();
            var ov = LiteBoxOption.GetOverride(scope, entityId, optKey);
            bool custom = !string.IsNullOrEmpty(ov) && ov != LiteBoxOption.Disabled;
            var cbo = Cbo(y, 170);
            cbo.Items.AddRange(new object[] { $"Use global ({(string.IsNullOrEmpty(glob) ? "Off" : glob)})", "Disabled", "Custom…" });
            cbo.SelectedIndex = string.IsNullOrEmpty(ov) ? 0 : (ov == LiteBoxOption.Disabled ? 1 : 2);
            var box = new HotkeyCaptureBox(custom ? ov : "") { Location = new Point(S(458), S(y)), Width = S(150), BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Visible = custom, Enabled = !readOnly };
            p.Controls.Add(cbo); p.Controls.Add(box);
            cbo.SelectedIndexChanged += (_, _) => box.Visible = cbo.SelectedIndex == 2;
            return (cbo, box, () => cbo.SelectedIndex switch
            {
                1 => LiteBoxOption.Disabled,
                2 => string.IsNullOrWhiteSpace(box.HotkeyValue) ? null : box.HotkeyValue,
                _ => null,
            });
        }

        // 3. Pause hotkey (string tri-state).
        p.Controls.Add(Lab("Pause hotkey:", 104));
        var pauseHk = HotkeyTri(102, "PauseHotkey");
        // 4. Screenshot hotkey (string tri-state).
        p.Controls.Add(Lab("Screenshot hotkey:", 136));
        var capHk = HotkeyTri(134, "ScreenCaptureKey");

        // 5. Controller pause enable (bool tri-state).
        p.Controls.Add(Lab("Pause with controller:", 168));
        var padGlobalOn = GameplaySettings.PadPauseEnabled();
        var padEnCbo = Cbo(166, 200);
        padEnCbo.Items.AddRange(new object[] { $"Use global ({(padGlobalOn ? "On" : "Off")})", "On", "Off" });
        var padEnOv = LiteBoxOption.GetOverride(scope, entityId, "PadPauseEnabled");
        padEnCbo.SelectedIndex = string.IsNullOrEmpty(padEnOv) ? 0 : (string.Equals(padEnOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        p.Controls.Add(padEnCbo);

        // 6. Controller pause button (preset tri-state).
        p.Controls.Add(Lab("Controller pause button:", 200));
        var padBtnGlobal = GameplaySettings.PadPauseButton();
        var padBtnCbo = Cbo(198, 200);
        padBtnCbo.Items.Add($"Use global ({padBtnGlobal})");
        padBtnCbo.Items.AddRange(Pause.XInputPad.ComboPresets);
        var padBtnOv = LiteBoxOption.GetOverride(scope, entityId, "PadPauseButton");
        padBtnCbo.SelectedIndex = string.IsNullOrEmpty(padBtnOv) ? 0
            : Math.Max(0, Array.IndexOf(Pause.XInputPad.ComboPresets, padBtnOv) + 1);   // +1: index 0 is "Use global"
        p.Controls.Add(padBtnCbo);

        // 7. Smart Capture (its own "Override for this entity" block).
        p.Controls.Add(new Label { Text = "Smart Capture — reveal the startup screen when the game actually renders:", Location = new Point(S(12), S(234)), AutoSize = true, ForeColor = UiKit.LiteBoxFrame.Accent, BackColor = bg, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) });
        var (scPanel, scSave) = SmartCaptureEditor.Build(scope, entityId, s, bg, fg, subFg, panel2, readOnly);
        scPanel.Location = new Point(S(8), S(256));
        scPanel.Size = new Size(S(652), S(250));
        p.Controls.Add(scPanel);

        void Save()
        {
            if (readOnly) return;
            LiteBoxOption.SetOverride(scope, entityId, "StartupStayOnTop",
                stayCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null });
            LiteBoxOption.SetOverride(scope, entityId, "ExitScreenEagerMs",
                eagerCbo.SelectedIndex switch { 1 => "-1", 2 => ((int)eagerMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null });
            LiteBoxOption.SetOverride(scope, entityId, "PauseHotkey", pauseHk.value());
            LiteBoxOption.SetOverride(scope, entityId, "ScreenCaptureKey", capHk.value());
            LiteBoxOption.SetOverride(scope, entityId, "PadPauseEnabled",
                padEnCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null });
            LiteBoxOption.SetOverride(scope, entityId, "PadPauseButton",
                padBtnCbo.SelectedIndex <= 0 ? null : Pause.XInputPad.ComboPresets[padBtnCbo.SelectedIndex - 1]);
            scSave();
        }

        return (p, Save);
    }
}
