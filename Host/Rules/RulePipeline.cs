// The launch-rule pipeline — BigBoxProfile's EmulatorLauncher.Exec loop, native. Rules run in their
// stored order, each seeing the arguments as its predecessors left them; the marker-removal entries
// (RemoveFilter) are collected across the WHOLE pipeline and applied in one final pass, so a marker
// can route several rules before it is stripped — exactly the original's two-phase contract.
//
// Probes evaluate against exe + arguments joined, lowercase — BigBoxProfile matched on the full
// command line INCLUDING args[0], which is what makes filters like "retroarch" work at all.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rules;

internal static class RulePipeline
{
    private const string Tag = "rules";

    /// <summary>Args an ACTION generated during this walk and flagged for the final strip —
    /// BigBoxProfile's argsToFilterOut (the HID detector's "force remove" per device type): the
    /// generated argument is visible to every downstream rule's probe, then removed before launch.
    /// Thread-static and scoped to one pipeline walk; actions add via AddDynamicMarker.</summary>
    [ThreadStatic] private static List<string>? _dynamicMarkers;

    /// <summary>For actions: flags one generated argument for the end-of-pipeline strip (lowercased,
    /// whole-argument match — same normalization as RemoveFilter markers). No-op outside a walk.</summary>
    internal static void AddDynamicMarker(string arg)
    {
        string key = arg.ToLowerInvariant().Trim();
        if (_dynamicMarkers != null && !_dynamicMarkers.Contains(key)) _dynamicMarkers.Add(key);
    }

    /// <summary>Runs the entity's rules (exclusive resolve) over the launch command. Returns the
    /// exe and arguments to spawn with — unchanged when no rule applies or the module is off. The
    /// exe travels through the pipeline since ChangeExe: an action may retarget it, and Spawn's
    /// working directory follows the exe it is given (BigBoxProfile set WorkingDirExe by hand).</summary>
    public static (string Exe, string Args) Apply(string exePath, string args, string? emulatorId)
    {
        List<LaunchRule> rules;
        try { rules = LaunchRuleStore.Resolve(emulatorId); }
        catch { return (exePath, args); }
        if (rules.Count == 0) return (exePath, args);
        return ApplyRules(rules, exePath, args);
    }

