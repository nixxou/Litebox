// Decrypts BigBox's <LockPin> blob so the uninstaller can verify the entered PIN. Rijndael-256 / CBC / PKCS7
// with the fixed LaunchBox key+seed (same constants as the plugin's PinVerify and LiteBox's LbSettingsCrypto).
// BouncyCastle does the 256-bit-block cipher .NET's built-in AES can't.

using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace LiteBoxParentalInstaller;

internal static class PinCrypto
{
    private const string LockPinKey  = "7b7fdf9d179643e0be4bea45c827b693";
    private const string LockPinSeed = "cf2976b6f11c459bab7a3f2acc1795f3";

    /// <summary>Decrypt a LockPin base64 blob to the clear PIN. "" on any failure (empty / wrong / not our blob).</summary>
    public static string Decrypt(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        byte[] data;
        try { data = System.Convert.FromBase64String(b64); } catch { return ""; }
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
        catch { return ""; }
    }
}
