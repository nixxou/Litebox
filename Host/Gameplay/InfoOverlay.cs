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

    private readonly string _banner;
    private Bitmap? _bg;
    private bool _cursorHidden;

    // Full-screen overlay (own art, no app chrome) rather than a themed dialog, same as
    // LegacyPauseScreen - so a local DPI scale factor instead of deriving from LiteBoxForm.
    private readonly float _s;
    private int S(int px) => (int)Math.Round(px * _s);

    public InfoOverlay(PauseContext ctx, string banner, bool hideCursor)
    {
        _banner = banner ?? "";
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

        // Banner shows instantly (drawn in OnPaintBackground); the full art swaps in
        // a moment later (image loads off the UI thread).
        BuildBackgroundAsync(Bounds.Size, ctx);
    }

    /// <summary>WS_EX_COMPOSITED: window + children drawn into one buffer (no flicker).</summary>
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }
    }

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
        if (_bg != null) { e.Graphics.DrawImageUnscaled(_bg, 0, 0); return; }
        // Pre-art fallback: solid background + the banner so it's visible immediately.
        e.Graphics.Clear(ScreenArt.Bg);
        if (_banner.Length == 0) return;
        int bandH = Math.Max(S(120), ClientSize.Height / 7);
        int bandY = (ClientSize.Height - bandH) / 2;
        using var band = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        e.Graphics.FillRectangle(band, 0, bandY, ClientSize.Width, bandH);
        using var f = new Font("Segoe UI", 34f);
        using var br = new SolidBrush(ScreenArt.Fg);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(_banner, f, br, new RectangleF(0, bandY, ClientSize.Width, bandH), sf);
    }

    private System.Windows.Forms.Timer? _frontTimer;
    private bool _frontYielded;

    /// <summary>Re-assert the foreground only while it is actually lost (no per-tick flicker).
    /// Cancellable via <see cref="ReleaseTopFront"/> so the startup screen can hand the
    /// foreground to the emulator the moment it spawns.</summary>
    public void ForceToFront(int attempts)
    {
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
        try { TopMost = false; } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_cursorHidden) { try { Cursor.Show(); } catch { } _cursorHidden = false; }
            _bg?.Dispose();
        }
        base.Dispose(disposing);
    }
}
