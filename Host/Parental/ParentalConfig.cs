// ─────────────────────────────────────────────────────────────────────────────
// Parental control — native LiteBox config model.
// ─────────────────────────────────────────────────────────────────────────────
//
// Clean-room native port of the ExtendDB plugin's ParentalControlConfig. The MODEL
// is reproduced faithfully (same knobs + semantics) so the host, the web frontends
// and the launcher all agree with what LaunchBox-web/BigBox enforce — but nothing
// here reflects into or depends on the plugin. Only the STORAGE backend differs:
//
//   • The scalar switches (LaunchBox/BigBox enable, force-web, write-mode, the two
//     "allow while locked" toggles, block-install, filter Mode, the lock hotkey) →
//     LiteBox.ini [Parental] via LiteBoxConfig.GetSec/SetSec.
//   • The three lists (rating rules + the two BigBox hide-lists) → a small JSON
//     sidecar LiteBoxPaths.File("parental-lists.json") (System.Text.Json).
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
using System.Globalization;
using System.IO;
using System.Text.Json;
using LbApiHost.Host;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

/// <summary>Whitelist = only listed ratings are shown; Blacklist = listed ratings are hidden.</summary>
internal enum ParentalMode { Whitelist = 0, Blacklist = 1 }

/// <summary>How the BigBox write-guard reacts to a filtered platform-XML save.
/// Block (default) = never rewrite the live library; Merge = fold the filtered
/// subset back in (experimental). Persisted for parity / future enforcement.</summary>
internal enum ParentalWriteMode { Merge = 0, Block = 1 }

/// <summary>Persisted parental-control settings. Scalars live in LiteBox.ini
/// [Parental]; the three lists live in parental-lists.json. See file header.</summary>
internal sealed class ParentalConfig
{
    internal const string Section = "Parental";

    // ── Scalars (LiteBox.ini [Parental]) ────────────────────────────────────

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

    /// <summary>Global hotkey that pops the lock/unlock dialog, as the int value of a
    /// WinForms <c>Keys</c> (may carry modifier flags). 0 = no hotkey.</summary>
    public int HotKey { get; set; } = 0;

    // ── Lists (parental-lists.json) ─────────────────────────────────────────

    /// <summary>Rating patterns; wildcards '*' (any run) and '?' (one char), matched
    /// case-insensitively against a game's rating.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>Platform / category names hidden from the BigBox filter page WHEN LOCKED.</summary>
    public List<string> HiddenPlatformsBigBoxOn { get; set; } = new();

    /// <summary>Platform / category names hidden from the BigBox filter page WHEN UNLOCKED.</summary>
    public List<string> HiddenPlatformsBigBoxOff { get; set; } = new();

    /// <summary>Config-format version that last wrote parental-lists.json ("0.0.0" = pre-versioning).</summary>
    public string ConfigVersion { get; set; } = "0.0.0";

    /// <summary>The config-level "configured" flag (alias of <see cref="Enabled"/>, kept for callers).
    /// The module master switch LbModule.Parental gates on top of this in ParentalFilter.</summary>
    public bool AnyScopeEnabled => Enabled;

    // ── Singleton lifecycle ─────────────────────────────────────────────────

    private static ParentalConfig? _instance;
    public static ParentalConfig Instance => _instance ??= Load();

    /// <summary>Force a reload from disk on next access (after a Save from the config panel).</summary>
    public static void Invalidate() => _instance = null;

