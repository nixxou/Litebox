// RetroAchievements module config panel — STUB. Per-ROM RA hashing with our own RAHasher (per ROM / per
// version) plus the achievements panel. A parallel agent replaces this body with the full settings UI; for now
// it returns the shared placeholder so the tab renders consistently.

#nullable enable

using System;
using System.Windows.Forms;

namespace LbApiHost.Host.Options;

internal static class RaPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
        => ModulePanelKit.Placeholder(
            "RetroAchievements",
            "Resolve each game's RetroAchievements set by hashing the actual ROM with our own RAHasher (per ROM / per version), and show the achievements panel.",
            dpiS);
}
