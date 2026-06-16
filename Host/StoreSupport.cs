// Store-game (GOG / Steam) support for LiteBox. LaunchBox/LiteBox tie a game to ONE
// store via <Source>; the store handles download/install while LiteBox just triggers
// it (a URI) and launches the installed game. NOTHING here reimplements a downloader.
//
//   GOG   — installed: ApplicationPath = "<install>\Launch <Product>.lnk" → ShellExecute.
//           not installed: Install → goggalaxy://openGameView/{GogAppId} (Galaxy's page).
//   Steam — installed/not: ApplicationPath = steam://rungameid/{appid}.
//           Play → that URI; Install → steam://install/{appid} (Steam's install dialog).
//   Epic  — installed/not: ApplicationPath = com.epicgames.launcher://apps/{appName}?action=launch&silent=true.
//           Play → that URI; Install → …?action=install (EGL launches the exe with EOS auth itself).
//
// Install-state itself is reconciled by StoreInstallStateSync (reads Galaxy's DB /
// Steam's appmanifest / Epic's EGL manifests). Launch must use ShellExecute=true (a .lnk or a
// steam:// / com.epicgames.launcher:// URI can't run with UseShellExecute=false, the emulator path).

#nullable enable

using System.Diagnostics;
using System.IO;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal enum StoreKind { None, Gog, Steam, Epic, Uplay, Ea }

internal static class StoreSupport
{
    public static StoreKind KindOf(string? source)
        => string.Equals(source, "GOG", StringComparison.OrdinalIgnoreCase) ? StoreKind.Gog
         : string.Equals(source, "Steam", StringComparison.OrdinalIgnoreCase) ? StoreKind.Steam
         : string.Equals(source, "Epic Games", StringComparison.OrdinalIgnoreCase) ? StoreKind.Epic
         : string.Equals(source, "Uplay", StringComparison.OrdinalIgnoreCase) ? StoreKind.Uplay
         : string.Equals(source, "EA", StringComparison.OrdinalIgnoreCase) ? StoreKind.Ea
         : StoreKind.None;

    public static StoreKind KindOf(IGame? game)
    { try { return game == null ? StoreKind.None : KindOf(game.Source); } catch { return StoreKind.None; } }

    /// <summary>Extracts the Steam appid from "steam://rungameid/{appid}".</summary>
    public static string? SteamAppId(string? applicationPath)
    {
        if (string.IsNullOrEmpty(applicationPath)) return null;
        const string marker = "rungameid/";
        int i = applicationPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = applicationPath.Substring(i + marker.Length).Trim();
        int end = 0; while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return end > 0 ? rest.Substring(0, end) : null;
    }

    /// <summary>Extracts the Epic appName from "com.epicgames.launcher://apps/{appName}?action=…".
    /// Handles the rare triple form ({namespace}%3A{catalogItemId}%3A{appName}) by taking the last
    /// segment, matching the AppName in EGL's manifests.</summary>
    public static string? EpicAppName(string? applicationPath)
    {
        if (string.IsNullOrEmpty(applicationPath)) return null;
        const string marker = "apps/";
        int i = applicationPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = applicationPath.Substring(i + marker.Length);
        int cut = rest.IndexOfAny(new[] { '?', '&', '/' });
        if (cut >= 0) rest = rest.Substring(0, cut);
        rest = Uri.UnescapeDataString(rest);          // %3A → :
        int colon = rest.LastIndexOf(':');
        if (colon >= 0) rest = rest.Substring(colon + 1);
        rest = rest.Trim();
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>Extracts the Ubisoft Connect game id from "uplay://launch/{id}" (same number as the
    /// registry Installs subkey name).</summary>
    public static string? UplayId(string? applicationPath)
    {
        if (string.IsNullOrEmpty(applicationPath)) return null;
        const string marker = "launch/";
        int i = applicationPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = applicationPath.Substring(i + marker.Length).Trim();
        int end = 0; while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return end > 0 ? rest.Substring(0, end) : null;
    }

    /// <summary>Extracts the EA offer/content id from "ea://{id}" (== &lt;contentID&gt; in installerdata.xml).</summary>
    public static string? EaId(string? applicationPath)
    {
        if (string.IsNullOrEmpty(applicationPath)) return null;
        const string marker = "ea://";
        int i = applicationPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = applicationPath.Substring(i + marker.Length).Trim();
        int cut = rest.IndexOfAny(new[] { '/', '?', '&' });
        if (cut >= 0) rest = rest.Substring(0, cut);
        rest = rest.Trim();
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>The game's on-disk install folder, used to watch its process for exit (the running
    /// screen). GOG: the folder of the "Launch *.lnk" ApplicationPath. Steam: resolved from the
    /// appmanifest. Null if it can't be determined.</summary>
    public static string? ResolveInstallDir(StoreKind kind, IGame? game)
    {
        try
        {
            if (kind == StoreKind.Gog)
            {
                var app = game?.ApplicationPath;
                if (!string.IsNullOrEmpty(app))
                {
                    var dir = Path.GetDirectoryName(app);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
                return null;
            }
            if (kind == StoreKind.Steam)
            {
                var appid = SteamAppId(game?.ApplicationPath);
                return appid == null ? null : StoreInstallStateSync.SteamInstallDir(appid);
            }
            if (kind == StoreKind.Epic)
            {
                var appName = EpicAppName(game?.ApplicationPath);
                return appName == null ? null : StoreInstallStateSync.EpicInstallDir(appName);
            }
            if (kind == StoreKind.Uplay)
            {
                var id = UplayId(game?.ApplicationPath);
                return id == null ? null : StoreInstallStateSync.UplayInstallDir(id);
            }
            if (kind == StoreKind.Ea)
            {
                var id = EaId(game?.ApplicationPath);
                return id == null ? null : StoreInstallStateSync.EaInstallDir(id);
            }
        }
        catch { }
        return null;
    }

    /// <summary>The store URI that triggers an install for an uninstalled game (delegated to the client).</summary>
    public static string? InstallUri(StoreKind kind, string? gogAppId, string? steamAppId, string? epicAppName = null, string? uplayId = null, string? eaId = null) => kind switch
    {
        StoreKind.Gog when !string.IsNullOrWhiteSpace(gogAppId) => "goggalaxy://openGameView/" + gogAppId!.Trim(),
        StoreKind.Steam when !string.IsNullOrWhiteSpace(steamAppId) => "steam://install/" + steamAppId!.Trim(),
        StoreKind.Epic when !string.IsNullOrWhiteSpace(epicAppName) => "com.epicgames.launcher://apps/" + epicAppName!.Trim() + "?action=install",
        StoreKind.Uplay when !string.IsNullOrWhiteSpace(uplayId) => "uplay://install/" + uplayId!.Trim(),
        StoreKind.Ea when !string.IsNullOrWhiteSpace(eaId) => "ea://" + eaId!.Trim(),
        _ => null,
    };

    /// <summary>Open a target via the shell (handles .lnk shortcuts and steam:// / goggalaxy:// URIs,
    /// which a plain UseShellExecute=false Process.Start cannot run).</summary>
    public static bool ShellOpen(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Console.WriteLine("[store] ShellOpen: " + target);
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[store] ShellOpen failed (" + target + "): " + ex.Message); return false; }
    }
}
