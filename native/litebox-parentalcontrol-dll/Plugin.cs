// Entry points. The ModuleInitializer installs the write-guard the instant LaunchBox/BigBox
// touches this assembly (its plugin scan instantiates ParentalEventsPlugin below). The event
// plugin then keeps the lock state in sync with BigBox's own lock/unlock so the ASI read
// filter, the write-guard latch and the library reload all move together.
//
// HarmonyLib appears only inside Boot.Init's BODY and in WriteGuard's attributes — the plugin
// ENTRY type (ParentalEventsPlugin) has no Harmony in its signature, and 0Harmony is deployed
// beside this dll, so LB's GetTypes() plugin scan resolves cleanly.

using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Unbroken.LaunchBox.Plugins;

namespace LiteBoxParental
{
    internal static class Boot
    {
        private static bool _done;

        [ModuleInitializer]
        internal static void Init()
        {
            try
            {
                if (_done) return;
                _done = true;
                Log.Line($"=== litebox-parentalcontrol loaded (isBigBox={LockState.IsBigBox}, scopeActive={LockState.ScopeActive}) ===");
                if (!LockState.ScopeActive)
                {
                    Log.Line("[Boot] parental not configured for this process — write-guard NOT installed (inert).");
                    return;
                }
                new Harmony("litebox.parentalcontrol").PatchAll(typeof(Boot).Assembly);
                Log.Line("[Boot] write-guard installed (blocks File.Copy into Data\\ while locked).");
            }
            catch (Exception ex) { Log.Line("[Boot] init error: " + ex); }
        }
    }

    /// <summary>LB lifecycle events. Syncs the lock state with BigBox's own lock/unlock so the ASI
    /// filter, the write-guard latch and the library reload move together. LaunchBox has no lock
    /// events — there the ASI cold-start keeps it filtered until an unlock (LiteBox, or the PIN UI
    /// to be added in a follow-up).</summary>
    public sealed class ParentalEventsPlugin : ISystemEventsPlugin
    {
        public void OnEventRaised(string eventType)
        {
            try
            {
                if (!LockState.ScopeActive) return;
                switch (eventType)
                {
                    case "BigBoxStartupCompleted":
                    case "BigBoxStartup":
                        SyncBigBox();                       // handoff: match BigBox's real lock state
                        break;
                    case "BigBoxLocked":
                        LockState.SetLocked(true);
                        break;
                    case "BigBoxUnlocked":
                        LockState.SetLocked(false);
                        break;
                }
            }
            catch (Exception ex) { Log.Line("[Events] " + eventType + " error: " + ex.Message); }
        }

        private static void SyncBigBox()
        {
            bool locked = true;   // fail safe: assume locked if unreadable
            try { locked = PluginHelper.StateManager.IsBigBoxLocked; }
            catch (Exception ex) { Log.Line("[Events] IsBigBoxLocked read failed — assuming locked: " + ex.Message); }
            Log.Line($"[Events] BigBox handoff: IsBigBoxLocked={locked}");
            LockState.SetLocked(locked);
        }
    }
}
