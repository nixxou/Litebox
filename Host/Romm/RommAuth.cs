// Authentication for the RomM surface: who is asking, and what they are allowed to ask for.
//
// Four ways in, all of which real clients use:
//   • HTTP Basic          — the simplest, and what a "just type the URL and password" client does.
//   • OAuth2 bearer (JWT) — POST /api/token, 30 min access + 7 day refresh. Argosy's path.
//   • Client token        — "Bearer rmm_<64 hex>", created here and paired onto a handheld with a code.
//   • Session cookie      — POST /api/login, for a client that drives the API from a browser context.
//
// All four resolve to the SAME single account, because LiteBox has one user. What varies is the scope set:
// a client token can be narrowed, so a handheld that only needs to download and sync saves can be given a
// token that cannot touch anything else.
//
// CSRF is not implemented, and does not need to be: upstream skips it whenever an Authorization header is
// present, and the session path here issues a token cookie for clients that expect to read one without
// making it a gate. There is no TLS on this surface — the password is the wall (see RommServer).

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

/// <summary>RomM's scope strings. Ours is a single ADMIN account, so the full set is what a password login
/// gets; a client token may carry any subset.</summary>
internal static class RommScopes
{
    public const string MeRead = "me.read";
    public const string MeWrite = "me.write";
    public const string RomsRead = "roms.read";
    public const string RomsWrite = "roms.write";
    public const string RomsUserRead = "roms.user.read";
    public const string RomsUserWrite = "roms.user.write";
    public const string PlatformsRead = "platforms.read";
    public const string PlatformsWrite = "platforms.write";
    public const string AssetsRead = "assets.read";
    public const string AssetsWrite = "assets.write";
    public const string DevicesRead = "devices.read";
    public const string DevicesWrite = "devices.write";
    public const string FirmwareRead = "firmware.read";
    public const string FirmwareWrite = "firmware.write";
    public const string CollectionsRead = "collections.read";
    public const string CollectionsWrite = "collections.write";
    public const string PlaylistsRead = "playlists.read";
    public const string PlaylistsWrite = "playlists.write";
    public const string UsersRead = "users.read";
    public const string UsersWrite = "users.write";

    /// <summary>What this surface actually grants. The write scopes it does NOT list are the ones the
    /// module refuses by design: roms.write beyond user properties, platforms.write, firmware.write,
    /// users.write, tasks.run. Advertising a scope we would then refuse would be a lie a client acts on.</summary>
    public static readonly string[] Granted =
    {
        MeRead, MeWrite,
        RomsRead, RomsUserRead, RomsUserWrite,
        PlatformsRead,
        AssetsRead, AssetsWrite,
        DevicesRead, DevicesWrite,
        FirmwareRead,
        CollectionsRead, CollectionsWrite,
        PlaylistsRead,
        UsersRead,
    };

    public static string Joined => string.Join(" ", Granted);

    /// <summary>Every scope string RomM defines. A client may legitimately ASK for one we do not grant
    /// (Argosy requests the full set); the request is valid, the answer is the intersection with
    /// <see cref="Granted"/>. Validating an init payload against Granted instead would reject a
    /// well-behaved client outright.</summary>
    public static readonly string[] All =
    {
        MeRead, MeWrite, RomsRead, RomsWrite, RomsUserRead, RomsUserWrite,
        PlatformsRead, PlatformsWrite, AssetsRead, AssetsWrite,
        DevicesRead, DevicesWrite, FirmwareRead, FirmwareWrite,
        CollectionsRead, CollectionsWrite, PlaylistsRead, PlaylistsWrite,
        UsersRead, UsersWrite, "tasks.run", "logs.read",
    };
}

/// <summary>Who is making this request, and with which scopes.</summary>
internal sealed class RommIdentity
{
    public string Username { get; init; } = "";
    public HashSet<string> Scopes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>How they authenticated — "basic", "bearer", "token" or "session". Logged, not enforced.</summary>
    public string Method { get; init; } = "";

