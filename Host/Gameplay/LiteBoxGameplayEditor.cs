// Reusable per-entity LiteBox GAMEPLAY overrides editor — the LiteBox-only options (no LaunchBox
// field) rendered as tri-states over the global default, for one scope (emulator OR game). Same
// options as the global "LiteBox-Options" tab, but each is "Use global (<value>)" / an explicit
// override, stored under scope=entity in litebox-options.db (so LB never sees them and the resolution
// game → emulator → global — GameplaySettings — picks the tightest set at launch).
//
// Used by Edit Emulator (a dedicated "LiteBox" section) and Edit Game (a per-game overrides modal),
// so both surfaces share one implementation. Takes a LIST of entity ids: one for the single-entity
// surfaces, several for a multi-game selection — then each option MERGES across the games (a
// "‹multiple values›" combo item when they differ) and OK writes the pick to EVERY game.
// Web-return timing is deliberately absent: it is a process-global behaviour, not a per-entity setting.

#nullable enable

using System;
using System.Collections.Generic;
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
    private const string MultiItem = "‹multiple values›";
    private const string DiffMark = "diff";   // internal sentinel: the selected games' overrides differ

    public static (Panel panel, Action save) Build(string scope, string entityId, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly, GameplaySection? only = null)
        => Build(scope, new[] { entityId }, s, bg, fg, subFg, panel2, readOnly, only);

    /// <summary>Builds the per-entity LiteBox gameplay overrides panel + a save action (call on OK).
    /// Several ids ⇒ multi-select: each option merges across the games, and OK writes to all of them.</summary>
    public static (Panel panel, Action save) Build(string scope, IReadOnlyList<string> entityIds, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly, GameplaySection? only = null)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = bg, AutoScroll = true };
        string what = scope == LiteBoxOption.ScopeGame ? "game" : "emulator";
        bool multi = entityIds.Count > 1;
        string primaryId = entityIds.Count > 0 ? entityIds[0] : "";
        var saves = new List<Action>();
        bool wS = only is null or GameplaySection.Startup;
        bool wP = only is null or GameplaySection.Pause;

        // Merged override: the common value (null = none), or the DiffMark when the games disagree.
        string? GetOv(string key)
        {
            if (!multi) return LiteBoxOption.GetOverride(scope, primaryId, key);
            string? first = null;
            foreach (var id in entityIds)
            {
                var v = LiteBoxOption.GetOverride(scope, id, key) ?? "";
                if (first == null) first = v;
                else if (!string.Equals(first, v, StringComparison.Ordinal)) return DiffMark;
            }
            return string.IsNullOrEmpty(first) ? null : first;
        }
        void SetAll(string key, string? value) { foreach (var id in entityIds) LiteBoxOption.SetOverride(scope, id, key, value); }

        // When the games differ on a combo, append "‹multiple values›" and select it (hiding any custom
        // sub-controls). Returns a predicate the save uses to SKIP when the user left it on that item.
        Func<bool> Multied(ComboBox cbo, string? ovRaw, params Control?[] hideIfDiff)
        {
            if (ovRaw == DiffMark)
            {
                cbo.Items.Add(MultiItem);
                cbo.SelectedIndex = cbo.Items.Count - 1;
                foreach (var h in hideIfDiff) if (h != null) h.Visible = false;
            }
            return () => (cbo.SelectedItem as string) == MultiItem;
        }

        Label Lab(string t, int y) => new() { Text = t, Location = new Point(S(12), S(y + 3)), AutoSize = true, ForeColor = fg, BackColor = bg };
        ComboBox Cbo(int y, int w) => new() { Location = new Point(S(280), S(y)), Width = S(w), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = panel2, ForeColor = fg, FlatStyle = FlatStyle.Flat, Enabled = !readOnly };

        p.Controls.Add(new Label { Text = $"These LiteBox-only options override the global defaults for this {what} only. " +
            "\"Use global\" inherits; stored separately from LaunchBox." + (multi ? "  Across a selection, differing games show ‹multiple values›." : ""),
            Location = new Point(S(12), S(6)), AutoSize = true, ForeColor = subFg, BackColor = bg, Font = new Font("Segoe UI", 8.25f) });

        // string tri-state (Use global / Disabled / Custom + capture box) for a hotkey option.
        (ComboBox cbo, HotkeyCaptureBox box, Func<string?> value, Func<bool> skip) HotkeyTri(int y, string optKey)
        {
            var glob = optKey == "PauseHotkey" ? GameplaySettings.PauseKey() : GameplaySettings.ScreenCaptureKey();
            var ovRaw = GetOv(optKey);
            var ov = ovRaw == DiffMark ? null : ovRaw;
            bool custom = !string.IsNullOrEmpty(ov) && ov != LiteBoxOption.Disabled;
            var cbo = Cbo(y, 170);
            cbo.Items.AddRange(new object[] { $"Use global ({(string.IsNullOrEmpty(glob) ? "Off" : glob)})", "Disabled", "Custom…" });
            cbo.SelectedIndex = string.IsNullOrEmpty(ov) ? 0 : (ov == LiteBoxOption.Disabled ? 1 : 2);
            var box = new HotkeyCaptureBox(custom ? ov! : "") { Location = new Point(S(458), S(y)), Width = S(150), BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Visible = custom, Enabled = !readOnly };
            p.Controls.Add(cbo); p.Controls.Add(box);
            cbo.SelectedIndexChanged += (_, _) => box.Visible = cbo.SelectedIndex == 2 && (cbo.SelectedItem as string) != MultiItem;
            var skip = Multied(cbo, ovRaw, box);
            return (cbo, box, () => cbo.SelectedIndex switch
            {
                1 => LiteBoxOption.Disabled,
                2 => string.IsNullOrWhiteSpace(box.HotkeyValue) ? null : box.HotkeyValue,
                _ => null,
            }, skip);
        }

        int y = 40;   // running cursor: rows advance +32; unbuilt rows leave no gap

        if (wS)
        {
            // 1. Keep startup/end screens on top (bool tri-state).
            p.Controls.Add(Lab("Keep startup/end screens on top:", y));
            var stayGlobal = GameplaySettings.StartupStayOnTop();
            var stayCbo = Cbo(y - 2, 200);
            stayCbo.Items.AddRange(new object[] { $"Use global ({(stayGlobal ? "On" : "Off")})", "On", "Off" });
            var stayRaw = GetOv("StartupStayOnTop"); var stayOv = stayRaw == DiffMark ? null : stayRaw;
            stayCbo.SelectedIndex = string.IsNullOrEmpty(stayOv) ? 0 : (string.Equals(stayOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            p.Controls.Add(stayCbo);
            var staySkip = Multied(stayCbo, stayRaw);
            saves.Add(() => { if (staySkip()) return; SetAll("StartupStayOnTop", stayCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null }); });
            y += 32;

            // 2. Exit / end screen early (int tri-state: Use global / Disabled / Custom ms).
            p.Controls.Add(Lab("Show exit/end screen early:", y));
            var eagerGlobal = GameplaySettings.ExitScreenEagerMsGlobal();
            var eagerCbo = Cbo(y - 2, 170);
            eagerCbo.Items.AddRange(new object[] { $"Use global ({(eagerGlobal < 0 ? "Off" : eagerGlobal + " ms")})", "Disabled", "Custom…" });
            var eagerRaw = GetOv("ExitScreenEagerMs"); var eagerOv = eagerRaw == DiffMark ? null : eagerRaw;
            int eagerOvNum = int.TryParse(eagerOv, out var _ev) ? _ev : int.MinValue;
            eagerCbo.SelectedIndex = string.IsNullOrEmpty(eagerOv) ? 0 : (eagerOvNum >= 0 ? 2 : 1);
            p.Controls.Add(eagerCbo);
            var eyy = y;
            var eagerMs = new NumericUpDown { Location = new Point(S(458), S(eyy - 2)), Width = S(70), Minimum = 0, Maximum = 10000, Increment = 100, BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Value = eagerOvNum >= 0 ? Math.Min(10000, eagerOvNum) : Math.Max(0, Math.Min(10000, eagerGlobal)), Visible = eagerCbo.SelectedIndex == 2, Enabled = !readOnly };
            p.Controls.Add(eagerMs);
            var eagerMsLbl = new Label { Text = "ms", Location = new Point(S(534), S(eyy + 1)), AutoSize = true, ForeColor = subFg, BackColor = bg, Visible = eagerCbo.SelectedIndex == 2 };
            p.Controls.Add(eagerMsLbl);
            eagerCbo.SelectedIndexChanged += (_, _) => { bool cust = eagerCbo.SelectedIndex == 2 && (eagerCbo.SelectedItem as string) != MultiItem; eagerMs.Visible = eagerMsLbl.Visible = cust; };
            var eagerSkip = Multied(eagerCbo, eagerRaw, eagerMs, eagerMsLbl);
            saves.Add(() => { if (eagerSkip()) return; SetAll("ExitScreenEagerMs", eagerCbo.SelectedIndex switch { 1 => "-1", 2 => ((int)eagerMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null }); });
            y += 32;
        }

        if (wP)
        {
            // 3. Pause hotkey (string tri-state).
            p.Controls.Add(Lab("Pause hotkey:", y));
            var pauseHk = HotkeyTri(y - 2, "PauseHotkey");
            saves.Add(() => { if (pauseHk.skip()) return; SetAll("PauseHotkey", pauseHk.value()); });
            y += 32;
            // 4. Screenshot hotkey (string tri-state).
            p.Controls.Add(Lab("Screenshot hotkey:", y));
            var capHk = HotkeyTri(y - 2, "ScreenCaptureKey");
            saves.Add(() => { if (capHk.skip()) return; SetAll("ScreenCaptureKey", capHk.value()); });
            y += 32;
            // 5. Controller pause enable (bool tri-state).
            p.Controls.Add(Lab("Pause with controller:", y));
            var padGlobalOn = GameplaySettings.PadPauseEnabled();
            var padEnCbo = Cbo(y - 2, 200);
            padEnCbo.Items.AddRange(new object[] { $"Use global ({(padGlobalOn ? "On" : "Off")})", "On", "Off" });
            var padEnRaw = GetOv("PadPauseEnabled"); var padEnOv = padEnRaw == DiffMark ? null : padEnRaw;
            padEnCbo.SelectedIndex = string.IsNullOrEmpty(padEnOv) ? 0 : (string.Equals(padEnOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            p.Controls.Add(padEnCbo);
            var padEnSkip = Multied(padEnCbo, padEnRaw);
            saves.Add(() => { if (padEnSkip()) return; SetAll("PadPauseEnabled", padEnCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null }); });
            y += 32;
            // 6. Controller pause button (preset tri-state).
            p.Controls.Add(Lab("Controller pause button:", y));
            var padBtnGlobal = GameplaySettings.PadPauseButton();
            var padBtnCbo = Cbo(y - 2, 200);
            padBtnCbo.Items.Add($"Use global ({padBtnGlobal})");
            padBtnCbo.Items.AddRange(Pause.XInputPad.ComboPresets);
            var padBtnRaw = GetOv("PadPauseButton"); var padBtnOv = padBtnRaw == DiffMark ? null : padBtnRaw;
            padBtnCbo.SelectedIndex = string.IsNullOrEmpty(padBtnOv) ? 0
                : Math.Max(0, Array.IndexOf(Pause.XInputPad.ComboPresets, padBtnOv) + 1);   // +1: index 0 is "Use global"
            p.Controls.Add(padBtnCbo);
            var padBtnSkip = Multied(padBtnCbo, padBtnRaw);
            saves.Add(() => { if (padBtnSkip()) return; SetAll("PadPauseButton", padBtnCbo.SelectedIndex <= 0 ? null : Pause.XInputPad.ComboPresets[padBtnCbo.SelectedIndex - 1]); });
            y += 32;
            // 7. Pause screen vs. process freeze — timing (After / Before) tri-state.
            p.Controls.Add(Lab("Pause screen vs. freeze:", y));
            var frzGlobal = GameplaySettings.PauseScreenFreezeTiming();
            var frzCbo = Cbo(y - 2, 200);
            frzCbo.Items.AddRange(new object[] { $"Use global ({(frzGlobal.showBefore ? "Before" : "After")})", "After freezing", "Before freezing" });
            var frzRaw = GetOv("PauseScreenFreezeTiming"); var frzOv = frzRaw == DiffMark ? null : frzRaw;
            frzCbo.SelectedIndex = string.IsNullOrEmpty(frzOv) ? 0 : (string.Equals(frzOv, "before", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            p.Controls.Add(frzCbo);
            var frzSkip = Multied(frzCbo, frzRaw);
            saves.Add(() => { if (frzSkip()) return; SetAll("PauseScreenFreezeTiming", frzCbo.SelectedIndex switch { 1 => "after", 2 => "before", _ => null }); });
            y += 32;
            // 8. Freeze ↔ screen delay (int tri-state: Use global / Custom ms) — only meaningful when suspended.
            p.Controls.Add(Lab("Freeze ↔ screen delay:", y));
            var offCbo = Cbo(y - 2, 170);
            offCbo.Items.AddRange(new object[] { $"Use global ({frzGlobal.offsetMs} ms)", "Custom…" });
            var offRaw = GetOv("PauseScreenFreezeOffsetMs"); var offOv = offRaw == DiffMark ? null : offRaw;
            int offOvNum = int.TryParse(offOv, out var _off) ? _off : int.MinValue;
            offCbo.SelectedIndex = string.IsNullOrEmpty(offOv) ? 0 : 1;
            p.Controls.Add(offCbo);
            var oyy = y;
            var offMs = new NumericUpDown { Location = new Point(S(458), S(oyy - 2)), Width = S(70), Minimum = 0, Maximum = 5000, Increment = 50, BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Value = offOvNum >= 0 ? Math.Min(5000, offOvNum) : Math.Max(0, Math.Min(5000, frzGlobal.offsetMs)), Visible = offCbo.SelectedIndex == 1, Enabled = !readOnly };
            p.Controls.Add(offMs);
            var offMsLbl = new Label { Text = "ms", Location = new Point(S(534), S(oyy + 1)), AutoSize = true, ForeColor = subFg, BackColor = bg, Visible = offCbo.SelectedIndex == 1 };
            p.Controls.Add(offMsLbl);
            offCbo.SelectedIndexChanged += (_, _) => { bool cust = offCbo.SelectedIndex == 1 && (offCbo.SelectedItem as string) != MultiItem; offMs.Visible = offMsLbl.Visible = cust; };
            var offSkip = Multied(offCbo, offRaw, offMs, offMsLbl);
            saves.Add(() => { if (offSkip()) return; SetAll("PauseScreenFreezeOffsetMs", offCbo.SelectedIndex == 1 ? ((int)offMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : null); });
            y += 32;
            // 9. Pause freeze TARGET (string tri-state): the SmartCapture game window's owner vs the launched process.
            p.Controls.Add(Lab("Pause freeze target:", y));
            var tgtGlobal = GameplaySettings.PauseTargetGlobal();
            var tgtCbo = Cbo(y - 2, 200);
            tgtCbo.Items.AddRange(new object[] { $"Use global ({(string.Equals(tgtGlobal, "process", StringComparison.OrdinalIgnoreCase) ? "Emulator/app" : "SmartCapture")})", "SmartCapture game", "Emulator / app" });
            var tgtRaw = GetOv("PauseTarget"); var tgtOv = tgtRaw == DiffMark ? null : tgtRaw;
            tgtCbo.SelectedIndex = string.IsNullOrEmpty(tgtOv) ? 0 : (string.Equals(tgtOv, "process", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            p.Controls.Add(tgtCbo);
            var tgtSkip = Multied(tgtCbo, tgtRaw);
            saves.Add(() => { if (tgtSkip()) return; SetAll("PauseTarget", tgtCbo.SelectedIndex switch { 1 => "smartcapture", 2 => "process", _ => null }); });
            y += 32;
            // 10. Freeze whole process TREE (bool tri-state).
            p.Controls.Add(Lab("Freeze whole process tree:", y));
            var treeGlobal = GameplaySettings.PauseFreezeTreeGlobal();
            var treeCbo = Cbo(y - 2, 200);
            treeCbo.Items.AddRange(new object[] { $"Use global ({(treeGlobal ? "On" : "Off")})", "On", "Off" });
            var treeRaw = GetOv("PauseFreezeTree"); var treeOv = treeRaw == DiffMark ? null : treeRaw;
            treeCbo.SelectedIndex = string.IsNullOrEmpty(treeOv) ? 0 : (string.Equals(treeOv, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            p.Controls.Add(treeCbo);
            var treeSkip = Multied(treeCbo, treeRaw);
            saves.Add(() => { if (treeSkip()) return; SetAll("PauseFreezeTree", treeCbo.SelectedIndex switch { 1 => "true", 2 => "false", _ => null }); });
            y += 32;
        }

        if (wS)
        {
            // 9. Smart Capture (its own "Override for this entity" block) — multi-aware (the master checkbox is
            //    3-state and the block is applied atomically across the selection).
            p.Controls.Add(new Label { Text = "Smart Capture — reveal the startup screen when the game actually renders:", Location = new Point(S(12), S(y + 2)), AutoSize = true, ForeColor = UiKit.LiteBoxFrame.Accent, BackColor = bg, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) });
            var (scPanel, scSave) = SmartCaptureEditor.Build(scope, entityIds, s, bg, fg, subFg, panel2, readOnly);
            scPanel.Location = new Point(S(8), S(y + 24));
            scPanel.Size = new Size(S(652), S(340));
            p.Controls.Add(scPanel);
            saves.Add(scSave);
        }

        void Save() { if (readOnly) return; foreach (var a in saves) a(); }
        return (p, Save);
    }
}
