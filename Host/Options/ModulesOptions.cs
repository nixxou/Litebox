// Options → Modules: the multi-tab module manager.
//   Tab 1 ("Modules")  — lists every module with an enable checkbox + description; the master on/off page.
//   Tabs 2..N          — one per module, its own settings (placeholder until each module's port lands).
//
// Native LiteBox UI (no ExtendDB dependency). Enable state is persisted by the section's apply callback through
// LbModules.SetOn, so it follows the standard OptionsWindow Apply/OK flow. Per-module config panels plug in here
// as they are ported (replace the placeholder in ModuleConfigPanel).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Media;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Rom;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class ModulesOptions
{
    public static (Control panel, Action apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg;
        var Field = LiteBoxTheme.Panel2; var PanelC = LiteBoxTheme.PanelC;

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(S(116), S(26)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                sel ? Color.White : Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        // ── Tab 1: the module list ────────────────────────────────────────────────
        var listPage = new TabPage("Modules") { BackColor = Bg, UseVisualStyleBackColor = false };
        var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(14), S(12), S(14), S(8)) };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg,
        };
        flow.Controls.Add(new Label
        {
            Text = "Enable the ExtendDB features you want, natively in LiteBox. Each is independent; use the tabs above for a module's own settings.",
            AutoSize = true, MaximumSize = new Size(S(680), 0), ForeColor = Sub, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, S(14)),
        });

        var checks = new List<(LbModule module, CheckBox cb)>();
        foreach (var m in LbModules.Catalog)
        {
            var cb = new CheckBox
            {
                Text = m.Title + (m.Ready ? "" : "   (coming soon)"),
                AutoSize = true, Checked = LbModules.On(m.Module), Enabled = !readOnly,
                ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, S(1)),
            };
            var desc = new Label
            {
                Text = m.Description, AutoSize = true, MaximumSize = new Size(S(680), 0),
                ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(S(22), 0, 0, S(14)),
            };
            flow.Controls.Add(cb);
            flow.Controls.Add(desc);
            checks.Add((m.Module, cb));
        }
        scroll.Controls.Add(flow);
        listPage.Controls.Add(scroll);
        tabs.TabPages.Add(listPage);

        // ── Tabs 2..N: one per module (its own settings) ──────────────────────────
        var configApplies = new List<Action>();
        foreach (var m in LbModules.Catalog)
        {
            var page = new TabPage(m.Title.Length > 16 ? m.Key : m.Title) { BackColor = Bg, UseVisualStyleBackColor = false };
            var (cfgPanel, cfgApply) = ModuleConfigPanel(m, dpiS, readOnly);
            page.Controls.Add(cfgPanel);
            if (cfgApply != null) configApplies.Add(cfgApply);
            tabs.TabPages.Add(page);
        }

        root.Controls.Add(tabs);

        void Apply()
        {
            if (readOnly) return;
            foreach (var (module, cb) in checks) LbModules.SetOn(module, cb.Checked);
            foreach (var a in configApplies) a();
        }
        return (root, Apply);
    }

    /// <summary>The per-module settings panel + its optional apply. Base has real settings (ScreenScraper
    /// account + image mirror); the others are placeholders until each port lands.</summary>
    private static (Control panel, Action? apply) ModuleConfigPanel(LbModules.Info m, float dpiS, bool readOnly)
    {
        if (m.Module == LbModule.Base) return BaseConfigPanel(dpiS, readOnly);
        if (m.Module == LbModule.Parental) return ParentalConfigPanel(dpiS, readOnly);
        if (m.Module == LbModule.Rom) return RomConfigPanel(dpiS, readOnly);
        return (Placeholder(m, dpiS), null);
    }

    /// <summary>Parental settings: manages BigBox's NATIVE parental PIN (LockPin in BigBoxSettings.xml) —
    /// one PIN shared with BigBox, set or cleared here, visible to BigBox on its next start.</summary>
    private static (Control panel, Action? apply) ParentalConfigPanel(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg;
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(10)) };

        p.Controls.Add(new Label { Text = "BigBox parental PIN", AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(4), S(6)), Font = new Font("Segoe UI", 10f, FontStyle.Bold) });
        p.Controls.Add(new Label
        {
            Text = "This is BigBox's own four-digit PIN (BigBoxSettings.xml), not a separate code. Set or clear it here; BigBox picks the change up on its next start. Leave empty for no PIN.",
            AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Sub, BackColor = Bg,
            Location = new Point(S(4), S(30)), Font = new Font("Segoe UI", 8.5f),
        });

        bool available = Data.BigBoxPin.Available;
        p.Controls.Add(new Label { Text = "PIN", AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(4), S(78)), Font = new Font("Segoe UI", 8.5f) });
        var pin = new TextBox
        {
            Location = new Point(S(4), S(96)), Width = S(120), BackColor = LiteBoxTheme.PanelC, ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f), UseSystemPasswordChar = true,
            MaxLength = 8, ReadOnly = readOnly || !available,
        };
        p.Controls.Add(pin);
        var show = new CheckBox { Text = "Show", AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(136), S(97)), Font = new Font("Segoe UI", 8.5f) };
        show.CheckedChanged += (_, _) => pin.UseSystemPasswordChar = !show.Checked;
        p.Controls.Add(show);

        var status = new Label { AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Sub, BackColor = Bg, Location = new Point(S(4), S(130)), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic) };
        p.Controls.Add(status);

        if (!available)
            status.Text = "BigBoxSettings.xml was not found (BigBox has never run on this install).";
        else
        {
            try { pin.Text = Data.BigBoxPin.Current(); } catch { }
            status.Text = pin.Text.Length > 0 ? "A PIN is currently set." : "No PIN is currently set.";
        }

        void Apply()
        {
            if (readOnly || !available) return;
            var v = (pin.Text ?? "").Trim();
            if (v.Length > 0 && (v.Length != 4 || !v.All(char.IsAsciiDigit))) return;   // BigBox PINs are 4 digits
            Data.BigBoxPin.Set(v);
        }
        return (p, Apply);
    }

    private static Control Placeholder(LbModules.Info m, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(8)) };
        p.Controls.Add(new Label
        {
            Text = m.Title, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            Location = new Point(S(4), S(6)), Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        });
        p.Controls.Add(new Label
        {
            Text = m.Description, AutoSize = true, MaximumSize = new Size(S(640), 0),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg, Location = new Point(S(4), S(32)),
            Font = new Font("Segoe UI", 8.5f),
        });
        p.Controls.Add(new Label
        {
            Text = "This module's settings will appear here as it is ported into LiteBox.",
            AutoSize = true, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Location = new Point(S(4), S(72)), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
        });
        return p;
    }

    /// <summary>Extended-database settings: the ScreenScraper account used for credentialed media downloads, and
    /// the image-mirror base URL. The password is stored encrypted (LbSettingsCrypto); values persist to
    /// LiteBox.ini [Base] via BaseCredentials on apply.</summary>
    private static (Control panel, Action? apply) BaseConfigPanel(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg; var PanelC = LiteBoxTheme.PanelC;
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(10)) };

        Label Head(string t, int y) => new() { Text = t, AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(4), S(y)), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        Label Cap(string t, int y) => new() { Text = t, AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(4), S(y)), Font = new Font("Segoe UI", 8.5f) };
        TextBox Field(int y, bool pw = false) => new() { Location = new Point(S(4), S(y)), Width = S(300), BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f), UseSystemPasswordChar = pw, ReadOnly = readOnly };

        p.Controls.Add(Head("ScreenScraper account", 6));
        p.Controls.Add(Cap("Used to download medias through your personal ScreenScraper quota. Leave blank to use the other sources only.", 30));
        p.Controls.Add(Cap("Username", 58));
        var user = Field(76); p.Controls.Add(user);
        p.Controls.Add(Cap("Password", 106));
        var pass = Field(124, pw: true); p.Controls.Add(pass);

        p.Controls.Add(Head("Image mirror", 164));
        p.Controls.Add(Cap("Base URL of the ExtendDB image mirror. Leave as the default unless you have a custom endpoint.", 188));
        var mirror = Field(214); mirror.Width = S(440); p.Controls.Add(mirror);

        // ── Extended database (download / update) ─────────────────────────────
        p.Controls.Add(Head("Extended database", 256));
        var dbStatus = Cap("", 280); dbStatus.MaximumSize = new Size(S(640), 0); p.Controls.Add(dbStatus);
        void RefreshDbStatus()
        {
            try
            {
                var path = Data.ExtDbDownloader.TargetPath;
                dbStatus.Text = System.IO.File.Exists(path)
                    ? $"Installed: {path} ({new System.IO.FileInfo(path).Length / (1 << 20)} MB)"
                    : MetadataDb.ExtendedDbPath != null
                        ? $"Using the legacy plugin copy: {MetadataDb.ExtendedDbPath}"
                        : "Not installed. The extra metadata and non-LaunchBox medias need it.";
            }
            catch { dbStatus.Text = "Status unavailable."; }
        }
        RefreshDbStatus();
        var dbBtn = new Button
        {
            Text = "Check for updates && install", AutoSize = true, Location = new Point(S(4), S(306)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = Fg, Enabled = !readOnly,
        };
        dbBtn.FlatAppearance.BorderColor = LiteBoxTheme.Panel2;
        var dbProg = Cap("", 340); dbProg.MaximumSize = new Size(S(640), 0); p.Controls.Add(dbProg);
        p.Controls.Add(dbBtn);
        dbBtn.Click += async (_, _) =>
        {
            dbBtn.Enabled = false;
            var prog = new Progress<string>(msg => { try { if (!dbProg.IsDisposed) dbProg.Text = msg; } catch { } });
            try { await Data.ExtDbDownloader.DownloadAndInstallAsync(prog, System.Threading.CancellationToken.None); }
            catch (Exception ex) { try { dbProg.Text = "Failed: " + ex.Message; } catch { } }
            finally { try { if (!dbBtn.IsDisposed) { dbBtn.Enabled = true; RefreshDbStatus(); } } catch { } }
        };

        // Prefill.
        try
        {
            var acc = BaseCredentials.UserAccount();
            if (acc is { } a) { user.Text = a.User; pass.Text = a.Password; }
            mirror.Text = BaseCredentials.RemoteImageBaseUrl();
        }
        catch { }

        void Apply()
        {
            if (readOnly) return;
            BaseCredentials.SetUserAccount(user.Text, pass.Text);
            try
            {
                var cfg = LiteBoxConfig.LoadForExe();
                cfg.SetSec(BaseCredentials.Section, "RemoteImageBaseUrl", (mirror.Text ?? "").Trim());
                cfg.Save();
            }
            catch { }
        }
        return (p, Apply);
    }

    /// <summary>ROM-extractor (ArchiveMGS) settings — R1 MVP: the global cache root + Browse, the cache size
    /// band, and the three global extension lists. The per-(platform, emulator) cascade editor lands with the
    /// engine that consumes it (R2/R3). Globals persist to LiteBox.ini [Rom]; the profiles (edited later) to
    /// rom-profiles.json — both written by RomConfig.Save().</summary>
    private static (Control panel, Action? apply) RomConfigPanel(float dpiS, bool readOnly)
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
