// LiteBox's OWN 3D case renderer — pure WPF Media3D, ZERO LaunchBox involvement at runtime. Every rule in
// here was reverse-engineered against LB's FlowModel with a side-by-side harness (validated per type, per
// option) before the oracle was removed: geometry tables, texture composition, colour derivation
// (corner-average of the front art), per-side spine/logo rules with rotations, full-scan sheet slicing,
// autonomous dims (art aspect capped at 1; box D=0.143; dvd D=W×spineAspect/frontAspect), scene constants
// (camera (0,0,2) FOV 50; white directional (0,-0.5,-1) + ambient #333), model-rotation under fixed lights.
// Case models + spine presets are LiteBox-shipped resources (case-assets/, LB dll only as fallback).

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


    private readonly System.Windows.Controls.Grid _root;

    // ── AUTONOMOUS SCENE (no LaunchBox-core dependency) — constants dumped from the live FlowModel once:
    //   camera  : PerspectiveCamera pos (0,0,2) look (0,0,-1) up (0,1,0) FOV 50, near 0.001, far 20
    //   lights  : DirectionalLight #FFFFFFFF dir (0,-0.5,-1) + AmbientLight #FF333333
    //   rotation: Transform3DGroup [ RotateY(AxisAngle 0,1,0), RotateX(AxisAngle 1,0,0) ] about the origin —
    //             the exact structure LB's RotateModel maintains (whose parameters are 7.5°-units).
    private readonly AxisAngleRotation3D _yawRot = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _pitchRot = new(new Vector3D(1, 0, 0), 0);
    private double _yawTarget, _pitchTarget;

    public HomeModel3d()
    {
        _viewport = new Viewport3D { ClipToBounds = true };
        _viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, 2),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 50,
            NearPlaneDistance = 0.001,
            FarPlaneDistance = 20,
        };
        _lightHost.Children.Add(new ModelVisual3D { Content = new DirectionalLight(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(0, -0.5, -1)) });
        _lightHost.Children.Add(new ModelVisual3D { Content = new AmbientLight(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)) });
        var tg = new Transform3DGroup();
        tg.Children.Add(new RotateTransform3D(_yawRot));
        tg.Children.Add(new RotateTransform3D(_pitchRot));
        _modelHost.Transform = tg;
        _viewport.Children.Add(_lightHost);
        _viewport.Children.Add(_modelHost);
        // Wrap in a Grid WITH a Background so the WPF content is HIT-TESTABLE (a bare Viewport3D is transparent
        // to the mouse → the ElementHost never sees drag/wheel). This is what let LB's opaque UserControl get
        // mouse while our zone didn't.
        _root = new System.Windows.Controls.Grid { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 30)) };
        _root.Children.Add(_viewport);
        _host = new ElementHost { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(28, 28, 30), Child = _root };
    }

    /// <summary>Animate the model rotation by the given degree deltas (own scene — no core involved).</summary>
    public void OrbitBy(double dYawDeg, double dPitchDeg)
    {
        _yawTarget += dYawDeg;
        _pitchTarget += dPitchDeg;
        var ease = new System.Windows.Media.Animation.DoubleAnimation(_yawTarget, TimeSpan.FromMilliseconds(400)) { DecelerationRatio = 1 };
        _yawRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, ease);
        var ease2 = new System.Windows.Media.Animation.DoubleAnimation(_pitchTarget, TimeSpan.FromMilliseconds(400)) { DecelerationRatio = 1 };
        _pitchRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, ease2);
    }

    /// <summary>Zoom = camera distance multiplier on the own fixed camera.</summary>
    public void SetZoom(double zoom)
    {
        if (_viewport.Camera is PerspectiveCamera pc) pc.Position = new Point3D(0, 0, 2 * zoom);
    }

    /// <summary>Build the model for the given settings map + sample game. Unknown/absent ModelType renders as
    /// a box (what LB does for null settings).</summary>
    public void Build(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform)
    {
        try
        {
            string type = map != null && map.TryGetValue("ModelType", out var t) ? t : "box";
            _modelHost.Content = type switch
            {
                "jewelCase" => BuildJewel(map, gameTitle, platform),
                "dvd" => BuildDvd(map, gameTitle, platform),
                "longJewelCase" => BuildLongJewel(map, gameTitle, platform),
                "doubleJewelCase" => BuildDoubleJewel(map, gameTitle, platform),
                _ => BuildBox(map, gameTitle, platform),
            };
        }
        catch (Exception ex) { Console.WriteLine("[homemodel] build: " + ex.Message); _modelHost.Content = null; }
    }

    // ── ITERATION 2b/2c: procedural BOX, geometry AND materials home-made. Decoded from LB's structure dumps
    // (PS1 A-Train + SNES Secret of Mana + synthetic colour probes):
    //   • 6 quads centred at origin, dims = ModelSize (art-aspect-derived width); vertex/UV/winding table below
    //     is LB's EXACT data (verbatim from model3d-structure.log).
    //   • Case colour = CoverColor option, else the AVERAGE OF THE 4 CORNER PIXELS of the front art (decoded
    //     empirically: uniform-red front → red, half red/blue → exact mean, white-corners-only → white).
    //   • Side faces follow SpineRotation / LogoRotation CSVs (slots = left,top,right,bottom; empty = off):
    //       spine slot on + a Box - Spine scan → VisualBrush{ bare Image, Uniform } (no background grid);
    //       else logo slot on + a Clear Logo   → Grid(case colour) + logo Uniform;
    //       else solid case colour.
    //   • front = art Fill on case-colour grid; back = Box - Back scan as a PLAIN ImageBrush (Fill) when
    //     present, else solid case colour; bottom = solid.
    // Dims still read from LB's built bounds (the oracle); own sizing rules come with the full re-implementation.
    // TODO: non-zero per-side rotations; UseFullScanImages wrap mode; LB's decode sizing (h=600 / logo w=206 —
    // only affects texture sharpness).
    // ── AUTONOMOUS DIMS (probe-derived rules — nothing read from the live model) ──
    //   forced ModelSizeString "w;h;d" → used directly. Else the art aspect a = W/H of the decoded front:
    //   W = min(1, a), H = min(1, 1/a); no art → LB's placeholder aspect 0.766 (W .766, H 1).
    //   Depth: box = 0.143 (constant, all observations); dvd = W×(spineW/frontW) when a spine scan exists,
    //   else defaultD(0.065)×H. Full-scan box: spinePx = FullImageSpineWidth×sheetW, D = spinePx/sheetH,
    //   panel = (sheetW−spinePx)/2, W/H from panel aspect (landscape flag rotates the panel).
    private static (double w, double h, double d) BoxDims(System.Collections.Generic.Dictionary<string, string>? map,
                                                          System.Windows.Media.Imaging.BitmapSource? front, double defaultD)
    {
        if (map != null && map.TryGetValue("ModelSizeString", out var mss) && !string.IsNullOrWhiteSpace(mss))
        {
            var p = mss.Split(';', ',');
            if (p.Length == 3
                && double.TryParse(p[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fw)
                && double.TryParse(p[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fh)
                && double.TryParse(p[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fd))
                return (fw, fh, fd);
        }
        double a = front != null && front.PixelHeight > 0 ? (double)front.PixelWidth / front.PixelHeight : 0.766;
        return (Math.Min(1, a), Math.Min(1, 1 / a), defaultD);
    }

    private static Model3D? BuildBox(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform)
    {
        string? frontPath = ResolveArt(platform, gameTitle, Media.MediaResolver.Front);
        string? logoPath = ResolveArt(platform, gameTitle, Media.MediaResolver.ClearLogo);
        string? spinePath = ResolveArt(platform, gameTitle, new[] { "Box - Spine" });
        string? backPath = ResolveArt(platform, gameTitle, new[] { "Box - Back" });
        var front = LoadBitmap(frontPath);
        var logo = LoadBitmap(logoPath);
        var spine = LoadBitmap(spinePath);
        var back = LoadBitmap(backPath);

        // FULL-SCAN MODE (probe-decoded priority): when UseFullScanImages is on, the game's SPINE SCAN is the
        // arbiter — a spine scan forces the composed per-face mode; with NO spine scan and a Box - Full image
        // present, LB slices the SINGLE full sheet by ALIGNMENT CROPPING: every textured face gets the whole
        // image UniformToFill — front hAlign=Right, back hAlign=Left, sides hAlign=Center — and top/bottom are
        // the full scan's corner-average. (Flag off → Box - Full is ignored entirely.)
        bool fullScanFlag = map != null && map.TryGetValue("UseFullScanImages", out var ufs) && ufs.Equals("true", StringComparison.OrdinalIgnoreCase);
        var fullImg = fullScanFlag && spine == null ? LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Full" })) : null;

        // Dims (autonomous — nothing read from the live model): full-scan mode derives them from the sheet
        // (spinePx = FullImageSpineWidth×sheetW; D = spinePx/sheetH; panel aspect rotated by the landscape
        // flag), else BoxDims (forced size / art aspect / placeholder), box depth constant 0.143.
        double W, H, D;
        if (fullImg != null && (map == null || !map.ContainsKey("ModelSizeString")))
        {
            double spineFrac = 0.143;
            if (map != null && map.TryGetValue("FullImageSpineWidth", out var fsw))
                double.TryParse(fsw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out spineFrac);
            bool landscape = map != null && map.TryGetValue("FullScanIsLandscape", out var fsl) && fsl.Equals("true", StringComparison.OrdinalIgnoreCase);
            double sheetW = fullImg.PixelWidth, sheetH = Math.Max(1, fullImg.PixelHeight);
            double spinePx = spineFrac * sheetW;
            double panel = Math.Max(1, (sheetW - spinePx) / 2);
            double a = landscape ? sheetH / panel : panel / sheetH;
            W = Math.Min(1, a); H = Math.Min(1, 1 / a);
            D = spinePx / sheetH;
        }
        else
            (W, H, D) = BoxDims(map, front, 0.143);
        double hw = W / 2, hh = H / 2, hd = D / 2;

        // Case colour: CoverColor option (signed-ARGB string), else corner average of the art (full scan in
        // full-scan mode, front otherwise).
        var caseColor = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        if (map != null && map.TryGetValue("CoverColor", out var cc) && int.TryParse(cc, out var argb))
            caseColor = System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        else if (fullImg != null) caseColor = CornerAverage(fullImg);
        else if (front != null) caseColor = CornerAverage(front);
        var solid = new DiffuseMaterial(new SolidColorBrush(caseColor));

        if (fullImg != null)
        {
            Material Slice(System.Windows.HorizontalAlignment align, double gw, double gh)
            {
                var g = new System.Windows.Controls.Grid { Width = gw, Height = gh, Background = new SolidColorBrush(caseColor), ClipToBounds = true };
                g.Children.Add(new System.Windows.Controls.Image
                { Source = fullImg, Stretch = System.Windows.Media.Stretch.UniformToFill,
                  HorizontalAlignment = align, VerticalAlignment = System.Windows.VerticalAlignment.Stretch });
                return new DiffuseMaterial(new VisualBrush(g) { Stretch = System.Windows.Media.Stretch.Fill });
            }
            double gW = W * 1000, gH = H * 1000, gD = D * 1000;
            var fFront = Slice(System.Windows.HorizontalAlignment.Right, gW, gH);
            var fBack = Slice(System.Windows.HorizontalAlignment.Left, gW, gH);
            var fSide = Slice(System.Windows.HorizontalAlignment.Center, gD, gH);
            var grpF = new Model3DGroup();
            void QuadF(Material mat, (double x, double y, double z)[] p, (double u, double v)[] uv, int[] tris)
            {
                var mesh = new MeshGeometry3D();
                for (int i = 0; i < 4; i++)
                {
                    mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                    mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].u, uv[i].v));
                }
                foreach (var ix in tris) mesh.TriangleIndices.Add(ix);
                grpF.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat });
            }
            var up4 = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };
            int[] TF = { 3, 0, 2, 3, 1, 0 };
            QuadF(solid, new[] { (-hw, hh, -hd), (hw, hh, -hd), (-hw, hh, hd), (hw, hh, hd) }, up4, TF);
            QuadF(solid, new[] { (-hw, -hh, -hd), (hw, -hh, -hd), (-hw, -hh, hd), (hw, -hh, hd) },
                  new[] { (0d, 1d), (1d, 1d), (0d, 0d), (1d, 0d) }, new[] { 3, 2, 0, 3, 0, 1 });
            QuadF(fFront, new[] { (-hw, hh, hd), (hw, hh, hd), (-hw, -hh, hd), (hw, -hh, hd) }, up4, TF);
            QuadF(fBack, new[] { (hw, hh, -hd), (-hw, hh, -hd), (hw, -hh, -hd), (-hw, -hh, -hd) }, up4, TF);
            QuadF(fSide, new[] { (-hw, hh, -hd), (-hw, hh, hd), (-hw, -hh, -hd), (-hw, -hh, hd) }, up4, TF);
            QuadF(fSide, new[] { (hw, hh, hd), (hw, hh, -hd), (hw, -hh, hd), (hw, -hh, -hd) }, up4, TF);
            return grpF;
        }

        var frontMat = front != null ? FaceMaterial(front, System.Windows.Media.Stretch.Fill, W * 1000, H * 1000, caseColor) : solid;
        var logoMat = logo != null ? FaceMaterial(logo, System.Windows.Media.Stretch.Uniform, W * 1000, D * 1000, caseColor) : solid;
        Material spineMat = spine != null ? SpineFaceMaterial(spine) : solid;
        Material backFace = back != null ? new DiffuseMaterial(new ImageBrush(back) { Stretch = System.Windows.Media.Stretch.Fill }) : solid;

        // Per-side spine/logo toggles: CSV slots left,top,right,bottom — empty slot = off (value = rotation°).
        // The UV orientation DIFFERS per texture kind (decoded from LB dumps): a SPINE scan (portrait) maps
        // UPRIGHT onto the side face, while the CLEAR LOGO (landscape) maps ROTATED 90° (opposite ways on
        // left vs right, like a real box). Non-zero rotations become a LayoutTransform on the face's Image
        // (probe-derived): spine → Rotate(r) everywhere; logo → Rotate(r) on top/bottom but on the SIDE faces
        // 90→270, 180→0, 270→90 (the side's inherent mesh-UV rotation folds in).
        var spineRots = ParseSideRots(map, "SpineRotation");
        var logoRots = ParseSideRots(map, "LogoRotation");
        Material RotSpine(int r) => r == 0 ? spineMat : spine == null ? solid
            : new DiffuseMaterial(new VisualBrush(new System.Windows.Controls.Image
              { Source = spine, Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                LayoutTransform = new System.Windows.Media.RotateTransform(r) })
              { Stretch = System.Windows.Media.Stretch.Fill });
        Material RotLogo(int layoutRot, double gw, double gh)
        {
            if (logo == null) return solid;
            var g = new System.Windows.Controls.Grid { Width = gw, Height = gh, Background = new SolidColorBrush(caseColor) };
            var im = new System.Windows.Controls.Image
            { Source = logo, Stretch = System.Windows.Media.Stretch.Uniform,
              HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            if (layoutRot != 0) im.LayoutTransform = new System.Windows.Media.RotateTransform(layoutRot);
            g.Children.Add(im);
            return new DiffuseMaterial(new VisualBrush(g) { Stretch = System.Windows.Media.Stretch.Fill });
        }
        int SideLogoRot(int r) => r == 90 ? 270 : r == 270 ? 90 : 0;
        (Material mat, bool upright) Side(int slot)
        {
            bool isSide = slot == 0 || slot == 2;   // left/right (vs top/bottom)
            if (spineRots[slot] is int sr && spine != null) return (RotSpine(sr), true);
            if (logoRots[slot] is int lr && logo != null)
                return isSide ? (RotLogo(SideLogoRot(lr), W * 1000, D * 1000), false)
                              : (lr == 0 ? (logoMat, true) : (RotLogo(lr, W * 1000, D * 1000), true));
            return (solid, true);
        }

        var grp = new Model3DGroup();
        // One quad from LB's exact vertex/UV/tris table. p = 4 positions, uv = 4 texture coords.
        void Quad(Material mat, (double x, double y, double z)[] p, (double u, double v)[] uv, int[] tris)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < 4; i++)
            {
                mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].u, uv[i].v));
            }
            foreach (var ix in tris) mesh.TriangleIndices.Add(ix);
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat });
        }
        int[] T = { 3, 0, 2, 3, 1, 0 };   // LB's winding (all faces except bottom)
        var upright4 = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };   // spine scan / plain mapping
        // top (Y+): slot 1, U→+X V→+Z (same table for logo and spine on top, per dumps)
        var topSide = Side(1);
        Quad(topSide.mat, new[] { (-hw, hh, -hd), (hw, hh, -hd), (-hw, hh, hd), (hw, hh, hd) }, upright4, T);
        // bottom (Y-): slot 3
        var botSide = Side(3);
        Quad(botSide.mat, new[] { (-hw, -hh, -hd), (hw, -hh, -hd), (-hw, -hh, hd), (hw, -hh, hd) },
             new[] { (0d, 1d), (1d, 1d), (0d, 0d), (1d, 0d) }, new[] { 3, 2, 0, 3, 0, 1 });
        // front (Z+): art, U→+X V-down
        Quad(frontMat, new[] { (-hw, hh, hd), (hw, hh, hd), (-hw, -hh, hd), (hw, -hh, hd) }, upright4, T);
        // back (Z-): Box - Back scan (mirrored so it reads correctly from outside)
        Quad(backFace, new[] { (hw, hh, -hd), (-hw, hh, -hd), (hw, -hh, -hd), (-hw, -hh, -hd) }, upright4, T);
        // left (X-): slot 0 — positions (top,-Z),(top,+Z),(bottom,-Z),(bottom,+Z)
        var leftSide = Side(0);
        Quad(leftSide.mat, new[] { (-hw, hh, -hd), (-hw, hh, hd), (-hw, -hh, -hd), (-hw, -hh, hd) },
             leftSide.upright ? upright4                                       // spine scan upright
                              : new[] { (1d, 0d), (1d, 1d), (0d, 0d), (0d, 1d) }, T);   // logo rotated (U bottom→top)
        // right (X+): slot 2 — positions (top,+Z),(top,-Z),(bottom,+Z),(bottom,-Z)
        var rightSide = Side(2);
        Quad(rightSide.mat, new[] { (hw, hh, hd), (hw, hh, -hd), (hw, -hh, hd), (hw, -hh, -hd) },
             rightSide.upright ? upright4                                      // spine scan upright (mirrored positions)
                               : new[] { (0d, 1d), (0d, 0d), (1d, 1d), (1d, 0d) }, T);  // logo rotated the other way
        return grp;
    }

    // CSV "0,,0," → [true,false,true,false] (slots: left, top, right, bottom; empty = not drawn).
    private static bool[] ParseSides(System.Collections.Generic.Dictionary<string, string>? map, string key)
    {
        var res = new bool[4];
        if (map == null || !map.TryGetValue(key, out var csv) || string.IsNullOrEmpty(csv)) return res;
        var parts = csv.Split(',');
        for (int i = 0; i < 4 && i < parts.Length; i++) res[i] = parts[i].Trim().Length > 0;
        return res;
    }

    // CSV "90,,270," → [90, null, 270, null] (slots: left, top, right, bottom; empty = not drawn).
    private static int?[] ParseSideRots(System.Collections.Generic.Dictionary<string, string>? map, string key)
    {
        var res = new int?[4];
        if (map == null || !map.TryGetValue(key, out var csv) || string.IsNullOrEmpty(csv)) return res;
        var parts = csv.Split(',');
        for (int i = 0; i < 4 && i < parts.Length; i++)
            if (parts[i].Trim().Length > 0) res[i] = int.TryParse(parts[i].Trim(), out var v) ? v : 0;
        return res;
    }

    // LB's spine-scan side face: VisualBrush over a BARE Image (Uniform) — no background grid.
    private static Material SpineFaceMaterial(System.Windows.Media.Imaging.BitmapSource img)
        => new DiffuseMaterial(new VisualBrush(new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.Uniform })
        { Stretch = System.Windows.Media.Stretch.Fill });

    // The case colour LB derives from the art: the average of the 4 corner pixels (probe-verified).
    private static System.Windows.Media.Color CornerAverage(System.Windows.Media.Imaging.BitmapSource src)
    {
        try
        {
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[4];
            long bsum = 0, gsum = 0, rsum = 0;
            foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
            {
                conv.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
                bsum += px[0]; gsum += px[1]; rsum += px[2];
            }
            return System.Windows.Media.Color.FromRgb((byte)(rsum / 4), (byte)(gsum / 4), (byte)(bsum / 4));
        }
        catch { return System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69); }
    }

    // ── ITERATION 3: JEWEL CASE — paper insert (5 quads, exact constants from LB's structure dump) + the
    // plastic case (LB's embedded JewelCaseObj via our own OBJ loader, transform verbatim from the dump).
    //   insert: spine strip WRAPS the back-left edge (face part u∈[0.416,1] + left cap u∈[0,0.416]), art front
    //   (BackMat grey #FF696969), grey back (BackMat #FF050505), top/bottom→n/a, left/right edge strips = clear
    //   logo Uniform on #FF1C1116. Brush grids: spine 120×1000 transparent, front/back 1000×889.63, strips
    //   1000×54.27.  TODO: Box - Back image on the back quad; preset/custom/text spine styles (FrontSpineImage).
    private static Model3D? BuildJewel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform)
    {
        var plastic = LbCaseObj.Load("JewelCase");
        if (plastic == null) return null;   // no embedded model → keep LB's clone (comparison still works)

        var front = LoadBitmap(ResolveArt(platform, gameTitle, Media.MediaResolver.Front));
        var logo = LoadBitmap(ResolveArt(platform, gameTitle, Media.MediaResolver.ClearLogo));

        // Spine: FrontSpineImage = "{Resources}\<preset name>" (embedded preset, clear overlay), a custom image
        // path, or empty (clear / solid). Falls back to the game's own Box - Spine scan when nothing is set.
        string spineSpec = map != null && map.TryGetValue("FrontSpineImage", out var ss) ? ss : "";
        bool spineClear = map != null && map.TryGetValue("FrontSpineIsClear", out var sc) && sc == "true";
        System.Windows.Media.Imaging.BitmapSource? spineImg =
            spineSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase) ? LbCaseObj.SpineImage(spineSpec.Substring(12))
            : spineSpec.Length > 0 ? LoadBitmap(spineSpec)
            : LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Spine" }));

        var grey = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        var clear = System.Windows.Media.Colors.Transparent;
        var backScan = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Back" }));
        var scan = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Spine" }));

        // Strip background = CoverColor option else corner-average of the front art (probe-decoded; the old
        // #1C1116 constant was just A-Train's corner average). Text mode: CaseColor = text colour.
        var stripBg = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        if (map != null && map.TryGetValue("CoverColor", out var cs) && int.TryParse(cs, out var cargb2))
            stripBg = System.Windows.Media.Color.FromArgb((byte)(cargb2 >> 24), (byte)(cargb2 >> 16), (byte)(cargb2 >> 8), (byte)cargb2);
        else if (front != null) stripBg = CornerAverage(front);
        string logoFont = map != null && map.TryGetValue("LogoFont", out var lf) ? lf : "";
        var textColor = System.Windows.Media.Colors.White;
        if (map != null && map.TryGetValue("CaseColor", out var tc) && int.TryParse(tc, out var targb))
            textColor = System.Windows.Media.Color.FromArgb((byte)(targb >> 24), (byte)(targb >> 16), (byte)(targb >> 8), (byte)targb);

        Material frontMat = front != null ? FaceMaterial(front, System.Windows.Media.Stretch.Fill, 1000, 889.628809154057, clear)
                                          : new DiffuseMaterial(new SolidColorBrush(grey));
        Material backMat = backScan != null ? FaceMaterial(backScan, System.Windows.Media.Stretch.Fill, 1000, 889.628809154057, grey)
                                            : FaceMaterialNoImage(1000, 889.628809154057, grey);
        // Wrapped spine quad: FrontSpineImage (preset/custom) else the game's scan; without any image, LB emits
        // NO spine quad at all — a fully transparent material is visually identical.
        Material spineMat = spineImg != null ? FaceMaterial(spineImg, System.Windows.Media.Stretch.Fill, 120, 1000, spineClear ? clear : stripBg)
                                             : new DiffuseMaterial(System.Windows.Media.Brushes.Transparent);
        // Edge strips: the spine SCAN when present (bare centered image), else clear logo ROTATED 180 on the
        // strip background, else the plain-text title (Viewbox'd TextBlock, LogoFont/CaseColor), else solid.
        Material stripMat;
        if (scan != null)
            stripMat = new DiffuseMaterial(new VisualBrush(new System.Windows.Controls.Image
            { Source = scan, Stretch = System.Windows.Media.Stretch.Uniform,
              HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center })
            { Stretch = System.Windows.Media.Stretch.Fill });
        else if (logo != null && logoFont.Length == 0)
        {
            var g = new System.Windows.Controls.Grid { Width = 1000, Height = 54.274459132109115, Background = new SolidColorBrush(stripBg) };
            g.Children.Add(new System.Windows.Controls.Image
            { Source = logo, Stretch = System.Windows.Media.Stretch.Uniform,
              HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
              LayoutTransform = new System.Windows.Media.RotateTransform(180) });
            stripMat = new DiffuseMaterial(new VisualBrush(g) { Stretch = System.Windows.Media.Stretch.Fill });
        }
        else if (logoFont.Length > 0)
        {
            var g = new System.Windows.Controls.Grid { Width = 1000, Height = 54.06310944167632, Background = new SolidColorBrush(stripBg) };
            var tb = new System.Windows.Controls.TextBlock
            { Text = gameTitle ?? "", FontSize = 12, Foreground = new SolidColorBrush(textColor) };
            try { tb.FontFamily = new System.Windows.Media.FontFamily(logoFont); } catch { }
            g.Children.Add(new System.Windows.Controls.Viewbox { Child = tb });
            stripMat = new DiffuseMaterial(new VisualBrush(g) { Stretch = System.Windows.Media.Stretch.Fill });
        }
        else
            stripMat = new DiffuseMaterial(new SolidColorBrush(stripBg));

        var grp = new Model3DGroup();
        void Quad(Material mat, Material? back, (double x, double y, double z)[] p, (double u, double v)[] uv, int[] tris)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < p.Length; i++)
            {
                mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].u, uv[i].v));
            }
            foreach (var ix in tris) mesh.TriangleIndices.Add(ix);
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = back });
        }
        int[] T = { 3, 0, 2, 3, 1, 0 };
        // spine: back-plane strip (u 0.416→1) + left edge cap (u 0→0.416) — one mesh, 8 verts (dump-exact).
        Quad(spineMat, null,
             new[] { (-0.493, 0.4315, -0.0203), (-0.43, 0.4315, -0.0203), (-0.493, -0.4315, -0.0203), (-0.43, -0.4315, -0.0203),
                     (-0.493, 0.4315, 0.0204), (-0.493, 0.4315, -0.0203), (-0.493, -0.4315, 0.0204), (-0.493, -0.4315, -0.0203) },
             new[] { (0.416, 0d), (1d, 0d), (0.416, 1d), (1d, 1d), (0d, 0d), (0.416, 0d), (0d, 1d), (0.416, 1d) },
             new[] { 3, 0, 2, 3, 1, 0, 7, 4, 6, 7, 5, 4 });
        // front insert (art), BackMat grey
        Quad(frontMat, new DiffuseMaterial(new SolidColorBrush(grey)),
             new[] { (-0.42, 0.4315, 0.0204), (0.492, 0.4315, 0.0204), (-0.42, -0.4315, 0.0204), (0.492, -0.4315, 0.0204) },
             new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) }, T);
        // back insert (grey grid), BackMat near-black
        Quad(backMat, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 5, 5))),
             new[] { (0.492, 0.4315, -0.0204), (-0.492, 0.4315, -0.0204), (0.492, -0.4315, -0.0204), (-0.492, -0.4315, -0.0204) },
             new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) }, T);
        // left edge strip (logo, reads bottom→top)
        Quad(stripMat, null,
             new[] { (-0.492, 0.4315, -0.0204), (-0.492, 0.4315, 0.0204), (-0.492, -0.4315, -0.0204), (-0.492, -0.4315, 0.0204) },
             new[] { (1d, 0d), (1d, 1d), (0d, 0d), (0d, 1d) }, T);
        // right edge strip (logo, reads top→bottom)
        Quad(stripMat, null,
             new[] { (0.494, 0.4315, 0.0204), (0.494, 0.4315, -0.0204), (0.494, -0.4315, 0.0204), (0.494, -0.4315, -0.0204) },
             new[] { (0d, 1d), (0d, 0d), (1d, 1d), (1d, 0d) }, T);

        // plastic case: LB's embedded OBJ, positioned exactly as LB does (Translate THEN Scale, dump-verbatim).
        var tg = new Transform3DGroup();
        tg.Children.Add(new TranslateTransform3D(-0.031, -0.629, 0.004));
        tg.Children.Add(new ScaleTransform3D(0.707, 0.707, 0.707));
        grp.Children.Add(new Model3DGroup { Children = { plastic.Clone() }, Transform = tg });
        return grp;
    }

    // ── ITERATION 4c: DOUBLE JEWEL CASE — dump-exact structure:
    //   • front/back art quads Z=±0.0573 (Grid 1000×1000, bg = corner-avg of the front art — NOTE: LB's actual
    //     bg on SoM was #1C3219 vs corner-avg #104E30, formula not fully pinned, but the bg is INVISIBLE in
    //     practice: the art Fill covers the whole grid) + a TINT overlay quad Z=±0.0466 per side
    //     (Diffuse #DE24262C + Specular #34969BA0 pow=18 — the insert seen through the closed lid).
    //   • FOUR spine strips (two per side, split at z=±0.008..0.058): LB splits the spine image into left/right
    //     HALVES — right side gets [left-half (back), right-half (front)], left side gets the SAME halves
    //     ROTATED 180° (probe-verified against LB's .bbflow-double-jewel-spine cache files, diff 0.04).
    //     Strip brush = Grid[220×1000] transparent + half Image Fill.
    //   • plastic: NOT an embedded obj (LB builds it procedurally — 7 segments, scale 69.78); cloned from the
    //     live model's child group (game-independent). TODO: reproduce procedurally.
    //   TODO: DoubleSpineImageMode variants (Single / DualSplitCenter / DualMiddleSeparator) — Automatic split
    //   is what's implemented (observed behaviour with a spine scan).
    private static Model3D? BuildDoubleJewel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform)
    {
        var front = LoadBitmap(ResolveArt(platform, gameTitle, Media.MediaResolver.Front));
        var backImg = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Back" }));

        // Spine source: preset resource / custom path / the game's Box - Spine scan (same rules as jewel).
        string spineSpec = map != null && map.TryGetValue("FrontSpineImage", out var ss) ? ss : "";
        System.Windows.Media.Imaging.BitmapSource? spine =
            spineSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase) ? LbCaseObj.SpineImage(spineSpec.Substring(12))
            : spineSpec.Length > 0 ? LoadBitmap(spineSpec)
            : LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Spine" }));

        var bg = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        if (front != null) bg = CornerAverage(front);
        var grey = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69)));
        Material frontMat = front != null ? FaceMaterial(front, System.Windows.Media.Stretch.Fill, 1000, 1000, bg) : grey;
        Material backMat = backImg != null ? FaceMaterial(backImg, System.Windows.Media.Stretch.Fill, 1000, 1000, bg)
                          : front != null ? frontMat : grey;
        var tintG = new MaterialGroup();
        tintG.Children.Add(new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xDE, 0x24, 0x26, 0x2C))));
        tintG.Children.Add(new SpecularMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x34, 0x96, 0x9B, 0xA0)), 18));

        // Spine halves: left/right split; the left side of the case shows them rotated 180°.
        Material Strip(System.Windows.Media.Imaging.BitmapSource? img)
        {
            var grid = new System.Windows.Controls.Grid { Width = 220, Height = 1000, Background = System.Windows.Media.Brushes.Transparent };
            if (img != null)
                grid.Children.Add(new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.Fill,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            return new DiffuseMaterial(new VisualBrush(grid) { Stretch = System.Windows.Media.Stretch.Fill });
        }
        // Mode table (probe-verified): Single/MiddleSeparator → the FULL spine on both strips;
        // Automatic/DualSplitCenter → left/right halves. The opposite side is always rotated 180°.
        string mode = map != null && map.TryGetValue("DoubleSpineImageMode", out var dm) ? dm : "AutomaticDetection";
        bool single = mode is "SingleSpineImage" or "DualSpineImageMiddleSeparator";
        System.Windows.Media.Imaging.BitmapSource? halfL = null, halfR = null, halfLr = null, halfRr = null;
        if (spine != null)
        {
            if (single) { halfL = spine; halfR = spine; }
            else
            {
                int w = spine.PixelWidth, hw2 = w / 2;
                halfL = new System.Windows.Media.Imaging.CroppedBitmap(spine, new System.Windows.Int32Rect(0, 0, hw2, spine.PixelHeight));
                halfR = new System.Windows.Media.Imaging.CroppedBitmap(spine, new System.Windows.Int32Rect(w - hw2, 0, hw2, spine.PixelHeight));
            }
            halfLr = new System.Windows.Media.Imaging.TransformedBitmap(halfL, new System.Windows.Media.RotateTransform(180));
            halfRr = new System.Windows.Media.Imaging.TransformedBitmap(halfR, new System.Windows.Media.RotateTransform(180));
        }

        var grp = new Model3DGroup();
        void Quad(Material mat, (double x, double y, double z)[] p)
        {
            var mesh = new MeshGeometry3D();
            var uv = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };
            for (int i = 0; i < 4; i++)
            {
                mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].Item1, uv[i].Item2));
            }
            foreach (var ix in new[] { 3, 0, 2, 3, 1, 0 }) mesh.TriangleIndices.Add(ix);
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat });
        }
        Quad(frontMat, new[] { (-0.493, 0.4265, 0.0573), (0.492, 0.4265, 0.0573), (-0.493, -0.4265, 0.0573), (0.492, -0.4265, 0.0573) });
        Quad(tintG, new[] { (-0.489, 0.4195, 0.0466), (0.488, 0.4195, 0.0466), (-0.489, -0.4195, 0.0466), (0.488, -0.4195, 0.0466) });
        Quad(backMat, new[] { (0.492, 0.4265, -0.0573), (-0.482, 0.4265, -0.0573), (0.492, -0.4265, -0.0573), (-0.482, -0.4265, -0.0573) });
        Quad(tintG, new[] { (0.488, 0.4195, -0.0466), (-0.478, 0.4195, -0.0466), (0.488, -0.4195, -0.0466), (-0.478, -0.4195, -0.0466) });
        // strips: v0 at -Y (bottom) — the dump's exact tables (u along z, v bottom→top)
        Quad(Strip(halfLr), new[] { (-0.491, -0.4221, -0.0076), (-0.491, -0.4221, -0.0579), (-0.491, 0.4221, -0.0076), (-0.491, 0.4221, -0.0579) });
        Quad(Strip(halfRr), new[] { (-0.491, -0.4221, 0.0579), (-0.491, -0.4221, 0.0076), (-0.491, 0.4221, 0.0579), (-0.491, 0.4221, 0.0076) });
        Quad(Strip(halfL), new[] { (0.494, -0.4221, -0.0579), (0.494, -0.4221, -0.0076), (0.494, 0.4221, -0.0579), (0.494, 0.4221, -0.0076) });
        Quad(Strip(halfR), new[] { (0.494, -0.4221, 0.0076), (0.494, -0.4221, 0.0579), (0.494, 0.4221, 0.0076), (0.494, 0.4221, 0.0579) });

        // Plastic: LiteBox's SHIPPED DoubleJewelCase model (exported once from LB's live scene — LB builds this
        // one procedurally, no embedded resource exists; the one-shot exporter lived in git history, commit
        // ee76135, should it ever need regenerating against a future LB).
        var shipped = LbCaseObj.Load("DoubleJewelCase");
        if (shipped != null) grp.Children.Add(shipped.Clone());
        return grp;
    }

    // ── ITERATION 4b: LONG JEWEL CASE (Sega long box) — dump-exact structure:
    //   4 insert quads: front art X[-0.2954..0.3376] Z=+0.055 (Grid 703.267×1000 transparent, BackMat grey),
    //   back scan X±0.346 Z=-0.055 (Grid grey bg), left/right = Box - Spine scan bare-Image UNIFORM CENTERED
    //   (natural size, not stretched) — no clear-logo strips on this type. Plastic = embedded LongJewelCaseObj
    //   with Translate(0.056,-0.146,-0.04) → Scale(0.488) → Scale(1,1,1.459) (dump-verbatim).
    private static Model3D? BuildLongJewel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform)
    {
        var plastic = LbCaseObj.Load("LongJewelCase");
        if (plastic == null) return null;

        var front = LoadBitmap(ResolveArt(platform, gameTitle, Media.MediaResolver.Front));
        var backImg = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Back" }));
        var spineImg = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Spine" }));

        var grey = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        var clear = System.Windows.Media.Colors.Transparent;
        Material frontMat = front != null ? FaceMaterial(front, System.Windows.Media.Stretch.Fill, 703.2674123689327, 1000, clear)
                                          : new DiffuseMaterial(new SolidColorBrush(grey));
        Material backMat = backImg != null ? FaceMaterial(backImg, System.Windows.Media.Stretch.Fill, 703.2674123689327, 1000, grey)
                                           : FaceMaterialNoImage(703.2674123689327, 1000, grey);
        Material sideMat = spineImg != null
            ? new DiffuseMaterial(new VisualBrush(new System.Windows.Controls.Image
              { Source = spineImg, Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center })
              { Stretch = System.Windows.Media.Stretch.Fill })
            : new DiffuseMaterial(new SolidColorBrush(grey));
        var greySolid = new DiffuseMaterial(new SolidColorBrush(grey));

        var grp = new Model3DGroup();
        void Quad(Material mat, Material? back, (double x, double y, double z)[] p)
        {
            var mesh = new MeshGeometry3D();
            var uv = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };
            for (int i = 0; i < 4; i++)
            {
                mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].Item1, uv[i].Item2));
            }
            foreach (var ix in new[] { 3, 0, 2, 3, 1, 0 }) mesh.TriangleIndices.Add(ix);
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = back });
        }
        Quad(frontMat, greySolid, new[] { (-0.2954, 0.491, 0.055), (0.3376, 0.491, 0.055), (-0.2954, -0.491, 0.055), (0.3376, -0.491, 0.055) });
        Quad(backMat, greySolid, new[] { (0.346, 0.491, -0.055), (-0.346, 0.491, -0.055), (0.346, -0.491, -0.055), (-0.346, -0.491, -0.055) });
        Quad(sideMat, null, new[] { (-0.346, 0.491, -0.055), (-0.346, 0.491, 0.055), (-0.346, -0.491, -0.055), (-0.346, -0.491, 0.055) });
        Quad(sideMat, null, new[] { (0.3474, 0.491, 0.055), (0.3474, 0.491, -0.055), (0.3474, -0.491, 0.055), (0.3474, -0.491, -0.055) });

        var tg = new Transform3DGroup();
        tg.Children.Add(new TranslateTransform3D(0.056, -0.146, -0.04));
        tg.Children.Add(new ScaleTransform3D(0.488, 0.488, 0.488));
        tg.Children.Add(new ScaleTransform3D(1, 1, 1.459));
        grp.Children.Add(new Model3DGroup { Children = { plastic.Clone() }, Transform = tg });
        return grp;
    }

    // ── ITERATION 4a: DVD CASE — everything from LB's embedded GenericCaseObj ("o DVDcase", TWO usemtl
    // segments): the EMPTY-named segment = the paper WRAP (LB textures it with a composed [back|spine|front]
    // sheet, its UVs are authored in the obj), the "case" segment = the plastic shell whose MTL colour is a
    // TEMPLATE ("Kd {{{CaseColor}}}"). Decoded from the structure dump:
    //   • wrap Material AND BackMaterial = VisualBrush{ Grid(auto×600, bg=cover colour) with 3 auto columns:
    //     col0=Box - Back, col1=Box - Spine, col2=Box - Front, each Image H=600 W=aspect×600, Stretch=Fill }
    //   • cover colour (grid bg) = CoverColor option else corner-average of the front art;
    //     case colour (plastic) = CaseColor option else LB's dvd default #FF1D1D1D
    //   • group transform = LB's Scale (art-aspect-derived) — borrowed from the live model (the oracle), the
    //     full sizing-rule re-implementation comes later like box/jewel dims.
    // TODO: spine fallback when no Box - Spine scan; Full Scan mode; spine/logo rotation options for dvd.
    private static Model3D? BuildDvd(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform)
    {
        // Colours: forced options else derived.
        var front = LoadBitmap(ResolveArt(platform, gameTitle, Media.MediaResolver.Front));
        var backImg = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Back" }));
        var spineImg = LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Spine" }));

        var caseColor = System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x1D);
        if (map != null && map.TryGetValue("CaseColor", out var kc) && int.TryParse(kc, out var kargb))
            caseColor = System.Windows.Media.Color.FromArgb((byte)(kargb >> 24), (byte)(kargb >> 16), (byte)(kargb >> 8), (byte)kargb);
        var coverColor = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        if (map != null && map.TryGetValue("CoverColor", out var cc2) && int.TryParse(cc2, out var cargb))
            coverColor = System.Windows.Media.Color.FromArgb((byte)(cargb >> 24), (byte)(cargb >> 16), (byte)(cargb >> 8), (byte)cargb);
        else if (front != null) coverColor = CornerAverage(front);

        var (obj, names) = LbCaseObj.LoadWithNames("GenericCase", new System.Collections.Generic.Dictionary<string, string>
        { ["CaseColor"] = $"{caseColor.R / 255.0:0.###} {caseColor.G / 255.0:0.###} {caseColor.B / 255.0:0.###}" });
        if (obj == null) return null;

        // FULL-SCAN MODE (same arbiter as the box: spine scan wins; no spine + Box - Full + flag → sheet mode):
        // the WHOLE sheet becomes a plain ImageBrush on the wrap mesh (its authored UVs already lay out
        // back|spine|front), plastic keeps the case colour. Dims: W=min(1,panel/sheetH), H=min(1,sheetH/panel),
        // D=spinePx/sheetH with spinePx=FullImageSpineWidth×sheetW (the dvd ignores the landscape flag).
        bool fullFlag = map != null && map.TryGetValue("UseFullScanImages", out var ufs2) && ufs2.Equals("true", StringComparison.OrdinalIgnoreCase);
        var sheet = fullFlag && spineImg == null ? LoadBitmap(ResolveArt(platform, gameTitle, new[] { "Box - Full" })) : null;
        if (sheet != null)
        {
            var wrapSheet = new DiffuseMaterial(new ImageBrush(sheet) { Stretch = System.Windows.Media.Stretch.Fill });
            var grpS = new Model3DGroup();
            var cloneS = (Model3DGroup)obj.Clone();
            for (int i = 0; i < cloneS.Children.Count && i < names.Count; i++)
                if (names[i].Length == 0 && cloneS.Children[i] is GeometryModel3D gms)
                { gms.Material = wrapSheet; gms.BackMaterial = wrapSheet; }
            foreach (var c in cloneS.Children) grpS.Children.Add(c.Clone());

            double spineFrac2 = 0.143;
            if (map != null && map.TryGetValue("FullImageSpineWidth", out var fsw2))
                double.TryParse(fsw2, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out spineFrac2);
            double shW = sheet.PixelWidth, shH = Math.Max(1, sheet.PixelHeight);
            double spx = spineFrac2 * shW;
            double pan = Math.Max(1, (shW - spx) / 2);
            double a2 = pan / shH;
            double W2 = Math.Min(1, a2), H2 = Math.Min(1, 1 / a2), D2 = spx / shH;
            var bs = cloneS.Bounds;
            if (bs.SizeX > 0 && bs.SizeY > 0 && bs.SizeZ > 0)
                grpS.Transform = new ScaleTransform3D(W2 / bs.SizeX, H2 / bs.SizeY, D2 / bs.SizeZ);
            return grpS;
        }

        // Compose the wrap sheet exactly like LB: 3 auto columns [back|spine|front], images normalized to
        // height 600 (LB's decode convention). A missing image adds NO element — its column collapses to zero
        // width, exactly like LB's grid. Spine column: the scan when present (and enabled), else the CLEAR LOGO
        // rotated 90° with margins (0.015×frontW, 0.05×600) — all three constants probe-derived (AC Wii /
        // SoM / Super Mario Kart give 6.42/12.3/12.3 for front widths 428/820/820).
        var logo = LoadBitmap(ResolveArt(platform, gameTitle, Media.MediaResolver.ClearLogo));
        var sides = ParseSides(map, "SpineRotation");
        var logoSides = ParseSides(map, "LogoRotation");
        double frontW = front != null ? Math.Round(front.PixelWidth * 600.0 / front.PixelHeight) : 820;

        var wrapGrid = new System.Windows.Controls.Grid { Height = 600, Background = new SolidColorBrush(coverColor) };
        for (int i = 0; i < 3; i++)
            wrapGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        void Put(System.Windows.FrameworkElement el, int col) { System.Windows.Controls.Grid.SetColumn(el, col); wrapGrid.Children.Add(el); }
        void Panel(System.Windows.Media.Imaging.BitmapSource? img, int col)
        {
            if (img == null) return;
            double w = Math.Round(img.PixelWidth * 600.0 / img.PixelHeight);
            Put(new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.Fill, Width = w, Height = 600 }, col);
        }
        Panel(backImg, 0);
        if (sides[0] && spineImg != null) Panel(spineImg, 1);
        else if (logoSides[0] && logo != null)
            Put(new System.Windows.Controls.Image
            {
                Source = logo,
                Stretch = System.Windows.Media.Stretch.Uniform,
                LayoutTransform = new System.Windows.Media.RotateTransform(90),
                Margin = new System.Windows.Thickness(Math.Round(frontW * 0.015, 2), 30, Math.Round(frontW * 0.015, 2), 30),
            }, 1);
        Panel(front, 2);
        var wrapMat = new DiffuseMaterial(new VisualBrush(wrapGrid) { Stretch = System.Windows.Media.Stretch.Fill });

        var grp = new Model3DGroup();
        var clone = (Model3DGroup)obj.Clone();
        for (int i = 0; i < clone.Children.Count && i < names.Count; i++)
            if (names[i].Length == 0 && clone.Children[i] is GeometryModel3D gm)
            { gm.Material = wrapMat; gm.BackMaterial = wrapMat; }
        foreach (var c in clone.Children) grp.Children.Add(c.Clone());

        // AUTONOMOUS scale (probe-derived; nothing read from the live model): the obj is normalized so that
        // final W = min(1, art aspect), H = min(1, 1/aspect); depth D = W × (spineW/frontW of the DECODED
        // scans) when a spine scan exists, else defaultD(0.065) × H. Forced ModelSizeString wins outright.
        var (W, H, D) = BoxDims(map, front, 0.065);
        bool forced = map != null && map.ContainsKey("ModelSizeString");
        if (!forced)
        {
            if (spineImg != null && front != null && spineImg.PixelHeight > 0 && front.PixelHeight > 0)
            {
                // spineW/frontW at LB's common decode height (aspect ratios — size-invariant).
                double spineAspect = (double)spineImg.PixelWidth / spineImg.PixelHeight;
                double frontAspect = (double)front.PixelWidth / front.PixelHeight;
                D = W * (spineAspect / frontAspect);
            }
            else
                D = 0.065 * H;
        }
        var bounds = clone.Bounds;
        if (bounds.SizeX > 0 && bounds.SizeY > 0 && bounds.SizeZ > 0)
            grp.Transform = new ScaleTransform3D(W / bounds.SizeX, H / bounds.SizeY, D / bounds.SizeZ);
        return grp;
    }

    // Grey-grid face (the insert back when no back image): brush = empty Grid with a solid background — kept as
    // a VisualBrush (not a plain SolidColorBrush) to mirror LB's structure exactly.
    private static Material FaceMaterialNoImage(double gridW, double gridH, System.Windows.Media.Color bg)
    {
        var grid = new System.Windows.Controls.Grid { Width = gridW, Height = gridH, Background = new SolidColorBrush(bg) };
        return new DiffuseMaterial(new VisualBrush(grid) { Stretch = System.Windows.Media.Stretch.Fill });
    }

    // Art resolution for the preview: the preview only has a TITLE (no game Guid), so use MediaResolver's
    // title-only classic disk walk with the STANDARD region order (user RegionPriorities first — real-LaunchBox
    // parity, now that no oracle needs matching). The id-keyed cache bridge is skipped on purpose: with
    // Guid.Empty it would answer null when the cache is Ready.
    private static string? ResolveArt(string? platform, string? title, string[] typeChain)
    {
        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(title)) return null;
        try { return Media.MediaResolver.ImageByTitle(platform, title, typeChain); } catch { return null; }
    }

    // VisualBrush face material exactly as LB composes it: Grid sized dim×1000 px, case-colour background,
    // one Image child (Fill for the front art, Uniform for the centred clear logo).
    private static Material FaceMaterial(System.Windows.Media.Imaging.BitmapSource img, System.Windows.Media.Stretch stretch,
                                         double gridW, double gridH, System.Windows.Media.Color bg)
    {
        var grid = new System.Windows.Controls.Grid { Width = gridW, Height = gridH, Background = new SolidColorBrush(bg) };
        grid.Children.Add(new System.Windows.Controls.Image { Source = img, Stretch = stretch });
        return new DiffuseMaterial(new VisualBrush(grid) { Stretch = System.Windows.Media.Stretch.Fill });
    }

    private static System.Windows.Media.Imaging.BitmapSource? LoadBitmap(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // no file lock kept
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }


    public void Dispose() { try { _host.Dispose(); } catch { } }
}
