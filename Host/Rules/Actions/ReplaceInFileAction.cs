// Replace in file — the "as File" mode of BigBoxProfile's Replace, promoted to its own action
// (Mehdi's call): semantically another animal entirely — a SIDE EFFECT on disk, not a line
// transform. It inaugurates ExecuteBefore on the interface: the real channel runs it once the probe
// passed, right before the spawn; the EXAMPLE channel never calls side effects, by construction.
//
// Original semantics kept whole: the file's content goes through the same search/replace core (with
// Singleline, the file mode's flag), variables expand the search, the replacement, the FILE PATH
// and the result, and the write only happens when something actually changed. And as in the
// original, the change is PERMANENT — no restore at game end; this is config poking, and whoever
// pokes a config wants it poked. The original's MessageBox-on-error in the middle of a launch
// became a log line.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Rules.Actions;

internal sealed class ReplaceInFileAction : IRuleAction
{
    public string Type => LaunchRule.TypeReplaceInFile;
    public string AddLabel => "Add: Replace in file…";
    public string DialogTitle => "Replace in file rule";

    public bool IsConfigured(LaunchRule r) => r.Search.Length > 0 && r.TargetFile.Length > 0;

    public string Describe(LaunchRule r)
        => "Replace in file " + r.TargetFile + " : \"" + r.Search + "\" → \"" + r.ReplaceWith + "\""
           + (r.UseRegex ? " [regex]" : "") + (r.CaseSensitive ? " [case]" : "");

    /// <summary>The line is untouched — this action's whole work is the side effect below.</summary>
    public RuleCmd Apply(LaunchRule r, RuleCmd cmd) => cmd;

    public void ExecuteBefore(LaunchRule r, RuleCmd cmd)
    {
        try
        {
            var vars = RuleVariables.Parse(r.VariablesData);
            string file = RuleVariables.Expand(r.TargetFile, vars, cmd.Exe, cmd.Args);
            if (!File.Exists(file))
            {
                LbLog.Warn("rules", $"ReplaceInFile: \"{file}\" does not exist — skipped");
                return;
            }
            string search = RuleVariables.Expand(r.Search, vars, cmd.Exe, cmd.Args);
            string replace = RuleVariables.Expand(r.ReplaceWith, vars, cmd.Exe, cmd.Args);

            string content = File.ReadAllText(file);
            string rewritten = RuleVariables.DoReplace(content, search, replace, r.UseRegex, r.CaseSensitive, singleline: true);
            rewritten = RuleVariables.Expand(rewritten, vars, cmd.Exe, cmd.Args);
            if (!string.Equals(content, rewritten, StringComparison.Ordinal))
            {
                File.WriteAllText(file, rewritten);
                LbLog.Info("rules", $"ReplaceInFile: \"{file}\" rewritten");
            }
        }
        catch (Exception ex) { LbLog.Warn("rules", $"ReplaceInFile failed ({ex.Message}) — file left alone"); }
    }

    public (Control Body, int Height, Action Save) BuildActionUi(LaunchRule r, float dpiS)
        => ReplaceUi.Build(r, dpiS, withFile: true);
}