    /// <summary>The client token's id when Method is "token", so a device can be attributed.</summary>
    public int? TokenId { get; init; }

    public bool Has(string scope) => Scopes.Contains(scope);
}

/// <summary>One issued client token. The secret is never stored — only its SHA-256.</summary>
internal sealed class RommClientToken
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Scopes { get; set; } = "";          // space-separated
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>The device this token was minted for, when it came from the pairing flow.</summary>
    public string? DeviceId { get; set; }
}

internal static class RommAuth
{
    private const string TokensKey = "Romm.ClientTokens";
    private const string TokenPrefix = "rmm_";

    public static readonly TimeSpan AccessTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan PairTtl = TimeSpan.FromMinutes(5);

    // ── The identity of a request ─────────────────────────────────────────────

    /// <summary>Resolves the caller, or null when the request carries no valid credential. A server with no
    /// password set authenticates nobody — an unprotected library on the LAN is never a default.</summary>
    public static RommIdentity? Authenticate(HttpRequest req)
    {
        if (req == null) return null;
        if (!RommConfig.HasPassword) return null;

        var auth = req.GetHeader("Authorization") ?? "";

        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return FromBasic(auth.Substring(6).Trim());

        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = auth.Substring(7).Trim();
            return value.StartsWith(TokenPrefix, StringComparison.Ordinal)
                ? FromClientToken(value)
                : FromAccessToken(value);
        }

        var cookie = req.GetCookie("romm_session");
        if (!string.IsNullOrEmpty(cookie)) return FromSession(cookie!);

