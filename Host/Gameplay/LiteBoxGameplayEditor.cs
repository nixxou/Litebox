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

/// <summary>Which concern of the per-game LiteBox options to render — they now live split across the
/// Startup and Pause override modals. null ⇒ all rows (Edit Emulator's single "LiteBox" section).</summary>
internal enum GameplaySection { Startup, Pause }

internal static class LiteBoxGameplayEditor
{
    /// <summary>Builds the per-entity LiteBox gameplay overrides panel + a save action (call on OK).
    /// <paramref name="only"/> limits the rows to one concern (Startup or Pause) for the per-game
    /// modals; null = all rows (Edit Emulator). AutoScrolls.</summary>
    public static (Panel panel, Action save) Build(string scope, string entityId, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly, GameplaySection? only = null)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = bg, AutoScroll = true };
        string what = scope == LiteBoxOption.ScopeGame ? "game" : "emulator";
        var saves = new System.Collections.Generic.List<Action>();
        bool wS = only is null or GameplaySection.Startup;
        bool wP = only is null or GameplaySection.Pause;

        Label Lab(string t, int y) => new() { Text = t, Location = new Point(S(12), S(y + 3)), AutoSize = true, ForeColor = fg, BackColor = bg };
        ComboBox Cbo(int y, int w) => new() { Location = new Point(S(280), S(y)), Width = S(w), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = panel2, ForeColor = fg, FlatStyle = FlatStyle.Flat, Enabled = !readOnly };

        p.Controls.Add(new Label { Text = $"These LiteBox-only options override the global defaults for this {what} only. " +
            "\"Use global\" inherits; stored separately from LaunchBox.", Location = new Point(S(12), S(6)), AutoSize = true,
            ForeColor = subFg, BackColor = bg, Font = new Font("Segoe UI", 8.25f) });

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

        int y = 40;   // running cursor: rows advance +32; unbuilt rows leave no gap

        if (wS)
        {
            // 1. Keep startup/end screens on top (bool tri-state).
            p.Controls.Add(Lab("Keep startup/end screens on top:", y));
            var stayGlobal = GameplaySettings.StartupStayOnTop();
            var stayCbo = Cbo(y - 2, 200);
            stayCbo.Items.AddRange(new object[] { $"Use global ({(stayGlobal ? "On" : "Off")})", "On", "Off" });
            var stayOv = LiteBoxOption.GetOverride(scope, entityId, "StartupStayOnTop");
            stayCbo.SelectedIndex = string.IsNullOrEmpty(stayOv) ? 0 : (string.Equals(stayOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            p.Controls.Add(stayCbo);
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "StartupStayOnTop", stayCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null }));
            y += 32;

            // 2. Exit / end screen early (int tri-state: Use global / Disabled / Custom ms).
            p.Controls.Add(Lab("Show exit/end screen early:", y));
            var eagerGlobal = GameplaySettings.ExitScreenEagerMsGlobal();
            var eagerCbo = Cbo(y - 2, 170);
            eagerCbo.Items.AddRange(new object[] { $"Use global ({(eagerGlobal < 0 ? "Off" : eagerGlobal + " ms")})", "Disabled", "Custom…" });
            var eagerOv = LiteBoxOption.GetOverride(scope, entityId, "ExitScreenEagerMs");
            int eagerOvNum = int.TryParse(eagerOv, out var _ev) ? _ev : int.MinValue;
            eagerCbo.SelectedIndex = string.IsNullOrEmpty(eagerOv) ? 0 : (eagerOvNum >= 0 ? 2 : 1);
            p.Controls.Add(eagerCbo);
            var eyy = y;
            var eagerMs = new NumericUpDown { Location = new Point(S(458), S(eyy - 2)), Width = S(70), Minimum = 0, Maximum = 10000, Increment = 100, BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Value = eagerOvNum >= 0 ? Math.Min(10000, eagerOvNum) : Math.Max(0, Math.Min(10000, eagerGlobal)), Visible = eagerCbo.SelectedIndex == 2, Enabled = !readOnly };
            p.Controls.Add(eagerMs);
            var eagerMsLbl = new Label { Text = "ms", Location = new Point(S(534), S(eyy + 1)), AutoSize = true, ForeColor = subFg, BackColor = bg, Visible = eagerCbo.SelectedIndex == 2 };
            p.Controls.Add(eagerMsLbl);
            eagerCbo.SelectedIndexChanged += (_, _) => { eagerMs.Visible = eagerMsLbl.Visible = eagerCbo.SelectedIndex == 2; };
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "ExitScreenEagerMs", eagerCbo.SelectedIndex switch { 1 => "-1", 2 => ((int)eagerMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null }));
            y += 32;
        }

        if (wP)
        {
            // 3. Pause hotkey (string tri-state).
            p.Controls.Add(Lab("Pause hotkey:", y));
            var pauseHk = HotkeyTri(y - 2, "PauseHotkey");
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "PauseHotkey", pauseHk.value()));
            y += 32;
            // 4. Screenshot hotkey (string tri-state).
            p.Controls.Add(Lab("Screenshot hotkey:", y));
            var capHk = HotkeyTri(y - 2, "ScreenCaptureKey");
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "ScreenCaptureKey", capHk.value()));
            y += 32;
            // 5. Controller pause enable (bool tri-state).
            p.Controls.Add(Lab("Pause with controller:", y));
            var padGlobalOn = GameplaySettings.PadPauseEnabled();
            var padEnCbo = Cbo(y - 2, 200);
            padEnCbo.Items.AddRange(new object[] { $"Use global ({(padGlobalOn ? "On" : "Off")})", "On", "Off" });
            var padEnOv = LiteBoxOption.GetOverride(scope, entityId, "PadPauseEnabled");
            padEnCbo.SelectedIndex = string.IsNullOrEmpty(padEnOv) ? 0 : (string.Equals(padEnOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            p.Controls.Add(padEnCbo);
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "PadPauseEnabled", padEnCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null }));
            y += 32;
            // 6. Controller pause button (preset tri-state).
            p.Controls.Add(Lab("Controller pause button:", y));
            var padBtnGlobal = GameplaySettings.PadPauseButton();
            var padBtnCbo = Cbo(y - 2, 200);
            padBtnCbo.Items.Add($"Use global ({padBtnGlobal})");
            padBtnCbo.Items.AddRange(Pause.XInputPad.ComboPresets);
            var padBtnOv = LiteBoxOption.GetOverride(scope, entityId, "PadPauseButton");
            padBtnCbo.SelectedIndex = string.IsNullOrEmpty(padBtnOv) ? 0
                : Math.Max(0, Array.IndexOf(Pause.XInputPad.ComboPresets, padBtnOv) + 1);   // +1: index 0 is "Use global"
            p.Controls.Add(padBtnCbo);
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "PadPauseButton", padBtnCbo.SelectedIndex <= 0 ? null : Pause.XInputPad.ComboPresets[padBtnCbo.SelectedIndex - 1]));
            y += 32;
            // 7. Pause screen vs. process freeze — timing (After / Before) tri-state.
            p.Controls.Add(Lab("Pause screen vs. freeze:", y));
            var frzGlobal = GameplaySettings.PauseScreenFreezeTiming();
            var frzCbo = Cbo(y - 2, 200);
            frzCbo.Items.AddRange(new object[] { $"Use global ({(frzGlobal.showBefore ? "Before" : "After")})", "After freezing", "Before freezing" });
            var frzOv = LiteBoxOption.GetOverride(scope, entityId, "PauseScreenFreezeTiming");
            frzCbo.SelectedIndex = string.IsNullOrEmpty(frzOv) ? 0 : (string.Equals(frzOv, "before", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            p.Controls.Add(frzCbo);
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "PauseScreenFreezeTiming", frzCbo.SelectedIndex switch { 1 => "after", 2 => "before", _ => null }));
            y += 32;
            // 8. Freeze ↔ screen delay (int tri-state: Use global / Custom ms) — only meaningful when suspended.
            p.Controls.Add(Lab("Freeze ↔ screen delay:", y));
            var offCbo = Cbo(y - 2, 170);
            offCbo.Items.AddRange(new object[] { $"Use global ({frzGlobal.offsetMs} ms)", "Custom…" });
            var offOv = LiteBoxOption.GetOverride(scope, entityId, "PauseScreenFreezeOffsetMs");
            int offOvNum = int.TryParse(offOv, out var _off) ? _off : int.MinValue;
            offCbo.SelectedIndex = string.IsNullOrEmpty(offOv) ? 0 : 1;
            p.Controls.Add(offCbo);
            var oyy = y;
            var offMs = new NumericUpDown { Location = new Point(S(458), S(oyy - 2)), Width = S(70), Minimum = 0, Maximum = 5000, Increment = 50, BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Value = offOvNum >= 0 ? Math.Min(5000, offOvNum) : Math.Max(0, Math.Min(5000, frzGlobal.offsetMs)), Visible = offCbo.SelectedIndex == 1, Enabled = !readOnly };
            p.Controls.Add(offMs);
            var offMsLbl = new Label { Text = "ms", Location = new Point(S(534), S(oyy + 1)), AutoSize = true, ForeColor = subFg, BackColor = bg, Visible = offCbo.SelectedIndex == 1 };
            p.Controls.Add(offMsLbl);
            offCbo.SelectedIndexChanged += (_, _) => { offMs.Visible = offMsLbl.Visible = offCbo.SelectedIndex == 1; };
            saves.Add(() => LiteBoxOption.SetOverride(scope, entityId, "PauseScreenFreezeOffsetMs", offCbo.SelectedIndex == 1 ? ((int)offMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
            y += 32;
        }

        if (wS)
        {
            // 9. Smart Capture (its own "Override for this entity" block).
            p.Controls.Add(new Label { Text = "Smart Capture — reveal the startup screen when the game actually renders:", Location = new Point(S(12), S(y + 2)), AutoSize = true, ForeColor = UiKit.LiteBoxFrame.Accent, BackColor = bg, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) });
            var (scPanel, scSave) = SmartCaptureEditor.Build(scope, entityId, s, bg, fg, subFg, panel2, readOnly);
            scPanel.Location = new Point(S(8), S(y + 24));
            scPanel.Size = new Size(S(652), S(340));
            p.Controls.Add(scPanel);
            saves.Add(scSave);
        }

        void Save() { if (readOnly) return; foreach (var a in saves) a(); }
        return (p, Save);
    }
}
