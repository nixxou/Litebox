// Versioning knobs for upgrade/migration. The exe carries its version (csproj <Version>); at boot the
// installed version is read back from Core\litebox\LiteBox.ini (config) and the LiteBox.pending.db
// PRAGMA user_version (db schema), and compared against the thresholds below to decide what to reset /
// refresh. To ship a breaking change: bump <Version> AND raise the relevant "…Below" threshold to it.

#nullable enable

using System;

namespace LbApiHost.Host.Install;

internal static class LiteBoxVersion
{
    /// <summary>This exe's version (from csproj &lt;Version&gt;), normalised to Major.Minor.Build.</summary>
    public static readonly Version Current = Norm(typeof(LiteBoxVersion).Assembly.GetName().Version);

    // 0.7.5 invalidates EVERYTHING before it: any older (or version-less → read as 0.0.0) install has its
    // config + DB reset and its natives re-verified. Bump these to Current on each release that changes the
    // matching target in a breaking way; keep them where they are otherwise.

    /// <summary>Wipe LiteBox.ini when the installed config version is below this.</summary>
    public static readonly Version ResetConfigBelow = new(0, 7, 5);
    /// <summary>Wipe LiteBox.pending.db (drop LiteBox's tables) when its schema version is below this.</summary>
    public static readonly Version ResetPendingDbBelow = new(0, 7, 5);
    /// <summary>Re-verify/overwrite the ThirdParty natives when the installed version is below this
    /// (i.e. bump this on any release whose embedded native payload changed).</summary>
    public static readonly Version RefreshNativesBelow = new(0, 7, 5);

    private static Version Norm(Version? v) => v == null ? new(0, 0, 0) : new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Encode Major.Minor.Build into a single int for SQLite's PRAGMA user_version.</summary>
    public static int Encode(Version v) => v.Major * 1_000_000 + v.Minor * 1_000 + Math.Max(0, v.Build);
}
