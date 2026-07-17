// Web module config panel — full parity with ExtendDB's "Web services" tab, native to LiteBox.
//
// Three left-column groups plus two keyboard-navigation grids:
//   • Local web server  — enable-at-startup (the Web MODULE master flag), port (+ live in-use warning),
//                          LAN allow-list, gzip-JSON. Persists to LiteBox.ini [Web] Port/AllowedIps/GzipJson.
//   • Surfaces          — enable the database site "/", LiteBox Web "/launchbox/", BigBox Web "/bigbox/",
//                          each with a clickable link. Persists to [Web] EnableDatabaseSite/EnableLiteBoxWeb/
//                          EnableBigBoxWeb.
//   • Embedded view (kiosk) — launch hotkeys on/off, BigBox key (F11), LaunchBox key (F10), and whether the
//                          launch entries show in the system menu. Persists to [Web] KioskHotKeys /
//                          KioskBigBoxKey / KioskLaunchBoxKey / ShowKioskMenu.
//   • Two nav-key grids — BigBox + LaunchBox: Action → keys, persisted to [Web] Keys.* / KeysLb.*.
//
// Reuses the existing LiteBox web backend (WebConfig snapshot + EmbeddedWebServer). The keyboard/kiosk keys
// have no native runtime consumer yet (see the coordinator notes in the agent hand-off) — they are persisted
// so the kiosk window + hotkey hook can read them once wired.

#nullable enable

using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using LbApiHost.Host;
using LbApiHost.Host.Modules;
using LbApiHost.Host.UiKit;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Options;

internal static class WebPanel
{
    private const string Sec = "Web";

    // Ordered nav-command rows. One shared skeleton for both surfaces (BigBox = Keys.*, LaunchBox = KeysLb.*);
    // the default key list per command differs slightly between the two.
    private static readonly (string label, string key, string bbDefault, string lbDefault)[] NavRows =
    {
        ("Up",          "Up",       "ArrowUp",                        "ArrowUp"),
        ("Down",        "Down",     "ArrowDown",                      "ArrowDown"),
        ("Left",        "Left",     "ArrowLeft",                      "ArrowLeft"),
        ("Right",       "Right",    "ArrowRight",                     "ArrowRight"),
        ("Page up",     "PageUp",   "PageUp",                         "PageUp"),
        ("Page down",   "PageDown", "PageDown",                       "PageDown"),
        ("Home",        "Home",     "Home",                           "Home"),
        ("End",         "End",      "End",                            "End"),
        ("Select / OK", "Select",   "Enter,a,A",                      "Enter,Spacebar"),
        ("Back",        "Back",     "Escape,Backspace,BrowserBack,b,B", "Escape,Backspace"),
        ("Menu",        "Menu",     "s,S",                            "Tab"),
    };

    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var root = ModulePanelKit.Root(dpiS);

        int y = S(6);
        const int GroupW = 560;

        // ── Local web server ──────────────────────────────────────────────────
        var gServer = ModulePanelKit.Group("Local web server", dpiS);
        gServer.Location = new Point(S(4), y);
        gServer.Size = new Size(S(GroupW), S(162));
        root.Controls.Add(gServer);
        y += gServer.Height + S(12);

        // NOTE: the module's enable state is owned solely by the Modules card grid (LbModules.SetOn).
        // This panel MUST NOT call SetOn(Web) — a second writer here would fight the card and revert it.

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

        var chkGzip = ModulePanelKit.Check("Gzip-compress JSON responses", dpiS, readOnly: readOnly);
        chkGzip.Location = new Point(S(14), S(118));
        gServer.Controls.Add(chkGzip);

        void RefreshPortStatus()
        {
            int port = (int)numPort.Value;
            bool ours = false;
            try { ours = EmbeddedWebServer.IsRunning && EmbeddedWebServer.CurrentPort == port; } catch { }
            if (ours)
            {
                lblPortStatus.Text = "● in use by LiteBox (running)";
                lblPortStatus.ForeColor = Color.FromArgb(120, 200, 140);
                return;
            }
            bool ok = TryProbePort(port, out var reason);
            lblPortStatus.Text = ok ? "✓ available" : "✗ " + reason;
            lblPortStatus.ForeColor = ok ? Color.FromArgb(120, 200, 140) : Color.IndianRed;
        }

        // ── Surfaces ──────────────────────────────────────────────────────────
        var gSurf = ModulePanelKit.Group("Surfaces", dpiS);
        gSurf.Location = new Point(S(4), y);
        gSurf.Size = new Size(S(GroupW), S(190));
        root.Controls.Add(gSurf);
        y += gSurf.Height + S(12);

