// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — native LiteBox config model. Slice R1.
// ─────────────────────────────────────────────────────────────────────────────
//
// Native clean-room port of the ExtendDB plugin's ArchiveMgsConfig. The MODEL is
// reproduced faithfully (same field names + semantics) so the later slices (R2/R3)
// can call Resolve(...) and get the plugin's exact cascade + defaults. Only the
// STORAGE backend changes:
//
//   • The bare on/off flag  → LbModules.On(LbModule.Rom) (the module master switch;
//     no separate key).
//   • The scalar GLOBALS (cache path + band + trigger/metadata extension lists) →
//     LiteBox.ini [Rom] via LiteBoxConfig.GetSec/SetSec.
//   • The per-(platform, emulator) PROFILES (GlobalDefault + Priorities[], each an
//     ArchivePriorityRow with nested TagWeight / ConvertRule lists) → a JSON sidecar
//     LiteBoxPaths.File("rom-profiles.json") (System.Text.Json, enums by name).
//
// A "profile" (ArchivePriorityRow, name kept for parity) is a whole bundle resolved
// on (Platform, Emulator) with the cascade
//     exact (Platform, Emulator) → (Platform, "All") → GlobalDefault.
// No partial field merge — a profile is resolved whole. AdvancedMode is force-true
// (the Simple/Advanced toggle was removed in the plugin) so the cascade always applies.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LbApiHost.Host;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rom;

/// <summary>Operation performed on a launched archive / disc image.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ArchiveMode
{
    SmartExtract, // extract the picked file (+ optional companions / other roms)
    Copy,         // copy matching-extension files to the cache, no extraction
    Convert,      // convert disc images per the Conversions table
    DoNothing,    // passthrough — launch the file as-is, no cache
}

/// <summary>How the extracted file is named in the cache.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum OutputNameMode
{
    Original, // keep the entry's own name (cacheable, shared)
    Title,    // rename to the LB game title → forces extraction to \tmp
}

/// <summary>Optional grouping sub-dir inserted AFTER the mandatory signature folder.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum CacheSubDirScheme { None, Title, Platform, Emulator, PlatformCode }

/// <summary>One row of the convert table: an input format mapped to a target output
/// format. Empty / "ignore" Output = leave untouched.</summary>
internal sealed class ConvertRule
{
    public string Input { get; set; } = "";   // e.g. "chd", "rvz", "iso"
    public string Output { get; set; } = "";  // e.g. "cue/bin", "iso"; "" = ignore
}

/// <summary>One weighted tag in the priority scorer. Score of a candidate = sum of the
/// weights of every tag it matches; highest wins.</summary>
internal sealed class TagWeight
{
    public string Tag { get; set; } = "";  // literal tag or wildcard, e.g. "(Europe)" or "*[!]*"
    public int Weight { get; set; }          // signed; negative = avoid
}

/// <summary>A full per-(platform, emulator) profile. Name kept as
/// <c>ArchivePriorityRow</c> for parity with the plugin (callers that read
/// <see cref="CacheSubDir"/> / <see cref="Priority"/>).</summary>
internal sealed class ArchivePriorityRow
{
    // ── Cascade key ───────────────────────────────────────────────────
    /// <summary>"All" = applies to every emulator of the platform (platform-default level).</summary>
    public string Emulator { get; set; } = "All";
    public string Platform { get; set; } = "";

    // ── Legacy fields (still read by some callers; kept populated) ─────
    /// <summary>Legacy free-text sub-dir. Superseded by <see cref="SubDirScheme"/> but
    /// retained so old JSON + the existing extractor path keep working.</summary>
    public string CacheSubDir { get; set; } = "";
    /// <summary>Legacy CSV wildcard priority (first-match). Superseded by
    /// <see cref="TagWeights"/>; still consulted as a fallback when no weights are defined.</summary>
    public string Priority { get; set; } = "";

