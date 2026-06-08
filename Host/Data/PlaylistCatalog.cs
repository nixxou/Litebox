// Loads LB\Data\Playlists\*.xml: one <Playlist> per file, with EITHER explicit
// <PlaylistGame> entries (manual playlist) OR <PlaylistFilter> rules evaluated
// over every game (AutoPopulate playlist). Manual games are resolved lazily via a
// Func<string,IGame>; auto playlists pull the full game list via a provider — both
// injected by the DataManager. Setters route through GameStore's op-log (each op
// carries the playlist's source file in ParentId); games/filters use the "replace"
// collection pattern.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Data;

/// <summary>A single auto-populate rule (mutable so plugin edits round-trip).</summary>
internal sealed class PlaylistFilterDef
{
    public string FieldKey, ComparisonTypeKey, Value;
    public PlaylistFilterDef(string fieldKey, string comparisonTypeKey, string value)
    { FieldKey = fieldKey; ComparisonTypeKey = comparisonTypeKey; Value = value; }
}

internal sealed class HostPlaylistFilter : DummyPlaylistFilter
{
    private readonly PlaylistFilterDef _f;
    private readonly HostPlaylist _owner;
    public HostPlaylistFilter(PlaylistFilterDef f, HostPlaylist owner) { _f = f; _owner = owner; }

    public override string PlaylistId { get => _owner?.PlaylistIdValue ?? ""; set { } }
    public override string Value { get => _f.Value ?? ""; set { _f.Value = value; _owner?.RecordFilters(); } }
    public override string FieldKey { get => _f.FieldKey ?? ""; set { _f.FieldKey = value; _owner?.RecordFilters(); } }
    public override string ComparisonTypeKey { get => _f.ComparisonTypeKey ?? ""; set { _f.ComparisonTypeKey = value; _owner?.RecordFilters(); } }
    public override bool GetMatches(IGame game) => HostPlaylist.Match(game, _f);
}

internal sealed class HostPlaylistGame : DummyPlaylistGame
{
    private Func<string, IGame> _resolve;
    private HostPlaylist _owner;
    public string GameIdValue, GameTitleValue, GamePlatformValue, GameFileNameValue, PlaylistIdValue;
    public int ManualOrderValue;
    public int? LaunchBoxDbIdValue;

    public void SetResolver(Func<string, IGame> r) => _resolve = r;
    internal void SetOwner(HostPlaylist o) => _owner = o;

    public override string GameId { get => GameIdValue ?? ""; set { GameIdValue = value; _owner?.RecordGames(); } }
    public override string GameTitle { get => GameTitleValue ?? ""; set { GameTitleValue = value; _owner?.RecordGames(); } }
    public override string GamePlatform { get => GamePlatformValue ?? ""; set { GamePlatformValue = value; _owner?.RecordGames(); } }
    public override string GameFileName { get => GameFileNameValue ?? ""; set { GameFileNameValue = value; _owner?.RecordGames(); } }
    public override string PlaylistId { get => PlaylistIdValue ?? ""; set { } }
    public override int ManualOrder { get => ManualOrderValue; set { ManualOrderValue = value; _owner?.RecordGames(); } }
    public override Nullable<int> LaunchBoxDbId { get => LaunchBoxDbIdValue; set { LaunchBoxDbIdValue = value; _owner?.RecordGames(); } }
    public override IGame GetActualGame() => _resolve?.Invoke(GameIdValue);
}

