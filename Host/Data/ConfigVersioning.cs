// Shared version stamp for LiteBox's JSON config/data files (the ExtendDB versioning pattern, applied
// host-side). A file carries the LiteBox version that LAST WROTE it; on load a consumer can compare it
// against its own threshold and migrate (or reset) an older shape explicitly, instead of relying on
// System.Text.Json silently defaulting missing/renamed fields — which is how a format change turns into
// a quiet data bug (the Show3dBox default-true boolean losing the user's "off" was exactly that).
//
// Contract:
//   • WRITE  — every save stamps Stamp() (the running LiteBox version, "Major.Minor.Build").
//   • READ   — the stored string is kept as-is; "0.0.0" means "written before versioning" (or by a
//              build that didn't stamp), which is a perfectly valid state, not an error.
//   • GATE   — a consumer that needs a migration declares its own threshold and calls IsBelow(stored,
//              threshold). No global reset switch: each file decides what "too old" means for itself,
//              because these files hold USER data (backup vaults, parental lists) that must never be
//              dropped as a side effect of an unrelated version bump.

#nullable enable

using System;

namespace LbApiHost.Host.Data;

internal static class ConfigVersioning
{
    /// <summary>The version string to write into a config file being saved now.</summary>
    public static string Stamp() => Install.LiteBoxVersion.Current.ToString(3);

    /// <summary>Parse a stored stamp; unreadable/absent → 0.0.0 (pre-versioning).</summary>
    public static Version Parse(string? stored)
        => Version.TryParse(string.IsNullOrWhiteSpace(stored) ? "0.0.0" : stored, out var v) ? v : new Version(0, 0, 0);

    /// <summary>True when the file was written by a version OLDER than <paramref name="threshold"/> —
    /// the consumer's cue to run its migration for that file.</summary>
    public static bool IsBelow(string? stored, Version threshold) => Parse(stored) < threshold;
}
