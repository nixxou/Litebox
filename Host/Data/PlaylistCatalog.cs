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
internal sealed class PlaylistFilterDef : PlaylistFilterDefLike
{
    public string FieldKey, ComparisonTypeKey, Value;
    public Dictionary<string, string> Extra;   // fields LiteBox does not model — see ChildExtras
    public PlaylistFilterDef(string fieldKey, string comparisonTypeKey, string value)
    { FieldKey = fieldKey; ComparisonTypeKey = comparisonTypeKey; Value = value; }

    string PlaylistFilterDefLike.FieldKey => FieldKey;
    string PlaylistFilterDefLike.ComparisonTypeKey => ComparisonTypeKey;
    string PlaylistFilterDefLike.Value => Value;
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
    public Dictionary<string, string> Extra;   // fields LiteBox does not model — see ChildExtras

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
        // No BigBox sort-override entry: no such element appears in any real playlist file, so it is
        // not modelled. Should a LaunchBox version write one, ExtraFields carries it through the
        // round-trip untouched — which is what an unverified field deserves.
        "LastGameId", "BigBoxView", "BigBoxTheme",
        "AutoPopulate", "IncludeWithPlatforms", "HideInBigBox",
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
    public void AddFilter(PlaylistFilterDef f) { _filters.Add(f); InvalidateFilterPlan(); }

    /// <summary>Les règles et les jeux TELS QU'ILS SONT STOCKÉS, pour qui doit les recopier fidèlement :
    /// GetAllPlaylistFilters/GetAllPlaylistGames emballent dans des wrappers SDK qui n'exposent pas le
    /// dictionnaire Extra (les champs que LiteBox ne modélise pas), et ReplaceFilters le perd. Lecture
    /// seule : muter ces instances muterait la playlist.</summary>
    internal IReadOnlyList<PlaylistFilterDef> FiltersRaw => _filters;
    internal IReadOnlyList<HostPlaylistGame> GamesRaw => _games;
    internal void Attach(GameStore s) => _store = s;
    private void Rec(string field, string value) => _store?.RecordPlaylistModify(PlaylistIdValue, FileValue, field, value);

    private static readonly Dictionary<string, string> EmptyExtra = new(StringComparer.Ordinal);

    internal void RecordGames()
        => _store?.RecordPlaylistChildReplace("PlaylistGame", PlaylistIdValue, FileValue, JsonSerializer.Serialize(
            _games.Select(g => new Dictionary<string, string>(g.Extra ?? EmptyExtra, StringComparer.Ordinal)
            {
                ["GameId"] = g.GameIdValue, ["LaunchBoxDbId"] = g.LaunchBoxDbIdValue?.ToString(CultureInfo.InvariantCulture),
                ["GameTitle"] = g.GameTitleValue, ["GameFileName"] = g.GameFileNameValue, ["GamePlatform"] = g.GamePlatformValue,
                ["ManualOrder"] = g.ManualOrderValue.ToString(CultureInfo.InvariantCulture),
            }).ToList()));
    internal void RecordFilters()
    {
        InvalidateFilterPlan();
        _store?.RecordPlaylistChildReplace("PlaylistFilter", PlaylistIdValue, FileValue, JsonSerializer.Serialize(
            _filters.Select(f => new Dictionary<string, string>(f.Extra ?? EmptyExtra, StringComparer.Ordinal)
                { ["Value"] = f.Value, ["FieldKey"] = f.FieldKey, ["ComparisonTypeKey"] = f.ComparisonTypeKey }).ToList()));
    }

    /// <summary>Atomically replaces the editable filter grid.  The public SDK mutators journal once per
    /// property, which is useful to plugins but needlessly emits several intermediate collections when the
    /// playlist editor applies a whole grid.</summary>
    internal void ReplaceFilters(IEnumerable<PlaylistFilterDef> filters)
    {
        var next = (filters ?? Enumerable.Empty<PlaylistFilterDef>())
            .Where(f => f != null && !string.IsNullOrWhiteSpace(f.FieldKey))
            .Select(f => new PlaylistFilterDef(f.FieldKey?.Trim(), f.ComparisonTypeKey?.Trim(), f.Value ?? ""))
            .ToList();
        bool changed = next.Count != _filters.Count;
        if (!changed)
            for (int i = 0; i < next.Count && !changed; i++)
                changed = !string.Equals(next[i].FieldKey, _filters[i].FieldKey, StringComparison.Ordinal)
                          || !string.Equals(next[i].ComparisonTypeKey, _filters[i].ComparisonTypeKey, StringComparison.Ordinal)
                          || !string.Equals(next[i].Value, _filters[i].Value, StringComparison.Ordinal);
        if (!changed) return;
        _filters.Clear();
        _filters.AddRange(next);
        RecordFilters();
    }

