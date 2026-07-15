// LaunchBox's settings cipher, reproduced. LB stores a few Settings.xml values encrypted rather than in clear
// (the EmuMovies password, BigBox's parental LockPin, …); the field is a base64 blob, not the value. LiteBox
// needs the CLEAR value to use it (the EmuMovies API wants the plaintext password), and must be able to write
// a blob the REAL LaunchBox can read back — so this both decrypts and encrypts in LB's exact format.
//
// The scheme, recovered by capturing the key BouncyCastle receives at runtime (LB's core is obfuscated, so it
// can't be read statically — see ExtendDB's [CryptoKey] probe):
//
//     Rijndael-256  (256-bit BLOCK, not AES's 128) · CBC · PKCS7 · plaintext UTF-8
//     key = iv = the 32 ASCII bytes of a per-setting GUID in "N" form
//
// LaunchBox's own primitive is `Unbroken.LaunchBox.Rijndael.Encrypt/Decrypt(value, key, seed)` where key and
// seed are GUID strings; each setting picks its own pair. For the EmuMovies password that pair is the constant
// below (verified: it round-trips this install's stored blob byte-for-byte). It is a fixed LaunchBox constant,
// not machine-derived, so hardcoding it is safe and keeps updates simple.
//
// Implemented on BouncyCastle (LaunchBox ships it in Core; .NET's own RijndaelManaged can't do a 256-bit
// block). Every call clones the IV — the cipher must not see the key and IV as one shared array.

#nullable enable

using System;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace LbApiHost.Host.Data;

internal static class LbSettingsCrypto
{
    /// <summary>The key/seed GUID (format "N") LaunchBox uses for the EmuMovies password. Fixed LB constant.</summary>
    private const string EmuMoviesKeySeed = "57b00a8c743b4ea49738f9d3c05e7797";

    /// <summary>Decrypt an EmuMovies password blob to clear text. Returns the input unchanged when it isn't a
    /// valid blob (already clear, or empty) so a hand-typed password still works.</summary>
    public static string DecryptEmuMoviesPassword(string? stored)
        => TryDecrypt(stored, EmuMoviesKeySeed, out var clear) ? clear : (stored ?? "");

    /// <summary>Encrypt a clear EmuMovies password to LaunchBox's blob format (real LB reads it back).</summary>
    public static string EncryptEmuMoviesPassword(string? clear)
        => string.IsNullOrEmpty(clear) ? "" : Encrypt(clear!, EmuMoviesKeySeed);

    // ── Core ──────────────────────────────────────────────────────────────────
    private static bool TryDecrypt(string? b64, string keySeed, out string clear)
    {
        clear = "";
        if (string.IsNullOrEmpty(b64)) return false;
        byte[] data;
        try { data = Convert.FromBase64String(b64); }
        catch { return false; }                        // not base64 → treat as already-clear
        if (data.Length == 0 || data.Length % 32 != 0) return false;   // Rijndael-256 blocks are 32 bytes
        try
        {
            var outBytes = Run(false, data, keySeed);
            clear = Encoding.UTF8.GetString(outBytes);
            return true;
        }
        catch { return false; }                        // wrong key / corrupt padding → not our blob
    }

    private static string Encrypt(string clear, string keySeed)
        => Convert.ToBase64String(Run(true, Encoding.UTF8.GetBytes(clear), keySeed));

    private static byte[] Run(bool encrypt, byte[] data, string keySeed)
    {
        byte[] key = Encoding.ASCII.GetBytes(keySeed);
        byte[] iv = (byte[])key.Clone();               // distinct array — the cipher mustn't share key and IV
        var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new RijndaelEngine(256)), new Pkcs7Padding());
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
        var buf = new byte[cipher.GetOutputSize(data.Length)];
        int n = cipher.ProcessBytes(data, 0, data.Length, buf, 0);
        n += cipher.DoFinal(buf, n);
        if (n == buf.Length) return buf;
        var trimmed = new byte[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }
}
