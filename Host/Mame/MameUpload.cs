// MAME high-score UPLOAD — the write brick for the LaunchBox Games Database MAME leaderboards.
//
// ⚠️ GRAY ZONE: submitting scores to LB's community leaderboards from a non-LaunchBox client circumvents LB's
// client gating (anti-cheat). Only ever the user's OWN real scores, under their own account token.
// NB: this IS auto-wired — MameHighScoreSubmit calls it at the end of every MAME/FBNeo game that beat its
// pre-game best. (It once was not, and this notice still said so long after that stopped being true.)
//
// Two ways to send, and the ORDER is the whole design:
//
//   OWN REQUEST (primary) — reproduce the call ourselves: "token|rom|score|inGameName" encrypted
//   Rijndael-256/CBC/PKCS7 with the captured fixed key, base64, POSTed as contents=. SelfTest pins it against
//   a REAL captured LaunchBox upload and still matches byte for byte on 13.27 and 13.28. Its virtue here is
//   not speed, it is that it comes back with an HTTP status: we know whether the score landed.
//
//   THE CORE (fallback) — drive GamesDatabase.UploadMameHighScore in Unbroken.LaunchBox.dll. It encrypts with
//   LB's CURRENT key, so it is the escape hatch if that fixed key ever rotates and our own request starts
//   being rejected.
//
// The core used to be primary, for that rotation-proofing — but the method is a fire-and-forget void, so
// "it did not throw" was the only thing we could observe, and returning success on that suppressed the
// request that could actually tell us. A real score (mslugx, 142130) was reported submitted and never
// reached the leaderboard. Verifiable first, rotation-proof second: the fallback only runs when the primary
// says no, which is exactly the case rotation would produce. See memory reference-lb-mame-leaderboards.

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

/// <summary>What we actually know afterwards — not which code path ran. The names used to be the two route
/// names, which stopped meaning anything the day the routes swapped: the log then said "Fallback" for the
/// good outcome and needed a footnote to read.
///   Confirmed   — the server accepted it (our own request, HTTP 2xx).
///   Unconfirmed — our request was refused, so the core was called instead; it returns nothing, so this says
///                 only that the call was made. The caller treats it as sent ON PURPOSE: retrying a score the
///                 core may well have delivered means duplicates, and on a rom the database refuses it means
///                 retrying at the end of every game, forever. A rare unconfirmed loss beats permanent noise.
///   Failed      — nothing was accepted.</summary>
internal enum MameUploadResult { Unconfirmed, Confirmed, Failed }

internal static class MameUpload
{
    private const string UploadUrl = "https://gamesdb.launchbox-app.com/launchbox/uploadmamehighscore";
    // The fixed leaderboard key/IV live in LbKeys (the one place LiteBox defines/resolves key material).
    private static string KeyAscii => Data.LbKeys.MameKey;
    private static string IvAscii => Data.LbKeys.MameIv;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Submit one MAME high score. token = the user's LBGDB CloudAuthenticationToken (Settings.xml);
    /// inGameName = the arcade initials (may be blank). Sends the request ourselves first because that is the
    /// only path that reports whether the score was accepted; the core is tried only if it was not.</summary>
    public static async Task<MameUploadResult> SendAsync(string token, string rom, long score, string inGameName)
    {
        if (string.IsNullOrWhiteSpace(token)) { Log("no LaunchBox Games Database token — sign in on the LB Integrations tab."); return MameUploadResult.Failed; }
        if (string.IsNullOrWhiteSpace(rom)) { Log("no rom name — nothing to submit."); return MameUploadResult.Failed; }
        inGameName ??= "";

        // PRIMARY — our own request. Returns an HTTP status, so success here is observed, not assumed.
        if (await TrySelfAsync(token, rom, score, inGameName).ConfigureAwait(false)) return MameUploadResult.Confirmed;

        // FALLBACK — the core, which signs with LB's current key. Reached when our own request was refused,
        // which is what a rotated key would look like. It cannot confirm anything (void, fire-and-forget), so
        // the result says only that the call was made.
        if (TryCore(token, rom, score, inGameName)) return MameUploadResult.Unconfirmed;

        return MameUploadResult.Failed;
    }

    private static void Log(string s) => MameHighScoreSubmit.Log(s, "mame-upload");

    /// <summary>Drive the core's own upload. It is a VOID, fire-and-forget method: all we can observe is that
    /// the invoke did not throw, which says the call was made and nothing about whether the score arrived.
    /// That is precisely why it is no longer the primary path.</summary>
    private static bool TryCore(string token, string rom, long score, string inGameName)
    {
        try
        {
            var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll"));
            var gdb = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase");
            var m = gdb?.GetMethod("UploadMameHighScore", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(long), typeof(string) }, null);
            if (m == null) { Log("core: GamesDatabase.UploadMameHighScore not found in Unbroken.LaunchBox.dll."); return false; }
            m.Invoke(null, new object[] { token, rom, score, inGameName });
            Log($"core: called UploadMameHighScore({rom}, {score}, \"{inGameName}\") — it reports nothing back, so this is NOT a confirmation.");
            return true;
        }
        catch (Exception ex)
        {
            Log("core: call failed — " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name));
            return false;
        }
    }

    /// <summary>Our own request, byte-identical to LaunchBox's (see SelfTest). The one path that answers.</summary>
    private static async Task<bool> TrySelfAsync(string token, string rom, long score, string inGameName)
    {
        try
        {
            string plaintext = $"{token}|{rom}|{score}|{inGameName}";
            string contents = Convert.ToBase64String(RijndaelEncrypt(plaintext));
            using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("contents", contents) });
            using var resp = await Http.PostAsync(UploadUrl, body).ConfigureAwait(false);
            string reply = "";
            try { reply = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false) ?? "").Trim(); } catch { }
            if (reply.Length > 200) reply = reply.Substring(0, 200) + "…";
            bool ok = resp.IsSuccessStatusCode;
            // A 2xx is taken at face value — but if the body ever contradicts it, say so loudly instead of
            // quietly counting the score as sent. Deliberately does NOT flip the verdict: a malformed blob
            // already comes back 500, this shape has never been observed, and treating it as a failure would
            // retry at the end of every game on a rom the database simply refuses. If this line ever shows
            // up in the log, that is the moment to decide what it should mean.
            if (ok && reply.IndexOf("\"Success\":false", StringComparison.OrdinalIgnoreCase) >= 0)
                Log($"WARNING {rom}: HTTP 2xx but the body reports failure — counted as sent anyway (see body).");
            Log($"own request: {rom} {score} \"{inGameName}\" → HTTP {(int)resp.StatusCode} {resp.StatusCode}"
                + (reply.Length > 0 ? $" | body: {reply}" : ""));
            return ok;
        }
        catch (Exception ex) { Log("own request failed: " + ex.Message); return false; }
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
