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
    /// <summary>--model-export &lt;outDir&gt;: extract every embedded .obj/.mtl case model (and model textures)
    /// from Unbroken.LaunchBox.Windows.dll to disk — inputs for the home-made case reproduction. Resources
    /// only, no type reflection (Dump's GetTypes crashes on missing deps in 13.27).</summary>
    public static void Export(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var asmFile in new[] { "Unbroken.LaunchBox.Windows.dll", "BigBox.dll", "LaunchBox.dll", "Unbroken.LaunchBox.dll" })
        {
            Console.WriteLine("[export] ==== assembly " + asmFile);
            try { ExportFrom(Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, asmFile)), outDir); }
            catch (Exception ex) { Console.WriteLine("[export] load failed: " + ex.Message); }
        }
    }

    private static void ExportFrom(Assembly win, string outDir)
    {
        var wanted = new Regex(@"\.(obj|mtl|dds|tga)$", RegexOptions.IgnoreCase);
        int n = 0;
        foreach (var r in win.GetManifestResourceNames())
        {
            Console.WriteLine("[export] manifest: " + r);
            try
            {
                if (wanted.IsMatch(r))
                {
                    using var s = win.GetManifestResourceStream(r);
                    if (s == null) continue;
                    string dst = Path.Combine(outDir, r);
                    using var f = File.Create(dst);
                    s.CopyTo(f);
                    Console.WriteLine($"[export]   -> {r}  ({f.Length:N0} bytes)");
                    n++;
                }
                else if (r.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    // .resources bundle → walk entries via DeserializingResourceReader (LB's bundles use the
                    // newer System.Resources.Extensions format — the classic ResourceReader refuses them).
                    using var s = win.GetManifestResourceStream(r);
                    if (s == null) continue;
                    var ext = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "System.Resources.Extensions")
                              ?? Assembly.Load("System.Resources.Extensions");
                    var rt = ext.GetType("System.Resources.Extensions.DeserializingResourceReader")!;
                    var reader = Activator.CreateInstance(rt, s);
                    var en = (System.Collections.IDictionaryEnumerator)rt.GetMethod("GetEnumerator", Type.EmptyTypes)!.Invoke(reader, null)!;
                    bool spineBundle = r.IndexOf("JewelCaseSpines", StringComparison.OrdinalIgnoreCase) >= 0;
                    while (en.MoveNext())
                    {
                        string key = en.Key?.ToString() ?? "";
                        bool want = spineBundle
                                    || wanted.IsMatch(key)
                                    || key.IndexOf("case", StringComparison.OrdinalIgnoreCase) >= 0
                                    || key.IndexOf("cart", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!want) { Console.WriteLine($"[export]   entry: {key}"); continue; }
                        object? v;
                        try { v = en.Value; }   // deserializes — only for wanted keys
                        catch (Exception ex) { Console.WriteLine($"[export]   entry: {key}  VALUE FAILED: {ex.Message}"); continue; }
                        string vt = v?.GetType().FullName ?? "null";
                        Console.WriteLine($"[export]   entry: {key}  ({vt})");
                        string dst = Path.Combine(outDir, key.Replace('/', '_').Replace('\\', '_'));
                        if (v is byte[] bytes) File.WriteAllBytes(dst, bytes);
                        else if (v is string str) File.WriteAllText(dst, str);
                        else if (v is Stream stream) { using var f = File.Create(dst); stream.CopyTo(f); }
                        else if (v is System.Drawing.Bitmap bmp) { dst += ".png"; bmp.Save(dst, System.Drawing.Imaging.ImageFormat.Png); }
                        else { Console.WriteLine($"[export]   (unhandled value type {vt})"); continue; }
                        Console.WriteLine($"[export]   -> {dst}");
                        n++;
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[export] {r} FAILED: {ex.Message}"); }
        }
        Console.WriteLine($"[export] {n} files -> {outDir}");
    }

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

        // Bare-constructor defaults — what LB renders when a platform has NO hardcoded defaults and no override.
        try
        {
            var ctor = Activator.CreateInstance(t);
            L("");
            L("[new ModelSettings() — ctor defaults]");
            foreach (var p in props)
            {
                object? v; try { v = p.GetValue(ctor); } catch { continue; }
                string sv = v == null ? "null"
                    : v is System.Collections.IEnumerable && v is not string
                        ? string.Join(",", ((System.Collections.IEnumerable)v).Cast<object>().Select(x => x?.ToString()))
                        : v.ToString() ?? "";
                L($"    {p.Name} = {sv}");
            }
        }
        catch (Exception ex) { L("ctor dump failed: " + ex.Message); }

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

    /// <summary>--model-defaults-extract [out.json]: enumerate EVERY platform name (+ alternate names) from
    /// LB's Metadata db and drive ModelSettings.GetDefaultSettings for each, serializing the non-null results
    /// to a JSON table — the frozen, core-independent source ModelDefaults consults at runtime (the goal is a
    /// LiteBox with NO LaunchBox-core dependency for the 3D preset chain).</summary>
    public static void DefaultsExtract(string lbRoot, string? outPath)
    {
        outPath ??= Path.Combine(AppContext.BaseDirectory, "model-defaults.json");
        var names = new List<string>();
        try
        {
            var dbPath = Path.Combine(lbRoot, "Metadata", "LaunchBox.Metadata.db");
            using var con = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
            con.Open();
            foreach (var sql in new[] { "SELECT Name FROM Platforms", "SELECT Alternate FROM PlatformAlternateNames" })
                try
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = sql;
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) if (!r.IsDBNull(0)) names.Add(r.GetString(0));
                }
                catch (Exception ex) { Console.WriteLine("[model-extract] " + sql + " → " + ex.Message); }
        }
        catch (Exception ex) { Console.WriteLine("[model-extract] metadata db: " + ex.Message); return; }
        Console.WriteLine($"[model-extract] {names.Count} platform names");

        var table = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        int hits = 0;
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var map = ModelProbeDefaultsBridge(n);
            if (map == null) continue;
            table[n] = map;
            hits++;
        }
        Console.WriteLine($"[model-extract] {hits} platforms with hardcoded defaults");

        var sb = new StringBuilder();
        sb.AppendLine("{");
        bool firstP = true;
        foreach (var kv in table)
        {
            if (!firstP) sb.AppendLine(",");
            firstP = false;
            sb.Append("  \"").Append(J(kv.Key)).Append("\": { ");
            sb.Append(string.Join(", ", kv.Value.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => "\"" + J(f.Key) + "\": \"" + J(f.Value) + "\"")));
            sb.Append(" }");
        }
        sb.AppendLine();
        sb.AppendLine("}");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine("[model-extract] wrote " + outPath);
    }

    private static string J(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Resolve through the SAME conversion ModelDefaults uses (reflection call, colors → signed ARGB).
    private static Dictionary<string, string>? ModelProbeDefaultsBridge(string name)
        => Platforms.ModelDefaults.ResolveViaCore(name, name);

    /// <summary>--hunt-regions: scan every static field of the core assemblies for the hard-coded prioritized-
    /// region list ("World, North America, …") — locates the (obfuscated) static the 3D preview's art resolution
    /// actually uses, so CoreModelHost can overwrite it with the user's RegionPriorities.</summary>
    public static void HuntRegions()
    {
        foreach (var asmName in new[] { "Unbroken.LaunchBox.Windows.dll", "Unbroken.LaunchBox.dll", "Unbroken.LaunchBox.LocalDb.dll" })
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, asmName)); } catch { continue; }
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); } catch { continue; }
                foreach (var f in fields)
                {
                    if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(string)) continue;
                    object? v;
                    try { v = f.GetValue(null); } catch { continue; }
                    if (v is not System.Collections.IEnumerable en) continue;
                    List<string> items = new();
                    try { foreach (var o in en) { if (o is string s) items.Add(s); if (items.Count > 4) break; } } catch { continue; }
                    if (items.Count >= 3 && items[0] == "World" && items[1] == "North America" && items[2] == "United States")
                        Console.WriteLine($"[hunt] {asmName} :: {t.FullName}.{f.Name} ({f.FieldType.Name}) = [{string.Join(", ", items)}...]");
                }
            }
        }
        Console.WriteLine("[hunt] done");
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

    // --model-map: full member dump of the CoverFlow namespace types + every method ANYWHERE whose signature
    // touches ModelSettings / Bitmap→Model3D — hunting the procedural case builder (jewel/dvd/box) that the
    // name-filtered --model-dump misses (obfuscated names). → <Core>\model-map.log
    public static void MapCoverFlow(string lbRoot)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[model-map] " + s); sb.AppendLine(s); }
        L("=== CoverFlow / model-builder map ===");
        try
        {
            var win = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll"));
            Type[] types;
            try { types = win.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            // 1) FULL member dump of every CoverFlow-namespace type (fields incl. — texture/mesh caches live there).
            foreach (var t in types.Where(t => t?.FullName != null && t.FullName.Contains(".CoverFlow", StringComparison.Ordinal)).OrderBy(t => t!.FullName))
            {
                L("");
                L($"--- {t!.FullName} : {t.BaseType?.Name} ---");
                const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (var f in t.GetFields(BF).OrderBy(f => f.Name))
                    L($"    fld  {f.FieldType.Name} {f.Name}");
                foreach (var p in t.GetProperties(BF).OrderBy(p => p.Name))
                    L($"    prop {p.PropertyType.Name} {p.Name}");
                foreach (var m in t.GetMethods(BF).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
                    L($"    {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                foreach (var c in t.GetConstructors(BF))
                    L($"    ctor ({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }

            // 2) ANY method in the assembly whose signature involves ModelSettings, or mixes Bitmap/ImageSource
            //    with a 3D return/parameter type — the builder must show up here whatever its name.
            L("");
            L("--- methods touching ModelSettings / images→3D (whole assembly) ---");
            static bool Is3D(Type t2) => t2.Name.Contains("Model3D") || t2.Name.Contains("Visual3D") || t2.Name.Contains("MeshGeometry3D") || t2.Name.Contains("Geometry3D");
            static bool IsImg(Type t2) => t2.Name is "Bitmap" or "BitmapSource" or "ImageSource" or "BitmapImage" or "Image";
            foreach (var t in types.Where(t => t?.FullName != null && !t.FullName.StartsWith("HelixToolkit", StringComparison.Ordinal)))
            {
                MethodInfo[] ms;
                try { ms = t!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms.Where(m => !m.IsSpecialName))
                {
                    var ps = m.GetParameters();
                    bool touchesSettings = m.ReturnType.Name == "ModelSettings" || ps.Any(p => p.ParameterType.Name == "ModelSettings");
                    bool imgTo3D = (Is3D(m.ReturnType) && ps.Any(p => IsImg(p.ParameterType)))
                                || (Is3D(m.ReturnType) && ps.Any(p => p.ParameterType.Name == "String") && ps.Length <= 3);
                    if (!touchesSettings && !imgTo3D) continue;
                    L($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {t!.FullName}.{m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))})");
                }
            }
        }
        catch (Exception ex) { L("failed: " + ex.Message); }
        SaveAs(sb, "model-map.log");
    }

    // --type-dump <FullTypeName>: ctors + every property (with get/SET markers) + non-accessor methods of one
    // core type. For planning the FlowModel/Game/ModelSettings reflection calls. → <Core>\type-dump.log
    public static void TypeDump(string lbRoot, string typeName)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[type-dump] " + s); sb.AppendLine(s); }
        try
        {
            // Search BOTH core assemblies (Windows + base Unbroken.LaunchBox) + everything already loaded.
            var asms = new List<Assembly>();
            foreach (var dll in new[] { "Unbroken.LaunchBox.Windows.dll", "Unbroken.LaunchBox.dll", "Unbroken.LaunchBox.LocalDb.dll" })
                try { asms.Add(Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, dll))); } catch { }
            asms.AddRange(AppDomain.CurrentDomain.GetAssemblies());

            Type? t = null;
            foreach (var a in asms) { t = a.GetType(typeName); if (t != null) break; }
            if (t == null)
                foreach (var a in asms)
                {
                    Type[] all; try { all = a.GetTypes(); } catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(x => x != null).ToArray()!; } catch { continue; }
                    t = all.FirstOrDefault(x => x?.Name?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true
                                             || x?.FullName?.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase) == true);
                    if (t != null) break;
                }
            if (t == null) { L("type not found: " + typeName); SaveAs(sb, "type-dump.log"); return; }
            L("(assembly: " + t.Assembly.GetName().Name + ")");
            L($"=== {t.FullName} : {t.BaseType?.FullName} ===");
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            L("-- constructors --");
            foreach (var c in t.GetConstructors(BF))
                L($"  {(c.IsPublic ? "pub " : "priv")} ctor ({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            L("-- properties --");
            foreach (var p in t.GetProperties(BF).OrderBy(p => p.Name))
                L($"  {p.PropertyType.Name} {p.Name}  {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "SET; " : "")}}}");
            L("-- methods --");
            foreach (var m in t.GetMethods(BF).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
                L($"  {(m.IsStatic ? "static " : "")}{(m.IsPublic ? "pub " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
        catch (Exception ex) { L("failed: " + ex.Message); }
        SaveAs(sb, "type-dump.log");
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
