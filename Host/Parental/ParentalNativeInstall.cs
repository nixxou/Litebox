// ─────────────────────────────────────────────────────────────────────────────
// Install / uninstall the native parental payload into the LB install.
// ─────────────────────────────────────────────────────────────────────────────
//
// Single-artifact design (4 files, but the filter + guard are inseparable):
//   • LB\Core\winhttp.dll                 — the Ultimate ASI Loader (loads the trigger early).
//   • LB\Core\litebox-parental.asi        — a tiny GENERIC trigger: sets DOTNET_STARTUP_HOOKS to the
//                                           managed dll IF it exists. No business logic.
//   • LB\Plugins\litebox-parental\
//       litebox-parental.dll              — the managed dll (single net9, loads on both runtimes): its
//                                           StartupHook.Initialize LoadLibrary's + arms the .bin before
//                                           LaunchBox's Main, and it is also the lock/unlock plugin.
//       litebox-parental-native.bin       — the native hooks: CreateFileW read filter + CopyFileExW write
//                                           guard, ARMED TOGETHER by the managed startup hook.
//
// Atomicity is structural: the read filter and the write guard live in the SAME .bin, armed by one call;
// the .bin only arms if the managed dll loaded (startup hook) and found it. Missing managed dll → the
// trigger sets nothing → LaunchBox runs normally. So there is NO file-existence interlock, no boot
// migration, and no per-TFM guard juggling — those are gone with the two-artifact design.
//
// All best-effort: a locked/absent file yields a false result + a reason, never a throw.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

internal static class ParentalNativeInstall
{
    private const string LoaderName  = "winhttp.dll";                    // Core — the ASI loader
    private const string TriggerName = "litebox-parental.asi";           // Core — the DOTNET_STARTUP_HOOKS trigger
    private const string ManagedName = "litebox-parental.dll";           // Plugins\litebox-parental — the managed dll
    private const string NativeBin   = "litebox-parental-native.bin";    // Plugins\litebox-parental — the native hooks
    private const string PluginDir   = "litebox-parental";               // under LB\Plugins