    /// <summary>The pipeline body over an explicit rule list — what the selftest drives directly.</summary>
    public static (string Exe, string Args) ApplyRules(List<LaunchRule> rules, string exePath, string args)
    {
        var cmd = new Actions.RuleCmd(exePath, args);
        var removeMarkers = new List<string>();
        _dynamicMarkers = removeMarkers;   // actions may flag their own generated args for the strip
        try
        {
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.IsConfigured) continue;
            try
            {
                var before = cmd;
                if (ProbePasses(rule, cmd.Exe, cmd.Args))
                {
                    var action = Actions.RuleActions.ByType(rule.Type);
                    // Side effects first, then the transform — the original's ExecuteBefore→Modify
                    // order. The REAL channel only; the preview walk never calls ExecuteBefore.
                    action?.ExecuteBefore(rule, cmd);
                    cmd = action?.Apply(rule, cmd) ?? cmd;
                    if (cmd != before) LbLog.Info(Tag, $"{rule.Type}: → \"{cmd.Exe}\" {cmd.Args}");
                }
                // Markers are collected from CONFIGURED rules whether or not their probe fired this
                // time — BigBoxProfile gathers FiltersToRemoveOnFinalPass over every configured
                // module, so a marker used only to SUPPRESS a rule is still stripped before launch.
                if (rule.RemoveFilter && rule.Filter.Length > 0)
                    removeMarkers.AddRange(SplitList(rule.Filter, rule.CommaFilter));
            }
            catch (Exception ex) { LbLog.Warn(Tag, $"{rule.Type} failed ({ex.Message}) — rule skipped"); }
        }
        }
        finally { _dynamicMarkers = null; }

        if (removeMarkers.Count > 0)
        {
            var kept = RuleArgs.Split(cmd.Args)
                .Where(a => !removeMarkers.Contains(a.ToLowerInvariant().Trim()))
                .ToArray();
            string stripped = RuleArgs.Join(kept);
            if (stripped != cmd.Args)
            {
                LbLog.Info(Tag, $"markers stripped: args → {stripped}");
                cmd = cmd with { Args = stripped };
            }
        }
        return (cmd.Exe, cmd.Args);
    }

    /// <summary>The PREVIEW pipeline — BigBoxProfile's ModifyExemple channel, faithfully separate:
    /// every action has an EXAMPLE treatment that may differ from the real one (RomExtractor's preview
    /// shows the would-be path without extracting; Prefix's is identical to the real thing). The input
    /// is a FULL command line, exe included, as typed in the "Example command IN" box; the output is
    /// the whole line after every configured rule and the final marker pass — what EmulatorConfig's
    /// CalculateExemple computed. A rule that throws is skipped (ModifyExemple's try/catch).</summary>
    public static string PreviewExample(List<LaunchRule> rules, string fullCommandLine)
        => PreviewWithTrace(rules, fullCommandLine, out _);

    /// <summary>One rule's fate for the current example line. Skipped = disabled or not configured;
    /// AnchorBroken = this rule is anchored (AsGroup) and its probe no longer evaluates the way it
    /// did when its group was entered — proof that a rule in between modified the arguments the
    /// group's condition matches on, the very thing the checkbox promises never happens.</summary>
    internal enum TraceState { Fired, Refused, Skipped }
    internal sealed record RuleTrace(TraceState State, bool AnchorBroken);

    /// <summary>PreviewExample with the per-rule trace the grouped view displays. Same walk, same
    /// example channel — the trace is a by-product of the preview, never a second engine.</summary>
    public static string PreviewWithTrace(List<LaunchRule> rules, string fullCommandLine, out List<RuleTrace> trace)
    {
        trace = new List<RuleTrace>();
        var all = RuleArgs.SplitFull(fullCommandLine);
        if (all.Length == 0)
        {
            foreach (var _ in rules) trace.Add(new RuleTrace(TraceState.Skipped, false));
            return fullCommandLine;
        }
        var cmd = new Actions.RuleCmd(all[0], RuleArgs.Join(all.Skip(1)));

        // Group-entry probe results, per canonical signature: the first anchored rule of a signature
        // records how the condition evaluated at group entry; every later member re-evaluates and a
        // mismatch marks it broken.
        var entryResults = new Dictionary<ProbeSignature, bool>();

        var removeMarkers = new List<string>();
        _dynamicMarkers = removeMarkers;   // example channel: same contract (HID's "marker only" args)
        try
        {
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.IsConfigured)
            {
                trace.Add(new RuleTrace(TraceState.Skipped, false));
                continue;
            }
            bool fired = false, broken = false;
            try
            {
                bool passes = ProbePasses(rule, cmd.Exe, cmd.Args);
                var sig = ProbeSignature.Of(rule);
                if (sig != null)
                {
                    if (entryResults.TryGetValue(sig, out bool atEntry)) broken = passes != atEntry;
                    else entryResults[sig] = passes;
                }
                if (passes)
                {
                    fired = true;
                    // The EXAMPLE channel — each action's own ApplyExample (ModifyExemple), which
                    // defaults to the real treatment and diverges where the real one acts on the
                    // world (see IRuleAction).
                    cmd = Actions.RuleActions.ByType(rule.Type)?.ApplyExample(rule, cmd) ?? cmd;
                }
                if (rule.RemoveFilter && rule.Filter.Length > 0)
                    removeMarkers.AddRange(SplitList(rule.Filter, rule.CommaFilter));
            }
            catch { /* the example never breaks the page */ }
            trace.Add(new RuleTrace(fired ? TraceState.Fired : TraceState.Refused, broken));
        }
        }
        finally { _dynamicMarkers = null; }

        var kept = RuleArgs.Split(cmd.Args)
            .Where(a => !removeMarkers.Contains(a.ToLowerInvariant().Trim()));
        return RuleArgs.Join(new[] { cmd.Exe }.Concat(kept));
    }

    // ── probes ────────────────────────────────────────────────────────────────

    /// <summary>The shared filter/exclude block. Haystack = exe + args, case-insensitive substring.
    /// Filter: single = must contain; comma list = ANY entry, or ALL with MatchAllFilter.
    /// Exclude: single/comma = block on ANY match; MatchAllExclude = block only when ALL entries are
    /// present (the checkbox's stated INTENT — the original code made the flag unsatisfiable).</summary>
    private static bool ProbePasses(LaunchRule rule, string exePath, string args)
    {
        string hay = (exePath + " " + args).ToLowerInvariant();

        if (rule.Filter.Length > 0)
        {
            var entries = SplitList(rule.Filter, rule.CommaFilter);
            if (entries.Count > 0)
            {
                int found = entries.Count(e => hay.Contains(e));
                if (found == 0) return false;
                if (rule.CommaFilter && rule.MatchAllFilter && found < entries.Count) return false;
            }
        }

        if (rule.Exclude.Length > 0)
        {
            var entries = SplitList(rule.Exclude, rule.CommaExclude);
            if (entries.Count > 0)
            {
                int found = entries.Count(e => hay.Contains(e));
                bool blocked = rule.CommaExclude && rule.MatchAllExclude
                    ? found == entries.Count
                    : found > 0;
                if (blocked) return false;
            }
        }
        return true;
    }

    /// <summary>Filter text → lowercase trimmed entries ("a, b" with comma mode; the whole text as one
    /// entry otherwise). Blank entries are dropped, as in BigBoxUtils.explode consumers.</summary>
    private static List<string> SplitList(string text, bool comma)
    {
        var raw = comma ? text.Split(',') : new[] { text };
        return raw.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).ToList();
    }

    // ── actions ───────────────────────────────────────────────────────────────

}
