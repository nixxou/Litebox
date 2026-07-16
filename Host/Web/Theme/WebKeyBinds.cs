// Resolves the user-configurable web navigation keybinds from LiteBox.ini and hands them to
// WebKeyBindsApi as an engine-ready { command: [DOM-keys…] } map.
//
// Why this helper exists
//   The Web options panel (Options/Modules/WebPanel) persists two nav-key grids to the
//   LiteBox.ini [Web] section:
//       BigBox   → Keys.<Row>       (Row = Up/Down/Left/Right/PageUp/PageDown/Home/End/Select/Back/Menu)
//       LaunchBox→ KeysLb.<Row>     (same Row set)
//   but the theme engines (BigBoxWeb/web/engine/app.js :: KEYCMD, litebox/app.js :: LBKEYCMD)
//   key their command→keys map on the LOWER-CASE engine command names, which do NOT line up
//   1:1 with the panel's PascalCase row names:
//       • BigBox engine commands : up down left right pgup pgdn select back menu poster
//       • LaunchBox engine cmds   : up down left right pgup pgdn home end select zone
//   So a straight "Keys." + command lookup misses every rebind. This helper owns the explicit
//   engine-command → config-suffix mapping (plus the shipped default per command), so the two
//   API handlers just serialize what it returns.
//
// Mapping notes (engine command ⇐ WebPanel config row)
//   BigBox : up⇐Up down⇐Down left⇐Left right⇐Right pgup⇐PageUp pgdn⇐PageDown
//            select⇐Select back⇐Back menu⇐Menu ; poster has no panel row → default "Tab" only.
//   LaunchBox: up⇐Up down⇐Down left⇐Left right⇐Right pgup⇐PageUp pgdn⇐PageDown
//            home⇐Home end⇐End select⇐Select ; zone (Tab) ⇐ the panel's "Menu" row, whose
//            LaunchBox default is "Tab" — that row IS the zone/tab switch on this surface.
//   (Any residual naming quirk here is a WebPanel row-label matter, not a runtime bug: the
//    stored value flows through unchanged; only the label reads "Menu".)
//
// DOM e.key is case-sensitive ("a" vs "A"), so single-char entries keep their case: Split() only
// trims, never changes case.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LbApiHost.Host.Web;

internal static class WebKeyBinds
{
    private const string Sec = "Web";

    // (engineCommand, iniSuffix, default). iniSuffix == null → command has no panel row; default only.
    // Order is irrelevant to the engines (they build a key→command reverse lookup).
    private static readonly (string cmd, string suffix, string def)[] BigBoxRows =
    {
        ("up",     "Up",       "ArrowUp"),
        ("down",   "Down",     "ArrowDown"),
        ("left",   "Left",     "ArrowLeft"),
        ("right",  "Right",    "ArrowRight"),
        ("pgup",   "PageUp",   "PageUp"),
        ("pgdn",   "PageDown", "PageDown"),
        ("select", "Select",   "Enter,a,A"),
        ("back",   "Back",     "Escape,Backspace,BrowserBack,b,B"),
        ("menu",   "Menu",     "s,S"),
        ("poster", null,       "Tab"),
    };

    private static readonly (string cmd, string suffix, string def)[] LaunchBoxRows =
    {
        ("up",     "Up",       "ArrowUp"),
        ("down",   "Down",     "ArrowDown"),
        ("left",   "Left",     "ArrowLeft"),
        ("right",  "Right",    "ArrowRight"),
        ("pgup",   "PageUp",   "PageUp"),
        ("pgdn",   "PageDown", "PageDown"),
        ("home",   "Home",     "Home"),
        ("end",    "End",      "End"),
        ("select", "Select",   "Enter,Spacebar"),
        ("zone",   "Menu",     "Tab"),
    };

    /// <summary>Serialized { command: [keys…] } map for the BigBox engine (/bigbox/api/keybinds).</summary>
    public static string BigBoxJson() => JsonSerializer.Serialize(Resolve(BigBoxRows, "Keys."));

    /// <summary>Serialized { command: [keys…] } map for the LaunchBox engine (/launchbox/api/keybinds).</summary>
    public static string LaunchBoxJson() => JsonSerializer.Serialize(Resolve(LaunchBoxRows, "KeysLb."));

    private static Dictionary<string, string[]> Resolve((string cmd, string suffix, string def)[] rows, string prefix)
    {
        LiteBoxConfig cfg = null;
        try { cfg = LiteBoxConfig.LoadForExe(); } catch { }

        var map = new Dictionary<string, string[]>(rows.Length);
        foreach (var r in rows)
        {
            string csv = r.def;
            if (r.suffix != null)
            {
                try
                {
                    var over = cfg?.GetSec(Sec, prefix + r.suffix, null);
                    if (!string.IsNullOrWhiteSpace(over)) csv = over;
                }
                catch { }
            }
            map[r.cmd] = Split(csv);
        }
        return map;
    }

    /// <summary>Splits a comma/semicolon-separated key list, trimming blanks and preserving case
    /// (DOM <c>e.key</c> is case-sensitive: "a" vs "A").</summary>
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
