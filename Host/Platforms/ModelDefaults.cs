// LaunchBox's HARDCODED per-platform 3D defaults, resolved at runtime by invoking the core's
// ModelSettings.GetDefaultSettings(platformName, scrapeAs) via reflection (clean public static — no
// obfuscated-warm-up issues under the light multi-file host; PROVEN by the --model-defaults probe).
// This is the third level of LB's override chain: game block → platform block → THESE defaults.
// The scrapeAs parameter is what makes a custom-named platform inherit a preset ("FooBar" with
// Scrape As "Sony Playstation" → the PS1 jewel preset) — LB does the matching internally.
// Returns the result converted into the SAME field→string map the XML blocks use (PlatformModelStore
// format), or null when LB has no default for that platform (cartridge/arcade platforms).

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace LbApiHost.Host.Platforms;

internal static class ModelDefaults
{
    private static MethodInfo? _method;
    private static PropertyInfo[]? _props;
    private static bool _initTried;
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // XML field names we surface (ModelSize is skipped — ModelSizeString carries it).
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModelType", "ModelSizeString", "CaseColor", "CoverColor", "FrontSpineImage", "FrontSpineIsClear",
        "FullImageSpineWidth", "DoubleSpineImageMode", "LogoFont", "SpineRotation", "LogoRotation",
        "UseFullScanImages", "FullScanIsLandscape",
    };

    public static Dictionary<string, string>? TryGet(string? platformName, string? scrapeAs)
    {
        string name = (platformName ?? "").Trim();
        string scrape = (scrapeAs ?? "").Trim();
        if (name.Length == 0 && scrape.Length == 0) return null;
        string key = name + "|" + scrape;
        return _cache.GetOrAdd(key, _ => Resolve(name, scrape));
    }

    // LiteBox's CORRECTIONS to LB's hardcoded defaults, applied on top of whichever source answered.
    //
    // LaunchBox ships Genesis and Master System as ModelType "dvd" — a keep case: a paper wrap over a
    // plastic shell, and that shell is #1D1D1D (the dvd default for any platform carrying no CaseColor,
    // and neither of these two carries one). Those platforms never shipped in a keep case; they shipped in
    // a printed CARDBOARD box, which is exactly what "box" models — every face either textured or tinted
    // with the artwork's corner average. The shell is what shows as strictly black edges beside, say, an
    // SNES box, whose platform has no LB default at all and so falls through to CtorDefaults' "box".
    // The size those two carry reads the same way: "5;7.165;1" is 5 x 7.165 x 1 INCHES of cardboard.
    //
    // Corrections live HERE rather than in model-defaults.json because that file is regenerated verbatim
    // from the core by --model-defaults-extract; an edit to it would be silently lost on the next LB
    // version. Changing ModelType also changes the resolved map, hence the cache key, so exactly these
    // platforms re-bake — no BakerVersion bump, and no library-wide re-bake for a two-platform fix.
    private static readonly Dictionary<string, Dictionary<string, string>> Corrections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sega Genesis"]       = new(StringComparer.OrdinalIgnoreCase) { ["ModelType"] = "box" },
        ["Sega Master System"] = new(StringComparer.OrdinalIgnoreCase) { ["ModelType"] = "box" },
    };

    // LiteBox's ADDITIONS: whole blocks for platforms LB simply has NO entry for — a missing entry falls
    // through to the ctor default, a plain "box", which is a different failure than a wrong field.
    //
    // Sony Playstation 5: LB's 3D module predates the console and was never revised for it, so it renders
    // PS5 games as a cardboard box. The PS5 retail case is the same keep-case shape as the PS4's, in
    // translucent blue — PS4's block verbatim (dvd, CaseColor #1E51CE blue, full-scan enabled) is the
    // closest thing LB itself would have shipped, and what a user override would have hand-copied anyway.
    //
    // Nintendo Switch 2: same story, one generation later. The retail case is the Switch's keep case with
    // the plastic gone RED — the Switch block with CaseColor #D22A28 instead of its white #F5F5F5.
    private static readonly Dictionary<string, Dictionary<string, string>> Additions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sony Playstation 5"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CaseColor"] = "-14790194",                    // PS4's #FF1E51CE — the keep-case blue
            ["DoubleSpineImageMode"] = "AutomaticDetection",
            ["FrontSpineIsClear"] = "false",
            ["FullImageSpineWidth"] = "0.065",
            ["FullScanIsLandscape"] = "false",
            ["LogoRotation"] = "0,0,0,",
            ["ModelType"] = "dvd",
            ["SpineRotation"] = "0,,0,",
            ["UseFullScanImages"] = "true",
        },
        ["Nintendo Switch 2"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CaseColor"] = "-3003864",                     // #FFD22A28 — the Switch 2 keep-case red
            ["DoubleSpineImageMode"] = "AutomaticDetection",
            ["FrontSpineIsClear"] = "false",
            ["FullImageSpineWidth"] = "0.065",
            ["FullScanIsLandscape"] = "false",
            ["LogoRotation"] = "0,0,0,",
            ["ModelType"] = "dvd",
            ["SpineRotation"] = "0,,0,",
            ["UseFullScanImages"] = "true",
        },
    };

    /// <summary>An addition for <paramref name="scrape"/>/<paramref name="name"/> (scrapeAs first, same
    /// precedence as the table itself), or null. Returns a copy — callers mutate their maps.</summary>
    private static Dictionary<string, string>? AdditionFor(string name, string scrape)
    {
        if (scrape.Length > 0 && Additions.TryGetValue(scrape, out var byScrape)) return new(byScrape, StringComparer.OrdinalIgnoreCase);
        if (name.Length > 0 && Additions.TryGetValue(name, out var byName)) return new(byName, StringComparer.OrdinalIgnoreCase);
        return null;
    }

    private static Dictionary<string, string>? Resolve(string name, string scrape)
    {
        // FROZEN TABLE first (embedded model-defaults.json, extracted from the core once per LB version via
        // --model-defaults-extract over every Metadata-db platform name) — the core-independent path. The
        // scrapeAs drives the preset match, the platform's own name is the fallback key (same semantics as
        // GetDefaultSettings). Live reflection only when the table has no entry AND the core is present.
        var table = FrozenTable();
        if (table != null)
        {
            if (scrape.Length > 0 && table.TryGetValue(scrape, out var byScrape)) return Corrected(scrape, byScrape);
            if (name.Length > 0 && table.TryGetValue(name, out var byName)) return Corrected(name, byName);
            // LB has no entry (authoritative when the table loaded) — LiteBox's own additions answer last.
            return AdditionFor(name, scrape);
        }
        var core = ResolveViaCore(name, scrape);
        return core == null ? AdditionFor(name, scrape) : Corrected(scrape.Length > 0 ? scrape : name, core);
    }

    /// <summary>A copy of <paramref name="src"/> with the corrections for <paramref name="key"/> applied.
    /// Keyed on whichever of scrapeAs/name actually matched, so a custom-named platform inherits the
    /// correction through its Scrape As exactly the way it inherits the preset itself.</summary>
    private static Dictionary<string, string> Corrected(string key, Dictionary<string, string> src)
    {
        var m = new Dictionary<string, string>(src, StringComparer.OrdinalIgnoreCase);
        if (Corrections.TryGetValue(key, out var fix))
            foreach (var kv in fix) m[kv.Key] = kv.Value;
        return m;
    }

    private static Dictionary<string, Dictionary<string, string>>? _frozen;
    private static bool _frozenTried;
    private static Dictionary<string, Dictionary<string, string>>? FrozenTable()
    {
        if (_frozenTried) return _frozen;
        _frozenTried = true;
        try
        {
            using var s = typeof(ModelDefaults).Assembly.GetManifestResourceStream("LbApiHost.model-defaults.json");
            if (s == null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            var t = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var plat in doc.RootElement.EnumerateObject())
            {
                var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in plat.Value.EnumerateObject()) m[f.Name] = f.Value.GetString() ?? "";
                if (m.Count > 0) t[plat.Name] = m;
            }
            _frozen = t.Count > 0 ? t : null;
            Console.WriteLine($"[modeldefaults] frozen table loaded ({t.Count} platforms)");
        }
        catch (Exception ex) { Console.WriteLine("[modeldefaults] frozen table: " + ex.Message); _frozen = null; }
        return _frozen;
    }

    /// <summary>Live resolution through the core (reflection) — used by the extractor probe and as the
    /// fallback when no frozen table is embedded.</summary>
    internal static Dictionary<string, string>? ResolveViaCore(string name, string scrape)
    {
        try
        {
            Init();
            if (_method == null) return null;
            var res = _method.Invoke(null, new object?[] { name, scrape.Length > 0 ? scrape : name });
            if (res == null) return null;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _props!)
            {
                if (!Fields.Contains(p.Name)) continue;
                object? v;
                try { v = p.GetValue(res); } catch { continue; }
                var sv = Str(v);
                if (sv != null) map[p.Name] = sv;
            }
            return map.Count > 0 ? map : null;
        }
        catch { return null; }
    }

    private static void Init()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            Assembly? win = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(a.GetName().Name, "Unbroken.LaunchBox.Windows", StringComparison.OrdinalIgnoreCase)) { win = a; break; }
            win ??= Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Unbroken.LaunchBox.Windows.dll"));
            var t = win.GetType("Unbroken.LaunchBox.Windows.Data.ModelSettings");
            _method = t?.GetMethod("GetDefaultSettings", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            _props = t?.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        }
        catch { _method = null; }
    }

    // Convert a ModelSettings property value into the XML string form (bool → true/false, double →
    // invariant, WPF/GDI Color → SIGNED ARGB int32, enum → name). Null → skip the field.
    private static string? Str(object? v)
    {
        switch (v)
        {
            case null: return null;
            case bool b: return b ? "true" : "false";
            case string s: return s;
            case double d: return d.ToString(CultureInfo.InvariantCulture);
            case float f: return f.ToString(CultureInfo.InvariantCulture);
            case int i: return i.ToString(CultureInfo.InvariantCulture);
            case Enum e: return e.ToString();
        }
        // A color struct (WPF System.Windows.Media.Color or GDI Color) — read A/R/G/B via reflection.
        var t = v.GetType();
        var pa = t.GetProperty("A"); var pr = t.GetProperty("R"); var pg = t.GetProperty("G"); var pb = t.GetProperty("B");
        if (pa != null && pr != null && pg != null && pb != null)
        {
            try
            {
                uint a = Convert.ToUInt32(pa.GetValue(v)), r = Convert.ToUInt32(pr.GetValue(v)),
                     g = Convert.ToUInt32(pg.GetValue(v)), b = Convert.ToUInt32(pb.GetValue(v));
                return unchecked((int)((a << 24) | (r << 16) | (g << 8) | b)).ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }
        return v.ToString();
    }
}
