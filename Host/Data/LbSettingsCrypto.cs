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
// seed are GUID strings; each setting picks its own pair. For the EmuMovies password that pair is PER-INSTALL:
// key == seed == LaunchBox/Settings/ID from Data\Settings.xml (dashes stripped). We read it at runtime (see the
// SettingsId property) — a hardcoded value only decrypts the install it was captured on. The BigBox LockPin pair
// below, by contrast, IS a fixed LaunchBox constant.
//
// Implemented on BouncyCastle (LaunchBox ships it in Core; .NET's own RijndaelManaged can't do a 256-bit
// block). Every call clones the IV — the cipher must not see the key and IV as one shared array.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace LbApiHost.Host.Data;

internal static class LbSettingsCrypto
{
    /// <summary>The EmuMovies key/seed is NOT a fixed constant — it is this install's own GUID, stored as
    /// LaunchBox/Settings/ID in Data\Settings.xml (GUID in "N" form: dashes stripped, key == seed). Proven across
    /// installs: 13.27/13.28 → 57b00a8c…, 12.9 → abd77fbb…, each matching its own &lt;ID&gt;. We read it at runtime
    /// — never hardcoded (that would leak a real install's ID into public source, and would only ever match the one
    /// machine it was lifted from). LaunchBox writes this &lt;ID&gt; on its very first run, and LiteBox installs onto
    /// an existing LaunchBox, so it is always present in practice; a boot guard (<see cref="HasSettingsId"/>) tells
    /// the user to run LaunchBox once if it is somehow missing.</summary>
    private static string? _emuKeySeed;
    private static bool _emuKeyResolved;

    // The RESOLVED LB root when the host booted one (MediaResolver.Init ran — covers a dev bin\ run
    // pointed at any install via --library); exe\.. otherwise (installed layout: the exe IS in Core).
    private static string SettingsPath
        => Path.Combine(Media.MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "Data", "Settings.xml");

