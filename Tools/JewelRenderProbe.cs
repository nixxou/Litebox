// Headless render harness for iterating on the 3D case geometry without the GUI/core. Builds a jewel-case
// model straight from HomeModel3d.BuildModel (art forced via an image-override map, so no MediaResolver /
// LaunchBox core is needed) and renders it to a PNG at a chosen yaw/pitch/distance. Wired to the
// --render-jewel CLI command. Dev-only; not shipped behaviour.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
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
            AddIfExists(ov, "back", FirstExisting(
                @"Images\Sony Playstation\Box - Back\France\Final Fantasy 7-01.png",
                @"Images\Sony Playstation\Box - Back\World\Final Fantasy 7-20.png"));
            AddIfExists(ov, "logo", FirstExisting(
                @"Images\Sony Playstation\Clear Logo\World\Final Fantasy 7-20.png"));
            Host.Platforms.HomeModel3d.DebugSkipPlastic = args.Contains("noplastic");
            _fillLight = args.Contains("fill");
            int ai = Array.IndexOf(args, "amb");
            if (ai >= 0 && ai + 1 < args.Length && byte.TryParse(args[ai + 1], System.Globalization.NumberStyles.HexNumber, null, out var ab)) _ambient = ab;
            Console.WriteLine($"[jewel-probe] art: {string.Join(", ", ov.Keys)}  skipPlastic={Host.Platforms.HomeModel3d.DebugSkipPlastic}  (cwd={Environment.CurrentDirectory})");

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
                    var map = Host.Platforms.ModelDefaults.TryGet("Sony Playstation", "Sony Playstation");
                    prev.Redraw(map, "Final Fantasy 7", "Sony Playstation");
                    ui.Measure(new System.Windows.Size(w, h));
                    ui.Arrange(new System.Windows.Rect(0, 0, w, h));
                    Pump(wait);                    // FlowModel loads art ASYNC and rebuilds — let it settle
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
