// The device-pairing endpoints + the approval page LiteBox serves in place of RomM's web UI.
//
//   POST /api/auth/device/init              open   → device_code + user_code + verification path
//   GET  /api/auth/device/pending/{code}    me.read
//   POST /api/auth/device/approve           me.write
//   POST /api/auth/device/deny              me.write
//   POST /api/auth/device/token             open   → the client token, once approved
//   GET  /pair/device[?user_code=…]         the approval page (Basic-authenticated by the browser)
//
// The poll answers with 400 + a `detail` that is one of RFC 8628's machine-readable strings
// (`authorization_pending`, `slow_down`, `access_denied`, `expired_token`) — a client branches on the
// exact word, so these are not free text.
//
// Scope handling: a client may ask for scopes this surface does not grant (Argosy asks for the full
// set). The init accepts any VALID RomM scope, and the approval issues the intersection with what we
// actually grant — refusing the request outright would break a well-behaved client for no gain.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;
using LiteBox.Notifications;

namespace LbApiHost.Host.Romm;

internal static class RommDeviceAuthApi
{
    // ── 1. init (open) ────────────────────────────────────────────────────────

    public static HttpResponse Init(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");

        // The account still gates the flow: with no password the server has no owner to approve
        // anything, and minting a pairing would be minting access to an unowned library.
        if (!RommConfig.HasPassword)
            return RommApi.Error(401, "No account is configured on this server");

        JsonElement root;
        try { root = JsonDocument.Parse(ctx.Request!.Body).RootElement; }
        catch { return RommApi.Error(400, "Malformed body"); }

        string? Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var identifier = Str("client_device_identifier");
        var name = Str("name");
        var client = Str("client");
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(client))
            return RommApi.Error(422, "client_device_identifier, name and client are required");

        var requested = new List<string>();
        if (root.TryGetProperty("requested_scopes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s) requested.Add(s);

        if (requested.Count == 0) return RommApi.Error(422, "requested_scopes must not be empty");
        var unknown = requested.Where(s => Array.IndexOf(RommScopes.All, s) < 0).Distinct().ToList();
        if (unknown.Count > 0)
            return RommApi.Error(422, "Unknown scopes: " + string.Join(", ", unknown.OrderBy(x => x)));

        var p = RommDeviceAuth.Start(identifier!, name!, client!, Str("platform"), Str("client_version"),
                                     requested.Distinct().ToArray());

        LbLog.Info("romm", $"pairing started: client={p.Client} device=\"{p.Name}\" code={p.UserCode} " +
                           $"(approve on the desktop, or at /pair/device on this server)");

        Prompt(p);

        return RommApi.Json(new
        {
            device_code = p.DeviceCode,
            user_code = p.UserCode,
            verification_path = "/pair/device",
            verification_path_complete = "/pair/device?user_code=" + p.UserCode,
            expires_in = RommDeviceAuth.PendingTtlSeconds,
            interval = RommDeviceAuth.PollIntervalSeconds,
        }, 201);
    }

    // ── 1b. the desktop prompt ────────────────────────────────────────────────
    //
    // A sticky notification card with Approve / Deny, raised the moment a device asks. The card is the
    // whole point of the flow being safe: init and token are unauthenticated, so a human saying yes IS
    // the access control. It shows the code the device is displaying, because comparing the two is what
    // tells you the request on screen is the device in your hand and not somebody else's on the LAN.
    //
    // Raised without blocking: init must answer immediately (the device is already polling), so this
    // fires and returns. The notification sink marshals to the UI thread itself, and no-ops when there
    // is no GUI — a headless run just leaves /pair/device as the way in.

    private static void Prompt(RommDeviceAuth.Pending p)
    {
        try
        {
            var allowed = AllowedFor(p);
            var where = string.IsNullOrWhiteSpace(p.Platform) ? p.Client : $"{p.Client} · {p.Platform}";
            var scopes = allowed.Length > 0 ? string.Join(", ", allowed) : "nothing this server grants";
            var message =
                $"Pair \"{p.Name}\" ({where}) with your library?\n" +
                $"Code shown on the device: {p.UserCode}\n" +
                $"It will be allowed to: {scopes}";

            if (allowed.Length == 0)
            {
                NotificationCenter.Error(
                    $"\"{p.Name}\" ({where}) asked to pair, but this server grants none of the scopes it wants.");
                return;
            }

            NotificationCenter.Input(message, new[]
            {
                new KeyValuePair<string, Action>("Approve", () => PromptApprove(p, allowed)),
                new KeyValuePair<string, Action>("Deny", () => PromptDeny(p)),
            });
        }
        catch (Exception ex) { LbLog.Warn("romm", "pairing prompt failed: " + ex.Message); }
    }

    private static void PromptApprove(RommDeviceAuth.Pending p, string[] allowed)
    {
        // The card can outlive the flow's 10-minute TTL, so re-resolve rather than trusting the captured
        // object: approving a pairing nobody is waiting for would mint a token for no one.
        var live = RommDeviceAuth.ByUserCode(p.UserCode);
        if (live == null || live.Status != RommDeviceAuth.StatusPending)
        {
            NotificationCenter.Info($"That pairing request expired. Start it again from \"{p.Name}\".");
            return;
        }

        var done = ApproveFlow(live, allowed, null, null);
        NotificationCenter.Info(done == null
            ? $"Could not pair \"{p.Name}\" — see the log."
            : $"\"{done.Value.deviceName}\" is paired. It will pick up its access within a few seconds.");
    }

    private static void PromptDeny(RommDeviceAuth.Pending p)
    {
        var live = RommDeviceAuth.ByUserCode(p.UserCode);
        if (live != null && live.Status == RommDeviceAuth.StatusPending) RommDeviceAuth.MarkDenied(live);
        LbLog.Info("romm", $"pairing denied from the desktop: \"{p.Name}\" ({p.Client})");
    }

    // ── 2. the human side ─────────────────────────────────────────────────────

    public static HttpResponse GetPending(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.MeRead, out _);
        if (refused != null) return refused;

        var p = RommDeviceAuth.ByUserCode(ctx.GetRoute("user_code"));
        if (p == null) return RommApi.Error(404, "Unknown or expired code");
        if (p.Status != RommDeviceAuth.StatusPending) return RommApi.Error(410, $"Code already {p.Status}");

        return RommApi.Json(PendingDto(p));
    }

