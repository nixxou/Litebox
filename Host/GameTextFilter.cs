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

    public static bool Matches(string? title, string? query, bool prefix)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0) return true;
        string t = title ?? "";

        if (prefix ? t.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                   : t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // A query made only of punctuation normalizes to nothing; without this guard it would
        // match every game through the normalized form.
        string nq = Normalize(q);
        if (nq.Length == 0) return false;
        string nt = Normalize(t);
        return prefix ? nt.StartsWith(nq, StringComparison.Ordinal)
                      : nt.IndexOf(nq, StringComparison.Ordinal) >= 0;
    }
}
