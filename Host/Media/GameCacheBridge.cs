// Fast path for media lookups: if ExtendDB is loaded AND its GameCache is in a
// ready state, delegate to it via reflection (the host can't reference ExtendDB)
// instead of walking the filesystem. When the cache isn't present/ready, callers
// fall back to MediaResolver's classic IO enumeration.
//
// Reflected surface (ExtendDB.GameCache, namespace ExtendDB):
//   static bool IsGlobalReady
//   static Dictionary<string, GameCachePlatform> Platforms
//     GameCachePlatform.GamesByUUID : Dictionary<Guid, GameCacheGame>
//       GameCacheGame.GetBestImageOfType(string) -> GameCacheImageRef { string FullPath }
//       GameCacheGame.FindVideos(string subDir)  -> List<GameCacheVideoRef> { string FullPath }
//       GameCacheGame.FindAllVideos()            -> List<GameCacheVideoRef>

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LbApiHost.Host.Gc;

namespace LbApiHost.Host.Media;

internal static class GameCacheBridge
{
    private const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static bool _probed;
    private static Type _gcType;
    private static FieldInfo _isReadyField;     // IsGlobalReady (field or backing)
    private static PropertyInfo _isReadyProp;
    private static MemberInfo _platformsMember;  // Platforms (field or property)

    // Per-instance-type method/prop caches (resolved lazily on first hit).
    private static PropertyInfo _gamesByUuidProp;
    private static MethodInfo _getBestImageOfType;
    private static MethodInfo _getBestImageTypeFirst;
    private static MethodInfo _getAllImagesTypeFirst;
    private static MethodInfo _findVideos;
    private static MethodInfo _findAllVideos;
    private static PropertyInfo _imgFullPath;
    private static PropertyInfo _vidFullPath;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            if (asm == null) return;

            _gcType = asm.GetType("ExtendDB.GameCache") ?? asm.GetType("ExtendDB.Cache.GameCache");
            if (_gcType == null) return;

