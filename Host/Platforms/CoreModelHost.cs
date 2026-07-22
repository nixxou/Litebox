// Live 3D box preview by hosting LaunchBox's OWN CoverFlow.FlowModel control (WPF UserControl) in a WinForms
// ElementHost. LiteBox's UI thread is STA (WinForms), so the control lives directly on it — no separate thread
// (unlike Model3dProbe, which had no UI thread). All LB core types are reflected (obfuscated); WPF types are
// used directly (UseWPF=true).
//
// The one-time INIT recipe that makes RedrawModel resolve a game's box art (decoded 2026-07-22, see
// reference-lb-3d-box-models): NamingHelper.RootFolder → LocalDbContext.DbFilePath → GamesDb
// .GetPlatformScrapeValueFunc = identity (the load-bearing null-delegate fix) → Root.DataManager = bare shim.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Platforms;

internal static class CoreModelHost
{
    private static bool _initTried, _available;
    private static Assembly? _win, _baseAsm, _localDb;
    private static Type? _flowType, _gameType, _msType;
    private static MethodInfo? _redraw, _rotate;

    /// <summary>True when the obfuscated core + init succeeded and a live preview can be built.</summary>
    public static bool Available { get { EnsureInit(); return _available; } }

    private static void EnsureInit()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            string core = AppContext.BaseDirectory;
            _win = Assembly.LoadFrom(Path.Combine(core, "Unbroken.LaunchBox.Windows.dll"));
            _baseAsm = Assembly.LoadFrom(Path.Combine(core, "Unbroken.LaunchBox.dll"));
            try { _localDb = Assembly.LoadFrom(Path.Combine(core, "Unbroken.LaunchBox.LocalDb.dll")); } catch { }

            _flowType = _win.GetType("Unbroken.LaunchBox.Windows.Controls.CoverFlow.FlowModel");
            _gameType = _win.GetType("Unbroken.LaunchBox.Windows.Data.Game");
            _msType = _win.GetType("Unbroken.LaunchBox.Windows.Data.ModelSettings");
            _redraw = _flowType?.GetMethod("RedrawModel", BindingFlags.Public | BindingFlags.Instance);
            _rotate = _flowType?.GetMethod("RotateModel", new[] { typeof(double), typeof(double), typeof(double), typeof(double) });
            if (_flowType == null || _gameType == null || _msType == null || _redraw == null) return;

            // WPF Application context — the FlowModel is a XAML UserControl; its InitializeComponent resolves
            // resources against Application.Current, which is null in a pure-WinForms host. Create one (do NOT
            // Run it) so WPF resource resolution works. Harmless alongside WinForms' own Application.
            try { if (System.Windows.Application.Current == null) new System.Windows.Application(); } catch { }

            string lbRoot = MediaResolver.LbRoot ?? "";

            // 1) NamingHelper.RootFolder
            TrySetStatic(_baseAsm.GetType("Unbroken.LaunchBox.NamingHelper"), "RootFolder", lbRoot);

            // 2) LocalDbContext.DbFilePath
            string db = Path.Combine(lbRoot, "Metadata", "LaunchBox.Metadata.db");
            if (File.Exists(db))
                TrySetStatic(_localDb?.GetType("Unbroken.LaunchBox.LocalDb.LocalDbContext"), "DbFilePath", db);

            // 3) GamesDb.GetPlatformScrapeValueFunc = identity — the load-bearing null-delegate fix.
            try
            {
                var gp = _localDb?.GetType("Unbroken.LaunchBox.LocalDb.GamesDb")?.GetProperty("GetPlatformScrapeValueFunc", BindingFlags.Public | BindingFlags.Static);
                if (gp?.SetMethod != null && gp.GetValue(null) == null)
                {
                    var ga = gp.PropertyType.GetGenericArguments();
                    if (ga.Length == 2 && ga[0] == typeof(string) && ga[1] == typeof(string))
                        gp.SetValue(null, (Func<string, string>)(s => s));
                }
            }
            catch { }

            // 4) Root.DataManager = bare shim (only set if still null — never clobber a real one).
            try
            {
                var rootDm = _win.GetType("Unbroken.LaunchBox.Windows.Root")?.GetProperty("DataManager", BindingFlags.Public | BindingFlags.Static);
                var dmCtor = _win.GetType("Unbroken.LaunchBox.Windows.Data.DataManager")?.GetConstructor(new[] { typeof(bool), typeof(bool) });
                if (rootDm?.SetMethod != null && dmCtor != null && rootDm.GetValue(null) == null)
                    rootDm.SetValue(null, dmCtor.Invoke(new object[] { true, false }));
            }
            catch { }

