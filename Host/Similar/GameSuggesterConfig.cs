// Native LiteBox store for the "Similar Games" rule engine configuration — LiteBox's own copy of
// LaunchBox's "Related Games" rule sets (Similar / Recommended / Possible Ports).
//
// The design mirrors what LaunchBox itself persists: LB serializes three rule sets as entity-encoded
// XML strings inside LB\Data\Settings.xml — <SimilarGamesXmlString>, <RecommendedGamesXmlString> and
// <PossiblePortsXmlString> (schema RE'd below). LiteBox reads those through LbSettingsStore.
//
// The DOUBLE-REGISTRATION model (the whole point of this file):
//   LiteBox keeps its OWN copy of every category's rules that, BY DEFAULT, simply MIRRORS LaunchBox's
//   config read-only. A per-category "use LaunchBox's config" switch lets a category DIVERGE — uncheck
//   it and LiteBox scores from an INDEPENDENT copy stored in Core\litebox\game-suggester.json, editable
//   without touching (or being touched by) LaunchBox. This lets free-LB users author rules they don't
//   get in LB's premium editor, and lets everyone tweak LiteBox's extra scoring layer.
//
// Resolution chain (SuggesterResolver.Resolve):
//   1. mirror = true  (default) → LaunchBox's Settings.xml config when present & valid, else the
//                                 built-in factory default (SuggesterDefaults).
//   2. mirror = false           → the LiteBox override (game-suggester.json) when present, else default.
//
// Nothing here evaluates candidates — this is the CONFIG surface + persistence only. A future
// SuggesterEngine (not yet ported to LiteBox) consumes the resolved SuggesterConfig + the global
// BonusSettings.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Similar;

// ── Enums (LB's rule vocabulary) ────────────────────────────────────────────────────────────────

/// <summary>Comparison operators understood by the suggester. Names mirror the strings LB writes to
/// its &lt;ComparisonTypeKey&gt; element so mirrored configs round-trip by name.</summary>
internal enum ComparisonType
{
    Unknown = 0,
    EqualTo, NotEqualTo, Contains, NotContains,
    IsEmpty, IsNotEmpty, AtLeastOneOf, NoneOf,
    IsSimilarTo, IsNotSimilarTo, ContainsAnyValue, ContainsNoValue,
    StartsWith, StartsWithNone, GreaterThan, LessThan,
    IsAmazon, IsSteam, IsGog, IsEpic, IsEa, IsUbisoft, IsMicrosoft,
}

/// <summary>Which subset of candidate games a criterion applies to.</summary>
internal enum FilterScope
{
    AllGames = 0,
    LocalGamesOnly,
    DatabaseGamesOnly,
}

/// <summary>The three LB-defined suggester categories.</summary>
internal enum SuggesterCategory
{
    SimilarGames,
    RecommendedGames,
    PossiblePorts,
}

/// <summary>Where a resolved config came from (for the UI's status lines / diagnostics).</summary>
internal enum ConfigSource
{
    HardcodedDefault,   // built-in factory preset (SuggesterDefaults)
    SettingsXml,        // mirrored from LaunchBox's Settings.xml
    ParseError,         // LaunchBox had a value but it failed to parse
    LiteBoxOverride,    // LiteBox's own edited rules (game-suggester.json)
}

// ── DTOs ────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One rule. <see cref="Weight"/> is null for a HARD FILTER (candidate must satisfy it to be
/// included) and a positive integer for a SCORING contribution. <see cref="ComparisonValue"/> is empty
/// when <see cref="UseGameValue"/> is true (compare against the viewed game's same field instead).</summary>
internal sealed class CriteriaRecord
{
    public ComparisonType ComparisonTypeKey { get; set; } = ComparisonType.Unknown;
    public string ComparisonValue { get; set; } = "";
    public string FieldKey { get; set; } = "";
    public FilterScope FilterType { get; set; } = FilterScope.AllGames;
    public bool UseGameValue { get; set; }
    public int? Weight { get; set; }

