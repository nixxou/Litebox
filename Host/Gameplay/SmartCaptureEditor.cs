// Reusable "Smart Capture override" editor block, embedded per-emulator (Edit Emulator →
// LiteBox) and per-game (Edit Game → Smart Capture). One checkbox toggles the whole block:
// checked = this entity overrides Smart Capture (its values are stored under scope=entity in
// litebox-options.db); unchecked = inherit global (all rows cleared). The controls are seeded
// from the current override, else from the resolved GLOBAL config so the inherited values show.
//
// Multi-select: every control merges PER-FIELD across the games — 3-state checkboxes and
// "‹multiple values›" text/number boxes (grey) when they differ. OK writes only the fields the
// user actually changed, to all games; the "Override" master unchecked clears the whole block.

#nullable enable

using System;
using System.Collections.Generic;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Gameplay;

internal static class SmartCaptureEditor
{
    private const string MultiVal = "‹multiple values›";
    private static readonly StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static (Panel panel, Action save) Build(string scope, string entityId, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly)
        => Build(scope, new[] { entityId }, s, bg, fg, subFg, panel2, readOnly);

    /// <summary>Builds the override block for (scope, entityIds). Returns the panel to embed and a
    /// save action (call on OK). The panel is fixed-height (~330px scaled, AutoScroll).</summary>
    public static (Panel panel, Action save) Build(string scope, IReadOnlyList<string> entityIds, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = bg, AutoScroll = true };
        bool multi = entityIds.Count > 1;
        string primaryId = entityIds.Count > 0 ? entityIds[0] : "";
        var g = GameplaySettings.ResolveSmartCapture(null, null);   // global defaults for display

        // Per-game effective value of a key (its override, else the global), MERGED across the selection.
        string MergeS(string key, string glob)
        {
            if (!multi) { var v = LiteBoxOption.GetOverride(scope, primaryId, key); return string.IsNullOrEmpty(v) ? glob : v; }
            string? first = null;
            foreach (var id in entityIds) { var v = LiteBoxOption.GetOverride(scope, id, key) ?? glob; if (first == null) first = v; else if (!string.Equals(first, v)) return MultiVal; }
            return first ?? glob;
        }
        bool? MergeB(string key, bool glob)
        {
            bool? first = null;
            foreach (var id in entityIds) { var sv = LiteBoxOption.GetOverride(scope, id, key); bool b = string.IsNullOrEmpty(sv) ? glob : sv.Equals("true", OIC); if (first == null) first = b; else if (first != b) return null; }
            return first;
        }
        // Master "does the game override Smart Capture" merged (null = the games disagree → Indeterminate).
        bool? masterMerged;
        {
            bool? f = null; bool mix = false;
            foreach (var id in entityIds) { bool o = !string.IsNullOrEmpty(LiteBoxOption.GetOverride(scope, id, "SmartCaptureEnabled")); if (f == null) f = o; else if (f != o) { mix = true; break; } }
            masterMerged = mix ? null : f;
        }

        var toWrite = new List<(string key, Func<string?> get)>();   // multi save: key → value (null = leave each game)

        // ── control builders (dirty-tracked so untouched multi fields stay ‹multiple values›) ──
        var revBtns = new List<(Control c, Button b, Func<bool> mod)>();
        Button MkRev(Point loc, Action onClick)
        {
            var rb = new Button { Text = "↺", Size = new Size(S(16), S(16)), Location = loc, Visible = false, TabStop = false, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(92, 46, 42), ForeColor = Color.FromArgb(255, 180, 165), Font = new Font("Segoe UI Symbol", 8f), FlatAppearance = { BorderSize = 1 } };
            rb.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
            rb.Click += (_, _) => onClick();
            return rb;
        }

