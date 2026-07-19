// Deploys the bundled web-frontend assets into their served folders under Core\litebox\web\.
//
// The integrator populates AppContext.BaseDirectory\web-assets\{bigbox,litebox,vendor}\ (a build-output copy
// of whatever asset tree is shipped — this file NEVER creates or fetches an asset, it only copies what is
// there). Each present sub-folder is copied into the matching LiteBoxPaths.Web(site) folder when the target
// is stale: a version stamp file (holding the LiteBox assembly version) is written after each copy, so a
// version bump re-ships edited assets — mirroring how NativeInstaller / MagickSupport refresh natives on an
// upgrade. When no web-assets\ folder is present (a plain framework-dependent dev build with no bundled
// assets) it is a no-op. The database site is 100% server-rendered, so its folder is only ensured to exist.
// Idempotent; every failure is swallowed and logged.

#nullable enable

using System;
using System.IO;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Install;

namespace LbApiHost.Host.Web;

internal static class WebAssets
{
    // Sub-folders the integrator may ship under web-assets\ (source name == served site name).
    private static readonly string[] Sites = { "bigbox", "litebox", "vendor" };

    // Stamp file dropped in each served folder; its content is the LiteBox version that populated the folder.
    private const string StampName = ".litebox-web-stamp";

    /// <summary>Copies bundled assets into their served folders when stale, and ensures the database site
    /// folder exists. Safe + cheap to call repeatedly.</summary>
    public static void EnsureDeployed()
    {
        try
        {
            // The database site is server-rendered — just make sure the folder exists (empty is fine).
            LiteBoxPaths.Web("database");

            // Source staging lives under Core\litebox\ (LiteBox-own, removed with the data dir on uninstall).
            // Fall back to the pre-relocation Core\web-assets so an existing install still deploys.
            string baseDir = Path.Combine(LiteBoxPaths.Data, "web-assets");
            if (!Directory.Exists(baseDir))
            {
                string legacy = Path.Combine(AppContext.BaseDirectory, "web-assets");
                if (Directory.Exists(legacy)) baseDir = legacy;
                else { LbLog.Info("web", "no bundled web assets to deploy"); return; }
            }

            string version = LiteBoxVersion.Current.ToString();
            foreach (var site in Sites)
            {
                string src = Path.Combine(baseDir, site);
                if (!Directory.Exists(src)) continue;   // integrator didn't ship this site

                string dst = LiteBoxPaths.Web(site);
                string stamp = Path.Combine(dst, StampName);
                if (ReadStamp(stamp) == version) continue;   // already current

                CopyTree(src, dst);
                try { File.WriteAllText(stamp, version); } catch { }
                LbLog.Info("web", $"deployed web assets '{site}' (v{version})");
            }
        }
        catch (Exception ex) { LbLog.Warn("web", "asset deploy failed: " + ex.Message); }
    }

    private static string? ReadStamp(string stampPath)
    {
        try { return File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null; }
        catch { return null; }
    }

    // Recursively copies every file under src into dst (overwriting), preserving the sub-tree. Existing
    // extra files in dst are left in place — a copy, not a mirror.
    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyTree(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}
