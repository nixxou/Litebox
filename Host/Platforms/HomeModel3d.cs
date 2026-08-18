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
    /// <summary>Dev harness only (JewelRenderProbe): omit the plastic shell to inspect the paper insert alone.</summary>
    internal static bool DebugSkipPlastic = false;
    /// <summary>Dev harness only: omit the jewel spine cap/label quad (isolation of depth interactions).</summary>
    internal static bool DebugSkipCap = false;
    /// <summary>Dev harness only: paint the jewel BACK insert Material RED and its BackMaterial GREEN —
    /// tells which face of that quad a given camera angle is actually showing.</summary>
    internal static bool DebugBackFaces = false;

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

    /// <summary>Rotate by the given degree deltas IMMEDIATELY (no animation) — 1:1 mouse-drag tracking.
    /// Restarting the 400 ms ease on every mouse-move (OrbitBy) both lags and stutters; a drag wants the
    /// angle to follow the pointer directly.</summary>
    public void OrbitImmediate(double dYawDeg, double dPitchDeg)
    {
        _yawRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        _pitchRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        _yawTarget += dYawDeg;
        _pitchTarget += dPitchDeg;
        _yawRot.Angle = _yawTarget;
        _pitchRot.Angle = _pitchTarget;
    }

    /// <summary>Zoom = camera distance multiplier on the own fixed camera.</summary>
    public void SetZoom(double zoom)
    {
        if (_viewport.Camera is PerspectiveCamera pc) pc.Position = new Point3D(0, 0, 2 * zoom);
    }

    /// <summary>Match the WinForms host's backdrop exactly (root grid + ElementHost) — a shade
    /// difference with a layer above (the anti-flicker PNG) reads as a flash at swap time.</summary>
    public void SetBackground(System.Drawing.Color c)
    {
        _root.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        _host.BackColor = c;
    }


    /// <summary>Build the model for the given settings map + sample game. Unknown/absent ModelType renders as
    /// a box (what LB does for null settings). <paramref name="imgOv"/> = optional per-slot image override
    /// (front/back/spine/logo/full — see Model3dImageStore); <paramref name="gameId"/> = the game behind the
    /// title when there is one (the platform-settings preview has only a sample title).
    /// <paramref name="overridden"/> = the editor's Override box is ticked, i.e. <paramref name="map"/> is a
    /// human choice rather than a fallback — the preview must show the auto rules standing down exactly as
    /// the bake will, or the panel would promise a shape the library never renders.</summary>
    public void Build(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle, string? platform,
                      System.Collections.Generic.Dictionary<string, string>? imgOv = null, Guid gameId = default,
                      bool overridden = false)
    {
        try
        {
            string platKey = ScrapeKeyOf(platform);
            bool multiDisc = CanAutoDoubleJewel(platKey) && PreviewIsMultiDisc(gameId);
            var art = Model3d.Model3dArt.Resolve(map, platform, gameId, gameTitle, imgOv, platKey, multiDisc, overridden);
            _modelHost.Content = BuildModel(map, gameTitle, art);
        }
        catch (Exception ex) { Console.WriteLine("[homemodel] build: " + ex.Message); _modelHost.Content = null; }
    }

    /// <summary>The platform's Scrape As when it has one, else its own name — the identity the auto rules
    /// key on, so a custom-named PS1 library behaves like the real thing.</summary>
    private static string? ScrapeKeyOf(string? platform)
    {
        if (string.IsNullOrEmpty(platform)) return platform;
        try
        {
            var sa = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetPlatformByName(platform)?.ScrapeAs;
            return string.IsNullOrWhiteSpace(sa) ? platform : sa;
        }
        catch { return platform; }
    }

    /// <summary>Preview-side disc count (Model3dCache owns the one used for the bake). Guid.Empty — the
    /// platform-settings preview, which has a sample title and no game — is never multi-disc.</summary>
    private static bool PreviewIsMultiDisc(Guid gameId)
    {
        if (gameId == Guid.Empty) return false;
        try
        {
            var g = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetGameById(gameId.ToString());
            if (g == null) return false;
            var apps = g.GetAllAdditionalApplications();
            if (apps == null) return false;
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (var a in apps)
            {
                int? d = null;
                try { d = a.Disc; } catch { }
                if (d.HasValue && seen.Add(d.Value) && seen.Count >= 2) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>The pure model factory behind <see cref="Build"/> — usable without a viewport (GLB baking,
    /// offscreen thumb rendering). Must run on an STA thread (the composed textures are WPF visuals).
    /// <paramref name="art"/> is ALREADY resolved (see <see cref="Model3d.Model3dArt"/>): the builders load
    /// paths, they never look for them — which is what lets the cache key describe exactly what will be
    /// consumed. The title is still needed beyond the art: the jewel's plain-text edge strip prints it.</summary>
    internal static Model3D? BuildModel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                        Model3d.Model3dArt art)
    {
        string type = map != null && map.TryGetValue("ModelType", out var t) ? t : "box";
        type = RefineCaseType(type, art);
        return type switch
        {
            "jewelCase" => BuildJewel(map, gameTitle, art),
            "dvd" => BuildDvd(map, gameTitle, art),
            "longJewelCase" => BuildLongJewel(map, gameTitle, art),
            "doubleJewelCase" => BuildDoubleJewel(map, gameTitle, art),
            "cassetteCase" => BuildCassette(map, gameTitle, art),
            _ => BuildBox(map, gameTitle, art),
        };
    }

    // ── AUTO JEWEL (Model3dAutoJewelCase, default ON) — a deliberate DIVERGENCE from LaunchBox ──
    // LB pins Sega Saturn and Sega CD to longJewelCase, the US long box. Their JAPANESE releases came in an
    // ordinary CD jewel case, and the two shapes are nowhere near each other: the long box's front quad is
    // 0.633 x 0.982 (aspect 0.645), the jewel case's is 0.912 x 0.863 (aspect 1.057). Face artwork is
    // Fill-STRETCHED onto whichever quad it lands on — nothing adapts — so a square-ish jewel scan on a long
    // box is squeezed to roughly 60% of its width, which is what the JP half of those libraries looked like.
    // Choosing the case whose own front the artwork actually fits costs one image-header read per model.
    //
    // ONE-DIRECTIONAL on purpose: nothing is ever promoted from jewel to long. The jewelCase platforms carry
    // {Resources} spine presets (PS1, Dreamcast) authored against that builder, and the long-box builder has
    // no clear-logo strips to put them on.
    private const double JewelFrontAspect = 0.912 / 0.863;    // 1.0568 — the jewel case's own front quad
    private const double LongFrontAspect = 0.6330 / 0.982;    // 0.6446 — the long box's

    /// <summary>Where the two shapes stop competing: the LOG-space midpoint, so "twice as wide" and "half as
    /// wide" sit equally far from each side (a plain average would bias toward the wider case). RAISED by 5%
    /// — the bar to leave the long box is deliberately above the true midpoint, so artwork that lands in the
    /// ambiguous middle keeps LaunchBox's own default and the divergence has to earn itself. Landmarks: a CD
    /// jewel insert (120x120 = 1.000) and a whole jewel front (125x142 = 0.880) clear it; a Sega CD long box
    /// scan (137x190 = 0.721) does not.</summary>
    private static readonly double JewelCrossover = Math.Sqrt(JewelFrontAspect * LongFrontAspect) * 1.05;

    // ── AUTO DOUBLE JEWEL (Model3dAutoDoubleJewel, default ON) — also a divergence from LaunchBox ──
    // A multi-disc release MAY sit in a double-width jewel case, and LaunchBox has no way to know: ModelType
    // is per platform, so every PS1 game is a single jewel until someone overrides that one game by hand.
    //
    // Two signals are required TOGETHER, because each one alone is wrong in a way the other covers:
    //   • the disc count alone does not imply a thick case — plenty of multi-disc releases ship in a
    //     single-width case with stacked trays (the Japanese and North American Final Fantasy 7 both do);
    //   • the spine scan alone is not a measurement — its ratio depends on how the strip was cropped, and
    //     the SAME game measures 0.048 in one scan set and 0.105 in another.
    // Requiring both means the case has to be declared multi-disc AND look thick in its own artwork.
    //
    // The band is CALIBRATED on measured scans, not on millimetres. The four Final Fantasy 7 spines in the
    // reference library split cleanly in two with nothing in between — 0.0484 (Japan) and 0.0500 (North
    // America) against 0.0956 (Asia) and 0.1047 (France), a 1.91x gap — and the user confirmed the last two
    // are the double cases. That the two groups differ by 2.04x while a real double-to-single case differs
    // by 2.12x is what says the scan preserves the RATIO faithfully even though it shows the printed strip
    // (about half the case width) rather than the case: absolute millimetres are not recoverable, the
    // proportion is. PS1 only — the band is calibrated on PS1 scans and claims nothing about any other
    // platform's artwork conventions.
    private const string DoubleJewelPlatform = "Sony Playstation";
    private const double DoubleSpineMin = 0.09;   // below: the single-width cases, measured at 0.048-0.050
    private const double DoubleSpineMax = 0.11;   // above: not a spine strip — a wrap or a mis-slotted cover

    /// <summary>Is <paramref name="platformKey"/> a platform the double-jewel rule is calibrated for? Public
    /// so Model3dCache can skip the disc lookup for every other platform.</summary>
    internal static bool CanAutoDoubleJewel(string? platformKey)
        => string.Equals(platformKey, DoubleJewelPlatform, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refine the platform's model type against what this game's own artwork measures. Two steps,
    /// in order, so they can chain — a Japanese two-disc Saturn release goes long box → jewel → double jewel.
    /// Anything unreadable, out of range, or with the matching option off leaves the type untouched.
    ///
    /// AUTO MODE ONLY. A ModelSettings block somebody wrote — per game or per platform — ends this function
    /// before it starts: these rules exist to make the DEFAULT smarter, and a default that argued with an
    /// explicit choice would just be a bug wearing a feature's clothes.</summary>
    private static string RefineCaseType(string type, Model3d.Model3dArt art)
    {
        if (art.Overridden) return type;

        // 1) long box → jewel case, on the FRONT art's shape.
        if (string.Equals(type, "longJewelCase", StringComparison.OrdinalIgnoreCase)
            && Model3d.Model3dOptions.AutoJewelCase
            && ImageAspect(art.Front) > JewelCrossover)          // 0 (no readable front) keeps the default
            type = "jewelCase";

        // 2) jewel case → double jewel: PS1, declared multi-disc, AND a spine scan that measures thick.
        //    Deliberately the game's OWN scan (art.Spine) and never the {Resources} preset — a preset is one
        //    generic strip shipped with LiteBox, identical for every game on the platform, so it measures the
        //    artwork pack rather than the case and would flip the whole platform at once.
        if (string.Equals(type, "jewelCase", StringComparison.OrdinalIgnoreCase)
            && Model3d.Model3dOptions.AutoDoubleJewel
            && CanAutoDoubleJewel(art.Platform) && art.MultiDisc)
        {
            double t = SpineThickness(art.Spine);
            if (t >= DoubleSpineMin && t <= DoubleSpineMax) type = "doubleJewelCase";
        }
        return type;
    }

    /// <summary>A spine scan's depth-over-height, orientation-independent. Spine strips are scanned both
    /// upright and lying down (BuildBox rotates the lying ones before use), and a case is thinner than it is
    /// tall either way — so the short side over the long side measures the same case from both. 0 = unknown.</summary>
    private static double SpineThickness(string? path)
    {
        double a = ImageAspect(path);
        return a <= 0 ? 0 : a > 1 ? 1 / a : a;
    }

    /// <summary>A spine scan the right way up. Strips are scanned both upright and LYING DOWN (a Game Boy
    /// spine comes in at 230x41), but every builder's geometry contract is PORTRAIT: the spine's height runs
    /// along the case height. A lying scan therefore paints the side sideways — and in BuildBox, which takes
    /// the box depth straight from spineW/spineH, it also explodes that depth into a slab. Rotated 90°
    /// CLOCKWISE: the US top-to-bottom reading direction, matching how portrait scans are oriented (verified
    /// on real NA scans).
    ///
    /// This lived inside BuildBox alone, so the four other builders drew lying scans lying down. It is the
    /// same defect in all five, hence one helper — the alternative was fixing the two jewel builders that
    /// the double-case detection newly depends on and leaving the identical bug in dvd and long box.
    /// Only the GAME'S OWN scan is passed through here; the {Resources} presets already ship upright.</summary>
    private static System.Windows.Media.Imaging.BitmapSource? UprightSpine(System.Windows.Media.Imaging.BitmapSource? spine)
    {
        if (spine == null || spine.PixelWidth <= spine.PixelHeight) return spine;
        var rot = new System.Windows.Media.Imaging.TransformedBitmap(spine, new System.Windows.Media.RotateTransform(90));
        rot.Freeze();
        return rot;
    }

    /// <summary>An image's width/height read from its HEADER — no pixel decode, so this stays
    /// affordable on the bake path where the builders decode the image again anyway. 0 when unknown.</summary>
    private static double ImageAspect(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return 0;
            using var s = System.IO.File.OpenRead(path);
            var f = System.Windows.Media.Imaging.BitmapFrame.Create(
                s, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            return f.PixelHeight > 0 ? (double)f.PixelWidth / f.PixelHeight : 0;
        }
        catch { return 0; }
    }

    /// <summary>A "{Resources}\&lt;preset&gt;" key with its AUTO-DETECT version resolved. A key that already
    /// carries " - &lt;version&gt;" is an EXPLICIT user pick (Black/White/European Version) and is returned
    /// untouched; only the bare Auto-Detect form gets a suffix computed (region for PAL, measured
    /// black-vs-white artwork for Dreamcast NTSC — see LbCaseObj.AutoVersionSuffix).</summary>
    private static string ResolvePresetKey(string key, System.Windows.Media.Imaging.BitmapSource? scan,
                                           System.Windows.Media.Imaging.BitmapSource? front,
                                           string? scanRegion, string? frontRegion)
        => key.Contains(" - ", StringComparison.Ordinal)
            ? key
            : key + LbCaseObj.AutoVersionSuffix(key, scan, front, scanRegion, frontRegion);

    /// <summary>Display an ALREADY-BUILT (e.g. GLB-cache-loaded, frozen) model — skips the builders entirely.</summary>
    public void SetModel(Model3D? model) => _modelHost.Content = model;

    /// <summary>Set the model pose directly (degrees), without animation — the detail block's default slight
    /// rotation. Also resets the orbit targets so a subsequent drag continues from this pose.</summary>
    public void SetPose(double yawDeg, double pitchDeg)
    {
        _yawRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        _pitchRot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        _yawTarget = yawDeg; _pitchTarget = pitchDeg;
        _yawRot.Angle = yawDeg; _pitchRot.Angle = pitchDeg;
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
    //   forced ModelSizeString "w;h;d" → NORMALISED to the unit box (see below). Else the art aspect a = W/H of the decoded front:
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
            {
                // A forced size states PROPORTIONS, not scene units — so it is scaled onto the same unit box
                // the automatic path below always produces (max(W,H) = 1). LB's own Genesis and Master System
                // defaults are "5;7.165;1": the cardboard box measured in INCHES. Taken verbatim that builds a
                // model seven units tall around a camera sitting at 2.76 — the extreme zoom a fresh install
                // showed on those two platforms, baked into the GLB and its thumb. Dividing the three by
                // max(W,H) keeps the box's aspect AND its depth ratio while restoring the scale the framing
                // law assumes; a size already written in unit terms (max(W,H) = 1) passes through unchanged.
                double k = Math.Max(fw, fh);
                if (k > 0 && double.IsFinite(k)) return (fw / k, fh / k, fd / k);
                // k unusable (zeros, negatives, NaN) — a degenerate model renders as nothing at all, so the
                // art-derived path below is the better answer than honouring the garbage.
            }
        }
        double a = front != null && front.PixelHeight > 0 ? (double)front.PixelWidth / front.PixelHeight : 0.766;
        return (Math.Min(1, a), Math.Min(1, 1 / a), defaultD);
    }

    private static Model3D? BuildBox(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                     Model3d.Model3dArt art)
    {
        string? frontPath = art.Front;
        string? logoPath = art.Logo;
        string? spinePath = art.Spine;
        string? backPath = art.Back;
        // Missing front → LB's NoImage placeholder (shipped): texture, dims and corner colour all follow.
        var front = LoadBitmap(frontPath) ?? LbCaseObj.SpineImage("NoImage");
        var logo = LoadBitmap(logoPath);
        System.Windows.Media.Imaging.BitmapSource? spine = UprightSpine(LoadBitmap(spinePath));   // see UprightSpine
        var back = LoadBitmap(backPath);

        // FULL-SCAN MODE (probe-decoded priority): when UseFullScanImages is on, the game's SPINE SCAN is the
        // arbiter — a spine scan forces the composed per-face mode; with NO spine scan and a Box - Full image
        // present, LB slices the SINGLE full sheet by ALIGNMENT CROPPING: every textured face gets the whole
        // image UniformToFill — front hAlign=Right, back hAlign=Left, sides hAlign=Center — and top/bottom are
        // the full scan's corner-average. (Flag off → Box - Full is ignored entirely.)
        var fullImg = art.FullScan && spine == null ? LoadBitmap(art.Full) : null;

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
        {
            (W, H, D) = BoxDims(map, front, 0.143);
            // Box depth = the SPINE SCAN's decoded aspect when one exists (probe-decoded on the exotic-aspect
            // matrix: a 300×600 spine → D=0.5, SoM's 86×600 → 0.143 — the old "constant 0.143" was that
            // coincidence). No spine → 0.143 stays the default.
            if (spine != null && spine.PixelHeight > 0 && (map == null || !map.ContainsKey("ModelSizeString")))
                D = (double)spine.PixelWidth / spine.PixelHeight;
        }
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
    private static Model3D? BuildJewel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                       Model3d.Model3dArt art)
    {
        // Spine mode first — it selects the PLASTIC MODEL (oracle-dumped 2026-07-28, all modes):
        //   FrontSpineIsClear=true  (empty clear / {Resources} presets / custom clear) → ClearSpineJewelCase
        //     (6 segments — see-through hinge) + the wrapped spine CAP quad carrying the spine image;
        //   FrontSpineIsClear=false (solid / custom solid) → JewelCase (5 segments — solid hinge), NO wrapped
        //     cap; custom solid instead gets a FLAT LABEL quad glued on the hinge FRONT (Z=+0.0271).
        string spineSpec = map != null && map.TryGetValue("FrontSpineImage", out var ss) ? ss : "";
        bool spineClear = map != null && map.TryGetValue("FrontSpineIsClear", out var sc) && sc == "true";
        var plastic = LbCaseObj.Load(spineClear ? "ClearSpineJewelCase" : "JewelCase") ?? LbCaseObj.Load("JewelCase");
        if (plastic == null) return null;   // no embedded model → keep LB's clone (comparison still works)

        string? frontPath = art.Front;
        string? region = LbCaseObj.RegionOfImagePath(frontPath);   // drives the Auto-Detect spine version
        var front = LoadBitmap(frontPath);
        var logo = LoadBitmap(art.Logo);

        var grey = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        var clear = System.Windows.Media.Colors.Transparent;
        var backScan = LoadBitmap(art.Back);
        string? scanPath = art.Spine;
        string? scanRegion = LbCaseObj.RegionOfImagePath(scanPath);
        var scan = UprightSpine(LoadBitmap(scanPath));
        string logoFont = map != null && map.TryGetValue("LogoFont", out var lf) ? lf : "";
        var textColor = System.Windows.Media.Colors.White;
        if (map != null && map.TryGetValue("CaseColor", out var tc) && int.TryParse(tc, out var targb))
            textColor = System.Windows.Media.Color.FromArgb((byte)(targb >> 24), (byte)(targb >> 16), (byte)(targb >> 8), (byte)targb);

        Material frontMat = front != null ? FaceMaterial(front, System.Windows.Media.Stretch.Fill, 1000, 889.628809154057, clear)
                                          : new DiffuseMaterial(new SolidColorBrush(grey));
        Material backMat = backScan != null ? FaceMaterial(backScan, System.Windows.Media.Stretch.Fill, 1000, 889.628809154057, grey)
                                            : FaceMaterialNoImage(1000, 889.628809154057, grey);
        // The "Front Spine Image" — oracle-decoded semantics (dumps 2026-07-28, every mode, scan or not):
        //   • the spine image is INDEPENDENT of the game's Box - Spine scan — the scan ALWAYS rides the edge
        //     strips (all modes confirmed), while the spine image lives on its own quad(s):
        //   • IsClear=true → the wrapped 8-vert CAP (LB leaf0, emitted FIRST): Grid 120×1000 with a
        //     TRANSPARENT background + the image Fill — preset ({Resources}), custom file, or NO image at all
        //     ("Empty Clear Spine": the cap exists with an empty Image — fully transparent hinge). Its
        //     back-plane part faces +Z (seen from the FRONT through the clear hinge — hence the name) and its
        //     left part faces INWARD (culled from outside, so the side view shows the scan strips).
        //   • IsClear=false + custom path ("Custom Solid Spine") → NO wrapped cap; instead a FLAT LABEL quad
        //     glued on the hinge FRONT: X[-0.499..-0.43], Z=+0.0271, Grid 70×1000 transparent bg + image Fill.
        //   • IsClear=false + no image ("Solid Spine") → no spine quad at all (the solid-hinge plastic IS the
        //     spine look).
        System.Windows.Media.Imaging.BitmapSource? spineImg =
            spineSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase)
                ? LbCaseObj.SpineImage(ResolvePresetKey(spineSpec.Substring(12), scan, front, scanRegion, region), region)
            : spineSpec.Length > 0 ? LoadBitmap(spineSpec)
            : null;
        // Cap only when it has an IMAGE: LB emits an imageless cap for "Empty Clear Spine", but in WPF a
        // VisualBrush is classified opaque (depth-write) even when its visual paints nothing — our imageless
        // cap depth-walled the strips into invisibility at side angles. No quad renders identically to LB's
        // invisible quad, without the wall.
        bool emitCap = spineClear && spineImg != null;
        bool emitLabel = !spineClear && spineImg != null;      // custom solid → flat front label
        Material? spineMat = emitCap ? FaceMaterial(spineImg, System.Windows.Media.Stretch.Fill, 120, 1000, clear)
                           : emitLabel ? FaceMaterial(spineImg!, System.Windows.Media.Stretch.Fill, 70, 1000, clear)
                           : null;
        // Strip background cascade: CoverColor option → corner-average of the front art (the historical
        // #1C1116 was A-Train's) → corner-average of the SPINE image (oracle: a frontless game with the
        // black PS1 preset showed #050505 strips = that preset's corners) → grey.
        var stripBg = System.Windows.Media.Color.FromRgb(0x69, 0x69, 0x69);
        if (map != null && map.TryGetValue("CoverColor", out var cs) && int.TryParse(cs, out var cargb2))
            stripBg = System.Windows.Media.Color.FromArgb((byte)(cargb2 >> 24), (byte)(cargb2 >> 16), (byte)(cargb2 >> 8), (byte)cargb2);
        else if (front != null) stripBg = CornerAverage(front);
        else if (spineImg != null) stripBg = CornerAverage(spineImg);
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
        var paperGrp = new Model3DGroup();   // the printed insert (front/back/spine/edges) — Z-scaled for the depth test
        void Quad(Material mat, Material? back, (double x, double y, double z)[] p, (double u, double v)[] uv, int[] tris)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < p.Length; i++)
            {
                mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].u, uv[i].v));
            }
            foreach (var ix in tris) mesh.TriangleIndices.Add(ix);
            // Explicit OUTWARD flat normal for simple planar quads (4 verts). WPF's auto-generated normals left
            // the thin side faces (spine / left-right edge strips) effectively unlit — they took only ambient,
            // so a dark spine scan never showed, at ANY viewing angle. A per-face normal pointed away from the
            // case centre fixes the diffuse lighting on those faces. (The 8-vert wrapped spine quad keeps auto.)
            if (p.Length == 4)
            {
                var a = new Vector3D(p[1].x - p[0].x, p[1].y - p[0].y, p[1].z - p[0].z);
                var b = new Vector3D(p[2].x - p[0].x, p[2].y - p[0].y, p[2].z - p[0].z);
                var n = Vector3D.CrossProduct(a, b);
                var centre = new Vector3D((p[0].x + p[3].x) / 2, (p[0].y + p[3].y) / 2, (p[0].z + p[3].z) / 2);
                if (Vector3D.DotProduct(n, centre) < 0) n = -n;   // point away from the origin (outward)
                if (n.LengthSquared > 0) { n.Normalize(); for (int i = 0; i < 4; i++) mesh.Normals.Add(n); }
            }
            paperGrp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = back });
        }
        int[] T = { 3, 0, 2, 3, 1, 0 };
        // Spine quad FIRST — LB's own scene order (leaf0), and the depth interactions depend on it: the cap's
        // back-plane part (Z=-0.0203, facing +Z) is drawn before the back insert (Z=-0.0204) so the wrap
        // region of the back shows the SPINE image (the folded flap) — or see-through for an imageless clear
        // cap. Its left part faces INWARD (back=null → culled from outside), so the side view shows the scan
        // strips unobstructed.
        if (emitCap && spineMat != null && !DebugSkipCap)
            Quad(spineMat, null,
                 new[] { (-0.493, 0.4315, -0.0203), (-0.43, 0.4315, -0.0203), (-0.493, -0.4315, -0.0203), (-0.43, -0.4315, -0.0203),
                         (-0.493, 0.4315, 0.0204), (-0.493, 0.4315, -0.0203), (-0.493, -0.4315, 0.0204), (-0.493, -0.4315, -0.0203) },
                 new[] { (0.416, 0d), (1d, 0d), (0.416, 1d), (1d, 1d), (0d, 0d), (0.416, 0d), (0d, 1d), (0.416, 1d) },
                 new[] { 3, 0, 2, 3, 1, 0, 7, 4, 6, 7, 5, 4 });
        // Custom Solid Spine: the flat label glued on the hinge FRONT (dump-exact: X[-0.499..-0.43], Z=+0.0271).
        else if (emitLabel && spineMat != null)
            Quad(spineMat, null,
                 new[] { (-0.499, 0.4315, 0.0271), (-0.43, 0.4315, 0.0271), (-0.499, -0.4315, 0.0271), (-0.43, -0.4315, 0.0271) },
                 new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) }, T);
        // front insert (art), BackMat grey
        Quad(frontMat, new DiffuseMaterial(new SolidColorBrush(grey)),
             new[] { (-0.42, 0.4315, 0.0204), (0.492, 0.4315, 0.0204), (-0.42, -0.4315, 0.0204), (0.492, -0.4315, 0.0204) },
             new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) }, T);
        // back insert (grey grid). Its BackMaterial = the INSIDE of the back wall, seen from the front through
        // the clear hinge — oracle-decoded: the corner-average of the SPINE IMAGE (white for the Dreamcast
        // White preset, ~#050505 for the black PS1 preset — the historical constant was just that), and plain
        // black when there is no spine image (solid / empty clear).
        // Split into TWO single-sided quads (Material+BackMaterial on one quad rendered the Material on BOTH
        // sides in practice): the outer shell shows the back art from behind; the inner wall shows the
        // innerBack colour from the front (through the clear hinge) — LB's leaf2 Material/Back pair.
        var innerBack = spineImg != null ? CornerAverage(spineImg) : System.Windows.Media.Colors.Black;
        var backPos = new[] { (0.492, 0.4315, -0.0204), (-0.492, 0.4315, -0.0204), (0.492, -0.4315, -0.0204), (-0.492, -0.4315, -0.0204) };
        var backUv = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };
        Quad(DebugBackFaces ? new DiffuseMaterial(System.Windows.Media.Brushes.Red) : backMat,
             null, backPos, backUv, T);                                      // outer: back art, seen from behind
        var innerPos = new[] { (0.492, 0.4315, -0.02035), (-0.492, 0.4315, -0.02035), (0.492, -0.4315, -0.02035), (-0.492, -0.4315, -0.02035) };
        Quad(DebugBackFaces ? new DiffuseMaterial(System.Windows.Media.Brushes.Lime)
                            : new DiffuseMaterial(new SolidColorBrush(innerBack)),
             null, innerPos, backUv, new[] { 2, 0, 3, 0, 1, 3 });            // inner wall: nudged inward (no z-fight), reversed winding
        // Edge strip UVs depend on the CONTENT. A spine SCAN maps PLAIN (file top = case top) — orientation
        // verified side-by-side against the LB oracle (solid mode, FF7: "PlayStation" at the top both sides).
        // The historical rotated UVs are the LOGO layout (clear logo laid sideways, opposite directions like
        // a real box) — applying them to a scan drew the spine text mirrored sideways.
        bool scanStrips = scan != null;
        var scanUv = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };   // plain (oracle-verified orientation)
        // left edge strip
        Quad(stripMat, null,
             new[] { (-0.492, 0.4315, -0.0204), (-0.492, 0.4315, 0.0204), (-0.492, -0.4315, -0.0204), (-0.492, -0.4315, 0.0204) },
             scanStrips ? scanUv                                                  // scan → LB's top-down reading
                        : new[] { (1d, 0d), (1d, 1d), (0d, 0d), (0d, 1d) }, T);   // logo reads bottom→top
        // right edge strip
        Quad(stripMat, null,
             new[] { (0.494, 0.4315, 0.0204), (0.494, 0.4315, -0.0204), (0.494, -0.4315, 0.0204), (0.494, -0.4315, -0.0204) },
             scanStrips ? scanUv                                                  // scan → LB's top-down reading
                        : new[] { (0d, 1d), (0d, 0d), (1d, 1d), (1d, 0d) }, T);   // logo reads top→bottom
        grp.Children.Add(paperGrp);

        // plastic case: LB's embedded OBJ, positioned exactly as LB does (Translate THEN Scale, dump-verbatim).
        if (!DebugSkipPlastic)
        {
            var tg = new Transform3DGroup();
            tg.Children.Add(new TranslateTransform3D(-0.031, -0.629, 0.004));
            tg.Children.Add(new ScaleTransform3D(0.707, 0.707, 0.707));
            grp.Children.Add(new Model3DGroup { Children = { plastic.Clone() }, Transform = tg });
        }
        return grp;
    }

    // ── ITERATION 4c: DOUBLE JEWEL CASE — dump-exact structure (re-dumped 2026-08-09, FF7/PS1, 16 leaves):
    //   • FRONT art quad X[-0.42..0.492] Z=+0.0573 — NOT full width: the leftmost 0.073 of the front face is
    //     the HINGE CAP quad X[-0.493..-0.42], textured with the FrontSpineImage preset's DOUBLE-JEWEL
    //     variant (asset "<preset> - NA Double Jewel", 82×861 — the only double variant shipped: the dump's
    //     FRANCE-region FF7 still gets NA). Grid 1000×1000, bg = corner-avg of the cap image, Img Fill.
    //     This cap is the black plastic-looking left border LB shows and LiteBox was missing.
    //     Front tint follows the narrowed front: X[-0.416..0.488] (was [-0.489..0.488] when full-width).
    //   • back art X[-0.482..0.492] Z=-0.0573 + tint X[-0.478..0.488] Z=-0.0466 (unchanged by the cap).
    //     Tints: Diffuse #DE24262C + Specular #34969BA0 pow=18 — the insert seen through the closed lid.
    //   • Every paper leaf in the dump carries BackMaterial = the SAME material (nothing is single-sided).
    //   • FOUR spine strips (two per side, split at z=±0.008..0.058): LB splits the spine image into left/right
    //     HALVES — right side gets [left-half (back), right-half (front)], left side gets the SAME halves
    //     ROTATED 180° (probe-verified against LB's .bbflow-double-jewel-spine cache files, diff 0.04).
    //     Strip brush = Grid[220×1000] transparent + half Image Fill. The GAME's scan feeds the strips; the
    //     cap uses the PRESET even when a scan exists (dump: strips 33/34×640 = the scan's halves, cap 82×861).
    //   • plastic: NOT an embedded obj (LB builds it procedurally — 7 segments, scale 69.78); LiteBox ships a
    //     one-shot export (DoubleJewelCase) whose 7 segments match the dump's leaf9/0..6 exactly.
    //   TODO: DoubleSpineImageMode variants (Single / DualSplitCenter / DualMiddleSeparator) — Automatic split
    //   is what's implemented (observed behaviour with a spine scan).
    private static Model3D? BuildDoubleJewel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                             Model3d.Model3dArt art)
    {
        string? frontPath = art.Front;
        string? region = LbCaseObj.RegionOfImagePath(frontPath);   // drives the Auto-Detect spine version
        var front = LoadBitmap(frontPath);
        var backImg = LoadBitmap(art.Back);

        // Spine strips: the game's OWN Box - Spine scan first (ov-aware — an Image Selection pick flows
        // through Model3dArt), the FrontSpineImage preset/custom only as the NO-SCAN fallback. Same
        // priority rule as the single jewel (v9): "Sony Playstation Spine" mode used to REPLACE the scan
        // with the generic preset and the game's texture vanished from the side strips — the real
        // LaunchBox keeps showing the scan in that mode (user-verified, doubled-spine test).
        string spineSpec = map != null && map.TryGetValue("FrontSpineImage", out var ss) ? ss : "";
        string? djScanPath = art.Spine;
        var djScan = UprightSpine(LoadBitmap(djScanPath));
        System.Windows.Media.Imaging.BitmapSource? spine =
            djScan
            ?? (spineSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase)
                    ? LbCaseObj.SpineImage(ResolvePresetKey(spineSpec.Substring(12), djScan, front, LbCaseObj.RegionOfImagePath(djScanPath), region), region)
                : spineSpec.Length > 0 ? LoadBitmap(spineSpec)
                : null);

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

        // The hinge cap image: the preset's DOUBLE-JEWEL variant when one is shipped, the resolved regular
        // preset otherwise; a custom (non-{Resources}) file is used as-is. The game's own scan never lands
        // here — the dump shows the cap wearing the preset while the strips wear the scan's halves.
        System.Windows.Media.Imaging.BitmapSource? cap = null;
        if (spineSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase))
        {
            string key = spineSpec.Substring(12);
            int dash = key.IndexOf(" - ", StringComparison.Ordinal);
            string baseKey = dash > 0 ? key.Substring(0, dash) : key;   // an explicit version still wants ITS double variant
            cap = LbCaseObj.SpineImage(baseKey + " - NA Double Jewel")
                  ?? LbCaseObj.SpineImage(ResolvePresetKey(key, djScan, front, LbCaseObj.RegionOfImagePath(djScanPath), region), region);
        }
        else if (spineSpec.Length > 0) cap = LoadBitmap(spineSpec);

        var grp = new Model3DGroup();
        // Every paper quad is double-faced with the SAME material — the dump shows back=mat on all of them
        // (the art's mirror is what shows through the clear shell from behind).
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
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat });
        }
        // Front insert: full width only when there is NO cap to wear (unobserved case — every shipped double
        // platform carries a preset); with a cap the front starts at -0.42 and the cap owns [-0.493..-0.42].
        double fl = cap != null ? -0.42 : -0.493, tl = cap != null ? -0.416 : -0.489;
        Quad(frontMat, new[] { (fl, 0.4265, 0.0573), (0.492, 0.4265, 0.0573), (fl, -0.4265, 0.0573), (0.492, -0.4265, 0.0573) });
        Quad(tintG, new[] { (tl, 0.4195, 0.0466), (0.488, 0.4195, 0.0466), (tl, -0.4195, 0.0466), (0.488, -0.4195, 0.0466) });
        if (cap != null)
            Quad(FaceMaterial(cap, System.Windows.Media.Stretch.Fill, 1000, 1000, CornerAverage(cap)),
                 new[] { (-0.493, 0.4265, 0.0573), (-0.42, 0.4265, 0.0573), (-0.493, -0.4265, 0.0573), (-0.42, -0.4265, 0.0573) });
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
    private static Model3D? BuildLongJewel(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                           Model3d.Model3dArt art)
    {
        var plastic = LbCaseObj.Load("LongJewelCase");
        if (plastic == null) return null;

        var front = LoadBitmap(art.Front);
        var backImg = LoadBitmap(art.Back);
        var spineImg = UprightSpine(LoadBitmap(art.Spine));

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
    private static Model3D? BuildDvd(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                     Model3d.Model3dArt art)
    {
        // Colours: forced options else derived. A missing front falls back to LB's NoImage placeholder
        // (shipped, 245×319 ≈ aspect 0.766) — LB uses it as the front TEXTURE and every derived value
        // (dims, corner colour) follows from it naturally (probe case F).
        var front = LoadBitmap(art.Front) ?? LbCaseObj.SpineImage("NoImage");
        var backImg = LoadBitmap(art.Back);
        var spineImg = UprightSpine(LoadBitmap(art.Spine));

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
        var sheet = art.FullScan && spineImg == null ? LoadBitmap(art.Full) : null;
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

        // Compose the wrap sheet exactly like LB (probe-decoded on the exotic-aspect matrix): the grid has
        // THREE FIXED-PIXEL columns — 434.892 | 59.860 | 434.892 at height 600 — which are the obj wrap's
        // UNWRAPPED PANEL WIDTHS at h=600 (they line up with the mesh's authored UV fold zones). Every image
        // is Fill-STRETCHED into its fixed column regardless of its own aspect (LB renders a 1200-wide back
        // and a 600-wide front into equal 434.9 columns). Missing back/spine → the cover colour shows;
        // missing front → the NoImage placeholder (already substituted above). The no-spine fallback = the
        // clear logo decoded at width 206, rotated 90°, margins (0.015×frontW, 30), centred — it overflows
        // the narrow spine column symmetrically, exactly like LB.
        const double ColPanel = 434.89202807270095, ColSpine = 59.8601763541479;
        var logo = LoadBitmap(art.Logo);
        var sides = ParseSides(map, "SpineRotation");
        var logoSides = ParseSides(map, "LogoRotation");
        double frontW = front != null ? Math.Round(front.PixelWidth * 600.0 / front.PixelHeight) : 460;

        var wrapGrid = new System.Windows.Controls.Grid { Height = 600, Background = new SolidColorBrush(coverColor) };
        foreach (var cw in new[] { ColPanel, ColSpine, ColPanel })
            wrapGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(cw) });
        void Put(System.Windows.FrameworkElement el, int col) { System.Windows.Controls.Grid.SetColumn(el, col); wrapGrid.Children.Add(el); }
        void Panel(System.Windows.Media.Imaging.BitmapSource? img, int col)
        {
            if (img == null) return;   // fixed column keeps its width; the cover colour shows
            Put(new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.Fill }, col);
        }
        Panel(backImg, 0);
        if (sides[0] && spineImg != null) Panel(spineImg, 1);
        else if (logoSides[0] && logo != null)
            Put(new System.Windows.Controls.Image
            {
                Source = logo,
                Width = 206,
                Height = Math.Round(206.0 * logo.PixelHeight / Math.Max(1, logo.PixelWidth)),
                Stretch = System.Windows.Media.Stretch.Uniform,
                LayoutTransform = new System.Windows.Media.RotateTransform(90),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
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
    // ── CASSETTE CASE (LB 14's cassetteCase) — oracle-decoded 2026-08-18 (--render-oracle dump of every
    //    style: StraightBack / AngledBack / ClearStraightBack, portrait + landscape, Age of Wonders):
    //
    //      leaf0 J-card FRONT  quad X[-0.2839..0.313] Y[±0.488] Z=+0.0661 — VisualBrush Grid 1000×1000,
    //            bg colour + front art Fill; BackMaterial paper #FFE8E3D8.
    //      leaf1 J-card BACK FLAP at Z=-0.0661 — bg only. X reach BY STYLE: Straight [-0.2839..-0.1368],
    //            Angled [-0.2839..-0.0582], ClearStraightBack full width [-0.2839..0.313].
    //      leaf2 J-card SPINE  X[-0.313..-0.2839] wrapping Z±0.0661 (24-vert rounded wrap in LB; a flat
    //            left-facing quad here) — VisualBrush Grid 1000×169.4: bg + clear logo (Uniform), else
    //            Text(title) in SpineForegroundColor (ARGB, -1 = white), else bg alone.
    //      leaf3 TAPE inside   X[0.0142..0.0751] Y[±0.2478] Z[-0.0638..0.0294] — black-case: silhouette
    //            #FF000000 + Specular #50DCE1E6 pow28; clear case: CassetteTape.png diffuse + gloss pow40.
    //      leaf4 BODY (CassetteCaseObj) — CaseColor ("Plastic Color", default #FF000000) + Specular
    //            #50DCE1E6 pow28; ClearStraightBack: #15CCCCCC + Specular #FF808080 pow250.
    //      leaf5 LID  — #10CCCCCC + Specular #FF808080 pow250 (all styles).
    //
    //    CassettePosition Landscape = the whole model rotated -90° about Z (dump: X/Y bounds swap);
    //    Automatic goes landscape when the FRONT art is wider than tall. CassetteWornPlastic /
    //    CassetteCloudyPlastic (wear/haze overlays) and the Cassette*Rotation label spins are TODO —
    //    they alter looks, never geometry.
    private static Model3D? BuildCassette(System.Collections.Generic.Dictionary<string, string>? map, string? gameTitle,
                                          Model3d.Model3dArt art)
    {
        string Get(string k) => map != null && map.TryGetValue(k, out var v) ? (v ?? "") : "";
        string cassType = Get("CassetteType");
        bool clearCase = string.Equals(cassType, "ClearStraightBack", StringComparison.OrdinalIgnoreCase);
        bool angled = string.Equals(cassType, "AngledBack", StringComparison.OrdinalIgnoreCase);

        System.Windows.Media.Color Argb(string key, System.Windows.Media.Color def)
            => int.TryParse(Get(key), out var a)
               ? System.Windows.Media.Color.FromArgb((byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a) : def;
        var plasticColor = Argb("CaseColor", System.Windows.Media.Color.FromRgb(0, 0, 0));
        var spineFg = Argb("SpineForegroundColor", System.Windows.Media.Colors.White);
        bool worn = string.Equals(Get("CassetteWornPlastic"), "true", StringComparison.OrdinalIgnoreCase);
        bool cloudy = string.Equals(Get("CassetteCloudyPlastic"), "true", StringComparison.OrdinalIgnoreCase);
        string logoFont = Get("LogoFont");
        int spineRot = int.TryParse(Get("CassetteSpineRotation"), out var srr) ? ((srr % 360) + 360) % 360 : 0;
        int logoRot = int.TryParse(Get("CassetteLogoRotation"), out var lrr) ? ((lrr % 360) + 360) % 360 : 0;

        var front = LoadBitmap(art.Front);
        var logo = LoadBitmap(art.Logo);
        var bg = System.Windows.Media.Color.FromRgb(0x06, 0x0A, 0x1D);
        if (map != null && map.ContainsKey("CoverColor")) bg = Argb("CoverColor", bg);
        else if (front != null) bg = CardBgFromArt(front);
        // Position: Automatic goes landscape when the front art is wider than tall. Decided HERE
        // because the front material depends on it: the CASE rotates but the art must stay upright
        // (LB keeps it readable), so the texture gets a counter-rotation.
        bool landscape = string.Equals(Get("CassettePosition"), "Landscape", StringComparison.OrdinalIgnoreCase)
                         || (string.Equals(Get("CassettePosition"), "Automatic", StringComparison.OrdinalIgnoreCase) || Get("CassettePosition").Length == 0)
                            && front != null && front.PixelWidth > front.PixelHeight;

        var grp = new Model3DGroup();

        // Plastic: body + lid from the vendored LB mesh, materials retargeted per style (the MTL's own
        // colours are placeholders — LB always overrides them, per the dump).
        var (plastic0, segNames) = LbCaseObj.LoadWithNames("CassetteCase");
        if (plastic0 == null) return null;
        var plastic = plastic0.Clone();   // the loader caches FROZEN groups — clone before retargeting materials
        // Worn ("Add Scuffs and Scratches"): the wear texture rides BOTH channels — a 10%-opacity
        // overlay on the plastic colour and the raw image as a TILED specular map (oracle-dump
        // exact). Cloudy ("Cloudy Aged Plastic") changes only the LID: hazier diffuse #38F5F7FA,
        // brighter lower-power specular #6EFFFFFF pow90.
        var wear = worn && !clearCase ? LbCaseObj.SpineImage("CassetteWear") : null;
        Material WornMat(System.Windows.Media.Color baseColor)
        {
            var wg = new System.Windows.Controls.Grid { Width = 384, Height = 384, Background = new SolidColorBrush(baseColor) };
            wg.Children.Add(new System.Windows.Controls.Image { Source = wear, Stretch = System.Windows.Media.Stretch.Fill, Opacity = 0.1 });
            return new MaterialGroup
            {
                Children =
                {
                    new DiffuseMaterial(new VisualBrush(wg) { Stretch = System.Windows.Media.Stretch.Fill }),
                    new SpecularMaterial(new System.Windows.Media.ImageBrush(wear)
                    { Stretch = System.Windows.Media.Stretch.Fill, TileMode = System.Windows.Media.TileMode.Tile,
                      Viewport = new System.Windows.Rect(0, 0, 1, 1) }, 28),
                }
            };
        }
        // Lid vs tray, LB's real split (dump: the 284-vert lid is #10CCCCCC clear plastic covering
        // front + spine side + a lip over the back's flap strip; the 770-vert tray is CaseColor and
        // IS the visible matte back). The first attempt painted the tray opaque and blacked out the
        // side views — that failure came from the wrong axis mapping below, not from this split.
        Material bodyMat = wear != null ? WornMat(plasticColor)
            : Mat2(plasticColor, System.Windows.Media.Color.FromArgb(0x50, 0xDC, 0xE1, 0xE6), 28);
        Material lidMat = cloudy
            ? Mat2(System.Windows.Media.Color.FromArgb(0x38, 0xF5, 0xF7, 0xFA), System.Windows.Media.Color.FromArgb(0x6E, 0xFF, 0xFF, 0xFF), 90)
            : Mat2(System.Windows.Media.Color.FromArgb(0x10, 0xCC, 0xCC, 0xCC), System.Windows.Media.Color.FromArgb(0xFF, 0x80, 0x80, 0x80), 250);
        Material trayMat = clearCase
            ? (cloudy ? Mat2(System.Windows.Media.Color.FromArgb(0x4A, 0xF0, 0xF3, 0xF8), System.Windows.Media.Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 80)
                      : Mat2(System.Windows.Media.Color.FromArgb(0x15, 0xCC, 0xCC, 0xCC), System.Windows.Media.Color.FromArgb(0xFF, 0x80, 0x80, 0x80), 250))
            : bodyMat;
        for (int i = 0; i < plastic.Children.Count; i++)
        {
            if (plastic.Children[i] is not GeometryModel3D gm) continue;
            bool isLid = i < segNames.Count && segNames[i].IndexOf("Lid", StringComparison.OrdinalIgnoreCase) >= 0;
            gm.Material = isLid ? lidMat : trayMat;
            gm.BackMaterial = gm.Material;
        }
        // The vendored OBJ sits in the mesh's NATIVE frame — real-world metres, Y = the 1.6 cm
        // thickness, Z = the 6.9 cm height. LB re-bases it into the model frame: thickness onto Z,
        // then a per-axis bounds-fit onto the dumped shell extents (±0.3168, ±0.5, ±0.0734 —
        // deliberately non-uniform, LB stretches the case into its portrait presentation).
        var pb = plastic.Bounds;
        if (!pb.IsEmpty && pb.SizeX > 0 && pb.SizeY > 0 && pb.SizeZ > 0)
        {
            var ptr = new Transform3DGroup();
            ptr.Children.Add(new TranslateTransform3D(-pb.X - pb.SizeX / 2, -pb.Y - pb.SizeY / 2, -pb.Z - pb.SizeZ / 2));
            // The OBJ lies FLAT: 10.9 cm length on native X, 6.9 cm height on native Z, 1.6 cm
            // thickness on native Y. LB stands it upright — native X→model Y, native Z→model X,
            // native Y→model Z — which makes the scale essentially UNIFORM (×9.17 on every axis;
            // the earlier mapping stretched 5.8×/14.5× and put every wall on a wrong side). The
            // negative X scale lands the lid's deep lip on the SPINE side; the mirror is safe since
            // each shell part carries the same material on both faces.
            ptr.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), 90)));
            ptr.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90)));   // net: (x,y,z)→(z,x,y)
            ptr.Children.Add(new ScaleTransform3D(-0.3168 * 2 / pb.SizeZ, 0.5 * 2 / pb.SizeX, 0.0734 * 2 / pb.SizeY));
            plastic.Transform = ptr;
        }
        // NOT added yet — WPF 3D draws in child order with a z-test, so the translucent shell must
        // come AFTER every opaque part it covers (lid over the art, clear body over the tape) or
        // they would z-fail behind it and vanish. Shell + tape join grp after the J-card quads.

        // Cassette inside — CLEAR cases only: the closed case's oracle dump carries no cassette mesh
        // at all, while the clear dump shows the tape FULL SIZE, filling the interior (X ±0.2936,
        // Y ±0.461, Z ±0.055) with CassetteTape.png diffuse + its gloss map as specular pow 40.
        Model3DGroup? tape = null;
        if (clearCase && (tape = LbCaseObj.Load("CassetteTape")?.Clone()) != null)   // loader cache is frozen — clone
        {
            var tex = LbCaseObj.SpineImage("CassetteTape");        // case-assets pngs via the shared loader
            var gloss = LbCaseObj.SpineImage("CassetteTapeGloss"); // its specular map (oracle: Image 512-sq, pow 40)
            Material spec = gloss != null
                ? new SpecularMaterial(new System.Windows.Media.ImageBrush(gloss) { Stretch = System.Windows.Media.Stretch.Fill }, 40)
                : new SpecularMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x80, 0x80, 0x80)), 40);
            Material tapeMat = tex != null
                ? new MaterialGroup { Children = { new DiffuseMaterial(new System.Windows.Media.ImageBrush(tex) { Stretch = System.Windows.Media.Stretch.Fill }), spec } }
                : Mat2(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20), System.Windows.Media.Color.FromArgb(0xFF, 0x80, 0x80, 0x80), 40);
            foreach (var c in tape.Children)
                if (c is GeometryModel3D gm) { gm.Material = tapeMat; gm.BackMaterial = tapeMat; }
            var tb = tape.Bounds;
            if (!tb.IsEmpty && tb.SizeX > 0 && tb.SizeY > 0 && tb.SizeZ > 0)
            {
                // The tape OBJ is in its own frame (X = the 10 cm length, Y = width, Z = thickness).
                // Swap length onto the model's HEIGHT (the case stands portrait), then stretch per-axis
                // onto LB's dumped interior extents.
                var target = new Rect3D(-0.2936, -0.461, -0.055, 0.2936 * 2, 0.461 * 2, 0.055 * 2);
                var tr = new Transform3DGroup();
                tr.Children.Add(new TranslateTransform3D(-tb.X - tb.SizeX / 2, -tb.Y - tb.SizeY / 2, -tb.Z - tb.SizeZ / 2));
                tr.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), 90)));   // (x,y,z)→(−y,x,z)
                tr.Children.Add(new ScaleTransform3D(target.SizeX / tb.SizeY, target.SizeY / tb.SizeX, target.SizeZ / tb.SizeZ));
                tr.Children.Add(new TranslateTransform3D(target.X + target.SizeX / 2, target.Y + target.SizeY / 2, target.Z + target.SizeZ / 2));
                tape.Transform = tr;
            }
        }

        // ── J-card ──
        var paper = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xE3, 0xD8)));
        void Quad(Material mat, Material? back, (double x, double y, double z)[] p, (double u, double v)[] uv)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < p.Length; i++)
            {
                mesh.Positions.Add(new Point3D(p[i].x, p[i].y, p[i].z));
                mesh.TextureCoordinates.Add(new System.Windows.Point(uv[i].u, uv[i].v));
            }
            foreach (var ix in new[] { 3, 0, 2, 3, 1, 0 }) mesh.TriangleIndices.Add(ix);
            var a = new Vector3D(p[1].x - p[0].x, p[1].y - p[0].y, p[1].z - p[0].z);
            var b = new Vector3D(p[2].x - p[0].x, p[2].y - p[0].y, p[2].z - p[0].z);
            var n = Vector3D.CrossProduct(a, b);
            var centre = new Vector3D((p[0].x + p[3].x) / 2, (p[0].y + p[3].y) / 2, (p[0].z + p[3].z) / 2);
            if (Vector3D.DotProduct(n, centre) < 0) n = -n;
            if (n.LengthSquared > 0) { n.Normalize(); for (int i = 0; i < 4; i++) mesh.Normals.Add(n); }
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = back });
        }
        var uv4 = new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d) };

        // Front (inside the case, under the lid). LB's Viewbox STRETCHES the art edge-to-edge
        // (flat oracle front render measured full-bleed — no letterbox).
        Quad(FaceMaterialVb(front, 1000, 1000, bg, landscape ? -90 : 0), paper,
             new[] { (-0.2839, 0.488, 0.0661), (0.313, 0.488, 0.0661), (-0.2839, -0.488, 0.0661), (0.313, -0.488, 0.0661) }, uv4);
        // Back flap, style-dependent reach (faces -Z: wound the other way so the front side is
        // outward). Angled = simply a WIDER rectangle (dump-verified; no diagonal). The clear case's
        // full-width flap carries the two hub WINDOWS (LB "CassetteWindow": grid-space ellipses,
        // anisotropic because the 1000² brush stretches onto 0.597×1) and must be added AFTER the
        // cassette so the holes' transparency reveals the reels behind it.
        double flapEnd = clearCase ? 0.313 : angled ? -0.0582 : -0.1368;
        Material flapMat;
        if (clearCase)
        {
            var fg = new System.Windows.Controls.Grid { Width = 1000, Height = 1000, Background = System.Windows.Media.Brushes.Transparent };
            var holes = new System.Windows.Media.GeometryGroup { FillRule = System.Windows.Media.FillRule.EvenOdd };
            holes.Children.Add(new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 1000, 1000)));
            holes.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(478, 302), 77, 46));
            holes.Children.Add(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(478, 698), 77, 46));
            fg.Children.Add(new System.Windows.Shapes.Path { Data = holes, Fill = new SolidColorBrush(bg) });
            flapMat = new DiffuseMaterial(new VisualBrush(fg) { Stretch = System.Windows.Media.Stretch.Fill });
        }
        else flapMat = FaceMaterialNoImage(1000, 1000, bg);
        void AddFlap()
        {
            // The REAL flap rect at LB's depth. In closed cases it reads through the tray plate's
            // natural spine-side hole (which ends at X=-0.156, under the lid's clear lip) and KEEPS
            // GOING under the plate — so a grazing view past the hole's edge parallaxes onto more
            // red flap instead of a black slit, exactly like LB. In clear cases the whole plate is
            // translucent and the full-width holed flap simply shows through.
            Quad(flapMat, paper,
                 new[] { (flapEnd, 0.5, -0.0661), (-0.2839, 0.5, -0.0661), (flapEnd, -0.5, -0.0661), (-0.2839, -0.5, -0.0661) }, uv4);
            if (clearCase) return;
            // The parts of LB's WINDOW that are CUT beyond the natural hole ("cut the angled
            // cassette case back window") have opaque plate in front of the flap, so they float
            // just outside the plate instead, glass-free like LB's: the fine matte border at the
            // straight window's inner edge, and Angled's wide reveal (its inner edge pushed to
            // -0.0742 over the middle 65% with diagonals over the top/bottom ~17% — both shapes
            // measured off flat oracle back renders).
            // Shared depth profile for every back-window piece: ONE continuous gentle slope from
            // the spine edge to the window's inner edge. A piecewise flat+dive profile (LB's real
            // lip shape) put a crease mid-pane where the auto normals averaged across the kink and
            // the sheen broke into two visibly distinct parts; a single plane has a single normal,
            // so the pow-250 sheen ignites uniformly across the WHOLE reveal and sweeps it as one
            // surface. It leans outward from the shell (inward would dive behind the tray plate).
            double ZOf(double x) => -0.0754 - (x + 0.2842) / 0.21 * 0.0058;
            // Flat variant for pieces that must NOT ride the sloped profile: the corner masks
            // mirror LB's FLAT recut tray plate — put on the tilted ZOf they ignited their broad
            // pow-28 specular together with the glaze and read as the glass overflowing the
            // diagonal, while LB's flat corners stay dark at those angles.
            void AddPanelFlat((double x, double y)[] poly, double z, Material mat)
            {
                var wm = new MeshGeometry3D();
                foreach (var (px, py) in poly)
                {
                    wm.Positions.Add(new Point3D(px, py, z));
                    wm.TextureCoordinates.Add(new System.Windows.Point(py + 0.5, (px + 0.2842) / 0.23));
                    wm.Normals.Add(new Vector3D(0, 0, -1));
                }
                for (int i = 1; i + 1 < poly.Length; i++)
                { wm.TriangleIndices.Add(0); wm.TriangleIndices.Add(i); wm.TriangleIndices.Add(i + 1); }
                grp.Children.Add(new GeometryModel3D { Geometry = wm, Material = mat });
            }
            void AddPanel((double x, double y)[] poly, Material mat)
            {
                var wm = new MeshGeometry3D();
                foreach (var (px, py) in poly)
                {
                    wm.Positions.Add(new Point3D(px, py, ZOf(px)));
                    wm.TextureCoordinates.Add(new System.Windows.Point(py + 0.5, (px + 0.2842) / 0.23));
                }
                for (int i = 1; i + 1 < poly.Length; i++)
                { wm.TriangleIndices.Add(0); wm.TriangleIndices.Add(i); wm.TriangleIndices.Add(i + 1); }
                grp.Children.Add(new GeometryModel3D { Geometry = wm, Material = mat });   // auto normals follow the profile
            }
            // TESSELLATED panel (16x16). WPF 3D lights PER VERTEX: a pow-250 specular evaluated
            // only at a big panel's 4 corners never lands on the highlight and renders dead-flat --
            // which is exactly why the shell mesh's lip glosses while a 4-vertex pane next to it
            // stayed matte. Depth comes from the shared ZOf profile.
            void AddPanelGrid((double x, double y) tl, (double x, double y) tr, (double x, double y) br, (double x, double y) bl,
                              Material mat)
            {
                const int NU = 16, NV = 16;
                var wm = new MeshGeometry3D();
                for (int v = 0; v <= NV; v++)
                    for (int u = 0; u <= NU; u++)
                    {
                        double fu = (double)u / NU, fv = (double)v / NV;
                        double px = (tl.x + (tr.x - tl.x) * fu) * (1 - fv) + (bl.x + (br.x - bl.x) * fu) * fv;
                        double py = (tl.y + (tr.y - tl.y) * fu) * (1 - fv) + (bl.y + (br.y - bl.y) * fu) * fv;
                        wm.Positions.Add(new Point3D(px, py, ZOf(px)));
                        wm.TextureCoordinates.Add(new System.Windows.Point(py + 0.5, (px + 0.2842) / 0.23));
                    }
                for (int v = 0; v < NV; v++)
                    for (int u = 0; u < NU; u++)
                    {
                        int a = v * (NU + 1) + u, b = a + 1, c = a + (NU + 1), d = c + 1;
                        foreach (var ix in new[] { c, b, d, c, a, b }) wm.TriangleIndices.Add(ix);
                    }
                grp.Children.Add(new GeometryModel3D { Geometry = wm, Material = mat });   // auto normals follow the profile
            }
            // ONE mesh for the whole hexagonal reveal: columns across x, each spanning the
            // hexagon's local height (full until the diagonals start, then tapering). A single
            // mesh means shared vertices and coherent normals -- the two-grid + border-ring build
            // showed its seams as shading lines.
            void AddHexGrid(Material mat)
            {
                const int NU = 24, NV = 16;
                // The window diagonal, refit from the flat oracle measurements as one line: it
                // meets the top edge at x=-0.2231 and runs at slope -1.15 down to (-0.0742, 0.328).
                // Every piece (this boundary AND the corner masks) shares this single line -- two
                // slightly different diagonals made the glaze visibly overflow past the masks.
                double YL(double x) => x <= -0.2231 ? 0.5 : 0.5 - (x + 0.2231) * 1.15;
                var wm = new MeshGeometry3D();
                for (int v = 0; v <= NV; v++)
                    for (int u = 0; u <= NU; u++)
                    {
                        double px = -0.2842 + 0.21 * u / NU;
                        double yl = YL(px);
                        double py = yl - 2 * yl * v / NV;
                        wm.Positions.Add(new Point3D(px, py, ZOf(px)));
                        wm.TextureCoordinates.Add(new System.Windows.Point(py + 0.5, (px + 0.2842) / 0.23));
                    }
                for (int v = 0; v < NV; v++)
                    for (int u = 0; u < NU; u++)
                    {
                        int a = v * (NU + 1) + u, b = a + 1, c = a + (NU + 1), d = c + 1;
                        foreach (var ix in new[] { c, b, d, c, a, b }) wm.TriangleIndices.Add(ix);
                    }
                grp.Children.Add(new GeometryModel3D { Geometry = wm, Material = mat });
            }
            // Depth discipline: with the scene's near plane at 0.001, the z-buffer only resolves
            // ~0.0004 at this distance — panels closer than that to the tray plate (−0.0734) or to
            // each other z-fight and drop out (the glass pane lost every pixel at 0.0004). Every
            // layer here keeps ≥0.002 of separation; the ~0.004 total overhang off the shell is
            // invisible at preview scale.
            if (angled)
            {
                // LB's cut reshapes the LID's lip too: glass covers the angled reveal up to a fine
                // matte border along its edges. A separate translucent pane proved unreliable
                // (alpha-16 brushes drop out of the baked pipeline), so the glazing is PRE-COMPOSED
                // into an opaque material — bg blended 6.3% toward the lid plastic, plus the lid's
                // specular — and the reveal is split into coplanar adjacent polygons: the glazed
                // inner pane and the matte border ring (no overlap, no z-fighting).
                // The pre-composed glass must track the LID's actual variant: normal glass is a 6.3%
                // blend toward #CCCCCC with the tight pow-250 gloss, but CLOUDY plastic is a much
                // heavier 22% milk toward #F5F7FA with the broad pow-90 sheen — without this, aged
                // plastic left the reveal near-bare while LB's went milky.
                double ga = cloudy ? 0x38 / 255.0 : 0x10 / 255.0;
                var tint = cloudy ? System.Windows.Media.Color.FromRgb(0xF5, 0xF7, 0xFA) : System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC);
                var gz = System.Windows.Media.Color.FromRgb(
                    (byte)Math.Min(255, bg.R * (1 - ga) + tint.R * ga),
                    (byte)Math.Min(255, bg.G * (1 - ga) + tint.G * ga),
                    (byte)Math.Min(255, bg.B * (1 - ga) + tint.B * ga));
                Material glazed = new MaterialGroup
                {
                    Children =
                    {
                        new DiffuseMaterial(new SolidColorBrush(gz)),
                        cloudy
                            ? new SpecularMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x6E, 0xFF, 0xFF, 0xFF)), 90)
                            : new SpecularMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x80, 0x80, 0x80)), 250),
                    }
                };
                // The glass covers the WHOLE hexagonal reveal as ONE mesh -- LB's lip spans it
                // entirely (vertex dump: x -0.3162 to -0.0729), and any split into sections showed
                // its seams. The fine matte border was sacrificed for seamlessness.
                AddHexGrid(glazed);
                // The tray's natural spine-side hole is FULL height, but the angled window narrows
                // toward the corners -- mask the flap showing through the hole outside the window
                // with CaseColor corner triangles, completing the hexagon LB cuts.
                // PURE-DIFFUSE masks: with any specular at all, Gouraud interpolation from a single
                // lit vertex of these small triangles washed a pink feather across them whenever the
                // glaze's sheen was on -- reading as the glass overflowing the diagonal.
                Material maskMat = new DiffuseMaterial(new SolidColorBrush(plasticColor));
                AddPanelFlat(new (double, double)[] { (-0.2231, 0.5), (-0.156, 0.5), (-0.156, 0.4228) }, -0.0754, maskMat);
                AddPanelFlat(new (double, double)[] { (-0.2231, -0.5), (-0.156, -0.4228), (-0.156, -0.5) }, -0.0754, maskMat);
            }
            else
                AddPanel(new (double, double)[] { (-0.156, 0.5), (-0.1539, 0.5), (-0.1539, -0.5), (-0.156, -0.5) }, flapMat);
        }
        if (!clearCase) AddFlap();
        // Spine: clear logo, else the plain-text title in SpineForegroundColor, else bg alone. The 1000-wide
        // grid runs along the case's HEIGHT, so uv maps u onto Y (text upright when the case lies on its left
        // side, exactly like a real J-card).
        System.Windows.Media.Visual spineVisual;
        // A set LogoFont = "Use Plain Text Title Instead of Clear Logo" (the jewel family's own
        // convention). CassetteLogoRotation spins the clear logo, CassetteSpineRotation the text.
        var spineScan = LoadBitmap(art.Spine);
        var spineGrid = new System.Windows.Controls.Grid { Width = 1000, Height = spineScan != null ? 220 : 169.4, Background = new SolidColorBrush(spineScan != null ? (LinearAverage(spineScan) ?? bg) : bg) };
        if (spineScan != null)
        {
            // A real Box - Spine scan wins over logo/text (LB: Grid 1000x220, scan in a Viewbox).
            // The scan is stored portrait; lay it along the spine's length (the grid's 1000 axis).
            var sim = new System.Windows.Controls.Image { Source = spineScan, Stretch = System.Windows.Media.Stretch.Fill };
            sim.LayoutTransform = new System.Windows.Media.RotateTransform(90 + spineRot);
            spineGrid.Children.Add(new System.Windows.Controls.Viewbox
            { Child = sim, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center });
        }
        else if (logo != null && logoFont.Length == 0)
        {
            // Clear logo, Uniform-centred in a Viewbox (oracle: Viewbox Img st=Uniform). One dump
            // showed the TEXT here instead — that was LB caught mid-async-art-load, not the rule.
            var lim = new System.Windows.Controls.Image
            { Source = logo, Stretch = System.Windows.Media.Stretch.Uniform,
              HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            if (logoRot != 0) lim.LayoutTransform = new System.Windows.Media.RotateTransform(logoRot);
            spineGrid.Children.Add(new System.Windows.Controls.Viewbox
            { Child = lim, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center });
        }
        else if (!string.IsNullOrEmpty(gameTitle))
        {
            var tb = new System.Windows.Controls.TextBlock { Text = gameTitle, FontSize = 12, Foreground = new SolidColorBrush(spineFg) };
            if (logoFont.Length > 0) { try { tb.FontFamily = new System.Windows.Media.FontFamily(logoFont); } catch { } }
            var vb = new System.Windows.Controls.Viewbox
            { Child = tb, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            if (spineRot != 0) vb.LayoutTransform = new System.Windows.Media.RotateTransform(spineRot);
            spineGrid.Children.Add(vb);
        }
        spineVisual = spineGrid;
        var spineMat = new DiffuseMaterial(new VisualBrush(spineVisual) { Stretch = System.Windows.Media.Stretch.Fill });
        // Rounded spine WRAP — LB's exact 24-vertex arc (dump-verbatim): a strip from the flap edge
        // around the corner to the front edge, flat between z ±0.037. Radial normals give the smooth
        // plastic-corner shading; the triangle order continues the flat spine's verified −X-facing
        // winding (u = 0 at the case top, v = 1 at the back edge — same map as the old flat quad).
        var arc = new (double x, double z, double s)[]
        {
            (-0.2839, -0.0661, 0.000), (-0.2929, -0.0646, 0.055), (-0.3010, -0.0605, 0.110),
            (-0.3075, -0.0541, 0.165), (-0.3116, -0.0460, 0.221), (-0.3130, -0.0370, 0.276),
            (-0.3130,  0.0370, 0.724), (-0.3116,  0.0460, 0.779), (-0.3075,  0.0541, 0.835),
            (-0.3010,  0.0605, 0.890), (-0.2929,  0.0646, 0.945), (-0.2839,  0.0661, 1.000),
        };
        var wrap = new MeshGeometry3D();
        for (int i = 0; i < arc.Length; i++)
        {
            wrap.Positions.Add(new Point3D(arc[i].x, 0.488, arc[i].z));
            wrap.Positions.Add(new Point3D(arc[i].x, -0.488, arc[i].z));
            wrap.TextureCoordinates.Add(new System.Windows.Point(0, 1 - arc[i].s));
            wrap.TextureCoordinates.Add(new System.Windows.Point(1, 1 - arc[i].s));
            int ip = Math.Max(0, i - 1), iq = Math.Min(arc.Length - 1, i + 1);
            var nrm = new Vector3D(-(arc[iq].z - arc[ip].z), 0, arc[iq].x - arc[ip].x);
            if (Vector3D.DotProduct(nrm, new Vector3D(arc[i].x + 0.245, 0, arc[i].z)) < 0) nrm = -nrm;
            if (nrm.LengthSquared > 0) nrm.Normalize();
            wrap.Normals.Add(nrm); wrap.Normals.Add(nrm);
        }
        for (int i = 0; i + 1 < arc.Length; i++)
        {
            int t0 = 2 * i, b0 = 2 * i + 1, t1 = 2 * i + 2, b1 = 2 * i + 3;
            foreach (var ix in new[] { b1, t0, b0, b1, t1, t0 }) wrap.TriangleIndices.Add(ix);
        }
        grp.Children.Add(new GeometryModel3D { Geometry = wrap, Material = spineMat, BackMaterial = paper });

        // Interior back plate: the tray's back plate has a CUT-OUT on the spine side (covered only
        // by the lid's clear lip); this CaseColor plate just inside stops the J-card's paper
        // backside from beaming through it. It STOPS at the spine wrap's start (−0.2839) — running
        // it under the wrap blacked out the wrap's rounded back portion, which LB shows spilling
        // onto the back. A clear case shows its real interior — no plate.
        if (!clearCase)
            Quad(bodyMat, bodyMat,
                 new[] { (0.3168, 0.5, -0.0650), (-0.2839, 0.5, -0.0650), (0.3168, -0.5, -0.0650), (-0.2839, -0.5, -0.0650) }, uv4);

        // Draw order (child order in WPF 3D): the cassette, then the clear case's holed flap (its
        // window transparency must blend over the reels already drawn), then the translucent shell.
        if (tape != null) grp.Children.Add(tape);
        if (clearCase) AddFlap();
        if (!DebugSkipPlastic) grp.Children.Add(plastic);   // `noplastic` probe flag: J-card + tape alone

        if (landscape)
            grp.Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), -90));
        return grp;
    }

    /// <summary>Diffuse + specular pair — the plastic material shape every cassette part uses.</summary>
    private static Material Mat2(System.Windows.Media.Color diffuse, System.Windows.Media.Color specular, double power)
        => new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(new SolidColorBrush(diffuse)),
                new SpecularMaterial(new SolidColorBrush(specular), power),
            }
        };

    // Grid bg + centred Viewbox around the image — LB's cassette front/spine composition: the art
    // is uniform-fit and letterboxed on the bg colour instead of stretched.
    private static Material FaceMaterialVb(System.Windows.Media.Imaging.BitmapSource? img,
                                           double gridW, double gridH, System.Windows.Media.Color bg, double imgRotation = 0)
    {
        var grid = new System.Windows.Controls.Grid { Width = gridW, Height = gridH, Background = new SolidColorBrush(bg) };
        if (img != null)
        {
            var im = new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.Fill };
            if (imgRotation != 0) im.LayoutTransform = new System.Windows.Media.RotateTransform(imgRotation);
            grid.Children.Add(new System.Windows.Controls.Viewbox
            {
                Child = im,
                Stretch = System.Windows.Media.Stretch.Fill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
        }
        return new DiffuseMaterial(new VisualBrush(grid) { Stretch = System.Windows.Media.Stretch.Fill });
    }

    /// <summary>The card colour LB derives from the art: gamma-correct (linear-space) average,
    /// falling back to LB's navy constant #060A1D when the average is too grey or too dark to read
    /// as "the box colour" (oracle: AoW's muddy grey-violet falls back, DK2's saturated red holds).</summary>
    private static System.Windows.Media.Color CardBgFromArt(System.Windows.Media.Imaging.BitmapSource? img)
    {
        var c = LinearAverage(img);
        if (c == null) return System.Windows.Media.Color.FromRgb(0x06, 0x0A, 0x1D);
        int mx = Math.Max(c.Value.R, Math.Max(c.Value.G, c.Value.B));
        int mn = Math.Min(c.Value.R, Math.Min(c.Value.G, c.Value.B));
        return mx < 32 || (mx - mn) < mx * 0.35 ? System.Windows.Media.Color.FromRgb(0x06, 0x0A, 0x1D) : c.Value;
    }

    /// <summary>Average colour computed in linear light (gamma 2.2), over a 32-px thumbnail.
    /// Matches LB's cassette card/spine backgrounds far better than a straight sRGB average,
    /// which dilutes bright saturated art toward black.</summary>
    private static System.Windows.Media.Color? LinearAverage(System.Windows.Media.Imaging.BitmapSource? img)
    {
        try
        {
            if (img == null) return null;
            var small = new System.Windows.Media.Imaging.TransformedBitmap(img,
                new System.Windows.Media.ScaleTransform(64.0 / Math.Max(1, img.PixelWidth), 64.0 / Math.Max(1, img.PixelHeight)));
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            var buf = new byte[w * h * 4];
            conv.CopyPixels(buf, w * 4, 0);
            // Near-black pixels are EXCLUDED (max channel <= 80): LB's card colour reads as "the
            // colour of the printed art", undiluted by black borders and shadow masses — with the
            // cut-off at 80 the gamma-correct average reproduces LB's dumped values to within a
            // couple of counts (DK2 poster -> 151,44,28 vs LB's #972B1D).
            double r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < buf.Length; i += 4)
            {
                if (Math.Max(buf[i], Math.Max(buf[i + 1], buf[i + 2])) <= 80) continue;
                b += Math.Pow(buf[i] / 255.0, 2.2);
                g += Math.Pow(buf[i + 1] / 255.0, 2.2);
                r += Math.Pow(buf[i + 2] / 255.0, 2.2);
                n++;
            }
            if (n == 0) return null;
            byte C(double v) => (byte)Math.Clamp((int)Math.Round(Math.Pow(v / n, 1 / 2.2) * 255), 0, 255);
            return System.Windows.Media.Color.FromRgb(C(r), C(g), C(b));
        }
        catch { return null; }
    }

    private static Material FaceMaterialNoImage(double gridW, double gridH, System.Windows.Media.Color bg)
    {
        var grid = new System.Windows.Controls.Grid { Width = gridW, Height = gridH, Background = new SolidColorBrush(bg) };
        return new DiffuseMaterial(new VisualBrush(grid) { Stretch = System.Windows.Media.Stretch.Fill });
    }

    // VisualBrush face material exactly as LB composes it: Grid sized dim×1000 px, case-colour background,
    // one Image child (Fill for the front art, Uniform for the centred clear logo). A NULL image still emits
    // the Image child without a source — LB's own "Empty Clear Spine" cap is exactly that (dump-verified).
    private static Material FaceMaterial(System.Windows.Media.Imaging.BitmapSource? img, System.Windows.Media.Stretch stretch,
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
