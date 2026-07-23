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

    /// <summary>Snap the pose to absolute angles (no animation) — used to re-sync with the oracle after LB
    /// rebuilds its model (its pose resets). Harness-only; irrelevant without a live zone.</summary>
    public void SetPose(double yawDeg, double pitchDeg)
    {
        _yawTarget = yawDeg; _pitchTarget = pitchDeg;
        _yawRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        _pitchRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        _yawRot.Angle = yawDeg; _pitchRot.Angle = pitchDeg;
    }

    /// <summary>Zoom = camera distance multiplier on the own fixed camera.</summary>
    public void SetZoom(double zoom)
    {
        if (_viewport.Camera is PerspectiveCamera pc) pc.Position = new Point3D(0, 0, 2 * zoom);
    }

    /// <summary>Reproduce LB's model in our viewport. Lights are always cloned from LB (until reproduced).
    /// Geometry: for types we've reproduced (box) we BUILD IT OURSELVES (geometry + composed materials);
    /// for the rest we clone LB's group so the zone still shows something to compare against.</summary>
    public void CaptureFrom(CoreModelHost.Preview? lb, System.Collections.Generic.Dictionary<string, string>? map,
                            string? gameTitle = null, string? platform = null)
    {
        try
        {
            _modelHost.Content = null;
            if (lb == null) return;

            // Camera and lights are the zone's OWN (autonomous constants set in the ctor) — nothing cloned.
            var geom = lb.BuiltGeometry();
            if (geom == null) return;
            if (DumpStructure) DumpGroup(geom);

            string type = map != null && map.TryGetValue("ModelType", out var t) ? t : "";
            Model3D? own = type switch                              // reproduced types build their own model
            {
                "box" => BuildBox(geom, map, gameTitle, platform),
                "jewelCase" => BuildJewel(map, gameTitle, platform),
                "dvd" => BuildDvd(geom, map, gameTitle, platform),
                "longJewelCase" => BuildLongJewel(map, gameTitle, platform),
                "doubleJewelCase" => BuildDoubleJewel(geom, map, gameTitle, platform),
                _ => null,
            };
            _modelHost.Content = own ?? geom.Clone();               // else clone LB's (comparison fallback)
        }
        catch (Exception ex) { Console.WriteLine("[homemodel] capture: " + ex.Message); }
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
    private static Model3D? BuildBox(Model3DGroup lb, System.Collections.Generic.Dictionary<string, string>? map,
                                     string? gameTitle, string? platform)
    {
        var b = lb.Bounds;
        if (b.IsEmpty) return null;
        double hw = b.SizeX / 2, hh = b.SizeY / 2, hd = b.SizeZ / 2;

        string? frontPath = ResolveArt(platform, gameTitle, Media.MediaResolver.Front);
        string? logoPath = ResolveArt(platform, gameTitle, Media.MediaResolver.ClearLogo);
        string? spinePath = ResolveArt(platform, gameTitle, new[] { "Box - Spine" });
        string? backPath = ResolveArt(platform, gameTitle, new[] { "Box - Back" });
        if (DumpStructure) Console.WriteLine($"[homemodel] box art: title='{gameTitle}' front={frontPath ?? "NULL"} logo={logoPath ?? "NULL"} spine={spinePath ?? "NULL"} back={backPath ?? "NULL"}");
        var front = LoadBitmap(frontPath);
        var logo = LoadBitmap(logoPath);
        var spine = LoadBitmap(spinePath);
        var back = LoadBitmap(backPath);

        // Case colour: CoverColor option (signed-ARGB string), else corner average of the front art.
        var caseColor = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        if (map != null && map.TryGetValue("CoverColor", out var cc) && int.TryParse(cc, out var argb))
            caseColor = System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        else if (front != null) caseColor = CornerAverage(front);
        var solid = new DiffuseMaterial(new SolidColorBrush(caseColor));

        var frontMat = front != null ? FaceMaterial(front, System.Windows.Media.Stretch.Fill, b.SizeX * 1000, b.SizeY * 1000, caseColor) : solid;
        var logoMat = logo != null ? FaceMaterial(logo, System.Windows.Media.Stretch.Uniform, b.SizeX * 1000, b.SizeZ * 1000, caseColor) : solid;
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
                return isSide ? (RotLogo(SideLogoRot(lr), b.SizeX * 1000, b.SizeZ * 1000), false)
                              : (lr == 0 ? (logoMat, true) : (RotLogo(lr, b.SizeX * 1000, b.SizeZ * 1000), true));
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
    private static Model3D? BuildDoubleJewel(Model3DGroup lbGroup, System.Collections.Generic.Dictionary<string, string>? map,
                                             string? gameTitle, string? platform)
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

        // Plastic: clone the live model's plastic child group (the only Model3DGroup child).
        foreach (var c in lbGroup.Children)
            if (c is Model3DGroup sub) { grp.Children.Add(sub.Clone()); break; }
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
    private static Model3D? BuildDvd(Model3DGroup lbGroup, System.Collections.Generic.Dictionary<string, string>? map,
                                     string? gameTitle, string? platform)
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

        // LB's art-aspect-derived Scale, borrowed from the live group (the oracle).
        grp.Transform = lbGroup.Transform?.Clone() as Transform3D;
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
    // title-only classic disk walk. Region order = LB's HARD-CODED list (World first) — the same order the
    // hosted FlowModel's throwaway Game resolves with (its obfuscated getters ignore user RegionPriorities for
    // a game with no Region), so both zones pick the SAME regional scan. The id-keyed cache bridge is skipped
    // on purpose: with Guid.Empty it would answer null when the cache is Ready.
    private static string? ResolveArt(string? platform, string? title, string[] typeChain)
    {
        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(title)) return null;
        try { return Media.MediaResolver.ImageByTitle(platform, title, typeChain, Media.MediaResolver.LbFallbackRegionOrder); } catch { return null; }
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


    // ── structure capture (for reproducing LB's geometry procedurally) ──
    public static bool DumpStructure = false;

    private static int _dumpN;
    private static void DumpGroup(Model3DGroup g)
    {
        var sb = new System.Text.StringBuilder();
        void L(string s) => sb.AppendLine(s);
        L($"=== LB Model3DGroup structure (dump #{_dumpN}) ===");
        L("group.Transform = " + Describe(g.Transform));
        L("group.Children = " + g.Children.Count);
        DumpChildren(g.Children, L, 0);
        string p = System.IO.Path.Combine(AppContext.BaseDirectory, "model3d-structure.log");
        try { if (_dumpN++ == 0) System.IO.File.WriteAllText(p, sb.ToString()); else System.IO.File.AppendAllText(p, sb.ToString()); } catch { }
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
                    if (mesh.Positions.Count <= 120)   // small mesh (quad/box/wrap) → dump exact vertex data to reproduce
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
        VisualBrush vb => "Visual{" + DescribeVisual(vb.Visual, 0) + "} viewbox=" + vb.Viewbox + " stretch=" + vb.Stretch,
        _ => b.GetType().Name,
    };

    // Decode a VisualBrush's visual tree — this is where LB composes the box faces (art + borders + text).
    private static string DescribeVisual(Visual? v, int depth)
    {
        if (v == null || depth > 6) return "null";
        var sb = new System.Text.StringBuilder();
        switch (v)
        {
            case System.Windows.Controls.Image im:
                sb.Append($"Image[{(im.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth}x{(im.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelHeight} stretch={im.Stretch} W={im.Width} H={im.Height} margin={im.Margin} hAlign={im.HorizontalAlignment} vAlign={im.VerticalAlignment} col={System.Windows.Controls.Grid.GetColumn(im)} layoutT={DescribeT2(im.LayoutTransform)} renderT={DescribeT2(im.RenderTransform)} src={ImgSrc(im.Source)} saved={SaveTex(im.Source)}]");
                break;
            case System.Windows.Controls.StackPanel sp:
                sb.Append($"StackPanel[{sp.Orientation} {sp.Width}x{sp.Height}]");
                break;
            case System.Windows.Controls.Border bd:
                sb.Append($"Border[bg={DescribeBrush(bd.Background)} pad={bd.Padding} bthick={bd.BorderThickness}]");
                break;
            case System.Windows.Controls.TextBlock tb:
                sb.Append($"Text['{tb.Text}' size={tb.FontSize} fg={DescribeBrush(tb.Foreground)}]");
                break;
            case System.Windows.Shapes.Rectangle rc:
                sb.Append($"Rect[fill={DescribeBrush(rc.Fill)} {rc.Width}x{rc.Height}]");
                break;
            case System.Windows.FrameworkElement fe:
                string feBg = (fe as System.Windows.Controls.Panel)?.Background is { } pb ? DescribeBrush(pb) : "-";
                sb.Append($"{fe.GetType().Name}[{fe.Width}x{fe.Height} actual={fe.ActualWidth:0.#}x{fe.ActualHeight:0.#} bg={feBg}]");
                break;
            default:
                sb.Append(v.GetType().Name);
                break;
        }
        int n = VisualTreeHelper.GetChildrenCount(v);
        if (n > 0)
        {
            sb.Append("(");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(", "); sb.Append(DescribeVisual(VisualTreeHelper.GetChild(v, i) as Visual, depth + 1)); }
            sb.Append(")");
        }
        return sb.ToString();
    }

    // Save a face texture bitmap to PNG next to the log (identify WHAT image LB puts on each face).
    private static int _texN;
    private static string SaveTex(ImageSource? s)
    {
        try
        {
            if (s is not System.Windows.Media.Imaging.BitmapSource bs) return "-";
            string p = System.IO.Path.Combine(AppContext.BaseDirectory, $"model3d-tex{_texN++}.png");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
            using var fs = System.IO.File.Create(p);
            enc.Save(fs);
            return System.IO.Path.GetFileName(p);
        }
        catch (Exception ex) { return "err:" + ex.Message; }
    }

    private static string DescribeT2(System.Windows.Media.Transform? t) => t switch
    {
        null => "null",
        System.Windows.Media.RotateTransform r => $"Rotate({r.Angle})",
        System.Windows.Media.ScaleTransform s => $"Scale({s.ScaleX},{s.ScaleY})",
        System.Windows.Media.TransformGroup g => "Group[" + string.Join(",", System.Linq.Enumerable.Select(g.Children, c => DescribeT2(c))) + "]",
        System.Windows.Media.MatrixTransform m => m.Matrix.IsIdentity ? "Identity" : "Matrix(" + m.Matrix + ")",
        _ => t.GetType().Name,
    };

    private static string ImgSrc(ImageSource? s)
    {
        try
        {
            if (s is System.Windows.Media.Imaging.BitmapImage bi && bi.UriSource != null) return bi.UriSource.ToString();
            if (s is System.Windows.Media.Imaging.BitmapFrame bf && bf.BaseUri != null) return bf.BaseUri.ToString();
            return s?.GetType().Name ?? "null";
        }
        catch { return "?"; }
    }

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
