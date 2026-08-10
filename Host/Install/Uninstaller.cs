// Self-uninstall for LiteBox. Because the running exe (Core\LiteBox.exe) is locked and can't delete
// itself, we WRITE a .bat into %TEMP%, launch it DETACHED (ShellExecute → not a child of LiteBox, so
// killing LiteBox doesn't kill it), then quit LiteBox. The bat taskkills LiteBox, waits until it's gone
// (locks released), deletes everything, and finally self-deletes.
//
// Always removed (LiteBox-exclusive): the app files in Core (LiteBox.exe + LiteBox.dll + json + the extra
// managed deps LaunchBox doesn't ship — LibVLCSharp / ZstdSharp / Magick.NET — derived from
// LightPayload.Files), Core\litebox\ (ALL our data — dbs, caches, config, logs, our own thirdparty\ +
// cache\thumbs + web-assets\ + the WebView2 profiles), the root re-launcher, and the LiteBox-ONLY
// ThirdParty natives (Steam / Pdfium / RomExtractor — derived from NativeInstaller.Payload).
//
// Opt-in (off by default): the ThirdParty tools SHARED with ExtendDB (Everything / ImageMagick native /
// RAHasher) — removed as files, then their dirs are rmdir'd empty-only so a real ExtendDB's content is never
// nuked. LiteBox no longer keeps anything under Plugins\ExtendDB\ (thumbs live in Core\litebox\cache\thumbs),
// so the plugin's folders are never touched.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Install;

internal static class Uninstaller
{
    /// <summary>Writes the uninstall .bat, launches it detached, and exits LiteBox. Does not return.</summary>
    public static void RunSelfUninstall(bool alsoSharedThirdParty)
    {
        string core = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string root = (MediaResolver.LbRoot ?? Path.GetDirectoryName(core) ?? core).TrimEnd('\\', '/');

        string bat = Path.Combine(Path.GetTempPath(), "litebox-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bat");
        File.WriteAllText(bat, BuildScript(core, root, alsoSharedThirdParty), new UTF8Encoding(false));

        // Detached: ShellExecute launches cmd for the .bat independently of LiteBox's process, so our own
        // taskkill (and exit) can't take it down.
        Process.Start(new ProcessStartInfo { FileName = bat, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });

        Environment.Exit(0);   // release our file locks so the bat can delete the exe
    }

    /// <summary>The uninstall .bat body. <paramref name="core"/> = LB\Core, <paramref name="root"/> = LB.
    /// Exposed for the --dump-uninstall-bat dev flag / testing.</summary>
    public static string BuildScript(string core, string root, bool alsoSharedThirdParty)
    {
        core = core.TrimEnd('\\', '/');
        root = root.TrimEnd('\\', '/');
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine("taskkill /IM LiteBox.exe /F >nul 2>&1");
        // Wait until LiteBox.exe is truly gone so its files unlock.
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq LiteBox.exe\" 2>nul | find /I \"LiteBox.exe\" >nul && ( ping -n 2 127.0.0.1 >nul & goto wait )");

        // Always: LiteBox-exclusive. The light build drops these app files next to LiteBox.exe in Core (the
        // standalone is a single file, so the extras are just absent then). Derived from LightPayload.Files so
        // the deploy + uninstall lists can't drift — the apphost, the managed host dll, its json, and the
        // extra managed deps LaunchBox's Core doesn't provide (LibVLCSharp / ZstdSharp / Magick.NET).
        foreach (var f in LightPayload.Files) sb.AppendLine($"del /q \"{core}\\{f}\" 2>nul");
        sb.AppendLine($"rmdir /s /q \"{core}\\litebox\" 2>nul");                  // ALL LiteBox data (dbs, caches, our thirdparty\, cache\thumbs, web-assets\, webview2-kiosk\)
        sb.AppendLine($"del /q \"{root}\\LiteBox.exe\" 2>nul");

        // Always: the LiteBox-ONLY ThirdParty natives (Steam / Pdfium / RomExtractor), derived from the deploy
        // payload so the two never drift. Files first, then the (LiteBox-owned) sub-dirs recursively.
        foreach (var rel in NativeInstaller.LiteBoxOnlyNativeFiles())
            sb.AppendLine($"del /q \"{root}\\{rel}\" 2>nul");
        foreach (var dir in NativeInstaller.LiteBoxOnlySubDirs())
            sb.AppendLine($"rmdir /s /q \"{root}\\{dir}\" 2>nul");

        // Always: the native parental payload the in-app button deploys ON DEMAND into Core + Plugins
        // (the ASI + winhttp loader in Core, the write-guard plugin folder). del/rmdir no-op when the
        // user never ran Install; the .api SOURCES under Core\litebox are already gone with the rmdir above.
        foreach (var rel in Parental.ParentalNativeInstall.DeployedRelPaths())
            sb.AppendLine($"del /q \"{root}\\{rel}\" 2>nul");
        sb.AppendLine($"rmdir /s /q \"{root}\\{Parental.ParentalNativeInstall.DeployedPluginDirRel}\" 2>nul");

        // Opt-in: the ThirdParty tools SHARED with ExtendDB. Delete the files LiteBox deploys, then rmdir the
        // dirs EMPTY-ONLY (no /s) so a real ExtendDB's own content in them is never nuked.
        if (alsoSharedThirdParty)
        {
            foreach (var rel in NativeInstaller.SharedNativeFiles())
                sb.AppendLine($"del /q \"{root}\\{rel}\" 2>nul");
            foreach (var dir in NativeInstaller.SharedSubDirs())
                sb.AppendLine($"rmdir \"{root}\\{dir}\" 2>nul");
        }

        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }
}
