using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed record GameSortDefinition(string Key, string Label, string LaunchBoxValue);

/// <summary>
/// Holds a kiosk-selected global order until the desktop loads another game list.
/// A desktop sort action before that load wins and cancels the staged kiosk order.
/// </summary>
internal sealed class DeferredGameSort
{
    public bool Pending { get; private set; }

    public void Stage(ref string sessionKey, ref bool sessionAscending, string key, bool ascending)
    {
        sessionKey = key;
        sessionAscending = ascending;
        Pending = true;
    }

    public void DesktopSelection(
        ref string sessionKey,
        ref bool sessionAscending,
        bool updatesSession,
        string selectedKey,
        bool selectedAscending)
    {
        // A NODE-LOCAL pick (Manual, or any re-sort inside a playlist that imposes its own order)
        // is not a change of the session order, so it must leave a staged kiosk choice alone —
        // picking Manual in a playlist should not silently throw away what was chosen in the kiosk.
        if (!updatesSession) return;

        // A global desktop pick IS the user changing the order themselves: it replaces the staged
        // kiosk choice, which then never reaches the next node load.
        sessionKey = selectedKey;
        sessionAscending = selectedAscending;
        Pending = false;
    }

    public void AppliedOnNodeLoad() => Pending = false;
}

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

    /// <summary>Vide le cache de support high-score. Appelé quand les hiscore.dat installés changent
    /// (émulateur installé, dat déployé) — sinon un jeu resterait « non supporté » pour la session.</summary>
    internal static void ClearMameSupportCache() => MameSupportCache.Clear();

    /// <summary>Forget ONE game's MAME-support answer — it is keyed on the rom path and the emulator,
    /// so editing either makes the memo lie (and nothing else clears it per game).</summary>
    internal static void ForgetMameSupport(string gameId)
    { if (!string.IsNullOrEmpty(gameId)) MameSupportCache.TryRemove(gameId, out _); }

    public static readonly GameSortDefinition[] Standard =
    {
        new("dateadded",       "Date Added",                  "DateAdded"),
        new("datemodified",    "Date Modified",               "DateModified"),
        new("developer",       "Developer",                   "Developer"),
        // LaunchBox labels the ESRB/PEGI field "Rating" and the score "Star Rating", which reads
        // badly side by side. LiteBox shows "ESRB" — the name of its own list column — and places
        // it where that label sorts. Only the LABEL differs: the XML value stays "Rating", and
        // Parse still accepts "Rating", "ESRB" and the "esrb" alias.
        new("rating",          "ESRB",                        "Rating"),
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

    public static bool IsStandard(string key)
        => Standard.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string ToLaunchBoxValue(string key)
    {
        if (key != null && key.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return key.Substring(CustomPrefix.Length);
        if (string.Equals(key, Manual, StringComparison.OrdinalIgnoreCase)) return "Manual";
        if (string.Equals(key, Default, StringComparison.OrdinalIgnoreCase)) return "Default";
        return Standard.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.LaunchBoxValue ?? "Default";
    }

    public static bool UpdatesSession(bool playlistHasConfiguredOverride, string key)
        => !playlistHasConfiguredOverride
           && !string.Equals(key, Manual, StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, int> ManualRanks(IEnumerable<IPlaylistGame>? playlistGames)
    {
        var ranked = (playlistGames ?? Array.Empty<IPlaylistGame>())
            .Select((pg, sourceIndex) => new
            {
                Id = Safe(() => pg.GameId) ?? "",
                Order = Safe(() => pg.ManualOrder),
                SourceIndex = sourceIndex,
            })
            .Where(x => x.Id.Length > 0)
            .OrderBy(x => x.Order)
            .ThenBy(x => x.SourceIndex)
            .ToArray();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ranked.Length; i++) result[ranked[i].Id] = i;
        return result;
    }

    // Collecting the custom-field names walks the WHOLE library and allocates an ICustomField[]
    // per game. The Arrange By menu, the playlist editor, every games.json and every kiosk sort
    // message need that list, so it is computed at most once per CustomFieldsTtlMs. Adding a
    // custom field therefore shows up in the menus within a few seconds instead of instantly —
    // an acceptable trade for not re-scanning the library on every dropdown click.
    private const int CustomFieldsTtlMs = 5000;
    private static readonly object CustomFieldsLock = new();
    private static string[]? _customFieldNames;
    private static long _customFieldNamesAt;

    /// <summary>Drops the cached custom-field names — call after the library changes in a way
    /// that can introduce one (import, game edit).</summary>
    public static void InvalidateCustomFieldNames()
    {
        lock (CustomFieldsLock) _customFieldNames = null;
    }

    public static string[] CustomFieldNames(IEnumerable<IGame>? games)
    {
        lock (CustomFieldsLock)
        {
            long now = Environment.TickCount64;
            if (_customFieldNames != null && now - _customFieldNamesAt < CustomFieldsTtlMs)
                return _customFieldNames;
            _customFieldNames = Collect(games);
            _customFieldNamesAt = now;
            return _customFieldNames;
        }

        static string[] Collect(IEnumerable<IGame>? source)
            => (source ?? Array.Empty<IGame>())
                .Where(g => g != null)
                .SelectMany(SafeCustomFields)
                .Select(f => Safe(() => f.Name)?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
    }

    /// <summary>Sort key for one field. A MISSING value is returned as null on purpose: ValueComparer
    /// ranks null as the greatest value, which keeps blanks at the bottom ascending / the top descending —
    /// the rule LiteBox's own columns always used. vendor/game-sort.js mirrors it exactly.
    /// Empty TEXT stays "" (an empty string is a value, not a hole), as it always did.</summary>
    public static Func<IGame, object?> Getter(string key, TitleSortNormalization titleMode)
    {
        if (key != null && key.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = key.Substring(CustomPrefix.Length);
            return g => CustomValue(g, name);
        }
        return key?.ToLowerInvariant() switch
        {
            "dateadded"      => g => Date(Safe(() => (DateTime?)g.DateAdded)),
            "datemodified"   => g => Date(Safe(() => (DateTime?)g.DateModified)),
            "developer"      => g => Safe(() => (object)g.Developer) ?? "",
            "favorite"       => g => Safe(() => (object)g.Favorite),
            "genre"          => g => Safe(() => (object)g.GenresString) ?? "",
            // Tri-state on purpose. Installed is a user checkbox, so "unset" is a hole (it ranks
            // last, like a missing year) and is NOT the same as an explicit "not installed".
            // The web badge asks a different question — is the game present — and treats unset as
            // present; see WebStoreState.IsInstalledOrPresent.
            "installed"      => g => Safe(() => (object)g.Installed),
            "lastplayed"     => g => Date(Safe(() => g.LastPlayedDate)),
            "launchboxid"    => g => Safe(() => (object?)g.LaunchBoxDbId),
            "mamehighscores" => g => MameHighScoresSupported(g),
            "maxplayers"     => g => Safe(() => (object?)g.MaxPlayers),
            "platform"       => g => Safe(() => (object)g.Platform) ?? "",
            "playcount"      => g => Safe(() => (object)g.PlayCount),
            "playmode"       => g => Safe(() => (object)g.PlayMode) ?? "",
            "playtime"       => g => Safe(() => (object)g.PlayTime),
            "portable"       => g => Safe(() => (object)g.Portable),
            "progress"       => g => Safe(() => (object)g.Progress) ?? "",
            "publisher"      => g => Safe(() => (object)g.Publisher) ?? "",
            "rating"         => g => Safe(() => (object)g.Rating) ?? "",
            "region"         => g => Safe(() => (object)g.Region) ?? "",
            "releasedate"    => g => Date(Safe(() => g.ReleaseDate)),
            "releaseyear"    => g => (object?)EffectiveYear(g),
            "releasetype"    => g => Safe(() => (object)g.ReleaseType) ?? "",
            "series"         => g => Safe(() => (object)g.Series) ?? "",
            "source"         => g => Safe(() => (object)g.Source) ?? "",
            // Local rating when the user set one, community rating otherwise — the same value the
            // Rating column shows. An unrated game has NO score (null), it does not score zero.
            "starrating"     => g => Score(Safe(() => (double?)g.CommunityOrLocalStarRating)),
            "status"         => g => Safe(() => (object)g.Status) ?? "",
            "version"        => g => Safe(() => (object)g.Version) ?? "",
            _                => g => TitleSortNormalizer.Normalize(g, titleMode),
        };
    }

    /// <summary>An unset date is a hole, not 01/01/0001.</summary>
    private static object? Date(DateTime? value)
        => value.HasValue && value.Value != default ? value.Value : null;

    /// <summary>An absent score is a hole, not a zero — otherwise unrated games would outrank
    /// nothing and still sit before the 1-star ones.</summary>
    private static object? Score(double? value)
        => value.HasValue && value.Value > 0 ? value.Value : null;

    /// <summary>LaunchBox's "Release Date Year": the explicit ReleaseYear, else the year carried by
    /// ReleaseDate. Shared by the Year column, the Arrange By key and the web payload so the value a
    /// surface DISPLAYS is the value it SORTS on.</summary>
    public static int? EffectiveYear(IGame game)
    {
        try { var y = game?.ReleaseYear; if (y.HasValue && y.Value > 1950 && y.Value < 2100) return y; } catch { }
        try { var d = game?.ReleaseDate; if (d.HasValue && d.Value.Year > 1950 && d.Value.Year < 2100) return d.Value.Year; } catch { }
        return null;
    }

    public static bool MameHighScoresSupported(IGame game)
    {
        if (game == null) return false;
        // Cache FIRST: this runs once per game per games.json payload, and both fallbacks below are
        // expensive (a reflection probe, or a walk over every emulator and its platforms).
        var id = Safe(() => game.Id) ?? "";
        if (id.Length > 0 && MameSupportCache.TryGetValue(id, out bool cached)) return cached;

        bool supported;
        // In a real LaunchBox process the concrete Game exposes the exact computed property.
        var probe = Safe(() => game.GetType().GetProperty("HasMameHighScoreSupport"));
        if (probe?.PropertyType == typeof(bool) && Safe(() => probe.GetValue(game)) is bool value)
            supported = value;
        else
            // LiteBox's XML-backed HostGame cannot call that concrete property. La réponse locale fidèle est
            // le hiscore.dat installé : il liste exactement les machines dont on sait lire le score, ce que
            // « tourne sur MAME » ne disait qu'approximativement.
            supported = Safe(() => Mame.MameLeaderboards.HasHiscoreSupport(game));

        if (id.Length > 0) MameSupportCache[id] = supported;
        return supported;
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
