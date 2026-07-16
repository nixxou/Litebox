// Host-side global hotkeys for the ExtendDB integration.
//
// Why this exists
//   ExtendDB registers its kiosk + parental hotkeys on the WPF input system:
//     EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.KeyDownEvent, …)
//   which only fires while a WPF UIElement holds keyboard focus. LaunchBox/BigBox are WPF apps, so
//   it works there. LiteBox is WinForms — no WPF element ever has focus — so ExtendDB's F10/F11/F12
//   and the parental hotkey never fire. We replicate them here with an app-wide WinForms
//   IMessageFilter and call ExtendDB's PUBLIC entry points by reflection (KioskBridge /
//   ParentalBridge). ExtendDB is NOT modified.
//
// Scope
//   A message filter only sees this process's UI-thread messages, so the hotkeys are live only
//   while LiteBox itself has focus — the same scope ExtendDB's WPF handler has under LB. Once a
//   kiosk window is open and focused, ExtendDB's own injected WebView2 keydown listener takes over
//   (it posts kiosk:F10/F11/F12 back to the host), so this filter only needs to OPEN the kiosk and
//   drive the parental dialog from LiteBox's own UI.
//
// Keys
//   • Parental hotkey (user-configured in ExtendDBParental.dat, may include modifiers) — checked
//     FIRST and PIN-gated via ExtendDB's own popup. Their default here is F12.
//   • F10 — toggle the LaunchBoxWeb kiosk     (bare key)
//   • F11 — toggle the BigBoxWeb kiosk        (bare key)
//   • F12 — kiosk DevTools                    (bare key; only when not claimed by the parental hotkey)

using System;
using System.Windows.Forms;
using LbApiHost.Host.Media;
using LbApiHost.Host.Web.Kiosk;

namespace LbApiHost.Host;

internal sealed class HostHotKeys : IMessageFilter
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;   // F10 (a system key) arrives here, not WM_KEYDOWN
    private const int DebounceMs = 300;          // matches ExtendDB's kiosk debounce

    private readonly Form _owner;
    private DateTime _lastUtc = DateTime.MinValue;

    private static HostHotKeys _installed;

    private HostHotKeys(Form owner) => _owner = owner;

    /// <summary>Install the app-wide hotkey filter (idempotent). Call once the main form exists.</summary>
    public static void Install(Form owner)
    {
        if (_installed != null) return;
        _installed = new HostHotKeys(owner);
        Application.AddMessageFilter(_installed);
    }

    /// <summary>Remove the filter (on form close).</summary>
    public static void Uninstall()
    {
        if (_installed == null) return;
        Application.RemoveMessageFilter(_installed);
        _installed = null;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN) return false;   // only key-down messages

        var key = (Keys)((int)m.WParam & 0xFFFF);
        Keys pressed = key | Control.ModifierKeys;   // full combo incl. Ctrl/Alt/Shift

        // 1) Parental hotkey — whatever the user configured in ExtendDBParental.dat (ANY key, any
        //    modifiers). NOT hardcoded: read live from ParentalBridge.HotKey. Checked first so a key
        //    that the kiosk also uses (e.g. F12) opens the PIN-gated lock dialog instead.
        if (ParentalBridge.Enabled)
        {
            var hk = (Keys)ParentalBridge.HotKey;
            if (hk != Keys.None && pressed == hk)
            {
                if (!Debounced()) Try(() => ParentalBridge.ShowLockDialog(_owner), "parental lock dialog");
                return true;   // consume regardless (debounced repeats are swallowed too)
            }
        }

        // 2) Kiosk toggles — hardcoded bare F10/F11/F12 (mirror ExtendDB's own consts, no modifiers).
        //    Only capture when ExtendDB actually exposes the kiosk, so without the plugin these keys
        //    keep their normal behaviour (e.g. F10 → menu) instead of being silently swallowed.
        if (Control.ModifierKeys == Keys.None && KioskBridge.Available)
        {
            switch (key)
            {
                case Keys.F10: if (!Debounced()) Try(KioskBridge.ToggleLaunchBox, "LaunchBox kiosk toggle"); return true;
                case Keys.F11: if (!Debounced()) Try(KioskBridge.ToggleBigBox, "BigBox kiosk toggle"); return true;
                case Keys.F12: if (!Debounced()) Try(KioskBridge.ShowDevTools, "kiosk DevTools"); return true;
            }
        }

        // 3) Native LiteBox kiosk — used only when ExtendDB's own kiosk is NOT available (no plugin)
        //    but LiteBox's WebView2 kiosk is. Keys are user-configurable in [Web]
        //    (KioskLaunchBoxKey / KioskBigBoxKey, gated by KioskHotKeys); F12 → DevTools.
        //    The embedded server (LbModule.Web) must be running for the kiosk to load.
        if (Control.ModifierKeys == Keys.None && !KioskBridge.Available && WebKioskWindow.IsAvailable() && NativeKioskEnabled())
        {
            if (key == NativeKey("KioskLaunchBoxKey", Keys.F10)) { if (!Debounced()) Try(WebKioskWindow.ToggleLaunchBox, "LaunchBox kiosk toggle (native)"); return true; }
            if (key == NativeKey("KioskBigBoxKey", Keys.F11)) { if (!Debounced()) Try(WebKioskWindow.ToggleBigBox, "BigBox kiosk toggle (native)"); return true; }
            if (key == Keys.F12) { if (!Debounced()) Try(WebKioskWindow.ShowDevTools, "kiosk DevTools (native)"); return true; }
        }
        return false;
    }

    // The native kiosk hotkeys are opt-in via [Web] KioskHotKeys (default on).
    private static bool NativeKioskEnabled()
    {
        try { return LiteBoxConfig.LoadForExe().GetSecBool("Web", "KioskHotKeys", true); }
        catch { return false; }
    }

    // Resolve a configured [Web] key name (e.g. "F11") to a Keys value; falls back to def.
    private static Keys NativeKey(string cfgKey, Keys def)
    {
        try
        {
            var s = LiteBoxConfig.LoadForExe().GetSec("Web", cfgKey, def.ToString());
            return Enum.TryParse<Keys>((s ?? "").Trim(), true, out var k) && k != Keys.None ? k : def;
        }
        catch { return def; }
    }

    // True when the press lands inside the debounce window (swallow it). Otherwise records the
    // timestamp and returns false so the caller fires.
    private bool Debounced()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastUtc).TotalMilliseconds < DebounceMs) return true;
        _lastUtc = now;
        return false;
    }

    private static void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { Console.WriteLine("[HostHotKeys] " + what + " failed: " + ex.Message); }
    }
}
