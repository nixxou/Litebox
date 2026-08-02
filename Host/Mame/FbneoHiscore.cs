// FbneoHiscore — deploy the FBNeo score-support database (hiscore.dat) so the RetroArch FBNeo core actually
// WRITES .hi files. Without it, FBNeo tracks no high score and there is nothing to submit.
//
// The core reads it from <system_directory>\fbneo\hiscore.dat. RetroArch's system_directory defaults to
// <RetroArch>\system but can be redirected in retroarch.cfg (incl. the ":" = install-dir token), so we resolve
// it from the cfg when present. We only write when the file is ABSENT — never clobber a user's own copy.
//
// Trigger: when the FBNeo-upload option is enabled (options apply) and, belt-and-suspenders, right before a
// FBNeo game launches. Bundled as the embedded resource "natives/fbneo-hiscore.dat" (see the csproj).

#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Mame;

internal static class FbneoHiscore
{
    private const string ResName = "natives/fbneo-hiscore.dat";

    /// <summary>Deploy the DB (if absent) for every RetroArch/FBNeo emulator in the library.</summary>
    public static void EnsureDeployedForAllFbneo()
    {
        foreach (var e in Safe(() => PluginHelper.DataManager?.GetAllEmulators()) ?? Array.Empty<IEmulator>())
            if (MameLeaderboards.IsFbneoRetroArch(e)) EnsureDeployed(e);
    }

    /// <summary>Where this RetroArch reads (or would read) the FBNeo hiscore.dat — resolved from its cfg.
    /// "" when the emulator has no usable path. Le lecteur du dat (HiscoreDat) s'en sert pour savoir quels
    /// jeux sont supportés : c'est le MÊME fichier, il ne doit pas y avoir deux idées de son emplacement.</summary>
    public static string DeployedPath(IEmulator? retroarch)
    {
        try
        {
            var ap = Safe(() => retroarch?.ApplicationPath);
            if (string.IsNullOrWhiteSpace(ap)) return "";
            string dir = Path.GetDirectoryName(Path.GetFullPath(ap!)) ?? "";
            return dir.Length == 0 ? "" : Path.Combine(ResolveSystemDir(dir), "fbneo", "hiscore.dat");
        }
        catch { return ""; }
    }

    /// <summary>Deploy the DB into this RetroArch emulator's system\fbneo dir if it isn't already there. Returns
    /// true when the file ends up present (already there or written). Never throws.</summary>
    public static bool EnsureDeployed(IEmulator? retroarch)
    {
        try
        {
            string dest = DeployedPath(retroarch);
            if (dest.Length == 0) return false;
            if (File.Exists(dest)) return true;   // user already has one → leave it
            bool ok = WriteTo(dest);
            // Un dat de plus sur le disque = des jeux de plus qui savent produire un score.
            if (ok) HiscoreDat.Invalidate();
            return ok;
        }
        catch (Exception ex) { Console.WriteLine("[fbneo] hiscore.dat deploy failed: " + ex.Message); return false; }
    }

    /// <summary>Write the embedded hiscore.dat to an explicit path (parent dir created). Returns false if the
    /// resource is missing or the write fails. Used by EnsureDeployed and the --fbneo-hiscore diag flag.</summary>
    public static bool WriteTo(string destFile)
    {
        try
        {
            using var s = typeof(FbneoHiscore).Assembly.GetManifestResourceStream(ResName);
            if (s == null) { Console.WriteLine("[fbneo] embedded hiscore.dat resource missing"); return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            using (var f = File.Create(destFile)) s.CopyTo(f);
            Console.WriteLine($"[fbneo] wrote hiscore.dat ({new FileInfo(destFile).Length:N0} bytes) → {destFile}");
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[fbneo] hiscore.dat write failed: " + ex.Message); return false; }
    }

    /// <summary>RetroArch's system directory: retroarch.cfg system_directory (":" token = install dir), else the
    /// default &lt;RetroArch&gt;\system.</summary>
    private static string ResolveSystemDir(string retroarchDir)
    {
        try
        {
            string cfg = Path.Combine(retroarchDir, "retroarch.cfg");
            if (File.Exists(cfg))
                foreach (var line in File.ReadLines(cfg))
                {
                    var m = Regex.Match(line, "^\\s*system_directory\\s*=\\s*\"?([^\"]*)\"?\\s*$");
                    if (!m.Success) continue;
                    string v = m.Groups[1].Value.Trim();
                    if (v.Length == 0 || v.Equals("default", StringComparison.OrdinalIgnoreCase)) break;   // → default
                    if (v.StartsWith(":")) return Path.GetFullPath(retroarchDir + v.Substring(1));          // ":\system"
                    return Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(retroarchDir, v));
                }
        }
        catch { }
        return Path.Combine(retroarchDir, "system");
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
