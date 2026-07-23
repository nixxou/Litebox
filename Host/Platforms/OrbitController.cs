// Shared rotate/zoom driving BOTH preview zones identically, the way the REAL LaunchBox does: the MODEL
// rotates under a FIXED camera and FIXED lights (orbiting the camera instead showed the un-lit side — rotated
// views looked far darker than real LaunchBox; user-reported).
//
// The two zones are INDEPENDENT renderers kept in step by equal inputs:
//   live zone : FlowModel.RotateModel — LB's own API. Decoded semantics: parameters are 7.5°-UNITS
//               (left,right,up,down), maintained as Transform3DGroup [RotateY, RotateX] about the origin.
//   home zone : its OWN AxisAngleRotation3D pair + animation (HomeModel3d.OrbitBy) — zero core dependency.
// After LB rebuilds its model (async art load) its pose resets; the capture path calls SyncPose() which reads
// the live pose (plain WPF objects) and snaps the home zone to it — harness-only glue, meaningless without an
// oracle. Wheel-zoom scales the fixed camera distance on the live viewport; the home zone scales its own.

#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace LbApiHost.Host.Platforms;

internal sealed class OrbitController
{
    private const double UnitDegrees = 7.5;   // RotateModel's unit (probe-decoded: 30 units → 225°)

    private readonly List<Viewport3D> _views = new();
    private CoreModelHost.Preview? _live;
    private HomeModel3d? _home;
    private ProjectionCamera? _baseCam;      // LB's default camera (fixed), zoom scales its distance
    private double _zoom = 1.0;
    private bool _touched;

    /// <summary>Wire the two zones (idempotent).</summary>
    public void Attach(CoreModelHost.Preview? live, HomeModel3d? home) { _live = live; _home = home; }

    public void Add(Viewport3D v) { if (v != null && !_views.Contains(v)) { _views.Add(v); Apply(); } }

    /// <summary>Capture LB's camera as the FIXED base camera for the live viewport (the home zone owns its
    /// camera). Re-seeds until the user has interacted.</summary>
    public void SeedFrom(ProjectionCamera? cam, Rect3D bounds)
    {
        if ((_baseCam != null && _touched) || cam == null) return;
        _baseCam = (ProjectionCamera)cam.Clone();
        Apply();
    }

    /// <summary>Rotate the MODEL by the given 7.5°-unit deltas on both zones.</summary>
    public void Orbit(double dYawUnits, double dPitchUnits)
    {
        _touched = true;
        try { _live?.Rotate(Math.Max(0, -dYawUnits), Math.Max(0, dYawUnits), Math.Max(0, -dPitchUnits), Math.Max(0, dPitchUnits)); } catch { }
        _home?.OrbitBy(dYawUnits * UnitDegrees, dPitchUnits * UnitDegrees);
    }

    /// <summary>Snap the home pose to the live model's current yaw/pitch — used by the capture path after LB
    /// rebuilds (its pose resets to 0/0). Reads plain WPF transform objects, no core execution.</summary>
    public void SyncPose()
    {
        try
        {
            double yaw = 0, pitch = 0;
            if (_live?.ModelVisualTransform is Transform3DGroup tg)
                foreach (var c in tg.Children)
                    if (c is RotateTransform3D rt && rt.Rotation is AxisAngleRotation3D ax)
                    {
                        if (ax.Axis.Y != 0) yaw = ax.Angle;
                        else if (ax.Axis.X != 0) pitch = ax.Angle;
                    }
            _home?.SetPose(yaw, pitch);
        }
        catch { }
    }

    public void Zoom(double wheelDelta)
    {
        _touched = true;
        double factor = wheelDelta > 0 ? 0.9 : 1.0 / 0.9;
        _zoom = Math.Max(0.2, Math.Min(10, _zoom * factor));
        _home?.SetZoom(_zoom);
        Apply();
    }

    /// <summary>Push the fixed (zoom-scaled) camera to the registered live viewport(s).</summary>
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
