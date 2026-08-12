// ─────────────────────────────────────────────────────────────────────────────
// Parental control — native LiteBox runtime + match engine.
// ─────────────────────────────────────────────────────────────────────────────
//
// The enforcement half of the native parental subsystem: the single source of
// truth the host GUI, the web frontends and the launcher read to decide what to
// show and what to gate. Clean-room port of the parts of ExtendDB's
// ParentalControlManager that LiteBox actually consumes — no plugin, no reflection.
//
// It combines three things over Host/Parental/ParentalConfig:
//
//   • Runtime LOCK STATE — an in-memory flag (never persisted) that starts LOCKED
//     at boot so a restart can never leave the library exposed. SetLocked() toggles
//     it and fires StateChanged; the PIN gate for unlocking lives in the caller.
//
//   • Derived STATE — Enabled (module on + a scope configured), Active (Enabled +
//     locked), ForceAll (Active + force-web), InstallNeedsUnlock, HotKey.
//
//   • MATCH tests — IsRatingAllowed (wildcard rating rules + Whitelist/Blacklist)
//     and IsNameHidden (the locked / unlocked BigBox hide-list). The wildcard
//     semantics ('*' any run, '?' one char, whole-string, case-insensitive) match
//     ExtendDB byte-for-byte so every LiteBox surface agrees.
//
//   • PIN gate — VerifyPin against BigBox's own PIN (Host/Data/BigBoxPin) plus a
//     process-wide 3-strike lockout, shared across every PIN entry point.
//
// Host/Media/ParentalBridge is a thin adapter over this class (padlock indicator,
// tree/list filtering, the install gate); Host/Web/WebParentalState reads the same
// state so the web enforces identically.

#nullable enable

using System;
using System.Linq;
using System.Text.RegularExpressions;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Parental;

internal static class ParentalFilter
{
    // ── Module gate ─────────────────────────────────────────────────────────
    // Disabling the parental MODULE (Options → Modules) fully disengages parental
    // regardless of the config scopes / PIN — same as ExtendDB's own gate.
    private static bool ModuleOn()
    {
        try { return LbModules.On(LbModule.Parental); } catch { return false; }
    }

    // ── Runtime lock state (in-memory only, defaults LOCKED) ────────────────

    private static bool _locked = true;

    /// <summary>Current runtime lock state. True = locked (filter engaged). Starts
    /// locked at boot; unlocking goes through the PIN gate in the caller.</summary>
    public static bool Locked => _locked;

    /// <summary>Fires on every <see cref="Locked"/> transition AND after a config save
    /// (see <see cref="NotifyConfigChanged"/>) so consumers re-apply / drop filters.</summary>
    public static event Action? StateChanged;

