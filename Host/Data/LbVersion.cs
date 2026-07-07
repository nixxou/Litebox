// Which LaunchBox is LiteBox running against? Detected once at boot from the LB install
// LiteBox borrows its Core runtime from. Two independent axes:
//   • .NET major of the Core runtime (net9 = LB 13.27 line, net10 = LB 13.28+). This is the
//     TODAY discriminator — the 13.28 rewrite that renamed several Settings.xml keys is the
//     .NET-10 line. Read at runtime from Environment.Version (the process IS running on
//     Core's runtime), cross-checked against Core\LiteBox.runtimeconfig / LaunchBox.exe.
//   • The LaunchBox product version (e.g. 13.27.0 / 13.28.0), read from LaunchBox.exe's
//     FileVersionInfo. Kept alongside because a future key change may key on the exact
//     version, not just the .NET major.
//
// Everything downstream (ProblemKeys) asks THIS, never re-detects — so the "which LB" logic
// lives in one place and the growing set of version-conditioned decisions stays auditable.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;

namespace LbApiHost.Host.Data;

internal static class LbVersion
{
    /// <summary>.NET major the host (= LB Core) runs on. 10 ⇒ LB 13.28+ line, 9 ⇒ 13.27.</summary>
    public static int DotNetMajor { get; private set; } = Environment.Version.Major;

    /// <summary>LaunchBox product version (LaunchBox.exe FileVersionInfo), or null if unreadable.</summary>
    public static Version? Product { get; private set; }

    /// <summary>True on the LB 13.28+ line (the Settings.xml key rename baseline). The single
    /// condition today; ProblemKeys derives its routing from this. May grow more nuanced later
    /// (exact product version) without any caller changing — they ask ProblemKeys, not this.</summary>
    public static bool Is1328OrLater => DotNetMajor >= 10;

    private static bool _done;

    /// <summary>Resolve from the LB root once. Idempotent; never throws.</summary>
    public static void Detect(string? lbRoot)
    {
        if (_done) return;
        _done = true;
        try
        {
            DotNetMajor = Environment.Version.Major;   // authoritative: we run on Core's runtime
            if (!string.IsNullOrEmpty(lbRoot))
            {
                string exe = Path.Combine(lbRoot!, "LaunchBox.exe");
                if (File.Exists(exe))
                {
                    var fi = FileVersionInfo.GetVersionInfo(exe);
                    if (fi.ProductMajorPart > 0 || fi.FileMajorPart > 0)
                        Product = new Version(
                            Math.Max(fi.ProductMajorPart, fi.FileMajorPart),
                            Math.Max(fi.ProductMinorPart, fi.FileMinorPart),
                            Math.Max(fi.ProductBuildPart, fi.FileBuildPart));
                }
            }
            Console.WriteLine($"[lbversion] .NET major={DotNetMajor} product={Product?.ToString() ?? "?"} is1328+={Is1328OrLater}");
        }
        catch (Exception ex) { Console.WriteLine("[lbversion] detect failed: " + ex.Message); }
    }
}
