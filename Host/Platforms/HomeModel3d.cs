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
    public Viewport3D Viewport => _viewport;

    public HomeModel3d()
    {
        _viewport = new Viewport3D { ClipToBounds = true };
        _viewport.Children.Add(_lightHost);
        _viewport.Children.Add(_modelHost);
        _host = new ElementHost { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(28, 28, 30), Child = _viewport };
    }

    /// <summary>Reproduce LB's model in our viewport. Lights are always cloned from LB (until reproduced).
    /// Geometry: for types we've reproduced (box) we BUILD IT OURSELVES (borrowing LB's face materials for now);
    /// for the rest we clone LB's group so the zone still shows something to compare against.</summary>
    public void CaptureFrom(CoreModelHost.Preview? lb, System.Collections.Generic.Dictionary<string, string>? map)
    {
        try
        {
            _modelHost.Content = null;
            _lightHost.Children.Clear();
            if (lb == null) return;

            var geom = lb.BuiltGeometry();
            var (_, lights) = lb.Scene();

            // Camera is owned by the shared OrbitController (not set here) so both zones stay in sync.
            if (lights.Count > 0)
                foreach (var l in lights) _lightHost.Children.Add(new ModelVisual3D { Content = (Model3D)l.Clone() });
            else
            {
                _lightHost.Children.Add(new ModelVisual3D { Content = new AmbientLight(System.Windows.Media.Color.FromRgb(80, 80, 80)) });
                _lightHost.Children.Add(new ModelVisual3D { Content = new DirectionalLight(System.Windows.Media.Color.FromRgb(220, 220, 220), new Vector3D(-1, -1, -3)) });
            }

            if (geom == null) return;
            if (DumpStructure) DumpGroup(geom);

            string type = map != null && map.TryGetValue("ModelType", out var t) ? t : "";
            Model3D? own = type == "box" ? BuildBox(geom) : null;   // reproduced types build their own geometry
            _modelHost.Content = own ?? geom.Clone();               // else clone LB's (comparison fallback)
        }
        catch (Exception ex) { Console.WriteLine("[homemodel] capture: " + ex.Message); }
    }

    // ── ITERATION 2: procedural BOX (own geometry). LB's box = a 6-face rectangular box centred at origin,
    // dims from ModelSize (W×H×D). Faces: front(+Z)/left(-X)/right(+X)/top(+Y) carry the wrapped cover art
    // (VisualBrush); back(-Z)/bottom(-Y) are a solid dark. We rebuild the 6 quads ourselves at LB's exact dims
    // (read from LB's bounds) and, for now, reuse LB's per-face materials (compositing our own art comes later).
    private static Model3D? BuildBox(Model3DGroup lb)
    {
        // LB's box dims from its overall bounds (symmetric about origin).
        var b = lb.Bounds;
        if (b.IsEmpty) return null;
        double hw = b.SizeX / 2, hh = b.SizeY / 2, hd = b.SizeZ / 2;

        // Grab LB's material per face orientation (by each quad's constant axis + sign).
        var mats = new System.Collections.Generic.Dictionary<string, Material>(StringComparer.Ordinal);
        CollectFaceMaterials(lb, mats);

        var grp = new Model3DGroup();
        // face key → (centre offset dir, u dir, v dir) with outward normal.
        void Face(string key, Point3D o, Vector3D u, Vector3D v)
        {
            var mesh = new MeshGeometry3D();
            // o = bottom-left, u = to bottom-right, v = to top-left. LB's UV convention is V-DOWN (top row = 0),
            // so bottom-left = (0,1), bottom-right = (1,1), top-right = (1,0), top-left = (0,0).
            mesh.Positions.Add(o);           // bottom-left
            mesh.Positions.Add(o + u);       // bottom-right
            mesh.Positions.Add(o + u + v);   // top-right
            mesh.Positions.Add(o + v);       // top-left
            mesh.TextureCoordinates.Add(new System.Windows.Point(0, 1));
            mesh.TextureCoordinates.Add(new System.Windows.Point(1, 1));
            mesh.TextureCoordinates.Add(new System.Windows.Point(1, 0));
            mesh.TextureCoordinates.Add(new System.Windows.Point(0, 0));
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);
            var mat = mats.TryGetValue(key, out var mm) ? mm : new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x18, 0x1E)));
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat });
        }
        // Corners: outward-facing winding (CCW seen from outside).
        Face("Z+", new Point3D(-hw, -hh, hd), new Vector3D(2 * hw, 0, 0), new Vector3D(0, 2 * hh, 0));   // front
        Face("Z-", new Point3D(hw, -hh, -hd), new Vector3D(-2 * hw, 0, 0), new Vector3D(0, 2 * hh, 0));  // back
        Face("X-", new Point3D(-hw, -hh, -hd), new Vector3D(0, 0, 2 * hd), new Vector3D(0, 2 * hh, 0));  // left
        Face("X+", new Point3D(hw, -hh, hd), new Vector3D(0, 0, -2 * hd), new Vector3D(0, 2 * hh, 0));   // right
        Face("Y+", new Point3D(-hw, hh, hd), new Vector3D(2 * hw, 0, 0), new Vector3D(0, 0, -2 * hd));   // top
        Face("Y-", new Point3D(-hw, -hh, -hd), new Vector3D(2 * hw, 0, 0), new Vector3D(0, 0, 2 * hd));  // bottom
        return grp;
    }

    // Walk LB's group, classify each GeometryModel3D by its constant-axis face and store its Material.
    private static void CollectFaceMaterials(Model3DGroup g, System.Collections.Generic.Dictionary<string, Material> outMats)
    {
        foreach (var m in g.Children)
        {
            if (m is Model3DGroup sub) { CollectFaceMaterials(sub, outMats); continue; }
            if (m is not GeometryModel3D gm || gm.Geometry is not MeshGeometry3D mesh || mesh.Positions.Count == 0 || gm.Material == null) continue;
            var b = mesh.Bounds;
            string? key = b.SizeX < 1e-3 ? (b.X > 0 ? "X+" : "X-")
                        : b.SizeY < 1e-3 ? (b.Y > 0 ? "Y+" : "Y-")
                        : b.SizeZ < 1e-3 ? (b.Z > 0 ? "Z+" : "Z-") : null;
            if (key != null && !outMats.ContainsKey(key)) outMats[key] = gm.Material;
        }
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
        // NOT auto-reset: the last capture (after the probe forces a Model Type) overwrites the log = final state.
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
                    if (mesh.Positions.Count <= 24)   // small mesh (quad/box) → dump exact vertex data to reproduce
                    {
                        for (int vi = 0; vi < mesh.Positions.Count; vi++)
                        {
                            var pos = mesh.Positions[vi];
                            var uv = vi < mesh.TextureCoordinates.Count ? mesh.TextureCoordinates[vi] : default;
                            L($"{ind}    v{vi} pos=({pos.X:0.####},{pos.Y:0.####},{pos.Z:0.####}) uv=({uv.X:0.###},{uv.Y:0.###})");
                        }
                        L($"{ind}    tris=[{string.Join(",", mesh.TriangleIndices)}]");
                    }
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
