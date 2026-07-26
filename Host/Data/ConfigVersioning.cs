// Shared version stamp for LiteBox's JSON config/data files: a file carries the LiteBox version that
// LAST WROTE it ("0.0.0" = absent/pre-stamp — a valid state, not an error). Pure provenance today —
// it tells a bug report / a future format decision WHICH build wrote the file. Policy note: format
// breaks are handled by a fresh install (no per-file migration gates), so this deliberately carries
// no compare/threshold helper — a consumer that one day needs one derives it from the stamp.

#nullable enable

namespace LbApiHost.Host.Data;

internal static class ConfigVersioning
{
    /// <summary>The version string to write into a config file being saved now.</summary>
    public static string Stamp() => Install.LiteBoxVersion.Current.ToString(3);
}
