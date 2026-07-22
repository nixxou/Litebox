// Passive RE probe for LaunchBox's 3D game-box model system (branch 3d-model). Reads ONLY metadata from the
// core assembly (manifest resources + reflected types/members) — it never executes obfuscated bodies, so no
// warm-up and no anti-tamper risk. Output → <Core>\model-dump.log.
//
// Answers: which .obj/.mtl case models + textures are embedded; the CaseType enum; ModelSettings' members; and
// any GetModel / platform→case mapping surface.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LbApiHost.Host.Diag;

internal static class ModelProbe
{
    public static void Dump(string lbRoot)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[model] " + s); sb.AppendLine(s); }
        L("=== LaunchBox 3D box-model probe (metadata only) ===");

        Assembly win;
        try { win = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll")); }
        catch (Exception ex) { L("load Windows.dll failed: " + ex.Message); Save(sb); return; }

        // 1) embedded resources — every name, sizes for the model/material/texture ones.
        L("");
        L("--- embedded manifest resources ---");
        string[] res;
        try { res = win.GetManifestResourceNames(); }
        catch (Exception ex) { res = Array.Empty<string>(); L("GetManifestResourceNames failed: " + ex.Message); }
        L($"({res.Length} resources total)");
        var modelRe = new Regex(@"\.(obj|mtl|png|jpg|jpeg|dds|tga|bmp)$", RegexOptions.IgnoreCase);
        foreach (var r in res.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            bool modelish = modelRe.IsMatch(r)
                || r.IndexOf("Case", StringComparison.OrdinalIgnoreCase) >= 0
                || r.IndexOf("Model", StringComparison.OrdinalIgnoreCase) >= 0
                || r.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0
                || r.IndexOf("Cart", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!modelish) continue;
            long len = -1; try { using var s = win.GetManifestResourceStream(r); len = s?.Length ?? -1; } catch { }
            L($"  {r}   [{len:N0} bytes]");
        }

        // 2) reflected types.
        Type[] types;
        try { types = win.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        catch (Exception ex) { L("GetTypes failed: " + ex.Message); Save(sb); return; }

        // 2a) enums whose members name case types (CaseType, spine-image modes, …).
        L("");
        L("--- enums (case types / spine modes) ---");
        foreach (var t in types.Where(t => t is { IsEnum: true }).OrderBy(t => t!.FullName))
        {
            string[] names; try { names = Enum.GetNames(t!); } catch { continue; }
            bool relevant = names.Any(n =>
                n.IndexOf("JewelCase", StringComparison.OrdinalIgnoreCase) >= 0
                || n.Equals("DvdCase", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Cartridge", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0
                || (t!.Name.IndexOf("Case", StringComparison.OrdinalIgnoreCase) >= 0));
            if (relevant) L($"  enum {t!.FullName} = {{ {string.Join(", ", names)} }}");
        }

        // 2b) ModelSettings members (properties + non-accessor methods).
        L("");
        foreach (var t in types.Where(t => t != null && (t.Name == "ModelSettings" || t.Name.EndsWith("ModelSettings"))))
            DumpTypeMembers(t!, L);

        // 2c) any type named *Model*/*Case*/*Box3D* (the model classes + builders), and any method that looks
        // like a platform→model resolver (GetModel/BuildModel/…/returns or takes ModelSettings).
        L("");
        L("--- model/case types ---");
        foreach (var t in types.Where(t => t != null).OrderBy(t => t!.FullName))
        {
            var n = t!.Name;
            if (n.IndexOf("Model3D", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("BoxModel", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("CaseModel", StringComparison.OrdinalIgnoreCase) >= 0
                || n.EndsWith("Case", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("ModelBuilder", StringComparison.OrdinalIgnoreCase) >= 0)
                L($"  {(t.IsEnum ? "enum " : t.IsInterface ? "iface " : "")}{t.FullName}");
        }

        L("");
        L("--- methods hinting at platform→model resolution (GetModel/BuildModel/…) ---");
        foreach (var t in types.Where(t => t != null))
        {
            MethodInfo[] ms;
            try { ms = t!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var m in ms)
            {
                var nm = m.Name;
                bool hit = (nm.IndexOf("Model", StringComparison.OrdinalIgnoreCase) >= 0
                            && (nm.StartsWith("Get") || nm.StartsWith("Build") || nm.StartsWith("Create") || nm.StartsWith("Load") || nm.StartsWith("Make")))
                        || m.ReturnType.Name.IndexOf("ModelSettings", StringComparison.OrdinalIgnoreCase) >= 0
                        || m.ReturnType.Name.IndexOf("Model3D", StringComparison.OrdinalIgnoreCase) >= 0;
                if (t!.FullName?.IndexOf(".Properties.", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!hit) continue;
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                L($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {t.FullName}.{nm}({ps})");
            }
        }

        Save(sb);
    }

    // Drive ModelSettings.GetDefaultSettings(platform, scrapeAs) for a set of platforms and dump the hardcoded
    // per-platform defaults (ModelType/case, ModelSize, colours, spine/logo handling). Executes a clean core
    // method by reflection (same proven pattern as the MAME probes). Output → <Core>\model-defaults.log.
    public static void Defaults(string lbRoot, string? platformArg)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[model-def] " + s); sb.AppendLine(s); }
        L("=== ModelSettings.GetDefaultSettings per platform (hardcoded defaults) ===");

        Assembly win;
        try { win = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll")); }
        catch (Exception ex) { L("load failed: " + ex.Message); SaveAs(sb, "model-defaults.log"); return; }

        var t = win.GetType("Unbroken.LaunchBox.Windows.Data.ModelSettings");
        var m = t?.GetMethod("GetDefaultSettings", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
        if (m == null) { L("GetDefaultSettings(string,string) not found."); SaveAs(sb, "model-defaults.log"); return; }

        string[] platforms = string.IsNullOrWhiteSpace(platformArg)
            ? new[]
            {
                "Sony Playstation", "Sony Playstation 2", "Sony Playstation 3", "Sony PSP",
                "Nintendo Entertainment System", "Super Nintendo Entertainment System", "Nintendo 64",
                "Nintendo GameCube", "Nintendo Wii", "Nintendo Game Boy", "Nintendo DS", "Nintendo Switch",
                "Microsoft Xbox", "Microsoft Xbox 360", "Sega Genesis", "Sega Saturn", "Sega Dreamcast",
                "Arcade", "MS-DOS", "3DO Interactive Multiplayer",
            }
            : new[] { platformArg! };

        var props = t!.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name).ToArray();
        foreach (var plat in platforms)
        {
            object? res;
            try { res = m.Invoke(null, new object?[] { plat, plat }); }
            catch (Exception ex) { L($"[{plat}] THREW: " + (ex.InnerException?.Message ?? ex.Message)); continue; }
            if (res == null) { L($"[{plat}] → null"); continue; }
            L("");
            L($"[{plat}]");
            foreach (var p in props)
            {
                object? v; try { v = p.GetValue(res); } catch { continue; }
                if (v == null) continue;
                string sv = v is System.Collections.IEnumerable && v is not string
                    ? string.Join(",", ((System.Collections.IEnumerable)v).Cast<object>().Select(x => x?.ToString()))
                    : v.ToString() ?? "";
                if (sv.Length == 0) continue;
                L($"    {p.Name} = {sv}");
            }
        }
        SaveAs(sb, "model-defaults.log");
    }

    private static void SaveAs(StringBuilder sb, string file)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, file), sb.ToString()); } catch { }
    }

    // Dump the entry names inside the embedded JewelCaseSpines.resources bundle — these are the Spine Style
    // dropdown presets (+ regional variants) LaunchBox offers for jewel cases. → <Core>\jewel-spines.log
    public static void JewelSpines(string lbRoot)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[spines] " + s); sb.AppendLine(s); }
        L("=== JewelCaseSpines.resources entries ===");
        try
        {
            var win = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll"));
            var resName = win.GetManifestResourceNames().FirstOrDefault(n => n.IndexOf("JewelCaseSpines", StringComparison.OrdinalIgnoreCase) >= 0);
            if (resName == null) { L("resource not found"); SaveAs(sb, "jewel-spines.log"); return; }
            L("resource = " + resName);
            using var s0 = win.GetManifestResourceStream(resName);
            try { using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, "jewel-spines.bin")); s0!.CopyTo(fs); L("raw bytes → jewel-spines.bin (parse the name table)"); } catch { }
            using var s = win.GetManifestResourceStream(resName);
            // The bundle uses System.Resources.Extensions.DeserializingResourceReader (newer format). Its
            // enumerator advances the NAME table without deserializing the image values, so reading only .Key is safe.
            var ext = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "System.Resources.Extensions")
                      ?? Assembly.Load("System.Resources.Extensions");
            var rt = ext.GetType("System.Resources.Extensions.DeserializingResourceReader");
            var reader = Activator.CreateInstance(rt!, s);
            var en = (System.Collections.IDictionaryEnumerator)rt!.GetMethod("GetEnumerator", Type.EmptyTypes)!.Invoke(reader, null)!;
            var names = new List<string>();
            while (en.MoveNext()) { try { names.Add(en.Key?.ToString() ?? ""); } catch { } }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            L($"({names.Count} entries)");
            foreach (var n in names) L("  " + n);
        }
        catch (Exception ex) { L("failed: " + ex.Message); }
        SaveAs(sb, "jewel-spines.log");
    }

    private static void DumpTypeMembers(Type t, Action<string> L)
    {
        L($"--- {t.FullName} members ---");
        try
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(p => p.Name))
                L($"    {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                .Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                L($"    {m.ReturnType.Name} {m.Name}({ps})");
            }
        }
        catch (Exception ex) { L("    (member dump failed: " + ex.Message + ")"); }
    }

    private static void Save(StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model-dump.log"), sb.ToString()); } catch { }
    }
}
