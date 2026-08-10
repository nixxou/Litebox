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

    /// <summary>Where LiteBox ships the payload: Core\litebox\parental-native\.</summary>
    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "litebox", "parental-native");

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
                foreach (var f in new[] { AsiName, LoaderName, PluginName, HarmonyName })
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
        var src = Path.Combine(srcDir, name);
        var dst = Path.Combine(dstDir, name);
        var tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        try { File.Move(tmp, dst, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    private static void Log(string m) => LbLog.Info("parental", "native-install: " + m);
}
