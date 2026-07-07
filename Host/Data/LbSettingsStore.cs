// LaunchBox global settings (LB\Data\Settings.xml — one flat <Settings> element).
// Loaded once into a name→value dict; Set() updates memory and appends a
// "Settings" modify op to the journal, flushed back to Settings.xml via the
// scoped flush (FlushLbSettingsJournalIfSafe) when the options window closes.
//
// CAUTION (verified empirically): LaunchBox deserializes Settings.xml into a
// typed DTO and re-serializes it at close — UNKNOWN elements are STRIPPED.
// Only write fields LB itself owns; LiteBox-only data belongs in LiteBox.ini
// or a LiteBox-owned file, never in Settings.xml.

#nullable enable

using System.IO;
using System.Xml.Linq;

namespace LbApiHost.Host.Data;

/// <summary>One LB startup application (a repeatable &lt;StartupAppSettings&gt; element,
/// SIBLING of &lt;Settings&gt; under &lt;LaunchBox&gt;). Field names verified against a real
/// LB write (incl. CommandLine, which LB preserves — so the name is canonical).
///
/// Forward-compatible like EmulatorPlatform: <see cref="Extra"/> carries every child
/// element we don't model, so a field a future LB adds round-trips untouched instead of
/// being dropped on rewrite.</summary>
internal sealed class LbStartupApp
{
    public string ApplicationPath = "";
    public string CommandLine = "";
    public bool StartWithLaunchBox = true;
    public bool StartWithBigBox = true;
    public bool AllowMultipleInstances;
    /// <summary>Child elements outside the modelled set, preserved verbatim on rewrite.</summary>
    public Dictionary<string, string> Extra = new(StringComparer.Ordinal);

    private static readonly HashSet<string> Modelled = new(StringComparer.Ordinal)
    { "ApplicationPath", "CommandLine", "StartWithLaunchBox", "StartWithBigBox", "AllowMultipleInstances" };

    public static LbStartupApp FromXml(System.Xml.Linq.XElement sa)
    {
        var app = new LbStartupApp
        {
            ApplicationPath = (string?)sa.Element("ApplicationPath") ?? "",
            CommandLine = (string?)sa.Element("CommandLine") ?? "",
            StartWithLaunchBox = ((string?)sa.Element("StartWithLaunchBox") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
            StartWithBigBox = ((string?)sa.Element("StartWithBigBox") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
            AllowMultipleInstances = ((string?)sa.Element("AllowMultipleInstances") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
        };
        foreach (var c in sa.Elements())
            if (!Modelled.Contains(c.Name.LocalName)) app.Extra[c.Name.LocalName] = c.Value;
        return app;
    }

    public LbStartupApp Clone() => new()
    {
        ApplicationPath = ApplicationPath, CommandLine = CommandLine,
        StartWithLaunchBox = StartWithLaunchBox, StartWithBigBox = StartWithBigBox,
        AllowMultipleInstances = AllowMultipleInstances,
        Extra = new Dictionary<string, string>(Extra, StringComparer.Ordinal),
    };

    /// <summary>Full field map for serialization — modelled fields plus the preserved Extra.</summary>
    public Dictionary<string, string> ToFieldMap()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AllowMultipleInstances"] = AllowMultipleInstances ? "true" : "false",
            ["ApplicationPath"] = ApplicationPath ?? "",
            ["CommandLine"] = CommandLine ?? "",
            ["StartWithBigBox"] = StartWithBigBox ? "true" : "false",
            ["StartWithLaunchBox"] = StartWithLaunchBox ? "true" : "false",
        };
        foreach (var kv in Extra) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
        return d;
    }
}

/// <summary>One LB image-type setting (a repeatable &lt;ImageTypeSettings&gt; element,
/// SIBLING of &lt;Settings&gt;). The "Automatic Imports Media" grid edits only
/// <see cref="UseInAutoImports"/>; <see cref="IsDefault"/> and any future field ride
/// along in <see cref="Extra"/>. NOTE: the type→group mapping and grid order are LB's
/// hardcoded catalog (see LbGlobalOptions.MediaCatalog), NOT stored here.</summary>
internal sealed class LbImageTypeSetting
{
    public string ImageType = "";
    public bool IsDefault;
    public bool UseInAutoImports;
    public Dictionary<string, string> Extra = new(StringComparer.Ordinal);

    private static readonly HashSet<string> Modelled = new(StringComparer.Ordinal)
    { "ImageType", "IsDefault", "UseInAutoImports" };

