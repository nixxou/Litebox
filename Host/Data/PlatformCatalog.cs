// Loads LB\Data\Platforms.xml: platform definitions (metadata), their custom
// media folders (<PlatformFolder>) and platform categories. Few entities
// (hundreds), so plain objects with full fidelity — only Games need the compact
// store. The PlatformFolder map is what makes custom image paths (e.g. MS-DOS
// "Box - Front" -> Images\MS-DOS\Front) resolve correctly via the API.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Data;

internal sealed class HostPlatform : DummyPlatform
{
    private readonly string _name;
    private readonly Dictionary<string, string> _folders; // MediaType -> absolute FolderPath
    private readonly string _imagesRoot;
    private IGame[] _games = Array.Empty<IGame>();

    public HostPlatform(string name, Dictionary<string, string> folders, string imagesRoot)
    {
        _name = name;
        _folders = folders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _imagesRoot = imagesRoot;
    }

    public void SetGames(IGame[] games) => _games = games ?? Array.Empty<IGame>();

    // ── metadata (from Platforms.xml) ────────────────────────────────────────
    public override string Name { get => _name; set { } }
    public string DeveloperValue, ManufacturerValue, NotesValue, CategoryValue,
                  CpuValue, MemoryValue, GraphicsValue, SoundValue, DisplayValue,
                  MaxControllersValue, ScrapeAsValue, SortTitleValue, ImageTypeValue,
                  VideoPathValue, BigBoxThemeValue, BigBoxViewValue;
    public DateTime? ReleaseDateValue;
    public bool HideInBigBoxValue;
    // image-folder config fields
    public string FrontImagesFolderValue, BackImagesFolderValue, ClearLogoImagesFolderValue,
                  FanartImagesFolderValue, ScreenshotImagesFolderValue, BannerImagesFolderValue,
                  SteamBannerImagesFolderValue, ManualsFolderValue, MusicFolderValue, VideosFolderValue, FolderValue;

    public override string Developer { get => DeveloperValue ?? ""; set { } }
    public override string Manufacturer { get => ManufacturerValue ?? ""; set { } }
    public override string Notes { get => NotesValue ?? ""; set { } }
    public override string Category { get => CategoryValue ?? ""; set { } }
    public override string Cpu { get => CpuValue ?? ""; set { } }
    public override string Memory { get => MemoryValue ?? ""; set { } }
    public override string Graphics { get => GraphicsValue ?? ""; set { } }
    public override string Sound { get => SoundValue ?? ""; set { } }
    public override string Display { get => DisplayValue ?? ""; set { } }
    public override string MaxControllers { get => MaxControllersValue ?? ""; set { } }
    public override string ScrapeAs { get => ScrapeAsValue ?? ""; set { } }
    public override string SortTitle { get => SortTitleValue ?? ""; set { } }
    public override string ImageType { get => ImageTypeValue ?? ""; set { } }
    public override string VideoPath { get => VideoPathValue ?? ""; set { } }
    public override string BigBoxTheme { get => BigBoxThemeValue ?? ""; set { } }
    public override string BigBoxView { get => BigBoxViewValue ?? ""; set { } }
    public override Nullable<DateTime> ReleaseDate { get => ReleaseDateValue; set { } }
    public override bool HideInBigBox { get => HideInBigBoxValue; set { } }
    public override string Folder { get => FolderValue ?? ""; set { } }
    public override string FrontImagesFolder { get => FrontImagesFolderValue ?? ""; set { } }
    public override string BackImagesFolder { get => BackImagesFolderValue ?? ""; set { } }
    public override string ClearLogoImagesFolder { get => ClearLogoImagesFolderValue ?? ""; set { } }
    public override string FanartImagesFolder { get => FanartImagesFolderValue ?? ""; set { } }
    public override string ScreenshotImagesFolder { get => ScreenshotImagesFolderValue ?? ""; set { } }
    public override string BannerImagesFolder { get => BannerImagesFolderValue ?? ""; set { } }
    public override string SteamBannerImagesFolder { get => SteamBannerImagesFolderValue ?? ""; set { } }
    public override string ManualsFolder { get => ManualsFolderValue ?? ""; set { } }
    public override string MusicFolder { get => MusicFolderValue ?? ""; set { } }
    public override string VideosFolder { get => VideosFolderValue ?? ""; set { } }

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

    // ── Platform-level images (Images\Platforms\<name>\<type>\<name>.ext) ─────
    public override string ClearLogoImagePath => Img("Clear Logo");
    public override string BannerImagePath => Img("Banner");
    public override string BackgroundImagePath => Img("Fanart");
    public override string DeviceImagePath => Img("Device");
    public override string DefaultBoxImagePath => Img("Default Box");
    public override string Default3DBoxImagePath => Img("Default 3D Box");
    public override string DefaultCartImagePath => Img("Default Cart");
    public override string Default3DCartImagePath => Img("Default 3D Cart");
    private string Img(string type) => MediaResolver.NamedImage(_imagesRoot, "Platforms", _name, type);

    // Minimal LB-style filename sanitize (matches the common case).
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace('\'', '_').Trim();
    }
}

internal sealed class HostPlatformCategory : DummyPlatformCategory
{
    private readonly string _name;
    private readonly string _imagesRoot;
    public HostPlatformCategory(string name, string imagesRoot) { _name = name; _imagesRoot = imagesRoot; }
    public override string Name { get => _name; set { } }
    public string NotesValue, NestedNameValue;
    public override string Notes { get => NotesValue ?? ""; set { } }
    public override string NestedName { get => NestedNameValue ?? ""; set { } }

    // Category images: Images\Platform Categories\<name>\<type>\<name>.ext
    public override string ClearLogoImagePath => Img("Clear Logo");
    public override string BannerImagePath => Img("Banner");
    public override string BackgroundImagePath => Img("Fanart");
    public override string DeviceImagePath => Img("Device");
    private string Img(string type) => MediaResolver.NamedImage(_imagesRoot, "Platform Categories", _name, type);

    // ── Tree children (from Parents.xml) + aggregated games ──────────────────
    // Children are held as object because IPlatformCategory / IPlaylist do NOT derive
    // from IPlatform in this SDK (so a single typed list can't hold all three).
    private readonly List<object> _children = new();
    public void AddChild(object c) { if (c != null) _children.Add(c); }
    public IReadOnlyList<object> Children => _children;
    public void SortChildren() => _children.Sort((a, b) => string.Compare(NodeName(a), NodeName(b), StringComparison.OrdinalIgnoreCase));
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
                MaxControllersValue = (string)pe.Element("MaxControllers"),
                ScrapeAsValue = (string)pe.Element("ScrapeAs"),
                SortTitleValue = (string)pe.Element("SortTitle"),
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
            platforms.Add(hp);
        }

        foreach (var ce in root.Elements("PlatformCategory"))
        {
            string name = (string)ce.Element("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            categories.Add(new HostPlatformCategory(name, imagesRoot)
            {
                NotesValue = (string)ce.Element("Notes"),
                NestedNameValue = (string)ce.Element("NestedName"),
            });
        }

        Console.WriteLine($"[platcat] file={file} exists={File.Exists(file)} platforms={platforms.Count} categories={categories.Count} folders={foldersByPlatform.Count} rootChildren={root.Elements().Count()}");
        return (platforms, categories);
    }

    private static DateTime? ParseDate(string s)
        => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;
}