    [JsonIgnore]
    public bool IsHardFilter => !Weight.HasValue;

    public CriteriaRecord Clone() => new()
    {
        ComparisonTypeKey = ComparisonTypeKey,
        ComparisonValue = ComparisonValue,
        FieldKey = FieldKey,
        FilterType = FilterType,
        UseGameValue = UseGameValue,
        Weight = Weight,
    };
}

/// <summary>A fully resolved rule set for one category, tagged with its provenance.</summary>
internal sealed class SuggesterConfig
{
    public bool AllowDbGames { get; set; }
    public List<CriteriaRecord> Criteria { get; set; } = new();
    public int MinimumScore { get; set; }
    public SuggesterCategory Category { get; set; }
    public ConfigSource Source { get; set; } = ConfigSource.HardcodedDefault;
}

// ── Built-in factory defaults ─────────────────────────────────────────────────────────────────────

/// <summary>The factory rule sets, built in code (no serialized-XML blob to copy). These match the
/// defaults LaunchBox applies out of the box and act as the fallback whenever LaunchBox has no valid
/// config for a category and LiteBox has no override.</summary>
internal static class SuggesterDefaults
{
    private static CriteriaRecord Filter(ComparisonType cmp, string field, FilterScope scope, bool useGame, string custom = "")
        => new() { ComparisonTypeKey = cmp, FieldKey = field, FilterType = scope, UseGameValue = useGame, ComparisonValue = custom, Weight = null };

    private static CriteriaRecord Score(ComparisonType cmp, string field, FilterScope scope, bool useGame, int weight, string custom = "")
        => new() { ComparisonTypeKey = cmp, FieldKey = field, FilterType = scope, UseGameValue = useGame, ComparisonValue = custom, Weight = weight };

    public static SuggesterConfig Default(SuggesterCategory cat)
    {
        var cfg = new SuggesterConfig { Category = cat, Source = ConfigSource.HardcodedDefault, AllowDbGames = true, MinimumScore = 0 };
        switch (cat)
        {
            case SuggesterCategory.SimilarGames:
                cfg.Criteria.AddRange(new[]
                {
                    Filter(ComparisonType.IsNotEmpty, "Notes",         FilterScope.DatabaseGamesOnly, false),
                    Filter(ComparisonType.EqualTo,    "ReleaseType",   FilterScope.DatabaseGamesOnly, false, "Released"),
                    Filter(ComparisonType.NotEqualTo, "Title",         FilterScope.AllGames,          true),
                    Score (ComparisonType.IsSimilarTo,"Title",         FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.IsSimilarTo,"AlternateName", FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.IsSimilarTo,"Series",        FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.EqualTo,    "Genre",         FilterScope.AllGames,          true, 3),
                    Score (ComparisonType.EqualTo,    "PlayMode",      FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.EqualTo,    "MaxPlayers",    FilterScope.AllGames,          true, 1),
                    Score (ComparisonType.EqualTo,    "Platform",      FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.EqualTo,    "Rating",        FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.EqualTo,    "Developer",     FilterScope.AllGames,          true, 1),
                    Score (ComparisonType.EqualTo,    "Publisher",     FilterScope.AllGames,          true, 1),
                });
                break;

            case SuggesterCategory.RecommendedGames:
                cfg.Criteria.AddRange(new[]
                {
                    Filter(ComparisonType.EqualTo,       "ReleaseType", FilterScope.DatabaseGamesOnly, false, "Released"),
                    Filter(ComparisonType.NotEqualTo,    "Title",       FilterScope.AllGames,          true),
                    Filter(ComparisonType.GreaterThan,   "StarRating",  FilterScope.AllGames,          false, "3.5"),
                    Filter(ComparisonType.IsNotSimilarTo,"Series",      FilterScope.LocalGamesOnly,    true),
                    Score (ComparisonType.EqualTo,       "Genre",       FilterScope.AllGames,          true, 3),
                    Score (ComparisonType.EqualTo,       "PlayMode",    FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.EqualTo,       "MaxPlayers",  FilterScope.AllGames,          true, 1),
                    Score (ComparisonType.EqualTo,       "Platform",    FilterScope.AllGames,          true, 1),
                    Score (ComparisonType.GreaterThan,   "StarRating",  FilterScope.AllGames,          false, 3, "4.1"),
                    Score (ComparisonType.EqualTo,       "Rating",      FilterScope.AllGames,          true, 2),
                    Score (ComparisonType.EqualTo,       "Developer",   FilterScope.AllGames,          true, 1),
                    Score (ComparisonType.EqualTo,       "Publisher",   FilterScope.AllGames,          true, 1),
                });
                break;

            case SuggesterCategory.PossiblePorts:
                cfg.Criteria.AddRange(new[]
                {
                    Filter(ComparisonType.EqualTo,    "ReleaseType", FilterScope.DatabaseGamesOnly, false, "Released"),
                    Filter(ComparisonType.NotEqualTo, "Platform",    FilterScope.AllGames,          true),
                    Filter(ComparisonType.EqualTo,    "Title",       FilterScope.AllGames,          true),
                });
                break;
        }
        return cfg;
    }
}

