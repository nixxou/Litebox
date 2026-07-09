// Reusable "Smart Capture override" editor block, embedded per-emulator (Edit Emulator →
// LiteBox) and per-game (Edit Game → Smart Capture). One checkbox toggles the whole block:
// checked = this entity overrides Smart Capture (its values are stored under scope=entity in
// litebox-options.db); unchecked = inherit global (all rows cleared). The controls are seeded
// from the current override, else from the resolved GLOBAL config so the inherited values show.

#nullable enable

using LbApiHost.Host.Data;

namespace LbApiHost.Host.Gameplay;

internal static class SmartCaptureEditor
{
    /// <summary>Builds the override block for (scope, entityId). Returns the panel to embed and a
    /// save action (call on OK). The panel is fixed-height (~330px scaled, AutoScroll).</summary>
    public static (Panel panel, Action save) Build(string scope, string entityId, float s,
        Color bg, Color fg, Color subFg, Color panel2, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { BackColor = bg, AutoScroll = true };

        // Current override (SmartCaptureEnabled row = the block is overridden), else global effective.
        bool overridden = !string.IsNullOrEmpty(LiteBoxOption.GetOverride(scope, entityId, "SmartCaptureEnabled"));
        var g = GameplaySettings.ResolveSmartCapture(null, null);   // global defaults for display
        string Cur(string key, string glob) { var v = LiteBoxOption.GetOverride(scope, entityId, key); return string.IsNullOrEmpty(v) ? glob : v; }

        Label Lab(string t, int y, Color? c = null) => new() { Text = t, Location = new Point(S(8), S(y)), AutoSize = true, ForeColor = c ?? fg, BackColor = bg };
        CheckBox Chk(string t, bool v, int y) => new() { Text = t, Location = new Point(S(8), S(y)), AutoSize = true, ForeColor = fg, BackColor = bg, Checked = v, Enabled = !readOnly };
        TextBox Txt(string v, int x, int y, int w) => new() { Text = v, Location = new Point(S(x), S(y)), Width = S(w), BackColor = panel2, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Enabled = !readOnly };

        Label Unit(string t, int x, int y) => new() { Text = t, Location = new Point(S(x), S(y)), AutoSize = true, ForeColor = fg, BackColor = bg };
        RadioButton Rad(string t, int x, int y, bool on) => new() { Text = t, Location = new Point(S(x), S(y)), AutoSize = true, ForeColor = fg, BackColor = bg, Checked = on, Enabled = !readOnly };
        bool CurB(string key, bool glob) => Cur(key, glob ? "true" : "false").Equals("true", StringComparison.OrdinalIgnoreCase);

        var ovr = Chk($"Override Smart Capture for this {(scope == LiteBoxOption.ScopeGame ? "game" : "emulator")}", overridden, 8);
        p.Controls.Add(ovr);
        p.Controls.Add(Lab("Unchecked = use the global Smart Capture settings.", 30, subFg));

        var en = Chk("Enable Smart Capture", CurB("SmartCaptureEnabled", g.Enabled), 58);
        p.Controls.Add(en);

        // Title — OR-priority: a matching window is the game on its own.
        p.Controls.Add(Lab("Title filter (wildcard) — optional, priority:", 88, subFg));
        var title = Txt(Cur("SmartCaptureTitle", g.Title), 8, 108, 380); p.Controls.Add(title);
        p.Controls.Add(Lab("If set, a window whose title matches IS the game — no matter the rest.", 132, subFg));

        p.Controls.Add(Lab("— otherwise, detect the window by: —", 160, subFg));

        // fps test:  [x] Renders at ≥ [fps] fps for [sus] ms
        var useFps = Chk("Renders at ≥", CurB("SmartCaptureUseFps", g.UseFps), 186);
        p.Controls.Add(useFps);
        var fps = Txt(Cur("SmartCaptureMinFps", g.MinFps.ToString()), 130, 184, 55); p.Controls.Add(fps);
        p.Controls.Add(Unit("fps for", 192, 187));
        var sus = Txt(Cur("SmartCaptureSustainMs", g.SustainMs.ToString()), 240, 184, 55); p.Controls.Add(sus);
        p.Controls.Add(Unit("ms", 300, 187));

        // size test:  [x] Window covers ≥ [sz] % of the screen
        var useSize = Chk("Window covers ≥", CurB("SmartCaptureUseSize", g.UseSize), 214);
        p.Controls.Add(useSize);
        var sz = Txt(Cur("SmartCaptureMinSizePct", g.MinSizePct.ToString()), 150, 212, 55); p.Controls.Add(sz);
        p.Controls.Add(Unit("% of the screen", 212, 215));

        // combine (live only when BOTH tests are on)
        var lblComb = Lab("When both are on, combine with:", 242);
        p.Controls.Add(lblComb);
        string comb = Cur("SmartCaptureCombine", g.Combine);
        bool isOr = comb.Equals("or", StringComparison.OrdinalIgnoreCase);
        var rAnd = Rad("AND", 230, 240, !isOr); var rOr = Rad("OR", 300, 240, isOr);
        p.Controls.Add(rAnd); p.Controls.Add(rOr);

        p.Controls.Add(Lab("Reveal anyway after (ms):", 272));
        var maxw = Txt(Cur("SmartCaptureMaxMs", g.MaxWaitMs.ToString()), 200, 269, 70); p.Controls.Add(maxw);

        var stopWin = Chk("End session when the game window closes", CurB("SmartCaptureStopOnWindowClose", g.StopOnWindowClose), 300);
        p.Controls.Add(stopWin);

        // Enable cascade: the whole block follows the override checkbox; each test's params follow their
        // own checkbox; the AND/OR pair is live only when BOTH tests are on.
        var block = new Control[] { en, title, useFps, fps, sus, useSize, sz, maxw, stopWin, rAnd, rOr, lblComb };
        void Sync()
        {
            bool on = !readOnly && ovr.Checked;
            foreach (var c in block) c.Enabled = on;
            if (on)
            {
                fps.Enabled = sus.Enabled = useFps.Checked;
                sz.Enabled = useSize.Checked;
                bool both = useFps.Checked && useSize.Checked;
                rAnd.Enabled = rOr.Enabled = lblComb.Enabled = both;
            }
        }
        ovr.CheckedChanged += (_, _) => Sync();
        useFps.CheckedChanged += (_, _) => Sync();
        useSize.CheckedChanged += (_, _) => Sync();
        Sync();

        void Save()
        {
            if (!ovr.Checked)
            {
                foreach (var k in SmartCaptureConfig.Keys) LiteBoxOption.SetOverride(scope, entityId, k, null);
                return;
            }
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureEnabled", en.Checked ? "true" : "false");
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureUseFps", useFps.Checked ? "true" : "false");
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureUseSize", useSize.Checked ? "true" : "false");
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureCombine", rOr.Checked ? "or" : "and");
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureMinFps", fps.Text.Trim());
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureSustainMs", sus.Text.Trim());
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureMinSizePct", sz.Text.Trim());
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureTitle", title.Text.Trim());
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureMaxMs", maxw.Text.Trim());
            LiteBoxOption.SetOverride(scope, entityId, "SmartCaptureStopOnWindowClose", stopWin.Checked ? "true" : "false");
        }

        return (p, Save);
    }
}
