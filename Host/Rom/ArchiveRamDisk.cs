// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — optional ImDisk RAM-disk backing. Slice R4.
// ─────────────────────────────────────────────────────────────────────────────
//
// Native LiteBox clean-room port of the ExtendDB plugin's ArchiveRamDisk. When a
// profile enables it and the game fits under both the profile's MB cap and free RAM,
// a per-game ImDisk RAM drive is mounted, used as the cache root for THAT extraction
// only, and unmounted when the game exits. RAM extractions are ephemeral → never
// LRU-counted.
//
// EVERYTHING degrades gracefully — a mount that cannot happen (no driver, no elevated
// helper, not enough RAM, no free drive letter, heap-starved session) is logged and
// the caller falls back to the normal disk cache. Nothing here ever throws to the
// launch thread.
//
// Elevation
//   ImDisk mounting needs admin. Two ways in:
//     • LiteBox already runs elevated → a direct imdisk.exe call works.
//     • Otherwise a ONE-TIME elevated scheduled task (registered from the config
//       window, one UAC prompt) runs the bundled RamDiskHelper.exe, which reads its
//       action from ramdisk.cfg — so `schtasks /run` triggers it WITHOUT further UAC.
//   RamDiskHelper.exe is NOT bundled by LiteBox today; when it (and/or the ImDisk
//   driver) is absent, IsReady() is false and the feature is skipped.
//
// Portability note: under LiteBox's desktop-heap-limited launch context (memory:
// litebox-desktop-heap-launch) a mount from a heap-starved session may fail
// differently than under LaunchBox — hence every path returns null on failure and the
// disk cache takes over. Validate on real hardware.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rom;

internal static class ArchiveRamDisk
{
    // gameId → mounted drive root ("R:\"), so the exit cleanup can unmount.
    private static readonly ConcurrentDictionary<string, string> _active = new();

    private static string System32 => Environment.SystemDirectory ?? @"C:\Windows\System32";
    public static string ImDiskExe => Path.Combine(System32, "imdisk.exe");
    private static string SchTasks => Path.Combine(System32, "schtasks.exe");

    public const string TaskName = "LiteBox_RomExtractor_RamDisk";
    public static string HelperDir => Path.Combine(RomPaths.LbRoot, "ThirdParty", "RomExtractor", "ramdisk");
    public static string HelperExe => Path.Combine(HelperDir, "RamDiskHelper.exe");
    private static string CfgPath => Path.Combine(HelperDir, "ramdisk.cfg");
    private static string ResultPath => Path.Combine(HelperDir, "ramdisk.result");

    // ── Capability probes ──────────────────────────────────────────────

    /// <summary>True when the ImDisk CLI is present (driver installed).</summary>
    public static bool IsDriverInstalled()
    {
        try { return File.Exists(ImDiskExe); } catch { return false; }
    }

