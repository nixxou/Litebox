// Per-request store install-state for the theme surfaces (BigBox Web / LiteBox Web).
//
// LiteBox already OWNS the store-scan + write-back: Host/StoreInstallStateSync reads the GOG / Steam / Epic /
// Uplay / EA clients' local state and writes IGame.Installed back through the data store, and Host/StoreSupport
// classifies a game by its <Source> and extracts the store ids. This adapter does NOT re-port any scanner — it
// is the thin theme-facing layer over those pieces:
//
//   • EnsureFresh()          — debounced trigger of StoreInstallStateSync (via the data manager) so IGame.Installed
//                              is fresh-ish for the current list/detail request; bumps Epoch when anything changed.
//   • IsInstalledOrPresent   — list/filter verdict: true = installed OR a non-store game (always present),
//                              false = a store game confirmed not installed (reads IGame.Installed after the sync).
//   • StoreLabel / IsStoreGame — the game's store ("GOG"/"Steam"/"Epic"/"Ubisoft"/"EA", null = non-store).
//
// No plugin, no reflection: StoreInstallStateSync + StoreSupport are native in-process LiteBox types.

#nullable enable

using System;
using System.Threading;
using LbApiHost.Host;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class WebStoreState
{
    // ── Debounce / single-flight around the native sync ──────────────────────────
    private static readonly object _lock = new();
    private static volatile bool _syncing;
    private static DateTime _lastSyncUtc = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    private static int _epoch;
    /// <summary>Bumps whenever the native sync reported a changed install field — drives the web heartbeat.</summary>
    public static int Epoch => Volatile.Read(ref _epoch);

    /// <summary>Reconcile store install state if the throttle window elapsed. Globally single-flight so a burst
    /// of concurrent web requests can never overlap store-DB scans; delegates to LiteBox's own
    /// StoreInstallStateSync (which writes IGame.Installed back), then per-game reads see the fresh value.</summary>
    public static void EnsureFresh(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastSyncUtc < MinInterval) return;
        lock (_lock)
        {
            if (_syncing) return;
            if (!force && DateTime.UtcNow - _lastSyncUtc < MinInterval) return;
            _syncing = true;
        }
        try
        {
            int changed = 0;
            try
            {
                if (PluginHelper.DataManager is HostDataManagerXml hdm)
                    changed = hdm.SyncStoreInstallStates(quiet: true);
            }
            catch (Exception ex) { LbLogWarn("store sync: " + ex.Message); }
            if (changed > 0) Interlocked.Increment(ref _epoch);
        }
        finally { _lastSyncUtc = DateTime.UtcNow; lock (_lock) _syncing = false; }
    }

    /// <summary>True only for games this service can meaningfully report on (store games).</summary>
    public static bool IsStoreGame(IGame? game) => StoreSupport.KindOf(game) != StoreKind.None;

    /// <summary>Store label for the detail button ("GOG"/"Steam"/"Epic"/"Ubisoft"/"EA"), or null for a
    /// non-store game (front-end then shows a normal Play action).</summary>
    public static string? StoreLabel(IGame? game) => StoreSupport.KindOf(game) switch
    {
        StoreKind.Gog => "GOG",
        StoreKind.Steam => "Steam",
        StoreKind.Epic => "Epic",
        StoreKind.Uplay => "Ubisoft",
        StoreKind.Ea => "EA",
        _ => null,
    };

    /// <summary>List/filter-friendly verdict: true = present, false = confirmed NOT installed.
    ///
    /// Installed is a USER checkbox (LaunchBox's game editor shows it next to Favorite / Hide /
    /// Broken) that LaunchBox ALSO maintains automatically for store games — it is not a computed
    /// "the file is on disk" flag. So a non-store game is not unconditionally present either: it is
    /// present unless the user unticked it. Only an UNSET value means nobody has an opinion, and for
    /// a local ROM that reads as present. Store state is kept fresh by <see cref="EnsureFresh"/>.</summary>
    public static bool IsInstalledOrPresent(IGame? game)
    {
        if (game == null) return true;
        try { return game.Installed ?? true; } catch { return true; }
    }

    private static void LbLogWarn(string msg) => LbApiHost.Host.Diag.LbLog.Warn("web", "[store] " + msg);
}
