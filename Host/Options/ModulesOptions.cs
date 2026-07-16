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
}
