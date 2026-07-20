// MAME leaderboard key ORACLE — LiteBox extracts the CURRENT upload key from the obfuscated core it loads,
// in-process, WITHOUT LaunchBox and WITHOUT sending anything.
//
// How: Harmony-hook (1) the outbound POST so the dummy upload is BLOCKED before it leaves the machine — nothing
// reaches gamesdb.launchbox-app.com — and (2) the BouncyCastle cipher Init so we read the (key, iv) the moment
// the core uses it. Then trigger the encrypt via the clean core call GamesDatabase.UploadMameHighScore(dummy).
// The encrypt runs (key captured); the POST is dropped. Diagnostic only (behind --mame-keyhook). The shipping
// upload path doesn't need this — it just calls UploadMameHighScore directly (the core owns the key).
//
// Safety: the POST block is installed FIRST and its absence ABORTS the run — we never trigger the encrypt
// unless the send is guaranteed blocked.

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

internal static class MameKeyHook
{
    private static readonly List<string> _caps = new();
    private static readonly StringBuilder _log = new();
    private static readonly object _gate = new();
    private static int _blocked;

    private static void L(string s) { Console.WriteLine("[keyhook] " + s); lock (_gate) _log.AppendLine(s); }
    private static int CapCount() { lock (_gate) return _caps.Count; }
    private static void Dump() { try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mame-keyhook.log"), _log.ToString()); } catch { } }

    public static void Run(string lbRoot)
    {
        L("=== MAME leaderboard key ORACLE (in-process cipher hook, POST blocked) ===");
        var h = new Harmony("litebox.mamekeyhook");

        // 0) WARM UP the obfuscated core UNHOOKED first — patching the BouncyCastle cipher BEFORE the module
        // initializer runs makes that (crypto-using) initializer throw. A read (DownloadMameGameLeaderboard, the
        // proven-safe drive-test path) runs the module init cleanly; only then do we install the hooks.
        MethodInfo? up = null;
        try
        {
            var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll"));
            var gdb = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase");
            up = gdb?.GetMethod("UploadMameHighScore", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(long), typeof(string) }, null);
            var dl = gdb?.GetMethod("DownloadMameGameLeaderboard", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            try { dl?.Invoke(null, new object[] { "1942" }); L("core warmed up (module init ran via a read)."); }
            catch (Exception ex) { L("warmup read threw (continuing): " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name)); }
        }
        catch (Exception ex) { L("core load failed: " + ex.Message); Dump(); return; }
        if (up == null) { L("UploadMameHighScore not found."); Dump(); return; }

        // 1) BLOCK the outbound upload POST. If we can't guarantee the block, abort before triggering.
        var send = typeof(SocketsHttpHandler).GetMethod("SendAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new[] { typeof(HttpRequestMessage), typeof(CancellationToken) }, null);
        if (send == null) { L("ABORT: SocketsHttpHandler.SendAsync not found — cannot guarantee the POST block."); Dump(); return; }
        try
        {
            h.Patch(send, prefix: new HarmonyMethod(typeof(MameKeyHook).GetMethod(nameof(Send_Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            L("POST block armed on SocketsHttpHandler.SendAsync (uploadmamehighscore → dropped).");
        }
        catch (Exception ex) { L("ABORT: could not patch SendAsync (" + ex.Message + ") — won't risk a send."); Dump(); return; }

        // 2) Cipher capture (installed AFTER warmup so the module init isn't disturbed).
        try { InstallBouncy(h); } catch (Exception ex) { L("cipher hook install failed: " + ex.Message); }

        // 3) Trigger the encrypt via the clean core method.
        L("triggering UploadMameHighScore(dummy, \"1942\", 1, \"ZZZ\") — POST blocked, encrypt captured…");
        try { up.Invoke(null, new object[] { "00000000-0000-0000-0000-000000000000", "1942", 1L, "ZZZ" }); }
        catch (Exception ex)
        {
            var real = ex is TargetInvocationException ? (ex.InnerException ?? ex) : ex;
            L("invoke threw: " + real.GetType().FullName + ": " + real.Message);
            for (var e = real.InnerException; e != null; e = e.InnerException) L("  ← " + e.GetType().Name + ": " + e.Message);
        }

        // 4) Wait for the (possibly async) encrypt to land.
        for (int i = 0; i < 80 && CapCount() == 0; i++) Thread.Sleep(100);

        L($"POSTs blocked: {_blocked}   cipher captures: {CapCount()}");
        lock (_gate) foreach (var c in _caps) L(c);
        if (CapCount() == 0) L("→ no cipher captured (encrypt didn't run through BouncyCastle, or didn't fire).");
        Dump();
    }

    // Block ONLY the upload write; harmless public reads (if any) pass through.
    private static bool Send_Prefix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result)
    {
        try
        {
            var u = request?.RequestUri?.ToString() ?? "";
            if (u.IndexOf("uploadmamehighscore", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Interlocked.Increment(ref _blocked);
                __result = Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Success\":true}") });
                return false;   // skip the real send — nothing leaves the machine
            }
        }
        catch { }
        return true;
    }

    private static void InstallBouncy(Harmony h)
    {
        var bc = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "BouncyCastle.Crypto")
                 ?? Assembly.Load(new AssemblyName("BouncyCastle.Crypto"));
        var icp = bc.GetType("Org.BouncyCastle.Crypto.ICipherParameters");
        int n = 0;
        foreach (var tn in new[]
        {
            "Org.BouncyCastle.Crypto.Paddings.PaddedBufferedBlockCipher",
            "Org.BouncyCastle.Crypto.BufferedBlockCipher",
            "Org.BouncyCastle.Crypto.Modes.CbcBlockCipher",
            "Org.BouncyCastle.Crypto.Engines.AesEngine",
        })
        {
            var ty = bc.GetType(tn);
            var m = ty?.GetMethod("Init", new[] { typeof(bool), icp });
            if (m == null || m.IsAbstract) continue;
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(MameKeyHook).GetMethod(nameof(Init_Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }); n++; }
            catch { }
        }
        L($"cipher hook armed on {n} BouncyCastle Init overloads.");
    }

    private static void Init_Prefix(bool forEncryption, object parameters)
    {
        try
        {
            if (parameters == null) return;
            byte[]? iv = null, key = null;
            object p = parameters;
            var pIv = p.GetType().GetMethod("GetIV");
            if (pIv != null) { iv = pIv.Invoke(p, null) as byte[]; var inner = p.GetType().GetProperty("Parameters")?.GetValue(p); if (inner != null) p = inner; }
            key = p.GetType().GetMethod("GetKey")?.Invoke(p, null) as byte[];
            if (key == null) return;
            string line = $"{(forEncryption ? "Encrypt" : "Decrypt")} keyLen={key.Length} ivLen={iv?.Length ?? 0}\n"
                        + $"    key={Hex(key)}  (\"{Ascii(key)}\")\n"
                        + $"    iv ={Hex(iv)}  (\"{Ascii(iv)}\")";
            lock (_gate) { if (!_caps.Contains(line)) _caps.Add(line); }
        }
        catch { }
    }

    private static string Hex(byte[]? b) { if (b == null) return "<null>"; var sb = new StringBuilder(b.Length * 2); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }
    private static string Ascii(byte[]? b) { if (b == null) return ""; var sb = new StringBuilder(); foreach (var x in b) sb.Append(x >= 32 && x < 127 ? (char)x : '.'); return sb.ToString(); }
}
