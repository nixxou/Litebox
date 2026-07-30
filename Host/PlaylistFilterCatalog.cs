// The vocabulary of Edit Playlist -> Auto-Populate: which fields a rule can target, and which
// comparisons each field TYPE accepts.
//
// Transcribed from LaunchBox's own dropdowns. Two things drive the whole design:
//
//   • The comparison list depends on the field's type. A text field offers fourteen comparisons,
//     a boolean offers four (Is True / Is False / Is Empty / Is Not Empty) and greys the Value
//     cell out to "(Unused)", a number offers the ordering ones. One flat list for every field
//     both hides real comparisons and offers nonsense ones.
//   • Custom fields sit INLINE in the alphabetical field list (unlike Arrange By, which appends
//     them after a separator).
//
// CONFIDENCE. Only two ComparisonTypeKey values have been seen in real playlist XML — EqualTo and
// Contains — so that PascalCase-without-"Is" shape is what the keys below follow. The rest are
// reconstructed from the labels; Accepts() therefore matches on several spellings so a file
// written by LaunchBox parses whichever form it actually uses. Same for FieldKey: the five
// observed ones (Platform, Genre, Publisher, Source, PlayMode) match. An unknown key read from
// XML is never rewritten — EditPlaylistPopulate injects it verbatim into the combo.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal enum PlaylistFieldKind { Text, Number, Bool, Date }

internal sealed record PlaylistFilterField(string Key, string Label, PlaylistFieldKind Kind)
{
    /// <summary>False when LiteBox has no local equivalent for the field (Steam/GOG achievements,
    /// controller support…). The rule is kept in the file and shown in the editor, but skipped when
    /// resolving membership — see HostPlaylist.MatchesFilters.</summary>
    public bool Evaluable { get; init; } = true;
}

internal sealed record PlaylistComparison(string Key, string Label, bool UsesValue, bool Positive);

/// <summary>An auto-populate rule set with its fields and comparisons already resolved.
///
/// Resolving them per game was costing ~28 microseconds per game per playlist: every game re-ran
/// the field lookup, the comparison lookup and an ICustomField[] allocation, so opening a category
/// that aggregates a few dozen auto playlists took close to a second. Compile once, evaluate many.
/// </summary>
internal sealed class PlaylistFilterPlan
{
    private sealed record Group(PlaylistFilterField Field, bool IsCustom, (PlaylistComparison Cmp, string Value)[] Rules);

    private readonly Group[] _groups;

    private PlaylistFilterPlan(Group[] groups) => _groups = groups;

    /// <summary>True when at least one rule resolves to something LiteBox can actually evaluate.</summary>
    public bool HasEvaluableGroup => _groups.Length > 0;

