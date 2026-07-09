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
    private static PropertyInfo _featureEnabled, _raModuleActive;
    private static MethodInfo _getInfo, _pick, _arm, _entries, _heal, _healSync, _clearHist, _recordDetection;

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
            _raModuleActive = _t.GetProperty("RaModuleActive", F);
            _getInfo = _t.GetMethod("GetLaunchInfoJson", F, null, new[] { typeof(IGame) }, null);
            _pick = _t.GetMethod("PickRomModal", F, null, new[] { typeof(IGame), typeof(string) }, null);
            _arm = _t.GetMethod("ArmSelectedRom", F, null, new[] { typeof(IGame), typeof(string), typeof(string), typeof(bool) }, null);
            _entries = _t.GetMethod("GetArchiveEntriesJson", F, null, new[] { typeof(IGame), typeof(string) }, null);
            _heal = _t.GetMethod("HealRa", F, null, new[] { typeof(IGame) }, null);
            _healSync = _t.GetMethod("HealRaSync", F, null, new[] { typeof(IGame) }, null);
            _clearHist = _t.GetMethod("ClearLaunchHistory", F, null, new[] { typeof(IGame) }, null);
            _recordDetection = _t.GetMethod("RecordDetectionMs", F, null, new[] { typeof(IGame), typeof(long) }, null);
        }
        catch { }
    }

    /// <summary>Mirror the launch→detection latency (ms) into ExtendDB's launch-history.db too, so both
    /// copies stay in sync. No-op when ExtendDB is absent or predates the method (LiteBox's own op-log
    /// copy is the one the feature reads).</summary>
    public static void RecordDetection(IGame game, long detectionMs)
    {
        Probe();
        try { _recordDetection?.Invoke(null, new object[] { game, detectionMs }); } catch { }
    }

    /// <summary>Select-time RA heal (plugin-side): reconciles a present-but-possibly-wrong
    /// RetroAchievementsHash on the game to our value. No-op when ExtendDB is absent / no hash.</summary>
    public static void HealRa(IGame game)
    {
        Probe();
        try { _heal?.Invoke(null, new object[] { game }); } catch { }
    }

    /// <summary>BLOCKING RA heal — resolves+writes the raid/hash before returning, so a caller that reads
    /// the raid right after (the RA detail panel) doesn't race the async heal. Falls back to the async
    /// HealRa on an older plugin that lacks it. Call OFF the UI thread.</summary>
    public static void HealRaSync(IGame game)
    {
        Probe();
        try
        {
            if (_healSync != null) { _healSync.Invoke(null, new object[] { game }); return; }
            _heal?.Invoke(null, new object[] { game });   // older plugin: best-effort async
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

    /// <summary>True when ExtendDB is present AND its RetroAchievements module owns the hash/raid resolution.
    /// When false (ExtendDB absent, or its RA module off), the LiteBox-native fallback (RaResolveLite) takes
    /// over. An older plugin that predates the RaModuleActive property is assumed to handle RA (defer, so we
    /// never double-resolve) — same conservative default as today.</summary>
    public static bool RaActive
    {
        get
        {
            Probe();
            if (_t == null) return false;                  // ExtendDB absent → LiteBox handles RA
            if (_raModuleActive == null) return true;      // old plugin without the flag → assume it handles RA
            try { return (bool)_raModuleActive.GetValue(null); }
            catch { return true; }
        }
    }

    public static string GetLaunchInfoJson(IGame game)
    {
        Probe();
        try { return _getInfo?.Invoke(null, new object[] { game }) as string; }
        catch { return null; }
    }

    /// <summary>Cancels the plugin's launch-history row for the game (reset-to-default button) so
    /// GetLaunchInfoJson stops re-seeding the last version/emulator/ROM. No-op when ExtendDB is
    /// absent or predates the method — the in-memory reset still applies for the session, but the
    /// history would re-seed on the next selection.</summary>
    public static void ClearLaunchHistory(IGame game)
    {
        Probe();
        try { _clearHist?.Invoke(null, new object[] { game }); } catch { }
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
