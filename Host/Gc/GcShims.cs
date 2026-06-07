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
    /// SettingsWatcher methods the GameCache consumes (cached once; LB needs a restart to change them).</summary>
    internal static class SettingsWatcher
    {
        private static readonly object _lock = new();
        private static List<string> _regions;
        private static Dictionary<string, List<string>> _regroup;

        private static string SettingsFile => Path.Combine(GcPaths.LBPath, "Data", "Settings.xml");

        private static string GetData(string key)
        {
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
