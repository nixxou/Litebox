// The AHK script engine — BigBoxProfile's ExecuteAHK, kept in its BETTER half only: BBP had two
// modes (in-process AutoHotkey.Interop, and an AutoHotkeyU32.exe + temp-file + result-file mode);
// ours is exclusively out-of-process, which buys everything the interop could not give — clean
// x64, crash isolation, and a watchdog that can actually KILL a runaway script.
//
// NO new payload (Mehdi): LaunchBox itself ships AutoHotkey v1.1 at ThirdParty\AutoHotkey\
// AutoHotkey.exe — the same exe our AhkScript (the emulator "Running AutoHotkey Script" parity)
// already runs — so v1 is always available. v2 is optional: drop an AutoHotkey64.exe (v2) into
// that same folder and the per-rule version switch lights up.
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
    string EmulatorTitle, string VersionName, bool Preview);

internal static class AhkScriptEngine
{
    private const string Tag = "ahk";
    private const int TimeoutMs = 10_000;

    /// <summary>The interpreter homes, probed in order: the install's own ThirdParty\AutoHotkey
    /// (LaunchBox ships v1.1 there), then the dev-tree sibling LB install (lets the selftests run
    /// the real interpreter from bin). v2 is user-provided in the same folder.</summary>
    private static string? ExePath(bool v1)
    {
        string[] names = v1
            ? new[] { "AutoHotkey.exe" }
            : new[] { "AutoHotkey64.exe", "AutoHotkeyV2.exe", Path.Combine("v2", "AutoHotkey64.exe") };
        foreach (var dir in new[]
        {
            Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "ThirdParty", "AutoHotkey"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "LB", "ThirdParty", "AutoHotkey")),
        })
        {
            foreach (var name in names)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    public static bool IsAvailable(bool v1) => ExePath(v1) != null;

    /// <summary>AHK string-literal escape for a value spliced into the prelude. Backtick first
    /// (it is the escape char), then the quote — doubled in v1, backtick-escaped in v2 — and
    /// newlines become `n so a value never breaks the line structure.</summary>
    internal static string Esc(string v, bool v1)
    {
        var sb = new StringBuilder(v.Length + 8);
        foreach (char c in v)
        {
            switch (c)
            {
                case '`': sb.Append("``"); break;
                case '"': sb.Append(v1 ? "\"\"" : "`\""); break;
                case '\r': break;
                case '\n': sb.Append("`n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The variables every slot sees, both dialects (v1 accepts := expressions too).</summary>
    internal static string BuildPrelude(AhkScriptData d, bool v1)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; ── LiteBox prelude (generated — the script body follows) ──");
        if (!v1) sb.AppendLine("#Requires AutoHotkey v2.0");
        void V(string name, string value) => sb.AppendLine($"{name} := \"{Esc(value, v1)}\"");
        V("Exe", d.Exe);
        V("Args", d.Args);
        V("OriginalExe", d.OriginalExe);
        V("OriginalArgs", d.OriginalArgs);
        V("GameTitle", d.GameTitle);
        V("GamePlatform", d.GamePlatform);
        V("GameId", d.GameId);
        V("EmulatorTitle", d.EmulatorTitle);
        V("VersionName", d.VersionName);
        sb.AppendLine($"Preview := {(d.Preview ? 1 : 0)}");
        sb.AppendLine("; ── end prelude ──");
        return sb.ToString();
    }

    /// <summary>Transform slot: prelude + body + result epilogue, run to completion (10 s cap,
    /// killed past it). Returns the script's Exe/Args on success.</summary>
    public static (bool Ok, string Exe, string Args, string Error) RunTransform(
        bool v1, string body, AhkScriptData d)
    {
        string? exe = ExePath(v1);
        if (exe == null) return (false, "", "", MissingMessage(v1));

        string dir = Path.Combine(Path.GetTempPath(), "litebox-rules-ahk");
        Directory.CreateDirectory(dir);
        string stamp = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(dir, stamp + ".ahk");
        string outPath = Path.Combine(dir, stamp + ".out");

        var sb = new StringBuilder();
        sb.Append(BuildPrelude(d, v1));
        sb.AppendLine($"__LB_OUT := \"{Esc(outPath, v1)}\"");
        sb.AppendLine(body);
        sb.AppendLine("; ── LiteBox epilogue: the transform's result ──");
        sb.AppendLine(v1
            ? "FileAppend, % Exe . \"`n\" . Args, %__LB_OUT%, UTF-8"
            : "FileAppend(Exe . \"`n\" . Args, __LB_OUT, \"UTF-8\")");
        sb.AppendLine("ExitApp");

        try
        {
            File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(true));
            var (exited, stderr, _) = Exec(exe, scriptPath, wait: true);
            if (!exited) return (false, "", "", "timeout — script killed");
            if (!File.Exists(outPath))
                return (false, "", "", "no result" + (stderr.Length > 0 ? ": " + stderr : " (script exited before the epilogue?)"));
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
        bool v1, string body, AhkScriptData d, bool wait)
    {
        string? exe = ExePath(v1);
        if (exe == null) return (false, MissingMessage(v1), null);

        string dir = Path.Combine(Path.GetTempPath(), "litebox-rules-ahk");
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".ahk");

        var sb = new StringBuilder();
        sb.Append(BuildPrelude(d, v1));
        sb.AppendLine(body);
        if (wait) sb.AppendLine("ExitApp");   // a resident script (hotkeys) belongs in background mode

        try
        {
            File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(true));
            if (wait)
            {
                var (exited, stderr, _) = Exec(exe, scriptPath, wait: true);
                try { File.Delete(scriptPath); } catch { }
                return exited ? (true, stderr, null) : (false, "timeout — script killed", null);
            }
            var p = Exec(exe, scriptPath, wait: false).Resident;
            // The temp .ahk stays while the script runs; swept on the next launch's writes.
            return (true, "", p);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    /// <summary>Syntax check. v2 has a real /validate switch; v1 has none — running the script to
    /// find out is not a "check", so v1 answers honestly.</summary>
    public static (bool Ok, string Message) Check(bool v1, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (true, "(empty)");
        string? exe = ExePath(v1);
        if (exe == null) return (false, MissingMessage(v1));
        if (v1) return (true, "v1 has no syntax-check mode — test through the exported .ahk");

        string scriptPath = Path.Combine(Path.GetTempPath(), "litebox-rules-ahk", Guid.NewGuid().ToString("N") + ".ahk");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        try
        {
            var d = new AhkScriptData("emu.exe", "", "emu.exe", "", "", "", "", "", "", true);
            File.WriteAllText(scriptPath, BuildPrelude(d, false) + body, new UTF8Encoding(true));
            var psi = new ProcessStartInfo(exe, $"/ErrorStdOut /validate \"{scriptPath}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(TimeoutMs);
            return p.ExitCode == 0 ? (true, "OK") : (false, err.Trim().Length > 0 ? err.Trim() : "invalid script");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    private static (bool Exited, string Stderr, Process? Resident) Exec(string exe, string scriptPath, bool wait)
    {
        var psi = new ProcessStartInfo(exe, $"/ErrorStdOut \"{scriptPath}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        var p = Process.Start(psi)!;
        if (!wait) return (true, "", p);
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(TimeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            LbLog.Warn(Tag, "script still running after 10s — killed");
            p.Dispose();
            return (false, stderr, null);
        }
        p.Dispose();
        return (true, stderr.Trim(), null);
    }

    private static string MissingMessage(bool v1)
        => v1 ? "AutoHotkey.exe not found (LaunchBox ships it at ThirdParty\\AutoHotkey)"
              : "AutoHotkey v2 not found — drop an AutoHotkey64.exe (v2) into ThirdParty\\AutoHotkey";
}
