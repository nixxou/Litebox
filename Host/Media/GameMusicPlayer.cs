// Ambient game music — the engine behind View ▸ Media ▸ Auto-Play Music / Shuffle Music (LaunchBox's
// AutoPlayMusic / ShuffleMusic Settings.xml keys; MainWindow reads them and drives this player).
//
// Audio-only MediaPlayer on the shared VlcService LibVLC (no Hwnd — nothing is rendered). One track
// plays at a time; when it ends the next one starts (shuffle = random never-same-twice, else list
// order, wrapping). Play() with the key of the game ALREADY playing only refreshes the track list and
// shuffle flag — selecting the same game twice must not restart its music.
//
// Silence rules live at the CALLERS, all funnelled through Stop():
//   • selection moved to a node / nothing        → MainWindow (ShowNodeDetails / ShowDetails(null))
//   • a game launches                            → VlcService.Stopping (subscribed here), raised by
//                                                  StopPlayback/Shutdown on launch
//   • a web kiosk opens (LiteBox Web / BigBox)   → WebKioskWindow.Toggle + KioskBridge toggles
//   • a video plays WITH SOUND                   → VideoBlock.SetMuted(false) / Play() unmuted
//
// Every entry point is fail-soft and lock-guarded; the EndReached continuation hops to the thread
// pool first (calling back into libvlc from its own event thread deadlocks — same lesson as
// VideoBlock).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;
using LbApiHost.Host.Video;

namespace LbApiHost.Host.Media;

internal static class GameMusicPlayer
{
    private static readonly object _lock = new();
    private static readonly Random _rng = new();
    private static MediaPlayer? _mp;
    private static List<string> _tracks = new();
    private static int _ix;
    private static bool _shuffle;
    private static string? _key;      // identity of the game whose music is playing (its Id)
    private static bool _hooked;

    /// <summary>Start (or keep) the music for one game. <paramref name="key"/> identifies the game;
    /// the same key keeps the current track running and only refreshes the list/shuffle flag.
    /// An empty track list stops playback.</summary>
    public static void Play(string key, IEnumerable<string> tracks, bool shuffle)
    {
        var list = (tracks ?? Enumerable.Empty<string>())
            .Where(t => { try { return !string.IsNullOrEmpty(t) && File.Exists(t); } catch { return false; } })
            .ToList();
        lock (_lock)
        {
            if (!_hooked) { try { VlcService.Stopping += () => Stop(); _hooked = true; } catch { } }
            if (list.Count == 0) { StopLocked(); return; }
            if (_mp != null && string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
            {
                _tracks = list; _shuffle = shuffle;                  // same game — don't restart the track
                if (_ix >= list.Count) _ix = 0;
                return;
            }
            _key = key; _tracks = list; _shuffle = shuffle;
            _ix = shuffle && list.Count > 1 ? _rng.Next(list.Count) : 0;
            StartCurrentLocked();
        }
    }

    public static void Stop() { lock (_lock) StopLocked(); }

    private static void StartCurrentLocked()
    {
        StopPlayerLocked();
        var lib = VlcService.Instance;
        if (lib == null || _tracks.Count == 0) { _key = null; return; }
        try
        {
            var mp = new MediaPlayer(lib);
            _mp = mp;
            // VLC event thread → thread pool before touching the player again (libvlc self-deadlock).
            mp.EndReached += (_, _) => System.Threading.ThreadPool.QueueUserWorkItem(_ => OnTrackEnded(mp));
            using var media = new LibVLCSharp.Shared.Media(lib, _tracks[_ix], FromType.FromPath);
            mp.Play(media);
        }
        catch { StopPlayerLocked(); }
    }

    private static void OnTrackEnded(MediaPlayer mp)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_mp, mp)) return;   // stopped or replaced while the hop was in flight
            if (_tracks.Count == 0) { StopLocked(); return; }
            _ix = _shuffle && _tracks.Count > 1 ? RandomOtherLocked() : (_ix + 1) % _tracks.Count;
            StartCurrentLocked();
        }
    }

    private static int RandomOtherLocked()
    {
        int n;
        do { n = _rng.Next(_tracks.Count); } while (n == _ix);
        return n;
    }

    private static void StopLocked()
    {
        _key = null;
        _tracks = new List<string>();
        StopPlayerLocked();
    }

    private static void StopPlayerLocked()
    {
        var mp = _mp;
        _mp = null;
        if (mp == null) return;
        try { mp.Stop(); } catch { }
        try { mp.Dispose(); } catch { }
    }
}
