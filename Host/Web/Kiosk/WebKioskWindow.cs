// Fullscreen WebView2 "kiosk" window that loads the local embedded web server's BigBox (/bigbox/) or LiteBox
// (/launchbox/) surface — the native-LiteBox equivalent of ExtendDB's BigBoxWebKioskFormsWindow.
//
// Availability
//   WebView2's managed assemblies are Core-provided on LaunchBox 13.28 and ABSENT on 13.27; the csproj
//   references them compile-only (Private=false). Every entry point is guarded through IsAvailable() (a probe
//   of the Evergreen runtime + the managed types) so a Core without WebView2 is caught and NEVER fatal — the
//   toggles simply no-op. The config UI (WebPanel) is fully functional regardless of this window.
//
// Wiring (coordinator — see the agent hand-off notes)
//   Nothing calls these toggles yet. HostHotKeys currently drives ExtendDB's plugin kiosk by reflection
//   (KioskBridge). To make LiteBox open its OWN kiosk, the coordinator wires the F11/F10 hotkeys (read from
//   LiteBox.ini [Web] KioskBigBoxKey / KioskLaunchBoxKey, gated by KioskHotKeys) to ToggleBigBox()/
//   ToggleLaunchBox() when KioskBridge is not available. The embedded server (LbModule.Web) must be running.

#nullable enable

using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using LbApiHost.Host.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LbApiHost.Host.Web.Kiosk;

/// <summary>A borderless, top-most, maximized WebView2 window over the local web server. Single live instance;
/// the toggles open it, switch its surface, or close it.</summary>
internal sealed class WebKioskWindow : Form
{
    private static WebKioskWindow? _instance;

    private readonly WebView2 _web;
    private string _surface;            // "bigbox" or "launchbox" (updated on live surface switch)
    private bool _ready;
    private string _deepLink;           // one-shot extra hash for a restore deep-link ("" normally)
    private bool _reassertTopMost;      // dropped TopMost for an external launch → restore when re-focused

    /// <summary>True when the WebView2 managed assemblies AND the Evergreen runtime are both present. Isolated
    /// + guarded so a Core without WebView2 (13.27) is caught, never fatal.</summary>
    public static bool IsAvailable()
    {
        try { return Probe(); } catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Probe() => !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());

    /// <summary>Toggle the BigBox kiosk (/bigbox/): open it, or if already showing that surface, close it.</summary>
    public static void ToggleBigBox() => Toggle("bigbox");

    /// <summary>Toggle the LiteBox/LaunchBox kiosk (/launchbox/).</summary>
    public static void ToggleLaunchBox() => Toggle("launchbox");

    /// <summary>Open DevTools on the live kiosk (no-op when none is open).</summary>
    public static void ShowDevTools()
    {
        try { _instance?._web.CoreWebView2?.OpenDevToolsWindow(); } catch { }
    }

    /// <summary>Close any open kiosk.</summary>
    public static void CloseKiosk()
    {
        try { _instance?.Close(); } catch { }
    }

