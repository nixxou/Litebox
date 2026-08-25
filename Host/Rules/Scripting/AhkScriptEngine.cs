// The AHK script engine — BigBoxProfile's ExecuteAHK, kept in its BETTER half only: BBP had two
// modes (in-process AutoHotkey.Interop, and an AutoHotkeyU32.exe + temp-file + result-file mode);
// ours is exclusively out-of-process, which buys everything the interop could not give — clean
// x64, crash isolation, and a watchdog that can actually KILL a runaway script.
//
// NO new payload and NO second interpreter (Mehdi): the ONE AutoHotkey that matters is the v1.1
// LaunchBox itself ships at ThirdParty\AutoHotkey\AutoHotkey.exe — the same exe our AhkScript
// (the emulator "Running AutoHotkey Script" parity) already runs. Everything here is that dialect.
//
// The contract mirrors the C# rule: a generated PRELUDE defines Exe, Args, OriginalExe,
// OriginalArgs, GameTitle/GamePlatform/GameId, EmulatorTitle, VersionName, Preview (0/1); the
// body runs; for TRANSFORM slots an epilogue writes Exe + Args (two lines, UTF-8) into a result
// file we read back — assigning those variables IS the transform. Side-effect slots skip the
// epilogue; a background Before script is left running and killed when the game exits (its whole
// point is living during the game — hotkeys, overlays).

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rules.Scripting;

/// <summary>What the prelude injects — the AHK twin of RuleScriptGlobals' context.</summary>
internal sealed record AhkScriptData(string Exe, string Args, string OriginalExe, string OriginalArgs,
    string GameTitle, string GamePlatform, string GameId,
    string EmulatorTitle, string VersionName, bool Preview, string ProfileName = "");

internal static class AhkScriptEngine
{
    private const string Tag = "ahk";
    private const int TimeoutMs = 10_000;

