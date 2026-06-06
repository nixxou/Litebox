// Loads LB\Data\Playlists\*.xml: one <Playlist> per file, with EITHER explicit
// <PlaylistGame> entries (manual playlist) OR <PlaylistFilter> rules evaluated
// over every game (AutoPopulate playlist). Manual games are resolved lazily via a
// Func<string,IGame>; auto playlists pull the full game list via a provider — both
// injected by the DataManager.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;

namespace LbApiHost.Host.Data;

/// <summary>A single auto-populate rule (Value / FieldKey / ComparisonTypeKey).</summary>
internal sealed record PlaylistFilterDef(string FieldKey, string ComparisonTypeKey, string Value);

internal sealed class HostPlaylistGame : DummyPlaylistGame
{
    private Func<string, IGame> _resolve;
    public string GameIdValue, GameTitleValue, GamePlatformValue, GameFileNameValue, PlaylistIdValue;
    public int ManualOrderValue;
    public int? LaunchBoxDbIdValue;

    public void SetResolver(Func<string, IGame> r) => _resolve = r;

    public override string GameId { get => GameIdValue ?? ""; set { } }
    public override string GameTitle { get => GameTitleValue ?? ""; set { } }
    public override string GamePlatform { get => GamePlatformValue ?? ""; set { } }
    public override string GameFileName { get => GameFileNameValue ?? ""; set { } }
    public override string PlaylistId { get => PlaylistIdValue ?? ""; set { } }
    public override int ManualOrder { get => ManualOrderValue; set { } }
    public override Nullable<int> LaunchBoxDbId { get => LaunchBoxDbIdValue; set { } }
    public override IGame GetActualGame() => _resolve?.Invoke(GameIdValue);
}

internal sealed class HostPlaylist : DummyPlaylist
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private readonly List<HostPlaylistGame> _games = new();
    private readonly List<PlaylistFilterDef> _filters = new();
    private Func<string, IGame> _resolve;
    private Func<IEnumerable<IGame>> _allGames;

    public string PlaylistIdValue, NameValue, NestedNameValue, NotesValue, SortByValue, CategoryValue;
    public bool AutoPopulateValue, IncludeWithPlatformsValue;

    public void Add(HostPlaylistGame g) => _games.Add(g);
    public void AddFilter(PlaylistFilterDef f) => _filters.Add(f);

    public void SetResolver(Func<string, IGame> r)
    {
        _resolve = r;
        foreach (var g in _games) g.SetResolver(r);
    }
    public void SetAllGamesProvider(Func<IEnumerable<IGame>> p) => _allGames = p;

    public override string PlaylistId { get => PlaylistIdValue ?? ""; set { } }
    public override string Name { get => NameValue ?? ""; set { } }
    public override string NestedName { get => NestedNameValue ?? ""; set { } }
    public override string Notes { get => NotesValue ?? ""; set { } }
    public override string SortBy { get => SortByValue ?? ""; set { } }
    public override string Category { get => CategoryValue ?? ""; set { } }
    public override bool AutoPopulate { get => AutoPopulateValue; set { } }
    public override bool IncludeWithPlatforms { get => IncludeWithPlatformsValue; set { } }

    public override IPlaylistGame[] GetAllPlaylistGames() => _games.Cast<IPlaylistGame>().ToArray();

    public override IGame[] GetAllGames(bool sort)
    {
        // AutoPopulate: evaluate the filter rules over every game (AND of all rules).
        if (AutoPopulateValue && _filters.Count > 0 && _allGames != null)
            return _allGames().Where(MatchesAllFilters).ToArray();

        // Manual: resolve the explicit <PlaylistGame> entries.
        return _games.Select(pg => _resolve?.Invoke(pg.GameIdValue)).Where(g => g != null).ToArray();
    }

    public override int GetGameCount(bool includeHidden, bool includeBroken) => GetAllGames(false).Length;
    public override bool HasGames(bool includeHidden, bool includeBroken) => GetAllGames(false).Length > 0;

    // ── Filter evaluation ────────────────────────────────────────────────────
    private bool MatchesAllFilters(IGame g)
    {
        foreach (var f in _filters)
            if (!Compare(Field(g, f.FieldKey), f.ComparisonTypeKey, f.Value)) return false;
        return true;
    }

    private static string Field(IGame g, string key)
    {
        try
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "platform": return g.Platform;
                case "title": case "name": return g.Title;
                case "sorttitle": return g.SortTitle;
                case "developer": return g.Developer;
                case "publisher": return g.Publisher;
                case "genre": case "genres": return g.GenresString;
                case "region": return g.Region;
                case "playmode": return g.PlayMode;
                case "rating": return g.Rating;
                case "status": return g.Status;
                case "version": return g.Version;
                case "releaseyear": return g.ReleaseYear?.ToString(CultureInfo.InvariantCulture);
                case "favorite": return g.Favorite ? "true" : "false";
                case "completed": return g.Completed ? "true" : "false";
                case "playcount": case "played": return g.PlayCount.ToString(CultureInfo.InvariantCulture);
                default: return null;
            }
        }
        catch { return null; }
    }

    private static bool Compare(string field, string cmpKey, string target)
    {
        field ??= ""; target ??= "";
        switch ((cmpKey ?? "").ToLowerInvariant())
        {
            case "equalto": return string.Equals(field, target, OIC);
            case "notequalto": return !string.Equals(field, target, OIC);
            case "contains": return field.IndexOf(target, OIC) >= 0;
            case "doesnotcontain": return field.IndexOf(target, OIC) < 0;
            case "startswith": return field.StartsWith(target, OIC);
            case "endswith": return field.EndsWith(target, OIC);
            case "greaterthan":
                return double.TryParse(field, out var a1) && double.TryParse(target, out var b1)
                    ? a1 > b1 : string.Compare(field, target, OIC) > 0;
            case "lessthan":
                return double.TryParse(field, out var a2) && double.TryParse(target, out var b2)
                    ? a2 < b2 : string.Compare(field, target, OIC) < 0;
            default: return false;
        }
    }
}

