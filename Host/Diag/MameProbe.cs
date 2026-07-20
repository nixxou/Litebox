// Passive feasibility probe for the "drive LaunchBox's obfuscated MAME high-score code directly" idea
// (instead of reproducing the encrypted LBGDB protocol). Same principle as the emulator-plugin shim
// (EmuInstall): resolve the obfuscated core type by assembly-qualified name via reflection and call it.
//
// It calls ONLY the local, no-network methods — CheckIfSupported(string) and ParseDefaultScores(string).
// It NEVER contacts gamesdb.launchbox-app.com. Nothing is uploaded, no dummy data leaves the machine.
//
// The decompiled bodies are DECOY stubs (CheckIfSupported => true); the real bodies are runtime-decrypted
// by LB's obfuscator engine. So we discriminate REAL execution from the stub by testing a SUPPORTED rom
// (1942, in the hi2txt db) against a BOGUS one: if both report "supported", the real body did NOT run.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LbApiHost.Host.Diag;

internal static class MameProbe
{
    // ── Leaderboard-key scan (no Harmony) ───────────────────────────────
    // The upload blob key is a fixed value (per LB version). If LB holds it in a STATIC FIELD, accessing that
    // field triggers the (obfuscated) static ctor, which decrypts the string into the field — so a plain
    // reflection read yields the cleartext key. We scan every static string/byte[]/char[] field in the
    // Unbroken.LaunchBox assembly (where GamesDatabase/Rijndael live) and print 32-hex-char candidates (the
    // key/IV shape). If nothing shows, the key is an inline local, not a field → fall back to a runtime hook.
    public static void KeyScan(string lbRoot, string? knownKey)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[keyscan] " + s); sb.AppendLine(s); }

        L("=== leaderboard-key static-field scan (no Harmony) ===");
        string coreDir = AppContext.BaseDirectory;
        var hits = new List<string>();
        var methodHits = new List<string>();
        int scanned = 0;

