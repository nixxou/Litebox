// ffmpeg / ffprobe, borrowed from LaunchBox exactly like libvlc is: it ships a full build (8.1.1) at
// <LB>\ThirdParty\FFMPEG — so the video trimmer costs 0 MB of payload. When a LaunchBox install somehow lacks
// it, Available goes false and the trim UI simply doesn't appear.
//
// Everything here is a short, bounded, window-less child process. Nothing is decoded: the keyframe index comes
// from a pure DEMUX pass (-show_packets), which on a 30 s trailer takes ~0.15 s.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Video;

internal static class FfmpegService
{
    private static string? Dir
    {
        get
        {
            try
            {
                string? root = MediaResolver.LbRoot;
                if (string.IsNullOrEmpty(root)) return null;
                string d = Install.NativeInstaller.FfmpegDir(root);
                return File.Exists(Path.Combine(d, "ffmpeg.exe")) && File.Exists(Path.Combine(d, "ffprobe.exe")) ? d : null;
            }
            catch { return null; }
        }
    }

    public static string? FfmpegExe => Dir is { } d ? Path.Combine(d, "ffmpeg.exe") : null;
    public static string? FfprobeExe => Dir is { } d ? Path.Combine(d, "ffprobe.exe") : null;

    /// <summary>True when both tools are on disk (cheap; no process is started).</summary>
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

    /// <summary>Container duration in seconds (0 when unknown).</summary>
    public static double Duration(string path)
    {
        if (FfprobeExe is not { } probe) return 0;
        var (code, so, _) = Run(probe, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"", 20_000);
        if (code != 0) return 0;
        return double.TryParse(so.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    // ── Keyframe index ────────────────────────────────────────────────────────
    // The ONLY places a stream copy can start. -show_packets is a demux-only pass (no decoding), and the "K"
    // flag marks a keyframe; typical game trailers carry one every 2-10 s, which is exactly why a no-re-encode
    // trim can't be frame-accurate. Cached per (path, mtime, size) so re-opening the trimmer is instant.

    private static readonly object _lock = new();
    private static readonly Dictionary<string, List<double>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<double> Keyframes(string path)
    {
        string key;
        try { var fi = new FileInfo(path); key = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
        catch { return Array.Empty<double>(); }

        lock (_lock) { if (_cache.TryGetValue(key, out var hit)) return hit; }

        var list = new List<double>();
        if (FfprobeExe is { } probe)
        {
            var (code, so, _) = Run(probe,
                $"-v error -select_streams v:0 -show_packets -show_entries packet=pts_time,flags -of csv=p=0 \"{path}\"",
                60_000);
            if (code == 0)
            {
                foreach (var line in so.Split('\n'))
                {
                    // "8.333333,K__" — the flags field starts with K on a keyframe.
                    int comma = line.IndexOf(',');
                    if (comma <= 0 || comma + 1 >= line.Length) continue;
                    if (line[comma + 1] != 'K') continue;
                    if (double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        list.Add(t);
                }
                list.Sort();
            }
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