internal sealed class HostPlaylist : DummyPlaylist, ILiteBoxFields
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    internal static readonly HashSet<string> Modeled = new(StringComparer.Ordinal)
    {
        "PlaylistId", "Name", "NestedName", "Notes", "SortBy", "Category", "VideoPath", "ImageType", "SortTitle",
        "LastGameId", "BigBoxView", "BigBoxTheme", "AutoPopulate", "IncludeWithPlatforms", "HideInBigBox",
    };

    private readonly List<HostPlaylistGame> _games = new();
    private readonly List<PlaylistFilterDef> _filters = new();
    private Func<string, IGame> _resolve;
    private Func<IEnumerable<IGame>> _allGames;
    private GameStore _store;
    private Dictionary<string, string> _extra;
    internal void SetExtra(Dictionary<string, string> e) => _extra = e;

    // ── ILiteBoxFields: read/write playlist fields the SDK IPlaylist doesn't expose ──
    public string GetField(string xmlElementName) => _extra != null && _extra.TryGetValue(xmlElementName, out var v) ? (v ?? "") : "";
    public void SetField(string xmlElementName, string value)
    {
        if (string.IsNullOrEmpty(xmlElementName)) return;
        if (string.IsNullOrEmpty(value)) _extra?.Remove(xmlElementName);
        else (_extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[xmlElementName] = value;
        Rec(xmlElementName, value);
    }
    public IReadOnlyCollection<string> ExtraFieldNames => _extra != null ? (IReadOnlyCollection<string>)_extra.Keys : Array.Empty<string>();

    public string PlaylistIdValue, NameValue, NestedNameValue, NotesValue, SortByValue, CategoryValue,
                  VideoPathValue, ImageTypeValue, SortTitleValue, LastGameIdValue, BigBoxViewValue, BigBoxThemeValue;
    public bool AutoPopulateValue, IncludeWithPlatformsValue, HideInBigBoxValue;
    public string ImagesRootValue;   // <LB>\Images, for playlist images
    public string FileValue;         // source xml (one playlist per file) — carried in every op

    public void Add(HostPlaylistGame g) { g.SetOwner(this); _games.Add(g); }
    public void AddFilter(PlaylistFilterDef f) => _filters.Add(f);
    internal void Attach(GameStore s) => _store = s;
    private void Rec(string field, string value) => _store?.RecordPlaylistModify(PlaylistIdValue, FileValue, field, value);

    internal void RecordGames()
        => _store?.RecordPlaylistChildReplace("PlaylistGame", PlaylistIdValue, FileValue, JsonSerializer.Serialize(
            _games.Select(g => new Dictionary<string, string>
            {
                ["GameId"] = g.GameIdValue, ["LaunchBoxDbId"] = g.LaunchBoxDbIdValue?.ToString(CultureInfo.InvariantCulture),
                ["GameTitle"] = g.GameTitleValue, ["GameFileName"] = g.GameFileNameValue, ["GamePlatform"] = g.GamePlatformValue,
                ["ManualOrder"] = g.ManualOrderValue.ToString(CultureInfo.InvariantCulture),
            }).ToList()));
    internal void RecordFilters()
        => _store?.RecordPlaylistChildReplace("PlaylistFilter", PlaylistIdValue, FileValue, JsonSerializer.Serialize(
            _filters.Select(f => new Dictionary<string, string> { ["Value"] = f.Value, ["FieldKey"] = f.FieldKey, ["ComparisonTypeKey"] = f.ComparisonTypeKey }).ToList()));

    public void SetResolver(Func<string, IGame> r) { _resolve = r; foreach (var g in _games) g.SetResolver(r); }
    public void SetAllGamesProvider(Func<IEnumerable<IGame>> p) => _allGames = p;

    public override string PlaylistId { get => PlaylistIdValue ?? ""; set { } }
    public override string Name { get => NameValue ?? ""; set { NameValue = value; Rec("Name", value); } }
    public override string NestedName { get => NestedNameValue ?? ""; set { NestedNameValue = value; Rec("NestedName", value); } }
    public override string Notes { get => NotesValue ?? ""; set { NotesValue = value; Rec("Notes", value); } }
    public override string SortBy { get => SortByValue ?? ""; set { SortByValue = value; Rec("SortBy", value); } }
    public override string Category { get => CategoryValue ?? ""; set { CategoryValue = value; Rec("Category", value); } }
    public override string VideoPath { get => VideoPathValue ?? ""; set { VideoPathValue = value; Rec("VideoPath", value); } }
    public override string ImageType { get => ImageTypeValue ?? ""; set { ImageTypeValue = value; Rec("ImageType", value); } }
    public override string SortTitle { get => SortTitleValue ?? ""; set { SortTitleValue = value; Rec("SortTitle", value); } }
    public override string LastGameId { get => LastGameIdValue ?? ""; set { LastGameIdValue = value; Rec("LastGameId", value); } }
    public override string BigBoxView { get => BigBoxViewValue ?? ""; set { BigBoxViewValue = value; Rec("BigBoxView", value); } }
    public override string BigBoxTheme { get => BigBoxThemeValue ?? ""; set { BigBoxThemeValue = value; Rec("BigBoxTheme", value); } }
    public override bool AutoPopulate { get => AutoPopulateValue; set { AutoPopulateValue = value; Rec("AutoPopulate", value ? "true" : "false"); } }
    public override bool IncludeWithPlatforms { get => IncludeWithPlatformsValue; set { IncludeWithPlatformsValue = value; Rec("IncludeWithPlatforms", value ? "true" : "false"); } }
    public override bool HideInBigBox { get => HideInBigBoxValue; set { HideInBigBoxValue = value; Rec("HideInBigBox", value ? "true" : "false"); } }

    public override IPlaylistGame[] GetAllPlaylistGames() => _games.Cast<IPlaylistGame>().ToArray();
    public override IPlaylistGame AddNewPlaylistGame()
    {
        var g = new HostPlaylistGame { PlaylistIdValue = PlaylistIdValue };
        g.SetResolver(_resolve);
        Add(g);
        RecordGames();
        return g;
    }
    public override bool TryRemovePlaylistGame(IPlaylistGame playlistGame)
    {
        int n = _games.RemoveAll(x => x.GameIdValue == playlistGame?.GameId);
        if (n > 0) RecordGames();
        return n > 0;
    }

    public override IPlaylistFilter[] GetAllPlaylistFilters()
        => _filters.Select(f => (IPlaylistFilter)new HostPlaylistFilter(f, this)).ToArray();
    public override IPlaylistFilter AddNewPlaylistFilter()
    {
        var f = new PlaylistFilterDef(null, null, null);
        _filters.Add(f);
        RecordFilters();
        return new HostPlaylistFilter(f, this);
    }
    public override bool TryRemovePlaylistFilter(IPlaylistFilter playlistFilter)
    {
        int n = _filters.RemoveAll(x => x.FieldKey == playlistFilter?.FieldKey && x.Value == playlistFilter?.Value && x.ComparisonTypeKey == playlistFilter?.ComparisonTypeKey);
        if (n > 0) RecordFilters();
        return n > 0;
    }

    // ── Images (Images\Playlists\<name>\<type>\<name>.ext) ────────────────────
    public override string ClearLogoImagePath => Img("Clear Logo");
    public override string BannerImagePath => Img("Banner");
    public override string BackgroundImagePath => Img("Fanart");
    public override string DeviceImagePath => Img("Device");
    public override string DefaultBoxImagePath => Img("Default Box");
    public override string Default3DBoxImagePath => Img("Default 3D Box");
    public override string DefaultCartImagePath => Img("Default Cart");
    public override string Default3DCartImagePath => Img("Default 3D Cart");
    private string Img(string type) => MediaResolver.NamedImage(ImagesRootValue, "Playlists", NameValue, type);

    public override IGame[] GetAllGames(bool sort)
    {
        if (AutoPopulateValue && _filters.Count > 0 && _allGames != null)
            return _allGames().Where(MatchesAllFilters).ToArray();
        return _games.Select(pg => _resolve?.Invoke(pg.GameIdValue)).Where(g => g != null).ToArray();
    }

    public override int GetGameCount(bool includeHidden, bool includeBroken) => GetAllGames(false).Length;
    public override bool HasGames(bool includeHidden, bool includeBroken) => GetAllGames(false).Length > 0;

    private bool MatchesAllFilters(IGame g)
    {
        foreach (var f in _filters)
            if (!Match(g, f)) return false;
        return true;
    }

    internal static bool Match(IGame g, PlaylistFilterDef f) => Compare(Field(g, f.FieldKey), f.ComparisonTypeKey, f.Value);

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
    public static List<HostPlaylist> Load(string dataDir, string imagesRoot)
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
                VideoPathValue = (string)pe.Element("VideoPath"),
                ImageTypeValue = (string)pe.Element("ImageType"),
                SortTitleValue = (string)pe.Element("SortTitle"),
                LastGameIdValue = (string)pe.Element("LastGameId"),
                BigBoxViewValue = (string)pe.Element("BigBoxView"),
                BigBoxThemeValue = (string)pe.Element("BigBoxTheme"),
                AutoPopulateValue = ((string)pe.Element("AutoPopulate") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                IncludeWithPlatformsValue = ((string)pe.Element("IncludeWithPlatforms") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                HideInBigBoxValue = ((string)pe.Element("HideInBigBox") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                ImagesRootValue = imagesRoot,
                FileValue = file,
            };

            Dictionary<string, string> plex = null;
            foreach (var pce in pe.Elements())
            {
                string n = pce.Name.LocalName;
                if (HostPlaylist.Modeled.Contains(n)) continue;
                string val = pce.Value;
                if (string.IsNullOrEmpty(val)) continue;
                (plex ??= new Dictionary<string, string>(StringComparer.Ordinal))[n] = val;
            }
            if (plex != null) pl.SetExtra(plex);

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
                pl.AddFilter(new PlaylistFilterDef(
                    (string)pfe.Element("FieldKey"), (string)pfe.Element("ComparisonTypeKey"), (string)pfe.Element("Value")));

            result.Add(pl);
        }
        return result;
    }
}
