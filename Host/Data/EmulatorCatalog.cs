// Loads LB\Data\Emulators.xml: <Emulator> definitions + <EmulatorPlatform> mappings. Backs
// IEmulator/IEmulatorPlatform so GetAllEmulators/GetEmulatorById work and real launch can build a
// command line. Dict-backed (xml element name -> value) so every field round-trips; setters route
// through GameStore's op-log for write-back (modify), and EmulatorPlatform rows — which have no
// stable ID — are persisted as a per-emulator "replace" collection.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;

namespace LbApiHost.Host.Data;

internal sealed class HostEmulatorPlatform : DummyEmulatorPlatform
{
    private readonly Dictionary<string, string> _f;   // xml element name -> value
    private HostEmulator _owner;
    public HostEmulatorPlatform(Dictionary<string, string> f) { _f = f; }
    internal void Attach(HostEmulator owner) { _owner = owner; }
    internal Dictionary<string, string> Fields => _f;

    private string G(string k) => _f.TryGetValue(k, out var v) ? v ?? "" : "";
    private bool GB(string k) => string.Equals(G(k), "true", StringComparison.OrdinalIgnoreCase);
    private void S(string k, string v) { _f[k] = v ?? ""; _owner?.RecordPlatforms(); }

    public override string EmulatorId { get => G("Emulator"); set => S("Emulator", value); }
    public override string Platform { get => G("Platform"); set => S("Platform", value); }
    public override string CommandLine { get => G("CommandLine"); set => S("CommandLine", value); }
    public override bool IsDefault { get => GB("Default"); set => S("Default", value ? "true" : "false"); }
    public override bool M3uDiscLoadEnabled { get => GB("M3uDiscLoadEnabled"); set => S("M3uDiscLoadEnabled", value ? "true" : "false"); }
    public override Nullable<bool> AutoExtract
    {
        get { var v = G("AutoExtract"); return string.IsNullOrEmpty(v) ? (bool?)null : v.Equals("true", StringComparison.OrdinalIgnoreCase); }
        set => S("AutoExtract", value.HasValue ? (value.Value ? "true" : "false") : "");
    }
}

internal sealed class HostEmulator : DummyEmulator
{
    private readonly string _id;
    private readonly Dictionary<string, string> _f;
    private readonly List<HostEmulatorPlatform> _platforms;
    private GameStore _store;
    public HostEmulator(string id, Dictionary<string, string> f, List<HostEmulatorPlatform> platforms)
    { _id = id; _f = f; _platforms = platforms; foreach (var p in _platforms) p.Attach(this); }
    internal void Attach(GameStore s) { _store = s; }

