// The one ordering rule for game sort keys, shared by the desktop list and the parity self-test.
//
// null is treated as the GREATEST value. That is what keeps a game with no year / no rating / never
// played at the bottom of an ascending sort and at the top of a descending one — the behaviour
// LiteBox's columns have always had. web-assets/vendor/game-sort.js :: sorted implements the same
// rule, so both surfaces put blanks in the same place.

using System;
using System.Collections.Generic;

namespace LbApiHost.Host;

internal sealed class SortValueComparer : IComparer<object>
{
    public static readonly SortValueComparer Instance = new();

    public int Compare(object a, object b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return 1;
        if (b == null) return -1;
        if (a is string sa && b is string sb) return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
        if (a is IComparable ca && a.GetType() == b.GetType()) { try { return ca.CompareTo(b); } catch { } }
        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
