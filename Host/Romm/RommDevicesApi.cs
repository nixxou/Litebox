// /api/devices — registration with fingerprint dedup, listing, update, delete.
//
// The contract that matters is the CREATE flow (upstream's register_device): same fingerprint → the
// existing device comes back with 200, a new one with 201, and allow_existing=false turns the collision
// into a 409 carrying the existing id. Grout drives all three branches.

#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

internal static class RommDevicesApi
{
    public static HttpResponse Collection(RouteContext ctx)
    {
        bool post = string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, post ? RommScopes.DevicesWrite : RommScopes.DevicesRead, out _);
        if (refused != null) return refused;

        if (!post)
            return RommApi.Json(RommDevices.All().Select(Dto).ToArray());

        JsonElement root;
        try { root = JsonDocument.Parse(ctx.Request!.Body).RootElement; }
        catch { return RommApi.Error(400, "Malformed body"); }

        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(string name, bool dflt) => root.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : dflt;

        bool allowDuplicate = Flag("allow_duplicate", false);
        bool allowExisting = !allowDuplicate && Flag("allow_existing", true);

        if (!allowDuplicate)
        {
            var existing = RommDevices.ByFingerprint(Str("mac_address"), Str("hostname"), Str("platform"));
            if (existing != null)
            {
                if (!allowExisting)
                {
                    return RommApi.Json(new
                    {
                        detail = new
                        {
                            error = "device_exists",
                            message = "A device with this fingerprint already exists",
                            device_id = existing.Id,
                        },
                    }, 409);
                }
                RommDevices.Touch(existing.Id);
                return RommApi.Json(new { device_id = existing.Id, name = existing.Name, created_at = RommAuthApi.Iso(existing.CreatedUtc) });
            }
        }

        var d = RommDevices.Register(new RommDevice
        {
            Name = Str("name"),
            Platform = Str("platform"),
            Client = Str("client"),
            ClientVersion = Str("client_version"),
            IpAddress = Str("ip_address"),
            MacAddress = Str("mac_address"),
            Hostname = Str("hostname"),
            SyncMode = Str("sync_mode") ?? "manual",
        });
        return RommApi.Json(new { device_id = d.Id, name = d.Name, created_at = RommAuthApi.Iso(d.CreatedUtc) }, 201);
    }

    public static HttpResponse ById(RouteContext ctx)
    {
        var method = ctx.Request?.Method ?? "GET";
        bool write = method is "PUT" or "DELETE";
        var refused = RommAuthApi.Require(ctx, write ? RommScopes.DevicesWrite : RommScopes.DevicesRead, out _);
        if (refused != null) return refused;

        var id = ctx.GetRoute("id") ?? "";
        var d = RommDevices.ById(id);
        if (d == null) return RommApi.Error(404, $"Device with ID {id} not found");

        switch (method)
        {
            case "GET":
                return RommApi.Json(Dto(d));

            case "PUT":
            {
                JsonElement root;
                try { root = JsonDocument.Parse(ctx.Request!.Body).RootElement; }
                catch { return RommApi.Error(400, "Malformed body"); }
                var updated = RommDevices.Update(id, dev =>
                {
                    string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (Str("name") is { } n) dev.Name = n;
                    if (Str("platform") is { } p) dev.Platform = p;
                    if (Str("client") is { } c) dev.Client = c;
                    if (Str("client_version") is { } cv) dev.ClientVersion = cv;
                    if (Str("hostname") is { } h) dev.Hostname = h;
                    if (Str("sync_mode") is { } sm) dev.SyncMode = sm;
                    if (root.TryGetProperty("sync_enabled", out var se) && (se.ValueKind == JsonValueKind.True || se.ValueKind == JsonValueKind.False))
                        dev.SyncEnabled = se.GetBoolean();
                    dev.LastSeenUtc = DateTime.UtcNow;
                });
                return RommApi.Json(Dto(updated!));
            }

            case "DELETE":
                RommDevices.Delete(id);
                return RommApi.Json(new { msg = "Device deleted" });

            default:
                return RommApi.Error(405, "Method not allowed");
        }
    }

    private static object Dto(RommDevice d) => new
    {
        id = d.Id,
        user_id = RommAuthApi.UserId,
        name = d.Name,
        platform = d.Platform,
        client = d.Client,
        client_version = d.ClientVersion,
        ip_address = d.IpAddress,
        mac_address = d.MacAddress,
        hostname = d.Hostname,
        client_device_identifier = (string?)null,
        sync_mode = d.SyncMode,
        sync_enabled = d.SyncEnabled,
        sync_config = (object?)null,
        last_seen = d.LastSeenUtc == null ? null : RommAuthApi.Iso(d.LastSeenUtc.Value),
        created_at = RommAuthApi.Iso(d.CreatedUtc),
        updated_at = RommAuthApi.Iso(d.LastSeenUtc ?? d.CreatedUtc),
    };
}
