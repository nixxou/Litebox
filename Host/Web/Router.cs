// URL → handler dispatcher for the embedded web server.
//
// Routes are (anchored regex, handler) pairs; named groups (?<name>…) capture path segments into the
// RouteContext. First match wins, so register specific patterns before generic ones. The table is rebuilt on
// each server Start so per-site enable flags take effect on a restart.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

/// <summary>Per-request context handed to a handler: the parsed request + captured route placeholders.</summary>
internal sealed class RouteContext
{
    public HttpRequest Request { get; init; }
    public Dictionary<string, string> RouteValues { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string GetRoute(string name) =>
        RouteValues.TryGetValue(name, out var v) ? v : null;

    public int GetRouteInt(string name, int fallback = 0) =>
        int.TryParse(GetRoute(name), out var v) ? v : fallback;
}

/// <summary>Handler delegate — receives the routed request, returns the response.</summary>
internal delegate HttpResponse RouteHandler(RouteContext ctx);

/// <summary>Lightweight regex URL dispatcher (first-match-wins).</summary>
internal sealed class Router
{
    private readonly List<(Regex Pattern, RouteHandler Handler)> _routes = new();

    /// <summary>Register a handler for the given path pattern (regex with optional (?&lt;name&gt;…) groups).
    /// Anchored automatically (^…$).</summary>
    public void Add(string pattern, RouteHandler handler)
    {
        var anchored = "^" + pattern + "$";
        _routes.Add((new Regex(anchored, RegexOptions.Compiled), handler));
    }

    /// <summary>Drops every registered route — used to rebuild the table on a server restart.</summary>
    public void Clear() => _routes.Clear();

    /// <summary>Dispatches to the first matching handler; null when nothing matched (caller emits 404).</summary>
    public HttpResponse Dispatch(HttpRequest req)
    {
        foreach (var (pattern, handler) in _routes)
        {
            var m = pattern.Match(req.Path);
            if (!m.Success) continue;

            var ctx = new RouteContext { Request = req };
            foreach (var name in pattern.GetGroupNames())
            {
                if (int.TryParse(name, out _)) continue; // skip numeric (default) groups
                ctx.RouteValues[name] = m.Groups[name].Value;
            }

            try
            {
                return handler(ctx);
            }
            catch (Exception ex)
            {
                LbLog.Warn("web", $"handler error on {req.Path}: {ex}");
                return HttpResponse.ServerError($"Handler error: {ex.Message}");
            }
        }
        return null;
    }
}
