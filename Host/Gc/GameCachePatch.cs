// ─────────────────────────────────────────────────────────────────────────────
// AI / agent context — read this before touching the file
// ─────────────────────────────────────────────────────────────────────────────
//
// Purpose
//   Incremental maintenance of the host GameCache, ONE FILE AT A TIME. When the
//   editor writes or deletes a single image, the cached image array of the game
//   that file belongs to is patched in place — instead of re-scanning the whole
//   platform (GameCache.RebuildPlatform → GameCachePlatform.ScanImages, a
//   recursive sweep of tens of thousands of files).
//
// Why it exists
//   That rebuild is asynchronous. The editor closes, MainWindow drops its poster
//   tiles and repaints IMMEDIATELY — re-resolving every source through a cache
//   that has not been re-scanned yet, so the art just downloaded was re-cached
//   as "the old file", or as "no art at all" (a cache hit that answers null does
//   NOT fall back to a disk walk). The tile then stayed stale for the whole
//   session, because nothing drops it a second time: changing the Image Group,
//   which rebuilds the poster geometry wholesale, was the only way out. Patching
//   synchronously, as the file is written, removes the race instead of racing
//   better — and costs one stat per file instead of a platform sweep.
//
// No watcher, on purpose
//   ExtendDB kept its cache in step with a FileSystemWatcher over the media folders (Watchers/
//   GameCacheWatcher — the file header of the ported GameCache.cs still mentions it). LiteBox has none,
//   and this is not it: the cache learns about a file because the code that WROTE it says so. Two
//   reasons that is the better half of the trade here. A watcher debounces — ExtendDB buffered 30 s —
//   so it would still be sitting on its timer when the editor closes and the poster repaints, which is
//   the very race being fixed. And a bulk download fires thousands of events through it (ExtendDB had
//   to quadruple its InternalBufferSize over exactly that) to re-learn what LiteBox already knew
//   first-hand. What a watcher WOULD buy is files written by someone else — LaunchBox running beside
//   us, a scraper, a folder drop by hand. Those still go unnoticed until something asks for a rebuild.
//
// Contract
//   Image(path, out platform) returns TRUE when the cache now agrees with the
//   disk about that file — including the cases where there is nothing to agree
//   about (an extension no scan indexes, a name no scan would attribute, a file
//   nested deeper than a region folder). It returns FALSE when the cache may now
//   be wrong and only a real scan can settle it; the caller then falls back to
//   RebuildPlatform for the platform named by "platform" (null when the file
//   could not even be placed). A caller must read false as "I owe this platform
//   a rebuild" — never as "the write failed".
//
// Mirrors the scan, deliberately
//   Everything here reproduces what ScanImages / ProcessImageFile / Freeze would
//   have recorded for the same file: the longest configured folder decides the
//   image TYPE, the remainder decides the REGION (lower-cased, "none" at the type
//   root), the two filename grammars decide the game, and the per-type GUID
//   filter is re-applied on insert. A patched entry must be indistinguishable
//   from a scanned one — ResolveImagePath rebuilds the file name out of it.
//
// Threading
//   Reads elsewhere are lock-free (GameCacheGame publishes an atomic snapshot).
//   The read-modify-write below is serialized by _lock so two writers cannot
//   drop each other's entry; it is only ever taken on a media write, never on
//   the display path.
//
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LbApiHost.Host.Gc
{
    internal static class GameCachePatch
    {
        /// <summary>Serializes the read-modify-write of a game's image array (media writes only).</summary>
        private static readonly object _lock = new();

        // The two grammars GameCachePlatform recognizes: "{name}-{NNN}" and "{name}.{guid}{middle?}-{NNN}".
        private static readonly Regex SuffixPattern =
            new(@"^(.+)-(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GuidSuffixPattern =
            new(@"^(.+)\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})((?:-[^-]+)*)-(\d+)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> ImageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

        /// <summary>Which (platform, image type, region) a FOLDER stands for, remembered across calls.
        /// A bulk session writes hundreds of files into a handful of folders, and answering this from
        /// scratch means walking every image type of every platform — the one part of a patch that grows
        /// with the size of the library. Keyed by the file's directory, since that is what decides all
        /// three. A null platform means "known folder, but nothing a scan would index here".</summary>
        private static readonly Dictionary<string, (string platform, string type, string region)> _folders =
            new(StringComparer.Ordinal);

        /// <summary>Names are stored, never object references: a rebuild swaps the platform snapshot, and a
        /// full one re-numbers the image-type registry. Everything is re-resolved by name on use, so a stale
        /// entry cannot outlive what it points at — it just fails the lookup and falls back to a scan.</summary>
        private static (string platform, string type, string region)? Locate(string lowerPath)
        {
            string dir = lowerPath.Substring(0, lowerPath.LastIndexOf('\\') + 1);
            lock (_folders)
                if (_folders.TryGetValue(dir, out var hit))
                {
                    // Re-checked, not trusted: LaunchBox can be told to move an image type's folder while
                    // this window is open. Two lookups and a prefix test — still nothing next to the walk.
                    if (hit.platform == null || StillCoveredBy(hit, dir)) return hit;
                    _folders.Remove(dir);
                }

            var plats = GameCache.Platforms;
            if (plats == null) return null;

            // The longest configured folder that prefixes it — the same rule ScanImages uses for nested type
            // folders, widened across platforms because a move can carry a file out of the one being edited.
            string ownerName = null, typeName = null;
            int baseLen = -1;
            foreach (var p in plats.Values)
            {
                if (p?.ImageTypeData == null) continue;
                foreach (var t in p.ImageTypeData.Values)
                {
                    string bas = t.FullPathLowerWithSlash;
                    if (bas == null || bas.Length <= baseLen) continue;
                    if (!dir.StartsWith(bas, StringComparison.Ordinal)) continue;
                    ownerName = p.Name; typeName = t.Name; baseLen = bas.Length;
                }
            }
            if (ownerName == null) return null;

            // What is left of the directory after the type folder: "" (the type root) or "<region>\".
            // Anything deeper the scan ignores too, so the cache is already right about it — remembered as
            // such so the walk above is not redone for every file in it.
            string remainder = dir.Substring(baseLen).TrimEnd('\\');
            (string platform, string type, string region) result = remainder.IndexOf('\\') != -1
                ? (null, null, null)
                : (ownerName, typeName, remainder.Length == 0 ? "none" : remainder);
            lock (_folders) _folders[dir] = result;
            return result;
        }

        /// <summary>Does the remembered (platform, type) still own this folder?</summary>
        private static bool StillCoveredBy((string platform, string type, string region) hit, string dir)
        {
            var plats = GameCache.Platforms;
            if (plats == null || !plats.TryGetValue(hit.platform, out var p) || p?.ImageTypeData == null) return false;
            if (!p.ImageTypeData.TryGetValue(hit.type, out var t) || t.FullPathLowerWithSlash == null) return false;
            return dir.StartsWith(t.FullPathLowerWithSlash, StringComparison.Ordinal);
        }

        /// <summary>One image file just appeared, changed or vanished on disk. Brings the cache in line
        /// with it. See the file header for what the return value obliges the caller to do.</summary>
        internal static bool Image(string fullPath, out string platform)
        {
            platform = null;
            if (string.IsNullOrEmpty(fullPath)) return true;
            try
            {
                string lower = Path.GetFullPath(fullPath).Replace('/', '\\').ToLowerInvariant();
                if (!ImageExtensions.Contains(Path.GetExtension(lower))) return true;   // no scan would index it

                var slot = Locate(lower);
                if (slot == null) return false;    // under no known image folder — a scan may still disagree
                if (slot.Value.platform == null) return true;   // known folder, but nested deeper than a region
                platform = slot.Value.platform;
                string region = slot.Value.region;

                var plats = GameCache.Platforms;
                if (plats == null || !plats.TryGetValue(platform, out var owner) || owner?.ImageTypeData == null) return false;
                if (!owner.ImageTypeData.TryGetValue(slot.Value.type, out var imtype)) return false;
                // Resolved by NAME on every call, never memoised: a full rebuild re-numbers the registry.
                if (!ImageTypeRegistry.TryGetIndex(imtype.Name, out byte typeIndex)) return false;

                string name = Path.GetFileNameWithoutExtension(lower);
                GameCacheGame[] targets;
                bool hasGuid;
                string guidMiddle = null;
                string numText;

                var guidMatch = GuidSuffixPattern.Match(name);
                if (guidMatch.Success && Guid.TryParse(guidMatch.Groups[2].Value, out var gid))
                {
                    // The GUID form names its game outright — which is also how a file whose title part went
                    // stale (a rename) still lands on the right one.
                    if (owner.GamesByUUID == null || !owner.GamesByUUID.TryGetValue(gid, out var one)) return false;
                    targets = new[] { one };
                    hasGuid = true;
                    guidMiddle = string.IsNullOrEmpty(guidMatch.Groups[3].Value) ? null : guidMatch.Groups[3].Value;
                    numText = guidMatch.Groups[4].Value;
                }
                else
                {
                    var m = SuffixPattern.Match(name);
                    if (!m.Success) return true;   // not a name the scan would attribute to anyone
                    // A sanitized title can be shared by several games — the scan hands the file to all of
                    // them, so the patch has to as well.
                    if (owner.GamesBySanitizedName == null
                        || !owner.GamesBySanitizedName.TryGetValue(m.Groups[1].Value, out targets)
                        || targets == null || targets.Length == 0)
                        return false;   // orphan: only a scan, which re-reads the game list, can place it
                    hasGuid = false;
                    numText = m.Groups[2].Value;
                }

                if (!int.TryParse(numText, out int numVal)) return true;

                // One stat answers both questions — does it exist, and how big is it. A file that vanished
                // between the write and here is a DELETE, which is exactly what should be recorded.
                long size = -1; bool exists;
                try { var fi = new FileInfo(fullPath); exists = fi.Exists; if (exists) size = fi.Length; }
                catch { return false; }

                var ext = ParseExt(Path.GetExtension(lower));
                bool ok = true;
                lock (_lock)
                {
                    foreach (var g in targets)
                    {
                        if (g == null) continue;
                        if (!Apply(g, imtype, typeIndex, numVal, (byte)numText.Length, region, ext, hasGuid, guidMiddle, size, exists))
                            ok = false;
                    }
                }
                return ok;
            }
            catch { return false; }
        }

        /// <summary>Applies one file's state to one game's image array and republishes it atomically.
        /// False = the array may now be incomplete and only a scan can fix it.</summary>
        private static bool Apply(GameCacheGame game, GameCacheImageType imtype, byte typeIndex, int numVal, byte numLen,
                                  string region, ImageExt ext, bool hasGuid, string guidMiddle, long size, bool exists)
        {
            var images = new List<GameCacheImage>(game.Images);

            // The slot a file occupies is its FILE NAME, nothing looser — every field the scanner keeps is
            // one ResolveImagePath puts back into the name, so two entries that differ in any of them are
            // two files on disk. Number alone is not identity: "foo-01.png" and "foo-001.png" share it, as
            // do "foo.{guid}-01.png" and "foo.{guid}-231-359-01.png". Merging them would swallow an add
            // (the survivor silently taking the newcomer's size, which is half of a ThumbCache key) and
            // take both out on the first delete — while still reporting success, so no scan would come.
            bool SameSlot(GameCacheImage i)
                => i.ImageTypeIndex == typeIndex && i.NumVal == numVal && i.NumTextLen == numLen
                   && i.HasGuid == hasGuid && i.Ext == ext
                   && string.Equals(i.GuidMiddle ?? "", guidMiddle ?? "", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(i.Region ?? "none", region, StringComparison.OrdinalIgnoreCase);

            if (exists)
            {
                // The GUID filter cuts both ways, and this is the direction the editor actually produces:
                // a plain-named file arriving where the type ALREADY has a GUID-named one. Freeze would
                // drop it, so the array must not hold it either — the cache is right about this file by
                // ignoring it. (ImgPrefix picks the naming form from the target FOLDER, while the filter
                // is per TYPE across every region, so a GUID file one region over is enough.)
                if (!hasGuid && images.Any(i => i.HasGuid && i.ImageTypeIndex == typeIndex)) return true;

                int at = images.FindIndex(SameSlot);
                if (at >= 0)
                {
                    var img = images[at];
                    if (img.FileSize == size) return true;   // same slot, same bytes — the cache already says this
                    img.FileSize = size;
                    img.Crc = -1;                            // the content changed; the stored CRC did not
                    images[at] = img;
                }
                else
                {
                    images.Add(new GameCacheImage
                    {
                        NumVal = numVal,
                        NumTextLen = numLen,
                        ImageTypeIndex = typeIndex,
                        Ext = ext,
                        Region = region,
                        FileSize = size,
                        Crc = -1,
                        HasGuid = hasGuid,
                        GuidMiddle = guidMiddle,
                    });
                    // Freeze's per-type GUID filter: once a type holds a GUID-named file, the legacy non-GUID
                    // ones of that type are dropped. A patched array must land where a rebuilt one would.
                    if (hasGuid) images.RemoveAll(i => !i.HasGuid && i.ImageTypeIndex == typeIndex);
                }
                game.ReplaceImages(images.ToArray());
                return true;
            }

            int removed = images.RemoveAll(SameSlot);
            if (removed == 0) return true;   // the cache never knew it — nothing to unlearn

            // The shadow that filter cast: with the last GUID-named file of this type gone, the plain-named
            // files it hid become pickable again — and they were dropped at Freeze, so the array does not
            // know them. They are read back from disk here rather than handed to a platform re-scan: a
            // deleted GUID image is common enough (LB-scraped libraries are full of them) that paying a
            // full sweep for it would put the poster back in the race this whole file exists to end.
            bool ok = true;
            if (hasGuid && !images.Any(i => i.HasGuid && i.ImageTypeIndex == typeIndex))
                ok = ReinstateShadowedPlain(imtype, typeIndex, game, images);

            game.ReplaceImages(images.ToArray());
            return ok;
        }

        /// <summary>Reads the plain-named files of ONE image type back into the array — the type's own
        /// folder plus its region sub-folders, one glob each, filtered on the game's sanitized title the
        /// same way the scan attributes them. False when the folders could not be read: the caller then
        /// falls back to a real scan rather than leave the array short.</summary>
        private static bool ReinstateShadowedPlain(GameCacheImageType imtype, byte typeIndex,
                                                   GameCacheGame game, List<GameCacheImage> images)
        {
            string root;
            try { root = imtype.IsRelative ? Path.GetFullPath(imtype.Path) : imtype.Path; }
            catch { return false; }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return true;   // nothing to reinstate

            // The CURRENT title, not the deleted file's name part: that is the key the scan attributes
            // plain-named files by, so a renamed game reinstates exactly what a scan would give it.
            string sani = Utils.LaunchboxFileNameSanitize(game.Title ?? "").ToLower().Trim();
            if (sani.Length == 0) return true;

            var dirs = new List<(string dir, string region)> { (root, "none") };
            try { foreach (var d in Directory.EnumerateDirectories(root)) dirs.Add((d, Path.GetFileName(d).ToLowerInvariant())); }
            catch { return false; }

            foreach (var (dir, region) in dirs)
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, sani + "-*", SearchOption.TopDirectoryOnly); }
                catch { return false; }
                foreach (var f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!ImageExtensions.Contains(ext)) continue;
                    var m = SuffixPattern.Match(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
                    if (!m.Success || !string.Equals(m.Groups[1].Value, sani, StringComparison.Ordinal)) continue;
                    if (!int.TryParse(m.Groups[2].Value, out int num)) continue;
                    images.Add(new GameCacheImage
                    {
                        NumVal = num,
                        NumTextLen = (byte)m.Groups[2].Value.Length,
                        ImageTypeIndex = typeIndex,
                        Ext = ParseExt(ext),
                        Region = region,
                        FileSize = -1,   // lazy, exactly as the directory-backed scan leaves it
                        Crc = -1,
                        HasGuid = false,
                    });
                }
            }
            return true;
        }

        private static ImageExt ParseExt(string extLower) => extLower switch
        {
            ".jpeg" => ImageExt.Jpeg,
            ".png" => ImageExt.Png,
            _ => ImageExt.Jpg,
        };
    }
}
