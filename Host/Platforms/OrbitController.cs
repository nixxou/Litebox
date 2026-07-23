// Shared rotate/zoom driving BOTH preview zones (LaunchBox + home-made) identically. Works the way the REAL
// LaunchBox does: dragging rotates the MODEL under a FIXED camera and FIXED lights (user report: orbiting the
// camera instead showed the un-lit side of the scene — rotated views looked much darker than real LaunchBox).
//
// Mechanics: drag → FlowModel.RotateModel on the live zone (LB's own model-rotation API), then the transform LB
// maintains on its ModelVisual3D is CLONED onto the home zone's model — identical rotation by construction, no
// angle bookkeeping drift. The accumulated yaw/pitch is kept only to re-apply the pose after LB rebuilds its
// model (RedrawModel resets both its camera and the rotation). Wheel-zoom moves the fixed camera along its look
// direction on both viewports (lights unaffected).

#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace LbApiHost.Host.Platforms;

internal sealed class OrbitController
{
    private readonly List<Viewport3D> _views = new();
    private CoreModelHost.Preview? _live;
    private HomeModel3d? _home;
    private ProjectionCamera? _baseCam;      // LB's default camera (fixed), zoom scales its distance
    private double _zoom = 1.0;              // multiplier on the camera offset from origin

    private bool _touched;

    /// <summary>Wire the two zones (idempotent).</summary>
    public void Attach(CoreModelHost.Preview? live, HomeModel3d? home) { _live = live; _home = home; }

    public void Add(Viewport3D v) { if (v != null && !_views.Contains(v)) { _views.Add(v); Apply(); } }

    /// <summary>Capture LB's camera as the FIXED base camera. Re-seeds on later calls until the user has
    /// interacted (LB's async art-load rebuild may re-frame its camera).</summary>
    public void SeedFrom(ProjectionCamera? cam, Rect3D bounds)
    {
        if ((_baseCam != null && _touched) || cam == null) return;
        _baseCam = (ProjectionCamera)cam.Clone();
        Apply();
    }

    /// <summary>Rotate the MODEL by the given deltas (degrees) — LB's RotateModel on the live zone; the home
    /// zone shares the animated transform instance so it follows automatically.</summary>
    public void Orbit(double dYawDeg, double dPitchDeg)
    {
        _touched = true;
        // RotateModel(left, right, up, down): positive amounts per direction.
        try { _live?.Rotate(Math.Max(0, -dYawDeg), Math.Max(0, dYawDeg), Math.Max(0, -dPitchDeg), Math.Max(0, dPitchDeg)); } catch { }
        SyncModelTransform();
    }

    /// <summary>Copy LB's model transform onto the home zone (call after any live rotation or re-capture).</summary>
    public void SyncModelTransform()
    {
        try
        {
            var t = _live?.ModelVisualTransform;
            if (Environment.GetEnvironmentVariable("LB_ORBIT_TRACE") == "1")
            {
                Console.WriteLine("[orbit] live model transform = " + (t == null ? "null" : t.GetType().Name + " " + t.Value));
                var (cam, lights) = _live?.Scene() ?? (null, new List<Model3D>());
                Console.WriteLine("[orbit] live cam pos=" + cam?.Position + " look=" + cam?.LookDirection);
                foreach (var l in lights)
                    if (l is DirectionalLight dl) Console.WriteLine("[orbit] dir light " + dl.Direction + " parentT=" + Describe(l));
                var vp = _live?.Viewport;
                if (vp != null)
                    foreach (var ch in vp.Children)
                        DumpVisual(ch, 1);
            }
            _home?.SetModelTransform(t);
        }
        catch { }
    }

    /// <summary>Called by the capture path after redraws/rebuilds — re-share the (possibly new) live transform
    /// with the home zone. No angle bookkeeping: if LB resets the pose on rebuild, both zones reset together.</summary>
    public void ReapplyRotation() => SyncModelTransform();

    private static string Describe(Model3D m) => m.Transform?.Value.ToString() ?? "-";

    private static void DumpVisual(System.Windows.Media.Media3D.Visual3D v, int depth)
    {
        string ind = new string(' ', depth * 2);
        var mv = v as ModelVisual3D;
        Console.WriteLine($"[orbit]{ind}{v.GetType().Name} T={(mv?.Transform == null ? "null" : mv.Transform.GetType().Name + ":" + mv.Transform.Value)} content={(mv?.Content?.GetType().Name ?? "-")} contentT={(mv?.Content?.Transform?.Value.ToString() ?? "-")}");
        if (mv != null)
            foreach (var c in mv.Children) DumpVisual(c, depth + 1);
    }

    public void Zoom(double wheelDelta)
    {
        _touched = true;
        double factor = wheelDelta > 0 ? 0.9 : 1.0 / 0.9;
        _zoom = Math.Max(0.2, Math.Min(10, _zoom * factor));
        Apply();
    }

    /// <summary>Push the fixed (zoom-scaled) camera to every registered viewport.</summary>
    public void Apply()
    {
        if (_baseCam == null) return;
        var cam = (ProjectionCamera)_baseCam.Clone();
        var pos = cam.Position;
        cam.Position = new Point3D(pos.X * _zoom, pos.Y * _zoom, pos.Z * _zoom);
        foreach (var v in _views)
            try { v.Camera = (Camera)cam.Clone(); } catch { }
    }
}
