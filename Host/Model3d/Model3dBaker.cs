// Bakes a game's 3D case model to cacheable data: builds the live WPF model (HomeModel3d.BuildModel),
// flattens transforms into world-space vertices, rasterizes the composed VisualBrush textures to PNG once
// (RenderTargetBitmap), and renders the transparent scene snapshot at the detail block's DEFAULT POSE —
// the thumb that goes first into the GLB so the UI can show the case before the model finishes loading.
//
// Everything WPF here (visual composition, RTB) requires STA, and the model builders were never written
// for concurrency — so ALL bakes run serialized on ONE dedicated background STA thread (Run<T> marshals).
// The scene constants (camera, lights) mirror HomeModel3d's viewport exactly; the POSE constants are shared
// with the detail block so the pre-rendered thumb and the live viewport line up pixel-for-pixel on swap.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;

namespace LbApiHost.Host.Model3d;

internal static class Model3dBaker
{
    // ── the default pose (the "très légère rotation" LB shows instead of a flat box) + shared framing ──
    public const double DefaultYawDeg = 20;     // +yaw turns the LEFT side (spine) toward the camera
    public const double DefaultPitchDeg = 7;    // +pitch tilts the top slightly toward the camera
    public const double CameraDistance = 1.55;  // closer than the editor preview's 2.0 → the case fills the block
    public const int ThumbPx = 640;             // snapshot HEIGHT; width = ThumbPx × TargetAspect()

    // ── single STA bake worker ───────────────────────────────────────────────
    private static readonly BlockingCollection<Action> _queue = new();
    private static Thread[]? _threads;
    private static readonly object _startLock = new();

    /// <summary>Parallel bake workers. WPF requires STA, but nothing requires a SINGLE STA thread —
    /// each bake is independent (own model build, own offscreen render, frozen results), so a small
    /// pool parallelizes the bulk Generate-Media-Cache pass. Two jobs racing the SAME key both bake
    /// (rare: a user select racing the bulk pass) — wasteful but safe, the unique-tmp atomic move wins.</summary>
    public static readonly int WorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private static void EnsureThreads()
    {
        if (_threads != null) return;
        lock (_startLock)
        {
            if (_threads != null) return;
            var arr = new Thread[WorkerCount];
            for (int i = 0; i < arr.Length; i++)
            {
                var t = new Thread(() => { foreach (var job in _queue.GetConsumingEnumerable()) { try { job(); } catch { } } })
                { IsBackground = true, Name = "model3d-bake-" + i };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                arr[i] = t;
            }
            _threads = arr;
        }
    }

