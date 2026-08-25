// One HID matcher — BigBoxProfile's HIDMatcher, whole. A matcher picks its backends (any mix of the
// seven), regexes each line of their combined dump, and for every matching line emits its Suffix
// with "\1".."\9" spliced from the capture groups (the house syntax). UniqueMatch collapses
// duplicate suffixes — and, faithfully, a collapsed duplicate does NOT count toward MaxMatch, while
// in non-unique mode every match does. MaxMatch caps how many this matcher may emit (0 = unlimited,
// the original's == comparison never firing). Returns null on no match at all — the action treats
// null and empty alike but the distinction is the original's signature, kept.
//
// Storage is our own System.Text.Json list inside the rule's HidData blob — semantics imported,
// serialization not.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LbApiHost.Host.Rules.Hid;

internal sealed class HidMatcher
{
    public string RegexToMatch { get; set; } = "";
    public string Suffix { get; set; } = "";
    /// <summary>"controller" | "lightgun" | "wheel" | "other" — which quota/prefix bucket feeds.</summary>
    public string DeviceType { get; set; } = "controller";
    public bool UseHidSharp { get; set; }
    public bool UseDs4Lib { get; set; }
    public bool UseBt { get; set; }
    public bool UseXInput { get; set; }
    public bool UseDInput { get; set; }
    public bool UseSdl { get; set; }
    public bool UseSdlNoRI { get; set; }
    public int MaxMatch { get; set; } = 1;
    public bool UniqueMatch { get; set; }

    /// <summary>The backends this matcher reads, dumped through the launch cache.</summary>
    public string LibData(string ds4WinLogPath)
    {
        string data = "";
        if (UseHidSharp) data += HidInfoCache.HidSharpInfo();
        if (UseDs4Lib) data += HidInfoCache.Ds4Info();
        if (UseBt) data += HidInfoCache.BtInfo();
        if (UseXInput) data += HidInfoCache.XInputInfo(ds4WinLogPath);
        if (UseDInput) data += HidInfoCache.DInputInfo();
        if (UseSdl) data += HidInfoCache.SdlInfo();
        if (UseSdlNoRI) data += HidInfoCache.SdlNoRIInfo();
        return data;
    }

    /// <summary>The original's isMatching: line-by-line regex over the combined dump; suffixes out,
    /// null when nothing matched. Per-line try/catch as there (a bad regex just yields nothing).</summary>
    public string[]? Match(string ds4WinLogPath)
    {
        var suffixes = new List<string>();
        int count = 0;
        using var reader = new StringReader(LibData(ds4WinLogPath));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            try
            {
                var m = Regex.Match(line, RegexToMatch);
                if (!m.Success) continue;
                string suffixOut = Suffix;
                // The original iterated i = 1 .. Groups.Count INCLUSIVE — the last index resolves to
                // an empty unmatched group, so "\N" one past the captures blanks out. Kept verbatim.
                for (int i = 1; i <= m.Groups.Count; i++)
                    suffixOut = suffixOut.Replace($"\\{i}", m.Groups[i].Value);
                if (UniqueMatch)
                {
                    if (!suffixes.Contains(suffixOut)) { suffixes.Add(suffixOut); count++; }
                }
                else { suffixes.Add(suffixOut); count++; }
                if (count == MaxMatch) return suffixes.ToArray();
            }
            catch { }
        }
        return suffixes.Count > 0 ? suffixes.ToArray() : null;
    }

    public string Describe()
    {
        var libs = new List<string>();
        if (UseHidSharp) libs.Add("HidSharp");
        if (UseDs4Lib) libs.Add("DS4");
        if (UseBt) libs.Add("BT");
        if (UseXInput) libs.Add("XInput");
        if (UseDInput) libs.Add("DInput");
        if (UseSdl) libs.Add("SDL");
        if (UseSdlNoRI) libs.Add("SDL-noRI");
        string max = MaxMatch == 1 ? "" : MaxMatch == 0 ? ", all" : $", max {MaxMatch}";
        return $"{DeviceType}: /{RegexToMatch}/ → \"{Suffix}\" [{string.Join("+", libs)}{max}{(UniqueMatch ? ", unique" : "")}]";
    }
}
