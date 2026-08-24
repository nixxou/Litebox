// The rom-token search — the heart of Mehdi's unification: ALL rules run once, in their positions,
// on the PRE-resolution line (built with the original rom), and only then does the launch ask what
// became of its rom argument. Three answers, and the whole special-cased rom-source phase collapsed
// into them:
//
//   FOUND      the token survived untouched → normal flow (m3u planning, extraction, substitution).
//   RELOCATED  an argument carries the SAME file name in another directory → the relocation case:
//              alias validation, extraction of the relocated source, substitution of that argument.
//   MISSING    the rules did something else with it (renamed, removed, rewrapped) → resolution is
//              skipped entirely and the line goes to the emulator exactly as the rules made it.
//
// The token's shape is known because the construction inserted it: the full path, or the bare name
// when the emulator uses FileNameWithoutExtensionAndPath (where a rename simply means MISSING — no
// path exists in the line to relocate). Arguments are compared post-Split, so quoting never lies.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host.Rules;

internal enum RomTokenState { Found, Relocated, Missing }

internal static class RomTokenSearch
{
    public static (RomTokenState State, int ArgIndex, string? Relocated) Classify(string[] args, string romAbs, bool nameOnly)
    {
        string token;
        try { token = nameOnly ? Path.GetFileNameWithoutExtension(romAbs) : romAbs; }
        catch { token = romAbs; }

        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], token, StringComparison.OrdinalIgnoreCase))
                return (RomTokenState.Found, i, null);

        // Name-only lines carry no path — a changed token cannot be traced to a file.
        if (nameOnly) return (RomTokenState.Missing, -1, null);

        string fileName;
        try { fileName = Path.GetFileName(romAbs); } catch { return (RomTokenState.Missing, -1, null); }
        if (fileName.Length == 0) return (RomTokenState.Missing, -1, null);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.IndexOf('\\') < 0 && a.IndexOf('/') < 0 && a.IndexOf(':') < 0) continue;   // not path-shaped
            string name;
            try { name = Path.GetFileName(a.Trim().TrimEnd('"')); } catch { continue; }
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return (RomTokenState.Relocated, i, a);
        }
        return (RomTokenState.Missing, -1, null);
    }
}
