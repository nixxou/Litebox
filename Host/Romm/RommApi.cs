// The always-on RomM endpoints: discovery, configuration, and the honest refusals.
//
// /api/heartbeat is the only route that answers before authentication — a client has to be able to tell
// "wrong address" from "wrong password", and upstream leaves it unprotected for the same reason. Its
// booleans are how we announce what this server is NOT: no metadata provider is enabled, there is no setup
// wizard, no OIDC, no scheduled task. A well-behaved client then never asks for scanning or scraping.
//
// Everything under /api that this surface does not implement answers in FastAPI's own error shape
// ({"detail": "…"}), because a client that gets a bare 404 logs "unexpected response" and a client that
// gets {"detail":…} logs the reason.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

internal static class RommApi
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Serialises <paramref name="dto"/> as the response body.</summary>
    public static HttpResponse Json(object dto, int status = 200)
        => HttpResponse.Json(JsonSerializer.Serialize(dto, JsonOpts), status);

    /// <summary>FastAPI's error shape, which is what every RomM client parses on a non-2xx.</summary>
    public static HttpResponse Error(int status, string detail)
        => HttpResponse.Json(JsonSerializer.Serialize(new { detail }, JsonOpts), status);

    /// <summary>CORS preflight: the browser only needs the headers, which the surface stamps on every
    /// response anyway.</summary>
    public static HttpResponse Preflight()
    {
        var r = HttpResponse.PlainText("", 204);
        r.Headers["Access-Control-Max-Age"] = "600";
        return r;
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    public static HttpResponse Heartbeat(RouteContext ctx)
    {
        return Json(new
        {
            SYSTEM = new
            {
                VERSION = RommConfig.RommVersion,
                // Never: the library already exists, and a wizard would offer to create platform folders
                // inside a LaunchBox install.
                SHOW_SETUP_WIZARD = false,
            },
            METADATA_SOURCES = new
            {
                ANY_SOURCE_ENABLED = false,
                IGDB_API_ENABLED = false,
                SS_API_ENABLED = false,
                SS_DEV_CREDENTIALS_SET = false,
                MOBY_API_ENABLED = false,
                STEAMGRIDDB_API_ENABLED = false,
                RA_API_ENABLED = false,
                LAUNCHBOX_API_ENABLED = false,
                HASHEOUS_API_ENABLED = false,
                PLAYMATCH_API_ENABLED = false,
                TGDB_API_ENABLED = false,
                FLASHPOINT_API_ENABLED = false,
                HLTB_API_ENABLED = false,
                LIBRETRO_API_ENABLED = false,
            },
            FILESYSTEM = new
            {
                // The platform folders a scan would find. LiteBox never scans, so this is the mapped
                // platform list — filled once the projection lands.
                FS_PLATFORMS = RommLibrary.PlatformSlugs(),
            },
            EMULATION = new
            {
                // Nothing plays in a browser on THIS surface: the in-browser player lives on the BigBox Web
                // theme, served by the other listener.
                DISABLE_EMULATOR_JS = true,
                DISABLE_RUFFLE_RS = true,
            },
            FRONTEND = new
            {
                DISABLE_USERPASS_LOGIN = false,
                DISABLE_LOGS_VIEWER = true,
                YOUTUBE_BASE_URL = "https://www.youtube.com",
            },
            OIDC = new
            {
                ENABLED = false,
                AUTOLOGIN = false,
                PROVIDER = "",
                RP_INITIATED_LOGOUT = false,
            },
            TASKS = new
            {
                ENABLE_SCHEDULED_RESCAN = false,
                SCHEDULED_RESCAN_CRON = "",
                ENABLE_SCHEDULED_UPDATE_SWITCH_TITLEDB = false,
                SCHEDULED_UPDATE_SWITCH_TITLEDB_CRON = "",
                ENABLE_SCHEDULED_UPDATE_LAUNCHBOX_METADATA = false,
                SCHEDULED_UPDATE_LAUNCHBOX_METADATA_CRON = "",
                ENABLE_SCHEDULED_CONVERT_IMAGES_TO_WEBP = false,
                SCHEDULED_CONVERT_IMAGES_TO_WEBP_CRON = "",
            },
        });
    }

    /// <summary>Per-provider health probe. Every provider is off here, so the honest answer is always
    /// false — upstream returns a bare boolean.</summary>
    public static HttpResponse MetadataHeartbeat(RouteContext ctx)
        => HttpResponse.Json("false");

    public static HttpResponse Config(RouteContext ctx)
    {
        return Json(new
        {
            // The config file is LiteBox.ini, edited from the options page — not through the API.
            CONFIG_FILE_MOUNTED = true,
            CONFIG_FILE_WRITABLE = false,
            CONFIG_FILE_PARSE_ERROR = (string?)null,
            EXCLUDED_PLATFORMS = Array.Empty<string>(),
            EXCLUDED_SINGLE_EXT = Array.Empty<string>(),
            EXCLUDED_SINGLE_FILES = Array.Empty<string>(),
            EXCLUDED_MULTI_FILES = Array.Empty<string>(),
            EXCLUDED_MULTI_PARTS_EXT = Array.Empty<string>(),
            EXCLUDED_MULTI_PARTS_FILES = Array.Empty<string>(),
            DEFAULT_EXCLUDED_DIRS = Array.Empty<string>(),
            DEFAULT_EXCLUDED_FILES = Array.Empty<string>(),
            DEFAULT_EXCLUDED_EXTENSIONS = Array.Empty<string>(),
            PLATFORMS_BINDING = new Dictionary<string, string>(),
            PLATFORMS_VERSIONS = new Dictionary<string, string>(),
            SKIP_HASH_CALCULATION = true,
            EJS_DEBUG = false,
            EJS_CACHE_LIMIT = (int?)null,
            EJS_DISABLE_AUTO_UNLOAD = false,
            EJS_DISABLE_BATCH_BOOTUP = false,
            EJS_NETPLAY_ENABLED = false,
            EJS_NETPLAY_ICE_SERVERS = Array.Empty<object>(),
            EJS_SETTINGS = new Dictionary<string, object>(),
            EJS_CONTROLS = new Dictionary<string, object>(),
            SCAN_METADATA_PRIORITY = Array.Empty<string>(),
            SCAN_ARTWORK_PRIORITY = Array.Empty<string>(),
            SCAN_ARTWORK_PRIORITY_OVERRIDES = new Dictionary<string, string[]>(),
            SCAN_REGION_PRIORITY = Array.Empty<string>(),
            SCAN_LANGUAGE_PRIORITY = Array.Empty<string>(),
            SCAN_MEDIA = Array.Empty<string>(),
            GAMELIST_AUTO_EXPORT_ON_SCAN = false,
            GAMELIST_MEDIA_THUMBNAIL = "",
            GAMELIST_MEDIA_IMAGE = "",
            PEGASUS_AUTO_EXPORT_ON_SCAN = false,
        });
    }

    /// <summary>Anything under /api this surface does not serve. 501 rather than 404 so a client can tell
    /// "this server does not do that" from "you asked for a thing that does not exist".</summary>
    public static HttpResponse NotImplemented(RouteContext ctx)
    {
        var rest = ctx.GetRoute("rest") ?? "";
        // Logged, not silent: a client that fails on an endpoint we never wrote is the single most
        // likely way this surface breaks, and the log line is the whole diagnosis.
        LbLog.Info("romm", $"unimplemented: {ctx.Request?.Method} /api/{rest}");
        return Error(501, $"This RomM server does not implement /api/{rest}");
    }

    public static HttpResponse Landing(RouteContext ctx)
    {
        var html =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>LiteBox — RomM server</title></head>" +
            "<body style=\"font-family:system-ui,sans-serif;background:#0f0f12;color:#e6e6e6;" +
            "display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0\">" +
            "<main style=\"text-align:center;max-width:34rem;padding:1rem\">" +
            "<h1 style=\"font-weight:600\">LiteBox — RomM server</h1>" +
            "<p style=\"opacity:.75\">This port serves the RomM API, not a web page. Point a RomM client " +
            "(Argosy, Grout, the Playnite plugin) at this address and sign in with the account set in " +
            "Options &rarr; Modules &rarr; RomM server.</p>" +
            $"<p style=\"opacity:.5;font-size:.9rem\">Compatible with RomM {RommConfig.RommVersion}</p>" +
            "</main></body></html>";
        return HttpResponse.Html(html);
    }
}
