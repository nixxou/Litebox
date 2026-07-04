// Mirrors ExtendDB's parental-control state into the host so the LiteBox GUI can
// enforce the SAME display restrictions launchbox-web does — without referencing
// ExtendDB (the host can't, it loads the plugin, not the other way round). All
// access is by reflection over ExtendDB.ParentalControlManager (namespace ExtendDB).
//
// What the host uses this for:
//   • a toolbar padlock indicator (active / locked / unlocked),
//   • hiding the configured categories / platforms / playlists from the source tree,
//   • hiding games conditionally from the list (ESRB rating + force-all + a game
//     whose platform sits under a hidden category/platform).
//
// Design — snapshot, no per-game reflection:
//   Refresh() reads everything ONCE (at GUI build, and on every ExtendDB lock-state
//   change) into managed fields. The hot path (IsRatingAllowed / IsNameHidden, called
//   once per game / per node) then touches only those fields, never reflection. The
//   wildcard rating semantics are re-implemented to match
//   ExtendDB.ParentalControlManager.WildcardMatch byte-for-byte (whole-string,
//   case-insensitive, '*' = any run, '?' = one char) so the host and the plugin agree.
//
// Reflected surface (all public static on ExtendDB.ParentalControlManager):
//   ParentalControlConfig Config { get; }
//     bool LaunchBoxEnabled, BigBoxEnabled, LaunchBoxForceWeb
//     ParentalFilterMode Mode                       (enum: Whitelist=0, Blacklist=1)
//     List<string> Rules
//     List<string> HiddenPlatformsBigBoxOn / HiddenPlatformsBigBoxOff
//   bool LaunchBoxLocked { get; }
//   event Action LaunchBoxLockStateChanged / BigBoxLockStateChanged
//
// Lock semantics under LiteBox:
//   LiteBox runs as LiteBox.exe, so to ExtendDB it is a "LaunchBox" host (IsBigBox
//   false). The runtime lock therefore tracks LaunchBoxLocked, which defaults to
//   LOCKED at boot and has no unlock UI here yet — so when parental control is
//   configured the host starts (and stays) locked, exactly like launchbox-web with
//   no unlock cookie. Enabled = either scope switched on; Active = Enabled && Locked.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Media;

internal static class ParentalBridge
{
    private const BindingFlags SP = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags IP = BindingFlags.Public | BindingFlags.Instance;

    private static bool _probed;
    private static Type _mgrType;
    private static PropertyInfo _configProp;   // ParentalControlManager.Config
    private static PropertyInfo _lockedProp;   // ParentalControlManager.LaunchBoxLocked
    private static MethodInfo _showLockDialog;  // ParentalLockPopupForm.ShowFor(IWin32Window)
    private static MethodInfo _modulesOn;       // ExtendDB.Modules.On(string key)
    private static MethodInfo _verifyPin;       // ParentalControlManager.VerifyPin(string)→bool
    private static PropertyInfo _pinLockedOutProp; // ParentalControlManager.PinLockedOut
    private static MethodInfo _registerFail;    // ParentalControlManager.RegisterFailedPinAttempt()→int

    // ── Snapshot (filled by Refresh) ──────────────────────────────────────────
    private static bool _snap;          // a snapshot has been taken at least once
    private static bool _present;       // the parental type is loaded (ExtendDB present + new enough)
    private static bool _enabled;       // LaunchBoxEnabled || BigBoxEnabled
    private static bool _locked;        // LaunchBoxLocked
    private static bool _forceWeb;      // LaunchBoxForceWeb
    private static bool _blockInstall;  // BlockInstallWhenLocked (gate store installs behind the PIN)
    private static bool _whitelist;     // Mode == Whitelist
    private static int _hotKey;         // ParentalControlConfig.HotKey (WinForms Keys value; 0 = none)
    private static Regex[] _ruleRegex = Array.Empty<Regex>();
    private static HashSet<string> _hiddenOn = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _hiddenOff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the UI-agnostic background thread whenever ExtendDB reports a
    /// lock-state change (already Refresh()ed). The GUI marshals + re-applies filters.</summary>
    public static event Action StateChanged;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            _mgrType = asm?.GetType("ExtendDB.ParentalControlManager");
            if (_mgrType == null) return;

            _configProp = _mgrType.GetProperty("Config", SP);
            _lockedProp = _mgrType.GetProperty("LaunchBoxLocked", SP);

            // Module gate: ExtendDB.Modules.On("parental"). When the parental
            // MODULE is disabled the subsystem is off regardless of the config
            // switches (LaunchBoxEnabled / PIN), so the host must treat parental
            // as absent — same as launchbox-web. The string overload avoids
            // reflecting the Module enum value. Absent on older plugins → null,
            // treated as "on" (back-compat) in Refresh.
            var modulesType = asm.GetType("ExtendDB.Modules");
            _modulesOn = modulesType?.GetMethod("On", SP, null, new[] { typeof(string) }, null);

