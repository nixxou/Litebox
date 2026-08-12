// LB lifecycle events. The native read-filter + write-guard are armed early by StartupHook (see
// StartupHook.cs); this plugin keeps the lock state in sync with BigBox's own lock/unlock so the read
// filter, the native write latch and the library reload all move together. There is NO ModuleInitializer
// and NO Harmony anymore — the write-guard is the .bin's CopyFileExW hook, not a managed File.Copy patch.

using System;
using Unbroken.LaunchBox.Plugins;

namespace LiteBoxParental
{
    /// <summary>LB lifecycle events. LaunchBox has no lock events — there the native filter boots armed
    /// (locked) and stays until an unlock via the Tools menu (UnlockMenu). BigBox drives lock/unlock here.</summary>
    public sealed class ParentalEventsPlugin : ISystemEventsPlugin
    {
        // Arm the SOFT admin-lock + the managed HARD write-guard at plugin load (NOT in StartupHook — Harmony must
        // never touch the early phase). Idempotent; LaunchBox/BigBox only; fully fail-safe.
        public ParentalEventsPlugin() { ArmGuards(); }

        public void OnEventRaised(string eventType)
        {
            ArmGuards();   // belt-and-suspenders: ensure armed even if the ctor ran before WPF was up
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

        private static void ArmGuards() => Guards.Arm();

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
