// The authentication endpoints, and the scope gate every other handler goes through.
//
//   POST /api/login                             HTTP Basic → romm_session cookie
//   POST /api/logout                            drops it
//   POST /api/token                             OAuth2 password / refresh_token grant → JWT pair
//   GET  /api/users, /api/users/me, /api/users/{id}, /api/users/identifiers
//   POST/GET/DELETE/PUT /api/client-tokens…     mint, list, revoke, regenerate, pair, exchange
//
// The single account is user id 1 with role ADMIN, because that is the only shape a RomM client
// understands and LiteBox has exactly one user. What a caller may actually DO is decided by scopes, not by
// the role: a client token can be narrowed, and the scopes this surface never grants (roms.write,
// platforms.write, users.write, tasks.run) are simply not in the set — so a refusal is consistent with
// what /api/users/me told the client it had.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

internal static class RommAuthApi
{
    /// <summary>The one account's id. Fixed: clients persist it alongside their saves.</summary>
    public const int UserId = 1;

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>Resolves the caller and checks a scope. On success <paramref name="identity"/> is set and
    /// the return is null; otherwise it is the response to send back.</summary>
    public static HttpResponse? Require(RouteContext ctx, string scope, out RommIdentity identity)
    {
        identity = null!;

        if (!RommConfig.HasPassword)
            return Unauthorized("No account is configured on this server");

        var id = RommAuth.Authenticate(ctx.Request);
        if (id == null) return Unauthorized("Not authenticated");

        if (!id.Has(scope))
            return RommApi.Error(403, $"This credential does not carry the {scope} scope");

        identity = id;
        return null;
    }

    private static HttpResponse Unauthorized(string detail)
    {
        var r = RommApi.Error(401, detail);
        // Without this a browser never offers the password box, and curl -u never retries.
        r.Headers["WWW-Authenticate"] = "Basic realm=\"LiteBox RomM\", charset=\"UTF-8\"";
        return r;
    }

    private static bool IsPost(RouteContext ctx) => Is(ctx, "POST");
    private static bool Is(RouteContext ctx, string method)
        => string.Equals(ctx.Request?.Method, method, StringComparison.OrdinalIgnoreCase);

    // ── Session login ─────────────────────────────────────────────────────────

