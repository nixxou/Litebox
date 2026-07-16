// Serves the user-configurable keyboard bindings for the two theme surfaces:
//   GET /bigbox/api/keybinds       → Handle          (BigBox engine onKey commands)
//   GET /launchbox/api/keybinds    → HandleLaunchBox  (LiteBox Web engine lbOnKey commands)
//
// The engine fetches these once at startup and maps each pressed DOM key to a navigation command; until the
// fetch lands it uses its own built-in defaults, so this only has to return a valid { command: [keys…] } map.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Theme/WebKeyBindsApi.cs. Source of truth is the LiteBox.ini
// [Web] section (LiteBoxConfig): each command reads its comma-separated DOM-key list from a "Keys.<cmd>" key,
// falling back to the shipped default when unset. DOM e.key is case-sensitive ("a" vs "A"), so single-char
// entries keep their case.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LbApiHost.Host.Web;

internal static class WebKeyBindsApi
{
    // Built-in defaults (mirror the engines' own onKey/lbOnKey defaults).
    private static readonly Dictionary<string, string> BigBoxDefaults = new()
    {
        ["up"] = "ArrowUp",
        ["down"] = "ArrowDown",
        ["left"] = "ArrowLeft",
        ["right"] = "ArrowRight",
        ["pgup"] = "PageUp",
        ["pgdn"] = "PageDown",
        ["select"] = "Enter",
        ["back"] = "Backspace,Escape",
        ["menu"] = "Control",
        ["poster"] = "Tab",
    };

    private static readonly Dictionary<string, string> LaunchBoxDefaults = new()
    {
        ["up"] = "ArrowUp",
        ["down"] = "ArrowDown",
        ["left"] = "ArrowLeft",
        ["right"] = "ArrowRight",
        ["pgup"] = "PageUp",
        ["pgdn"] = "PageDown",
        ["home"] = "Home",
        ["end"] = "End",
        ["select"] = "Enter",
        ["zone"] = "Tab",
    };

    public static HttpResponse Handle(RouteContext ctx) => Emit(BigBoxDefaults);

    public static HttpResponse HandleLaunchBox(RouteContext ctx) => Emit(LaunchBoxDefaults);

    private static HttpResponse Emit(Dictionary<string, string> defaults)
    {
        LiteBoxConfig cfg = null;
        try { cfg = LiteBoxConfig.LoadForExe(); } catch { }

        var map = new Dictionary<string, string[]>(defaults.Count);
        foreach (var kv in defaults)
        {
            string csv = kv.Value;
            try
            {
                var over = cfg?.GetSec("Web", "Keys." + kv.Key, null);
                if (!string.IsNullOrWhiteSpace(over)) csv = over;
            }
            catch { }
            map[kv.Key] = Split(csv);
        }
        return HttpResponse.Json(JsonSerializer.Serialize(map));
    }

    /// <summary>Splits a comma/semicolon-separated key list, trimming blanks and preserving case.</summary>
    private static string[] Split(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        var parts = csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.ToArray();
    }
}
