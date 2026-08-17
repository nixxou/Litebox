// "LaunchBox legacy" pause mode: a borderless, top-most, full-screen overlay with a
// vertical action menu (Resume / Save State / Load State / Reset / Swap Discs /
// Exit Game). Keyboard-driven (Up/Down + Enter, Esc = Resume) and clickable.
//
// Cosmetics (all sourced from the LaunchedGame snapshot, never the dropped caches):
//   • the game's fanart painted as a scaled-to-cover background at low opacity,
//   • the clear logo instead of the plain title text when available,
//   • the box front, slightly tilted, as a bottom-right accent,
//   • the session play time in the status line.
//
// Anti-flicker design (the first version visibly "waved"):
//   • The whole background (base colour + fanart + box accent + logo/title + status
//     line) is PRE-COMPOSITED into ONE bitmap sized to the screen, off the UI
//     thread; OnPaintBackground just blits it. No transparent stacked controls —
//     transparency in WinForms makes every child repaint the parent region
//     separately (the black waves). Only the opaque buttons are real controls.
//   • WS_EX_COMPOSITED double-buffers the window INCLUDING children.
//   • ForceToFront only re-asserts when the window actually lost the foreground
//     (blind TopMost re-sets every 250ms repainted the whole window each tick).
//
// Presentation only — every action is reported through PauseContext.OnAction and the
// PauseManager runs the mechanics (resume the process, fire the AHK scripts, …).

#nullable enable

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Pause;

internal sealed class LegacyPauseScreen : IPauseScreen
{
    private PauseForm? _form;

    public bool IsOpen => _form is { IsDisposed: false, Visible: true };

    public void Show(PauseContext ctx)
    {
        Close();
        _form = new PauseForm(ctx);
        _form.Show();
        _form.ForceToFront(ctx.ForcefulActivation ? 8 : 2);
    }

    public void Close()
    {
        try { if (_form is { IsDisposed: false }) _form.Close(); } catch { }
        try { _form?.Dispose(); } catch { }
        _form = null;
    }

    // ── The overlay form ──────────────────────────────────────────────
    private sealed class PauseForm : Form
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private static readonly Color Bg = Color.FromArgb(14, 14, 20);
        private static readonly Color Fg = Color.FromArgb(235, 235, 235);
        private static readonly Color Dim = Color.FromArgb(165, 165, 175);
        private static readonly Color Hi = Color.FromArgb(45, 110, 200);
        private static readonly Color BtnBg = Color.FromArgb(26, 26, 34);

        private readonly PauseContext _ctx;
        // Menu entries as (fire, button) delegates: the same list renders the MAIN menu and the
        // "Additional Documents" SUBMENU (console-style menu swap — one flat list at a time, the
        // same Up/Down/Enter/Esc + pad navigation drives both levels).
        private readonly List<(Action fire, Button btn)> _items = new();
        private bool _inDocs;   // submenu shown → Esc / pad-B go BACK instead of resuming
        private int _sel;
        private Bitmap? _bg;   // pre-composited full-screen background (blitted as-is)
        private System.Windows.Forms.Timer? _padTimer;   // controller poll (overlay lifetime)

        // This overlay is a full-screen game pause menu with its own deliberately distinct color
        // scheme - not a settings dialog - so it doesn't derive from LiteBoxForm (which would force
        // the standard app palette). Same DPI-scale-factor idea as everywhere else, just local: only
        // the button/menu-layout pixel dimensions below need it (Font sizes are already DPI-correct
        // via GDI+, same as always).
        private readonly float _s;
        private int S(int px) => (int)Math.Round(px * _s);