    public static HttpResponse Login(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");

        var auth = ctx.Request?.GetHeader("Authorization") ?? "";
        if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Basic credentials required");

        string user, pass;
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Substring(6).Trim()));
            int colon = raw.IndexOf(':');
            if (colon < 0) return Unauthorized("Malformed credentials");
            user = raw.Substring(0, colon);
            pass = raw.Substring(colon + 1);
        }
        catch { return Unauthorized("Malformed credentials"); }

        if (!RommAuth.VerifyLogin(user, pass)) return Unauthorized("Invalid credentials");

        var session = RommAuth.CreateSession();
        var resp = RommApi.Json(new { msg = "Successfully logged in" });
        resp.SetCookie("romm_session", session, (int)TimeSpan.FromDays(14).TotalSeconds, httpOnly: true);
        return resp;
    }

    public static HttpResponse Logout(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");
        var cookie = ctx.Request?.GetCookie("romm_session");
        if (!string.IsNullOrEmpty(cookie)) RommAuth.DropSession(cookie!);
        var resp = RommApi.Json(new { msg = "Successfully logged out" });
        resp.ClearCookie("romm_session");
        return resp;
    }

    // ── OAuth2 token ──────────────────────────────────────────────────────────

    public static HttpResponse Token(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");
        var form = ctx.Request?.Form ?? new Dictionary<string, string>();

        var grant = form.TryGetValue("grant_type", out var g) ? g : "password";
        switch (grant)
        {
            case "refresh_token":
            {
                if (!form.TryGetValue("refresh_token", out var refresh) || string.IsNullOrEmpty(refresh))
                    return RommApi.Error(400, "Missing refresh token");
                var scopes = RommAuth.ValidateRefreshToken(refresh);
                if (scopes == null) return Unauthorized("Invalid refresh token");
                // No new refresh token: rotating it here would invalidate a client that retries the same
                // request, and upstream does not rotate either.
                return TokenPair(scopes, includeRefresh: false);
            }

            case "password":
            {
                form.TryGetValue("username", out var user);
                form.TryGetValue("password", out var pass);
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                    return RommApi.Error(400, "Missing username or password");
                if (!RommAuth.VerifyLogin(user, pass)) return Unauthorized("Invalid credentials");

                // An empty scope request means "everything you have" — that is how OAuth2 clients ask.
                var wanted = form.TryGetValue("scope", out var sc) && !string.IsNullOrWhiteSpace(sc)
                    ? RommAuth.SplitScopes(sc)
                    : RommScopes.Granted;
                return TokenPair(wanted, includeRefresh: true);
            }

            default:
                return RommApi.Error(400, $"Unsupported grant type: {grant}");
        }
    }

    private static HttpResponse TokenPair(IReadOnlyCollection<string> scopes, bool includeRefresh)
    {
        var access = RommAuth.CreateAccessToken(scopes);
        var expires = (int)RommAuth.AccessTtl.TotalSeconds;

        if (!includeRefresh)
            return RommApi.Json(new { access_token = access, token_type = "bearer", expires });

        return RommApi.Json(new
        {
            access_token = access,
            token_type = "bearer",
            expires,
            refresh_token = RommAuth.CreateRefreshToken(scopes),
            refresh_expires = (int)RommAuth.RefreshTtl.TotalSeconds,
        });
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    public static HttpResponse Me(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.MeRead, out var id);
        if (refused != null) return refused;
        return RommApi.Json(UserDto(id));
    }

    public static HttpResponse Users(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.UsersRead, out var id);
        if (refused != null) return refused;
        return RommApi.Json(new[] { UserDto(id) });
    }

    public static HttpResponse UserById(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.UsersRead, out var id);
        if (refused != null) return refused;
        return ctx.GetRouteInt("id", -1) == UserId
            ? RommApi.Json(UserDto(id))
            : RommApi.Error(404, "User not found");
    }

    public static HttpResponse UserIdentifiers(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.UsersRead, out _);
        if (refused != null) return refused;
        return RommApi.Json(new[] { UserId });
    }

    private static object UserDto(RommIdentity id) => new
    {
        id = UserId,
        username = id.Username,
        email = (string?)null,
        enabled = true,
        role = "admin",
        permission_group_id = (int?)null,
        oauth_scopes = id.Scopes.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        avatar_path = "",
        last_login = Iso(DateTime.UtcNow),
        last_active = Iso(DateTime.UtcNow),
        ra_username = (string?)null,
        ra_progression = (object?)null,
        ui_settings = new Dictionary<string, object>(),
        current_device_id = (string?)null,
        created_at = Iso(DateTime.UnixEpoch),
        updated_at = Iso(DateTime.UtcNow),
    };

    /// <summary>The datetime shape RomM emits (UTC, ISO 8601 with a Z).</summary>
    public static string Iso(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // ── Client tokens ─────────────────────────────────────────────────────────

    public static HttpResponse TokensCollection(RouteContext ctx)
    {
        if (IsPost(ctx)) return CreateClientToken(ctx);

        var refused = Require(ctx, RommScopes.MeRead, out _);
        if (refused != null) return refused;
        return RommApi.Json(RommAuth.ListTokens().Select(TokenDto).ToArray());
    }

    private static HttpResponse CreateClientToken(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.MeWrite, out _);
        if (refused != null) return refused;

        string name = "client";
        string[]? scopes = null;
        DateTime? expires = null;
        try
        {
            var body = ctx.Request?.Body ?? "";
            if (body.Length > 0)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    name = n.GetString() ?? name;
                if (root.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array)
                    scopes = RommAuth.SplitScopes(string.Join(" ", s.EnumerateArray().Select(e => e.GetString() ?? "")));
                if (root.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                    expires = when;
            }
        }
        catch { return RommApi.Error(400, "Malformed body"); }

        var (record, secret) = RommAuth.CreateClientToken(name, scopes, expires);
        var dto = (Dictionary<string, object?>)TokenDtoMap(record);
        dto["raw_token"] = secret;
        return RommApi.Json(dto, 201);
    }

    public static HttpResponse TokenById(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.MeWrite, out _);
        if (refused != null) return refused;

        int id = ctx.GetRouteInt("id", -1);
        if (!Is(ctx, "DELETE")) return RommApi.Error(405, "Method not allowed");
        return RommAuth.DeleteToken(id)
            ? RommApi.Json(new { msg = "Token deleted" })
            : RommApi.Error(404, "Token not found");
    }

    public static HttpResponse Regenerate(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.MeWrite, out _);
        if (refused != null) return refused;

        int id = ctx.GetRouteInt("id", -1);
        var secret = RommAuth.RegenerateToken(id);
        if (secret == null) return RommApi.Error(404, "Token not found");

        var record = RommAuth.ListTokens().First(t => t.Id == id);
        var dto = (Dictionary<string, object?>)TokenDtoMap(record);
        dto["raw_token"] = secret;
        return RommApi.Json(dto);
    }

    /// <summary>Starts a pairing: the device gets a short code instead of the secret, and only exchanges it
    /// for the real token once. Nothing sensitive is typed on the handheld.</summary>
    public static HttpResponse Pair(RouteContext ctx)
    {
        var refused = Require(ctx, RommScopes.MeWrite, out _);
        if (refused != null) return refused;

        int id = ctx.GetRouteInt("id", -1);
        var record = RommAuth.ListTokens().FirstOrDefault(t => t.Id == id);
        if (record == null) return RommApi.Error(404, "Token not found");

        // Pairing hands over a secret, and we only ever stored the hash — so the token is regenerated as
        // part of pairing. That also means an old device loses access when a new one is paired, which is
        // the honest reading of "pair this token to a device".
        var secret = RommAuth.RegenerateToken(id);
        if (secret == null) return RommApi.Error(404, "Token not found");

        var code = RommAuth.CreatePairCode(id, secret);
        return RommApi.Json(new { code, expires_in = 300 });
    }

    /// <summary>Polled by the client that started the pairing: 200 while the code is outstanding, 404 once
    /// it has been used or has expired.</summary>
    public static HttpResponse PairStatus(RouteContext ctx)
    {
        var code = (ctx.GetRoute("code") ?? "").ToUpperInvariant();
        return RommAuth.PairCodePending(code)
            ? RommApi.Json(new { status = "pending" })
            : RommApi.Error(404, "Unknown or expired pair code");
    }

    /// <summary>Redeemed by the device. Unauthenticated by necessity — the code IS the credential — and
    /// single-use, short-lived and 24 bits wide, so guessing it inside five minutes is not a plan.</summary>
    public static HttpResponse Exchange(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");

        string code = "";
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request?.Body ?? "{}");
            if (doc.RootElement.TryGetProperty("code", out var c)) code = (c.GetString() ?? "").ToUpperInvariant();
        }
        catch { return RommApi.Error(400, "Malformed body"); }

        var secret = RommAuth.RedeemPairCode(code);
        if (secret == null) return RommApi.Error(404, "Unknown or expired pair code");

        // Say so. A code is redeemed once, silently, on a device that is often not the machine you are
        // sitting at — without this the only way to know it worked is to watch the client succeed.
        try
        {
            LiteBox.Notifications.NotificationCenter.Info(
                "RomM: a device paired with the server.", lifeSpanSeconds: 8);
        }
        catch { }

        return RommApi.Json(new { raw_token = secret, token_type = "bearer" });
    }

    private static object TokenDto(RommClientToken t) => TokenDtoMap(t);

    private static Dictionary<string, object?> TokenDtoMap(RommClientToken t) => new()
    {
        ["id"] = t.Id,
        ["name"] = t.Name,
        ["scopes"] = RommAuth.SplitScopes(t.Scopes),
        ["expires_at"] = t.ExpiresUtc == null ? null : Iso(t.ExpiresUtc.Value),
        ["last_used_at"] = t.LastUsedUtc == null ? null : Iso(t.LastUsedUtc.Value),
        ["created_at"] = Iso(t.CreatedUtc),
        ["user_id"] = UserId,
        ["device_id"] = null,
    };
}
