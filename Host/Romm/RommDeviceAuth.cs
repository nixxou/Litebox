// The RFC 8628-style device pairing flow — what Argosy (and any RomM client with a QR pairing screen)
// runs the moment you type the server address.
//
//   1. the device POSTs /api/auth/device/init with its identity + the scopes it wants, and gets back a
//      secret device_code (for polling), a short human user_code (for the QR), and a verification path;
//   2. a human opens that path, authenticates with the account, and approves;
//   3. the device's next poll on /api/auth/device/token returns a client token bound to a Device record.
//
// Steps 1 and 3 are UNAUTHENTICATED by necessity — the device has no credential yet — which is exactly why
// the approval in step 2 is the whole security of the flow and can never be automatic. A LAN peer that
// guesses nothing still needs somebody at the keyboard to say yes.
//
// State is in memory with a hard 10-minute TTL, like upstream's Redis keys: a pairing that outlives one
// run of LiteBox is not a pairing anyone is still watching.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace LbApiHost.Host.Romm;

internal static class RommDeviceAuth
{
    public const int PendingTtlSeconds = 600;      // 10-minute ceiling (upstream's PENDING_TTL_SECONDS)
    public const int PollIntervalSeconds = 5;
    private const int UserCodeLength = 8;

    /// <summary>Upstream's confusable-free alphabet: no I/L/O/0/1, because a human reads this off a
    /// phone screen and types it on a keyboard.</summary>
    private const string PairAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const string StatusPending = "pending";
    public const string StatusApproved = "approved";
    public const string StatusDenied = "denied";

    internal sealed class Pending
    {
        public string DeviceCode = "";
        public string UserCode = "";
        public string ClientDeviceIdentifier = "";
        public string Name = "";
        public string Client = "";
        public string? Platform;
        public string? ClientVersion;
        public string[] RequestedScopes = Array.Empty<string>();
        public string Status = StatusPending;
        public DateTime ExpiresUtc;
        public DateTime? LastPollUtc;

        // Filled on approval, consumed once by the device's poll.
        public string? RawToken;
        public string? DeviceId;
        public string[] ApprovedScopes = Array.Empty<string>();
        public DateTime? TokenExpiresUtc;
    }

    private static readonly Dictionary<string, Pending> _byDeviceCode = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    /// <summary>Strips separators and uppercases a user-typed code — upstream's normalize_user_code.</summary>
    public static string NormalizeUserCode(string? code)
        => (code ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

    public static Pending Start(string clientDeviceIdentifier, string name, string client,
                                string? platform, string? clientVersion, string[] requestedScopes)
    {
        lock (_lock)
        {
            Prune();
            var p = new Pending
            {
                DeviceCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                UserCode = NewUserCode(),
                ClientDeviceIdentifier = clientDeviceIdentifier,
                Name = name,
                Client = client,
                Platform = platform,
                ClientVersion = clientVersion,
                RequestedScopes = requestedScopes,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(PendingTtlSeconds),
            };
            _byDeviceCode[p.DeviceCode] = p;
            return p;
        }
    }

    private static string NewUserCode()
    {
        // Retried on the (vanishingly unlikely) collision with a live code rather than trusted blindly:
        // two devices sharing a user code would let one approve the other.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var chars = new char[UserCodeLength];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = PairAlphabet[RandomNumberGenerator.GetInt32(PairAlphabet.Length)];
            var code = new string(chars);
            if (!_byDeviceCode.Values.Any(p => p.UserCode == code)) return code;
        }
        return new string(Enumerable.Range(0, UserCodeLength)
            .Select(_ => PairAlphabet[RandomNumberGenerator.GetInt32(PairAlphabet.Length)]).ToArray());
    }

    public static Pending? ByDeviceCode(string? deviceCode)
    {
        if (string.IsNullOrEmpty(deviceCode)) return null;
        lock (_lock)
        {
            Prune();
            return _byDeviceCode.TryGetValue(deviceCode!, out var p) ? p : null;
        }
    }

    public static Pending? ByUserCode(string? userCode)
    {
        var norm = NormalizeUserCode(userCode);
        if (norm.Length == 0) return null;
        lock (_lock)
        {
            Prune();
            return _byDeviceCode.Values.FirstOrDefault(p => p.UserCode == norm);
        }
    }

    /// <summary>Every flow still waiting for a human — what the approval page lists when it is opened
    /// without a code (a device that showed a QR the user could not scan is still findable).</summary>
    public static List<Pending> PendingFlows()
    {
        lock (_lock)
        {
            Prune();
            return _byDeviceCode.Values.Where(p => p.Status == StatusPending).ToList();
        }
    }

    public static void MarkApproved(Pending p, string rawToken, string deviceId, string[] scopes, DateTime? tokenExpiresUtc)
    {
        lock (_lock)
        {
            p.Status = StatusApproved;
            p.RawToken = rawToken;
            p.DeviceId = deviceId;
            p.ApprovedScopes = scopes;
            p.TokenExpiresUtc = tokenExpiresUtc;
        }
    }

    public static void MarkDenied(Pending p)
    {
        lock (_lock)
        {
            p.Status = StatusDenied;
            // Shrink the window after an explicit no — nothing is coming to collect it.
            p.ExpiresUtc = DateTime.UtcNow.AddSeconds(60);
        }
    }

    /// <summary>Hands the approved token to the device exactly once, then drops the flow.</summary>
    public static Pending? ConsumeApproved(Pending p)
    {
        lock (_lock)
        {
            if (p.Status != StatusApproved || p.RawToken == null) return null;
            _byDeviceCode.Remove(p.DeviceCode);
            return p;
        }
    }

    /// <summary>True when this device polled more often than the interval it was told to use.</summary>
    public static bool PolledTooFast(Pending p)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            bool tooFast = p.LastPollUtc != null && (now - p.LastPollUtc.Value).TotalSeconds < PollIntervalSeconds;
            p.LastPollUtc = now;
            return tooFast;
        }
    }

    private static void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _byDeviceCode.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList())
            _byDeviceCode.Remove(k);
    }
}
