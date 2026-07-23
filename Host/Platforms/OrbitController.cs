// Shared orbit camera driving BOTH preview viewports (LaunchBox + home-made) identically, so the two zones can
// be compared for fidelity while rotating/zooming. Mouse-drag on either zone orbits (yaw/pitch); the wheel
// zooms (distance). One PerspectiveCamera is computed and applied to every registered Viewport3D — perfect sync.
//
// Initialized from LB's own camera (position/target/FOV) so the starting framing matches LB exactly; the target
// is the centre of the built model's bounds. After a redraw resets a viewport's camera, re-Apply() restores it.

#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace LbApiHost.Host.Platforms;

internal sealed class OrbitController
{
    private Point3D _target = new(0, 0, 0);
    private double _distance = 3, _yaw = 0, _pitch = 0, _fov = 45;
    private readonly List<Viewport3D> _views = new();
    private bool _seeded, _touched;

    public void Add(Viewport3D v) { if (v != null && !_views.Contains(v)) { _views.Add(v); Apply(); } }

    /// <summary>Seed the orbit from LB's camera + model bounds so the initial view matches LB. Re-seeds on later
    /// calls as long as the user hasn't orbited/zoomed yet — LB's async art-load rebuilds the model (new size,
    /// new own-camera framing) and the first seed may have captured the provisional bare-box state.</summary>
    public void SeedFrom(ProjectionCamera? cam, Rect3D bounds)
    {
        if ((_seeded && _touched) || cam == null) return;
        if (!bounds.IsEmpty) _target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var toCam = cam.Position - _target;
        _distance = toCam.Length > 0.001 ? toCam.Length : 3;
        // yaw around Y, pitch around X, from the camera offset direction.
        var dir = toCam; dir.Normalize();
        _pitch = Math.Asin(Math.Max(-1, Math.Min(1, dir.Y))) * 180 / Math.PI;
        _yaw = Math.Atan2(dir.X, dir.Z) * 180 / Math.PI;
        if (cam is PerspectiveCamera pc) _fov = pc.FieldOfView;
        _seeded = true;
        Apply();
    }

    public void Orbit(double dYawDeg, double dPitchDeg)
    {
        _touched = true;
        _yaw += dYawDeg;
        _pitch = Math.Max(-89, Math.Min(89, _pitch + dPitchDeg));
        Apply();
    }

    public void Zoom(double wheelDelta)
    {
        _touched = true;
        // Exponential zoom: each notch scales distance by ~0.9 / 1.1.
        double factor = wheelDelta > 0 ? 0.9 : 1.0 / 0.9;
        _distance = Math.Max(0.2, Math.Min(50, _distance * factor));
        Apply();
    }

    /// <summary>Rebuild the camera and push it to every registered viewport.</summary>
    public void Apply()
    {
        var cam = Build();
        foreach (var v in _views)
            try { v.Camera = (Camera)cam.Clone(); } catch { }
    }

    private PerspectiveCamera Build()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        double cp = Math.Cos(pitch);
        var offset = new Vector3D(Math.Sin(yaw) * cp, Math.Sin(pitch), Math.Cos(yaw) * cp) * _distance;
        var pos = _target + offset;
        return new PerspectiveCamera
        {
            Position = pos,
            LookDirection = _target - pos,
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = _fov,
        };
    }
}
