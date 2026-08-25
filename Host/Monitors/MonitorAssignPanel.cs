// The "Monitor Profile" page shared by the entity editors — Edit Emulator, Edit Additional Version, and
// (for a single game) Edit Game ▸ Launching.
//
// Two shapes, from one builder:
//
//   emulator  no override box. The emulator is the BOTTOM of the chain, so "nothing chosen" and
//             "explicitly nothing" mean the same thing there and a third state would be noise. It also
//             gets "Custom settings", an inline configuration that belongs to this emulator alone.
//
//   game /    an override box first. Unticked means "no opinion, ask the level below" — which is a
//   version   DIFFERENT answer from "do not use a monitor profile", and the distinction is the whole
//             reason the chain exists: an emulator can carry a profile for all its games while one game
//             opts out.
//
// Every saved profile is offered, not only the ones shown in the Tools menu: hiding a profile from a
// one-click menu is not the same as withdrawing it from deliberate configuration.
//
// Custom is deliberately WITHOUT a layout: capturing a whole desktop arrangement from inside an emulator
// editor would be a strange gesture, and the case it serves — a display mode and a sound card for this
// emulator — is exactly the other three parts.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Options;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Monitors;

internal static class MonitorAssignPanel
{
    private const string NoProfile = "Do not use a monitor profile";
    private const string CustomItem = "Custom settings for this emulator…";

    /// <summary>Builds the page. <paramref name="allowCustom"/> adds the inline configuration (emulators);
    /// <paramref name="withOverride"/> puts an "override" checkbox in front (game / version).</summary>
    public static (Control panel, Action apply) Build(string scope, string entityId, float dpiS, bool readOnly,
                                                      bool allowCustom, bool withOverride)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = ModulePanelKit.Root(dpiS);
        int y = S(8);
        void Row(Control c, int h, int indent = 0) { c.Location = new Point(S(4 + indent), y); root.Controls.Add(c); y += h; }

        var current = MonitorAssign.Get(scope, entityId);
        var profiles = MonitorProfileStore.All();

        // ── the override box (game / version only) ──
        CheckBox? over = null;
        if (withOverride)
        {
            over = new CheckBox
            {
                Text = "Override the monitor profile for this " + (scope == Data.LiteBoxOption.ScopeVersion ? "version" : "game"),
                AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg,
                Checked = current.IsSet, Enabled = !readOnly,
            };
            Row(over, S(26));
            Row(ModulePanelKit.Caption("Unticked, this level has no opinion and the one below decides "
                                     + (scope == Data.LiteBoxOption.ScopeVersion ? "(the game, then its emulator)." : "(its emulator)."), dpiS, 620), S(34), 18);
        }

        Row(ModulePanelKit.Caption("Monitor profile", dpiS, 620), S(20));

        var combo = ModulePanelKit.Combo(dpiS, readOnly, 420);
        combo.Items.Add(NoProfile);
        foreach (var p in profiles) combo.Items.Add(p.Name);
        if (allowCustom) combo.Items.Add(CustomItem);

        combo.SelectedIndex = current.Kind switch
        {
            AssignKind.Custom when allowCustom => combo.Items.Count - 1,
            AssignKind.Profile => Math.Max(0, profiles.FindIndex(p => p.Id == current.ProfileId) + 1),
            _ => 0,
        };
        Row(combo, S(30), 18);

        Row(ModulePanelKit.Caption("Applied when a game launches from here, and undone when it exits — on its "
                                 + "own snapshot, so \"Restore Original Layout\" is left alone.", dpiS, 620), S(36), 18);

        // ── the inline custom configuration ──
        Action? applyCustom = null;
        Panel? customBox = null;
        MonitorProfile? customProfile = null;
        if (allowCustom)
        {
            customProfile = MonitorAssign.GetCustom(scope, entityId) ?? new MonitorProfile { Name = "Custom" };
            (customBox, applyCustom) = BuildCustom(customProfile, dpiS, readOnly);
            customBox.Location = new Point(S(18), y);
            root.Controls.Add(customBox);
        }

        void Sync()
        {
            bool on = over == null || over.Checked;
            combo.Enabled = !readOnly && on;
            if (customBox != null) customBox.Visible = on && combo.SelectedIndex == combo.Items.Count - 1;
        }
        if (over != null) over.CheckedChanged += (_, _) => Sync();
        combo.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        ThemedCheckBox.StyleAll(root);

