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

    // The native files we ship (as ".dll.api") and where they land (".dll") next to the exe.
    private static readonly (string src, string dst)[] Payload =
    {
        ("RahasherExtendDB.exe", "RahasherExtendDB.exe"),
        ("7z.dll.api",           "7z.dll"),
        ("MSVCP140.dll.api",     "MSVCP140.dll"),
        ("VCRUNTIME140.dll.api", "VCRUNTIME140.dll"),
        ("VCRUNTIME140_1.dll.api","VCRUNTIME140_1.dll"),
        // Licences (GPL/LGPL) — ship alongside the binary.
        ("RAHasher.COPYING.txt",     "COPYING.txt"),
        ("RAHasher.7z-LICENSE.txt",  "7z.dll-LICENSE.txt"),
        ("RAHasher.RVZ-SUPPORT.txt", "RVZ-SUPPORT.txt"),
    };

    private static string RaDir => Path.Combine(MediaResolver.LbRoot ?? "", "ThirdParty", "RetroAchievements");

    /// <summary>Ensures RahasherExtendDB.exe is present in LB\ThirdParty\RetroAchievements\, deploying the
    /// bundled payload there only if absent. Returns the exe path, or null when it can't be made available.</summary>
    public static string? EnsureExe()
    {
        try
        {
            string raDir = RaDir;
            string exe = Path.Combine(raDir, "RahasherExtendDB.exe");
            if (File.Exists(exe)) return exe;   // already deployed (by us or by ExtendDB)

            // Source: the bundled payload next to LiteBox.exe (thirdparty\ first, then the root).
            string root = AppContext.BaseDirectory;
            string SrcDir(string f)
            {
                string a = Path.Combine(root, "thirdparty", f);
                if (File.Exists(a)) return a;
                return Path.Combine(root, f);
            }

            Directory.CreateDirectory(raDir);
            foreach (var (src, dst) in Payload)
            {
                string from = SrcDir(src), to = Path.Combine(raDir, dst);
                try { if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to); }
                catch (Exception ex) { Console.WriteLine($"[ra-lite] deploy {dst} failed: {ex.Message}"); }
            }
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
        var args = new List<string> { "--arc-details" };
        if (!string.IsNullOrEmpty(arcExt)) { args.Add("--arc-ext"); args.Add(arcExt); }
        args.Add(consoleId.ToString());
        args.Add(archivePath);
        var stdout = Run(exe, args, 120000);
        if (stdout == null) return list;
        foreach (var line in stdout.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{32})\s+([0-9a-fA-F]+)\s+(\d+)\s+(.+)$");
            if (m.Success) list.Add(new ArcEntry(m.Groups[1].Value.ToLowerInvariant(), m.Groups[4].Value.Trim()));
        }
        return list;
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
