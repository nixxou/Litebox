// Snapshot of the LiteBox.ini [RommServer] section, plus the secrets that must never sit in an ini.
//
// Split on purpose: the knobs a user edits (port, LAN allow-list, account name, what the library exposes)
// live in LiteBox.ini like every other module's settings; the password verifier and the token-signing key
// live in litebox-options.db, generated per install and never displayed. MediaTokenSecret is the precedent.
//
// The whole-server gate stays LbModules.On(LbModule.RommServer); these keys only shape how it answers.

#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Romm;

internal static class RommConfig
{
    private const string Section = "RommServer";

    /// <summary>The RomM release whose wire contract this surface implements. Clients branch on it, so it
    /// names a real upstream version rather than a LiteBox one.</summary>
    public const string RommVersion = "5.2.0";

    /// <summary>TCP listen port ([RommServer] Port, default 8998). Its own listener: the theme server's
    /// database site already owns /api/platforms and /api/search, which RomM needs for itself.</summary>
    public static int Port { get; private set; } = 8998;

    /// <summary>Comma/space wildcard IP patterns ([RommServer] AllowedIps). Non-empty ⇒ bind 0.0.0.0 +
    /// per-connection filter (loopback always allowed); empty ⇒ loopback-only bind.</summary>
    public static string AllowedIps { get; private set; } = "";

    /// <summary>The single account's name ([RommServer] Username, default "litebox").</summary>
    public static string Username { get; private set; } = "litebox";

    /// <summary>Serve games flagged Hidden in LaunchBox ([RommServer] ExposeHiddenGames, default false).</summary>
    public static bool ExposeHiddenGames { get; private set; }

    /// <summary>The LaunchBox platforms served to clients ([RommServer] IncludedPlatforms, pipe-separated
    /// names). DEFAULT IS NONE: a fresh install serves an empty library until platforms are ticked in the
    /// module page. Exclusion is a serving gate only — romm ids, assignments and client pins stay in
    /// romm.db untouched, so re-including a platform restores exactly what the clients had.</summary>
    public static HashSet<string> IncludedPlatforms { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool PlatformIncluded(string? lbPlatformName)
        => !string.IsNullOrEmpty(lbPlatformName) && IncludedPlatforms.Contains(lbPlatformName);

    /// <summary>Serve games the parental lock hides ([RommServer] IgnoreParental, default false). Off means
    /// a locked library looks the same to a phone as it does to the TV.</summary>
    public static bool IgnoreParental { get; private set; }

    /// <summary>Write one line per request to Core\litebox\romm-requests.log ([RommServer] LogRequests,
    /// default off). A debugging instrument: it records which ROMs a client asks for, so it stays off
    /// unless somebody is actually looking.</summary>
    public static bool LogRequests { get; private set; }

    /// <summary>Also record each exchange IN FULL — headers and bodies, both ways ([RommServer]
    /// LogBodies, default false; implies LogRequests).
    ///
    /// A heavy instrument, and deliberately so: it writes what a client sent and what it was answered,
    /// which is the only way to settle "the server sent the wrong thing" against "the client asked for
    /// the wrong thing". Credentials are redacted and binary payloads are described, never dumped — a
    /// download would otherwise put megabytes of ROM in a text file.</summary>
    public static bool LogBodies { get; private set; }

    // ── Two throwaway probes ──────────────────────────────────────────────────
    //
    // Both exist to answer one question: what makes a client drop its cached copy of the library?
    // Reading Freegosy's source says the per-platform view compares rom_count and nothing else -- no
    // date is consulted -- but that is a deduction, and these turn it into a measurement. Delete both
    // once the answer is written down.


    /// <summary>Added to every platform's rom_count ([RommServer] DebugBumpRomCount, default 0). A client
    /// that keys its cache on the count should notice one refresh and then settle -- it stores the new
    /// count. Bump it again for another round; the value is read at startup.</summary>
    public static int DebugBumpRomCount { get; private set; }

    /// <summary>Re-read the [RommServer] section. Failures leave the last good values in place.</summary>
    public static void Reload()
    {
        try
        {
            var c = LiteBoxConfig.LoadForExe();
            Port = ParsePort(c.GetSec(Section, "Port"), 8998);
            AllowedIps = c.GetSec(Section, "AllowedIps", "") ?? "";
            var user = (c.GetSec(Section, "Username", "") ?? "").Trim();
            Username = user.Length > 0 ? user : "litebox";
            ExposeHiddenGames = c.GetSecBool(Section, "ExposeHiddenGames", false);
            IncludedPlatforms = new HashSet<string>(
                (c.GetSec(Section, "IncludedPlatforms", "") ?? "")
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            IgnoreParental = c.GetSecBool(Section, "IgnoreParental", false);
            LogRequests = c.GetSecBool(Section, "LogRequests", false);
            LogBodies = c.GetSecBool(Section, "LogBodies", false);
            DebugBumpRomCount = ParseCount(c.GetSec(Section, "DebugBumpRomCount"), 0);
        }
        catch (Exception ex) { LbLog.Warn("romm", "config reload failed: " + ex.Message); }
    }

    private static int ParsePort(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), out var p) && p is > 0 and <= 65535 ? p : fallback;

    /// <summary>A non-negative count, where 0 means "no limit". A blank or malformed value keeps the
    /// default rather than silently becoming 0 — an unreadable setting must not lift a cap.</summary>
    private static int ParseCount(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), out var n) && n >= 0 ? n : fallback;

