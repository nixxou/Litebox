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

    internal static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
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
    // Full pick order: user priorities → LaunchBox's hard-coded fallback → root ("none") last. See LbRegions.
    private static List<string> _regionOrder = LbRegions.Order(Array.Empty<string>());
    private static List<string> RegionOrder() => _regionOrder;

    /// <summary>L'ordre effectif, pour que le test puisse formuler son attente au lieu de la coder
    /// en dur : il depend des priorites de l'utilisateur, donc de la machine.</summary>
    internal static List<string> RegionOrderForTest() => _regionOrder;

    /// <summary>Region order with the GAME's own region(s) prepended (LaunchBox step 1, which LiteBox skips by
    /// default). Empty game region → the plain global order. Split on comma/semicolon; the user priorities,
    /// LB fallback and root ("none") still follow, de-duplicated.</summary>
    private static IEnumerable<string> RegionOrderForGame(string? gameRegion)
    {
        if (string.IsNullOrWhiteSpace(gameRegion)) return RegionOrder();
        var own = gameRegion.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return LbRegions.Order(own.Concat(_regions));
    }

    /// <summary>The LaunchBox Images root (or null before Init).</summary>
    public static string ImagesRoot => string.IsNullOrEmpty(_lbRoot) ? null : Path.Combine(_lbRoot, "Images");

    /// <summary>The LaunchBox root (parent of Data/Images), or null before Init.</summary>
    public static string LbRoot => _lbRoot;

    /// <summary>A stored media-path override, turned into a usable absolute path — or null when
    /// there is none, or when it names a file that is not there.
    ///
    /// &lt;ManualPath&gt;, &lt;MusicPath&gt;, &lt;VideoPath&gt; and &lt;ThemeVideoPath&gt; are the four fields where
    /// LaunchBox lets a game name its media OUTRIGHT instead of deriving the name from the title.
    /// When one is set it wins: that is the whole point of an override, and LaunchBox mirrors the
    /// pair everywhere — ManualPath alongside GetDefaultManualPath(), and the same for the other
    /// three. A "default" only means something if something else can displace it.
    ///
    /// A path is stored relative to the LB root when it sits under it and absolute otherwise, so
    /// both shapes are accepted. One that points at nothing falls back to the convention rather
    /// than showing the game as having no manual at all: a stale override should not hide a file
    /// that is sitting right there under the expected name.</summary>
    internal static string Override(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            string abs = Path.IsPathRooted(stored)
                ? Path.GetFullPath(stored)
                : (string.IsNullOrEmpty(_lbRoot) ? null : Path.GetFullPath(Path.Combine(_lbRoot, stored)));
            return abs != null && File.Exists(abs) ? abs : null;
        }
        catch { return null; }
    }

    /// <summary>Points the resolver at another tree, for tests and audits only. Returns the previous
    /// root so the caller can put it back — nothing else here is designed to be re-pointed.</summary>
    internal static string SwapRootForTest(string root)
    {
        string was = _lbRoot;
        _lbRoot = root;
        return was;
    }

    /// <summary>
    /// A node icon from the "Nostalgic Platform Icons" media pack (as launchbox-web uses):
    /// Images\Media Packs\Platform Icons\Nostalgic Platform Icons\&lt;subFolder&gt;\&lt;name&gt;.png.
    /// subFolder = "Platforms" | "Platform Categories" | "Playlists". Null if none.
    /// </summary>
    public static string PlatformIcon(string imagesRoot, string subFolder, params string[] names)
    {
        if (string.IsNullOrEmpty(imagesRoot) || names == null || names.Length == 0) return null;
        string dir = Path.Combine(imagesRoot, "Media Packs", "Platform Icons", "Nostalgic Platform Icons", subFolder);
        if (!Directory.Exists(dir)) return null;
        try
        {
            var files = Directory.GetFiles(dir, "*.png");
            // 1) exact (case-insensitive) match on any candidate name (tried in order — NestedName then Name).
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var f in files)
                    if (string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                        return f;
            }
            // 2) normalized match (lowercase, punctuation → single space) so the playlist "2-Player Games"
            //    finds the pack file "2 Player Games.png", "Beat Em Ups" finds "Beat _em Up", etc.
            foreach (var name in names)
            {
                string norm = NormIcon(name);
                if (norm.Length == 0) continue;
                foreach (var f in files)
                    if (NormIcon(Path.GetFileNameWithoutExtension(f)) == norm)
                        return f;
            }
        }
        catch { }
        return null;
    }

    // Loose key for icon-name matching: lowercase, any run of non-alphanumerics collapsed to one space.
    private static string NormIcon(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        bool sp = false;
        foreach (char ch in s)
        {
            char c = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(c)) { sb.Append(c); sp = false; }
            else if (sb.Length > 0 && !sp) { sb.Append(' '); sp = true; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Initialise with the LaunchBox root (parent of Data/Images). Reads region priorities.</summary>
    public static void Init(string lbRoot)
    {
        _lbRoot = lbRoot;
        _regions = ReadRegionPriorities(lbRoot);
        _regionOrder = LbRegions.Order(_regions);   // + LaunchBox's hard-coded fallback, root ("none") last
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

            // User priorities → LaunchBox's hard-coded fallback → root ("none", no region sub-folder) last.
            foreach (var region in RegionOrder())
            {
                var dir = region == LbRegions.None ? folder : Path.Combine(folder, region);
                var hit = BestInDir(dir, id, sani, ImageExts);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    /// <summary>Title-only best image via the classic disk walk (region-ordered), NEVER the game cache — for
    /// callers with no game Guid (e.g. the 3D-model preview's sample game, matched by filename): when the cache
    /// is Ready, Image() answers through the id-keyed bridge and Guid.Empty finds nothing.</summary>
    public static string ImageByTitle(string platformName, string title, string[] typeChain)
    {
        if (string.IsNullOrEmpty(platformName) || string.IsNullOrEmpty(title) || typeChain == null) return null;
        var plat = SafePlatform(platformName);   // null outside a plugin host (e.g. render probes)
        string sani = Sanitize(title);
        foreach (var type in typeChain)
        {
            // Custom platform folder when configured, else LB's conventional Images\<platform>\<type> layout.
            string folder = (plat != null ? SafeFolder(plat, type) : null)
                            ?? (ImagesRoot != null ? Path.Combine(ImagesRoot, Sanitize(platformName), type) : null);
            if (folder == null || !Directory.Exists(folder)) continue;
            foreach (var region in RegionOrder())
            {
                var dir = region == LbRegions.None ? folder : Path.Combine(folder, region);
                var hit = BestInDir(dir, Guid.Empty, sani, ImageExts);
                if (hit != null) return hit;
            }
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

    /// <summary>
    /// The five LaunchBox video types (SDK: VideoTypes.GetList()) and the sub-folder each lives in.
    /// A null sub-folder means the platform's video ROOT — that's the classic "Video Snap".
    /// </summary>
    public static readonly (string Type, string SubDir)[] VideoTypeDirs =
    {
        ("Video Snap",  null),
        ("Trailer",     "Trailer"),
        ("Theme Video", "Theme"),
        ("Recording",   "Recordings"),
        ("Marquee",     "Marquee"),
    };

    /// <summary>Every video file for a game as (path, type) — the video twin of <see cref="AllImageFiles"/>.
    /// Always IO: the cache only exposes the BEST video per sub-dir, not the whole list.</summary>
    public static List<(string path, string type)> AllVideoFiles(string platformName, Guid id, string title)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(platformName)) return result;
        string baseDir = VideoFolder(platformName);
        if (baseDir == null) return result;

        string sani = Sanitize(title);
        foreach (var (type, sub) in VideoTypeDirs)
        {
            string dir = sub == null ? baseDir : Path.Combine(baseDir, sub);
            foreach (var p in AllInDir(dir, id, sani, VideoExts)) result.Add((p, type));
        }
        return result;
    }

    /// <summary>The on-disk folder a video of the given TYPE belongs in (not created). Null if unresolvable.
    /// Used when ADDING or MOVING a video.</summary>
    public static string VideoTypeFolder(string platformName, string videoType)
    {
        string baseDir = VideoFolder(platformName);
        if (baseDir == null) return null;
        foreach (var (type, sub) in VideoTypeDirs)
            if (string.Equals(type, videoType, StringComparison.OrdinalIgnoreCase))
                return sub == null ? baseDir : Path.Combine(baseDir, sub);
        return null;
    }

    /// <summary>The extensions LiteBox recognises as a video (used by the Add Video picker).</summary>
    public static IReadOnlyCollection<string> VideoExtensions => VideoExts;

    /// <summary>Manual file path (always IO — the GameCache does not index manuals). Null if none.</summary>
    /// <summary>TOUS les manuels du jeu, dans l ordre du parcours — priorite de region puis
    /// alphabet, sous-dossiers avant fichiers : le premier est celui que Manual() rend.</summary>
    public static List<string> ManualsAll(string platformName, Guid id, string title)
    {
        var into = new List<string>();
        string dir = MediaFolder("Manuals", platformName);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            WalkFlatAll(dir, id, Sanitize(title), into);
        return into;
    }

    public static string Manual(string platformName, Guid id, string title)
        => BestInDir(MediaFolder("Manuals", platformName), id, Sanitize(title), ManualExts, flat: true);

    /// <summary>TOUTES les musiques du jeu, dans l ordre du parcours (meme regle que les manuels :
    /// appartenance au nom de fichier, toute profondeur) — le premier est celui que Music() rend.</summary>
    public static List<string> MusicsAll(string platformName, Guid id, string title)
    {
        var into = new List<string>();
        string dir = MediaFolder("Music", platformName);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            WalkFlatAll(dir, id, Sanitize(title), into);
        return into;
    }

    /// <summary>Music/theme-music file path (always IO). Null if none.</summary>
    public static string Music(string platformName, Guid id, string title)
        // Musique : meme forme que les manuels dans Platforms.xml (un MediaType, un FolderPath par
        // plateforme, aucun sous-dossier impose), donc meme regle. NON MESURE, contrairement aux
        // manuels — c'est une symetrie, pas une observation.
        => BestInDir(MediaFolder("Music", platformName), id, Sanitize(title), MusicExts, flat: true);

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

    /// <summary>Every image file for a game as (path, type, region) — the same walk as
    /// <see cref="AllImages"/> but without the SDK ImageDetails wrapper (so no reflection). Grouped by
    /// image type, root files then region sub-folders, each -NNN-ordered. region "" = root.</summary>
    public static List<(string path, string type, string region)> AllImageFiles(string platformName, Guid id, string title)
    {
        var result = new List<(string, string, string)>();
        if (string.IsNullOrEmpty(platformName)) return result;
        var plat = SafePlatform(platformName);
        if (plat == null) return result;
        string sani = Sanitize(title);
        foreach (var type in AllImageTypes())
        {
            string folder = SafeFolder(plat, type);
            if (folder == null || !Directory.Exists(folder)) continue;
            foreach (var p in AllInDir(folder, id, sani, ImageExts)) result.Add((p, type, ""));
            foreach (var sub in SafeSubdirs(folder))
                foreach (var p in AllInDir(sub, id, sani, ImageExts)) result.Add((p, type, Path.GetFileName(sub)));
        }
        return result;
    }

    /// <summary>The on-disk folder for a platform's image type (root, no region), created-or-not. Null if
    /// the platform / folder can't be resolved. Used when ADDING a new image.</summary>
    public static string TypeFolder(string platformName, string imageType)
    {
        var plat = SafePlatform(platformName);
        return plat == null ? null : SafeFolder(plat, imageType);
    }

    /// <summary>The known image-type names (LaunchBox's list when available, else the built-in defaults).</summary>
    public static IReadOnlyList<string> ImageTypeNames() => AllImageTypes().ToList();

    /// <summary>
    /// All image paths for ONE exact image type, in LaunchBox-native order: region
    /// priority first (root files last), then lowest "-NNN" suffix. Pure IO, so it
    /// returns the same set whether or not ExtendDB's GameCache is active. Empty if none.
    /// </summary>
    public static List<string> AllOfType(string platformName, Guid id, string title, string imageType)
        => AllOfType(platformName, id, title, imageType, null);

    /// <summary>As above, but the game's own region(s) can be tried FIRST (<paramref name="preferGameRegion"/>
    /// non-empty = LaunchBox-identical), and <paramref name="allRegions"/> controls breadth: false = only the
    /// BEST region (the first in priority order with a match); true = every region (can duplicate the same art).</summary>
    public static List<string> AllOfType(string platformName, Guid id, string title, string imageType, string? preferGameRegion, bool allRegions = true)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(platformName) || string.IsNullOrEmpty(imageType)) return result;
        var plat = SafePlatform(platformName);
        if (plat == null) return result;
        string folder = SafeFolder(plat, imageType);
        if (folder == null || !Directory.Exists(folder)) return result;
        string sani = Sanitize(title);
        foreach (var region in RegionOrderForGame(preferGameRegion))   // [game region] → priorities → LB fallback → root last
        {
            var dir = region == LbRegions.None ? folder : Path.Combine(folder, region);
            result.AddRange(AllInDir(dir, id, sani, ImageExts));
            if (!allRegions && result.Count > 0) break;   // best-region-only: stop at the first region that has a match
        }
        return result;
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    /// <summary>All matching files in one directory (lowest -NNN first), not just the best.</summary>
    /// <summary>Interne pour que le test puisse l'appeler : les listes publiques qui l'utilisent
    /// passent par le DataManager du greffon, absent d'un auto-test, et renverraient vide sans jamais
    /// atteindre ce code — une verification qui passe A VIDE.</summary>
    internal static IEnumerable<string> AllInDir(string dir, Guid id, string sani, HashSet<string> exts)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
        string glob = sani.Length > 0 ? sani + "*" : "*";
        // Un seul glob, sur le titre. Une seconde enumeration par GUID rendait visibles les fichiers
        // dont la partie titre est perimee — la forme que produit un renommage differe — mais mesure
        // sur la vraie bibliotheque : 1153 fichiers en forme GUID, dont 1147 portent deja le bon
        // titre et 2 seulement seraient caches, tous deux issus de fichiers de test. Zero cas reel,
        // pour 14 ms sur chaque listage. La correction a donc ete retiree, sciemment.
        //
        // Ce que cela laisse : entre un renommage et le vidage du XML, et tant qu un rival garde le
        // titre, un fichier GUID au titre perime reste invisible d ICI. La GameCache, elle, le voit
        // — elle attribue par GUID seul — donc l affichage est correct ; ce sont les appelants qui
        // inspectent le disque qui l ignorent. Le test qui suit epingle ce comportement pour que
        // personne ne le prenne pour un oubli.
        List<(long num, string path)> hits = new();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var f in files)
        {
            if (!exts.Contains(Path.GetExtension(f))) continue;
            if (TryMatch(Path.GetFileNameWithoutExtension(f), id, sani, out long num)) hits.Add((num, f));
        }
        // A numero egal, List.Sort n etant pas stable, deux fichiers pouvaient changer de place d un
        // appel a l autre. Le nom tranche : arbitraire, mais reproductible. Garde, il ne coute rien.
        hits.Sort((x, y) => x.num != y.num ? x.num.CompareTo(y.num)
                          : string.Compare(Path.GetFileName(x.path), Path.GetFileName(y.path),
                                           StringComparison.OrdinalIgnoreCase));
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

    // Media-pack category per platform image type: Images\Media Packs\<category>\<pack>\<scrapeAs|name>.<ext>.
    // Platform media-pack files are keyed by the platform's canonical (Scrape As) name, e.g. "Arcade.png".
    private static readonly Dictionary<string, string> _platformMediaPackCat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Clear Logo"] = "Platform Clear Logos",
        ["Banner"] = "Platform Banners",
        ["Fanart"] = "Platform Fanart",
        ["Device"] = "Platform Devices",
        ["Steam Banner"] = "Platform Steam Banners",
    };

    /// <summary>Every image for a platform image TYPE: the platform's OWN files (Images\Platforms\&lt;name&gt;\
    /// &lt;type&gt;\*) first, then media-pack files (Images\Media Packs\&lt;category&gt;\&lt;pack&gt;\
    /// &lt;scrapeAs|name&gt;.&lt;ext&gt;). Each entry is (path, source): the type name for own images, the pack
    /// folder name for media-pack ones (so the UI can label "Clear Logo (Nostalgic Platform Clear Logos)").</summary>
    public static List<(string path, string source)> PlatformTypeImages(string imagesRoot, string platformName, string scrapeAs, string imageType)
        => EntityTypeImages(imagesRoot, "Platforms", platformName, scrapeAs, imageType);

    /// <summary>Same as <see cref="PlatformTypeImages"/> but with the entity folder parameterized —
    /// "Platforms" or "Platform Categories" (category images: Images\Platform Categories\&lt;name&gt;\&lt;type&gt;\*).</summary>
    public static List<(string path, string source)> EntityTypeImages(string imagesRoot, string entityFolder, string platformName, string scrapeAs, string imageType)
    {
        var outList = new List<(string path, string source)>();
        if (string.IsNullOrEmpty(imagesRoot) || string.IsNullOrEmpty(platformName) || string.IsNullOrEmpty(imageType)) return outList;
        string san = Sanitize(platformName);
        try
        {
            var ownDir = Path.Combine(imagesRoot, entityFolder, san, imageType);
            if (Directory.Exists(ownDir))
                foreach (var f in Directory.EnumerateFiles(ownDir).Where(f => ImageExts.Contains(Path.GetExtension(f))).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    outList.Add((f, imageType));
        }
        catch { }
        // Media-pack fallback (FALLBACK: appended after the platform's own images). Packs organise files in
        // sub-folders by entity type — Images\Media Packs\<category>\<pack>\{Platforms|Platform Categories}\
        // <key>.png (and occasionally at the pack root). key = Scrape As first (LB's canonical name, which can be
        // a CATEGORY like "Arcade"), else the platform Name. First hit per pack; the pack folder is the source.
        if (_platformMediaPackCat.TryGetValue(imageType, out var cat))
        {
            try
            {
                var catDir = Path.Combine(imagesRoot, "Media Packs", cat);
                if (Directory.Exists(catDir))
                {
                    var keys = new List<string>();
                    if (!string.IsNullOrWhiteSpace(scrapeAs)) keys.Add(Sanitize(scrapeAs));
                    if (!keys.Contains(san)) keys.Add(san);
                    var subs = new[] { "Platforms", "Platform Categories", "" };   // "" = pack root
                    foreach (var packDir in Directory.EnumerateDirectories(catDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        string? hit = FindInPackSubs(packDir, subs, keys);
                        if (hit != null) outList.Add((hit, Path.GetFileName(packDir)));
                    }
                }
            }
            catch { }
        }
        return outList;
    }

    private static string? FindInPackSubs(string packDir, string[] subs, List<string> keys)
    {
        foreach (var sub in subs)
        {
            string baseDir = sub.Length == 0 ? packDir : Path.Combine(packDir, sub);
            if (!Directory.Exists(baseDir)) continue;
            foreach (var key in keys)
                foreach (var ext in ImageExts)
                {
                    var f = Path.Combine(baseDir, key + ext);
                    if (File.Exists(f)) return f;
                }
        }
        return null;
    }

    // ── Core: best matching file in a single directory ───────────────────────
    /// <param name="flat">Manuels et musiques. Ces deux types n'ont pas de sous-dossiers imposes
    /// — pas de region, pas de categorie — donc tout sous-dossier est un rangement libre de
    /// l'utilisateur, et LaunchBox descend dedans quel que soit son nom. Mesure : un fichier sous
    /// "ZZ Dossier Quelconque\" est trouve, un autre a deux niveaux aussi, mais un fichier au nom
    /// libre dans un dossier portant le nom du jeu ne l'est PAS. C'est donc le nom du FICHIER qui
    /// designe le jeu, jamais celui du dossier. Images et videos gardent la recherche a plat : chez
    /// elles un sous-dossier veut dire quelque chose (region, type de video) et fusionner les
    /// niveaux melangerait les categories.</param>
    private static string BestInDir(string dir, Guid id, string sani, HashSet<string> exts,
                                    bool flat = false)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        string best = null;
        long bestNum = long.MaxValue;
        string glob = sani.Length > 0 ? sani + "*" : "*";

        // TWO globs, and the second is not an optimisation — it is the whole point of the GUID form.
        // TryMatch ignores the title part of a GUID name so a file follows its game whatever the
        // title becomes; narrowing on the title first quietly undid that. The deferred rename writes
        // exactly the offending shape — "<OLD title>.<guid>-01.pdf" for a game already renamed — so
        // with the title glob alone a manual or a music track went INVISIBLE between the rename and
        // the flush. Images survive it through the id-keyed cache; nothing else does.
        IEnumerable<string> files;
        try
        {
            if (flat) return WalkFlat(dir, id, sani, exts);
            files = Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly);
            if (id != Guid.Empty)
                files = files.Concat(Directory.EnumerateFiles(dir, "*." + id.ToString("D") + "*",
                                                              SearchOption.TopDirectoryOnly));
        }
        catch { return null; }

        foreach (var f in files)
        {
            if (!exts.Contains(Path.GetExtension(f))) continue;
            // "best == null" en plus de la comparaison : le nom nu vaut long.MaxValue, et
            // "MaxValue < MaxValue" etant faux, il n'aurait jamais ete retenu meme seul.
            if (TryMatch(Path.GetFileNameWithoutExtension(f), id, sani, out long num, flat)
                && (best == null || num < bestNum))
            {
                best = f;
                bestNum = num;
            }
        }
        return best;
    }

    /// <summary>Matches a filename (no ext) to the game by GUID form or legacy form; out = the -NNN value.</summary>
    /// <summary>Does this file name designate that game? Exposed so the code that MOVES media can
    /// be audited against the code that FINDS it — two independent notions of ownership that nothing
    /// forces to agree.</summary>
    internal static bool BelongsTo(string nameNoExt, Guid id, string sani, bool flat = false)
        => TryMatch(nameNoExt, id, sani, out _, flat);

    /// <param name="allowUnnumbered">Accepte aussi le nom NU "&lt;titre&gt;" sans "-NN". Mesure sur
    /// LaunchBox, plateforme Nintendo Game Boy : depose seul, "&lt;titre&gt;.pdf" est bien reconnu
    /// comme le manuel du jeu ; depose a cote de "&lt;titre&gt;-01.pdf", c'est le fichier NUMEROTE qui
    /// l'emporte. D'ou le rang maximal ci-dessous : present, mais dernier servi. Le commentaire qui
    /// affirmait "LaunchBox numerote toujours, 49 sur 49" portait sur les images et a ete etendu aux
    /// manuels sans etre verifie.</param>
    private static bool TryMatch(string nameNoExt, Guid id, string sani, out long num,
                                 bool allowUnnumbered = false)
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
        if (allowUnnumbered && string.Equals(nameNoExt, sani, StringComparison.OrdinalIgnoreCase))
        {
            // Aucun rang a donner : pour les types plats c'est l'ordre de PARCOURS qui tranche, pas
            // le numero. Le numero ne sert plus qu'aux images et aux videos, qui n'acceptent pas
            // cette forme.
            num = long.MaxValue;
            return true;
        }
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

    /// <summary>L'ordre exact dans lequel LaunchBox retient un manuel : il DESCEND dans les
    /// sous-dossiers avant de regarder les fichiers du dossier courant, alphabetiquement a chaque
    /// niveau, et garde la PREMIERE correspondance.
    ///
    /// Mesure, et contre-intuitif : avec "&lt;jeu&gt;\&lt;jeu&gt;.pdf", "&lt;jeu&gt;\YYY\&lt;jeu&gt;.pdf" et
    /// "&lt;jeu&gt;\ZZZ\&lt;jeu&gt;.pdf", c'est YYY qui sort — alors que trier les chemins complets
    /// donnerait le fichier du dossier parent, 'W' venant avant 'Y'. Renommer un dossier suffit a
    /// changer le manuel affiche.
    ///
    /// Cette seule regle explique tout ce qui avait ete mesure, et remplace deux regles que j'avais
    /// inventees : il n'y a NI priorite au plus petit numero — "-01" precede "-02" par simple ordre
    /// alphabetique — NI preference pour le fichier numerote — "titre-01.pdf" precede "titre.pdf"
    /// parce que '-' (0x2D) precede '.' (0x2E). La date des fichiers n'intervient pas non plus :
    /// onze ans d'ecart, dans les deux sens, n'ont rien change.</summary>
    private static string WalkFlat(string dir, Guid id, string sani, HashSet<string> exts)
    {
        var into = new List<string>(4);
        WalkFlatAll(dir, id, sani, into, stopAtFirst: true);
        return into.Count > 0 ? into[0] : null;
    }

    /// <summary>Le meme parcours, mais collectant TOUTES les correspondances : la fenetre
    /// Documents montre la collection de manuels comme elle montre les images, et elle doit voir
    /// exactement ce que la resolution voit — une deuxieme implementation aurait derive.</summary>
    private static void WalkFlatAll(string dir, Guid id, string sani, List<string> into, bool stopAtFirst = false)
    {
        string[] subs, files;
        try
        {
            subs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch { return; }

        // ICI, et ici seulement, LiteBox s'ecarte de LaunchBox. La regle d'APPARTENANCE reste la
        // sienne — nom de fichier, toute profondeur, nom de dossier indifferent — mais l'ORDRE de
        // descente suit NOS priorites de region au lieu de l'alphabet. LaunchBox ne sait ordonner
        // que par nom de dossier : avec "Europe" et "North America" il prendra Europe, nous celui
        // que la priorite designe. Les deux peuvent donc afficher un manuel different pour un meme
        // jeu, et c'est un choix assume : LaunchBox applique ses priorites, nous les notres.
        //
        // Un dossier dont le nom n'est pas une region connue n'a pas de rang : il passe apres
        // toutes les regions, et se departage alphabetiquement.
        var order = RegionOrder();
        int Rank(string d)
        {
            string n = Path.GetFileName(d) ?? "";
            for (int k = 0; k < order.Count; k++)
                if (string.Equals(order[k], n, StringComparison.OrdinalIgnoreCase)) return k;
            return int.MaxValue;
        }
        Array.Sort(subs, (a, b) =>
        {
            int ra = Rank(a), rb = Rank(b);
            return ra != rb ? ra.CompareTo(rb)
                            : string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                                             StringComparison.OrdinalIgnoreCase);
        });
        foreach (var sub in subs)
        {
            WalkFlatAll(sub, id, sani, into, stopAtFirst);
            if (stopAtFirst && into.Count > 0) return;
        }

        Array.Sort(files, (a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                                                   StringComparison.OrdinalIgnoreCase));
        foreach (var f in files)
        {
            // exts n'est plus un filtre ici, volontairement : LaunchBox ne regarde pas l'extension.
            // Mesure — un raccourci ".lnk" pose au bon nom a bien ete retenu comme LE manuel du jeu,
            // et c'est l'OUVERTURE qui a echoue ensuite. Filtrer nous ferait diverger sur le choix
            // du fichier, ce qui est plus grave que de designer un format qu'on ne sait pas rendre :
            // l'appelant peut constater qu'il ne sait pas l'afficher, il ne peut pas deviner qu'il
            // regarde un autre fichier que LaunchBox.
            if (TryMatch(Path.GetFileNameWithoutExtension(f), id, sani, out _, allowUnnumbered: true))
            { into.Add(f); if (stopAtFirst) return; }
        }
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