    /// <summary>LaunchBox's own interpreter, probed in order: the install's ThirdParty\AutoHotkey,
    /// then the dev-tree sibling LB install (lets the selftests run the real thing from bin).</summary>
    private static string? ExePath()
    {
        foreach (var dir in new[]
        {
            Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "ThirdParty", "AutoHotkey"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "LB", "ThirdParty", "AutoHotkey")),
        })
        {
            string p = Path.Combine(dir, "AutoHotkey.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool IsAvailable() => ExePath() != null;

    /// <summary>AHK v1 string-literal escape for a value spliced into the prelude. Backtick first
    /// (it is the escape char), then the doubled quote, and newlines become `n so a value never
    /// breaks the line structure.</summary>
    internal static string Esc(string v)
    {
        var sb = new StringBuilder(v.Length + 8);
        foreach (char c in v)
        {
            switch (c)
            {
                case '`': sb.Append("``"); break;
                case '"': sb.Append("\"\""); break;
                case '\r': break;
                case '\n': sb.Append("`n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The variables every slot sees.</summary>
    internal static string BuildPrelude(AhkScriptData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; ── LiteBox prelude (generated — the script body follows) ──");
        void V(string name, string value) => sb.AppendLine($"{name} := \"{Esc(value)}\"");
        V("Exe", d.Exe);
        V("Args", d.Args);
        V("OriginalExe", d.OriginalExe);
        V("OriginalArgs", d.OriginalArgs);
        V("GameTitle", d.GameTitle);
        V("GamePlatform", d.GamePlatform);
        V("GameId", d.GameId);
        V("EmulatorTitle", d.EmulatorTitle);
        V("VersionName", d.VersionName);
        V("ProfileName", d.ProfileName);
        sb.AppendLine($"Preview := {(d.Preview ? 1 : 0)}");
        sb.AppendLine("; ── end prelude ──");
        return sb.ToString();
    }

    /// <summary>Transform slot: prelude + body + result epilogue, run to completion (10 s cap,
    /// killed past it). Returns the script's Exe/Args on success.</summary>
    public static (bool Ok, string Exe, string Args, string Error) RunTransform(
        string body, AhkScriptData d)
    {
        string? exe = ExePath();
        if (exe == null) return (false, "", "", MissingMessage);

        string dir = Path.Combine(Path.GetTempPath(), "litebox-rules-ahk");
        Directory.CreateDirectory(dir);
        string stamp = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(dir, stamp + ".ahk");
        string outPath = Path.Combine(dir, stamp + ".out");

        var sb = new StringBuilder();
        sb.Append(BuildPrelude(d));
        sb.AppendLine($"__LB_OUT := \"{Esc(outPath)}\"");
        sb.AppendLine(body);
        sb.AppendLine("; ── LiteBox epilogue: the transform's result ──");
        sb.AppendLine("FileAppend, % Exe . \"`n\" . Args, %__LB_OUT%, UTF-8");
        sb.AppendLine("ExitApp");

        try
        {
            File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(true));
            var (exited, exitCode, stderr, _) = Exec(exe, scriptPath, wait: true);
            if (!exited) return (false, "", "", "timeout — script killed" + (stderr.Length > 0 ? " (" + stderr + ")" : ""));
            if (!File.Exists(outPath))
                return (false, "", "", $"no result (exit {exitCode})" + (stderr.Length > 0 ? ": " + stderr : " — script exited before the epilogue?"));
            var lines = File.ReadAllLines(outPath);
            string newExe = lines.Length > 0 ? lines[0] : d.Exe;
            string newArgs = lines.Length > 1 ? string.Join(" ", lines[1..]) : "";
            return (true, newExe, newArgs, "");
        }
        catch (Exception ex) { return (false, "", "", ex.Message); }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(outPath); } catch { }
        }
    }

    /// <summary>Side-effect slot. <paramref name="wait"/> false = the script is LEFT RUNNING (its
    /// point is living during the game) and the returned process handle lets the caller kill it at
    /// game exit. Waited runs are capped at 10 s.</summary>
    public static (bool Ok, string Error, Process? Resident) RunSideEffect(
        string body, AhkScriptData d, bool wait, int? timeoutMs = null)
    {
        string? exe = ExePath();
        if (exe == null) return (false, MissingMessage, null);

        string dir = Path.Combine(Path.GetTempPath(), "litebox-rules-ahk");
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".ahk");

        var sb = new StringBuilder();
        sb.Append(BuildPrelude(d));
        sb.AppendLine(body);
        if (wait) sb.AppendLine("ExitApp");   // a resident script (hotkeys) belongs in background mode

        try
        {
            File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(true));
            if (wait)
            {
                var (exited, exitCode, stderr, _) = Exec(exe, scriptPath, wait: true, timeoutMs);
                try { File.Delete(scriptPath); } catch { }
                if (!exited) return (false, "timeout — script killed", null);
                // A non-zero exit is a FAILED script (interpreter or runtime error) — reporting it
                // as success let a broken setup/cleanup pass silently (the review's finding 4).
                if (exitCode != 0)
                    return (false, $"exit {exitCode}" + (stderr.Length > 0 ? ": " + stderr : ""), null);
                return (true, "", null);
            }
            var p = Exec(exe, scriptPath, wait: false).Resident;
            // The temp .ahk stays while the script runs; swept on the next launch's writes.
            return (true, "", p);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    /// <summary>Syntax check without executing anything: the classic "/iLib nul" trick — the
    /// interpreter LOADS the script (full syntax pass, errors on stderr via /ErrorStdOut) and
    /// exits before the auto-execute section runs. Verified on the 1.1.24 exe LaunchBox ships:
    /// valid → exit 0 and nothing executed, broken → exit 2 with the line and message.</summary>
    public static (bool Ok, string Message) Check(string body) => Check(body, withPrelude: true);

    /// <summary>Same check, prelude optional: the RULE dialogs validate prelude + body (the script
    /// will really run that way); the EMULATOR script sections validate the BARE text — those are
    /// LaunchBox's own verbatim scripts, nothing is ever prepended, and error line numbers must
    /// match what the user sees.</summary>
    public static (bool Ok, string Message) Check(string body, bool withPrelude)
    {
        if (string.IsNullOrWhiteSpace(body)) return (true, "(empty)");
        string? exe = ExePath();
        if (exe == null) return (false, MissingMessage);

        string scriptPath = Path.Combine(Path.GetTempPath(), "litebox-rules-ahk", Guid.NewGuid().ToString("N") + ".ahk");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        try
        {
            var d = new AhkScriptData("emu.exe", "", "emu.exe", "", "", "", "", "", "", true);
            File.WriteAllText(scriptPath, withPrelude ? BuildPrelude(d) + body : body, new UTF8Encoding(true));
            var psi = new ProcessStartInfo(exe, $"/ErrorStdOut /iLib nul \"{scriptPath}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            // Async reads + timed wait — the same no-hang pattern as Exec.
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(EffectiveTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "syntax check timed out");
            }
            string err = errTask.GetAwaiter().GetResult() + outTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? (true, "OK") : (false, err.Trim().Length > 0 ? err.Trim() : "invalid script");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    /// <summary>Runs the interpreter. WAITED mode: stderr is read ASYNCHRONOUSLY while WaitForExit
    /// carries the timeout — a synchronous ReadToEnd would block until the child dies and the
    /// watchdog would never fire (the Codex review's finding: a MsgBox or a loop froze the launch
    /// forever). On timeout the whole tree is killed. The exit code rides back so callers can tell
    /// a script that DIED from one that ran (finding 4: silent side-effect failures).</summary>
    private static (bool Exited, int ExitCode, string Stderr, Process? Resident) Exec(string exe, string scriptPath, bool wait, int? timeoutMs = null)
    {
        var psi = new ProcessStartInfo(exe, $"/ErrorStdOut \"{scriptPath}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        var p = Process.Start(psi)!;
        if (!wait) return (true, 0, "", p);
        var stderrTask = p.StandardError.ReadToEndAsync();
        int cap = timeoutMs ?? EffectiveTimeoutMs;
        if (!p.WaitForExit(cap))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(2000); } catch { }   // let the streams close so the read completes
            LbLog.Warn(Tag, $"script still running after {cap / 1000}s — killed");
            string tail = stderrTask.Wait(1000) ? stderrTask.Result.Trim() : "";
            p.Dispose();
            return (false, -1, tail, null);
        }
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();
        int exitCode = p.ExitCode;
        p.Dispose();
        return (true, exitCode, stderr, null);
    }

    /// <summary>Selftests shrink the watchdog so the timeout path is testable in seconds.</summary>
    internal static int? TestTimeoutMs;
    private static int EffectiveTimeoutMs => TestTimeoutMs ?? TimeoutMs;

    private const string MissingMessage = "AutoHotkey.exe not found (LaunchBox ships it at ThirdParty\\AutoHotkey)";
}