    /// <summary>Run <paramref name="job"/> on a bake STA worker and wait for its result.</summary>
    public static T Run<T>(Func<T> job)
    {
        EnsureThreads();
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => { try { tcs.SetResult(job()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>Build + bake + thumb-render a game's model. MUST run on the bake thread (callers go through
    /// <see cref="Run{T}"/>). Null when the model can't be built.</summary>
    public static (List<BakedMesh> meshes, List<BakedMaterial> mats, byte[] thumbPng)? Bake(
        Dictionary<string, string>? map, string title, string platform, Dictionary<string, string>? imgOv = null)
    {
        var model = Platforms.HomeModel3d.BuildModel(map, title, platform, imgOv);
        if (model == null) return null;
        var (meshes, mats) = BakeModel(model);
        if (meshes.Count == 0) return null;
        byte[]? thumb = RenderThumb(model);
        return thumb == null ? null : (meshes, mats, thumb);
    }

    // ── runtime hi-res model (fullscreen viewer) ─────────────────────────────
    // The cached GLB's textures are sized for the ~500px detail pane (1024px cap + JPEG). The fullscreen
    // viewer rebuilds the SAME model live (same builders → same geometry and shape by construction) but
    // keeps textures at their SOURCE resolution: plain image faces reuse the full-res decoded bitmap
    // directly, and COMPOSED faces (the VisualBrush grids, whose fixed layout sizes — W×1000, the dvd
    // wrap's h=600 — used to bound the raster) are rasterized at the scale their largest source image
    // actually provides (capped below). Fully frozen → build on a bake STA worker, render on the UI thread.
    private const int HiResMaxTexPx = 4096;   // rasterization bound (full-scan sheets can be huge)

    /// <summary>Build the game's model with source-resolution textures, flattened + frozen (UI-thread
    /// safe). MUST run on a bake STA worker (callers go through <see cref="Run{T}"/>). Null when the
    /// model can't be built.</summary>
    public static Model3D? BakeRuntimeModel(Dictionary<string, string>? map, string title, string platform,
                                            Dictionary<string, string>? imgOv = null)
    {
        var model = Platforms.HomeModel3d.BuildModel(map, title, platform, imgOv);
        if (model == null) return null;
        var grp = new Model3DGroup();
        void Walk(Model3D m, Matrix3D parent)
        {
            var local = (m.Transform?.Value ?? Matrix3D.Identity) * parent;
            if (m is Model3DGroup g) { foreach (var c in g.Children) Walk(c, local); return; }
            if (m is not GeometryModel3D gm || gm.Geometry is not MeshGeometry3D mesh || mesh.Positions.Count == 0) return;
            var mat = FlattenRuntimeMaterial(gm.Material);
            var m2 = new MeshGeometry3D();
            for (int i = 0; i < mesh.Positions.Count; i++) m2.Positions.Add(local.Transform(mesh.Positions[i]));
            if (mesh.TextureCoordinates.Count == mesh.Positions.Count)
                foreach (var uv in mesh.TextureCoordinates) m2.TextureCoordinates.Add(uv);
            foreach (var ix in mesh.TriangleIndices) m2.TriangleIndices.Add(ix);
            m2.Freeze();
            // Material on both sides, like the GLB loader — the flattened set has no per-face BackMaterial.
            var g2 = new GeometryModel3D { Geometry = m2, Material = mat, BackMaterial = mat };
            g2.Freeze();
            grp.Children.Add(g2);
        }
        Walk(model, Matrix3D.Identity);
        if (grp.Children.Count == 0) return null;
        grp.Freeze();
        return grp;
    }

    // FlattenMaterial's twin for the runtime path: instead of encoding textures to bytes, it produces
    // frozen WPF brushes directly — image faces at source resolution, composed faces via the hi-res
    // rasterizer, solids with the GLB loader's colour-times-opacity rule.
    private static Material FlattenRuntimeMaterial(Material? mat)
    {
        Color col = Color.FromRgb(0x80, 0x80, 0x80);
        double opacity = 1;
        System.Windows.Media.Brush? tex = null;
        void Scan(Material? m)
        {
            switch (m)
            {
                case MaterialGroup mg: foreach (var c in mg.Children) Scan(c); break;
                case DiffuseMaterial dm:
                    switch (dm.Brush)
                    {
                        case SolidColorBrush sb: col = sb.Color; opacity = sb.Opacity * sb.Color.A / 255.0; break;
                        case ImageBrush ib when ib.ImageSource is BitmapSource bs: tex = FrozenImageBrush(bs); break;
                        case VisualBrush vb when vb.Visual is System.Windows.FrameworkElement fe:
                            if (RasterizeVisualHiRes(fe) is { } hi) tex = FrozenImageBrush(hi);
                            break;
                    }
                    break;
            }
        }
        Scan(mat);
        Material res;
        if (tex != null) res = new DiffuseMaterial(tex);
        else
        {
            var sb2 = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), col.R, col.G, col.B));
            sb2.Freeze();
            res = new DiffuseMaterial(sb2);
        }
        res.Freeze();
        return res;
    }

    // Frozen Fill brush over a bitmap. Most sources are already frozen (LoadBitmap freezes); the few
    // derived ones that aren't (CroppedBitmap halves…) get frozen, or copied when they can't be.
    private static ImageBrush FrozenImageBrush(BitmapSource bs)
    {
        if (!bs.IsFrozen)
        {
            if (bs.CanFreeze) bs.Freeze();
            else { var wb = new System.Windows.Media.Imaging.WriteableBitmap(bs); wb.Freeze(); bs = wb; }
        }
        var br = new ImageBrush(bs) { Stretch = Stretch.Fill };
        br.Freeze();
        return br;
    }

    // RasterizeVisual's hi-res twin: same offscreen Measure/Arrange, but the raster runs at the scale k
    // the composite's largest source image can actually feed (each Image child's source pixels vs its
    // arranged size), so a 2100px scan in a 600px-high wrap grid rasters at ~2100px instead of 600.
    private static BitmapSource? RasterizeVisualHiRes(System.Windows.FrameworkElement fe)
    {
        try
        {
            fe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            fe.Arrange(new System.Windows.Rect(fe.DesiredSize));
            var sz = fe.DesiredSize;
            if (sz.Width < 1 || sz.Height < 1) return null;
            double k = 1;
            void WalkImages(System.Windows.DependencyObject o)
            {
                if (o is System.Windows.Controls.Image im && im.Source is BitmapSource b
                    && im.ActualWidth > 0 && im.ActualHeight > 0)
                    k = Math.Max(k, Math.Max(b.PixelWidth / im.ActualWidth, b.PixelHeight / im.ActualHeight));
                foreach (object c in System.Windows.LogicalTreeHelper.GetChildren(o))
                    if (c is System.Windows.DependencyObject d) WalkImages(d);
            }
            WalkImages(fe);
            k = Math.Max(1, Math.Min(k, HiResMaxTexPx / Math.Max(sz.Width, sz.Height)));
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(sz.Width * k), (int)Math.Ceiling(sz.Height * k),
                                             96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(fe);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    // ── bake: walk the model, flatten transforms into vertices, rasterize composite brushes to PNG ──
    private static (List<BakedMesh>, List<BakedMaterial>) BakeModel(Model3D root)
    {
        var meshes = new List<BakedMesh>();
        var materials = new List<BakedMaterial>();
        void Walk(Model3D m, Matrix3D parent)
        {
            var local = (m.Transform?.Value ?? Matrix3D.Identity) * parent;
            if (m is Model3DGroup g) { foreach (var c in g.Children) Walk(c, local); return; }
            if (m is not GeometryModel3D gm || gm.Geometry is not MeshGeometry3D mesh || mesh.Positions.Count == 0) return;
            var baked = FlattenMaterial(gm.Material);
            int matIx = materials.Count;
            materials.Add(baked);
            var pos = new Point3D[mesh.Positions.Count];
            for (int i = 0; i < pos.Length; i++) pos[i] = local.Transform(mesh.Positions[i]);
            Vector3D[] nrm;
            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                nrm = new Vector3D[mesh.Normals.Count];
                for (int i = 0; i < nrm.Length; i++) { var v = local.Transform(mesh.Normals[i]); v.Normalize(); nrm[i] = v; }
            }
            else nrm = Array.Empty<Vector3D>();
            var uv = mesh.TextureCoordinates.Count == mesh.Positions.Count
                ? System.Linq.Enumerable.ToArray(mesh.TextureCoordinates) : Array.Empty<System.Windows.Point>();
            var tri = new int[mesh.TriangleIndices.Count];
            mesh.TriangleIndices.CopyTo(tri, 0);
            meshes.Add(new BakedMesh(pos, nrm, uv, tri, matIx));
        }
        Walk(root, Matrix3D.Identity);
        return (meshes, materials);
    }

    private static BakedMaterial FlattenMaterial(Material? mat)
    {
        Color col = Color.FromRgb(0x80, 0x80, 0x80);
        double opacity = 1;
        byte[]? tex = null;
        void Scan(Material? m)
        {
            switch (m)
            {
                case MaterialGroup mg: foreach (var c in mg.Children) Scan(c); break;
                case DiffuseMaterial dm:
                    switch (dm.Brush)
                    {
                        case SolidColorBrush sb: col = sb.Color; opacity = sb.Opacity * sb.Color.A / 255.0; break;
                        case ImageBrush ib when ib.ImageSource is BitmapSource bs: tex = EncodeTexture(bs); break;
                        case VisualBrush vb when vb.Visual is System.Windows.FrameworkElement fe: tex = RasterizeVisual(fe); break;
                    }
                    break;
            }
        }
        Scan(mat);
        return new BakedMaterial(col, opacity, tex);
    }

    private static byte[]? RasterizeVisual(System.Windows.FrameworkElement fe)
    {
        try
        {
            fe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            fe.Arrange(new System.Windows.Rect(fe.DesiredSize));
            var sz = fe.DesiredSize;
            if (sz.Width < 1 || sz.Height < 1) return null;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(fe);
            return EncodeTexture(rtb);
        }
        catch { return null; }
    }

    // ── texture encoding: sized to DISPLAY need, format by content ───────────────────────────────
    // The v1-v3 bakes embedded every face at its SOURCE resolution, PNG-encoded — a 2140px JPEG scan
    // became a multi-MB lossless PNG inside the GLB. The box renders in a ~500px pane: cap the longest
    // side at MaxTexPx (zoom headroom included) and use JPEG (q85) for OPAQUE faces — the vast
    // majority (fronts/backs/spines/scans). PNG stays for anything actually using alpha (jewel shells).
    private const int MaxTexPx = 1024;

    private static byte[]? EncodeTexture(BitmapSource bs)
    {
        try
        {
            int w = bs.PixelWidth, h = bs.PixelHeight;
            int longest = Math.Max(w, h);
            if (longest > MaxTexPx)
            {
                double k = (double)MaxTexPx / longest;
                var scaled = new TransformedBitmap(bs, new System.Windows.Media.ScaleTransform(k, k));
                scaled.Freeze();
                bs = scaled;
            }
            if (HasRealAlpha(bs)) return EncodePng(bs);
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { try { return EncodePng(bs); } catch { return null; } }
    }

    // True when any pixel is meaningfully transparent. Formats without an alpha channel short-circuit;
    // alpha-capable ones (RTB output is Pbgra32) get a pixel scan — ~ms at the capped size.
    private static bool HasRealAlpha(BitmapSource bs)
    {
        try
        {
            var f = bs.Format;
            if (f != PixelFormats.Bgra32 && f != PixelFormats.Pbgra32 && f != PixelFormats.Prgba64 && f != PixelFormats.Rgba64)
            {
                if (f == PixelFormats.Bgr24 || f == PixelFormats.Rgb24 || f == PixelFormats.Bgr32 || f == PixelFormats.Bgr565) return false;
                var conv = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0); conv.Freeze(); bs = conv;
            }
            else if (f == PixelFormats.Prgba64 || f == PixelFormats.Rgba64)
            {
                var conv = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0); conv.Freeze(); bs = conv;
            }
            int w = bs.PixelWidth, h = bs.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bs.CopyPixels(px, stride, 0);
            for (int i = 3; i < px.Length; i += 4)
                if (px[i] < 250) return true;
            return false;
        }
        catch { return true; }   // unsure → keep PNG (never lose alpha)
    }

    private static byte[] EncodePng(BitmapSource bs)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ── the transparent scene snapshot at the default pose (HomeModel3d's exact scene constants) ──
    // The snapshot is rendered AT THE MAIN MEDIA BOX'S ASPECT with the SAME aspect-compensated camera
    // the live viewport uses (distance × max(1, aspect), horizontal FOV 50 — see Model3dBlock). The
    // PNG and the live model thus come out of the IDENTICAL camera: the swap can't shift by a pixel,
    // and no display-time compensation is needed. (The first attempt kept the PNG square and bent the
    // live camera to match — FOV compensation subtly changed the projection. The PNG follows the
    // viewport now, not the other way around.)

    /// <summary>The media box aspect the whole 3D pipeline targets (ini "Use16:9ForMainScreenshot").
    /// Part of the bake manifest — flipping the option re-bakes.</summary>
    public static double TargetAspect()
    {
        try { return LiteBoxConfig.LoadForExe().Use169ForMainScreenshot ? 16.0 / 9.0 : 2.0 / 3.0; }
        catch { return 16.0 / 9.0; }
    }

    /// <summary>The live-viewport camera distance for <paramref name="aspect"/> — shared by the bake
    /// and Model3dBlock so both cameras are the same object in two places.</summary>
    public static double CameraDistanceFor(double aspect) => CameraDistance * Math.Max(1.0, aspect);

    private static byte[]? RenderThumb(Model3D model)
    {
        try
        {
            double aspect = TargetAspect();
            int w = Math.Max(64, (int)Math.Round(ThumbPx * aspect)), h = ThumbPx;
            var tg = new Transform3DGroup();
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), DefaultYawDeg)));
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), DefaultPitchDeg)));
            var viewport = new System.Windows.Controls.Viewport3D
            {
                Width = w, Height = h,
                Camera = new PerspectiveCamera
                {
                    Position = new Point3D(0, 0, CameraDistanceFor(aspect)),
                    LookDirection = new Vector3D(0, 0, -1),
                    UpDirection = new Vector3D(0, 1, 0),
                    FieldOfView = 50, NearPlaneDistance = 0.001, FarPlaneDistance = 20,
                },
            };
            viewport.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Color.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(0, -0.5, -1)) });
            viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(0x33, 0x33, 0x33)) });
            viewport.Children.Add(new ModelVisual3D { Content = model, Transform = tg });
            viewport.Measure(new System.Windows.Size(w, h));
            viewport.Arrange(new System.Windows.Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(viewport);   // no background element → transparent where the case isn't
            return EncodePng(rtb);
        }
        catch (Exception ex) { Console.WriteLine("[model3d] thumb render: " + ex.Message); return null; }
    }
}
