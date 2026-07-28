// Headless render harness for iterating on the 3D case geometry without the GUI/core. Builds a jewel-case
// model straight from HomeModel3d.BuildModel (art forced via an image-override map, so no MediaResolver /
// LaunchBox core is needed) and renders it to a PNG at a chosen yaw/pitch/distance. Wired to the
// --render-jewel CLI command. Dev-only; not shipped behaviour.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;

namespace LbApiHost.Tools;

internal static class JewelRenderProbe
{
    private static byte _ambient = 0x33;
    private static bool _fillLight;

    // usage: --render-jewel <out.png> <yawDeg> <pitchDeg> [distance] [WxH]
    public static int Run(string[] args, int idx)
    {
        try
        {
            string outp = args[idx + 1];
            double yaw = ParseD(args, idx + 2, 30);
            double pitch = ParseD(args, idx + 3, 8);
            double dist = ParseD(args, idx + 4, 2.6);
            int w = 900, h = 1200;
            if (args.Length > idx + 5 && args[idx + 5].Contains('x'))
            {
                var p = args[idx + 5].Split('x');
                int.TryParse(p[0], out w); int.TryParse(p[1], out h);
            }

            // FF7 PS1 jewel defaults (from model-defaults.json) + real art forced via the override map.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ModelType"] = "jewelCase",
                ["FrontSpineImage"] = @"{Resources}\Sony Playstation",
                ["FrontSpineIsClear"] = "true",
                ["DoubleSpineImageMode"] = "AutomaticDetection",
                ["FullImageSpineWidth"] = "0.143",
                ["FullScanIsLandscape"] = "false",
                ["LogoRotation"] = "0,0,0,",
                ["SpineRotation"] = "0,,0,",
                ["UseFullScanImages"] = "false",
            };
            // Art relative to CWD (= LB root when launched from Core). Falls through to whatever exists.
            var ov = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddIfExists(ov, "front", FirstExisting(
                @"Images\Sony Playstation\Box - Front\France\Final Fantasy 7-01.png",
                @"Images\Sony Playstation\Box - Front\World\Final Fantasy 7-20.png"));
            if (!args.Contains("noscan"))   // "noscan" simulates a game without a Box - Spine scan (preset cap path)
                AddIfExists(ov, "spine", FirstExisting(
                    @"Images\Sony Playstation\Box - Spine\Europe\Final Fantasy 7-01.jpg",
                    @"Images\Sony Playstation\Box - Spine\North America\Final Fantasy 7-20.jpg"));
            if (!args.Contains("noback"))
                AddIfExists(ov, "back", FirstExisting(
                    @"Images\Sony Playstation\Box - Back\France\Final Fantasy 7-01.png",
                    @"Images\Sony Playstation\Box - Back\World\Final Fantasy 7-20.png"));
            AddIfExists(ov, "logo", FirstExisting(
                @"Images\Sony Playstation\Clear Logo\World\Final Fantasy 7-20.png"));
            Host.Platforms.HomeModel3d.DebugSkipPlastic = args.Contains("noplastic");
            Host.Platforms.HomeModel3d.DebugSkipCap = args.Contains("nocap");
            Host.Platforms.HomeModel3d.DebugBackFaces = args.Contains("diagback");
            _fillLight = args.Contains("fill");
            int ai = Array.IndexOf(args, "amb");
            if (ai >= 0 && ai + 1 < args.Length && byte.TryParse(args[ai + 1], System.Globalization.NumberStyles.HexNumber, null, out var ab)) _ambient = ab;
            ApplyMapArg(args, map);   // `map "K=V;K=V"` → spine-mode overrides etc. (empty V = remove)
            if (map.TryGetValue("FrontSpineImage", out var dbgSpec) && dbgSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase))
            {
                string key = dbgSpec.Substring(12);
                string? rgn = Host.Platforms.LbCaseObj.RegionOfImagePath(ov.TryGetValue("front", out var fp) ? fp : null);
                var got = Host.Platforms.LbCaseObj.SpineImage(key, rgn);
                Console.WriteLine($"[jewel-probe] SpineImage(\"{key}\", region={rgn ?? "-"}) -> {(got == null ? "NULL" : got.PixelWidth + "x" + got.PixelHeight)}");
            }
            Console.WriteLine($"[jewel-probe] art: {string.Join(", ", ov.Keys)}  map: {string.Join(";", map.Select(kv => kv.Key + "=" + kv.Value))}  (cwd={Environment.CurrentDirectory})");