    /// <summary>Applies the manual Games-tab order/removals as one collection replacement. ManualOrder is
    /// LaunchBox's zero-based display order; cached title/platform/file metadata is preserved for missing games.</summary>
    internal void ReplaceGames(IEnumerable<HostPlaylistGame> games)
    {
        var next = (games ?? Enumerable.Empty<HostPlaylistGame>()).Where(g => g != null).Distinct().ToList();
        bool changed = next.Count != _games.Count;
        if (!changed)
            for (int i = 0; i < next.Count && !changed; i++)
                changed = !ReferenceEquals(next[i], _games[i]);
        if (!changed) return;
        _games.Clear();
        for (int i = 0; i < next.Count; i++)
        {
            var g = next[i];
            g.ManualOrderValue = i;
            g.PlaylistIdValue = PlaylistIdValue;
            g.SetOwner(this);
            g.SetResolver(_resolve);
            _games.Add(g);
        }
        RecordGames();
    }

    public void SetResolver(Func<string, IGame> r) { _resolve = r; foreach (var g in _games) g.SetResolver(r); }
    public void SetAllGamesProvider(Func<IEnumerable<IGame>> p) => _allGames = p;

    // ── Boot-overlay appliers: re-apply PENDING journal ops without re-recording them ──────────
    private static readonly HashSet<string> _gameRowModeled = new(StringComparer.Ordinal)
    { "GameId", "LaunchBoxDbId", "GameTitle", "GameFileName", "GamePlatform", "ManualOrder" };