    // ── Operation mode ────────────────────────────────────────────────
    public ArchiveMode Mode { get; set; } = ArchiveMode.SmartExtract;

    // smartextract
    public bool ExtractCompanions { get; set; } = true;
    public bool ExtractOtherRoms { get; set; } = false;
    /// <summary>Flatten the extraction. true (legacy): 7z 'e' drops the picked ROM + its companions into
    /// the cache dir by basename. false (DEFAULT): 7z 'x' extracts the SAME selective set (picked ROM +
    /// companions) but KEEPS each entry's in-archive sub-path — so a launcher / complete game keeps its
    /// data folders (which come in as companions, being non-ROM). Other ROMs stay excluded in BOTH
    /// modes; only the on-disk layout differs (flat basenames vs preserved tree).</summary>
    public bool FlattenExtraction { get; set; } = false;

    /// <summary>Extensions that are ROM candidates yet can ALSO act as companions (e.g. "bin" — a raw
    /// dump is launchable on its own, but a .cue needs its .bin). When the launched file's extension is
    /// NOT in this list, files with a listed extension are pulled as companions (so cue/bin works without
    /// "extract other ROMs"). CSV; default "bin".</summary>
    public string CompanionExtensions { get; set; } = "bin";

    /// <summary><see cref="CompanionExtensions"/> as a lowercase, dot-stripped set.</summary>
    public HashSet<string> CompanionExtensionSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in RomConfig.SplitCsv(CompanionExtensions ?? ""))
        {
            var x = e.TrimStart('.').Trim().ToLowerInvariant();
            if (x.Length > 0) set.Add(x);
        }
        return set;
    }

    /// <summary>SmartExtract follow-up: after extracting the picked entry, if its format has a rule in
    /// <see cref="Conversions"/>, convert it (e.g. an archived cue/bin → chd). Forces companion extraction
    /// so the disc image's sibling files are present for the conversion.</summary>
    public bool ConvertAfterExtract { get; set; } = false;
    public OutputNameMode OutputName { get; set; } = OutputNameMode.Original;

    // copy
    public string CopyExtensions { get; set; } = "";

    // convert
    public List<ConvertRule> Conversions { get; set; } = new();

    // ── Cache placement ───────────────────────────────────────────────
    public CacheSubDirScheme SubDirScheme { get; set; } = CacheSubDirScheme.None;

    // ── File rules (empty → fall back to the global lists) ────────────
    public string RomExtensions { get; set; } = "";
    public string IgnoredExtensions { get; set; } = "";

    // ── Tag priority (weighted, signed) ───────────────────────────────
    public List<TagWeight> TagWeights { get; set; } = new();

    /// <summary>Flat score bonus added to an archive entry whose ROM has RetroAchievements (a matched
    /// RetroAchievementsId in archive_entry) when picking which entry to auto-extract / launch. Default
    /// +10000 — large enough to outrank tag weights, so the version that actually has achievements wins.
    /// 0 disables it.</summary>
    public int RetroAchievementsBonus { get; set; } = 10000;

    // ── Ram disk (ImDisk) ─────────────────────────────────────────────
    public bool RamDiskEnabled { get; set; } = false;
    public int RamDiskMaxMb { get; set; } = 2000;

    // ── Texture pack (single-file model) ──────────────────────────────
    public bool TextureEnabled { get; set; } = false;
    public string TextureExtensions { get; set; } = "";       // e.g. "htc, hts"
    public string TextureExtractPath { get; set; } = "";      // tokens: {EmuDir} {GameId} {GameTitle}

    // ── M3U input support (intercept + rewrite; no generation) ────────
    public bool M3uInput { get; set; } = true;

    /// <summary>Deep-ish copy used to seed one profile from another ("Copy settings from"). Keeps this
    /// row's Platform/Emulator.</summary>
    public void CopyFrom(ArchivePriorityRow? src)
    {
        if (src == null) return;
        CacheSubDir = src.CacheSubDir; Priority = src.Priority;
        Mode = src.Mode;
        ExtractCompanions = src.ExtractCompanions; ExtractOtherRoms = src.ExtractOtherRoms;
        FlattenExtraction = src.FlattenExtraction;
        CompanionExtensions = src.CompanionExtensions;
        ConvertAfterExtract = src.ConvertAfterExtract;
        OutputName = src.OutputName;
        CopyExtensions = src.CopyExtensions;
        Conversions = CloneConversions(src.Conversions);
        SubDirScheme = src.SubDirScheme;
        RomExtensions = src.RomExtensions; IgnoredExtensions = src.IgnoredExtensions;
        TagWeights = CloneWeights(src.TagWeights);
        RamDiskEnabled = src.RamDiskEnabled; RamDiskMaxMb = src.RamDiskMaxMb;
        TextureEnabled = src.TextureEnabled; TextureExtensions = src.TextureExtensions; TextureExtractPath = src.TextureExtractPath;
        M3uInput = src.M3uInput;
    }

    private static List<ConvertRule> CloneConversions(List<ConvertRule>? src)
    {
        var list = new List<ConvertRule>();
        if (src != null) foreach (var c in src) list.Add(new ConvertRule { Input = c.Input, Output = c.Output });
        return list;
    }

    private static List<TagWeight> CloneWeights(List<TagWeight>? src)
    {
        var list = new List<TagWeight>();
        if (src != null) foreach (var w in src) list.Add(new TagWeight { Tag = w.Tag, Weight = w.Weight });
        return list;
    }
}

