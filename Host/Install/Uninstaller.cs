// Self-uninstall for LiteBox. Because the running exe (Core\LiteBox.exe) is locked and can't delete
// itself, we WRITE a .bat into %TEMP%, launch it DETACHED (ShellExecute → not a child of LiteBox, so
// killing LiteBox doesn't kill it), then quit LiteBox. The bat taskkills LiteBox, waits until it's gone
// (locks released), deletes everything, and finally self-deletes.
//
// Always removed (LiteBox-exclusive): Core\LiteBox.exe, Core\litebox\ (all our data), the root re-launcher
// + uninstall.bat, ThirdParty\Steam, and any pre-reorg Core-root leftovers.
// Opt-in (shared with ExtendDB — off by default): the thumbnail cache (Plugins\ExtendDB\cache\thumbs) and
// the shared ThirdParty tools (Everything / ImageMagick native / RAHasher).

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Install;

internal static class Uninstaller
{
    // Shared-with-ExtendDB ThirdParty files (removed only when the user opts in).
    private static readonly string[] SharedThirdParty =
    {
        @"ThirdParty\Everything\Everything64.dll",
        @"ThirdParty\ExtendDB\Magick.Native-Q16-x64.dll",
        @"ThirdParty\RetroAchievements\RahasherExtendDB.exe",
        @"ThirdParty\RetroAchievements\7z.dll",
        @"ThirdParty\RetroAchievements\MSVCP140.dll",
        @"ThirdParty\RetroAchievements\VCRUNTIME140.dll",
        @"ThirdParty\RetroAchievements\VCRUNTIME140_1.dll",
    };

    /// <summary>Writes the uninstall .bat, launches it detached, and exits LiteBox. Does not return.</summary>
    public static void RunSelfUninstall(bool alsoThumbs, bool alsoSharedThirdParty)
    {
        string core = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string root = (MediaResolver.LbRoot ?? Path.GetDirectoryName(core) ?? core).TrimEnd('\\', '/');

        string bat = Path.Combine(Path.GetTempPath(), "litebox-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bat");
        File.WriteAllText(bat, BuildScript(core, root, alsoThumbs, alsoSharedThirdParty), new UTF8Encoding(false));

        // Detached: ShellExecute launches cmd for the .bat independently of LiteBox's process, so our own
        // taskkill (and exit) can't take it down.
        Process.Start(new ProcessStartInfo { FileName = bat, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });

        Environment.Exit(0);   // release our file locks so the bat can delete the exe
    }

    /// <summary>The uninstall .bat body. <paramref name="core"/> = LB\Core, <paramref name="root"/> = LB.
    /// Exposed for the --dump-uninstall-bat dev flag / testing.</summary>
    public static string BuildScript(string core, string root, bool alsoThumbs, bool alsoSharedThirdParty)
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

        // Always: LiteBox-exclusive.
        sb.AppendLine($"del /q \"{core}\\LiteBox.exe\" 2>nul");
        sb.AppendLine($"rmdir /s /q \"{core}\\litebox\" 2>nul");
        sb.AppendLine($"del /q \"{root}\\LiteBox.exe\" 2>nul");
        sb.AppendLine($"del /q \"{root}\\LiteBox uninstall.bat\" 2>nul");
        sb.AppendLine($"del /q \"{root}\\ThirdParty\\Steam\\steam_api64.dll\" 2>nul");
        sb.AppendLine($"rmdir \"{root}\\ThirdParty\\Steam\" 2>nul");
        // Pre-reorg Core-root leftovers (harmless if absent).
        sb.AppendLine($"del /q \"{core}\\LiteBox.ini\" \"{core}\\LiteBox.pending\" \"{core}\\LiteBox.pending.db*\" \"{core}\\rom-selection.json\" \"{core}\\ra-platform-overrides.json\" \"{core}\\litebox*.log\" 2>nul");
        sb.AppendLine($"for %%D in (ra-cache ra-badges store-ach-cache store-ach-badges) do rmdir /s /q \"{core}\\%%D\" 2>nul");

        // Opt-in: shared thumbnail cache.
        if (alsoThumbs)
            sb.AppendLine($"rmdir /s /q \"{root}\\Plugins\\ExtendDB\\cache\\thumbs\" 2>nul");

        // Opt-in: shared ThirdParty tools (files, then empty-only rmdir so ExtendDB content is never nuked).
        if (alsoSharedThirdParty)
        {
            foreach (var rel in SharedThirdParty)
                sb.AppendLine($"del /q \"{root}\\{rel}\" 2>nul");
            sb.AppendLine($"rmdir \"{root}\\ThirdParty\\Everything\" 2>nul");
            sb.AppendLine($"rmdir \"{root}\\ThirdParty\\ExtendDB\" 2>nul");
            sb.AppendLine($"rmdir \"{root}\\ThirdParty\\RetroAchievements\" 2>nul");
        }

        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }
}
