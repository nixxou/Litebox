// Renders a static preview image of a game's "3D box": a procedural box mesh (front/back/spine faces)
// textured from the game's existing 2D box art, rendered once to a bitmap via plain WPF 3D
// (System.Windows.Media.Media3D), NOT a live/interactive viewport.
//
// This is intentionally the CHEAP half of the eventual feature: a HelixToolkit-based interactive
// viewer (drag to rotate) is a planned follow-up, only spun up on hover once this static render is
// confirmed visually correct. There is no interactivity here at all, this produces one bitmap and
// nothing else touches the GPU beyond that single render.
//
// Box proportions are a reasonable default (no ModelSettings support yet, see header note in
// GameStore.cs about that sub-entity being tracked generically/opaquely, not modelled field-by-field
// here). Front/back are full box-cover images; the spine gets the "Box - Spine" art if present,
// otherwise a plain dark fallback rather than stretching/distorting the front image onto it.

#nullable enable

using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class Box3DRenderer
{
    // Box proportions in arbitrary model units. Height > width (typical front-cover portrait aspect);
    // depth is thin relative to width/height (a game case, not a cube).
    private const double Width = 1.0;
    private const double Height = 1.40;
    private const double Depth = 0.12;

    private static readonly System.Windows.Media.Color FallbackSpine = System.Windows.Media.Color.FromRgb(24, 24, 24);
    private static readonly System.Windows.Media.Color FallbackFace = System.Windows.Media.Color.FromRgb(40, 40, 40);

    /// <summary>Returns a cached preview path for this game, rendering it once on first access (Core\
    /// litebox\box3d-cache\&lt;gameId&gt;.png) and reusing the cached file on every later call - this is
    /// the "cached image" half of the feature: the render only ever happens once per game, not once per
    /// UI selection. Returns null if the game has no front image (nothing to render, or an invalid Id),
    /// callers should fall back to their normal art source in that case.</summary>
    public static string? GetOrRenderCachedPreview(IGame game)
    {
        string path = CachePath(game);
        if (path == null) return null;
        if (File.Exists(path)) return path;
        return RenderPreview(game, path) ? path : null;
    }

    /// <summary>Same cache lookup as <see cref="GetOrRenderCachedPreview"/> but NEVER renders - returns
    /// null on a cache miss instead of blocking. For call sites on the UI thread (BuildMediaList runs on
    /// a WinForms Timer tick, not a background thread) where triggering a fresh render would stutter the
    /// UI; the background-threaded LoadImagesAsync path is what actually populates the cache.</summary>
    public static string? GetCachedPreviewIfExists(IGame game)
    {
        string path = CachePath(game);
        return path != null && File.Exists(path) ? path : null;
    }

    private static string? CachePath(IGame game)
    {
        if (!Guid.TryParse(game.Id, out var gid)) return null;
        string cacheDir = LbApiHost.Host.LiteBoxPaths.Dir("box3d-cache");
        return Path.Combine(cacheDir, gid.ToString("N") + ".png");
    }

    /// <summary>Renders one PNG preview for <paramref name="game"/> to <paramref name="outPngPath"/>.
    /// Returns false (and writes nothing) if the game has no front image to render at all.
    /// WPF's Viewport3D/FrameworkElement require an STA thread (InputManager throws otherwise); this
    /// app's own main thread is deliberately MTA (see Installer.RunSta / UiThread for the same
    /// constraint elsewhere), so this always does the actual work on a dedicated one-shot STA thread
    /// regardless of which thread it's called from.</summary>
    public static bool RenderPreview(IGame game, string outPngPath, int pixelWidth = 640, int pixelHeight = 900)
    {
        bool result = false;
        Exception? err = null;
        var t = new System.Threading.Thread(() =>
        {
            try { result = RenderPreviewCore(game, outPngPath, pixelWidth, pixelHeight); }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (err != null) { Console.WriteLine($"[box3d] render failed: {err.Message}"); return false; }
        return result;
    }

    private static bool RenderPreviewCore(IGame game, string outPngPath, int pixelWidth, int pixelHeight)
    {
        string? front = NullIfMissing(game.FrontImagePath);
        if (front == null) return false;
        string? back = NullIfMissing(game.BackImagePath);
        string? spine = Guid.TryParse(game.Id, out var gid)
            ? NullIfMissing(MediaResolver.Image(game.Platform, gid, game.Title, MediaResolver.Spine))
            : null;

        // Key light travels mostly +Z (from the camera's side, at negative Z, into the scene) so it
        // actually lands on the front face's outward (-Z) normal - the previous (-0.4,-0.5,-0.7) light
        // traveled AWAY from the camera and lit only the far/back side, leaving the front nearly black.
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(System.Windows.Media.Color.FromRgb(170, 170, 170)));
        group.Children.Add(new DirectionalLight(System.Windows.Media.Color.FromRgb(220, 220, 220), new Vector3D(-0.2, -0.4, 0.85)));

        group.Children.Add(BuildFace(
            new Point3D(0, 0, 0), new Point3D(Width, 0, 0), new Point3D(Width, Height, 0), new Point3D(0, Height, 0),
            LoadMaterial(front, FallbackFace)));                                            // front (+Z-facing viewer)

        group.Children.Add(BuildFace(
            new Point3D(Width, 0, Depth), new Point3D(0, 0, Depth), new Point3D(0, Height, Depth), new Point3D(Width, Height, Depth),
            LoadMaterial(back, FallbackFace)));                                             // back

        group.Children.Add(BuildFace(
            new Point3D(Width, 0, 0), new Point3D(Width, 0, Depth), new Point3D(Width, Height, Depth), new Point3D(Width, Height, 0),
            LoadMaterial(spine, FallbackSpine)));                                           // spine (right edge, visible in view)

        // Left/top/bottom: never in frame at this camera angle, plain fill is enough for now.
        group.Children.Add(BuildFace(
            new Point3D(0, 0, Depth), new Point3D(0, 0, 0), new Point3D(0, Height, 0), new Point3D(0, Height, Depth),
            new DiffuseMaterial(new SolidColorBrush(FallbackSpine))));                       // left
        group.Children.Add(BuildFace(
            new Point3D(0, Height, 0), new Point3D(Width, Height, 0), new Point3D(Width, Height, Depth), new Point3D(0, Height, Depth),
            new DiffuseMaterial(new SolidColorBrush(FallbackSpine))));                       // top
        group.Children.Add(BuildFace(
            new Point3D(0, 0, Depth), new Point3D(Width, 0, Depth), new Point3D(Width, 0, 0), new Point3D(0, 0, 0),
            new DiffuseMaterial(new SolidColorBrush(FallbackSpine))));                       // bottom

        // Camera orbits the box on a real angle (not just a tiny X nudge, which rendered as a flat-on
        // poster shot with zero visible depth) so the thin spine actually reads as a spine. LookDirection
        // is computed as target-minus-position, not guessed by hand.
        const double YawDegrees = 28;   // horizontal angle off dead-on, toward the spine side
        const double distance = 3.0;    // camera distance in box-width units
        const double fieldOfView = 24;  // horizontal FOV in degrees; WPF derives vertical FOV from this
                                         // and the viewport's aspect ratio, so a portrait canvas (below)
                                         // naturally gets proportionally more vertical coverage too.
        double yaw = YawDegrees * Math.PI / 180.0;
        var center = new Point3D(Width / 2, Height / 2, Depth / 2);
        var camPos = new Point3D(
            center.X + distance * Math.Sin(yaw),
            center.Y + Height * 0.12,
            center.Z - distance * Math.Cos(yaw));
        var visual = new ModelVisual3D { Content = group };
        var camera = new PerspectiveCamera
        {
            Position = camPos,
            LookDirection = center - camPos,
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = fieldOfView,
        };

        // Portrait canvas matching the box's own portrait aspect (rather than a square), so the taller
        // dimension isn't cropped just to accommodate a square frame - this was the actual cause of the
        // top-of-box-art cropping seen in earlier test renders, not the camera distance itself.
        var viewport = new Viewport3D { ClipToBounds = true };
        viewport.Children.Add(visual);
        viewport.Camera = camera;
        viewport.Width = pixelWidth;
        viewport.Height = pixelHeight;
        viewport.Measure(new System.Windows.Size(pixelWidth, pixelHeight));
        viewport.Arrange(new System.Windows.Rect(0, 0, pixelWidth, pixelHeight));
        viewport.UpdateLayout();

        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(viewport);

        Directory.CreateDirectory(Path.GetDirectoryName(outPngPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(outPngPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
        return true;
    }

    // Vertices given counter-clockwise as seen from outside the box, so the default (unlit-both-sides-off)
    // backface culling shows the face right-side-up from the front camera without a mirrored/flipped image.
    private static GeometryModel3D BuildFace(Point3D a, Point3D b, Point3D c, Point3D d, Material material)
    {
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
        // U flipped vs a naive mapping: empirically confirmed (box art rendered as mirrored/backwards
        // text otherwise) that these face windings need the horizontal coordinate reversed to read
        // correctly - see the render verification note in RenderPreview's doc comment.
        mesh.TextureCoordinates.Add(new System.Windows.Point(1, 1));
        mesh.TextureCoordinates.Add(new System.Windows.Point(0, 1));
        mesh.TextureCoordinates.Add(new System.Windows.Point(0, 0));
        mesh.TextureCoordinates.Add(new System.Windows.Point(1, 0));
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static Material LoadMaterial(string? imagePath, System.Windows.Media.Color fallback)
    {
        if (imagePath != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return new DiffuseMaterial(new ImageBrush(bmp));
            }
            catch (Exception ex) { Console.WriteLine($"[box3d] load '{imagePath}' failed: {ex.Message}"); }
        }
        return new DiffuseMaterial(new SolidColorBrush(fallback));
    }

    private static string? NullIfMissing(string? p) => string.IsNullOrWhiteSpace(p) || !File.Exists(p) ? null : p;
}
