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
    /// whether or not the user ever ran Install.</summary>
    public static System.Collections.Generic.IEnumerable<string> DeployedRelPaths()
    {
        yield return Path.Combine("Core", LoaderName);
        yield return Path.Combine("Core", TriggerName);
        yield return Path.Combine("Plugins", PluginDir, ManagedName);
        yield return Path.Combine("Plugins", PluginDir, NativeBin);
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
            Directory.CreateDirectory(pluginDst);
            CopyApi(SourceDir, pluginDst, ManagedName + ".api", ManagedName);
            CopyApi(SourceDir, pluginDst, NativeBin + ".api",   NativeBin);
            CopyApi(SourceDir, core,      LoaderName + ".api",  LoaderName);
            CopyApi(SourceDir, core,      TriggerName + ".api", TriggerName);
            Log($"installed → Core + Plugins\\{PluginDir}");
            return (true, "Native parental control installed. Restart LaunchBox / BigBox for it to take effect.");
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

        Del(Path.Combine(core, TriggerName));   // first: no trigger → the startup hook never fires next launch
        Del(Path.Combine(core, LoaderName));
        var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
        Del(Path.Combine(pluginDst, ManagedName));
        Del(Path.Combine(pluginDst, NativeBin));
        try { if (Directory.Exists(pluginDst) && Directory.GetFileSystemEntries(pluginDst).Length == 0) Directory.Delete(pluginDst); } catch { }

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
