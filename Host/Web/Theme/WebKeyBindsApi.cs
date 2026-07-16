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

    public static HttpResponse Handle(RouteContext ctx) => HttpResponse.Json(WebKeyBinds.BigBoxJson());

    public static HttpResponse HandleLaunchBox(RouteContext ctx) => HttpResponse.Json(WebKeyBinds.LaunchBoxJson());
}
