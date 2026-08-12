// The standalone Tools-menu entry that makes the plugin usable WITHOUT LiteBox:
//   • "Parental control: Settings…" → the config replica (enable, mode, rating rules, hidden platforms, PIN)
//     PLUS a "Restricted games…" button that opens the per-platform restriction browser (colour-tag reasons).
//
// The browser is reached ONLY from inside Settings — no separate menu entry — so there is one parental door.
// It edits / reads the shared Core\litebox-parental.dat (ParentalDat). When parental is LOCKED and a PIN is
// set, opening Settings prompts for the PIN first (same gate + lockout as UnlockMenu) so a locked-down machine
// can't be reconfigured by whoever is using it. BigBox drives its own lock, so — like UnlockMenu — this is
// hidden in BigBox while locked (AllowInBigBoxWhenLocked = false).

using System;
using System.Drawing;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;

namespace LiteBoxParental
{
    public sealed class ParentalConfigMenuItem : ISystemMenuItemPlugin
    {
        public string Caption => "Parental control: Settings…";
        public Image IconImage => SystemIcons.Shield.ToBitmap();
        public bool ShowInLaunchBox => true;
        public bool ShowInBigBox => true;
        public bool AllowInBigBoxWhenLocked => false;   // BigBox has its own lock/PIN flow

        public void OnSelected()
        {
            try
            {
                if (!AdminGate.RequireUnlocked()) return;
                using (var f = new ParentalConfigForm()) f.ShowDialog();
            }
            catch (Exception ex) { Log.Line("[ConfigMenu] " + ex.Message); }
        }
    }

    /// <summary>Gate for the admin modal: the settings are reachable ONLY once the session is unlocked. Unlocking
    /// is a deliberate, separate step (Tools → "Parental control: … click to unlock") so the PIN is entered in one
    /// place, not re-prompted here. When no PIN protects the config yet (first-time setup) it opens freely.</summary>
    internal static class AdminGate
    {
        public static bool RequireUnlocked()
        {
            if (!LockState.ScopeActive) return true;   // parental not enabled → nothing to protect (also avoids a
                                                       // deadlock: unlocking needs ScopeActive, so a disabled-but-
                                                       // PIN'd config must stay openable to re-enable it)
            if (!LockState.Locked) return true;        // unlocked this session
            if (!PinVerify.HasPin) return true;         // no PIN protecting it yet — allow first-time setup

            MessageBox.Show(
                "Parental control is locked.\n\nUnlock it first: Tools → \"Parental control: Locked (click to unlock)\", "
                + "then open Settings again.",
                "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
    }
}
