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

internal sealed class Model3dBlock : Panel
{
    private readonly PictureBox _pic;
    private Platforms.HomeModel3d? _home;      // created lazily on first model (ElementHost is not free)
    private Platforms.OrbitController? _orbit;
    private int _token;
    private bool _hasContent;

    /// <summary>The block has something to show for the current game — drives its grid row height.</summary>
    public bool HasContent => _hasContent;

    /// <summary>Raised (on the UI thread) when <see cref="HasContent"/> flips — the detail pane relayouts.</summary>
    public Action? ContentChanged;

    public Model3dBlock()
    {
        DoubleBuffered = true;
        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = BackColor };
        Controls.Add(_pic);
        BackColorChanged += (_, _) => _pic.BackColor = BackColor;
    }

    private Platforms.HomeModel3d EnsureHome()
    {
        if (_home == null)
        {
            _home = new Platforms.HomeModel3d();
            _home.Control.Dock = DockStyle.Fill;
            _home.Control.Visible = false;
            _home.SetZoom(Model3dBaker.CameraDistance / 2.0);   // same framing as the baked thumb
            Controls.Add(_home.Control);
            _orbit = new Platforms.OrbitController();
            _orbit.Attach(_home);
            WireOrbit(_home.Control, _orbit);
        }
        return _home;
    }

    /// <summary>Node selected / feature off — collapse and forget the current game.</summary>
    public void Clear()
    {
        _token++;
        SetThumb(null);
        _home?.SetModel(null);
        if (_home != null) _home.Control.Visible = false;
        if (_hasContent) { _hasContent = false; ContentChanged?.Invoke(); }
    }

    /// <summary>Show the 3D case of <paramref name="g"/> (thumb first, live model behind). Non-blocking.</summary>
    public void ShowFor(IGame g)
    {
        int token = ++_token;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var idn = Model3dCache.Resolve(g);
                if (idn == null || !idn.HasArt) { Collapse(token); return; }

                string glb = idn.GlbPath;
                bool existed;
                try { existed = File.Exists(glb); } catch { existed = false; }

                // HIT → snapshot right now (partial read), model behind. MISS → bake first (STA worker,
                // serialized), then the same two-step. Every UI hand-off re-checks the token.
                if (!existed && Model3dCache.Ensure(g) == null) { Collapse(token); return; }
                var thumb = GlbFile.ReadThumb(glb);
                if (thumb != null && _token == token) Apply(token, ThumbImage(thumb), null);

                var model = GlbFile.LoadModel(glb);   // frozen → UI-thread safe
                if (model != null && _token == token) Apply(token, null, model);
                else if (thumb == null && model == null) Collapse(token);
            }
            catch (Exception ex) { Console.WriteLine("[model3d] block: " + ex.Message); Collapse(token); }
        });
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
                    if (_home != null) { _home.SetModel(null); _home.Control.Visible = false; }
                }
                if (model != null)
                {
                    var home = EnsureHome();
                    home.SetPose(Model3dBaker.DefaultYawDeg, Model3dBaker.DefaultPitchDeg);
                    home.SetZoom(Model3dBaker.CameraDistance / 2.0);
                    home.SetModel(model);
                    home.Control.Visible = true;
                    _pic.Visible = false;
                }
                if (!_hasContent) { _hasContent = true; ContentChanged?.Invoke(); }
            }));
        }
        catch { thumb?.Dispose(); }
    }

    private void SetThumb(Image? img)
    {
        var old = _pic.Image;
        _pic.Image = img;
        old?.Dispose();
    }

    // Mouse-drag orbits, wheel zooms (WPF Preview events on the ElementHost child; WinForms fallback kept).
    // Drag = IMMEDIATE rotation (1:1 pointer tracking — the animated ease stuttered), 0.25°/px (÷2 vs the
    // editor's historical feel, which was too twitchy), and the NATURAL direction: drag right → the front
    // face follows the pointer right (yaw+ brings the left side toward the camera).
    private static void WireOrbit(Control host, Platforms.OrbitController orbit)
    {
        const double Sens = 12.0;   // px per 7.5°-unit → 0.625°/px (~145 px drag = 90°)
        if (host is System.Windows.Forms.Integration.ElementHost eh && eh.Child is System.Windows.UIElement ui)
        {
            bool wd = false; System.Windows.Point wl = default;
            ui.PreviewMouseDown += (_, e) => { wd = true; wl = e.GetPosition(ui); ui.CaptureMouse(); e.Handled = true; };
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
