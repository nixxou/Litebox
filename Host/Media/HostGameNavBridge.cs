// Reverse bridge: registers OUR "navigate to a game" callback on the ExtendDB plugin
// (ExtendDB.HostGameNavigation.NavigateHandler, a Func<string,bool>) so the plugin's
// Similar-Games viewer jumps to an OWNED game inside LiteBox instead of opening a web
// page. Opposite direction from RomBridge/KioskBridge (there the host CALLS the plugin;
// here the host REGISTERS a callback the plugin calls back).
//
// No-op when ExtendDB isn't loaded or is too old to expose the hook (resolved lazily,
// failures swallowed) — exactly like the other bridges. The callback finds the live
// MainWindow at call time and marshals to its UI thread, so it can be registered at
// boot (before the window exists).

using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class HostGameNavBridge
{
    private static Func<string, bool> _handler;

    /// <summary>Sets <c>ExtendDB.HostGameNavigation.NavigateHandler</c> to our in-host
    /// game-selection callback. Safe to call once after the plugin is loaded.</summary>
    public static void Register()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            var t = asm?.GetType("ExtendDB.HostGameNavigation");
            var field = t?.GetField("NavigateHandler", BindingFlags.Public | BindingFlags.Static);
            if (field == null) return;
            _handler ??= NavigateToGame;
            field.SetValue(null, _handler);
            Console.WriteLine("[HostGameNavBridge] navigation handler registered on ExtendDB.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[HostGameNavBridge] register failed: " + ex.Message);
        }
    }

    /// <summary>Selects/navigates to the game whose IGame.Id is <paramref name="gameId"/>
    /// in the LiteBox main window (on its UI thread). Returns false when there's no
    /// window or the game can't be found, so the plugin falls back to the web page.</summary>
    private static bool NavigateToGame(string gameId)
    {
        try
        {
            var mw = Application.OpenForms.OfType<MainWindow>().FirstOrDefault();
            if (mw == null || mw.IsDisposed) return false;
            if (mw.InvokeRequired)
                return (bool)mw.Invoke((Func<bool>)(() => mw.SelectGameById(gameId)));
            return mw.SelectGameById(gameId);
        }
        catch { return false; }
    }
}
