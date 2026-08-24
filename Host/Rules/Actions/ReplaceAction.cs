// Replace — BigBoxProfile's beast, tamed by splitting (Mehdi's call: the "as File" mode was an
// intruder in its own dialog — it lives in ReplaceInFileAction now). This is the LINE half: search
// and replace over each argument (as Arg) or the joined argument string (as Cmd), literal or regex
// with the "\1".."\9" house syntax, case toggle, the variables system expanding the search, the
// replacement AND the result (the original's exact order). The scarred original code was not
// ported — its SEMANTICS were, pinned by selftests; the dead self-recursive Modify, the commented
// duplicates and the triple expansion loops stayed in the museum.

#nullable enable

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class ReplaceAction : IRuleAction
{
    public string Type => LaunchRule.TypeReplace;
    public string AddLabel => "Add: Replace…";
    public string DialogTitle => "Replace rule";

    public bool IsConfigured(LaunchRule r) => r.Search.Length > 0;

    public string Describe(LaunchRule r)
        => (r.AsArg ? "Replace in each Arg : " : "Replace in the command line : ")
           + "\"" + r.Search + "\" → \"" + r.ReplaceWith + "\""
           + (r.UseRegex ? " [regex]" : "") + (r.CaseSensitive ? " [case]" : "");

    public RuleCmd Apply(LaunchRule r, RuleCmd cmd)
    {
        var vars = RuleVariables.Parse(r.VariablesData);
        string search = RuleVariables.Expand(r.Search, vars, cmd.Exe, cmd.Args);
        string replace = RuleVariables.Expand(r.ReplaceWith, vars, cmd.Exe, cmd.Args);

        string args;
        if (r.AsArg)
            args = RuleArgs.Join(RuleArgs.Split(cmd.Args)
                .Select(a => RuleVariables.DoReplace(a, search, replace, r.UseRegex, r.CaseSensitive)));
        else
            args = RuleVariables.DoReplace(cmd.Args, search, replace, r.UseRegex, r.CaseSensitive);

        // The original expanded variables in the RESULT too — a replacement may introduce tokens.
        args = RuleVariables.Expand(args, vars, cmd.Exe, args);
        return cmd with { Args = args };
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
        => ReplaceUi.Build(r, dpiS, withFile: false);
}
