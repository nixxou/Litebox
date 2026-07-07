// Which Settings.xml keys can't safely live in the CURRENT LaunchBox's XML — routed to
// LiteBox's own DB (LiteBoxOptionsDb) instead. Built once at boot from LbVersion.
//
// The default is ALWAYS "share with LB" — a key stays in Settings.xml so a real LaunchBox
// booted on the same library sees it and its UI edits it. A key becomes "DB-managed" ONLY
// when the running LB version can't host it safely: a 13.28-line key name would be stripped
// (or ignored) by a 13.27 LB on its next rewrite. So the routing is version-conditioned, not
// a blanket duplication — no double storage, no slow loads, shared-with-LB preserved wherever
// the running LB understands the key.
//
// This is the ONE place the "problematic key" list is defined. LbSettingsStore consults
// IsDbManaged(key) on every Get/Set and routes accordingly. Adding a future version-sensitive
// key = one entry here. Keys LiteBox invented outright (LB has NO field, any version) are a
// different bucket — those live in the DB unconditionally and never touch this list.

#nullable enable

using System.Collections.Generic;

namespace LbApiHost.Host.Data;

internal static class ProblemKeys
{
    // Settings.xml keys introduced on the LB 13.28 line (renamed or brand-new). Present and
    // understood by 13.28+, unknown to 13.27 — which would drop them on rewrite. When we run
    // against a pre-13.28 LB these are DB-managed; against 13.28+ they stay in shared XML.
    private static readonly string[] _new1328Keys =
    {
        "StartupScreenPostLaunchDisplayTime",   // was MinimumStartupScreenDisplayTime
        "ShutdownScreenPostReadyDisplayTime",   // was MinimumShutdownScreenDisplayTime
        "ForceFrontendFocusOnShutdown",         // new feature (no 13.27 equivalent)
        "MonitorStartupShutdownWithProcess",    // new feature (auto-close startup on window show)
    };

    // Renamed keys: 13.28 new name ← 13.27 legacy name. When a new-name key is DB-managed
    // (running against 13.27) and the DB has no value yet, seed it from the legacy XML value
    // so an existing 13.27 config carries over instead of resetting to the default.
    private static readonly (string newKey, string legacyKey)[] _renamed =
    {
        ("StartupScreenPostLaunchDisplayTime", "MinimumStartupScreenDisplayTime"),
        ("ShutdownScreenPostReadyDisplayTime", "MinimumShutdownScreenDisplayTime"),
    };

    private static HashSet<string>? _dbManaged;

    /// <summary>Build the DB-managed set from the detected LB version. Idempotent.</summary>
    public static void Build()
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        // 13.28-line keys are only XML-safe on a 13.28+ LB; otherwise route them to the DB.
        if (!LbVersion.Is1328OrLater)
            foreach (var k in _new1328Keys) set.Add(k);
        _dbManaged = set;
        System.Console.WriteLine($"[problemkeys] db-managed keys ({set.Count}): {string.Join(", ", set)}");
    }

    /// <summary>True when this Settings.xml key must be read/written from LiteBox's DB rather
    /// than the LaunchBox XML for the currently-detected LB version.</summary>
    public static bool IsDbManaged(string key)
    {
        if (_dbManaged == null) Build();
        return !string.IsNullOrEmpty(key) && _dbManaged!.Contains(key);
    }

    /// <summary>One-shot: for each renamed key that is now DB-managed but has no DB value yet,
    /// copy the pre-rename value out of Settings.xml so an existing config carries over. No-op
    /// on 13.28+ (the new keys aren't DB-managed there) and after the first seed. Never throws.</summary>
    public static void SeedRenamedFromXml(string settingsXmlPath)
    {
        try
        {
            System.Collections.Generic.Dictionary<string, string>? xml = null;
            foreach (var (newKey, legacyKey) in _renamed)
            {
                if (!IsDbManaged(newKey)) continue;
                if (!string.IsNullOrEmpty(LiteBoxOptionsDb.GetGlobal(newKey))) continue;   // already set
                xml ??= ReadXml(settingsXmlPath);
                if (xml.TryGetValue(legacyKey, out var v) && !string.IsNullOrEmpty(v))
                {
                    LiteBoxOptionsDb.SetGlobal(newKey, v);
                    System.Console.WriteLine($"[problemkeys] seeded {newKey} = {v} (from {legacyKey})");
                }
            }
        }
        catch (System.Exception ex) { System.Console.WriteLine("[problemkeys] seed failed: " + ex.Message); }
    }

    private static System.Collections.Generic.Dictionary<string, string> ReadXml(string path)
    {
        var d = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        try
        {
            if (!System.IO.File.Exists(path)) return d;
            var root = System.Xml.Linq.XDocument.Load(path).Root?.Element("Settings");
            if (root != null) foreach (var e in root.Elements()) d[e.Name.LocalName] = e.Value;
        }
        catch { }
        return d;
    }
}
