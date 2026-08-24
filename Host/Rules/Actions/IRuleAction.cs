// The action contract — BigBoxProfile's IEmulatorAction, reborn for the LiteBox model (Mehdi's
// pattern, and he was right to insist: with a dozen actions coming, switch-based dispatch scattered
// over model, pipeline, panel and dialog is shotgun surgery; here EVERYTHING one action is lives in
// its one file, and the registry below is the only list to touch when the next one lands).
//
// What stays shared, on purpose: the probe block (filter/exclude/marker/group — every action carries
// the same one, as in the original), the marker final pass, the storage class (LaunchRule is a
// SUPERSET of typed fields rather than BigBoxProfile's per-action string dictionary — the schema is
// centralized, the behaviour is not; saved rules keep their shape forever), and the dialog SHELL
// (enabled box + Action group + probes + reflow) — an action only provides the Action group's body.

#nullable enable

using System;
using System.Linq;
using System.Windows.Forms;

namespace LbApiHost.Host.Rules.Actions;

internal interface IRuleAction
{
    /// <summary>The discriminator stored in <see cref="LaunchRule.Type"/>.</summary>
    string Type { get; }

    /// <summary>"Add: Prefix…" — the rule page generates its Add buttons from the registry.</summary>
    string AddLabel { get; }

    /// <summary>"Prefix rule" — the dialog title.</summary>
    string DialogTitle { get; }

    bool IsConfigured(LaunchRule r);

    /// <summary>The action clause of the rule's one-line description — the shared code appends the
    /// probe clauses ([Only if…], [remove marker], …) itself.</summary>
    string Describe(LaunchRule r);

    /// <summary>The REAL channel: transform the argument string (the exe is never part of it).</summary>
    string Apply(LaunchRule r, string args);

    /// <summary>The EXAMPLE channel (ModifyExemple): what the preview shows. Defaults to the real
    /// treatment — override when the real one has side effects or reads state no example holds.</summary>
    string ApplyExample(LaunchRule r, string args) => Apply(r, args);

    /// <summary>The Action group's BODY for the edit dialog: the controls, their height, and the
    /// save that writes the controls back to the rule when OK lands.</summary>
    (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS);
}

/// <summary>The one list to grow per ported action — ordered as the Add buttons appear.</summary>
internal static class RuleActions
{
    public static readonly IRuleAction[] All =
    {
        new PrefixAction(),
        new SuffixAction(),
    };

    public static IRuleAction? ByType(string type)
        => All.FirstOrDefault(a => string.Equals(a.Type, type, StringComparison.OrdinalIgnoreCase));
}