/// <summary>Singleton holding the ROM-extractor config. Scalar globals live in
/// LiteBox.ini [Rom]; the per-(platform, emulator) profiles live in a JSON sidecar
/// (<c>rom-profiles.json</c>). The bare on/off flag is <see cref="LbModule.Rom"/>.</summary>
internal sealed class RomConfig
{
    internal const string Section = "Rom";

    private static RomConfig? _instance;
    public static RomConfig Instance => _instance ??= Load();

    /// <summary>Force a reload from disk on next access (after a Save from the config panel).</summary>
    public static void Invalidate() => _instance = null;

    /// <summary>Wipe every setting back to shipped defaults: drops all customized per-(platform, emulator)
    /// profiles, restores the Default profile (extensions + tag priority), the cache band, and the global
    /// extension lists + cache path. Persists immediately and becomes the live instance.</summary>
    public static void ResetToDefaults()
    {
        var fresh = new RomConfig();   // all C# field defaults
        fresh.EnsureDefaults();        // seed the Default profile
        fresh.Save();                  // overwrite [Rom] + rom-profiles.json
        _instance = fresh;
    }

    // ── Persisted globals (LiteBox.ini [Rom]) ──────────────────────────────

    /// <summary>Cache root. Default = <c>Core\litebox\romcache</c>.</summary>
    public string CachePath { get; set; } = DefaultCachePath();

    /// <summary>Global cache size band (MB). Extractions whose unpacked size is outside
    /// [CacheMinMb, CacheMaxMb] go to <c>\tmp</c> each launch instead of the persistent LRU cache.</summary>
    public int CacheMaxGb { get; set; } = 50;   // total LRU budget
    public int CacheMinMb { get; set; } = 100;  // below → \tmp
    public int CacheMaxMb { get; set; } = 8000; // at/above → \tmp

    /// <summary>Default metadata extensions (don't carry game data). A profile's IgnoredExtensions
    /// overrides this when non-empty.</summary>
    public string MetadataExtensions { get; set; } = "nfo, txt, dat, xml, json, htc, hts";

    /// <summary>Extensions that trigger the pipeline when found in the launch arguments.</summary>
    public string ArchiveExtensions { get; set; } = "zip, 7z, rar, tar, gz, bz2, xz";

