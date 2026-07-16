// ROM module config panel — the ROM-extractor (ArchiveMGS) settings: the global cache root + Browse, the cache
// size band, the global extension lists, and the GlobalDefault-profile fields (disc-image conversion, copy /
// texture / RAM-disk). The full per-(platform, emulator) cascade editor lands with the engine that consumes it.
// Globals persist to LiteBox.ini [Rom]; the profiles to rom-profiles.json — both written by RomConfig.Save().
//
// Split out of ModulesOptions verbatim (with its ConvertRules / RamDisk helpers); ModulesOptions
// .ModuleConfigPanel dispatches here for LbModule.Rom.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Rom;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class RomPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg; var PanelC = LiteBoxTheme.PanelC;
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(10)) };

        Label Head(string t, int y) => new() { Text = t, AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(4), S(y)), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        Label Cap(string t, int y, int w = 640) => new() { Text = t, AutoSize = true, MaximumSize = new Size(S(w), 0), ForeColor = Sub, BackColor = Bg, Location = new Point(S(4), S(y)), Font = new Font("Segoe UI", 8.5f) };
        Label CapAt(string t, int x, int y) => new() { Text = t, AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(x), S(y)), Font = new Font("Segoe UI", 8.5f) };
        TextBox Field(int x, int y, int w) => new() { Location = new Point(S(x), S(y)), Width = S(w), BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f), ReadOnly = readOnly };

        var cfg = RomConfig.Instance;

        // ── Cache location ────────────────────────────────────────────────────
        p.Controls.Add(Head("Cache location", 6));
        p.Controls.Add(Cap("Where extracted / converted ROMs are cached. Leave as the default unless you want the cache on a faster or larger drive.", 30));
        var cachePath = Field(4, 58, 440); cachePath.Text = cfg.CachePath ?? ""; p.Controls.Add(cachePath);
        var browse = new Button
        {
            Text = "Browse…", AutoSize = true, Location = new Point(S(452), S(56)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = Fg, Enabled = !readOnly,
        };
        browse.FlatAppearance.BorderColor = LiteBoxTheme.Panel2;
        browse.Click += (_, _) =>
        {
            try
            {
                using var dlg = new FolderBrowserDialog { Description = "Choose the ROM extractor cache folder" };
                try { if (!string.IsNullOrWhiteSpace(cachePath.Text) && System.IO.Directory.Exists(cachePath.Text)) dlg.SelectedPath = cachePath.Text; } catch { }
                if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    cachePath.Text = dlg.SelectedPath;
            }
            catch { }
        };
        p.Controls.Add(browse);

        // ── Cache size band ───────────────────────────────────────────────────
        p.Controls.Add(Head("Cache size band", 100));
        p.Controls.Add(Cap("Total LRU budget (GB), and the per-extraction size window (MB). An unpacked ROM smaller than the minimum or larger than the maximum goes to a temporary folder each launch instead of the persistent cache.", 124));
        p.Controls.Add(CapAt("Max cache (GB)", 4, 164));
        var maxGb = Field(4, 182, 90); maxGb.Text = cfg.CacheMaxGb.ToString(); p.Controls.Add(maxGb);
        p.Controls.Add(CapAt("Min per ROM (MB)", 110, 164));
        var minMb = Field(110, 182, 90); minMb.Text = cfg.CacheMinMb.ToString(); p.Controls.Add(minMb);
        p.Controls.Add(CapAt("Max per ROM (MB)", 216, 164));
        var maxMb = Field(216, 182, 90); maxMb.Text = cfg.CacheMaxMb.ToString(); p.Controls.Add(maxMb);

        // ── Extension lists ───────────────────────────────────────────────────
        p.Controls.Add(Head("Extension lists (global)", 226));
        p.Controls.Add(Cap("Comma-separated. Metadata = files that carry no game data (skipped when picking). Archive = extensions that trigger the extractor. Disc image = disc formats handled directly (convert / pass-through).", 250));

        p.Controls.Add(Cap("Metadata extensions", 292));
        var metaExt = Field(4, 310, 560); metaExt.Text = cfg.MetadataExtensions ?? ""; p.Controls.Add(metaExt);
        p.Controls.Add(Cap("Archive extensions", 340));
        var arcExt = Field(4, 358, 560); arcExt.Text = cfg.ArchiveExtensions ?? ""; p.Controls.Add(arcExt);
        p.Controls.Add(Cap("Disc-image extensions", 388));
        var discExt = Field(4, 406, 560); discExt.Text = cfg.DiscImageExtensions ?? ""; p.Controls.Add(discExt);

        // ── R4: the following controls edit the GlobalDefault profile (the bottom of the cascade). The full
        //    per-(platform, emulator) editor is still deferred. ──
        var gd = cfg.GlobalDefault;

        CheckBox Check(string t, int y, bool val) => new()
        {
            Text = t, AutoSize = true, Checked = val, Enabled = !readOnly,
            ForeColor = Fg, BackColor = Bg, Location = new Point(S(4), S(y)), Font = new Font("Segoe UI", 9f),
        };

        // ── Disc-image conversion ─────────────────────────────────────────────
        p.Controls.Add(Head("Disc-image conversion", 440));
        p.Controls.Add(Cap("One rule per line: inputExt = outputFormat (e.g. \"cue/bin = chd\", \"iso = rvz\"). Applies to Convert mode and to \"convert after extract\". Needs chdman.exe / DolphinTool.exe under ThirdParty\\RomExtractor (reused from the ExtendDB plugin if installed).", 464));
        var convertBox = new TextBox
        {
            Location = new Point(S(4), S(506)), Width = S(400), Height = S(70), Multiline = true, ScrollBars = ScrollBars.Vertical,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f), ReadOnly = readOnly, WordWrap = false,
        };
        convertBox.Text = ConvertRulesToText(gd);
        p.Controls.Add(convertBox);
        var convertAfter = Check("Convert after extract (archived disc image → the format above)", 582, gd.ConvertAfterExtract);
        p.Controls.Add(convertAfter);

        p.Controls.Add(Cap("Copy extensions (Copy mode) — files with these extensions are copied to the cache instead of extracted.", 612));
        var copyExt = Field(4, 636, 400); copyExt.Text = gd.CopyExtensions ?? ""; p.Controls.Add(copyExt);

        // ── Texture pack ──────────────────────────────────────────────────────
        p.Controls.Add(Head("Hi-res texture pack", 676));
        var texEnabled = Check("Extract texture files from the archive on launch", 700, gd.TextureEnabled);
        p.Controls.Add(texEnabled);
        p.Controls.Add(Cap("Texture extensions (comma-separated, e.g. \"htc, hts\")", 728));
        var texExt = Field(4, 746, 300); texExt.Text = gd.TextureExtensions ?? ""; p.Controls.Add(texExt);
        p.Controls.Add(Cap("Install path — tokens: {EmuDir} {GameId} {GameTitle}", 776));
        var texPath = Field(4, 794, 560); texPath.Text = gd.TextureExtractPath ?? ""; p.Controls.Add(texPath);

        // ── RAM disk ──────────────────────────────────────────────────────────
        p.Controls.Add(Head("RAM disk (ImDisk)", 834));
        var ramEnabled = Check("Extract to a RAM drive when possible (falls back to the disk cache)", 858, gd.RamDiskEnabled);
        p.Controls.Add(ramEnabled);
        p.Controls.Add(CapAt("Max RAM drive (MB)", 4, 886));
        var ramMax = Field(4, 904, 120); ramMax.Text = gd.RamDiskMaxMb.ToString(); p.Controls.Add(ramMax);
        var ramNote = Cap(RamDiskCapabilityNote(), 936, 640); p.Controls.Add(ramNote);

        // ── Reset ─────────────────────────────────────────────────────────────
        var reset = new Button
        {
            Text = "Reset to defaults", AutoSize = true, Location = new Point(S(4), S(980)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = Fg, Enabled = !readOnly,
        };
        reset.FlatAppearance.BorderColor = LiteBoxTheme.Panel2;
        reset.Click += (_, _) =>
        {
            if (readOnly) return;
            if (MessageBox.Show("Reset every ROM-extractor setting (cache path, size band, extension lists and all per-platform profiles) to the shipped defaults?",
                                "ROM extractor", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            RomConfig.ResetToDefaults();
            var fresh = RomConfig.Instance;
            cachePath.Text = fresh.CachePath ?? "";
            maxGb.Text = fresh.CacheMaxGb.ToString();
            minMb.Text = fresh.CacheMinMb.ToString();
            maxMb.Text = fresh.CacheMaxMb.ToString();
            metaExt.Text = fresh.MetadataExtensions ?? "";
            arcExt.Text = fresh.ArchiveExtensions ?? "";
            discExt.Text = fresh.DiscImageExtensions ?? "";
            var fgd = fresh.GlobalDefault;
            convertBox.Text = ConvertRulesToText(fgd);
            convertAfter.Checked = fgd.ConvertAfterExtract;
            copyExt.Text = fgd.CopyExtensions ?? "";
            texEnabled.Checked = fgd.TextureEnabled;
            texExt.Text = fgd.TextureExtensions ?? "";
            texPath.Text = fgd.TextureExtractPath ?? "";
            ramEnabled.Checked = fgd.RamDiskEnabled;
            ramMax.Text = fgd.RamDiskMaxMb.ToString();
        };
        p.Controls.Add(reset);

        void Apply()
        {
            if (readOnly) return;
            var c = RomConfig.Instance;
            var path = (cachePath.Text ?? "").Trim();
            if (path.Length > 0) c.CachePath = path;
            if (int.TryParse((maxGb.Text ?? "").Trim(), out var g) && g > 0) c.CacheMaxGb = g;
            if (int.TryParse((minMb.Text ?? "").Trim(), out var mn) && mn >= 0) c.CacheMinMb = mn;
            if (int.TryParse((maxMb.Text ?? "").Trim(), out var mx) && mx > 0) c.CacheMaxMb = mx;
            c.MetadataExtensions = (metaExt.Text ?? "").Trim();
            c.ArchiveExtensions = (arcExt.Text ?? "").Trim();
            c.DiscImageExtensions = (discExt.Text ?? "").Trim();

            // R4 — GlobalDefault profile fields.
            var g2 = c.GlobalDefault;
            g2.Conversions = TextToConvertRules(convertBox.Text);
            g2.ConvertAfterExtract = convertAfter.Checked;
            g2.CopyExtensions = (copyExt.Text ?? "").Trim();
            g2.TextureEnabled = texEnabled.Checked;
            g2.TextureExtensions = (texExt.Text ?? "").Trim();
            g2.TextureExtractPath = (texPath.Text ?? "").Trim();
            g2.RamDiskEnabled = ramEnabled.Checked;
            if (int.TryParse((ramMax.Text ?? "").Trim(), out var rm) && rm > 0) g2.RamDiskMaxMb = rm;

            c.Save();
            RomConfig.Invalidate();   // next reader re-reads from disk
        }
        return (p, Apply);
    }

    /// <summary>Renders a profile's convert table as one "input = output" line per rule.</summary>
    private static string ConvertRulesToText(ArchivePriorityRow row)
    {
        if (row?.Conversions == null || row.Conversions.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var r in row.Conversions)
        {
            if (string.IsNullOrWhiteSpace(r.Input)) continue;
            sb.Append(r.Input.Trim()).Append(" = ").Append((r.Output ?? "").Trim()).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Parses the "input = output" convert-rules text back into a ConvertRule list. Blank / malformed
    /// lines are skipped.</summary>
    private static System.Collections.Generic.List<ConvertRule> TextToConvertRules(string text)
    {
        var list = new System.Collections.Generic.List<ConvertRule>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var input = line.Substring(0, eq).Trim();
            var output = line.Substring(eq + 1).Trim();
            if (input.Length == 0) continue;
            list.Add(new ConvertRule { Input = input, Output = output });
        }
        return list;
    }

    /// <summary>Live ImDisk availability note for the RAM-disk section.</summary>
    private static string RamDiskCapabilityNote()
    {
        try
        {
            if (!ArchiveRamDisk.IsDriverInstalled())
                return "ImDisk driver not detected — RAM disk unavailable; extractions use the disk cache.";
            if (ArchiveRamDisk.IsReady())
                return "ImDisk ready.";
            return "ImDisk driver present but the elevated mount helper is not installed — mounts need admin (falls back to the disk cache).";
        }
        catch { return "ImDisk status unavailable."; }
    }
}
