// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — disc-image convert / copy backends. Slice R4.
// ─────────────────────────────────────────────────────────────────────────────
//
// Native LiteBox clean-room port of the ExtendDB plugin's ArchiveConvert. Produces
// the loose launch file for the "Convert" and "Copy" operation modes, and the
// SmartExtract "convert-after-extract" follow-up (an archived cue/bin → chd).
//
// Backends (all self-contained single exes; NOT bundled by LiteBox today — resolved
// from ThirdParty\RomExtractor\ or, if the ExtendDB plugin is installed, reused from
// its thirdparty\ folder):
//
//   chdman.exe      cue/bin · gdi → chd (createcd)   ·  iso → chd (createdvd)
//                   chd → cue/bin · gdi (extractcd)  ·  chd → iso (extractdvd)
//   DolphinTool.exe iso · gcm · rvz · gcz · wia  ⇄  any of the same set (convert)
//
// Anything else (e.g. iso → cso — needs maxcso) logs "unsupported" and the caller
// falls back to launching the original / extracted file untouched. A MISSING tool
// is logged ONCE and treated the same way — the launch never fails because a backend
// binary is absent.
//
// The exact tool argument lists are reproduced verbatim from the plugin so a cache
// produced by either side is interchangeable.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rom;

/// <summary>Result of a convert / copy backend run. <see cref="OutputFilePath"/> is the
/// file the emulator should be launched against (the produced .chd / .cue / .iso, or the
/// copied file).</summary>
internal sealed class BackendResult
{
    public bool Success { get; init; }
    public string OutputFilePath { get; init; } = "";
    public bool WasCacheHit { get; init; }
    public string ErrorMessage { get; init; } = "";

    public static BackendResult Ok(string path, bool cacheHit = false) =>
        new() { Success = true, OutputFilePath = path, WasCacheHit = cacheHit };
    public static BackendResult Fail(string reason) =>
        new() { Success = false, ErrorMessage = reason };
}

/// <summary>Resolves + runs the bundled disc-image tools. Degrades gracefully: a
/// missing binary is logged once and reported as exit code -1 (the convert backend
/// turns that into a Fail, and the caller launches the untouched file).</summary>
internal static class RomToolRunner
{
    private static readonly object _lock = new();
    private static readonly HashSet<string> _loggedMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Candidate ThirdParty directories, in priority order: LiteBox's own
    /// bundle first, then the ExtendDB plugin's thirdparty (reused when installed).</summary>
    private static IEnumerable<string> ToolDirs()
    {
        var root = RomPaths.LbRoot;
        if (string.IsNullOrEmpty(root)) yield break;
        yield return Path.Combine(root, "ThirdParty", "RomExtractor");
        yield return Path.Combine(root, "ThirdParty", "ExtendDB");
        yield return Path.Combine(root, "Plugins", "ExtendDB", "thirdparty");
    }

    /// <summary>Absolute path of a bundled tool, or null when it is not present in any
    /// candidate directory (logged once per tool name).</summary>
    public static string? Resolve(string exeName)
    {
        foreach (var d in ToolDirs())
        {
            try { var p = Path.Combine(d, exeName); if (File.Exists(p)) return p; }
            catch { }
        }
        LogMissingOnce(exeName);
        return null;
    }

    /// <summary>True when the tool can be resolved (used by the config panel's capability note).</summary>
    public static bool IsAvailable(string exeName) => Resolve(exeName) != null;

    private static void LogMissingOnce(string exeName)
    {
        lock (_lock)
        {
            if (!_loggedMissing.Add(exeName.ToLowerInvariant())) return;
        }
        LbLog.Info("rom", $"convert: tool \"{exeName}\" not found under ThirdParty\\RomExtractor (or the ExtendDB plugin) — the affected conversions no-op and the file launches as-is.");
    }

    /// <summary>Runs <paramref name="exe"/> with <paramref name="args"/>, draining
    /// stdout/stderr, returning the exit code. -1 on launch failure / cancellation.</summary>
    public static int Run(string exe, IList<string> args, CancellationToken ct, string tag)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { Log(tag, "tool NOT FOUND: " + exe); return -1; }
        Log(tag, "run: \"" + exe + "\" " + string.Join(" ", args));
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) { Log(tag, "Process.Start returned null"); return -1; }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    Log(tag, "cancelled");
                    return -1;
                }
                proc.WaitForExit(250);
            }
            sw.Stop();
            Log(tag, $"exit={proc.ExitCode} ({sw.ElapsedMilliseconds}ms)");
            return proc.ExitCode;
        }
        catch (Exception ex) { Log(tag, "run failed: " + ex.Message); return -1; }
    }

    public static void Log(string tag, string msg) => LbLog.Info("rom", tag + ": " + msg);
}

