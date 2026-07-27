// Fullscreen 3D case viewer (the ⤢ badge on the detail block's overlay). Two-step load:
//   1. the CACHED GLB (display-sized textures) shows within ~100 ms — the user can orbit immediately;
//   2. the SAME model is rebuilt LIVE at source texture resolution (Model3dBaker.BakeRuntimeModel on a
//      bake STA worker — same builders, same geometry/shape by construction, only sharper faces) and
//      swapped in silently. The current orbit pose survives the swap (SetModel doesn't touch the pose).
// Drag orbits, wheel zooms (the detail block's exact feel), Esc / the ⤡ badge closes.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Model3d;

internal sealed class Model3dFullscreen : Form
{
    private readonly IGame _game;
    private readonly Platforms.HomeModel3d _home;
    private readonly Platforms.OrbitController _orbit;
    private readonly Label _status;
    private bool _hiResSet;   // the hi-res model landed — a late GLB load must not downgrade it

    public Model3dFullscreen(IGame game)
    {
        _game = game;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        KeyPreview = true;

        _home = new Platforms.HomeModel3d();
        _home.Control.Dock = DockStyle.Fill;
        _home.SetBackground(Color.Black);
        _home.SetPose(Model3dBaker.DefaultYawDeg, Model3dBaker.DefaultPitchDeg);
        Controls.Add(_home.Control);
        _orbit = new Platforms.OrbitController();
        _orbit.Attach(_home);
        Model3dBlock.WireOrbit(_home.Control, _orbit);

        var close = new Model3dExpandBadge { Shrink = true, BackColor = Color.Black };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();

        _status = new Label
        {
            AutoSize = true, Text = "Loading HD textures…",
            ForeColor = Color.FromArgb(150, 150, 155), BackColor = Color.Black,
        };
        Controls.Add(_status);
        _status.BringToFront();

        void Place()
        {
            close.Location = new Point(Math.Max(0, ClientSize.Width - close.Width - 14),
                                       Math.Max(0, ClientSize.Height - close.Height - 14));
            _status.Location = new Point(14, Math.Max(0, ClientSize.Height - _status.Height - 14));
        }
        Resize += (_, _) => { Place(); ApplyFraming(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => { Place(); ApplyFraming(); LoadModels(); };
    }

    // The detail block's framing law with the SCREEN's aspect: distance × max(1, aspect), horizontal
    // FOV 50 — the whole case fits vertically, the wheel continues from this zoom.
    private void ApplyFraming()
    {
        double aspect = ClientSize.Height > 0 ? (double)ClientSize.Width / ClientSize.Height : 16.0 / 9.0;
        _orbit.InitZoom(Model3dBaker.CameraDistanceFor(aspect) / 2.0);
    }

    private void LoadModels()
    {
        // LongRunning: both steps BLOCK (GLB read / the serialized STA bake queue) — never on the pool.
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                var idn = Model3dCache.Resolve(_game);
                if (idn == null || !idn.HasArt) { Post(Close); return; }

                // 1) instant: the cached GLB when present (the badge only shows on a displayed model,
                //    so this hits in practice — a sweep can still race it away, hence the guard).
                try
                {
                    if (File.Exists(idn.GlbPath) && GlbFile.LoadModel(idn.GlbPath) is { } quick)
                        Post(() => { if (!_hiResSet) _home.SetModel(quick); });
                }
                catch { }

                // 2) the hi-res live rebuild (may briefly queue behind bulk bakes — the GLB covers the wait).
                System.Windows.Media.Media3D.Model3D? hi = null;
                try { hi = Model3dBaker.Run(() => Model3dBaker.BakeRuntimeModel(idn.Map, idn.Title, idn.Platform, idn.ImgOv)); }
                catch (Exception ex) { Console.WriteLine("[model3d] hi-res build failed: " + ex.Message); }
                Post(() =>
                {
                    if (hi != null) { _hiResSet = true; _home.SetModel(hi); }
                    _status.Visible = false;
                });
            }
            catch (Exception ex) { Console.WriteLine("[model3d] fullscreen: " + ex.Message); }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
    }

    private void Post(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _home.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
