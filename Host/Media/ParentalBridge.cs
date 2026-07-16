// Thin host-side adapter over the NATIVE parental subsystem (Host/Parental/*).
//
// Historically this reflected into ExtendDB.ParentalControlManager. LiteBox now owns
// the parental engine natively (Host/Parental/ParentalConfig + ParentalFilter), so this
// class is just a stable façade: the padlock indicator, the source-tree/list filters,
// HostHotKeys, LaunchButtons and WebParentalState all keep calling ParentalBridge.* while
// the internals delegate to ParentalFilter / ParentalConfig — no plugin, no reflection.
//
// Public surface (unchanged from the reflected version):
//   Present / Enabled / Locked / Active / ForceAll / InstallNeedsUnlock / HotKey,
//   IsRatingAllowed / IsNameHidden, Refresh(), StateChanged,
//   VerifyInstallPin(owner), ShowLockDialog(owner).

using System;
using System.Windows.Forms;
using System.Drawing;
using LbApiHost.Host.Parental;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Media;

internal static class ParentalBridge
{
    private static bool _hooked;

    /// <summary>Raised whenever the native parental runtime reports a lock-state (or config)
    /// change. Re-raised from <see cref="ParentalFilter.StateChanged"/>; the GUI marshals +
    /// re-applies filters.</summary>
    public static event Action StateChanged;

    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        try { ParentalFilter.StateChanged += OnNativeStateChanged; } catch { }
    }

    private static void OnNativeStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    /// <summary>Re-reads the parental snapshot (drops the cached config so the next access reloads).</summary>
    public static void Refresh()
    {
        EnsureHooked();
        ParentalConfig.Invalidate();
    }

    // ── Public state (delegated to the native filter) ───────────────────────────

    /// <summary>True iff the parental subsystem participates (module enabled).</summary>
    public static bool Present { get { EnsureHooked(); return ParentalFilter.Present; } }

    /// <summary>True iff parental control is configured (module on AND a scope switched on).</summary>
    public static bool Enabled { get { EnsureHooked(); return ParentalFilter.Enabled; } }

    /// <summary>Current runtime lock state.</summary>
    public static bool Locked { get { EnsureHooked(); return ParentalFilter.Locked; } }

    /// <summary>True when parental control is actively filtering this session (configured AND locked).</summary>
    public static bool Active { get { EnsureHooked(); return ParentalFilter.Active; } }

    /// <summary>True when the "force web" block-all is in effect (hide EVERY game, any rating).</summary>
    public static bool ForceAll { get { EnsureHooked(); return ParentalFilter.ForceAll; } }

    /// <summary>True when installing a store game must be gated behind the PIN.</summary>
    public static bool InstallNeedsUnlock { get { EnsureHooked(); return ParentalFilter.InstallNeedsUnlock; } }

    /// <summary>The configured parental hotkey as a WinForms <see cref="Keys"/> value (0 = none).</summary>
    public static int HotKey { get { EnsureHooked(); return ParentalFilter.HotKey; } }

    /// <summary>True when a game with this ESRB/age rating should be VISIBLE. Allow-all when inactive.</summary>
    public static bool IsRatingAllowed(string rating)
    {
        EnsureHooked();
        return ParentalFilter.IsRatingAllowed(rating);
    }

    /// <summary>True when a platform / category / playlist with this name must be hidden.</summary>
    public static bool IsNameHidden(string name)
    {
        EnsureHooked();
        return ParentalFilter.IsNameHidden(name);
    }

    /// <summary>Pops the native lock/unlock dialog. If currently locked, prompts for the PIN and
    /// unlocks on success; if unlocked, re-locks immediately (no PIN needed to lock). Toggling the
    /// lock fires <see cref="ParentalFilter.StateChanged"/>, which refreshes the padlock + filters.</summary>
    public static void ShowLockDialog(IWin32Window owner)
    {
        EnsureHooked();
        try
        {
            if (!ParentalFilter.Locked)
            {
                // Currently unlocked → re-lock unconditionally.
                ParentalFilter.SetLocked(true);
                return;
            }

            // Currently locked → require the PIN to unlock.
            if (!ParentalFilter.HasPin)
            {
                // No PIN configured: unlocking can't be gated, so just unlock.
                ParentalFilter.SetLocked(false);
                return;
            }
            if (ParentalFilter.PinLockedOut)
            {
                try { MessageBox.Show(owner, "Locked out — too many wrong PINs. Restart required.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                return;
            }
            while (true)
            {
                var pin = PinPromptForm.Prompt(owner);
                if (pin == null) return;   // cancelled
                if (ParentalFilter.VerifyPin(pin)) { ParentalFilter.SetLocked(false); return; }
                int remaining = ParentalFilter.RegisterFailedPinAttempt();
                if (remaining == 0)
                {
                    try { MessageBox.Show(owner, "Locked out — restart required.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                    return;
                }
                try { MessageBox.Show(owner, "Wrong PIN — " + remaining + " attempt(s) left.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Prompts for the PIN and verifies it to authorize ONE store install — WITHOUT
    /// unlocking parental globally. Returns true only on a correct PIN. Honours the shared 3-strike
    /// lockout. Cancel / wrong PIN / lockout → false.</summary>
    public static bool VerifyInstallPin(IWin32Window owner)
    {
        EnsureHooked();
        if (!ParentalFilter.HasPin) return false;   // can't verify → deny (safe)
        if (ParentalFilter.PinLockedOut)
        {
            try { MessageBox.Show(owner, "Locked out — too many wrong PINs. Restart required.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            return false;
        }
        while (true)
        {
            var pin = PinPromptForm.Prompt(owner);
            if (pin == null) return false;   // cancelled
            if (ParentalFilter.VerifyPin(pin)) return true;
            int remaining = ParentalFilter.RegisterFailedPinAttempt();
            if (remaining == 0)
            {
                try { MessageBox.Show(owner, "Locked out — restart required.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                return false;
            }
            try { MessageBox.Show(owner, "Wrong PIN — " + remaining + " attempt(s) left.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }
    }

    /// <summary>Minimal modal PIN entry (masked). Returns the entered PIN, or null if cancelled.</summary>
    private sealed class PinPromptForm : LiteBoxForm
    {
        private readonly TextBox _box;
        private PinPromptForm()
        {
            Text = "Parental PIN";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(S(300), S(112));
            var lbl = new Label { Text = "Enter PIN:", AutoSize = true, Left = S(12), Top = S(14), ForeColor = LiteBoxTheme.Fg };
            _box = new TextBox { Left = S(12), Top = S(38), Width = S(276), UseSystemPasswordChar = true, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = S(132), Top = S(72), Width = S(70), Height = S(26), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Ok, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = S(212), Top = S(72), Width = S(70), Height = S(26), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.CancelBtn, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { lbl, _box, ok, cancel });
        }
        public static string Prompt(IWin32Window owner)
        {
            try
            {
                using var f = new PinPromptForm();
                var res = owner != null ? f.ShowDialog(owner) : f.ShowDialog();
                return res == DialogResult.OK ? f._box.Text : null;
            }
            catch { return null; }
        }
    }
}
