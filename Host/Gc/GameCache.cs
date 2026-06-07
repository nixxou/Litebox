// ─────────────────────────────────────────────────────────────────────────────
// AI / agent context — read this before touching the file
// ─────────────────────────────────────────────────────────────────────────────
//
// Purpose
//   In-memory cache of the image and video files present on disk for
//   every LaunchBox game. Used to answer queries like
//     • "does this game have a Front-Box image?"
//     • "what's the best Boxart for this game given the user's region/type
//        priorities?"
//     • "list all videos for this game"
//   without re-walking the filesystem on every call.
//
// Layered architecture
//   GameCache (static, top-level)
//     └── GameCachePlatform  (one per LB platform)
//           └── GameCacheGame  (one per game on that platform)
//                 ├── GameCacheImage[] (struct, immutable except lazy fields)
//                 └── GameCacheVideo[] (struct, immutable except lazy fields)
//
//   The structs are intentionally value types to keep the cache compact
//   in memory. Images/Videos are stored as flat arrays per game.
//   GameCacheImageRef / GameCacheVideoRef are thin reference wrappers
//   that let consumers mutate the lazy FileSize/Crc fields on the
//   underlying struct (via `ref`).
//
// Build-then-freeze pattern
//   While scanning, GameCacheGame holds a List<GameCacheImage> /
//   List<GameCacheVideo> (the "build" lists). When the platform finishes
//   scanning, every game's Freeze() is called:
//     • GUID filter applied: if at least one image of a given type has
//       a GUID in its filename, the non-GUID variants for that type are
//       dropped (they're considered legacy duplicates).
//     • Build lists are converted to arrays.
//     • The Volatile.Write swaps the _data snapshot atomically.
//     • Build lists are released for GC.
//   After freeze, the public API exposes Images/Videos as immutable
//   arrays. The only writes still allowed post-freeze are:
//     • FileSize / Crc lazy population (via Refs).
//     • ReplaceImages / ReplaceVideos used by the watcher for delta
//       updates (atomic snapshot replacement).
//
// Threading model
//   • A dedicated worker (Task.Run started in the static ctor) consumes
//     a ConcurrentQueue<RebuildJob>. Jobs are enqueued by the API
//     methods (Initialize / RebuildAll / RebuildPlatform).
//   • `IsGlobalReady` is a volatile bool flipped to true once the
//     initial RebuildAll completes; consumers gate their reads on it.
//   • `_platformsRebuilding` (HashSet<string>) tracks per-platform
//     rebuild state, guarded by `_rebuildingLock`.
//   • Reads through GameCacheGame use `_data` (volatile reference). A
//     reader observes either the previous snapshot or the new one — no
//     torn state.
//
// Filename patterns
//   Two patterns are recognized when associating a file to a game:
//     • GuidSuffixPattern: "{sanitizedTitle}.{guid}{middle?}-{NNN}.ext"
//       Example: "Sonic the Hedgehog.f1d23a92-...-1.png"
//       The {middle?} captures additional dash-separated segments
//       between the GUID and the number (rarely used in the wild).
//     • SuffixPattern (fallback): "{sanitizedTitle}-{NNN}.ext"
//       Example: "Sonic the Hedgehog-1.png"
//   Once a GUID variant is detected for a (game, image type), all
//   non-GUID variants for that type are filtered out at Freeze.
//
// File enumeration
//   Two backends:
//     • Everything (NTFS-indexed search): used when EverythingBridge
//       reports it's available. Returns FullPath + FileSize directly,
//       so we get the size for free.
//     • Directory.EnumerateFiles fallback: returns FullPath only;
//       FileSize is set to -1 ("unknown") and resolved lazily through
//       GameCacheImageRef.GetFileSize() / GameCacheVideoRef.GetFileSize().
//
// Critical invariants for editors
//   • All public types live in `namespace ExtendDB`.
//   • The static worker task NEVER terminates by design. Don't add
//     code that exits the WorkerLoop (it would silently kill the cache
//     for the rest of the session). Errors inside ExecuteJob are
//     caught and logged.
//   • RebuildPlatform / RebuildAll are non-blocking by default; pass
//     `wait: true` to await completion via the TaskCompletionSource on
//     the job.
//   • SetPlatformRebuilding fires the ReadyChanged event with the
//     platform name (or null on global change). UI code subscribes to
//     this; don't change the contract without updating consumers.
//   • The Volatile.Write on _data MUST stay volatile to guarantee
//     readers see the new array reference without missing updates.
//   • Image/Video structs use `ref` access through GameCacheImageRef /
//     GameCacheVideoRef. This is the only safe way to mutate FileSize
//     and Crc in-place after Freeze. Do NOT copy the struct out then
//     write it back — readers may have already cached a stale copy.
//   • This file does NOT honor DisableBackgroundWorkers. The cache is
//     central to many LB UI operations (image rendering, video lookup)
//     and disabling it would break the UX. The flag in ExtendDBConfig
//     is for Phase 3 stuff (DefaultOverviewCache rebuild), not this.
//
// Files that depend on this one
//   • Patches/* (e.g. ImageTypesPatches, VideoTypesPatches) — call
//     FindImages, FindVideos, GetBestImageRegionFirst, etc.
//   • Watchers/GameCacheWatcher — calls RebuildPlatform / RebuildAll
//     when LaunchBox emits relevant events.
//   • Forms/GameCacheDebugForm — reads the cache state for diagnostics.
//
// External dependencies in this file
//   • EverythingBridge       (Utility/Everything.cs) — fast file search
//   • CrcCache               (Utility/CrcCache.cs) — CRC32 + ADS storage
//   • Utils.LaunchboxFileNameSanitize — file-name sanitizer
//   • SettingsWatcher        — region & regroupement priorities
//   • ImageTypes             — list of LB image types
//   • PluginHelper.DataManager — LB plugin SDK (platform/game enumeration)
//
// ─────────────────────────────────────────────────────────────────────────────
// NOTE (for humans, short)
// ─────────────────────────────────────────────────────────────────────────────
//
// In-memory cache mapping every LaunchBox game to the image/video files
// available on disk for it. Built and rebuilt by a permanent background
// worker; reads are atomic snapshots. Used by patches and UI to answer
// "best image for X" and "list videos for X" without filesystem walks.
//
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using Path = System.IO.Path;

namespace LbApiHost.Host.Gc
{
    // ── Enums ────────────────────────────────────────────────────────────────

    /// <summary>Compact representation of a recognized image file extension.</summary>
    public enum ImageExt : byte
    {
        Jpg = 0,
        Jpeg = 1,
        Png = 2,
    }

    /// <summary>Compact representation of a recognized video file extension.</summary>
    public enum VideoExt : byte
    {
        Mp4 = 0,
        Avi = 1,
        Mkv = 2,
        Mov = 3,
        Wmv = 4,
        Webm = 5,
    }

    // ── ImageTypeRegistry ───────────────────────────────────────────────────

    /// <summary>
    /// Static registry mapping LaunchBox image-type names (e.g.
    /// "Front - Box") to compact byte indices used inside
    /// <see cref="GameCacheImage"/>. Populated once at the start of
    /// every full rebuild via <see cref="ImageTypes.GetList"/>.
    /// </summary>
    public static class ImageTypeRegistry
    {
        private static string[] _types;
        private static Dictionary<string, byte> _indexByName;

        /// <summary>Snapshot of the recognized image-type names, in registry order.</summary>
        public static string[] Types => _types;

