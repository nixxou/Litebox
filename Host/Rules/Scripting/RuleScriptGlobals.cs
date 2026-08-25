// What a C# script rule SEES — the globals object whose public members become top-level identifiers
// in the script (Roslyn's globalsType contract, hence everything here is public). The design brief
// (Mehdi): give it access to EVERYTHING — the current line (mutable: assign Exe/Args and the change
// IS the transform), the original pre-rules line, the live IGame / IEmulator / version objects, the
// rule's variables, and a Swiss-army `Lb` API: HID queries through the same seven libraries the
// detector uses but returned as CLEAN OBJECTS (no "<>" strings) and queried ON DEMAND only — a
// script that never asks never pays a scan — plus monitor-profile switching, logging, paths.
// JSON / XML / regex need no helpers of ours: System.Text.Json, System.Xml.Linq and
// System.Text.RegularExpressions are imported by default (see RuleScriptEngine).
//
// The HID records parse OUR OWN backend lines (we control both sides); parsing stays here so the
// "<>" wire format remains the matchers' contract and scripts never see it.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Monitors;
using LbApiHost.Host.Rules.Hid;

namespace LbApiHost.Host.Rules.Scripting;

// ── the clean HID shapes ──────────────────────────────────────────────────────
public sealed record HidDeviceInfo(string Name, int VendorId, int ProductId, string Path);
public sealed record Ds4DeviceInfo(int VendorId, int ProductId, string Path, bool Usb);
public sealed record BtDeviceInfo(string Name, string ClassOfDevice, string Address);
public sealed record XInputSlotInfo(int Slot, string SubType, string Signature,
    int VendorId, int ProductId, int RevisionId,
    string Ds4Mac, string Ds4Type, string Ds4Connection, int Ds4InputSlot);
public sealed record DInputDeviceInfo(int Index, string ProductName, string Type,
    Guid InstanceGuid, string InstanceName, string InterfacePath);
public sealed record SdlDeviceInfo(int Index, string Name, string CapsSignature,
    string Serial, string Guid, int VendorId, int ProductId);

/// <summary>One media file of the launch's game: family = the image type ("Box - Front", …) or the
/// video kind; sub-family = the region folder for images, empty when the file sits at the root.</summary>
public sealed record MediaFileInfo(string Path, string Family, string SubFamily);

/// <summary>The script's world. Mutable Exe/Args = the transform channel; everything else is
/// context. Null Game/Emulator happens on the page preview — guard with <see cref="Preview"/>.</summary>
public sealed class RuleScriptGlobals
{
    /// <summary>The executable as the line stands at this rule's position. Assignable.</summary>
    public string Exe { get; set; } = "";
    /// <summary>The arguments as the line stands at this rule's position. Assignable.</summary>
    public string Args { get; set; } = "";
    /// <summary>The current Args, split like the launcher splits them. A fresh snapshot per read.</summary>
    public string[] ArgList => RuleArgs.Split(Args);

    public string OriginalExe { get; init; } = "";
    public string OriginalArgs { get; init; } = "";
    public string[] OriginalArgList => RuleArgs.Split(OriginalArgs);

    // dynamic, not the SDK interfaces: scripts write Game.Title naturally, LiteBox's own type
    // never hard-references the plugin SDK (the selftest harness runs without it), and a cast to
    // IGame stays available to scripts that want static typing.
    public dynamic? Game { get; init; }
    public dynamic? Emulator { get; init; }
    public dynamic? Version { get; init; }

    /// <summary>True on the EXAMPLE channel (the page preview and sandbox test runs) — scripts with
    /// side effects guard on it.</summary>
    public bool Preview { get; init; }

    /// <summary>The Swiss-army API. Set right after construction (it needs the globals back-ref).</summary>
    public LbScriptApi Lb { get; set; } = null!;
}

/// <summary>`Lb.…` in scripts. One instance per run, bound to the globals and the rule.</summary>
public sealed class LbScriptApi
{
    private readonly RuleScriptGlobals _g;
    private readonly LaunchRule _rule;
    internal LbScriptApi(RuleScriptGlobals g, LaunchRule rule) { _g = g; _rule = rule; }

