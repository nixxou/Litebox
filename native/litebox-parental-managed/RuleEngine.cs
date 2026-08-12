// Pure match engine — a standalone port of LiteBox's Host/Parental/ParentalFilter match tests, so the
// plugin computes each game's restriction reasons IDENTICALLY to LiteBox and the native .bin.
//
//   • WildcardMatch — '*' any run (incl. empty), '?' exactly one char, whole-string, case-insensitive
//     (byte-for-byte with ExtendDB.ParentalControlManager.WildcardMatch);
//   • IsRatingAllowed — Whitelist (show only when a rule matches) / Blacklist (hide when a rule matches);
//   • IsNameHidden — whole-name, case-insensitive, against the locked hide-list.
//
// No runtime lock state here (the browser reflects the CONFIG, not a live session) — callers pass the
// parsed ParentalDat model.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiteBoxParental
{
    internal static class RuleEngine
    {
        /// <summary>True when a game with this rating would be VISIBLE under the rules. Whitelist: visible
        /// only when a rule matches; Blacklist: visible unless a rule matches.</summary>
        public static bool IsRatingAllowed(string rating, bool blacklist, IEnumerable<string> rules)
        {
            string r = rating ?? "";
            bool matched = false, hasRule = false;
            if (rules != null)
                foreach (var rule in rules)
                {
                    hasRule = true;
                    if (WildcardMatch(r, rule)) { matched = true; break; }
                }
            // Whitelist with NO rules = no rating filter (don't hide the whole library) — everything visible.
            if (!blacklist && !hasRule) return true;
            return blacklist ? !matched : matched;
        }

        /// <summary>True when a platform / category name is on the (locked) hide-list. Whole-name, case-insensitive.</summary>
        public static bool IsNameHidden(string name, IEnumerable<string> hideList)
        {
            if (string.IsNullOrEmpty(name) || hideList == null) return false;
            foreach (var n in hideList)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whole-string, case-insensitive wildcard match. '*' = any run (incl. empty), '?' = one char.</summary>
        public static bool WildcardMatch(string input, string pattern)
        {
            if (pattern == null) return false;
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(input ?? "", rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