    /// <summary>Every flow still waiting — lets the approval page work when the user opened it without a
    /// code (typed the address off the phone rather than scanning).</summary>
    public static HttpResponse ListPending(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.MeRead, out _);
        if (refused != null) return refused;
        return RommApi.Json(RommDeviceAuth.PendingFlows().Select(PendingDto).ToArray());
    }

    private static object PendingDto(RommDeviceAuth.Pending p) => new
    {
        user_code = p.UserCode,
        client_device_identifier = p.ClientDeviceIdentifier,
        name = p.Name,
        client = p.Client,
        platform = p.Platform,
        client_version = p.ClientVersion,
        requested_scopes = p.RequestedScopes.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        allowed_scopes = AllowedFor(p),
        expires_at = RommAuthApi.Iso(p.ExpiresUtc),
    };

    /// <summary>What this server can actually hand over: what the device asked for ∩ what we grant.</summary>
    private static string[] AllowedFor(RommDeviceAuth.Pending p)
        => p.RequestedScopes.Where(s => Array.IndexOf(RommScopes.Granted, s) >= 0)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .ToArray();

    public static HttpResponse Approve(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");
        var refused = RommAuthApi.Require(ctx, RommScopes.MeWrite, out _);
        if (refused != null) return refused;

        JsonElement root;
        try { root = JsonDocument.Parse(ctx.Request!.Body).RootElement; }
        catch { return RommApi.Error(400, "Malformed body"); }

        var p = RommDeviceAuth.ByUserCode(root.TryGetProperty("user_code", out var uc) ? uc.GetString() : null);
        if (p == null) return RommApi.Error(404, "Unknown or expired code");
        if (p.Status != RommDeviceAuth.StatusPending) return RommApi.Error(410, $"Code already {p.Status}");

        var allowed = AllowedFor(p);
        var approved = allowed;
        if (root.TryGetProperty("approved_scopes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var asked = arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                           .Select(e => e.GetString()!).ToArray();
            if (asked.Length > 0)
            {
                if (asked.Any(s => Array.IndexOf(allowed, s) < 0))
                    return RommApi.Error(403, "Approved scopes exceed what this server grants");
                approved = asked.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            }
        }
        if (approved.Length == 0)
            return RommApi.Error(403, "This server grants none of the scopes the device asked for");

        var deviceName = (root.TryGetProperty("device_name", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString() : null) ?? p.Name;
        DateTime? expiresUtc = ParseExpiry(root.TryGetProperty("expires_in", out var ex) ? ex.GetString() : null);

        var done = ApproveFlow(p, approved, deviceName, expiresUtc);
        return done == null
            ? RommApi.Error(500, "Could not complete the pairing")
            : RommApi.Json(new { device_id = done.Value.deviceId, device_name = done.Value.deviceName });
    }

    /// <summary>Creates (or reuses) the Device, mints the client token bound to it, and arms the waiting
    /// device's next poll. <paramref name="approved"/> must already be a subset of what this server
    /// grants — both callers validate before getting here. Shared by the HTTP route and the desktop
    /// prompt, so an approval means the same thing wherever it was clicked.</summary>
    internal static (string deviceId, string deviceName)? ApproveFlow(
        RommDeviceAuth.Pending p, string[] approved, string? deviceName, DateTime? expiresUtc)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(deviceName) ? p.Name : deviceName!;

            // Same device (same client identifier) pairing again reuses its record instead of piling up rows.
            var device = RommDevices.All().FirstOrDefault(d =>
                string.Equals(d.Hostname ?? "", p.ClientDeviceIdentifier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Client ?? "", p.Client, StringComparison.OrdinalIgnoreCase));
            if (device != null)
            {
                RommDevices.Update(device.Id, d =>
                {
                    d.Name = name;
                    d.ClientVersion = p.ClientVersion;
                    d.LastSeenUtc = DateTime.UtcNow;
                });
            }
            else
            {
                device = RommDevices.Register(new RommDevice
                {
                    Name = name,
                    Platform = p.Platform,
                    Client = p.Client,
                    ClientVersion = p.ClientVersion,
                    Hostname = p.ClientDeviceIdentifier,
                    SyncMode = "api",
                });
            }

            var (record, secret) = RommAuth.CreateClientToken(name, approved, expiresUtc, device.Id);
            RommDeviceAuth.MarkApproved(p, secret, device.Id, approved, record.ExpiresUtc);

            LbLog.Info("romm", $"pairing approved: \"{name}\" ({p.Client}) scopes={string.Join(" ", approved)}");
            return (device.Id, name);
        }
        catch (Exception ex)
        {
            LbLog.Warn("romm", "pairing approval failed: " + ex.Message);
            return null;
        }
    }

    public static HttpResponse Deny(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");
        var refused = RommAuthApi.Require(ctx, RommScopes.MeWrite, out _);
        if (refused != null) return refused;

        JsonElement root;
        try { root = JsonDocument.Parse(ctx.Request!.Body).RootElement; }
        catch { return RommApi.Error(400, "Malformed body"); }

        var p = RommDeviceAuth.ByUserCode(root.TryGetProperty("user_code", out var uc) ? uc.GetString() : null);
        if (p == null) return RommApi.Error(404, "Unknown or expired code");
        if (p.Status != RommDeviceAuth.StatusPending) return RommApi.Error(410, $"Code already {p.Status}");

        RommDeviceAuth.MarkDenied(p);
        LbLog.Info("romm", $"pairing denied: \"{p.Name}\" ({p.Client})");
        return RommApi.Json(new { msg = "Denied" });
    }

    /// <summary>Upstream's expires_in vocabulary. Anything else is a client bug worth naming.</summary>
    private static DateTime? ParseExpiry(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "never") return null;
        return value switch
        {
            "30d" => DateTime.UtcNow.AddDays(30),
            "90d" => DateTime.UtcNow.AddDays(90),
            "180d" => DateTime.UtcNow.AddDays(180),
            "1y" => DateTime.UtcNow.AddDays(365),
            _ => null,
        };
    }

    // ── 3. the device's poll (open) ───────────────────────────────────────────

    public static HttpResponse Token(RouteContext ctx)
    {
        if (!IsPost(ctx)) return RommApi.Error(405, "Method not allowed");

        string? deviceCode = null;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body);
            if (doc.RootElement.TryGetProperty("device_code", out var dc)) deviceCode = dc.GetString();
        }
        catch { return RommApi.Error(400, "Malformed body"); }

        var p = RommDeviceAuth.ByDeviceCode(deviceCode);
        if (p == null) return RommApi.Error(400, "expired_token");

        switch (p.Status)
        {
            case RommDeviceAuth.StatusPending:
                return RommApi.Error(400, RommDeviceAuth.PolledTooFast(p) ? "slow_down" : "authorization_pending");

            case RommDeviceAuth.StatusDenied:
                return RommApi.Error(400, "access_denied");

            case RommDeviceAuth.StatusApproved:
            {
                var done = RommDeviceAuth.ConsumeApproved(p);
                if (done?.RawToken == null) return RommApi.Error(400, "expired_token");
                LbLog.Info("romm", $"pairing token issued to \"{done.Name}\" ({done.Client})");
                return RommApi.Json(new
                {
                    access_token = done.RawToken,
                    device_id = done.DeviceId,
                    scopes = done.ApprovedScopes,
                    expires_at = done.TokenExpiresUtc == null ? null : RommAuthApi.Iso(done.TokenExpiresUtc.Value),
                });
            }

            default:
                return RommApi.Error(400, "expired_token");
        }
    }

    // ── 4. the approval page ──────────────────────────────────────────────────
    //
    // RomM approves in its Vue UI at /pair/device. We have no such UI, so this surface serves the
    // equivalent: the browser authenticates with the account (Basic — the same credential a client
    // uses), the page lists what is waiting, and one click approves. It is the one step in the flow a
    // human must perform, so it cannot be skipped or automated.

    public static HttpResponse PairPage(RouteContext ctx)
    {
        var code = ctx.Request?.GetQuery("user_code") ?? "";
        var html = PageHtml.Replace("__CODE__", JsonEncodedText.Encode(code).ToString());
        return HttpResponse.Html(html);
    }

    private static bool IsPost(RouteContext ctx)
        => string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);

    private const string PageHtml = """
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Pair a device — LiteBox</title>
<style>
 :root{--bg:#0f0f12;--panel:#1a1a20;--panel2:#22222a;--fg:#e6e6e6;--sub:#9a9aa5;--accent:#7a5cff;--ok:#5cba7d;--err:#d96c6c}
 *{box-sizing:border-box;margin:0;padding:0}
 body{background:var(--bg);color:var(--fg);font-family:system-ui,"Segoe UI",sans-serif;padding:2rem 1rem}
 main{max-width:620px;margin:0 auto}
 h1{font-weight:600;font-size:1.5rem;margin-bottom:.25rem}
 .sub{color:var(--sub);margin-bottom:1.5rem}
 .card{background:var(--panel);border:1px solid #2c2c34;border-radius:10px;padding:1.1rem 1.25rem;margin-bottom:1rem}
 .dev{font-size:1.15rem;font-weight:600}
 .meta{color:var(--sub);font-size:.9rem;margin-top:.2rem}
 .code{font-family:ui-monospace,Consolas,monospace;font-size:1.6rem;letter-spacing:.18em;margin:.6rem 0;color:var(--accent)}
 ul{list-style:none;margin:.6rem 0 1rem;display:flex;flex-wrap:wrap;gap:.4rem}
 li{background:var(--panel2);border:1px solid #33333d;border-radius:999px;padding:.25rem .7rem;font-size:.82rem}
 button{padding:.7rem 1.4rem;font-size:1rem;font-weight:600;border:0;border-radius:9px;cursor:pointer;margin-right:.5rem}
 .approve{background:var(--accent);color:#fff}.deny{background:var(--panel2);color:var(--fg);border:1px solid #3a3a44}
 .msg{margin-top:1rem}.ok{color:var(--ok)}.err{color:var(--err)}
</style></head><body><main>
<h1>Pair a device</h1>
<div class="sub">A device is asking for access to this library. Approve it only if you started it.</div>
<div id="list"></div>
<div class="msg" id="msg"></div>
</main>
<script>
(() => {
  const wanted = "__CODE__";
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;"}[c]));
  const msg = (t, cls) => { $("msg").className = "msg " + (cls || ""); $("msg").textContent = t; };

  async function load() {
    let r;
    try {
      r = wanted
        ? await fetch("/api/auth/device/pending/" + encodeURIComponent(wanted))
        : await fetch("/api/auth/device/pending");
    } catch { msg("Server unreachable.", "err"); return; }

    if (r.status === 401) { msg("Sign in with the RomM account to approve a device.", "err"); return; }
    if (!r.ok) {
      const j = await r.json().catch(() => ({}));
      msg(j.detail || "No pending pairing for that code.", "err");
      return;
    }
    const data = await r.json();
    render(Array.isArray(data) ? data : [data]);
  }

  function render(items) {
    if (!items.length) { $("list").innerHTML = ""; msg("Nothing is waiting to be paired right now.", ""); return; }
    $("list").innerHTML = items.map(p => `
      <div class="card" data-code="${esc(p.user_code)}">
        <div class="dev">${esc(p.name)}</div>
        <div class="meta">${esc(p.client)}${p.platform ? " · " + esc(p.platform) : ""}${p.client_version ? " · v" + esc(p.client_version) : ""}</div>
        <div class="code">${esc(p.user_code)}</div>
        <div class="meta">Access it will be granted:</div>
        <ul>${(p.allowed_scopes || []).map(s => `<li>${esc(s)}</li>`).join("") || "<li>none</li>"}</ul>
        <button class="approve">Approve</button><button class="deny">Deny</button>
      </div>`).join("");

    document.querySelectorAll(".card").forEach(card => {
      const code = card.dataset.code;
      card.querySelector(".approve").onclick = () => act("approve", code, card);
      card.querySelector(".deny").onclick = () => act("deny", code, card);
    });
  }

  async function act(what, code, card) {
    card.querySelectorAll("button").forEach(b => b.disabled = true);
    try {
      const r = await fetch("/api/auth/device/" + what, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ user_code: code })
      });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { msg(j.detail || (what + " failed"), "err"); card.querySelectorAll("button").forEach(b => b.disabled = false); return; }
      card.remove();
      msg(what === "approve"
        ? "Approved. The device will pick up its access within a few seconds."
        : "Denied.", what === "approve" ? "ok" : "");
    } catch { msg("Network error.", "err"); card.querySelectorAll("button").forEach(b => b.disabled = false); }
  }

  load();
})();
</script></body></html>
""";
}
