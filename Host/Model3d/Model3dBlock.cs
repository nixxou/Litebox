// The detail pane's 3D case block (right panel, directly under the hero) — INDEPENDENT of the post-load
// image system for now (own row, own loader; integration into the instant/post-load config is a later step).
//
// Selection flow (all off the UI thread, token-guarded):
//   1. Resolve the game's cache identity (a few stats). No art → the block collapses.
//   2. Cache HIT  → read ONLY the GLB head (header + JSON + thumb bytes) and show the pre-rendered
//      transparent snapshot IMMEDIATELY — this is the whole point of the thumb-first GLB layout.
//      Then load the full model in the background and swap the live viewport in (same pose/camera as the
//      thumb → the swap is invisible until the user drags to orbit).
//   3. Cache MISS → bake on the STA worker (writes the GLB), then same as a hit.
//
// The viewport reuses HomeModel3d (exact LB scene: fixed camera/lights, model rotation) with the shared
// default pose from Model3dBaker; drag orbits, wheel zooms (OrbitController semantics).

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Model3d;

/// <summary>Draws the baked snapshot FIT TO HEIGHT, centred — never a letterbox. The frame is baked wide
/// (Model3dBaker.BakeAspect) with the model fitted vertically, so scaling on the height and cropping the
/// leftover width reproduces, for any box ratio, exactly what the live viewport shows with
/// CameraDistanceFor(that ratio) — same model size to the pixel, no re-bake when the ratio changes.</summary>
internal sealed class SnapshotBox : Panel
{
    private Image? _img;

    public SnapshotBox()
    {
        DoubleBuffered = true; ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? Snapshot
    {
        get => _img;
        set { var old = _img; _img = value; if (!ReferenceEquals(old, value)) old?.Dispose(); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_img == null || _img.Height <= 0 || ClientSize.Height <= 0) return;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        double k = (double)ClientSize.Height / _img.Height;          // the HEIGHT is the invariant
        int w = Math.Max(1, (int)Math.Round(_img.Width * k));
        g.DrawImage(_img, (ClientSize.Width - w) / 2, 0, w, ClientSize.Height);   // centred → equal crop both sides
    }
}

internal sealed class Model3dBlock : Panel
{
    private readonly SnapshotBox _pic;
    private readonly Model3dExpandBadge _expand;
    private Platforms.HomeModel3d? _home;      // created lazily on first model (ElementHost is not free)
    private Platforms.OrbitController? _orbit;
    private int _token;
    private bool _hasContent;

    /// <summary>The block has something to show for the current game — drives its grid row height.</summary>
    public bool HasContent => _hasContent;

    /// <summary>Raised (on the UI thread) when <see cref="HasContent"/> flips — the detail pane relayouts.</summary>
    public Action? ContentChanged;

    /// <summary>The ⤢ badge (bottom-right) was clicked — the host opens the fullscreen 3D viewer.</summary>
    public Action? ExpandClicked;

    public Model3dBlock()
    {
        DoubleBuffered = true;
        _pic = new SnapshotBox { Dock = DockStyle.Fill, BackColor = BackColor };
        _pic.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ExpandClicked?.Invoke();
        };
        Controls.Add(_pic);
        // Fullscreen badge: a WinForms sibling ABOVE both layers (HWND z-order beats the ElementHost).
        _expand = new Model3dExpandBadge { Visible = false, BackColor = BackColor };
        _expand.Click += (_, _) => ExpandClicked?.Invoke();
        Controls.Add(_expand);
        BackColorChanged += (_, _) => { _pic.BackColor = BackColor; _expand.BackColor = BackColor; };
        // The pane width (→ the box aspect) can change while the viewport is live — keep the framing law.
        Resize += (_, _) => { PlaceBadge(); if (_home is { } h) ApplyFraming(h); };
        PlaceBadge();
    }

    private void PlaceBadge()
        => _expand.Location = new Point(Math.Max(0, ClientSize.Width - _expand.Width - 8),
                                        Math.Max(0, ClientSize.Height - _expand.Height - 8));

