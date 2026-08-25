// Per-profile scripts (Mehdi): a monitor profile can carry a BEFORE script (power the TV on over
// Home Assistant, wake a device over ADB, warn node-red…) and an AFTER script, each in C# (the
// launch-rule Roslyn engine, Lb API included) or AHK (LaunchBox's own v1.1). They run on EVERY
// application of the profile — a game launch's BeginGameScope and a manual Tools switch alike —
// around MonitorProfileApply.Apply: before-script → profile → after-script.
//
// Timeout is 30 s here (not the rules' 10): the whole point of a before-script is often WAITING
// for hardware — a TV that boots, an ADB handshake. The scripts see ProfileName; Game/Emulator are
// usually not set at this stage (the profile applies before the command line even exists) — these
// scripts are profile-centric by design. Failures log to [monitors] and never block the apply.

#nullable enable

using System;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rules;
using LbApiHost.Host.Rules.Scripting;

namespace LbApiHost.Host.Monitors;

internal static class MonitorProfileScripts
{
    private const string Tag = "monitors";
    private const int TimeoutMs = 30_000;

    public static void Run(MonitorProfile p, bool before)
    {
        string code = before ? p.ScriptBefore : p.ScriptAfter;
        if (string.IsNullOrWhiteSpace(code)) return;
        string lang = before ? p.ScriptBeforeLang : p.ScriptAfterLang;
        string slot = (before ? "before" : "after") + $"-script of \"{p.Name}\"";
        try
        {
            if (string.Equals(lang, "ahk", StringComparison.OrdinalIgnoreCase))
            {
                var d = new AhkScriptData("", "", "", "", "", "", "", "", "", Preview: false, ProfileName: p.Name);
                var (ok, error, _) = AhkScriptEngine.RunSideEffect(code, d, wait: true, timeoutMs: TimeoutMs);
                if (!ok) LbLog.Warn(Tag, $"{slot} failed: {error}");
                else LbLog.Info(Tag, $"{slot} ran");
            }
            else
            {
                var g = new RuleScriptGlobals { ProfileName = p.Name };
                g.Lb = new LbScriptApi(g, new LaunchRule());
                var (ok, error) = RuleScriptEngine.Run(code, g, TimeoutMs);
                if (!ok) LbLog.Warn(Tag, $"{slot} failed: {error}");
                else LbLog.Info(Tag, $"{slot} ran");
            }
        }
        catch (Exception ex) { LbLog.Warn(Tag, $"{slot} threw: {ex.Message}"); }
    }
}
