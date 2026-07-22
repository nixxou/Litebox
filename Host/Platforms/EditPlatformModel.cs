// The "3D Model Settings" tab of the Edit Platform window — LB parity. Override checkbox + Model Type dropdown
// (Box / DVD Case / Jewel Case / Double Jewel Case / Long Jewel Case), with a MORPHING set of controls per type
// (which fields each type exposes was decoded empirically — see memory reference-lb-3d-box-models). Writes the
// root-level <ModelSettings> block to Platforms.xml via PlatformModelStore (removed when Override is off).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformModel
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color Panel2 = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;

    // Model Type dropdown label → stored ModelType string.
    private static readonly (string label, string val)[] ModelTypes =
    {
        ("Box", "box"), ("DVD Case", "dvd"), ("Jewel Case", "jewelCase"),
        ("Double Jewel Case", "doubleJewelCase"), ("Long Jewel Case", "longJewelCase"),
    };
    private static readonly string[] SpineModes = { "AutomaticDetection", "SingleSpineImage", "DualSpineImageSplitCenter", "DualSpineImageMiddleSeparator" };
    private static readonly int[] Rotations = { 0, 90, 180, 270 };
    private static readonly string[] SideNames = { "Left", "Top", "Right", "Bottom" };

    public static (Control panel, Action apply) Build(IPlatform plat, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        string name = Safe(() => plat.Name) ?? "";
        var cur = PlatformModelStore.Read(name);   // existing override, or null

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(12)) };
        int y = S(6);

        var overrideChk = new CheckBox { Text = "Override Default Settings", Location = new Point(S(6), y), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = cur != null }; root.Controls.Add(overrideChk);
        y += S(34);

        // Model Type
        root.Controls.Add(new Label { Text = "Model Type:", Location = new Point(S(6), y + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg });
        var modelType = new ComboBox { Location = new Point(S(150), y), Width = S(260), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        foreach (var t in ModelTypes) modelType.Items.Add(t.label);
        modelType.SelectedIndex = Math.Max(0, Array.FindIndex(ModelTypes, t => string.Equals(t.val, Get(cur, "ModelType"), StringComparison.OrdinalIgnoreCase)));
        root.Controls.Add(modelType);
        y += S(36);

        // ── all controls (shown/hidden per type; visible rows reflow compactly, no gaps) ──
        var rows = new List<(Control[] ctrls, Func<string, bool> visible, int baseY, int height)>();
        var origTop = new Dictionary<Control, int>();
        int firstRowY = y;
        void Row(Func<string, bool> visible, params Control[] ctrls)
        {
            foreach (var c in ctrls) { root.Controls.Add(c); origTop[c] = c.Top; }
            int minTop = ctrls.Min(c => c.Top), maxBot = ctrls.Max(c => c.Top + Math.Max(c.Height, S(20)));
            rows.Add((ctrls, visible, minTop, maxBot - minTop));
        }
        Label Lbl(string t, int yy) => new() { Text = t, Location = new Point(S(6), yy + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
        CheckBox Chk(string t, int yy, bool v) => new() { Text = t, Location = new Point(S(6), yy), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = v };

        bool IsBox(string t) => t == "box"; bool IsDvd(string t) => t == "dvd";
        bool IsJewel(string t) => t is "jewelCase" or "doubleJewelCase" or "longJewelCase";
        bool BoxDvd(string t) => IsBox(t) || IsDvd(t);

        // Force Model Size (box, dvd) — checkbox on line 1, Width/Height/Depth on line 2 (LB layout).
        var sizeChk = Chk("Force Model Size:", y, HasSize(cur));
        int sy = y + S(26);
        var szParts = Get(cur, "ModelSizeString").Split(';');
        string DV(int i, string def) { var v = szParts.ElementAtOrDefault(i); return string.IsNullOrWhiteSpace(v) ? def : v!; }
        Label InLbl(string t, int x, int yy) => new() { Text = t, Location = new Point(S(x), yy + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
        var wLbl = InLbl("Width:", 20, sy); var w = Num(S, S(80), sy, DV(0, "1"));
        var hLbl = InLbl("Height:", 190, sy); var h = Num(S, S(255), sy, DV(1, "1"));
        var dLbl = InLbl("Depth:", 370, sy); var d = Num(S, S(430), sy, DV(2, "0.001"));
        Row(BoxDvd, sizeChk, wLbl, w, hLbl, h, dLbl, d); y += S(56);

        // Colors
        var (caseChk, caseSwatch) = ColorRow(S, "Force Case Background Color:", y, PlatformModelStore.ParseArgb(Get(cur, "CaseColor")));
        Row(IsDvd, caseChk, caseSwatch); y += S(0);   // dvd only (jewel uses CaseColor as text colour, below)
        var (coverChk, coverSwatch) = ColorRow(S, "Force Cover Background Color:", y, PlatformModelStore.ParseArgb(Get(cur, "CoverColor")));
        Row(_ => true, coverChk, coverSwatch); y += S(34);

        // Full scan / landscape (box, dvd)
        var fullScan = Chk("Enable Full Scan Images", y, GetBool(cur, "UseFullScanImages"));
        var landscape = Chk("Landscape", y, GetBool(cur, "FullScanIsLandscape")); landscape.Location = new Point(S(260), y);
        Row(BoxDvd, fullScan, landscape); y += S(30);

        // Spine width (box, dvd, jewel, double)
        var spineWidth = new TextBox { Location = new Point(S(220), y), Width = S(90), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Text = Get(cur, "FullImageSpineWidth") };
        var spineWidthLbl = Lbl("Spine Width (% of Full Scan):", y);
        Row(t => BoxDvd(t) || t is "jewelCase" or "doubleJewelCase", spineWidthLbl, spineWidth); y += S(32);

        // Side Spine Image Mode (double jewel)
        var spineMode = new ComboBox { Location = new Point(S(220), y), Width = S(240), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        spineMode.Items.AddRange(SpineModes); spineMode.SelectedIndex = Math.Max(0, Array.IndexOf(SpineModes, Get(cur, "DoubleSpineImageMode")));
        Row(t => t == "doubleJewelCase", Lbl("Side Spine Image Mode:", y), spineMode); y += S(32);

        // Spine Style (jewel, double): Solid / Clear / Custom Solid / Custom Clear + a custom path. (Embedded
        // platform presets are read-only fallbacks; the editor exposes generic + custom, matching common use.)
        var spineStyle = new ComboBox { Location = new Point(S(150), y), Width = S(200), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        spineStyle.Items.AddRange(new object[] { "Solid Spine", "Clear Spine", "Custom Solid Spine", "Custom Clear Spine" });
        var spinePath = new TextBox { Location = new Point(S(360), y), Width = S(180), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        var spineBrowse = Mini(S, "…", S(546), y, 30);
        InitSpineStyle(spineStyle, spinePath, Get(cur, "FrontSpineImage"), GetBool(cur, "FrontSpineIsClear"));
        spineBrowse.Click += (_, _) => { using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" }; if (dlg.ShowDialog() == DialogResult.OK) spinePath.Text = dlg.FileName; };
        Row(t => t is "jewelCase" or "doubleJewelCase", Lbl("Spine Style:", y), spineStyle, spinePath, spineBrowse); y += S(32);

        // Plain-text title (jewel types): Use Plain Text Title + Title Font + Text Foreground Color (CaseColor)
        var textTitle = Chk("Use Plain Text Title Instead of Clear Logo", y, !string.IsNullOrEmpty(Get(cur, "LogoFont")));
        var fontLbl = new Label { Location = new Point(S(360), y + S(2)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Text = string.IsNullOrEmpty(Get(cur, "LogoFont")) ? "(font)" : Get(cur, "LogoFont") };
        var fontBtn = Mini(S, "Font…", S(300), y, 54);
        string chosenFont = Get(cur, "LogoFont");
        fontBtn.Click += (_, _) => { using var fd = new FontDialog(); try { if (!string.IsNullOrEmpty(chosenFont)) fd.Font = new Font(chosenFont, 12f); } catch { } if (fd.ShowDialog() == DialogResult.OK) { chosenFont = fd.Font.Name; fontLbl.Text = chosenFont; } };
        var (textColorChk, textColorSwatch) = ColorRow(S, "Text Foreground Color:", y + S(28), PlatformModelStore.ParseArgb(Get(cur, "CaseColor"))); textColorChk.Text = "Text Foreground Color:";
        Row(IsJewel, textTitle, fontBtn, fontLbl); Row(IsJewel, textColorChk, textColorSwatch); y += S(62);

        // Sides (box=4, dvd=1 spine group, jewel/double/long=2). Each: Draw Spine (+rot), Draw Logo/Title (+rot).
        var spineSides = PlatformModelStore.ParseSides(Get(cur, "SpineRotation"));
        var logoSides = PlatformModelStore.ParseSides(Get(cur, "LogoRotation"));
        var sideCtrls = new (CheckBox spine, ComboBox spineRot, CheckBox logo, ComboBox logoRot)[4];
        var sideHeaders = new Label[4];
        for (int i = 0; i < 4; i++)
        {
            int si = i, yy = y;
            var lblSide = new Label { Text = SideNames[i] + " Side", Location = new Point(S(6), yy), AutoSize = true, ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            sideHeaders[i] = lblSide;
            var ds = new CheckBox { Text = "Draw Spine Image", Location = new Point(S(20), yy + S(22)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = spineSides[i].draw };
            var dsRot = RotCombo(S, S(200), yy + S(20), spineSides[i].rot);
            var dl = new CheckBox { Text = "Draw Clear Logo / Title", Location = new Point(S(20), yy + S(46)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = logoSides[i].draw };
            var dlRot = RotCombo(S, S(200), yy + S(44), logoSides[i].rot);
            sideCtrls[i] = (ds, dsRot, dl, dlRot);
            // Left=box/jewel/dvd (dvd's single "Spine" group), Top/Bottom=box only, Right=box/jewel (NOT dvd).
            Func<string, bool> vis = si switch
            {
                0 => (t => IsBox(t) || IsJewel(t) || IsDvd(t)),
                2 => (t => IsBox(t) || IsJewel(t)),
                _ => IsBox,                                       // Top / Bottom → box only
            };
            Row(vis, lblSide, ds, dsRot, dl, dlRot);
            y += S(74);
        }

        // ── enable/visibility wiring ──
        void Refresh()
        {
            bool on = overrideChk.Checked && !readOnly;
            modelType.Enabled = on;
            string t = ModelTypes[Math.Max(0, modelType.SelectedIndex)].val;
            int cy = firstRowY;
            foreach (var (ctrls, visible, baseY, height) in rows)
            {
                bool vis = visible(t);
                int delta = cy - baseY;
                foreach (var c in ctrls) { c.Visible = vis; c.Enabled = on && vis; if (vis) c.Top = origTop[c] + delta; }
                if (vis) cy += height + S(8);   // compact: only visible rows advance the cursor
            }
            coverChk.Text = IsBox(t) ? "Force Box Background Color:" : "Force Cover Background Color:";
            sideHeaders[0].Text = IsDvd(t) ? "Spine" : "Left Side";   // dvd shows a single "Spine" group
        }
        overrideChk.CheckedChanged += (_, _) => Refresh();
        modelType.SelectedIndexChanged += (_, _) => Refresh();
        Refresh();

        void Apply()
        {
            if (readOnly) return;
            if (!overrideChk.Checked) { PlatformModelStore.Write(name, null); return; }   // override off → remove block
            string t = ModelTypes[Math.Max(0, modelType.SelectedIndex)].val;
            var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ModelType"] = t };
            if (BoxDvd(t) && sizeChk.Checked) f["ModelSizeString"] = $"{NumV(w)};{NumV(h)};{NumV(d)}";
            if (IsDvd(t) && caseChk.Checked && caseSwatch.BackColor != Bg) f["CaseColor"] = PlatformModelStore.ToArgb(caseSwatch.BackColor);
            if (coverChk.Checked && coverSwatch.BackColor != Bg) f["CoverColor"] = PlatformModelStore.ToArgb(coverSwatch.BackColor);
            if (BoxDvd(t)) { f["UseFullScanImages"] = fullScan.Checked ? "true" : "false"; f["FullScanIsLandscape"] = landscape.Checked ? "true" : "false"; }
            if (!string.IsNullOrWhiteSpace(spineWidth.Text)) f["FullImageSpineWidth"] = spineWidth.Text.Trim();
            if (t == "doubleJewelCase") f["DoubleSpineImageMode"] = SpineModes[Math.Max(0, spineMode.SelectedIndex)];
            if (t is "jewelCase" or "doubleJewelCase")
            {
                var (img, clear) = SpineStyleValue(spineStyle, spinePath);
                if (img != null) f["FrontSpineImage"] = img;
                f["FrontSpineIsClear"] = clear ? "true" : "false";
            }
            if (IsJewel(t))
            {
                if (textTitle.Checked && !string.IsNullOrEmpty(chosenFont)) f["LogoFont"] = chosenFont;
                if (textColorChk.Checked && textColorSwatch.BackColor != Bg) f["CaseColor"] = PlatformModelStore.ToArgb(textColorSwatch.BackColor);
            }
            // Sides → CSV. Only the visible sides for this type contribute; hidden ones stay empty.
            var spineArr = new (bool, int)[4]; var logoArr = new (bool, int)[4];
            for (int i = 0; i < 4; i++)
            {
                bool sideVisible = sideCtrls[i].spine.Visible;
                spineArr[i] = sideVisible ? (sideCtrls[i].spine.Checked, RotV(sideCtrls[i].spineRot)) : (false, 0);
                logoArr[i] = sideVisible ? (sideCtrls[i].logo.Checked, RotV(sideCtrls[i].logoRot)) : (false, 0);
            }
            f["SpineRotation"] = PlatformModelStore.BuildSides(spineArr);
            f["LogoRotation"] = PlatformModelStore.BuildSides(logoArr);
            PlatformModelStore.Write(name, f);
        }
        return (root, Apply);
    }

    // ── control helpers ──
    private static NumericUpDown Num(Func<int, int> S, int x, int y, string? v)
    {
        var n = new NumericUpDown { Location = new Point(x, y), Width = S(90), DecimalPlaces = 3, Increment = 0.001M, Minimum = 0, Maximum = 100, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle };
        if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) n.Value = Math.Min(100, Math.Max(0, d));
        return n;
    }
    private static string NumV(NumericUpDown n) => n.Value.ToString(CultureInfo.InvariantCulture);
    private static ComboBox RotCombo(Func<int, int> S, int x, int y, int rot)
    {
        var c = new ComboBox { Location = new Point(x, y), Width = S(70), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg };
        foreach (var r in Rotations) c.Items.Add(r + "°");
        c.SelectedIndex = Math.Max(0, Array.IndexOf(Rotations, rot));
        return c;
    }
    private static int RotV(ComboBox c) => Rotations[Math.Max(0, c.SelectedIndex)];
    private static Button Mini(Func<int, int> S, string t, int x, int y, int w) => new()
    { Text = t, Location = new Point(x, y - S(1)), Size = new Size(S(w), S(23)), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f) };

    private static (CheckBox chk, Button swatch) ColorRow(Func<int, int> S, string label, int y, Color? c)
    {
        var chk = new CheckBox { Text = label, Location = new Point(S(6), y), AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg, Checked = c.HasValue };
        var sw = new Button { Location = new Point(S(300), y - S(1)), Size = new Size(S(120), S(22)), FlatStyle = FlatStyle.Flat, BackColor = c ?? LiteBoxTheme.Bg, FlatAppearance = { BorderColor = LiteBoxTheme.SubFg, BorderSize = 1 } };
        sw.Click += (_, _) => { using var d = new ColorDialog { Color = sw.BackColor, FullOpen = true }; if (d.ShowDialog() == DialogResult.OK) { sw.BackColor = d.Color; chk.Checked = true; } };
        return (chk, sw);
    }

    private static void InitSpineStyle(ComboBox combo, TextBox path, string frontSpineImage, bool clear)
    {
        bool custom = !string.IsNullOrEmpty(frontSpineImage) && !frontSpineImage.StartsWith("{Resources}", StringComparison.OrdinalIgnoreCase);
        if (custom) { path.Text = frontSpineImage; combo.SelectedIndex = clear ? 3 : 2; }
        else combo.SelectedIndex = clear ? 1 : 0;
    }
    private static (string? img, bool clear) SpineStyleValue(ComboBox combo, TextBox path)
        => combo.SelectedIndex switch
        {
            1 => ("", true),                                  // Clear Spine
            2 => (path.Text.Trim(), false),                   // Custom Solid
            3 => (path.Text.Trim(), true),                    // Custom Clear
            _ => ("", false),                                 // Solid Spine
        };

    private static string Get(Dictionary<string, string>? m, string k) => m != null && m.TryGetValue(k, out var v) ? (v ?? "") : "";
    private static bool GetBool(Dictionary<string, string>? m, string k) => string.Equals(Get(m, k), "true", StringComparison.OrdinalIgnoreCase);
    private static bool HasSize(Dictionary<string, string>? m) => !string.IsNullOrWhiteSpace(Get(m, "ModelSizeString"));
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
