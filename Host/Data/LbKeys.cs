// LbKeys — the ONE place LiteBox resolves every LaunchBox key / secret / token it needs, at boot.
//
// Before this, key material was scattered: the settings cipher id in LbSettingsCrypto, the MAME leaderboard
// key inline in MameUpload, the gamesdb token read ad-hoc from Settings.xml at each call site. This facade
// centralises both the DEFINITION (the fixed app-wide constants we RE'd) and the RETRIEVAL (the per-install
// values read from Data\Settings.xml). Crypto itself stays in LbSettingsCrypto; this only surfaces the keys.
//
// Two kinds of value:
//   • Fixed LaunchBox constants (same for every 13.x user; RE'd, not personal) — the MAME leaderboard key/IV.
//     Safe here in source (same category as any protocol constant). The BigBox LockPin pair lives in
//     LbSettingsCrypto for the same reason.
//   • Per-install values read at runtime from Settings.xml — the gamesdb CloudAuthenticationToken (clear),
//     the EmuMovies user id + (decrypted) password, and the settings cipher id (via LbSettingsCrypto). NEVER
//     hardcoded: a hardcoded per-install value only works on the one machine it was lifted from.
//
// Settings.xml is re-read per access (cheap; a few elements) rather than cached, because values like the token
// change under us — the Connect-to-LaunchBox login rewrites CloudAuthenticationToken, and the EmuMovies options
// page rewrites the password blob. Callers always see the current value.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LbApiHost.Host.Data;

internal static class LbKeys
{
    // ── Fixed LaunchBox constants (RE'd; app-wide, not personal) ──────────────────────────────
    /// <summary>The MAME community-leaderboard blob key/IV (Rijndael-256/CBC). Captured + verified byte-identical
    /// on LB 13.27 and 13.28. See memory reference-lb-mame-leaderboards.</summary>
    public const string MameKey = "d6a78237aafb4c9eb2a5c0339e019abc";
    public const string MameIv  = "d5c7c1658e2146ff998af26432c2b2a4";

    // ── Per-install values, read from Data\Settings.xml at runtime ────────────────────────────
    private static string SettingsPath
        => Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "Data", "Settings.xml");

    /// <summary>Read one direct child of LaunchBox/Settings by local name (trimmed), or "" if absent.</summary>
    private static string ReadSetting(string name)
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return "";
            var doc = XDocument.Load(path);
            var settings = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings");
            return settings?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>The LaunchBox Games Database (gamesdb.launchbox-app.com) auth token — a GUID stored IN CLEAR as
    /// LaunchBox/Settings/CloudAuthenticationToken. Empty when the user isn't signed in. Written by LaunchBox's
    /// Connect flow (and by LiteBox's own Connect-to-LaunchBox login), so read fresh each time.</summary>
    public static string GamesDbToken => ReadSetting("CloudAuthenticationToken");
    public static bool HasGamesDbToken => GamesDbToken.Length > 0;

    /// <summary>Write the gamesdb token into Settings.xml, editing ONLY that element (content, self-closing, or
    /// inserted before &lt;/Settings&gt;) and preserving the file's BOM/encoding so the real LaunchBox reads it
    /// back unchanged — same surgical approach as RaTokenStore. Returns false on any failure.</summary>
    public static bool WriteGamesDbToken(string token)
    {
        try
        {
            // Internal guard (read-only / LB running): the byte-preserving surgical write below is
            // exactly what LB would clobber on exit — refuse rather than write something doomed.
            if (WriteGuard.Refuse(out var why)) { Console.WriteLine("[keys] gamesdb token write refused: " + why); return false; }
            var file = SettingsPath;
            if (!File.Exists(file)) return false;
            byte[] raw = File.ReadAllBytes(file);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string xml = new UTF8Encoding(false).GetString(bom ? raw[3..] : raw);

            string el = $"<CloudAuthenticationToken>{System.Net.WebUtility.HtmlEncode(token ?? "")}</CloudAuthenticationToken>";
            string updated;
            if (Regex.IsMatch(xml, @"<CloudAuthenticationToken>[^<]*</CloudAuthenticationToken>"))
                updated = Regex.Replace(xml, @"<CloudAuthenticationToken>[^<]*</CloudAuthenticationToken>", el);
            else if (Regex.IsMatch(xml, @"<CloudAuthenticationToken\s*/>"))
                updated = Regex.Replace(xml, @"<CloudAuthenticationToken\s*/>", el);
            else if (xml.Contains("</Settings>"))
                updated = xml.Replace("</Settings>", "  " + el + Environment.NewLine + "</Settings>");
            else
                return false;

            if (updated == xml) return true;
            File.WriteAllText(file, updated, new UTF8Encoding(bom));
            return true;
        }
        catch { return false; }
    }

    /// <summary>EmuMovies account id (clear) and the decrypted password (blob → clear via the per-install
    /// settings cipher). Empty when unset.</summary>
    public static string EmuMoviesUserId => ReadSetting("EmuMoviesUserId");
    public static string EmuMoviesPasswordClear => LbSettingsCrypto.DecryptEmuMoviesPassword(ReadSetting("EmuMoviesPassword"));

    /// <summary>True when this install has the settings cipher id (LaunchBox/Settings/ID) that the EmuMovies /
    /// per-install crypto needs. The boot guard already blocks the no-id case; this mirrors it for callers.</summary>
    public static bool HasSettingsId => LbSettingsCrypto.HasSettingsId;

    /// <summary>Log a one-line inventory of what resolved at boot (no secret values — presence only), so the
    /// debug log shows the key state without ever printing a password or token.</summary>
    public static void LogSummary()
    {
        Console.WriteLine($"[keys] settingsId={(HasSettingsId ? "ok" : "MISSING")}  gamesdbToken={(HasGamesDbToken ? "present" : "none")}  " +
                          $"emuMovies={(EmuMoviesUserId.Length > 0 ? "user-set" : "none")}  mameKey=fixed");
    }
}