    /// <summary>Free physical RAM in MB (0 on failure).</summary>
    public static int GetFreeRamMb()
    {
        try
        {
            var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref s)) return (int)(s.ullAvailPhys / (1024UL * 1024UL));
        }
        catch { }
        return 0;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>True when the elevated mount task is registered.</summary>
    public static bool IsTaskInstalled()
    {
        try { return RomToolRunner.Run(SchTasks, new[] { "/query", "/tn", TaskName }, default, "ramdisk-task") == 0; }
        catch { return false; }
    }

    /// <summary>True when a RAM disk can actually be mounted: driver present AND
    /// (an elevated task is installed OR LiteBox already runs elevated).</summary>
    public static bool IsReady() => IsDriverInstalled() && (IsTaskInstalled() || IsElevated());

    // ── Elevated task management (config-window actions) ────────────────

    /// <summary>Registers the elevated mount task (one UAC prompt via runas). Returns
    /// true on success. Called from the config window's "Install" action.</summary>
    public static bool InstallTask()
    {
        try
        {
            if (!File.Exists(HelperExe)) { Log("InstallTask: helper missing at " + HelperExe); return false; }
            var psi = new ProcessStartInfo(SchTasks)
            {
                UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{HelperExe}\\\"\" /sc ONCE /st 00:00 /rl HIGHEST /f",
            };
            using var p = Process.Start(psi); if (p == null) return false; p.WaitForExit();
            Log("InstallTask exit=" + p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log("InstallTask failed: " + ex.Message); return false; }
    }

    public static bool UninstallTask()
    {
        try
        {
            var psi = new ProcessStartInfo(SchTasks)
            {
                UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/delete /tn \"{TaskName}\" /f",
            };
            using var p = Process.Start(psi); if (p == null) return false; p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log("UninstallTask failed: " + ex.Message); return false; }
    }

    // ── Mount / unmount ────────────────────────────────────────────────

    /// <summary>Mounts an NTFS RAM drive of <paramref name="sizeMb"/> MB, registers it
    /// for <paramref name="gameId"/>, and returns the drive root ("R:\") — or null on
    /// any failure (caller then uses the disk cache).</summary>
    public static string? MountForGame(string gameId, int sizeMb, CancellationToken ct = default)
    {
        var root = Mount(sizeMb, ct);
        if (!string.IsNullOrEmpty(root)) Register(gameId, root!);
        return root;
    }

    /// <summary>Mounts an NTFS RAM drive of <paramref name="sizeMb"/> MB. Uses the
    /// elevated task when installed, else a direct imdisk call (only works if LiteBox is
    /// already elevated). Returns the drive root ("R:\") or null on failure.</summary>
    public static string? Mount(int sizeMb, CancellationToken ct = default)
    {
        try
        {
            if (sizeMb <= 0 || !IsDriverInstalled()) return null;
            char letter = FreeDriveLetter();
            if (letter == '\0') { Log("no free drive letter"); return null; }
            string root = letter + ":\\";

            if (IsTaskInstalled())
            {
                WriteCfg("mount", letter, sizeMb);
                try { if (File.Exists(ResultPath)) File.Delete(ResultPath); } catch { }
                RunTask(ct);
                for (int i = 0; i < 50 && !Directory.Exists(root); i++) { if (ct.IsCancellationRequested) break; Thread.Sleep(100); }
                if (Directory.Exists(root)) { Log("mounted " + root + " (" + sizeMb + "MB) via task"); return root; }
                Log("task mount produced no drive (result=" + ReadResult() + ")");
                return null;
            }

            // No task → direct (needs admin).
            int exit = RomToolRunner.Run(ImDiskExe, new[] { "-a", "-s", sizeMb + "M", "-m", letter + ":", "-p", "/fs:ntfs /q /y" }, ct, "ramdisk");
            if (exit == 0 && Directory.Exists(root)) { Log("mounted " + root + " (" + sizeMb + "MB) directly"); return root; }
            Log("mount failed — no elevated task installed and a direct mount needs admin. Install it from the ROM-extractor config.");
            return null;
        }
        catch (Exception ex) { Log("Mount threw: " + ex.Message); return null; }
    }

    public static bool Unmount(string driveRoot, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(driveRoot)) return false;
            char letter = driveRoot[0];
            if (IsTaskInstalled())
            {
                WriteCfg("umount", letter, 0);
                RunTask(ct);
                for (int i = 0; i < 30 && Directory.Exists(driveRoot); i++) Thread.Sleep(100);
                Log("unmounted " + driveRoot + " via task (gone=" + !Directory.Exists(driveRoot) + ")");
                return !Directory.Exists(driveRoot);
            }
            int exit = RomToolRunner.Run(ImDiskExe, new[] { "-D", "-m", letter + ":" }, ct, "ramdisk");
            return exit == 0;
        }
        catch (Exception ex) { Log("Unmount threw: " + ex.Message); return false; }
    }

    public static void Register(string gameId, string driveRoot)
    {
        if (!string.IsNullOrEmpty(gameId) && !string.IsNullOrEmpty(driveRoot)) _active[gameId] = driveRoot;
    }

    /// <summary>Unmounts the RAM drive (if any) mounted for this game.</summary>
    public static void UnmountForGame(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        if (_active.TryRemove(gameId, out var root)) Unmount(root);
    }

    /// <summary>Unmounts every active RAM drive. The exit cleanup carries no game id and
    /// only one game runs at a time, so this is what the host calls on exit.</summary>
    public static void UnmountAll()
    {
        foreach (var kv in _active.ToArray())
            if (_active.TryRemove(kv.Key, out var root)) Unmount(root);
    }

    public static bool HasActiveMounts => !_active.IsEmpty;

    // ── Helpers ────────────────────────────────────────────────────────

    private static void WriteCfg(string action, char drive, int sizeMb)
    {
        try
        {
            Directory.CreateDirectory(HelperDir);
            File.WriteAllText(CfgPath, "action=" + action + "\r\ndrive=" + drive + "\r\nsize=" + sizeMb + "\r\nlabel=RomExtractorRAM\r\n");
        }
        catch (Exception ex) { Log("WriteCfg failed: " + ex.Message); }
    }

    private static void RunTask(CancellationToken ct) => RomToolRunner.Run(SchTasks, new[] { "/run", "/tn", TaskName }, ct, "ramdisk-task");

    private static string ReadResult()
    {
        try { return File.Exists(ResultPath) ? File.ReadAllText(ResultPath).Trim() : "<none>"; } catch { return "<err>"; }
    }

    private static char FreeDriveLetter()
    {
        try
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            // Prefer the high letters (Z..D) the way the plugin's RomExtractor did.
            for (char c = 'Z'; c >= 'D'; c--) if (!used.Contains(c)) return c;
        }
        catch { }
        return '\0';
    }

    private static void Log(string msg) => LbLog.Info("rom", "ramdisk: " + msg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
