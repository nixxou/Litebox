// Single owner of native-payload deployment into <LB>\ThirdParty\… for the LiteBox host.
//
// The payload (RAHasher + its runtime deps, Everything64, Magick.Native, steam_api64) is EMBEDDED in
// the exe as resources named "natives/<target-relative-path>" (see LiteBox.csproj — each thirdparty\*.api
// gets a LogicalName encoding its final on-disk location, e.g. natives/ThirdParty/Everything/Everything64.dll).
//
//   • Normal (refresh=false): write each resource ONLY IF ABSENT — never clobbers a copy ExtendDB already
//     deployed. Used at boot and by the lazy self-heal in RaHasherLite / SteamHelper.
//   • Refresh (refresh=true, run once after a version bump — see Migration): for a file that already exists,
//     compare it to the embedded one; identical → skip; different → if it's a tool SHARED with an INSTALLED
//     ExtendDB, ask the user before replacing (one prompt); otherwise (LiteBox-only, or ExtendDB absent)
//     overwrite silently.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace LbApiHost.Host.Install;

internal static class NativeInstaller
{
    private const string Prefix = "natives/";

    /// <summary>Deploys the embedded natives into &lt;lbRoot&gt;\ThirdParty\… . Only-if-absent unless
    /// <paramref name="refresh"/> (then updates changed files, prompting for shared ExtendDB tools).
    /// Safe + cheap to call repeatedly.</summary>
    public static void EnsureDeployed(string? lbRoot, bool refresh = false)
    {
        if (string.IsNullOrEmpty(lbRoot)) return;
        try
        {
            var asm = typeof(NativeInstaller).Assembly;
            var sharedDiff = new List<(string res, string target)>();   // shared-with-ExtendDB natives that differ
            bool? extendDbPresent = null;

            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = res.Substring(Prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                string target = Path.Combine(lbRoot, rel);

                if (!File.Exists(target)) { Extract(asm, res, target); continue; }   // absent → deploy (both modes)
                if (!refresh) continue;                                              // present + normal → only-if-absent
                if (SameContent(asm, res, target)) continue;                         // present + refresh + identical → skip

                // Present, differs, in a refresh pass.
                if (IsShared(rel))
                {
                    extendDbPresent ??= File.Exists(Path.Combine(lbRoot, "Plugins", "ExtendDB", "ExtendDB.dll"));
                    if (extendDbPresent == true) { sharedDiff.Add((res, target)); continue; }  // ExtendDB owns it → ask
                }
                Extract(asm, res, target);   // LiteBox-only, or no ExtendDB → overwrite
            }

            // One prompt for all shared tools that differ while ExtendDB is installed.
            if (sharedDiff.Count > 0
                && AskReplaceShared(sharedDiff.Select(x => Path.GetFileName(x.target))))
                foreach (var (res, target) in sharedDiff) Extract(asm, res, target);
        }
        catch (Exception ex) { Console.WriteLine("[installer] EnsureDeployed failed: " + ex.Message); }
    }

    // Everything / ExtendDB / RetroAchievements are shared with the ExtendDB plugin; Steam is LiteBox-only.
    private static bool IsShared(string rel)
        => rel.IndexOf(@"ThirdParty\Steam", StringComparison.OrdinalIgnoreCase) < 0;

    private static void Extract(Assembly asm, string res, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var rs = asm.GetManifestResourceStream(res);
            if (rs == null) return;
            using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
            rs.CopyTo(fs);
            Console.WriteLine("[installer] deployed " + Path.GetFileName(target));
        }
        catch (Exception ex) { Console.WriteLine($"[installer] {Path.GetFileName(target)} failed: {ex.Message}"); }
    }

    // True when the embedded resource is byte-for-byte the on-disk file (length first, then hash).
    private static bool SameContent(Assembly asm, string res, string target)
    {
        try
        {
            using var rs = asm.GetManifestResourceStream(res);
            if (rs == null) return false;
            if (rs.CanSeek && rs.Length != new FileInfo(target).Length) return false;   // fast reject
            using var md5 = MD5.Create();
            byte[] embHash = md5.ComputeHash(rs);
            using var fsr = File.OpenRead(target);
            byte[] fileHash = md5.ComputeHash(fsr);
            return embHash.SequenceEqual(fileHash);
        }
        catch { return false; }   // on any doubt, treat as different
    }

    private static bool AskReplaceShared(IEnumerable<string> names)
    {
        try
        {
            string list = string.Join("\n  • ", names);
            return MessageBox.Show(
                "LiteBox has different versions of these tools it shares with the ExtendDB plugin:\n\n  • " + list +
                "\n\nReplace them with LiteBox's versions? (ExtendDB re-creates its own on next run if needed.)",
                "LiteBox — update shared tools?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
        catch { return false; }   // no UI available → leave them
    }
}