internal static class PlaylistCatalog
{
    public static List<HostPlaylist> Load(string dataDir)
    {
        var result = new List<HostPlaylist>();
        string dir = Path.Combine(dataDir, "Playlists");
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
        {
            XDocument doc;
            try { doc = XDocument.Load(file); } catch { continue; }
            var root = doc.Root;
            var pe = root?.Element("Playlist");
            if (pe == null) continue;

            var pl = new HostPlaylist
            {
                PlaylistIdValue = (string)pe.Element("PlaylistId"),
                NameValue = (string)pe.Element("Name"),
                NestedNameValue = (string)pe.Element("NestedName"),
                NotesValue = (string)pe.Element("Notes"),
                SortByValue = (string)pe.Element("SortBy"),
                CategoryValue = (string)pe.Element("Category"),
                AutoPopulateValue = ((string)pe.Element("AutoPopulate") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                IncludeWithPlatformsValue = ((string)pe.Element("IncludeWithPlatforms") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
            };

            foreach (var pge in root.Elements("PlaylistGame"))
            {
                pl.Add(new HostPlaylistGame
                {
                    GameIdValue = (string)pge.Element("GameId"),
                    GameTitleValue = (string)pge.Element("GameTitle"),
                    GamePlatformValue = (string)pge.Element("GamePlatform"),
                    GameFileNameValue = (string)pge.Element("GameFileName"),
                    PlaylistIdValue = pl.PlaylistIdValue,
                    ManualOrderValue = int.TryParse((string)pge.Element("ManualOrder"), out var mo) ? mo : 0,
                    LaunchBoxDbIdValue = int.TryParse((string)pge.Element("LaunchBoxDbId"), out var db) ? db : (int?)null,
                });
            }

            foreach (var pfe in root.Elements("PlaylistFilter"))
            {
                pl.AddFilter(new PlaylistFilterDef(
                    (string)pfe.Element("FieldKey"),
                    (string)pfe.Element("ComparisonTypeKey"),
                    (string)pfe.Element("Value")));
            }

            result.Add(pl);
        }
        return result;
    }
}
