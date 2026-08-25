// Elevated launches WITHOUT a per-launch UAC prompt — the RamDiskHelper pattern, whole: a ONE-TIME
// elevated scheduled task (single UAC at install, /rl HIGHEST) whose action is LiteBox.exe itself
// with --admin-spawn; each admin launch then writes a cfg (exe, args, workdir, hideConsole),
// triggers `schtasks /run` (no prompt), and the elevated helper instance spawns the emulator with a
// REAL CreateProcess — so HideConsole works again, unlike the ShellExecute-runas fallback — writes
// the PID back, and exits. LiteBox waits on that PID from medium IL (SYNCHRONIZE is grantable on an
// elevated process; when even that is refused we poll for death).
//
// When the task is not installed, Spawn falls back to the direct UAC runas — the feature always
// works, the task only removes the prompt. Install/uninstall are user gestures from the two UIs
// that surface run-as-admin (the game checkbox page, the RunAsAdmin rule dialog).
//
// Threat-model note, stated on purpose: the cfg is user-writable and the task runs it elevated —
// that is INHERENT to every no-UAC elevation bridge (RamDiskHelper included) and fine for a
// launcher owned by the machine's own administrator; this is not a privilege boundary.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LbApiHost.Host;

internal static class AdminLaunch
{
    /// <summary>The task name is PER INSTALL: several LiteBox installs coexist on one machine
    /// (G:\LB, G:\LB1326, a dev tree…) and a single fixed name would let install A's task answer
    /// install B's IsTaskInstalled() while running A's helper — which then reads A's cfg folder
    /// and never sees B's request (10 s of nothing, then the UAC fallback). The suffix is a stable
    /// hash of THIS install's Core path, so each install owns its own task and its own exchange
    /// folder, and uninstalling one never disarms the others.</summary>
    public static string TaskName => "LiteBox_AdminLaunch_" + InstallTag;

    private static string InstallTag
    {
        get
        {
            string core = AppContext.BaseDirectory.TrimEnd('\\', '/').ToLowerInvariant();
            uint h = 2166136261;                       // FNV-1a, stable across runs and machines
            foreach (char c in core) { h = (h ^ c) * 16777619; }
            return h.ToString("x8");
        }
    }

    private static string System32 => Environment.SystemDirectory ?? @"C:\Windows\System32";
    private static string SchTasks => Path.Combine(System32, "schtasks.exe");

    private static string ExchangeDir => LiteBoxPaths.Dir("admin-launch");
    private static string CfgPath => Path.Combine(ExchangeDir, "launch.cfg");
    private static string PidPath => Path.Combine(ExchangeDir, "launch.pid");
    private static string ErrPath => Path.Combine(ExchangeDir, "launch.err");

    // ── task management (one UAC at install, none after) ─────────────────────

    public static bool IsTaskInstalled()
    {
        try
        {
            // /v /fo LIST prints the action too: we require the task to name THIS install's exe.
            // A task left by a LiteBox that has moved (or a same-hash collision) is treated as
            // absent, so InstallTask re-registers it on the right path instead of silently
            // launching someone else's helper.
            var psi = new ProcessStartInfo(SchTasks, $"/query /tn \"{TaskName}\" /v /fo LIST")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0) return false;
            string exe = Path.Combine(AppContext.BaseDirectory, "LiteBox.exe");
            return outp.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>Registers the elevated task (ONE UAC prompt, via runas). The action is this very
    /// LiteBox.exe with --admin-spawn.</summary>
    public static bool InstallTask()
    {
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "LiteBox.exe");
            var psi = new ProcessStartInfo(SchTasks)
            {
                UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --admin-spawn\" /sc ONCE /st 00:00 /rl HIGHEST /f",
            };
            using var p = Process.Start(psi); if (p == null) return false; p.WaitForExit();
            return p.ExitCode == 0 && IsTaskInstalled();
        }
        catch { return false; }   // UAC refused, or schtasks failed
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
        catch { return false; }
    }

    // ── the launcher side (medium IL) ────────────────────────────────────────

    /// <summary>Spawns <paramref name="fileName"/> elevated through the task. Returns the child PID,
    /// or 0 when the bridge is unavailable/failed (caller falls back to UAC runas).</summary>
    public static int SpawnViaTask(string fileName, string args, string? workDir, bool hideConsole)
    {
        try
        {
            if (!IsTaskInstalled()) return 0;
            Directory.CreateDirectory(ExchangeDir);
            try { File.Delete(PidPath); } catch { }
            try { File.Delete(ErrPath); } catch { }
            File.WriteAllLines(CfgPath, new[]
            {
                fileName,
                args ?? "",
                workDir ?? "",
                hideConsole ? "hide" : "show",
            });

            var psi = new ProcessStartInfo(SchTasks, $"/run /tn \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)!)
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10000) || p.ExitCode != 0) { Console.WriteLine("[launch] admin task /run failed"); return 0; }
            }

            // The helper writes the PID (or an error) within moments of starting. Back-to-back
            // bridge calls (an admin pre-command then an admin emulator) can hit the scheduler's
            // don't-start-while-running policy while the previous helper instance is still shutting
            // down — one silent /run retry at the 3 s mark covers that window.
            for (int i = 0; i < 100; i++)   // 10 s
            {
                if (File.Exists(ErrPath))
                {
                    Console.WriteLine("[launch] admin helper: " + SafeRead(ErrPath));
                    return 0;
                }
                if (File.Exists(PidPath) && int.TryParse(SafeRead(PidPath).Trim(), out int pid) && pid > 0)
                    return pid;
                if (i == 30)
                {
                    var retry = new ProcessStartInfo(SchTasks, $"/run /tn \"{TaskName}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using var r = Process.Start(retry)!;
                    r.StandardOutput.ReadToEnd(); r.StandardError.ReadToEnd();
                    r.WaitForExit(5000);
                }
                Thread.Sleep(100);
            }
            Console.WriteLine("[launch] admin helper produced no PID within 10s — falling back to UAC");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[launch] admin bridge error: " + ex.Message); return 0; }
    }

    /// <summary>Waits for the elevated child to exit. SYNCHRONIZE is normally grantable from medium
    /// IL; when the wait itself is refused, degrade to polling for the process's death.</summary>
    public static void WaitForPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit();
            return;
        }
        catch (ArgumentException) { return; }   // already gone
        catch { /* wait refused → poll */ }
        while (true)
        {
            try { using var p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; }
            Thread.Sleep(500);
        }
    }

    // ── the helper side (elevated LiteBox.exe --admin-spawn) ─────────────────

    /// <summary>The --admin-spawn entry: read the cfg, spawn the emulator (we ARE elevated — the
    /// child inherits), write the PID, exit. Never throws out; errors land in launch.err.</summary>
    public static int RunHelper()
    {
        try
        {
            var lines = File.ReadAllLines(CfgPath);
            if (lines.Length < 4) { File.WriteAllText(ErrPath, "bad cfg"); return 1; }
            string fileName = lines[0], args = lines[1], workDir = lines[2];
            bool hide = lines[3] == "hide";
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = hide,
                WorkingDirectory = workDir.Length > 0 && Directory.Exists(workDir)
                    ? workDir : (Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory),
            };
            var p = Process.Start(psi);
            if (p == null) { File.WriteAllText(ErrPath, "spawn returned null"); return 1; }
            File.WriteAllText(PidPath, p.Id.ToString());
            try { File.Delete(CfgPath); } catch { }
            return 0;   // the helper leaves; the child runs on, LiteBox waits on the PID
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(ErrPath, ex.Message); } catch { }
            return 1;
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }
}