    private string G(string k) => _f.TryGetValue(k, out var v) ? v ?? "" : "";
    private bool GB(string k) => string.Equals(G(k), "true", StringComparison.OrdinalIgnoreCase);
    private int GI(string k) => int.TryParse(G(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private void S(string k, string v) { _f[k] = v ?? ""; _store?.RecordEntityModify("Emulator", _id, k, v ?? ""); }
    private void SB(string k, bool v) => S(k, v ? "true" : "false");

    /// <summary>Re-serialise this emulator's whole platform collection and log a replace op.</summary>
    internal void RecordPlatforms()
        => _store?.RecordEntityReplace("EmulatorPlatform", _id, JsonSerializer.Serialize(_platforms.Select(p => p.Fields).ToList()));

    public override string Id { get => _id; set { } }
    public override string ApplicationPath { get => G("ApplicationPath"); set => S("ApplicationPath", value); }
    public override string CommandLine { get => G("CommandLine"); set => S("CommandLine", value); }
    public override string DefaultPlatform { get => G("DefaultPlatform"); set => S("DefaultPlatform", value); }
    public override string Title { get => G("Title"); set => S("Title", value); }
    public override bool NoQuotes { get => GB("NoQuotes"); set => SB("NoQuotes", value); }
    public override bool NoSpace { get => GB("NoSpace"); set => SB("NoSpace", value); }
    public override bool HideConsole { get => GB("HideConsole"); set => SB("HideConsole", value); }
    public override bool FileNameWithoutExtensionAndPath { get => GB("FileNameWithoutExtensionAndPath"); set => SB("FileNameWithoutExtensionAndPath", value); }
    public override string AutoHotkeyScript { get => G("AutoHotkeyScript"); set => S("AutoHotkeyScript", value); }
    public override string PauseAutoHotkeyScript { get => G("PauseAutoHotkeyScript"); set => S("PauseAutoHotkeyScript", value); }
    public override string ResumeAutoHotkeyScript { get => G("ResumeAutoHotkeyScript"); set => S("ResumeAutoHotkeyScript", value); }
    public override string ResetAutoHotkeyScript { get => G("ResetAutoHotkeyScript"); set => S("ResetAutoHotkeyScript", value); }
    public override string SaveStateAutoHotkeyScript { get => G("SaveStateAutoHotkeyScript"); set => S("SaveStateAutoHotkeyScript", value); }
    public override string LoadStateAutoHotkeyScript { get => G("LoadStateAutoHotkeyScript"); set => S("LoadStateAutoHotkeyScript", value); }
    public override string SwapDiscsAutoHotkeyScript { get => G("SwapDiscsAutoHotkeyScript"); set => S("SwapDiscsAutoHotkeyScript", value); }
    public override string ExitAutoHotkeyScript { get => G("ExitAutoHotkeyScript"); set => S("ExitAutoHotkeyScript", value); }
    public override bool AutoExtract { get => GB("AutoExtract"); set => SB("AutoExtract", value); }
    public override bool UseStartupScreen { get => GB("UseStartupScreen"); set => SB("UseStartupScreen", value); }
    public override bool HideAllNonExclusiveFullscreenWindows { get => GB("HideAllNonExclusiveFullscreenWindows"); set => SB("HideAllNonExclusiveFullscreenWindows", value); }
    public override int StartupLoadDelay { get => GI("StartupLoadDelay"); set => S("StartupLoadDelay", value.ToString(CultureInfo.InvariantCulture)); }
    public override bool HideMouseCursorInGame { get => GB("HideMouseCursorInGame"); set => SB("HideMouseCursorInGame", value); }
    public override bool DisableShutdownScreen { get => GB("DisableShutdownScreen"); set => SB("DisableShutdownScreen", value); }
    public override bool AggressiveWindowHiding { get => GB("AggressiveWindowHiding"); set => SB("AggressiveWindowHiding", value); }
    public override bool EnableHardcoreAchievements { get => GB("EnableHardcoreAchievements"); set => SB("EnableHardcoreAchievements", value); }

    public override IEmulatorPlatform[] GetAllEmulatorPlatforms() => _platforms.ToArray();
    public override IEmulatorPlatform AddNewEmulatorPlatform()
    {
        var ep = new HostEmulatorPlatform(new Dictionary<string, string>(StringComparer.Ordinal) { ["Emulator"] = _id });
        ep.Attach(this);
        _platforms.Add(ep);
        RecordPlatforms();
        return ep;
    }
}

internal static class EmulatorCatalog
{
    public static List<HostEmulator> Load(string dataDir)
    {
        var result = new List<HostEmulator>();
        string file = Path.Combine(dataDir, "Emulators.xml");
        if (!File.Exists(file)) return result;
        XDocument doc;
        try { doc = XDocument.Load(file); } catch { return result; }
        var root = doc.Root;
        if (root == null) return result;

        var platByEmu = new Dictionary<string, List<HostEmulatorPlatform>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in root.Elements("EmulatorPlatform"))
        {
            var f = ReadFields(ep);
            string emuId = f.TryGetValue("Emulator", out var e) ? e : null;
            if (string.IsNullOrWhiteSpace(emuId)) continue;
            if (!platByEmu.TryGetValue(emuId, out var l)) platByEmu[emuId] = l = new List<HostEmulatorPlatform>();
            l.Add(new HostEmulatorPlatform(f));
        }

        foreach (var ee in root.Elements("Emulator"))
        {
            var f = ReadFields(ee);
            string id = f.TryGetValue("ID", out var i) ? i : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var plats = platByEmu.TryGetValue(id, out var pl) ? pl : new List<HostEmulatorPlatform>();
            result.Add(new HostEmulator(id, f, plats));
        }

        Console.WriteLine($"[emucat] file={file} exists={File.Exists(file)} emulators={result.Count} emPlatformGroups={platByEmu.Count}");
        return result;
    }

    private static Dictionary<string, string> ReadFields(XElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in e.Elements()) d[c.Name.LocalName] = c.Value;
        return d;
    }
}
