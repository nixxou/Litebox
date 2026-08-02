// The detail pane's VIDEO surface — the main media zone when the selected item is a video sentinel.
//
// Same overlay shape as the 3D block: a panel that covers the main image box, driven by the media list.
// libvlc renders straight into our HWND (no LibVLCSharp.WinForms — VideoPlayerDialog proved one Panel +
// `mediaPlayer.Hwnd = Handle` is all that control does).
//
// Behaviour (decided with the user):
//   • the video plays in the main zone when it is the selected item — automatically only when the
//     Autoplay option is on, otherwise it waits behind a big ▶ over its still frame;
//   • CONTROLS ONLY ON HOVER: a slim bar (play/pause, seek, time, mute) fades in when the mouse is over
//     the zone and disappears when it leaves — a still frame with a ▶ the rest of the time;
//   • teardown is the delicate part (the lesson from VideoPlayerDialog): libvlc renders from ITS own
//     threads, so we Stop() and WAIT for Stopped before disposing, or the surface dies under it.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace LbApiHost.Host.Video;

internal sealed class VideoBlock : Panel
{
    private sealed class VideoSurface : Panel
    {
        public VideoSurface()
            => SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
    }

    private readonly Panel _surface;          // libvlc's render target
    private readonly PictureBox _still;       // frame shown before playback (and when paused at start)
    private readonly Panel _bar;              // hover controls
    private readonly Button _playBtn;
    private readonly TrackBar _seek;
    private readonly Label _time;
    private readonly Button _muteBtn;
    private readonly Button _fsBtn;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };

    private MediaPlayer? _mp;
    private string? _path;
    private int _token;
    private bool _seeking, _hasContent, _muted = true, _playRequested, _ended;
    private bool _pauseWhenReady, _markEndedWhenReady, _fullscreenMode;
    private long _durMs, _pauseAtMs;

    /// <summary>The block has a video to show — drives its visibility in the media box.</summary>
    public bool HasContent => _hasContent;

    /// <summary>Raised on the UI thread when <see cref="HasContent"/> flips.</summary>
    public Action? ContentChanged;

    /// <summary>The ⤢ button in the hover bar was clicked — the host opens the fullscreen player.</summary>
    public Action? FullscreenRequested;

    /// <summary>Mouse movement anywhere over the video surface or its controls. The fullscreen host uses
    /// this to reveal all overlays together, then hide them again after a short idle period.</summary>
    public Action? MouseActivity;

    /// <summary>Inline blocks reveal their bar while hovered. Fullscreen turns this off and drives the
    /// controls explicitly so an idle pointer does not leave the overlays on screen forever.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ControlsOnHover { get; set; } = true;

    /// <summary>The block IS the fullscreen player: the bar's chip becomes ⤡ ("leave fullscreen") instead of
    /// ⤢, so it — and the double-click on the video, which goes through the same
    /// <see cref="FullscreenRequested"/> — mean "go back" rather than pointing at a fullscreen we are
    /// already in. The host wires FullscreenRequested to its own Close.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool FullscreenMode
    {
        get => _fullscreenMode;
        set
        {
            if (_fullscreenMode == value) return;
            _fullscreenMode = value;
            _fsBtn.Text = value ? "⤡" : "⤢";
            _fsBtn.AccessibleName = value ? "Leave fullscreen" : "Fullscreen";
        }
    }

    /// <summary>Start playing as soon as a video is shown (Options → Display → Right panel).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Autoplay { get; set; }

    /// <summary>Autoplay WITH SOUND. Off (default): an autoplayed video starts muted — a game list that
    /// starts talking as you scroll is unbearable. A video the user STARTS by clicking always has sound,
    /// whatever this says: an explicit play is an explicit request to hear it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoplaySound { get; set; }

    public VideoBlock()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);

        _surface = new VideoSurface { Dock = DockStyle.Fill, BackColor = Color.Black };
        Controls.Add(_surface);

        // The still frame sits ABOVE the surface until playback actually starts, so the zone never flashes
        // an empty black hole between selection and first frame.
        _still = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        _still.Paint += (_, e) => { if (!IsPlaying) DrawPlayGlyph(e.Graphics, _still.ClientSize); };
        _still.Click += (_, _) => TogglePlay();
        Controls.Add(_still);
        _still.BringToFront();

        // The video itself is the largest and most natural fullscreen target. The still layer handles the
        // not-yet-started case; the VLC surface handles an already-playing video.
        foreach (Control c in new Control[] { this, _surface, _still })
            c.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) FullscreenRequested?.Invoke();
            };

        _bar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(210, 18, 18, 22), Visible = false };
        _playBtn = new Button
        {
            Text = "▶", Width = 34, Height = 26, Top = 4, Left = 6, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 58), ForeColor = Color.White, TabStop = false,
        };
        _playBtn.FlatAppearance.BorderSize = 0;
        _playBtn.Click += (_, _) => TogglePlay();
        _seek = new TrackBar { Top = 2, Height = 28, TickStyle = TickStyle.None, Minimum = 0, Maximum = 1000 };
        _seek.MouseDown += (_, _) => _seeking = true;
        _seek.MouseUp += (_, _) =>
        {
            _seeking = false;
            if (_durMs > 0) SeekTo((long)(_durMs * (_seek.Value / 1000.0)));
        };
        _time = new Label { AutoSize = false, Width = 96, Height = 26, Top = 6, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(210, 214, 222) };
        _muteBtn = new Button
        {
            Text = "🔇", Width = 34, Height = 26, Top = 4, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 58), ForeColor = Color.White, TabStop = false,
        };
        _muteBtn.FlatAppearance.BorderSize = 0;
        _muteBtn.Click += (_, _) => SetMuted(!_muted);
        _fsBtn = new Button
        {
            Text = "⤢", Width = 34, Height = 26, Top = 4, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 58), ForeColor = Color.White, TabStop = false,
            Font = new Font("Segoe UI Symbol", 10f),
        };
        _fsBtn.FlatAppearance.BorderSize = 0;
        _fsBtn.Click += (_, _) => FullscreenRequested?.Invoke();
        _bar.Controls.Add(_playBtn); _bar.Controls.Add(_seek); _bar.Controls.Add(_time); _bar.Controls.Add(_muteBtn); _bar.Controls.Add(_fsBtn);
        _bar.Resize += (_, _) => LayoutBar();
        Controls.Add(_bar);
        _bar.BringToFront();

        _tick.Tick += (_, _) => UpdateProgress();

        // A game is launching: libvlc is about to be disposed to hand its memory to the game, so release
        // the player FIRST — disposing the instance under a live MediaPlayer crashes in libvlc's threads.
        VlcService.Stopping += OnVlcStopping;

        // Hover = controls. The children must forward it, or moving onto the bar/still would "leave".
        foreach (Control c in new Control[] { this, _surface, _still, _bar })
        {
            c.MouseEnter += (_, _) => { if (ControlsOnHover) ShowBar(true); };
            c.MouseLeave += (_, _) =>
            {
                if (ControlsOnHover)
                    ShowBar(ClientRectangle.Contains(PointToClient(Cursor.Position)));
            };
        }

        WireMouseActivity(this);
    }

    private bool IsPlaying { get { try { return _mp?.IsPlaying == true; } catch { return false; } } }

    private void LayoutBar()
    {
        int right = _bar.ClientSize.Width;
        _fsBtn.Left = right - _fsBtn.Width - 6;
        _muteBtn.Left = _fsBtn.Left - _muteBtn.Width - 4;
        _time.Left = _muteBtn.Left - _time.Width - 4;
        _seek.Left = _playBtn.Right + 6;
        _seek.Width = Math.Max(40, _time.Left - _seek.Left - 6);
    }

    private void ShowBar(bool on)
    {
        if (_bar.Visible == on) return;
        _bar.Visible = on;
        if (on) { _bar.BringToFront(); LayoutBar(); }
    }

    /// <summary>Show or hide the control bar from the fullscreen inactivity controller.</summary>
    public void SetControlsVisible(bool visible) => ShowBar(visible);

    private void WireMouseActivity(Control root)
    {
        root.MouseMove += (_, _) => MouseActivity?.Invoke();
        foreach (Control child in root.Controls) WireMouseActivity(child);
    }

    // The big ▶ over the still frame: drawn, never baked into the cached thumbnail.
    private static void DrawPlayGlyph(Graphics g, Size size)
    {
        int r = Math.Max(18, Math.Min(size.Width, size.Height) / 8);
        int cx = size.Width / 2, cy = size.Height / 2;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(Color.FromArgb(150, 12, 12, 16));
        g.FillEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
        using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1.5f, r / 12f));
        g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        using var tri = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        int t = (int)(r * 0.55);
        g.FillPolygon(tri, new[] { new Point(cx - t / 2, cy - t), new Point(cx - t / 2, cy + t), new Point(cx + t, cy) });
    }

    /// <summary>Leave the video behind (another media item, another game, a node).</summary>
    public void Clear()
    {
        _token++;
        _tick.Stop();
        _playRequested = false;
        _ended = _pauseWhenReady = _markEndedWhenReady = false;
        _durMs = 0;
        StopPlayer();
        _path = null;
        SetStill(null);
        _still.Visible = true;
        ShowBar(false);
        if (_hasContent) { _hasContent = false; ContentChanged?.Invoke(); }
    }

    /// <summary>Show <paramref name="path"/> in the main zone: its still frame (when already extracted)
    /// plus the ▶, and playback right away when Autoplay is on.</summary>
    public void ShowFor(string path, Image? still)
    {
        try { if (!IsHandleCreated && !IsDisposed) _ = Handle; } catch { }   // BeginInvoke needs an HWND
        _token++;
        _path = path;
        _playRequested = false;
        _ended = _pauseWhenReady = _markEndedWhenReady = false;
        _durMs = 0;
        SetStill(still);
        _still.Visible = true;
        if (!_hasContent) { _hasContent = true; ContentChanged?.Invoke(); }
        if (Autoplay) { SetMuted(!AutoplaySound); Play(); }
    }

    /// <summary>Late-arriving still frame (the deferred extraction landed) — only applied if it is still
    /// the current video and playback hasn't already covered the zone.</summary>
    public void SetStillFor(string path, Image? still)
    {
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) { still?.Dispose(); return; }
        if (IsPlaying) { still?.Dispose(); return; }
        SetStill(still);
    }

    private void SetStill(Image? img)
    {
        var old = _still.Image;
        _still.Image = img;
        if (!ReferenceEquals(old, img)) old?.Dispose();
        _still.Invalidate();
    }

    // Every USER-initiated start unmutes: clicking a video means wanting to hear it (the muted default
    // only exists to keep autoplay quiet while scrolling).
    /// <summary>Play/pause from outside (the fullscreen window's Space key).</summary>
    public void TogglePlayPause() => TogglePlay();

    /// <summary>Pause if playing — used when handing the video over to the fullscreen player, so the two
    /// decoders never run (and never talk) at the same time.</summary>
    public void PauseIfPlaying()
    {
        try
        {
            if (_mp?.IsPlaying == true) _mp.SetPause(true);
            _playRequested = false;
            _playBtn.Text = "▶";
        }
        catch { }
    }

    /// <summary>Current position in ms (so fullscreen can resume exactly where the inline player was).</summary>
    public long PositionMs
    {
        get
        {
            try { return _ended && _durMs > 0 ? Math.Max(0, _durMs - 1) : _mp?.Time ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>Whether playback is meant to continue. Unlike MediaPlayer.IsPlaying this remains true
    /// while VLC is opening/buffering, which makes fullscreen hand-offs reliable even when closed quickly.</summary>
    public bool WantsToPlay => _playRequested;

    /// <summary>The last frame is displayed after natural completion. Play from here restarts at zero.</summary>
    public bool HasEnded => _ended;

    /// <summary>Apply the state returned by a fullscreen player to this block.</summary>
    public void ResumeAt(long ms, bool play, bool ended = false)
    {
        try
        {
            if (ended && !play)
            {
                RestartPausedAt(ms, markEnded: true);
                return;
            }

            if (_mp == null)
            {
                if (play)
                {
                    Play();
                    StartAt(ms);
                }
                else if (ms > 0) RestartPausedAt(ms, markEnded: false);
                return;
            }

            _ended = ended;
            if (ms >= 0) _mp.Time = ms;
            _playRequested = play;
            if (play)
            {
                _ended = _pauseWhenReady = _markEndedWhenReady = false;
                _mp.SetPause(false);
                _still.Visible = false;
                _playBtn.Text = "❚❚";
                _tick.Start();
            }
            else
            {
                if (_mp.IsPlaying) _mp.SetPause(true);
                _playBtn.Text = "▶";
            }
            UpdateProgress();
        }
        catch { }
    }

    /// <summary>Jump by a FRACTION of the whole video (+ forward / − backward), clamped to its bounds.</summary>
    public void SeekBy(double fraction)
    {
        try
        {
            var mp = _mp;
            if (mp == null) return;
            long dur = _durMs > 0 ? _durMs : mp.Length;
            if (dur <= 0) return;
            long from = _ended ? dur : mp.Time;
            long t = (long)Math.Clamp(from + fraction * dur, 0, dur - 1);
            SeekTo(t);
        }
        catch { }
    }

    private void SeekTo(long ms)
    {
        try
        {
            if (_mp == null) return;
            long target = _durMs > 0 ? Math.Clamp(ms, 0, Math.Max(0, _durMs - 1)) : Math.Max(0, ms);
            if (_ended)
            {
                RestartPausedAt(target, markEnded: false);
                return;
            }
            _mp.Time = target;
            UpdateProgress();
        }
        catch { }
    }

    /// <summary>Start at a given position (fullscreen hand-over).</summary>
    public void StartAt(long ms)
    {
        try { if (_mp != null && ms > 0) _mp.Time = ms; } catch { }
    }

    private void TogglePlay()
    {
        // A real user command overrides any pending paused seek.
        _pauseWhenReady = _markEndedWhenReady = false;
        if (_ended)
        {
            _ended = false;
            SetMuted(false);
            Play();   // an ended VLC player is not reliably resumable: rebuild naturally starts at zero
            return;
        }
        if (_mp == null) { SetMuted(false); Play(); return; }
        try
        {
            if (_mp.IsPlaying)
            {
                _playRequested = false;
                _mp.SetPause(true);
                _playBtn.Text = "▶";
                _still.Visible = false;
            }
            else
            {
                _playRequested = true;
                _mp.Play();
                _playBtn.Text = "❚❚";
            }
        }
        catch { }
    }

    private void Play()
    {
        if (_path == null || IsDisposed) return;
        var lib = VlcService.Instance;
        if (lib == null) return;
        _ended = false;
        _playRequested = true;
        int token = _token;
        try
        {
            StopPlayer();
            _mp = new MediaPlayer(lib) { EnableKeyInput = false, EnableMouseInput = false, Hwnd = _surface.Handle };
            _mp.Mute = _muted;
            // Fires on a VLC thread → hop to the UI thread before touching anything of ours.
            _mp.Playing += (_, _) => Post(() =>
            {
                if (_token != token) return;
                _still.Visible = false;
                if (_pauseWhenReady)
                {
                    long at = _pauseAtMs;
                    bool markEnded = _markEndedWhenReady;
                    _pauseWhenReady = _markEndedWhenReady = false;
                    try
                    {
                        if (_mp != null)
                        {
                            _mp.Time = at;
                            _mp.SetPause(true);
                        }
                    }
                    catch { }
                    _ended = markEnded;
                    _playRequested = false;
                    _playBtn.Text = "▶";
                    if (markEnded)
                    {
                        _seek.Value = 1000;
                        SetEndTimeLabel();
                    }
                    else UpdateProgress();
                }
                else _playBtn.Text = "❚❚";
            });
            _mp.EndReached += (_, _) => Post(() =>
            {
                if (_token == token) ShowEndedState();
            });
            _mp.EncounteredError += (_, _) => Post(() =>
            {
                if (_token == token)
                {
                    _playRequested = false;
                    _still.Visible = true;
                }
            });
            using var media = new VlcMedia(lib, _path, FromType.FromPath);
            _mp.Play(media);
            _tick.Start();
        }
        catch (Exception ex) { Console.WriteLine("[video] play failed (" + _path + "): " + ex.Message); }
    }

    private void ShowEndedState()
    {
        _tick.Stop();
        _ended = true;
        _playRequested = false;
        _still.Visible = false;   // keep VLC's last rendered frame, never cover it with the cached thumb
        _playBtn.Text = "▶";
        _seek.Value = 1000;
        SetEndTimeLabel();
    }

    // Seeking an ended VLC player is unreliable. Recreate it, seek after Playing fires, then pause on the
    // requested real frame. markEnded preserves "Play means restart at zero" when handing the last frame
    // back from fullscreen to the inline block.
    private void RestartPausedAt(long ms, bool markEnded)
    {
        if (_path == null || IsDisposed) return;
        _ended = false;
        _pauseAtMs = Math.Max(0, ms);
        _pauseWhenReady = true;
        _markEndedWhenReady = markEnded;
        Play();
        _playRequested = false;
    }

    private void SetEndTimeLabel()
    {
        if (_durMs <= 0) { _time.Text = "End"; return; }
        string d = TimeSpan.FromMilliseconds(_durMs).ToString(@"m\:ss");
        _time.Text = $"{d} / {d}";
    }

    private void SetMuted(bool on)
    {
        _muted = on;
        _muteBtn.Text = on ? "🔇" : "🔊";
        try { if (_mp != null) _mp.Mute = on; } catch { }
    }

    private void UpdateProgress()
    {
        var mp = _mp;
        if (mp == null) return;
        try
        {
            _durMs = mp.Length;
            long t = mp.Time;
            if (!_seeking && _durMs > 0) _seek.Value = (int)Math.Clamp(t * 1000.0 / _durMs, 0, 1000);
            _time.Text = _durMs > 0 ? $"{Fmt(t)} / {Fmt(_durMs)}" : Fmt(t);
        }
        catch { }
        static string Fmt(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss");
    }

    private void Post(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
    }

    // libvlc renders into our HWND from its own threads: Stop is ASYNCHRONOUS in VLC 3, so wait for the
    // Stopped event before disposing — the exact teardown VideoPlayerDialog had to learn.
    private void StopPlayer()
    {
        var mp = _mp;
        _mp = null;
        if (mp == null) return;
        _tick.Stop();
        try
        {
            using var stopped = new ManualResetEventSlim(false);
            mp.Stopped += (_, _) => stopped.Set();
            mp.Stop();
            stopped.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        try { mp.Dispose(); } catch { }
    }

    // Called from VlcService.Shutdown on ITS thread (usually the UI thread that started the launch, but
    // not guaranteed): the VLC teardown itself is thread-safe, the WinForms timer is not — hence the guard.
    private void OnVlcStopping()
    {
        try
        {
            if (IsHandleCreated && InvokeRequired) { try { Invoke((Action)Clear); return; } catch { } }
            Clear();
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { VlcService.Stopping -= OnVlcStopping; } catch { }
            try { StopPlayer(); } catch { }
            try { _tick.Dispose(); } catch { }
            try { SetStill(null); } catch { }
        }
        base.Dispose(disposing);
    }
}