    /// <summary>Disc-image extensions that trigger the convert/do-nothing path (handled even though they
    /// aren't 7z archives).</summary>
    public string DiscImageExtensions { get; set; } = "chd, rvz, wia, gcz, iso, cue, gdi, cso, zso";

    // ── Persisted profiles (rom-profiles.json) ─────────────────────────────

    /// <summary>Config-format version that last wrote rom-profiles.json ("0.0.0" = pre-versioning).</summary>
    public string ConfigVersion { get; set; } = "0.0.0";

    /// <summary>Per-(platform, emulator) profiles. Resolution cascade:
    /// exact → (platform, "All") → <see cref="GlobalDefault"/>.</summary>
    public List<ArchivePriorityRow> Priorities { get; set; } = new();

    /// <summary>Bottom of the cascade — also what a global-only config applies everywhere. Never null.</summary>
    public ArchivePriorityRow GlobalDefault { get; set; } = new() { Platform = "All", Emulator = "All" };

    /// <summary>Always true — the Simple/Advanced toggle was removed; the cascade always applies (falling
    /// back to <see cref="GlobalDefault"/> when no platform profile matches). Kept as a field so the
    /// ported <see cref="Resolve(string,string)"/> reads identically to the plugin.</summary>
    public bool AdvancedMode { get; set; } = true;

    // ── Default-profile seed values ────────────────────────────────────────
    // Shipped defaults for the GlobalDefault profile's File-rules + tag priority. Seeded into GlobalDefault
    // on first run / when its fields are still empty (see EnsureDefaults). A user's own values are never
    // clobbered.
    public const string DefaultRomExtensions =
        "gb, gbc, gba, agb, srl, mb, nes, unf, fds, smc, sfc, fig, swc, bs, n64, z64, v64, ndd, dmg, sgb, cgb, " +
        "nds, dsi, ids, 3ds, cci, cxi, cia, 3dsx, vb, vboy, gcm, iso, gcz, rvz, ciso, tgc, wia, wbfs, wad, wud, " +
        "wux, wua, rpx, md, smd, gen, bin, sms, gg, sg, 32x, gdi, cdi, img, nrg, pbp, ecm, cso, zso, chd, pkg, " +
        "vpk, a26, a78, lnx, ngp, ngc, pce, sgx, ws, wsc, col, rom, dsk, d64, t64, prg, crt, tap, adf, ipf, hdf";
    public const string DefaultIgnoredExtensions =
        "nfo, txt, dat, xml, json, htc, hts";

    // Quality / revision / translation baseline + the static region seeds ([USA]/[Europe]), visible + editable
    // in the Tag-priority grid. (These are the pick weights; the grid shows exactly what scoring uses.)
    private static List<TagWeight> DefaultTagWeights() => new()
    {
        new TagWeight { Tag = "[!]",      Weight = 1000 },
        new TagWeight { Tag = "[!!]",     Weight = 1001 },
        new TagWeight { Tag = "Rev 1",    Weight = 2 },
        new TagWeight { Tag = "Rev 2",    Weight = 4 },
        new TagWeight { Tag = "Rev 3",    Weight = 6 },
        new TagWeight { Tag = "Rev 4",    Weight = 8 },
        new TagWeight { Tag = "Rev 5",    Weight = 10 },
        new TagWeight { Tag = "Proto",    Weight = -10 },
        new TagWeight { Tag = "[b]",      Weight = -500 },
        new TagWeight { Tag = "T+En",     Weight = 100 },
        new TagWeight { Tag = "T-En",     Weight = 50 },
        new TagWeight { Tag = "T.Eng",    Weight = 101 },
        new TagWeight { Tag = "[USA]",    Weight = 200 },
        new TagWeight { Tag = "(USA)",    Weight = 200 },
        new TagWeight { Tag = "[Europe]", Weight = 150 },
        new TagWeight { Tag = "(Europe)", Weight = 150 },
    };