// ── LaunchBox mirror (Settings.xml reader/parser) ──────────────────────────────────────────────────

/// <summary>Reads LaunchBox's serialized suggester rule sets out of Settings.xml (via LbSettingsStore)
/// and turns them into <see cref="SuggesterConfig"/>. This is the "mirror" side of the double model.</summary>
internal static class LaunchBoxSuggester
{
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The Settings.xml element name LaunchBox stores each category under.</summary>
    public static string ElementName(SuggesterCategory cat) => cat switch
    {
        SuggesterCategory.SimilarGames     => "SimilarGamesXmlString",
        SuggesterCategory.RecommendedGames => "RecommendedGamesXmlString",
        SuggesterCategory.PossiblePorts    => "PossiblePortsXmlString",
        _ => "SimilarGamesXmlString",
    };

    /// <summary>The live LB-settings store (lazy singleton on the host data manager), or null when the
    /// host isn't a LiteBox XML data manager (e.g. running headless).</summary>
    private static LbSettingsStore? Settings
    {
        get { try { return (PluginHelper.DataManager as HostDataManagerXml)?.LbSettings; } catch { return null; } }
    }

    /// <summary>LaunchBox's config for a category, or null when LB has none usable (element absent,
    /// unreadable, parse error, or zero criteria). Drives the read-only mirror and greys "Copy from LB".</summary>
    public static SuggesterConfig? GetOrNull(SuggesterCategory cat)
    {
        string xml;
        try { xml = Settings?.Get(ElementName(cat)) ?? ""; }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(xml)) return null;

