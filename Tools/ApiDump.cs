// Reflection dump of the LaunchBox plugin SDK (Unbroken.LaunchBox.Plugins).
//
// Run:  dotnet run --project LbApiHost -- --dump-api
//
// Emits the COMPLETE public surface (every interface / class / enum + members)
// to api-surface.txt, plus a console summary that splits interfaces into
// "Plugin" (implemented BY plugins → the host discovers/instantiates) vs
// "Service/Data" (implemented BY the host → DataManager, IGame, IPlatform...).
// This is the spec for the host's dummy implementation.

using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace LbApiHost.Tools;

internal static class ApiDump
{
    // Dev-machine probe dirs so reflection can resolve any SDK dependency.
    private static readonly string[] ProbeDirs =
    {
        @"C:\Users\mehdi\source\repos\scrapper-project\LB\Core",
        @"C:\Users\mehdi\source\repos\scrapper-project\LB\ThirdParty\Chromium",
    };

    public static int Run(string outPath)
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var dir in ProbeDirs)
            {
                var p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) { try { return ctx.LoadFromAssemblyPath(p); } catch { } }
            }
            return null;
        };

        var asm = typeof(Unbroken.LaunchBox.Plugins.PluginHelper).Assembly;

        Type[] types;
        try
        {
            types = asm.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            Console.WriteLine("ReflectionTypeLoadException — partial load. Loader errors (first 15):");
            foreach (var le in ex.LoaderExceptions.Where(e => e != null).Take(15))
                Console.WriteLine("  " + le.Message);
        }

        var sb = new StringBuilder();
        var ifaces  = types.Where(t => t.IsInterface).OrderBy(t => t.FullName).ToList();
        var enums   = types.Where(t => t.IsEnum).OrderBy(t => t.FullName).ToList();
        var classes = types.Where(t => t.IsClass).OrderBy(t => t.FullName).ToList();
        var structs = types.Where(t => t.IsValueType && !t.IsEnum).OrderBy(t => t.FullName).ToList();

        sb.AppendLine($"ASSEMBLY: {asm.GetName().Name} v{asm.GetName().Version}");
        sb.AppendLine($"Exported types: {types.Length} | interfaces: {ifaces.Count} enums: {enums.Count} classes: {classes.Count} structs: {structs.Count}");
        sb.AppendLine();

        foreach (var t in types.OrderBy(t => t.FullName))
            DumpType(sb, t);

        File.WriteAllText(outPath, sb.ToString());

        // ── Console summary ────────────────────────────────────────────
        Console.WriteLine($"Full dump -> {outPath}");
        Console.WriteLine($"Exported types: {types.Length} | interfaces: {ifaces.Count} enums: {enums.Count} classes: {classes.Count} structs: {structs.Count}");

        var pluginIfaces  = ifaces.Where(t => t.Name.EndsWith("Plugin")).ToList();
        var serviceIfaces = ifaces.Where(t => !t.Name.EndsWith("Plugin")).ToList();

        Console.WriteLine();
        Console.WriteLine($"--- PLUGIN interfaces ({pluginIfaces.Count}) — host DISCOVERS/instantiates ---");
        foreach (var t in pluginIfaces) Console.WriteLine("  " + MemberLine(t));

        Console.WriteLine();
        Console.WriteLine($"--- SERVICE / DATA interfaces ({serviceIfaces.Count}) — host IMPLEMENTS ---");
        foreach (var t in serviceIfaces) Console.WriteLine("  " + MemberLine(t));

        Console.WriteLine();
        Console.WriteLine($"--- ENUMS ({enums.Count}) ---");
        foreach (var t in enums) Console.WriteLine("  " + t.FullName);

        Console.WriteLine();
        Console.WriteLine($"--- PUBLIC CLASSES ({classes.Count}) ---");
        foreach (var t in classes) Console.WriteLine("  " + t.FullName + (t.IsAbstract && t.IsSealed ? "  (static)" : t.IsAbstract ? "  (abstract)" : ""));

        return 0;
    }

    private static string MemberLine(Type t)
    {
        int pc = t.GetProperties().Length;
        int mc = t.GetMethods().Count(m => !m.IsSpecialName);
        int ec = t.GetEvents().Length;
        return $"{t.FullName,-58} props={pc,-3} methods={mc,-3} events={ec}";
    }

    private static void DumpType(StringBuilder sb, Type t)
    {
        string kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : "class";
        sb.AppendLine($"=== [{kind}] {t.FullName} ===");

        if (t.IsEnum)
        {
            sb.AppendLine("    values: " + string.Join(", ", Enum.GetNames(t)));
            sb.AppendLine();
            return;
        }

        foreach (var p in t.GetProperties().OrderBy(p => p.Name))
        {
            string acc = string.Join("/", new[] { p.CanRead ? "get" : null, p.CanWrite ? "set" : null }.Where(x => x != null));
            sb.AppendLine($"    PROP  {Short(p.PropertyType)} {p.Name} {{{acc}}}");
        }
        foreach (var m in t.GetMethods().Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
        {
            string ps = string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
            sb.AppendLine($"    METH  {Short(m.ReturnType)} {m.Name}({ps})");
        }
        foreach (var e in t.GetEvents())
            sb.AppendLine($"    EVT   {Short(e.EventHandlerType)} {e.Name}");
        sb.AppendLine();
    }

    private static string Short(Type t)
    {
        if (t == null) return "void";
        if (t == typeof(void)) return "void";
        if (t.IsGenericType)
        {
            string baseName = t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name;
            string args = string.Join(", ", t.GetGenericArguments().Select(Short));
            return $"{baseName}<{args}>";
        }
        return t.Name;
    }
}
