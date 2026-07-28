// Fullscreen video player — the ⤢ button of the detail pane's hover bar.
//
// It REUSES VideoBlock rather than re-implementing a player: the surface, the hover controls, the play
// glyph, the mute rule and (most importantly) the careful libvlc teardown all come for free, and there
// is exactly one place where playback logic lives.
//
// Behaviour (decided with the user):
//   • Space toggles play/pause, ←/→ jump by SeekStep of the whole video, Esc leaves — the same Esc as the
//     image and 3D viewers;
//   • NO navigation between videos: this is one video, fullscreen. (The image viewer pages through the
//     game's images; a video is a destination, not a slide.)
//   • the inline player is PAUSED while this is up, so two decoders never run — and never talk — at once,
//     and playback resumes here exactly where the pane had got to.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;

namespace LbApiHost.Host.Video;

internal sealed class VideoFullscreen : Form
{
    /// <summary>How far ←/→ jump, as a fraction of the whole video. 5 % suits both a 90-second trailer
    /// (~4 s a press) and a long recording, and matches the "x %" the user asked for.</summary>
    private const double SeekStep = 0.05;

    private readonly VideoBlock _block;

    public VideoFullscreen(string path, Image? still, bool sound, long startMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        KeyPreview = true;

        // Autoplay ON: reaching fullscreen is an explicit request to watch. Sound follows the same rule as
        // a click in the pane — the user asked for it, so it plays audible unless they muted the option.
        _block = new VideoBlock { Dock = DockStyle.Fill, Autoplay = true, AutoplaySound = sound };
        Controls.Add(_block);

        // Exit chip, same glyph as the 3D viewer's (⤡) — one visual language for "leave fullscreen".
        var close = new Model3d.Model3dExpandBadge { Shrink = true, BackColor = Color.Black };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();

        void Place() => close.Location = new Point(Math.Max(0, ClientSize.Width - close.Width - 14),
                                                   Math.Max(0, ClientSize.Height - close.Height - 14));
        Resize += (_, _) => Place();

        KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Escape: Close(); break;
                case Keys.Space: _block.TogglePlayPause(); break;
                case Keys.Left: _block.SeekBy(-SeekStep); break;
                case Keys.Right: _block.SeekBy(+SeekStep); break;
                default: return;
            }
            e.Handled = e.SuppressKeyPress = true;   // Space must not "click" a focused child
        };

        Shown += (_, _) =>
        {
            Place();
            _block.ShowFor(path, still);
            if (startMs > 0) _block.StartAt(startMs);   // continue where the pane was
            Focus();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _block.Clear(); } catch { } }   // stop + release the player before the form dies
        base.Dispose(disposing);
    }
}
