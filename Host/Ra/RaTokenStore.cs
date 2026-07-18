// Surgical read/write of RetroAchievements' session token in LaunchBox's Data\Settings.xml.
//
// The token (RetroAchievementsToken) is what RetroArch reads (cheevos_token) to unlock achievements in
// game; LaunchBox stores it in clear in Settings.xml but never refreshes it, so it eventually expires —
// the problem the auto-renew (RaTokenRenew) fixes. This writes ONLY that one element, byte-for-byte
// leaving the rest of the file untouched (same surgical approach as the plugin's BigBox LockPin editor),
// so the real LaunchBox / BigBox reads it back without surprises.
//
// Username and API key are left where LaunchBox keeps them (the options UI edits those through the normal
// settings store); this helper is only about the token, because it is the one value rewritten in the
// background outside the options window.

#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Ra;

internal static class RaTokenStore
{
    private static string SettingsPath
        => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Settings.xml");

    /// <summary>The current token in Settings.xml ("" when unset / file missing).</summary>
    public static string Current()
    {
        try
        {
            string file = SettingsPath;
            if (!File.Exists(file)) return "";
            string xml = File.ReadAllText(file);
            var m = Regex.Match(xml, @"<RetroAchievementsToken>([^<]*)</RetroAchievementsToken>");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        }
        catch { return ""; }
    }

    /// <summary>The username in Settings.xml ("" when unset). Needed by the renewal to know who to log in.</summary>
    public static string Username()
    {
        try
        {
            string file = SettingsPath;
            if (!File.Exists(file)) return "";
            string xml = File.ReadAllText(file);
            var m = Regex.Match(xml, @"<RetroAchievementsUsername>([^<]*)</RetroAchievementsUsername>");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>Write the token into Settings.xml, editing only that element (content form, self-closing
    /// form, or — absent — inserted before &lt;/Settings&gt;). Returns false on any failure. The file's
    /// existing encoding (UTF-8 with/without BOM) is preserved so LaunchBox reads it back unchanged.</summary>
    public static bool Write(string token)
    {
        try
        {
            string file = SettingsPath;
            if (!File.Exists(file)) return false;

            byte[] raw = File.ReadAllBytes(file);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string xml = new UTF8Encoding(false).GetString(bom ? Slice(raw, 3) : raw);

            string enc = System.Net.WebUtility.HtmlEncode(token ?? "");
            string el = $"<RetroAchievementsToken>{enc}</RetroAchievementsToken>";

            string updated;
            if (Regex.IsMatch(xml, @"<RetroAchievementsToken>[^<]*</RetroAchievementsToken>"))
                updated = Regex.Replace(xml, @"<RetroAchievementsToken>[^<]*</RetroAchievementsToken>", el);
            else if (Regex.IsMatch(xml, @"<RetroAchievementsToken\s*/>"))
                updated = Regex.Replace(xml, @"<RetroAchievementsToken\s*/>", el);
            else if (xml.Contains("</Settings>"))
                updated = xml.Replace("</Settings>", "  " + el + Environment.NewLine + "</Settings>");
            else
                return false;

            if (ReferenceEquals(updated, xml) || updated == xml) return true;   // nothing changed
            File.WriteAllText(file, updated, new UTF8Encoding(bom));
            return true;
        }
        catch { return false; }
    }

    private static byte[] Slice(byte[] a, int start)
    {
        var r = new byte[a.Length - start];
        Array.Copy(a, start, r, 0, r.Length);
        return r;
    }
}
