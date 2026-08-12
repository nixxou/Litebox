// The parental PIN is BigBox's OWN <LockPin> — the single credential LiteBox also uses. It lives centrally in
// Data\BigBoxSettings.xml, but we NEVER edit that file directly from inside a running LaunchBox: LaunchBox holds
// the settings in memory and rewrites its own copy on close, erasing a file edit. Instead:
//   • READ  — from the live in-memory BigBoxSettings when reachable (authoritative), else the file.
//   • WRITE — set LockPin on the live BigBoxSettings object and let LaunchBox's own save persist it
//             (BigBoxSettingsAccess). See that class for the reflection path.
// The blob is Rijndael-256 / CBC / PKCS7 with a fixed LaunchBox key+seed (recovered — see LbSettingsCrypto);
// BouncyCastle (shipped in Core) does the 256-bit-block cipher .NET can't. A 3-strike lockout lives in PinLockout.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace LiteBoxParental
{
    internal static class PinVerify
    {
        // The fixed LaunchBox 13.x key/seed for the BigBox parental LockPin (constants, not per-install;
        // proven identical ciphertext across 13.25/27/28 — see LbSettingsCrypto). Leaks no user data.
        private const string LockPinKey  = "7b7fdf9d179643e0be4bea45c827b693";
        private const string LockPinSeed = "cf2976b6f11c459bab7a3f2acc1795f3";

        public static bool HasPin => Current().Length > 0;

        /// <summary>A PIN can be set when we can reach the in-memory BigBoxSettings (i.e. inside a running
        /// LaunchBox/BigBox — always true for the plugin). Setting it there lets LaunchBox persist it itself.</summary>
        public static bool CanSetPin => BigBoxSettingsAccess.Available;

        /// <summary>The clear PIN. PRIMARY: the live in-memory BigBoxSettings.LockPin (authoritative while
        /// LaunchBox runs). FALLBACK: BigBox's &lt;LockPin&gt; in BigBoxSettings.xml. "" when none / unreadable.</summary>
        public static string Current()
        {
            try
            {
                if (BigBoxSettingsAccess.Available)
                    return Decrypt(BigBoxSettingsAccess.ReadLockPinBlob());
            }
            catch { }
            return BigBoxPinFromFile();
        }

        /// <summary>Set (or clear, when <paramref name="clear"/> is empty) BigBox's LockPin through the live model,
        /// so LaunchBox's own save writes it into BigBoxSettings.xml. Returns true on success. This is the ONLY
        /// write path — a direct file edit would be clobbered by LaunchBox's in-memory copy.</summary>
        public static bool SetPin(string clear)
        {
            var blob = Encrypt(clear ?? "");   // "" ⇒ empty blob ⇒ LockPin cleared
            return BigBoxSettingsAccess.WriteLockPinBlob(blob);
        }

        /// <summary>BigBox's own LockPin from BigBoxSettings.xml, decrypted. Self-closing &lt;LockPin /&gt; ⇒ "".
        /// Read-only fallback for when the in-memory model isn't reachable.</summary>
        private static string BigBoxPinFromFile()
        {
            try
            {
                var path = BigBoxSettingsPath();
                if (path == null || !File.Exists(path)) return "";
                var m = Regex.Match(File.ReadAllText(path), "<LockPin>([^<]*)</LockPin>");
                return m.Success ? Decrypt(m.Groups[1].Value.Trim()) : "";
            }
            catch { return ""; }
        }

        private static string BigBoxSettingsPath()
        {
            try
            {
                var core = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
                return string.IsNullOrEmpty(core) ? null : Path.Combine(core, "..", "Data", "BigBoxSettings.xml");
            }
            catch { return null; }
        }

        /// <summary>Constant-time-ish compare of the entered PIN against the configured one.</summary>
        public static bool Verify(string pin)
        {
            var expected = Current();
            if (expected.Length == 0 || string.IsNullOrEmpty(pin) || pin.Length != expected.Length) return false;
            int diff = 0;
            for (int i = 0; i < pin.Length; i++) diff |= pin[i] ^ expected[i];
            return diff == 0;
        }

        private static string Encrypt(string clear)
        {
            if (string.IsNullOrEmpty(clear)) return "";
            try
            {
                var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new RijndaelEngine(256)), new Pkcs7Padding());
                cipher.Init(true, new ParametersWithIV(new KeyParameter(Encoding.ASCII.GetBytes(LockPinKey)),
                                                       Encoding.ASCII.GetBytes(LockPinSeed)));
                var data = Encoding.UTF8.GetBytes(clear);
                var buf = new byte[cipher.GetOutputSize(data.Length)];
                int n = cipher.ProcessBytes(data, 0, data.Length, buf, 0);
                n += cipher.DoFinal(buf, n);
                return Convert.ToBase64String(buf, 0, n);
            }
            catch { return ""; }
        }

        private static string Decrypt(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            byte[] data;
            try { data = Convert.FromBase64String(b64); } catch { return ""; }
            if (data.Length == 0 || data.Length % 32 != 0) return "";   // Rijndael-256 blocks are 32 bytes
            try
            {
                var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new RijndaelEngine(256)), new Pkcs7Padding());
                cipher.Init(false, new ParametersWithIV(new KeyParameter(Encoding.ASCII.GetBytes(LockPinKey)),
                                                        Encoding.ASCII.GetBytes(LockPinSeed)));
                var buf = new byte[cipher.GetOutputSize(data.Length)];
                int n = cipher.ProcessBytes(data, 0, data.Length, buf, 0);
                n += cipher.DoFinal(buf, n);
                return Encoding.UTF8.GetString(buf, 0, n);
            }
            catch { return ""; }   // wrong key / corrupt padding / not our blob
        }
    }

    /// <summary>Process-wide 3-strike lockout for the LaunchBox unlock PIN (reset on restart), mirroring
    /// LiteBox's ParentalFilter lockout so a wrong-guesser can't grind.</summary>
    internal static class PinLockout
    {
        private const int Max = 3;
        private static int _failed;
        private static bool _out;

        public static bool LockedOut => _out;

        /// <summary>Records a wrong attempt; returns attempts remaining (0 = just locked out).</summary>
        public static int RegisterFail()
        {
            if (_out) return 0;
            if (++_failed >= Max) { _out = true; return 0; }
            return Max - _failed;
        }

        public static void Reset() { _failed = 0; }
    }
}