    // The region-STRIPPED default a short-lived build seeded (region was briefly made dynamic). A default that
    // exactly matches it gets the static region rows back on load (EnsureDefaults) — the user wants them visible.
    private static List<TagWeight> RegionlessDefaultTagWeights()
        => DefaultTagWeights().Where(w => w.Tag is not ("[USA]" or "(USA)" or "[Europe]" or "(Europe)")).ToList();

    private static bool SameWeights(List<TagWeight>? a, List<TagWeight> b)
    {
        if (a == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Tag, b[i].Tag, StringComparison.Ordinal) || a[i].Weight != b[i].Weight) return false;
        return true;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private static string DefaultCachePath()
    {
        try { return LiteBoxPaths.CacheDir("romcache"); } catch { return "romcache"; }
    }

    private static string ProfilesPath => LiteBoxPaths.File("rom-profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The on-disk shape of rom-profiles.json — only the per-profile model persists here; the
    /// scalar globals live in [Rom].</summary>
    private sealed class ProfileStore
    {
        public string ConfigVersion { get; set; } = "0.0.0";
        public ArchivePriorityRow GlobalDefault { get; set; } = new() { Platform = "All", Emulator = "All" };
        public List<ArchivePriorityRow> Priorities { get; set; } = new();
    }

    private static RomConfig Load()
    {
        var c = new RomConfig();
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            c.CachePath          = FirstNonEmpty(cfg.GetSec(Section, "CachePath"), DefaultCachePath());
            c.CacheMaxGb         = GetSecInt(cfg, "CacheMaxGb", c.CacheMaxGb);
            c.CacheMinMb         = GetSecInt(cfg, "CacheMinMb", c.CacheMinMb);
            c.CacheMaxMb         = GetSecInt(cfg, "CacheMaxMb", c.CacheMaxMb);
            c.MetadataExtensions = FirstNonEmpty(cfg.GetSec(Section, "MetadataExtensions"), c.MetadataExtensions);
            c.ArchiveExtensions  = FirstNonEmpty(cfg.GetSec(Section, "ArchiveExtensions"), c.ArchiveExtensions);
            c.DiscImageExtensions= FirstNonEmpty(cfg.GetSec(Section, "DiscImageExtensions"), c.DiscImageExtensions);
        }
        catch (Exception ex) { Log("Load globals failed: " + ex.Message); }

        try
        {
            if (File.Exists(ProfilesPath))
            {
                var store = JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(ProfilesPath), JsonOpts);
                if (store != null)
                {
                    c.ConfigVersion = store.ConfigVersion ?? "0.0.0";
                    c.GlobalDefault = store.GlobalDefault ?? new ArchivePriorityRow { Platform = "All", Emulator = "All" };
                    c.Priorities = store.Priorities ?? new();
                }
            }
        }
        catch (Exception ex) { Log("Load profiles failed: " + ex.Message); }