    // ── The account password ──────────────────────────────────────────────────
    //
    // PBKDF2-SHA256 rather than RomM's bcrypt: nobody but us ever verifies these hashes, so matching
    // upstream's algorithm would buy nothing and cost a dependency.

    private const string PasswordKey = "Romm.PasswordHash";
    private const int PbkdfIterations = 100_000;

    /// <summary>True once a password has been set. Without one the server refuses every request — an open
    /// library on the LAN is not a default anyone should get by accident.</summary>
    public static bool HasPassword
    {
        get
        {
            try { return !string.IsNullOrEmpty(LiteBoxOptionsDb.GetGlobal(PasswordKey)); }
            catch { return false; }
        }
    }

    /// <summary>Stores the verifier for <paramref name="plain"/>. An empty value clears it (and with it,
    /// every way in).</summary>
    public static void SetPassword(string? plain)
    {
        try
        {
            if (string.IsNullOrEmpty(plain)) { LiteBoxOptionsDb.SetGlobal(PasswordKey, null); return; }

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Derive(plain!, salt);
            var encoded = $"pbkdf2${PbkdfIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            LiteBoxOptionsDb.SetGlobal(PasswordKey, encoded);
        }
        catch (Exception ex) { LbLog.Warn("romm", "password store failed: " + ex.Message); }
    }

    /// <summary>Constant-time check of <paramref name="plain"/> against the stored verifier.</summary>
    public static bool VerifyPassword(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return false;
        try
        {
            var stored = LiteBoxOptionsDb.GetGlobal(PasswordKey);
            if (string.IsNullOrEmpty(stored)) return false;

            var parts = stored!.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            if (!int.TryParse(parts[1], out int iters) || iters <= 0) return false;

            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Derive(plain!, salt, iters, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    private static byte[] Derive(string plain, byte[] salt, int iterations = PbkdfIterations, int length = 32)
    {
        using var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(plain), salt, iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(length);
    }

    // ── The token-signing key ─────────────────────────────────────────────────

    private const string SigningKeyKey = "Romm.SigningKey";
    private static byte[]? _signingKey;

    /// <summary>HS256 key for the access / refresh tokens, generated once per install and kept in the
    /// options DB. Rotating it invalidates every issued token, which is what "sign everyone out" means.</summary>
    public static byte[] SigningKey
    {
        get
        {
            if (_signingKey != null) return _signingKey;
            try
            {
                var stored = LiteBoxOptionsDb.GetGlobal(SigningKeyKey);
                if (!string.IsNullOrEmpty(stored))
                {
                    _signingKey = Convert.FromBase64String(stored!);
                    return _signingKey;
                }
            }
            catch { }

            var fresh = RandomNumberGenerator.GetBytes(32);
            try { LiteBoxOptionsDb.SetGlobal(SigningKeyKey, Convert.ToBase64String(fresh)); } catch { }
            _signingKey = fresh;
            return fresh;
        }
    }

    /// <summary>Drops the signing key so a new one is generated — every access and refresh token dies.</summary>
    public static void RotateSigningKey()
    {
        _signingKey = null;
        try { LiteBoxOptionsDb.SetGlobal(SigningKeyKey, null); } catch { }
    }
}
