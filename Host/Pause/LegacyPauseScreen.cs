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

        private static readonly Color Bg = Color.FromArgb(14, 14, 20);
        private static readonly Color Fg = Color.FromArgb(235, 235, 235);
        private static readonly Color Dim = Color.FromArgb(165, 165, 175);
        private static readonly Color Hi = Color.FromArgb(45, 110, 200);

        private readonly PauseContext _ctx;
        private readonly List<(PauseAction action, Button btn)> _items = new();
        private int _sel;
        private Image? _fanart, _logo, _box;
        private readonly Label _title;
        private readonly Label _sub;
        private readonly PictureBox _logoBox;

        public PauseForm(PauseContext ctx)
        {
            _ctx = ctx;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Bg;
            KeyPreview = true;
            Cursor = Cursors.Default;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            // Title block: clear logo when available (hidden until loaded), else text.
            _title = new Label
            {
                Text = _ctx.GameTitle,
                Font = new Font("Segoe UI", 26f, FontStyle.Bold),
                ForeColor = Fg, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top, Height = 130, Padding = new Padding(0, 44, 0, 0),
            };
            Controls.Add(_title);
            _logoBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Dock = DockStyle.Top, Height = 130, Padding = new Padding(0, 30, 0, 0),
                Visible = false,
            };
            Controls.Add(_logoBox);
            _logoBox.BringToFront();

            // Status line: platform — PAUSED — session time (+ dev / year when known).
            var mins = (int)Math.Max(0, (DateTime.UtcNow - _ctx.SessionStartUtc).TotalMinutes);
            var session = mins >= 60 ? $"{mins / 60}h{mins % 60:00}" : $"{mins} min";
            var meta = _ctx.Developer.Length > 0 || _ctx.ReleaseYear > 0
                ? "  ·  " + string.Join(" ", new[] { _ctx.Developer, _ctx.ReleaseYear > 0 ? _ctx.ReleaseYear.ToString() : "" }.Where(s => s.Length > 0))
                : "";
            _sub = new Label
            {
                Text = $"{(_ctx.Platform.Length > 0 ? _ctx.Platform + "  —  " : "")}PAUSED  ·  session {session}{meta}",
                Font = new Font("Segoe UI", 11f),
                ForeColor = Dim, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top, Height = 32,
            };
            Controls.Add(_sub);
            _sub.BringToFront();

            // Action menu, centred below the title.
            var menu = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            Controls.Add(menu);
            menu.BringToFront();

            void Add(PauseAction a, string label, bool enabled = true)
            {
                if (!enabled) return;
                var b = new Button
                {
                    Text = label,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(26, 26, 34), ForeColor = Fg,
                    FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(40, 40, 52) },
                    Font = new Font("Segoe UI", 15f),
                    Size = new Size(420, 52),
                    TabStop = false,
                };
                b.Click += (_, _) => Fire(a);
                _items.Add((a, b));
                menu.Controls.Add(b);
            }

            Add(PauseAction.Resume, "Resume");
            Add(PauseAction.SaveState, "Save State", _ctx.CanSaveState);
            Add(PauseAction.LoadState, "Load State", _ctx.CanLoadState);
            Add(PauseAction.Reset, "Reset", _ctx.CanReset);
            Add(PauseAction.SwapDiscs, "Swap Discs", _ctx.CanSwapDiscs);
            Add(PauseAction.ExitGame, "Exit Game");

            menu.Resize += (_, _) => LayoutMenu(menu);
            LayoutMenu(menu);
            Select(0);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { Fire(PauseAction.Resume); e.Handled = true; }
                else if (e.KeyCode == Keys.Up) { Select((_sel - 1 + _items.Count) % _items.Count); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { Select((_sel + 1) % _items.Count); e.Handled = true; }
                else if (e.KeyCode is Keys.Enter or Keys.Space) { Fire(_items[_sel].action); e.Handled = true; }
            };

            LoadArtAsync();
        }

        // ── Art (fanart bg / logo / box accent) — async, never blocks the open ──
        private void LoadArtAsync()
        {
            var fanart = _ctx.FanartPath; var logo = _ctx.ClearLogoPath; var box = _ctx.BoxFrontPath;
            System.Threading.Tasks.Task.Run(() =>
            {
                var f = LoadBitmap(fanart); var l = LoadBitmap(logo); var b = LoadBitmap(box);
                try
                {
                    if (IsDisposed) { f?.Dispose(); l?.Dispose(); b?.Dispose(); return; }
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed) { f?.Dispose(); l?.Dispose(); b?.Dispose(); return; }
                        _fanart = f; _box = b;
                        if (l != null)
                        {
                            _logo = l;
                            _logoBox.Image = l;
                            _logoBox.Visible = true;
                            _title.Visible = false;
                        }
                        Invalidate();
                    }));
                }
                catch { f?.Dispose(); l?.Dispose(); b?.Dispose(); }
            });
        }

        /// <summary>File → independent Bitmap (no file lock kept).</summary>
        private static Bitmap? LoadBitmap(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = Image.FromStream(ms);
                return new Bitmap(img);
            }
            catch { return null; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);

            // Fanart, scaled to COVER, at low opacity so the menu stays readable.
            if (_fanart != null)
            {
                var dst = ClientRectangle;
                float ir = (float)_fanart.Width / _fanart.Height, ar = (float)dst.Width / Math.Max(1, dst.Height);
                int w, h;
                if (ir > ar) { h = dst.Height; w = (int)(h * ir); } else { w = dst.Width; h = (int)(w / ir); }
                var att = new ImageAttributes();
                att.SetColorMatrix(new ColorMatrix { Matrix33 = 0.22f });   // ~22% opacity
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(_fanart,
                    new Rectangle(dst.X + (dst.Width - w) / 2, dst.Y + (dst.Height - h) / 2, w, h),
                    0, 0, _fanart.Width, _fanart.Height, GraphicsUnit.Pixel, att);
            }

            // Box front accent, bottom-right, slightly tilted.
            if (_box != null)
            {
                int bh = Math.Min(280, ClientRectangle.Height / 3);
                int bw = (int)(bh * (float)_box.Width / Math.Max(1, _box.Height));
                int x = ClientRectangle.Right - bw - 70, y = ClientRectangle.Bottom - bh - 60;
                var st = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.TranslateTransform(x + bw / 2f, y + bh / 2f);
                g.RotateTransform(-4f);
                var att = new ImageAttributes();
                att.SetColorMatrix(new ColorMatrix { Matrix33 = 0.92f });
                g.DrawImage(_box, new Rectangle(-bw / 2, -bh / 2, bw, bh), 0, 0, _box.Width, _box.Height, GraphicsUnit.Pixel, att);
                g.Restore(st);
            }
        }

        private void LayoutMenu(Panel menu)
        {
            int totalH = _items.Count * 58;
            int y = Math.Max(10, (menu.Height - totalH) / 2 - 30);
            foreach (var (_, b) in _items)
            {
                b.Location = new Point((menu.Width - b.Width) / 2, y);
                y += 58;
            }
        }

        private void Select(int i)
        {
            _sel = Math.Max(0, Math.Min(_items.Count - 1, i));
            for (int k = 0; k < _items.Count; k++)
            {
                var b = _items[k].btn;
                b.BackColor = k == _sel ? Hi : Color.FromArgb(26, 26, 34);
                b.ForeColor = k == _sel ? Color.White : Fg;
            }
        }

        private void Fire(PauseAction a)
        {
            try { _ctx.OnAction?.Invoke(a); } catch { }
        }

        /// <summary>Re-assert top-most + foreground a few times — emulators (especially
        /// exclusive-fullscreen ones) can win the first foreground fight.</summary>
        public void ForceToFront(int attempts)
        {
            int n = 0;
            var t = new System.Windows.Forms.Timer { Interval = 250 };
            t.Tick += (_, _) =>
            {
                if (IsDisposed || !Visible || n++ >= attempts) { t.Stop(); t.Dispose(); return; }
                try { TopMost = true; Activate(); SetForegroundWindow(Handle); } catch { }
            };
            try { Activate(); SetForegroundWindow(Handle); } catch { }
            t.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fanart?.Dispose(); _logo?.Dispose(); _box?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
