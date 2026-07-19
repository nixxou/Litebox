// Runtime read-side of the RetroAchievements options panel's config — the missing link that makes the
// per-platform settings the panel writes (RaPanelSupport.RaPanelConfig) actually GATE the auto-resolve path.
//
// The panel (Host/Options/Modules/RaPanel.cs) persists two LiteBox-own settings via RaPanelConfig:
//   • an auto-update trigger  — "On select" / "On launch"  (RaPanelConfig.Mode)
//   • a per-platform Enabled flag (only diffs-from-default stored; a platform's DEFAULT is "enabled when it
//     maps to an RA console"), which RaPanelConfig.IsEnabled resolves against a caller-supplied default.
//
// Until now the auto-resolve-on-select path (MainWindow.LoadRaPanel → RaResolveLite.Resolve) and the catalogue
// heartbeat (RaCatalogEngine) both ignored these — they only checked "is the module on?". This class
// exposes the two answers the gate needs, WITHOUT re-implementing RaResolveLite (callers still call Resolve;
// they just ask here first whether they're allowed to):
//
//   • AutoUpdateMode / IsAutoUpdateOnSelect / IsAutoUpdateOnLaunch — the stored trigger.
//   • IsPlatformEnabled(platform) — the panel's Enabled flag for that platform, defaulting to the same rule
//     the panel uses for a fresh row (enabled iff the platform maps to an RA console via RaPlatformMap, so
//     the console-mapping / RaPlatformMap overrides stay honored: a platform mapped to "none" is disabled).
//   • ShouldAutoResolveOnSelect(platform) — the composed on-select predicate (mode == select AND enabled).
//
// Config source of truth is unchanged (Core\litebox\ra-panel.json via RaPanelConfig); this is a pure reader.
// With no config file the defaults reproduce today's behaviour (mode = on-select, every RA-mapped platform
// enabled), so gating on this introduces no regression for users who never touched the panel.

#nullable enable

using System;

namespace LbApiHost.Host.Ra;

/// <summary>Runtime accessor for the RA options panel's per-platform Enabled flags + auto-update trigger.
/// Lets the auto-resolve path honour the panel's config without duplicating any RaResolveLite logic.</summary>
internal static class RaPlatformState
{
    /// <summary>The stored auto-update trigger, verbatim ("select" or "launch").</summary>
    public static string AutoUpdateMode => RaPanelConfig.Mode;

    /// <summary>True when the panel's auto-update trigger is "On select" (the default).</summary>
    public static bool IsAutoUpdateOnSelect
        => string.Equals(RaPanelConfig.Mode, RaPanelConfig.ModeOnSelect, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the panel's auto-update trigger is "On launch".</summary>
    public static bool IsAutoUpdateOnLaunch
        => string.Equals(RaPanelConfig.Mode, RaPanelConfig.ModeOnLaunch, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the RetroAchievements module is enabled for a platform. The default (used when the
    /// user never toggled this platform in the panel) is exactly the panel's own row default: enabled iff the
    /// platform maps to an RA console — so RaPlatformMap's console-mapping and its user overrides are honored
    /// (a platform overridden to "none" is not RA-mapped, hence disabled). Pass the game's Platform.</summary>
    public static bool IsPlatformEnabled(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return false;
        bool defaultEnabled = RaPlatformMap.ConsoleIdFor(platform) != null;
        return RaPanelConfig.IsEnabled(platform!.Trim(), defaultEnabled);
    }

    /// <summary>The composed predicate for the auto-resolve-on-select path: the trigger is "On select" AND
    /// this platform is RA-enabled. Callers still invoke RaResolveLite.Resolve themselves — this only gates
    /// whether they should.</summary>
    public static bool ShouldAutoResolveOnSelect(string? platform)
        => IsAutoUpdateOnSelect && IsPlatformEnabled(platform);
}
