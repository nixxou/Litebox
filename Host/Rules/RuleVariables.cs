// The variables system — BigBoxProfile's hidden gem inside Replace, extracted as the TRANSVERSE
// facility it always conceptually was (nothing tied it to Replace but wiring). A variable is a
// token ("{ROM}") whose value is EXTRACTED at expansion time: a regex applied to a source — the
// command line, each argument (last match wins, the original's rule), or a file's content — with
// capture groups splicing into the value (the "\1" house syntax) and a fallback when nothing
// matches. Expansion is iterative (a value may contain another variable's token) and capped, the
// original's own recursion guard.
//
// The "ahk" source is deliberately NOT here (Mehdi: skip AHK; the slot's future is a C# script
// source via Roslyn, a later lot). Storage is OUR OWN clean JSON list per rule — we are not
// importing BigBoxProfile's serialized dictionaries, only its semantics.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rules;

/// <summary>One variable: token name, source, extraction regex, value template, fallback.</summary>
internal sealed class RuleVariable
{
    /// <summary>The token as it appears in texts, e.g. "{ROM}". Matched case-insensitively.</summary>
    public string Name { get; set; } = "";
    /// <summary>"cmd" (exe + arguments), "arg" (each argument, last match wins), or a FILE PATH
    /// whose content is the source. ("script" is reserved for the future C# source.)</summary>
    public string Source { get; set; } = "cmd";
    /// <summary>The regex applied to the source text.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>The value on match — "\1".."\9" splice the capture groups in (house syntax).</summary>
    public string Value { get; set; } = "";
    /// <summary>The value when nothing matches (or the source is unreadable).</summary>
    public string Fallback { get; set; } = "";
}

internal static class RuleVariables
{
    private const string Tag = "rules";
    private const int MaxRounds = 10;   // the original's recursion guard, kept as-is

    public static List<RuleVariable> Parse(string variablesData)
    {
        if (string.IsNullOrWhiteSpace(variablesData)) return new List<RuleVariable>();
        try { return JsonSerializer.Deserialize<List<RuleVariable>>(variablesData) ?? new List<RuleVariable>(); }
        catch { return new List<RuleVariable>(); }
    }

    public static string Serialize(List<RuleVariable> vars)
        => vars.Count == 0 ? "" : JsonSerializer.Serialize(vars);

    /// <summary>Expands every known token in <paramref name="text"/>, iteratively — a resolved value
    /// may itself contain another token — capped at the original's ten rounds.</summary>
    public static string Expand(string text, List<RuleVariable> vars, string exePath, string args)
    {
        if (vars.Count == 0 || string.IsNullOrEmpty(text)) return text;
        for (int round = 0; round < MaxRounds; round++)
        {
            bool any = false;
            foreach (var v in vars)
            {
                if (v.Name.Length == 0) continue;
                if (text.IndexOf(v.Name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                any = true;
                string value = Resolve(v, exePath, args);
                text = Regex.Replace(text, Regex.Escape(v.Name), value.Replace("$", "$$"),
                    RegexOptions.IgnoreCase);
            }
            if (!any) break;
        }
        return text;
    }

    /// <summary>One variable's resolved value — public for the sandboxes: the dialogs show each
    /// variable's live value for a test line.</summary>
    public static string ResolveOne(RuleVariable v, string exePath, string args) => Resolve(v, exePath, args);

    /// <summary>One variable's value for this launch: the regex over its source, groups spliced into
    /// Value via the "\1" house syntax, Fallback when nothing matches anywhere. The "arg" source
    /// tries every argument and the LAST match wins — the original's exact behaviour.</summary>
    private static string Resolve(RuleVariable v, string exePath, string args)
    {
        try
        {
            var sources = new List<string>();
            if (string.Equals(v.Source, "cmd", StringComparison.OrdinalIgnoreCase))
                sources.Add(exePath + " " + args);
            else if (string.Equals(v.Source, "arg", StringComparison.OrdinalIgnoreCase))
                sources.AddRange(RuleArgs.Split(args));
            else if (v.Source.Length > 0 && File.Exists(v.Source))
                sources.Add(File.ReadAllText(v.Source));

            var options = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline;
            string result = v.Fallback;
            foreach (var src in sources)
            {
                if (string.IsNullOrEmpty(src)) continue;
                var m = Regex.Match(src, v.Pattern, options);
                if (!m.Success) continue;
                string value = v.Value;
                for (int i = 1; i < m.Groups.Count; i++)
                    value = value.Replace("\\" + i, m.Groups[i].Value);
                result = value;
            }
            return result;
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, $"variable {v.Name} failed ({ex.Message}) — fallback used");
            return v.Fallback;
        }
    }

    /// <summary>The search/replace core both Replace actions share: literal (escaped, the "$"
    /// protected) or regex with the "\1".."\9" house syntax in the replacement — BigBoxProfile's
    /// MatchEvaluator2, whole. <paramref name="singleline"/> adds Singleline, the file mode's flag.</summary>
    public static string DoReplace(string input, string search, string replace,
        bool useRegex, bool caseSensitive, bool singleline = false)
    {
        var options = RegexOptions.Multiline;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        if (singleline) options |= RegexOptions.Singleline;
        if (!useRegex)
            return Regex.Replace(input, Regex.Escape(search), replace.Replace("$", "$$"), options);
        return new Regex(search, options).Replace(input, m =>
        {
            string rw = replace;
            for (int i = 1; i < m.Groups.Count; i++)
                rw = rw.Replace("\\" + i, m.Groups[i].Value);
            return rw;
        });
    }
}