        var surfIntro = ModulePanelKit.Caption("Choose which surfaces the server exposes. Click a link to open it in your browser.", dpiS);
        surfIntro.Location = new Point(S(14), S(24));
        gSurf.Controls.Add(surfIntro);

        CheckBox chkDb = SurfaceRow(gSurf, dpiS, readOnly, "ExtendDB web database", S(52), out var lnkDb);
        CheckBox chkLb = SurfaceRow(gSurf, dpiS, readOnly, "LaunchBox (LiteBox) Web", S(84), out var lnkLb);
        CheckBox chkBb = SurfaceRow(gSurf, dpiS, readOnly, "BigBox Web", S(116), out var lnkBb);

        var surfNote = ModulePanelKit.Caption("Surface changes take effect when the web server restarts (done on Save).", dpiS);
        surfNote.Location = new Point(S(14), S(152));
        gSurf.Controls.Add(surfNote);

        void RefreshLinks()
        {
            int port = (int)numPort.Value;
            string b = $"http://127.0.0.1:{port}";
            lnkDb.Text = b + "/";           lnkDb.Enabled = chkDb.Checked;
            lnkLb.Text = b + "/launchbox/"; lnkLb.Enabled = chkLb.Checked;
            lnkBb.Text = b + "/bigbox/";    lnkBb.Enabled = chkBb.Checked;
        }

        numPort.ValueChanged += (_, _) => { RefreshPortStatus(); RefreshLinks(); };
        chkDb.CheckedChanged += (_, _) => RefreshLinks();
        chkLb.CheckedChanged += (_, _) => RefreshLinks();
        chkBb.CheckedChanged += (_, _) => RefreshLinks();

        // ── Embedded view (kiosk) — launch ────────────────────────────────────
        var gKiosk = ModulePanelKit.Group("Embedded view (kiosk) — launch", dpiS);
        gKiosk.Location = new Point(S(4), y);
        gKiosk.Size = new Size(S(GroupW), S(150));
        root.Controls.Add(gKiosk);
        y += gKiosk.Height + S(12);

        var chkKioskKeys = ModulePanelKit.Check("Enable launch hotkeys", dpiS, readOnly: readOnly);
        chkKioskKeys.Location = new Point(S(15), S(26));
        gKiosk.Controls.Add(chkKioskKeys);

        var lblBbKey = ModulePanelKit.Caption("BigBox kiosk:", dpiS);
        lblBbKey.Location = new Point(S(15), S(62));
        gKiosk.Controls.Add(lblBbKey);
        var txtBbKey = KeyCaptureBox(dpiS, readOnly);
        txtBbKey.Location = new Point(S(125), S(59));
        gKiosk.Controls.Add(txtBbKey);

        var lblLbKey = ModulePanelKit.Caption("LaunchBox kiosk:", dpiS);
        lblLbKey.Location = new Point(S(235), S(62));
        gKiosk.Controls.Add(lblLbKey);
        var txtLbKey = KeyCaptureBox(dpiS, readOnly);
        txtLbKey.Location = new Point(S(360), S(59));
        gKiosk.Controls.Add(txtLbKey);

        var kioskHint = ModulePanelKit.Caption("Click a field and press a key (e.g. F11). Press Delete to unbind.", dpiS);
        kioskHint.Location = new Point(S(15), S(92));
        gKiosk.Controls.Add(kioskHint);

        var chkKioskMenu = ModulePanelKit.Check("Show the launch entries in the system menu", dpiS, readOnly: readOnly);
        chkKioskMenu.Location = new Point(S(15), S(118));
        gKiosk.Controls.Add(chkKioskMenu);

        void SyncKioskEnabled()
        {
            bool on = chkKioskKeys.Checked && !readOnly;
            txtBbKey.Enabled = txtLbKey.Enabled = on;
        }
        chkKioskKeys.CheckedChanged += (_, _) => SyncKioskEnabled();

        // ── Nav-key grids (BigBox + LaunchBox) ────────────────────────────────
        var gBbKeys = ModulePanelKit.Group("BigBox Web — keyboard navigation (incl. embedded view)", dpiS);
        gBbKeys.Location = new Point(S(4), y);
        gBbKeys.Size = new Size(S(GroupW), S(370));
        root.Controls.Add(gBbKeys);
        y += gBbKeys.Height + S(12);
        var gridBb = NavGrid(dpiS, readOnly, out var resetBb, isLb: false);
        AddGridBody(gBbKeys, dpiS, gridBb, resetBb,
            "Rebind the navigation keys. Use DOM key names (ArrowUp, Enter, Escape, PageDown, Tab)",
            "or single characters. Comma-separated = several keys for one action.");

