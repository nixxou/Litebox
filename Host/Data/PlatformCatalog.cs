// Loads LB\Data\Platforms.xml: platform definitions (metadata), their custom
// media folders (<PlatformFolder>) and platform categories. Few entities
// (hundreds), so plain objects with full fidelity — only Games need the compact
// store. The PlatformFolder map is what makes custom image paths (e.g. MS-DOS
// "Box - Front" -> Images\MS-DOS\Front) resolve correctly via the API.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Data;

internal sealed class HostPlatform : DummyPlatform, ILiteBoxFields
{
    internal static readonly HashSet<string> Modeled = new(StringComparer.Ordinal)
    {
        "Name", "Developer", "Manufacturer", "Notes", "Category", "Cpu", "Memory", "Graphics", "Sound", "Display",
        "Media", "MaxControllers", "ScrapeAs", "SortTitle", "NestedName", "LastGameId", "ImageType", "VideoPath",
        "BigBoxTheme", "BigBoxView", "ReleaseDate", "HideInBigBox", "Folder", "FrontImagesFolder", "BackImagesFolder",
        "ClearLogoImagesFolder", "FanartImagesFolder", "ScreenshotImagesFolder", "BannerImagesFolder",
        "SteamBannerImagesFolder", "ManualsFolder", "MusicFolder", "VideosFolder",
    };
    private readonly string _name;
    private readonly Dictionary<string, string> _folders; // MediaType -> absolute FolderPath
    private readonly string _imagesRoot;
    private IGame[] _games = Array.Empty<IGame>();
    private Dictionary<string, string> _extra;            // non-modelled <Platform> fields
    internal void SetExtra(Dictionary<string, string> e) => _extra = e;

