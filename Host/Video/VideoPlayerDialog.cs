// Plays a video INSIDE LiteBox (click a thumbnail on the Videos page) instead of shelling out to whatever the
// system associates with .mp4 — a foreign player stealing focus over a modal edit window is exactly the kind
// of thing we spent a week debugging.
//
// No LibVLCSharp.WinForms dependency: that package's VideoView is a Control that does one thing —
// `mediaPlayer.Hwnd = Handle`. We do the same on a plain Panel, and ship one DLL less.
//
// Teardown is the delicate part. libvlc renders into our HWND from ITS own threads, so the window must not die
// under it: on close we Stop() and WAIT for the Stopped event (Stop is asynchronous in VLC 3 — same lesson as
// VideoThumbnailer) before disposing the player and letting WinForms destroy the surface.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace LbApiHost.Host.Video;

internal sealed class VideoPlayerDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(30, 30, 36);
    private static readonly Color Fg = Color.FromArgb(222, 226, 235);
    private static readonly Color SubFg = Color.FromArgb(150, 156, 172);

    private static readonly Color SelColor = Color.FromArgb(150, 90, 200);

    /// <summary>One thing to try: a local path (no referer) or an upstream URL from ExtendDB's per-origin chain.</summary>
    internal readonly record struct Source(string Location, string? Referer);

    // ── Keyframe timeline ─────────────────────────────────────────────────────
    // Every tick is a place a stream copy is ALLOWED to start. The In/Out handles snap onto them, so what you
    // see is exactly what you can get: no re-encode means no cut between two ticks.
    private sealed class TrimStrip : Panel
    {
        public IReadOnlyList<double> Keys = Array.Empty<double>();
        public double Duration, In, Out, Playhead;
        public event Action<double>? Scrub;   // fraction 0..1

        public TrimStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(22, 22, 28);
            Cursor = Cursors.Hand;
        }

        private int X(double t) => Duration <= 0 ? 0 : (int)(t / Duration * (Width - 1));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Duration <= 0) return;

            using (var sel = new SolidBrush(Color.FromArgb(70, SelColor)))
                g.FillRectangle(sel, X(In), 0, Math.Max(1, X(Out) - X(In)), Height);

            using (var tick = new Pen(Color.FromArgb(96, 102, 118)))
                foreach (var k in Keys) { int x = X(k); g.DrawLine(tick, x, Height - 8, x, Height - 1); }

            using (var handle = new Pen(SelColor, 3f))
            {
                g.DrawLine(handle, X(In), 0, X(In), Height);
                g.DrawLine(handle, X(Out), 0, X(Out), Height);
            }
            using (var head = new Pen(Color.White))
                g.DrawLine(head, X(Playhead), 0, X(Playhead), Height);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Scrub?.Invoke(Frac(e.X)); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Scrub?.Invoke(Frac(e.X));
            base.OnMouseMove(e);
        }
        private double Frac(int x) => Math.Min(1.0, Math.Max(0.0, (double)x / Math.Max(1, Width - 1)));
    }

    private readonly IReadOnlyList<Source> _sources;
    private int _at;                     // index of the source currently playing
    private MediaPlayer? _mp;
    private readonly Panel _surface;
    private readonly TrackBar _seek;
    private readonly Button _playBtn;
    private readonly Label _time;
    private readonly System.Windows.Forms.Timer _tick;
    private bool _dragging;

    // ── Trim state ────────────────────────────────────────────────────────────
    private readonly string? _localPath;      // null for a web stream: nothing to trim
    private readonly Button _trimBtn;
    private readonly Panel _trimPanel;
    private TrimStrip _strip = null!;
    private Label _trimInfo = null!;
    private IReadOnlyList<double> _keys = Array.Empty<double>();
    private double _dur, _in, _out;
    private bool _previewing;

    /// <summary>Set when the trimmer rewrote the file — the caller must refresh its thumbnail / page.</summary>
    public bool FileChanged { get; private set; }

    /// <summary>Play a file we own. Returns true when it was TRIMMED (so the caller can refresh).</summary>
    public static bool Play(IWin32Window owner, string path)
    {
        if (!File.Exists(path)) { MessageBox.Show(owner, "The file is gone:\n" + path, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        return Show(owner, Path.GetFileName(path), new[] { new Source(path, null) }, VideoProbe.Get(path));
    }

    /// <summary>
    /// Stream a video we DON'T own, straight from the upstream — the candidates come from ExtendDB's per-origin
    /// chain (MediaApiBridge.ListUrls), tried in order, so a dead CDN falls through to the mirror. This is also
    /// how a Steam trailer stored as a fake ".m3u8.mp4" plays: the chain hands us the real HLS manifest, which
    /// libvlc reads natively (LaunchBox's own downloader can only skip it).
    /// </summary>
    public static void PlayWeb(IWin32Window owner, string title, IReadOnlyList<Source> sources)
    {
        if (sources == null || sources.Count == 0)
        {
            MessageBox.Show(owner, "No upstream URL for this video.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Show(owner, title, sources, null);   // no probe: that would cost a network round-trip before the window even opens
    }

    private static bool Show(IWin32Window owner, string title, IReadOnlyList<Source> sources, VideoProbe.Info? probe)
    {
        if (VlcService.Instance == null)
        {
            MessageBox.Show(owner, "libvlc isn't available, so LiteBox can't play this video.", "LiteBox",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        using var dlg = new VideoPlayerDialog(title, sources, probe);
        dlg.ShowDialog(owner);
        return dlg.FileChanged;
    }

    private VideoPlayerDialog(string title, IReadOnlyList<Source> sources, VideoProbe.Info? probe)
    {
        _sources = sources;

        // Open at the video's own size, capped to 70% of the screen (and never microscopic).
        int vw = probe?.Width ?? 0, vh = probe?.Height ?? 0;
        var scr = (Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800));
        int maxW = (int)(scr.Width * 0.7), maxH = (int)(scr.Height * 0.7);
        double sc = (vw > 0 && vh > 0) ? Math.Min(1.0, Math.Min((double)maxW / vw, (double)maxH / vh)) : 1.0;
        int w = vw > 0 ? Math.Max(480, (int)(vw * sc)) : 960;
        int h = vh > 0 ? Math.Max(270, (int)(vh * sc)) : 540;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(w, h + 44);
        MinimumSize = new Size(420, 260);
        BackColor = Bg;
        ShowInTaskbar = false;
        KeyPreview = true;

        _surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _surface.DoubleClick += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Bg };

        _playBtn = new Button
        {
            Text = "❚❚", Width = 44, Height = 28, Left = 6, Top = 8, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 58), ForeColor = Fg, TabStop = false,
        };
        _playBtn.FlatAppearance.BorderSize = 0;
        _playBtn.Click += (_, _) => TogglePlay();

        _seek = new TrackBar
        {
            Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Left = 56, Top = 6, Height = 32,
            BackColor = Bg,
        };
        _seek.MouseDown += (_, _) => _dragging = true;
        _seek.MouseUp += (_, _) => { _dragging = false; if (_mp != null) _mp.Position = _seek.Value / 1000f; };

        _time = new Label
        {
            Text = "0:00 / " + VideoProbe.Duration(probe?.DurationMs ?? 0), ForeColor = SubFg, BackColor = Bg,
            AutoSize = false, Width = 110, Height = 28, Top = 9, TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", 8.5f),
        };

        // The trimmer only exists for a file we OWN (a stream has nothing to rewrite), in a container a stream
        // copy is safe in (mp4/mkv — see VideoTrimmer.CanTrim), and only when LaunchBox's ffmpeg is on disk.
        // Its button lives at the right of the bar, next to the clock.
        _localPath = (sources.Count == 1 && !sources[0].Location.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ? sources[0].Location : null;
        _trimBtn = new Button
        {
            Text = "✂", Width = 32, Height = 28, Top = 8, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 58), ForeColor = Fg, TabStop = false,
            Visible = _localPath != null && VideoTrimmer.CanTrim(_localPath) && FfmpegService.Available,
        };
        _trimBtn.FlatAppearance.BorderSize = 0;
        _trimBtn.Click += (_, _) => ToggleTrim();
        new ToolTip().SetToolTip(_trimBtn, "Trim this video (no re-encoding — cuts land on keyframes)");

        bar.Controls.Add(_playBtn);
        bar.Controls.Add(_seek);
        bar.Controls.Add(_time);
        bar.Controls.Add(_trimBtn);
        bar.Resize += (_, _) =>
        {
            _trimBtn.Left = bar.ClientSize.Width - _trimBtn.Width - 8;
            _time.Left = _trimBtn.Left - _time.Width - 8;
            _seek.Width = Math.Max(60, _time.Left - _seek.Left - 8);
        };

        _trimPanel = BuildTrimPanel();

        Controls.Add(_surface);     // Fill first …
        Controls.Add(_trimPanel);   // … then the (hidden) trim strip …
        Controls.Add(bar);          // … Bottom-most last

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Space) { TogglePlay(); e.Handled = true; }
        };

        _tick = new System.Windows.Forms.Timer { Interval = 250 };   // poll: VLC's own events fire on VLC threads
        _tick.Tick += (_, _) => Refresh_();
        Shown += (_, _) => { bar.PerformLayout(); OnShownStart(); };
    }

    private void OnShownStart() => StartSource(0);

    /// <summary>Play candidate <paramref name="index"/>. On an upstream error we walk to the next one (the chain
    /// is ordered by ExtendDB's policy: CDN first, its mirror after), and only give up once they're exhausted.</summary>
    private void StartSource(int index)
    {
        var lib = VlcService.Instance;
        if (lib == null) { Close(); return; }
        if (index >= _sources.Count)
        {
            MessageBox.Show(this, "None of the sources for this video could be opened.", "LiteBox",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        _at = index;
        var src = _sources[index];
        bool remote = src.Location.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        try
        {
            _mp = new MediaPlayer(lib) { EnableKeyInput = false, EnableMouseInput = false, Hwnd = _surface.Handle };
            // Fires on a VLC thread → hop to the UI thread before touching libvlc again.
            _mp.EncounteredError += (_, _) => { try { BeginInvoke(new Action(() => NextSource(index))); } catch { } };

            using var media = new VlcMedia(lib, src.Location, remote ? FromType.FromLocation : FromType.FromPath);
            if (remote) VideoThumbnailer.ApplyNet(media, src.Referer);   // Referer + UA + buffering, per the chain
            _mp.Play(media);
            _tick.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[vlc] play failed ({src.Location}): {ex.Message}");
            NextSource(index);
        }
    }

    private void NextSource(int failed)
    {
        if (_at != failed || IsDisposed) return;   // already moved on
        _tick.Stop();
        DisposePlayer();
        StartSource(failed + 1);
    }

    // ── Trimmer ───────────────────────────────────────────────────────────────
    private Panel BuildTrimPanel()
    {
        var p = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = Bg, Visible = false };

        _strip = new TrimStrip { Left = 6, Top = 6, Height = 26 };
        _strip.Scrub += f =>
        {
            _previewing = false;
            if (_mp != null && _dur > 0) _mp.Time = (long)(f * _dur * 1000);
        };
        p.Controls.Add(_strip);

        Button B(string text, int left, int width, Action onClick)
        {
            var b = new Button
            {
                Text = text, Left = left, Top = 36, Width = width, Height = 24, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(48, 48, 58), ForeColor = Fg, TabStop = false,
                Font = new Font("Segoe UI", 8f),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => onClick();
            p.Controls.Add(b);
            return b;
        }

        B("⟦ Set In", 6, 66, () => SetMark(true));
        B("Set Out ⟧", 76, 70, () => SetMark(false));
        B("▶ Preview", 150, 70, PreviewRange);
        var save = B("💾 Save", 224, 66, SaveTrim);
        save.BackColor = Color.FromArgb(45, 95, 60);

        _trimInfo = new Label
        {
            Left = 298, Top = 36, Width = 400, Height = 24, ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        p.Controls.Add(_trimInfo);

        p.Resize += (_, _) =>
        {
            _strip.Width = Math.Max(60, p.ClientSize.Width - 12);
            _trimInfo.Width = Math.Max(60, p.ClientSize.Width - _trimInfo.Left - 6);
        };
        return p;
    }

    private void ToggleTrim()
    {
        if (_localPath == null) return;
        if (_trimPanel.Visible)
        {
            _trimPanel.Visible = false;
            _previewing = false;
            GrowFor(-_trimPanel.Height);
            return;
        }

        // Index the keyframes on first open (a demux-only ffprobe pass — ~0.15 s on a trailer).
        UseWaitCursor = true;
        try
        {
            _keys = FfmpegService.Keyframes(_localPath);
            _dur = FfmpegService.Duration(_localPath);
        }
        finally { UseWaitCursor = false; }

        if (_dur <= 0 || _keys.Count < 2)
        {
            MessageBox.Show(this,
                _keys.Count < 2
                    ? "This video has fewer than two keyframes, so there is nothing to cut on without re-encoding it."
                    : "ffprobe couldn't read this video's duration.",
                "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _in = 0;
        _out = _dur;
        _strip.Keys = _keys; _strip.Duration = _dur; _strip.In = _in; _strip.Out = _out;
        GrowFor(_trimPanel.Height);      // the strip must not eat into the picture
        _trimPanel.Visible = true;
        _trimPanel.PerformLayout();
        UpdateTrimInfo();
    }

    /// <summary>Give the window <paramref name="dy"/> more (or less) height, without spilling off the screen.</summary>
    private void GrowFor(int dy)
    {
        if (WindowState != FormWindowState.Normal) return;
        var wa = (Screen.FromControl(this) ?? Screen.PrimaryScreen)?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        int h = Math.Min(Height + dy, wa.Height);
        Height = Math.Max(MinimumSize.Height, h);
        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
    }

    /// <summary>Drop the In (or Out) marker at the playhead, SNAPPED to the nearest legal cut point. For Out the
    /// end of the file counts as one too (keeping the tail needs no keyframe).</summary>
    private void SetMark(bool isIn)
    {
        if (_mp == null || _dur <= 0) return;
        double t = Math.Min(_dur, Math.Max(0, _mp.Time / 1000.0));

        if (isIn)
        {
            double snapped = FfmpegService.Snap(_keys, t);
            if (snapped >= _out - VideoTrimmer.MinLengthSec) return;
            _in = snapped;
        }
        else
        {
            var candidates = new List<double>(_keys) { _dur };
            double snapped = FfmpegService.Snap(candidates, t);
            if (snapped <= _in + VideoTrimmer.MinLengthSec) return;
            _out = snapped;
        }

        _strip.In = _in; _strip.Out = _out; _strip.Invalidate();
        _previewing = false;
        UpdateTrimInfo();
    }

    private void UpdateTrimInfo()
    {
        _trimInfo.Text =
            $"In {Clock(_in)}  ·  Out {Clock(_out)}  ·  keeps {(_out - _in):0.0}s of {_dur:0.0}s" +
            $"   ({_keys.Count} keyframes — cuts snap to them, nothing is re-encoded)";
    }

    private static string Clock(double sec) => VideoProbe.Duration((long)(sec * 1000));

    private void PreviewRange()
    {
        if (_mp == null || _dur <= 0) return;
        _mp.Time = (long)(_in * 1000);
        if (!_mp.IsPlaying) _mp.Play();
        _previewing = true;   // the tick pauses us at Out
    }

    private void SaveTrim()
    {
        if (_localPath == null || _dur <= 0) return;
        if (_in <= 0 && _out >= _dur - 0.01)
        {
            MessageBox.Show(this, "Nothing to cut — the selection is the whole video.", "LiteBox",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Trim to {Clock(_in)} → {Clock(_out)}  ({(_out - _in):0.0}s of {_dur:0.0}s)?\n\n" +
                "The file is replaced in place. Nothing is re-encoded — the picture is untouched — and its " +
                "ExtendDB metadata (:crc32 / :info) is carried over unchanged, so the video stays recognised as " +
                "the one you already own.\n\nThis cannot be undone.",
                "Trim video", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
            != DialogResult.OK) return;

        // ffmpeg must be the only one holding the file: stop playback first (libvlc keeps it open while playing).
        _tick.Stop();
        _previewing = false;
        DisposePlayer();

        UseWaitCursor = true;
        bool ok;
        string err;
        try { ok = VideoTrimmer.Cut(_localPath, _in, _out, out err); }
        finally { UseWaitCursor = false; }

        if (!ok)
        {
            MessageBox.Show(this, "Trim failed:\n" + err, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
            StartSource(_at);   // put the player back on the (untouched) file
            return;
        }

        FileChanged = true;
        _keys = FfmpegService.Keyframes(_localPath);   // fresh read: the index is keyed on mtime+size
        _dur = FfmpegService.Duration(_localPath);
        _in = 0; _out = _dur;
        _strip.Keys = _keys; _strip.Duration = _dur; _strip.In = _in; _strip.Out = _out; _strip.Invalidate();
        UpdateTrimInfo();
        StartSource(_at);       // play the trimmed file
    }

    private void TogglePlay()
    {
        if (_mp == null) return;
        try
        {
            if (_mp.IsPlaying) { _mp.Pause(); _playBtn.Text = "▶"; }
            else { _mp.Play(); _playBtn.Text = "❚❚"; }
        }
        catch { }
    }

    private void Refresh_()
    {
        if (_mp == null) return;
        try
        {
            if (!_dragging) _seek.Value = Math.Min(1000, Math.Max(0, (int)(_mp.Position * 1000)));
            _time.Text = VideoProbe.Duration(_mp.Time) + " / " + VideoProbe.Duration(_mp.Length);
            _playBtn.Text = _mp.IsPlaying ? "❚❚" : "▶";

            if (_trimPanel.Visible)
            {
                double t = _mp.Time / 1000.0;
                _strip.Playhead = t;
                _strip.Invalidate();
                if (_previewing && t >= _out) { _mp.Pause(); _previewing = false; }   // preview stops at Out
            }
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _tick.Stop();
        DisposePlayer();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Stop is ASYNCHRONOUS in VLC 3: wait for the Stopped event before the surface HWND goes away, or libvlc's
    /// vout keeps drawing into a destroyed window. The event fires on a VLC thread, so blocking here is safe.
    /// </summary>
    private void DisposePlayer()
    {
        var mp = _mp;
        _mp = null;
        if (mp == null) return;
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
        if (disposing) { try { _tick.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
