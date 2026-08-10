// ─────────────────────────────────────────────────────────────────────────────
// Install / uninstall the native parental payload into the LB install (WS6).
// ─────────────────────────────────────────────────────────────────────────────
//
// Deploys the native pieces that enforce parental control inside vanilla
// LaunchBox.exe / BigBox.exe:
//   • LB\Core\litebox-parentalcontrol.asi   — the read-filter (WS5.1)
//   • LB\Core\winhttp.dll                    — the Ultimate ASI Loader that loads it
//   • LB\Plugins\litebox-parentalcontrol\    — the managed write-guard plugin (WS5.2)
//       litebox-parentalcontrol.dll + 0Harmony.dll
//
// The write-guard plugin is DUAL-TARGET (net9 for LB 13.27, net10 for LB 13.28+). LiteBox ships
// BOTH builds as litebox-parentalcontrol.net9.dll.api / .net10.dll.api and deploys the one matching
// THIS host's runtime (a Core host is always its Core's TFM, which is the LaunchBox/BigBox TFM), as
// the single bare litebox-parentalcontrol.dll. The ASI + winhttp + 0Harmony are TFM-agnostic (native,
// or a net9 build that loads on both) so they stay single-copy.
//
// RefreshDeployedIfInstalled re-deploys the matching guard on every boot, so an LB upgrade across the
// net9↔net10 boundary self-heals (the root launcher re-extracts the matching host, which then swaps in
// the matching guard). The ASI's SAFETY INTERLOCK means order doesn't create a danger window — it
// refuses to filter until the write-guard plugin dll is present, and nothing takes effect until the
// next LaunchBox/BigBox launch. Uninstall removes winhttp.dll FIRST (so the loader never loads the ASI
// again) then the rest.
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
    private const string AsiName     = "litebox-parentalcontrol.asi";
    private const string LoaderName  = "winhttp.dll";
    private const string PluginName  = "litebox-parentalcontrol.dll";   // the DEPLOYED (bare) guard name
    private const string HarmonyName = "0Harmony.dll";
    private const string PluginDir   = "litebox-parentalcontrol";       // under LB\Plugins

    /// <summary>The runtime moniker of THIS host build (= its Core's runtime = the LaunchBox/BigBox runtime the
    /// guard must match). Compile-time — no detection needed.</summary>
    private const string Tfm =
#if NET10_0_OR_GREATER
        "net10";
#else
        "net9";