    // ── basics ──
    /// <summary>The LaunchBox install root.</summary>
    public string LbRoot => System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, ".."));
    /// <summary>Writes into LiteBox's log, tag "script".</summary>
    public void Log(string message) => LbLog.Info("script", message);

    // ── the rule's variables ──
    /// <summary>One variable of THIS rule, resolved against the current Exe/Args. "" when unknown.</summary>
    public string Var(string name)
    {
        var v = RuleVariables.Parse(_rule.VariablesData)
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(x.Name, "{" + name + "}", StringComparison.OrdinalIgnoreCase));
        return v == null ? "" : RuleVariables.ResolveOne(v, _g.Exe, _g.Args);
    }
    /// <summary>Expands every {TOKEN} of this rule's variables in <paramref name="text"/>.</summary>
    public string ExpandVars(string text)
        => RuleVariables.Expand(text, RuleVariables.Parse(_rule.VariablesData), _g.Exe, _g.Args);

    // ── HID, on demand — same libraries as the detector, clean objects out ──
    // Each call goes through the per-launch cache (first call scans, the rest are free); nothing is
    // queried unless the script asks. Bluetooth is the slow one (a live inquiry): last on purpose.

    public List<HidDeviceInfo> HidDevices()
        => Parse(HidInfoCache.HidSharpInfo(), 4, f => new HidDeviceInfo(f[0], Int(f[1]), Int(f[2]), f[3]));

    public List<Ds4DeviceInfo> Ds4Devices()
        => Parse(HidInfoCache.Ds4Info(), 5, f => new Ds4DeviceInfo(Int(f[1]), Int(f[2]), f[3], f[4] == "USB:YES"));

    public List<BtDeviceInfo> BluetoothDevices()
        => Parse(HidInfoCache.BtInfo(), 3, f => new BtDeviceInfo(f[0], f[1], f[2]));

    public List<DInputDeviceInfo> DInputDevices()
        => Parse(HidInfoCache.DInputInfo(), 6, f => new DInputDeviceInfo(
            Int(f[0].Replace("DINPUT", "")), f[1], f[2],
            System.Guid.TryParse(f[3], out var g) ? g : System.Guid.Empty, f[4], f[5]));

    public List<XInputSlotInfo> XInputSlots(string ds4WinLogPath = "")
        => Parse(HidInfoCache.XInputInfo(ds4WinLogPath), 6, f => new XInputSlotInfo(
            Int(f[0].Replace("XINPUT", "")), f[1], f[2], Hex(f[3]), Hex(f[4]), Hex(f[5]),
            f.Length > 6 ? f[6] : "", f.Length > 7 ? f[7] : "", f.Length > 8 ? f[8] : "",
            f.Length > 9 ? Int(f[9]) : 0));

    public List<SdlDeviceInfo> SdlDevices(bool rawInputOff = false)
        => rawInputOff
            ? Parse(HidInfoCache.SdlNoRIInfo(), 6, f => new SdlDeviceInfo(
                Int(f[0].Replace("SDLNORI", "")), f[1], f[2], f[4], f[5], 0, 0))
            : Parse(HidInfoCache.SdlInfo(), 8, f => new SdlDeviceInfo(
                Int(f[0].Replace("SDL", "")), f[1], f[2], f[4], f[5], Hex(f[6]), Hex(f[7])));

    /// <summary>Fresh scan on the next HID query (the per-launch cache is dropped).</summary>
    public void RescanDevices() => HidInfoCache.Clear();

    // ── the game's media — families = image types / video kinds, sub-families = regions ──
    // Everything binds to the LAUNCH's game (empty results in the page preview or when the game is
    // unknown). "Selected" = the file the UI itself retains (LaunchBox-native region/rank rules);
    // the list forms return every file in native priority order.

    /// <summary>Every image family name ("Box - Front", "Screenshot - Gameplay", …).</summary>
    public string[] ImageFamilies()
        => Media.MediaResolver.ImageTypeNames().ToArray();

    /// <summary>The RETAINED image of a family — what LiteBox itself displays. Empty family = the
    /// front-box chain (Box - Front → Reconstructed → Fanart). "" when the game has none.</summary>
    public string SelectedImage(string family = "")
    {
        var k = GameKey(); if (k == null) return "";
        string[] chain = family.Length == 0 ? Media.MediaResolver.FrontChain() : new[] { family };
        return Media.MediaResolver.Image(k.Value.Platform, k.Value.Id, k.Value.Title, chain) ?? "";
    }

    /// <summary>Every image of one family, native priority order. A region ("Europe", …) narrows to
    /// that sub-family; empty = all regions.</summary>
    public List<string> Images(string family, string region = "")
    {
        var k = GameKey(); if (k == null) return new List<string>();
        if (region.Length == 0)
            return Media.MediaResolver.AllOfType(k.Value.Platform, k.Value.Id, k.Value.Title, family);
        return Media.MediaResolver.AllImageFiles(k.Value.Platform, k.Value.Id, k.Value.Title)
            .Where(f => string.Equals(f.type, family, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(f.region, region, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.path).ToList();
    }

    /// <summary>The whole image inventory of the game as (Path, Family, SubFamily=region).</summary>
    public List<MediaFileInfo> AllImages()
    {
        var k = GameKey(); if (k == null) return new List<MediaFileInfo>();
        return Media.MediaResolver.AllImageFiles(k.Value.Platform, k.Value.Id, k.Value.Title)
            .Select(f => new MediaFileInfo(f.path, f.type, f.region ?? "")).ToList();
    }

    /// <summary>The retained video (theme-first when asked). "" when none.</summary>
    public string SelectedVideo(bool preferTheme = false)
    {
        var k = GameKey(); if (k == null) return "";
        return Media.MediaResolver.Video(k.Value.Platform, k.Value.Id, k.Value.Title, preferTheme) ?? "";
    }

    /// <summary>Every video, optionally narrowed to one kind ("Video Snap", "Trailer", "Theme Video",
    /// "Recording", "Marquee").</summary>
    public List<string> Videos(string kind = "")
    {
        var k = GameKey(); if (k == null) return new List<string>();
        return Media.MediaResolver.AllVideoFiles(k.Value.Platform, k.Value.Id, k.Value.Title)
            .Where(v => kind.Length == 0 || string.Equals(v.type, kind, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.path).ToList();
    }

    /// <summary>The retained manual ("" when none) / every manual.</summary>
    public string SelectedManual()
    {
        var k = GameKey(); if (k == null) return "";
        return Media.MediaResolver.Manual(k.Value.Platform, k.Value.Id, k.Value.Title) ?? "";
    }
    public List<string> Manuals()
    {
        var k = GameKey(); if (k == null) return new List<string>();
        return Media.MediaResolver.ManualsAll(k.Value.Platform, k.Value.Id, k.Value.Title);
    }

    /// <summary>The retained music track ("" when none) / every track.</summary>
    public string SelectedMusic()
    {
        var k = GameKey(); if (k == null) return "";
        return Media.MediaResolver.Music(k.Value.Platform, k.Value.Id, k.Value.Title) ?? "";
    }
    public List<string> Musics()
    {
        var k = GameKey(); if (k == null) return new List<string>();
        return Media.MediaResolver.MusicsAll(k.Value.Platform, k.Value.Id, k.Value.Title);
    }

    /// <summary>The launch's game identity through the dynamic Game — null in the preview or when
    /// any member is missing (a script-provided fake, …). Every media method degrades to empty.</summary>
    private (string Platform, Guid Id, string Title)? GameKey()
    {
        try
        {
            if (_g.Game == null) return null;
            string platform = (string)_g.Game.Platform;
            string id = (string)_g.Game.Id;
            string title = (string)_g.Game.Title;
            if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(title)) return null;
            return (platform, Guid.TryParse(id, out var g) ? g : Guid.Empty, title);
        }
        catch { return null; }
    }

    // ── monitor profiles ──
    /// <summary>Every saved profile's name.</summary>
    public string[] MonitorProfileNames()
        => MonitorProfileStore.All().Select(p => p.Name).ToArray();

    /// <summary>Applies a saved profile by name. Inside a launch the change joins the game scope
    /// (restored when the game exits); outside one, a scope is opened so the exit still restores.
    /// False when the module is off or the name is unknown. No-op in Preview.</summary>
    public bool ApplyMonitorProfile(string name)
    {
        if (_g.Preview) { Log($"preview: ApplyMonitorProfile(\"{name}\") skipped"); return true; }
        if (!LbModules.On(LbModule.Monitors)) { Log("ApplyMonitorProfile: the Monitor Profiles module is off"); return false; }
        var p = MonitorProfileStore.All()
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p == null) { Log($"ApplyMonitorProfile: no profile named \"{name}\""); return false; }
        var r = MonitorProfileApply.GameScopeActive ? MonitorProfileApply.Apply(p) : MonitorProfileApply.BeginGameScope(p);
        Log($"ApplyMonitorProfile(\"{name}\"): {r.Message.ReplaceLineEndings(" | ")}");
        return r.Ok;
    }

    // ── plumbing ──
    private static int Int(string s) => int.TryParse(s.Trim(), out int v) ? v : 0;
    private static int Hex(string s)
    {
        int i = s.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && int.TryParse(s[(i + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : 0;
    }
    private static List<T> Parse<T>(string dump, int minFields, Func<string[], T> make)
    {
        var list = new List<T>();
        using var reader = new StringReader(dump);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            try
            {
                var f = line.Split(new[] { "<>" }, StringSplitOptions.None);
                if (f.Length >= minFields) list.Add(make(f));
            }
            catch { }
        }
        return list;
    }
}