    // ── ILiteBoxFields: read/write the platform fields the SDK IPlatform doesn't expose ──
    public string GetField(string xmlElementName) => _extra != null && _extra.TryGetValue(xmlElementName, out var v) ? (v ?? "") : "";
    public void SetField(string xmlElementName, string value)
    {
        if (string.IsNullOrEmpty(xmlElementName)) return;
        if (string.IsNullOrEmpty(value)) _extra?.Remove(xmlElementName);
        else (_extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[xmlElementName] = value;
        Rec(xmlElementName, value);
    }
    public IReadOnlyCollection<string> ExtraFieldNames => _extra != null ? (IReadOnlyCollection<string>)_extra.Keys : Array.Empty<string>();

    public HostPlatform(string name, Dictionary<string, string> folders, string imagesRoot)
    {
        _name = name;
        _folders = folders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _imagesRoot = imagesRoot;
    }

    public void SetGames(IGame[] games) => _games = games ?? Array.Empty<IGame>();

    // ── metadata (from Platforms.xml) — setters route through the op-log (keyed by Name) ──────
    private GameStore _store;
    internal void Attach(GameStore s) => _store = s;
    private void Rec(string field, string value) => _store?.RecordEntityModify("Platform", _name, field, value);

    public override string Name { get => _name; set { } }
    public string DeveloperValue, ManufacturerValue, NotesValue, CategoryValue,
                  CpuValue, MemoryValue, GraphicsValue, SoundValue, DisplayValue, MediaValue,
                  MaxControllersValue, ScrapeAsValue, SortTitleValue, NestedNameValue, LastGameIdValue,
                  ImageTypeValue, VideoPathValue, BigBoxThemeValue, BigBoxViewValue;
    public DateTime? ReleaseDateValue;
    public bool HideInBigBoxValue;
    // image-folder config fields
    public string FrontImagesFolderValue, BackImagesFolderValue, ClearLogoImagesFolderValue,
                  FanartImagesFolderValue, ScreenshotImagesFolderValue, BannerImagesFolderValue,
                  SteamBannerImagesFolderValue, ManualsFolderValue, MusicFolderValue, VideosFolderValue, FolderValue;

    public override string Developer { get => DeveloperValue ?? ""; set { DeveloperValue = value; Rec("Developer", value); } }
    public override string Manufacturer { get => ManufacturerValue ?? ""; set { ManufacturerValue = value; Rec("Manufacturer", value); } }
    public override string Notes { get => NotesValue ?? ""; set { NotesValue = value; Rec("Notes", value); } }
    public override string Category { get => CategoryValue ?? ""; set { CategoryValue = value; Rec("Category", value); } }
    public override string Cpu { get => CpuValue ?? ""; set { CpuValue = value; Rec("Cpu", value); } }
    public override string Memory { get => MemoryValue ?? ""; set { MemoryValue = value; Rec("Memory", value); } }
    public override string Graphics { get => GraphicsValue ?? ""; set { GraphicsValue = value; Rec("Graphics", value); } }
    public override string Sound { get => SoundValue ?? ""; set { SoundValue = value; Rec("Sound", value); } }
    public override string Display { get => DisplayValue ?? ""; set { DisplayValue = value; Rec("Display", value); } }
    public override string Media { get => MediaValue ?? ""; set { MediaValue = value; Rec("Media", value); } }
    public override string MaxControllers { get => MaxControllersValue ?? ""; set { MaxControllersValue = value; Rec("MaxControllers", value); } }
    public override string ScrapeAs { get => ScrapeAsValue ?? ""; set { ScrapeAsValue = value; Rec("ScrapeAs", value); } }
    public override string SortTitle { get => SortTitleValue ?? ""; set { SortTitleValue = value; Rec("SortTitle", value); } }
    public override string NestedName { get => NestedNameValue ?? ""; set { NestedNameValue = value; Rec("NestedName", value); } }
    public override string LastGameId { get => LastGameIdValue ?? ""; set { LastGameIdValue = value; Rec("LastGameId", value); } }
    public override string ImageType { get => ImageTypeValue ?? ""; set { ImageTypeValue = value; Rec("ImageType", value); } }
    public override string VideoPath { get => VideoPathValue ?? ""; set { VideoPathValue = value; Rec("VideoPath", value); } }
    public override string BigBoxTheme { get => BigBoxThemeValue ?? ""; set { BigBoxThemeValue = value; Rec("BigBoxTheme", value); } }
    public override string BigBoxView { get => BigBoxViewValue ?? ""; set { BigBoxViewValue = value; Rec("BigBoxView", value); } }
    public override Nullable<DateTime> ReleaseDate { get => ReleaseDateValue; set { ReleaseDateValue = value; Rec("ReleaseDate", value.HasValue ? value.Value.ToString("o", CultureInfo.InvariantCulture) : ""); } }
    public override bool HideInBigBox { get => HideInBigBoxValue; set { HideInBigBoxValue = value; Rec("HideInBigBox", value ? "true" : "false"); } }
    public override string Folder { get => FolderValue ?? ""; set { FolderValue = value; Rec("Folder", value); } }
    public override string FrontImagesFolder { get => FrontImagesFolderValue ?? ""; set { FrontImagesFolderValue = value; Rec("FrontImagesFolder", value); } }
    public override string BackImagesFolder { get => BackImagesFolderValue ?? ""; set { BackImagesFolderValue = value; Rec("BackImagesFolder", value); } }
    public override string ClearLogoImagesFolder { get => ClearLogoImagesFolderValue ?? ""; set { ClearLogoImagesFolderValue = value; Rec("ClearLogoImagesFolder", value); } }
    public override string FanartImagesFolder { get => FanartImagesFolderValue ?? ""; set { FanartImagesFolderValue = value; Rec("FanartImagesFolder", value); } }
    public override string ScreenshotImagesFolder { get => ScreenshotImagesFolderValue ?? ""; set { ScreenshotImagesFolderValue = value; Rec("ScreenshotImagesFolder", value); } }
    public override string BannerImagesFolder { get => BannerImagesFolderValue ?? ""; set { BannerImagesFolderValue = value; Rec("BannerImagesFolder", value); } }
    public override string SteamBannerImagesFolder { get => SteamBannerImagesFolderValue ?? ""; set { SteamBannerImagesFolderValue = value; Rec("SteamBannerImagesFolder", value); } }
    public override string ManualsFolder { get => ManualsFolderValue ?? ""; set { ManualsFolderValue = value; Rec("ManualsFolder", value); } }
    public override string MusicFolder { get => MusicFolderValue ?? ""; set { MusicFolderValue = value; Rec("MusicFolder", value); } }
    // Platforms.xml carries a media folder only when the user MOVED one — this library declares none, and
    // most installs are the same. LaunchBox answers with its convention path anyway; we answered with "",
    // which reads as "this platform has no videos". A plugin that locates media through the platform
    // rather than through the game therefore got nothing: ThirdScreen fell straight past Video Snap and
    // down to Banner, on games whose video sits exactly where the convention says it should.
    //
    // RELATIVE, like LaunchBox — the caller prefixes the LaunchBox root. That is visible in ThirdScreen's
    // own log, which prints "G:\LB1326/Videos\Nintendo Game Boy\Ring Rage-01.mp4": a root, a slash it
    // added itself, then the convention path. An absolute answer would have concatenated into nonsense.
    private string ConventionFolder(string root)
        => System.IO.Path.Combine(root, LbApiHost.Host.Media.MediaResolver.Sanitize(_name ?? ""));

    public override string VideosFolder
    {
        get => !string.IsNullOrEmpty(VideosFolderValue) ? VideosFolderValue
             : !string.IsNullOrWhiteSpace(VideoPathValue) ? VideoPathValue      // the per-platform override
             : ConventionFolder("Videos");
        set { VideosFolderValue = value; Rec("VideosFolder", value); }
    }

    /// <summary>Boot-overlay apply: set a field from a PENDING journal op without re-recording it
    /// (the op is already in the journal — going through the public setter would double it).
    /// Mirrors the Rec'ing setters above; unknown fields land in the extra dict like the loader's.</summary>
    internal void ApplyFieldSilent(string field, string value)
    {
        if (string.IsNullOrEmpty(field)) return;
        switch (field)
        {
            case "Developer": DeveloperValue = value; break;
            case "Manufacturer": ManufacturerValue = value; break;
            case "Notes": NotesValue = value; break;
            case "Category": CategoryValue = value; break;
            case "Cpu": CpuValue = value; break;
            case "Memory": MemoryValue = value; break;
            case "Graphics": GraphicsValue = value; break;
            case "Sound": SoundValue = value; break;
            case "Display": DisplayValue = value; break;
            case "Media": MediaValue = value; break;
            case "MaxControllers": MaxControllersValue = value; break;
            case "ScrapeAs": ScrapeAsValue = value; break;
            case "SortTitle": SortTitleValue = value; break;
            case "NestedName": NestedNameValue = value; break;
            case "LastGameId": LastGameIdValue = value; break;
            case "ImageType": ImageTypeValue = value; break;
            case "VideoPath": VideoPathValue = value; break;
            case "BigBoxTheme": BigBoxThemeValue = value; break;
            case "BigBoxView": BigBoxViewValue = value; break;
            case "ReleaseDate":
                ReleaseDateValue = DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;
                break;
            case "HideInBigBox": HideInBigBoxValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
            case "Folder": FolderValue = value; break;
            case "FrontImagesFolder": FrontImagesFolderValue = value; break;
            case "BackImagesFolder": BackImagesFolderValue = value; break;
            case "ClearLogoImagesFolder": ClearLogoImagesFolderValue = value; break;
            case "FanartImagesFolder": FanartImagesFolderValue = value; break;
            case "ScreenshotImagesFolder": ScreenshotImagesFolderValue = value; break;
            case "BannerImagesFolder": BannerImagesFolderValue = value; break;
            case "SteamBannerImagesFolder": SteamBannerImagesFolderValue = value; break;
            case "ManualsFolder": ManualsFolderValue = value; break;
            case "MusicFolder": MusicFolderValue = value; break;
            case "VideosFolder": VideosFolderValue = value; break;
            case "Name": break;   // renames are surgical, never journaled — and the name keys the ops
            default:
                if (string.IsNullOrEmpty(value)) _extra?.Remove(field);
                else (_extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[field] = value;
                break;
        }
    }

    // ── games ────────────────────────────────────────────────────────────────
    public override IGame[] GetAllGames(bool includeHidden, bool includeBroken)
        => Filtered(includeHidden, includeBroken).ToArray();
    public override int GetGameCount(bool includeHidden, bool includeBroken)
        => Filtered(includeHidden, includeBroken).Count();
    public override bool HasGames(bool includeHidden, bool includeBroken)
        => Filtered(includeHidden, includeBroken).Any();

    public override IGame[] GetAllGames(bool includeHidden, bool includeBroken,
        bool exVideo, bool exBoxFront, bool exScreenshot, bool exClearLogo, bool exBackground)
        => Filtered(includeHidden, includeBroken, exVideo, exBoxFront, exScreenshot, exClearLogo, exBackground).ToArray();
    public override int GetGameCount(bool includeHidden, bool includeBroken,
        bool exVideo, bool exBoxFront, bool exScreenshot, bool exClearLogo, bool exBackground)
        => Filtered(includeHidden, includeBroken, exVideo, exBoxFront, exScreenshot, exClearLogo, exBackground).Count();
    public override bool HasGames(bool includeHidden, bool includeBroken,
        bool exVideo, bool exBoxFront, bool exScreenshot, bool exClearLogo, bool exBackground)
        => Filtered(includeHidden, includeBroken, exVideo, exBoxFront, exScreenshot, exClearLogo, exBackground).Any();

    private IEnumerable<IGame> Filtered(bool includeHidden, bool includeBroken,
        bool exVideo = false, bool exBoxFront = false, bool exScreenshot = false,
        bool exClearLogo = false, bool exBackground = false)
    {
        IEnumerable<IGame> q = _games;
        if (!includeHidden) q = q.Where(g => !B(() => g.Hide));
        if (!includeBroken) q = q.Where(g => !B(() => g.Broken));
        // Media-presence excludes (resolve through the game's media accessors — IO or
        // the GameCache fast path). Only evaluated when the flag is set.
        if (exVideo)      q = q.Where(g => Has(() => g.GetVideoPath(false)));
        if (exBoxFront)   q = q.Where(g => Has(() => g.FrontImagePath));
        if (exScreenshot) q = q.Where(g => Has(() => g.ScreenshotImagePath));
        if (exClearLogo)  q = q.Where(g => Has(() => g.ClearLogoImagePath));
        if (exBackground) q = q.Where(g => Has(() => g.BackgroundImagePath));
        return q;
    }

    private static bool B(Func<bool> f) { try { return f(); } catch { return false; } }
    private static bool Has(Func<string> f) { try { return !string.IsNullOrEmpty(f()); } catch { return false; } }

    // ── media folders (custom paths honoured here) ───────────────────────────
    public override IPlatformFolder GetPlatformFolderByImageType(string imageType)
    {
        string path = _folders.TryGetValue(imageType, out var p) && !string.IsNullOrWhiteSpace(p)
            ? p
            : Path.Combine(_imagesRoot, Sanitize(_name), Sanitize(imageType)); // default convention
        return new DummyPlatformFolder { MediaType = imageType, Platform = _name, FolderPath = path };
    }

    public override IPlatformFolder[] GetAllPlatformFolders()
        => _folders.Select(kv => (IPlatformFolder)new DummyPlatformFolder
        { MediaType = kv.Key, Platform = _name, FolderPath = kv.Value }).ToArray();

    // ── 3D model settings, platform level (root <ModelSettings> keyed by <PlatformName>) ──────────
    // Not in GameStore: that one only reads Data\Platforms\*.xml and only captures blocks with a
    // parseable <GameId>, and this block's is empty by definition. So the platform object is its home,
    // exactly like the folder map — which is what lets the write be journalled: the 3D code reads from
    // here, so the edit shows immediately even though the XML lands later. HostPlatform is never
    // dropped at game launch, so unlike the per-game blocks there is no tier to reason about.
    private Dictionary<string, string> _modelSettings;

    /// <summary>The platform's stored ModelSettings (field → value), or null when it has no override.</summary>
    internal Dictionary<string, string> ModelSettings => _modelSettings;
    internal void SetModelSettings(Dictionary<string, string> fields)
        => _modelSettings = fields == null ? null : new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);

    // ── Platform documents (root <PlatformDocument> keyed by <Platform>), same story as the folders:
    // the tree's Documents submenu reads them, so they live here to make the journalled write visible.
    // Order matters — it is the grid's, and LaunchBox keeps it.
    private List<(string Name, string FilePath)> _documents;
    internal IReadOnlyList<(string Name, string FilePath)> Documents => _documents;
    internal void SetPlatformDocuments(List<(string Name, string FilePath)> docs)
        => _documents = docs == null ? null : new List<(string, string)>(docs);

    /// <summary>Replace this platform's custom media folders — mediaType → path as STORED (relative to
    /// the LB root, or absolute), resolved here the way <see cref="PlatformCatalog.Load"/> does.
    ///
    /// This map is what MediaResolver and the game cache answer from. Until now the editor wrote the
    /// XML and left it alone, so a folder changed in this session kept resolving to the OLD location
    /// until LiteBox restarted — media simply looked missing. Writing through here is what makes the
    /// journalled write visible immediately, which is the whole point of not writing the XML directly.
    /// <paramref name="lbRoot"/> anchors relative paths; empty leaves them as given.</summary>
    internal void SetPlatformFolders(IReadOnlyDictionary<string, string> stored, string lbRoot)
    {
        _folders.Clear();
        foreach (var kv in stored)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            string path = kv.Value;
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(lbRoot))
            { try { path = Path.GetFullPath(Path.Combine(lbRoot, path)); } catch { } }
            _folders[kv.Key] = path;
        }
    }

    // ── Tree children (Parents.xml): LB allows categories AND playlists to nest UNDER a platform ──
    private readonly List<object> _treeChildren = new();
    public void AddTreeChild(object c) { if (c != null) _treeChildren.Add(c); }
    public void ClearTreeChildren() => _treeChildren.Clear();
    public IReadOnlyList<object> TreeChildren => _treeChildren;
    // Sort Title s'il y en a un, sinon ce qui est LU : trier sur le nom unique rangerait
    // "2-Player Games" à la lettre A, sous un nom que personne ne voit.
    public void SortTreeChildren() => _treeChildren.Sort((a, b) => string.Compare(HostPlatformCategory.NodeSortKey(a), HostPlatformCategory.NodeSortKey(b), StringComparison.OrdinalIgnoreCase));
    // SDK tree: expose the nested nodes (playlists/categories under this platform) like real LB does.
    public override IList<IPlatform> GetChildren() => SdkTree.WrapChildren(_treeChildren);

    // ── Platform-level images (Images\Platforms\<name>\<type>\<name>.ext) ─────
    public override string ClearLogoImagePath => Img("Clear Logo");
    public override string BannerImagePath => Img("Banner");
    public override string BackgroundImagePath => Img("Fanart");
    public override string DeviceImagePath => Img("Device");
    public override string DefaultBoxImagePath => Img("Default Box");
    public override string GetNewPlatformLogoPath(string url)
        => MediaResolver.NewEntityFile("Images", "Platforms", _name, url, "Clear Logo", ".png");
    public override string GetNewPlatformVideoPath(string url)
        => MediaResolver.NewEntityFile("Videos", "Platforms", _name, url, null, ".mp4");


    // The entity's OWN video (its marquee / attract clip), not the videos of the games inside it. Answered
    // null until now, which a second-screen plugin reads as "this platform has no video" — ThirdScreen asks
    // for exactly this on its "Platform Video" and "Platform Marquee Video" entries.
    //
    // fallBackToGameVideos: when the entity has none, borrow one from a game inside it — that is what the
    // flag is for, and refusing to would make the parameter a lie. allowThemePath prefers the platform's
    // configured VideoPath override when it points somewhere real.
    public override string GetPlatformVideoPath(bool fallBackToGameVideos, bool allowThemePath)
    {
        try
        {
            if (allowThemePath && MediaResolver.Override(VideoPathValue) is { } ov) return ov;
            if (MediaResolver.EntityVideo("Platforms", _name, null) is { } own) return own;
            if (!fallBackToGameVideos) return "";
            foreach (var g in GetAllGames(false, false))
            {
                try { var v = g?.GetVideoPath(false); if (!string.IsNullOrEmpty(v)) return v; } catch { }
            }
        }
        catch { }
        return "";
    }

    public override string Default3DBoxImagePath => Img("Default 3D Box");
    public override string DefaultCartImagePath => Img("Default Cart");
    public override string Default3DCartImagePath => Img("Default 3D Cart");

    /// <summary>All images for a platform image TYPE (folder name, e.g. "Clear Logo") — the platform's own files
    /// first, then media-pack fallbacks — each with its source label. Reusable (editor, tree, web); see
    /// MediaResolver.PlatformTypeImages.</summary>
    public List<(string path, string source)> GetImagesForType(string imageType)
        => MediaResolver.PlatformTypeImages(_imagesRoot, _name, ScrapeAsValue ?? "", imageType);

    // Single best path (own-first, then media-pack). Routing through GetImagesForType makes the SDK image
    // properties (ClearLogoImagePath, …) media-pack aware app-wide, not just in the editor.
    private string Img(string type) { var l = GetImagesForType(type); return l.Count > 0 ? l[0].path : ""; }

    // Minimal LB-style filename sanitize (matches the common case).
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace('\'', '_').Trim();
    }
}

