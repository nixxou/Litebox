// The one LibVLC instance LiteBox owns — used for video thumbnails today, and in-app playback later.
//
// LAZY: nothing is loaded until something actually asks for it. Touch no video page, pay nothing (measured:
// libvlc idle costs ~12 MB of private memory, and ~50 MB once frames have been decoded — that figure PLATEAUS,
// it does not creep).
//
// SHARED: one long-lived LibVLC for the whole session. Creating one per thumbnail works but is 2.5x slower
// (110 ms vs 42 ms) because the plugin bank is re-scanned every time.
//
// RELEASABLE: Shutdown() drops the instance and hands the memory back. It is called when a game launches —
// LiteBox is idle then, and the RAM is better spent on the game. The next thumbnail transparently re-creates
// it (~200 ms).
//
// COSTS NOTHING TO SHIP: we bundle no libvlc at all. LaunchBox already installs a complete libvlc 3.0.23 (366
// plugins, 133 MB) at <LB>\ThirdParty\VLC\x64 — the very engine it plays videos with — so we just point at it.
// Verified end to end: 500/500 thumbnails against LaunchBox's own build. It is slower per frame than a trimmed
// private copy (111 ms vs 42 ms, its plugin bank is far bigger to probe), which is irrelevant because every
// thumbnail is decoded once and then cached on disk. If a LaunchBox install somehow lacks it, Available goes
// false and the video pages simply list files without thumbnails.

#nullable enable

using System;
using System.IO;
using LibVLCSharp.Shared;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Video;

internal static class VlcService
{
    private static readonly object _lock = new();
    private static LibVLC? _lib;
    private static bool _initFailed;

    /// <summary>Where the trimmed libvlc was deployed, or null when it isn't there (feature simply stays off).</summary>
    public static string? NativeDir
    {
        get
        {
            try
            {
                string? root = MediaResolver.LbRoot;
                if (string.IsNullOrEmpty(root)) return null;
                string dir = Install.NativeInstaller.VlcDir(root);
                return File.Exists(Path.Combine(dir, "libvlc.dll")) ? dir : null;
            }
            catch { return null; }
        }
    }

    /// <summary>True when video features can work at all (the natives are on disk). Cheap; no init.</summary>
    public static bool Available => !_initFailed && NativeDir != null;

    /// <summary>
    /// The shared LibVLC, created on first use. Null when the natives are missing or init failed — every
    /// caller must degrade gracefully rather than throw (a LiteBox without libvlc simply shows no thumbnails).
    /// </summary>
    public static LibVLC? Instance
    {
        get
        {
            if (_initFailed) return null;
            lock (_lock)
            {
                if (_lib != null) return _lib;

                string? dir = NativeDir;
                if (dir == null) { _initFailed = true; return null; }
                try
                {
                    Core.Initialize(dir);
                    // NO --no-audio here: this instance also PLAYS videos (VideoPlayerDialog). Silencing the
                    // thumbnailer is a per-MEDIA option (":no-audio") — an instance-wide one would mute playback.
                    _lib = new LibVLC(
                        "--no-osd", "--no-spu",    // no overlays burned into a thumbnail frame
                        "--no-video-title-show",
                        "--quiet");
                    Console.WriteLine("[vlc] initialized from " + dir);
                    return _lib;
                }
                catch (Exception ex)
                {
                    _initFailed = true;   // don't retry every thumbnail
                    Console.WriteLine("[vlc] init failed: " + ex.Message);
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// Drop the instance and give the memory back (~50 MB once frames have been decoded). Called when a game
    /// launches: LiteBox is idle, the game wants the RAM. Re-created lazily on the next use — callers never
    /// need to know this happened.
    /// </summary>
    public static void Shutdown()
    {
        LibVLC? doomed;
        lock (_lock)
        {
            doomed = _lib;
            _lib = null;
            _initFailed = false;   // a shutdown is not a failure: allow a later re-init
        }
        if (doomed == null) return;
        try { doomed.Dispose(); Console.WriteLine("[vlc] released (game launching)"); } catch { }
    }
}
