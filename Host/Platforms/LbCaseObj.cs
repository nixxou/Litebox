// Home-made Wavefront OBJ/MTL loader for LaunchBox's embedded plastic-case models (JewelCaseObj,
// GenericCaseObj/"DVDcase", LongJewelCaseObj, ClearSpineJewelCaseObj + their Mtl twins). The models live as
// STRING entries in Unbroken.LaunchBox.Windows.dll's JewelCaseSpines.resources bundle — we read them at
// RUNTIME from the LB install (nothing redistributed) and parse them ourselves (no HelixToolkit dependency).
//
// LB's MTL dialect quirk (decoded from CD_rib.mtl vs the built WPF materials): `Kd` may carry FOUR components
// — the FIRST is then the ALPHA (e.g. "Kd 0.125 0.8 0.8 0.8" → #1FCCCCCC). Materials map to
// MaterialGroup[ Diffuse(Kd) + Specular(Ks, pow=Ns) ] applied to Material AND BackMaterial (matches the
// structure dump of LB's built jewel case).

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LbApiHost.Host.Platforms;

internal static class LbCaseObj
{
    private static readonly Dictionary<string, (Model3DGroup group, List<string> names)?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Load an embedded case model by resource base name ("JewelCase", "GenericCase", "LongJewelCase",
    /// "ClearSpineJewelCase") → a frozen Model3DGroup of its material segments, or null on failure. Cached.
    /// <paramref name="vars"/> fills LB's MTL template placeholders ("Kd {{{CaseColor}}}" in GenericCaseMtl)
    /// with "r g b" float strings.</summary>
    public static Model3DGroup? Load(string baseName, Dictionary<string, string>? vars = null)
        => LoadWithNames(baseName, vars).group;

    /// <summary>Like Load, but also returns each segment's MTL material name (in child order) — the DVD wrap is
    /// the segment with an EMPTY material name (usemtl with no argument in GenericCaseObj), which the caller
    /// replaces with the composed cover texture.</summary>
    public static (Model3DGroup? group, List<string> segmentNames) LoadWithNames(string baseName, Dictionary<string, string>? vars = null)
    {
        string key = baseName + (vars == null ? "" : "|" + string.Join(";", System.Linq.Enumerable.Select(vars, kv => kv.Key + "=" + kv.Value)));
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit)) return (hit?.group, hit?.names ?? new List<string>());
            (Model3DGroup group, List<string> names)? m = null;
            try { m = Build(baseName, vars); } catch (Exception ex) { Console.WriteLine($"[caseobj] {baseName}: {ex.Message}"); }
            _cache[key] = m;
            return (m?.group, m?.names ?? new List<string>());
        }
    }

    private static (Model3DGroup, List<string>)? Build(string baseName, Dictionary<string, string>? vars)
    {
        string? objText = ReadResourceString(baseName + "Obj");
        string? mtlText = ReadResourceString(baseName + "Mtl");
        if (objText == null) return null;
        if (vars != null && mtlText != null)
            mtlText = System.Text.RegularExpressions.Regex.Replace(mtlText, @"\{\{\{(\w+)\}\}\}",
                m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : "0.5 0.5 0.5");
        var mats = mtlText != null ? ParseMtl(mtlText) : new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        var (grp, names) = ParseObj(objText, mats);
        // FROZEN: the cache is shared across THREADS (UI previews + the GLB bake STA worker — whichever
        // populates it first would otherwise own the objects and the other thread throws "different thread
        // owns it"). Every caller Clone()s before mutating (the DVD wrap material swap works on the clone),
        // and cloning a frozen Freezable from any thread is legal — freezing costs nothing here.
        if (grp.CanFreeze) grp.Freeze();
        else Console.WriteLine("[caseobj] " + baseName + ": not freezable — cross-thread use will fail");
        return (grp, names);
    }

    // ── resource extraction (JewelCaseSpines.resources uses the DeserializingResourceReader format) ──
    private static Dictionary<string, string>? _resEntries;          // Obj/Mtl model texts
    private static Dictionary<string, object?>? _resImages;         // spine preset images (raw values)

    private static void EnsureResources()
    {
        if (_resEntries != null) return;
        _resEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _resImages = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var win = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unbroken.LaunchBox.Windows")
                      ?? Assembly.LoadFrom(System.IO.Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll"));
            var resName = win.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("JewelCaseSpines.resources", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return;
            using var s = win.GetManifestResourceStream(resName);
            var ext = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "System.Resources.Extensions")
                      ?? Assembly.Load("System.Resources.Extensions");
            var rt = ext.GetType("System.Resources.Extensions.DeserializingResourceReader")!;
            var reader = Activator.CreateInstance(rt, s);
            var en = (System.Collections.IDictionaryEnumerator)rt.GetMethod("GetEnumerator", Type.EmptyTypes)!.Invoke(reader, null)!;
            while (en.MoveNext())
            {
                string k = en.Key?.ToString() ?? "";
                try
                {
                    if (k.EndsWith("Obj", StringComparison.Ordinal) || k.EndsWith("Mtl", StringComparison.Ordinal))
                    { if (en.Value is string str) _resEntries[k] = str; }
                    else _resImages[k] = en.Value;   // spine preset image (byte[]/Bitmap/stream depending on format)
                }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("[caseobj] resources: " + ex.Message); }
    }

    private static string? ReadResourceString(string key)
    {
        // LiteBox's OWN shipped copy first (case-assets/<key>.txt — no LaunchBox needed at runtime).
        try
        {
            using var s = typeof(LbCaseObj).Assembly.GetManifestResourceStream("case-assets/" + key + ".txt");
            if (s != null) { using var r = new System.IO.StreamReader(s); return r.ReadToEnd(); }
        }
        catch { }
        lock (_cache) { EnsureResources(); return _resEntries!.TryGetValue(key, out var v) ? v : null; }
    }

    /// <summary>Spine-preset image by entry name (e.g. "Sony Playstation - NA"); exact key first, then the
    /// regional variants (Auto-Detect approximation — LB detects from the game's region). Sources: LiteBox's
    /// OWN shipped copies (case-assets/<name>.png) first, then LB's dll bundle as fallback. Null if none.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? SpineImage(string name, string? regionHint = null)
    {
        // "Auto-Detect" = the preset name with NO " - <version>" suffix. It used to fall straight through to
        // the " - NA" asset, so a PAL game got the North-American hinge. With a regionHint (the region folder
        // of the game's resolved FRONT art, else its Region field) a European game now prefers " - EU".
        // An EXPLICIT version (name already carries " - …") is honoured first and only falls back if missing.
        bool explicitVersion = name.Contains(" - ", StringComparison.Ordinal);
        var candidates = explicitVersion
            ? new[] { name, name + " - NA", name + " - EU", name + " - NA Black", name + " - NA White" }
            : IsEuropeanRegion(regionHint)
                ? new[] { name + " - EU", name, name + " - NA", name + " - NA Black", name + " - NA White" }
                : new[] { name, name + " - NA", name + " - NA Black", name + " - NA White", name + " - EU" };
        foreach (var k in candidates)
            try
            {
                using var s = typeof(LbCaseObj).Assembly.GetManifestResourceStream("case-assets/" + k + ".png");
                if (s != null)
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = s;
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
            catch { }
        lock (_cache)
        {
            EnsureResources();
            foreach (var k in candidates)
                if (_resImages!.TryGetValue(k, out var raw) && raw != null)
                    return DecodeImage(raw);
            return null;
        }
    }

    /// <summary>PAL/European territory? Used by the Auto-Detect spine version. The names are LaunchBox's
    /// region folder names (Images\&lt;Platform&gt;\&lt;Type&gt;\&lt;Region&gt;\) plus the usual Region-field values.</summary>
    internal static bool IsEuropeanRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return false;   // unknown → LB's NA default
        foreach (var eu in new[] { "europe", "united kingdom", "france", "germany", "italy", "spain",
                                   "netherlands", "sweden", "norway", "denmark", "finland", "russia",
                                   "australia", "pal" })
            if (region.IndexOf(eu, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>The LaunchBox region folder of an image path (…\Images\&lt;Platform&gt;\&lt;Type&gt;\&lt;Region&gt;\file),
    /// or null when the path isn't inside an Images tree.</summary>
    internal static string? RegionOfImagePath(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = System.IO.Path.GetFullPath(path).Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Equals("Images", StringComparison.OrdinalIgnoreCase))
                    return i + 3 < parts.Length ? parts[i + 3] : null;   // Images/<platform>/<type>/<region>/file
            return null;
        }
        catch { return null; }
    }

    private static System.Windows.Media.Imaging.BitmapSource? DecodeImage(object raw)
    {
        try
        {
            System.IO.Stream? s = raw switch
            {
                byte[] b => new System.IO.MemoryStream(b),
                System.IO.Stream st => st,
                System.Drawing.Bitmap bmp => BmpToStream(bmp),
                _ => null,
            };
            if (s == null) { Console.WriteLine("[caseobj] spine image type? " + raw.GetType().FullName); return null; }
            s.Position = 0;
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.StreamSource = s;
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex) { Console.WriteLine("[caseobj] spine decode: " + ex.Message); return null; }
    }

    private static System.IO.Stream BmpToStream(System.Drawing.Bitmap bmp)
    {
        var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms;
    }

    // ── MTL ──
    private static Dictionary<string, Material> ParseMtl(string text)
    {
        var res = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        double a = 1, dr = 0.8, dg = 0.8, db = 0.8, sr = 0.5, sg = 0.5, sb = 0.5, ns = 250;
        void Flush()
        {
            if (name == null) return;
            var mg = new MaterialGroup();
            mg.Children.Add(new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(B(a), B(dr), B(dg), B(db)))));
            mg.Children.Add(new SpecularMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(B(sr), B(sg), B(sb))), ns));
            res[name] = mg;
        }
        foreach (var raw in text.Split('\n'))
        {
            var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0])
            {
                case "newmtl": Flush(); name = p.Length > 1 ? p[1] : null; a = 1; dr = dg = db = 0.8; sr = sg = sb = 0.5; ns = 250; break;
                case "Kd":
                    // LB dialect: 4 components = alpha r g b; standard 3 = r g b.
                    if (p.Length >= 5) { a = D(p[1]); dr = D(p[2]); dg = D(p[3]); db = D(p[4]); }
                    else if (p.Length >= 4) { dr = D(p[1]); dg = D(p[2]); db = D(p[3]); }
                    break;
                case "Ks": if (p.Length >= 4) { sr = D(p[1]); sg = D(p[2]); sb = D(p[3]); } break;
                case "Ns": if (p.Length >= 2) ns = D(p[1]); break;
                case "d": if (p.Length >= 2) a *= D(p[1]); break;
            }
        }
        Flush();
        return res;
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    // ── OBJ ──
    private static (Model3DGroup, List<string>) ParseObj(string text, Dictionary<string, Material> mats)
    {
        var vs = new List<Point3D>();
        var vts = new List<System.Windows.Point>();
        var vns = new List<Vector3D>();
        var grp = new Model3DGroup();
        var names = new List<string>();

        MeshGeometry3D? mesh = null;
        Material? cur = null;
        string curName = "";
        Dictionary<(int, int, int), int>? dedup = null;

        void StartSegment(Material? m, string name)
        {
            FlushSegment();
            cur = m;
            curName = name;
            mesh = new MeshGeometry3D();
            dedup = new();
        }
        void FlushSegment()
        {
            if (mesh == null || mesh.Positions.Count == 0) return;
            var mat = cur ?? new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80)));
            grp.Children.Add(new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat });
            names.Add(curName);
            mesh = null;
        }
        int AddVert(string spec)
        {
            var q = spec.Split('/');
            int vi = Idx(q[0], vs.Count);
            int ti = q.Length > 1 ? Idx(q[1], vts.Count) : -1;
            int ni = q.Length > 2 ? Idx(q[2], vns.Count) : -1;
            var k = (vi, ti, ni);
            if (dedup!.TryGetValue(k, out var ex)) return ex;
            mesh!.Positions.Add(vs[vi]);
            if (ti >= 0 && ti < vts.Count) mesh.TextureCoordinates.Add(vts[ti]);
            if (ni >= 0 && ni < vns.Count) mesh.Normals.Add(vns[ni]);
            int ix = mesh.Positions.Count - 1;
            dedup[k] = ix;
            return ix;
        }
        static int Idx(string s, int count) { int i = int.Parse(s, CultureInfo.InvariantCulture); return i > 0 ? i - 1 : count + i; }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (p[0])
            {
                case "v": vs.Add(new Point3D(D(p[1]), D(p[2]), D(p[3]))); break;
                case "vt": vts.Add(new System.Windows.Point(D(p[1]), 1 - D(p[2]))); break;   // OBJ origin bottom-left → WPF top-left
                case "vn": vns.Add(new Vector3D(D(p[1]), D(p[2]), D(p[3]))); break;
                case "usemtl":
                    string mn = p.Length > 1 ? p[1] : "";
                    StartSegment(mats.TryGetValue(mn, out var m) ? m : null, mn);
                    break;
                case "f":
                    if (mesh == null) StartSegment(null, "");
                    // fan-triangulate (tris, quads and n-gons all appear in LB's models)
                    int a0 = AddVert(p[1]);
                    for (int i = 2; i + 1 < p.Length; i++)
                    {
                        int b0 = AddVert(p[i]); int c0 = AddVert(p[i + 1]);
                        mesh!.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(c0);
                    }
                    break;
            }
        }
        FlushSegment();
        return (grp, names);
    }
}