internal sealed class HostPlatformCategory : DummyPlatformCategory, ILiteBoxFields
{
    internal static readonly HashSet<string> Modeled = new(StringComparer.Ordinal)
    { "Name", "NestedName", "Notes", "VideoPath", "SortTitle", "HideInBigBox" };
    private string _name;
    private readonly string _imagesRoot;
    private GameStore _store;
    private Dictionary<string, string> _extra;
    internal void Attach(GameStore s) => _store = s;
    internal void SetExtra(Dictionary<string, string> e) => _extra = e;
    private void Rec(string field, string value) => _store?.RecordEntityModify("PlatformCategory", _name, field, value);
    public HostPlatformCategory(string name, string imagesRoot) { _name = name; _imagesRoot = imagesRoot; }
    public override string Name { get => _name; set { } }
    // Rename (Edit Category "Unique Name"): the XML rename is done SURGICALLY by the editor (Platforms.xml +
    // Parents.xml refs + images folder); this just re-points the live object so the tree/op-log follow. Call it
    // BEFORE recording other field edits (Rec keys records by the current name).
    internal void SetNameInternal(string n) { if (!string.IsNullOrWhiteSpace(n)) _name = n.Trim(); }
    public string NotesValue, NestedNameValue, VideoPathValue, SortTitleValue;
    public bool HideInBigBoxValue;

