// Per-install HMAC key that signs and verifies the embedded server's media-proxy URLs (and the parental
// unlock cookie). It is the cryptographic gate that stops a third-party page — or a curious browser tab —
// from forging a token that would point the proxy at an arbitrary file on disk: without the key, no caller
// can produce a signature TryDecodeAndVerify accepts.
//
// Clean-room LiteBox rewrite of ExtendDB's MediaTokenSecret. Differences from the plugin version:
//   • Key location  : <LB>\Core\litebox\config\media-token.key (LiteBoxPaths), not next to a plugin DLL.
//   • NO secret literal — the 32 bytes are generated with the OS CSPRNG on first use and never shipped.
//   • DPAPI (ProtectedData, CurrentUser) at rest; the entropy string is a domain separator, not a secret
//     (its only job is to scope Unprotect to LiteBox so another app under the same account can't read the blob).
//   • Base32-Crockford codec bundled here so both the sign path and the token/sig decode share one alphabet.
//
// If the blob is missing or unreadable a fresh key is generated: any URL a browser already cached becomes
// invalid and silently re-fetches once — a cache miss, not a functional break.

#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

internal static class MediaTokenSecret
{
    /// <summary>Truncated HMAC length, in bytes. 128 bits is plenty for a forgery gate.</summary>
    public const int SignatureLength = 16;

    // DPAPI domain separator. Not a secret — see the file header. Constant so an existing blob keeps decrypting;
    // bumping the suffix is a deliberate key-format rotation.
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("LiteBox.MediaProxy.HmacSecret.v1");

    private static byte[]? _secret;
    private static readonly object _lock = new();

    /// <summary>The in-memory key. Lazy-loads the DPAPI blob on first call; generates + persists a fresh one when
    /// the blob is missing or can't be decrypted. Thread-safe.</summary>
    public static byte[] Get()
    {
        var s = _secret;
        if (s != null) return s;
        lock (_lock)
        {
            _secret ??= LoadOrGenerate();
            return _secret;
        }
    }

    private static byte[] LoadOrGenerate()
    {
        var path = KeyFilePath();

        if (System.IO.File.Exists(path))
        {
            try
            {
                var blob = System.IO.File.ReadAllBytes(path);
                var plain = ProtectedData.Unprotect(blob, _entropy, DataProtectionScope.CurrentUser);
                if (plain is { Length: >= 32 }) return plain;
                LbLog.Warn("web", $"media-token key blob has unexpected length ({plain?.Length ?? -1}), regenerating");
            }
            catch (Exception ex)
            {
                LbLog.Warn("web", $"media-token key DPAPI Unprotect failed ({ex.GetType().Name}), regenerating");
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            var blob = ProtectedData.Protect(fresh, _entropy, DataProtectionScope.CurrentUser);
            System.IO.File.WriteAllBytes(path, blob);
            LbLog.Info("web", "generated new media-token key at " + path);
        }
        catch (Exception ex)
        {
            // A write failure leaves an in-memory-only key: URLs signed this run stay valid, but the browser
            // cache won't survive a restart. Acceptable degradation.
            LbLog.Warn("web", $"media-token key write failed ({ex.GetType().Name}) — running with an ephemeral key");
        }
        return fresh;
    }

    private static string KeyFilePath()
        => Path.Combine(LiteBoxPaths.Dir("config"), "media-token.key");

    // ── HMAC ──────────────────────────────────────────────────────────────────

    /// <summary>Truncated HMAC-SHA256 of a UTF-8 payload (the Base32 token segment).</summary>
    public static byte[] Sign(string payload) => Sign(Encoding.UTF8.GetBytes(payload ?? ""));

    public static byte[] Sign(byte[] payload)
    {
        using var hmac = new HMACSHA256(Get());
        var full = hmac.ComputeHash(payload);
        var sig = new byte[SignatureLength];
        Buffer.BlockCopy(full, 0, sig, 0, SignatureLength);
        return sig;
    }

    /// <summary>Constant-time signature compare (dodges timing oracles). False on length or content mismatch.</summary>
    public static bool Verify(string payload, byte[]? candidate)
    {
        if (candidate == null || candidate.Length != SignatureLength) return false;
        return CryptographicOperations.FixedTimeEquals(Sign(payload), candidate);
    }

    // ── Signed opaque marker (parental unlock cookie) ───────────────────────────

    /// <summary>A Base32 signature of <paramref name="purpose"/> — an unforgeable per-install marker to drop in a
    /// cookie. A child can't reproduce it without the DPAPI-protected key, so it can't self-unlock.</summary>
    public static string SignedMarker(string purpose) => Base32.Encode(Sign(purpose));

    /// <summary>Constant-time check that <paramref name="value"/> is the marker <see cref="SignedMarker"/> would
    /// produce for <paramref name="purpose"/>. False on any decode failure.</summary>
    public static bool VerifyMarker(string purpose, string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try { return Verify(purpose, Base32.Decode(value)); }
        catch { return false; }
    }

    // ── Base32-Crockford (case-insensitive URL-safe encoding of the token + sig) ─
    // Kept byte-identical to the alphabet ExtendDB uses so a URL signed by either verifies against the other's
    // decoder shape — the encoding is a plain transport, the HMAC key is what actually gates.

    internal static class Base32
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;

            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = data[0];
            int next = 1;
            int bitsLeft = 8;

            while (bitsLeft > 0 || next < data.Length)
            {
                if (bitsLeft < 5)
                {
                    if (next < data.Length)
                    {
                        buffer <<= 8;
                        buffer |= data[next++] & 0xff;
                        bitsLeft += 8;
                    }
                    else
                    {
                        buffer <<= (5 - bitsLeft);
                        bitsLeft = 5;
                    }
                }
                int index = (buffer >> (bitsLeft - 5)) & 0x1f;
                bitsLeft -= 5;
                sb.Append(Alphabet[index]);
            }
            return sb.ToString();
        }

        public static byte[] Decode(string input)
        {
            if (string.IsNullOrEmpty(input)) return Array.Empty<byte>();

            var bytes = new System.Collections.Generic.List<byte>(input.Length * 5 / 8);
            int buffer = 0, bitsLeft = 0;
            foreach (char c in input)
            {
                buffer = (buffer << 5) | DecodeChar(c);
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    bytes.Add((byte)(buffer >> (bitsLeft - 8)));
                    bitsLeft -= 8;
                }
            }
            return bytes.ToArray();
        }

        // Folds case and the visually confused letters (O→0, I/L→1) per the Crockford spec.
        private static int DecodeChar(char c)
        {
            c = char.ToUpperInvariant(c);
            return c switch
            {
                >= '0' and <= '9' => c - '0',
                'O' => 0,
                'I' or 'L' => 1,
                >= 'A' and <= 'Z' => Alphabet.IndexOf(c) is var i && i >= 0
                    ? i
                    : throw new FormatException($"Invalid Base32 character: {c}"),
                _ => throw new FormatException($"Invalid Base32 character: {c}"),
            };
        }
    }
}
