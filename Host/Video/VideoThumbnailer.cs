// A still frame from a video, taken ~20% in (the start of a video is usually a logo or a black fade, so the
// first frame makes a useless thumbnail).
//
// HEADLESS: we register video callbacks and let libvlc decode straight into our own buffer. The obvious
// alternative — MediaPlayer.TakeSnapshot — needs a real video output (a window), which we don't have.
//
// Two lessons paid for during the spike, both load-bearing:
//   • ONE reusable buffer. Allocating one per video inside the Format callback and freeing it by hand (i.e.
//     outside libvlc's Cleanup callback) leaked ~0.45 MB per thumbnail — on a 1200-video library that was
//     +500 MB. With a single buffer, private memory PLATEAUS at ~55 MB across 500 thumbnails.
//   • Stop() is ASYNCHRONOUS in VLC 3. Tearing down without waiting for the Stopped event leaves the decoder
//     writing into the buffer we're about to reuse.
//
// Extraction is serialized (one at a time): the callbacks share the buffer, and at ~42 ms a frame there is
// nothing to gain from racing them. Results are cached on disk, so a video is only ever decoded once — in the
// dedicated VIDEO sub-folder (ThumbCache.VideoFolder = <LB>\Plugins\ExtendDB\cache\thumbs\video), at the same
// 360 px / JPEG convention; see CachePath for why the key still has to be video-specific.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using LibVLCSharp.Shared;
// LbApiHost.Host.Media (our namespace) would otherwise shadow LibVLCSharp's Media TYPE.
using VlcMedia = LibVLCSharp.Shared.Media;

namespace LbApiHost.Host.Video;

internal static class VideoThumbnailer
{
    /// <summary>Where in the video the frame is taken from. 0.20 = 20% in.</summary>
    public const double Position = 0.20;

    // Same bounding box as every other LiteBox thumbnail (ThumbCache.DefaultMaxDim), so the frames sit in the
    // shared thumbs folder under one convention — and are already the right size for the game page later.
    private const int MaxDim = Media.ThumbCache.DefaultMaxDim;
    private const int MaxW = MaxDim, MaxH = MaxDim;

    private static readonly object _gate = new();          // one extraction at a time (the callbacks share state)
    private static IntPtr _buf;                            // THE buffer — allocated once, never per-video
    private static int _w, _h, _pitch;
    private static volatile bool _captured;
    private static Bitmap? _frame;
    private static readonly ManualResetEventSlim _got = new(false);

    // Delegates must be rooted: libvlc keeps the native function pointers and would call into collected stubs.
    private static readonly MediaPlayer.LibVLCVideoFormatCb _formatCb = FormatCb;
    private static readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb = CleanupCb;
    private static readonly MediaPlayer.LibVLCVideoLockCb _lockCb = LockCb;
    private static readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb = DisplayCb;

    /// <summary>
    /// The thumbnail for a video, decoded once and cached on disk. Null when libvlc isn't available, the file
    /// is gone, or the video can't be decoded — callers must degrade gracefully.
    /// </summary>
    public static Image? Get(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return null;

        string? cache = CachePath(videoPath);
        if (cache != null && File.Exists(cache))
        {
            try { using var ms = new MemoryStream(File.ReadAllBytes(cache)); return new Bitmap(Image.FromStream(ms)); }
            catch { try { File.Delete(cache); } catch { } }   // corrupt entry → re-extract
        }

        var frame = Extract(videoPath);
        if (frame == null) return null;

        if (cache != null)
        {
            try { frame.Save(cache, ImageFormat.Jpeg); } catch { }
        }
        return frame;
    }

    /// <summary>True when a thumbnail for this video is already on disk (no decode needed).</summary>
    public static bool IsCached(string videoPath)
    {
        var c = CachePath(videoPath);
        return c != null && File.Exists(c);
    }

    /// <summary>
    /// Same thing for a video we DON'T own: libvlc opens the URL and range-requests its way to the 20% mark, so
    /// we decode a frame without downloading the whole file (a Steam trailer is 5-10 MB; we pull a fraction).
    /// HLS works too — VLC just fetches the segments around that point. <paramref name="referer"/> is the header
    /// the CDN gates on (ExtendDB's chain tells us which); <paramref name="key"/> keys the disk cache (the row's
    /// CRC, which never changes for a given DB entry).
    /// </summary>
    public static Image? GetFromUrl(string url, string? referer, string key)
    {
        if (string.IsNullOrEmpty(url)) return null;

        string? cache = UrlCachePath(key);
        if (cache != null && File.Exists(cache))
        {
            try { using var ms = new MemoryStream(File.ReadAllBytes(cache)); return new Bitmap(Image.FromStream(ms)); }
            catch { try { File.Delete(cache); } catch { } }
        }

        var frame = Extract(url, referer);
        if (frame == null) return null;
        if (cache != null) { try { frame.Save(cache, ImageFormat.Jpeg); } catch { } }
        return frame;
    }

