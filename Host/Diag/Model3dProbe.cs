// PLAN A make-or-break probe (--model3d-probe <platform> <scrapeAs> <out.png>): can LiteBox host LaunchBox's
// OWN 3D box control by reflection instead of reimplementing the geometry? Instantiates the core's
// CoverFlow.FlowModel (a WPF UserControl), builds a ModelSettings (new + set every prop from the platform
// defaults), builds a throwaway concrete Game (Title/Platform so LB resolves the box art off disk), calls
// RedrawModel(game, settings), pumps the dispatcher, then renders FlowModel.Viewport to a PNG via
// RenderTargetBitmap. If the PNG shows the case → plan A works and we host FlowModel.Viewport in an ElementHost.
// Runs on an STA thread with a WPF Application context. Every step is logged to <Core>\model3d-probe.log.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace LbApiHost.Host.Diag;

internal static class Model3dProbe
{
    public static string GameTitle = "SampleGame";   // set via a 4th arg so a real title resolves box art

    public static void Run(string lbRoot, string platform, string scrapeAs, string outPng)
    {
        var sb = new StringBuilder();
        void L(string s) { Console.WriteLine("[model3d] " + s); sb.AppendLine(s); }
        void Save() { try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model3d-probe.log"), sb.ToString()); } catch { } }

        var t = new Thread(() =>
        {
            try { RunSta(lbRoot, platform, scrapeAs, outPng, L); }
            catch (Exception ex) { L("FATAL: " + ex); }
            Save();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(40));
        if (t.IsAlive) { L("TIMEOUT (STA thread still running)"); Save(); }
    }

    private static void RunSta(string lbRoot, string platform, string scrapeAs, string outPng, Action<string> L)
    {
        var win = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll"));
        var baseAsm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.dll"));
        L("loaded " + win.GetName().Name + " + " + baseAsm.GetName().Name);

        // WPF Application context (some controls need Application.Current + resources).
        EnsureWpfApp(L);

        // Initialize the core naming/images context so a Game's image paths resolve off disk. The RootFolder
        // setter throws at boot for an unknown reason — capture the FULL exception to see what it needs.
        try
        {
            var nh = baseAsm.GetType("Unbroken.LaunchBox.NamingHelper");
            var rf = nh?.GetProperty("RootFolder", BindingFlags.Public | BindingFlags.Static);
            if (rf?.SetMethod != null)
            {
                try { rf.SetValue(null, lbRoot); L("NamingHelper.RootFolder set = " + rf.GetValue(null)); }
                catch (Exception ex) { L("NamingHelper.RootFolder SET THREW:\n" + (ex.InnerException?.ToString() ?? ex.ToString())); }
            }
            else L("NamingHelper.RootFolder not settable");
        }
        catch (Exception ex) { L("NamingHelper access: " + ex.Message); }

        // Configure the LocalDb metadata context — GetImageFolder builds a Platform-with-metadata which queries
        // GamesDb (EF); without a DbFilePath it NREs (same requirement as EmuInstall.EnsureLocalDbConfigured).
        try
        {
            var lc = Type.GetType("Unbroken.LaunchBox.LocalDb.LocalDbContext, Unbroken.LaunchBox.LocalDb");
            var dbp = lc?.GetProperty("DbFilePath", BindingFlags.Public | BindingFlags.Static);
            string db = Path.Combine(lbRoot, "Metadata", "LaunchBox.Metadata.db");
            if (dbp?.SetMethod != null && File.Exists(db)) { dbp.SetValue(null, db); L("LocalDbContext.DbFilePath = " + db); }
            else L("LocalDbContext.DbFilePath unset (prop=" + (dbp != null) + " dbExists=" + File.Exists(db) + ")");
        }
        catch (Exception ex) { L("LocalDb config: " + ex.Message); }

        // GamesDb.GetValidPlatformNameAsync NREs on a null static delegate GetPlatformScrapeValueFunc — set it
        // to identity (platform name → itself) so platform-metadata lookup doesn't crash.
        try
        {
            var gamesDb = Type.GetType("Unbroken.LaunchBox.LocalDb.GamesDb, Unbroken.LaunchBox.LocalDb");
            var fp = gamesDb?.GetProperty("GetPlatformScrapeValueFunc", BindingFlags.Public | BindingFlags.Static);
            if (fp?.SetMethod != null)
            {
                var ft = fp.PropertyType;               // Func<TIn,TOut>
                var ga = ft.GetGenericArguments();
                L("GetPlatformScrapeValueFunc type = Func<" + string.Join(",", ga.Select(a => a.Name)) + ">, current=" + (fp.GetValue(null) == null ? "null" : "set"));
                if (ga.Length == 2 && ga[0] == typeof(string) && ga[1] == typeof(string))
                {
                    Func<string, string> id = s => s;
                    fp.SetValue(null, id);
                    L("  set GetPlatformScrapeValueFunc = identity");
                }
            }
        }
        catch (Exception ex) { L("GetPlatformScrapeValueFunc: " + ex.Message); }

        // Inject a BARE core Root.DataManager (proven technique from EmuInstall) — the image-scan statics
        // (GetActualFrontImages → obfuscated CalcProjectProxy) read it; without it they NRE.
        try
        {
            var tRoot = win.GetType("Unbroken.LaunchBox.Windows.Root");
            var tDm = win.GetType("Unbroken.LaunchBox.Windows.Data.DataManager");
            var rootDm = tRoot?.GetProperty("DataManager", BindingFlags.Public | BindingFlags.Static);
            var ctor = tDm?.GetConstructor(new[] { typeof(bool), typeof(bool) });
            if (rootDm?.SetMethod != null && ctor != null)
            {
                var shim = ctor.Invoke(new object[] { true, false });
                rootDm.SetValue(null, shim);
                L("Root.DataManager injected (bare shim)");
            }
            else L("Root.DataManager / DataManager(bool,bool) not resolvable");
        }
        catch (Exception ex) { L("Root.DataManager inject THREW: " + (ex.InnerException?.Message ?? ex.Message)); }

        // 1) ModelSettings = new + set every prop from the hardcoded platform defaults (proven-clean call).
        var msType = win.GetType("Unbroken.LaunchBox.Windows.Data.ModelSettings")!;
        var getDef = msType.GetMethod("GetDefaultSettings", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null)!;
        object? settings = getDef.Invoke(null, new object?[] { platform, string.IsNullOrEmpty(scrapeAs) ? platform : scrapeAs });
        if (settings == null)
        {
            L("GetDefaultSettings → null; building a bare box ModelSettings");
            settings = Activator.CreateInstance(msType);
            msType.GetProperty("ModelType")?.SetValue(settings, "box");
            msType.GetProperty("UseFullScanImages")?.SetValue(settings, true);
        }
        L("ModelSettings ready: ModelType=" + msType.GetProperty("ModelType")?.GetValue(settings));

        // 2) throwaway concrete Game with Title + Platform so LB's image-path props resolve off disk.
        var gameType = win.GetType("Unbroken.LaunchBox.Windows.Data.Game")!;
        object game = MakeGame(gameType, platform, L);

        // 3) FlowModel (WPF UserControl) — parameterless or (bool loadMainThread) ctor.
        var fmType = win.GetType("Unbroken.LaunchBox.Windows.Controls.CoverFlow.FlowModel")!;
        object flow = NewFlow(fmType, L);

        // OFFICIAL PATH first: RedrawModel(game, settings) — now that GamesDb may resolve.
        var redraw = fmType.GetMethod("RedrawModel", BindingFlags.Public | BindingFlags.Instance);
        try { redraw?.Invoke(flow, new[] { game, settings }); L("RedrawModel invoked OK"); }
        catch (Exception ex) { L("RedrawModel THREW: " + (ex.InnerException?.Message ?? ex.Message)); }
        Pump(L, 2500);
        var mdl = fmType.GetProperty("Model", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(flow);
        L("after RedrawModel: Model = " + (mdl?.GetType().Name ?? "null") + "  Children=" + ChildCount(mdl));
        if (mdl != null && ChildCount(mdl) > 0)
        {
            var vp0 = fmType.GetField("Viewport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(flow);
            L("★ RedrawModel produced geometry — rendering the OFFICIAL model");
            if (vp0 != null) { RenderToPng(vp0, outPng, L); return; }
        }

        // PLAN A2 — bypass the Game/EF image chain: feed BITMAPS to the low-level case builder directly.
        // Enumerate FlowModel static methods returning ModelVisual3D, print their FULL signatures, then try
        // to invoke each with synthetic bitmaps + our ModelSettings and report which yields geometry.
        L("");
        L("=== A2: static ModelVisual3D builders on FlowModel ===");
        var builders = fmType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType.Name == "ModelVisual3D")
            .ToArray();
        var bmp = MakeTestBitmap(win, L, 512, 640);   // System.Drawing.Bitmap (solid) — texture content irrelevant for a geometry test
        object? best = null;
        foreach (var m in builders)
        {
            var ps = m.GetParameters();
            L($"try {m.Name}({string.Join(", ", ps.Select(p => FullSig(p.ParameterType) + (p.IsOut ? " out" : p.ParameterType.IsByRef ? " ref" : "") + " " + p.Name))})");
            // The params are Object-typed (obfuscation erased Bitmap/ModelSettings). Fill BY POSITION per the
            // deobfuscated delegate sigs: out/ref Lists = null (method allocates), the LAST Object param =
            // ModelSettings, all other Object params = bitmaps. Try two variants for the leading slot (bmp, then
            // game) so the Game+4bmp shape is covered too.
            int lastObj = -1;
            for (int k = 0; k < ps.Length; k++) { var et0 = ps[k].ParameterType.IsByRef ? ps[k].ParameterType.GetElementType()! : ps[k].ParameterType; if (!ps[k].ParameterType.IsByRef && et0.Name == "Object") lastObj = k; }
            foreach (var leadGame in new[] { false, true })
            {
                var args = new object?[ps.Length];
                for (int k = 0; k < ps.Length; k++)
                {
                    var pt = ps[k].ParameterType;
                    if (pt.IsByRef) { args[k] = null; continue; }          // out/ref List
                    if (k == lastObj) { args[k] = settings; continue; }    // last Object = ModelSettings
                    args[k] = (leadGame && k == 0) ? game : bmp;           // leading slot: bmp, then game variant
                }
                try
                {
                    var mv = m.Invoke(null, args);
                    int kids = ChildCount(mv);
                    L($"    [{(leadGame ? "game+bmp" : "all-bmp")}] → {mv?.GetType().Name ?? "null"}  Children={kids}");
                    if (mv != null && kids > 0 && best == null) { best = mv; L("    ★ GEOMETRY — keeping this one"); break; }
                }
                catch (Exception ex) { L($"    [{(leadGame ? "game+bmp" : "all-bmp")}] THREW: " + (ex.InnerException?.Message ?? ex.Message)); }
            }
        }

        // Render: put the best ModelVisual3D into the FlowModel.Viewport and snapshot it.
        var viewport = fmType.GetField("Viewport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(flow);
        L("");
        L("Viewport = " + (viewport?.GetType().FullName ?? "null") + "; best builder = " + (best != null ? "found" : "NONE"));
        if (viewport == null) { L("no Viewport to render"); return; }
        if (best != null) TryAddToViewport(viewport, best, L);
        Pump(L, 500);
        RenderToPng(viewport, outPng, L);
    }

    // ── helpers (all reflection so this file compiles without a WPF/core reference) ──

    private static void EnsureWpfApp(Action<string> L)
    {
        try
        {
            var appType = Type.GetType("System.Windows.Application, PresentationFramework");
            if (appType == null) { L("PresentationFramework not resolved yet (will load on first WPF use)"); return; }
            var cur = appType.GetProperty("Current")?.GetValue(null);
            if (cur == null) { Activator.CreateInstance(appType); L("created WPF Application"); }
            else L("WPF Application already present");
        }
        catch (Exception ex) { L("EnsureWpfApp: " + ex.Message); }
    }

    private static object MakeGame(Type gameType, string platform, Action<string> L)
    {
        object game;
        var ctorBool = gameType.GetConstructor(new[] { typeof(bool) });
        game = ctorBool != null ? ctorBool.Invoke(new object[] { true }) : Activator.CreateInstance(gameType)!;
        // Set Title + Platform via the property setter or the backing field, walking the hierarchy.
        SetMember(game, "Title", GameTitle, L);
        SetMember(game, "Platform", platform, L);
        L("Game: Title=" + Get(game, "Title") + " Platform=" + Get(game, "Platform"));
        // Full-exception read of FrontImagePath — its stack names the missing static if it throws.
        try
        {
            var fip = game.GetType().GetProperty("FrontImagePath", BindingFlags.Public | BindingFlags.Instance);
            var v = fip?.GetValue(game);
            L("  FrontImagePath = " + (v ?? "(null)"));
        }
        catch (TargetInvocationException tie) { L("  FrontImagePath THREW:\n" + (tie.InnerException?.ToString() ?? tie.ToString())); }
        catch (Exception ex) { L("  FrontImagePath THREW:\n" + ex); }
        return game;
    }

    private static object NewFlow(Type fmType, Action<string> L)
    {
        var cb = fmType.GetConstructor(new[] { typeof(bool) });
        var flow = cb != null ? cb.Invoke(new object[] { true }) : Activator.CreateInstance(fmType)!;
        L("FlowModel instantiated (" + (cb != null ? "loadMainThread ctor" : "default ctor") + ")");
        return flow;
    }

    private static void SetMember(object obj, string name, object? value, Action<string> L)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null && p.CanWrite) { try { p.SetValue(obj, value); return; } catch { } }
            var f = t.GetField("_" + char.ToLowerInvariant(name[0]) + name.Substring(1), BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? t.GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { try { f.SetValue(obj, value); return; } catch { } }
        }
        L("could not set " + name);
    }

    private static object? Get(object obj, string name)
    {
        try { for (var t = obj.GetType(); t != null; t = t.BaseType) { var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly); if (p != null) return p.GetValue(obj); } }
        catch (Exception ex) { return "<throws: " + (ex.InnerException?.Message ?? ex.Message) + ">"; }
        return null;
    }

    private static string FullSig(Type t)
    {
        var e = t.IsByRef ? t.GetElementType()! : t;
        if (!e.IsGenericType) return e.Name;
        return e.Name.Split('`')[0] + "<" + string.Join(",", e.GetGenericArguments().Select(a => a.Name)) + ">";
    }

    private static int ChildCount(object? modelVisual)
    {
        try
        {
            var kids = modelVisual?.GetType().GetProperty("Children")?.GetValue(modelVisual);
            var cnt = kids?.GetType().GetProperty("Count")?.GetValue(kids);
            return cnt is int i ? i : 0;
        }
        catch { return 0; }
    }

    // A solid System.Drawing.Bitmap (the case builder textures with it; content doesn't matter for a geometry test).
    private static object? MakeTestBitmap(Assembly win, Action<string> L, int w, int h)
    {
        try
        {
            var bmpType = Type.GetType("System.Drawing.Bitmap, System.Drawing.Common") ?? Type.GetType("System.Drawing.Bitmap, System.Drawing");
            if (bmpType == null) { L("System.Drawing.Bitmap not resolvable"); return null; }
            var bmp = Activator.CreateInstance(bmpType, w, h);
            return bmp;
        }
        catch (Exception ex) { L("MakeTestBitmap: " + ex.Message); return null; }
    }

    private static void TryAddToViewport(object viewport, object modelVisual, Action<string> L)
    {
        try
        {
            // Viewport3D.Children.Add(modelVisual) — clear existing first.
            var childrenProp = viewport.GetType().GetProperty("Children");
            var children = childrenProp?.GetValue(viewport);
            if (children == null) { L("viewport has no Children"); return; }
            children.GetType().GetMethod("Add")?.Invoke(children, new[] { modelVisual });
            var cnt = children.GetType().GetProperty("Count")?.GetValue(children);
            L("added best model to viewport (Viewport.Children now " + cnt + ")");
        }
        catch (Exception ex) { L("TryAddToViewport: " + ex.Message); }
    }

    private static void Pump(Action<string> L, int ms)
    {
        try
        {
            var dispType = Type.GetType("System.Windows.Threading.Dispatcher, WindowsBase");
            var frameType = Type.GetType("System.Windows.Threading.DispatcherFrame, WindowsBase");
            if (dispType == null || frameType == null) { Thread.Sleep(ms); return; }
            var frame = Activator.CreateInstance(frameType)!;
            var timer = new System.Threading.Timer(_ => frameType.GetProperty("Continue")!.SetValue(frame, false), null, ms, Timeout.Infinite);
            dispType.GetMethod("PushFrame", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new[] { frame });
            timer.Dispose();
            L("pumped dispatcher " + ms + "ms");
        }
        catch (Exception ex) { L("Pump: " + ex.Message); Thread.Sleep(ms); }
    }

    private static void RenderToPng(object viewport, string outPng, Action<string> L)
    {
        try
        {
            var pf = Assembly.Load("PresentationFramework");
            var pc = Assembly.Load("PresentationCore");
            var wb = Assembly.Load("WindowsBase");
            var uiElement = pc.GetType("System.Windows.UIElement")!;
            var sizeType = wb.GetType("System.Windows.Size")!;
            var rectType = wb.GetType("System.Windows.Rect")!;

            // Force measure/arrange at a fixed size so the Viewport has a render area.
            var size = Activator.CreateInstance(sizeType, 480.0, 640.0)!;
            uiElement.GetMethod("Measure")!.Invoke(viewport, new[] { size });
            var rect = Activator.CreateInstance(rectType, 0.0, 0.0, 480.0, 640.0)!;
            uiElement.GetMethod("Arrange")!.Invoke(viewport, new[] { rect });
            uiElement.GetMethod("UpdateLayout")!.Invoke(viewport, null);

            var rtbType = pc.GetType("System.Windows.Media.Imaging.RenderTargetBitmap")!;
            var pixelFormats = pc.GetType("System.Windows.Media.PixelFormats")!;
            var pbgra = pixelFormats.GetProperty("Pbgra32")!.GetValue(null);
            var rtb = Activator.CreateInstance(rtbType, 480, 640, 96.0, 96.0, pbgra)!;
            rtbType.GetMethod("Render")!.Invoke(rtb, new[] { viewport });

            var encType = pc.GetType("System.Windows.Media.Imaging.PngBitmapEncoder")!;
            var enc = Activator.CreateInstance(encType)!;
            var framesProp = encType.GetProperty("Frames")!;
            var frames = framesProp.GetValue(enc);
            var bitmapFrameType = pc.GetType("System.Windows.Media.Imaging.BitmapFrame")!;
            var createFrame = bitmapFrameType.GetMethod("Create", new[] { pc.GetType("System.Windows.Media.Imaging.BitmapSource")! })!;
            var frame = createFrame.Invoke(null, new[] { rtb });
            frames!.GetType().GetMethod("Add")!.Invoke(frames, new[] { frame });

            using var fs = File.Create(outPng);
            encType.GetMethod("Save")!.Invoke(enc, new object[] { fs });
            L("wrote " + outPng);
        }
        catch (Exception ex) { L("RenderToPng THREW: " + (ex.InnerException?.ToString() ?? ex.ToString())); }
    }
}