    // ── ILiteBoxFields ──
    public string GetField(string xmlElementName) => _extra != null && _extra.TryGetValue(xmlElementName, out var v) ? (v ?? "") : "";
    public void SetField(string xmlElementName, string value)
    {
        if (string.IsNullOrEmpty(xmlElementName)) return;
        if (string.IsNullOrEmpty(value)) _extra?.Remove(xmlElementName);
        else (_extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[xmlElementName] = value;
        Rec(xmlElementName, value);
    }
    public IReadOnlyCollection<string> ExtraFieldNames => _extra != null ? (IReadOnlyCollection<string>)_extra.Keys : Array.Empty<string>();
    public override string Notes { get => NotesValue ?? ""; set { NotesValue = value; Rec("Notes", value); } }
    public override string NestedName { get => NestedNameValue ?? ""; set { NestedNameValue = value; Rec("NestedName", value); } }
    public override string VideoPath { get => VideoPathValue ?? ""; set { VideoPathValue = value; Rec("VideoPath", value); } }
    public override string SortTitle { get => SortTitleValue ?? ""; set { SortTitleValue = value; Rec("SortTitle", value); } }
    public override bool HideInBigBox { get => HideInBigBoxValue; set { HideInBigBoxValue = value; Rec("HideInBigBox", value ? "true" : "false"); } }

    /// <summary>Boot-overlay apply (see <see cref="HostPlatform.ApplyFieldSilent"/>).</summary>
    internal void ApplyFieldSilent(string field, string value)
    {
        if (string.IsNullOrEmpty(field)) return;
        switch (field)
        {
            case "Notes": NotesValue = value; break;
            case "NestedName": NestedNameValue = value; break;
            case "VideoPath": VideoPathValue = value; break;
            case "SortTitle": SortTitleValue = value; break;
            case "HideInBigBox": HideInBigBoxValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
            case "Name": break;   // renames are surgical, never journaled
            default:
                if (string.IsNullOrEmpty(value)) _extra?.Remove(field);
                else (_extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[field] = value;
                break;
        }
    }


    // The entity's OWN video (its marquee / attract clip), not the videos of the games inside it. Answered
    // null until now, which a second-screen plugin reads as "this platform has no video" — ThirdScreen asks
    // for exactly this on its "Platform Video" and "Platform Marquee Video" entries.
    //
    // fallBackToGameVideos: when the entity has none, borrow one from a game inside it — that is what the
    // flag is for, and refusing to would make the parameter a lie. allowThemePath prefers the platform's
    // configured VideoPath override when it points somewhere real.
    public override string GetPlatformVideoPath(bool fallBackToGameVideos, bool allowThemePath)
    {
        try
        {
            if (allowThemePath && MediaResolver.Override(VideoPathValue) is { } ov) return ov;
            if (MediaResolver.EntityVideo("Platform Categories", _name, NestedNameValue) is { } own) return own;
            if (!fallBackToGameVideos) return "";
            foreach (var g in GetAllGames(false, false))
            {
                try { var v = g?.GetVideoPath(false); if (!string.IsNullOrEmpty(v)) return v; } catch { }
            }
        }
        catch { }
        return "";
    }


    public override string GetNewPlatformLogoPath(string url)
        => MediaResolver.NewEntityFile("Images", "Platform Categories", _name, url, "Clear Logo", ".png");
    public override string GetNewPlatformVideoPath(string url)
        => MediaResolver.NewEntityFile("Videos", "Platform Categories", _name, url, null, ".mp4");

    // Category images: Images\Platform Categories\<name>\<type>\<name>.ext
    public override string ClearLogoImagePath => Img("Clear Logo");
    public override string BannerImagePath => Img("Banner");
    public override string BackgroundImagePath => Img("Fanart");
    public override string DeviceImagePath => Img("Device");
    // Own image first (exact <name>.<ext>, then anything in the folder), THEN the media packs — the same
    // order LaunchBox uses, and the same NamedImage answered with alone. Packs were simply never consulted
    // here: a category whose logo ships only in one (Nostalgic Platform Clear Logos files them under
    // Platform Categories\<name>.png) reported "no image" to the tree, to the hero and to every plugin,
    // while LaunchBox showed it. EntityTypeImages walks the own folder too, so the fallback costs a second
    // look only when there was nothing to find.
    private string Img(string type)
    {
        var own = MediaResolver.NamedImage(_imagesRoot, "Platform Categories", _name, type);
        if (!string.IsNullOrEmpty(own)) return own;
        var l = MediaResolver.EntityTypeImages(_imagesRoot, "Platform Categories", _name,
                                               string.IsNullOrWhiteSpace(NestedNameValue) ? _name : NestedNameValue, type);
        return l.Count > 0 ? l[0].path : "";
    }

    // ── Tree children (from Parents.xml) + aggregated games ──────────────────
    // Children are held as object because IPlatformCategory / IPlaylist do NOT derive
    // from IPlatform in this SDK (so a single typed list can't hold all three).
    private readonly List<object> _children = new();
    public void AddChild(object c) { if (c != null) _children.Add(c); }
    public void ClearChildren() => _children.Clear();   // for ReloadHierarchy (re-read of Parents.xml)
    public IReadOnlyList<object> Children => _children;
    public void SortChildren() => _children.Sort((a, b) => string.Compare(NodeSortKey(a), NodeSortKey(b), StringComparison.OrdinalIgnoreCase));
    // SDK GetChildren can only carry the platform children (typed IList<IPlatform>).
    public override IList<IPlatform> GetChildren() => _children.OfType<IPlatform>().ToList();

    // A category's games = the union of all its descendant platforms'/playlists' games.
    public override IGame[] GetAllGames(bool includeHidden, bool includeBroken) => Aggregate(includeHidden, includeBroken);
    public override int GetGameCount(bool includeHidden, bool includeBroken) => Aggregate(includeHidden, includeBroken).Length;
    public override bool HasGames(bool includeHidden, bool includeBroken) => Aggregate(includeHidden, includeBroken).Length > 0;

    private IGame[] Aggregate(bool h, bool b)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IGame>();
        void Visit(object node)
        {
            if (node is HostPlatformCategory cat) { foreach (var c in cat._children) Visit(c); return; }
            IGame[] gs;
            try { gs = node is IPlaylist pl ? pl.GetAllGames(false) : node is IPlatform p ? p.GetAllGames(h, b) : Array.Empty<IGame>(); }
            catch { gs = Array.Empty<IGame>(); }
            foreach (var g in gs) { string id = SafeId(g); if (id == null || seen.Add(id)) result.Add(g); }
        }
        foreach (var c in _children) Visit(c);
        return result.ToArray();
    }

    /// <summary>What a READER should see for a tree node. LaunchBox keeps two names on a nested item: the
    /// unique one, which has to carry its parent to stay unique across the library ("Arcade 2-Player Games"),
    /// and the nested one, which is what it is called once you are already looking at its parent
    /// ("2-Player Games"). Shown nested, the unique name repeats the parent you just clicked through.
    ///
    /// Deliberately NOT NodeName: that one is identity — it matches parental hidden-name entries, feeds the
    /// media lookups and the web slugs, and is what a plugin is told. Renaming those to the short form would
    /// silently unhide a hidden playlist and break every path built from a name.
    ///
    /// An item that is not nested has no nested name, so this falls back on its own.</summary>
    internal static string NodeDisplayName(object n)
    {
        try
        {
            string nested = n is IPlatform p ? p.NestedName
                          : n is IPlatformCategory c ? c.NestedName
                          : n is IPlaylist pl ? pl.NestedName : null;
            if (!string.IsNullOrWhiteSpace(nested)) return nested.Trim();
        }
        catch { }
        return NodeName(n);
    }

    /// <summary>What a node ORDERS by. Sort Title exists on all three kinds, is editable in every edit
    /// window, and was written to the XML and then ignored — the tree ordered on the visible name, so a
    /// library that had arranged its sidebar deliberately saw that arrangement quietly dropped.
    ///
    /// Falls back on the DISPLAYED name, not the unique one: without a sort title, a node belongs where a
    /// reader would look for it.</summary>
    internal static string NodeSortKey(object n)
    {
        try
        {
            string st = n is IPlatform p ? p.SortTitle
                      : n is IPlatformCategory c ? c.SortTitle
                      : n is IPlaylist pl ? pl.SortTitle : null;
            if (!string.IsNullOrWhiteSpace(st)) return st.Trim();
        }
        catch { }
        return NodeDisplayName(n);
    }

    /// <summary>Display name of any tree node (platform / category / playlist).</summary>
    internal static string NodeName(object n)
    {
        try { return n is IPlatform p ? (p.Name ?? "") : n is IPlatformCategory c ? (c.Name ?? "") : n is IPlaylist pl ? (pl.Name ?? "") : (n?.ToString() ?? ""); }
        catch { return ""; }
    }
    private static string SafeId(IGame g) { try { return g?.Id; } catch { return null; } }
}

internal static class PlatformCatalog
{
    public static (List<HostPlatform> platforms, List<HostPlatformCategory> categories) Load(string dataDir, string imagesRoot)
    {
        var platforms = new List<HostPlatform>();
        var categories = new List<HostPlatformCategory>();
        string file = Path.Combine(dataDir, "Platforms.xml");
        if (!File.Exists(file)) return (platforms, categories);

        XDocument doc;
        try { doc = XDocument.Load(file); } catch { return (platforms, categories); }
        var root = doc.Root;
        if (root == null) return (platforms, categories);

        // LB root (parent of Images): <FolderPath> entries are RELATIVE to it
        // (e.g. "Images\Nintendo 64\Box - Front"). Resolve against the LB root —
        // NOT the process CWD — or every custom folder resolves under LB\Core.
        string lbRoot = Path.GetDirectoryName(imagesRoot?.TrimEnd('\\', '/')) ?? imagesRoot;

        // Folders grouped by platform name (paths stored ABSOLUTE).
        var foldersByPlatform = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pf in root.Elements("PlatformFolder"))
        {
            string plat = (string)pf.Element("Platform");
            string media = (string)pf.Element("MediaType");
            string path = (string)pf.Element("FolderPath");
            if (string.IsNullOrWhiteSpace(plat) || string.IsNullOrWhiteSpace(media) || string.IsNullOrWhiteSpace(path)) continue;
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(lbRoot, path));
            if (!foldersByPlatform.TryGetValue(plat, out var map))
                foldersByPlatform[plat] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[media] = path;
        }

