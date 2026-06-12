// Dev probe: empirically answers "what does the RetroArch integration plugin DO
// to the command line?" by calling its PUBLIC contract with controlled inputs and
// printing the outputs — no decompilation, the DLL runs as-is.
//
//   • NormalizeCommandLineForExecutable on several command-line shapes
//   • GetCurrentVersion / SupportsRetroAchievements (read-only)
//   • PrepareEmulatorForLaunch with creds=null, then with fake credentials —
//     retroarch.cfg is BACKED UP first, diffed, and restored, so the probe
//     leaves no trace.
//
// Run:  dotnet run --project LiteBox.csproj -- --probe-emuplugin

using System.IO;
using System.Linq;
using System.Runtime.Loader;
using LbApiHost.Generated;
using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Tools;

internal static class EmuPluginProbe
{
    private const string LbRoot = @"C:\Users\mehdi\source\repos\scrapper-project\LB";

    /// <summary>Registers the resolver BEFORE the typed core is JIT-ed (RunCore
    /// references EmulatorPlugin, which needs the SDK resolvable at method entry).</summary>
    public static int Run()
    {
        var pluginDir = Path.Combine(LbRoot, "Plugins", "RetroArch LaunchBox Integration");
        var core = Path.Combine(LbRoot, "Core");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var d in new[] { pluginDir, core })
            {
                var p = Path.Combine(d, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            }
            return null;
        };
        return RunCore(pluginDir);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunCore(string pluginDir)
    {
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(pluginDir, "Unbroken.LaunchBox.Windows.RetroArch.dll"));
        var t = asm.GetType("Unbroken.LaunchBox.Windows.RetroArch.RetroArchPlugin")!;
        var plugin = (EmulatorPlugin)Activator.CreateInstance(t)!;
        Console.WriteLine($"plugin: {t.FullName}  EmulatorName=\"{plugin.EmulatorName}\"");

        var exe = Path.Combine(LbRoot, "Emulators", "RetroArch", "retroarch.exe");
        Console.WriteLine($"exe: {exe}  exists={File.Exists(exe)}");

        // ── NormalizeCommandLineForExecutable ────────────────────────────
        string[] cmdlines =
        {
            "",
            "-L \"cores\\snes9x_libretro.dll\"",
            "-L cores/snes9x_libretro.dll -f",
            "-f --fullscreen",
            "-L \"C:\\some\\abs\\path\\cores\\snes9x_libretro.dll\" -f",
            "-L \"cores\\snes9x_libretro2010.dll\"",
        };
        Console.WriteLine("\n== NormalizeCommandLineForExecutable ==");
        foreach (var c in cmdlines)
        {
            string r;
            try { r = plugin.NormalizeCommandLineForExecutable(c, exe); }
            catch (Exception ex) { r = "<threw: " + ex.Message + ">"; }
            Console.WriteLine($"  \"{c}\"\n    → \"{r}\"");
        }

        // ── Read-only info ───────────────────────────────────────────────
        Console.WriteLine("\n== GetCurrentVersion ==");
        try { Console.WriteLine("  " + plugin.GetCurrentVersion(exe)); }
        catch (Exception ex) { Console.WriteLine("  <threw: " + ex.Message + ">"); }

        Console.WriteLine("\n== SupportsRetroAchievements ==");
        try
        {
            var ra = plugin.SupportsRetroAchievements(exe);
            Console.WriteLine($"  supported={ra.IsSupported} enabled={ra.IsEnabled} hardcore={ra.IsHardcore}");
        }
        catch (Exception ex) { Console.WriteLine("  <threw: " + ex.Message + ">"); }

        // ── PrepareEmulatorForLaunch ─────────────────────────────────────
        var emu = new DummyEmulator
        {
            Id = "probe-emu",
            Title = "RetroArch",
            ApplicationPath = exe,
            CommandLine = "",
        };
        var game = new DummyGame
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Probe Game",
            Platform = "Super Nintendo Entertainment System",
            ApplicationPath = @"C:\fake\roms\probe.sfc",
        };

        var cfg = Path.Combine(LbRoot, "Emulators", "RetroArch", "retroarch.cfg");
        string? cfgBackup = null;
        if (File.Exists(cfg)) { cfgBackup = cfg + ".probe-bak"; File.Copy(cfg, cfgBackup, true); }

        try
        {
            Console.WriteLine("\n== PrepareEmulatorForLaunch (creds=null) ==");
            Probe(plugin, emu, game, "-L \"cores\\snes9x_libretro.dll\" -f", null);
            Probe(plugin, emu, game, "-f", null);

            Console.WriteLine("\n== PrepareEmulatorForLaunch (fake creds user=probeuser) ==");
            var creds = new RetroAchievementCredentials("probeuser", "probetoken123");
            Probe(plugin, emu, game, "-L \"cores\\snes9x_libretro.dll\" -f", creds);

            if (cfgBackup != null)
            {
                var before = File.ReadAllLines(cfgBackup);
                var after = File.ReadAllLines(cfg);
                var changed = after.Where(l => !before.Contains(l)).ToList();
                var removed = before.Where(l => !after.Contains(l)).ToList();
                Console.WriteLine("\n== retroarch.cfg diff after Prepare(creds) ==");
                if (changed.Count == 0 && removed.Count == 0) Console.WriteLine("  (no change)");
                foreach (var l in removed.Take(12)) Console.WriteLine("  - " + l);
                foreach (var l in changed.Take(12)) Console.WriteLine("  + " + l);
            }
        }
        finally
        {
            if (cfgBackup != null) { File.Copy(cfgBackup, cfg, true); File.Delete(cfgBackup); Console.WriteLine("\nretroarch.cfg restored."); }
        }

        return 0;
    }

    private static void Probe(EmulatorPlugin plugin, DummyEmulator emu, DummyGame game, string cmdline, RetroAchievementCredentials? creds)
    {
        try
        {
            var args = new PrepareForLaunchArgs(emu, game, cmdline, null, creds);
            var resp = plugin.PrepareEmulatorForLaunch(args);
            Console.WriteLine($"  in : \"{cmdline}\"");
            Console.WriteLine($"  out: success={resp?.WasSuccess} newCmd={(resp?.NewCommandLine == null ? "<null>" : "\"" + resp.NewCommandLine + "\"")} msg=\"{resp?.Message}\"");
        }
        catch (Exception ex) { Console.WriteLine($"  in : \"{cmdline}\"\n  out: <threw: {ex.InnerException?.Message ?? ex.Message}>"); }
    }
}
