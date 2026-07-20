// KeyHarvest — LiteBox reads the crypto keys OUT of the obfuscated core it loaded, at runtime, so we use
// the ACTUAL keys of THIS install instead of hardcoded constants (which we proved are wrong for the settings
// cipher: its key is the per-install InstanceId, e.g. 57b00a8c… on one install, abd77fbb… on another).
//
// Two capture surfaces, both via Harmony:
//   • Rijndael.Encrypt/Decrypt(value, key, seed) — LB's own helper, and the key/seed are EXPLICIT ARGS, so we
//     read the settings/EmuMovies key straight off the call. (Bonus BouncyCastle cipher hook as a fallback.)
//   • the MAME leaderboard key — an inline constant that only appears when the core encrypts a submission, so
//     we trigger GamesDatabase.UploadMameHighScore(dummy) with the POST BLOCKED and read the key off Rijndael.
//
// Triggers: MAME = a clean static call (works). Settings/EmuMovies = fires only at EmuMovies login, whose
// client lives in the un-decompiled helper — so we ALSO just capture whatever settings crypto happens to run,
// and report if none did. Output: <Core>\litebox\keys.report.txt + keys.json (cache Lb* readers can prefer).
//
// Needs Harmony (2.4.2 on .NET 10). Nothing is uploaded (the dummy MAME POST is dropped).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace LbApiHost.Host.Diag;

internal static class KeyHarvest
{
    private sealed class Cap { public string Op=""; public string Key=""; public string Seed=""; public string Stack=""; }
    private static readonly List<Cap> _caps = new();
    private static readonly StringBuilder _log = new();
    private static readonly object _gate = new();
    private static volatile bool _blockUpload;
    private static volatile bool _blockAll;   // during the EmuMovies trigger: drop ALL outbound (never send creds)

    private static void L(string s) { Console.WriteLine("[keyharvest] " + s); lock (_gate) _log.AppendLine(s); }

    public static void Run(string lbRoot)
    {
        L("=== KeyHarvest — read the core's keys at runtime (nothing uploaded) ===");
        var h = new Harmony("litebox.keyharvest");

        // 0) warm up the core UNHOOKED (patching BouncyCastle before the obfuscated <Module> init NREs it).
        Assembly? asm = null;
        try
        {
            asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll"));
            var gdb0 = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase");
            try { gdb0?.GetMethod("DownloadMameGameLeaderboard", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)?.Invoke(null, new object[] { "1942" }); } catch { }
            L("core warmed up.");
        }
        catch (Exception ex) { L("core load failed: " + ex.Message); Dump(lbRoot); return; }

        // 1) block the outbound MAME upload FIRST (safety — nothing is sent).
        var send = typeof(SocketsHttpHandler).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new[] { typeof(HttpRequestMessage), typeof(CancellationToken) }, null);
        if (send == null) { L("ABORT: SendAsync not found — won't risk a send."); Dump(lbRoot); return; }
        try { h.Patch(send, prefix: new HarmonyMethod(typeof(KeyHarvest).GetMethod(nameof(Send_Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }); }
        catch (Exception ex) { L("ABORT: SendAsync patch failed: " + ex.Message); Dump(lbRoot); return; }

        // 2) hook Rijndael.Encrypt/Decrypt (key/seed are explicit args) + BouncyCastle cipher (fallback).
        try { HookRijndael(h, asm); } catch (Exception ex) { L("rijndael hook: " + ex.Message); }
        try { HookBouncy(h); } catch (Exception ex) { L("bouncy hook: " + ex.Message); }

        // 3) trigger the MAME key (clean static call, POST blocked).
        try
        {
            var up = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase")?.GetMethod("UploadMameHighScore",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string), typeof(long), typeof(string) }, null);
            if (up != null)
            {
                _blockUpload = true;
                try { up.Invoke(null, new object[] { "00000000-0000-0000-0000-000000000000", "1942", 1L, "ZZ" }); } catch { }
                finally { _blockUpload = false; }
                for (int i = 0; i < 50 && !_caps.Any(c => c.Op == "mame"); i++) Thread.Sleep(100);
                L("MAME trigger done.");
            }
            else L("MAME: UploadMameHighScore not found (feature absent on this LB).");
        }
        catch (Exception ex) { _blockUpload = false; L("MAME trigger failed: " + ex.Message); }

        // 4) trigger the SETTINGS/EmuMovies key: EmuMoviesWrapper.CanAttemptLogin/Login decrypts the stored
        // password blob (→ Rijndael.Decrypt(blob, InstanceId, InstanceId)), which our hook reads. ALL network
        // is blocked during this so the real credentials are never sent.
        try { TriggerSettings(lbRoot); } catch (Exception ex) { _blockAll = false; L("settings trigger failed: " + ex.Message); }

        Dump(lbRoot);
    }

    private static void TriggerSettings(string lbRoot)
    {
        // read userId + stored (encrypted) password from Settings.xml
        string userId = "", blob = "";
        try
        {
            var p = Path.Combine(lbRoot, "Data", "Settings.xml");
            if (File.Exists(p))
            {
                var doc = System.Xml.Linq.XDocument.Load(p);
                userId = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "EmuMoviesUserId")?.Value ?? "";
                blob = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "EmuMoviesPassword")?.Value ?? "";
            }
        }
        catch { }
        if (string.IsNullOrEmpty(blob)) { L("settings trigger: no EmuMoviesPassword in Settings.xml → nothing to decrypt."); return; }

