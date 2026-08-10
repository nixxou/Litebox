// ─────────────────────────────────────────────────────────────────────────────
// Install / uninstall the native parental payload into the LB install (WS6).
// ─────────────────────────────────────────────────────────────────────────────
//
// Deploys the two native pieces that enforce parental control inside vanilla
// LaunchBox.exe / BigBox.exe:
//   • LB\Core\litebox-parentalcontrol.asi   — the read-filter (WS5.1)
//   • LB\Core\winhttp.dll                    — the Ultimate ASI Loader that loads it
//   • LB\Plugins\litebox-parentalcontrol\    — the managed write-guard plugin (WS5.2)
//       litebox-parentalcontrol.dll + 0Harmony.dll
//
// LiteBox ships the four files under Core\litebox\parental-native\ (the SOURCE); this
// copies them into place. The ASI's SAFETY INTERLOCK means order doesn't create a
// danger window — it refuses to filter until the write-guard plugin dll is present, and
// nothing takes effect until the next LaunchBox/BigBox launch anyway. Uninstall removes
// winhttp.dll FIRST (so the loader never even loads the ASI again) then the rest.
//
// All best-effort: a locked/absent file yields a false result + a reason, never a throw.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

internal static class ParentalNativeInstall
{
    private const string AsiName     = "litebox-parentalcontrol.asi";
    private const string LoaderName  = "winhttp.dll";
    private const string PluginName  = "litebox-parentalcontrol.dll";
    private const string HarmonyName = "0Harmony.dll";
    private const string PluginDir   = "litebox-parentalcontrol";   // under LB\Plugins

    /// <summary>Where LiteBox ships the payload: Core\litebox\parental-native\ — each file suffixed
    /// ".api" (so the ASI loader / plugin scanner never picks them up from the shipped location), the
    /// Install button strips it on deploy. Under Core\litebox\, so LiteBox's own uninstall wipes the
    /// SOURCE for free; the DEPLOYED copies are removed via <see cref="DeployedRelPaths"/>.</summary>
    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "litebox", "parental-native");

    private static readonly string[] Names = { AsiName, LoaderName, PluginName, HarmonyName };

    /// <summary>Native parental control is offered ONLY on net10 (LaunchBox 13.28+). The managed write-guard
    /// plugin targets net10; on a net9 Core it cannot load, and the ASI's presence interlock keys on the DLL
    /// FILE — a present-but-unloadable guard would let the ASI filter reads with NO write-guard, and the next
    /// LaunchBox/BigBox save would overwrite the real library with the filtered subset (irreversible loss).
    /// LiteBox shares Core's runtime with LaunchBox/BigBox, so this build's own TFM is theirs. Uninstall stays
    /// available on every runtime so a payload deployed by an earlier build can always be cleaned up.</summary>
    public static bool SupportedOnThisRuntime =>
#if NET10_0_OR_GREATER
        true;
#else
        false;
#endif

    /// <summary>Deployed target paths (relative to LB root) — for the LiteBox uninstaller to remove
    /// whether or not the user ever ran Install.</summary>
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
    /// here at boot when the folder is missing them. Best-effort; called once from HostBoot.</summary>
    public static void EnsureShipped()
    {
        try
        {
            if (!SupportedOnThisRuntime)
            {
                // net9: the guard can't load here — never stage the payload. And if an EARLIER build already
                // deployed it (Core\winhttp.dll + ASI + the net10 plugin), that is now a data-loss trap on this
                // runtime (the ASI would filter while the guard sits unloadable), so actively remove it on
                // upgrade. Passive "don't reinstall" is not enough — the already-deployed copy must go.
                if (IsInstalled)
                {
                    var (ok, msg) = Uninstall();
                    Log((ok ? "net9 auto-cleanup of native parental: " : "net9 auto-cleanup INCOMPLETE (close LaunchBox/BigBox): ") + msg);
                }
                return;
            }
            if (PayloadAvailable) return;   // already loose (zip / dev deploy)
            var dir = SourceDir;
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            bool any = false;
            foreach (var name in Names)
            {
                using var s = asm.GetManifestResourceStream("parental-native/" + name + ".api");
                if (s == null) continue;
                Directory.CreateDirectory(dir);
                using var f = File.Create(Path.Combine(dir, name + ".api"));
                s.CopyTo(f);
                any = true;
            }
            if (any) Log("extracted embedded payload -> " + dir);
        }
        catch (Exception ex) { Log("EnsureShipped failed: " + ex.Message); }
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

    /// <summary>True when the payload LiteBox ships is actually present to install from.</summary>
    public static bool PayloadAvailable
    {
        get
        {
            try
            {
                var s = SourceDir;
                foreach (var f in Names)
                    if (!File.Exists(Path.Combine(s, f + ".api"))) return false;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Deploy the ASI + loader into Core and the plugin (+ Harmony) into Plugins.
    /// Returns (ok, message). Idempotent — re-copies over an existing install.</summary>
    public static (bool ok, string message) Install()
    {
        if (!SupportedOnThisRuntime)
            return (false, "Native parental control for vanilla LaunchBox / BigBox requires LaunchBox 13.28 or newer (.NET 10). LiteBox's own parental control still applies.");
        var core = CoreDir();
        if (core == null) return (false, "LaunchBox install folder not found.");
        if (!PayloadAvailable) return (false, "The native parental payload is missing next to LiteBox (Core\\litebox\\parental-native).");

        var pluginDst = Path.Combine(Root!, "Plugins", PluginDir);
        try
        {
            Directory.CreateDirectory(pluginDst);
            // Plugin first, then the ASI + loader — so the ASI's write-guard-presence interlock is
            // always satisfied whenever the loader brings the ASI up on the next launch.
            Copy(SourceDir, pluginDst, PluginName);
            Copy(SourceDir, pluginDst, HarmonyName);
            Copy(SourceDir, core, AsiName);
            Copy(SourceDir, core, LoaderName);
            Log($"installed → Core + Plugins\\{PluginDir}");
            return (true, "Native parental control installed. Restart LaunchBox / BigBox for it to take effect.");
        }
        catch (Exception ex)
        {
            Log("install failed: " + ex.Message);
            return (false, "Install failed (a file may be locked — close LaunchBox / BigBox): " + ex.Message);
        }
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

    private static void Copy(string srcDir, string dstDir, string name)
    {
        var src = Path.Combine(srcDir, name + ".api");   // shipped with the .api suffix; deploy the real name
        var dst = Path.Combine(dstDir, name);
        var tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        try { File.Move(tmp, dst, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    private static void Log(string m) => LbLog.Info("parental", "native-install: " + m);
}
