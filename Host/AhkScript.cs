// Replicates LaunchBox's per-emulator "Running AutoHotkey Script" for the host's launch
// lifecycle. Reverse-engineered from a live LB 13.x trace (ExtendDB's Process.Start patch,
// 2026-06-12) — the observed mechanics, mirrored exactly:
//
//   • LB ships AutoHotkey v1.1 as a STANDALONE exe (ThirdParty\AutoHotkey\AutoHotkey.exe);
//     it is GPL v2, hence the shell-out instead of linking.
//   • Start (AutoHotkey.RunGameScript, ~90ms BEFORE the emulator spawn):
//       - write "#NoTrayIcon\n" + the emulator's AutoHotkeyScript verbatim (no token
//         substitution) to <LB>\Metadata\Temp\<GUID> (no extension),
//       - Process.Start(AutoHotkey.exe, "\"<tempfile>\"") — UseShellExecute=false, no
//         redirects, normal window; keep the Process.
//   • Exit (KillGameScript, when the emulator process dies): kill the AHK process and
//     delete the temp file. The ExitAutoHotkeyScript is NOT run at game exit (it belongs
//     to the pause screen, which LiteBox doesn't implement) — only the running script
//     matters here.
//   • Empty detection: placeholder scripts like ";" must not spawn AHK — a script whose
//     lines are all blank or comment-only counts as empty (mirrors AutoHotkey.GetIsScriptEmpty).

#nullable enable

using System.Diagnostics;
using System.IO;

namespace LbApiHost.Host;

internal static class AhkScript
{
    private static Process? _proc;
    private static string? _tempFile;
    private static readonly object _lock = new();

    /// <summary>True when the script has no effective content: null/whitespace, or
    /// every line blank / comment-only (";" placeholders ship in many configs).</summary>
    public static bool IsScriptEmpty(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return true;
        foreach (var raw in script!.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            return false;
        }
        return true;
    }

    /// <summary>Starts the emulator's running script (no-op when empty or when the
    /// bundled AutoHotkey.exe is missing). Call right BEFORE spawning the emulator;
    /// any previous script is killed first (single game at a time).</summary>
    public static void StartGameScript(string? script, string lbRoot)
    {
        lock (_lock)
        {
            KillLocked();
            if (IsScriptEmpty(script)) return;
            try
            {
                var exe = Path.Combine(lbRoot, "ThirdParty", "AutoHotkey", "AutoHotkey.exe");
                if (!File.Exists(exe)) { Console.WriteLine("[ahk] AutoHotkey.exe not found at " + exe + " — running script skipped"); return; }
                var dir = Path.Combine(lbRoot, "Metadata", "Temp");
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, Guid.NewGuid().ToString());
                File.WriteAllText(tmp, "#NoTrayIcon\n" + script);
                _proc = Process.Start(new ProcessStartInfo(exe, "\"" + tmp + "\"") { UseShellExecute = false });
                _tempFile = tmp;
                Console.WriteLine("[ahk] running script started (pid " + (_proc?.Id.ToString() ?? "?") + ")");
            }
            catch (Exception ex) { Console.WriteLine("[ahk] start failed: " + ex.Message); }
        }
    }

    /// <summary>Kills the running script (if still alive) and deletes its temp file.
    /// Idempotent — call on game exit.</summary>
    public static void KillGameScript()
    {
        lock (_lock) KillLocked();
    }

    private static void KillLocked()
    {
        try { if (_proc != null && !_proc.HasExited) { _proc.Kill(); Console.WriteLine("[ahk] running script killed"); } }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        try { if (_tempFile != null && File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        _tempFile = null;
    }
}
