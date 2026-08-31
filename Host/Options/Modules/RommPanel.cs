// RomM server module config panel.
//
// Three tabs:
//   • Server      — port (+ live in-use probe), LAN allow-list, and the plain warning that this surface has
//                    no TLS: the account password is the only wall between a phone and the whole library.
//   • Account     — the single user name + password. The password is written through RommConfig (PBKDF2 in
//                    litebox-options.db), never into LiteBox.ini, and is never read back for display.
//   • Clients     — the paired clients: mint a pairing code and revoke one.
//   • Library     — what the clients get to see (hidden games, parental-locked games) and how many of an
//                    archive's ROMs a rom advertises.
//
// Ini keys: [RommServer] Port / AllowedIps / Username / ExposeHiddenGames / IgnoreParental /
// LogRequests. Applying restarts a live server so a port or allow-list change takes effect at
// once, the way the Web panel does.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using LbApiHost.Host;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Romm;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Options;

internal static class RommPanel
{
    private const string Sec = "RommServer";

    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);

        // Three scrolling surfaces, one per tab. The groups stay built inline here because Apply()
        // closes over their controls; only which panel they land in changes.
        var pServer = ModulePanelKit.Root(dpiS);
        var pClients = ModulePanelKit.Root(dpiS);
        var pLib = ModulePanelKit.Root(dpiS);

        int y = S(6), yc = S(6), yl = S(6);
        const int GroupW = 560;

        var cfg = LiteBoxConfig.LoadForExe();

        // ── Server ────────────────────────────────────────────────────────────
        var gServer = ModulePanelKit.Group("Server", dpiS);
        gServer.Location = new Point(S(4), y);
        gServer.Size = new Size(S(GroupW), S(214));
        pServer.Controls.Add(gServer);
        y += gServer.Height + S(12);

        var lblPort = ModulePanelKit.Caption("Port:", dpiS);
        lblPort.Location = new Point(S(14), S(28));
        gServer.Controls.Add(lblPort);
        var numPort = new NumericUpDown
        {
            Minimum = 1, Maximum = 65535, Location = new Point(S(70), S(25)), Width = S(90),
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f), Enabled = !readOnly,
        };
        gServer.Controls.Add(numPort);
        var lblPortStatus = ModulePanelKit.Caption("", dpiS);
        lblPortStatus.Location = new Point(S(175), S(28));
        gServer.Controls.Add(lblPortStatus);

        var lblIps = ModulePanelKit.Caption("Allowed IPs (LAN, comma-separated; empty = loopback only):", dpiS);
        lblIps.Location = new Point(S(14), S(62));
        gServer.Controls.Add(lblIps);
        var txtIps = ModulePanelKit.TextField(dpiS, readOnly, width: 500);
        txtIps.Location = new Point(S(14), S(84));
        gServer.Controls.Add(txtIps);

        var warn = ModulePanelKit.Caption(
            "No TLS on this port: traffic is plain HTTP and the account password is the only protection. " +
            "Open it to the LAN only on a network you trust.", dpiS, maxWidth: 520);
        warn.Location = new Point(S(14), S(116));
        warn.ForeColor = Color.FromArgb(220, 180, 120);
        gServer.Controls.Add(warn);

        var lnkAddr = new LinkLabel
        {
            Text = "", AutoSize = true, Location = new Point(S(14), S(150)), BackColor = ModulePanelKit.Bg,
            LinkColor = Color.FromArgb(120, 170, 255), ActiveLinkColor = Color.White,
            Font = new Font("Segoe UI", 9f),
        };
        lnkAddr.LinkClicked += (_, _) => OpenUrl(lnkAddr.Text);
        gServer.Controls.Add(lnkAddr);

        var chkLog = ModulePanelKit.Check(
            @"Log every request to Core\litebox\romm-requests.log", dpiS, readOnly: readOnly);
        chkLog.Location = new Point(S(14), S(176));
        chkLog.Width = S(420);
        gServer.Controls.Add(chkLog);

        void RefreshPortStatus()
        {
            int port = (int)numPort.Value;
            bool ours = false;
            try { ours = RommServer.IsRunning && RommServer.CurrentPort == port; } catch { }
            if (ours)
            {
                lblPortStatus.Text = "● in use by LiteBox (running)";
                lblPortStatus.ForeColor = Color.FromArgb(120, 200, 140);
            }
            else
            {
                bool ok = TryProbePort(port, out var reason);
                lblPortStatus.Text = ok ? "✓ available" : "✗ " + reason;
                lblPortStatus.ForeColor = ok ? Color.FromArgb(120, 200, 140) : Color.IndianRed;
            }
            lnkAddr.Text = $"http://{LocalAddress(txtIps.Text)}:{port}";
        }

        // ── Account ───────────────────────────────────────────────────────────
        var gAccount = ModulePanelKit.Group("Account", dpiS);
        gAccount.Location = new Point(S(4), y);
        gAccount.Size = new Size(S(GroupW), S(150));
        pServer.Controls.Add(gAccount);
        y += gAccount.Height + S(12);

        var accIntro = ModulePanelKit.Caption(
            "One account, used by every client. Clients sign in with these, or pair a token from them.", dpiS, maxWidth: 520);
        accIntro.Location = new Point(S(14), S(24));
        gAccount.Controls.Add(accIntro);

        var lblUser = ModulePanelKit.Caption("User name:", dpiS);
        lblUser.Location = new Point(S(14), S(54));
        gAccount.Controls.Add(lblUser);
        var txtUser = ModulePanelKit.TextField(dpiS, readOnly, width: 220);
        txtUser.Location = new Point(S(100), S(51));
        gAccount.Controls.Add(txtUser);

        var lblPass = ModulePanelKit.Caption("Password:", dpiS);
        lblPass.Location = new Point(S(14), S(88));
        gAccount.Controls.Add(lblPass);
        var txtPass = ModulePanelKit.TextField(dpiS, readOnly, password: true, width: 220);
        txtPass.Location = new Point(S(100), S(85));
        gAccount.Controls.Add(txtPass);

        var lblPassState = ModulePanelKit.Caption("", dpiS);
        lblPassState.Location = new Point(S(334), S(88));
        gAccount.Controls.Add(lblPassState);

        void RefreshPassState()
        {
            bool set = RommConfig.HasPassword;
            lblPassState.Text = set ? "● set (leave blank to keep)" : "✗ not set — the server refuses everything";
            lblPassState.ForeColor = set ? Color.FromArgb(120, 200, 140) : Color.IndianRed;
        }

        var btnSignOut = ModulePanelKit.Button("Sign every client out", dpiS, readOnly);
        btnSignOut.Location = new Point(S(14), S(116));
        btnSignOut.Width = S(180);
        btnSignOut.Click += (_, _) =>
        {
            RommConfig.RotateSigningKey();
            MessageBox.Show("Every issued token is now invalid. Clients will have to sign in again.",
                "RomM server", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        gAccount.Controls.Add(btnSignOut);

        // ── Devices ───────────────────────────────────────────────────────────
        // The pairing flow existed end to end and had no way in: a client asks for an eight-digit code
        // and nothing here could produce one, so "connect a handheld" was unreachable from the UI.
        var gDev = ModulePanelKit.Group("Devices", dpiS);
        gDev.Location = new Point(S(4), yc);
        gDev.Size = new Size(S(GroupW), S(172));
        pClients.Controls.Add(gDev);
        yc += gDev.Height + S(12);

        var devIntro = ModulePanelKit.Caption(
            "Pairing hands a client its own token without typing one on the device: press the button, "
          + "then enter the code on the client. The code lasts five minutes and works once.", dpiS, maxWidth: 520);
        devIntro.Location = new Point(S(14), S(24));
        gDev.Controls.Add(devIntro);

        var btnPair = ModulePanelKit.Button("Pair a device", dpiS, readOnly);
        btnPair.Location = new Point(S(14), S(66));
        btnPair.Width = S(150);
        gDev.Controls.Add(btnPair);

        var txtCode = ModulePanelKit.TextField(dpiS, readOnly: true, width: 170);
        txtCode.Location = new Point(S(180), S(64));
        txtCode.Font = new Font(FontFamily.GenericMonospace, 15f * dpiS, FontStyle.Bold);
        txtCode.TextAlign = HorizontalAlignment.Center;
        txtCode.Height = S(34);
        gDev.Controls.Add(txtCode);

        var lblCodeState = ModulePanelKit.Caption("", dpiS, maxWidth: 200);
        lblCodeState.Location = new Point(S(362), S(72));
        gDev.Controls.Add(lblCodeState);

        var lblDevices = ModulePanelKit.Caption("", dpiS, maxWidth: 330);
        lblDevices.Location = new Point(S(14), S(112));
        gDev.Controls.Add(lblDevices);

        // Set below, once the grid exists; the pairing button refreshes it after minting a code.
        Action? ReloadClients = null;

        void RefreshDevices()
        {
            int n = 0;
            try { n = RommAuth.ListTokens().Count; } catch { }
            lblDevices.Text = n == 0
                ? "No client is paired."
                : (n == 1 ? "1 paired client." : n + " paired clients.");
            ReloadClients?.Invoke();
        }

        DateTime codeExpiresUtc = DateTime.MinValue;
        var codeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        codeTimer.Tick += (_, _) =>
        {
            var left = codeExpiresUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                codeTimer.Stop();
                txtCode.Text = "";
                lblCodeState.Text = "the code has expired";
                lblCodeState.ForeColor = ModulePanelKit.Sub;
                RefreshDevices();
                return;
            }
            lblCodeState.Text = $"valid for {left.Minutes}:{left.Seconds:00}";
            lblCodeState.ForeColor = Color.FromArgb(120, 200, 140);
        };
        pClients.Disposed += (_, _) => { try { codeTimer.Stop(); codeTimer.Dispose(); } catch { } };

        btnPair.Click += (_, _) =>
        {
            // A code is only worth anything if something is listening to redeem it against.
            bool up = false;
            try { up = RommServer.IsRunning; } catch { }
            if (!up && MessageBox.Show(
                    "The server is not running, so no client can redeem this code yet.\n\nCreate it anyway?",
                    "RomM server", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                var name = "Paired device — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                var (record, secret) = RommAuth.CreateClientToken(name);
                txtCode.Text = RommAuth.CreatePairCode(record.Id, secret);
                codeExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                codeTimer.Start();
                RefreshDevices();
            }
            catch (Exception ex)
            {
                txtCode.Text = "";
                lblCodeState.Text = "";
                MessageBox.Show("Could not create a pairing code:\n\n" + ex.Message,
                    "RomM server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        // ── Clients ───────────────────────────────────────────────────────────
        var gCli = ModulePanelKit.Group("Paired clients", dpiS);
        gCli.Location = new Point(S(4), yc);
        gCli.Size = new Size(S(GroupW), S(260));
        pClients.Controls.Add(gCli);
        yc += gCli.Height + S(12);

        var grid = ModulePanelKit.Grid(dpiS, readOnly: true);   // a row is selected, never typed into
        grid.Location = new Point(S(14), S(24));
        grid.Size = new Size(S(GroupW - 32), S(160));
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add("name", "Client");
        grid.Columns.Add("created", "Paired");
        grid.Columns.Add("used", "Last used");
        grid.Columns.Add("pinned", "Games pinned");
        grid.Columns[0].Width = S(210);
        grid.Columns[1].Width = S(90);
        grid.Columns[2].Width = S(90);
        grid.Columns[3].Width = S(100);
        gCli.Controls.Add(grid);

        var tokenIds = new List<int>();

        void Reload()
        {
            grid.Rows.Clear();
            tokenIds.Clear();
            List<RommClientToken> tokens;
            try { tokens = RommAuth.ListTokens(); } catch { tokens = new List<RommClientToken>(); }
            foreach (var t in tokens)
            {
                tokenIds.Add(t.Id);
                int pinned = 0;
                try { pinned = Romm.RommRoms.PinCountFor(t.Id); } catch { }
                grid.Rows.Add(
                    t.Name,
                    t.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    t.LastUsedUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "never",
                    pinned == 0 ? "—" : pinned.ToString(CultureInfo.InvariantCulture));
            }
        }
        ReloadClients = Reload;

        int? SelectedToken()
        {
            int i = grid.CurrentRow?.Index ?? -1;
            return i >= 0 && i < tokenIds.Count ? tokenIds[i] : null;
        }

        var btnRevoke = ModulePanelKit.Button("Revoke…", dpiS, readOnly);
        btnRevoke.Location = new Point(S(14), S(196));
        btnRevoke.Width = S(120);
        btnRevoke.Click += (_, _) =>
        {
            if (SelectedToken() is not int id) return;
            var name = grid.CurrentRow?.Cells[0].Value?.ToString() ?? "this client";

            var answer = MessageBox.Show($"Revoke \"{name}\"? It will have to be paired again.",
                "RomM server", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return;

            try
            {
                RommAuth.DeleteToken(id);
                // A revoked client's pins go with it: they name a credential that no longer exists, and
                // leaving them would pin a token id a future client could be given.
                Romm.RommIndexer.RemoveClient(id);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not revoke: " + ex.Message, "RomM server",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshDevices();
        };
        gCli.Controls.Add(btnRevoke);

        var lblBindHint = ModulePanelKit.Caption(
            "A client is pinned to one file of a game the first time it downloads one, and stays on it. "
          + "The Assignment tab shows which, and lets you move a client to another version. Only PAIRED "
          + "clients can be pinned — one signing in with the account password always gets the default.",
            dpiS, maxWidth: 520);
        lblBindHint.Location = new Point(S(14), S(226));
        gCli.Controls.Add(lblBindHint);

        RefreshDevices();

        // ── Library ───────────────────────────────────────────────────────────
        var gLib = ModulePanelKit.Group("Library", dpiS);
        gLib.Location = new Point(S(4), yl);
        gLib.Size = new Size(S(GroupW), S(96));
        pLib.Controls.Add(gLib);
        yl += gLib.Height + S(12);

        var chkHidden = ModulePanelKit.Check("Include games marked Hidden in LaunchBox", dpiS, readOnly: readOnly);
        chkHidden.Location = new Point(S(14), S(28));
        chkHidden.Width = S(500);
        gLib.Controls.Add(chkHidden);

        var chkParental = ModulePanelKit.Check("Ignore the parental lock (clients see restricted games)", dpiS, readOnly: readOnly);
        chkParental.Location = new Point(S(14), S(58));
        chkParental.Width = S(500);
        gLib.Controls.Add(chkParental);

        // "ROMs listed per archive" lived here. It capped how many of an archive's entries a rom
        // advertised, back when a rom advertised several — a client then rendered a picker over them.
        // A rom names ONE file now, so there is nothing to cap: the choice is made in the Assignment
        // tab, and capping THAT would only stop you reaching the entry you want.

        // ── Load ──────────────────────────────────────────────────────────────
        try
        {
            RommConfig.Reload();
            numPort.Value = Math.Min(Math.Max(RommConfig.Port, 1), 65535);
            txtIps.Text = RommConfig.AllowedIps;
            txtUser.Text = RommConfig.Username;
            chkHidden.Checked = RommConfig.ExposeHiddenGames;
            chkParental.Checked = RommConfig.IgnoreParental;
            chkLog.Checked = RommConfig.LogRequests;
        }
        catch { }
        RefreshPortStatus();
        RefreshPassState();
        numPort.ValueChanged += (_, _) => RefreshPortStatus();
        txtIps.TextChanged += (_, _) => RefreshPortStatus();

        void Apply()
        {
            if (readOnly) return;
            try
            {
                var c = LiteBoxConfig.LoadForExe();
                c.SetSec(Sec, "Port", ((int)numPort.Value).ToString(CultureInfo.InvariantCulture));
                c.SetSec(Sec, "AllowedIps", (txtIps.Text ?? "").Trim());
                c.SetSec(Sec, "Username", (txtUser.Text ?? "").Trim());
                c.SetSec(Sec, "ExposeHiddenGames", chkHidden.Checked ? "true" : "false");
                c.SetSec(Sec, "IgnoreParental", chkParental.Checked ? "true" : "false");
                c.SetSec(Sec, "LogRequests", chkLog.Checked ? "true" : "false");
                c.Save();

                // A blank box KEEPS the current password — it is never read back for display, so blank
                // cannot mean "clear it" without silently locking every client out on an unrelated Apply.
                var pass = txtPass.Text ?? "";
                if (pass.Length > 0)
                {
                    RommConfig.SetPassword(pass);
                    txtPass.Text = "";
                }

                try
                {
                    RommConfig.Reload();
                    if (RommServer.IsRunning) RommServer.Restart();
                }
                catch { }

                RefreshPassState();
                RefreshPortStatus();
            }
            catch { }
        }

        var tabs = new TabControl { Dock = DockStyle.Fill };
        void Page(string title, Control c)
        {
            var pg = new TabPage(title) { BackColor = ModulePanelKit.Bg };
            c.Dock = DockStyle.Fill;
            pg.Controls.Add(c);
            tabs.TabPages.Add(pg);
        }
        Page("Server", pServer);
        Page("Clients", pClients);
        Page("Assignment", RommAssignPanel.Build(dpiS, readOnly, () => ReloadClients?.Invoke()));
        Page("Library", pLib);

        var root = new Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg };
        root.Controls.Add(tabs);
        return (root, Apply);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The address a client should be pointed at: this machine's LAN IPv4 when the allow-list opens
    /// the surface up, otherwise loopback (which is all that would answer).</summary>
    private static string LocalAddress(string? allowedIps)
    {
        if (string.IsNullOrWhiteSpace(allowedIps)) return "127.0.0.1";
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    return a.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>Can we bind this port right now? Answers the "is something else already there" question
    /// before the user applies a port they cannot have.</summary>
    private static bool TryProbePort(int port, out string reason)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            reason = "";
            return true;
        }
        catch (SocketException ex) { reason = ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? "in use" : ex.SocketErrorCode.ToString(); return false; }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
