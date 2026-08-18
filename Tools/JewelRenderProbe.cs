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

    // usage: --render-jewel <out.png> [yawDeg] [pitchDeg] [distance] [WxH]
    //        [platform <name>] [title <game title>] [type <box|jewelCase|dvd|longJewelCase|doubleJewelCase>]
    //        [front|spine|back|logo|full <path>] [noscan] [noback] [map "K=V;K=V"] [noplastic|nocap|diagback]
    //
    // WHICH GAME: platform + title, resolved through MediaResolver exactly as the app does — so the probe
    // renders the same art the cache would bake. There is no catalogue in this process (--render-jewel
    // returns before HostBoot), hence no game id and no game cache: MediaResolver falls back to its disk
    // walk over LB's conventional Images\<platform>\<type> layout, which needs no plugin host to answer.
    // Per-slot paths override that, for art that is NOT in the library — the only way to exercise a case
    // the real tree cannot produce (an image outside Images\ has no region, which is how the Dreamcast
    // black/white measurement gets tested).
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

            string platform = ArgValue(args, "platform") ?? "Sony Playstation";
            string title = ArgValue(args, "title") ?? "Final Fantasy 7";
            InitMediaRoot();

            // Defaults for the platform (model-defaults.json, scrapeAs-aware) — the settings the app itself
            // would start from — else the ModelSettings ctor defaults. `type` and `map` override from there.
            var map = new Dictionary<string, string>(
                Host.Platforms.ModelDefaults.TryGet(platform, platform)
                ?? Host.Platforms.EditPlatformModel.CtorDefaults()
                ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            if (ArgValue(args, "type") is { Length: > 0 } forcedType) map["ModelType"] = forcedType;

            // Per-slot path overrides. Absolute or relative to CWD (= LB root when launched from Core).
            var ov = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in Host.Model3d.Model3dImageStore.Slots)
                if (ArgValue(args, slot) is { Length: > 0 } sp)
                {
                    if (File.Exists(sp)) ov[slot] = Path.GetFullPath(sp);
                    else Console.WriteLine($"[jewel-probe] {slot} \"{sp}\" does not exist — ignored");
                }
            // Legacy spelling of `spine <path>`, kept so old command lines keep working.
            if (!ov.ContainsKey("spine") && ArgValue(args, "spinefile") is { Length: > 0 } fs && File.Exists(fs))
                ov["spine"] = Path.GetFullPath(fs);
            // "noscan" simulates a game with no Box - Spine scan (exercises the preset cap path); "noback"
            // a game with no back scan. Both work by pinning the slot to nothing — see SuppressedSlots.
            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (args.Contains("noscan")) suppressed.Add("spine");
            if (args.Contains("noback")) suppressed.Add("back");

            Host.Platforms.HomeModel3d.DebugSkipPlastic = args.Contains("noplastic");
            Host.Platforms.HomeModel3d.DebugSkipCap = args.Contains("nocap");
            Host.Platforms.HomeModel3d.DebugBackFaces = args.Contains("diagback");
            _fillLight = args.Contains("fill");
            int ai = Array.IndexOf(args, "amb");
            if (ai >= 0 && ai + 1 < args.Length && byte.TryParse(args[ai + 1], System.Globalization.NumberStyles.HexNumber, null, out var ab)) _ambient = ab;
            ApplyMapArg(args, map);   // `map "K=V;K=V"` → spine-mode overrides etc. (empty V = remove)

            // ONE resolution, the app's own (Model3dArt → MediaResolver), then the explicit overrides and
            // the suppressions on top. Everything below reads these paths — the probe cannot disagree with
            // the renderer about which files it is looking at.
            var art = Suppress(Host.Model3d.Model3dArt.Resolve(map, platform, Guid.Empty, title, ov), suppressed);
            Console.WriteLine($"[jewel-probe] {platform} / \"{title}\" type={(map.TryGetValue("ModelType", out var mt) ? mt : "box")}");
            foreach (var (slot, path) in new[] { ("front", art.Front), ("logo", art.Logo), ("spine", art.Spine),
                                                 ("back", art.Back), ("full", art.Full) })
                Console.WriteLine($"[jewel-probe]   {slot,-6} {(path == null ? (suppressed.Contains(slot) ? "(suppressed)" : "NOT FOUND") : (ov.ContainsKey(slot) ? "[forced] " : "") + path)}");

            if (map.TryGetValue("FrontSpineImage", out var dbgSpec) && dbgSpec.StartsWith("{Resources}\\", StringComparison.OrdinalIgnoreCase))
            {
                string key = dbgSpec.Substring(12);
                string? fRgn = Host.Platforms.LbCaseObj.RegionOfImagePath(art.Front);
                string? sRgn = Host.Platforms.LbCaseObj.RegionOfImagePath(art.Spine);
                var scanBmp = LoadBmp(art.Spine); var frontBmp = LoadBmp(art.Front);
                string suffix = key.Contains(" - ", StringComparison.Ordinal) ? " (explicite)"
                    : Host.Platforms.LbCaseObj.AutoVersionSuffix(key, scanBmp, frontBmp, sRgn, fRgn);
                string resolved = key.Contains(" - ", StringComparison.Ordinal) ? key : key + suffix;
                var got = Host.Platforms.LbCaseObj.SpineImage(resolved, fRgn);
                Console.WriteLine($"[jewel-probe] auto: front={fRgn ?? "-"} spine={sRgn ?? "-"} | \"{key}\" -> \"{resolved}\" -> {(got == null ? "NULL" : got.PixelWidth + "x" + got.PixelHeight)}");
            }

            // "loop <n>": bake the SAME model n times through the real worker pool and report memory as it
            // goes — the Generate-Media-Cache pass in miniature, minus the library. A bake that leaks shows
            // up as a straight line here in seconds, and a fix can be judged on the same line instead of on
            // an hour-long pass. `gc` collects before each report, which separates what the GC can still
            // reclaim (managed) from what it cannot (WPF's unmanaged render resources).
            if (int.TryParse(ArgValue(args, "loop"), out var loopN) && loopN > 0)
                return LeakLoop(map, title, art, loopN, args.Contains("gc"), args.Contains("fresh"), args.Contains("shutdown"));

            byte[]? png = null;
            var t = new Thread(() =>
            {
                try { png = Render(map, platform, title, art, yaw, pitch, dist, w, h); }
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
                    // FlowModel builds NOTHING outside a real window: its art load + model build hang off
                    // Loaded/render hooks that never fire for a control that was only Measured/Arranged.
                    // Host it in an off-screen form for the probe's lifetime — same trick as the live probe.
                    var host = new System.Windows.Forms.Form
                    {
                        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                        StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                        Location = new System.Drawing.Point(-4000, -4000),   // off-screen, still "shown"
                        Size = new System.Drawing.Size(w, h), ShowInTaskbar = false,
                    };
                    prev.Control.Dock = System.Windows.Forms.DockStyle.Fill;
                    host.Controls.Add(prev.Control);
                    host.Show();
                    // Same platform/title arguments as --render-jewel: the A/B is only worth anything if both
                    // sides are asked for the SAME game with the SAME settings. LaunchBox resolves that
                    // game's art through its own code — which is the point, it is the reference.
                    string platform = ArgValue(args, "platform") ?? "Sony Playstation";
                    string title = ArgValue(args, "title") ?? "Final Fantasy 7";
                    // SAME settings cascade as --render-jewel, ctor defaults included: LB has no defaults
                    // entry for every platform (Game Boy, SNES…), and starting one side from an empty map
                    // would make the two renders differ for a reason that has nothing to do with the
                    // renderer — which is precisely the confusion this A/B exists to avoid.
                    var map = new Dictionary<string, string>(
                        Host.Platforms.ModelDefaults.TryGet(platform, platform)
                        ?? Host.Platforms.EditPlatformModel.CtorDefaults()
                        ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    ApplyMapArg(args, map);   // `map "K=V;K=V"` → overrides (empty V = remove)
                    Console.WriteLine($"[oracle] {platform} / \"{title}\"  map: " + string.Join(";", map.Select(kv => kv.Key + "=" + kv.Value)));
                    prev.Redraw(map, title, platform);
                    // FlowModel loads art ASYNC and rebuilds — pump until geometry exists, up to `wait` ms.
                    long until = Environment.TickCount64 + wait;
                    while (Environment.TickCount64 < until && prev.BuiltGeometry() == null) Pump(100);
                    Pump(400);                     // settle: textures land a beat after the group appears
                    if (args.Contains("dump")) DumpStructure(prev.BuiltGeometry());   // exact quads + materials
                    if (l != 0 || r != 0 || u != 0 || dn != 0) { prev.Rotate(l, r, u, dn); Pump(800); }
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(ui);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    png = ms.ToArray();
                    try { host.Close(); host.Dispose(); } catch { }
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
    // Internal: the WINDOWED probe (--model3d-live + LB_ORACLE_DUMP=1) calls it on the oracle zone's
    // geometry — the headless FlowModel never builds (its art/build hooks need a real window), so the
    // live window is where a trustworthy dump comes from.
    internal static void DumpStructure(Model3DGroup? root)
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
            // LB_DUMP_VERTS=1: full positions+uv for small meshes — the only way to recover non-
            // rectangular shapes (the angled flap's diagonal) that bounds can't express.
            if (Environment.GetEnvironmentVariable("LB_DUMP_VERTS") == "1" && mesh.Positions.Count <= 200)
                for (int i = 0; i < mesh.Positions.Count; i++)
                {
                    var p = local.Transform(mesh.Positions[i]);
                    string tuv = i < mesh.TextureCoordinates.Count ? $" uv({mesh.TextureCoordinates[i].X:0.###},{mesh.TextureCoordinates[i].Y:0.###})" : "";
                    Console.WriteLine($"[oracle-dump]     v{i} ({p.X:0.####}, {p.Y:0.####}, {p.Z:0.####}){tuv}");
                }
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

    private static byte[]? Render(Dictionary<string, string> map, string platform, string title,
                                  Host.Model3d.Model3dArt art,
                                  double yaw, double pitch, double dist, int w, int h)
    {
        // BakeRuntimeModel flattens VisualBrush composites to frozen ImageBrush textures (they render headless,
        // unlike the live VisualBrush materials BuildModel emits) — same geometry, faithful to what's shown.
        // Bounds dump (geometry diagnosis): the LIVE model keeps its child structure (paper quads + plastic
        // group) so we can compare the paper-insert depth to the plastic-shell depth.
        var live = Host.Platforms.HomeModel3d.BuildModel(map, title, art);
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
            var bakedOut = Host.Model3d.Model3dBaker.Bake(map, title, art);
            if (bakedOut == null) { Console.WriteLine("[jewel-probe] Bake returned null"); return null; }
            var (meshes, mats, thumb) = bakedOut.Value;
            string tmp = Path.Combine(Path.GetTempPath(), "jewelprobe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".glb");
            try
            {
                Host.Model3d.GlbFile.Write(tmp, meshes, mats, thumb, new Host.Model3d.GlbInfo("probe", "", platform, title, Host.Model3d.Model3dCache.BakerVersion, "probe"));
                var reloaded = Host.Model3d.GlbFile.LoadModel(tmp);
                if (reloaded == null) { Console.WriteLine("[jewel-probe] GLB reload null"); return null; }
                Console.WriteLine("[jewel-probe] GLB round-trip path (bake → write → reload)");
                return RenderModel(reloaded, yaw, pitch, dist, w, h);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        var model = Host.Model3d.Model3dBaker.BakeRuntimeModel(map, title, art);
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

    private static BitmapSource? LoadBmp(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit(); bi.UriSource = new Uri(path); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Bake the same model <paramref name="n"/> times through Model3dBaker's real STA worker pool
    /// — the same path Generate Media Cache drives — reporting memory every 50. Same art, same settings
    /// every round, so anything that grows is the bake retaining what it should have released.</summary>
    private static int LeakLoop(Dictionary<string, string> map, string title, Host.Model3d.Model3dArt art,
                                int n, bool collect, bool fresh, bool shutdown)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        void Report(int i, string note = "")
        {
            if (collect) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            proc.Refresh();
            Console.WriteLine($"[leak] {i,5}/{n}  managed={GC.GetTotalMemory(false) / (1024 * 1024),5} MB"
                            + $"  private={proc.PrivateMemorySize64 / (1024 * 1024),5} MB"
                            + $"  ws={proc.WorkingSet64 / (1024 * 1024),5} MB  handles={proc.HandleCount,6}{note}");
        }
        Console.WriteLine($"[leak] {(fresh ? "one THROWAWAY STA thread per bake" + (shutdown ? " + dispatcher shutdown" : "") : Host.Model3d.Model3dBaker.WorkerCount + " persistent STA worker(s)")}, collect={collect}");
        Report(0, "  (baseline)");
        for (int i = 1; i <= n; i++)
        {
            object? baked;
            if (fresh)
            {
                // Same work, but the thread — and with it the Dispatcher and MediaContext WPF attaches to
                // it — dies after every bake. If the growth goes away here and not with the pool, what the
                // pool retains is per-thread WPF render state, not anything the bake itself allocates.
                object? r = null;
                var th = new Thread(() =>
                {
                    try { r = Host.Model3d.Model3dBaker.Bake(map, title, art); } catch { }
                    // `shutdown`: end the Dispatcher WPF attached to this thread. Without it the dispatcher
                    // stays registered in WPF's static table forever — holding its MediaContext, everything
                    // rendered through it, and the thread itself (hence the handle count climbing).
                    if (shutdown) { try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { } }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.Start(); th.Join();
                baked = r;
            }
            else baked = Host.Model3d.Model3dBaker.Run(() => Host.Model3d.Model3dBaker.Bake(map, title, art));
            if (baked == null) { Report(i, "  BAKE RETURNED NULL"); return 1; }
            if (i % 50 == 0 || i == n) Report(i);
        }
        return 0;
    }

    /// <summary>Point MediaResolver at the LaunchBox root so `platform`+`title` resolve. There is no plugin
    /// host here, so it answers from LB's conventional Images\&lt;platform&gt;\&lt;type&gt; layout. The root is the
    /// exe's Core\.. when the DEPLOYED build is run, else the current directory.</summary>
    private static void InitMediaRoot()
    {
        try
        {
            if (!string.IsNullOrEmpty(Host.Media.MediaResolver.LbRoot)) return;
            string exeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            string root = Directory.Exists(Path.Combine(exeRoot, "Images")) ? exeRoot : Environment.CurrentDirectory;
            Host.Media.MediaResolver.Init(root);
        }
        catch (Exception ex) { Console.WriteLine("[jewel-probe] media init: " + ex.Message); }
    }

    /// <summary>Blank the named slots — "noscan"/"noback" reproduce a game that simply does not have that
    /// scan, which is a different render path (the preset spine cap, the grey back insert) and not something
    /// the library can be asked for on demand.</summary>
    private static Host.Model3d.Model3dArt Suppress(Host.Model3d.Model3dArt art, HashSet<string> slots)
        => slots.Count == 0 ? art : art with
        {
            Front = slots.Contains("front") ? null : art.Front,
            Logo = slots.Contains("logo") ? null : art.Logo,
            Spine = slots.Contains("spine") ? null : art.Spine,
            Back = slots.Contains("back") ? null : art.Back,
            Full = slots.Contains("full") ? null : art.Full,
        };
}
