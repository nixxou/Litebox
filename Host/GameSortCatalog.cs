using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed record GameSortDefinition(string Key, string Label, string LaunchBoxValue);

/// <summary>
/// One vocabulary for the desktop Arrange By menu, playlist SortBy values and both web clients.
/// Internal keys are stable and lower-case; LaunchBoxValue is what is written to playlist XML.
/// </summary>
internal static class GameSortCatalog
{
    public const string Default = "default";
    public const string Manual = "manual";
    public const string CustomPrefix = "custom:";
    private static readonly ConcurrentDictionary<string, bool> MameSupportCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly GameSortDefinition[] Standard =
    {
        new("dateadded",       "Date Added",                  "DateAdded"),
        new("datemodified",    "Date Modified",               "DateModified"),
        new("developer",       "Developer",                   "Developer"),
        new("favorite",        "Favorite",                    "Favorite"),
        new("genre",           "Genre",                       "Genre"),
        new("installed",       "Installed",                   "Installed"),
        new("lastplayed",      "Last Played",                 "LastPlayed"),
        new("launchboxid",     "LaunchBox Database ID",       "LaunchBoxId"),
        new("mamehighscores",  "MAME High Scores Supported",  "MameHighScoresSupported"),
        new("maxplayers",      "Max Players",                 "MaxPlayers"),
        new("platform",        "Platform",                    "Platform"),
        new("playcount",       "Play Count",                  "PlayCount"),
        new("playmode",        "Play Mode",                   "PlayMode"),
        new("playtime",        "Play Time",                   "PlayTime"),
        new("portable",        "Portable",                    "Portable"),
        new("progress",        "Progress",                    "Progress"),
        new("publisher",       "Publisher",                   "Publisher"),
        new("rating",          "Rating",                      "Rating"),
        new("region",          "Region",                      "Region"),
        new("releasedate",     "Release Date",                "ReleaseDate"),
        new("releaseyear",     "Release Date Year",           "ReleaseDateYear"),
        new("releasetype",     "Release Type",                "ReleaseType"),
        new("series",          "Series",                      "Series"),
        new("source",          "Source",                      "Source"),
        new("starrating",      "Star Rating",                 "StarRating"),
        new("status",          "Status",                      "Status"),
        new("title",           "Title",                       "Title"),
        new("version",         "Version",                     "Version"),
    };

