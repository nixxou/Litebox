// Passive RE probe for LaunchBox UI internals: finds types by name fragment and prints their members,
// plus the numeric/string literals each method's IL carries. Metadata only — nothing is executed, so
// there is no warm-up and no anti-tamper risk (same rule as ModelProbe).
//
// Wired to --lb-ui-probe <fragment> [<fragment> ...]. Dev-only; not shipped behaviour.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LbApiHost.Tools;

internal static class LbUiProbe
{
    public static int Run(string[] args, int idx)
    {
        var frags = args.Skip(idx + 1).Where(a => !a.StartsWith("-")).ToArray();
        if (frags.Length == 0) { Console.WriteLine("usage: --lb-ui-probe <name fragment> [...]"); return 1; }

        string core = Host.PluginLoader.ResolveCoreDir() ?? AppContext.BaseDirectory;
        Console.WriteLine("[ui-probe] core: " + core);
        foreach (var file in new[] { "LaunchBox.dll", "Unbroken.LaunchBox.Windows.dll", "Unbroken.LaunchBox.dll" })
        {
            string path = Path.Combine(core, file);
            if (!File.Exists(path)) { Console.WriteLine("[ui-probe] missing " + file); continue; }
            Assembly asm;
            try { asm = Assembly.LoadFrom(path); }
            catch (Exception ex) { Console.WriteLine($"[ui-probe] {file}: load failed — {ex.Message}"); continue; }
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch (Exception ex) { Console.WriteLine($"[ui-probe] {file}: GetTypes failed — {ex.Message}"); continue; }

            foreach (var t in types)
            {
                if (t == null) continue;
                string name = t.FullName ?? t.Name;
                if (!frags.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                Console.WriteLine($"\n[ui-probe] ==== {file} :: {name}  (base {t.BaseType?.Name})");
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var fi in t.GetFields(All))
                {
                    string val = "";
                    if (fi.IsLiteral) { try { val = " = " + fi.GetRawConstantValue(); } catch { } }
                    Console.WriteLine($"    field  {fi.FieldType.Name} {fi.Name}{val}");
                }
                foreach (var pi in t.GetProperties(All))
                    Console.WriteLine($"    prop   {pi.PropertyType.Name} {pi.Name}");
                foreach (var mi in t.GetMethods(All))
                {
                    var ps = string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                    Console.WriteLine($"    method {mi.ReturnType.Name} {mi.Name}({ps})");
                    DumpIlLiterals(mi, asm);
                }
            }
        }
        Console.WriteLine("[ui-probe] done");
        return 0;
    }

    /// <summary>Numeric and string literals a method's IL loads — enough to read thresholds and formats
    /// out of a compiled body without decompiling it. Only the opcodes that carry inline operands are
    /// decoded; anything else is skipped by its fixed operand size.</summary>
    private static void DumpIlLiterals(MethodBase mi, Assembly asm)
    {
        byte[] il;
        try { il = mi.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>(); }
        catch (Exception ex) { Console.WriteLine("        (no IL: " + ex.GetType().Name + " " + ex.Message + ")"); return; }
        if (il.Length == 0) { Console.WriteLine("        (IL empty)"); return; }
        var nums = new List<string>();
        var strs = new List<string>();
        for (int i = 0; i < il.Length;)
        {
            byte op = il[i++];
            switch (op)
            {
                case 0x20 when i + 4 <= il.Length:                      // ldc.i4
                    nums.Add(BitConverter.ToInt32(il, i).ToString()); i += 4; break;
                case 0x1F when i < il.Length:                           // ldc.i4.s
                    nums.Add(((sbyte)il[i]).ToString()); i += 1; break;
                case 0x22 when i + 4 <= il.Length:                      // ldc.r4
                    nums.Add(BitConverter.ToSingle(il, i).ToString("0.###")); i += 4; break;
                case 0x23 when i + 8 <= il.Length:                      // ldc.r8
                    nums.Add(BitConverter.ToDouble(il, i).ToString("0.###")); i += 8; break;
                case 0x72 when i + 4 <= il.Length:                      // ldstr
                    try { strs.Add(asm.ManifestModule.ResolveString(BitConverter.ToInt32(il, i))); } catch { }
                    i += 4; break;
                case 0x28: case 0x6F: case 0x73: case 0x7B: case 0x7D:  // call/callvirt/newobj/ldfld/stfld
                case 0x74: case 0x8C: case 0xA5: case 0x71: case 0x7E: case 0x80:
                    i += 4; break;
                case 0xFE: i += 1; break;                               // two-byte opcodes: skip prefix
                default: break;
            }
        }
        if (nums.Count > 0) Console.WriteLine("        nums: " + string.Join(" ", nums.Take(40)));
        if (strs.Count > 0) Console.WriteLine("        strs: " + string.Join(" | ", strs.Take(20).Select(s => "\"" + s + "\"")));
    }
}
