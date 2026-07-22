// Home-made 3D box renderer — the second preview zone. Goal: reproduce LaunchBox's 3D case EXACTLY with our
// OWN code (plan B), using the live LB preview above it as the pixel-perfect oracle. This is all pure WPF
// Media3D (UseWPF=true) — no reflection, no obfuscated core: the objects LB builds are standard framework types
// we can read/clone directly.
//
// ITERATIVE STRATEGY (per the user): start by CAPTURING LB's built scene (geometry + camera + lights) and
// rendering it in our own Viewport3D — this validates our camera/light/viewport pipeline matches LB. Then
// progressively replace the captured MeshGeometry3D + materials with OUR OWN procedural generation (box / dvd /
// jewel case scaled by ModelSize, spine image, cover texture…), comparing side-by-side until identical.
//
// Current stage: CAPTURE (iteration 1) — clone LB's Model3DGroup + camera + lights into our viewport.

#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LbApiHost.Host.Platforms;

internal sealed class HomeModel3d : IDisposable
{
    private readonly ElementHost _host;
    private readonly Viewport3D _viewport;
    private readonly ModelVisual3D _modelHost = new();
    private readonly ModelVisual3D _lightHost = new();

    public System.Windows.Forms.Control Control => _host;

    public HomeModel3d()
    {
        _viewport = new Viewport3D { ClipToBounds = true };
        _viewport.Children.Add(_lightHost);
        _viewport.Children.Add(_modelHost);
        _host = new ElementHost { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(28, 28, 30), Child = _viewport };
    }

    /// <summary>ITERATION 1 — capture LB's scene and mirror it in our viewport (clone geometry + camera +
    /// lights). Later iterations will build the geometry ourselves and only borrow the camera/lights until
    /// those are reproduced too.</summary>
    public void CaptureFrom(CoreModelHost.Preview? lb)
    {
        try
        {
            _modelHost.Content = null;
            _lightHost.Children.Clear();
            if (lb == null) return;

            var geom = lb.BuiltGeometry();
            var (cam, lights) = lb.Scene();

            // Camera: clone LB's exact camera so the framing matches.
            if (cam != null) _viewport.Camera = (Camera)cam.Clone();

            // Lights: clone each; if LB had none yet, add a sane default so our capture isn't black.
            if (lights.Count > 0)
                foreach (var l in lights) _lightHost.Children.Add(new ModelVisual3D { Content = (Model3D)l.Clone() });
            else
            {
                _lightHost.Children.Add(new ModelVisual3D { Content = new AmbientLight(System.Windows.Media.Color.FromRgb(80, 80, 80)) });
                _lightHost.Children.Add(new ModelVisual3D { Content = new DirectionalLight(System.Windows.Media.Color.FromRgb(220, 220, 220), new Vector3D(-1, -1, -3)) });
            }

            // Geometry: clone LB's built Model3DGroup (freezable → deep clone).
            if (geom != null) { _modelHost.Content = geom.Clone(); if (DumpStructure) DumpGroup(geom); }
        }
        catch (Exception ex) { Console.WriteLine("[homemodel] capture: " + ex.Message); }
    }

    /// <summary>Mouse-drag rotate — same feel as the LB preview (rotate the model host's transform).</summary>
    public void Rotate(double dx, double dy)
    {
        try
        {
            var group = _modelHost.Transform as Transform3DGroup ?? new Transform3DGroup();
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), dx * 0.5)));
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), dy * 0.5)));
            _modelHost.Transform = group;
        }
        catch { }
    }

    // ── structure capture (for reproducing LB's geometry procedurally) ──
    public static bool DumpStructure = false;

    private static void DumpGroup(Model3DGroup g)
    {
        var sb = new System.Text.StringBuilder();
        void L(string s) => sb.AppendLine(s);
        L("=== LB Model3DGroup structure ===");
        L("group.Transform = " + Describe(g.Transform));
        L("group.Children = " + g.Children.Count);
        DumpChildren(g.Children, L, 0);
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "model3d-structure.log"), sb.ToString()); } catch { }
        DumpStructure = false;
    }

    private static void DumpChildren(Model3DCollection children, Action<string> L, int depth)
    {
        string ind = new string(' ', depth * 2);
        foreach (var m in children)
        {
            if (m is Model3DGroup sub) { L($"{ind}Group (Transform={Describe(sub.Transform)}, children={sub.Children.Count})"); DumpChildren(sub.Children, L, depth + 1); }
            else if (m is GeometryModel3D gm)
            {
                var mesh = gm.Geometry as MeshGeometry3D;
                L($"{ind}GeometryModel3D  positions={mesh?.Positions.Count} tris={(mesh?.TriangleIndices.Count ?? 0) / 3} tex={mesh?.TextureCoordinates.Count} normals={mesh?.Normals.Count}");
                L($"{ind}  Material   = {DescribeMaterial(gm.Material)}");
                L($"{ind}  BackMat    = {DescribeMaterial(gm.BackMaterial)}");
                L($"{ind}  Transform  = {Describe(gm.Transform)}");
                if (mesh != null && mesh.Positions.Count > 0)
                {
                    var b = mesh.Bounds;
                    L($"{ind}  Bounds     = X[{b.X:0.###}..{b.X + b.SizeX:0.###}] Y[{b.Y:0.###}..{b.Y + b.SizeY:0.###}] Z[{b.Z:0.###}..{b.Z + b.SizeZ:0.###}]");
                }
            }
            else if (m is Light lt) L($"{ind}Light {lt.GetType().Name} color={(lt as dynamic)?.Color}");
            else L($"{ind}{m.GetType().Name}");
        }
    }

    private static string DescribeMaterial(Material? m)
    {
        switch (m)
        {
            case null: return "null";
            case MaterialGroup mg: return "Group[" + string.Join("+", System.Linq.Enumerable.Select(mg.Children, DescribeMaterial)) + "]";
            case DiffuseMaterial dm: return "Diffuse(" + DescribeBrush(dm.Brush) + ")";
            case SpecularMaterial sm: return "Specular(" + DescribeBrush(sm.Brush) + ", pow=" + sm.SpecularPower + ")";
            case EmissiveMaterial em: return "Emissive(" + DescribeBrush(em.Brush) + ")";
            default: return m.GetType().Name;
        }
    }

    private static string DescribeBrush(System.Windows.Media.Brush? b) => b switch
    {
        null => "null",
        SolidColorBrush s => "Solid " + s.Color,
        ImageBrush img => "Image " + (img.ImageSource as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth + "x" + (img.ImageSource as System.Windows.Media.Imaging.BitmapSource)?.PixelHeight + " tile=" + img.TileMode + " stretch=" + img.Stretch,
        _ => b.GetType().Name,
    };

    private static string Describe(Transform3D? t) => t switch
    {
        null => "null",
        Transform3DGroup g => "Group[" + string.Join(",", System.Linq.Enumerable.Select(g.Children, Describe)) + "]",
        TranslateTransform3D tr => $"Translate({tr.OffsetX:0.###},{tr.OffsetY:0.###},{tr.OffsetZ:0.###})",
        ScaleTransform3D sc => $"Scale({sc.ScaleX:0.###},{sc.ScaleY:0.###},{sc.ScaleZ:0.###})",
        RotateTransform3D => "Rotate",
        MatrixTransform3D => "Matrix",
        _ => t.GetType().Name,
    };

    public void Dispose() { try { _host.Dispose(); } catch { } }
}
