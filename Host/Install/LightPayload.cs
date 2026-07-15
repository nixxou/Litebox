// A2 distribution: the standalone single-file exe is a pure INSTALLER — it never runs as the host.
// Instead of copying its (single-file) self into Core, it extracts the embedded "light" host there:
// the 4 multi-file app files (LiteBox.exe apphost + LiteBox.dll + deps.json + runtimeconfig.json),
// which share LaunchBox\Core's .NET runtime and — crucially — DO NOT trigger LaunchBox's method-body-
// encryption obfuscator crash that a single-file bundle causes (see ExtendDB/docs/lb-save-management.md
// and the reference-lb-obfuscator-singlefile memory). The host that actually boots is therefore the
// light multi-file, so calling the LaunchBox core / integration plugins works.
//
// The exe embeds BOTH runtime targets (light/net9/* and light/net10/*). At install time we detect which
// .NET LaunchBox's Core carries (net9 for LB 13.27, net10 for LB 13.28+) and extract the matching set —
// its apphost binds Core's runtime, so it must match.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LbApiHost.Host.Install;

internal static class LightPayload
{
    // The multi-file host files, extracted verbatim into Core. LiteBox.exe is the apphost (native
    // launcher); the other three describe how it binds LiteBox.dll against Core's shared runtime.
    // LibVLCSharp.dll is the managed wrapper over the libvlc natives (deployed separately into
    // ThirdParty\VLC) — it has NO transitive managed dependencies, so it's just a 5th app file.
    private static readonly string[] Files = { "LiteBox.exe", "LiteBox.dll", "LiteBox.deps.json", "LiteBox.runtimeconfig.json", "LibVLCSharp.dll" };

    /// <summary>True when this build actually carries an embedded light payload (i.e. the standalone
    /// installer). A plain light/dev build carries none — it IS the host, nothing to extract.</summary>
    public static bool IsEmbedded(out string? reason)
    {
        var res = typeof(LightPayload).Assembly.GetManifestResourceNames();
        bool any = res.Any(n => n.StartsWith("light/", StringComparison.OrdinalIgnoreCase));
        reason = any ? null : "no embedded light payload (not a standalone installer build)";
        return any;
    }

    /// <summary>Extracts the light host matching Core's .NET into <paramref name="coreDir"/>.
    /// Returns null on success, else an error message.</summary>
    public static string? ExtractToCore(string coreDir)
    {
        try
        {
            var asm = typeof(LightPayload).Assembly;
            var resNames = asm.GetManifestResourceNames();

            string tfm = DetectCoreTfm(coreDir);
            // Fall back to the other TFM if the detected one wasn't embedded (defensive).
            if (!resNames.Any(n => n.StartsWith($"light/{tfm}/", StringComparison.OrdinalIgnoreCase)))
            {
                string other = tfm == "net10" ? "net9" : "net10";
                if (resNames.Any(n => n.StartsWith($"light/{other}/", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"[installer] light/{tfm} not embedded — falling back to light/{other}");
                    tfm = other;
                }
                else return $"No embedded light payload found (looked for light/{tfm}/).";
            }

            Directory.CreateDirectory(coreDir);
            foreach (var f in Files)
            {
                string logical = $"light/{tfm}/{f}";
                var res = resNames.FirstOrDefault(n => n.Equals(logical, StringComparison.OrdinalIgnoreCase));
                if (res == null)
                {
                    // runtimeconfig/deps are always present; the apphost/dll are required. Missing → hard error.
                    return $"Embedded light payload is incomplete (missing {logical}).";
                }
                string target = Path.Combine(coreDir, f);
                TryFreeLocked(target);
                using var rs = asm.GetManifestResourceStream(res)!;
                using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
                rs.CopyTo(fs);
            }
            Console.WriteLine($"[installer] extracted light host ({tfm}) into {coreDir}");
            return null;
        }
        catch (Exception ex) { return "Could not extract the LiteBox host into Core:\n" + ex.Message; }
    }

    /// <summary>net9 / net10 — the .NET major LaunchBox's Core self-contained runtime carries. Read from
    /// coreclr.dll (always present in a self-contained Core), else System.Private.CoreLib, else the host's
    /// own runtimeconfig; defaults to net10.</summary>
    public static string DetectCoreTfm(string coreDir)
    {
        int major = FileMajor(Path.Combine(coreDir, "coreclr.dll"))
                    ?? FileMajor(Path.Combine(coreDir, "System.Private.CoreLib.dll"))
                    ?? FileMajor(Path.Combine(coreDir, "hostpolicy.dll"))
                    ?? 10;
        string tfm = major <= 9 ? "net9" : "net10";
        Console.WriteLine($"[installer] Core .NET major = {major} → {tfm}");
        return tfm;
    }

    private static int? FileMajor(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var v = FileVersionInfo.GetVersionInfo(path);
            // ProductVersion like "9.0.14.xxxx" / "10.0.0-..."; ProductMajorPart is the reliable int.
            int m = v.ProductMajorPart > 0 ? v.ProductMajorPart : v.FileMajorPart;
            return m > 0 ? m : (int?)null;
        }
        catch { return null; }
    }

    // Best-effort: if Core\LiteBox.exe (or a file) is locked by a running host, try to stop it so the
    // overwrite succeeds. Only touches a LiteBox-owned name; never LaunchBox's files.
    private static void TryFreeLocked(string target)
    {
        try
        {
            if (!File.Exists(target)) return;
            // A quick open test; if it's free, nothing to do.
            try { using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return; }
            catch { /* locked → fall through */ }

            if (!string.Equals(Path.GetFileName(target), "LiteBox.exe", StringComparison.OrdinalIgnoreCase)) return;
            string full = Path.GetFullPath(target);
            foreach (var p in Process.GetProcessesByName("LiteBox"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(4000); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }
}
