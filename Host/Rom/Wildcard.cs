// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — glob matcher. Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// Original LiteBox implementation of the glob semantics the priority scorer needs
// (GoodMerged-style patterns like "*(*e*)*[!].*"): '*' matches any run (incl.
// empty), '?' matches exactly ONE character that is NOT '.'. Case folding is the
// caller's job (every caller lowercases both sides). A boolean matcher — it
// returns only whether the value matches, which is all the scorer consumes, so it
// is behaviourally equivalent to any correct glob for accept/reject decisions.

#nullable enable

using System;

namespace LbApiHost.Host.Rom;

internal static class Wildcard
{
    /// <summary>True when <paramref name="value"/> matches the glob
    /// <paramref name="pattern"/> ('*' = any run, '?' = one non-'.' char). An empty
    /// pattern never matches (parity with the reference behaviour).</summary>
    public static bool Match(string value, string pattern)
    {
        value ??= "";
        pattern ??= "";
        if (pattern.Length == 0) return false;

        int s = 0, p = 0;
        int starP = -1, starS = 0;   // last '*' position + the value index it was matched from

        while (s < value.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == value[s] || (pattern[p] == '?' && value[s] != '.')))
            {
                s++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p; starS = s; p++;          // '*' absorbs zero chars for now
            }
            else if (starP != -1)
            {
                p = starP + 1; s = ++starS;          // backtrack: let the last '*' eat one more char
            }
            else return false;
        }

        while (p < pattern.Length && pattern[p] == '*') p++;   // trailing '*'s match empty
        return p == pattern.Length;
    }
}
