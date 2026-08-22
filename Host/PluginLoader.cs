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

    /// <summary>The real LB Core folder, set by the host (HostBoot / probes) from the resolved LB root
    /// BEFORE LoadFrom. Null → derived by <see cref="ResolveCoreDir"/>.</summary>
    public static string LbCoreDir;

    /// <summary>LB's Core for dependency probing: the explicit <see cref="LbCoreDir"/> when set; else
    /// the exe's own folder when it IS Core (installed layout — LiteBox.exe lives beside LaunchBox.dll);
    /// else the dev-repo sibling LB (a `dotnet run` from the repo, where the exe folder is bin\…).
    /// Also the DEV fallback of Program.cs's global assembly resolver (SDK & friends in bin runs).</summary>
    internal static string ResolveCoreDir()
    {
        if (!string.IsNullOrEmpty(LbCoreDir) && Directory.Exists(LbCoreDir)) return LbCoreDir;
        string baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(baseDir, "LaunchBox.dll"))) return baseDir;
        return @"C:\Users\mehdi\source\repos\scrapper-project\LB\Core";   // dev fallback (repo-sibling LB)
    }

    /// <param name="dirs">Folders to SCAN, one plugin each — LB 14's System\Plugins, which LaunchBox owns
    /// and lays out that way. Scanned at the folder and one level below it, as before.</param>
    /// <param name="files">Plugin DLLs to load by name — everything under Plugins\, where a plugin is a file
    /// found at any depth. Passed as files rather than as their folder: that folder holds the plugin's
    /// dependencies and, often, other plugins the user disabled.</param>
    /// <param name="probeDirs">Resolver-only. Never scanned; they exist so a loaded plugin can find the
    /// private dependencies sitting beside it.</param>
    public static PluginRegistry LoadFrom(IEnumerable<string> dirs, IEnumerable<string> files = null,
                                          IEnumerable<string> probeDirs = null)
    {
        var reg = new PluginRegistry();
        var dirList = dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                          .Select(Path.GetFullPath).Distinct().ToList();
        var fileList = (files ?? Enumerable.Empty<string>())
                          .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
                          .Select(Path.GetFullPath).Distinct().ToList();
        if (dirList.Count == 0 && fileList.Count == 0) { Console.WriteLine("[loader] no existing plugin dirs."); return reg; }

        // Gather DLLs from each dir AND its immediate subdirs (LB layout is
        // LB\Plugins\<PluginName>\<plugin>.dll). Register every containing
        // folder as a probe dir so each plugin's private deps resolve.
        var dllFiles = new List<string>(fileList);
        foreach (var dir in dirList)
        {
            try { dllFiles.AddRange(Directory.GetFiles(dir, "*.dll")); } catch { }
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    try { dllFiles.AddRange(Directory.GetFiles(sub, "*.dll")); } catch { }
            }
            catch { }
        }
        dllFiles = dllFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Same FILE NAME twice means the same plugin found in two places — a copy left loose in Plugins        // after being moved into a folder of its own, or the reverse. Loading both registers the plugin
        // twice: two menu entries, two event subscribers, two writers to the same log. First one wins,
        // which given the order above is the folder copy — the deliberate placement over the leftover.
        var bySeenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>();
        foreach (var f in dllFiles.Distinct())
        {
            if (bySeenName.Add(Path.GetFileName(f))) { deduped.Add(f); continue; }
            Console.WriteLine($"[loader] duplicate ignored: {f}");
        }
        dllFiles = deduped;
        // Probe dirs cover the private dependencies of everything DISCOVERED, not just what is loaded.
        AddResolver(dirList.Concat(probeDirs ?? Enumerable.Empty<string>())
                           .Concat(dllFiles.Select(Path.GetDirectoryName))
                           .Where(d => !string.IsNullOrEmpty(d))
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList());

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
                    try { inst = Instantiate(t, dll); }
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

    /// <summary>Creates the plugin instance the way LaunchBox does. ≤13.28: parameterless ctor only.
    /// The v14 SDK adds constructor injection — "a plugin can request these paths by declaring a public
    /// constructor with an IPluginPaths parameter" (SDK xml; the v14 RetroArch integration does exactly
    /// that, and has NO parameterless ctor). The param type is matched BY NAME and the typed code lives
    /// in a separate non-inlined method: this net10 build also runs against LB 13.28, whose SDK has no
    /// IPluginPaths — touching the type here would blow up type-loading on first plugin load there.</summary>
    private static object Instantiate(Type t, string dllPath)
    {
        if (t.GetConstructor(Type.EmptyTypes) != null) return Activator.CreateInstance(t);
#if NET10_0_OR_GREATER   // the net9 target compiles against a 13.x SDK, which has no IPluginPaths
        var ctor = t.GetConstructors().FirstOrDefault(c =>
            c.GetParameters() is { Length: 1 } ps
            && ps[0].ParameterType.FullName == "Unbroken.LaunchBox.Plugins.IPluginPaths");
        if (ctor != null) return InstantiateWithPluginPaths(ctor, dllPath);
#endif
        return Activator.CreateInstance(t);   // no supported ctor → the usual informative throw
    }

#if NET10_0_OR_GREATER
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object InstantiateWithPluginPaths(System.Reflection.ConstructorInfo ctor, string dllPath)
    {
        string installDir = Path.GetDirectoryName(Path.GetFullPath(dllPath))!;
        // DataDirectory — LB's OWN convention, read off its resolver (PluginInstallLocationResolver
        // .GetDataDirectory(pluginId, installLocationKey), invoked on the real v14 core):
        //     <LB>\System\Plugins\.data\<PluginId>    for a plugin installed under System\Plugins
        //     <LB>\Local\Plugins\.data\<PluginId>     for every other location
        // The id is the manifest's PluginId (v14 plugins ship manifest.json). No manifest (a classic
        // pre-14 user plugin) → key by folder name under the same root, so the path stays stable and
        // obvious rather than inventing a GUID.
        string lbRoot = Path.GetFullPath(Path.Combine(ResolveCoreDir(), ".."));
        bool underSystem = installDir.Replace('/', '\\')
            .Contains(@"\System\Plugins\", StringComparison.OrdinalIgnoreCase);
        string dataRoot = underSystem
            ? Path.Combine(lbRoot, "System", "Plugins", ".data")
            : Path.Combine(lbRoot, "Local", "Plugins", ".data");
        string dataDir = Path.Combine(dataRoot, ManifestPluginId(installDir) ?? Path.GetFileName(installDir));
        Console.WriteLine($"[loader] {ctor.DeclaringType!.Name}: IPluginPaths ctor (install={installDir}, data={dataDir})");
        return ctor.Invoke(new object[] { new HostPluginPaths(installDir, dataDir) });
    }

    /// <summary>The plugin's manifest PluginId (v14 manifest.json), or null when there is no manifest.
    /// Parsed with a plain scan — no JSON dependency for one field, and a malformed manifest just
    /// falls back to the folder name.</summary>
    private static string? ManifestPluginId(string installDir)
    {
        try
        {
            var f = Path.Combine(installDir, "manifest.json");
            if (!File.Exists(f)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(f), "\"PluginId\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private sealed class HostPluginPaths : Unbroken.LaunchBox.Plugins.IPluginPaths
    {
        public HostPluginPaths(string install, string data) { InstallDirectory = install; DataDirectory = data; }
        public string InstallDirectory { get; }
        public string DataDirectory { get; }
    }
#endif

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
            string core = ResolveCoreDir();
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