    /// <summary>Set a field from a pending op silently (mirrors the Rec'ing setters).</summary>
    internal void ApplyFieldSilent(string field, string value)
    {
        if (string.IsNullOrEmpty(field)) return;
        switch (field)
        {
            case "Name": NameValue = value; break;
            case "NestedName": NestedNameValue = value; break;
            case "Notes": NotesValue = value; break;
            case "SortBy": SortByValue = value; break;
            case "Category": CategoryValue = value; break;
            case "VideoPath": VideoPathValue = value; break;
            case "ImageType": ImageTypeValue = value; break;
            case "SortTitle": SortTitleValue = value; break;
            case "LastGameId": LastGameIdValue = value; break;
            case "BigBoxView": BigBoxViewValue = value; break;
            case "BigBoxTheme": BigBoxThemeValue = value; break;
            case "AutoPopulate": AutoPopulateValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
            case "IncludeWithPlatforms": IncludeWithPlatformsValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
            case "HideInBigBox": HideInBigBoxValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
            default:
                if (string.IsNullOrEmpty(value)) _extra?.Remove(field);
                else (_extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[field] = value;
                break;
        }
    }

    /// <summary>Apply a pending PlaylistGame "replace" op (whole collection, JSON of field maps —
    /// the exact shape <see cref="RecordGames"/> serialises).</summary>
    internal void ReplaceGamesSilent(string json)
    {
        List<Dictionary<string, string>> maps;
        try { maps = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        _games.Clear();
        foreach (var m in maps)
        {
            string G(string k) => m.TryGetValue(k, out var v) ? v : null;
            Dictionary<string, string> extra = null;
            foreach (var kv in m)
                if (!_gameRowModeled.Contains(kv.Key))
                    (extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[kv.Key] = kv.Value;
            var g = new HostPlaylistGame
            {
                GameIdValue = G("GameId"),
                GameTitleValue = G("GameTitle"),
                GamePlatformValue = G("GamePlatform"),
                GameFileNameValue = G("GameFileName"),
                PlaylistIdValue = PlaylistIdValue,
                ManualOrderValue = int.TryParse(G("ManualOrder"), out var mo) ? mo : 0,
                LaunchBoxDbIdValue = int.TryParse(G("LaunchBoxDbId"), out var db) ? db : (int?)null,
                Extra = extra,
            };
            g.SetOwner(this);
            g.SetResolver(_resolve);
            _games.Add(g);
        }
    }

    /// <summary>Apply a pending PlaylistFilter "replace" op (the shape <see cref="RecordFilters"/> writes).</summary>
    internal void ReplaceFiltersSilent(string json)
    {
        List<Dictionary<string, string>> maps;
        try { maps = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        _filters.Clear();
        foreach (var m in maps)
        {
            string G(string k) => m.TryGetValue(k, out var v) ? v : null;
            Dictionary<string, string> extra = null;
            foreach (var kv in m)
                if (kv.Key != "Value" && kv.Key != "FieldKey" && kv.Key != "ComparisonTypeKey")
                    (extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[kv.Key] = kv.Value;
            _filters.Add(new PlaylistFilterDef(G("FieldKey"), G("ComparisonTypeKey"), G("Value")) { Extra = extra });
        }
        InvalidateFilterPlan();
    }

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
        if (n > 0)
        {
            for (int i = 0; i < _games.Count; i++) _games[i].ManualOrderValue = i;
            RecordGames();
        }
        return n > 0;
    }
    public override void ClearGames()
    {
        if (_games.Count == 0) return;
        _games.Clear();
        RecordGames();
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
    // Own image first, then the media packs — see the twin in PlatformCatalog. The pack key is the NESTED
    // name when there is one: packs file a nested playlist under its short name (Playlists-Player Games.png),
    // never the unique one it carries in the sidebar.
    private string Img(string type)
    {
        var own = MediaResolver.NamedImage(ImagesRootValue, "Playlists", NameValue, type);
        if (!string.IsNullOrEmpty(own)) return own;
        var l = MediaResolver.EntityTypeImages(ImagesRootValue, "Playlists", NameValue,
                                               string.IsNullOrWhiteSpace(NestedNameValue) ? NameValue : NestedNameValue, type);
        return l.Count > 0 ? l[0].path : "";
    }

    public override IGame[] GetAllGames(bool sort)
    {
        // An auto-populate playlist whose rules cannot be resolved — none written yet, or every one
        // of them naming a field LiteBox has no equivalent for — falls back to the stored
        // <PlaylistGame> rows instead of showing nothing. A playlist that LaunchBox fills must not
        // look empty here just because we could not read its recipe.
        if (AutoPopulateValue && _allGames != null)
        {
            var plan = FilterPlan();
            if (plan.HasEvaluableGroup) return _allGames().Where(plan.Matches).ToArray();
        }
        return _games.OrderBy(pg => pg.ManualOrderValue).Select(pg => _resolve?.Invoke(pg.GameIdValue)).Where(g => g != null).ToArray();
    }

    // Compiled once per rule set, not once per game — and reused across calls, since a category
    // load asks several playlists for their games in a row.
    private PlaylistFilterPlan _plan;

    private PlaylistFilterPlan FilterPlan() => _plan ??= PlaylistFilterPlan.Compile(_filters);

    /// <summary>Called whenever the rules change, so the next evaluation recompiles.</summary>
    private void InvalidateFilterPlan() => _plan = null;

    public override int GetGameCount(bool includeHidden, bool includeBroken) => GetAllGames(false).Length;
    public override bool HasGames(bool includeHidden, bool includeBroken) => GetAllGames(false).Length > 0;

    /// <summary>Different fields are ANDed. Repeated rules on ONE field combine by their nature:
    ///
    ///   • POSITIVE rules (Equal To, Contains, Starts With, Has At Least One Of…) are ORed. This is
    ///     what makes the stock playlists work — Arcade Beat Em Ups is Platform=Arcade AND
    ///     (Genre contains "Fighter / 2D" OR "Fighter / 2.5D" OR …); ANDing them yields nothing.
    ///   • NEGATIVE and ORDERING rules (Is Not Equal To, Doesn't Contain, Is Greater/Less Than,
    ///     Is Empty…) are ANDed. ORing them would break the obvious reading of a range:
    ///     "Play Count Is Greater Than 5" + "Play Count Is Less Than 20" must be an interval,
    ///     and ORing two exclusions never excludes anything.
    ///
    /// A rule LiteBox cannot evaluate (Steam achievements, controller support…) is SKIPPED rather
    /// than failed: an unsupported field must not silently empty a playlist that LaunchBox fills.</summary>
    internal static bool MatchesFilters(IGame g, IEnumerable<PlaylistFilterDef> filters)
        => PlaylistFilterPlan.Compile(filters).Matches(g);

    internal static bool Match(IGame g, PlaylistFilterDef f)
        => f != null && PlaylistFilterPlan.Compile(new[] { f }).Matches(g);
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
                    Extra = ChildExtras.Capture(pge, "PlaylistGame"),
                });
            }

            foreach (var pfe in root.Elements("PlaylistFilter"))
                pl.AddFilter(new PlaylistFilterDef(
                    (string)pfe.Element("FieldKey"), (string)pfe.Element("ComparisonTypeKey"), (string)pfe.Element("Value"))
                { Extra = ChildExtras.Capture(pfe, "PlaylistFilter") });

            result.Add(pl);
        }
        return result;
    }
}