    // ── Extraction ────────────────────────────────────────────────────────────
    private static Bitmap? Extract(string path, string? referer = null)
    {
        var lib = VlcService.Instance;
        if (lib == null) return null;

        bool remote = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var from = remote ? FromType.FromLocation : FromType.FromPath;

        lock (_gate)
        {
            try
            {
                // 1) parse, only to learn the duration (so we know where 20% is). A remote parse must be allowed
                //    to hit the network — ParseLocal alone would return a 0 duration for a URL.
                long durMs;
                using (var probe = new VlcMedia(lib, path, from))
                {
                    if (remote) ApplyNet(probe, referer);
                    probe.Parse(remote ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal)
                         .Wait(TimeSpan.FromSeconds(remote ? 20 : 10));
                    durMs = probe.Duration;
                }
                if (durMs <= 0) return null;
                double startSec = (durMs / 1000.0) * Position;

                // 2) reopen seeking to that point BEFORE playback, so we decode as little as possible
                using var media = new VlcMedia(lib, path, from);
                if (remote) ApplyNet(media, referer);
                media.AddOption(":start-time=" + startSec.ToString("0.###", CultureInfo.InvariantCulture));
                media.AddOption(":avcodec-hw=none");   // headless: no D3D
                media.AddOption(":no-audio");          // per-media, NOT instance-wide: the same LibVLC plays videos

                _captured = false;
                _frame?.Dispose();
                _frame = null;
                _got.Reset();

                using var mp = new MediaPlayer(media);
                mp.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
                mp.SetVideoCallbacks(_lockCb, null, _displayCb);

                if (!mp.Play()) return null;
                _got.Wait(TimeSpan.FromSeconds(15));

                // Stop is async: wait for it, or the decoder keeps writing into the buffer we reuse next time.
                using var stopped = new ManualResetEventSlim(false);
                mp.Stopped += (_, _) => stopped.Set();
                try { mp.Stop(); } catch { }
                stopped.Wait(TimeSpan.FromSeconds(3));

                var result = _frame;
                _frame = null;
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vlc] thumb failed for {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Network options every remote media needs: the Referer some CDNs gate on (Steam, LaunchBox,
    /// screenscraper — ExtendDB's chain says which), a browser UA, and enough buffering for a seek.</summary>
    internal static void ApplyNet(VlcMedia m, string? referer)
    {
        if (!string.IsNullOrEmpty(referer)) m.AddOption(":http-referrer=" + referer);
        m.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        m.AddOption(":network-caching=2000");
    }

    // libvlc asks what we want: RV32, scaled down to thumbnail size, aspect preserved.
    private static uint FormatCb(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        double sc = Math.Min(1.0, Math.Min((double)MaxW / width, (double)MaxH / height));
        _w = Math.Max(2, (int)(width * sc)) & ~1;    // even dimensions keep the scaler happy
        _h = Math.Max(2, (int)(height * sc)) & ~1;
        _pitch = _w * 4;

        Marshal.Copy(System.Text.Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
        width = (uint)_w; height = (uint)_h;
        pitches = (uint)_pitch; lines = (uint)_h;

        if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(MaxW * MaxH * 4);   // once, for the whole session
        return 1;
    }

    private static void CleanupCb(ref IntPtr opaque) { }

    private static IntPtr LockCb(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buf);
        return IntPtr.Zero;
    }

    private static void DisplayCb(IntPtr opaque, IntPtr picture)
    {
        if (_captured) return;   // first frame at the seek point is the one we want
        try
        {
            using var view = new Bitmap(_w, _h, _pitch, PixelFormat.Format32bppRgb, _buf);
            _frame = new Bitmap(view);   // copy out of the VLC buffer before it's overwritten
            _captured = true;
        }
        catch { /* leave _frame null; Extract returns null */ }
        finally { _got.Set(); }
    }

    // ── Disk cache ────────────────────────────────────────────────────────────
    // Under the VIDEO sub-folder (<LB>\Plugins\ExtendDB\cache\thumbs\video), same 360 px JPEG output as the image
    // thumbnails. The KEY is video-specific though — it also carries the mtime and the frame position, because
    // replacing a video (same name, same size even) must re-extract instead of showing the stale frame, and
    // moving the 20% mark must not collide with the frames already on disk. "vid-" keeps them recognisable.
    private static string? CachePath(string videoPath)
    {
        try
        {
            var fi = new FileInfo(videoPath);
            string key = videoPath.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks
                       + "|" + MaxDim + "|v" + Position.ToString("0.###", CultureInfo.InvariantCulture);   // invariant: the key must not depend on the locale
            using var md5 = MD5.Create();
            return Path.Combine(Media.ThumbCache.VideoFolder,
                "vid-" + Convert.ToHexString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".jpg");
        }
        catch { return null; }
    }

    // Web videos: same video sub-folder, "vidweb-" prefix. Keyed by the caller (the DB row's CRC — immutable for a row),
    // so a frame pulled over the network is decoded ONCE for good, not on every toggle of "show web videos".
    private static string? UrlCachePath(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            using var md5 = MD5.Create();
            string seed = key + "|" + MaxDim + "|v" + Position.ToString("0.###", CultureInfo.InvariantCulture);
            return Path.Combine(Media.ThumbCache.VideoFolder,
                "vidweb-" + Convert.ToHexString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant() + ".jpg");
        }
        catch { return null; }
    }
}
