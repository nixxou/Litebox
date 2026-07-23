// Rotate/zoom input hub for the 3D preview — drives LiteBox's own renderer (HomeModel3d) the way the real
// LaunchBox behaves: the MODEL rotates under a FIXED camera and FIXED lights (orbiting the camera showed the
// un-lit side of the scene). Angles are expressed in LB's historical 7.5°-units so the probe env values
// (LB_ORBIT_YAW/PITCH) keep their meaning across versions.

#nullable enable

namespace LbApiHost.Host.Platforms;

internal sealed class OrbitController
{
    private const double UnitDegrees = 7.5;

    private HomeModel3d? _home;
    private double _zoom = 1.0;

    public void Attach(HomeModel3d? home) => _home = home;

    /// <summary>Rotate the model by the given 7.5°-unit deltas (animated, ease-out — LB-like feel).</summary>
    public void Orbit(double dYawUnits, double dPitchUnits)
        => _home?.OrbitBy(dYawUnits * UnitDegrees, dPitchUnits * UnitDegrees);

    /// <summary>Wheel zoom: scales the fixed camera's distance.</summary>
    public void Zoom(double wheelDelta)
    {
        double factor = wheelDelta > 0 ? 0.9 : 1.0 / 0.9;
        _zoom = System.Math.Max(0.2, System.Math.Min(10, _zoom * factor));
        _home?.SetZoom(_zoom);
    }
}
