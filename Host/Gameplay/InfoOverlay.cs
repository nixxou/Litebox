// Full-screen "info" overlay for the Game Startup ("NOW LOADING…") and Game End
// ("GAME OVER") screens. Same look as the pause screen (shared ScreenArt background:
// fanart + logo/title + box accent) but with a big centred banner instead of an
// action menu, no buttons, and no input — the manager just shows it for a duration
// then closes it. Built on the dedicated UI thread (UiThread), like the pause screen.

#nullable enable

using System.Runtime.InteropServices;
using LbApiHost.Host.Pause;

namespace LbApiHost.Host.Gameplay;

internal sealed class InfoOverlay : Form
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    private readonly string _banner;
    private readonly bool _noActivate;   // StartupStayOnTop: cover the screen, NEVER take focus
    private Bitmap? _bg;
    private bool _cursorHidden;

    // Startup progress bar (startup cover only): fills 0→~90% over the predicted ETA (the game's past
    // launch→detection time), then snaps to 100% when the reveal fade starts. null ⇒ no bar.
    private readonly int? _etaMs;
    private double _progress;   // UI-thread only (progress timer + paint)
    private System.Diagnostics.Stopwatch? _progressSw;
    private System.Windows.Forms.Timer? _progressTimer;

    // Full-screen overlay (own art, no app chrome) rather than a themed dialog, same as
    // LegacyPauseScreen - so a local DPI scale factor instead of deriving from LiteBoxForm.
    private readonly float _s;
    private int S(int px) => (int)Math.Round(px * _s);

    public InfoOverlay(PauseContext ctx, string banner, bool hideCursor, bool noActivate = false, int? etaMs = null)
    {
        _banner = banner ?? "";
        _noActivate = noActivate;
        _etaMs = etaMs.HasValue && etaMs.Value > 0 ? etaMs : null;
        _s = DeviceDpi / 96f;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Screen target;
        try { target = ctx.EmulatorMainWindow != IntPtr.Zero ? Screen.FromHandle(ctx.EmulatorMainWindow) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]); }
        catch { target = Screen.PrimaryScreen ?? Screen.AllScreens[0]; }
        Bounds = target?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = ScreenArt.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        if (hideCursor) { try { Cursor.Hide(); _cursorHidden = true; } catch { } }

        // Stay-on-top mode: a fullscreen emulator (RetroArch) makes ITSELF topmost when it
        // activates, and within the topmost band the activated window goes in front — a
        // single TopMost=true loses the cover after a second. Re-assert our spot at the top
        // of the band every 300 ms, WITHOUT activation, for as long as the cover is up: the
        // emulator asserts once at window creation, we assert continuously, we win.
        if (_noActivate)
        {
            int ticks = 0;
            _topTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _topTimer.Tick += (_, _) =>
            {
                try
                {
                    if (IsDisposed || !Visible) return;
                    bool ok = SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    if (++ticks == 1 || !ok)
                        Console.WriteLine($"[startup] top re-assert #{ticks} ok={ok} err={Marshal.GetLastWin32Error()}");
                }
                catch (Exception ex) { Console.WriteLine("[startup] top re-assert EX: " + ex.Message); }
            };
            _topTimer.Start();
        }

        // Banner shows instantly (drawn in OnPaintBackground); the full art swaps in
        // a moment later (image loads off the UI thread).
        BuildBackgroundAsync(Bounds.Size, ctx);

        // Progress bar: advance toward the ETA, capped at 90% until the reveal snaps it to 100%.
        if (_etaMs != null)
        {
            _progressSw = System.Diagnostics.Stopwatch.StartNew();
            _progressTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _progressTimer.Tick += (_, _) =>
            {
                if (IsDisposed) return;
                double frac = (double)_progressSw!.ElapsedMilliseconds / _etaMs!.Value;
                double p = Math.Min(0.90, frac);
                if (p > _progress) { _progress = p; try { Invalidate(); } catch { } }
            };
            _progressTimer.Start();
        }
    }

    /// <summary>WS_EX_COMPOSITED: window + children drawn into one buffer (no flicker).
    /// In no-activate mode, WS_EX_NOACTIVATE too: the window can be shown, sized and kept
    /// TOPMOST while NEVER entering the activation chain — the emulator behind it keeps
    /// the focus (RetroArch pause_nonactive stays running).</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;                    // WS_EX_COMPOSITED
            // WinForms quirk: with ShowWithoutActivation=true the TopMost property is
            // silently LOST (its SetWindowPos rides the activation path) — measured: the
            // shown overlay had no WS_EX_TOPMOST bit and sat BELOW the emulator. Bake the
            // bit into the window at birth instead.
            if (_noActivate) cp.ExStyle |= 0x08000000 | 0x00000008;   // WS_EX_NOACTIVATE | WS_EX_TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => _noActivate;

    private void BuildBackgroundAsync(Size size, PauseContext ctx)
    {
        var banner = _banner;
        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { bmp = ScreenArt.Compose(size, ctx, statusLine: null, bannerText: banner); }
            catch { bmp?.Dispose(); return; }
            try
            {
                if (IsDisposed) { bmp?.Dispose(); return; }
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) { bmp?.Dispose(); return; }
                    var old = _bg; _bg = bmp; old?.Dispose();
                    Invalidate();
                }));
            }
            catch { bmp?.Dispose(); }
        });
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_bg != null) e.Graphics.DrawImageUnscaled(_bg, 0, 0);
        else
        {
            // Pre-art fallback: solid background + the banner so it's visible immediately.
            e.Graphics.Clear(ScreenArt.Bg);
            if (_banner.Length > 0)
            {
                int fbH = Math.Max(S(120), ClientSize.Height / 7);
                int fbY = (ClientSize.Height - fbH) / 2;
                using var band = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                e.Graphics.FillRectangle(band, 0, fbY, ClientSize.Width, fbH);
                using var f = new Font("Segoe UI", 34f);
                using var br = new SolidBrush(ScreenArt.Fg);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(_banner, f, br, new RectangleF(0, fbY, ClientSize.Width, fbH), sf);
            }
        }
        DrawProgress(e.Graphics);
    }

    /// <summary>A thin, centred rounded progress bar just under the banner band — discreet, accent-tinted,
    /// with a soft trailing glow at the leading edge. Only drawn when an ETA was supplied.</summary>
    private void DrawProgress(Graphics g)
    {
        if (_etaMs == null) return;
        double p = Math.Max(0.0, Math.Min(1.0, _progress));
        int barW = Math.Min(S(560), (int)(ClientSize.Width * 0.32));
        int barH = S(6);
        int x = (ClientSize.Width - barW) / 2;
        int bandH = Math.Max(S(120), ClientSize.Height / 7);
        int y = (ClientSize.Height - bandH) / 2 + bandH + S(30);
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // track
        using (var tp = RoundRect(x, y, barW, barH, barH / 2f))
        using (var tb = new SolidBrush(Color.FromArgb(64, 255, 255, 255)))
            g.FillPath(tb, tp);
        // fill
        int fw = (int)Math.Round(barW * p);
        if (fw >= barH)
        {
            var accent = Color.FromArgb(235, 120, 180, 255);
            using (var fp = RoundRect(x, y, fw, barH, barH / 2f))
            using (var fb = new SolidBrush(accent))
                g.FillPath(fb, fp);
            // leading-edge glow
            using (var glow = new SolidBrush(Color.FromArgb(90, accent)))
                g.FillEllipse(glow, x + fw - barH, y - barH / 2, barH * 2, barH * 2);
        }
        g.SmoothingMode = old;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        var gp = new System.Drawing.Drawing2D.GraphicsPath();
        if (w <= 0 || h <= 0) return gp;
        gp.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        gp.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        gp.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        gp.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    private System.Windows.Forms.Timer? _frontTimer;
    private System.Windows.Forms.Timer? _topTimer;   // stay-on-top band re-assert (no-activate mode)
    private bool _frontYielded;

    /// <summary>Re-assert the foreground only while it is actually lost (no per-tick flicker).
    /// Cancellable via <see cref="ReleaseTopFront"/> so the startup screen can hand the
    /// foreground to the emulator the moment it spawns.</summary>
    public void ForceToFront(int attempts)
    {
        if (_noActivate) return;   // stay-on-top mode: TOPMOST already covers, focus is never ours
        if (_frontYielded) return;
        try { Activate(); SetForegroundWindow(Handle); } catch { }
        int n = 0;
        try { _frontTimer?.Stop(); _frontTimer?.Dispose(); } catch { }
        _frontTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _frontTimer.Tick += (_, _) =>
        {
            if (_frontYielded || IsDisposed || !Visible || n++ >= attempts)
            { try { _frontTimer?.Stop(); _frontTimer?.Dispose(); } catch { } _frontTimer = null; return; }
            try { if (GetForegroundWindow() != Handle) { Activate(); SetForegroundWindow(Handle); } } catch { }
        };
        _frontTimer.Start();
    }

    /// <summary>Drop the always-on-top state and stop re-asserting the foreground, so the
    /// emulator window can come forward and keep focus. The overlay stays visible (it simply
    /// no longer floats above nor steals focus) until it is closed on its own timer. Must be
    /// called on the overlay's UI thread.</summary>
    public void ReleaseTopFront()
    {
        _frontYielded = true;
        try { _frontTimer?.Stop(); _frontTimer?.Dispose(); } catch { }
        _frontTimer = null;
        // Stay-on-top mode KEEPS TopMost for the whole configured duration — the emulator
        // loads (and runs, focused) behind the cover; only the focus-forcing ever stops.
        if (!_noActivate) { try { TopMost = false; } catch { } }
    }

    /// <summary>Close without ever yanking the foreground: when this window is NOT the
    /// foreground, hide first (hiding a non-active window never moves the focus, and
    /// destroying a hidden one doesn't either — the ExtendDB Update-form lesson).</summary>
    public void HideThenClose()
    {
        try { _topTimer?.Stop(); _topTimer?.Dispose(); } catch { }
        _topTimer = null;
        try { if (GetForegroundWindow() != Handle) Hide(); } catch { }
        try { Close(); } catch { }
    }

    /// <summary>Dissolve the overlay over <paramref name="fadeMs"/> — animate the window Opacity 1→0
    /// (revealing whatever is behind it: the game, or the frontend), then HideThenClose + Dispose.
    /// Keeps re-asserting TOPMOST during the fade so it stays above a fullscreen emulator. fadeMs ≤ 0 or
    /// no message loop ⇒ immediate close. Must run on the overlay's UI thread.</summary>
    private System.Windows.Forms.Timer? _fadeTimer;
    public void FadeOutClose(int fadeMs)
    {
        // The reveal is happening → snap the progress bar to 100% for the moment before it dissolves.
        try { _progressTimer?.Stop(); } catch { }
        if (_etaMs != null && _progress < 1.0) { _progress = 1.0; try { Invalidate(); Update(); } catch { } }
        if (fadeMs <= 0 || IsDisposed) { HideThenClose(); try { Dispose(); } catch { } return; }
        const int stepMs = 30;
        int steps = Math.Max(1, fadeMs / stepMs);
        int i = 0;
        _fadeTimer = new System.Windows.Forms.Timer { Interval = stepMs };
        _fadeTimer.Tick += (_, _) =>
        {
            i++;
            try { if (!IsDisposed) Opacity = Math.Max(0.0, 1.0 - (double)i / steps); } catch { }
            if (i >= steps || IsDisposed)
            {
                try { _fadeTimer?.Stop(); _fadeTimer?.Dispose(); } catch { }
                _fadeTimer = null;
                try { HideThenClose(); } catch { }
                try { Dispose(); } catch { }
            }
        };
        _fadeTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _topTimer?.Stop(); _topTimer?.Dispose(); } catch { }
            _topTimer = null;
            try { _fadeTimer?.Stop(); _fadeTimer?.Dispose(); } catch { }
            _fadeTimer = null;
            try { _progressTimer?.Stop(); _progressTimer?.Dispose(); } catch { }
            _progressTimer = null;
            if (_cursorHidden) { try { Cursor.Show(); } catch { } _cursorHidden = false; }
            _bg?.Dispose();
        }
        base.Dispose(disposing);
    }
}
