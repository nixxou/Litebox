// What a video actually IS: duration, resolution, frame rate, codec. Read from libvlc's own parser (the same
// engine that will play it), so what we report is what will be played — not what a container header claims.
//
// Cached per path+mtime: parsing is cheap (~10 ms, local file, no decoding) but the Info dialog and the player
// both ask for it, and a page rebuild asks again.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace LbApiHost.Host.Video;

internal static class VideoProbe
{
    internal readonly record struct Info(long DurationMs, int Width, int Height, double Fps, string Codec);

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Info?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when libvlc is unavailable or the file can't be parsed — callers show "?" rather than fail.</summary>
    public static Info? Get(string path)
    {
        string key;
        try { var fi = new FileInfo(path); if (!fi.Exists) return null; key = path + "|" + fi.LastWriteTimeUtc.Ticks; }
        catch { return null; }

        lock (_lock) { if (_cache.TryGetValue(key, out var hit)) return hit; }

        Info? info = Parse(path);
        lock (_lock) { _cache[key] = info; }
        return info;
    }

    private static Info? Parse(string path)
    {
        var lib = VlcService.Instance;
        if (lib == null) return null;
        try
        {
            using var m = new VlcMedia(lib, path, FromType.FromPath);
            m.Parse(MediaParseOptions.ParseLocal).Wait(TimeSpan.FromSeconds(10));

            int w = 0, h = 0; double fps = 0; string codec = "";
            foreach (var t in m.Tracks)
            {
                if (t.TrackType != TrackType.Video) continue;
                w = (int)t.Data.Video.Width;
                h = (int)t.Data.Video.Height;
                if (t.Data.Video.FrameRateDen > 0) fps = t.Data.Video.FrameRateNum / (double)t.Data.Video.FrameRateDen;
                try { codec = m.CodecDescription(TrackType.Video, t.Codec) ?? ""; } catch { }
                break;
            }
            return new Info(m.Duration, w, h, fps, codec);
        }
        catch { return null; }
    }

    /// <summary>mm:ss (or h:mm:ss past an hour). "?" for an unknown duration.</summary>
    public static string Duration(long ms)
    {
        if (ms <= 0) return "?";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
