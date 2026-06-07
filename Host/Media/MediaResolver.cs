// Host media resolution, deliberately CLOSE TO LAUNCHBOX'S NATIVE behaviour and
// with NO persistent cache: for a (game, image-type) it walks the folder the data
// layer already resolved and picks the best matching file on disk, by region
// priority then lowest "-NNN" suffix. Two filename shapes are recognised, exactly
// like LaunchBox / ExtendDB's GameCache:
//     {sanitizedTitle}.{guid}[-mid]-{NNN}.ext   (GUID form)
//     {sanitizedTitle}-{NNN}.ext                (legacy form)
//
// Fast path: if ExtendDB is loaded and its GameCache is ready, lookups are
// delegated to it (GameCacheBridge) and NO filesystem IO happens. The IO walk
// here is the fallback for when ExtendDB / its cache isn't available.
//
// Region priority is read from <LB>\Data\Settings.xml (<RegionPriorities>), the
// same source LaunchBox uses; root files (no region sub-folder) rank last, as in
// the GameCache.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class MediaResolver
{
    // ── Property → ordered LB image-type fallback chains (native-ish) ─────────
    public static readonly string[] Front = { "Box - Front", "Box - Front - Reconstructed", "Fanart - Box - Front" };
    public static readonly string[] Back = { "Box - Back", "Box - Back - Reconstructed", "Fanart - Box - Back" };
    public static readonly string[] Box3D = { "Box - 3D" };
    public static readonly string[] CartFront = { "Cart - Front", "Fanart - Cart - Front" };
    public static readonly string[] CartBack = { "Cart - Back", "Fanart - Cart - Back" };
    public static readonly string[] Cart3D = { "Cart - 3D" };
    public static readonly string[] ClearLogo = { "Clear Logo" };
    public static readonly string[] Screenshot = { "Screenshot - Gameplay", "Screenshot - Game Title", "Screenshot - Game Select" };
    public static readonly string[] Marquee = { "Arcade - Marquee", "Banner" };
    public static readonly string[] Background = { "Fanart - Background" };

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm" };
    private static readonly HashSet<string> ManualExts = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".cbz", ".cbr", ".txt", ".htm", ".html" };
    private static readonly HashSet<string> MusicExts = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".ogg", ".wav", ".flac", ".m4a" };

    private static readonly string[] VideoSubDirs = { "Trailer", "Theme", "Marquee", "Recordings" };

    private static readonly Regex GuidRe = new(
        @"^(.+)\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})((?:-[^-]+)*)-(\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlainRe = new(@"^(.+)-(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CollapseUnderscore = new("_{2,}", RegexOptions.Compiled);

    private static string _lbRoot;
    private static string[] _regions = Array.Empty<string>(); // priority order; root handled separately

    /// <summary>The LaunchBox Images root (or null before Init).</summary>
    public static string ImagesRoot => string.IsNullOrEmpty(_lbRoot) ? null : Path.Combine(_lbRoot, "Images");

    /// <summary>
    /// A node icon from the "Nostalgic Platform Icons" media pack (as launchbox-web uses):
    /// Images\Media Packs\Platform Icons\Nostalgic Platform Icons\&lt;subFolder&gt;\&lt;name&gt;.png.
    /// subFolder = "Platforms" | "Platform Categories" | "Playlists". Null if none.
    /// </summary>
    public static string PlatformIcon(string imagesRoot, string subFolder, string name)
    {
        if (string.IsNullOrEmpty(imagesRoot) || string.IsNullOrEmpty(name)) return null;
        string dir = Path.Combine(imagesRoot, "Media Packs", "Platform Icons", "Nostalgic Platform Icons", subFolder);
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
                if (string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch { }
        return null;
    }

    /// <summary>Initialise with the LaunchBox root (parent of Data/Images). Reads region priorities.</summary>
    public static void Init(string lbRoot)
    {
        _lbRoot = lbRoot;
        _regions = ReadRegionPriorities(lbRoot);
        Console.WriteLine($"[media] init lbRoot={lbRoot} regions=[{string.Join(", ", _regions)}]");
    }

    // ── Public API (used by HostGame) ────────────────────────────────────────

    /// <summary>Best image path for a property's type chain (fast path via cache, else IO). Null if none.</summary>
    public static string Image(string platformName, Guid id, string title, string[] typeChain)
    {
        if (string.IsNullOrEmpty(platformName) || typeChain == null) return null;

        if (GameCacheBridge.Ready(platformName))
        {
            foreach (var type in typeChain)
            {
                var p = GameCacheBridge.BestImage(platformName, id, type);
                if (!string.IsNullOrEmpty(p)) return p;
            }
            return null; // cache is authoritative when ready
        }

        // Classic IO fallback.
        var plat = SafePlatform(platformName);
        if (plat == null) return null;
        string sani = Sanitize(title);

        foreach (var type in typeChain)
        {
            string folder = SafeFolder(plat, type);
            if (folder == null || !Directory.Exists(folder)) continue;

            // Region priority, then root (no region sub-folder) last.
            foreach (var region in _regions)
            {
                var hit = BestInDir(Path.Combine(folder, region), id, sani, ImageExts);
                if (hit != null) return hit;
            }
            var root = BestInDir(folder, id, sani, ImageExts);
            if (root != null) return root;
        }
        return null;
    }

    /// <summary>Best video path; <paramref name="prioritizeTheme"/> puts the Theme sub-dir first.</summary>
    public static string Video(string platformName, Guid id, string title, bool prioritizeTheme)
    {
        string[] order = prioritizeTheme
            ? new[] { "Theme", null, "Trailer", "Marquee", "Recordings" }
            : new[] { null, "Trailer", "Theme", "Marquee", "Recordings" };
        foreach (var sub in order)
        {
            var p = VideoIn(platformName, id, title, sub);
            if (!string.IsNullOrEmpty(p)) return p;
        }
        return null;
    }

    /// <summary>Video path inside a specific sub-dir (null = root). Fast path via cache, else IO.</summary>
    public static string VideoIn(string platformName, Guid id, string title, string subDir)
    {
        if (string.IsNullOrEmpty(platformName)) return null;

        if (GameCacheBridge.Ready(platformName))
            return GameCacheBridge.Video(platformName, id, subDir);

        string baseDir = VideoFolder(platformName);
        if (baseDir == null) return null;
        string dir = subDir == null ? baseDir : Path.Combine(baseDir, subDir);
        return BestInDir(dir, id, Sanitize(title), VideoExts);
    }

    /// <summary>Manual file path (always IO — the GameCache does not index manuals). Null if none.</summary>
    public static string Manual(string platformName, Guid id, string title)
        => BestInDir(MediaFolder("Manuals", platformName), id, Sanitize(title), ManualExts);

    /// <summary>Music/theme-music file path (always IO). Null if none.</summary>
    public static string Music(string platformName, Guid id, string title)
        => BestInDir(MediaFolder("Music", platformName), id, Sanitize(title), MusicExts);

    /// <summary>
    /// Every image file for a game, as SDK ImageDetails (FilePath/ImageType/Region),
    /// across all image types (or a single <paramref name="typeFilter"/>). Always IO
    /// (the GameCache fast path only gives a best-per-type). Region "" = root folder.
    /// </summary>
    public static List<ImageDetails> AllImages(string platformName, Guid id, string title, string typeFilter)
    {
        var result = new List<ImageDetails>();
        if (string.IsNullOrEmpty(platformName)) return result;
        var plat = SafePlatform(platformName);
        if (plat == null) return result;
        string sani = Sanitize(title);

        IEnumerable<string> types = !string.IsNullOrWhiteSpace(typeFilter) ? new[] { typeFilter } : AllImageTypes();
        foreach (var type in types)
        {
            string folder = SafeFolder(plat, type);
            if (folder == null || !Directory.Exists(folder)) continue;

            foreach (var p in AllInDir(folder, id, sani, ImageExts))
                Add(result, p, type, "");                       // root (no region)
            foreach (var sub in SafeSubdirs(folder))
                foreach (var p in AllInDir(sub, id, sani, ImageExts))
                    Add(result, p, type, Path.GetFileName(sub)); // region subfolder
        }
        return result;

        static void Add(List<ImageDetails> list, string path, string type, string region)
        { var d = MakeImageDetails(path, type, region); if (d != null) list.Add(d); }
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    /// <summary>All matching files in one directory (lowest -NNN first), not just the best.</summary>
    private static IEnumerable<string> AllInDir(string dir, Guid id, string sani, HashSet<string> exts)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
        string glob = sani.Length > 0 ? sani + "*" : "*";
        List<(long num, string path)> hits = new();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var f in files)
        {
            if (!exts.Contains(Path.GetExtension(f))) continue;
            if (TryMatch(Path.GetFileNameWithoutExtension(f), id, sani, out long num)) hits.Add((num, f));
        }
        hits.Sort((a, b) => a.num.CompareTo(b.num));
        foreach (var h in hits) yield return h.path;
    }

    // ── Image-type list (all known types) ────────────────────────────────────
    private static string[] _allTypes;
    private static readonly string[] DefaultImageTypes =
    {
        "Box - Front", "Box - Front - Reconstructed", "Box - Back", "Box - Back - Reconstructed",
        "Box - 3D", "Box - Spine", "Box - Full", "Cart - Front", "Cart - Back", "Cart - 3D",
        "Disc", "Clear Logo", "Banner", "Steam Banner", "Fanart - Background",
        "Fanart - Box - Front", "Fanart - Box - Back", "Fanart - Cart - Front", "Fanart - Cart - Back",
        "Fanart - Disc", "Arcade - Marquee", "Arcade - Cabinet", "Arcade - Control Panel",
        "Arcade - Controls Information", "Screenshot - Gameplay", "Screenshot - Game Title",
        "Screenshot - Game Select", "Screenshot - Game Over", "Screenshot - High Scores",
        "Advertisement Flyer - Front", "Advertisement Flyer - Back", "Poster", "Square", "Icon",
    };

    private static IEnumerable<string> AllImageTypes()
    {
        if (_allTypes != null) return _allTypes;
        try
        {
            var list = ImageTypes.GetList();
            if (list != null && list.Count > 0)
                return _allTypes = list.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
        }
        catch { }
        return _allTypes = DefaultImageTypes;
    }

    // ── Build the SDK ImageDetails (get-only props → constructor, by param name) ─
    private static System.Reflection.ConstructorInfo _imgDetailsCtor;
    private static bool _imgDetailsCtorResolved;

    private static ImageDetails MakeImageDetails(string path, string type, string region)
    {
        if (!_imgDetailsCtorResolved)
        {
            _imgDetailsCtorResolved = true;
            try
            {
                _imgDetailsCtor = typeof(ImageDetails)
                    .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(c => c.GetParameters().Length == 3 && c.GetParameters().All(p => p.ParameterType == typeof(string)));
            }
            catch { _imgDetailsCtor = null; }
        }
        if (_imgDetailsCtor == null) return null;
        try
        {
            var ps = _imgDetailsCtor.GetParameters();
            var args = new object[3];
            for (int i = 0; i < 3; i++)
            {
                var n = (ps[i].Name ?? "").ToLowerInvariant();
                args[i] = (n.Contains("path") || n.Contains("file")) ? path
                        : n.Contains("type") ? type
                        : n.Contains("region") ? region
                        : (object)null;
            }
            return (ImageDetails)_imgDetailsCtor.Invoke(args);
        }
        catch { return null; }
    }

    /// <summary>
    /// A platform/category/playlist image: &lt;imagesRoot&gt;\&lt;rootFolder&gt;\&lt;name&gt;\&lt;type&gt;\&lt;name&gt;.ext
    /// (e.g. Images\Platforms\Nintendo 64\Banner\Nintendo 64.jpg). "" if none.
    /// </summary>
    public static string NamedImage(string imagesRoot, string rootFolder, string name, string typeFolder)
    {
        if (string.IsNullOrEmpty(imagesRoot) || string.IsNullOrEmpty(name)) return "";
        string san = Sanitize(name);
        string dir = Path.Combine(imagesRoot, rootFolder, san, typeFolder);
        if (!Directory.Exists(dir)) return "";
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
        {
            var f = Path.Combine(dir, san + ext);
            if (File.Exists(f)) return f;
        }
        try
        {
            var any = Directory.EnumerateFiles(dir).FirstOrDefault(f => ImageExts.Contains(Path.GetExtension(f)));
            if (any != null) return any;
        }
        catch { }
        return "";
    }

    // ── Core: best matching file in a single directory ───────────────────────
    private static string BestInDir(string dir, Guid id, string sani, HashSet<string> exts)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        string best = null;
        long bestNum = long.MaxValue;
        string glob = sani.Length > 0 ? sani + "*" : "*";

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly); }
        catch { return null; }

        foreach (var f in files)
        {
            if (!exts.Contains(Path.GetExtension(f))) continue;
            if (TryMatch(Path.GetFileNameWithoutExtension(f), id, sani, out long num) && num < bestNum)
            {
                best = f;
                bestNum = num;
            }
        }
        return best;
    }

    /// <summary>Matches a filename (no ext) to the game by GUID form or legacy form; out = the -NNN value.</summary>
    private static bool TryMatch(string nameNoExt, Guid id, string sani, out long num)
    {
        num = 0;
        var gm = GuidRe.Match(nameNoExt);
        if (gm.Success)
        {
            if (Guid.TryParse(gm.Groups[2].Value, out var g) && g == id)
                return long.TryParse(gm.Groups[4].Value, out num);
            return false; // a GUID file, but not this game
        }
        var pm = PlainRe.Match(nameNoExt);
        if (pm.Success && string.Equals(pm.Groups[1].Value, sani, StringComparison.OrdinalIgnoreCase))
            return long.TryParse(pm.Groups[2].Value, out num);
        return false;
    }

    // ── Folder resolution (through the data-layer API) ───────────────────────
    private static IPlatform SafePlatform(string name)
    {
        try { return PluginHelper.DataManager?.GetPlatformByName(name); } catch { return null; }
    }

    private static string SafeFolder(IPlatform plat, string imageType)
    {
        try
        {
            var pf = plat.GetPlatformFolderByImageType(imageType);
            var fp = pf?.FolderPath;
            if (string.IsNullOrWhiteSpace(fp)) return null;
            // Anchor any relative path on the LB root, never the process CWD.
            return Path.IsPathRooted(fp) ? fp : Path.GetFullPath(Path.Combine(_lbRoot ?? AppContext.BaseDirectory, fp));
        }
        catch { return null; }
    }

    /// <summary>Resolves the platform's video folder: custom IPlatform.VideoPath or <LB>\Videos\<platform>.</summary>
    private static string VideoFolder(string platformName)
    {
        var plat = SafePlatform(platformName);
        string custom = null;
        try { custom = plat?.VideoPath; } catch { }
        if (!string.IsNullOrWhiteSpace(custom))
            return Path.IsPathRooted(custom) ? custom : Path.GetFullPath(Path.Combine(_lbRoot ?? ".", custom));
        return MediaFolder("Videos", platformName);
    }

    /// <summary>Default convention folder: <LB>\<root>\<sanitized platform>.</summary>
    private static string MediaFolder(string root, string platformName)
    {
        if (_lbRoot == null || string.IsNullOrEmpty(platformName)) return null;
        return Path.Combine(_lbRoot, root, Sanitize(platformName));
    }

    // ── LaunchBox filename sanitizer (mirrors Utils.LaunchboxFileNameSanitize) ─
    public static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => (Array.IndexOf(invalid, c) >= 0 || c == '\'') ? '_' : c).ToArray());
        s = CollapseUnderscore.Replace(s, "_");
        return s.Trim();
    }

    private static string[] ReadRegionPriorities(string lbRoot)
    {
        try
        {
            string file = Path.Combine(lbRoot, "Data", "Settings.xml");
            if (File.Exists(file))
            {
                var raw = XDocument.Load(file).Root?.Element("Settings")?.Element("RegionPriorities")?.Value;
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        catch { }
        // Sensible default if Settings.xml is missing.
        return new[] { "North America", "United States", "World", "Europe", "Japan" };
    }
}
