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
                // Host guard FIRST: never do anything under LiteBox.exe (its own parental filtering +
                // legitimate Data\ writes) or any process that isn't LaunchBox.exe / BigBox.exe.
                if (!LockState.IsHost)
                {
                    Log.Line($"=== litebox-parentalcontrol: host is {System.Diagnostics.Process.GetCurrentProcess().ProcessName} (not LaunchBox/BigBox) — fully inert ===");
                    return;
                }
                Log.Line($"=== litebox-parentalcontrol loaded (isBigBox={LockState.IsBigBox}, scopeActive={LockState.ScopeActive}, indeterminate={LockState.ConfigIndeterminate}) ===");
                if (LockState.ConfigIndeterminate)
                {
                    // The config exists but we couldn't read it, so we can't prove parental is off — and the ASI
                    // may already be filtering. Fail CLOSED: install the write-guard (WritesUnsafe is armed while
                    // indeterminate) and DO NOT touch the ASI. A save can't persist a possibly-filtered library.
                    new Harmony("litebox.parentalcontrol").PatchAll(typeof(Boot).Assembly);
                    Log.Line("[Boot] config unreadable (indeterminate) — write-guard installed defensively; ASI left as-is.");
                    return;
                }
                if (!LockState.ScopeActive)
                {
                    // Confirmed NOT configured (file missing, or read OK with Enabled off). The write-guard is not
                    // installed, so the ASI must NOT be filtering — a filtered read with no guard is exactly the
                    // data-loss path. Tell the ASI to stop (idempotent when parental is genuinely off; decisive if
                    // a config-contract drift ever let the ASI's `enabled` gate fire while this check did not).
                    AsiBridge.SetFiltering(false);
                    Log.Line("[Boot] parental not configured for this process — write-guard NOT installed; ASI filter forced off (inert).");
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