        /// <summary>
        /// Refreshes the registry from <see cref="ImageTypes.GetList"/>.
        /// Called from <see cref="GameCache.ExecuteRebuildAll"/> at the
        /// start of every full rebuild, so the index assignments stay
        /// stable for the duration of the resulting cache.
        /// </summary>
        public static void Initialize()
        {
            _types = ImageTypes.GetList()
                .Where(t => t != null)
                .ToArray();

            _indexByName = _types
                .Select((name, i) => (name, i))
                .ToDictionary(
                    x => x.name,
                    x => (byte)x.i,
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the byte index for an image-type name. Case-insensitive.
        /// Returns false (and index=0) if the registry is not initialized
        /// or the name is unknown.
        /// </summary>
        public static bool TryGetIndex(string name, out byte index)
        {
            if (_indexByName == null) { index = 0; return false; }
            return _indexByName.TryGetValue(name, out index);
        }

        /// <summary>
        /// Returns the image-type name for a given byte index, or null
        /// if out of range.
        /// </summary>
        public static string GetName(byte index)
            => (_types != null && index < _types.Length) ? _types[index] : null;
    }

    // ── Structs ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-platform metadata about an image type's destination folder
    /// (default vs custom, relative vs absolute path, etc.).
    /// </summary>
    public struct GameCacheImageType
    {
        /// <summary>True if the folder is the LB-default location for this image type.</summary>
        public bool IsDefault;

        /// <summary>True if the folder path is relative to the LaunchBox install root.</summary>
        public bool IsRelative;

        /// <summary>Path as configured (relative or absolute).</summary>
        public string Path;

        /// <summary>Lower-cased absolute path with a trailing backslash, used for fast prefix matching.</summary>
        public string FullPathLowerWithSlash;

        /// <summary>Image type display name (e.g. "Front - Box").</summary>
        public string Name;
    }

    /// <summary>
    /// Compact representation of a single image file known to belong
    /// to a game. Stored as a value type in a flat array on the game.
    /// Some fields (FileSize, Crc) are populated lazily — see
    /// <see cref="GameCacheImageRef"/>.
    /// </summary>
    public struct GameCacheImage
    {
        /// <summary>Numeric value of the "-NNN" suffix in the filename.</summary>
        public int NumVal;

        /// <summary>Original textual length of the suffix, for round-trip formatting.</summary>
        public byte NumTextLen;

        /// <summary>Index into <see cref="ImageTypeRegistry"/>.</summary>
        public byte ImageTypeIndex;

        /// <summary>Compact extension token.</summary>
        public ImageExt Ext;

        /// <summary>Region subfolder (lower-case), or "none" if at root.</summary>
        public string Region;

        /// <summary>File size in bytes; -1 means "not yet resolved" (lazy via Ref).</summary>
        public long FileSize;

        /// <summary>CRC32; -1 means "not yet resolved" (lazy via Ref).</summary>
        public long Crc;

        /// <summary>True if the filename embeds the game's GUID.</summary>
        public bool HasGuid;

        /// <summary>Optional dash-separated segments between the GUID and the number (e.g. "-231-359"), or null if absent.</summary>
        public string GuidMiddle;

        /// <summary>Re-formats <see cref="NumVal"/> with leading zeros to match its original textual length.</summary>
        public string GetNumText() => NumVal.ToString().PadLeft(NumTextLen, '0');

        /// <summary>Resolves the image-type display name through <see cref="ImageTypeRegistry"/>.</summary>
        public string GetImageTypeName() => ImageTypeRegistry.GetName(ImageTypeIndex);
    }

    /// <summary>
    /// Compact representation of a single video file known to belong
    /// to a game. Stored as a value type in a flat array on the game.
    /// Some fields (FileSize) are populated lazily — see
    /// <see cref="GameCacheVideoRef"/>.
    /// </summary>
    public struct GameCacheVideo
    {
        /// <summary>Numeric value of the "-NNN" suffix in the filename.</summary>
        public int NumVal;

        /// <summary>Original textual length of the suffix, for round-trip formatting.</summary>
        public byte NumTextLen;

        /// <summary>Sub-folder name ("Trailer", "Theme", "Marquee", "Recordings") or null for the root.</summary>
        public string SubDir;

        /// <summary>Compact extension token.</summary>
        public VideoExt Ext;

        /// <summary>File size in bytes; -1 means "not yet resolved" (lazy via Ref).</summary>
        public long FileSize;

        /// <summary>True if the filename embeds the game's GUID.</summary>
        public bool HasGuid;

        /// <summary>Optional dash-separated segments between the GUID and the number, or null if absent.</summary>
        public string GuidMiddle;

        /// <summary>Re-formats <see cref="NumVal"/> with leading zeros to match its original textual length.</summary>
        public string GetNumText() => NumVal.ToString().PadLeft(NumTextLen, '0');
    }

    // ── Ref wrappers — by-reference access to structs in the array ──────────

    /// <summary>
    /// Lightweight wrapper that holds a reference to a slot inside the
    /// underlying <see cref="GameCacheImage"/> array. Lets callers mutate
    /// FileSize/Crc on the source struct (lazy resolution) without
    /// round-tripping through a copy.
    /// </summary>
    public class GameCacheImageRef
    {
        private readonly GameCacheImage[] _array;
        private readonly int _index;

        /// <summary>Direct ref to the struct in the source array.</summary>
        public ref GameCacheImage Value => ref _array[_index];

        /// <summary>Pre-resolved absolute path of this image file.</summary>
        public string FullPath { get; }

        /// <summary>Returns the image-type display name (e.g. "Front - Box").</summary>
        public string GetImageType() => _array[_index].GetImageTypeName();

        public GameCacheImageRef(GameCacheImage[] array, int index, string fullPath)
        {
            _array = array;
            _index = index;
            FullPath = fullPath;
        }

        /// <summary>
        /// Returns the file size. If unknown (-1), reads it from disk
        /// once and writes it back into the source struct.
        /// </summary>
        public long GetFileSize()
        {
            ref var img = ref _array[_index];
            if (img.FileSize >= 0) return img.FileSize;
            try
            {
                var fi = new FileInfo(FullPath);
                if (fi.Exists) img.FileSize = fi.Length;
            }
            catch { }
            return img.FileSize;
        }

        /// <summary>
        /// Returns the CRC32. If unknown (-1), reads it from the file's
        /// NTFS ADS or computes it (via <see cref="CrcCache"/>) and
        /// writes it back into the source struct.
        /// </summary>
        public long GetCrc()
        {
            ref var img = ref _array[_index];
            if (img.Crc >= 0) return img.Crc;
            try { img.Crc = CrcCache.GetCrc(FullPath); }
            catch { }
            return img.Crc;
        }

        /// <summary>
        /// Returns the list of regroupements ("Front", "Back",
        /// "Screenshots", …) this image's type belongs to, based on the
        /// user's settings.
        /// </summary>
        public List<string> GetRegroupements()
        {
            var result = new List<string>();
            string typeName = _array[_index].GetImageTypeName();
            if (typeName == null) return result;

            var regroupements = SettingsWatcher.GetImageRegroupementPriorities();
            foreach (var kvp in regroupements)
            {
                foreach (var t in kvp.Value)
                {
                    if (string.Equals(t, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(kvp.Key);
                        break;
                    }
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Lightweight wrapper that holds a reference to a slot inside the
    /// underlying <see cref="GameCacheVideo"/> array. Same pattern as
    /// <see cref="GameCacheImageRef"/> but for videos.
    /// </summary>
    public class GameCacheVideoRef
    {
        private readonly GameCacheVideo[] _array;
        private readonly int _index;

        /// <summary>Direct ref to the struct in the source array.</summary>
        public ref GameCacheVideo Value => ref _array[_index];

        /// <summary>Pre-resolved absolute path of this video file.</summary>
        public string FullPath { get; }

        public GameCacheVideoRef(GameCacheVideo[] array, int index, string fullPath)
        {
            _array = array;
            _index = index;
            FullPath = fullPath;
        }

        /// <summary>
        /// Returns the file size. If unknown (-1), reads it from disk
        /// once and writes it back into the source struct.
        /// </summary>
        public long GetFileSize()
        {
            ref var vid = ref _array[_index];
            if (vid.FileSize >= 0) return vid.FileSize;
            try
            {
                var fi = new FileInfo(FullPath);
                if (fi.Exists) vid.FileSize = fi.Length;
            }
            catch { }
            return vid.FileSize;
        }
    }

    // ── GameCacheData — atomic snapshot ─────────────────────────────────────

    /// <summary>
    /// Atomic snapshot of the images and videos arrays of a single game.
    /// Replaced wholesale (via <c>Volatile.Write</c>) when the game's
    /// content changes — readers therefore never observe torn state.
    /// </summary>
    public class GameCacheData
    {
        /// <summary>Singleton empty snapshot for newly-constructed games.</summary>
        public static readonly GameCacheData Empty = new()
        {
            Images = Array.Empty<GameCacheImage>(),
            Videos = Array.Empty<GameCacheVideo>(),
        };

        public GameCacheImage[] Images;
        public GameCacheVideo[] Videos;
    }

    // ── GameCacheGame ────────────────────────────────────────────────────────

    /// <summary>
    /// Cache entry for a single LaunchBox game. Holds a build-then-freeze
    /// pair of lists during scanning, then a frozen <see cref="GameCacheData"/>
    /// snapshot for fast reads.
    /// </summary>
    public class GameCacheGame
    {
        /// <summary>Builds an empty entry from a LaunchBox <see cref="IGame"/>.</summary>
        public GameCacheGame(IGame g, GameCachePlatform platform)
        {
            Id = Guid.Parse(g.Id);
            GuidSuffix = Id.ToString();
            Title = g.Title;
            Platform = platform;
        }

        /// <summary>Private constructor used by <see cref="DeepClone"/>.</summary>
        private GameCacheGame(Guid id, string title, GameCachePlatform platform, GameCacheData data)
        {
            Id = id;
            GuidSuffix = id.ToString();
            Title = title;
            Platform = platform;
            _data = data;
        }

        /// <summary>Game GUID (parsed from <c>IGame.Id</c>).</summary>
        public Guid Id;

        /// <summary>String form of the GUID, used to build filenames.</summary>
        public string GuidSuffix;

        /// <summary>Game title as known by LaunchBox.</summary>
        public string Title;

        /// <summary>Owning platform cache entry.</summary>
        public GameCachePlatform Platform { get; private set; }

        /// <summary>Atomic snapshot. Written via <c>Volatile.Write</c>; reads observe a single coherent array pair.</summary>
        private volatile GameCacheData _data = GameCacheData.Empty;

        /// <summary>Frozen images array; safe to iterate concurrently.</summary>
        public GameCacheImage[] Images => _data.Images;

        /// <summary>Frozen videos array; safe to iterate concurrently.</summary>
        public GameCacheVideo[] Videos => _data.Videos;

        /// <summary>
        /// True iff this game has at least one image of the given type
        /// stored in GUID-style filename. Used by the GUID filter logic.
        /// </summary>
        public bool UsesGuidFormat(byte typeIndex)
        {
            var imgs = _data.Images;
            for (int i = 0; i < imgs.Length; i++)
            {
                if (imgs[i].ImageTypeIndex == typeIndex && imgs[i].HasGuid)
                    return true;
            }
            return false;
        }

        // ── Build phase (pre-Freeze) ─────────────────────────────────────────

        private List<GameCacheImage> _imagesBuild = new();
        private List<GameCacheVideo> _videosBuild = new();

        /// <summary>Adds an image during the scan. Only valid before <see cref="Freeze"/>.</summary>
        public void AddImage(GameCacheImage img) => _imagesBuild.Add(img);

        /// <summary>Adds a video during the scan. Only valid before <see cref="Freeze"/>.</summary>
        public void AddVideo(GameCacheVideo vid) => _videosBuild.Add(vid);

        /// <summary>Resets the build lists (rare; used when re-doing a partial rebuild).</summary>
        public void ResetBuild()
        {
            _imagesBuild = new List<GameCacheImage>();
            _videosBuild = new List<GameCacheVideo>();
        }

        /// <summary>
        /// Closes the build phase. Applies the GUID filter (per type for
        /// images, global for videos), converts the build lists to
        /// arrays, atomically swaps the snapshot, and releases the build
        /// lists for GC.
        ///
        /// GUID filter: when both GUID-named and non-GUID-named files
        /// exist for the same image type, only the GUID-named ones are
        /// kept (the non-GUID ones are typically legacy LB exports).
        /// </summary>
        public void Freeze()
        {
            // Per-type GUID filter for images
            var guidTypeIndexes = new HashSet<byte>();
            foreach (var img in _imagesBuild)
            {
                if (img.HasGuid) guidTypeIndexes.Add(img.ImageTypeIndex);
            }
            if (guidTypeIndexes.Count > 0)
                _imagesBuild.RemoveAll(img => !img.HasGuid && guidTypeIndexes.Contains(img.ImageTypeIndex));

            // Same idea, but globally, for videos
            bool hasGuidVideos = _videosBuild.Any(v => v.HasGuid);
            if (hasGuidVideos)
                _videosBuild.RemoveAll(v => !v.HasGuid);

            Volatile.Write(ref _data, new GameCacheData
            {
                Images = _imagesBuild.ToArray(),
                Videos = _videosBuild.ToArray(),
            });
            _imagesBuild = null;
            _videosBuild = null;
        }

        /// <summary>
        /// Deep clone: copies the Images and Videos arrays. The clone
        /// points at the new platform.
        /// </summary>
        public GameCacheGame DeepClone(GameCachePlatform newPlatform)
        {
            var currentData = _data;
            var clonedData = new GameCacheData
            {
                Images = (GameCacheImage[])currentData.Images.Clone(),
                Videos = (GameCacheVideo[])currentData.Videos.Clone(),
            };
            return new GameCacheGame(Id, Title, newPlatform, clonedData);
        }

        /// <summary>
        /// Atomically replaces the images array (used by the watcher to
        /// apply incremental deltas).
        /// </summary>
        public void ReplaceImages(GameCacheImage[] newImages)
        {
            var current = _data;
            Volatile.Write(ref _data, new GameCacheData
            {
                Images = newImages,
                Videos = current.Videos,
            });
        }

        /// <summary>
        /// Atomically replaces the videos array (used by the watcher to
        /// apply incremental deltas).
        /// </summary>
        public void ReplaceVideos(GameCacheVideo[] newVideos)
        {
            var current = _data;
            Volatile.Write(ref _data, new GameCacheData
            {
                Images = current.Images,
                Videos = newVideos,
            });
        }

        // ── Lookup by reference ──────────────────────────────────────────────

        /// <summary>
        /// Returns the images that match the given type and region.
        /// Each returned ref points back at the original struct in the
        /// underlying array (so lazy FileSize/Crc population mutates
        /// the source).
        /// </summary>
        public List<GameCacheImageRef> FindImages(string imageType, string region)
        {
            var data = _data;
            var result = new List<GameCacheImageRef>();

            for (int i = 0; i < data.Images.Length; i++)
            {
                ref var img = ref data.Images[i];
                var name = img.GetImageTypeName();
                if (name == null) continue;
                if (!name.Equals(imageType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!img.Region.Equals(region, StringComparison.OrdinalIgnoreCase)) continue;

                string fullPath = Platform.ResolveImagePath(this, img);
                result.Add(new GameCacheImageRef(data.Images, i, fullPath));
            }
            return result;
        }

        /// <summary>
        /// Returns the best image for a given regroupement
        /// ("Front", "Back", etc.), with image type as the dominant axis.
        /// Tiebreakers: type rank → region rank (with "none" last) →
        /// lowest <see cref="GameCacheImage.NumVal"/>.
        /// Returns null if nothing matches.
        /// </summary>
        public GameCacheImageRef GetBestImageTypeFirst(string regroupement)
        {
            var regroupements = SettingsWatcher.GetImageRegroupementPriorities();
            if (!regroupements.TryGetValue(regroupement, out var typeNames)) return null;

            var regionPriorities = SettingsWatcher.GetRegionPriorities()
                .Select(r => r.ToLowerInvariant()).ToList();
            if (!regionPriorities.Contains("none"))
                regionPriorities.Add("none");

            var data = _data;

            foreach (var typeName in typeNames)
            {
                if (!ImageTypeRegistry.TryGetIndex(typeName, out byte typeIndex)) continue;

                GameCacheImageRef best = null;

                foreach (var region in regionPriorities)
                {
                    // Look at every image of this type+region combination
                    for (int i = 0; i < data.Images.Length; i++)
                    {
                        ref var img = ref data.Images[i];
                        if (img.ImageTypeIndex != typeIndex) continue;
                        if (!img.Region.Equals(region, StringComparison.OrdinalIgnoreCase)) continue;

                        if (best == null || img.NumVal < best.Value.NumVal)
                        {
                            string fullPath = Platform.ResolveImagePath(this, img);
                            best = new GameCacheImageRef(data.Images, i, fullPath);
                        }
                    }

                    // First region that yields at least one image wins
                    if (best != null) return best;
                }
            }

            return null;
        }

        /// <summary>
        /// Best image of ONE EXACT image type ("Screenshot - Game Title", "Box - Front", …),
        /// region-priority ("none" last), lowest <see cref="GameCacheImage.NumVal"/>. Returns
        /// null when the type is unknown or no image matches. Unlike the regroupement helpers,
        /// this targets a single explicit LB image type.
        /// </summary>
        public GameCacheImageRef GetBestImageOfType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (!ImageTypeRegistry.TryGetIndex(typeName, out byte typeIndex)) return null;

            var regionPriorities = SettingsWatcher.GetRegionPriorities()
                .Select(r => r.ToLowerInvariant()).ToList();
            if (!regionPriorities.Contains("none"))
                regionPriorities.Add("none");

            var data = _data;
            foreach (var region in regionPriorities)
            {
                GameCacheImageRef best = null;
                for (int i = 0; i < data.Images.Length; i++)
                {
                    ref var img = ref data.Images[i];
                    if (img.ImageTypeIndex != typeIndex) continue;
                    if (!img.Region.Equals(region, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || img.NumVal < best.Value.NumVal)
                    {
                        string fullPath = Platform.ResolveImagePath(this, img);
                        best = new GameCacheImageRef(data.Images, i, fullPath);
                    }
                }
                if (best != null) return best;   // 1re région prioritaire qui a une image
            }
            return null;
        }

        /// <summary>
        /// All images of ONE EXACT type across ALL regions: priority regions first ("none" last),
        /// then any OTHER region present on the game (for topping up when the preferred regions
        /// aren't enough). De-duplicated by path, lowest <see cref="GameCacheImage.NumVal"/> first
        /// within a region. Up to <paramref name="max"/>. Empty if the type is unknown / no image.
        /// </summary>
        public List<GameCacheImageRef> GetAllImagesOfType(string typeName, int max)
        {
            var result = new List<GameCacheImageRef>();
            if (max <= 0 || string.IsNullOrEmpty(typeName)) return result;
            if (!ImageTypeRegistry.TryGetIndex(typeName, out byte typeIndex)) return result;

            var data = _data;

            // Region order: configured priorities (+ "none"), then any other region this game has.
            var order = SettingsWatcher.GetRegionPriorities().Select(r => r.ToLowerInvariant()).ToList();
            if (!order.Contains("none")) order.Add("none");
            for (int i = 0; i < data.Images.Length; i++)
            {
                if (data.Images[i].ImageTypeIndex != typeIndex) continue;
                var reg = (data.Images[i].Region ?? "").ToLowerInvariant();
                if (!order.Contains(reg)) order.Add(reg);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var region in order)
            {
                var idxs = new List<int>();
                for (int i = 0; i < data.Images.Length; i++)
                {
                    if (data.Images[i].ImageTypeIndex != typeIndex) continue;
                    if (!data.Images[i].Region.Equals(region, StringComparison.OrdinalIgnoreCase)) continue;
                    idxs.Add(i);
                }
                idxs.Sort((a, b) => data.Images[a].NumVal.CompareTo(data.Images[b].NumVal));
                foreach (var i in idxs)
                {
                    ref var img = ref data.Images[i];
                    string fullPath = Platform.ResolveImagePath(this, img);
                    if (string.IsNullOrEmpty(fullPath) || !seen.Add(fullPath)) continue;
                    result.Add(new GameCacheImageRef(data.Images, i, fullPath));
                    if (result.Count >= max) return result;
                }
            }
            return result;
        }

        /// <summary>
        /// Returns up to <paramref name="max"/> images for a given regroupement
        /// ("Screenshots", …), ordered like <see cref="GetBestImageTypeFirst"/>
        /// (type rank → region rank with "none" last → NumVal), de-duplicated by
        /// resolved path. Used to fill the screenshot grid. Empty list if nothing.
        /// </summary>
        public List<GameCacheImageRef> GetAllImagesTypeFirst(string regroupement, int max)
        {
            var result = new List<GameCacheImageRef>();
            if (max <= 0) return result;

            var regroupements = SettingsWatcher.GetImageRegroupementPriorities();
            if (!regroupements.TryGetValue(regroupement, out var typeNames)) return result;

            var regionPriorities = SettingsWatcher.GetRegionPriorities()
                .Select(r => r.ToLowerInvariant()).ToList();
            if (!regionPriorities.Contains("none"))
                regionPriorities.Add("none");

            var data = _data;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeName in typeNames)
            {
                if (!ImageTypeRegistry.TryGetIndex(typeName, out byte typeIndex)) continue;

                foreach (var region in regionPriorities)
                {
                    var idxs = new List<int>();
                    for (int i = 0; i < data.Images.Length; i++)
                    {
                        if (data.Images[i].ImageTypeIndex != typeIndex) continue;
                        if (!data.Images[i].Region.Equals(region, StringComparison.OrdinalIgnoreCase)) continue;
                        idxs.Add(i);
                    }
                    idxs.Sort((a, b) => data.Images[a].NumVal.CompareTo(data.Images[b].NumVal));

                    foreach (var i in idxs)
                    {
                        ref var img = ref data.Images[i];
                        string fullPath = Platform.ResolveImagePath(this, img);
                        if (string.IsNullOrEmpty(fullPath) || !seen.Add(fullPath)) continue;
                        result.Add(new GameCacheImageRef(data.Images, i, fullPath));
                        if (result.Count >= max) return result;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the best image for a given regroupement, with region
        /// as the dominant axis.
        /// Score = region_rank * 100000 + type_rank * 1000 + NumVal.
        /// Lowest score wins. Returns null if nothing matches.
        /// </summary>
        public GameCacheImageRef GetBestImageRegionFirst(string regroupement)
        {
            var regroupements = SettingsWatcher.GetImageRegroupementPriorities();
            if (!regroupements.TryGetValue(regroupement, out var typeNames)) return null;

            var regionPriorities = SettingsWatcher.GetRegionPriorities()
                .Select(r => r.ToLowerInvariant()).ToList();

            // Type rank (1-based), excluding forced types
            var typeRank = new Dictionary<byte, int>();
            for (int t = 0; t < typeNames.Count; t++)
            {
                if (ImageTypeRegistry.TryGetIndex(typeNames[t], out byte idx))
                    typeRank[idx] = t + 1;
            }

            // Region rank (1-based); enabled regions plus "none" at the end
            var regionRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < regionPriorities.Count; r++)
                regionRank[regionPriorities[r]] = r + 1;
            if (!regionRank.ContainsKey("none"))
                regionRank["none"] = regionPriorities.Count + 1;

            var data = _data;
            GameCacheImageRef best = null;
            long bestScore = long.MaxValue;

            for (int i = 0; i < data.Images.Length; i++)
            {
                ref var img = ref data.Images[i];
                if (!typeRank.TryGetValue(img.ImageTypeIndex, out int tRank)) continue;

                string regionLower = img.Region.ToLowerInvariant();
                if (!regionRank.TryGetValue(regionLower, out int rRank)) continue;

                long score = (long)rRank * 100000 + (long)tRank * 1000 + img.NumVal;

                if (score < bestScore)
                {
                    bestScore = score;
                    string fullPath = Platform.ResolveImagePath(this, img);
                    best = new GameCacheImageRef(data.Images, i, fullPath);
                }
            }

            return best;
        }

        /// <summary>
        /// Returns the videos that match the given sub-folder (or null
        /// for the root). Each returned ref points back at the original
        /// struct in the underlying array.
        /// </summary>
        public List<GameCacheVideoRef> FindVideos(string subDir = null)
        {
            var data = _data;
            var result = new List<GameCacheVideoRef>();

            for (int i = 0; i < data.Videos.Length; i++)
            {
                ref var vid = ref data.Videos[i];
                if (!string.Equals(vid.SubDir, subDir, StringComparison.OrdinalIgnoreCase)) continue;

                string fullPath = Platform.ResolveVideoPath(this, vid);
                result.Add(new GameCacheVideoRef(data.Videos, i, fullPath));
            }
            return result;
        }

        /// <summary>Returns every video for the game, regardless of sub-folder.</summary>
        public List<GameCacheVideoRef> FindAllVideos()
        {
            var data = _data;
            var result = new List<GameCacheVideoRef>();

            for (int i = 0; i < data.Videos.Length; i++)
            {
                string fullPath = Platform.ResolveVideoPath(this, data.Videos[i]);
                result.Add(new GameCacheVideoRef(data.Videos, i, fullPath));
            }
            return result;
        }

        /// <summary>Cheap "any video?" check.</summary>
        public bool HasAnyVideo()
        {
            return _data.Videos.Length > 0;
        }

        /// <summary>Resolves the absolute path of an image.</summary>
        public string ResolveImagePath(GameCacheImage img)
            => Platform.ResolveImagePath(this, img);

        /// <summary>Resolves the absolute path of a video.</summary>
        public string ResolveVideoPath(GameCacheVideo vid)
            => Platform.ResolveVideoPath(this, vid);
    }

    // ── GameCachePlatform ────────────────────────────────────────────────────

    /// <summary>
    /// Cache entry for a single LaunchBox platform: holds the per-image-type
    /// folder configuration, the games dictionary (by GUID and by sanitized
    /// title), and the scan logic that walks the disk to populate the
    /// underlying <see cref="GameCacheGame"/> entries.
    /// </summary>
    public class GameCachePlatform
    {
        // Filename suffix patterns
        private static readonly Regex SuffixPattern =
            new(@"^(.+)-(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GuidSuffixPattern =
            new(@"^(.+)\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})((?:-[^-]+)*)-(\d+)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Recognized extensions
        private static readonly HashSet<string> ImageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

        private static readonly HashSet<string> VideoExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm" };

        // Recognized video sub-folders
        private static readonly string[] VideoSubDirs =
            { "Trailer", "Theme", "Marquee", "Recordings" };

        // ── Constructor ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds a fresh platform entry by enumerating its image-type
        /// folders and its games (no scan yet — see <see cref="ScanImages"/>
        /// and <see cref="ScanVideos"/>).
        /// </summary>
        public GameCachePlatform(IPlatform p)
        {
            Name = p.Name;
            SanitizeName = Utils.LaunchboxFileNameSanitize(Name);
            DefaultImagePathFull = Path.Combine(GcPaths.ImagePath, SanitizeName).Trim('\\');
            ImageTypeData = new Dictionary<string, GameCacheImageType>();
            GamesByUUID = new Dictionary<Guid, GameCacheGame>();

            // ── VideoPath ─────────────────────────────────────────────────
            string customVideoPath = p.VideoPath;
            if (string.IsNullOrWhiteSpace(customVideoPath))
            {
                VideoPath = Path.Combine("Videos", SanitizeName);
                VideoPathIsRelative = true;
            }
            else
            {
                VideoPath = customVideoPath;
                VideoPathIsRelative = VideoPath.StartsWith(GcPaths.LBPath,
                    StringComparison.OrdinalIgnoreCase);
            }

            // ── ImageTypeData ─────────────────────────────────────────────
            var gamesBySanitizedNameBuild =
                new Dictionary<string, List<GameCacheGame>>(StringComparer.OrdinalIgnoreCase);

            foreach (var imageType in ImageTypes.GetList())
            {
                if (imageType == null) continue;
                var res = Utils.GetPlatformFolderByImageType(p, imageType);
                string defaultPathForThisType = Path.Combine(DefaultImagePathFull,
                    Utils.LaunchboxFileNameSanitize(imageType));
                string resolvedFolderPath = Path.IsPathRooted(res.FolderPath)
                    ? res.FolderPath
                    : Path.GetFullPath(res.FolderPath);
                string fullPathLowerNoSlash = resolvedFolderPath.TrimEnd('\\').Replace('/', '\\').ToLower();
                string defaultNormalized = defaultPathForThisType.TrimEnd('\\').Replace('/', '\\').ToLower();
                bool isDefault = defaultNormalized == fullPathLowerNoSlash;
                bool isRelative = !Path.IsPathRooted(res.FolderPath);

                ImageTypeData.Add(imageType, new GameCacheImageType
                {
                    IsDefault = isDefault,
                    IsRelative = isRelative,
                    Path = res.FolderPath,
                    Name = imageType,
                    FullPathLowerWithSlash = fullPathLowerNoSlash + '\\'
                });
            }

            // ── Games ─────────────────────────────────────────────────────
            foreach (var g in p.GetAllGames(true, true))
            {
                if (g == null) continue;
                if (!Guid.TryParse(g.Id, out var guid)) continue;

                var newg = new GameCacheGame(g, this);
                GamesByUUID[guid] = newg;

                string sanitizedName = Utils.LaunchboxFileNameSanitize(g.Title).ToLower().Trim();
                if (string.IsNullOrEmpty(sanitizedName)) continue;

                if (!gamesBySanitizedNameBuild.TryGetValue(sanitizedName, out var list))
                    gamesBySanitizedNameBuild[sanitizedName] = list = new List<GameCacheGame>();
                list.Add(newg);
            }

            GamesBySanitizedName = gamesBySanitizedNameBuild.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        // ── Properties ──────────────────────────────────────────────────────

        /// <summary>Platform display name.</summary>
        public string Name { get; private set; }

        /// <summary>Sanitized version of <see cref="Name"/>, safe for filesystem use.</summary>
        public string SanitizeName { get; private set; }

        /// <summary>Configured video path (relative or absolute).</summary>
        public string VideoPath { get; private set; }

        /// <summary>True iff <see cref="VideoPath"/> is relative to the LaunchBox install root.</summary>
        public bool VideoPathIsRelative { get; private set; }

        /// <summary>Default per-platform image folder, in absolute form.</summary>
        public string DefaultImagePathFull { get; private set; }

        /// <summary>Per-image-type folder configuration, keyed by image-type display name.</summary>
        public Dictionary<string, GameCacheImageType> ImageTypeData { get; set; }

        /// <summary>Game cache entries by GUID.</summary>
        public Dictionary<Guid, GameCacheGame> GamesByUUID { get; set; }

        /// <summary>Game cache entries by sanitized title (multiple games may share one name).</summary>
        public Dictionary<string, GameCacheGame[]> GamesBySanitizedName { get; set; }

        /// <summary>Absolute video path (resolved from <see cref="VideoPath"/> + relativity flag).</summary>
        public string VideoPathAbsolute => VideoPathIsRelative
            ? Path.Combine(GcPaths.LBPath, VideoPath)
            : VideoPath;

        /// <summary>
        /// Deep clone: every dictionary and every <see cref="GameCacheGame"/>
        /// is copied. <see cref="ImageTypeData"/> values (immutable structs)
        /// are shallow-copied.
        /// </summary>
        public GameCachePlatform DeepClone()
        {
            var clone = (GameCachePlatform)MemberwiseClone();

            clone.ImageTypeData = new Dictionary<string, GameCacheImageType>(ImageTypeData);

            clone.GamesByUUID = new Dictionary<Guid, GameCacheGame>();
            foreach (var kvp in GamesByUUID)
                clone.GamesByUUID[kvp.Key] = kvp.Value.DeepClone(clone);

            var nameMap = new Dictionary<string, List<GameCacheGame>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in GamesBySanitizedName)
            {
                var clonedGames = new List<GameCacheGame>();
                foreach (var game in kvp.Value)
                {
                    if (clone.GamesByUUID.TryGetValue(game.Id, out var clonedGame))
                        clonedGames.Add(clonedGame);
                }
                if (clonedGames.Count > 0)
                    nameMap[kvp.Key] = clonedGames;
            }
            clone.GamesBySanitizedName = nameMap.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            return clone;
        }

        // ── Image scan ──────────────────────────────────────────────────────

        /// <summary>
        /// Walks the platform's image folders (default + custom paths)
        /// and populates the build lists of every <see cref="GameCacheGame"/>.
        /// Uses Everything when available, falls back to Directory.EnumerateFiles.
        /// </summary>
        public void ScanImages()
        {
            bool useEverything = EverythingBridge.IsEverythingAvailable();

            // Default image root (normalized, lowercased, trailing slash). Splits
            // image types into "inner" (configured folder under this root → found
            // by the single recursive sweep) and "outer" (folder elsewhere →
            // scanned individually).
            string rootLower = DefaultImagePathFull.TrimEnd('\\').Replace('/', '\\').ToLower() + "\\";

            // ── 1. Single recursive sweep of the default root ─────────────
            //
            // A file's image TYPE is decided by matching its path against each
            // type's CONFIGURED folder (FullPathLowerWithSlash), NOT by assuming
            // the first sub-folder is named after the type. This keeps discovery
            // and path reconstruction (ResolveImagePath) — and the watcher's
            // ValidateImagePath, which already works this way — on the SAME
            // source. So a type whose LB-configured folder differs from its name
            // (e.g. "Box - Front" → "Front") is found at its real folder, and a
            // file sitting in a folder merely *named* like a type but not
            // configured there is ignored (exactly like LaunchBox itself).
            if (Directory.Exists(DefaultImagePathFull))
            {
                // Inner types, longest configured path first (longest-prefix wins
                // for any nested type folders).
                var innerTypes = ImageTypeData.Values
                    .Where(t => t.FullPathLowerWithSlash != null
                             && t.FullPathLowerWithSlash.StartsWith(rootLower, StringComparison.Ordinal))
                    .OrderByDescending(t => t.FullPathLowerWithSlash.Length)
                    .ToList();

                foreach (var fi in EnumerateFiles(DefaultImagePathFull, ImageExtensions, useEverything))
                {
                    var lowerFile = fi.FullPath.ToLower().Replace('/', '\\');

                    GameCacheImageType imtype = default;
                    bool matched = false;
                    foreach (var t in innerTypes)
                        if (lowerFile.StartsWith(t.FullPathLowerWithSlash, StringComparison.Ordinal))
                        { imtype = t; matched = true; break; }
                    if (!matched) continue;   // not under any configured type folder → ignore

                    // Remainder after the type folder: "<region>\<file>" or "<file>".
                    string remainder = lowerFile.Substring(imtype.FullPathLowerWithSlash.Length);
                    int slash = remainder.IndexOf('\\');
                    if (slash != -1 && remainder.IndexOf('\\', slash + 1) != -1) continue;   // too deep

                    string region = slash == -1 ? "none" : remainder.Substring(0, slash);
                    ProcessImageFile(lowerFile, Path.GetExtension(lowerFile), region, imtype, fi.FileSize);
                }
            }

            // ── 2. Custom paths OUTSIDE the default root ──────────────────
            foreach (var imtype in ImageTypeData.Values.Where(i =>
                i.FullPathLowerWithSlash == null
                || !i.FullPathLowerWithSlash.StartsWith(rootLower, StringComparison.Ordinal)))
            {
                string absolutePath = imtype.IsRelative
                    ? Path.GetFullPath(imtype.Path)
                    : imtype.Path;

                if (!Directory.Exists(absolutePath)) continue;

                int imtypePathLen = absolutePath.TrimEnd('\\').Length + 1;

                foreach (var fi in EnumerateFiles(absolutePath, ImageExtensions, useEverything))
                {
                    var lowerFile = fi.FullPath.ToLower();
                    string relative = lowerFile.Substring(imtypePathLen);

                    int slashIdx = relative.IndexOf('\\');
                    if (slashIdx != -1 && relative.IndexOf('\\', slashIdx + 1) != -1) continue;

                    string region = slashIdx == -1 ? "none" : relative.Substring(0, slashIdx);
                    ProcessImageFile(lowerFile, Path.GetExtension(lowerFile), region, imtype, fi.FileSize);
                }
            }
        }

        // ── Video scan ──────────────────────────────────────────────────────

        /// <summary>
        /// Walks the platform's video root and known sub-folders
        /// (Trailer, Theme, Marquee, Recordings) and populates the
        /// build lists of every <see cref="GameCacheGame"/>.
        /// </summary>
        public void ScanVideos()
        {
            bool useEverything = EverythingBridge.IsEverythingAvailable();
            string absVideoPath = VideoPathAbsolute;
            if (!Directory.Exists(absVideoPath)) return;

            // Root (no sub-dir)
            ScanVideoDir(absVideoPath, null, useEverything);

            // Sub-folders
            foreach (var subDir in VideoSubDirs)
            {
                string dirPath = Path.Combine(absVideoPath, subDir);
                if (Directory.Exists(dirPath))
                    ScanVideoDir(dirPath, subDir, useEverything);
            }
        }

        private void ScanVideoDir(string dirPath, string subDir, bool useEverything)
        {
            foreach (var fi in EnumerateFiles(dirPath, VideoExtensions, useEverything, topOnly: true))
            {
                string ext = Path.GetExtension(fi.FullPath);
                if (!VideoExtensions.Contains(ext)) continue;

                string nameWithoutExt = Path.GetFileNameWithoutExtension(fi.FullPath);

                // Try the GUID pattern first
                var guidMatch = GuidSuffixPattern.Match(nameWithoutExt);
                if (guidMatch.Success)
                {
                    string guidStr = guidMatch.Groups[2].Value;
                    string middle = guidMatch.Groups[3].Value;
                    string numText = guidMatch.Groups[4].Value;

                    if (!Guid.TryParse(guidStr, out var guid)) continue;
                    if (!GamesByUUID.TryGetValue(guid, out var game)) continue;

                    game.AddVideo(new GameCacheVideo
                    {
                        NumVal = int.Parse(numText),
                        NumTextLen = (byte)numText.Length,
                        SubDir = subDir,
                        Ext = ParseVideoExt(ext),
                        FileSize = fi.FileSize,
                        HasGuid = true,
                        GuidMiddle = string.IsNullOrEmpty(middle) ? null : middle,
                    });
                    continue;
                }

                // Fallback without GUID
                var match = SuffixPattern.Match(nameWithoutExt);
                if (!match.Success) continue;

                string sanitizedGameName = match.Groups[1].Value.ToLower();
                if (!GamesBySanitizedName.TryGetValue(sanitizedGameName, out var games)) continue;

                string numTextNormal = match.Groups[2].Value;
                var vid = new GameCacheVideo
                {
                    NumVal = int.Parse(numTextNormal),
                    NumTextLen = (byte)numTextNormal.Length,
                    SubDir = subDir,
                    Ext = ParseVideoExt(ext),
                    FileSize = fi.FileSize,
                    HasGuid = false,
                };

                foreach (var g in games)
                    g.AddVideo(vid);
            }
        }

        // ── Freeze ──────────────────────────────────────────────────────────

        /// <summary>Freezes every game on the platform (calls <see cref="GameCacheGame.Freeze"/>).</summary>
        public void Freeze()
        {
            foreach (var g in GamesByUUID.Values)
                g.Freeze();
        }

        // ── Path resolution ─────────────────────────────────────────────────

        /// <summary>
        /// Resolves the absolute path of an image given its struct, by
        /// rebuilding the filename and joining with the configured
        /// folder for that image type.
        /// </summary>
        public string ResolveImagePath(GameCacheGame game, GameCacheImage img)
        {
            string imageTypeName = img.GetImageTypeName();
            if (imageTypeName == null) return null;
            if (!ImageTypeData.TryGetValue(imageTypeName, out var imtype)) return null;

            string basePath = imtype.IsDefault
                ? Path.Combine(DefaultImagePathFull, Utils.LaunchboxFileNameSanitize(imtype.Name))
                : (imtype.IsRelative ? Path.GetFullPath(imtype.Path) : imtype.Path);

            string sanitized = Utils.LaunchboxFileNameSanitize(game.Title);
            string fileName = img.HasGuid
                ? $"{sanitized}.{game.GuidSuffix}{img.GuidMiddle ?? ""}-{img.GetNumText()}{ImageExtToString(img.Ext)}"
                : $"{sanitized}-{img.GetNumText()}{ImageExtToString(img.Ext)}";

            return img.Region != "none"
                ? Path.Combine(basePath, img.Region, fileName)
                : Path.Combine(basePath, fileName);
        }

        /// <summary>
        /// Resolves the absolute path of a video given its struct, by
        /// rebuilding the filename and joining with the platform's
        /// (resolved) video folder and the optional sub-dir.
        /// </summary>
        public string ResolveVideoPath(GameCacheGame game, GameCacheVideo vid)
        {
            string absVideoPath = VideoPathAbsolute;
            string sanitized = Utils.LaunchboxFileNameSanitize(game.Title);
            string fileName = vid.HasGuid
                ? $"{sanitized}.{game.GuidSuffix}{vid.GuidMiddle ?? ""}-{vid.GetNumText()}{VideoExtToString(vid.Ext)}"
                : $"{sanitized}-{vid.GetNumText()}{VideoExtToString(vid.Ext)}";

            return vid.SubDir != null
                ? Path.Combine(absVideoPath, vid.SubDir, fileName)
                : Path.Combine(absVideoPath, fileName);
        }

        // ── Private helpers — enumeration ───────────────────────────────────

        /// <summary>
        /// Returns every file under <paramref name="absolutePath"/> with
        /// one of the given <paramref name="extensions"/>. Uses Everything
        /// when available (returns sizes for free), falls back to
        /// <see cref="Directory.EnumerateFiles"/> with FileSize=-1
        /// (lazy resolution at first read).
        /// </summary>
        private static FileInfoResult[] EnumerateFiles(
            string absolutePath, HashSet<string> extensions,
            bool useEverything, bool topOnly = false)
        {
            if (useEverything)
            {
                return EverythingBridge.GetFilesWithInfo(absolutePath, "*.*")
                    .Where(f => extensions.Contains(Path.GetExtension(f.FullPath)))
                    .ToArray();
            }

            var option = topOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            return Directory
                .EnumerateFiles(absolutePath, "*.*", option)
                .Where(f => extensions.Contains(Path.GetExtension(f)))
                .Select(f =>
                {
                    return new FileInfoResult
                    {
                        FullPath = f,
                        FileSize = -1,  // lazy — resolved by GetFileSize() later
                        DirectoryPath = Path.GetDirectoryName(f)
                    };
                })
                .ToArray();
        }

        // ── Private helpers — image filename parsing ────────────────────────

        /// <summary>
        /// Tries to associate a single image file with a game, creating
        /// the corresponding <see cref="GameCacheImage"/> entry. Tries
        /// the GUID pattern first, falls back to the plain suffix pattern.
        /// </summary>
        private void ProcessImageFile(
            string fullPathLower, string ext, string region,
            GameCacheImageType imtype, long fileSize)
        {
            if (!ImageTypeRegistry.TryGetIndex(imtype.Name, out byte typeIndex)) return;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(fullPathLower);

            // Try the GUID pattern first
            var guidMatch = GuidSuffixPattern.Match(nameWithoutExt);
            if (guidMatch.Success)
            {
                string sanitizedName = guidMatch.Groups[1].Value;
                string guidStr = guidMatch.Groups[2].Value;
                string middle = guidMatch.Groups[3].Value;  // e.g. "-231-359" or ""
                string numText = guidMatch.Groups[4].Value;

                if (!Guid.TryParse(guidStr, out var guid)) return;
                if (!GamesByUUID.TryGetValue(guid, out var game)) return;

                game.AddImage(new GameCacheImage
                {
                    NumVal = int.Parse(numText),
                    NumTextLen = (byte)numText.Length,
                    ImageTypeIndex = typeIndex,
                    Ext = ParseImageExt(ext),
                    Region = region,
                    FileSize = fileSize,
                    Crc = -1,
                    HasGuid = true,
                    GuidMiddle = string.IsNullOrEmpty(middle) ? null : middle,
                });
                return;
            }

            // Fallback pattern (no GUID)
            var match = SuffixPattern.Match(nameWithoutExt);
            if (!match.Success) return;
            if (!GamesBySanitizedName.TryGetValue(match.Groups[1].Value, out var games)) return;

            string numTextNormal = match.Groups[2].Value;
            var img = new GameCacheImage
            {
                NumVal = int.Parse(numTextNormal),
                NumTextLen = (byte)numTextNormal.Length,
                ImageTypeIndex = typeIndex,
                Ext = ParseImageExt(ext),
                Region = region,
                FileSize = fileSize,
                Crc = -1,
                HasGuid = false,
            };

            foreach (var g in games)
                g.AddImage(img);
        }

        // ── Private helpers — extension parsing ─────────────────────────────

        private static ImageExt ParseImageExt(string ext) => ext.ToLower() switch
        {
            ".jpeg" => ImageExt.Jpeg,
            ".png" => ImageExt.Png,
            _ => ImageExt.Jpg,
        };

        private static string ImageExtToString(ImageExt ext) => ext switch
        {
            ImageExt.Jpeg => ".jpeg",
            ImageExt.Png => ".png",
            _ => ".jpg",
        };

        private static VideoExt ParseVideoExt(string ext) => ext.ToLower() switch
        {
            ".avi" => VideoExt.Avi,
            ".mkv" => VideoExt.Mkv,
            ".mov" => VideoExt.Mov,
            ".wmv" => VideoExt.Wmv,
            ".webm" => VideoExt.Webm,
            _ => VideoExt.Mp4,
        };

        private static string VideoExtToString(VideoExt ext) => ext switch
        {
            VideoExt.Avi => ".avi",
            VideoExt.Mkv => ".mkv",
            VideoExt.Mov => ".mov",
            VideoExt.Wmv => ".wmv",
            VideoExt.Webm => ".webm",
            _ => ".mp4",
        };
    }

    // ── RebuildJob ───────────────────────────────────────────────────────────

    /// <summary>Scope of a rebuild request enqueued in <see cref="GameCache"/>'s queue.</summary>
    internal enum RebuildScope { Game, Platform, All }

    /// <summary>
    /// Single unit of work for the cache rebuild worker. Carries the
    /// scope, optional target (Game/Platform), and an optional
    /// TaskCompletionSource that lets the enqueuer await completion.
    /// </summary>
    internal class RebuildJob
    {
        public RebuildScope Scope { get; }
        public IGame Game { get; }
        public IPlatform Platform { get; }
        public TaskCompletionSource<bool> Tcs { get; }

        public RebuildJob(RebuildScope scope, IGame game = null,
                          IPlatform platform = null, bool wait = false)
        {
            Scope = scope;
            Game = game;
            Platform = platform;
            Tcs = wait
                ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }
    }

    // ── GameCache ────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level static manager. Holds the dictionary of platform caches
    /// and a permanent background worker that processes rebuild jobs from
    /// a concurrent queue.
    /// </summary>
    public static class GameCache
    {
        /// <summary>Platforms keyed by name.</summary>
        public static Dictionary<string, GameCachePlatform> Platforms = new();

        /// <summary>
        /// True once the initial RebuildAll has completed (every platform
        /// scanned at least once). Consumers gate their reads on this.
        /// </summary>
        public static volatile bool IsGlobalReady = false;

        /// <summary>Set of platforms currently being rebuilt; thread-safe via <see cref="_rebuildingLock"/>.</summary>
        private static readonly HashSet<string> _platformsRebuilding =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _rebuildingLock = new();

        /// <summary>
        /// Fired whenever a ready state changes (global or per-platform).
        /// The argument is the platform name, or null if the change is
        /// global (RebuildAll completion).
        /// </summary>
        public static event Action<string> ReadyChanged;

        /// <summary>True iff a specific platform is present and not currently rebuilding.</summary>
        public static bool IsPlatformReady(string platformName)
        {
            if (!IsGlobalReady) return false;
            lock (_rebuildingLock)
                return !_platformsRebuilding.Contains(platformName);
        }

        /// <summary>
        /// True iff every platform referenced by the given games is
        /// ready. No games supplied (null/empty) → just checks the
        /// global ready flag.
        /// </summary>
        public static bool AreAllReady(IGame[] games)
        {
            if (!IsGlobalReady) return false;
            if (games == null || games.Length == 0) return true;

            lock (_rebuildingLock)
            {
                foreach (var g in games)
                {
                    if (g?.Platform != null && _platformsRebuilding.Contains(g.Platform))
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns the list of platforms not yet ready among the given
        /// games. If the global cache itself isn't ready, returns
        /// "(initial scan)" as a placeholder.
        /// </summary>
        public static List<string> GetPendingPlatforms(IGame[] games)
        {
            var result = new List<string>();
            if (!IsGlobalReady)
            {
                result.Add("(initial scan)");
                return result;
            }
            if (games == null || games.Length == 0) return result;

            lock (_rebuildingLock)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in games)
                {
                    if (g?.Platform != null
                        && _platformsRebuilding.Contains(g.Platform)
                        && seen.Add(g.Platform))
                        result.Add(g.Platform);
                }
            }
            return result;
        }

        /// <summary>
        /// Marks a platform as rebuilding (or done), updates the set,
        /// and fires <see cref="ReadyChanged"/>.
        /// </summary>
        private static void SetPlatformRebuilding(string name, bool rebuilding)
        {
            lock (_rebuildingLock)
            {
                if (rebuilding) _platformsRebuilding.Add(name);
                else _platformsRebuilding.Remove(name);
            }
            Log($"Platform '{name}' rebuilding={rebuilding}");
            try { ReadyChanged?.Invoke(name); } catch { }
        }

        // ── Worker plumbing ─────────────────────────────────────────────────

        private static readonly ConcurrentQueue<RebuildJob> _queue = new();
        private static readonly SemaphoreSlim _signal = new(0);
        private static readonly Task _worker;

        /// <summary>Spawns the permanent background worker.</summary>
        static GameCache()
        {
            _worker = Task.Run(WorkerLoop);
        }

        /// <summary>
        /// Permanent worker loop: waits for a signal, drains the queue,
        /// and processes each job. Errors are caught per-job and logged
        /// (the loop never terminates by design).
        /// </summary>
        private static async Task WorkerLoop()
        {
            while (true)
            {
                await _signal.WaitAsync();

                while (_queue.TryDequeue(out var job))
                {
                    try
                    {
                        ExecuteJob(job);
                        job.Tcs?.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        Log($"Job error {job.Scope}: {ex.Message}");
                        job.Tcs?.SetException(ex);
                    }
                }
            }
        }

        /// <summary>Dispatches a job to the right execution path.</summary>
        private static void ExecuteJob(RebuildJob job)
        {
            switch (job.Scope)
            {
                case RebuildScope.Game: ExecuteRebuildGame(job.Game); break;
                case RebuildScope.Platform: ExecuteRebuildPlatform(job.Platform); break;
                case RebuildScope.All: ExecuteRebuildAll(); break;
            }
        }

        /// <summary>
        /// Kicks off the initial scan asynchronously. Non-blocking.
        /// <see cref="IsGlobalReady"/> flips to true when the scan finishes.
        /// </summary>
        public static void Initialize()
        {
            IsGlobalReady = false;

            var job = new RebuildJob(RebuildScope.All, wait: false);
            _queue.Enqueue(job);
            _signal.Release();

            Log("Initialize enqueued (async)");
        }

        // ── Rebuild API ─────────────────────────────────────────────────────

        /// <summary>
        /// Enqueues a rebuild for one platform. Skipped if a RebuildAll
        /// is already queued, or if the same platform is already queued.
        /// Pass <paramref name="wait"/>=true to await completion.
        /// </summary>
        public static Task RebuildPlatform(IPlatform platform, bool wait = false)
        {
            if (platform == null) return Task.CompletedTask;

            if (_queue.Any(j => j.Scope == RebuildScope.All))
            {
                Log($"RebuildPlatform ignored — RebuildAll queued");
                return Task.CompletedTask;
            }

            if (_queue.Any(j =>
                j.Scope == RebuildScope.Platform &&
                string.Equals(j.Platform?.Name, platform.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                Log($"RebuildPlatform already queued: {platform.Name}");
                return Task.CompletedTask;
            }

            // Mark the platform as rebuilding BEFORE enqueueing
            SetPlatformRebuilding(platform.Name, true);

            var job = new RebuildJob(RebuildScope.Platform, platform: platform, wait: wait);
            _queue.Enqueue(job);
            _signal.Release();
            Log($"RebuildPlatform enqueued: {platform.Name}");

            return wait ? job.Tcs!.Task : Task.CompletedTask;
        }

        /// <summary>
        /// Enqueues a full rebuild. Drops any pending jobs (they would
        /// be obsolete after a full scan). Pass <paramref name="wait"/>=true
        /// to await completion.
        /// </summary>
        public static Task RebuildAll(bool wait = false)
        {
            IsGlobalReady = false;

            int cleared = 0;
            while (_queue.TryDequeue(out var stale))
            {
                stale.Tcs?.SetResult(true);
                cleared++;
            }
            if (cleared > 0) Log($"RebuildAll — {cleared} stale job(s) cleared");

            var job = new RebuildJob(RebuildScope.All, wait: wait);
            _queue.Enqueue(job);
            _signal.Release();
            Log("RebuildAll enqueued");

            return wait ? job.Tcs!.Task : Task.CompletedTask;
        }

        // ── Execution ───────────────────────────────────────────────────────

        /// <summary>
        /// Rebuild for a single game: resolves its platform and runs
        /// <see cref="ExecuteRebuildPlatform"/> on it.
        /// </summary>
        private static void ExecuteRebuildGame(IGame game)
        {
            if (game == null) return;

            var platform = PluginHelper.DataManager.GetPlatformByName(game.Platform);
            if (platform == null) return;

            // SetPlatformRebuilding is handled inside ExecuteRebuildPlatform
            ExecuteRebuildPlatform(platform);
        }

        /// <summary>
        /// Rebuilds one platform: builds a fresh <see cref="GameCachePlatform"/>,
        /// scans images and videos, freezes, then atomically swaps the
        /// entry in <see cref="Platforms"/>.
        /// </summary>
        private static void ExecuteRebuildPlatform(IPlatform platform)
        {
            if (platform == null) return;
            string name = platform.Name;

            // Make sure we are flagged rebuilding (may already be the case
            // if the caller went through RebuildPlatform).
            SetPlatformRebuilding(name, true);

            try
            {
                Log($"RebuildPlatform: {name}");

                var newCache = new GameCachePlatform(platform);
                newCache.ScanImages();
                newCache.ScanVideos();
                newCache.Freeze();

                Platforms[name] = newCache;

                Log($"RebuildPlatform done: {name}");
            }
            finally
            {
                SetPlatformRebuilding(name, false);
            }
        }

        /// <summary>
        /// Full rebuild: refreshes the image type registry, builds fresh
        /// <see cref="GameCachePlatform"/> entries for every platform,
        /// scans them all (images then videos), freezes, swaps the
        /// dictionary, clears the rebuilding set, and flips
        /// <see cref="IsGlobalReady"/>.
        /// </summary>
        private static void ExecuteRebuildAll()
        {
            Log("RebuildAll — start");

            ImageTypeRegistry.Initialize();

            var newPlatforms = new Dictionary<string, GameCachePlatform>();

            foreach (var platform in PluginHelper.DataManager.GetAllPlatforms())
            {
                if (platform == null) continue;
                newPlatforms[platform.Name] = new GameCachePlatform(platform);
            }

            foreach (var p in newPlatforms.Values) p.ScanImages();
            foreach (var p in newPlatforms.Values) p.ScanVideos();
            foreach (var p in newPlatforms.Values) p.Freeze();

            Platforms = newPlatforms;

            // Drain any stale rebuilding markers
            lock (_rebuildingLock) _platformsRebuilding.Clear();

            IsGlobalReady = true;
            Log($"RebuildAll done: {newPlatforms.Count} platforms");

            try { ReadyChanged?.Invoke(null); }
            catch (Exception ex) { Log($"ReadyChanged event error: {ex.Message}"); }
        }

        // ── Fast lookup ─────────────────────────────────────────────────────

        /// <summary>
        /// Looks up games by sanitized title within a platform. Returns
        /// null if the platform isn't ready or no game matches.
        /// </summary>
        public static GameCacheGame[] FindGames(string platformName, string sanitizedGameName)
        {
            if (!IsPlatformReady(platformName)) return null;
            if (!Platforms.TryGetValue(platformName, out var platform)) return null;
            platform.GamesBySanitizedName.TryGetValue(sanitizedGameName, out var games);
            return games;
        }

        /// <summary>Routed log helper for this module.</summary>
        private static void Log(string message) =>
            GcPaths.Log("[GameCache] " + message);
    }
}