        // 3-state checkbox (multi) / plain (solo), with per-field merge + ↺.
        CheckBox Chk3(string t, string key, bool glob, int y, out Func<string?> get)
        {
            var cb = new CheckBox { Text = t, Location = new Point(S(8), S(y)), AutoSize = true, ForeColor = fg, BackColor = bg, Enabled = !readOnly };
            if (multi)
            {
                var m = MergeB(key, glob);
                cb.ThreeState = true;
                cb.CheckState = m.HasValue ? (m.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
                cb.AutoCheck = false;
                cb.Click += (_, _) => { if (!readOnly) cb.CheckState = cb.CheckState == CheckState.Checked ? CheckState.Unchecked : CheckState.Checked; };
                var baseCs = cb.CheckState;
                var rb = MkRev(new Point(S(8), S(y)), () => { cb.CheckState = baseCs; });   // repositioned after add
                cb.CheckStateChanged += (_, _) => { bool mod = cb.CheckState != baseCs; cb.ForeColor = mod ? Color.FromArgb(235, 150, 135) : fg; rb.Visible = mod && !readOnly; };
                p.Controls.Add(cb);
                rb.Location = new Point(cb.Right + S(4), S(y)); p.Controls.Add(rb); rb.BringToFront();
                get = () => cb.CheckState == baseCs ? null : (cb.Checked ? "true" : "false");
            }
            else
            {
                cb.Checked = string.Equals(MergeS(key, glob ? "true" : "false"), "true", OIC);
                p.Controls.Add(cb);
                get = () => cb.Checked ? "true" : "false";
            }
            return cb;
        }

        // text/number box with ‹multiple values› + ↺ (multi), optional digits-only filter.
        TextBox TxtF(string key, string glob, int x, int y, int w, bool digitsOnly, out Func<string?> get)
        {
            string merged = MergeS(key, glob);
            bool differ = multi && merged == MultiVal;
            var t = new TextBox { Text = merged, Location = new Point(S(x), S(y)), Width = S(w), BackColor = panel2, ForeColor = differ ? subFg : fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly };
            if (digitsOnly)
            {
                // ALWAYS block non-digits (no placeholder exception), and strip anything that slips in — so the
                // "‹multiple values›" placeholder gets fully replaced by the first digit, never mixed with letters.
                t.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
                t.TextChanged += (_, _) =>
                {
                    if (t.Text == MultiVal) return;
                    var d = new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(t.Text, char.IsDigit)));
                    if (d != t.Text) { int pos = t.SelectionStart; t.Text = d; t.SelectionStart = Math.Min(pos, t.Text.Length); }
                };
            }
            if (multi)
            {
                var baseTxt = merged;
                if (differ) t.Enter += (_, _) => { if (t.Text == MultiVal) t.SelectAll(); };
                Button? rb = null;
                void Paint() { bool mod = t.Text != baseTxt; t.ForeColor = t.Text == MultiVal ? subFg : (mod ? Color.FromArgb(235, 150, 135) : fg); if (rb != null) rb.Visible = mod && !readOnly; }
                rb = MkRev(new Point(S(x + w) + S(2), S(y)), () => { t.Text = baseTxt; });
                t.TextChanged += (_, _) => Paint();
                p.Controls.Add(t); p.Controls.Add(rb); rb.BringToFront();
                Paint();
                get = () => (t.Text != baseTxt && t.Text != MultiVal) ? t.Text.Trim() : null;
            }
            else { p.Controls.Add(t); get = () => t.Text.Trim(); }
            return t;
        }

        Label Lab(string t, int y, Color? c = null) => new() { Text = t, Location = new Point(S(8), S(y)), AutoSize = true, ForeColor = c ?? fg, BackColor = bg };
        Label Unit(string t, int x, int y) => new() { Text = t, Location = new Point(S(x), S(y)), AutoSize = true, ForeColor = fg, BackColor = bg };

        // ── Master override checkbox ──
        var ovr = new CheckBox { Text = $"Override Smart Capture for {(multi ? "these games" : scope == LiteBoxOption.ScopeGame ? "this game" : "this emulator")}", Location = new Point(S(8), S(8)), AutoSize = true, ForeColor = fg, BackColor = bg, Enabled = !readOnly };
        bool ovrTouched = false;
        if (multi)
        {
            ovr.ThreeState = true;
            ovr.CheckState = masterMerged.HasValue ? (masterMerged.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
            ovr.AutoCheck = false;
            var baseOvr = ovr.CheckState;
            ovr.Click += (_, _) => { if (!readOnly) ovr.CheckState = ovr.CheckState == CheckState.Checked ? CheckState.Unchecked : CheckState.Checked; };
            ovr.CheckStateChanged += (_, _) => { ovrTouched = ovr.CheckState != baseOvr; ovr.ForeColor = ovrTouched ? Color.FromArgb(235, 150, 135) : fg; };
        }
        else ovr.Checked = !string.IsNullOrEmpty(LiteBoxOption.GetOverride(scope, primaryId, "SmartCaptureEnabled"));
        p.Controls.Add(ovr);
        p.Controls.Add(Lab(multi ? "Unchecked = clear for all; ‹multiple values›/untouched fields leave each game as-is." : "Unchecked = use the global Smart Capture settings.", 30, subFg));

        var en = Chk3("Enable Smart Capture", "SmartCaptureEnabled", g.Enabled, 58, out var enGet);
        toWrite.Add(("SmartCaptureEnabled", enGet));

        p.Controls.Add(Lab("Title filter (wildcard) — optional, priority:", 88, subFg));
        var title = TxtF("SmartCaptureTitle", g.Title, 8, 108, 360, false, out var titleGet); toWrite.Add(("SmartCaptureTitle", titleGet));
        p.Controls.Add(Lab("If set, a window whose title matches IS the game — no matter the rest.", 132, subFg));

        p.Controls.Add(Lab("— otherwise, detect the window by: —", 160, subFg));

        var useFps = Chk3("Renders at ≥", "SmartCaptureUseFps", g.UseFps, 186, out var useFpsGet); toWrite.Add(("SmartCaptureUseFps", useFpsGet));
        var fps = TxtF("SmartCaptureMinFps", g.MinFps.ToString(), 130, 184, 55, true, out var fpsGet); toWrite.Add(("SmartCaptureMinFps", fpsGet));
        p.Controls.Add(Unit("fps for", 195, 187));
        var sus = TxtF("SmartCaptureSustainMs", g.SustainMs.ToString(), 245, 184, 55, true, out var susGet); toWrite.Add(("SmartCaptureSustainMs", susGet));
        p.Controls.Add(Unit("ms", 306, 187));

        var useSize = Chk3("Window covers ≥", "SmartCaptureUseSize", g.UseSize, 214, out var useSizeGet); toWrite.Add(("SmartCaptureUseSize", useSizeGet));
        var sz = TxtF("SmartCaptureMinSizePct", g.MinSizePct.ToString(), 150, 212, 55, true, out var szGet); toWrite.Add(("SmartCaptureMinSizePct", szGet));
        p.Controls.Add(Unit("% of the screen", 214, 215));

        // combine (AND/OR) — merged; ‹multiple values› ⇒ neither preselected, only written if the user picks one.
        var lblComb = Lab("When both are on, combine with:", 242);
        p.Controls.Add(lblComb);
        string combMerged = MergeS("SmartCaptureCombine", g.Combine);
        bool combDiffer = multi && combMerged == MultiVal;
        bool isOr = !combDiffer && combMerged.Equals("or", OIC);
        var rAnd = new RadioButton { Text = "AND", Location = new Point(S(230), S(240)), AutoSize = true, ForeColor = fg, BackColor = bg, Checked = !combDiffer && !isOr, Enabled = !readOnly };
        var rOr = new RadioButton { Text = "OR", Location = new Point(S(300), S(240)), AutoSize = true, ForeColor = fg, BackColor = bg, Checked = !combDiffer && isOr, Enabled = !readOnly };
        p.Controls.Add(rAnd); p.Controls.Add(rOr);
        if (combDiffer) { p.Controls.Add(new Label { Text = MultiVal, Location = new Point(S(360), S(240)), AutoSize = true, ForeColor = subFg, BackColor = bg }); }
        bool combTouched = false; rAnd.CheckedChanged += (_, _) => combTouched = true; rOr.CheckedChanged += (_, _) => combTouched = true;
        toWrite.Add(("SmartCaptureCombine", () => multi ? (combTouched ? (rOr.Checked ? "or" : "and") : null) : (rOr.Checked ? "or" : "and")));
        p.Controls.Add(Lab("Reveal ceiling = LB \"Startup Load Delay\" (Startup Screen tab).", 272, subFg));

        var stopWin = Chk3("End session when the game window closes", "SmartCaptureStopOnWindowClose", g.StopOnWindowClose, 296, out var stopGet); toWrite.Add(("SmartCaptureStopOnWindowClose", stopGet));

        // Enable cascade: the whole block follows the override checkbox (checked OR the multi square — editable
        // so the user can set differing games); each test's params follow their own checkbox.
        var block = new Control[] { en, title, useFps, fps, sus, useSize, sz, stopWin, rAnd, rOr, lblComb };
        void Sync()
        {
            bool on = !readOnly && ovr.CheckState != CheckState.Unchecked;
            foreach (var c in block) c.Enabled = on;
            if (on)
            {
                fps.Enabled = sus.Enabled = useFps.CheckState != CheckState.Unchecked;
                sz.Enabled = useSize.CheckState != CheckState.Unchecked;
                bool both = useFps.CheckState != CheckState.Unchecked && useSize.CheckState != CheckState.Unchecked;
                rAnd.Enabled = rOr.Enabled = lblComb.Enabled = both;
            }
        }
        ovr.CheckStateChanged += (_, _) => Sync();
        useFps.CheckStateChanged += (_, _) => Sync();
        useSize.CheckStateChanged += (_, _) => Sync();
        Sync();

        void Save()
        {
            if (readOnly) return;
            if (!multi)
            {
                if (!ovr.Checked) { foreach (var k in SmartCaptureConfig.Keys) LiteBoxOption.SetOverride(scope, primaryId, k, null); return; }
                foreach (var (key, get) in toWrite) LiteBoxOption.SetOverride(scope, primaryId, key, get());
                return;
            }
            // Multi: master turned OFF (definite) clears the whole block for all; else write only CHANGED fields.
            if (ovrTouched && ovr.CheckState == CheckState.Unchecked)
            {
                foreach (var id in entityIds) foreach (var k in SmartCaptureConfig.Keys) LiteBoxOption.SetOverride(scope, id, k, null);
                return;
            }
            foreach (var (key, get) in toWrite)
            {
                var v = get();
                if (v == null) continue;
                foreach (var id in entityIds) LiteBoxOption.SetOverride(scope, id, key, v);
            }
        }

        return (p, Save);
    }
}