        var gLbKeys = ModulePanelKit.Group("LaunchBox Web — keyboard navigation (incl. embedded view)", dpiS);
        gLbKeys.Location = new Point(S(4), y);
        gLbKeys.Size = new Size(S(GroupW), S(370));
        root.Controls.Add(gLbKeys);
        y += gLbKeys.Height + S(12);
        var gridLb = NavGrid(dpiS, readOnly, out var resetLb, isLb: true);
        AddGridBody(gLbKeys, dpiS, gridLb, resetLb,
            "Rebind the navigation keys. Use DOM key names (ArrowUp, Enter, Home, PageDown, Tab)",
            "or single characters. Use \"Spacebar\" for the space key. Comma-separated = several keys.");

        // ── Prefill ───────────────────────────────────────────────────────────
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            numPort.Value = Clamp(ParseInt(cfg.GetSec(Sec, "Port"), 8080), 1, 65535);
            txtIps.Text = cfg.GetSec(Sec, "AllowedIps", "") ?? "";
            chkGzip.Checked = cfg.GetSecBool(Sec, "GzipJson", true);
            chkDb.Checked = cfg.GetSecBool(Sec, "EnableDatabaseSite", true);
            chkLb.Checked = cfg.GetSecBool(Sec, "EnableLiteBoxWeb", true);
            chkBb.Checked = cfg.GetSecBool(Sec, "EnableBigBoxWeb", true);

            chkKioskKeys.Checked = cfg.GetSecBool(Sec, "KioskHotKeys", true);
            txtBbKey.Text = cfg.GetSec(Sec, "KioskBigBoxKey", "F11") ?? "";
            txtLbKey.Text = cfg.GetSec(Sec, "KioskLaunchBoxKey", "F10") ?? "";
            chkKioskMenu.Checked = cfg.GetSecBool(Sec, "ShowKioskMenu", true);

