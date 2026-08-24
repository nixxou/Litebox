// Prefix — BigBoxProfile's first and simplest action, and the mould the family is cast in.
// Everything Prefix IS lives here: its configuredness, its description, both channels, its dialog
// body. The engine, the panel and the dialog know it only through IRuleAction.

#nullable enable

using System;
using System.Linq;
using System.Windows.Forms;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class PrefixAction : IRuleAction
{
    public string Type => LaunchRule.TypePrefix;
    public string AddLabel => "Add: Prefix…";
    public string DialogTitle => "Prefix rule";

    public bool IsConfigured(LaunchRule r) => r.Prefix.Length > 0;

    public string Describe(LaunchRule r)
        => (r.AsArg ? "Prefix this to the Arg List : " : "Prefix this to the command line : ") + r.Prefix;

    /// <summary>As ARGUMENT = one token (trimmed) inserted before every existing argument; as
    /// CMDLINE = the text prepended verbatim to the joined argument string — which is how one prefix
    /// can inject several arguments. The exe is untouched by construction: rules receive the
    /// argument string WITHOUT args[0], the separation BigBoxProfile re-created by hand.</summary>
    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
        => cmd with
        {
            Args = r.AsArg
                ? RuleArgs.Join(new[] { r.Prefix.Trim() }.Concat(RuleArgs.Split(cmd.Args)))
                : r.Prefix + cmd.Args,
        };

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
        => PayloadUi.Build(
            "Prefix to add:",
            "One argument (the text becomes a single token)",
            "Raw command-line text (may carry several arguments)",
            r.Prefix, r.AsArg, dpiS,
            (payload, asArg) => { r.Prefix = payload; r.AsArg = asArg; });
}
