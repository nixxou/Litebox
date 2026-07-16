// Credentials + endpoint config for the Extended-database module's media fetch.
//
// Two kinds of secret, kept OUT of this public source tree:
//   • The USER's ScreenScraper account (ssid + password). Lives in LiteBox.ini [Base]; the password is stored
//     as a LiteBox-own encrypted blob (LbSettingsCrypto.EncryptLocal), never in clear.
//   • The plugin's fixed ScreenScraper DEVELOPER credentials (devid/devpassword/softname). These are NOT in
//     source — they are read at runtime from an encrypted, gitignored file deployed next to the config:
//     Core\litebox\config\ss-dev.dat. When the file is absent, DevCreds is null and the media fetch simply
//     skips the ScreenScraper-API source (the other sources still work). The .dat is produced locally with
//     WriteDevCredsFile (a maintenance one-off), so the clear dev creds never touch the repo or the source.
//
// The remote image mirror base URL (the obfuscated ExtendDB CDN prefix) is also config, defaulting to the
// public host — the secret routing prefix, if any, is the user's to set in LiteBox.ini, not shipped here.

#nullable enable

using System;
using System.IO;
using System.Text;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Media;

internal static class BaseCredentials
{
    public const string Section = "Base";

    // ── ScreenScraper user account (LiteBox.ini [Base]) ───────────────────────

    /// <summary>(ssid, clear password) the user configured, or null when no account is set.</summary>
    public static (string User, string Password)? UserAccount()
    {
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            var user = cfg.GetSec(Section, "ScreenScraperUser", "") ?? "";
            var pw = LbSettingsCrypto.DecryptLocal(cfg.GetSec(Section, "ScreenScraperPassword", "") ?? "");
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pw)) return null;
            return (user.Trim(), pw);
        }
        catch { return null; }
    }

    /// <summary>Persist the ScreenScraper account into LiteBox.ini [Base] (password encrypted). Pass empty to clear.</summary>
    public static void SetUserAccount(string? user, string? clearPassword)
    {
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            cfg.SetSec(Section, "ScreenScraperUser", (user ?? "").Trim());
            cfg.SetSec(Section, "ScreenScraperPassword", string.IsNullOrEmpty(clearPassword) ? "" : LbSettingsCrypto.EncryptLocal(clearPassword));
            cfg.Save();
        }
        catch { }
    }

    // ── Remote image mirror base URL (LiteBox.ini [Base]) ─────────────────────

    /// <summary>Base URL of the ExtendDB image mirror, verbatim from config (any /image/&lt;prefix&gt; routing
    /// segment included). Defaults to the public host. Trailing slash trimmed.</summary>
    public static string RemoteImageBaseUrl()
    {
        try
        {
            var v = LiteBoxConfig.LoadForExe().GetSec(Section, "RemoteImageBaseUrl", "") ?? "";
            v = v.Trim();
            return string.IsNullOrEmpty(v) ? "https://extenddb.com" : v.TrimEnd('/');
        }
        catch { return "https://extenddb.com"; }
    }

    // ── ScreenScraper developer credentials (encrypted, gitignored .dat) ──────

    /// <summary>The shipped ScreenScraper dev credentials, or null when the deployed .dat is absent (→ the SS-API
    /// source self-disables and the fetch falls through to the other sources).</summary>
    public static (string DevId, string DevPassword, string SoftName)? DevCreds()
    {
        try
        {
            var path = DevCredsPath();
            if (!File.Exists(path)) return null;
            var clear = LbSettingsCrypto.DecryptLocal(File.ReadAllText(path, Encoding.UTF8).Trim());
            var parts = clear.Split('\n');
            if (parts.Length < 3) return null;
            var id = parts[0].Trim(); var pw = parts[1].Trim(); var soft = parts[2].Trim();
            if (id.Length == 0 || pw.Length == 0 || soft.Length == 0) return null;
            return (id, pw, soft);
        }
        catch { return null; }
    }

    private static string DevCredsPath()
        => Path.Combine(LiteBoxPaths.Data, "config", "ss-dev.dat");

    /// <summary>Maintenance one-off: write the encrypted dev-creds .dat from clear values (run locally; the .dat
    /// is gitignored and deployed with the build — the clear creds never enter source or the repo).</summary>
    public static void WriteDevCredsFile(string devId, string devPassword, string softName)
    {
        var dir = Path.GetDirectoryName(DevCredsPath())!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(DevCredsPath(), LbSettingsCrypto.EncryptLocal($"{devId}\n{devPassword}\n{softName}"), Encoding.UTF8);
    }
}
