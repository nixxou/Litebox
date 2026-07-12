// LaunchBox's region ordering for picking images — the single source of truth for "which region wins".
//
// LaunchBox does NOT stop at the user's RegionPriorities. Decompiled from Unbroken.LaunchBox.LocalDb
// (GamesDb.GetImagesAsync / GamesDb.prioritizedRegions), it consults, in order:
//   1. the game's own region(s), re-sorted by the user's RegionPriorities,
//   2. the user's RegionPriorities,
//   3. a HARD-CODED fallback list of 27 regions  ← the part we were missing,
// taking, for each image Type, the first region that has one (a Type already served is never revisited).
//
// We previously stopped at (2) + root, so an image sitting in a region the user never listed (e.g. "Japan")
// was NEVER eligible — on this library that silently hid ~6k files and left 4553 (game, type) slots empty
// even though the file was on disk. Appending LaunchBox's own fallback fixes that with no change to any
// image already being picked (the fallback only fills gaps).
//
// Root files (no region sub-folder) are modelled as the region "none" and stay LAST, as before.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace LbApiHost.Host.Media;

internal static class LbRegions
{
    /// <summary>The root / no-region bucket (an image sitting directly in the image-type folder).</summary>
    public const string None = "none";

    /// <summary>LaunchBox's hard-coded fallback order (GamesDb.prioritizedRegions), verbatim.</summary>
    public static readonly string[] Fallback =
    {
        "World", "North America", "United States", "Europe", "United Kingdom", "Canada", "Australia",
        "Spain", "Italy", "Greece", "Holland", "Oceania", "Sweden", "Germany", "France",
        "The Netherlands", "Norway", "South America", "Finland", "Brazil", "Japan", "Asia", "China",
        "Hong Kong", "Korea", "Russia", "Thailand",
    };

    /// <summary>
    /// The canonical region order LaunchBox effectively uses: the user's priorities first, then LaunchBox's
    /// own fallback list (so an unlisted region is still eligible), then the root ("none") last.
    /// Lower-cased, de-duplicated. <paramref name="userPriorities"/> is the RegionPriorities setting.
    /// </summary>
    public static List<string> Order(IEnumerable<string>? userPriorities)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in userPriorities ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var lr = r.Trim().ToLowerInvariant();
            if (seen.Add(lr)) order.Add(lr);
        }
        foreach (var r in Fallback)
        {
            var lr = r.ToLowerInvariant();
            if (seen.Add(lr)) order.Add(lr);
        }
        if (seen.Add(None)) order.Add(None);   // root last (unless the user explicitly ranked it)
        return order;
    }
}