    /// <summary>LaunchBox/Settings/ID in "N" form (no dashes), read from Data\Settings.xml and cached. Null when the
    /// file or the &lt;ID&gt; element is absent — the boot guard turns that into a user-facing message.</summary>
    internal static string? SettingsId
    {
        get
        {
            if (_emuKeyResolved) return _emuKeySeed;
            _emuKeyResolved = true;
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var doc = XDocument.Load(path);
                    var settings = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings");
                    var raw = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "ID")?.Value?.Replace("-", "").Trim();
                    if (!string.IsNullOrEmpty(raw) && raw!.Length == 32) _emuKeySeed = raw;
                }
            }
            catch { /* leave null → boot guard handles it */ }
            return _emuKeySeed;
        }
    }

    /// <summary>True when this install has a usable LaunchBox settings ID. The boot path checks this and, when the
    /// data folder is a real LaunchBox install yet has no &lt;ID&gt;, tells the user to run LaunchBox once.</summary>
    internal static bool HasSettingsId => SettingsId != null;

    /// <summary>This install's EmuMovies key/seed. Only reached once <see cref="HasSettingsId"/> is true (the boot
    /// guard blocks the no-ID case), so the null-forgiving read is safe here.</summary>
    private static string EmuMoviesKeySeed => SettingsId!;

    /// <summary>Decrypt an EmuMovies password blob to clear text. Returns the input unchanged when it isn't a
    /// valid blob (already clear, or empty) so a hand-typed password still works.</summary>
    public static string DecryptEmuMoviesPassword(string? stored)
        => TryDecrypt(stored, EmuMoviesKeySeed, out var clear) ? clear : (stored ?? "");

    /// <summary>Encrypt a clear EmuMovies password to LaunchBox's blob format (real LB reads it back).</summary>
    public static string EncryptEmuMoviesPassword(string? clear)
        => string.IsNullOrEmpty(clear) ? "" : Encrypt(clear!, EmuMoviesKeySeed);

    /// <summary>The key / seed GUIDs (format "N") LaunchBox uses for BigBox's parental LockPin
    /// (BigBoxSettings.xml). Unlike EmuMovies, key and seed DIFFER here, and — crucially — this pair is a genuine
    /// app-wide CONSTANT, NOT the per-install &lt;ID&gt; scheme EmuMovies uses. Proven 2026-07-21: encrypting PIN
    /// "0000" yields the byte-identical blob `qZt4x1Vb02Nk0eEYqFrAmPWWp7RRCHeW64PmOxoAvRg=` on 13.25, 13.27 AND
    /// 13.28 (deterministic CBC/PKCS7 → identical ciphertext ⟹ identical key+IV across installs), and the key is
    /// neither the ID nor an MD5(ID). So it is safe to hardcode and leaks no user's data (same category as the
    /// MAME key). SCOPE: the LaunchBox 13.x line — BigBox 12.9 uses a DIFFERENT key (its "0000" blob won't decrypt
    /// with this pair), but LiteBox targets 13.x. A blob it can't decrypt just fails gracefully (returns "").</summary>
    private const string LockPinKey = "7b7fdf9d179643e0be4bea45c827b693";
    private const string LockPinSeed = "cf2976b6f11c459bab7a3f2acc1795f3";

    /// <summary>Decrypt a BigBox LockPin blob to the clear 4-digit PIN ("" when unset/not a blob).</summary>
    public static string DecryptBigBoxLockPin(string? stored)
        => TryDecrypt(stored, LockPinKey, LockPinSeed, out var clear) ? clear : "";

    /// <summary>Encrypt a clear PIN to BigBox's LockPin blob format (real BigBox reads it back).</summary>
    public static string EncryptBigBoxLockPin(string? clear)
        => string.IsNullOrEmpty(clear) ? "" : Convert.ToBase64String(Run(true, Encoding.UTF8.GetBytes(clear!), LockPinKey, LockPinSeed));

    /// <summary>A LiteBox-OWN key/seed for values LiteBox stores at rest for itself (never round-tripped with
    /// LaunchBox) — e.g. a ScreenScraper account password in LiteBox.ini. Obfuscation-grade, same threat model
    /// as ExtendDB's shipped secrets: it keeps the value out of casual plain sight, not from a determined reader
    /// of an open-source build. Distinct from the EmuMovies seed so the two never cross-decrypt.</summary>
    private const string LiteBoxLocalKeySeed = "9f3ac1e07d6b4c2f8a15e93b42d0c6e1";

    /// <summary>Encrypt a LiteBox-own value to a base64 blob (empty in → empty out).</summary>
    public static string EncryptLocal(string? clear)
        => string.IsNullOrEmpty(clear) ? "" : Encrypt(clear!, LiteBoxLocalKeySeed);

    /// <summary>Decrypt a LiteBox-own blob; returns the input unchanged when it isn't one (already clear/empty).</summary>
    public static string DecryptLocal(string? stored)
        => TryDecrypt(stored, LiteBoxLocalKeySeed, out var clear) ? clear : (stored ?? "");

    /// <summary>Diagnostic only: try to decrypt a base64 blob with an EXPLICIT key/seed (each a 32-char GUID-"N"
    /// hex string). Returns the clear text, or null if it isn't a valid blob under that pair. Used by the LockPin
    /// key-universality probe to test whether the captured key decrypts a blob made on a different install.</summary>
    internal static string? TryDecryptExplicit(string? b64, string keyHex, string seedHex)
        => TryDecrypt(b64, keyHex, seedHex, out var clear) ? clear : null;

    // ── Core ──────────────────────────────────────────────────────────────────
    private static bool TryDecrypt(string? b64, string keySeed, out string clear)
        => TryDecrypt(b64, keySeed, keySeed, out clear);

    private static bool TryDecrypt(string? b64, string keyGuid, string seedGuid, out string clear)
    {
        clear = "";
        if (string.IsNullOrEmpty(b64)) return false;
        byte[] data;
        try { data = Convert.FromBase64String(b64); }
        catch { return false; }                        // not base64 → treat as already-clear
        if (data.Length == 0 || data.Length % 32 != 0) return false;   // Rijndael-256 blocks are 32 bytes
        try
        {
            var outBytes = Run(false, data, keyGuid, seedGuid);
            clear = Encoding.UTF8.GetString(outBytes);
            return true;
        }
        catch { return false; }                        // wrong key / corrupt padding → not our blob
    }

    private static string Encrypt(string clear, string keySeed)
        => Convert.ToBase64String(Run(true, Encoding.UTF8.GetBytes(clear), keySeed, keySeed));

    private static byte[] Run(bool encrypt, byte[] data, string keyGuid, string seedGuid)
    {
        byte[] key = Encoding.ASCII.GetBytes(keyGuid);
        byte[] iv = Encoding.ASCII.GetBytes(seedGuid); // LB's primitive takes (key, seed) — may be equal or not
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
