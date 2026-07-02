// Removes files/directories left by OLDER LiteBox versions at locations current versions no longer use —
// chiefly everything that lived directly in <LB>\Core before the Core\litebox\ reorg (config, write-back
// journal, RA/store caches + badges, rom-selection.json, ra-platform-overrides.json, logs), plus the old
// launcher's markers (_litebox_files.txt, _litebox_backup\, "LiteBox uninstall.bat", *.bak), the Magick.NET
// managed DLLs an old build copied into Core, and the loose native .api payload old flat/zip installs
// dropped in Core root.
//
// This is the SINGLE source of truth for "obsolete LiteBox leftovers":
//   • boot (upgrade / extract-over-old): SweepObsolete() deletes them — idempotent, a no-op on a clean
//     current install, and it NEVER touches current data (Core\litebox\, the current LiteBox.exe/.dll/.json,
//     the root re-launcher, ThirdParty\Steam) — that stays the uninstaller's job.
//   • uninstall: Uninstaller.BuildScript emits del/rmdir for the same lists (on top of the current files).
//
// Verified against the real installs: the Magick.NET DLLs are a LiteBox-era copy (a clean .NET 10 LaunchBox
// Core has none); the moved Core-root files are the reorg commit's orphans; the launcher markers are the
// pre-merge launcher's. None belongs to LaunchBox or ExtendDB.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host.Install;

internal static class LegacyCleanup
{
    // Obsolete files, relative to <LB>\Core.
    public static readonly string[] CoreFiles =
    {
        // pre-litebox\ config / write-back journal / state
        "LiteBox.ini", "LiteBox.pending", "LiteBox.pending.db", "LiteBox.pending.db-wal", "LiteBox.pending.db-shm",
        "rom-selection.json", "ra-platform-overrides.json", "whitelist.txt",
        // stray build / launcher artifacts (NOT the current LiteBox.exe/.dll/.deps.json/.runtimeconfig.json)
        "LiteBox.exe.bak", "LiteBox.pdb", "_litebox_files.txt",
        // managed DLLs an old build copied into Core (current resolves Magick from ExtendDB, not Core)
        "Magick.NET-Q16-AnyCPU.dll", "Magick.NET.Core.dll",
        // loose native payload old flat/zip installs dropped in Core root (current ships it under litebox\thirdparty\)
        "Everything64.dll.api", "Magick.Native-Q16-x64.dll.api", "RahasherExtendDB.exe", "7z.dll.api",
        "MSVCP140.dll.api", "VCRUNTIME140.dll.api", "VCRUNTIME140_1.dll.api", "steam_api64.dll.api",
        "RAHasher.COPYING.txt", "RAHasher.7z-LICENSE.txt", "RAHasher.RVZ-SUPPORT.txt",
    };

    // Obsolete directories, relative to <LB>\Core (recursive delete). NB: NOT "litebox" (that's current data).
    public static readonly string[] CoreDirs =
    {
        "ra-cache", "ra-badges", "store-ach-cache", "store-ach-badges", "_litebox_backup",
    };

    // Obsolete file globs, relative to <LB>\Core (top-level only — never recurses into Core\litebox\).
    public static readonly string[] CoreGlobs = { "litebox*.log" };

    // Obsolete files / globs, relative to the LaunchBox ROOT (NOT the current root re-launcher LiteBox.exe).
    public static readonly string[] RootFiles = { "LiteBox.exe.bak", "LiteBox uninstall.bat", "_litebox_files.txt" };
    public static readonly string[] RootGlobs = { "litebox*.log" };

    /// <summary>Delete the obsolete leftovers under <paramref name="lbRoot"/> (idempotent). Called at boot so
    /// an upgrade / zip-extract-over-an-old-install self-cleans. Preserves ALL current files.</summary>
    public static void SweepObsolete(string? lbRoot)
    {
        if (string.IsNullOrEmpty(lbRoot)) return;
        string core = Path.Combine(lbRoot!, "Core");
        int n = 0;
        foreach (var f in CoreFiles) n += DelFile(Path.Combine(core, f));
        foreach (var d in CoreDirs)  n += DelDir(Path.Combine(core, d));
        foreach (var g in CoreGlobs) n += DelGlob(core, g);
        foreach (var f in RootFiles) n += DelFile(Path.Combine(lbRoot!, f));
        foreach (var g in RootGlobs) n += DelGlob(lbRoot!, g);
        if (n > 0) Console.WriteLine($"[legacy] swept {n} obsolete item(s) left by a previous LiteBox version");
    }

    private static int DelFile(string p) { try { if (File.Exists(p)) { File.Delete(p); return 1; } } catch { } return 0; }
    private static int DelDir(string p)  { try { if (Directory.Exists(p)) { Directory.Delete(p, true); return 1; } } catch { } return 0; }
    private static int DelGlob(string dir, string glob)
    {
        int n = 0;
        try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, glob)) n += DelFile(f); } catch { }
        return n;
    }
}
