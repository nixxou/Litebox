// ─────────────────────────────────────────────────────────────────────────────
// Parental control — native LiteBox config model.
// ─────────────────────────────────────────────────────────────────────────────
//
// Clean-room native port of the ExtendDB plugin's ParentalControlConfig. The MODEL
// is reproduced faithfully (same knobs + semantics) so the host, the web frontends
// and the launcher all agree with what LaunchBox-web/BigBox enforce — but nothing
// here reflects into or depends on the plugin.
//
// STORAGE: everything (scalars + the three lists) now lives in ONE shared flat file,
// Core\litebox-parental.dat, read/written through Host/Parental/ParentalNativeExport.
// That same file is what the native .bin reads before .NET and what the standalone
// parental plugin edits — one source of truth, no more LiteBox.ini [Parental] +
// parental-lists.json split. Load() parses it; Save() rewrites it atomically.
//
// The PIN itself is NOT stored here: it is BigBox's own parental PIN, read/written
// through Host/Data/BigBoxPin (one PIN everywhere). The runtime lock state, the
// wildcard rating match and the PIN gate live in Host/Parental/ParentalFilter.
//
// Singleton, mirrors RomConfig: Instance lazy-loads from disk, Invalidate() forces a
// reload after a Save from the config panel.

#nullable enable

using System;
using System.Collections.Generic;
using LbApiHost.Host;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

/// <summary>Whitelist = only listed ratings are shown; Blacklist = listed ratings are hidden.</summary>
internal enum ParentalMode { Whitelist = 0, Blacklist = 1 }

/// <summary>How the BigBox write-guard reacts to a filtered platform-XML save.
/// Block (default) = never rewrite the live library; Merge = fold the filtered
/// subset back in (experimental). Persisted for parity / future enforcement.</summary>
internal enum ParentalWriteMode { Merge = 0, Block = 1 }

/// <summary>Persisted parental-control settings. Everything (scalars + lists) lives in the shared
/// Core\litebox-parental.dat via ParentalNativeExport. See file header.</summary>
internal sealed class ParentalConfig
{
    // ── Scalars ─────────────────────────────────────────────────────────────

    /// <summary>Parental control is configured. ONE switch — when on it applies everywhere (LiteBox
    /// desktop, the web frontends, and vanilla LaunchBox/BigBox via the native filter). There is no
    /// per-app scope anymore. The module master switch LbModule.Parental gates on top of this.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>LaunchBox "force web" block-all: while active (enabled + locked) hide EVERY
    /// game regardless of rating — supersedes the rules. LaunchBox-only.</summary>
    public bool ForceWebHideAll { get; set; } = false;

    /// <summary>BigBox write-guard policy (Block = safe default). Persisted for parity.</summary>
    public ParentalWriteMode BigBoxWriteMode { get; set; } = ParentalWriteMode.Block;

    /// <summary>A locked user may still change a game's star rating from the web client.</summary>
    public bool AllowLockedModifyRatings { get; set; } = false;

    /// <summary>A locked user may still toggle a game's favorite state from the web client.</summary>
    public bool AllowLockedModifyFavorites { get; set; } = false;

    /// <summary>While locked, installing a not-yet-installed store game asks for the PIN first.</summary>
    public bool BlockInstallWhenLocked { get; set; } = false;

    /// <summary>Whitelist or Blacklist semantics for <see cref="Rules"/>.</summary>
    public ParentalMode Mode { get; set; } = ParentalMode.Whitelist;

    /// <summary>Hide games marked not-installed (Installed=false) while parental is active. Default ON.</summary>
    public bool HideUninstalled { get; set; } = true;

    /// <summary>Global hotkey that pops the lock/unlock dialog, as the int value of a
    /// WinForms <c>Keys</c> (may carry modifier flags). 0 = no hotkey.</summary>
    public int HotKey { get; set; } = 0;

    // ── Lists (shared .dat) ─────────────────────────────────────────────────

    /// <summary>Rating patterns; wildcards '*' (any run) and '?' (one char), matched
    /// case-insensitively against a game's rating.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>Platform / category names hidden from the BigBox filter page WHEN LOCKED.</summary>
    public List<string> HiddenPlatformsBigBoxOn { get; set; } = new();

    /// <summary>Platform / category names hidden from the BigBox filter page WHEN UNLOCKED.</summary>
    public List<string> HiddenPlatformsBigBoxOff { get; set; } = new();

    /// <summary>Config-format version that last wrote the shared .dat ("0.0.0" = pre-versioning).</summary>
    public string ConfigVersion { get; set; } = "0.0.0";

    /// <summary>The config-level "configured" flag (alias of <see cref="Enabled"/>, kept for callers).
    /// The module master switch LbModule.Parental gates on top of this in ParentalFilter.</summary>
    public bool AnyScopeEnabled => Enabled;

    // ── Singleton lifecycle ─────────────────────────────────────────────────

    private static ParentalConfig? _instance;
    public static ParentalConfig Instance => _instance ??= Load();

    /// <summary>Force a reload from disk on next access (after a Save from the config panel).</summary>
    public static void Invalidate() => _instance = null;

    private static ParentalConfig Load()
    {
        var c = new ParentalConfig();
        try
        {
            // Single shared source of truth: Core\litebox-parental.dat (read via ParentalNativeExport).
            var d = ParentalNativeExport.Read();
            if (d != null)
            {
                c.Enabled                    = d.Enabled;
                c.Mode                       = d.Mode;
                c.HideUninstalled            = d.HideUninstalled;
                c.AllowLockedModifyRatings   = d.AllowRatings;
                c.AllowLockedModifyFavorites = d.AllowFavorites;
                c.ForceWebHideAll            = d.ForceWebHideAll;
                c.BlockInstallWhenLocked     = d.BlockInstall;
                c.BigBoxWriteMode            = d.WriteMode;
                c.HotKey                     = d.HotKey;
                c.ConfigVersion              = d.ConfigVersion;
                c.Rules                      = Clean(d.Rules);
                c.HiddenPlatformsBigBoxOn    = Clean(d.HideOn);
                c.HiddenPlatformsBigBoxOff   = Clean(d.HideOff);
            }
            // No file yet (fresh install) → the defaults above stand; the first Save() writes the .dat.
        }
        catch (Exception ex) { Log("load failed: " + ex.Message); }
        return c;
    }

    /// <summary>Persist to the shared Core\litebox-parental.dat. The PIN is written separately through
    /// Host/Data/BigBoxPin (not here). `this` is the singleton the panel just mutated, so the writer reads
    /// the new values in memory.</summary>
    public void Save()
    {
        try { ConfigVersion = Data.ConfigVersioning.Stamp(); } catch { }   // provenance stamp
        try { ParentalNativeExport.Write(); }                              // the .dat IS the config now
        catch (Exception ex) { Log("save failed: " + ex.Message); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Trim, drop blanks, collapse any embedded newlines (each entry stays one line).</summary>
    private static List<string> Clean(List<string>? src)
    {
        var list = new List<string>();
        if (src == null) return list;
        foreach (var raw in src)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    private static void Log(string msg) => LbLog.Info("parental", "config: " + msg);
}
