// Game-title normalization for the web search — faithful port of the plugin's two-pass pipeline
// (Utility/Normalizer.cs) so the query-time keys match the Extended DB's pre-normalized columns
// (AltNameCompareValue / AltNameCompareValueFallback, written by the plugin's Searchrom indexer).
//
// Two passes with deliberately different aggressiveness:
//   • PerformSanitize — LOOSE: strips "(...)"/"[...]"/"{...}" annotations (whitespace-aware: embedded
//     brackets fuse the neighbours, spaced brackets keep the word boundary), maps filename-invalid
//     chars + a punctuation blacklist to space/void, converts Roman numerals II..VIII (longer first —
//     VIII before VII before V — or "VII" would be eaten as "V"+"II"), drops English articles,
//     compresses whitespace, uppercases.
//   • NormalizeCompareName — STRICT: NFD + strip diacritics, keep [A-Z0-9] only. isValid iff the key
//     is ≥ 5 chars AND ≤ 20% of the original non-whitespace chars were dropped; null for inputs
//     ending in '+'/'#' (variant suffixes).
//
// The 5-char / 20% thresholds and the kept character class are part of the on-disk schema contract —
// do not change them without re-indexing every Extended-DB row.

#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LbApiHost.Host.Web;

internal static class TitleNormalizer
{
    /// <summary>STRICT pass → compact "[A-Z0-9]+" key. See file header for the validity rule.</summary>
    public static string? NormalizeCompareName(string input, out bool isValid)
    {
        isValid = false;
        if (string.IsNullOrEmpty(input))
            return null;

        if (input.Trim().EndsWith('+') || input.Trim().EndsWith('#'))
            return null;

        int totalCharsForRatio = input.Count(c => !char.IsWhiteSpace(c));

        string normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        }

        string result = Regex.Replace(sb.ToString(), @"[^A-Z0-9]", "");
        int replacements = totalCharsForRatio - result.Length;

        isValid = result.Length >= 5 && ((double)replacements / totalCharsForRatio) <= 0.2;

        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>LOOSE pass — bracket stripping, blacklists, Roman numerals II..VIII, article removal,
    /// whitespace compression, uppercase. Matches the CompareName format the Extended DB stores.</summary>
    public static string PerformSanitize(string name)
    {
        string sanitized = name;

        sanitized = StripBrackets(sanitized, @"(?<=\S)?\([^)]*\)(?=\S)?");
        sanitized = StripBrackets(sanitized, @"(?<=\S)?\[[^\]]*\](?=\S)?");
        sanitized = StripBrackets(sanitized, @"(?<=\S)?\{[^}]*\}(?=\S)?");

        var invalidChars = Path.GetInvalidFileNameChars()
                               .Where(c => c != '*' && c != '`' && c != '|' && c != '>' && c != '<' && c != '~')
                               .ToArray();
        char[] toSpace = { '-', ':', '&', '!', ',', '/', '\\', '?' };
        char[] toVoid = { '\'', '.', '"' };

        sanitized = new string(sanitized.Select(c =>
        {
            if (invalidChars.Contains(c) || toSpace.Contains(c)) return ' ';
            if (toVoid.Contains(c)) return '\0';
            return c;
        }).Where(c => c != '\0').ToArray());

        sanitized = Regex.Replace(sanitized, " {2,}", " ").Trim();

        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)II(?=\s|$)", "2");
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)III(?=\s|$)", "3");
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)IV(?=\s|$)", "4");
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)VIII(?=\s|$)", "8");
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)VII(?=\s|$)", "7");
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)VI(?=\s|$)", "6");
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)V(?=\s|$)", "5");

        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)a(?=\s|$)", " ", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)an(?=\s|$)", " ", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)and(?=\s|$)", " ", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(?<=^|\s)the(?=\s|$)", "", RegexOptions.IgnoreCase);

        sanitized = Regex.Replace(sanitized, " {2,}", " ").Trim();
        return sanitized.ToUpper();
    }

    /// <summary>Whitespace-aware bracket strip: embedded → "" (fuse neighbours), spaced → " ".</summary>
    private static string StripBrackets(string input, string pattern)
        => Regex.Replace(input, pattern, match =>
        {
            char before = match.Index > 0 ? input[match.Index - 1] : ' ';
            char after = match.Index + match.Length < input.Length ? input[match.Index + match.Length] : ' ';
            return (!char.IsWhiteSpace(before) && !char.IsWhiteSpace(after)) ? "" : " ";
        });
}