    public static string Parse(string? value, IEnumerable<string>? customNames = null)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0 || raw.Equals("Default", StringComparison.OrdinalIgnoreCase)) return Default;
        if (raw.Equals("Manual", StringComparison.OrdinalIgnoreCase)) return Manual;

        var compact = Compact(raw);
        foreach (var d in Standard)
            if (compact == Compact(d.Key) || compact == Compact(d.Label) || compact == Compact(d.LaunchBoxValue))
                return d.Key;

        // Historical aliases seen in LiteBox INI / LaunchBox XML.
        if (compact is "name" or "comparename") return "title";
        if (compact is "year") return "releaseyear";
        if (compact is "plays") return "playcount";
        if (compact is "players") return "maxplayers";
        if (compact is "dbid" or "databaseid" or "launchboxdatabaseid") return "launchboxid";
        if (compact is "fav") return "favorite";
        if (compact is "esrb") return "rating";

        var custom = (customNames ?? Array.Empty<string>())
            .FirstOrDefault(x => string.Equals(x?.Trim(), raw, StringComparison.OrdinalIgnoreCase));
        return custom == null ? Default : CustomPrefix + custom.Trim();
    }

    public static string Label(string key)
    {
        if (key != null && key.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return key.Substring(CustomPrefix.Length);
        if (string.Equals(key, Manual, StringComparison.OrdinalIgnoreCase)) return "Manual";
        if (string.Equals(key, Default, StringComparison.OrdinalIgnoreCase)) return "Default";
        return Standard.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Label ?? "Title";
    }

    public static string ToLaunchBoxValue(string key)
    {
        if (key != null && key.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return key.Substring(CustomPrefix.Length);
        if (string.Equals(key, Manual, StringComparison.OrdinalIgnoreCase)) return "Manual";
        if (string.Equals(key, Default, StringComparison.OrdinalIgnoreCase)) return "Default";
        return Standard.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.LaunchBoxValue ?? "Default";
    }

    public static string[] CustomFieldNames(IEnumerable<IGame>? games)
        => (games ?? Array.Empty<IGame>())
            .Where(g => g != null)
            .SelectMany(SafeCustomFields)
            .Select(f => Safe(() => f.Name)?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;

    public static Func<IGame, object?> Getter(string key, TitleSortNormalization titleMode)
    {
        if (key != null && key.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = key.Substring(CustomPrefix.Length);
            return g => CustomValue(g, name);
        }
        return key?.ToLowerInvariant() switch
        {
            "dateadded"      => g => Safe(() => (object)g.DateAdded),
            "datemodified"   => g => Safe(() => (object)g.DateModified),
            "developer"      => g => Safe(() => (object)g.Developer) ?? "",
            "favorite"       => g => Safe(() => (object)g.Favorite),
            "genre"          => g => Safe(() => (object)g.GenresString) ?? "",
            "installed"      => g => Safe(() => (object)(g.Installed == true)),
            "lastplayed"     => g => Safe(() => (object?)g.LastPlayedDate) ?? DateTime.MinValue,
            "launchboxid"    => g => Safe(() => (object?)g.LaunchBoxDbId) ?? -1,
            "mamehighscores" => g => MameHighScoresSupported(g),
            "maxplayers"     => g => Safe(() => (object?)g.MaxPlayers) ?? -1,
            "platform"       => g => Safe(() => (object)g.Platform) ?? "",
            "playcount"      => g => Safe(() => (object)g.PlayCount),
            "playmode"       => g => Safe(() => (object)g.PlayMode) ?? "",
            "playtime"       => g => Safe(() => (object)g.PlayTime),
            "portable"       => g => Safe(() => (object)g.Portable),
            "progress"       => g => Safe(() => (object)g.Progress) ?? "",
            "publisher"      => g => Safe(() => (object)g.Publisher) ?? "",
            "rating"         => g => Safe(() => (object)g.Rating) ?? "",
            "region"         => g => Safe(() => (object)g.Region) ?? "",
            "releasedate"    => g => Safe(() => (object?)g.ReleaseDate) ?? DateTime.MinValue,
            "releaseyear"    => g => Safe(() => (object?)g.ReleaseYear) ?? -1,
            "releasetype"    => g => Safe(() => (object)g.ReleaseType) ?? "",
            "series"         => g => Safe(() => (object)g.Series) ?? "",
            "source"         => g => Safe(() => (object)g.Source) ?? "",
            "starrating"     => g => Safe(() => (object)g.CommunityOrLocalStarRating),
            "status"         => g => Safe(() => (object)g.Status) ?? "",
            "version"        => g => Safe(() => (object)g.Version) ?? "",
            _                => g => TitleSortNormalizer.Normalize(g, titleMode),
        };
    }

    public static bool MameHighScoresSupported(IGame game)
    {
        if (game == null) return false;
        // In a real LaunchBox process the concrete Game exposes the exact computed property.
        try
        {
            var p = game.GetType().GetProperty("HasMameHighScoreSupport");
            if (p?.PropertyType == typeof(bool)) return (bool)p.GetValue(game);
        }
        catch { }

        // LiteBox's XML-backed HostGame cannot call that concrete property. Its closest faithful
        // local equivalent is a game that can run through the supported MAME/FBNeo integration.
        var id = Safe(() => game.Id) ?? "";
        if (id.Length == 0) return Safe(() => Mame.MameLeaderboards.IsMameGame(game));
        return MameSupportCache.GetOrAdd(id, _ => Safe(() => Mame.MameLeaderboards.IsMameGame(game)));
    }

    private static string Compact(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static ICustomField[] SafeCustomFields(IGame game)
    {
        try { return game.GetAllCustomFields() ?? Array.Empty<ICustomField>(); }
        catch { return Array.Empty<ICustomField>(); }
    }

    private static string CustomValue(IGame game, string name)
    {
        foreach (var f in SafeCustomFields(game))
            if (string.Equals(Safe(() => f.Name), name, StringComparison.OrdinalIgnoreCase))
                return Safe(() => f.Value) ?? "";
        return "";
    }

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }
}