    // BOTH layers show the same framing at ANY box ratio, without re-baking anything:
    //   • the snapshot is baked ONCE at Model3dBaker.BakeAspect (wide), with the model fitted vertically;
    //   • the PNG layer draws it FIT TO HEIGHT, centred — a narrower box (poster ratio) simply crops the
    //     empty width off the sides, it never shrinks the model into a letterbox;
    //   • the viewport uses CameraDistanceFor(THIS BOX's aspect), whose vertical extent is aspect-
    //     independent by construction — i.e. exactly the same crop.
    // So the PNG → viewport swap still can't shift by a pixel, and flipping 16:9/poster costs no bake.
    private double BoxAspect => ClientSize.Height > 0 ? (double)ClientSize.Width / ClientSize.Height : Model3dBaker.BakeAspect;

    private void ApplyFraming(Platforms.HomeModel3d home)
    {
        double z = Model3dBaker.CameraDistanceFor(BoxAspect) / 2.0;
        if (_orbit != null) _orbit.InitZoom(z);   // keeps the wheel continuing from this framing
        else home.SetZoom(z);
    }

    private Platforms.HomeModel3d EnsureHome()
    {
        if (_home == null)
        {
            _home = new Platforms.HomeModel3d();
            _home.Control.Dock = DockStyle.Fill;
            // The host stays VISIBLE forever once created, always UNDER the PNG layer. Toggling its
            // visibility per swap was the flicker: showing an ElementHost HWND can flash OVER its
            // WinForms siblings for a frame (airspace), regardless of z-order — the cover PNG itself
            // blinked out. With the host permanently shown (a null model renders just the backdrop),
            // the whole swap is _pic.Visible alone, which cannot flash through anything.
            _home.SetBackground(BackColor);   // same backdrop as the PNG layer (anti-flash)
            ApplyFraming(_home);              // bake-identical framing (distance + aspect FOV)
            Controls.Add(_home.Control);
            _pic.BringToFront();               // the PNG layer stays above the (always-visible) host
            _expand.BringToFront();            // …and the badge above everything
            _orbit = new Platforms.OrbitController();
            _orbit.Attach(_home);
            WireOrbit(_home.Control, _orbit, () => ExpandClicked?.Invoke());
        }
        return _home;
    }

    /// <summary>Node selected / feature off — collapse and forget the current game.</summary>
    public void Clear()
    {
        _token++;
        SetThumb(null);
        _home?.SetModel(null);   // host STAYS visible (backdrop only) — hiding it re-arms the show-flash
        _pic.Visible = true;     // cover layer back in place for the next game
        _expand.Visible = false;
        if (_hasContent) { _hasContent = false; ContentChanged?.Invoke(); }
    }