    /// <summary>Sets the runtime lock state and fires <see cref="StateChanged"/> on a real
    /// change. Locking is unconditional; the PIN gate for unlocking lives in the caller.</summary>
    public static void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        LbLog.Info("parental", "lock state -> " + (locked ? "LOCKED" : "unlocked"));
        try { StateChanged?.Invoke(); } catch { }
    }

    /// <summary>Called after the config panel saves: drops the cached config and fires
    /// <see cref="StateChanged"/> so live filters (host tree/list, web) re-read the new rules.
    /// If the config now enables no scope, resets the runtime flag to locked so a later
    /// re-enable starts locked (the safe default).</summary>
    public static void NotifyConfigChanged()
    {
        ParentalConfig.Invalidate();
        _hasPinCache = null;   // a PIN set/clear is a config change — re-read next time
        try { if (!ParentalConfig.Instance.AnyScopeEnabled) _locked = true; } catch { }
        try { StateChanged?.Invoke(); } catch { }
    }

    // ── Derived state ───────────────────────────────────────────────────────

    /// <summary>The native subsystem is always compiled in; the module gate decides
    /// whether it participates. True when the parental module is enabled.</summary>
    public static bool Present => ModuleOn();

    /// <summary>Parental control is configured (module on, a scope switched on, AND a PIN set — without a PIN
    /// there is no unlock path, so parental must never engage).</summary>
    public static bool Enabled => ModuleOn() && ParentalConfig.Instance.AnyScopeEnabled && HasPin;

    /// <summary>Actively filtering this session (configured AND locked).</summary>
    public static bool Active => Enabled && _locked;

    /// <summary>The "force web" block-all is in effect (hide EVERY game, any rating).</summary>
    public static bool ForceAll => Active && ParentalConfig.Instance.ForceWebHideAll;

    /// <summary>Hide not-installed games (Installed=false) — active only while parental is Active. Default ON.</summary>
    public static bool HideUninstalled => Active && ParentalConfig.Instance.HideUninstalled;

    /// <summary>"Force web hide-all" is CONFIGURED (module + scope on) regardless of the DESKTOP lock —
    /// the web combines this with its own per-client lock state (a web-locked client sees nothing).</summary>
    public static bool ForceAllConfigured => Enabled && ParentalConfig.Instance.ForceWebHideAll;

    /// <summary>Installing a store game must be gated behind the PIN (active + block-install).</summary>
    public static bool InstallNeedsUnlock => Active && ParentalConfig.Instance.BlockInstallWhenLocked;

    /// <summary>The configured lock hotkey as a WinForms <c>Keys</c> int (0 = none).</summary>
    public static int HotKey => ParentalConfig.Instance.HotKey;

    // ── Match tests ─────────────────────────────────────────────────────────

    /// <summary>True when a game with this rating should be VISIBLE. Allow-all when inactive.
    /// Whitelist: show only when a rule matches; Blacklist: show unless a rule matches.</summary>
    public static bool IsRatingAllowed(string? rating)
    {
        if (!Active) return true;
        var cfg = ParentalConfig.Instance;
        string r = rating ?? "";
        bool matched = cfg.Rules.Any(rule => WildcardMatch(r, rule));
        return cfg.Mode == ParentalMode.Whitelist ? matched : !matched;
    }

    /// <summary>True when a platform / category / playlist with this name must be hidden.
    /// Whole-name, case-insensitive; the On-list when locked, the Off-list when unlocked.</summary>
    public static bool IsNameHidden(string? name)
    {
        if (!Active || string.IsNullOrEmpty(name)) return false;
        var cfg = ParentalConfig.Instance;
        var list = _locked ? cfg.HiddenPlatformsBigBoxOn : cfg.HiddenPlatformsBigBoxOff;
        return list.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whole-string, case-insensitive wildcard match. '*' = any run (incl. empty),
    /// '?' = exactly one char. Mirrors ExtendDB.ParentalControlManager.WildcardMatch.</summary>
    public static bool WildcardMatch(string? input, string? pattern)
    {
        if (pattern == null) return false;
        string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input ?? "", rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ── PIN gate (over BigBox's own PIN) ────────────────────────────────────
    // The credential is BigBox's parental PIN, read through Host/Data/BigBoxPin —
    // one PIN everywhere. Failed-attempt counting is process-wide so a wrong-guesser
    // can't get fresh tries by switching surfaces; 3 failures trip the lockout, which
    // only resets on a host restart.

    /// <summary>Wrong PIN attempts allowed before the lockout trips.</summary>
    public const int MaxPinAttempts = 3;

    private static int _pinFailed;
    private static bool _pinLockedOut;

    /// <summary>True once the PIN is locked out until the next host restart.</summary>
    public static bool PinLockedOut => _pinLockedOut;

    // Cached so the hot visibility path (Enabled → per-game) doesn't decrypt BigBoxSettings.xml each call.
    // Cleared on NotifyConfigChanged (a PIN set/clear goes through the config save).
    private static bool? _hasPinCache;

    /// <summary>True when a PIN is configured (BigBox has one set). Cached.</summary>
    public static bool HasPin
    {
        get
        {
            if (_hasPinCache == null) { try { _hasPinCache = !string.IsNullOrEmpty(CurrentPin()); } catch { _hasPinCache = false; } }
            return _hasPinCache.Value;
        }
    }

    /// <summary>True when <paramref name="pin"/> matches BigBox's configured PIN.
    /// False when none is set or the input is wrong. Single comparison point.</summary>
    public static bool VerifyPin(string? pin)
    {
        var expected = CurrentPin();
        if (string.IsNullOrEmpty(expected)) return false;
        // Constant-time compare (plugin parity: FixedTimeEquals) — the length check leaks only the
        // digit count, acceptable for a 4-8 digit PIN.
        var a = System.Text.Encoding.UTF8.GetBytes(pin ?? "");
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Records one wrong attempt and returns how many remain. 0 means the
    /// lockout just tripped (or was already tripped) — callers must stop accepting input.</summary>
    public static int RegisterFailedPinAttempt()
    {
        if (_pinLockedOut) return 0;
        _pinFailed++;
        if (_pinFailed >= MaxPinAttempts)
        {
            _pinLockedOut = true;
            LbLog.Info("parental", "PIN gate locked out after " + MaxPinAttempts + " failed attempts — restart required.");
            return 0;
        }
        return MaxPinAttempts - _pinFailed;
    }

    private static string CurrentPin()
    {
        try { return BigBoxPin.Current() ?? ""; } catch { return ""; }
    }
}
