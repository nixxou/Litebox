// MAME high-score UPLOAD — the write brick for the LaunchBox Games Database MAME leaderboards.
//
// ⚠️ GRAY ZONE: submitting scores to LB's community leaderboards from a non-LaunchBox client circumvents LB's
// client gating (anti-cheat). Use only with the user's OWN real scores + real account token, on explicit
// action. This brick is NOT auto-wired — nothing calls it on its own.
//
// Strategy (agreed): PRIMARY = drive the core's clean, non-obfuscated call GamesDatabase.UploadMameHighScore
// (Unbroken.LaunchBox.dll) — the core encrypts with ITS CURRENT key, so this is robust to the key rotating on
// a future LB update. FALLBACK (try/catch) = reproduce the request ourselves with the captured fixed key: the
// blob is Rijndael-256/CBC/PKCS7 of "token|rom|score|inGameName", base64, POSTed as contents= (verified
// byte-identical to LB on 13.27 & 13.28). The fallback works as long as that key hasn't rotated; the core path
// covers the case where it has. See memory reference-lb-mame-leaderboards.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace LbApiHost.Host.Mame;

internal enum MameUploadResult { Core, Fallback, Failed }

internal static class MameUpload
{
    private const string UploadUrl = "https://gamesdb.launchbox-app.com/launchbox/uploadmamehighscore";
    // The fixed leaderboard key/IV live in LbKeys (the one place LiteBox defines/resolves key material).
    private static string KeyAscii => Data.LbKeys.MameKey;
    private static string IvAscii => Data.LbKeys.MameIv;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Submit one MAME high score. token = the user's LBGDB CloudAuthenticationToken (Settings.xml);
    /// inGameName = the arcade initials (may be blank). Tries the core call first, then the captured-key POST.</summary>
    public static async Task<MameUploadResult> SendAsync(string token, string rom, long score, string inGameName)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(rom)) return MameUploadResult.Failed;
        inGameName ??= "";

        // PRIMARY — drive the core (uses the current key; robust to rotation). Fire-and-forget void: a
        // synchronous throw (missing method / module-init failure) drops us to the fallback; otherwise the
        // core owns the encrypt + POST.
        if (TryCore(token, rom, score, inGameName)) return MameUploadResult.Core;

        // FALLBACK — reproduce the request ourselves with the captured key.
        if (await TryFallbackAsync(token, rom, score, inGameName).ConfigureAwait(false)) return MameUploadResult.Fallback;

        return MameUploadResult.Failed;
    }

    private static bool TryCore(string token, string rom, long score, string inGameName)
    {
        try
        {
            var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll"));
            var gdb = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase");
            var m = gdb?.GetMethod("UploadMameHighScore", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(long), typeof(string) }, null);
            if (m == null) return false;
            m.Invoke(null, new object[] { token, rom, score, inGameName });
            Console.WriteLine($"[mame] upload via core: {rom} {score} \"{inGameName}\"");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[mame] core upload path failed → fallback: " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name));
            return false;
        }
    }

    private static async Task<bool> TryFallbackAsync(string token, string rom, long score, string inGameName)
    {
        try
        {
            string plaintext = $"{token}|{rom}|{score}|{inGameName}";
            string contents = Convert.ToBase64String(RijndaelEncrypt(plaintext));
            using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("contents", contents) });
            using var resp = await Http.PostAsync(UploadUrl, body).ConfigureAwait(false);
            bool ok = resp.IsSuccessStatusCode;
            Console.WriteLine($"[mame] upload via captured-key fallback: {rom} {score} → {(ok ? "OK" : resp.StatusCode.ToString())}");
            return ok;
        }
        catch (Exception ex) { Console.WriteLine("[mame] fallback upload failed: " + ex.Message); return false; }
    }

    /// <summary>No-network self-test: confirm the fallback crypto reproduces LB's blob byte-identically (the
    /// captured 1942|300 upload). Proves the BouncyCastle Rijndael-256 path works in-process before we ever rely
    /// on it. Nothing is sent.</summary>
    public static (bool match, string mine, string expected) SelfTest()
    {
        const string pt = "0d021068-e652-42d9-ada9-54d94cfa9501|1942|300|        ";
        const string expected = "vvAf1wxmTcPAtRnUgrs7+OE/4dEGt33xgQv6vAZSQ2x9G7PfGHH3kIBhgcchCE9A5tr1Vf+jC1oHudcjbMlk2Q==";
        string mine = Convert.ToBase64String(RijndaelEncrypt(pt));
        return (mine == expected, mine, expected);
    }

    // Rijndael-256/CBC/PKCS7 via BouncyCastle (LiteBox already references it; .NET's own RijndaelManaged can't
    // do the 256-bit BLOCK size LB uses). Produces the exact bytes LB's core does for the same plaintext.
    private static byte[] RijndaelEncrypt(string plaintext)
    {
        var key = Encoding.ASCII.GetBytes(KeyAscii);   // 32 bytes
        var iv = Encoding.ASCII.GetBytes(IvAscii);     // 32 bytes (Rijndael-256 block)
        var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new RijndaelEngine(256)), new Pkcs7Padding());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != output.Length) Array.Resize(ref output, len);
        return output;
    }
}