            _available = true;
        }
        catch (Exception ex) { Console.WriteLine("[model3d] init failed: " + ex.Message); _available = false; }
    }

    private static void TrySetStatic(Type? t, string prop, object? value)
    {
        try { var p = t?.GetProperty(prop, BindingFlags.Public | BindingFlags.Static); if (p?.SetMethod != null) p.SetValue(null, value); } catch { }
    }

    /// <summary>Build a ModelSettings core object from our field→string map (the same schema PlatformModelStore
    /// reads/writes). Returns null when unavailable.</summary>
    private static object? BuildSettings(Dictionary<string, string>? map)
    {
        if (_msType == null) return null;
        object? s;
        try { s = Activator.CreateInstance(_msType); } catch { return null; }
        if (s == null || map == null) return s;
        foreach (var kv in map)
        {
            var p = _msType.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance);
            if (p?.SetMethod == null) continue;
            try { p.SetValue(s, Convert(kv.Value, p.PropertyType)); } catch { }
        }
        return s;
    }

    private static object? Convert(string raw, Type target)
    {
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t == typeof(string)) return raw;
        if (string.IsNullOrEmpty(raw)) return null;
        if (t == typeof(bool)) return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        if (t == typeof(double)) return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        if (t == typeof(int)) return int.TryParse(raw, out var i) ? i : 0;
        if (t.FullName == "System.Windows.Media.Color")
        {
            if (int.TryParse(raw, out var argb))
            {
                byte a = (byte)((argb >> 24) & 0xFF), r = (byte)((argb >> 16) & 0xFF), g = (byte)((argb >> 8) & 0xFF), b = (byte)(argb & 0xFF);
                return System.Windows.Media.Color.FromArgb(a, r, g, b);
            }
            return null;
        }
        return raw;   // Vector3D (ModelSize) is skipped — ModelSizeString covers it
    }

    private static object? MakeGame(string title, string platform)
    {
        if (_gameType == null) return null;
        object? g;
        try { var c = _gameType.GetConstructor(new[] { typeof(bool) }); g = c != null ? c.Invoke(new object[] { true }) : Activator.CreateInstance(_gameType); }
        catch { return null; }
        if (g == null) return null;
        SetGameMember(g, "Title", string.IsNullOrEmpty(title) ? "SampleGame" : title);
        SetGameMember(g, "Platform", platform ?? "");
        return g;
    }

    private static void SetGameMember(object obj, string name, object? value)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null && p.CanWrite) { try { p.SetValue(obj, value); return; } catch { } }
            var f = t.GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? t.GetField("_" + char.ToLowerInvariant(name[0]) + name.Substring(1), BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { try { f.SetValue(obj, value); return; } catch { } }
        }
    }

    /// <summary>A live 3D preview: a WinForms control hosting LB's FlowModel; Redraw rebuilds from options.</summary>
    internal sealed class Preview : IDisposable
    {
        private readonly ElementHost _host;
        private readonly object _flow;   // FlowModel instance (WPF UserControl)

        public Control Control => _host;

        private Preview(ElementHost host, object flow) { _host = host; _flow = flow; }

        public static Preview? Create()
        {
            if (!Available || _flowType == null) return null;
            try
            {
                object? flow;
                var c = _flowType.GetConstructor(new[] { typeof(bool) });
                flow = c != null ? c.Invoke(new object[] { true }) : Activator.CreateInstance(_flowType);
                if (flow is not System.Windows.UIElement ui) return null;
                var host = new ElementHost { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(28, 28, 30), Child = ui };
                return new Preview(host, flow);
            }
            catch (Exception ex) { Console.WriteLine("[model3d] preview create: " + (ex.InnerException?.ToString() ?? ex.ToString())); return null; }
        }

        /// <summary>Rebuild the model from the current option map + the game to texture with. gameTitle empty →
        /// a bare sample case (geometry only).</summary>
        public void Redraw(Dictionary<string, string>? settingsMap, string gameTitle, string platform)
        {
            try
            {
                var settings = BuildSettings(settingsMap);
                var game = MakeGame(gameTitle, platform);
                if (settings == null || game == null) return;
                _redraw!.Invoke(_flow, new[] { game, settings });
            }
            catch (Exception ex) { Console.WriteLine("[model3d] redraw: " + (ex.InnerException?.Message ?? ex.Message)); }
        }

        public void Rotate(double left, double right, double up, double down)
        {
            try { _rotate?.Invoke(_flow, new object[] { left, right, up, down }); } catch { }
        }

        public void Dispose() { try { _host.Dispose(); } catch { } }
    }
}