    /// <summary>Show the 3D case of <paramref name="g"/> (thumb first, live model behind). Non-blocking.</summary>
    public void ShowFor(IGame g)
    {
        // The block starts HIDDEN (content-driven visibility) — but a never-shown control has no HWND,
        // and Apply()'s BeginInvoke marshalling needs one. Reading Handle creates it even while hidden;
        // without this the content could never land and the overlay never appeared (chicken-and-egg).
        try { if (!IsHandleCreated && !IsDisposed) _ = Handle; } catch { }
        int token = ++_token;
        // LongRunning (dedicated thread): a cache MISS BLOCKS on the serialized STA bake queue — doing
        // that on a pool thread starved the pool during fast scrolling (the transit image loader is a
        // pool task too, and it froze).
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                var idn = Model3dCache.Resolve(g);
                if (idn == null || !idn.HasArt) { Collapse(token); return; }

                string glb = idn.GlbPath;
                bool existed;
                try { existed = File.Exists(glb); } catch { existed = false; }

                // HIT → snapshot right now (partial read), model behind. MISS → bake first (STA worker,
                // serialized, stale-dropped when the selection moves on), then the same two-step.
                if (!existed && Model3dCache.Ensure(g, stillWanted: () => _token == token) == null) { Collapse(token); return; }
                var thumb = Model3dCache.ReadThumbPng(glb);   // sidecar fast path, GLB-extract fallback
                if (thumb != null && _token == token) Apply(token, ThumbImage(thumb), null);

                var model = GlbFile.LoadModel(glb);   // frozen → UI-thread safe
                if (model != null && _token == token) Apply(token, null, model);
                else if (thumb == null && model == null) Collapse(token);

                // A HIT is shown first for speed, but "present" is not "current": new art, changed model
                // settings or a newer baker all leave the old GLB sitting in the game's slot. Existence
                // alone used to end the story here, so a freshly downloaded back never reached the model —
                // it took a bulk regenerate. Validate AFTER painting and swap in the re-bake.
                if (existed && _token == token && !Model3dCache.IsCurrent(idn)
                    && Model3dCache.Ensure(g, stillWanted: () => _token == token) != null && _token == token)
                {
                    var freshThumb = Model3dCache.ReadThumbPng(glb);
                    if (freshThumb != null && _token == token) Apply(token, ThumbImage(freshThumb), null);
                    var fresh = GlbFile.LoadModel(glb);
                    if (fresh != null && _token == token) Apply(token, null, fresh);
                }
            }
            catch (Exception ex) { Console.WriteLine("[model3d] block: " + ex.Message); Collapse(token); }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
    }

    private static Image? ThumbImage(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return Image.FromStream(ms); }
        catch { return null; }
    }

    private void Collapse(int token)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { if (_token == token) Clear(); })); }
        catch { }
    }

    // One of the two payloads per call: a thumb Image (step 1) or the frozen model (step 2).
    private void Apply(int token, Image? thumb, System.Windows.Media.Media3D.Model3D? model)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) { thumb?.Dispose(); return; }
            BeginInvoke((Action)(() =>
            {
                if (_token != token || IsDisposed) { thumb?.Dispose(); return; }
                if (thumb != null)
                {
                    SetThumb(thumb);
                    _pic.Visible = true;
                    _pic.BringToFront();
                    _expand.BringToFront();
                }
                if (model != null)
                {
                    var home = EnsureHome();
                    home.SetPose(Model3dBaker.DefaultYawDeg, Model3dBaker.DefaultPitchDeg);
                    ApplyFraming(home);
                    home.SetModel(model);
                    _pic.BringToFront();          // PNG stays the top cover while the model composes beneath
                    _expand.BringToFront();
                    // Anti-flicker reveal: there is no single WPF "fully on screen" event, but
                    // CompositionTarget.Rendering ticks once per RENDERED frame. Subscribed after
                    // SetModel, two ticks mean the compositor has presented frames CONTAINING the
                    // model — only then is the PNG dropped. (ContextIdle wasn't enough: it signals
                    // an idle dispatcher, not a presented frame.)
                    var ui = (home.Control as System.Windows.Forms.Integration.ElementHost)?.Child;
                    if (ui != null)
                    {
                        // Reveal = FIRST of: 7 presented WPF frames (CompositionTarget.Rendering), or a
                        // 400 ms timer fallback — so the swap can never be lost to a quiet compositor.
                        bool revealed = false;
                        void Reveal(string via)
                        {
                            if (revealed || _token != token || IsDisposed) return;
                            revealed = true;
                            _pic.Visible = false;
                            Console.WriteLine("[model3d] reveal via " + via);
                        }
                        var fallback = new System.Windows.Forms.Timer { Interval = 400 };
                        fallback.Tick += (_, _) => { fallback.Stop(); fallback.Dispose(); Reveal("timer"); };
                        fallback.Start();
                        ui.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            int frames = 0;
                            EventHandler? h = null;
                            h = (_, _) =>
                            {
                                if (++frames < 7) return;   // a few presented frames (~120 ms @60fps) — fully covered by the PNG
                                System.Windows.Media.CompositionTarget.Rendering -= h;
                                try
                                {
                                    if (IsDisposed || !IsHandleCreated) return;
                                    BeginInvoke((Action)(() => Reveal("frames")));
                                }
                                catch { }
                            };
                            System.Windows.Media.CompositionTarget.Rendering += h;
                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                    else _pic.Visible = false;
                }
                if (!_hasContent) { _hasContent = true; _expand.Visible = true; PlaceBadge(); ContentChanged?.Invoke(); }
            }));
        }
        catch { thumb?.Dispose(); }
    }

    private void SetThumb(Image? img)
    {
        _pic.Snapshot = img;   // SnapshotBox disposes the previous one
    }

    // Mouse-drag orbits, wheel zooms (WPF Preview events on the ElementHost child; WinForms fallback kept).
    // Drag = IMMEDIATE rotation (1:1 pointer tracking — the animated ease stuttered), 0.25°/px (÷2 vs the
    // editor's historical feel, which was too twitchy), and the NATURAL direction: drag right → the front
    // face follows the pointer right (yaw+ brings the left side toward the camera).
    internal static void WireOrbit(Control host, Platforms.OrbitController orbit,
                                   Action? doubleClicked = null)   // shared with Model3dFullscreen
    {
        const double Sens = 12.0;   // px per 7.5°-unit → 0.625°/px (~145 px drag = 90°)
        if (host is System.Windows.Forms.Integration.ElementHost eh && eh.Child is System.Windows.UIElement ui)
        {
            bool wd = false; System.Windows.Point wl = default;
            ui.PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ClickCount == 2)
                {
                    wd = false;
                    ui.ReleaseMouseCapture();
                    doubleClicked?.Invoke();
                    e.Handled = true;
                    return;
                }
                wd = true;
                wl = e.GetPosition(ui);
                ui.CaptureMouse();
                e.Handled = true;
            };
            ui.PreviewMouseUp += (_, e) => { wd = false; ui.ReleaseMouseCapture(); e.Handled = true; };
            ui.PreviewMouseMove += (_, e) =>
            {
                if (!wd) return;
                var p = e.GetPosition(ui); double dx = p.X - wl.X, dy = p.Y - wl.Y; wl = p;
                orbit.OrbitImmediate(dx / Sens, dy / Sens);
                e.Handled = true;
            };
            ui.PreviewMouseWheel += (_, e) => { orbit.Zoom(e.Delta); e.Handled = true; };
        }
        bool dragging = false; int lx = 0, ly = 0;
        host.MouseDown += (_, e) => { dragging = true; lx = e.X; ly = e.Y; };
        host.MouseUp += (_, _) => dragging = false;
        host.MouseMove += (_, e) => { if (!dragging) return; int dx = e.X - lx, dy = e.Y - ly; lx = e.X; ly = e.Y; orbit.OrbitImmediate(dx / Sens, dy / Sens); };
        host.MouseWheel += (_, e) => orbit.Zoom(e.Delta);
        // ElementHost's WPF child owns mouse input (handled above). Wiring the WinForms double-click too
        // can deliver the same gesture twice on some framework versions and reopen fullscreen on exit.
        if (host is not System.Windows.Forms.Integration.ElementHost)
            host.MouseDoubleClick += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                dragging = false;
                doubleClicked?.Invoke();
            };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { SetThumb(null); } catch { }
            try { _home?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

/// <summary>The little ⤢ chip (LB parity): expand-to-fullscreen on the detail block.
/// Owner-drawn dark chip with the diagonal-arrows glyph (Segoe UI Symbol).</summary>
internal sealed class Model3dExpandBadge : Control
{
    private bool _hover;

    public Model3dExpandBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(30, 30);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var r = new System.Drawing.Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new System.Drawing.SolidBrush(_hover ? Color.FromArgb(52, 52, 58) : Color.FromArgb(34, 34, 38)))
            g.FillRectangle(bg, r);
        using (var bd = new System.Drawing.Pen(Color.FromArgb(_hover ? 160 : 90, 255, 255, 255)))
            g.DrawRectangle(bd, r);
        using var f = new Font("Segoe UI Symbol", 11f);
        TextRenderer.DrawText(g, "⤢", f, ClientRectangle,
            _hover ? Color.White : Color.FromArgb(210, 210, 214),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