        public PauseForm(PauseContext ctx)
        {
            _ctx = ctx;
            _s = DeviceDpi / 96f;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            // Cover the monitor the GAME is on (multi-monitor: the overlay must sit
            // over the frozen emulator, not necessarily the primary screen).
            Screen target;
            try { target = ctx.EmulatorMainWindow != IntPtr.Zero ? Screen.FromHandle(ctx.EmulatorMainWindow) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]); }
            catch { target = Screen.PrimaryScreen ?? Screen.AllScreens[0]; }
            Bounds = target?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Bg;
            KeyPreview = true;
            Cursor = Cursors.Default;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            BuildMainMenu();

            // While the manual viewer (or anything else) takes the foreground, yield
            // TopMost so it is actually readable above the overlay; reclaim on focus.
            Deactivate += (_, _) => { try { TopMost = false; } catch { } };
            Activated += (_, _) => { try { TopMost = true; } catch { } };

            LayoutMenu();
            Select(0);

            // Keyboard navigation lives in ProcessCmdKey (see below) — arrow keys are
            // DIALOG-NAVIGATION keys: with buttons on the form, ProcessDialogKey eats
            // Up/Down to move the native focus BEFORE the form's KeyDown ever fires,
            // so a KeyDown handler never sees them.

            // Controller navigation: D-pad / left stick = move, A = pick, B / Start
            // = resume. Polled (~60ms) only while the overlay is alive — XInput
            // needs no focus, so it works even if an emulator window fights us.
            var pad = new XInputPad();
            _padTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _padTimer.Tick += (_, _) =>
            {
                if (IsDisposed || !Visible) return;
                var (dirY, pressed) = pad.Poll();
                if (dirY < 0) Select((_sel - 1 + _items.Count) % _items.Count);
                else if (dirY > 0) Select((_sel + 1) % _items.Count);
                if ((pressed & XInputPad.A) != 0) _items[_sel].fire();
                else if ((pressed & (XInputPad.B | XInputPad.Start)) != 0) BackOrResume();
            };
            _padTimer.Start();

            // Compose the background off-thread (text-only immediately, art a moment later).
            BuildBackgroundAsync();

            // Track the display resolution: an exclusive-fullscreen game may have changed it (e.g. 640×480),
            // and it may change again when the game resumes. Re-fit the overlay to the CURRENT mode so it
            // always covers the whole screen instead of a native-sized rectangle spilling off a small mode.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(FitToScreen)); } catch { }
        }

        /// <summary>Resize the overlay to the CURRENT bounds of the monitor it sits on (the game may have
        /// switched the display mode after this form was built) and re-centre the menu + re-render the art.</summary>
        private void FitToScreen()
        {
            try
            {
                var b = Screen.FromHandle(Handle).Bounds;
                if (b == Bounds) return;
                Bounds = b;
                LayoutMenu();
                BuildBackgroundAsync();
                Invalidate();
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitToScreen();   // the resolution may have settled between construction and the actual show
        }

        /// <summary>WS_EX_COMPOSITED: the window and ALL children are drawn into one
        /// off-screen buffer — kills the per-control repaint waves.</summary>
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }
        }

        /// <summary>Keyboard handling. Runs BEFORE WinForms dialog-key navigation, so
        /// the arrows drive OUR selection instead of hopping the native button focus
        /// (and Enter can't double-fire a natively-focused button).</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: BackOrResume(); return true;
                case Keys.Up: Select((_sel - 1 + _items.Count) % _items.Count); return true;
                case Keys.Down: Select((_sel + 1) % _items.Count); return true;
                case Keys.Enter:
                case Keys.Space: _items[_sel].fire(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ── Pre-composited background ─────────────────────────────────
        private void BuildBackgroundAsync()
        {
            var size = Bounds.Size;
            var ctx = _ctx;
            System.Threading.Tasks.Task.Run(() =>
            {
                Bitmap? bmp = null;
                try { bmp = ComposeBackground(size, ctx); }
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

        private static Bitmap ComposeBackground(Size size, PauseContext ctx)
        {
            // Shared art (fanart + box + logo/title) + the pause-specific status line.
            var mins = (int)Math.Max(0, (DateTime.UtcNow - ctx.SessionStartUtc).TotalMinutes);
            var session = mins >= 60 ? $"{mins / 60}h{mins % 60:00}" : $"{mins} min";
            var meta = ctx.Developer.Length > 0 || ctx.ReleaseYear > 0
                ? "  ·  " + string.Join(" ", new[] { ctx.Developer, ctx.ReleaseYear > 0 ? ctx.ReleaseYear.ToString() : "" }.Where(s => s.Length > 0))
                : "";
            var status = $"{(ctx.Platform.Length > 0 ? ctx.Platform + "  —  " : "")}PAUSED  ·  session {session}{meta}";
            return ScreenArt.Compose(size, ctx, statusLine: status, bannerText: null);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_bg != null) e.Graphics.DrawImageUnscaled(_bg, 0, 0);
            else e.Graphics.Clear(Bg);
        }

        // ── Menu construction (main + Documents submenu, one flat list at a time) ──

        /// <summary>Replace the button list wholesale and re-centre it (menu swap).</summary>
        private void SetMenu(IEnumerable<(string label, Action fire)> entries)
        {
            foreach (var (_, b) in _items) { try { Controls.Remove(b); b.Dispose(); } catch { } }
            _items.Clear();
            foreach (var (label, fire) in entries)
            {
                var b = new Button
                {
                    Text = label,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = BtnBg, ForeColor = Fg,
                    FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(40, 40, 52) },
                    Font = new Font("Segoe UI", 15f),
                    Size = new Size(S(420), S(52)),
                    TabStop = false,
                    AutoEllipsis = true,   // document names can be long
                };
                b.Click += (_, _) => fire();
                _items.Add((fire, b));
                Controls.Add(b);
            }
            LayoutMenu();
            Select(0);
        }

        private void BuildMainMenu()
        {
            _inDocs = false;
            var m = new List<(string, Action)> { ("Resume Game", () => Fire(PauseAction.Resume)) };
            if (_ctx.CanViewManual) m.Add(("View Manual", () => Fire(PauseAction.ViewManual)));
            // LB 14: the game's Document records get their own submenu (console-style swap).
            if (_ctx.Documents.Count > 0) m.Add(("Additional Documents", BuildDocsMenu));
            if (_ctx.CanReset) m.Add(("Reset Game", () => Fire(PauseAction.Reset)));
            if (_ctx.CanSaveState) m.Add(("Save State", () => Fire(PauseAction.SaveState)));
            if (_ctx.CanLoadState) m.Add(("Load State", () => Fire(PauseAction.LoadState)));
            if (_ctx.CanSwapDiscs) m.Add(("Swap Discs", () => Fire(PauseAction.SwapDiscs)));
            m.Add(("Exit Game", () => Fire(PauseAction.ExitGame)));
            SetMenu(m);
        }

        private void BuildDocsMenu()
        {
            _inDocs = true;
            var m = new List<(string, Action)>();
            // The centred layout has no scrolling: cap the list so it always fits (a game with
            // more documents than that is exotic; the first N stay reachable, XML order).
            const int Max = 12;
            foreach (var (name, path) in _ctx.Documents.Take(Max))
            {
                string p = path;   // capture per-entry
                m.Add((name, () => { try { _ctx.OnOpenDocument?.Invoke(p); } catch { } }));
            }
            if (_ctx.Documents.Count > Max)
                Console.WriteLine($"[pause] documents submenu capped at {Max} of {_ctx.Documents.Count}");
            m.Add(("Back", BuildMainMenu));
            SetMenu(m);
        }

        /// <summary>Esc / pad-B: leave the submenu when one is open, resume the game otherwise.</summary>
        private void BackOrResume()
        {
            if (_inDocs) BuildMainMenu();
            else Fire(PauseAction.Resume);
        }

        private void LayoutMenu()
        {
            int rowH = S(58);
            int totalH = _items.Count * rowH;
            int y = Math.Max(S(220), (ClientSize.Height - totalH) / 2 - S(10));
            foreach (var (_, b) in _items)
            {
                b.Location = new Point((ClientSize.Width - b.Width) / 2, y);
                y += rowH;
            }
        }

        private void Select(int i)
        {
            _sel = Math.Max(0, Math.Min(_items.Count - 1, i));
            for (int k = 0; k < _items.Count; k++)
            {
                var b = _items[k].btn;
                b.BackColor = k == _sel ? Hi : BtnBg;
                b.ForeColor = k == _sel ? Color.White : Fg;
            }
        }

        private void Fire(PauseAction a)
        {
            try { _ctx.OnAction?.Invoke(a); } catch { }
        }

        /// <summary>Re-assert the foreground only while it is actually LOST —
        /// blind TopMost/Activate every tick repaints the window (flicker).</summary>
        public void ForceToFront(int attempts)
        {
            try { Activate(); SetForegroundWindow(Handle); } catch { }
            int n = 0;
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += (_, _) =>
            {
                if (IsDisposed || !Visible || n++ >= attempts) { t.Stop(); t.Dispose(); return; }
                try
                {
                    if (GetForegroundWindow() != Handle) { Activate(); SetForegroundWindow(Handle); }
                }
                catch { }
            };
            t.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
                try { _padTimer?.Stop(); _padTimer?.Dispose(); } catch { }
                _bg?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
