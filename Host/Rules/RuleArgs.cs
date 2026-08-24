// Argument-list ⇄ command-line conversions for the launch rules — faithful ports of the two
// BigBoxUtils helpers every BigBoxProfile action leans on, because the rules' semantics are DEFINED
// in terms of them: "as argument" inserts into the parsed list, "as cmdLine" edits the joined string
// and re-parses, and the marker-removal pass compares whole parsed arguments.
//
// Parsing is CommandLineToArgvW — the same parser Windows gives the emulator, so what a rule sees is
// exactly what the spawned process would. Joining is the standard re-quoting (quote when whitespace
// or quotes are present, backslash-escape embedded quotes) — BigBoxUtils.ArgsToCommandLine's contract.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LbApiHost.Host.Rules;

internal static class RuleArgs
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Splits an argument STRING (no exe) into arguments, Windows' own way. BigBoxProfile's
    /// CommandLineToArgs with its addfakeexe trick: a fake exe token keeps CommandLineToArgvW from
    /// treating the first argument with its special program-name rules.</summary>
    public static string[] Split(string argumentString)
    {
        if (string.IsNullOrWhiteSpace(argumentString)) return Array.Empty<string>();
        var result = CommandLineToArgvW("fake.exe " + argumentString, out int count);
        if (result == IntPtr.Zero) return new[] { argumentString };
        try
        {
            var args = new string[count - 1];
            for (int i = 1; i < count; i++)
                args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(result, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally { LocalFree(result); }
    }

    /// <summary>Joins arguments back into one command-line string (BigBoxUtils.ArgsToCommandLine):
    /// arguments containing whitespace or quotes are quoted, embedded quotes backslash-escaped.</summary>
    public static string Join(IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                sb.Append(argument);
                continue;
            }
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    sb.Append(c);
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);   // trailing backslashes double before the closing quote
            sb.Append('"');
        }
        return sb.ToString();
    }
}
