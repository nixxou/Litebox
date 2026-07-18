// RetroAchievements account connectivity — the two live checks LiteBox needs for the RA options page
// and the token auto-renewal:
//
//   • Login(user, password)  — the RA "Connect API" login (dorequest.php?r=login&u=&p=), the SAME call
//     LaunchBox makes. Returns the session Token on success. This token is what gets written into
//     RetroArch's config (cheevos_token) so achievements pop in game — see the RetroArch integration's
//     PrepareEmulatorForLaunch. The password itself is never stored by RA; you exchange it for the token.
//   • ValidateApiKey(user, key) — a cheap authenticated Web API ping (API_GetConsoleIDs.php?z=&y=); a
//     valid key returns a JSON array of consoles, an invalid one an error object / non-2xx.
//
// Both are network calls with a short timeout, safe to call off the UI thread. They never throw — a
// failure (offline, site down, bad credentials) is reported as a non-Ok result so callers can decide
// (the renewal, notably, must keep the existing token untouched on any failure).

#nullable enable

using System;
using System.Net.Http;
using System.Text.Json;

namespace LbApiHost.Host.Ra;

internal static class RaConnect
{
    private static readonly HttpClient Http = Build();
    private static HttpClient Build()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBox/1.0 (+RetroAchievements)"); } catch { }
        return c;
    }
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    internal readonly record struct LoginResult(bool Ok, string Token, string Error)
    {
        public static LoginResult Fail(string why) => new(false, "", why);
    }

    /// <summary>Log in to RetroAchievements with a username + password and return the session token.
    /// Never throws: network / HTTP / parse failures and rejected credentials all come back as
    /// <see cref="LoginResult.Ok"/> == false with a human message.</summary>
    public static LoginResult Login(string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            return LoginResult.Fail("Username and password are required.");
        try
        {
            string url = "https://retroachievements.org/dorequest.php?r=login"
                       + $"&u={Uri.EscapeDataString(user!.Trim())}&p={Uri.EscapeDataString(password!)}";
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return LoginResult.Fail($"HTTP {(int)resp.StatusCode}.");
            if (string.IsNullOrWhiteSpace(body))
                return LoginResult.Fail("Empty response.");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool success = root.TryGetProperty("Success", out var s) &&
                (s.ValueKind == JsonValueKind.True
                 || (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) && n == 1)
                 || (s.ValueKind == JsonValueKind.String && string.Equals(s.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            if (!success)
            {
                string err = root.TryGetProperty("Error", out var e) ? (e.GetString() ?? "") : "";
                return LoginResult.Fail(string.IsNullOrEmpty(err) ? "Username or password is incorrect." : err);
            }
            string token = root.TryGetProperty("Token", out var t) ? (t.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(token))
                return LoginResult.Fail("Logged in but no token returned.");
            return new LoginResult(true, token, "");
        }
        catch (Exception ex) { return LoginResult.Fail(ex.Message); }
    }

    /// <summary>True when the Web API key is accepted for this username (a valid key returns a JSON
    /// array of consoles). Never throws — any failure returns false.</summary>
    public static bool ValidateApiKey(string? user, string? key)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            string url = "https://retroachievements.org/API/API_GetConsoleIDs.php"
                       + $"?z={Uri.EscapeDataString(user!.Trim())}&y={Uri.EscapeDataString(key!.Trim())}";
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return false;
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(body)) return false;
            body = body.TrimStart();
            if (body[0] != '[') return false;                 // an error object ({"error":…}) is not a valid key
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch { return false; }
    }
}
