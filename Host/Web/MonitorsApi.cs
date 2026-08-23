// Web endpoints for the Monitor Profiles module — apply a profile by calling a URL.
//
// THREE GATES, all required: the Web module runs the server at all, the Monitors module owns the feature,
// and its own "Activate web endpoints" option turns these routes on. Off by default, and the routes are
// not merely refused — they are never REGISTERED, so nothing about the feature is discoverable on a
// server whose owner did not ask for it.
//
// The point is remote control: a Stream Deck button, a phone bookmark, a home-automation scene calling a
// URL when the projector comes on. That also means these are unauthenticated GETs on the LAN, exactly
// like the rest of this server — the profile list is deliberately limited to the ones marked shown in
// the Tools menu, so a profile can be kept out of reach by unticking one box.
//
//   GET /api/monitors            what is available and what the desktop looks like
//   GET /api/monitors/apply?name=…   apply it (name or unique prefix)
//   GET /api/monitors/restore    back to the saved original
//
// GET rather than POST on purpose: the callers are bookmarks and buttons, and half of them cannot POST.

#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Monitors;

namespace LbApiHost.Host.Web;

internal static class MonitorsApi
{
    public const string OptionKey = "MonitorWebEndpoints";

    /// <summary>True when the routes should exist: the module on AND the option ticked. (The Web module
    /// is implied — nothing here runs unless the server started.)</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                if (!LbModules.On(LbModule.Monitors)) return false;
                return string.Equals(LiteBoxOptionsDb.GetGlobal(OptionKey), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>GET /api/monitors — the profiles a caller may apply, plus the current desktop.</summary>
    public static HttpResponse List(RouteContext ctx)
    {
        try
        {
            var profiles = MonitorProfileStore.All().Where(p => p.Public).Select(p => new
            {
                name = p.Name,
                summary = p.Summary(),
                hotkey = p.Hotkey,
                url = "/api/monitors/apply?name=" + Uri.EscapeDataString(p.Name),
            }).ToArray();

            return HttpResponse.Json(JsonSerializer.Serialize(new
            {
                profiles,
                canRestore = MonitorProfileApply.CanRestore,
                savedOriginal = MonitorProfileApply.RestoreSummary(),
                monitors = DisplayTargets.Enumerate().Select(m => new
                {
                    name = m.FriendlyName,
                    display = m.DisplayName,
                    connected = m.Active,
                    primary = m.Primary,
                    mode = m.CurrentMode,
                }).ToArray(),
            }));
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>GET /api/monitors/apply?name=… — apply one profile.</summary>
    public static HttpResponse Apply(RouteContext ctx)
    {
        try
        {
            string name = ctx.Request?.GetQuery("name") ?? "";
            if (string.IsNullOrWhiteSpace(name)) return Fail("a name is required", 400);

            // Only what the Tools menu would offer: a profile kept out of the menu is out of reach here
            // too, which is the whole point of that flag on an unauthenticated LAN endpoint.
            var all = MonitorProfileStore.All().Where(p => p.Public).ToList();
            var hit = all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return Fail($"no profile named \"{name}\"", 404);

            var res = MonitorProfileApply.Apply(hit);
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = res.Ok, profile = hit.Name, message = res.Message }));
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>GET /api/monitors/restore — back to the saved original.</summary>
    public static HttpResponse Restore(RouteContext ctx)
    {
        try
        {
            var res = MonitorProfileApply.Restore();
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = res.Ok, message = res.Message }));
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static HttpResponse Fail(string message, int status = 500)
    {
        var r = HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, message }));
        r.StatusCode = status;
        return r;
    }
}
