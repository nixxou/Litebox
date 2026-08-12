// LaunchBox Tools-menu entry to lock / unlock parental control from inside LaunchBox itself.
//
// LaunchBox (unlike BigBox) raises no lock/unlock events, so its session stays filtered until
// someone unlocks it here. Clicking: if unlocked → re-lock (no PIN needed); if locked → prompt
// for BigBox's PIN and, on a match, unlock (SetLocked(false) → ASI filter off + ForceReload).
// A 3-strike lockout (PinLockout) guards the prompt. BigBox is excluded — it drives its own lock.

using System;
using System.Drawing;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;

namespace LiteBoxParental
{
    public sealed class UnlockMenuItem : ISystemMenuItemPlugin
    {
        // Caption is re-read each time LaunchBox builds the Tools menu, so it shows the LIVE lock STATUS and the
        // action a click performs. Not-configured (parental off / no scope) is stated too, so the line never
        // lies about what clicking will do.
        public string Caption => CurrentLabel();

        /// <summary>The live lock-status label. LaunchBox reads Caption only ONCE (at menu build) and caches it,
        /// so AdminGuard also re-applies this to our menu item on every menu open to keep it current after a
        /// lock/unlock. Single source of truth for the text.</summary>
        internal static string CurrentLabel()
        {
            if (!LockState.ScopeActive) return "Parental control: not configured";
            return LockState.Locked
                ? "Parental control:  🔒 Locked  (click to unlock)"      // 🔒
                : "Parental control:  🔓 Unlocked  (click to lock)";     // 🔓
        }
        public Image IconImage => SystemIcons.Shield.ToBitmap();
        public bool ShowInLaunchBox => true;
        public bool ShowInBigBox => false;               // BigBox has its own lock/unlock
        public bool AllowInBigBoxWhenLocked => false;

        public void OnSelected()
        {
            try
            {
                if (!LockState.ScopeActive)
                {
                    Info("Parental control is not enabled for LaunchBox on this install.");
                    return;
                }
                if (!LockState.Locked)   // currently unlocked → offer to re-lock (no PIN)
                {
                    if (LockState.SetLocked(true))
                        Info("Parental control re-locked. The library is filtered again.");
                    else
                        Warn("Re-locked, but the view may still show the full library until you refresh or restart LaunchBox.");
                    return;
                }
                // Locked → unlock via PIN.
                if (PinLockout.LockedOut) { Warn("Too many wrong attempts — restart LaunchBox to try again."); return; }
                if (!PinVerify.HasPin) { Warn("No parental PIN is set (set one in LiteBox → Parental control)."); return; }

                using var dlg = new PinDialog();
                if (dlg.ShowDialog() != DialogResult.OK) return;
                if (PinVerify.Verify(dlg.Pin))
                {
                    PinLockout.Reset();
                    if (LockState.SetLocked(false))
                        Info("Unlocked. Restart or refresh the view — the full library is back.");
                    else
                        Warn("Couldn't safely unlock (library reload failed) — still locked to protect your data. Try again.");
                }
                else
                {
                    int left = PinLockout.RegisterFail();
                    Warn(left > 0 ? $"Wrong PIN — {left} attempt(s) left." : "Locked out — restart LaunchBox to try again.");
                }
            }
            catch (Exception ex) { Log.Line("[UnlockMenu] " + ex.Message); }
        }

        private static void Info(string m) => MessageBox.Show(m, "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Information);
        private static void Warn(string m) => MessageBox.Show(m, "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>A tiny masked-digit PIN prompt. Enter submits, Esc cancels.</summary>
    internal sealed class PinDialog : Form
    {
        private readonly TextBox _box;
        public string Pin => _box.Text.Trim();

        public PinDialog()
        {
            Text = "Enter parental PIN";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(260, 96);

            var lbl = new Label { Text = "PIN:", AutoSize = true, Location = new Point(16, 18) };
            _box = new TextBox { UseSystemPasswordChar = true, MaxLength = 8, Location = new Point(56, 15), Width = 180 };
            _box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };

            var ok = new Button { Text = "Unlock", DialogResult = DialogResult.OK, Location = new Point(56, 56), Width = 84 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(152, 56), Width = 84 };
            Controls.AddRange(new Control[] { lbl, _box, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}