        c.EnsureDefaults();
        return c;
    }

    private void EnsureDefaults()
    {
        Priorities ??= new();
        GlobalDefault ??= new ArchivePriorityRow { Platform = "All", Emulator = "All" };
        // Seed the default profile's File-rules + tag priority when empty, without clobbering user values.
        if (string.IsNullOrWhiteSpace(GlobalDefault.RomExtensions))
            GlobalDefault.RomExtensions = DefaultRomExtensions;
        if (string.IsNullOrWhiteSpace(GlobalDefault.IgnoredExtensions))
            GlobalDefault.IgnoredExtensions = DefaultIgnoredExtensions;
        if (GlobalDefault.TagWeights == null || GlobalDefault.TagWeights.Count == 0)
            GlobalDefault.TagWeights = DefaultTagWeights();
        // Re-add the static region rows ([USA]/[Europe]) to a default that a short-lived region-dynamic build
        // had stripped — only when it EXACTLY matches that stripped default (a customised default is untouched).
        else if (SameWeights(GlobalDefault.TagWeights, RegionlessDefaultTagWeights()))
            GlobalDefault.TagWeights = DefaultTagWeights();
        // Simple mode removed — the engine is always Advanced (the cascade falls back to GlobalDefault
        // when no platform profile matches).
        AdvancedMode = true;
        if (string.IsNullOrWhiteSpace(CachePath)) CachePath = DefaultCachePath();
        if (CacheMaxGb <= 0) CacheMaxGb = 50;
        if (CacheMaxMb <= 0) CacheMaxMb = 8000;
    }

    /// <summary>Persist the scalar globals to LiteBox.ini [Rom] and the profiles to rom-profiles.json.</summary>
    public void Save()
    {
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            cfg.SetSec(Section, "CachePath", CachePath ?? "");
            cfg.SetSec(Section, "CacheMaxGb", CacheMaxGb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cfg.SetSec(Section, "CacheMinMb", CacheMinMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cfg.SetSec(Section, "CacheMaxMb", CacheMaxMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cfg.SetSec(Section, "MetadataExtensions", MetadataExtensions ?? "");
            cfg.SetSec(Section, "ArchiveExtensions", ArchiveExtensions ?? "");
            cfg.SetSec(Section, "DiscImageExtensions", DiscImageExtensions ?? "");
            cfg.Save();
        }
        catch (Exception ex) { Log("Save globals failed: " + ex.Message); }

        try
        {
            var store = new ProfileStore
            {
                // Stamp the writing version (the previous echo left every file at "0.0.0", i.e. useless).
                // Profiles are user config with nested structures — the most likely to need a migration.
                ConfigVersion = Data.ConfigVersioning.Stamp(),
                GlobalDefault = GlobalDefault,
                Priorities = Priorities ?? new(),
            };
            File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(store, JsonOpts));
            Log("Saved profiles to " + ProfilesPath);
        }
        catch (Exception ex) { Log("Save profiles failed: " + ex.Message); }
    }

    // ── Lookup ─────────────────────────────────────────────────────────────

    /// <summary>Back-compat single-arg lookup — resolves on platform with the emulator dimension treated
    /// as "All".</summary>
    public ArchivePriorityRow Resolve(string? platform) => Resolve(platform, null);

    /// <summary>Cascade resolution: exact (platform, emulator) → (platform, "All") →
    /// <see cref="GlobalDefault"/>. Never null.</summary>
    public ArchivePriorityRow Resolve(string? platform, string? emulator)
    {
        platform ??= "";
        emulator ??= "";

        // Simple mode: ignore the per-(platform, emulator) overrides and resolve every launch to the
        // global default profile. (AdvancedMode is force-true; kept for verbatim parity.)
        if (!AdvancedMode)
            return GlobalDefault ?? new ArchivePriorityRow { Platform = platform, Emulator = emulator };

        // 1) exact (platform, emulator)
        if (!string.IsNullOrEmpty(emulator))
            foreach (var p in Priorities)
                if (Eq(p.Platform, platform) && Eq(p.Emulator, emulator))
                    return p;

        // 2) platform-default (platform, "All")
        foreach (var p in Priorities)
            if (Eq(p.Platform, platform) && (Eq(p.Emulator, "All") || string.IsNullOrEmpty(p.Emulator)))
                return p;

        // 3) global default
        return GlobalDefault ?? new ArchivePriorityRow { Platform = platform, Emulator = emulator };
    }

    private static bool Eq(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    public static string[] SplitCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return Array.Empty<string>();
        var parts = csv.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result.ToArray();
    }

    private static string FirstNonEmpty(string? v, string fallback)
        => string.IsNullOrWhiteSpace(v) ? fallback : v!.Trim();

    private static int GetSecInt(LiteBoxConfig cfg, string key, int def)
        => int.TryParse(cfg.GetSec(Section, key), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : def;

    private static void Log(string msg) => LbLog.Info("rom", "config: " + msg);
}
