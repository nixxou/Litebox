// Dev tool: dump the CONSTRUCTORS (visibility + signature) of the EmulatorPlugin
// arg/response classes — api-surface.txt lists members but not ctors, and the host
// must know whether it can NEW these (public ctor) or has to reflect (internal).
//
// Run:  dotnet run --project LiteBox.csproj -- --dump-ctors

using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace LbApiHost.Tools;

internal static class CtorDump
{
    private static readonly string[] ProbeDirs =
    {
        @"C:\Users\mehdi\source\repos\scrapper-project\LB\Core",
    };

    public static int Run()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var dir in ProbeDirs)
            {
                var p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            }
            return null;
        };

        var sdk = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(ProbeDirs[0], "Unbroken.LaunchBox.Plugins.dll"));

        string[] names =
        {
            "Unbroken.LaunchBox.Plugins.EmulatorPlugin",
            "Unbroken.LaunchBox.Plugins.PrepareForLaunchArgs",
            "Unbroken.LaunchBox.Plugins.PrepareForLaunchResponse",
            "Unbroken.LaunchBox.Plugins.InstallEmulatorArgs",
            "Unbroken.LaunchBox.Plugins.EmulatorInstallResponse",
            "Unbroken.LaunchBox.Plugins.ImportActionArgs",
            "Unbroken.LaunchBox.Plugins.InjectRetroAchievementsCredentialsArgs",
            "Unbroken.LaunchBox.Plugins.RetroAchievementCredentials",
            "Unbroken.LaunchBox.Plugins.EmulatorControllerVersion",
            "Unbroken.LaunchBox.Plugins.EmulatorBiosFile",
            "Unbroken.LaunchBox.Plugins.PluginResponse",
            "Unbroken.LaunchBox.Plugins.EmulatorSupportResponse",
            "Unbroken.LaunchBox.Plugins.RetroAchievementSupportResponse",
        };
        foreach (var n in names)
        {
            var t = sdk.GetType(n);
            if (t == null) { Console.WriteLine($"{n} : NOT FOUND"); continue; }
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ctors.Length == 0) Console.WriteLine($"{t.Name} : (no instance ctors)");
            foreach (var c in ctors)
            {
                var vis = c.IsPublic ? "public" : c.IsAssembly ? "internal" : c.IsFamily ? "protected" : "private";
                var ps = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"{t.Name} : {vis} ctor({ps})");
            }
            // Settable props matter too (get-only args must be ctor-fed).
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanWrite && p.SetMethod is { IsPublic: true })
                    Console.WriteLine($"{t.Name} :   settable {p.PropertyType.Name} {p.Name}");
        }
        return 0;
    }
}
