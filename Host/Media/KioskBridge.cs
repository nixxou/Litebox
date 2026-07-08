// Calls ExtendDB's BigBoxWeb/LaunchBoxWeb kiosk toggles by reflection (the host can't reference
// ExtendDB). ExtendDB normally fires these off its own WPF KeyDown class handler (F11=BigBox,
// F10=LaunchBox, F12=DevTools) — which never fires under LiteBox because LiteBox is WinForms, not
// WPF. The host replicates the keys (see Host/HostHotKeys.cs) and routes them here.
//
// Reflected surface (ExtendDB.Forms.BigBoxWebKioskFormsWindow, all public static void):
//   Toggle()           — open/close the BigBoxWeb kiosk (/bigbox)
//   ToggleLaunchBox()  — open/close the LaunchBoxWeb kiosk (/launchbox)   [may be absent on older builds]
//   ShowDevTools()     — DevTools on the live kiosk
//
// Each call is a no-op if ExtendDB isn't loaded or the method is missing (e.g. ToggleLaunchBox on a
// plugin build that predates it) — resolved lazily and cached, failures swallowed.

using System;
using System.Linq;
using System.Reflection;

namespace LbApiHost.Host.Media;

internal static class KioskBridge
{
    private static bool _probed;
    private static MethodInfo _toggle, _toggleLb, _devTools, _prepareDefer, _revealDefer;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            var t = asm?.GetType("ExtendDB.Forms.BigBoxWebKioskFormsWindow");
            if (t == null) return;
            _toggle = t.GetMethod("Toggle", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            _toggleLb = t.GetMethod("ToggleLaunchBox", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            _devTools = t.GetMethod("ShowDevTools", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            // "behind GAME OVER" web-return timing (may be absent on older plugin builds → mode
            // silently falls back to reopening normally, i.e. "immediate").
            _prepareDefer = t.GetMethod("PrepareDeferredReopen", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            _revealDefer = t.GetMethod("RevealDeferredKiosk", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        }
        catch { }
    }

    /// <summary>True iff ExtendDB exposes the kiosk window (the plugin is loaded + new enough).</summary>
    public static bool Available { get { Probe(); return _toggle != null || _toggleLb != null; } }

    /// <summary>True iff the plugin supports the deferred (behind-GAME-OVER) reopen path.</summary>
    public static bool SupportsDeferredReopen { get { Probe(); return _prepareDefer != null && _revealDefer != null; } }

    public static void ToggleBigBox() { Probe(); _toggle?.Invoke(null, null); }
    public static void ToggleLaunchBox() { Probe(); _toggleLb?.Invoke(null, null); }
    public static void ShowDevTools() { Probe(); _devTools?.Invoke(null, null); }

    /// <summary>Ask ExtendDB to reopen the kiosk HIDDEN (non-topmost, un-activated) on the next
    /// OnGameExited restore, so it loads behind the GAME OVER cover. Pair with <see cref="RevealDeferredKiosk"/>.</summary>
    public static void PrepareDeferredReopen() { Probe(); try { _prepareDefer?.Invoke(null, null); } catch { } }

    /// <summary>Reveal the kiosk that was reopened hidden (topmost + activate). No-op if none is deferred.</summary>
    public static void RevealDeferredKiosk() { Probe(); try { _revealDefer?.Invoke(null, null); } catch { } }
}
