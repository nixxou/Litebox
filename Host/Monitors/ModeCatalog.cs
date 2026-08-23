// A generic catalogue of display modes, independent of any monitor.
//
// WHY NOT ASK THE HARDWARE. The Display-mode section of a profile can target "the main monitor" — meaning
// whichever screen the profile is about to MAKE primary, which is frequently not the one that is primary
// while you are editing. Offering the current primary's mode list there is offering the wrong panel's
// capabilities: author a profile that promotes the 1080p screen and the list you picked from belonged to
// the 1440p one. Worse, the choice looked validated.
//
// So the editor offers this fixed list instead, and the RECONCILIATION happens at apply time against the
// screen that actually ends up primary — which is the only moment the answer is knowable. A profile is a
// wish; the hardware arbitrates it later.
//
// A named monitor keeps its real mode list: there, the panel's identity is fixed and known, so there is
// no reason to hide what it can do.

#nullable enable

using System.Collections.Generic;

namespace LbApiHost.Host.Monitors;

internal static class ModeCatalog
{
    /// <summary>Common desktop resolutions up to 4K, widest first. Not every monitor does every one —
    /// that is the point; "Adjust to the closest supported value" decides what happens when it cannot.</summary>
    public static readonly (int W, int H)[] Resolutions =
    {
        (3840, 2160), (3840, 1600), (3440, 1440),
        (2560, 1600), (2560, 1440), (2560, 1080),
        (2048, 1152), (1920, 1200), (1920, 1080),
        (1680, 1050), (1600, 1200), (1600, 900),
        (1440, 900),  (1366, 768),  (1360, 768),
        (1280, 1024), (1280, 960),  (1280, 800), (1280, 720),
        (1152, 864),  (1024, 768),  (800, 600),  (640, 480),
    };

    /// <summary>Refresh rates worth offering, highest first. 59 is listed on purpose: Windows reports a
    /// great many "60 Hz" panels as 59, and a profile written against what the OS actually says should be
    /// expressible.</summary>
    public static readonly int[] Refreshes =
    {
        360, 300, 280, 265, 240, 200, 180, 175, 165, 160, 144, 120, 100, 90, 85, 75, 72, 60, 59, 50, 30, 24,
    };

    public static IEnumerable<string> ResolutionLabels()
    {
        foreach (var r in Resolutions) yield return $"{r.W} x {r.H}";
    }

    public static IEnumerable<string> RefreshLabels()
    {
        foreach (var hz in Refreshes) yield return hz + " Hz";
    }
}
