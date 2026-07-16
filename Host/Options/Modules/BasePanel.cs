// Base module config panel — the Extended-database settings: the ScreenScraper account used for credentialed
// media downloads, the image-mirror base URL, and the extended-DB download / update button. The password is
// stored encrypted (LbSettingsCrypto); values persist to LiteBox.ini [Base] via BaseCredentials on apply.
//
// Split out of ModulesOptions verbatim; ModulesOptions.ModuleConfigPanel dispatches here for LbModule.Base.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class BasePanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
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
