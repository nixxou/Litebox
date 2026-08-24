// Suffix — Prefix's mirror, with the original's two nuances kept: the payload goes at the very END
// (no exe to detach), and as CMDLINE it appends VERBATIM — cmd + suffix, no separator, the leading
// space is the author's to include (the dialog wording says so).

#nullable enable

using System;
using System.Linq;
using System.Windows.Forms;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class SuffixAction : IRuleAction
{
    public string Type => LaunchRule.TypeSuffix;
    public string AddLabel => "Add: Suffix…";
    public string DialogTitle => "Suffix rule";

    public bool IsConfigured(LaunchRule r) => r.Suffix.Length > 0;

    public string Describe(LaunchRule r)
        => (r.AsArg ? "Suffix this to the Arg List : " : "Suffix this to the command line : ") + r.Suffix;

    public string Apply(LaunchRule r, string args)
        => r.AsArg
            ? RuleArgs.Join(RuleArgs.Split(args).Concat(new[] { r.Suffix.Trim() }))
            : args + r.Suffix;

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
        => PayloadUi.Build(
            "Suffix to add:",
            "One argument (appended as a single token)",
            "Raw command-line text (appended verbatim — include the leading space yourself)",
            r.Suffix, r.AsArg, dpiS,
            (payload, asArg) => { r.Suffix = payload; r.AsArg = asArg; });
}
