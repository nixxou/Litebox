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

    private WebKioskWindow(string surface)
    {
        _surface = surface;

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

        Shown += async (_, _) => await InitAsync();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try { await _web.EnsureCoreWebView2Async(null); }
        catch { Close(); return; }
        if (_web.CoreWebView2 == null) { Close(); return; }

        try
        {
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
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
        string url = $"http://127.0.0.1:{port}{path}#embedded=1";
        if (!_ready) return;   // Shown/InitAsync will call us once CoreWebView2 exists
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
