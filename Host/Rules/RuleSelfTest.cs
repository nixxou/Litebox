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

        // ── the preview channel (EmulatorConfig.CalculateExemple) ──
        CheckPreview("preview transforms the full line, exe kept in front",
            Prefix("-a"), @"sd.exe ""C:\MyRomDir\MyRom.bin""", @"sd.exe -a C:\MyRomDir\MyRom.bin");

        CheckPreview("preview runs the marker pass too",
            Prefix("-a", filter: "-marker", remove: true), "sd.exe rom.bin -marker", "sd.exe -a rom.bin");

        // The parse + re-join round trip dequotes even when no rule acts — visible in BigBoxProfile's
        // own screenshot (IN quoted, OUT bare, one NON-CONFIGURED rule in the list).
        CheckPreview("preview skips a non-configured rule (round-trip requote only)",
            Prefix(""), @"sd.exe ""C:\MyRomDir\MyRom.bin""", @"sd.exe C:\MyRomDir\MyRom.bin");

        // ── Suffix (same probes, opposite end; BigBoxProfile parity) ──
        Check("suffix as ARGUMENT appends one token last",
            Suffix("-fullscreen"), @"""C:\roms\game.zip"" -x", @"C:\roms\game.zip -x -fullscreen");

        Check("suffix as ARGUMENT trims and quotes a spaced payload",
            Suffix("  hello world  "), "-x", @"-x ""hello world""");

        Check("suffix as CMDLINE appends verbatim (leading space is the author's)",
            Suffix(" -L \"cores\\snes.dll\"", asArg: false), @"""C:\roms\game.zip""", @"""C:\roms\game.zip"" -L ""cores\snes.dll""");

        Check("empty suffix = not configured, untouched",
            Suffix(""), "-x", "-x");

        Check("suffix honours the shared probes",
            Suffix("-a", filter: "nothere"), "-x", "-x");

        Check("prefix and suffix compose in pipeline order",
            new[] { P("-first"), Sfx("-last") }, "-x", "-first -x -last");

        // ── the trace channel (groups + trace lot) ──
        {
            var rules = new List<LaunchRule>
            {
                P("-a", filter: "rom.bin"),          // fires
                P("-b", filter: "nothere"),          // refused
                Disabled(P("-c")),                   // skipped
            };
            RulePipeline.PreviewWithTrace(rules, "emu.exe rom.bin", out var tr);
            Expect("trace: fired / refused / skipped",
                tr.Count == 3
                && tr[0].State == RulePipeline.TraceState.Fired
                && tr[1].State == RulePipeline.TraceState.Refused
                && tr[2].State == RulePipeline.TraceState.Skipped
                && tr.All(t => !t.AnchorBroken));
        }
        {
            // An anchored condition whose evaluation FLIPS mid-pipeline: rule 0 is anchored on a
            // token that is absent (group entry records "refused"), rule 1 injects the token, rule 2
            // (same anchored signature) now passes — the commitment the checkbox makes is broken,
            // and the trace is what says so.
            var rules = new List<LaunchRule>
            {
                Anchored(P("-x", filter: "-tok")),
                P("-tok"),
                Anchored(P("-y", filter: "-tok")),
            };
            RulePipeline.PreviewWithTrace(rules, "emu.exe rom.bin", out var tr);
            Expect("trace: a mid-pipeline injection breaks the anchor",
                !tr[0].AnchorBroken && tr[2].AnchorBroken
                && tr[0].State == RulePipeline.TraceState.Refused
                && tr[2].State == RulePipeline.TraceState.Fired);
        }

        // ── canonical signatures (the derived group view's keys) ──
        Expect("signature: entry order and case do not matter",
            Sig("a, b", comma: true, matchAll: true)!.Equals(Sig("B,a", comma: true, matchAll: true)));
        Expect("signature: refinement is strict EVERY-superset",
            Sig("a,b,c", comma: true, matchAll: true)!.Refines(Sig("a, b", comma: true, matchAll: true)!)
            && !Sig("a,b", comma: true, matchAll: true)!.Refines(Sig("a,b,c", comma: true, matchAll: true)!));
        Expect("signature: ANY mode never nests",
            !Sig("a,b", comma: true, matchAll: false)!.Refines(Sig("a", comma: false, matchAll: false)!));
        Expect("signature: only anchored rules have one",
            ProbeSignature.Of(P("-a", filter: "x")) == null && Sig("x") != null);

        Console.WriteLine(_fail == 0 ? "ALL OK" : $"{_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    private static LaunchRule Sfx(string suffix, bool asArg = true, string filter = "")
        => new() { Type = LaunchRule.TypeSuffix, Suffix = suffix, AsArg = asArg, Filter = filter };

    private static LaunchRule[] Suffix(string suffix, bool asArg = true, string filter = "")
        => new[] { Sfx(suffix, asArg, filter) };

    private static LaunchRule Anchored(LaunchRule r) { r.AsGroup = true; return r; }

    private static ProbeSignature? Sig(string filter, bool comma = false, bool matchAll = false)
        => ProbeSignature.Of(Anchored(P("-p", filter: filter, comma: comma, matchAll: matchAll)));

    private static void Expect(string what, bool ok)
    {
        if (ok) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}");
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

    private static void CheckPreview(string what, LaunchRule[] rules, string fullLine, string expected)
    {
        string got = RulePipeline.PreviewExample(rules.ToList(), fullLine);
        if (got == expected) { Console.WriteLine($"  ok    {what}"); return; }
        _fail++;
        Console.WriteLine($"  FAIL  {what}\n        in       : {fullLine}\n        expected : {expected}\n        got      : {got}");
    }

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
