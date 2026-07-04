// --probe-saves <title-substring> [--lbroot <LB>] : headless, read-only diagnostic of the Game Saves
// scan pipeline (the Edit Game → Game Saves page). Reproduces EXACTLY what the page does — real
// GameStore, real DataManager, real integration plugins — and prints every link in the chain:
//   NamingHelper.RootFolder, effective command lines, each candidate plugin's raw GetSaves result
//   (or full exception), RetroArch's resolved save/state directories, then SaveManager.ScanBase.
// Never writes (store stays ReadOnly).
//
// Run:  dotnet run -f net10.0-windows -- --probe-saves "Secret of Mana"

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using LbApiHost.Host;
using LbApiHost.Host.Data;
using LbApiHost.Host.Saves;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class ProbeSaves
{
    public static int Run(string[] args)
    {
        string lbRoot = GetArg(args, "--lbroot") ?? @"C:\Users\mehdi\source\repos\scrapper-project\LB";
        string core = Path.Combine(lbRoot, "Core");
        string pluginsRoot = Path.Combine(lbRoot, "Plugins");
        var probeDirs = new List<string> { core };
        try { probeDirs.AddRange(Directory.GetDirectories(pluginsRoot).Where(d => d.Contains("LaunchBox Integration"))); } catch { }
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var d in probeDirs)
            {
                var p = Path.Combine(d, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            }
            return null;
        };
        return RunCore(args, lbRoot, pluginsRoot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunCore(string[] args, string lbRoot, string pluginsRoot)
    {
        int idx = Array.IndexOf(args, "--probe-saves");
        string needle = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")) ? args[idx + 1] : "Secret of Mana";

        string platformsDir = Path.Combine(lbRoot, "Data", "Platforms");
        if (!Directory.Exists(platformsDir)) { Console.WriteLine("[probe] platforms dir not found: " + platformsDir); return 1; }

        var store = GameStore.Load(platformsDir);
        store.ReadOnly = true;   // hard guarantee: diagnostic never writes
        string dataDir = Path.GetFullPath(Path.Combine(platformsDir, ".."));
        string imagesRoot = Path.Combine(lbRoot, "Images");
        LbApiHost.Host.Media.MediaResolver.Init(lbRoot);
        HostBoot.SetLaunchBoxCoreRootFolder(lbRoot);

        // Read RootFolder back to prove the reflection set worked in THIS process.
        try
        {
            var t = Type.GetType("Unbroken.LaunchBox.NamingHelper, Unbroken.LaunchBox");
            Console.WriteLine($"[probe] NamingHelper type={(t == null ? "NOT FOUND" : t.AssemblyQualifiedName)}");
            var p = t?.GetProperty("RootFolder", BindingFlags.Public | BindingFlags.Static);
            Console.WriteLine($"[probe] NamingHelper.RootFolder = {(p == null ? "<no property>" : p.GetValue(null) ?? "<null>")}");
        }
        catch (Exception ex) { Console.WriteLine("[probe] NamingHelper read-back threw: " + ex); }

        // The RetroArch plugin finds retroarch.cfg via Path.Combine(dir(emulator.ApplicationPath),
        // "retroarch.cfg") — RELATIVE when ApplicationPath is relative → resolved against CWD. The real
        // app sets CWD=lbRoot (Program.cs). Report both, and try with CWD=lbRoot like the real app.
        string relCfg = @"Emulators\RetroArch\retroarch.cfg";
        Console.WriteLine($"[probe] CWD before = {Environment.CurrentDirectory}");
        Console.WriteLine($"[probe]   File.Exists(\"{relCfg}\") from CWD = {File.Exists(relCfg)}");
        if (args.Contains("--cwd-lbroot")) { Environment.CurrentDirectory = lbRoot; Console.WriteLine($"[probe] CWD set to lbRoot = {Environment.CurrentDirectory}  File.Exists(rel)={File.Exists(relCfg)}"); }

        var dm = new HostDataManagerXml(store, dataDir, imagesRoot);
        PluginHelper.DataManager = dm;

        // --all-plugins: load EVERY plugin folder (ExtendDB included), like the real GUI — so we can
        // reproduce any interference (e.g. a plugin changing the CWD). Default: integration plugins only.
        var pluginDirs = args.Contains("--all-plugins")
            ? new[] { pluginsRoot }.Concat(Directory.GetDirectories(pluginsRoot)).ToArray()
            : Directory.GetDirectories(pluginsRoot).Where(d => d.Contains("LaunchBox Integration")).ToArray();
        Console.WriteLine($"[probe] loading plugins from: {string.Join(", ", pluginDirs.Select(Path.GetFileName))}");
        var reg = PluginLoader.LoadFrom(pluginDirs);
        EmuPlugins.Configure(reg);
        Console.WriteLine($"[probe] emulator plugins loaded: {reg.EmulatorPlugins.Count}");
        Console.WriteLine($"[probe] CWD after plugin load = {Environment.CurrentDirectory}");

        var game = dm.GetAllGames().FirstOrDefault(g =>
        {
            try { return (g.Title ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; }
        });
        if (game == null) { Console.WriteLine($"[probe] no game matched '{needle}'"); return 1; }

        Console.WriteLine($"\n[probe] game: \"{game.Title}\" id={game.Id}");
        Console.WriteLine($"[probe]   platform={game.Platform} emuId={game.EmulatorId} appPath={game.ApplicationPath}");
        Console.WriteLine($"[probe]   game.GetEffectiveCommandLine() = \"{Try(() => game.GetEffectiveCommandLine())}\"");
        var apps = game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>();
        foreach (var a in apps)
            Console.WriteLine($"[probe]   app \"{Try(() => a.Name)}\": path={Try(() => a.ApplicationPath)} emuId={Try(() => a.EmulatorId)} useEmu={Try(() => a.UseEmulator.ToString())} effCmd=\"{Try(() => a.GetEffectiveCommandLine())}\"");

        foreach (var emu in dm.GetAllEmulators())
        {
            var plugin = EmuPlugins.ForEmulator(emu);
            if (plugin == null) continue;
            bool sup = false; try { sup = plugin.SupportsSaveManagement(); } catch (Exception ex) { Console.WriteLine("[probe]   SupportsSaveManagement threw: " + ex.Message); }
            Console.WriteLine($"\n[probe] candidate: emu=\"{emu.Title}\" path={emu.ApplicationPath} plugin={plugin.GetType().Name} supportsSaveMgmt={sup}");
            if (!sup) continue;

            // RetroArch's public static directory resolvers — pinpoint WHERE the chain dies.
            foreach (var mName in new[] { "GetGameSaveDirectory", "GetSaveStateDirectory" })
            {
                var m = plugin.GetType().GetMethod(mName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) continue;
                foreach (var a in apps)
                {
                    try
                    {
                        object? dir = m.Invoke(null, new object?[] { emu, game, a, false });
                        Console.WriteLine($"[probe]   {mName}(app \"{Try(() => a.Name)}\") = {dir ?? "<null>"}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[probe]   {mName} threw: {(ex.InnerException ?? ex)}"); }
                }
                try
                {
                    object? dir = m.Invoke(null, new object?[] { emu, game, null, false });
                    Console.WriteLine($"[probe]   {mName}(game) = {dir ?? "<null>"}");
                }
                catch (Exception ex) { Console.WriteLine($"[probe]   {mName}(game) threw: {(ex.InnerException ?? ex)}"); }
            }

            try
            {
                var resp = plugin.GetSaves(new GetSavesArgs { Emulator = emu, Games = new[] { game }, AdditionalApplications = apps });
                Console.WriteLine($"[probe]   GetSaves: success={resp?.WasSuccess} msg=\"{resp?.Message}\" found={resp?.FoundSaves?.Count ?? -1}");
                foreach (var s in resp?.FoundSaves ?? Array.Empty<GameSaveBase>())
                    Console.WriteLine($"[probe]     - {s.GetType().Name} file={s.FileLocation} appId={s.AdditionalApplicationId} core={s.EmulatorCore} group=\"{s.SaveGroupName}\" slot={(s as GameSaveState)?.Slot}");
            }
            catch (Exception ex) { Console.WriteLine("[probe]   GetSaves THREW: " + ex); }
        }

        // --on-threadpool: run the scan on a Task.Run thread like the real app's ReloadGameSaves does —
        // tests whether the plugin's native anti-tamper cctor (SendStackCalculator/…) is thread-sensitive.
        if (args.Contains("--on-threadpool"))
        {
            Console.WriteLine("\n[probe] ==== plugin GetSaves ON A THREADPOOL THREAD ====");
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var (emu, plugin) in EmuPlugins.All.Select(p => (p.GetApplicableEmulators(dm.GetAllEmulators())?.FirstOrDefault(), p)).Where(x => x.Item1 != null))
                {
                    try
                    {
                        var resp = plugin.GetSaves(new GetSavesArgs { Emulator = emu, Games = new[] { game }, AdditionalApplications = apps });
                        Console.WriteLine($"[probe][pool] {plugin.GetType().Name}.GetSaves found={resp?.FoundSaves?.Count ?? -1}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[probe][pool] {plugin.GetType().Name}.GetSaves THREW: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
                }
            }).GetAwaiter().GetResult();
        }

        Console.WriteLine("\n[probe] ==== SaveManager.ScanBase ====");
        var scan = SaveManager.ScanBase(game);
        Console.WriteLine($"[probe] error={scan.Error ?? "<none>"} candidates={scan.Candidates.Count} gameEmuSupported={scan.GameEmulatorSupported}");
        foreach (var g in scan.Files.Concat(scan.States))
            Console.WriteLine($"[probe]   group \"{g.GroupName}\" state={g.IsState} slot={g.Slot} active={(g.Active != null)} live={g.ActiveLive} recordOnly={g.RecordOnly} path={g.ActivePath} backups={g.Backups.Count} emu={g.EmulatorFileName} core={g.EmulatorCore}");
        return 0;
    }

    private static string Try(Func<string?> f) { try { return f() ?? ""; } catch (Exception ex) { return "<threw: " + ex.Message + ">"; } }

    private static string? GetArg(string[] args, string name)
    { int i = Array.IndexOf(args, name); return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null; }
}
