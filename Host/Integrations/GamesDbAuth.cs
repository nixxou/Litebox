// GamesDbAuth — sign in to the LaunchBox Games Database (gamesdb.launchbox-app.com) and obtain the
// CloudAuthenticationToken, exactly as LaunchBox's own Connect flow does.
//
// RE'd from LB's traffic (GamesDatabase.SignIn ← ConnectForm ← CloudConnectMenuAction.OnSelect):
//     POST https://gamesdb.launchbox-app.com/launchbox/signin
//     body: email=<url-encoded>&password=<url-encoded>   (form-urlencoded, HTTPS)
//     → 200 {"Success":true,"Token":"<guid>"}   /   {"Success":false,...} on bad credentials
//
// The token is a plain GUID that LaunchBox stores IN CLEAR as Settings/CloudAuthenticationToken. Each sign-in
// mints a fresh token (server-side rotation). This client only performs a REAL sign-in with the user's OWN
// credentials on explicit action — no dummy traffic.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LbApiHost.Host.Integrations;

internal static class GamesDbAuth
{
    private const string SignInUrl = "https://gamesdb.launchbox-app.com/launchbox/signin";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal readonly record struct Result(bool Ok, string Token, string Message);

    /// <summary>Sign in with real credentials. On success returns the fresh token; on failure a human message.
    /// Never throws — network/parse errors come back as Ok=false.</summary>
    public static async Task<Result> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new Result(false, "", "Enter your LaunchBox account email and password.");
        try
        {
            using var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("email", email.Trim()),
                new KeyValuePair<string, string>("password", password),
            });
            using var resp = await Http.PostAsync(SignInUrl, body, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result(false, "", $"Server returned {(int)resp.StatusCode}.");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool success = root.TryGetProperty("Success", out var sEl) && sEl.ValueKind == JsonValueKind.True;
            string token = root.TryGetProperty("Token", out var tEl) && tEl.ValueKind == JsonValueKind.String ? (tEl.GetString() ?? "") : "";
            if (success && token.Length > 0) return new Result(true, token, "Signed in — token saved.");
            return new Result(false, "", "Sign-in failed — check your email and password.");
        }
        catch (OperationCanceledException) { return new Result(false, "", "Cancelled."); }
        catch (Exception ex) { return new Result(false, "", "Network error: " + ex.Message); }
    }

    /// <summary>Check whether a stored token is still valid — WITHOUT the password — by driving the core's clean
    /// GamesDatabase.ValidateToken(string) (read-only server check). Returns false on any failure. Run off the UI
    /// thread (it hits the network).</summary>
    public static bool ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll"));
            var gdb = asm.GetType("Unbroken.LaunchBox.Cloud.GamesDatabase");
            var m = gdb?.GetMethod("ValidateToken", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return m?.Invoke(null, new object[] { token }) is bool b && b;
        }
        catch { return false; }
    }
}
