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
    private readonly Panel _surface;          // libvlc's render target
    private readonly PictureBox _still;       // frame shown before playback (and when paused at start)
    private readonly Panel _bar;              // hover controls
    private readonly Button _playBtn;
    private readonly TrackBar _seek;
    private readonly Label _time;
    private readonly Button _muteBtn;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };

    private MediaPlayer? _mp;
    private string? _path;
    private int _token;
    private bool _seeking, _hasContent, _muted = true;
    private long _durMs;

    /// <summary>The block has a video to show — drives its visibility in the media box.</summary>
    public bool HasContent => _hasContent;

    /// <summary>Raised on the UI thread when <see cref="HasContent"/> flips.</summary>
    public Action? ContentChanged;

    /// <summary>Start playing as soon as a video is shown (Options → Display → Right panel).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Autoplay { get; set; }

    public VideoBlock()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;

        _surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        Controls.Add(_surface);

        // The still frame sits ABOVE the surface until playback actually starts, so the zone never flashes
        // an empty black hole between selection and first frame.
        _still = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        _still.Paint += (_, e) => { if (!IsPlaying) DrawPlayGlyph(e.Graphics, _still.ClientSize); };
        _still.Click += (_, _) => TogglePlay();
        Controls.Add(_still);
        _still.BringToFront();

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
            try { if (_mp != null && _durMs > 0) _mp.Time = (long)(_durMs * (_seek.Value / 1000.0)); } catch { }
        };
        _time = new Label { AutoSize = false, Width = 96, Height = 26, Top = 6, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(210, 214, 222) };
        _muteBtn = new Button
        {
            Text = "🔇", Width = 34, Height = 26, Top = 4, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 58), ForeColor = Color.White, TabStop = false,
        };
        _muteBtn.FlatAppearance.BorderSize = 0;
        _muteBtn.Click += (_, _) => SetMuted(!_muted);
        _bar.Controls.Add(_playBtn); _bar.Controls.Add(_seek); _bar.Controls.Add(_time); _bar.Controls.Add(_muteBtn);
        _bar.Resize += (_, _) => LayoutBar();
        Controls.Add(_bar);
        _bar.BringToFront();

        _tick.Tick += (_, _) => UpdateProgress();

        // Hover = controls. The children must forward it, or moving onto the bar/still would "leave".
        foreach (Control c in new Control[] { this, _surface, _still, _bar })
        {
            c.MouseEnter += (_, _) => ShowBar(true);
            c.MouseLeave += (_, _) => ShowBar(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        }
    }

    private bool IsPlaying { get { try { return _mp?.IsPlaying == true; } catch { return false; } } }

    private void LayoutBar()
    {
        int right = _bar.ClientSize.Width;
        _muteBtn.Left = right - _muteBtn.Width - 6;
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
        SetStill(still);
        _still.Visible = true;
        if (!_hasContent) { _hasContent = true; ContentChanged?.Invoke(); }
        if (Autoplay) Play();
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

    private void TogglePlay()
    {
        if (_mp == null) { Play(); return; }
        try
        {
            if (_mp.IsPlaying) { _mp.SetPause(true); _playBtn.Text = "▶"; _still.Visible = false; }
            else { _mp.Play(); _playBtn.Text = "❚❚"; }
        }
        catch { }
    }

    private void Play()
    {
        if (_path == null || IsDisposed) return;
        var lib = VlcService.Instance;
        if (lib == null) return;
        int token = _token;
        try
        {
            StopPlayer();
            _mp = new MediaPlayer(lib) { EnableKeyInput = false, EnableMouseInput = false, Hwnd = _surface.Handle };
            _mp.Mute = _muted;
            // Fires on a VLC thread → hop to the UI thread before touching anything of ours.
            _mp.Playing += (_, _) => Post(() => { if (_token == token) { _still.Visible = false; _playBtn.Text = "❚❚"; } });
            _mp.EndReached += (_, _) => Post(() => { if (_token == token) { _still.Visible = true; _playBtn.Text = "▶"; _seek.Value = 0; } });
            _mp.EncounteredError += (_, _) => Post(() => { if (_token == token) _still.Visible = true; });
            using var media = new VlcMedia(lib, _path, FromType.FromPath);
            _mp.Play(media);
            _tick.Start();
        }
        catch (Exception ex) { Console.WriteLine("[video] play failed (" + _path + "): " + ex.Message); }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { StopPlayer(); } catch { }
            try { _tick.Dispose(); } catch { }
            try { SetStill(null); } catch { }
        }
        base.Dispose(disposing);
    }
}