/// <summary>copy mode — plain file copy into the cache, no extraction.</summary>
internal static class CopyBackend
{
    public static BackendResult Copy(string srcPath, string outDir, string outFileName)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var dest = Path.Combine(outDir, outFileName);
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(srcPath).Length)
            {
                try { File.SetLastWriteTime(dest, DateTime.Now); Directory.SetLastWriteTime(outDir, DateTime.Now); } catch { }
                RomToolRunner.Log("copy", "cache hit: " + dest);
                return BackendResult.Ok(dest, cacheHit: true);
            }
            RomToolRunner.Log("copy", "\"" + srcPath + "\" → \"" + dest + "\"");
            File.Copy(srcPath, dest, overwrite: true);
            try { File.SetLastWriteTime(dest, DateTime.Now); Directory.SetLastWriteTime(outDir, DateTime.Now); } catch { }
            return BackendResult.Ok(dest);
        }
        catch (Exception ex) { RomToolRunner.Log("copy", "FAIL: " + ex.Message); return BackendResult.Fail("copy failed: " + ex.Message); }
    }
}

/// <summary>convert mode — routes (inputExt → outputFormat) to chdman or DolphinTool.
/// Returns the produced launch file. The caller has already decided the output directory
/// (placement) and that a conversion applies to this file's extension.</summary>
internal static class ConvertBackend
{
    private static readonly HashSet<string> DolphinSet =
        new(StringComparer.OrdinalIgnoreCase) { "iso", "gcm", "rvz", "gcz", "wia" };

    public static BackendResult Convert(string inputPath, string outputFormat, string outDir, CancellationToken ct)
    {
        string inExt = (Path.GetExtension(inputPath) ?? "").TrimStart('.').ToLowerInvariant();
        string outFmt = NormalizeFormat(outputFormat);
        string outExt = OutExt(outFmt);
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        string outFile = Path.Combine(outDir, baseName + "." + outExt);

        RomToolRunner.Log("convert", $"{inExt} → {outFmt}  in=\"{inputPath}\" out=\"{outFile}\"");

        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { return BackendResult.Fail("CreateDirectory failed: " + ex.Message); }

        // Cache hit: produced file already present.
        if (File.Exists(outFile))
        {
            try { File.SetLastWriteTime(outFile, DateTime.Now); Directory.SetLastWriteTime(outDir, DateTime.Now); } catch { }
            RomToolRunner.Log("convert", "cache hit: " + outFile);
            return BackendResult.Ok(outFile, cacheHit: true);
        }

        int exit;
        if (string.Equals(inExt, "chd", StringComparison.OrdinalIgnoreCase))
            exit = ChdmanExtract(inputPath, outFmt, outFile, ct);
        else if (string.Equals(outFmt, "chd", StringComparison.OrdinalIgnoreCase))
            exit = ChdmanCreate(inputPath, inExt, outFile, ct);
        else if (DolphinSet.Contains(inExt) && DolphinSet.Contains(outFmt))
            exit = DolphinConvert(inputPath, outFmt, outFile, ct);
        else
        {
            RomToolRunner.Log("convert", $"UNSUPPORTED {inExt} → {outFmt} (no bundled tool) — leaving file untouched");
            return BackendResult.Fail($"unsupported conversion {inExt} → {outFmt}");
        }

        if (exit != 0) return BackendResult.Fail($"tool exited with code {exit}");
        if (!File.Exists(outFile)) return BackendResult.Fail("conversion completed but output is missing: " + outFile);
        try { File.SetLastWriteTime(outFile, DateTime.Now); Directory.SetLastWriteTime(outDir, DateTime.Now); } catch { }
        return BackendResult.Ok(outFile);
    }

    // chd → cue/bin · gdi (extractcd)  ·  chd → iso (extractdvd)
    private static int ChdmanExtract(string chd, string outFmt, string outFile, CancellationToken ct)
    {
        var exe = RomToolRunner.Resolve("chdman.exe");
        if (exe == null) return -1;
        string verb = outFmt == "iso" ? "extractdvd" : "extractcd";
        return RomToolRunner.Run(exe, new[] { verb, "-i", chd, "-o", outFile, "-f" }, ct, "chdman");
    }

    // cue/bin · gdi → chd (createcd)  ·  iso → chd (createdvd)
    private static int ChdmanCreate(string input, string inExt, string outFile, CancellationToken ct)
    {
        var exe = RomToolRunner.Resolve("chdman.exe");
        if (exe == null) return -1;
        string verb = inExt == "iso" ? "createdvd" : "createcd";
        return RomToolRunner.Run(exe, new[] { verb, "-i", input, "-o", outFile, "-f" }, ct, "chdman");
    }

    // iso/gcm/rvz/gcz/wia → iso/gcz/wia/rvz (DolphinTool convert)
    private static int DolphinConvert(string input, string outFmt, string outFile, CancellationToken ct)
    {
        var exe = RomToolRunner.Resolve("DolphinTool.exe");
        if (exe == null) return -1;
        return RomToolRunner.Run(exe, new[] { "convert", "-i", input, "-o", outFile, "-f", outFmt }, ct, "dolphin");
    }

    /// <summary>Normalises a table output token to a tool format keyword.</summary>
    private static string NormalizeFormat(string fmt)
    {
        var f = (fmt ?? "").Trim().ToLowerInvariant();
        if (f == "cue/bin" || f == "cue+bin" || f == "bin") return "cue";
        return f;
    }

    /// <summary>File extension of the produced launch file for a format.</summary>
    private static string OutExt(string fmt) => fmt switch
    {
        "cue" => "cue",
        "iso" => "iso",
        "gdi" => "gdi",
        "rvz" => "rvz",
        "gcz" => "gcz",
        "wia" => "wia",
        "chd" => "chd",
        _ => fmt,
    };
}