            foreach (var r in NavRows)
            {
                gridBb.Rows.Add(r.label, cfg.GetSec(Sec, "Keys." + r.key, r.bbDefault) ?? "");
                gridLb.Rows.Add(r.label, cfg.GetSec(Sec, "KeysLb." + r.key, r.lbDefault) ?? "");
            }
        }
        catch { }

        // Reset buttons repopulate from defaults (in-grid only; persisted on Save).
        resetBb.Click += (_, _) => { for (int i = 0; i < NavRows.Length && i < gridBb.Rows.Count; i++) gridBb.Rows[i].Cells[1].Value = NavRows[i].bbDefault; };
        resetLb.Click += (_, _) => { for (int i = 0; i < NavRows.Length && i < gridLb.Rows.Count; i++) gridLb.Rows[i].Cells[1].Value = NavRows[i].lbDefault; };

        SyncKioskEnabled();
        RefreshLinks();
        RefreshPortStatus();

        // ── Apply ─────────────────────────────────────────────────────────────
        void Apply()
        {
            if (readOnly) return;
            try
            {
                var cfg = LiteBoxConfig.LoadForExe();
                cfg.SetSec(Sec, "Port", ((int)numPort.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                cfg.SetSec(Sec, "AllowedIps", (txtIps.Text ?? "").Trim());
                cfg.SetSec(Sec, "GzipJson", chkGzip.Checked ? "true" : "false");
                cfg.SetSec(Sec, "EnableDatabaseSite", chkDb.Checked ? "true" : "false");
                cfg.SetSec(Sec, "EnableLiteBoxWeb", chkLb.Checked ? "true" : "false");
                cfg.SetSec(Sec, "EnableBigBoxWeb", chkBb.Checked ? "true" : "false");

                cfg.SetSec(Sec, "KioskHotKeys", chkKioskKeys.Checked ? "true" : "false");
                cfg.SetSec(Sec, "KioskBigBoxKey", (txtBbKey.Text ?? "").Trim());
                cfg.SetSec(Sec, "KioskLaunchBoxKey", (txtLbKey.Text ?? "").Trim());
                cfg.SetSec(Sec, "ShowKioskMenu", chkKioskMenu.Checked ? "true" : "false");

                for (int i = 0; i < NavRows.Length; i++)
                {
                    if (i < gridBb.Rows.Count) cfg.SetSec(Sec, "Keys." + NavRows[i].key, (gridBb.Rows[i].Cells[1].Value as string ?? "").Trim());
                    if (i < gridLb.Rows.Count) cfg.SetSec(Sec, "KeysLb." + NavRows[i].key, (gridLb.Rows[i].Cells[1].Value as string ?? "").Trim());
                }
                cfg.Save();

                // Best-effort: fold the new [Web] snapshot in, and restart a live server so port/surface
                // changes take effect immediately (mirrors ExtendDB's restart-on-save). Fully guarded.
                try
                {
                    WebConfig.Reload();
                    if (EmbeddedWebServer.IsRunning)
                    {
                        EmbeddedWebServer.Stop();
                        if (LbModules.On(LbModule.Web)) EmbeddedWebServer.Start(WebConfig.Port);
                    }
                }
                catch { }
            }
            catch { }
        }

        return (root, Apply);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>One surface row: a checkbox (left) + a clickable URL link (right). The link greys out when the
    /// surface is unchecked.</summary>
    private static CheckBox SurfaceRow(GroupBox g, float dpiS, bool readOnly, string label, int y, out LinkLabel link)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var chk = ModulePanelKit.Check(label, dpiS, readOnly: readOnly);
        chk.Location = new Point(S(15), y);
        var lnk = new LinkLabel
        {
            Text = "", AutoSize = true, Location = new Point(S(235), y + S(1)), BackColor = ModulePanelKit.Bg,
            LinkColor = Color.FromArgb(120, 170, 255), ActiveLinkColor = Color.White, DisabledLinkColor = ModulePanelKit.Sub,
            Font = new Font("Segoe UI", 9f),
        };
        lnk.LinkClicked += (_, _) => OpenUrl(lnk.Text);
        g.Controls.Add(chk);
        g.Controls.Add(lnk);
        link = lnk;
        return chk;
    }

    /// <summary>A read-only textbox that captures a single key press and shows its name (WinForms key name,
    /// e.g. "F11"). Delete/Backspace clears it.</summary>
    private static TextBox KeyCaptureBox(float dpiS, bool readOnly)
    {
        var tb = new TextBox
        {
            Width = ModulePanelKit.Sc(dpiS, 90), ReadOnly = true, ShortcutsEnabled = false,
            TextAlign = HorizontalAlignment.Center, Cursor = Cursors.Hand,
            BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f), Enabled = !readOnly,
        };
        tb.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true; e.Handled = true;
            var k = e.KeyCode;
            if (k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return; // bare modifier
            if (k is Keys.Delete or Keys.Back) { tb.Text = ""; return; }                               // unbind
            tb.Text = k.ToString();
        };
        return tb;
    }

    /// <summary>A themed Action | Keys grid (no rows). Also emits the "Reset to defaults" button.</summary>
    private static DataGridView NavGrid(float dpiS, bool readOnly, out Button reset, bool isLb)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var grid = ModulePanelKit.Grid(dpiS, readOnly);
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Location = new Point(S(15), S(66));
        grid.Size = new Size(S(GroupWInner), S(260));
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Action", Width = S(160), ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Keys (comma-separated)", Width = S(350), ReadOnly = readOnly });
        reset = ModulePanelKit.Button("Reset to defaults", dpiS, readOnly);
        reset.Location = new Point(S(15), S(332));
        return grid;
    }

    private const int GroupWInner = 520;

    private static void AddGridBody(GroupBox g, float dpiS, DataGridView grid, Button reset, string hint1, string hint2)
    {
        int S(int px) => ModulePanelKit.Sc(dpiS, px);
        var h1 = ModulePanelKit.Caption(hint1, dpiS); h1.Location = new Point(S(15), S(26)); g.Controls.Add(h1);
        var h2 = ModulePanelKit.Caption(hint2, dpiS); h2.Location = new Point(S(15), S(44)); g.Controls.Add(h2);
        g.Controls.Add(grid);
        g.Controls.Add(reset);
    }

    /// <summary>True if <paramref name="port"/> can be bound on loopback (or is already the running LiteBox
    /// server's port). <paramref name="reason"/> is set on failure.</summary>
    private static bool TryProbePort(int port, out string reason)
    {
        reason = "";
        if (port is < 1 or > 65535) { reason = "out of range"; return false; }
        try { if (EmbeddedWebServer.IsRunning && EmbeddedWebServer.CurrentPort == port) return true; } catch { }
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { reason = "already in use by another application"; return false; }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static int ParseInt(string? s, int def)
        => int.TryParse((s ?? "").Trim(), out var n) ? n : def;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