        return null;
    }

    private static RommIdentity? FromBasic(string encoded)
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            int colon = raw.IndexOf(':');
            if (colon < 0) return null;
            var user = raw.Substring(0, colon);
            var pass = raw.Substring(colon + 1);
            if (!VerifyLogin(user, pass)) return null;
            return Full("basic");
        }
        catch { return null; }
    }

    /// <summary>Checks a user name + password pair against the configured account.</summary>
    public static bool VerifyLogin(string? username, string? password)
        => string.Equals(username, RommConfig.Username, StringComparison.OrdinalIgnoreCase)
           && RommConfig.VerifyPassword(password);

    private static RommIdentity Full(string method) => new()
    {
        Username = RommConfig.Username,
        Scopes = new HashSet<string>(RommScopes.Granted, StringComparer.Ordinal),
        Method = method,
    };

    // ── JWT (HS256), self-issued and self-verified ────────────────────────────

    public static string CreateAccessToken(IEnumerable<string> scopes)
        => CreateToken("access", scopes, AccessTtl);

    public static string CreateRefreshToken(IEnumerable<string> scopes)
        => CreateToken("refresh", scopes, RefreshTtl);

    private static string CreateToken(string type, IEnumerable<string> scopes, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "romm:oauth",
            ["sub"] = RommConfig.Username,
            ["type"] = type,
            ["scopes"] = string.Join(" ", scopes),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(ttl).ToUnixTimeSeconds(),
        });

        var signingInput = B64(Encoding.UTF8.GetBytes(header)) + "." + B64(Encoding.UTF8.GetBytes(payload));
        var sig = Hmac(signingInput);
        return signingInput + "." + B64(sig);
    }

    private static RommIdentity? FromAccessToken(string jwt) => FromJwt(jwt, "access");

    /// <summary>Validates a refresh token and returns its scopes, or null.</summary>
    public static string[]? ValidateRefreshToken(string jwt)
    {
        var id = FromJwt(jwt, "refresh");
        return id?.Scopes.ToArray();
    }

    private static RommIdentity? FromJwt(string jwt, string expectedType)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            var expected = Hmac(parts[0] + "." + parts[1]);
            var actual = UnB64(parts[2]);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(UnB64(parts[1])));
            var root = doc.RootElement;

            if (root.GetProperty("iss").GetString() != "romm:oauth") return null;
            if (root.GetProperty("type").GetString() != expectedType) return null;
            if (root.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;

            // A token issued for a DIFFERENT account name is not ours: renaming the account has to
            // invalidate what was issued under the old one.
            var sub = root.GetProperty("sub").GetString() ?? "";
            if (!string.Equals(sub, RommConfig.Username, StringComparison.OrdinalIgnoreCase)) return null;

            var scopes = (root.TryGetProperty("scopes", out var s) ? s.GetString() : "") ?? "";
            return new RommIdentity
            {
                Username = sub,
                Scopes = new HashSet<string>(SplitScopes(scopes), StringComparer.Ordinal),
                Method = "bearer",
            };
        }
        catch { return null; }
    }

    private static byte[] Hmac(string input)
    {
        using var h = new HMACSHA256(RommConfig.SigningKey);
        return h.ComputeHash(Encoding.ASCII.GetBytes(input));
    }

    private static string B64(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }

    /// <summary>Splits a scope string, keeping only scopes this surface actually grants — a client that
    /// asks for more gets what exists, never a promise we would refuse.</summary>
    public static string[] SplitScopes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw!.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(s => Array.IndexOf(RommScopes.Granted, s) >= 0)
                   .Distinct(StringComparer.Ordinal)
                   .ToArray();
    }

    // ── Client tokens ─────────────────────────────────────────────────────────

    public static List<RommClientToken> ListTokens()
    {
        try
        {
            var raw = LiteBoxOptionsDb.GetGlobal(TokensKey);
            if (string.IsNullOrEmpty(raw)) return new List<RommClientToken>();
            return JsonSerializer.Deserialize<List<RommClientToken>>(raw!) ?? new List<RommClientToken>();
        }
        catch { return new List<RommClientToken>(); }
    }

    private static void SaveTokens(List<RommClientToken> tokens)
    {
        try { LiteBoxOptionsDb.SetGlobal(TokensKey, JsonSerializer.Serialize(tokens)); }
        catch (Exception ex) { LbLog.Warn("romm", "token store failed: " + ex.Message); }
    }

    /// <summary>Mints a token. The secret is returned ONCE — only its hash is kept, so a lost token is
    /// regenerated, never recovered.</summary>
    public static (RommClientToken record, string secret) CreateClientToken(string name, IEnumerable<string>? scopes = null, DateTime? expiresUtc = null, string? deviceId = null)
    {
        var secret = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var all = ListTokens();
        var record = new RommClientToken
        {
            Id = all.Count == 0 ? 1 : all.Max(t => t.Id) + 1,
            Name = string.IsNullOrWhiteSpace(name) ? "client" : name.Trim(),
            Sha256 = HashToken(secret),
            Scopes = string.Join(" ", scopes ?? RommScopes.Granted),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = expiresUtc,
            DeviceId = deviceId,
        };
        all.Add(record);
        SaveTokens(all);
        return (record, secret);
    }

    public static bool DeleteToken(int id)
    {
        var all = ListTokens();
        int removed = all.RemoveAll(t => t.Id == id);
        if (removed > 0) SaveTokens(all);
        return removed > 0;
    }

    /// <summary>Replaces a token's secret, keeping its id, name and scopes — what "regenerate" means.</summary>
    public static string? RegenerateToken(int id)
    {
        var all = ListTokens();
        var t = all.FirstOrDefault(x => x.Id == id);
        if (t == null) return null;

        var secret = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        t.Sha256 = HashToken(secret);
        t.LastUsedUtc = null;
        SaveTokens(all);
        return secret;
    }

    private static string HashToken(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret))).ToLowerInvariant();

    private static RommIdentity? FromClientToken(string secret)
    {
        var hash = HashToken(secret);
        var all = ListTokens();
        // Compared in constant time against every candidate: bailing out early on the first mismatched
        // byte would leak which prefix is close.
        RommClientToken? found = null;
        var wanted = Encoding.ASCII.GetBytes(hash);
        foreach (var t in all)
        {
            var candidate = Encoding.ASCII.GetBytes(t.Sha256 ?? "");
            if (candidate.Length == wanted.Length && CryptographicOperations.FixedTimeEquals(candidate, wanted))
                found = t;
        }
        if (found == null) return null;
        if (found.ExpiresUtc != null && found.ExpiresUtc <= DateTime.UtcNow) return null;

        // Last-used is a convenience for the options page, not a security record — a write per request
        // would be silly, so it lands at most once a minute.
        if (found.LastUsedUtc == null || (DateTime.UtcNow - found.LastUsedUtc.Value).TotalMinutes >= 1)
        {
            found.LastUsedUtc = DateTime.UtcNow;
            SaveTokens(all);
        }

        return new RommIdentity
        {
            Username = RommConfig.Username,
            Scopes = new HashSet<string>(SplitScopes(found.Scopes), StringComparer.Ordinal),
            Method = "token",
            TokenId = found.Id,
        };
    }

    // ── Pairing: enrol a handheld without typing a password on it ─────────────

    private static readonly Dictionary<string, (int TokenId, DateTime ExpiresUtc)> _pairCodes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string Secret, DateTime ExpiresUtc)> _pairSecrets = new(StringComparer.Ordinal);
    private static readonly object _pairLock = new();

    /// <summary>Issues a short code that stands in for a token for a few minutes. The device exchanges it
    /// for the secret; nothing but the code ever travels to the device before it is trusted.
    ///
    /// EIGHT DECIMAL DIGITS, which is not a free choice: RomM's own pairing code is eight digits and the
    /// clients size their input field for it. This used to mint six hex characters, and a code like
    /// "A3F91C" does not merely fail to authenticate — it does not fit in the box, so the client refuses
    /// it before a single request is made. GetInt32 draws uniformly over the whole range, so leading
    /// zeros occur as often as any other digit and "00123456" is a perfectly ordinary code.</summary>
    public static string CreatePairCode(int tokenId, string secret)
    {
        var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        lock (_pairLock)
        {
            Prune();
            _pairCodes[code] = (tokenId, DateTime.UtcNow.Add(PairTtl));
            _pairSecrets[code] = (secret, DateTime.UtcNow.Add(PairTtl));
        }
        return code;
    }

    /// <summary>True while a code is outstanding — what the issuing client polls.</summary>
    public static bool PairCodePending(string code)
    {
        lock (_pairLock) { Prune(); return _pairCodes.ContainsKey(code); }
    }

    /// <summary>Redeems a code exactly once, returning the token secret.</summary>
    public static string? RedeemPairCode(string code)
    {
        lock (_pairLock)
        {
            Prune();
            if (!_pairSecrets.TryGetValue(code, out var entry)) return null;
            _pairSecrets.Remove(code);
            _pairCodes.Remove(code);
            return entry.Secret;
        }
    }

    private static void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _pairCodes.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList())
            _pairCodes.Remove(k);
        foreach (var k in _pairSecrets.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList())
            _pairSecrets.Remove(k);
    }

    // ── Sessions ──────────────────────────────────────────────────────────────
    //
    // In memory, not in the options DB: a session is worth exactly one run of LiteBox, and persisting it
    // would outlive the reason it was granted.

    private static readonly Dictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private static readonly object _sessionLock = new();

    public static string CreateSession()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (_sessionLock)
        {
            PruneSessions();
            _sessions[id] = DateTime.UtcNow.Add(SessionTtl);
        }
        return id;
    }

    public static void DropSession(string id)
    {
        lock (_sessionLock) _sessions.Remove(id);
    }

    /// <summary>Invalidates every session — the counterpart of rotating the signing key.</summary>
    public static void DropAllSessions()
    {
        lock (_sessionLock) _sessions.Clear();
    }

    private static RommIdentity? FromSession(string id)
    {
        lock (_sessionLock)
        {
            PruneSessions();
            if (!_sessions.TryGetValue(id, out var expiry) || expiry <= DateTime.UtcNow) return null;
        }
        return Full("session");
    }

    private static void PruneSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _sessions.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _sessions.Remove(k);
    }
}