    public static LbImageTypeSetting FromXml(System.Xml.Linq.XElement e)
    {
        var s = new LbImageTypeSetting
        {
            ImageType = (string?)e.Element("ImageType") ?? "",
            IsDefault = ((string?)e.Element("IsDefault") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
            UseInAutoImports = ((string?)e.Element("UseInAutoImports") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
        };
        foreach (var c in e.Elements())
            if (!Modelled.Contains(c.Name.LocalName)) s.Extra[c.Name.LocalName] = c.Value;
        return s;
    }

    public LbImageTypeSetting Clone() => new()
    {
        ImageType = ImageType, IsDefault = IsDefault, UseInAutoImports = UseInAutoImports,
        Extra = new Dictionary<string, string>(Extra, StringComparer.Ordinal),
    };

    public Dictionary<string, string> ToFieldMap()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ImageType"] = ImageType ?? "",
            ["IsDefault"] = IsDefault ? "true" : "false",
            ["UseInAutoImports"] = UseInAutoImports ? "true" : "false",
        };
        foreach (var kv in Extra) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
        return d;
    }
}

internal sealed class LbSettingsStore
{
    private readonly Dictionary<string, string> _f = new(StringComparer.Ordinal);
    private readonly GameStore? _store;
    private readonly List<LbStartupApp> _startupApps = new();
    private readonly List<LbImageTypeSetting> _imageTypes = new();

    public bool Loaded { get; }

    public LbSettingsStore(string dataDir, GameStore? store)
    {
        _store = store;
        var file = Path.Combine(dataDir ?? "", "Settings.xml");
        try
        {
            if (File.Exists(file))
            {
                var lbRootEl = XDocument.Load(file).Root;
                var root = lbRootEl?.Element("Settings");
                if (root != null)
                {
                    foreach (var e in root.Elements()) _f[e.Name.LocalName] = e.Value;
                    Loaded = true;
                }
                if (lbRootEl != null)
                {
                    foreach (var sa in lbRootEl.Elements("StartupAppSettings"))
                        _startupApps.Add(LbStartupApp.FromXml(sa));
                    foreach (var it in lbRootEl.Elements("ImageTypeSettings"))
                        _imageTypes.Add(LbImageTypeSetting.FromXml(it));
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[lbsettings] load failed: " + ex.Message); }
        Console.WriteLine($"[lbsettings] {file} loaded={Loaded} fields={_f.Count} startupApps={_startupApps.Count} imageTypes={_imageTypes.Count}");
    }

    /// <summary>Snapshot of the image-type settings (deep copies, Extra included).</summary>
    public List<LbImageTypeSetting> ImageTypes => _imageTypes.Select(i => i.Clone()).ToList();

    /// <summary>Replaces the whole ImageTypeSettings collection. The grid only edits the
    /// catalog types' UseInAutoImports; entries NOT in the grid (and each entry's IsDefault
    /// + Extra) are preserved verbatim by passing the full updated list here.</summary>
    public void SetImageTypes(List<LbImageTypeSetting> items)
    {
        _imageTypes.Clear();
        _imageTypes.AddRange((items ?? new List<LbImageTypeSetting>()).Select(i => i.Clone()));
        var json = System.Text.Json.JsonSerializer.Serialize(_imageTypes.Select(i => i.ToFieldMap()).ToList());
        _store?.RecordEntityReplace("ImageTypeSettings", "Settings", json);
    }

    /// <summary>Snapshot of the startup applications list (deep copies, Extra included).</summary>
    public List<LbStartupApp> StartupApps => _startupApps.Select(a => a.Clone()).ToList();

    /// <summary>Replaces the whole startup-apps collection (rows have no stable id —
    /// same "replace" pattern as EmulatorPlatform) and logs one op. Each row's Extra
    /// (unmodelled fields) is serialized too, so future LB fields round-trip.</summary>
    public void SetStartupApps(List<LbStartupApp> apps)
    {
        _startupApps.Clear();
        _startupApps.AddRange((apps ?? new List<LbStartupApp>()).Select(a => a.Clone()));
        var json = System.Text.Json.JsonSerializer.Serialize(_startupApps.Select(a => a.ToFieldMap()).ToList());
        _store?.RecordEntityReplace("StartupAppSettings", "Settings", json);
    }

    // A key the current LaunchBox can't safely host in its XML (ProblemKeys, version-gated)
    // is read/written from LiteBox's own DB instead — shared-with-LB by default, DB only when
    // the running LB would drop/ignore the key. One choke point: every global setting flows here.
    public string Get(string field, string fallback = "")
    {
        if (ProblemKeys.IsDbManaged(field))
            return LiteBoxOptionsDb.GetGlobal(field) ?? fallback;
        return _f.TryGetValue(field, out var v) ? v : fallback;
    }

    public bool GetBool(string field, bool fallback = false)
    {
        var v = Get(field, fallback ? "true" : "false");
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void Set(string field, string value)
    {
        if (string.IsNullOrEmpty(field)) return;
        if (ProblemKeys.IsDbManaged(field)) { LiteBoxOptionsDb.SetGlobal(field, value ?? ""); return; }
        _f[field] = value ?? "";
        _store?.RecordEntityModify("Settings", "Settings", field, value ?? "");
    }

    public void SetBool(string field, bool value) => Set(field, value ? "true" : "false");
}