            byte[]? png = null;
            var t = new Thread(() =>
            {
                try { png = Render(map, ov, yaw, pitch, dist, w, h); }
                catch (Exception ex) { Console.WriteLine("[jewel-probe] render: " + ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();

            if (png == null) { Console.WriteLine("[jewel-probe] FAILED (null)"); return 1; }
            File.WriteAllBytes(outp, png);
            Console.WriteLine($"[jewel-probe] wrote {outp} ({png.Length / 1024} KB, {w}x{h}, yaw={yaw} pitch={pitch} dist={dist})");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[jewel-probe] " + ex); return 1; }
    }

    // ═══ LB-ORACLE ═══ usage: --render-oracle <out.png> [left right up down] [WxH] [waitMs]
    // Renders LaunchBox's OWN FlowModel (CoreModelHost) headless — the ground-truth image the home-made
    // builders are compared against. Rotation args go straight to FlowModel.RotateModel (LB units).
    // Needs the DEPLOYED exe (LB\Core) so the core dlls + LB root resolve.
    public static int RunOracle(string[] args, int idx)
    {
        try
        {
            string outp = args[idx + 1];
            double l = ParseD(args, idx + 2, 0), r = ParseD(args, idx + 3, 0), u = ParseD(args, idx + 4, 0), dn = ParseD(args, idx + 5, 0);
            int w = 900, h = 1200;
            if (args.Length > idx + 6 && args[idx + 6].Contains('x'))
            { var p = args[idx + 6].Split('x'); int.TryParse(p[0], out w); int.TryParse(p[1], out h); }
            int wait = (int)ParseD(args, idx + 7, 2500);

            byte[]? png = null;
            var t = new Thread(() =>
            {
                try
                {
                    var prev = Host.Platforms.CoreModelHost.Preview.Create();
                    if (prev == null) { Console.WriteLine("[oracle] core unavailable (run the DEPLOYED LB\\Core exe)"); return; }
                    var ui = (prev.Control as System.Windows.Forms.Integration.ElementHost)?.Child as System.Windows.FrameworkElement;
                    if (ui == null) { Console.WriteLine("[oracle] no WPF child"); return; }
                    var map = Host.Platforms.ModelDefaults.TryGet("Sony Playstation", "Sony Playstation")
                              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ApplyMapArg(args, map);   // `map "K=V;K=V"` → overrides (empty V = remove)
                    string title = ArgValue(args, "title") ?? "Final Fantasy 7";
                    Console.WriteLine("[oracle] map: " + string.Join(";", map.Select(kv => kv.Key + "=" + kv.Value)) + "  title=" + title);
                    prev.Redraw(map, title, "Sony Playstation");
                    ui.Measure(new System.Windows.Size(w, h));
                    ui.Arrange(new System.Windows.Rect(0, 0, w, h));
                    Pump(wait);                    // FlowModel loads art ASYNC and rebuilds — let it settle
                    if (args.Contains("dump")) DumpStructure(prev.BuiltGeometry());   // exact quads + materials
                    if (l != 0 || r != 0 || u != 0 || dn != 0) { prev.Rotate(l, r, u, dn); Pump(800); }
                    ui.Measure(new System.Windows.Size(w, h));
                    ui.Arrange(new System.Windows.Rect(0, 0, w, h));
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(ui);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    png = ms.ToArray();
                }
                catch (Exception ex) { Console.WriteLine("[oracle] " + ex); }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            if (png == null) return 1;
            File.WriteAllBytes(outp, png);
            Console.WriteLine($"[oracle] wrote {outp} ({png.Length / 1024} KB, rot {l}/{r}/{u}/{dn}, wait {wait} ms)");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[oracle] " + ex); return 1; }
    }

    // WinForms DoEvents pumps the shared Win32 queue — WPF Dispatcher work (async art, layout) runs too.
    private static void Pump(int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(15); }
    }

    // ═══ LB-ORACLE ═══ dump the built Model3DGroup: every leaf's transformed bounds, uv range and material —
    // the ground-truth structure to compare our builders against (successor of the deleted DumpGroup).
    private static void DumpStructure(Model3DGroup? root)
    {
        if (root == null) { Console.WriteLine("[oracle-dump] no built geometry"); return; }
        int leaf = 0;
        void Walk(Model3D m, Matrix3D parent, string path)
        {
            var local = (m.Transform?.Value ?? Matrix3D.Identity) * parent;
            if (m is Model3DGroup g)
            {
                for (int i = 0; i < g.Children.Count; i++) Walk(g.Children[i], local, path + "/" + i);
                return;
            }
            if (m is not GeometryModel3D gm || gm.Geometry is not MeshGeometry3D mesh || mesh.Positions.Count == 0) return;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p0 in mesh.Positions)
            {
                var p = local.Transform(p0);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            string uv = "-";
            if (mesh.TextureCoordinates.Count > 0)
            {
                double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
                foreach (var t in mesh.TextureCoordinates)
                { u0 = Math.Min(u0, t.X); u1 = Math.Max(u1, t.X); v0 = Math.Min(v0, t.Y); v1 = Math.Max(v1, t.Y); }
                uv = $"u[{u0:0.###}..{u1:0.###}] v[{v0:0.###}..{v1:0.###}]";
            }
            Console.WriteLine($"[oracle-dump] leaf{leaf++} {path}  verts={mesh.Positions.Count} tris={mesh.TriangleIndices.Count / 3}");
            Console.WriteLine($"[oracle-dump]   X[{minX:0.####}..{maxX:0.####}] Y[{minY:0.####}..{maxY:0.####}] Z[{minZ:0.####}..{maxZ:0.####}]  uv {uv}");
            Console.WriteLine($"[oracle-dump]   mat={Describe(gm.Material)}  back={Describe(gm.BackMaterial)}");
        }
        Walk(root, Matrix3D.Identity, "");
        Console.WriteLine($"[oracle-dump] total {leaf} leaves");
    }

    private static string Describe(Material? m)
    {
        switch (m)
        {
            case null: return "null";
            case MaterialGroup mg: return "Group[" + string.Join(", ", mg.Children.Select(Describe)) + "]";
            case DiffuseMaterial dm: return "Diffuse(" + DescribeBrush(dm.Brush) + ")";
            case SpecularMaterial sp: return $"Specular({DescribeBrush(sp.Brush)}, pow={sp.SpecularPower:0.#})";
            case EmissiveMaterial em: return "Emissive(" + DescribeBrush(em.Brush) + ")";
            default: return m.GetType().Name;
        }
    }

    private static string DescribeBrush(System.Windows.Media.Brush? b)
    {
        switch (b)
        {
            case null: return "null";
            case SolidColorBrush sb: return $"#{sb.Color.A:X2}{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}(op={sb.Opacity:0.##})";
            case ImageBrush ib:
                var src = ib.ImageSource as BitmapSource;
                return $"Image({src?.PixelWidth}x{src?.PixelHeight}, stretch={ib.Stretch}, viewbox={ib.Viewbox}, tile={ib.TileMode})";
            case VisualBrush vb:
                string vis = vb.Visual is System.Windows.FrameworkElement fe
                    ? $"{fe.GetType().Name} {fe.Width:0.#}x{fe.Height:0.#} [{DescribeVisualTree(fe)}]"
                    : vb.Visual?.GetType().Name ?? "null";
                return $"Visual({vis}, stretch={vb.Stretch})";
            default: return b.GetType().Name + $"(op={b.Opacity:0.##})";
        }
    }

    // Walk a composed visual (the VisualBrush content) and describe every child: Images (source size,
    // stretch, alignment, layout transform, grid cell), TextBlocks, nested panels, backgrounds.
    private static string DescribeVisualTree(System.Windows.DependencyObject o)
    {
        var parts = new List<string>();
        void Add(System.Windows.DependencyObject d)
        {
            switch (d)
            {
                case System.Windows.Controls.Image im:
                    var s = im.Source as BitmapSource;
                    string cell = "";
                    try
                    {
                        int col = System.Windows.Controls.Grid.GetColumn(im), row = System.Windows.Controls.Grid.GetRow(im);
                        if (col != 0 || row != 0) cell = $" cell={col},{row}";
                    }
                    catch { }
                    string tf = im.LayoutTransform is RotateTransform rt ? $" rot={rt.Angle}" : "";
                    string rtf = im.RenderTransform is System.Windows.Media.Transform rr && !rr.Value.IsIdentity ? $" rtf={rr.Value}" : "";
                    parts.Add($"Img({s?.PixelWidth}x{s?.PixelHeight} st={im.Stretch} ha={im.HorizontalAlignment} va={im.VerticalAlignment}" +
                              $" w={im.Width:0.#} h={im.Height:0.#} m={im.Margin}{cell}{tf}{rtf} op={im.Opacity:0.##})");
                    break;
                case System.Windows.Controls.TextBlock tb:
                    parts.Add($"Text(\"{tb.Text}\" fg={(tb.Foreground as SolidColorBrush)?.Color})");
                    break;
                case System.Windows.Controls.Panel pn:
                    string bg = pn is { } && pn.Background is SolidColorBrush pb ? $"#{pb.Color.A:X2}{pb.Color.R:X2}{pb.Color.G:X2}{pb.Color.B:X2}" : pn.Background?.GetType().Name ?? "-";
                    string cols = pn is System.Windows.Controls.Grid gg && gg.ColumnDefinitions.Count > 0
                        ? " cols=" + string.Join("|", gg.ColumnDefinitions.Select(c => c.Width.ToString())) : "";
                    parts.Add($"{pn.GetType().Name}(bg={bg}{cols})");
                    foreach (object c in pn.Children) if (c is System.Windows.DependencyObject cd) Add(cd);
                    break;
                case System.Windows.Controls.Decorator dec:
                    parts.Add(dec.GetType().Name);
                    if (dec.Child != null) Add(dec.Child);
                    break;
                default:
                    parts.Add(d.GetType().Name);
                    foreach (object c in System.Windows.LogicalTreeHelper.GetChildren(d))
                        if (c is System.Windows.DependencyObject cd) Add(cd);
                    break;
            }
        }
        Add(o);
        return string.Join(" ", parts);
    }

    /// <summary>`map "K=V;K=V"` CLI arg → merge into <paramref name="map"/> (empty V removes the key).</summary>
    private static void ApplyMapArg(string[] args, Dictionary<string, string> map)
    {
        string? extra = ArgValue(args, "map");
        if (string.IsNullOrEmpty(extra)) return;
        foreach (var kv in extra!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            string k = kv.Substring(0, eq).Trim(), v = kv.Substring(eq + 1);
            if (v.Length == 0) map.Remove(k); else map[k] = v;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // usage: --render-glb <glb> <out.png> <yawDeg> <pitchDeg> [distance] [WxH] — renders an ALREADY-BAKED
    // GLB (flattened ImageBrush textures → faithful to the detail pane, unlike live VisualBrush materials).
    public static int RunGlb(string[] args, int idx)
    {
        try
        {
            string glb = args[idx + 1], outp = args[idx + 2];
            double yaw = ParseD(args, idx + 3, 30), pitch = ParseD(args, idx + 4, 8), dist = ParseD(args, idx + 5, 2.6);
            int w = 900, h = 1200;
            if (args.Length > idx + 6 && args[idx + 6].Contains('x'))
            { var p = args[idx + 6].Split('x'); int.TryParse(p[0], out w); int.TryParse(p[1], out h); }

            byte[]? png = null;
            var t = new Thread(() =>
            {
                try
                {
                    var model = Host.Model3d.GlbFile.LoadModel(glb);
                    if (model == null) { Console.WriteLine("[jewel-probe] GLB load null: " + glb); return; }
                    png = RenderModel(model, yaw, pitch, dist, w, h);
                }
                catch (Exception ex) { Console.WriteLine("[jewel-probe] glb render: " + ex); }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            if (png == null) return 1;
            File.WriteAllBytes(outp, png);
            Console.WriteLine($"[jewel-probe] wrote {outp} from {Path.GetFileName(glb)} ({png.Length / 1024} KB, yaw={yaw} pitch={pitch} dist={dist})");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[jewel-probe] " + ex); return 1; }
    }

    private static byte[]? Render(Dictionary<string, string> map, Dictionary<string, string> ov,
                                  double yaw, double pitch, double dist, int w, int h)
    {
        // BakeRuntimeModel flattens VisualBrush composites to frozen ImageBrush textures (they render headless,
        // unlike the live VisualBrush materials BuildModel emits) — same geometry, faithful to what's shown.
        // Bounds dump (geometry diagnosis): the LIVE model keeps its child structure (paper quads + plastic
        // group) so we can compare the paper-insert depth to the plastic-shell depth.
        var live = Host.Platforms.HomeModel3d.BuildModel(map, "Final Fantasy VII", "Sony Playstation", ov);
        if (live is Model3DGroup lg)
        {
            Console.WriteLine($"[jewel-probe] model bounds: {Fmt(live.Bounds)}  ({lg.Children.Count} children)");
            for (int i = 0; i < lg.Children.Count; i++)
                Console.WriteLine($"[jewel-probe]   child[{i}] {lg.Children[i].GetType().Name} bounds {Fmt(lg.Children[i].Bounds)}");
        }

        // "glb" arg: exercise the EXACT detail-pane path — full bake to a temp GLB, then reload — instead of
        // the runtime flatten. This is what caught the doubleSided round-trip bug (live fine, GLB broken).
        if (Environment.GetCommandLineArgs().Contains("glb"))
        {
            var bakedOut = Host.Model3d.Model3dBaker.Bake(map, "Final Fantasy VII", "Sony Playstation", ov);
            if (bakedOut == null) { Console.WriteLine("[jewel-probe] Bake returned null"); return null; }
            var (meshes, mats, thumb) = bakedOut.Value;
            string tmp = Path.Combine(Path.GetTempPath(), "jewelprobe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".glb");
            try
            {
                Host.Model3d.GlbFile.Write(tmp, meshes, mats, thumb, new Host.Model3d.GlbInfo("probe", "", "Sony Playstation", "Final Fantasy VII", Host.Model3d.Model3dCache.BakerVersion, "probe"));
                var reloaded = Host.Model3d.GlbFile.LoadModel(tmp);
                if (reloaded == null) { Console.WriteLine("[jewel-probe] GLB reload null"); return null; }
                Console.WriteLine("[jewel-probe] GLB round-trip path (bake → write → reload)");
                return RenderModel(reloaded, yaw, pitch, dist, w, h);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        var model = Host.Model3d.Model3dBaker.BakeRuntimeModel(map, "Final Fantasy VII", "Sony Playstation", ov);
        if (model == null) { Console.WriteLine("[jewel-probe] BakeRuntimeModel returned null"); return null; }
        return RenderModel(model, yaw, pitch, dist, w, h);
    }

    private static string Fmt(Rect3D b)
        => $"X[{b.X:0.####}..{b.X + b.SizeX:0.####}] Y[{b.Y:0.####}..{b.Y + b.SizeY:0.####}] Z[{b.Z:0.####}..{b.Z + b.SizeZ:0.####}]  (W={b.SizeX:0.####} H={b.SizeY:0.####} D={b.SizeZ:0.####})";

    private static byte[]? RenderModel(Model3D model, double yaw, double pitch, double dist, int w, int h)
    {
        var tg = new Transform3DGroup();
        tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), yaw)));
        tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), pitch)));
        var viewport = new Viewport3D
        {
            Width = w, Height = h,
            Camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0, dist),
                LookDirection = new Vector3D(0, 0, -1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 50, NearPlaneDistance = 0.001, FarPlaneDistance = 20,
            },
        };
        byte amb = _ambient;
        viewport.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Color.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(0, -0.5, -1)) });
        viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(amb, amb, amb)) });
        if (_fillLight)   // fill from the left/front to test lighting the spine (-X faces)
            viewport.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Color.FromRgb(0xAA, 0xAA, 0xAA), new Vector3D(1, -0.3, -0.6)) });
        viewport.Children.Add(new ModelVisual3D { Content = model, Transform = tg });
        viewport.Measure(new System.Windows.Size(w, h));
        viewport.Arrange(new System.Windows.Rect(0, 0, w, h));

        // Grey backdrop (like the detail pane) so transparent gaps read as they do live, not as black.
        var grid = new Grid { Width = w, Height = h, Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)) };
        grid.Children.Add(viewport);
        grid.Measure(new System.Windows.Size(w, h));
        grid.Arrange(new System.Windows.Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(grid);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static double ParseD(string[] a, int i, double def)
        => i < a.Length && double.TryParse(a[i], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

    private static string? FirstExisting(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }

    private static void AddIfExists(Dictionary<string, string> ov, string slot, string? path)
    {
        if (!string.IsNullOrEmpty(path)) ov[slot] = path!;
    }
}
