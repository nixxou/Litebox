// Discovers and instantiates LaunchBox plugins from one or more folders.
//
// Plugins load into the DEFAULT AssemblyLoadContext so the SDK
// (Unbroken.LaunchBox.Plugins) unifies with the host's already-loaded copy —
// otherwise `obj is ISystemEventsPlugin` (host's type) would never match a
// plugin that loaded its own SDK copy. A Resolving probe satisfies each
// plugin's private dependencies from its own folder (and LB\Core).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Host;

internal sealed class PluginRegistry
{
    public List<object> All { get; } = new();
    public List<ISystemEventsPlugin> SystemEvents { get; } = new();
    public List<ISystemMenuItemPlugin> SystemMenus { get; } = new();
    public List<IGameMenuItemPlugin> GameMenus { get; } = new();
    public List<IGameMultiMenuItemPlugin> GameMultiMenus { get; } = new();
    public List<IGameLaunchingPlugin> GameLaunching { get; } = new();
    public List<IGameConfiguringPlugin> GameConfiguring { get; } = new();
    public List<IBigBoxThemeElementPlugin> ThemeElements { get; } = new();
    /// <summary>Emulator-integration plugins (RetroArch/Dolphin/MAME "LaunchBox
    /// Integration" DLLs): subclasses of the PUBLIC SDK abstract class
    /// <see cref="EmulatorPlugin"/>. The host only CALLS their public contract
    /// (install/update, bios files, launch preparation, cores…) — their DLLs run
    /// untouched, exactly as under LaunchBox.</summary>
    public List<EmulatorPlugin> EmulatorPlugins { get; } = new();
}

internal static class PluginLoader
{
    private static readonly object _lock = new();
    private static bool _resolverAdded;
    private static readonly List<string> _probeDirs = new();

    public static PluginRegistry LoadFrom(IEnumerable<string> dirs)
    {
        var reg = new PluginRegistry();
        var dirList = dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                          .Select(Path.GetFullPath).Distinct().ToList();
        if (dirList.Count == 0) { Console.WriteLine("[loader] no existing plugin dirs."); return reg; }

        // Gather DLLs from each dir AND its immediate subdirs (LB layout is
        // LB\Plugins\<PluginName>\<plugin>.dll). Register every containing
        // folder as a probe dir so each plugin's private deps resolve.
        var dllFiles = new List<string>();
        foreach (var dir in dirList)
        {
            dllFiles.AddRange(Directory.GetFiles(dir, "*.dll"));
            foreach (var sub in Directory.GetDirectories(dir))
                try { dllFiles.AddRange(Directory.GetFiles(sub, "*.dll")); } catch { }
        }
        dllFiles = dllFiles.Distinct().ToList();
        AddResolver(dllFiles.Select(Path.GetDirectoryName).Distinct().ToList());

        {
            foreach (var dll in dllFiles)
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (name.Equals("Unbroken.LaunchBox.Plugins", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("LbApiHost", StringComparison.OrdinalIgnoreCase)) continue;

                Assembly asm;
                try { asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); }
                catch (Exception ex) { continue; /* not a managed/loadable asm */ }

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface) continue;
                    if (!ImplementsAnyPluginIface(t)) continue;

                    object inst;
                    try { inst = Activator.CreateInstance(t); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[loader] ctor {t.FullName} failed: {ex.InnerException?.Message ?? ex.Message}");
                        continue;
                    }
                    Register(reg, inst);
                    Console.WriteLine($"[loader] + {t.FullName}  ({string.Join(", ", Roles(inst))})  [{Path.GetFileName(dll)}]");
                }
            }
        }
        return reg;
    }

    private static bool ImplementsAnyPluginIface(Type t) =>
        typeof(ISystemEventsPlugin).IsAssignableFrom(t) ||
        typeof(ISystemMenuItemPlugin).IsAssignableFrom(t) ||
        typeof(IGameMenuItemPlugin).IsAssignableFrom(t) ||
        typeof(IGameMultiMenuItemPlugin).IsAssignableFrom(t) ||
        typeof(IGameLaunchingPlugin).IsAssignableFrom(t) ||
        typeof(IGameConfiguringPlugin).IsAssignableFrom(t) ||
        typeof(IBigBoxThemeElementPlugin).IsAssignableFrom(t) ||
        typeof(EmulatorPlugin).IsAssignableFrom(t);

    private static void Register(PluginRegistry reg, object inst)
    {
        reg.All.Add(inst);
        if (inst is ISystemEventsPlugin se) reg.SystemEvents.Add(se);
        if (inst is ISystemMenuItemPlugin sm) reg.SystemMenus.Add(sm);
        if (inst is IGameMenuItemPlugin gm) reg.GameMenus.Add(gm);
        if (inst is IGameMultiMenuItemPlugin gmm) reg.GameMultiMenus.Add(gmm);
        if (inst is IGameLaunchingPlugin gl) reg.GameLaunching.Add(gl);
        if (inst is IGameConfiguringPlugin gc) reg.GameConfiguring.Add(gc);
        if (inst is IBigBoxThemeElementPlugin te) reg.ThemeElements.Add(te);
        if (inst is EmulatorPlugin ep) reg.EmulatorPlugins.Add(ep);
    }

    private static IEnumerable<string> Roles(object inst)
    {
        if (inst is ISystemEventsPlugin) yield return "events";
        if (inst is ISystemMenuItemPlugin) yield return "sysmenu";
        if (inst is IGameMenuItemPlugin) yield return "gamemenu";
        if (inst is IGameMultiMenuItemPlugin) yield return "gamemultimenu";
        if (inst is IGameLaunchingPlugin) yield return "launching";
        if (inst is IGameConfiguringPlugin) yield return "configuring";
        if (inst is IBigBoxThemeElementPlugin) yield return "themeelement";
        if (inst is EmulatorPlugin) yield return "emulator";
    }

    private static void AddResolver(List<string> dirs)
    {
        lock (_lock)
        {
            foreach (var d in dirs) if (!_probeDirs.Contains(d)) _probeDirs.Add(d);
            const string core = @"C:\Users\mehdi\source\repos\scrapper-project\LB\Core";
            if (Directory.Exists(core) && !_probeDirs.Contains(core)) _probeDirs.Add(core);
            // LaunchBox resolves its bundled third-party assemblies (CefSharp,
            // libcef, etc.) from ThirdParty\Chromium — plugins reference them
            // with Private=false, so we must probe there too.
            var chromium = Path.GetFullPath(Path.Combine(core, "..", "ThirdParty", "Chromium"));
            if (Directory.Exists(chromium) && !_probeDirs.Contains(chromium)) _probeDirs.Add(chromium);

            if (_resolverAdded) return;
            _resolverAdded = true;
            AssemblyLoadContext.Default.Resolving += (ctx, an) =>
            {
                foreach (var d in _probeDirs)
                {
                    var p = Path.Combine(d, an.Name + ".dll");
                    if (File.Exists(p)) { try { return ctx.LoadFromAssemblyPath(p); } catch { } }
                }
                return null;
            };
        }
    }
}