    private static string ListsPath => LiteBoxPaths.File("parental-lists.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>On-disk shape of parental-lists.json — only the lists persist here.</summary>
    private sealed class ListStore
    {
        public string ConfigVersion { get; set; } = "0.0.0";
        public List<string> Rules { get; set; } = new();
        public List<string> HiddenPlatformsBigBoxOn { get; set; } = new();
        public List<string> HiddenPlatformsBigBoxOff { get; set; } = new();
    }

    private static ParentalConfig Load()
    {
        var c = new ParentalConfig();
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            // Single switch now; migrate from the retired per-app scopes (either on → enabled).
            c.Enabled = cfg.GetSecBool(Section, "Enabled",
                cfg.GetSecBool(Section, "LaunchBoxEnabled", false) || cfg.GetSecBool(Section, "BigBoxEnabled", false));
            c.ForceWebHideAll           = cfg.GetSecBool(Section, "ForceWebHideAll", false);
            c.AllowLockedModifyRatings  = cfg.GetSecBool(Section, "AllowLockedModifyRatings", false);
            c.AllowLockedModifyFavorites= cfg.GetSecBool(Section, "AllowLockedModifyFavorites", false);
            c.BlockInstallWhenLocked    = cfg.GetSecBool(Section, "BlockInstallWhenLocked", false);
            c.Mode = string.Equals(cfg.GetSec(Section, "Mode"), "Blacklist", StringComparison.OrdinalIgnoreCase)
                ? ParentalMode.Blacklist : ParentalMode.Whitelist;
            c.BigBoxWriteMode = string.Equals(cfg.GetSec(Section, "BigBoxWriteMode"), "Merge", StringComparison.OrdinalIgnoreCase)
                ? ParentalWriteMode.Merge : ParentalWriteMode.Block;
            c.HotKey = GetSecInt(cfg, "HotKey", 0);
        }
        catch (Exception ex) { Log("load scalars failed: " + ex.Message); }

        try
        {
            if (File.Exists(ListsPath))
            {
                var store = JsonSerializer.Deserialize<ListStore>(File.ReadAllText(ListsPath), JsonOpts);
                if (store != null)
                {
                    c.ConfigVersion = store.ConfigVersion ?? "0.0.0";
                    c.Rules = Clean(store.Rules);
                    c.HiddenPlatformsBigBoxOn = Clean(store.HiddenPlatformsBigBoxOn);
                    c.HiddenPlatformsBigBoxOff = Clean(store.HiddenPlatformsBigBoxOff);
                }
            }
        }
        catch (Exception ex) { Log("load lists failed: " + ex.Message); }

        return c;
    }

    /// <summary>Persist the scalars to LiteBox.ini [Parental] and the lists to parental-lists.json.
    /// The PIN is written separately through Host/Data/BigBoxPin (not here).</summary>
    public void Save()
    {
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            cfg.SetSec(Section, "Enabled",                    B(Enabled));
            cfg.SetSec(Section, "ForceWebHideAll",            B(ForceWebHideAll));
            cfg.SetSec(Section, "AllowLockedModifyRatings",   B(AllowLockedModifyRatings));
            cfg.SetSec(Section, "AllowLockedModifyFavorites", B(AllowLockedModifyFavorites));
            cfg.SetSec(Section, "BlockInstallWhenLocked",     B(BlockInstallWhenLocked));
            cfg.SetSec(Section, "Mode", Mode == ParentalMode.Blacklist ? "Blacklist" : "Whitelist");
            cfg.SetSec(Section, "BigBoxWriteMode", BigBoxWriteMode == ParentalWriteMode.Merge ? "Merge" : "Block");
            cfg.SetSec(Section, "HotKey", HotKey.ToString(CultureInfo.InvariantCulture));
            cfg.Save();
        }
        catch (Exception ex) { Log("save scalars failed: " + ex.Message); }

        try
        {
            var store = new ListStore
            {
                // Stamp the version WRITING the file (echoing the old value made the field inert —
                // every file stayed "0.0.0" and carried no information). This file holds USER data:
                // no reset gate — the stamp is provenance (format breaks = fresh install).
                ConfigVersion = Data.ConfigVersioning.Stamp(),
                Rules = Clean(Rules),
                HiddenPlatformsBigBoxOn = Clean(HiddenPlatformsBigBoxOn),
                HiddenPlatformsBigBoxOff = Clean(HiddenPlatformsBigBoxOff),
            };
            File.WriteAllText(ListsPath, JsonSerializer.Serialize(store, JsonOpts));
            Log("saved lists to " + ListsPath);
        }
        catch (Exception ex) { Log("save lists failed: " + ex.Message); }

        // Regenerate the flat file the native ASI reads (it can't read this ini / json / the Options DB).
        // `this` is the singleton the panel just mutated, so the export reads the new values in memory.
        try { ParentalNativeExport.Write(); } catch { }
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

    private static string B(bool v) => v ? "true" : "false";

    private static int GetSecInt(LiteBoxConfig cfg, string key, int def)
        => int.TryParse(cfg.GetSec(Section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def;

    private static void Log(string msg) => LbLog.Info("parental", "config: " + msg);
}