        foreach (var dll in new[] { "Unbroken.LaunchBox.dll", "Unbroken.LaunchBox.Windows.dll" })
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(Path.Combine(coreDir, dll)); }
            catch (Exception ex) { L($"load {dll} failed: {ex.Message}"); continue; }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            catch (Exception ex) { L($"{dll}: GetTypes failed: {ex.Message}"); continue; }
            L($"{dll}: {types.Length} types");

            foreach (var t in types)
            {
                if (t == null) continue;
                FieldInfo[] fs;
                try { fs = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { continue; }
                foreach (var f in fs)
                {
                    if (f.FieldType != typeof(string) && f.FieldType != typeof(byte[]) && f.FieldType != typeof(char[])) continue;
                    object? v;
                    try { v = f.GetValue(null); }   // triggers the (decrypting) cctor for this type
                    catch { continue; }
                    scanned++;
                    string? s = v as string;
                    if (v is byte[] b) { try { s = Encoding.ASCII.GetString(b); } catch { } }
                    else if (v is char[] c) s = new string(c);
                    if (string.IsNullOrEmpty(s)) continue;
                    s = s!.Trim();
                    bool known = knownKey != null && knownKey.Length > 0 && s.IndexOf(knownKey, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (known || (s.Length == 32 && IsHex(s)))
                        hits.Add($"{(known ? "★KNOWN " : "")}{t.FullName}.{f.Name} = \"{s}\"");
                }

                // Discover the callable encrypt trigger: any method whose name hints at the MAME upload/leaderboard.
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms)
                {
                    var nm = m.Name; var tn = t.Name; var tf = t.FullName ?? "";
                    // skip localization resource strings (Properties.Strings.get_Label…)
                    if (tf.IndexOf(".Properties.", StringComparison.OrdinalIgnoreCase) >= 0 || tn == "Strings" || tn == "Resources") continue;
                    bool mame = nm.IndexOf("UploadMame", StringComparison.OrdinalIgnoreCase) >= 0
                             || nm.IndexOf("MameHighScore", StringComparison.OrdinalIgnoreCase) >= 0
                             || nm.IndexOf("MameLeaderboard", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool crypto = tn.IndexOf("Rijndael", StringComparison.OrdinalIgnoreCase) >= 0 && (nm == "Encrypt" || nm == "Decrypt");
                    // settings-decrypt TRIGGER hunt: any method of an EmuMovies-named type, and Save/Login on
                    // Settings/EmuMovies types (a Save re-encrypts the password → fires Rijndael.Encrypt).
                    bool emu = tn.IndexOf("EmuMovies", StringComparison.OrdinalIgnoreCase) >= 0
                            || nm.IndexOf("EmuMovies", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool trig = (nm == "Save" || nm == "Login" || nm == "Authenticate" || nm == "Connect")
                             && (tn.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("EmuMovies", StringComparison.OrdinalIgnoreCase) >= 0);
                    // BigBox parental LockPin trigger hunt: any member naming LockPin/Pin/Parental (getter that
                    // decrypts, or setter/save that re-encrypts → fires Rijndael and reveals the LockPin key).
                    bool pin = nm.IndexOf("LockPin", StringComparison.OrdinalIgnoreCase) >= 0
                            || nm.IndexOf("Parental", StringComparison.OrdinalIgnoreCase) >= 0
                            || nm.IndexOf("Pin", StringComparison.OrdinalIgnoreCase) >= 0
                            || tn.IndexOf("Parental", StringComparison.OrdinalIgnoreCase) >= 0
                            || tn.IndexOf("LockPin", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!mame && !crypto && !emu && !trig && !pin) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                    methodHits.Add($"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {t.FullName}.{nm}({ps})");
                }
            }
        }

        L($"static fields read: {scanned}   32-hex candidates: {hits.Count}");
        foreach (var h in hits) L("  " + h);
        if (hits.Count == 0) L("→ no static-field key. It's an inline local; a runtime cipher hook is needed.");
        L($"--- candidate trigger methods (mame / crypto / emumovies / lockpin): {methodHits.Count} ---");
        foreach (var h in methodHits.Distinct()) L("  " + h);
        DumpTo(sb, "mame-keyscan.log");
    }

    // Drive the core's leaderboard DOWNLOAD directly (read-only, public GET) to prove LiteBox can call the
    // GamesDatabase.* methods on this LB version — the same mechanism we'd use for UploadMameHighScore.
    public static void DriveTest(string lbRoot, string? rom)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[drivetest] " + s); sb.AppendLine(s); }
        L("=== drive core GamesDatabase.DownloadMameGameLeaderboard (read-only) ===");

        Assembly asm;
        try { asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll")); }
        catch (Exception ex) { L("load Unbroken.LaunchBox failed: " + ex.Message); DumpTo(sb, "mame-drivetest.log"); return; }

        Type? gdb = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase");
        if (gdb == null) { L("GamesDatabase type not found"); DumpTo(sb, "mame-drivetest.log"); return; }
        var m = gdb.GetMethod("DownloadMameGameLeaderboard", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (m == null) { L("DownloadMameGameLeaderboard(string) not found"); DumpTo(sb, "mame-drivetest.log"); return; }

        string r = string.IsNullOrWhiteSpace(rom) ? "1942" : rom!.Trim();
        object? res;
        try { res = m.Invoke(null, new object[] { r }); }
        catch (Exception ex) { L("invoke THREW: " + Flatten(ex)); DumpTo(sb, "mame-drivetest.log"); return; }

        L($"DownloadMameGameLeaderboard(\"{r}\") → {(res == null ? "null" : res.GetType().FullName)}");
        if (res is System.Collections.IEnumerable en)
        {
            int i = 0;
            foreach (var item in en)
            {
                if (item == null) continue;
                L($"  [{i++}] {item.ToString()?.Replace("\r", "").Replace("\n", " ")}");
                if (i >= 10) break;
            }
            if (i > 0) L(">>> LiteBox drove the core's leaderboard fetch on this LB version. Upload path is the same call.");
        }
        DumpTo(sb, "mame-drivetest.log");
    }

    private static bool IsHex(string s) { foreach (var ch in s) if (!Uri.IsHexDigit(ch)) return false; return true; }
    private static string SafeLoc2(Assembly a) { try { return a.Location; } catch { return "?"; } }
    private static void DumpTo(StringBuilder sb, string file)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, file), sb.ToString()); } catch { }
    }

    // Dump every method of the MAME high-score + cloud types, to locate the post-game extract/upload pipeline.
    public static void Members(string lbRoot)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[mame-members] " + s); sb.AppendLine(s); }
        L("=== MAME high-score / GamesDatabase members ===");

        Assembly win, core;
        try { win = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll")); }
        catch (Exception ex) { L("load Windows failed: " + ex.Message); DumpTo(sb, "mame-members.log"); return; }
        try { core = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll")); }
        catch (Exception ex) { L("load core failed: " + ex.Message); core = win; }

        var typeNames = new[]
        {
            ("win", "Unbroken.LaunchBox.Windows.Integrations.MAME.MameHighScores"),
            ("win", "Unbroken.LaunchBox.Windows.Integrations.MAME.MameHighScore"),
            ("core", "Unbroken.LaunchBox.Cloud.GamesDatabase"),
        };
        foreach (var (which, tn) in typeNames)
        {
            var asm = which == "core" ? core : win;
            var t = asm.GetType(tn);
            if (t == null) { L($"[{tn}] NOT FOUND"); continue; }
            L($"--- {tn} ---");
            MethodInfo[] ms;
            try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
            catch (Exception ex) { L("  GetMethods failed: " + ex.Message); continue; }
            foreach (var m in ms.OrderBy(m => m.Name))
            {
                if (m.IsSpecialName && (m.Name.StartsWith("get_") || m.Name.StartsWith("set_"))) continue;
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                L($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({ps})");
            }
        }
        DumpTo(sb, "mame-members.log");
    }

    private const string TypeName =
        "Unbroken.LaunchBox.Windows.Integrations.MAME.MameHighScores, Unbroken.LaunchBox.Windows";

    public static void Run(string lbRoot, string? arg)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[mameprobe] " + s); sb.AppendLine(s); }

        L("=== MAME high-score obfuscated-core probe (passive, no network) ===");
        L("lbRoot        = " + lbRoot);
        L("hi2txt.exe    = " + File.Exists(Path.Combine(lbRoot, "ThirdParty", "hi2txt", "hi2txt.exe")));

        Type? t;
        try { t = Type.GetType(TypeName); }
        catch (Exception ex) { L("Type.GetType THREW: " + Flatten(ex)); Dump(sb); return; }
        if (t == null)
        {
            L("RESULT: MameHighScores type NOT resolvable — the obfuscated core is not loadable from here.");
            Dump(sb); return;
        }
        L("MameHighScores resolved from: " + SafeLoc(t));

        // 1) CheckIfSupported(string) — the clean real-vs-stub discriminator.
        var mSup = t.GetMethod("CheckIfSupported", BindingFlags.Public | BindingFlags.Static,
                               null, new[] { typeof(string) }, null);
        if (mSup == null) L("CheckIfSupported(string) NOT found.");
        else
        {
            string good = arg is { Length: > 0 } && !arg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? arg : "1942";
            const string bad = "zzz_definitely_not_a_rom_9999";
            bool? rGood = CallBool(mSup, good, L);
            bool? rBad = CallBool(mSup, bad, L);
            L($"CheckIfSupported(\"{good}\") = {Show(rGood)}");
            L($"CheckIfSupported(\"{bad}\") = {Show(rBad)}");
            if (rGood == true && rBad == false)
                L(">>> REAL obfuscated body EXECUTED (supported vs unsupported discriminated). Approach VIABLE.");
            else if (rGood == true && rBad == true)
                L(">>> Both true → DECOY stub ran (obfuscator did NOT decrypt these bodies in this process).");
            else if (rGood is null || rBad is null)
                L(">>> A call threw — see the exception above (may need core init, or anti-tamper tripped).");
            else
                L(">>> Unexpected result combo — inspect the values above.");
        }

        // 2) ParseDefaultScores(string) — local parse of a scores XML (no network). Only invoked when the
        //    caller passes an existing .xml path as arg (we don't fabricate input here).
        var mParse = t.GetMethod("ParseDefaultScores", BindingFlags.Public | BindingFlags.Static,
                                 null, new[] { typeof(string) }, null);
        L("ParseDefaultScores(string) present = " + (mParse != null));
        if (mParse != null && arg is { Length: > 0 } && arg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
        {
            try
            {
                var res = mParse.Invoke(null, new object[] { arg });
                int n = (res as System.Collections.ICollection)?.Count ?? -1;
                L($"ParseDefaultScores(\"{arg}\") → {(res == null ? "null" : n + " entries")}");
                if (res is System.Collections.IEnumerable en)
                {
                    int i = 0;
                    foreach (var item in en)
                    {
                        var kt = item.GetType();
                        var k = kt.GetProperty("Key")?.GetValue(item);
                        var v = kt.GetProperty("Value")?.GetValue(item);
                        L($"    [{i++}] score={k}  name=\"{v}\"");
                        if (i >= 15) break;
                    }
                    if (i > 0) L(">>> REAL score DATA extracted through LB's obfuscated parser (local, no network).");
                }
            }
            catch (Exception ex) { L("ParseDefaultScores THREW: " + Flatten(ex)); }
        }

        Dump(sb);
    }

    private static bool? CallBool(MethodInfo m, string arg, Action<string> L)
    {
        try { return (bool)m.Invoke(null, new object[] { arg })!; }
        catch (Exception ex) { L($"  CheckIfSupported(\"{arg}\") THREW: " + Flatten(ex)); return null; }
    }

    private static string Show(bool? b) => b is null ? "THREW" : b.Value.ToString();

    private static string SafeLoc(Type t) { try { return t.Assembly.Location; } catch { return "(unknown)"; } }

    private static string Flatten(Exception ex)
    {
        var e = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        return e.GetType().Name + ": " + e.Message;
    }

    private static void Dump(StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mame-probe.log"), sb.ToString()); } catch { }
    }
}