    /// <summary>Let an EXTERNAL window (a store client's install/launch — GOG Galaxy, Steam, Epic…) surface
    /// above the kiosk. The kiosk is TopMost + fullscreen, so a normally-activated store window would open
    /// hidden behind it. We drop TopMost and push the kiosk to the back so the store window is usable; the
    /// next time the kiosk is focused again it re-asserts TopMost. No-op when no kiosk is open. Any store, not
    /// just GOG. Must run on the kiosk UI thread (marshalled if needed).</summary>
    public static void YieldForExternalLaunch()
    {
        var w = _instance;
        if (w == null || w.IsDisposed) return;
        try
        {
            if (w.InvokeRequired) { w.BeginInvoke((Action)YieldForExternalLaunch); return; }
            w.TopMost = false;
            w._reassertTopMost = true;   // Activated restores TopMost when the user comes back to the kiosk
            try { SetWindowPos(w.Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
        }
        catch { }
    }

    private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static bool _suspended;
    private static string? _suspendedSurface;
    private static string _suspendedDeepLink = "";

    /// <summary>FULLY TEAR DOWN the open kiosk while a game runs — closing the window disposes the WebView2,
    /// so its Chromium child process exits and frees RAM/CPU/GPU for the game (mirrors ExtendDB, which does a
    /// full teardown, not a hide). The pre-launch surface + a deep-link to the launched game are snapshotted;
    /// RestoreAfterGameLaunch recreates the kiosk on that surface, back on the played game. The in-memory
    /// parental unlock is PRESERVED across the close (the reset is skipped for a suspend-close). No-op when no
    /// kiosk is open. Must run on the kiosk's UI thread.</summary>
    public static void SuspendForGameLaunch(string? gameId, string? platform)
    {
        var w = _instance;
        if (w == null || w.IsDisposed) return;
        try
        {
            if (w.InvokeRequired) { string? gid = gameId, plat = platform; w.BeginInvoke((Action)(() => SuspendForGameLaunch(gid, plat))); return; }
            _suspendedSurface = w._surface;
            _suspendedDeepLink = BuildDeepLink(gameId, platform);
            _suspended = true;
            w.Close();                  // → WebView2 disposed, child process exits
        }
        catch { _suspended = false; _suspendedSurface = null; _suspendedDeepLink = ""; }
    }

    /// <summary>Recreate the kiosk that SuspendForGameLaunch tore down, on the same surface and deep-linked
    /// to the played game, once the game has exited. No-op when nothing was suspended. Call on the UI thread.</summary>
    public static void RestoreAfterGameLaunch()
    {
        if (!_suspended) return;
        var surface = _suspendedSurface;
        var deep = _suspendedDeepLink;
        _suspended = false; _suspendedSurface = null; _suspendedDeepLink = "";
        if (string.IsNullOrEmpty(surface) || !IsAvailable()) return;
        try
        {
            var w = new WebKioskWindow(surface!, deep);
            _instance = w;
            w.Show();
        }
        catch { }
    }

    /// <summary>Extra hash params (&amp;platform=&amp;gameId=) so the restored kiosk lands back on the played
    /// game. The BigBox theme honours them; the LaunchBox theme ignores them (harmless — the base path wins).</summary>
    private static string BuildDeepLink(string? gameId, string? platform)
    {
        var sb = new System.Text.StringBuilder();
        var slug = Slugify(platform ?? "");
        if (slug.Length > 0) sb.Append("&platform=").Append(Uri.EscapeDataString(slug));
        if (!string.IsNullOrEmpty(gameId)) sb.Append("&gameId=").Append(Uri.EscapeDataString(gameId!));
        return sb.ToString();
    }

    /// <summary>Same slug rule as the themes' engine/app.js slugify(): lowercase, non-alphanumeric runs → "-",
    /// trimmed — so the platform matches the theme's data/platforms/&lt;slug&gt;/games.json path.</summary>
    private static string Slugify(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder();
        bool dash = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
        }
        return sb.ToString().Trim('-');
    }

    private static void Toggle(string surface)
    {
        if (!IsAvailable()) return;
        try
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                // Same surface → close (a real toggle). Different surface → navigate the live window to it.
                if (string.Equals(_instance._surface, surface, StringComparison.OrdinalIgnoreCase))
                {
                    _instance.Close();
                    return;
                }
                _instance.NavigateSurface(surface);
                _instance.Activate();
                return;
            }
            var w = new WebKioskWindow(surface);
            _instance = w;
            w.Show();
        }
        catch { }
    }

    private WebKioskWindow(string surface, string deepLink = "")
    {
        _surface = surface;
        _deepLink = deepLink ?? "";

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        KeyPreview = true;

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
        Controls.Add(_web);

        // Escape closes the kiosk from the host side (the surface may also post its own close on Back).
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        FormClosed += (_, _) => { if (ReferenceEquals(_instance, this)) _instance = null; };

        // After yielding to a store window (TopMost dropped), reclaim TopMost the moment the kiosk is
        // focused again — so once the user is done with the installer the kiosk is back on top.
        Activated += (_, _) => { if (_reassertTopMost) { _reassertTopMost = false; try { TopMost = true; } catch { } } };

        Shown += async (_, _) => await InitAsync();
        // NB: closing the kiosk does NOT re-lock — the kiosk shares the desktop runtime lock (same user),
        // and a window close is not a lock gesture. Locking happens only via the explicit web "lock" action.
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        // Explicit user-data folder UNDER Core\litebox\ (else WebView2 spawns Core\LiteBox.exe.WebView2 next
        // to the exe, which our uninstall then has to special-case). Falls back to the default env on failure.
        CoreWebView2Environment? env = null;
        try { env = await CoreWebView2Environment.CreateAsync(null, LiteBoxPaths.Dir("webview2-kiosk"), null); } catch { }
        try { await _web.EnsureCoreWebView2Async(env); }
        catch { Close(); return; }
        if (_web.CoreWebView2 == null) { Close(); return; }

        try
        {
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            // Parental: mark this window's requests as KIOSK — the server keys the lock on the shared
            // desktop runtime lock (Host/Parental/ParentalFilter), so the kiosk and host GUI lock together.
            try { s.UserAgent += " " + LbApiHost.Host.Web.WebParentalState.KioskUaMarker; } catch { }
            // Keep navigation inside the one window.
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; try { _web.CoreWebView2.Navigate(e.Uri); } catch { } };
            // The embedded surface (#embedded=1) posts control WebMessages back to its host — the System Menu
            // "Exit" item and the top-right ×, plus the in-page F-key mirrors. Fires on the UI thread.
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
        }
        catch { }

        _ready = true;
        NavigateSurface(_surface);
    }

    private void NavigateSurface(string surface)
    {
        _surface = surface;
        int port;
        try { port = WebConfig.Port; } catch { port = 8080; }
        string path = string.Equals(surface, "bigbox", StringComparison.OrdinalIgnoreCase) ? "/bigbox/" : "/launchbox/";
        // #embedded=1 tells the surface it runs inside the kiosk host: it KEEPS the System Menu "Exit" item and
        // shows the top-right × (both removed from the DOM in standalone), and routes them to us via WebMessage.
        string extra = _deepLink; _deepLink = "";   // deep-link only the first (restore) navigation
        string url = $"http://127.0.0.1:{port}{path}#embedded=1{extra}";
        if (!_ready) { _deepLink = extra; return; }   // Shown/InitAsync will call us once CoreWebView2 exists
        try { _web.CoreWebView2?.Navigate(url); } catch { }
    }

    /// <summary>Handles the control WebMessages the embedded surface posts back (exit / F-key mirrors). Runs on
    /// the UI thread. Mirrors ExtendDB's BigBoxWebKioskFormsWindow.OnWebMessageReceived.</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); } catch { return; }
        if (string.IsNullOrEmpty(msg)) return;
        switch (msg)
        {
            case "kiosk:exit": try { Close(); } catch { } break;   // System Menu "Exit" / top-right ×
            case "kiosk:F11":  ToggleBigBox();    break;
            case "kiosk:F10":  ToggleLaunchBox(); break;
            case "kiosk:F12":  ShowDevTools();    break;
        }
    }
}