        var t = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType("Unbroken.LaunchBox.Search.EmuMovies.EmuMoviesWrapper"); } catch { return null; } })
            .FirstOrDefault(x => x != null);
        if (t == null) { L("settings trigger: EmuMoviesWrapper not found (feature absent?)."); return; }

        _blockAll = true;   // NOTHING goes out while we poke the login path
        try
        {
            // Prefer CanAttemptLogin (likely local); it — or Login — decrypts the blob first, firing our hook.
            var can = t.GetMethod("CanAttemptLogin", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            try { can?.Invoke(null, new object[] { userId, blob }); } catch { }
            if (!_caps.Any(c => c.Op == "settings"))
            {
                var login = t.GetMethod("Login", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string), typeof(bool) }, null);
                try { login?.Invoke(null, new object[] { userId, blob, false }); } catch { }
            }
            for (int i = 0; i < 40 && !_caps.Any(c => c.Op == "settings"); i++) Thread.Sleep(100);
        }
        finally { _blockAll = false; }
        L("settings trigger done (network was blocked).");
    }

    private static bool Send_Prefix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result)
    {
        try
        {
            var u = request?.RequestUri?.ToString() ?? "";
            if (_blockAll || (_blockUpload && u.IndexOf("uploadmamehighscore", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                __result = Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Success\":true}") });
                return false;
            }
        }
        catch { }
        return true;
    }

    // Rijndael.Encrypt/Decrypt(value, key, seed) — capture key+seed off the args (Guid or string overloads).
    private static void HookRijndael(Harmony h, Assembly asm)
    {
        var t = asm.GetType("Unbroken.LaunchBox.Rijndael");
        if (t == null) { L("Rijndael type not found."); return; }
        int n = 0;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Encrypt" && m.Name != "Decrypt") continue;
            var ps = m.GetParameters();
            if (ps.Length != 3) continue;
            var kt = ps[1].ParameterType;
            if (kt != typeof(string) && kt != typeof(Guid)) continue;   // the (value, key, seed) string/Guid forms
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(KeyHarvest).GetMethod(nameof(Rijndael_Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }); n++; }
            catch { }
        }
        L($"Rijndael hook armed on {n} Encrypt/Decrypt overloads.");
    }

    private static void Rijndael_Prefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 3) return;
            string key = GuidOrString(__args[1]);
            string seed = GuidOrString(__args[2]);
            if (key.Length == 0) return;
            string stack = ShortStack();
            string op = (stack.IndexOf("UploadMameHighScore", StringComparison.OrdinalIgnoreCase) >= 0
                      || stack.IndexOf("GamesDatabase", StringComparison.OrdinalIgnoreCase) >= 0) ? "mame" : "settings";
            Add(op, __originalMethod?.Name + " " + key + "/" + seed, key, seed, stack);
        }
        catch { }
    }

    private static string GuidOrString(object? o) => o switch { Guid g => g.ToString("N"), string s => s, _ => "" };

    private static void HookBouncy(Harmony h)
    {
        var bc = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "BouncyCastle.Crypto");
        var icp = bc?.GetType("Org.BouncyCastle.Crypto.ICipherParameters");
        if (bc == null || icp == null) return;
        foreach (var tn in new[] { "Org.BouncyCastle.Crypto.Paddings.PaddedBufferedBlockCipher", "Org.BouncyCastle.Crypto.Modes.CbcBlockCipher" })
        {
            var m = bc.GetType(tn)?.GetMethod("Init", new[] { typeof(bool), icp });
            if (m == null || m.IsAbstract) continue;
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(KeyHarvest).GetMethod(nameof(Bouncy_Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }); } catch { }
        }
    }

    private static void Bouncy_Prefix(bool forEncryption, object parameters)
    {
        try
        {
            if (parameters == null) return;
            byte[]? iv = null, key; object p = parameters;
            var pIv = p.GetType().GetMethod("GetIV");
            if (pIv != null) { iv = pIv.Invoke(p, null) as byte[]; var inner = p.GetType().GetProperty("Parameters")?.GetValue(p); if (inner != null) p = inner; }
            key = p.GetType().GetMethod("GetKey")?.Invoke(p, null) as byte[];
            if (key == null) return;
            string ka = new string(key.Select(b => (char)b).ToArray());
            string ia = iv == null ? "" : new string(iv.Select(b => (char)b).ToArray());
            string stack = ShortStack();
            Add(stack.IndexOf("UploadMameHighScore", StringComparison.OrdinalIgnoreCase) >= 0 ? "mame" : "cipher", (forEncryption ? "Enc" : "Dec"), ka, ia, stack);
        }
        catch { }
    }

    private static void Add(string op, string tag, string key, string seed, string stack)
    {
        lock (_gate)
        {
            if (_caps.Any(c => c.Op == op && c.Key == key && c.Seed == seed)) return;
            _caps.Add(new Cap { Op = op, Key = key, Seed = seed, Stack = stack });
            _log.AppendLine($"  captured [{op}] {tag}: key=\"{key}\" seed=\"{seed}\"");
        }
    }

    private static string ShortStack()
    {
        try
        {
            var fr = new System.Diagnostics.StackTrace(2, false).GetFrames();
            if (fr == null) return "";
            var parts = new List<string>();
            foreach (var f in fr)
            {
                var m = f.GetMethod(); var a = m?.DeclaringType?.Assembly.GetName().Name;
                if (a == null || a.StartsWith("System") || a == "LiteBox" || a == "0Harmony") continue;
                parts.Add($"{a}:{m!.DeclaringType!.Name}.{m.Name}");
                if (parts.Count >= 5) break;
            }
            return string.Join(" ← ", parts);
        }
        catch { return ""; }
    }

    private static void Dump(string lbRoot)
    {
        var rep = new StringBuilder();
        rep.AppendLine("LiteBox KeyHarvest report");
        lock (_gate)
        {
            var mame = _caps.FirstOrDefault(c => c.Op == "mame");
            var settings = _caps.Where(c => c.Op == "settings").ToList();
            rep.AppendLine();
            rep.AppendLine("MAME leaderboard key: " + (mame != null ? $"\"{mame.Key}\" / iv \"{mame.Seed}\"" : "(not captured)"));
            rep.AppendLine();
            rep.AppendLine("Settings/EmuMovies key(s) (per-install): " + (settings.Count == 0
                ? "(none — no settings crypto ran; fires only at EmuMovies login)"
                : string.Join(", ", settings.Select(s => $"\"{s.Key}\""))));
            foreach (var s in settings) rep.AppendLine("   via " + s.Stack);
            rep.AppendLine();
            rep.AppendLine("--- trace ---");
            rep.Append(_log);
        }
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "litebox");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keys.report.txt"), rep.ToString());
            // JSON cache the LbSettingsCrypto/MameUpload readers could prefer over hardcoded constants.
            lock (_gate)
            {
                var mame = _caps.FirstOrDefault(c => c.Op == "mame");
                var set = _caps.FirstOrDefault(c => c.Op == "settings");
                var json = "{\n" +
                    $"  \"mameKey\": \"{mame?.Key}\", \"mameIv\": \"{mame?.Seed}\",\n" +
                    $"  \"settingsKey\": \"{set?.Key}\", \"settingsSeed\": \"{set?.Seed}\"\n" + "}";
                File.WriteAllText(Path.Combine(dir, "keys.json"), json);
            }
            L("wrote litebox\\keys.report.txt + keys.json");
        }
        catch (Exception ex) { L("dump failed: " + ex.Message); }
    }
}
