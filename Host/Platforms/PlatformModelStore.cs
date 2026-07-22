// Read/write a platform's 3D box <ModelSettings> block in Data\Platforms.xml. LaunchBox stores these as a
// ROOT-level <ModelSettings> element (sibling of <Platform>), keyed by <PlatformName> — NOT a child field of
// the platform. LiteBox's op-log write-back edits <Platform> nodes surgically (by Name) and leaves unknown root
// elements untouched, so a direct XDocument edit here is safe and survives a field flush. Field schema fully
// decoded (see memory reference-lb-3d-box-models): ModelType, ModelSizeString "W;H;D", CaseColor/CoverColor
// (ARGB int32), FullImageSpineWidth, UseFullScanImages/FullScanIsLandscape, FrontSpineImage/FrontSpineIsClear,
// DoubleSpineImageMode, LogoFont, SpineRotation/LogoRotation ("Left,Top,Right,Bottom" — value if drawn, empty if not).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Platforms;

internal static class PlatformModelStore
{
    private static string FilePath => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

    /// <summary>The platform's persisted ModelSettings as a field→value map (element order lost, irrelevant),
    /// or null when the platform has no override.</summary>
    public static Dictionary<string, string>? Read(string platformName)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var doc = XDocument.Load(FilePath);
            var el = doc.Root?.Elements("ModelSettings")
                .FirstOrDefault(e => string.Equals((string?)e.Element("PlatformName"), platformName, StringComparison.OrdinalIgnoreCase));
            if (el == null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in el.Elements()) map[c.Name.LocalName] = c.Value;
            return map;
        }
        catch { return null; }
    }

    /// <summary>Write (override ON) or remove (fields == null, override OFF) the platform's ModelSettings block.
    /// PlatformName is always stamped. Empty-valued fields are written as empty elements (LB keeps them).</summary>
    public static bool Write(string platformName, Dictionary<string, string>? fields)
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var doc = XDocument.Load(FilePath);
            var root = doc.Root;
            if (root == null) return false;

            foreach (var old in root.Elements("ModelSettings")
                     .Where(e => string.Equals((string?)e.Element("PlatformName"), platformName, StringComparison.OrdinalIgnoreCase)).ToList())
                old.Remove();

            if (fields != null)
            {
                var el = new XElement("ModelSettings");
                foreach (var kv in fields.Where(kv => !string.Equals(kv.Key, "PlatformName", StringComparison.OrdinalIgnoreCase)))
                    el.Add(new XElement(kv.Key, kv.Value ?? ""));
                el.Add(new XElement("PlatformName", platformName));
                el.Add(new XElement("GameId", ""));   // platform-level → empty GameId (LB parity)
                root.Add(el);
            }
            doc.Save(FilePath);
            return true;
        }
        catch { return false; }
    }

    // ── ARGB int32 (signed) ↔ Color helpers, matching Color.ToArgb() (e.g. red = -65536) ──
    public static System.Drawing.Color? ParseArgb(string? s)
        => int.TryParse(s, out var v) ? System.Drawing.Color.FromArgb(v) : (System.Drawing.Color?)null;
    public static string ToArgb(System.Drawing.Color c) => c.ToArgb().ToString();

    // ── SpineRotation / LogoRotation CSV "Left,Top,Right,Bottom" (value if drawn, empty if not) ──
    public static (bool draw, int rot)[] ParseSides(string? csv)
    {
        var r = new (bool, int)[4];
        var parts = (csv ?? "").Split(',');
        for (int i = 0; i < 4; i++)
        {
            string p = i < parts.Length ? parts[i].Trim() : "";
            r[i] = p.Length > 0 && int.TryParse(p, out var deg) ? (true, deg) : (false, 0);
        }
        return r;
    }
    public static string BuildSides(IReadOnlyList<(bool draw, int rot)> sides)
        => string.Join(",", Enumerable.Range(0, 4).Select(i => i < sides.Count && sides[i].draw ? sides[i].rot.ToString() : ""));
}