        var cfg = Parse(xml, cat, ConfigSource.SettingsXml);
        return (cfg.Source != ConfigSource.ParseError && cfg.Criteria.Count > 0) ? cfg : null;
    }

    /// <summary>Parses one GameSuggesterSaveData XML string. On malformed XML returns a config tagged
    /// <see cref="ConfigSource.ParseError"/> with no criteria (caller falls back to a default).</summary>
    public static SuggesterConfig Parse(string xml, SuggesterCategory cat, ConfigSource source)
    {
        var cfg = new SuggesterConfig { Category = cat, Source = source };
        if (string.IsNullOrWhiteSpace(xml)) return cfg;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { cfg.Source = ConfigSource.ParseError; return cfg; }

        var root = doc.Root;
        if (root == null) return cfg;

        cfg.AllowDbGames = ParseBool(root.Element("AllowDbGames")?.Value, false);
        cfg.MinimumScore = ParseInt(root.Element("MinimumScore")?.Value, 0);

        var criteriaEl = root.Element("Criteria");
        if (criteriaEl != null)
        {
            foreach (var rec in criteriaEl.Elements("CriteriaRecord"))
            {
                cfg.Criteria.Add(new CriteriaRecord
                {
                    ComparisonTypeKey = ParseComparison(rec.Element("ComparisonTypeKey")?.Value),
                    ComparisonValue   = rec.Element("ComparisonValue")?.Value ?? "",
                    FieldKey          = rec.Element("FieldKey")?.Value ?? "",
                    FilterType        = ParseScope(rec.Element("FilterType")?.Value),
                    UseGameValue      = ParseBool(rec.Element("UseGameValue")?.Value, false),
                    Weight            = ParseWeight(rec.Element("Weight")),
                });
            }
        }
        return cfg;
    }

    private static bool ParseBool(string? v, bool dflt)
        => string.IsNullOrEmpty(v) ? dflt : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static int ParseInt(string? v, int dflt) => int.TryParse(v, out var r) ? r : dflt;

    /// <summary>xsi:nil="true" or empty/garbage ⇒ null (hard filter). Numeric ⇒ that weight.</summary>
    private static int? ParseWeight(XElement? weightEl)
    {
        if (weightEl == null) return null;
        var nil = weightEl.Attribute(XsiNs + "nil");
        if (nil != null && nil.Value == "true") return null;
        var v = weightEl.Value;
        if (string.IsNullOrEmpty(v)) return null;
        return int.TryParse(v, out var r) ? r : (int?)null;
    }

    private static ComparisonType ParseComparison(string? v)
        => !string.IsNullOrEmpty(v) && Enum.TryParse<ComparisonType>(v, out var r) ? r : ComparisonType.Unknown;

    private static FilterScope ParseScope(string? v)
        => !string.IsNullOrEmpty(v) && Enum.TryParse<FilterScope>(v, out var r) ? r : FilterScope.AllGames;
}

// ── LiteBox override store (game-suggester.json) ───────────────────────────────────────────────────

/// <summary>LiteBox's independent, persisted copy of the suggester config: the per-category mirror
/// switches, each category's own override (used when its switch is off), and the global scoring layer.
/// Persisted to <c>Core\litebox\game-suggester.json</c>. Singleton with Invalidate/Save, mirroring the
/// lifecycle of other LiteBox JSON stores.</summary>
internal sealed class SuggesterStore
{
    private static SuggesterStore? _instance;
    public static SuggesterStore Instance => _instance ??= Load();
    public static void Invalidate() => _instance = null;

    // Per-category mirror switch. true (default) = mirror LaunchBox read-only; false = use our override.
    public bool UseLbSimilarGames { get; set; } = true;
    public bool UseLbRecommendedGames { get; set; } = true;
    public bool UseLbPossiblePorts { get; set; } = true;

    /// <summary>Debug-only: expose the verbose "Write Similar Games Report" entry (engine-side; not yet
    /// wired in LiteBox). Persisted here so it survives once the engine lands.</summary>
    public bool ShowReportMenuItem { get; set; } = false;

    // Own overrides (null ⇒ no own config ⇒ resolution falls back to the factory default).
    public CategoryOverride? SimilarGames { get; set; }
    public CategoryOverride? RecommendedGames { get; set; }
    public CategoryOverride? PossiblePorts { get; set; }

    private BonusSettings _bonuses = new();
    /// <summary>Global scoring layer applied on top of every category's criteria. Never null.</summary>
    public BonusSettings Bonuses
    {
        get => _bonuses ??= new BonusSettings();
        set => _bonuses = value ?? new BonusSettings();
    }

    public bool GetUseLaunchBox(SuggesterCategory cat) => cat switch
    {
        SuggesterCategory.SimilarGames     => UseLbSimilarGames,
        SuggesterCategory.RecommendedGames => UseLbRecommendedGames,
        SuggesterCategory.PossiblePorts    => UseLbPossiblePorts,
        _ => true,
    };

    public void SetUseLaunchBox(SuggesterCategory cat, bool useLb)
    {
        switch (cat)
        {
            case SuggesterCategory.SimilarGames:     UseLbSimilarGames = useLb; break;
            case SuggesterCategory.RecommendedGames: UseLbRecommendedGames = useLb; break;
            case SuggesterCategory.PossiblePorts:    UseLbPossiblePorts = useLb; break;
        }
    }