        // Platform-level 3D model settings: root <ModelSettings> whose <PlatformName> names a platform
        // (a filled <GameId> means it belongs to a game and lives in that platform's own file instead).
        var modelByPlatform = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ms in root.Elements("ModelSettings"))
        {
            string plat = ((string)ms.Element("PlatformName") ?? "").Trim();
            if (plat.Length == 0) continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ms.Elements()) map[c.Name.LocalName] = c.Value;
            modelByPlatform[plat] = map;
        }

        // Platform documents, grouped by platform, order preserved (it is what the menu shows).
        var docsByPlatform = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pd in root.Elements("PlatformDocument"))
        {
            string plat = ((string)pd.Element("Platform") ?? "").Trim();
            if (plat.Length == 0) continue;
            if (!docsByPlatform.TryGetValue(plat, out var l)) docsByPlatform[plat] = l = new List<(string, string)>();
            l.Add(((string)pd.Element("Name") ?? "", (string)pd.Element("FilePath") ?? ""));
        }

        foreach (var pe in root.Elements("Platform"))
        {
            string name = (string)pe.Element("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            foldersByPlatform.TryGetValue(name, out var folders);
            var hp = new HostPlatform(name, folders, imagesRoot)
            {
                DeveloperValue = (string)pe.Element("Developer"),
                ManufacturerValue = (string)pe.Element("Manufacturer"),
                NotesValue = (string)pe.Element("Notes"),
                CategoryValue = (string)pe.Element("Category"),
                CpuValue = (string)pe.Element("Cpu"),
                MemoryValue = (string)pe.Element("Memory"),
                GraphicsValue = (string)pe.Element("Graphics"),
                SoundValue = (string)pe.Element("Sound"),
                DisplayValue = (string)pe.Element("Display"),
                MediaValue = (string)pe.Element("Media"),
                MaxControllersValue = (string)pe.Element("MaxControllers"),
                ScrapeAsValue = (string)pe.Element("ScrapeAs"),
                SortTitleValue = (string)pe.Element("SortTitle"),
                NestedNameValue = (string)pe.Element("NestedName"),
                LastGameIdValue = (string)pe.Element("LastGameId"),
                ImageTypeValue = (string)pe.Element("ImageType"),
                VideoPathValue = (string)pe.Element("VideoPath"),
                BigBoxThemeValue = (string)pe.Element("BigBoxTheme"),
                BigBoxViewValue = (string)pe.Element("BigBoxView"),
                ReleaseDateValue = ParseDate((string)pe.Element("ReleaseDate")),
                HideInBigBoxValue = ((string)pe.Element("HideInBigBox") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                FolderValue = (string)pe.Element("Folder"),
                FrontImagesFolderValue = (string)pe.Element("FrontImagesFolder"),
                BackImagesFolderValue = (string)pe.Element("BackImagesFolder"),
                ClearLogoImagesFolderValue = (string)pe.Element("ClearLogoImagesFolder"),
                FanartImagesFolderValue = (string)pe.Element("FanartImagesFolder"),
                ScreenshotImagesFolderValue = (string)pe.Element("ScreenshotImagesFolder"),
                BannerImagesFolderValue = (string)pe.Element("BannerImagesFolder"),
                SteamBannerImagesFolderValue = (string)pe.Element("SteamBannerImagesFolder"),
                ManualsFolderValue = (string)pe.Element("ManualsFolder"),
                MusicFolderValue = (string)pe.Element("MusicFolder"),
                VideosFolderValue = (string)pe.Element("VideosFolder"),
            };
            Dictionary<string, string> pex = null;
            foreach (var ce in pe.Elements())
            {
                string n = ce.Name.LocalName;
                if (HostPlatform.Modeled.Contains(n)) continue;
                string val = ce.Value;
                if (string.IsNullOrEmpty(val)) continue;
                (pex ??= new Dictionary<string, string>(StringComparer.Ordinal))[n] = val;
            }
            if (pex != null) hp.SetExtra(pex);
            if (modelByPlatform.TryGetValue(name, out var ms3d)) hp.SetModelSettings(ms3d);
            if (docsByPlatform.TryGetValue(name, out var pdocs)) hp.SetPlatformDocuments(pdocs);
            platforms.Add(hp);
        }

        foreach (var ce in root.Elements("PlatformCategory"))
        {
            string name = (string)ce.Element("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var hc = new HostPlatformCategory(name, imagesRoot)
            {
                NotesValue = (string)ce.Element("Notes"),
                NestedNameValue = (string)ce.Element("NestedName"),
                VideoPathValue = (string)ce.Element("VideoPath"),
                SortTitleValue = (string)ce.Element("SortTitle"),
                HideInBigBoxValue = ((string)ce.Element("HideInBigBox") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
            };
            Dictionary<string, string> cex = null;
            foreach (var cce in ce.Elements())
            {
                string n = cce.Name.LocalName;
                if (HostPlatformCategory.Modeled.Contains(n)) continue;
                string val = cce.Value;
                if (string.IsNullOrEmpty(val)) continue;
                (cex ??= new Dictionary<string, string>(StringComparer.Ordinal))[n] = val;
            }
            if (cex != null) hc.SetExtra(cex);
            categories.Add(hc);
        }

        Console.WriteLine($"[platcat] file={file} exists={File.Exists(file)} platforms={platforms.Count} categories={categories.Count} folders={foldersByPlatform.Count} rootChildren={root.Elements().Count()}");
        return (platforms, categories);
    }

    private static DateTime? ParseDate(string s)
        => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;
}
