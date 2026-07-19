// LiteBox-native RA hashing — the fallback hasher when ExtendDB isn't resolving RA.
//
//   • EnsureDeployed(): copies the bundled RahasherExtendDB.exe (+ its native .dll deps, shipped as
//     ".dll.api" so nothing tries to load them as managed assemblies) into LB\ThirdParty\RetroAchievements\
//     ONLY IF the exe isn't already there — shared with ExtendDB, whoever lands first wins. Idempotent.
//   • ComputeHash(): the three RA hash flavours, mirroring ExtendDB's RaScanner.ComputeOne —
//       ARC      → MD5(filename without extension, case-sensitive) — no RAHasher.
//       archive  → RahasherExtendDB --arc-details (lists + hashes every ROM entry in memory).
//       plain    → RahasherExtendDB <id> <file> (single hash).
//
// Process is spawned via the static Process.Start overload + drained concurrently (no pipe deadlock).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Ra;

internal static class RaHasherLite
{
    /// <summary>One ROM entry hashed inside an archive.</summary>
    public readonly struct ArcEntry
    {
        public ArcEntry(string hash, string name) { Hash = hash; Name = name; }
        public string Hash { get; }
        public string Name { get; }
    }

    private static string RaDir => Path.Combine(MediaResolver.LbRoot ?? "", "ThirdParty", "RetroAchievements");

    /// <summary>Optional live progress sink for archive hashing (done, total). Set by the on-select UI
    /// path only; when non-null, ComputeArchiveEntries passes --arc-flush and streams RAHasher's output,
    /// reporting after each entry so a progress bar can update. Null (scans, launch-correct) = no streaming
    /// overhead, plain read-to-end. Global + volatile: set right around one on-select parse and cleared in
    /// a finally; on-select is debounced/single-flight so overlap isn't a concern.</summary>
    internal static volatile Action<int, int>? ArcProgress;

    /// <summary>Returns the RAHasher exe to use: the user's RaPanelConfig.HasherPath override when it
    /// points at an existing file (plugin RaHasherPath parity), else the deployed copy in
    /// LB\ThirdParty\RetroAchievements\ (NativeInstaller deploys it on first miss). Null when neither
    /// can be made available.</summary>
    public static string? EnsureExe()
    {
        try
        {
            var custom = RaPanelConfig.HasherPath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

            string exe = Path.Combine(RaDir, "RahasherExtendDB.exe");
            if (!File.Exists(exe)) LbApiHost.Host.Install.NativeInstaller.EnsureDeployed(MediaResolver.LbRoot);
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception ex) { Console.WriteLine($"[ra-lite] EnsureExe failed: {ex.Message}"); return null; }
    }

    /// <summary>MD5 of the filename WITHOUT extension, bytes verbatim (case-SENSITIVE) — the RA arcade hash.</summary>
    public static string ArcadeNameHash(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? "");
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(name));
        var sb = new StringBuilder(32);
        foreach (var b in md5) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Lists + hashes every ROM entry of an archive (one --arc-details call). Empty on failure.
    /// <paramref name="arcExt"/> (comma-separated, no dots) filters to real ROM extensions; pass "" to hash all.</summary>
    public static List<ArcEntry> ComputeArchiveEntries(int consoleId, string archivePath, string arcExt)
    {
        var list = new List<ArcEntry>();
        var exe = EnsureExe();
        if (exe == null) return list;

        var prog = ArcProgress;   // snapshot; non-null only on the on-select UI path
        int total = 0, done = 0;
        if (prog != null)
        {
            total = CountRomEntries(archivePath, arcExt);   // denominator from a quick 7z listing (no hashing)
            try { prog(0, total); } catch { }
        }

        var args = new List<string> { "--arc-details" };
        if (prog != null) args.Add("--arc-flush");   // ask RAHasher to flush each line as produced (throttled)
        if (!string.IsNullOrEmpty(arcExt)) { args.Add("--arc-ext"); args.Add(arcExt); }
        args.Add(consoleId.ToString());
        args.Add(archivePath);

        RunStreaming(exe, args, 120000, line =>
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{32})\s+([0-9a-fA-F]+)\s+(\d+)\s+(.+)$");
            if (!m.Success) return;
            list.Add(new ArcEntry(m.Groups[1].Value.ToLowerInvariant(), m.Groups[4].Value.Trim()));
            if (prog != null) { done++; try { prog(done, total > 0 ? total : done); } catch { } }
        });
        return list;
    }

    /// <summary>Count the entries RAHasher will actually process (matching <paramref name="arcExt"/>), via a
    /// quick 7-Zip listing — no hashing. The determinate progress-bar denominator. 0 on any failure.</summary>
    private static int CountRomEntries(string archivePath, string arcExt)
    {
        try
        {
            var entries = LbApiHost.Host.Rom.SevenZipList.List(archivePath);
            if (entries == null) return 0;
            if (string.IsNullOrEmpty(arcExt))
            {
                int n = 0; foreach (var e in entries) if (!e.IsDirectory) n++; return n;
            }
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in arcExt.Split(','))
            {
                var x = raw.Trim().TrimStart('.');
                if (x.Length > 0) exts.Add("." + x);
            }
            int c = 0;
            foreach (var e in entries)
            {
                if (e.IsDirectory) continue;
                if (exts.Contains(Path.GetExtension(e.Path ?? ""))) c++;
            }
            return c;
        }
        catch { return 0; }
    }

    /// <summary>Run RAHasher and hand each stdout line to <paramref name="onLine"/> as it arrives (blocking
    /// ReadLine — call off the UI thread). With --arc-flush the lines stream live; without it they arrive in
    /// one burst at process end. stderr is drained concurrently so it can't deadlock the pipe.</summary>
    private static void RunStreaming(string exe, IEnumerable<string> args, int timeoutMs, Action<string> onLine)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return;
            var errTask = p.StandardError.ReadToEndAsync();
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null) { try { onLine(line); } catch { } }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
        }
        catch (Exception ex) { Console.WriteLine($"[ra-lite] RAHasher stream failed: {ex.Message}"); }
    }

    /// <summary>RAHasher single-file hash (plain ROM / disc image), or null.</summary>
    public static string? ComputeSingle(int consoleId, string path)
    {
        var exe = EnsureExe();
        if (exe == null) return null;
        var stdout = Run(exe, new[] { consoleId.ToString(), path }, 60000);
        if (stdout == null) return null;
        foreach (var line in stdout.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 32 && IsHex(t)) return t.ToLowerInvariant();
        }
        return null;
    }

    private static string? Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
            return outTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) { Console.WriteLine($"[ra-lite] RAHasher run failed: {ex.Message}"); return null; }
    }

    private static bool IsHex(string s)
    {
        foreach (var ch in s) if (!Uri.IsHexDigit(ch)) return false;
        return true;
    }
}