    public CategoryOverride? Get(SuggesterCategory cat) => cat switch
    {
        SuggesterCategory.SimilarGames     => SimilarGames,
        SuggesterCategory.RecommendedGames => RecommendedGames,
        SuggesterCategory.PossiblePorts    => PossiblePorts,
        _ => null,
    };

    public void Set(SuggesterCategory cat, CategoryOverride? ov)
    {
        switch (cat)
        {
            case SuggesterCategory.SimilarGames:     SimilarGames = ov; break;
            case SuggesterCategory.RecommendedGames: RecommendedGames = ov; break;
            case SuggesterCategory.PossiblePorts:    PossiblePorts = ov; break;
        }
    }

    // ── Persistence ─────────────────────────────────────────────────────────────────────────────

    private static string ConfigPath => LiteBoxPaths.File("game-suggester.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static SuggesterStore Load()
    {
        try
        {
            var path = ConfigPath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<SuggesterStore>(File.ReadAllText(path), JsonOpts);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex) { Console.WriteLine("[suggester] override load failed: " + ex.Message); }
        return new SuggesterStore();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
            Console.WriteLine("[suggester] overrides saved to " + ConfigPath);
        }
        catch (Exception ex) { Console.WriteLine("[suggester] override save failed: " + ex.Message); }
    }

    /// <summary>One category's LiteBox-owned rule set (the persisted half of a resolved config).</summary>
    public sealed class CategoryOverride
    {
        public bool AllowDbGames { get; set; }
        public int MinimumScore { get; set; }
        public List<CriteriaRecord> Criteria { get; set; } = new();

        public SuggesterConfig ToConfig(SuggesterCategory cat) => new()
        {
            Category = cat,
            Source = ConfigSource.LiteBoxOverride,
            AllowDbGames = AllowDbGames,
            MinimumScore = MinimumScore,
            Criteria = (Criteria ?? new List<CriteriaRecord>()).Select(c => c.Clone()).ToList(),
        };
    }

    /// <summary>Global scoring layer (LiteBox extra, editable in every host). A future SuggesterEngine
    /// layers these on top of the per-category criteria.</summary>
    public sealed class BonusSettings
    {
        /// <summary>Graded genre score (partial credit + tag bonus) vs strict LaunchBox binary match.</summary>
        public bool GradedGenreScoring { get; set; } = true;

        /// <summary>Boost games already in the user's library.</summary>
        public bool LocalLibraryBonusEnabled { get; set; } = true;
        /// <summary>Cap (points) for the owned-games boost.</summary>
        public int LocalLibraryBonusMax { get; set; } = 2;

        /// <summary>Sørensen–Dice threshold (0..1) for the "Is Similar To" comparator. Lower = looser.</summary>
        public double SimilarityThreshold { get; set; } = 0.4;

        public BonusSettings Clone() => new()
        {
            GradedGenreScoring = GradedGenreScoring,
            LocalLibraryBonusEnabled = LocalLibraryBonusEnabled,
            LocalLibraryBonusMax = LocalLibraryBonusMax,
            SimilarityThreshold = SimilarityThreshold,
        };
    }
}

// ── Resolution (the double-registration chain in one place) ────────────────────────────────────────

/// <summary>Resolves the EFFECTIVE config for a category from the mirror switch: mirror → LaunchBox's
/// config (else default); own → the LiteBox override (else default). Always returns a usable config.
/// The (future) engine and the options UI both go through here so they never disagree.</summary>
internal static class SuggesterResolver
{
    public static SuggesterConfig Resolve(SuggesterCategory cat)
    {
        var store = SuggesterStore.Instance;
        if (store.GetUseLaunchBox(cat))
            return LaunchBoxSuggester.GetOrNull(cat) ?? SuggesterDefaults.Default(cat);

        return store.Get(cat)?.ToConfig(cat) ?? SuggesterDefaults.Default(cat);
    }
}
