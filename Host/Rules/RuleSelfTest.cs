// --selftest-rules: the Prefix action's behaviour pinned case by case against BigBoxProfile's
// semantics (Prefix.Modify + EmulatorLauncher's final marker pass), so the next ported action starts
// from a harness instead of a hope. Pure pipeline tests — no DB, no UI: rules are built in memory and
// pushed through the same code the launch hook runs.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace LbApiHost.Host.Rules;

internal static class RuleSelfTest
{
    private static int _fail;

    public static int Run()
    {
        // ── the action itself ──
        // NOTE the round trips: parse + re-join re-quotes MINIMALLY (quotes only where whitespace or
        // quotes demand them) — BigBoxUtils.ArgsToCommandLine's exact behaviour, invisible to the
        // spawned process since Windows parses both spellings identically.
        Check("as ARGUMENT inserts one token first",
            Prefix("-fullscreen"), @"""C:\roms\game.zip"" -x", @"-fullscreen C:\roms\game.zip -x");

        Check("as ARGUMENT keeps quotes where they matter (spaced path)",
            Prefix("-fullscreen"), @"""C:\my roms\game.zip"" -x", @"-fullscreen ""C:\my roms\game.zip"" -x");

        Check("as ARGUMENT trims and quotes a spaced prefix",
            Prefix("  hello world  "), "-x", @"""hello world"" -x");

        Check("as CMDLINE prepends verbatim (several args in one prefix)",
            Prefix(@"-L ""cores\snes.dll"" ", asArg: false), @"""C:\roms\game.zip""", @"-L ""cores\snes.dll"" ""C:\roms\game.zip""");

        Check("empty prefix = not configured, untouched",
            Prefix(""), "-x", "-x");

        // ── the filter probe (haystack includes the exe) ──
        Check("filter matches in the ARGS",
            Prefix("-a", filter: "game.zip"), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("filter matches in the EXE path (BigBoxProfile matched on args[0] too)",
            Prefix("-a", filter: "retroarch"), "-x", "-a -x");

        Check("filter miss leaves args untouched",
            Prefix("-a", filter: "nothere"), "-x", "-x");

        Check("filter is case-insensitive",
            Prefix("-a", filter: "GAME.ZIP"), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("comma filter = ANY entry",
            Prefix("-a", filter: "nothere, game.zip", comma: true), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("comma filter + matchall requires EVERY entry",
            Prefix("-a", filter: "roms, nothere", comma: true, matchAll: true), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        Check("comma filter + matchall passes when all present",
            Prefix("-a", filter: "roms, game.zip", comma: true, matchAll: true), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        // ── the exclude probe ──
        Check("exclude blocks on match",
            Prefix("-a", exclude: "game.zip"), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        Check("comma exclude blocks on ANY entry",
            Prefix("-a", exclude: "nothere, game.zip", commaEx: true), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        Check("comma exclude + matchall blocks only when ALL present (the fixed intent)",
            Prefix("-a", exclude: "nothere, game.zip", commaEx: true, matchAllEx: true), @"""C:\roms\game.zip""", @"-a C:\roms\game.zip");

        Check("comma exclude + matchall with all present blocks",
            Prefix("-a", exclude: "roms, game.zip", commaEx: true, matchAllEx: true), @"""C:\roms\game.zip""", @"""C:\roms\game.zip""");

        // ── the marker system (RemoveFilter final pass) ──
        Check("marker routes the rule, then is stripped",
            Prefix("-a", filter: "-marker", remove: true), @"""C:\roms\game.zip"" -marker", @"-a C:\roms\game.zip");

        Check("marker strip is whole-argument, not substring",
            Prefix("-a", filter: "-marker", remove: true), @"-markers ""C:\roms\game.zip"" -marker", @"-a -markers C:\roms\game.zip");

        // A marker that only SUPPRESSED a rule (probe did not fire) is still stripped — BigBoxProfile
        // collects the removal list over every CONFIGURED module, fired or not.
        Check("marker stripped even when its rule did not fire",
            new[] { P("-a", filter: "nothere, -marker", comma: true, matchAll: true, remove: true) },
            "-marker -x", "-x");

        // ── the pipeline ──
        Check("rules run in order, each seeing the previous result",
            new[] { P("-first"), P("-second") }, "-x", "-second -first -x");

        Check("disabled rule is skipped",
            new[] { Disabled(P("-a")), P("-b") }, "-x", "-b -x");

        Console.WriteLine(_fail == 0 ? "ALL OK" : $"{_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static LaunchRule P(string prefix, bool asArg = true, string filter = "", bool comma = false,
        bool matchAll = false, bool remove = false, string exclude = "", bool commaEx = false, bool matchAllEx = false)
        => new()
        {
            Type = LaunchRule.TypePrefix, Prefix = prefix, AsArg = asArg,
            Filter = filter, CommaFilter = comma, MatchAllFilter = matchAll, RemoveFilter = remove,
            Exclude = exclude, CommaExclude = commaEx, MatchAllExclude = matchAllEx,
        };

    private static LaunchRule[] Prefix(string prefix, bool asArg = true, string filter = "", bool comma = false,
        bool matchAll = false, bool remove = false, string exclude = "", bool commaEx = false, bool matchAllEx = false)
        => new[] { P(prefix, asArg, filter, comma, matchAll, remove, exclude, commaEx, matchAllEx) };

    private static LaunchRule Disabled(LaunchRule r) { r.Enabled = false; return r; }

    private static void Check(string what, LaunchRule[] rules, string args, string expected)
    {
        string got = RunPipeline(rules, @"C:\emu\retroarch\retroarch.exe", args);
        if (got == expected) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}\n        args     : {args}\n        expected : {expected}\n        got      : {got}");
    }

    /// <summary>The pipeline body, minus the store: same probe + action + marker code as a launch.</summary>
    private static string RunPipeline(IEnumerable<LaunchRule> rules, string exe, string args)
        => RulePipeline.ApplyRules(rules.ToList(), exe, args);
}
