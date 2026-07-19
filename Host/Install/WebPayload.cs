// Unpacks the web frontend theme assets that the STANDALONE installer carries into Core\web-assets\.
//
// The single-file installer embeds every file under web-assets\ as a manifest resource named
// "webassets/<relative-path>" (see the csproj EmbeddedResource glob; standalone build only). At install we
// rebuild that tree into <Core>\web-assets\ — the SAME place the manual "extract into Core" zip drops it, and
// the SAME place WebAssets.EnsureDeployed reads from at boot (AppContext.BaseDirectory == Core) before copying
// each site into Core\litebox\web\. So write to Core\web-assets\, NOT the served Core\litebox\web\ (that stays
// EnsureDeployed's job, keyed on its version stamp).
//
// The light build embeds nothing here (its zip ships the assets loose), so this is a no-op there. Every failure
// is non-fatal: a missing web payload just means the Web module falls back to its placeholder theme.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LbApiHost.Host.Install;

internal static class WebPayload
{
    private const string Prefix = "webassets/";

    /// <summary>True when this build actually carries embedded web assets (standalone installer).</summary>
    public static bool IsEmbedded()
        => typeof(WebPayload).Assembly.GetManifestResourceNames()
               .Any(n => n.StartsWith(Prefix, StringComparison.Ordinal));

    /// <summary>Rebuilds the embedded web-assets tree into <paramref name="coreDir"/>\web-assets\. Returns an
    /// error string on failure, or null on success / nothing embedded. Never throws.</summary>
    public static string? ExtractToCore(string coreDir)
    {
        try
        {
            var asm = typeof(WebPayload).Assembly;
            var names = asm.GetManifestResourceNames()
                           .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            if (names.Length == 0) return null;   // light build (or no assets) → placeholder theme

            string root = Path.Combine(coreDir, "web-assets");
            int written = 0;
            foreach (var n in names)
            {
                // LogicalName is "webassets/<RecursiveDir><file>"; RecursiveDir uses '\' on Windows — normalise
                // both separators to a real relative path.
                string rel = n.Substring(Prefix.Length)
                              .Replace('/', Path.DirectorySeparatorChar)
                              .Replace('\\', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(rel)) continue;
                string dst = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using var s = asm.GetManifestResourceStream(n);
                if (s == null) continue;
                using (var f = File.Create(dst)) s.CopyTo(f);
                written++;
            }
            Console.WriteLine($"[installer] deployed {written} web-asset file(s) into Core\\web-assets");
            return null;
        }
        catch (Exception ex) { return "Could not extract web assets into Core:\n" + ex.Message; }
    }
}
