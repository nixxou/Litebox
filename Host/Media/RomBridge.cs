// Drives ExtendDB's Archive MultiGame Selector by reflection (the host can't reference the plugin),
// IN-PROCESS — no HTTP, the embedded web server may be off. Mirrors ExtendDB.HostRomBridge:
//
//   FeatureEnabled                                   — Archive MultiGame Selector enabled in config
//   GetLaunchInfoJson(IGame)                         — launchOptions + lastLaunch JSON (same shape as
//                                                      the web detail.json: emulators[+autoExtract],
//                                                      versions[+isArchive], mainPathIsArchive, lastLaunch)
//   PickRomModal(IGame, string appId)                — opens the ROM picker in selection mode, returns
//                                                      the chosen entry (null = cancelled)
//   ArmSelectedRom(IGame, appId, entry, forcePriority) — pre-arms the launch context; call right
//                                                      BEFORE PlayGame so ExtendDB's
//                                                      OnBeforeGameLaunching applies the selection
//
// Everything is a no-op / null / false when ExtendDB isn't loaded (resolved lazily, cached, failures
// swallowed) — exactly like KioskBridge / ParentalBridge.

using System;
using System.Linq;
using System.Reflection;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class RomBridge
{
    private static bool _probed;
    private static Type _t;
    private static PropertyInfo _featureEnabled;
    private static MethodInfo _getInfo, _pick, _arm, _entries;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            _t = asm?.GetType("ExtendDB.HostRomBridge");
            if (_t == null) return;
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            _featureEnabled = _t.GetProperty("FeatureEnabled", F);
            _getInfo = _t.GetMethod("GetLaunchInfoJson", F, null, new[] { typeof(IGame) }, null);
            _pick = _t.GetMethod("PickRomModal", F, null, new[] { typeof(IGame), typeof(string) }, null);
            _arm = _t.GetMethod("ArmSelectedRom", F, null, new[] { typeof(IGame), typeof(string), typeof(string), typeof(bool) }, null);
            _entries = _t.GetMethod("GetArchiveEntriesJson", F, null, new[] { typeof(IGame), typeof(string) }, null);
        }
        catch { }
    }

    /// <summary>True iff ExtendDB is loaded, new enough to expose the bridge, and
    /// the Archive MultiGame Selector feature is on. The host hides the ROM
    /// button entirely otherwise.</summary>
    public static bool Available
    {
        get
        {
            Probe();
            if (_t == null || _getInfo == null) return false;
            try { return _featureEnabled == null || (bool)_featureEnabled.GetValue(null); }
            catch { return false; }
        }
    }

    public static string GetLaunchInfoJson(IGame game)
    {
        Probe();
        try { return _getInfo?.Invoke(null, new object[] { game }) as string; }
        catch { return null; }
    }

    public static string PickRomModal(IGame game, string appId)
    {
        Probe();
        try { return _pick?.Invoke(null, new object[] { game, appId }) as string; }
        catch { return null; }
    }

    public static bool ArmSelectedRom(IGame game, string appId, string entry, bool forcePriority)
    {
        Probe();
        try { return _arm != null && (bool)_arm.Invoke(null, new object[] { game, appId, entry, forcePriority }); }
        catch { return false; }
    }

    /// <summary>Sorted + decorated archive entries JSON
    /// ({ entries: [{fileName,size,isFavorite,isLastPlayed}] }) — feeds the quick
    /// ROM dropdown. Null when unavailable.</summary>
    public static string GetArchiveEntriesJson(IGame game, string appId)
    {
        Probe();
        try { return _entries?.Invoke(null, new object[] { game, appId }) as string; }
        catch { return null; }
    }
}
