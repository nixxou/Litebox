// ffmpeg, borrowed from LaunchBox exactly like libvlc is: it ships a full build at <LB>\ThirdParty\FFMPEG — so
// the video trimmer costs 0 MB of payload. When a LaunchBox install somehow lacks it, Available goes false and
// the trim UI simply doesn't appear.
//
// ffmpeg ALONE, deliberately. Everything here used to go through ffprobe, until LaunchBox 14 shipped ffmpeg.exe
// without it and took the trimmer down with it — and, less obviously, the YouTube downloader too, which read the
// same Available and concluded it could not merge. Rather than hunt for a ffprobe matching whatever build
// LaunchBox ships next (they link by soname major, so the day it moves to avcodec-63 a bundled one stops
// loading), both jobs were moved onto ffmpeg:
//
//   * keyframes — "-skip_frame nokey -vf showinfo" instead of ffprobe's "-show_packets". Measured against the
//     old path on the same files: 63 timestamps vs 63 on an AV1 trailer, agreeing to 0.000000000 s, and 5 vs 5
//     on an H.264 video snap.
//   * duration — the "Duration:" banner instead of "-show_entries format=duration". 10 ms of precision instead
//     of 1 ms, which no trimmer can feel: it snaps to keyframes that sit seconds apart.
//
// Everything here is a short, bounded, window-less child process. The keyframe pass now DECODES the keyframes
// rather than demuxing past them, so it is not free on a long film — 0.09 s on a 77 s trailer, and this UI only
// ever sees trailers and video snaps.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Video;

internal static class FfmpegService
{
    private static string? Root
    {
        get
        {
            try
            {
                string? root = MediaResolver.LbRoot;
                return string.IsNullOrEmpty(root) ? null : Install.NativeInstaller.FfmpegDir(root);
            }
            catch { return null; }
        }
    }

    private static string? DirWith(string exe) => Root is { } d && File.Exists(Path.Combine(d, exe)) ? d : null;

    private static string? Dir => DirWith("ffmpeg.exe");

    public static string? FfmpegExe => Dir is { } d ? Path.Combine(d, "ffmpeg.exe") : null;

    /// <summary>ffmpeg.exe is on disk (cheap; no process is started) — everything here needs it and nothing
    /// here needs anything else, so this one question answers for the trimmer and the downloader alike.</summary>
    public static bool Available => Dir != null;

    /// <summary>Run a tool to completion. Returns (exitCode, stdout, stderr); exitCode -1 on failure/timeout.</summary>
    public static (int Code, string Out, string Err) Run(string exe, string args, int timeoutMs = 120_000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // never flash a console over the UI
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "could not start " + Path.GetFileName(exe));

            // Read both pipes BEFORE waiting: a full pipe buffer would deadlock the child.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "", "timed out"); }
            return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    private static readonly Regex DurationRe = new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>Container duration in seconds (0 when unknown).</summary>
    public static double Duration(string path)
    {
        if (FfmpegExe is not { } exe) return 0;
        // Asked for no output, ffmpeg prints what it knows about the input and exits NON-ZERO ("At least one
        // output file must be specified"). That banner is the whole point of the call, so the exit code is
        // ignored on purpose and only the parse decides.
        var (_, _, err) = Run(exe, $"-hide_banner -i \"{path}\"", 20_000);
        var m = DurationRe.Match(err);
        if (!m.Success) return 0;
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
             + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
             + double.Parse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ── Keyframe index ────────────────────────────────────────────────────────
    // The ONLY places a stream copy can start; typical game trailers carry one every 2-10 s, which is exactly
    // why a no-re-encode trim can't be frame-accurate. "-skip_frame nokey" tells the decoder to throw away
    // everything that isn't one, and showinfo then reports the pts_time of each survivor. Cached per
    // (path, mtime, size) so re-opening the trimmer is instant.

    private static readonly Regex PtsRe = new(@"pts_time:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    private static readonly object _lock = new();
    private static readonly Dictionary<string, List<double>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<double> Keyframes(string path)
    {
        string key;
        try { var fi = new FileInfo(path); key = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
        catch { return Array.Empty<double>(); }

        lock (_lock) { if (_cache.TryGetValue(key, out var hit)) return hit; }

        var list = new List<double>();
        if (FfmpegExe is { } exe)
        {
            // showinfo writes at INFO level, so -loglevel must NOT be raised to error — doing so returns a
            // perfectly successful run with an empty index, which reads exactly like a video with no keyframes.
            var (_, _, err) = Run(exe,
                $"-hide_banner -loglevel info -skip_frame nokey -i \"{path}\" -vf showinfo -an -f null -",
                60_000);
            foreach (Match m in PtsRe.Matches(err))
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    list.Add(t);
            list.Sort();
        }
        lock (_lock) { _cache[key] = list; }
        return list;
    }

    /// <summary>The keyframe closest to <paramref name="t"/> (the value itself when there are none).</summary>
    public static double Snap(IReadOnlyList<double> keys, double t)
    {
        if (keys == null || keys.Count == 0) return t;
        double best = keys[0], bestD = Math.Abs(keys[0] - t);
        for (int i = 1; i < keys.Count; i++)
        {
            double d = Math.Abs(keys[i] - t);
            if (d < bestD) { best = keys[i]; bestD = d; }
        }
        return best;
    }
}
