// The one text-search rule, shared by the desktop list and both web clients.
//
// It matches on the TITLE only, but against two forms of it at once:
//
//   raw        — the title as stored, compared case-insensitively. Keeps whatever the user typed
//                meaningful: spaces, articles, punctuation. "the legend" finds "The Legend of Zelda".
//   normalized — lowercase, diacritics folded, everything but letters and digits dropped.
//                "street-fighter", "streetfighter" and "Street Fighter" all find the same game,
//                and "pokemon" finds "Pokémon".
//
// A game matches when EITHER form does, so each rule catches what the other misses. Neither is a
// superset: raw keeps articles the normalized form would let through anyway, normalized ignores
// punctuation raw insists on.
//
// Two modes, because the two filters answer different questions:
//   Contains  — the deliberate search box: "find this anywhere in the title".
//   StartsWith — the transient filter: "narrow to titles beginning with this". That is what makes
//                a letter rail work — picking S seeds the transient filter with "s".
//
// web-assets/vendor/game-sort.js :: titleMatches mirrors this exactly, and
// --selftest-filter-parity compares the two on the same sample.

#nullable enable

using System;
using System.Globalization;
using System.Text;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class GameTextFilter
{
    /// <summary>Lowercase, diacritics folded, letters and digits only. "Pokémon Café!" → "pokemoncafe".</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            // A combining accent carries no information once its base letter is kept.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static bool Matches(IGame game, string? query, bool prefix)
    {
        string title;
        try { title = game?.Title ?? ""; } catch { return false; }
        return Matches(title, query, prefix);
    }

    /// <summary>Drops a leading English article, the way the title SORT does. Without this, picking
    /// L in a letter rail would not list "The Legend of Zelda" even though the list files it under
    /// L — the rail would contradict the order it sits next to.</summary>
    public static string StripLeadingArticle(string? value)
    {
        string s = (value ?? "").TrimStart();
        foreach (var article in new[] { "the ", "a ", "an " })
            if (s.StartsWith(article, StringComparison.OrdinalIgnoreCase)) return s.Substring(article.Length);
        return s;
    }

    public static bool Matches(string? title, string? query, bool prefix)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0) return true;
        string t = title ?? "";

        if (prefix ? t.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                   : t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // A query made only of punctuation normalizes to nothing; without this guard it would
        // match every game through the normalized forms.
        string nq = Normalize(q);
        if (nq.Length == 0) return false;

        // Three forms in all: the raw title (articles and punctuation count), the normalized title,
        // and the normalized title WITHOUT its leading article. The query itself keeps its own
        // article — "the legend" is already served by the raw form.
        if (Hit(Normalize(t), nq, prefix)) return true;
        return Hit(Normalize(StripLeadingArticle(t)), nq, prefix);
    }

    private static bool Hit(string haystack, string needle, bool prefix)
        => prefix ? haystack.StartsWith(needle, StringComparison.Ordinal)
                  : haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
}