            _isReadyField = _gcType.GetField("IsGlobalReady", SF);
            _isReadyProp = _gcType.GetProperty("IsGlobalReady", SF);
            _platformsMember = (MemberInfo)_gcType.GetField("Platforms", SF)
                               ?? _gcType.GetProperty("Platforms", SF);
        }
        catch { _gcType = null; }
    }

    private static object MemberValue(MemberInfo m, object target)
        => m is FieldInfo f ? f.GetValue(target)
         : m is PropertyInfo p ? p.GetValue(target)
         : null;

    /// <summary>True iff ExtendDB's GameCache TYPE is loaded (the plugin is present). When it is, we
    /// prefer ExtendDB's cache and do NOT build/use the host one.</summary>
    public static bool ExtendDbPresent { get { Probe(); return _gcType != null; } }

    /// <summary>True iff ExtendDB's GameCache is loaded, globally ready, and holds this platform.</summary>
    private static bool ExtendReady(string platformName)
    {
        Probe();
        if (_gcType == null || string.IsNullOrEmpty(platformName)) return false;
        try
        {
            bool global = _isReadyField != null
                ? (bool)_isReadyField.GetValue(null)
                : _isReadyProp != null && (bool)_isReadyProp.GetValue(null);
            if (!global) return false;

            return MemberValue(_platformsMember, null) is IDictionary plats && plats.Contains(platformName);
        }
        catch { return false; }
    }

    /// <summary>A cache (ExtendDB's, else the host's) can answer for this platform.</summary>
    public static bool Ready(string platformName)
        => ExtendReady(platformName) || HostGameCache.Ready(platformName);

    private static object GameObj(string platformName, Guid id)
    {
        if (MemberValue(_platformsMember, null) is not IDictionary plats) return null;
        if (!plats.Contains(platformName)) return null;
        var plat = plats[platformName];
        if (plat == null) return null;

        _gamesByUuidProp ??= plat.GetType().GetProperty("GamesByUUID", IF);
        if (_gamesByUuidProp?.GetValue(plat) is not IDictionary games) return null;
        return games.Contains(id) ? games[id] : null;
    }

    /// <summary>Best image path of an exact LB image type via the cache, or null.</summary>
    public static string BestImage(string platformName, Guid id, string imageType)
    {
        if (!ExtendReady(platformName)) return HostGameCache.BestImage(platformName, id, imageType);
        try
        {
            var game = GameObj(platformName, id);
            if (game == null) return null;

            _getBestImageOfType ??= game.GetType().GetMethod("GetBestImageOfType", new[] { typeof(string) });
            var imgRef = _getBestImageOfType?.Invoke(game, new object[] { imageType });
            if (imgRef == null) return null;

            _imgFullPath ??= imgRef.GetType().GetProperty("FullPath");
            return _imgFullPath?.GetValue(imgRef) as string;
        }
        catch { return null; }
    }

    /// <summary>Best image path for a REGROUPEMENT (e.g. "ClearLogo", "Front",
    /// "Screenshots", "Background") via the cache — same call launchbox-web/bigbox-web
    /// use (GetBestImageTypeFirst), so the resolved file (and thus the shared thumb
    /// cache key) matches. Null if unavailable.</summary>
    public static string BestImageTypeFirst(string platformName, Guid id, string regroupement)
    {
        if (!ExtendReady(platformName)) return HostGameCache.BestImageTypeFirst(platformName, id, regroupement);
        try
        {
            var game = GameObj(platformName, id);
            if (game == null) return null;

            _getBestImageTypeFirst ??= game.GetType().GetMethod("GetBestImageTypeFirst", new[] { typeof(string) });
            var imgRef = _getBestImageTypeFirst?.Invoke(game, new object[] { regroupement });
            if (imgRef == null) return null;

            _imgFullPath ??= imgRef.GetType().GetProperty("FullPath");
            return _imgFullPath?.GetValue(imgRef) as string;
        }
        catch { return null; }
    }

    /// <summary>All image paths for a REGROUPEMENT (GetAllImagesTypeFirst — same call the web
    /// uses for fanart/screenshot lists), up to <paramref name="max"/>. Empty if unavailable.</summary>
    public static List<string> AllImagesTypeFirst(string platformName, Guid id, string regroupement, int max)
    {
        if (!ExtendReady(platformName)) return HostGameCache.AllImagesTypeFirst(platformName, id, regroupement, max);
        var result = new List<string>();
        try
        {
            var game = GameObj(platformName, id);
            if (game == null) return result;
            _getAllImagesTypeFirst ??= game.GetType().GetMethod("GetAllImagesTypeFirst", new[] { typeof(string), typeof(int) });
            if (_getAllImagesTypeFirst?.Invoke(game, new object[] { regroupement, max }) is not IEnumerable list) return result;
            foreach (var r in list)
            {
                if (r == null) continue;
                _imgFullPath ??= r.GetType().GetProperty("FullPath");
                if (_imgFullPath?.GetValue(r) is string s && !string.IsNullOrEmpty(s)) result.Add(s);
            }
        }
        catch { }
        return result;
    }

    /// <summary>First video path for a sub-dir (null = root) via the cache, or null.</summary>
    public static string Video(string platformName, Guid id, string subDir)
    {
        if (!ExtendReady(platformName)) return HostGameCache.Video(platformName, id, subDir);
        try
        {
            var game = GameObj(platformName, id);
            if (game == null) return null;

            _findVideos ??= game.GetType().GetMethod("FindVideos", new[] { typeof(string) });
            if (_findVideos?.Invoke(game, new object[] { subDir }) is not IEnumerable list) return null;

            foreach (var vidRef in list)
            {
                if (vidRef == null) continue;
                _vidFullPath ??= vidRef.GetType().GetProperty("FullPath");
                if (_vidFullPath?.GetValue(vidRef) is string s && !string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>True if the game has any video at all (any sub-dir) via the cache.</summary>
    public static bool HasAnyVideo(string platformName, Guid id)
    {
        if (!ExtendReady(platformName)) return HostGameCache.HasAnyVideo(platformName, id);
        try
        {
            var game = GameObj(platformName, id);
            if (game == null) return false;
            _findAllVideos ??= game.GetType().GetMethod("FindAllVideos", Type.EmptyTypes);
            return _findAllVideos?.Invoke(game, null) is ICollection c && c.Count > 0;
        }
        catch { return false; }
    }
}
