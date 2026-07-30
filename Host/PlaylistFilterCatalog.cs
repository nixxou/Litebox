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

    public static PlaylistFilterField? Find(string? fieldKey, IEnumerable<string>? customNames = null)
    {
        var key = Norm(fieldKey);
        if (key.Length == 0) return null;
        var hit = Builtin.FirstOrDefault(f => Norm(f.Key) == key || Norm(f.Label) == key);
        if (hit != null) return hit;
        foreach (var alias in Aliases)
            if (key == alias.Key) return Builtin.FirstOrDefault(f => f.Key == alias.Value);
        var custom = (customNames ?? Array.Empty<string>())
            .FirstOrDefault(n => Norm(n) == key);
        return custom == null ? null : new PlaylistFilterField(custom.Trim(), custom.Trim(), PlaylistFieldKind.Text);
    }

    // Spellings seen in LiteBox INI / older LaunchBox files that are not the canonical key.
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

    /// <summary>Resolves a ComparisonTypeKey to the entry for a given field kind, tolerating the
    /// "Is" prefix LaunchBox shows in its labels but may or may not store.</summary>
    public static PlaylistComparison? FindComparison(string? key, PlaylistFieldKind kind)
    {
        var wanted = Norm(key);
        if (wanted.Length == 0) return null;
        foreach (var c in Comparisons(kind))
            if (Accepts(c, wanted)) return c;
        // A rule may name a comparison that does not belong to this field's type (hand-edited file,
        // or a field whose type LiteBox guessed differently). Fall back to the full vocabulary.
        foreach (var c in TextComparisons.Concat(BoolComparisons).Concat(NumberComparisons))
            if (Accepts(c, wanted)) return c;
        return null;
    }

    private static bool Accepts(PlaylistComparison c, string normalizedKey)
    {
        if (normalizedKey == Norm(c.Key) || normalizedKey == Norm(c.Label)) return true;
        // "NotEqualTo" / "IsNotEqualTo", "SimilarTo" / "IsSimilarTo", "DoesNotContain" /
        // "NotContains" — the same comparison under the spellings LaunchBox mixes.
        string bare = Norm(c.Key).StartsWith("is", StringComparison.Ordinal) && Norm(c.Key).Length > 2
            ? Norm(c.Key).Substring(2) : Norm(c.Key);
        if (normalizedKey == bare || normalizedKey == "is" + bare) return true;
        return c.Key switch
        {
            "DoesNotContain" => normalizedKey is "notcontains" or "doesntcontain",
            "NotEqualTo" => normalizedKey is "notequals",
            "HasNoneOfTheValues" => normalizedKey is "hasnoneofvalues" or "hasnone",
            "DoesNotStartWithAnyValue" => normalizedKey is "doesntstartwithanyvalue" or "doesnotstartwith",
            "DoesNotContainAnyValue" => normalizedKey is "doesntcontainanyvalue",
            _ => false,
        };
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
