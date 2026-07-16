// BigBox's native parental PIN, managed directly by LiteBox.
//
// The PIN lives as an encrypted <LockPin> blob in LB\Data\BigBoxSettings.xml (Rijndael-256, fixed LB
// key/seed — see LbSettingsCrypto.DecryptBigBoxLockPin). Historically the ExtendDB plugin kept a SEPARATE
// parental code because the BigBox one couldn't be read; the cipher has since been recovered, so LiteBox
// reads, sets and clears THE pin BigBox itself uses — one PIN everywhere, and BigBox sees any change made
// here on its next start.
//
// Writes preserve the rest of the file verbatim (surgical string replace of the one element, not an XML
// round-trip) so BigBox's own formatting and every other setting stay untouched.

#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Data;

internal static class BigBoxPin
{
    private static string? SettingsPath()
    {
        try
        {
            var root = MediaResolver.LbRoot;
            if (string.IsNullOrEmpty(root)) return null;
            var p = Path.Combine(root, "Data", "BigBoxSettings.xml");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>BigBoxSettings.xml exists (a BigBox has run at least once on this install).</summary>
    public static bool Available => SettingsPath() != null;

    /// <summary>The clear 4-digit PIN currently set in BigBox, or "" when no PIN is set.</summary>
    public static string Current()
    {
        try
        {
            var path = SettingsPath();
            if (path == null) return "";
            var m = Regex.Match(File.ReadAllText(path), "<LockPin>([^<]*)</LockPin>");
            return m.Success ? LbSettingsCrypto.DecryptBigBoxLockPin(m.Groups[1].Value.Trim()) : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Sets (or clears, when <paramref name="clearPin"/> is empty) BigBox's parental PIN. The new blob is
    /// byte-compatible with what BigBox writes itself. Returns false when BigBoxSettings.xml is absent or
    /// the write fails.
    /// </summary>
    public static bool Set(string? clearPin)
    {
        try
        {
            var path = SettingsPath();
            if (path == null) return false;
            var xml = File.ReadAllText(path);

            string blob = string.IsNullOrEmpty(clearPin) ? "" : LbSettingsCrypto.EncryptBigBoxLockPin(clearPin);
            string replacement = $"<LockPin>{blob}</LockPin>";

            string updated = Regex.IsMatch(xml, "<LockPin>[^<]*</LockPin>")
                ? Regex.Replace(xml, "<LockPin>[^<]*</LockPin>", replacement)
                : InsertIntoSettingsBlock(xml, replacement);
            if (updated == xml) return true;

            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>When the element is missing entirely, add it inside the first settings block (right after
    /// its opening tag) — matching where BigBox keeps it. No-op (returns the input) when no block is found.</summary>
    private static string InsertIntoSettingsBlock(string xml, string element)
    {
        var m = Regex.Match(xml, @"(<BigBoxSettings>\s*)");
        if (!m.Success) return xml;
        int at = m.Index + m.Length;
        return xml.Substring(0, at) + "  " + element + Environment.NewLine + "    " + xml.Substring(at);
    }
}
