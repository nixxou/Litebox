// The "3D Model Settings" tab of the Edit Platform window — LB-parity layout (mirrors LaunchBox's editor):
// full-width dropdowns, per-type morphing rows, boxed per-side groups with header strips, and a right-hand
// "3D Model Preview" panel. The preview area is a structural placeholder (LaunchBox renders the case there
// live via HelixToolkit; wiring a renderer is future work — the layout already reserves its spot + the
// "Switch Sample Game" button). Writes the root-level <ModelSettings> block via PlatformModelStore.
//
// Per-type field visibility (decoded from LB's editor):
//   box:    Force Model Size, Full Scan + Landscape, Force Box Background Color (=CoverColor), Spine Width, 4 sides
//   dvd:    Force Model Size, Case+Cover colors, Full Scan (no Landscape), Spine Width, single "Spine" group
//   jewel/double/long: Spine Style (NOT long), Side Spine Image Mode (double only), Cover color,
//                      Plain Text Title + font + text color (=CaseColor), Left+Right sides. No Spine Width.

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
    private static Color GroupBody => Blend(Bg, Panel2);

    // Model Type dropdown label → stored ModelType string.
    private static readonly (string label, string val)[] ModelTypes =
    {
        ("Box", "box"), ("DVD Case", "dvd"), ("Jewel Case", "jewelCase"),
        ("Double Jewel Case", "doubleJewelCase"), ("Long Jewel Case", "longJewelCase"),
    };
    private static readonly (string label, string val)[] SpineModes =
    {
        ("Automatic Detection", "AutomaticDetection"),
        ("Single Spine Image", "SingleSpineImage"),
        ("Dual Spine Image - Split Center", "DualSpineImageSplitCenter"),
        ("Dual Spine Image - Middle Separator", "DualSpineImageMiddleSeparator"),
    };
    private static readonly int[] Rotations = { 0, 90, 180, 270 };
    private static readonly string[] SideNames = { "Left Side", "Top Side", "Right Side", "Bottom Side" };

    // Embedded jewel-case spine presets LaunchBox ships (Unbroken.LaunchBox.Windows.Properties.JewelCaseSpines.resources —
    // enumerated with --model-spines). Each preset stores FrontSpineImage = "{Resources}\<platform>[ - <suffix>]" with
    // FrontSpineIsClear=True; the empty suffix ("Auto-Detect") is LB's own hardcoded default (GetDefaultSettings emits the
    // bare "{Resources}\Sony Playstation" and resolves the region at render). Update this map if a future LB adds spines.
    // Dropdown shows "<platform> Spine"; version labels are LB's exact strings (decoded empirically with the
    // user driving the real LB dialog — see reference-lb-3d-box-models). The stored FrontSpineImage folds
    // platform + region + case variant: "{Resources}\<platform>[ - <suffix>[ Double Jewel]]" — the " Double Jewel"
    // tail is appended for doubleJewelCase (Auto-Detect stays bare for both types).
    private static readonly (string platform, (string label, string suffix)[] versions)[] SpinePresets =
    {
        ("Sega Dreamcast",   new[] { ("Auto-Detect", ""), ("Black Version", "NA Black"), ("European Version", "EU"), ("White Version", "NA White") }),
        ("Sony Playstation", new[] { ("Auto-Detect", ""), ("North American Version", "NA"), ("European Version", "EU") }),
    };

    public static (Control panel, Action apply) Build(IPlatform plat, bool readOnly, float s)
    {
        string name = Safe(() => plat.Name) ?? "";
        string scrapeAs = Safe(() => plat.ScrapeAs) ?? "";
        // Fallback = LB's hardcoded per-platform defaults (resolved live through the core, scrapeAs-aware:
        // a custom-named platform with Scrape As "Sony Playstation" pre-fills the PS1 jewel preset).
        return BuildCore(PlatformModelStore.Read(name), ModelDefaults.TryGet(name, scrapeAs),
                         f => PlatformModelStore.Write(name, f), readOnly, s);
    }

    /// <summary>Per-GAME override (Edit Game window) — the SAME panel: the game's own block drives the Override
    /// checkbox; when the game has none, the fields PRE-FILL from the platform's override (LB behaviour,
    /// observed on the real dialog), else from LB's hardcoded defaults. Writes the game-keyed block in
    /// Data\Platforms\&lt;Platform&gt;.xml.</summary>
    public static (Control panel, Action apply) BuildForGame(string platformName, string gameId, bool readOnly, float s, string? scrapeAs = null)
        => BuildCore(PlatformModelStore.ReadGame(platformName, gameId),
                     PlatformModelStore.Read(platformName) ?? ModelDefaults.TryGet(platformName, scrapeAs ?? ""),
                     f => PlatformModelStore.WriteGame(platformName, gameId, f), readOnly, s);

    private static (Control panel, Action apply) BuildCore(Dictionary<string, string>? own,
                                                           Dictionary<string, string>? fallback,
                                                           Action<Dictionary<string, string>?> write,
                                                           bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var cur = own ?? fallback;   // displayed values: own override, else the parent level's (game←platform)
        bool hasOwn = own != null;

        int X0 = S(6);
        int W = S(560);                  // left-column content width
        int RE = X0 + W;                 // its right edge
        int HW = (W - S(8)) / 2;         // half-column width
        int XR = X0 + HW + S(8);         // right half-column x

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(12)) };
        var preview = BuildPreview(S);
        root.Controls.Add(left);
        root.Controls.Add(preview);      // preview docks right first, left fills the rest

        int y = S(6);
        var overrideChk = new CheckBox { Text = "Override Default Settings", Location = new Point(X0, y), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = hasOwn };
        left.Controls.Add(overrideChk);
        y += S(34);

        // ── morphing rows (shown/hidden per type; visible rows reflow compactly, no gaps) ──
        var rows = new List<(Control[] ctrls, Func<string, bool> visible, int baseY, int height)>();
        var origTop = new Dictionary<Control, int>();
        var sideHeaders = new Label[4];
        var sideGroups = new Panel[4];
        int grpH = S(92);
        int firstRowY = y;
        void Row(Func<string, bool> visible, params Control[] ctrls)
        {
            foreach (var c in ctrls) { left.Controls.Add(c); origTop[c] = c.Top; }
            int minTop = ctrls.Min(c => c.Top), maxBot = ctrls.Max(c => c.Top + Math.Max(c.Height, S(20)));
            rows.Add((ctrls, visible, minTop, maxBot - minTop));
        }
        Label Lbl(string t, int x, int yy) => new() { Text = t, Location = new Point(x, yy + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
        CheckBox Chk(string t, int x, int yy, bool v) => new() { Text = t, Location = new Point(x, yy), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = v };

        bool IsBox(string t) => t == "box"; bool IsDvd(string t) => t == "dvd";
        bool IsJewelFam(string t) => t is "jewelCase" or "doubleJewelCase" or "longJewelCase";
        bool BoxDvd(string t) => IsBox(t) || IsDvd(t);

        // Model Type (always)
        var modelType = Combo(S(116), y, RE - S(116));
        foreach (var t in ModelTypes) modelType.Items.Add(t.label);
        modelType.SelectedIndex = Math.Max(0, Array.FindIndex(ModelTypes, t => string.Equals(t.val, Get(cur, "ModelType"), StringComparison.OrdinalIgnoreCase)));
        Row(_ => true, Lbl("Model Type:", X0, y), modelType); y += S(40);

        // Force Model Size (box, dvd): checkbox row + Width/Height/Depth row.
        var szParts = Get(cur, "ModelSizeString").Split(';');
        bool hasStoredSize = HasSize(cur);
        var sizeChk = Chk("Force Model Size:", X0, y, hasStoredSize);
        Row(BoxDvd, sizeChk); y += S(28);
        var w = Num(S(56), y, S(126), szParts.ElementAtOrDefault(0), 1m);
        var h = Num(S(246), y, S(126), szParts.ElementAtOrDefault(1), 1m);
        var d = Num(S(432), y, RE - S(432), szParts.ElementAtOrDefault(2), 0.001m);
        Row(BoxDvd, Lbl("Width:", X0, y), w, Lbl("Height:", S(192), y), h, Lbl("Depth:", S(382), y), d); y += S(40);

        // ── box: Full Scan + Landscape + Force Box Background Color (=CoverColor), then Spine Width + swatch ──
        var caseColor = PlatformModelStore.ParseArgb(Get(cur, "CaseColor"));
        var coverColor = PlatformModelStore.ParseArgb(Get(cur, "CoverColor"));
        var fullScanB = Chk("Enable Full Scan Images", X0, y, GetBool(cur, "UseFullScanImages"));
        var landscapeB = Chk("Landscape", S(200), y, GetBool(cur, "FullScanIsLandscape"));
        var boxColorChk = Chk("Force Box Background Color:", XR, y, coverColor.HasValue);
        Row(IsBox, fullScanB, landscapeB, boxColorChk); y += S(28);
        var spineWidthB = Num(S(200), y, S(116), Get(cur, "FullImageSpineWidth"), 0.088m);
        var boxSwatch = Swatch(XR, y, RE - XR, S(36), coverColor ?? Color.Black, boxColorChk);
        Row(IsBox, Lbl("Spine Width (%) of Full Scan:", X0, y), spineWidthB, boxSwatch); y += S(48);

        // ── dvd: Case + Cover colors (two half-width swatches), Full Scan + Spine Width (no Landscape) ──
        var caseChk = Chk("Force Case Background Color:", X0, y, caseColor.HasValue);
        var coverChkD = Chk("Force Cover Background Color:", XR, y, coverColor.HasValue);
        Row(IsDvd, caseChk, coverChkD); y += S(26);
        var caseSwatch = Swatch(X0, y, HW, S(36), caseColor ?? Color.Black, caseChk);
        var coverSwatchD = Swatch(XR, y, RE - XR, S(36), coverColor ?? Color.White, coverChkD);
        Row(IsDvd, caseSwatch, coverSwatchD); y += S(48);
        var fullScanD = Chk("Enable Full Scan Images", X0, y, GetBool(cur, "UseFullScanImages"));
        var spineWidthD = Num(S(380), y, RE - S(380), Get(cur, "FullImageSpineWidth"), 0.065m);
        Row(IsDvd, fullScanD, Lbl("Spine Width (%) of Full Scan:", S(200), y), spineWidthD); y += S(40);

        // ── double jewel: Side Spine Image Mode ──
        var spineMode = Combo(S(186), y, RE - S(186));
        foreach (var m in SpineModes) spineMode.Items.Add(m.label);
        spineMode.SelectedIndex = Math.Max(0, Array.FindIndex(SpineModes, m => m.val == Get(cur, "DoubleSpineImageMode")));
        Row(t => t == "doubleJewelCase", Lbl("Side Spine Image Mode:", X0, y), spineMode); y += S(40);

        // ── Spine Style (jewel, double — NOT long): generic Solid/Clear, platform presets, Custom Solid/Clear.
        // A platform preset reveals the Spine Version row; Custom reveals the path+browse row; generic reveals neither.
        var spineStyle = Combo(S(116), y, RE - S(116));
        var styleKinds = new List<string>();
        string CurKind() => spineStyle.SelectedIndex >= 0 && spineStyle.SelectedIndex < styleKinds.Count ? styleKinds[spineStyle.SelectedIndex] : "";
        bool HasSpineStyle(string t) => t is "jewelCase" or "doubleJewelCase";
        // LB's per-type option lists (photographed from the real dropdowns): single jewel = Solid / Empty Clear /
        // Sega Dreamcast / Sony Playstation / Custom Clear / Custom Solid (Clear BEFORE Solid); DOUBLE jewel =
        // ONLY "Clear Spine" + "Sony Playstation Spine" (no solid, no Sega, no custom). Rebuilt on type change,
        // keeping the current choice when it still exists (else first item, LB's default).
        void RebuildSpineStyle(string t, string keepKind)
        {
            spineStyle.Items.Clear(); styleKinds.Clear();
            if (t == "doubleJewelCase")
            {
                spineStyle.Items.Add("Clear Spine"); styleKinds.Add("clear");
                spineStyle.Items.Add("Sony Playstation Spine"); styleKinds.Add("preset:1");
            }
            else
            {
                spineStyle.Items.Add("Solid Spine"); styleKinds.Add("solid");
                spineStyle.Items.Add("Empty Clear Spine"); styleKinds.Add("clear");
                spineStyle.Items.Add("Sega Dreamcast Spine"); styleKinds.Add("preset:0");
                spineStyle.Items.Add("Sony Playstation Spine"); styleKinds.Add("preset:1");
                spineStyle.Items.Add("Custom Clear Spine"); styleKinds.Add("customClear");
                spineStyle.Items.Add("Custom Solid Spine"); styleKinds.Add("customSolid");
            }
            int idx = styleKinds.IndexOf(keepKind);
            spineStyle.SelectedIndex = idx >= 0 ? idx : 0;
        }
        Row(HasSpineStyle, Lbl("Spine Style:", X0, y), spineStyle); y += S(40);

        var spineVersion = Combo(S(116), y, RE - S(116));
        void PopulateVersions(int presetIndex, string selectSuffix)
        {
            spineVersion.Items.Clear();
            if (presetIndex < 0 || presetIndex >= SpinePresets.Length) return;
            var vs = SpinePresets[presetIndex].versions;
            foreach (var v in vs) spineVersion.Items.Add(v.label);
            int sel = Array.FindIndex(vs, v => string.Equals(v.suffix, selectSuffix, StringComparison.OrdinalIgnoreCase));
            spineVersion.SelectedIndex = Math.Max(0, sel);
        }
        Row(t => HasSpineStyle(t) && CurKind().StartsWith("preset:"), Lbl("Spine Version:", X0, y), spineVersion); y += S(40);

        var spinePath = new TextBox { Location = new Point(S(186), y), Width = RE - S(186) - S(84), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        var spineBrowse = new Button { Text = "Browse…", Location = new Point(RE - S(76), y - S(1)), Size = new Size(S(76), S(25)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        spineBrowse.Click += (_, _) => { using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" }; if (dlg.ShowDialog() == DialogResult.OK) spinePath.Text = dlg.FileName; };
        Row(t => HasSpineStyle(t) && CurKind() is "customSolid" or "customClear", Lbl("Custom Spine Image Path:", X0, y), spinePath, spineBrowse); y += S(36);
        var initialSpine = ParseSpine(Get(cur, "FrontSpineImage"), GetBool(cur, "FrontSpineIsClear"));
        if (initialSpine.kind is "customSolid" or "customClear") spinePath.Text = Get(cur, "FrontSpineImage");
        if (initialSpine.preset >= 0) PopulateVersions(initialSpine.preset, initialSpine.suffix);
        RebuildSpineStyle(ModelTypes[Math.Max(0, modelType.SelectedIndex)].val, initialSpine.kind);

        // ── jewel family: Cover color (full-width swatch) + Plain Text Title + font + text color (=CaseColor) ──
        var coverChkJ = Chk("Force Cover Background Color:", X0, y, coverColor.HasValue);
        Row(IsJewelFam, coverChkJ); y += S(26);
        var coverSwatchJ = Swatch(X0, y, W, S(36), coverColor ?? Color.Black, coverChkJ);   // jewel cover default = black (LB)
        Row(IsJewelFam, coverSwatchJ); y += S(48);
        string chosenFont = Get(cur, "LogoFont");
        var textTitle = Chk("Use Plain Text Title Instead of Clear Logo", X0, y, chosenFont.Length > 0);
        Row(IsJewelFam, textTitle, Lbl("Text Foreground Color", XR, y)); y += S(26);
        var fontBtn = new Button { Text = chosenFont.Length > 0 ? chosenFont : "Title Font", Location = new Point(X0, y), Size = new Size(HW, S(36)), FlatStyle = FlatStyle.Flat, BackColor = Bg, ForeColor = SubFg, FlatAppearance = { BorderColor = SubFg, BorderSize = 1 } };
        fontBtn.Click += (_, _) => { using var fd = new FontDialog(); try { if (chosenFont.Length > 0) fd.Font = new Font(chosenFont, 12f); } catch { } if (fd.ShowDialog() == DialogResult.OK) { chosenFont = fd.Font.Name; fontBtn.Text = chosenFont; } };
        var textSwatch = Swatch(XR, y, RE - XR, S(36), caseColor ?? Color.White, null);
        Row(IsJewelFam, fontBtn, textSwatch); y += S(48);

        // ── per-side groups (boxed, header strip) laid out on a 2-column grid under the last row ──
        var spineSides = PlatformModelStore.ParseSides(Get(cur, "SpineRotation"));
        var logoSides = PlatformModelStore.ParseSides(Get(cur, "LogoRotation"));
        var sideCtrls = new (CheckBox spine, ComboBox spineRot, CheckBox logo, ComboBox logoRot)[4];
        Func<string, bool>[] sideVis =
        {
            t => IsBox(t) || IsJewelFam(t) || IsDvd(t),   // Left (dvd's single "Spine" group)
            IsBox,                                        // Top
            t => IsBox(t) || IsJewelFam(t),               // Right
            IsBox,                                        // Bottom
        };
        for (int i = 0; i < 4; i++)
        {
            var grp = new Panel { Size = new Size(HW, grpH), BackColor = GroupBody };
            var hdr = new Label { Dock = DockStyle.Top, Height = S(24), Text = "  " + SideNames[i], BackColor = Panel2, ForeColor = Fg, TextAlign = ContentAlignment.MiddleLeft };
            var ds = new CheckBox { Text = "Draw Spine Image", Location = new Point(S(8), S(31)), AutoSize = true, ForeColor = Fg, BackColor = GroupBody, Checked = spineSides[i].draw };
            var dsRotL = new Label { Text = "Rotation:", Location = new Point(S(152), S(34)), AutoSize = true, ForeColor = SubFg, BackColor = GroupBody };
            var dsRot = RotCombo(S, S(208), S(31), spineSides[i].rot);
            var dl = new CheckBox { Text = "Draw Clear Logo Image", Location = new Point(S(8), S(59)), AutoSize = true, ForeColor = Fg, BackColor = GroupBody, Checked = logoSides[i].draw };
            var dlRotL = new Label { Text = "Rotation:", Location = new Point(S(152), S(62)), AutoSize = true, ForeColor = SubFg, BackColor = GroupBody };
            var dlRot = RotCombo(S, S(208), S(59), logoSides[i].rot);
            grp.Controls.AddRange(new Control[] { hdr, ds, dsRotL, dsRot, dl, dlRotL, dlRot });
            sideHeaders[i] = hdr; sideGroups[i] = grp; sideCtrls[i] = (ds, dsRot, dl, dlRot);
            left.Controls.Add(grp);
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
                if (vis) cy += height + S(10);   // compact: only visible rows advance the cursor
            }
            int gy = cy + S(4), col = 0;
            for (int i = 0; i < 4; i++)
            {
                bool vis = sideVis[i](t);
                var g = sideGroups[i];
                g.Visible = vis; g.Enabled = on && vis;
                if (!vis) continue;
                g.Location = new Point(X0 + col * (HW + S(8)), gy);
                if (col == 1) gy += grpH + S(10);
                col ^= 1;
            }
            sideHeaders[0].Text = IsDvd(t) ? "  Spine" : "  Left Side";   // dvd shows a single "Spine" group

            // Dependent-control gating (LB greys these out until their master checkbox is on):
            // W/H/D need Force Model Size; Landscape + Spine Width need Full Scan; swatches need their
            // color checkbox; Title Font + text color need Plain Text Title.
            w.Enabled = w.Enabled && sizeChk.Checked; h.Enabled = h.Enabled && sizeChk.Checked; d.Enabled = d.Enabled && sizeChk.Checked;
            landscapeB.Enabled = landscapeB.Enabled && fullScanB.Checked;
            spineWidthB.Enabled = spineWidthB.Enabled && fullScanB.Checked;
            spineWidthD.Enabled = spineWidthD.Enabled && fullScanD.Checked;
            boxSwatch.Enabled = boxSwatch.Enabled && boxColorChk.Checked;
            caseSwatch.Enabled = caseSwatch.Enabled && caseChk.Checked;
            coverSwatchD.Enabled = coverSwatchD.Enabled && coverChkD.Checked;
            coverSwatchJ.Enabled = coverSwatchJ.Enabled && coverChkJ.Checked;
            fontBtn.Enabled = fontBtn.Enabled && textTitle.Checked;
            textSwatch.Enabled = textSwatch.Enabled && textTitle.Checked;
        }
        overrideChk.CheckedChanged += (_, _) => Refresh();
        foreach (var master in new[] { sizeChk, fullScanB, fullScanD, boxColorChk, caseChk, coverChkD, coverChkJ, textTitle })
            master.CheckedChanged += (_, _) => Refresh();
        modelType.SelectedIndexChanged += (_, _) =>
        {
            // No stored/forced size → track LB's per-type defaults (box 1/1/0.001, dvd 0.7/1/0.065).
            string t = ModelTypes[Math.Max(0, modelType.SelectedIndex)].val;
            if (!sizeChk.Checked && !hasStoredSize)
            { SetNum(w, IsDvd(t) ? 0.7m : 1m); SetNum(h, 1m); SetNum(d, IsDvd(t) ? 0.065m : 0.001m); }
            if (HasSpineStyle(t)) RebuildSpineStyle(t, CurKind());   // jewel↔double have different option lists
            Refresh();
        };
        spineStyle.SelectedIndexChanged += (_, _) =>
        {
            var k = CurKind();
            if (k.StartsWith("preset:")) PopulateVersions(int.Parse(k.Substring(7)), "");   // repopulate (versions differ per preset)
            Refresh();
        };
        Refresh();

        void Apply()
        {
            if (readOnly) return;
            if (!overrideChk.Checked) { write(null); return; }   // override off → remove block
            string t = ModelTypes[Math.Max(0, modelType.SelectedIndex)].val;
            var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ModelType"] = t };
            if (BoxDvd(t) && sizeChk.Checked) f["ModelSizeString"] = $"{NumV(w)};{NumV(h)};{NumV(d)}";
            if (IsBox(t))
            {
                f["UseFullScanImages"] = fullScanB.Checked ? "true" : "false";
                f["FullScanIsLandscape"] = landscapeB.Checked ? "true" : "false";
                if (spineWidthB.Value > 0) f["FullImageSpineWidth"] = NumV(spineWidthB);
                if (boxColorChk.Checked) f["CoverColor"] = PlatformModelStore.ToArgb(boxSwatch.BackColor);
            }
            if (IsDvd(t))
            {
                f["UseFullScanImages"] = fullScanD.Checked ? "true" : "false";
                f["FullScanIsLandscape"] = "false";
                if (spineWidthD.Value > 0) f["FullImageSpineWidth"] = NumV(spineWidthD);
                if (caseChk.Checked) f["CaseColor"] = PlatformModelStore.ToArgb(caseSwatch.BackColor);
                if (coverChkD.Checked) f["CoverColor"] = PlatformModelStore.ToArgb(coverSwatchD.BackColor);
            }
            if (t == "doubleJewelCase") f["DoubleSpineImageMode"] = SpineModes[Math.Max(0, spineMode.SelectedIndex)].val;
            if (HasSpineStyle(t))
            {
                var (img, clear) = SpineStyleValue(spineStyle, styleKinds, spinePath, spineVersion, t);
                if (img != null) f["FrontSpineImage"] = img;
                f["FrontSpineIsClear"] = clear ? "true" : "false";
            }
            if (IsJewelFam(t))
            {
                if (coverChkJ.Checked) f["CoverColor"] = PlatformModelStore.ToArgb(coverSwatchJ.BackColor);
                if (textTitle.Checked && chosenFont.Length > 0) f["LogoFont"] = chosenFont;
                if (textTitle.Checked) f["CaseColor"] = PlatformModelStore.ToArgb(textSwatch.BackColor);
            }
            // Sides → CSV. Only the visible side groups for this type contribute; hidden ones stay empty.
            var spineArr = new (bool, int)[4]; var logoArr = new (bool, int)[4];
            for (int i = 0; i < 4; i++)
            {
                bool sideVisible = sideGroups[i].Visible;
                spineArr[i] = sideVisible ? (sideCtrls[i].spine.Checked, RotV(sideCtrls[i].spineRot)) : (false, 0);
                logoArr[i] = sideVisible ? (sideCtrls[i].logo.Checked, RotV(sideCtrls[i].logoRot)) : (false, 0);
            }
            f["SpineRotation"] = PlatformModelStore.BuildSides(spineArr);
            f["LogoRotation"] = PlatformModelStore.BuildSides(logoArr);
            write(f);
        }
        return (root, Apply);

        // local scaled-control factories (capture S)
        ComboBox Combo(int x, int yy, int wd) => new()
        { Location = new Point(x, yy), Width = wd, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        NumericUpDown Num(int x, int yy, int wd, string? v, decimal def)
        {
            var n = new NumericUpDown { Location = new Point(x, yy), Width = wd, DecimalPlaces = 3, Increment = 0.001M, Minimum = 0, Maximum = 100, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
            n.Value = decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv) && dv > 0 ? Math.Min(100, dv) : def;
            return n;
        }
        Button Swatch(int x, int yy, int wd, int ht, Color init, CheckBox? pair)
        {
            var sw = new Button { Location = new Point(x, yy), Size = new Size(wd, ht), FlatStyle = FlatStyle.Flat, BackColor = init, FlatAppearance = { BorderColor = SubFg, BorderSize = 1 } };
            sw.Click += (_, _) => { using var dcd = new ColorDialog { Color = sw.BackColor, FullOpen = true }; if (dcd.ShowDialog() == DialogResult.OK) { sw.BackColor = dcd.Color; if (pair != null) pair.Checked = true; } };
            return sw;
        }
    }

    // ── right-hand "3D Model Preview" panel — placeholder reserving LB's preview spot ──
    private static Panel BuildPreview(Func<int, int> S)
    {
        var p = new Panel { Dock = DockStyle.Right, Width = S(348), BackColor = Bg, Padding = new Padding(S(10)) };
        var header = new Label { Dock = DockStyle.Top, Height = S(30), Text = "  3D Model Preview", BackColor = Panel2, ForeColor = Fg, TextAlign = ContentAlignment.MiddleLeft };
        var headGap = new Panel { Dock = DockStyle.Top, Height = S(10), BackColor = Bg };
        var btn = new Button { Dock = DockStyle.Bottom, Height = S(32), Text = "Switch Sample Game", FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = SubFg, Enabled = false, FlatAppearance = { BorderSize = 0 } };
        var btnGap = new Panel { Dock = DockStyle.Bottom, Height = S(10), BackColor = Bg };
        var box = new Panel { Dock = DockStyle.Fill, BackColor = GroupBody, BorderStyle = BorderStyle.FixedSingle };
        box.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "3D preview\n(not yet implemented)", ForeColor = SubFg, BackColor = GroupBody, TextAlign = ContentAlignment.MiddleCenter });
        p.Controls.Add(box);
        p.Controls.Add(btnGap);
        p.Controls.Add(btn);
        p.Controls.Add(headGap);
        p.Controls.Add(header);
        return p;
    }

    // ── control helpers ──
    private static void SetNum(NumericUpDown n, decimal v) { try { n.Value = v; } catch { } }
    private static string NumV(NumericUpDown n) => n.Value.ToString(CultureInfo.InvariantCulture);
    private static ComboBox RotCombo(Func<int, int> S, int x, int y, int rot)
    {
        var c = new ComboBox { Location = new Point(x, y), Width = S(62), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg };
        foreach (var r in Rotations) c.Items.Add(r + "°");
        c.SelectedIndex = Math.Max(0, Array.IndexOf(Rotations, rot));
        return c;
    }
    private static int RotV(ComboBox c) => Rotations[Math.Max(0, c.SelectedIndex)];
    private static Color Blend(Color a, Color b) => Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);

    // Decode a stored FrontSpineImage (+IsClear) into a style kind, preset index (-1 = none) and version suffix.
    private static (string kind, int preset, string suffix) ParseSpine(string frontSpineImage, bool clear)
    {
        if (string.IsNullOrEmpty(frontSpineImage)) return (clear ? "clear" : "solid", -1, "");

        if (frontSpineImage.StartsWith("{Resources}", StringComparison.OrdinalIgnoreCase))
        {
            // "{Resources}\<platform>[ - <suffix>[ Double Jewel]]" → longest platform prefix, remainder = version.
            string rel = frontSpineImage.Substring(frontSpineImage.IndexOf('\\') + 1).Trim();
            int best = -1, bestLen = -1;
            for (int i = 0; i < SpinePresets.Length; i++)
            {
                var p = SpinePresets[i].platform;
                if ((rel.Equals(p, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(p + " - ", StringComparison.OrdinalIgnoreCase)) && p.Length > bestLen)
                { best = i; bestLen = p.Length; }
            }
            if (best >= 0)
            {
                string suffix = rel.Length > SpinePresets[best].platform.Length
                    ? rel.Substring(SpinePresets[best].platform.Length).TrimStart(' ', '-').Trim() : "";
                // Double-jewel variants store "<region> Double Jewel" — strip the tail to match the version list.
                if (suffix.EndsWith(" Double Jewel", StringComparison.OrdinalIgnoreCase))
                    suffix = suffix.Substring(0, suffix.Length - " Double Jewel".Length).Trim();
                return ("preset:" + best, best, suffix);
            }
            return (clear ? "clear" : "solid", -1, "");   // unknown preset → generic fallback
        }

        return (clear ? "customClear" : "customSolid", -1, "");   // a file path = Custom
    }

    private static (string? img, bool clear) SpineStyleValue(ComboBox combo, List<string> kinds, TextBox path, ComboBox version, string modelType)
    {
        int idx = combo.SelectedIndex;
        string kind = idx >= 0 && idx < kinds.Count ? kinds[idx] : "solid";
        switch (kind)
        {
            case "clear": return ("", true);
            case "customSolid": return (path.Text.Trim(), false);
            case "customClear": return (path.Text.Trim(), true);
            case var k when k.StartsWith("preset:"):
                int pi = int.Parse(k.Substring(7));
                var vs = SpinePresets[pi].versions;
                int vidx = version.SelectedIndex;
                string suffix = vidx >= 0 && vidx < vs.Length ? vs[vidx].suffix : "";
                // Explicit version on a double jewel folds the case variant into the name; Auto-Detect stays bare.
                if (suffix.Length > 0 && modelType == "doubleJewelCase") suffix += " Double Jewel";
                string img = "{Resources}\\" + SpinePresets[pi].platform + (suffix.Length > 0 ? " - " + suffix : "");
                return (img, true);                          // embedded spine presets are clear overlays
            default: return ("", false);                     // solid
        }
    }

    private static string Get(Dictionary<string, string>? m, string k) => m != null && m.TryGetValue(k, out var v) ? (v ?? "") : "";
    private static bool GetBool(Dictionary<string, string>? m, string k) => string.Equals(Get(m, k), "true", StringComparison.OrdinalIgnoreCase);
    private static bool HasSize(Dictionary<string, string>? m) => !string.IsNullOrWhiteSpace(Get(m, "ModelSizeString"));
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
