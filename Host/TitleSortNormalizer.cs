// One canonical title-sort key for every LiteBox surface.
//
// without — raw Title only, ignoring SortTitle.
// simple — the historical LiteBox desktop rule.
// advanced — the historical LB-WEB PerformSanitize-style rule.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal enum TitleSortNormalization
{
    Without,
    Simple,
    Advanced,
}

internal static class TitleSortNormalizer
{
    public const string ConfigKey = "TitleSortNormalization";

    private static readonly Dictionary<string, string> Roman = new(StringComparer.Ordinal)
    {
        ["II"] = "2",
        ["III"] = "3",
        ["IV"] = "4",
        ["V"] = "5",
        ["VI"] = "6",
        ["VII"] = "7",
        ["VIII"] = "8",
    };

    public static TitleSortNormalization Parse(string? value)
        => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "without" => TitleSortNormalization.Without,
            "advanced" => TitleSortNormalization.Advanced,
            _ => TitleSortNormalization.Simple,
        };

    public static string ConfigValue(TitleSortNormalization mode)
        => mode switch
        {
            TitleSortNormalization.Without => "without",
            TitleSortNormalization.Advanced => "advanced",
            _ => "simple",
        };

    public static TitleSortNormalization ConfiguredMode()
    {
        try { return Parse(LiteBoxConfig.LoadForExe().Get(ConfigKey, "simple")); }
        catch { return TitleSortNormalization.Simple; }
    }

    public static string Normalize(IGame game, TitleSortNormalization mode)
    {
        string sortTitle = "";
        string title = "";
        try { sortTitle = game?.SortTitle ?? ""; } catch { }
        try { title = game?.Title ?? ""; } catch { }
        return Normalize(title, sortTitle, mode);
    }

    internal static string Normalize(string? title, string? sortTitle, TitleSortNormalization mode)
    {
        if (mode == TitleSortNormalization.Without)
            return title ?? "";

        string source = string.IsNullOrEmpty(sortTitle) ? (title ?? "") : sortTitle!;
        return mode switch
        {
            TitleSortNormalization.Advanced => Advanced(source),
            _ => Simple(source),
        };
    }

    private static string Simple(string source)
    {
        string s = (source ?? "").ToLowerInvariant().Trim();
        foreach (var article in new[] { "the ", "a ", "an " })
        {
            if (!s.StartsWith(article, StringComparison.Ordinal)) continue;
            s = s.Substring(article.Length);
            break;
        }

        var result = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) result.Append(c);
        return result.ToString();
    }

    private static string Advanced(string source)
    {
        string s = source ?? "";
        s = StripBracketed(s, '(', ')');
        s = StripBracketed(s, '[', ']');
        s = StripBracketed(s, '{', '}');

        var chars = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c <= '\x1f' || c is '"' or ':' or '?' or '\\' or '/' or '-' or '&' or '!' or ',')
                chars.Append(' ');
            else if (c is '\'' or '.')
            {
                // Removed without inserting a word boundary, matching LB-WEB's historical rule.
            }
            else
                chars.Append(c);
        }

        s = CompressAsciiSpaces(chars.ToString()).Trim();
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(tokens.Length);
        foreach (string token in tokens)
        {
            if (Roman.TryGetValue(token, out string? number))
            {
                result.Add(number);
                continue;
            }

            if (token.Equals("a", StringComparison.OrdinalIgnoreCase)
                || token.Equals("an", StringComparison.OrdinalIgnoreCase)
                || token.Equals("and", StringComparison.OrdinalIgnoreCase)
                || token.Equals("the", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(token);
        }
        return string.Join(" ", result).ToUpperInvariant();
    }

    private static string StripBracketed(string source, char open, char close)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        var result = new StringBuilder(source.Length);
        int copyFrom = 0;
        while (copyFrom < source.Length)
        {
            int start = source.IndexOf(open, copyFrom);
            if (start < 0) break;
            int end = source.IndexOf(close, start + 1);
            if (end < 0) break;

            result.Append(source, copyFrom, start - copyFrom);
            char before = start > 0 ? source[start - 1] : ' ';
            char after = end + 1 < source.Length ? source[end + 1] : ' ';
            if (char.IsWhiteSpace(before) || char.IsWhiteSpace(after)) result.Append(' ');
            copyFrom = end + 1;
        }
        result.Append(source, copyFrom, source.Length - copyFrom);
        return result.ToString();
    }

    private static string CompressAsciiSpaces(string source)
    {
        var result = new StringBuilder(source.Length);
        bool previousSpace = false;
        foreach (char c in source)
        {
            if (c == ' ')
            {
                if (!previousSpace) result.Append(c);
                previousSpace = true;
            }
            else
            {
                result.Append(c);
                previousSpace = false;
            }
        }
        return result.ToString();
    }
}