        return (root, () =>
        {
            if (readOnly) return;

            bool on = over == null || over.Checked;
            if (!on) { MonitorAssign.Clear(scope, entityId); return; }

            int ix = combo.SelectedIndex;
            if (allowCustom && ix == combo.Items.Count - 1)
            {
                applyCustom?.Invoke();                              // controls → the profile object
                if (customProfile != null) SaveCustom(scope, entityId, customProfile);   // …then to the DB
                MonitorAssign.Set(scope, entityId, new Assignment(AssignKind.Custom, ""));
            }
            else if (ix <= 0)
            {
                // The emulator tier has no "inherit", so its "do not use" is stored as an explicit none
                // too — same stored value, and it keeps the Assignments page able to list it.
                MonitorAssign.Set(scope, entityId, new Assignment(AssignKind.None, ""));
            }
            else
            {
                MonitorAssign.Set(scope, entityId, new Assignment(AssignKind.Profile, profiles[ix - 1].Id));
            }
        });
    }

    // ── the custom editor: display mode + sound card + solo, no layout ────────

    // Internal: the MonitorProfile LAUNCH RULE reuses this exact editor for its own inline custom.
    // twoColumns redistributes the content for a DIALOG (the rule editor): display mode on the left,
    // NVIDIA + sound + solo on the right — the emulator page keeps its single scrollable column.
    internal static (Panel box, Action apply) BuildCustom(MonitorProfile p, float dpiS, bool readOnly,
        bool twoColumns = false)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        // AutoSize rather than a guessed width: a Panel CLIPS its children, and sizing it from the parent's
        // ClientSize at build time reads zero — the parent has not been laid out yet. Letting it grow to
        // bound what it contains is the only measurement that is correct whenever it is taken.
        var box = new Panel
        {
            BackColor = ModulePanelKit.Bg,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        int y = 0, colX = 0;
        void Row(Control c, int h, int indent = 0) { c.Location = new Point(colX + S(indent), y); box.Controls.Add(c); y += h; }
        void NextColumn() { if (!twoColumns) return; colX = S(272); y = 0; }
        int wMain = twoColumns ? 246 : 360;    // monitor / sound-device combos
        int wSmall = twoColumns ? 246 : 260;   // the mode selectors
        Label Cap(string t) => ModulePanelKit.Caption(t, dpiS, twoColumns ? 246 : 560);

        var monitors = DisplayTargets.Enumerate();

        // display mode
        var usePreset = new CheckBox { Text = "Display mode", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Checked = p.Preset != null, Enabled = !readOnly };
        Row(usePreset, S(24));

        var monCombo = ModulePanelKit.Combo(dpiS, readOnly, wMain);
        monCombo.Items.Add("Main monitor (whichever is primary)");
        foreach (var m in monitors)
            monCombo.Items.Add($"{m.FriendlyName}  ({(m.DisplayName.Length > 0 ? m.DisplayName : "connected")})");
        // The preset's own monitor when it is NOT among the attached ones — a deliberate selection
        // must SURVIVE unplugging (or an identity edit in the database): silently snapping back to
        // "Main monitor" both lies and, on the next save, destroys the stored identity. Same
        // "(disconnected)" synthetic entry as the profile editor; the saved DevicePath/EDID ride it.
        var savedMon = p.Preset != null && p.Preset.DevicePath.Length > 0
            && !monitors.Any(m => string.Equals(m.DevicePath, p.Preset.DevicePath, StringComparison.OrdinalIgnoreCase))
            ? p.Preset : null;
        if (savedMon != null)
            monCombo.Items.Add($"{(savedMon.FriendlyName.Length > 0 ? savedMon.FriendlyName : "Saved monitor")}  (disconnected)");
        int mi = savedMon != null ? monCombo.Items.Count - 1
               : p.Preset != null && p.Preset.DevicePath.Length > 0
                 ? monitors.FindIndex(m => string.Equals(m.DevicePath, p.Preset.DevicePath, StringComparison.OrdinalIgnoreCase)) + 1 : 0;
        monCombo.SelectedIndex = Math.Max(0, mi);
        Row(monCombo, S(28), 18);

        var primary = new CheckBox { Text = "Make this monitor the main one", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Checked = p.Preset?.MakePrimary ?? false, Enabled = !readOnly };
        Row(primary, S(24), 18);

        var res = ModulePanelKit.Combo(dpiS, readOnly, wSmall);
        res.Items.Add("(leave unchanged)");
        foreach (var l in ModeCatalog.ResolutionLabels()) res.Items.Add(l);
        res.SelectedIndex = 0;
        if (p.Preset is { HasMode: true }) Select(res, $"{p.Preset.Width} x {p.Preset.Height}");
        Row(Cap("Resolution"), S(18), 18);
        Row(res, S(28), 18);

        var freq = ModulePanelKit.Combo(dpiS, readOnly, wSmall);
        freq.Items.Add("(leave unchanged)");
        foreach (var l in ModeCatalog.RefreshLabels()) freq.Items.Add(l);
        freq.SelectedIndex = 0;
        if (p.Preset is { Frequency: > 0 }) Select(freq, p.Preset.Frequency + " Hz");
        Row(Cap("Refresh rate"), S(18), 18);
        Row(freq, S(28), 18);

        var hdr = ModulePanelKit.Combo(dpiS, readOnly, wSmall);
        hdr.Items.AddRange(new object[] { "Leave unchanged", "Force HDR on", "Force HDR off (SDR)" });
        hdr.SelectedIndex = p.Preset?.Hdr switch { true => 1, false => 2, _ => 0 };
        Row(Cap("HDR"), S(18), 18);
        Row(hdr, S(28), 18);

        var rotValues = new[] { "", "Identity", "Rotate90", "Rotate180", "Rotate270" };
        var rot = ModulePanelKit.Combo(dpiS, readOnly, wSmall);
        rot.Items.AddRange(new object[] { "(leave unchanged)", "Landscape", "Portrait (90°)", "Landscape flipped (180°)", "Portrait flipped (270°)" });
        rot.SelectedIndex = Math.Max(0, Array.FindIndex(rotValues, v => string.Equals(v, p.Preset?.Rotation ?? "", StringComparison.OrdinalIgnoreCase)));
        Row(Cap("Rotation"), S(18), 18);
        Row(rot, S(28), 18);

        var scaleValues = new[] { "", "Default", "Stretch", "Center" };
        var scale = ModulePanelKit.Combo(dpiS, readOnly, wSmall);
        scale.Items.AddRange(new object[] { "(leave unchanged)", "Driver default", "Stretch to fill", "Center (no stretch)" });
        scale.SelectedIndex = Math.Max(0, Array.FindIndex(scaleValues, v => string.Equals(v, p.Preset?.OutputScaling ?? "", StringComparison.OrdinalIgnoreCase)));
        Row(Cap("Scaling below native"), S(18), 18);
        Row(scale, S(28), 18);

        var zoom = ModulePanelKit.Combo(dpiS, readOnly, wSmall);
        zoom.Items.Add("(leave unchanged)");
        foreach (var z in new[] { 100, 125, 150, 175, 200, 225, 250, 300 }) zoom.Items.Add(z + "%");
        zoom.SelectedIndex = 0;
        if (p.Preset is { DpiScale.Length: > 0 }) Select(zoom, LayoutPath.ZoomPercent(p.Preset.DpiScale));
        Row(Cap("Windows zoom"), S(18), 18);
        Row(zoom, S(28), 18);

        // NVIDIA output — the vendor's dark green frames its scope: driver-level, skipped with the
        // vendor named when this monitor is driven by another GPU. Shown only where it can act (an
        // NVIDIA driver answers AND applies are on) — stored values survive the hiding untouched.
        bool showNv = GpuColor.NvPresent && GpuColor.ApplyEnabled;
        var nvGreen = Color.FromArgb(24, 46, 24);
        var nvBorder = Color.FromArgb(58, 110, 58);
        var nvBox = new Panel
        {
            BackColor = nvGreen, BorderStyle = BorderStyle.FixedSingle,
            Width = S(300), Height = S(236), Padding = new Padding(S(8), S(6), S(8), S(6)),
        };
        nvBox.Paint += (_, e) => { using var pen = new Pen(nvBorder); e.Graphics.DrawRectangle(pen, 0, 0, nvBox.Width - 1, nvBox.Height - 1); };
        int ny = S(4);
        void N(Control c, int h, int indent = 0) { c.Location = new Point(S(8 + indent), ny); if (c is Label l) l.BackColor = nvGreen; if (c is CheckBox cb) cb.BackColor = nvGreen; nvBox.Controls.Add(c); ny += h; }
        N(new Label { Text = "NVIDIA output", AutoSize = true, BackColor = nvGreen, ForeColor = Color.FromArgb(140, 210, 140), Font = new Font("Segoe UI", 9f, FontStyle.Bold) }, S(24));

        var gFmt = ModulePanelKit.Combo(dpiS, readOnly, 220);
        gFmt.Items.AddRange(new object[] { "(leave unchanged)", "RGB", "YCbCr444", "YCbCr422", "YCbCr420" });
        gFmt.SelectedIndex = p.Preset?.GpuFormat switch { "RGB" => 1, "YCbCr444" => 2, "YCbCr422" => 3, "YCbCr420" => 4, _ => 0 };
        N(Cap("Color format"), S(18)); N(gFmt, S(30));

        var gDep = ModulePanelKit.Combo(dpiS, readOnly, 220);
        gDep.Items.AddRange(new object[] { "(leave unchanged)", "8 bpc", "10 bpc", "12 bpc" });
        gDep.SelectedIndex = p.Preset?.GpuDepthBpc switch { 8 => 1, 10 => 2, 12 => 3, _ => 0 };
        N(Cap("Color depth"), S(18)); N(gDep, S(30));

        var gRng = ModulePanelKit.Combo(dpiS, readOnly, 220);
        gRng.Items.AddRange(new object[] { "(leave unchanged)", "Full", "Limited" });
        gRng.SelectedIndex = p.Preset?.GpuDynamicRange switch { "Full" => 1, "Limited" => 2, _ => 0 };
        N(Cap("Dynamic range"), S(18)); N(gRng, S(30));

        string[] scaleVals = { "", "ToAspectScanOutToClosest", "ToAspectScanOutToNative", "ToClosest", "ToNative", "GPUScanOutToClosest", "GPUScanOutToNative", GpuColor.IntegerScalingName };
        var gScale = ModulePanelKit.Combo(dpiS, readOnly, 220);
        gScale.Items.AddRange(new object[] { "(leave unchanged)", "Aspect ratio (display)", "Aspect ratio (GPU)",
                                             "Full-screen (display)", "Full-screen (GPU)", "No scaling (display)", "No scaling (GPU)",
                                             "Integer scaling (GPU)" });
        gScale.SelectedIndex = Math.Max(0, Array.IndexOf(scaleVals, p.Preset?.GpuScaling ?? ""));
        N(Cap("GPU scaling (mode + device)"), S(18)); N(gScale, S(30));

        var gVrr = ModulePanelKit.Combo(dpiS, readOnly, 220);
        gVrr.Items.AddRange(new object[] { "(leave unchanged)", "Off", "Fullscreen only", "Fullscreen and windowed" });
        gVrr.SelectedIndex = p.Preset?.GpuVrr switch { "off" => 1, "fullscreen" => 2, "always" => 3, _ => 0 };
        N(Cap("G-Sync / VRR (driver-wide)"), S(18)); N(gVrr, S(30));

        var gVibOn = new CheckBox { Text = "Set digital vibrance", AutoSize = true, ForeColor = ModulePanelKit.Fg, Checked = p.Preset is { GpuVibrance: >= 0 }, Enabled = !readOnly };
        var gVib = new NumericUpDown { Minimum = 0, Maximum = 100, Width = S(64), BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.None, Enabled = !readOnly && gVibOn.Checked, Value = p.Preset is { GpuVibrance: >= 0 } ? Math.Min(100, p.Preset.GpuVibrance) : 50 };
        gVibOn.CheckedChanged += (_, _) => gVib.Enabled = !readOnly && gVibOn.Checked;
        N(gVibOn, S(24)); N(gVib, S(28), 16);
        nvBox.Height = ny + S(6);

        var adjust = new CheckBox { Text = "Adjust to the closest supported value", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Checked = p.Preset?.AdjustToClosest ?? true, Enabled = !readOnly };
        Row(adjust, S(26), 18);

        // Second column (dialog mode): NVIDIA, then sound, then solo. Single column keeps the
        // page's historical order — NVIDIA between the zoom and the adjust box.
        NextColumn();
        if (showNv) Row(nvBox, nvBox.Height + S(6), twoColumns ? 0 : 18);

        // sound
        var useAudio = new CheckBox { Text = "Sound card / volume", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Checked = p.Audio != null, Enabled = !readOnly };
        Row(useAudio, S(24));

        var dev = ModulePanelKit.Combo(dpiS, readOnly, wMain);
        dev.Items.Add("(leave unchanged)");
        foreach (var d in AudioEndpoints.Playback()) dev.Items.Add(d);
        dev.SelectedIndex = 0;
        if (p.Audio != null && p.Audio.Device.Length > 0) Select(dev, p.Audio.Device);
        Row(dev, S(28), 18);

        var useVol = new CheckBox { Text = "Set volume", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Checked = p.Audio?.Volume != null, Enabled = !readOnly };
        Row(useVol, S(24), 18);
        var vol = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 5, Width = S(80), BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.None, Enabled = !readOnly, Value = Math.Clamp(p.Audio?.Volume ?? 50, 0, 100) };
        Row(vol, S(28), 36);

        var solo = new CheckBox { Text = "Turn off every monitor except the primary one", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Checked = p.SoloPrimary, Enabled = !readOnly };
        Row(solo, S(26));

        ThemedCheckBox.StyleAll(box);

        return (box, () =>
        {
            var (w, h) = ParseRes(res.SelectedItem as string);
            int hz = ParseHz(freq.SelectedItem as string);
            bool? wantHdr = hdr.SelectedIndex switch { 1 => true, 2 => false, _ => (bool?)null };
            // The synthetic "(disconnected)" entry keeps the SAVED identity verbatim.
            bool savedSel = savedMon != null && monCombo.SelectedIndex == monCombo.Items.Count - 1;
            var mon = !savedSel && monCombo.SelectedIndex > 0 ? monitors[monCombo.SelectedIndex - 1] : null;

            var preset = new MonitorPreset
            {
                DevicePath = savedSel ? savedMon!.DevicePath : mon?.DevicePath ?? "",
                FriendlyName = savedSel ? savedMon!.FriendlyName : mon?.FriendlyName ?? "",
                EdidManufacture = savedSel ? savedMon!.EdidManufacture : mon?.EdidManufacture ?? "",
                EdidProduct = savedSel ? savedMon!.EdidProduct : mon?.EdidProduct ?? 0,
                Width = w > 0 ? w : 0,
                Height = w > 0 ? h : 0,
                Frequency = w > 0 ? hz : 0,
                Hdr = wantHdr,
                AdjustToClosest = adjust.Checked,
                MakePrimary = primary.Checked && (mon != null || savedSel),
                Rotation = rotValues[Math.Max(0, rot.SelectedIndex)],
                OutputScaling = scaleValues[Math.Max(0, scale.SelectedIndex)],
                DpiScale = (zoom.SelectedItem as string) is { Length: > 0 } zl && !zl.StartsWith("(")
                    ? (zl == "100%" ? "Identity" : "Scale" + zl.TrimEnd('%') + "Percent") : "",
                GpuFormat = gFmt.SelectedIndex switch { 1 => "RGB", 2 => "YCbCr444", 3 => "YCbCr422", 4 => "YCbCr420", _ => "" },
                GpuDepthBpc = gDep.SelectedIndex switch { 1 => 8, 2 => 10, 3 => 12, _ => 0 },
                GpuDynamicRange = gRng.SelectedIndex switch { 1 => "Full", 2 => "Limited", _ => "" },
                GpuVibrance = gVibOn.Checked ? (int)gVib.Value : -1,
                GpuVrr = gVrr.SelectedIndex switch { 1 => "off", 2 => "fullscreen", 3 => "always", _ => "" },
                GpuScaling = scaleVals[Math.Max(0, gScale.SelectedIndex)],
            };
            p.Preset = usePreset.Checked && !preset.IsEmpty ? preset : null;

            if (useAudio.Checked)
            {
                p.Audio = new AudioPreset
                {
                    Device = dev.SelectedIndex > 0 ? (dev.SelectedItem as string ?? "") : "",
                    Volume = useVol.Checked ? (int)vol.Value : null,
                };
                if (p.Audio.Device.Length == 0 && p.Audio.Volume == null) p.Audio = null;
            }
            else p.Audio = null;

            p.SoloPrimary = solo.Checked;
            p.Layout = null;             // never a layout here — see the header
            p.Public = false;            // not a menu entry: it belongs to one emulator
        });
    }

    // ── shared little helpers ────────────────────────────────────────────────

    private static void Select(ComboBox box, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (int i = 0; i < box.Items.Count; i++)
            if (string.Equals(box.Items[i] as string, text, StringComparison.OrdinalIgnoreCase)) { box.SelectedIndex = i; return; }
        box.Items.Insert(1, text);
        box.SelectedIndex = 1;
    }

    private static (int W, int H) ParseRes(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.StartsWith("(")) return (0, 0);
        var parts = s.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h) ? (w, h) : (0, 0);
    }

    private static int ParseHz(string? s)
        => string.IsNullOrEmpty(s) || s.StartsWith("(") ? 0 : (int.TryParse(s.Replace("Hz", "").Trim(), out int v) ? v : 0);

    /// <summary>The custom profile must be written before the assignment points at it — the caller's
    /// apply does both, in that order.</summary>
    public static void SaveCustom(string scope, string entityId, MonitorProfile p) => MonitorAssign.SetCustom(scope, entityId, p);
}
