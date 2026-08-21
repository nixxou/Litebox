// Shims that let the verbatim-ported ExtendDB GameCache (GameCache.cs / Everything.cs) compile and
// run inside the LiteBox host. They stand in for the ExtendDB-only types the cache referenced
// (ExtendDBPlugin paths/log, Utility.Utils, Watchers.SettingsWatcher, Utility.CrcCache) — same
// names/signatures, host-backed implementations. Keeping these here means GameCache.cs stays
// byte-faithful to ExtendDB (only its namespace + the ExtendDBPlugin path refs were rewritten).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Gc
{
    /// <summary>Paths + log the ported GameCache expects (were ExtendDBPlugin.* in ExtendDB).</summary>
    internal static class GcPaths
    {
        public static string LBPath => MediaResolver.LbRoot ?? AppContext.BaseDirectory;
        public static string ImagePath => MediaResolver.ImagesRoot ?? Path.Combine(LBPath, "Images");
        public static void Log(string message) { try { Console.WriteLine(message); } catch { } }
    }

    /// <summary>Filename sanitizer + per-type folder resolution (ports the ExtendDB.Utility.Utils bits
    /// the GameCache uses), with the same LBPath-anchored fallback when LB has no configured folder.</summary>
    internal static class Utils
    {
        private static readonly Regex CollapseUnderscore = new("_{2,}", RegexOptions.Compiled);

        public static string LaunchboxFileNameSanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var s = new string(name.Select(c => (Array.IndexOf(invalid, c) >= 0 || c == '\'') ? '_' : c).ToArray());
            s = CollapseUnderscore.Replace(s, "_");
            return s.Trim();
        }

        public static IPlatformFolder GetPlatformFolderByImageType(IPlatform p, string type)
        {
            try
            {
                var res = p.GetPlatformFolderByImageType(type);
                if (res != null && !string.IsNullOrWhiteSpace(res.FolderPath)) return res;
            }
            catch { /* fall through to default */ }

            return new DummyPlatformFolder
            {
                MediaType = type,
                FolderPath = Path.GetFullPath(Path.Combine(GcPaths.LBPath, "Images",
                    LaunchboxFileNameSanitize(p.Name), LaunchboxFileNameSanitize(type))),
                Platform = p.Name,
            };
        }
    }

    /// <summary>Region + image-regroupement priorities read from &lt;LB&gt;\Data\Settings.xml — ports the two
    /// SettingsWatcher methods the GameCache consumes.
    ///
    /// The XML is NOT the whole truth: LiteBox journals its settings edits and can only write them back
    /// once LaunchBox releases the files, so a value the user has just changed lives in the journal for
    /// as long as LaunchBox stays open. Reading the raw file made these two lists disagree with the very
    /// options page that set them — the Add-image dialog kept offering two regions while Options showed
    /// five, restart included, because a restart does not flush anything either. <see cref="Overlay"/>
    /// is the journal-aware reader (installed at boot), and <see cref="Invalidate"/> drops the caches
    /// when a setting is applied, so the change lands without waiting for anything.</summary>
    internal static class SettingsWatcher
    {
        private static readonly object _lock = new();
        private static List<string> _regions;
        private static Dictionary<string, List<string>> _regroup;

        /// <summary>Reads one Settings.xml field the way the OPTIONS window sees it (file + pending
        /// journal): true when it HAS the field, value included even when empty. Null until installed;
        /// a field it does not hold falls through to the raw file.</summary>
        public delegate bool TryRead(string key, out string value);
        public static TryRead OverlayTry;

        /// <summary>Forget the cached lists: the next read re-reads. Call after applying settings.</summary>
        public static void Invalidate() { lock (_lock) { _regions = null; _regroup = null; } }

        // Invalidate ourselves whenever a setting moves, instead of trusting every writer to remember
        // this cache exists. A self-test caught exactly that: a change announced without the matching
        // Invalidate left these lists on their pre-edit values while every other reader had moved on.
        static SettingsWatcher()
        {
            try { Data.LiveSettings.Changed += Invalidate; } catch { }
        }

        private static string SettingsFile => Path.Combine(GcPaths.LBPath, "Data", "Settings.xml");

        private static string GetData(string key)
        {
            // The journal-aware view first: it is the one the user's own options window edits. An
            // EMPTY answer counts — a cleared list is a choice, and falling through to the file here
            // would let a stale value override it.
            try
            {
                if (OverlayTry != null && OverlayTry(key, out var v)) return v;
            }
            catch { }
            try
            {
                var f = SettingsFile;
                if (!File.Exists(f)) return null;
                return XDocument.Load(f).Root?.Element("Settings")?.Element(key)?.Value;
            }
            catch { return null; }
        }

        public static List<string> GetRegionPriorities()
        {
            if (_regions != null) return _regions;
            lock (_lock)
            {
                if (_regions != null) return _regions;
                var raw = GetData("RegionPriorities") ?? "";
                return _regions = new List<string>(raw.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        /// <summary>
        /// The region order to PICK images with: the user's RegionPriorities, then LaunchBox's own hard-coded
        /// fallback list, then the root ("none") last. Using only GetRegionPriorities() made images in an
        /// unlisted region (e.g. "Japan") permanently unpickable — see <see cref="Media.LbRegions"/>.
        /// Lower-cased. Not cached: LbRegions.Order is cheap and RegionPriorities is already memoised.
        /// </summary>
        public static List<string> GetRegionOrder() => Media.LbRegions.Order(GetRegionPriorities());

        public static Dictionary<string, List<string>> GetImageRegroupementPriorities()
        {
            if (_regroup != null) return _regroup;
            lock (_lock)
            {
                if (_regroup != null) return _regroup;
                List<string> P(string key) => new List<string>((GetData(key) ?? "").Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return _regroup = new Dictionary<string, List<string>>
                {
                    ["Front"] = P("FrontImageTypePriorities"),
                    ["Back"] = P("BackImageTypePriorities"),
                    ["Background"] = P("BackgroundImageTypePriorities"),
                    ["Screenshots"] = P("ScreenshotsImageTypePriorities"),
                    ["Marquee"] = P("MarqueeImageTypePriorities"),
                    ["Box3d"] = P("Box3dImageTypePriorities"),
                    ["CartFront"] = P("CartFrontImageTypePriorities"),
                    ["CartBack"] = P("CartBackImageTypePriorities"),
                    ["Cart3d"] = P("Cart3dImageTypePriorities"),
                    ["ClearLogo"] = new List<string> { "Clear Logo" },
                    ["BoxSpine"] = new List<string> { "Box - Spine" },
                    ["BoxFull"] = new List<string> { "Box - Full" },
                };
            }
        }
    }

    /// <summary>CRC32 cache — used only by ExtendDB's SearchRom, never by media display, so the host
    /// stubs it (the GameCacheImageRef.GetCrc path is unused here).</summary>
    internal static class CrcCache
    {
        public static long GetCrc(string filePath) => -1;
    }
}
