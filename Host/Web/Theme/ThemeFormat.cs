// Small pure formatting helpers shared by the two theme data providers (BigBox Web + LiteBox Web). Keeping
// them in one place keeps the JSON contract identical regardless of which surface serves it — the shipped
// theme JS reads these exact field shapes (e.g. `y` is a STRING, "" when unknown; the related-card renderer
// does g.y.charCodeAt(3)).
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Theme/ThemeFormat.cs — pure string math, no plugin types.

using System;
using System.Globalization;
using System.Linq;
using System.Net;

namespace LbApiHost.Host.Web;

internal static class ThemeFormat
{
    /// <summary>Year as a string ("" when unknown / out of range).</summary>
    public static string YearStr(int? y) => (y.HasValue && y.Value > 0) ? y.Value.ToString() : "";

    /// <summary>First 4 chars of an ISO-ish date string, else "".</summary>
    public static string Year4(string date)
        => (date != null && date.Length >= 4) ? date.Substring(0, 4) : "";

    /// <summary>Community rating formatted "0.0" (invariant), "" when none.</summary>
    public static string RatingStr(double? r)
        => (r.HasValue && r.Value > 0)
            ? r.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "";

    /// <summary>Match-% (0..100) derived from a 0..5 rating.</summary>
    public static int PctFromRating(double? r)
        => r.HasValue ? Math.Max(0, Math.Min(100, (int)Math.Round(r.Value / 5.0 * 100))) : 0;

    /// <summary>Two short uppercase lines used as the box-art text placeholder.</summary>
    public static string[] BoxLines(string name)
    {
        var up = (name ?? "").ToUpperInvariant().Trim();
        var words = up.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1) return new[] { up, "" };
        int mid = (words.Length + 1) / 2;
        return new[] { string.Join(" ", words.Take(mid)), string.Join(" ", words.Skip(mid)) };
    }

    /// <summary>HTML-safe overview: encode, then turn newlines into &lt;br&gt;.</summary>
    public static string OverviewHtml(string overview)
    {
        if (string.IsNullOrEmpty(overview)) return "";
        var enc = WebUtility.HtmlEncode(overview.Trim());
        return enc.Replace("\r\n", "<br>").Replace("\n", "<br>");
    }

    /// <summary>Deterministic CSS gradient from a seed string (stable colors per name).</summary>
    public static string Gradient(string seed)
    {
        int hue = (int)(StableHash(seed ?? "") % 360);
        return $"linear-gradient(160deg,hsl({hue},38%,28%),hsl({hue},42%,10%))";
    }

    private static uint StableHash(string s)
    {
        // FNV-1a 32-bit.
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
