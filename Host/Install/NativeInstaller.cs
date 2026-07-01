// Single owner of native-payload deployment into <LB>\ThirdParty\… for the LiteBox host.
//
// The payload (RAHasher + its runtime deps, Everything64, Magick.Native, steam_api64) is EMBEDDED in
// the exe as resources named "natives/<target-relative-path>" (see LiteBox.csproj — each thirdparty\*.api
// gets a LogicalName encoding its final on-disk location, e.g. natives/ThirdParty/Everything/Everything64.dll).
// EnsureDeployed() writes each resource to <lbRoot>\<target-relative-path> ONLY IF ABSENT, so it never
// clobbers a copy ExtendDB already deployed (the ThirdParty\{RetroAchievements,ExtendDB,Everything} dirs
// are shared, first-writer-wins).
//
// This is the ONE place that knows the payload→ThirdParty mapping (it lives in the csproj LogicalNames);
// EverythingSupport / ThumbCache(MagickSupport) / RaHasherLite / SteamHelper no longer copy anything out
// themselves — they just consume the deployed files. Idempotent; cheap to call repeatedly.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host.Install;

internal static class NativeInstaller
{
    private const string Prefix = "natives/";
    private static bool _done;

    /// <summary>Extracts every embedded native (only-if-absent) into &lt;lbRoot&gt;\ThirdParty\… .
    /// Safe to call from anywhere; the first call does the work, later calls are no-ops.</summary>
    public static void EnsureDeployed(string? lbRoot)
    {
        if (_done || string.IsNullOrEmpty(lbRoot)) return;
        try
        {
            var asm = typeof(NativeInstaller).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = res.Substring(Prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                string target = Path.Combine(lbRoot, rel);
                if (File.Exists(target)) continue;   // shared with ExtendDB → never overwrite
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var rs = asm.GetManifestResourceStream(res);
                    if (rs == null) continue;
                    using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
                    rs.CopyTo(fs);
                    Console.WriteLine($"[installer] deployed {rel}");
                }
                catch (Exception ex) { Console.WriteLine($"[installer] {rel} failed: {ex.Message}"); }
            }
            _done = true;
        }
        catch (Exception ex) { Console.WriteLine($"[installer] EnsureDeployed failed: {ex.Message}"); }
    }
}