    public static PlaylistFilterPlan Compile(IEnumerable<PlaylistFilterDefLike>? filters)
    {
        var groups = new List<Group>();
        foreach (var byField in (filters ?? Array.Empty<PlaylistFilterDefLike>())
                     .Where(f => f != null && !string.IsNullOrWhiteSpace(f.FieldKey))
                     .GroupBy(f => f.FieldKey.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var field = PlaylistFilterCatalog.Find(byField.Key)
                        // Not a built-in: assume a custom field of that name, which is text.
                        ?? new PlaylistFilterField(byField.Key.Trim(), byField.Key.Trim(), PlaylistFieldKind.Text);
            if (!field.Evaluable) continue;   // unsupported → not a constraint at all
            bool isCustom = PlaylistFilterCatalog.Find(byField.Key) == null;

            var rules = byField
                .Select(r => (Cmp: PlaylistFilterCatalog.FindComparison(r.ComparisonTypeKey, field.Kind), r.Value))
                .Where(x => x.Cmp != null)
                .Select(x => (x.Cmp!, x.Value ?? ""))
                .ToArray();
            if (rules.Length == 0) continue;
            groups.Add(new Group(field, isCustom, rules));
        }
        return new PlaylistFilterPlan(groups.ToArray());
    }

    public bool Matches(IGame game)
    {
        if (game == null || _groups.Length == 0) return false;
        foreach (var group in _groups)
        {
            string value = group.IsCustom
                ? CustomValue(game, group.Field.Key)
                : PlaylistFilterCatalog.Read(game, group.Field) ?? "";

            bool sawPositive = false, positiveHit = false;
            foreach (var (cmp, target) in group.Rules)
            {
                bool hit = PlaylistFilterCatalog.Compare(value, cmp, target);
                if (cmp.Positive)
                {
                    sawPositive = true;
                    positiveHit |= hit;
                }
                else if (!hit) return false;   // negative and ordering rules are ANDed
            }
            if (sawPositive && !positiveHit) return false;   // positive rules are ORed
        }
        return true;
    }

    private static string CustomValue(IGame game, string name)
    {
        try
        {
            foreach (var cf in game.GetAllCustomFields() ?? Array.Empty<ICustomField>())
                if (string.Equals(cf?.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return cf.Value ?? "";
        }
        catch { }
        return "";
    }
}

/// <summary>The shape Compile needs from a rule, so the plan does not depend on the storage type.</summary>
internal interface PlaylistFilterDefLike
{
    string FieldKey { get; }
    string ComparisonTypeKey { get; }
    string Value { get; }
}

internal static class PlaylistFilterCatalog
{
    // Alphabetical by label, exactly as LaunchBox renders it.
    private static readonly PlaylistFilterField[] Builtin =
    {
        new("AlternateName",           "Alternate Name",             PlaylistFieldKind.Text)  { Evaluable = false },
        new("Amazon",                  "Amazon Games",               PlaylistFieldKind.Bool),
        new("AnyAchievements",         "Any Achievements",           PlaylistFieldKind.Bool),
        new("ApplicationPath",         "Application/ROM Path",       PlaylistFieldKind.Text),
        new("Broken",                  "Broken",                     PlaylistFieldKind.Bool),
        new("ControllerSupport",       "Controller Support",         PlaylistFieldKind.Text) { Evaluable = false },
        new("DateAdded",               "Date Added",                 PlaylistFieldKind.Date),
        new("DateModified",            "Date Modified",              PlaylistFieldKind.Date),
        new("Developer",               "Developer",                  PlaylistFieldKind.Text),
        new("EA",                      "EA",                         PlaylistFieldKind.Bool),
        new("EpicGames",               "Epic Games",                 PlaylistFieldKind.Bool),
        new("Favorite",                "Favorite",                   PlaylistFieldKind.Bool),
        new("GameSaves",               "Game Saves",                 PlaylistFieldKind.Bool) { Evaluable = false },
        new("Genre",                   "Genre",                      PlaylistFieldKind.Text),
        new("GOG",                     "GOG",                        PlaylistFieldKind.Bool),
        new("GOGAchievements",         "GOG Achievements",           PlaylistFieldKind.Bool) { Evaluable = false },
        new("Hide",                    "Hide",                       PlaylistFieldKind.Bool),
        new("Installed",               "Installed",                  PlaylistFieldKind.Bool),
        new("LastPlayedDate",          "Last Played",                PlaylistFieldKind.Date),
        new("LaunchBoxDbId",           "LaunchBox Database ID",      PlaylistFieldKind.Number),
        new("MameHighScoresSupported", "MAME High Scores Supported", PlaylistFieldKind.Bool),
        new("MaxPlayers",              "Max Players",                PlaylistFieldKind.Number),
        new("Notes",                   "Notes",                      PlaylistFieldKind.Text),
        new("Platform",                "Platform",                   PlaylistFieldKind.Text),
        new("PlayCount",               "Play Count",                 PlaylistFieldKind.Number),
        new("PlayMode",                "Play Mode",                  PlaylistFieldKind.Text),
        new("Portable",                "Portable",                   PlaylistFieldKind.Bool),
        new("Progress",                "Progress",                   PlaylistFieldKind.Text),
        new("Publisher",               "Publisher",                  PlaylistFieldKind.Text),
        // LaunchBox labels this field "Rating" and the score "Star Rating". LiteBox shows "ESRB"
        // here and in Arrange By, matching its own list column. Label only — FieldKey stays
        // "Rating", which is what goes into <FieldKey> and what LaunchBox reads back.
        new("Rating",                  "ESRB",                       PlaylistFieldKind.Text),
        new("Region",                  "Region",                     PlaylistFieldKind.Text),
        new("ReleaseDate",             "Release Date",               PlaylistFieldKind.Date),
        new("ReleaseType",             "Release Type",               PlaylistFieldKind.Text),
        new("RetroAchievements",       "RetroAchievements",          PlaylistFieldKind.Bool),
        new("Series",                  "Series",                     PlaylistFieldKind.Text),
        new("SortTitle",               "Sort Title",                 PlaylistFieldKind.Text),
        new("Source",                  "Source",                     PlaylistFieldKind.Text),
        new("StarRating",              "Star Rating",                PlaylistFieldKind.Number),
        new("Status",                  "Status",                     PlaylistFieldKind.Text),
        new("Steam",                   "Steam",                      PlaylistFieldKind.Bool),
        new("SteamAchievements",       "Steam Achievements",         PlaylistFieldKind.Bool) { Evaluable = false },
        new("Storefront",              "Storefront",                 PlaylistFieldKind.Text),
        new("Title",                   "Title",                      PlaylistFieldKind.Text),
        new("Uplay",                   "Uplay/Ubisoft Connect",      PlaylistFieldKind.Bool),
        new("UseDosBox",               "Use DOSBox",                 PlaylistFieldKind.Bool),
        new("UseScummVm",              "Use ScummVM",                PlaylistFieldKind.Bool),
        new("Version",                 "Version",                    PlaylistFieldKind.Text),
        new("Xbox",                    "Xbox/Microsoft Store",       PlaylistFieldKind.Bool),
    };

    // Text — the fourteen LaunchBox offers, in its order. "Positive" drives how repeated rules on
    // ONE field combine: see HostPlaylist.MatchesFilters.
    private static readonly PlaylistComparison[] TextComparisons =
    {
        new("EqualTo",                  "Is Equal To",                true,  true),
        new("NotEqualTo",               "Is Not Equal To",            true,  false),
        new("Contains",                 "Contains",                   true,  true),
        new("DoesNotContain",           "Doesn't Contain",            true,  false),
        new("IsEmpty",                  "Is Empty",                   false, false),
        new("IsNotEmpty",               "Is Not Empty",               false, false),
        new("HasAtLeastOneOf",          "Has At Least One Of",        true,  true),
        new("HasNoneOfTheValues",       "Has None of the Values",     true,  false),
        new("SimilarTo",                "Is Similar To",              true,  true),
        new("NotSimilarTo",             "Is Not Similar To",          true,  false),
        new("ContainsAnyValue",         "Contains Any Value",         true,  true),
        new("DoesNotContainAnyValue",   "Doesn't Contain Any Value",  true,  false),
        new("StartsWith",               "Starts With",                true,  true),
        new("DoesNotStartWithAnyValue", "Doesn't Start With Any Value", true, false),
    };

    private static readonly PlaylistComparison[] BoolComparisons =
    {
        new("IsTrue",     "Is True",      false, true),
        new("IsFalse",    "Is False",     false, true),
        new("IsEmpty",    "Is Empty",     false, false),
        new("IsNotEmpty", "Is Not Empty", false, false),
    };

    private static readonly PlaylistComparison[] NumberComparisons =
    {
        new("EqualTo",     "Is Equal To",      true,  true),
        new("NotEqualTo",  "Is Not Equal To",  true,  false),
        new("GreaterThan", "Is Greater Than",  true,  false),
        new("LessThan",    "Is Less Than",     true,  false),
        new("IsEmpty",     "Is Empty",         false, false),
        new("IsNotEmpty",  "Is Not Empty",     false, false),
    };

    /// <summary>The field list for the editor: built-ins plus the library's custom fields, merged
    /// into ONE alphabetical sequence (LaunchBox does not group them apart here).</summary>
    public static PlaylistFilterField[] Fields(IEnumerable<string>? customNames)
        => Builtin
            .Concat((customNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                // Collides on either spelling: a custom field named "Rating" must not shadow the
                // built-in whose key is Rating just because that built-in is labelled "ESRB".
                .Where(n => !Builtin.Any(b => b.Label.Equals(n, StringComparison.OrdinalIgnoreCase)
                                              || b.Key.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new PlaylistFilterField(n, n, PlaylistFieldKind.Text)))
            .OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static PlaylistComparison[] Comparisons(PlaylistFieldKind kind) => kind switch
    {
        PlaylistFieldKind.Bool => BoolComparisons,
        PlaylistFieldKind.Number or PlaylistFieldKind.Date => NumberComparisons,
        _ => TextComparisons,
    };

    // Spellings seen in LiteBox INI / older LaunchBox files that are not the canonical key.
    // Declared BEFORE the index that consumes it — static initialisers run in declaration order.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["name"] = "Title",
        ["genres"] = "Genre",
        ["hidden"] = "Hide",
        ["lastplayed"] = "LastPlayedDate",
        ["played"] = "PlayCount",
        ["dbid"] = "LaunchBoxDbId",
        ["launchboxdatabaseid"] = "LaunchBoxDbId",
        ["esrb"] = "Rating",
        ["applicationrompath"] = "ApplicationPath",
        ["ubisoftconnect"] = "Uplay",
        ["uplayubisoftconnect"] = "Uplay",
        ["xboxmicrosoftstore"] = "Xbox",
    };

    // Every spelling of every built-in field, resolved once at type init. Find is on the hot path
    // through PlaylistFilterPlan.Compile and used to be a linear scan that normalised all ~48
    // entries on each call.
    private static readonly Dictionary<string, PlaylistFilterField> ByNormalizedName = BuildIndex();

    private static Dictionary<string, PlaylistFilterField> BuildIndex()
    {
        var index = new Dictionary<string, PlaylistFilterField>(StringComparer.Ordinal);
        foreach (var f in Builtin)
        {
            index[Norm(f.Key)] = f;
            index.TryAdd(Norm(f.Label), f);
        }
        foreach (var alias in Aliases)
        {
            var target = Builtin.FirstOrDefault(f => f.Key == alias.Value);
            if (target != null) index.TryAdd(alias.Key, target);
        }
        return index;
    }

    public static PlaylistFilterField? Find(string? fieldKey, IEnumerable<string>? customNames = null)
    {
        var key = Norm(fieldKey);
        if (key.Length == 0) return null;
        if (ByNormalizedName.TryGetValue(key, out var hit)) return hit;
        var custom = (customNames ?? Array.Empty<string>())
            .FirstOrDefault(n => Norm(n) == key);
        return custom == null ? null : new PlaylistFilterField(custom.Trim(), custom.Trim(), PlaylistFieldKind.Text);
    }

    /// <summary>Resolves a ComparisonTypeKey to the entry for a given field kind, tolerating the
    /// "Is" prefix LaunchBox shows in its labels but may or may not store.</summary>
    // Accepted spellings per kind, plus a catch-all, resolved once. Accepts() allocates several
    // normalised strings per comparison, which is far too much to redo per rule per game.
    private static readonly Dictionary<PlaylistFieldKind, Dictionary<string, PlaylistComparison>> ComparisonIndex =
        new()
        {
            [PlaylistFieldKind.Text] = IndexOf(TextComparisons),
            [PlaylistFieldKind.Bool] = IndexOf(BoolComparisons),
            [PlaylistFieldKind.Number] = IndexOf(NumberComparisons),
            [PlaylistFieldKind.Date] = IndexOf(NumberComparisons),
        };

    private static readonly Dictionary<string, PlaylistComparison> AnyComparison =
        IndexOf(TextComparisons.Concat(BoolComparisons).Concat(NumberComparisons).ToArray());

    private static Dictionary<string, PlaylistComparison> IndexOf(PlaylistComparison[] comparisons)
    {
        var index = new Dictionary<string, PlaylistComparison>(StringComparer.Ordinal);
        foreach (var c in comparisons)
            foreach (var spelling in Spellings(c))
                index.TryAdd(spelling, c);
        return index;
    }

    public static PlaylistComparison? FindComparison(string? key, PlaylistFieldKind kind)
    {
        var wanted = Norm(key);
        if (wanted.Length == 0) return null;
        if (ComparisonIndex.TryGetValue(kind, out var byKind) && byKind.TryGetValue(wanted, out var hit))
            return hit;
        // A rule may name a comparison that does not belong to this field's type (hand-edited file,
        // or a field whose type LiteBox guessed differently). Fall back to the full vocabulary.
        return AnyComparison.TryGetValue(wanted, out var any) ? any : null;
    }

    /// <summary>Every spelling one comparison answers to — its key, its label, the same with or
    /// without the "Is" prefix LaunchBox shows but may not store, plus the odd historical form.</summary>
    private static IEnumerable<string> Spellings(PlaylistComparison c)
    {
        string key = Norm(c.Key);
        yield return key;
        yield return Norm(c.Label);
        string bare = key.StartsWith("is", StringComparison.Ordinal) && key.Length > 2 ? key.Substring(2) : key;
        yield return bare;
        yield return "is" + bare;
        switch (c.Key)
        {
            case "DoesNotContain": yield return "notcontains"; yield return "doesntcontain"; break;
            case "NotEqualTo": yield return "notequals"; break;
            case "HasNoneOfTheValues": yield return "hasnoneofvalues"; yield return "hasnone"; break;
            case "DoesNotStartWithAnyValue": yield return "doesntstartwithanyvalue"; yield return "doesnotstartwith"; break;
            case "DoesNotContainAnyValue": yield return "doesntcontainanyvalue"; break;
        }
    }

    /// <summary>Multi-value comparisons take a list. LaunchBox shows them semicolon-separated.</summary>
    public static string[] SplitValues(string? value)
        => (value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToArray();

    public static string Norm(string? value)
        => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The comparable text for one field, or null when LiteBox cannot evaluate it.
    /// Booleans answer "true"/"false", or "" when the underlying value is genuinely unset.</summary>
    public static string? Read(IGame game, PlaylistFilterField field)
    {
        if (game == null || field == null || !field.Evaluable) return null;
        try
        {
            switch (field.Key)
            {
                case "Title": return game.Title;
                case "SortTitle": return game.SortTitle;
                case "Platform": return game.Platform;
                case "Developer": return game.Developer;
                case "Publisher": return game.Publisher;
                case "Genre": return game.GenresString;
                case "Region": return game.Region;
                case "PlayMode": return game.PlayMode;
                case "Rating": return game.Rating;
                case "Status": return game.Status;
                case "Version": return game.Version;
                case "Series": return game.Series;
                case "Notes": return game.Notes;
                case "Progress": return game.Progress;
                case "ReleaseType": return game.ReleaseType;
                case "ApplicationPath": return game.ApplicationPath;
                case "Source": case "Storefront": return game.Source;

                case "MaxPlayers": return game.MaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "";
                case "LaunchBoxDbId": return game.LaunchBoxDbId?.ToString(CultureInfo.InvariantCulture) ?? "";
                case "PlayCount": return game.PlayCount.ToString(CultureInfo.InvariantCulture);
                case "StarRating": return game.CommunityOrLocalStarRating.ToString(CultureInfo.InvariantCulture);

                case "DateAdded": return DateText(game.DateAdded);
                case "DateModified": return DateText(game.DateModified);
                case "ReleaseDate": return DateText(game.ReleaseDate);
                case "LastPlayedDate": return DateText(game.LastPlayedDate);

                case "Favorite": return Bool(game.Favorite);
                case "Broken": return Bool(game.Broken);
                case "Hide": return Bool(game.Hide);
                case "Portable": return Bool(game.Portable);
                case "UseDosBox": return Bool(game.UseDosBox);
                case "UseScummVm": return Bool(game.UseScummVm);
                case "Installed": return game.Installed.HasValue ? Bool(game.Installed.Value) : "";
                case "MameHighScoresSupported": return Bool(GameSortCatalog.MameHighScoresSupported(game));

                // LiteBox only knows RetroAchievements, via the hash LaunchBox stores on the game.
                case "RetroAchievements":
                case "AnyAchievements":
                    return Bool(game is Data.HostGame hg && !string.IsNullOrWhiteSpace(hg.RetroAchievementsHash));

                // Per-store flags: LaunchBox's Source/Storefront IS the store name, so "Steam Is True"
                // means "this game came from Steam".
                case "Steam": case "GOG": case "EpicGames": case "EA": case "Amazon": case "Uplay": case "Xbox":
                    return Bool(StoreMatches(game.Source, field.Key));

                default: return null;
            }
        }
        catch { return null; }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string DateText(DateTime? value)
        => value.HasValue && value.Value != default
            ? value.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "";

    private static bool StoreMatches(string? source, string storeKey)
    {
        var s = Norm(source);
        if (s.Length == 0) return false;
        return storeKey switch
        {
            "Steam" => s.Contains("steam"),
            "GOG" => s.Contains("gog"),
            "EpicGames" => s.Contains("epic"),
            "EA" => s.Contains("origin") || s.Contains("eaapp") || s == "ea" || s.Contains("eadesktop"),
            "Amazon" => s.Contains("amazon"),
            "Uplay" => s.Contains("uplay") || s.Contains("ubisoft"),
            "Xbox" => s.Contains("xbox") || s.Contains("microsoftstore") || s.Contains("windowsstore"),
            _ => false,
        };
    }

    /// <summary>Evaluates one rule. `field` is the value returned by Read.</summary>
    public static bool Compare(string? field, PlaylistComparison comparison, string? target)
    {
        string f = field ?? "";
        string t = target ?? "";
        var values = SplitValues(t);
        switch (comparison.Key)
        {
            case "EqualTo": return string.Equals(f, t, StringComparison.OrdinalIgnoreCase);
            case "NotEqualTo": return !string.Equals(f, t, StringComparison.OrdinalIgnoreCase);
            case "Contains": return f.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            case "DoesNotContain": return f.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0;
            case "StartsWith": return f.StartsWith(t, StringComparison.OrdinalIgnoreCase);
            case "IsEmpty": return f.Length == 0;
            case "IsNotEmpty": return f.Length != 0;
            case "IsTrue": return string.Equals(f, "true", StringComparison.OrdinalIgnoreCase);
            case "IsFalse": return string.Equals(f, "false", StringComparison.OrdinalIgnoreCase);

            // Multi-value: the field itself may also be a list (GenresString is ";"-separated).
            case "HasAtLeastOneOf": return values.Any(v => HasToken(f, v));
            case "HasNoneOfTheValues": return !values.Any(v => HasToken(f, v));
            case "ContainsAnyValue": return values.Any(v => f.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);
            case "DoesNotContainAnyValue": return !values.Any(v => f.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);
            case "DoesNotStartWithAnyValue": return !values.Any(v => f.StartsWith(v, StringComparison.OrdinalIgnoreCase));

            // "Similar" = same once case, punctuation and articles are set aside — the rule the
            // title sort already uses, so "Spider-Man 2" and "Spider Man 2" match.
            case "SimilarTo": return Similar(f) == Similar(t);
            case "NotSimilarTo": return Similar(f) != Similar(t);

            case "GreaterThan":
                return double.TryParse(f, NumberStyles.Any, CultureInfo.InvariantCulture, out var a1)
                       && double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var b1)
                    ? a1 > b1 : string.Compare(f, t, StringComparison.OrdinalIgnoreCase) > 0;
            case "LessThan":
                return double.TryParse(f, NumberStyles.Any, CultureInfo.InvariantCulture, out var a2)
                       && double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var b2)
                    ? a2 < b2 : string.Compare(f, t, StringComparison.OrdinalIgnoreCase) < 0;
            default: return false;
        }
    }

    private static string Similar(string value)
        => TitleSortNormalizer.Normalize(value, "", TitleSortNormalization.Simple);

    /// <summary>True when one of the field's ";"-separated tokens equals the value — the semantics
    /// "Has At Least One Of" needs on a multi-valued field such as Genre.</summary>
    private static bool HasToken(string field, string value)
    {
        if (field.IndexOf(';') < 0) return string.Equals(field, value, StringComparison.OrdinalIgnoreCase);
        foreach (var token in field.Split(';'))
            if (string.Equals(token.Trim(), value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
