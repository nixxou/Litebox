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

    // ── STA bake workers ─────────────────────────────────────────────────────
    private static readonly BlockingCollection<Action> _queue = new();
    private static bool _started;
    private static readonly object _startLock = new();

    /// <summary>Parallel bake workers. WPF requires STA, but nothing requires a SINGLE STA thread —
    /// each bake is independent (own model build, own offscreen render, frozen results), so a small
    /// pool parallelizes the bulk Generate-Media-Cache pass. Two jobs racing the SAME key both bake
    /// (rare: a user select racing the bulk pass) — wasteful but safe, the unique-tmp atomic move wins.</summary>
    public static readonly int WorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <summary>Bakes a worker runs before it is retired and replaced.
    ///
    /// Touching a Viewport3D or a RenderTargetBitmap makes WPF attach a Dispatcher to the thread, and that
    /// dispatcher — with its MediaContext and EVERY resource rendered through it — is held by a static WPF
    /// table until it is shut down. On a thread that never ends, that is a leak with no ceiling: measured at
    /// ~5 MB per bake, which the GC cannot reclaim because the objects are still rooted. Generating the
    /// whole library (2994 models) that way climbed past 3 GB and ended in a run of
    /// "Insufficient memory to continue" thumb failures.
    ///
    /// A dispatcher cannot be emptied, and a thread whose dispatcher has been shut down cannot serve another
    /// job — so the only lever is to end the thread. Retiring a worker every few bakes caps what one can
    /// accumulate (this many × ~5 MB) against one thread creation, which is nothing next to a bake.</summary>
    private const int BakesPerWorker = 8;

    private static void EnsureThreads()
    {
        if (_started) return;
        lock (_startLock)
        {
            if (_started) return;
            for (int i = 0; i < WorkerCount; i++) StartWorker(i);
            _started = true;
        }
    }

    private static void StartWorker(int slot)
    {
        var t = new Thread(() =>
        {
            int done = 0;
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try { job(); } catch { }
                if (++done >= BakesPerWorker) break;
            }
            // Replace ourselves FIRST: the queue must not lose a worker while this one winds down (a bulk
            // pass keeps it full, and Run() blocks its caller until some worker picks the job up).
            try { StartWorker(slot); } catch (Exception ex) { Console.WriteLine("[model3d] worker respawn: " + ex.Message); }
            // Then let the dispatcher — and everything WPF rendered through it — go.
            try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { }
        })
        { IsBackground = true, Name = "model3d-bake-" + slot };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
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
        Dictionary<string, string>? map, string title, Model3dArt art)
    {
        var model = Platforms.HomeModel3d.BuildModel(map, title, art);
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
    public static Model3D? BakeRuntimeModel(Dictionary<string, string>? map, string title, Model3dArt art)
    {
        var model = Platforms.HomeModel3d.BuildModel(map, title, art);
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
            // Carry the source normals (same rule as the GLB bake, including the mirrored-winding
            // swap): dropping them let WPF derive flat per-face normals, so this path and the cached
            // one shaded the same model differently — the fullscreen viewer's hi-res swap visibly
            // changed the lighting mid-flight.
            if (mesh.Normals.Count == mesh.Positions.Count)
                foreach (var n in mesh.Normals)
                { var v = local.Transform(n); v.Normalize(); m2.Normals.Add(v); }
            if (mesh.TextureCoordinates.Count == mesh.Positions.Count)
                foreach (var uv in mesh.TextureCoordinates) m2.TextureCoordinates.Add(uv);
            if (local.Determinant < 0 && mesh.Normals.Count == mesh.Positions.Count)
                for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
                {
                    m2.TriangleIndices.Add(mesh.TriangleIndices[i]);
                    m2.TriangleIndices.Add(mesh.TriangleIndices[i + 2]);
                    m2.TriangleIndices.Add(mesh.TriangleIndices[i + 1]);
                }
            else foreach (var ix in mesh.TriangleIndices) m2.TriangleIndices.Add(ix);
            m2.Freeze();
            // Both sides for double-sided sources; single-sided faces (spine cap, split back walls) stay
            // single-sided or the cap occludes the scan strips behind it. A DISTINCT back material is
            // flattened on its own — mirroring `mat` onto the back painted the cassette J-card's art on
            // its paper backside, visible through the tray's hinge gap.
            var g2 = new GeometryModel3D { Geometry = m2, Material = mat,
                BackMaterial = gm.BackMaterial == null ? null
                             : ReferenceEquals(gm.BackMaterial, gm.Material) ? mat
                             : FlattenRuntimeMaterial(gm.BackMaterial) };
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
        Color specCol = default; double specPow = 0;
        System.Windows.Media.Brush? tex = null;
        void Scan(Material? m)
        {
            switch (m)
            {
                case MaterialGroup mg: foreach (var c in mg.Children) Scan(c); break;
                case SpecularMaterial sm when sm.Brush is SolidColorBrush ssb:
                    specCol = ssb.Color; specPow = sm.SpecularPower; break;
                case SpecularMaterial sm2 when sm2.Brush is ImageBrush:
                    // an image specular (the wear map) keeps its power, averaged to a plain white sheen
                    specCol = Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF); specPow = sm2.SpecularPower; break;
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
        // Keep the sheen: dropping it left the fullscreen viewer's hi-res model pure diffuse, visibly
        // flatter than the live preview it is supposed to match.
        if (specPow > 0)
        {
            var spb = new SolidColorBrush(specCol); spb.Freeze();
            var grp2 = new MaterialGroup();
            grp2.Children.Add(res);
            grp2.Children.Add(new SpecularMaterial(spb, specPow));
            res = grp2;
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
            // Single-sided source faces (BackMaterial == null: the jewel spine cap, the split back walls)
            // must stay single-sided through the GLB — see BakedMaterial.DoubleSided.
            var baked = FlattenMaterial(gm.Material) with { DoubleSided = gm.BackMaterial != null };
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
            // A MIRRORING transform (negative determinant — the cassette shell is mirrored onto its
            // spine side) reverses the geometric winding, so the faces WPF then treats as front are
            // the ones our outward normals point away from. Lighting reads those normals: diffuse
            // dims and the specular dies outright, which is why the cached model looked flat next to
            // the live one (the runtime bake carries no normals at all, so WPF derives them from the
            // winding and never had the problem). Swapping two indices per triangle puts the winding
            // back in agreement with the normals.
            if (local.Determinant < 0)
                for (int i = 0; i + 2 < tri.Length; i += 3) (tri[i + 1], tri[i + 2]) = (tri[i + 2], tri[i + 1]);
            meshes.Add(new BakedMesh(pos, nrm, uv, tri, matIx));
        }
        Walk(root, Matrix3D.Identity);
        return (meshes, materials);
    }

    private static BakedMaterial FlattenMaterial(Material? mat)
    {
        Color col = Color.FromRgb(0x80, 0x80, 0x80);
        double opacity = 1;
        Color specCol = default; double specPow = 0;
        byte[]? tex = null;
        void Scan(Material? m)
        {
            switch (m)
            {
                case MaterialGroup mg: foreach (var c in mg.Children) Scan(c); break;
                case SpecularMaterial sm when sm.Brush is SolidColorBrush ssb:
                    specCol = ssb.Color; specPow = sm.SpecularPower; break;
                case SpecularMaterial sm2 when sm2.Brush is ImageBrush:
                    // an image specular (the wear map) keeps its power, averaged to a plain white sheen
                    specCol = Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF); specPow = sm2.SpecularPower; break;
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
        return new BakedMaterial(col, opacity, tex, SpecColor: specCol, SpecPower: specPow);
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
    // majority (fronts/backs/full sheets). PNG stays for anything actually using alpha (jewel shells).
    private const int MaxTexPx = 1024;
    // …EXCEPT thin strips (spine scans, edge logos): their SHORT side is already small, so downscaling
    // saves nothing (a 75×1383 spine → 56×1024 is a handful of KB either way) while the cap + JPEG q85
    // turn fine vertical title text into illegible mush — the exact PS1-spine defect. A strip whose
    // shorter side is ≤ ThinSidePx keeps FULL resolution and lossless PNG, matching what LaunchBox
    // renders live from the same source. Cheap in bytes, sharp where it matters.
    private const int ThinSidePx = 256;

    private static byte[]? EncodeTexture(BitmapSource bs)
    {
        try
        {
            int w = bs.PixelWidth, h = bs.PixelHeight;
            int shorter = Math.Min(w, h), longest = Math.Max(w, h);
            // Thin strip → full-res, lossless (never JPEG fine text). Tiny regardless of length.
            if (shorter <= ThinSidePx) return EncodePng(bs);
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

    /// <summary>The aspect every snapshot is baked at — a CONSTANT, deliberately not the display ratio.
    /// The bake used to follow the ini's 16:9/poster option, which meant two artifacts per game and a full
    /// re-bake on every flip. One wide frame serves both: the camera fits the model VERTICALLY, so a
    /// narrower box (poster) just shows less empty width — see Model3dBlock's fit-to-height drawing.
    /// The value is unchanged from the old 16:9 case, so 16:9-baked caches keep their key.</summary>
    public const double BakeAspect = 16.0 / 9.0;

    /// <summary>Kept for callers that ask "what was this baked at" — now a constant.</summary>
    public static double TargetAspect() => BakeAspect;

    /// <summary>The live-viewport camera distance for <paramref name="aspect"/> — shared by the bake
    /// and Model3dBlock so both cameras are the same object in two places.</summary>
    /// <summary>Camera distance for a display aspect. WPF's FieldOfView is HORIZONTAL, so the vertical
    /// extent is (distance × tan(FOV/2)) / aspect: making the distance PROPORTIONAL to the aspect keeps
    /// that vertical extent constant at every ratio. The model therefore always fills the same share of
    /// the HEIGHT, and a narrower box simply crops empty width — the exact behaviour of the baked frame
    /// drawn fit-to-height. (The old max(1, aspect) left narrow ratios uncompensated: the poster box got
    /// a huge vertical extent and a tiny model.) At BakeAspect this returns the historical 2.756, so
    /// existing 16:9 bakes stay pixel-valid.</summary>
    public static double CameraDistanceFor(double aspect) => CameraDistance * Math.Max(0.05, aspect);

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