            // The lock/unlock popup (with PIN gate) — same entry the parental hotkey uses in LB.
            var popup = asm.GetType("ExtendDB.ParentalLockPopupForm");
            _showLockDialog = popup?.GetMethod("ShowFor", SP, null, new[] { typeof(IWin32Window) }, null);

            // PIN verify (without toggling the global lock) for the one-shot store-install gate.
            _verifyPin = _mgrType.GetMethod("VerifyPin", SP, null, new[] { typeof(string) }, null);
            _pinLockedOutProp = _mgrType.GetProperty("PinLockedOut", SP);
            _registerFail = _mgrType.GetMethod("RegisterFailedPinAttempt", SP, null, Type.EmptyTypes, null);

            // Refresh on either lock transition (LaunchBox is the one that moves under
            // LiteBox; BigBox is hooked too, harmlessly). The events are Action-typed.
            HookEvent("LaunchBoxLockStateChanged");
            HookEvent("BigBoxLockStateChanged");
        }
        catch { _mgrType = null; }
    }

    private static void HookEvent(string name)
    {
        try
        {
            var ev = _mgrType.GetEvent(name, SP);
            ev?.AddEventHandler(null, new Action(OnExtLockChanged));
        }
        catch { }
    }

    private static void OnExtLockChanged()
    {
        try { Refresh(); } catch { }
        try { StateChanged?.Invoke(); } catch { }
    }

    /// <summary>Re-reads the whole parental snapshot from ExtendDB. Cheap (a handful of
    /// reflection reads); call at GUI build and whenever the lock state may have moved.</summary>
    public static void Refresh()
    {
        Probe();
        _snap = true;
        _present = false;
        _enabled = _locked = _forceWeb = _whitelist = _blockInstall = false;
        _hotKey = 0;
        _ruleRegex = Array.Empty<Regex>();
        _hiddenOn = new(StringComparer.OrdinalIgnoreCase);
        _hiddenOff = new(StringComparer.OrdinalIgnoreCase);

        if (_mgrType == null || _configProp == null) return;
        try
        {
            object cfg = _configProp.GetValue(null);
            if (cfg == null) return;
            _present = true;

            Type ct = cfg.GetType();
            bool lbEnabled = ReadBool(ct, cfg, "LaunchBoxEnabled");
            bool bbEnabled = ReadBool(ct, cfg, "BigBoxEnabled");

            // Parental MODULE gate. Disabling the module fully disengages parental
            // (mirrors ExtendDB's own FilteringActiveLaunchBox / WebParentalState).
            // Default true when the accessor is missing (older plugin) so existing
            // setups keep working.
            bool moduleOn = true;
            try
            {
                if (_modulesOn != null)
                    moduleOn = _modulesOn.Invoke(null, new object[] { "parental" }) is bool mb && mb;
            }
            catch { moduleOn = true; }

            _enabled = moduleOn && (lbEnabled || bbEnabled);
            _forceWeb = ReadBool(ct, cfg, "LaunchBoxForceWeb");
            _blockInstall = ReadBool(ct, cfg, "BlockInstallWhenLocked");   // gate store installs behind the PIN
            _locked = _lockedProp != null && _lockedProp.GetValue(null) is bool b && b;
            _hotKey = ct.GetField("HotKey")?.GetValue(cfg) is int hk ? hk : 0;

            // Mode is an enum (Whitelist=0, Blacklist=1).
            var modeField = ct.GetField("Mode");
            object modeVal = modeField?.GetValue(cfg);
            _whitelist = modeVal == null || Convert.ToInt32(modeVal) == 0;

            _ruleRegex = ReadList(ct, cfg, "Rules")
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(BuildRuleRegex)
                .ToArray();

            foreach (var n in ReadList(ct, cfg, "HiddenPlatformsBigBoxOn")) if (!string.IsNullOrEmpty(n)) _hiddenOn.Add(n);
            foreach (var n in ReadList(ct, cfg, "HiddenPlatformsBigBoxOff")) if (!string.IsNullOrEmpty(n)) _hiddenOff.Add(n);
        }
        catch { _present = false; }
    }

    private static void EnsureSnapshot() { if (!_snap) Refresh(); }

    private static bool ReadBool(Type t, object obj, string field)
        => t.GetField(field)?.GetValue(obj) is bool b && b;

    private static IEnumerable<string> ReadList(Type t, object obj, string field)
    {
        if (t.GetField(field)?.GetValue(obj) is IEnumerable e)
            foreach (var o in e) yield return o as string;
    }

    /// <summary>Whole-string, case-insensitive wildcard → regex, matching
    /// ExtendDB.ParentalControlManager.WildcardMatch exactly.</summary>
    private static Regex BuildRuleRegex(string pattern)
    {
        string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>True iff ExtendDB exposes the parental subsystem (plugin loaded + new enough).</summary>
    public static bool Present { get { EnsureSnapshot(); return _present; } }

    /// <summary>True iff parental control is configured (LaunchBox or BigBox switch on).</summary>
    public static bool Enabled { get { EnsureSnapshot(); return _present && _enabled; } }

    /// <summary>Current runtime lock state (LaunchBox scope — the one that applies to LiteBox).</summary>
    public static bool Locked { get { EnsureSnapshot(); return _locked; } }

    /// <summary>True when parental control is actively filtering this session (configured AND locked).</summary>
    public static bool Active { get { EnsureSnapshot(); return _present && _enabled && _locked; } }

    /// <summary>True when the "force web" block-all is in effect (hide EVERY game, any rating).</summary>
    public static bool ForceAll { get { EnsureSnapshot(); return Active && _forceWeb; } }

    /// <summary>True when installing a store game must be gated behind the PIN (active + locked +
    /// the BlockInstallWhenLocked option). The store Install button shows the unlock dialog first.</summary>
    public static bool InstallNeedsUnlock { get { EnsureSnapshot(); return _present && _enabled && _locked && _blockInstall; } }

    /// <summary>True when a game with this ESRB/age rating should be VISIBLE. Allow-all when inactive.</summary>
    public static bool IsRatingAllowed(string rating)
    {
        EnsureSnapshot();
        if (!Active) return true;
        string r = rating ?? "";
        bool matched = _ruleRegex.Any(re => re.IsMatch(r));
        return _whitelist ? matched : !matched;   // Whitelist: show only if matched; Blacklist: show unless matched
    }

    /// <summary>True when a platform / category / playlist with this name must be hidden.
    /// Whole-name, case-insensitive; the On-list when locked, the Off-list when unlocked.</summary>
    public static bool IsNameHidden(string name)
    {
        EnsureSnapshot();
        if (!Active || string.IsNullOrEmpty(name)) return false;
        return (_locked ? _hiddenOn : _hiddenOff).Contains(name);
    }

    /// <summary>The configured parental hotkey as a WinForms <see cref="Keys"/> value (0 = none).
    /// Drives the host-side key capture in <see cref="Host.HostHotKeys"/>.</summary>
    public static int HotKey { get { EnsureSnapshot(); return _hotKey; } }

    /// <summary>Pops ExtendDB's parental lock/unlock dialog (PIN-gated) — the same entry its own
    /// hotkey uses. No-op if ExtendDB doesn't expose it. Toggling the lock fires the lock event,
    /// which refreshes the host padlock + filters via <see cref="StateChanged"/>.</summary>
    public static void ShowLockDialog(IWin32Window owner)
    {
        Probe();
        _showLockDialog?.Invoke(null, new object[] { owner });
    }

    /// <summary>Prompts for the PIN and verifies it to authorize ONE store install — WITHOUT
    /// unlocking parental globally (the lock state and the list filtering are untouched, so the
    /// catalog isn't reloaded). Returns true only on a correct PIN. Honours the shared lockout
    /// (3 wrong PINs anywhere = locked out until restart). Cancel / wrong PIN / lockout → false.</summary>
    public static bool VerifyInstallPin(IWin32Window owner)
    {
        Probe();
        if (_verifyPin == null) return false;   // can't verify → deny (safe)
        if (PinLockedOut())
        {
            try { MessageBox.Show(owner, "Locked out — too many wrong PINs. Restart required.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            return false;
        }
        while (true)
        {
            var pin = PinPromptForm.Prompt(owner);
            if (pin == null) return false;   // cancelled
            bool ok = false;
            try { ok = _verifyPin.Invoke(null, new object[] { pin }) is bool b && b; } catch { }
            if (ok) return true;
            int remaining = -1;
            try { if (_registerFail != null) remaining = _registerFail.Invoke(null, null) is int r ? r : -1; } catch { }
            if (remaining == 0)
            {
                try { MessageBox.Show(owner, "Locked out — restart required.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                return false;
            }
            try { MessageBox.Show(owner, remaining > 0 ? ("Wrong PIN — " + remaining + " attempt(s) left.") : "Wrong PIN.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }
    }

    private static bool PinLockedOut()
    {
        try { return _pinLockedOutProp?.GetValue(null) is bool b && b; } catch { return false; }
    }

    /// <summary>Minimal modal PIN entry (masked) for the one-shot install gate. Returns the entered
    /// PIN, or null if cancelled. Deliberately separate from ExtendDB's lock popup so it never
    /// toggles the global parental lock.</summary>
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
            var lbl = new Label { Text = "Enter PIN to allow this install:", AutoSize = true, Left = S(12), Top = S(14), ForeColor = LiteBoxTheme.Fg };
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
