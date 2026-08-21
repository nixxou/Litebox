// Who owns the audio right now — the single answer to "may the ambient game music play?".
//
// Three kinds of claim, all funnelled here so no caller has to know about the others:
//   • TAKE     a video started UNMUTED. It owns the audio until it is muted again, ends, or goes away.
//   • HOLD     a surface owns the whole media zone (Edit, Options) or the machine does (a game running,
//              the web kiosk): music AND video stop, and both come back when the hold is released.
//   • RELEASE  the claim is over → the host re-evaluates the ambient music, and after a hold also the
//              media item that was torn down (a video is Clear()ed by VlcService.Stopping, an image is not).
//
// MainWindow registers the two restore delegates at startup; everything else just claims and releases.
//
// Held is a COUNTER plus the two ambient states, not a flag: Edit opened from Options — or a game
// launched from either — must not let the inner surface's close resurrect the audio while the outer one
// is still up. Release is idempotent by design: MusicRestore ends in GameMusicPlayer.Play with the same
// key, which keeps the running track rather than restarting it.

#nullable enable

using System;
using System.Threading;
using System.Windows.Forms;
using LbApiHost.Host.Video;

namespace LbApiHost.Host.Media;

internal static class AmbientAudio
{
    private static int _holds;

    /// <summary>Set by MainWindow: re-evaluate the ambient music for the current selection.</summary>
    public static Action? MusicRestore;

    /// <summary>Set by MainWindow: re-show the current media item when it is a video (a hold tore it down).</summary>
    public static Action? MediaRestore;

    /// <summary>Someone else owns the audio — a modal hold, a running game, or the web kiosk. Fail-soft
    /// on the two foreign states: a broken probe must never be able to wedge the music off.</summary>
    public static bool Held
    {
        get
        {
            if (Volatile.Read(ref _holds) > 0) return true;
            try { if (HostLaunch.GameRunning) return true; } catch { }
            try { if (Web.Kiosk.WebKioskWindow.IsOpen) return true; } catch { }
            return false;
        }
    }

    /// <summary>A video with sound took the audio: the music goes quiet.</summary>
    public static void Take()
    {
        try { GameMusicPlayer.Stop(); } catch { }
    }

    /// <summary>A claim ended (a video muted, finished or cleared): give the music back, unless something
    /// else is still holding the audio.</summary>
    public static void Release()
    {
        if (Held) return;
        try { MusicRestore?.Invoke(); } catch { }
    }

    /// <summary>Silence music AND video for as long as <paramref name="owner"/> is open — the Edit and
    /// Options windows, which take the user away from the media zone entirely. Both come back when it
    /// closes (unless a game, the kiosk or an outer hold is still up).
    ///
    /// StopPlayback rather than Shutdown: the shared libvlc stays alive (the Edit window's own document
    /// and music previews need it), only what is PLAYING stops.</summary>
    public static void HoldFor(Form owner)
    {
        if (owner == null) return;
        Interlocked.Increment(ref _holds);
        try { VlcService.StopPlayback(); } catch { }

        // BOTH events, exactly once: a window that is shown ends on FormClosed, one that is built and
        // thrown away without ever being shown (the image-matrix diagnostic driver) only ever gets
        // Disposed — and a hold that is never released would wedge the music off for the session.
        int ended = 0;
        void End(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            if (Interlocked.Decrement(ref _holds) > 0 || Held) return;
            try { MediaRestore?.Invoke(); } catch { }
            try { MusicRestore?.Invoke(); } catch { }
        }
        owner.FormClosed += End;
        owner.Disposed += End;
    }
}
