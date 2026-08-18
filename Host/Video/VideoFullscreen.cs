// Fullscreen video player — the ⤢ button of the detail pane's hover bar.
//
// It REUSES VideoBlock rather than re-implementing a player: the surface, the hover controls, the play
// glyph, the mute rule and (most importantly) the careful libvlc teardown all come for free, and there
// is exactly one place where playback logic lives.
//
// Behaviour (decided with the user):
//   • Space toggles play/pause, ←/→ jump by SeekStep of the whole video, Esc leaves — the same Esc as the
//     image and 3D viewers; so do the top-right X, the bar's ⤡ chip and a double-click on the video;
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
    private const int MouseMoveTolerancePx = 3;

    private readonly VideoBlock _block;
    private readonly System.Windows.Forms.Timer _overlayIdle = new() { Interval = 1800 };
    private readonly Media.FullscreenCloseButton _close;
    private bool _exitCaptured;
    private bool _mousePositionSeeded;
    private Point _lastAcceptedMousePosition;

    public long ExitPositionMs { get; private set; }
    public bool ContinuePlaying { get; private set; } = true;
    public bool ExitEnded { get; private set; }

    public VideoFullscreen(string path, Image? still, bool sound, long startMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        UiKit.FullscreenPlacement.OnAppScreen(this);   // the monitor LiteBox/LaunchBox are on
        BackColor = Color.Black;
        ShowInTaskbar = false;
        KeyPreview = true;

        // Autoplay ON: reaching fullscreen is an explicit request to watch. Sound follows the same rule as
        // a click in the pane — the user asked for it, so it plays audible unless they muted the option.
        _block = new VideoBlock
        {
            Dock = DockStyle.Fill,
            Autoplay = true,
            AutoplaySound = sound,
            ControlsOnHover = false,
            FullscreenMode = true,
        };
        // The bar's chip now reads ⤡ ("leave fullscreen"), and a double-click on the video raises the same
        // event — both land here rather than on a dead handler pointing at the fullscreen we are already in.
        _block.FullscreenRequested = Close;
        Controls.Add(_block);

        // Same top-right close button as the image and 3D fullscreen viewers.
        _close = new Media.FullscreenCloseButton { Visible = false };
        _close.Click += (_, _) => Close();
        _close.MouseMove += (_, _) => RevealOverlays();
        Controls.Add(_close);
        _close.BringToFront();

        void Place() => _close.Location = new Point(
            Math.Max(0, ClientSize.Width - _close.Width - 14), 14);
        Resize += (_, _) => Place();
        _block.MouseActivity = RevealOverlays;
        MouseMove += (_, _) => RevealOverlays();
        _overlayIdle.Tick += (_, _) => HideOverlays();

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
            _lastAcceptedMousePosition = Cursor.Position;
            _mousePositionSeeded = true;
            HideOverlays();
            Focus();
        };
    }

    private void RevealOverlays()
    {
        if (IsDisposed) return;

        // Showing/hiding the docked control bar changes the video surface's bounds. WinForms can emit a
        // synthetic MouseMove for the control newly underneath an otherwise stationary pointer; accepting
        // it would immediately reveal the overlays again and produce a faint hide/show flicker. Compare
        // screen coordinates and require a small cumulative physical displacement instead.
        Point now = Cursor.Position;
        if (_mousePositionSeeded)
        {
            int dx = now.X - _lastAcceptedMousePosition.X;
            int dy = now.Y - _lastAcceptedMousePosition.Y;
            if (dx * dx + dy * dy < MouseMoveTolerancePx * MouseMoveTolerancePx) return;
        }
        _lastAcceptedMousePosition = now;
        _mousePositionSeeded = true;

        _block.SetControlsVisible(true);
        _close.Visible = true;
        _close.BringToFront();
        _overlayIdle.Stop();
        _overlayIdle.Start();
    }

    private void HideOverlays()
    {
        if (MouseButtons != MouseButtons.None)
        {
            _overlayIdle.Stop();
            _overlayIdle.Start();
            return;
        }
        _overlayIdle.Stop();
        _block.SetControlsVisible(false);
        _close.Visible = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitCaptured)
        {
            _exitCaptured = true;
            ExitPositionMs = _block.PositionMs;
            ContinuePlaying = _block.WantsToPlay;
            ExitEnded = _block.HasEnded;
            _block.Clear();   // stop the fullscreen decoder before the inline one resumes
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _overlayIdle.Dispose(); } catch { }
            try { _block.Clear(); } catch { }
        }
        base.Dispose(disposing);
    }
}
