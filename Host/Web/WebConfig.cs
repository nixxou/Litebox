// Snapshot of the LiteBox.ini [Web] section, read once when the embedded server (re)starts.
//
// This is the LiteBox replacement for ExtendDB's ExtendDBConfigManager.ExtendDBConfig.<field> seam: the web
// subsystem reads its knobs from here instead of the plugin config. Values are cached (loaded in Reload,
// called from EmbeddedWebServer.Start) so hot paths — e.g. the per-response gzip decision — never touch the
// ini file. The whole-server gate stays LbModules.On(LbModule.Web); these keys only shape which sites mount
// and how responses are written.

#nullable enable

using System;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

internal static class WebConfig
{
    private const string Section = "Web";

    /// <summary>TCP listen port ([Web] Port, default 8080).</summary>
    public static int Port { get; private set; } = 8080;

    /// <summary>Comma/space wildcard IP patterns ([Web] AllowedIps). Non-empty ⇒ bind 0.0.0.0 + per-connection
    /// filter (loopback always allowed); empty ⇒ loopback-only bind.</summary>
    public static string AllowedIps { get; private set; } = "";

    /// <summary>Mount the database site at "/" ([Web] EnableDatabaseSite, default true).</summary>
    public static bool EnableDatabaseSite { get; private set; } = true;

    /// <summary>Mount the LiteBox Web theme at "/launchbox/" ([Web] EnableLiteBoxWeb, default true).</summary>
    public static bool EnableLiteBoxWeb { get; private set; } = true;

    /// <summary>Mount the BigBox Web theme at "/bigbox/" ([Web] EnableBigBoxWeb, default true).</summary>
    public static bool EnableBigBoxWeb { get; private set; } = true;

    /// <summary>gzip application/json responses ≥1 KB when the client accepts gzip ([Web] GzipJson, default true).</summary>
    public static bool GzipJson { get; private set; } = true;

    /// <summary>Re-read the [Web] section from LiteBox.ini into the cached snapshot. Failures leave the last
    /// good values in place.</summary>
    public static void Reload()
    {
        try
        {
            var c = LiteBoxConfig.LoadForExe();
            Port = ParsePort(c.GetSec(Section, "Port"), 8080);
            AllowedIps = c.GetSec(Section, "AllowedIps", "") ?? "";
            EnableDatabaseSite = c.GetSecBool(Section, "EnableDatabaseSite", true);
            EnableLiteBoxWeb = c.GetSecBool(Section, "EnableLiteBoxWeb", true);
            EnableBigBoxWeb = c.GetSecBool(Section, "EnableBigBoxWeb", true);
            GzipJson = c.GetSecBool(Section, "GzipJson", true);
        }
        catch (Exception ex) { LbLog.Warn("web", "config reload failed: " + ex.Message); }
    }

    private static int ParsePort(string raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), out var p) && p is > 0 and <= 65535 ? p : fallback;
}