#endif

    /// <summary>The guard's SOURCE .api file for this runtime (per-TFM). Deployed AS the bare <see cref="PluginName"/>.</summary>
    private static string PluginApiFile => $"litebox-parentalcontrol.{Tfm}.dll.api";

    /// <summary>Where LiteBox ships the payload: Core\litebox\parental-native\ — each file suffixed
    /// ".api" (so the ASI loader / plugin scanner never picks them up from the shipped location), the
    /// Install button strips it on deploy. Under Core\litebox\, so LiteBox's own uninstall wipes the
    /// SOURCE for free; the DEPLOYED copies are removed via <see cref="DeployedRelPaths"/>.</summary>
    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "litebox", "parental-native");

    /// <summary>The SOURCE .api files this host needs to install: the three TFM-agnostic ones + this runtime's
    /// guard build. (Both guard builds ship; only ours is required to install here.)</summary>
    private static IEnumerable<string> RequiredApiFiles()
    {
        yield return AsiName + ".api";
        yield return LoaderName + ".api";
        yield return HarmonyName + ".api";
        yield return PluginApiFile;
    }

    /// <summary>Deployed target paths (relative to LB root) — for the LiteBox uninstaller to remove
    /// whether or not the user ever ran Install. Deployed names are the bare (TFM-agnostic) names.</summary>
    public static System.Collections.Generic.IEnumerable<string> DeployedRelPaths()
    {
        yield return Path.Combine("Core", AsiName);
        yield return Path.Combine("Core", LoaderName);
        yield return Path.Combine("Plugins", PluginDir, PluginName);
        yield return Path.Combine("Plugins", PluginDir, HarmonyName);
    }

    /// <summary>The deployed plugin folder (relative to LB root) — the uninstaller rmdir's it.</summary>
    public static string DeployedPluginDirRel => Path.Combine("Plugins", PluginDir);

    /// <summary>Ensure the payload SOURCE (the .api files) exists at Core\litebox\parental-native\. The light
    /// zip ships them loose there (no-op); the standalone installer embeds them as resources — extract those
    /// here at boot when the folder is missing them. Extracts EVERY embedded parental-native/* resource (both
    /// guard builds + the shared three). Best-effort; called once from HostBoot.</summary>
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

    /// <summary>True when the loader + ASI are deployed in Core (i.e. the native filter is installed).</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                var core = CoreDir();
                return core != null && File.Exists(Path.Combine(core, LoaderName)) && File.Exists(Path.Combine(core, AsiName));
            }
            catch { return false; }
        }
    }

    /// <summary>True when the payload LiteBox ships is actually present to install from (this runtime's set).</summary>
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

    /// <summary>Deploy the ASI + loader into Core and the plugin (+ Harmony) into Plugins.
    /// Returns (ok, message). Idempotent — re-copies over an existing install.</summary>
    public static (bool ok, string message) Install()
    {
        var core = CoreDir();
        if (core == null) return (false, "LaunchBox install folder not found.");
        if (!PayloadAvailable) return (false, "The native parental payload is missing next to LiteBox (Core\\litebox\\parental-native).");

        var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
        try
        {
            Directory.CreateDirectory(pluginDst);
            // Plugin first, then the ASI + loader — so the ASI's write-guard-presence interlock is
            // always satisfied whenever the loader brings the ASI up on the next launch.
            CopyApi(SourceDir, pluginDst, PluginApiFile,       PluginName);   // per-TFM guard → bare name
            CopyApi(SourceDir, pluginDst, HarmonyName + ".api", HarmonyName);
            CopyApi(SourceDir, core,      AsiName + ".api",     AsiName);
            CopyApi(SourceDir, core,      LoaderName + ".api",  LoaderName);
            Log($"installed ({Tfm}) → Core + Plugins\\{PluginDir}");
            return (true, "Native parental control installed. Restart LaunchBox / BigBox for it to take effect.");
        }
        catch (Exception ex)
        {
            Log("install failed: " + ex.Message);
            return (false, "Install failed (a file may be locked — close LaunchBox / BigBox): " + ex.Message);
        }
    }

    /// <summary>Migration hook: when the native filter is installed, re-deploy the guard matching THIS runtime
    /// (and refresh the shared files). Self-heals after an LB upgrade across the net9↔net10 boundary — the
    /// previously-deployed guard is the wrong TFM and would fail to load, so we overwrite it with ours. Silent,
    /// best-effort (a running LaunchBox/BigBox locks the dll → skipped, harmless). Called once from HostBoot.</summary>
    public static void RefreshDeployedIfInstalled()
    {
        try
        {
            if (!IsInstalled) return;              // nothing deployed → nothing to migrate
            if (!PayloadAvailable) return;         // no source to refresh from
            var core = CoreDir();
            if (core == null) return;
            var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
            int refreshed = 0;
            refreshed += TryCopyApi(SourceDir, pluginDst, PluginApiFile,        PluginName)  ? 1 : 0;
            refreshed += TryCopyApi(SourceDir, pluginDst, HarmonyName + ".api", HarmonyName) ? 1 : 0;
            refreshed += TryCopyApi(SourceDir, core,      AsiName + ".api",     AsiName)     ? 1 : 0;
            refreshed += TryCopyApi(SourceDir, core,      LoaderName + ".api",  LoaderName)  ? 1 : 0;
            if (refreshed > 0) Log($"refreshed deployed payload for {Tfm} ({refreshed}/4 file(s))");
        }
        catch (Exception ex) { Log("RefreshDeployedIfInstalled failed: " + ex.Message); }
    }

    /// <summary>Remove the payload. Loader first (so the ASI is never loaded again), then the rest.
    /// Returns (ok, message).</summary>
    public static (bool ok, string message) Uninstall()
    {
        var core = CoreDir();
        if (core == null) return (false, "LaunchBox install folder not found.");
        var problems = new List<string>();
        void Del(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { problems.Add(Path.GetFileName(path) + ": " + ex.Message); } }

        Del(Path.Combine(core, LoaderName));   // first: no loader → the ASI never loads next launch
        Del(Path.Combine(core, AsiName));
        var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
        Del(Path.Combine(pluginDst, PluginName));
        Del(Path.Combine(pluginDst, HarmonyName));
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

    /// <summary>Best-effort <see cref="CopyApi"/> — returns false (never throws) if the source is missing or the
    /// destination is locked (e.g. LaunchBox/BigBox is running). Used by the migration refresh.</summary>
    private static bool TryCopyApi(string srcDir, string dstDir, string apiFile, string destName)
    {
        try { CopyApi(srcDir, dstDir, apiFile, destName); return true; }
        catch { return false; }
    }

    private static void Log(string m) => LbLog.Info("parental", "native-install: " + m);
}