    /// <summary>Where LiteBox ships the payload: Core\litebox\parental-native\ — each file suffixed ".api"
    /// (so the ASI loader / plugin scanner never picks them up from the shipped location); Install strips it
    /// on deploy. Under Core\litebox\, so LiteBox's own uninstall wipes the SOURCE for free; the DEPLOYED
    /// copies are removed via <see cref="DeployedRelPaths"/>.</summary>
    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "litebox", "parental-native");

    /// <summary>The four SOURCE .api files needed to install.</summary>
    private static IEnumerable<string> RequiredApiFiles()
    {
        yield return LoaderName + ".api";
        yield return TriggerName + ".api";
        yield return ManagedName + ".api";
        yield return NativeBin + ".api";
    }

    /// <summary>Deployed target paths (relative to LB root) — for the LiteBox uninstaller to remove
    /// whether or not the user ever ran Install. Covers the LIVE files AND the cross-redundancy ".api"
    /// backups (SelfHeal / the .asi restore them from each other), plus the runtime .dat backup. NEVER the
    /// shared litebox-parental.dat itself (LiteBox keeps using it).</summary>
    public static System.Collections.Generic.IEnumerable<string> DeployedRelPaths()
    {
        // live
        yield return Path.Combine("Core", LoaderName);
        yield return Path.Combine("Core", TriggerName);
        yield return Path.Combine("Plugins", PluginDir, ManagedName);
        yield return Path.Combine("Plugins", PluginDir, NativeBin);
        // Core-side stash (backs up the plugin files)
        yield return Path.Combine("Core", ManagedName + ".api");
        yield return Path.Combine("Core", NativeBin + ".api");
        // plugin-side stash (backs up the Core files) + the runtime config backup
        yield return Path.Combine("Plugins", PluginDir, LoaderName + ".api");
        yield return Path.Combine("Plugins", PluginDir, TriggerName + ".api");
        yield return Path.Combine("Plugins", PluginDir, "litebox-parental.dat.api");
    }

    /// <summary>The deployed plugin folder (relative to LB root) — the uninstaller rmdir's it.</summary>
    public static string DeployedPluginDirRel => Path.Combine("Plugins", PluginDir);

    /// <summary>Ensure the payload SOURCE (the .api files) exists at Core\litebox\parental-native\. The light
    /// zip ships them loose there (no-op); the standalone installer embeds them as resources — extract those
    /// here at boot when the folder is missing them. Best-effort; called once from HostBoot.</summary>
    public static void EnsureShipped()
    {
        try
        {
            if (PayloadAvailable) return;   // already loose (zip / dev deploy)
            ExtractEmbeddedTo(SourceDir);
        }
        catch (Exception ex) { Log("EnsureShipped failed: " + ex.Message); }
    }

    /// <summary>Extract the embedded payload directly into a target LB\Core\litebox\parental-native\ — used by
    /// the STANDALONE installer (which alone carries the embedded resources; the light host does not), so a
    /// fresh install has the SOURCE .api files present before the light host boots. No-op on a light build.</summary>
    public static void ExtractShippedToCore(string coreDir)
    {
        try { ExtractEmbeddedTo(Path.Combine(coreDir, "litebox", "parental-native")); }
        catch (Exception ex) { Log("ExtractShippedToCore failed: " + ex.Message); }
    }

    /// <summary>Write every embedded <c>parental-native/*</c> resource into <paramref name="destDir"/>. Returns
    /// how many were written (0 on a light build — nothing embedded there).</summary>
    private static int ExtractEmbeddedTo(string destDir)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        const string prefix = "parental-native/";
        int any = 0;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var fileName = res.Substring(prefix.Length);
            if (fileName.Length == 0) continue;
            using var s = asm.GetManifestResourceStream(res);
            if (s == null) continue;
            Directory.CreateDirectory(destDir);
            using var f = File.Create(Path.Combine(destDir, fileName));
            s.CopyTo(f);
            any++;
        }
        if (any > 0) Log($"extracted {any} embedded payload file(s) -> {destDir}");
        return any;
    }

    private static string? Root => LbApiHost.Host.Media.MediaResolver.LbRoot;

    /// <summary>True when the loader + trigger are deployed in Core (i.e. the native parental is installed).</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                var core = CoreDir();
                return core != null && File.Exists(Path.Combine(core, LoaderName)) && File.Exists(Path.Combine(core, TriggerName));
            }
            catch { return false; }
        }
    }

    /// <summary>True when the payload LiteBox ships is actually present to install from.</summary>
    public static bool PayloadAvailable
    {
        get
        {
            try
            {
                var s = SourceDir;
                foreach (var f in RequiredApiFiles())
                    if (!File.Exists(Path.Combine(s, f))) return false;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Deploy the managed dll + native .bin into Plugins\litebox-parental, then the loader + trigger
    /// into Core. Order: the Plugins files FIRST, so the trigger (which only sets the env var when the managed
    /// dll exists) never points at a missing target. Returns (ok, message). Idempotent.</summary>
    public static (bool ok, string message) Install()
    {
        var core = CoreDir();
        if (core == null) return (false, "LaunchBox install folder not found.");
        if (!PayloadAvailable) return (false, "The native parental payload is missing next to LiteBox (Core\\litebox\\parental-native).");

        var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
        try
        {
            var backupNote  = BackupDataXml();     // safety net BEFORE touching anything (best-effort)
            var importsNote = DisableAutoImports(); // turn auto ROM imports off once (best-effort)

            Directory.CreateDirectory(pluginDst);
            // LIVE files — plugin dll+bin FIRST so the trigger never points at a missing dll, then Core loader+trigger.
            CopyApi(SourceDir, pluginDst, ManagedName + ".api", ManagedName);
            CopyApi(SourceDir, pluginDst, NativeBin + ".api",   NativeBin);
            CopyApi(SourceDir, core,      LoaderName + ".api",  LoaderName);
            CopyApi(SourceDir, core,      TriggerName + ".api", TriggerName);
            // CROSS-REDUNDANCY STASHES so a LaunchBox update (wipes Core\) or a lost plugin dir can self-heal:
            //   • Core keeps .api backups of the plugin files (dll, bin) — restored by the native .asi;
            //   • the plugin folder keeps .api backups of the Core files (winhttp, asi) — restored by SelfHeal.
            CopyApi(SourceDir, core,      ManagedName + ".api",  ManagedName + ".api");
            CopyApi(SourceDir, core,      NativeBin + ".api",    NativeBin + ".api");
            CopyApi(SourceDir, pluginDst, LoaderName + ".api",   LoaderName + ".api");
            CopyApi(SourceDir, pluginDst, TriggerName + ".api",  TriggerName + ".api");

            Log($"installed → Core + Plugins\\{PluginDir} (live + stashes)");
            return (true, "Native parental control installed." + backupNote + importsNote
                + " Restart LaunchBox / BigBox for it to take effect.");
        }
        catch (Exception ex)
        {
            Log("install failed: " + ex.Message);
            return (false, "Install failed (a file may be locked — close LaunchBox / BigBox): " + ex.Message);
        }
    }

    /// <summary>Remove the payload. Trigger first (so the startup hook stops firing), then the loader, then the
    /// Plugins files. Returns (ok, message).</summary>
    public static (bool ok, string message) Uninstall()
    {
        var core = CoreDir();
        if (core == null) return (false, "LaunchBox install folder not found.");
        var problems = new List<string>();
        void Del(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { problems.Add(Path.GetFileName(path) + ": " + ex.Message); } }

        var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
        // LIVE — trigger first so the startup hook never fires next launch, then loader, then plugin files.
        Del(Path.Combine(core, TriggerName));
        Del(Path.Combine(core, LoaderName));
        Del(Path.Combine(pluginDst, ManagedName));
        Del(Path.Combine(pluginDst, NativeBin));
        // STASHES — must go too, or the .asi could resurrect the plugin from Core\*.api, and orphan .api files
        // would keep the plugin folder from being removed.
        Del(Path.Combine(core, ManagedName + ".api"));
        Del(Path.Combine(core, NativeBin + ".api"));
        Del(Path.Combine(pluginDst, LoaderName + ".api"));
        Del(Path.Combine(pluginDst, TriggerName + ".api"));
        Del(Path.Combine(pluginDst, "litebox-parental.dat.api"));   // runtime config backup SelfHeal writes
        // Any leftover plugin logs, then the folder if now empty. The shared litebox-parental.dat in Core is
        // NEVER touched — LiteBox keeps using it.
        try
        {
            if (Directory.Exists(pluginDst))
            {
                foreach (var f in Directory.GetFiles(pluginDst, "*.log")) Del(f);
                if (Directory.GetFileSystemEntries(pluginDst).Length == 0) Directory.Delete(pluginDst);
            }
        }
        catch { }

        if (problems.Count > 0)
        {
            Log("uninstall incomplete: " + string.Join("; ", problems));
            return (false, "Some files could not be removed (close LaunchBox / BigBox): " + string.Join("; ", problems));
        }
        Log("uninstalled");
        return (true, "Native parental control removed. Restart LaunchBox / BigBox to fully clear it.");
    }

    private static string? CoreDir()
    {
        var r = Root;
        if (string.IsNullOrEmpty(r)) return null;
        var core = Path.Combine(r!, "Core");
        return Directory.Exists(core) ? core : null;
    }

    // ── Pre-install extras (mirror the standalone installer) ─────────────────────

    /// <summary>Before install, archive every .xml under Data\ (hierarchy preserved) into
    /// Backups\ParentalControl-backupbeforeinstall-&lt;datetime&gt;.7z using the 7-Zip LaunchBox ships.
    /// Best-effort; returns a short note appended to the install message.</summary>
    private static string BackupDataXml()
    {
        try
        {
            var root = Root;
            if (string.IsNullOrEmpty(root)) return "";
            var data = Path.Combine(root!, "Data");
            if (!Directory.Exists(data)) return "";
            if (!Directory.EnumerateFiles(data, "*.xml", SearchOption.AllDirectories).Any()) return "";

            var sevenZip = SevenZipPath(root!);
            if (sevenZip == null) return " (Data XML not backed up — 7-Zip not found.)";
            var backups = Path.Combine(root!, "Backups");
            Directory.CreateDirectory(backups);
            var archive = Path.Combine(backups, "ParentalControl-backupbeforeinstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".7z");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sevenZip, WorkingDirectory = root!,   // paths stored relative to root → "Data\...\*.xml"
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("a"); psi.ArgumentList.Add(archive);
            psi.ArgumentList.Add(@"Data\*.xml"); psi.ArgumentList.Add("-r");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return " (Data XML backup could not start.)";
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return " (Data XML backup timed out.)"; }
            if (p.ExitCode == 0 || p.ExitCode == 1) { Log("backed up Data XML → " + archive); return " Backed up Data XML."; }
            return " (Data XML backup failed — 7-Zip exit " + p.ExitCode + ".)";
        }
        catch (Exception ex) { Log("backup failed: " + ex.Message); return ""; }
    }

    /// <summary>Disable LaunchBox's automatic ROM imports once at install (they'd mutate the library behind
    /// parental control). Edits Data\Settings.xml on disk. Best-effort; returns a short note.</summary>
    private static string DisableAutoImports()
    {
        try
        {
            var root = Root;
            if (string.IsNullOrEmpty(root)) return "";
            var path = Path.Combine(root!, "Data", "Settings.xml");
            if (!File.Exists(path)) return "";
            var xml = File.ReadAllText(path); var before = xml;
            foreach (var name in new[] { "EnableAutomatedImports", "EnableRomAutoImports" })
            {
                var pat = "<" + name + @"\b[^>]*/>|<" + name + @"\b[^>]*>[^<]*</" + name + ">";
                if (Regex.IsMatch(xml, pat)) xml = Regex.Replace(xml, pat, "<" + name + ">false</" + name + ">");
            }
            if (xml == before) return "";
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            Log("disabled automatic ROM imports");
            return " Disabled automatic ROM imports.";
        }
        catch (Exception ex) { Log("disable auto-imports failed: " + ex.Message); return ""; }
    }

    /// <summary>The 7-Zip LaunchBox ships (or a couple of fallbacks). Null when none is present.</summary>
    private static string? SevenZipPath(string root)
    {
        foreach (var rel in new[] { @"ThirdParty\7-Zip\7z.exe", @"Core\7z.exe", @"Core\7za.exe", "7z.exe", "7za.exe" })
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Copy a shipped .api SOURCE (apiFile) to its deployed DEST name, atomically (tmp + move).</summary>
    private static void CopyApi(string srcDir, string dstDir, string apiFile, string destName)
    {
        var src = Path.Combine(srcDir, apiFile);
        var dst = Path.Combine(dstDir, destName);
        var tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        try { File.Move(tmp, dst, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    private static void Log(string m) => LbLog.Info("parental", "native-install: " + m);
}
