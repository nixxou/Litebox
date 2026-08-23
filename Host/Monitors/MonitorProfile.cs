// The Monitor Profiles model — what one profile remembers and replays.
//
// A profile is FOUR INDEPENDENT PARTS, each optional (null = "don't touch that"):
//
//   Layout  the full display topology — which monitors are on, where, at what resolution /
//           refresh / rotation / scaling, which one is primary, and each one's DPI zoom.
//   Preset  a single monitor's display mode ("main monitor: 1920x1080 @ 60 Hz"), applied AFTER
//           the layout. The cheap case: no topology change, just switch the mode for a game.
//   Audio   default playback device and/or its master volume.
//   Solo    disable every monitor except the primary one.
//
// A profile carrying only a Preset is the common one; a profile carrying only Audio is legal too.
//
// WHY WE DON'T STORE THE RAW STRUCTS — the previous generation of this feature (BigBoxProfile /
// TeknoparrotAutoXinput, via CCDWrapper) serialised DISPLAYCONFIG_PATH_INFO to XML and replayed it
// verbatim. Those structs identify an adapter by its LUID, which Windows REASSIGNS on reboot and on
// any GPU change — hence the pile of LUID-rematching heuristics in that code, and hence profiles
// that silently stopped applying after a hardware change.
//
// Here nothing adapter-scoped is persisted. Each monitor is stored by DevicePath (verified identical
// across PathDisplayTarget / DisplayDevice / Display — the one stable key in the whole API) with the
// EDID pair as a fallback, and MonitorProfileApply REBUILDS live PathInfo objects against whatever
// hardware is present at apply time. A stale LUID cannot exist because no LUID is ever written down.
//
// Enums are stored as STRINGS (their WindowsDisplayAPI enum names) rather than ints: a profile stays
// readable in the DB, and a future library version renumbering a value can't silently repoint it.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace LbApiHost.Host.Monitors;

/// <summary>One saved monitor profile. Serialised to JSON as a list under the global
/// "MonitorProfiles" option key (see <see cref="MonitorProfileStore"/>).</summary>
internal sealed class MonitorProfile
{
    /// <summary>Stable id (guid string) — the menu's check mark and any future per-emulator /
    /// per-game assignment reference this, so renaming a profile never breaks a binding.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Shown in Tools ▸ Monitor Profiles, and reachable through the web endpoints. Default ON:
    /// a profile you just made is one you want to reach. Unticking it does NOT disable the profile — its
    /// hotkey still works and it is still editable here; it simply stops being offered in the menu and
    /// over the network, which is how a scratch or a dangerous one is kept out of easy reach.</summary>
    public bool Public { get; set; } = true;

    /// <summary>Global hotkey ("Ctrl+Alt+1"), or empty. Independent of <see cref="Public"/> — binding a
    /// key to a profile hidden from the menu is a deliberate combination, not a contradiction.</summary>
    public string Hotkey { get; set; } = "";

    /// <summary>Register the hotkey system-wide (RegisterHotKey) instead of only while LiteBox has focus.
    /// Off by default: a global binding is CONFISCATED from every other application, game included, which
    /// is a fair trade for one deliberate key and a bad one applied to every profile that has one.</summary>
    public bool HotkeyGlobal { get; set; }

    /// <summary>Full topology, or null to leave the layout alone.</summary>
    public MonitorLayout? Layout { get; set; }

    /// <summary>One monitor's display mode, applied after the layout, or null.</summary>
    public MonitorPreset? Preset { get; set; }

    /// <summary>Default playback device / volume, or null.</summary>
    public AudioPreset? Audio { get; set; }

    /// <summary>Disable every monitor but the primary (applied after Layout, before Preset).</summary>
    public bool SoloPrimary { get; set; }

    /// <summary>DevicePaths to switch OFF, by name. The precise form of <see cref="SoloPrimary"/>, which is
    /// a hammer: on a cabinet with a marquee screen the answer is usually "turn off THAT one", not
    /// "everything but the primary".</summary>
    public List<string> DisableMonitors { get; set; } = new();

    /// <summary>What happens to monitors that are connected but NOT named by this profile's layout.
    ///
    /// A SEPARATE question from <see cref="AdaptToConnected"/>, which answers "a monitor the profile names
    /// is missing". Conflating them — as this module did at first — means one switch decides two unrelated
    /// things, and the user cannot express "keep my second screen, but refuse if the TV is unplugged".
    ///
    /// "" = decide from AdaptToConnected, which is how profiles written before this field behaved:
    /// adaptive kept them where they were, strict let them go dark.</summary>
    public string ExtrasPolicy { get; set; } = "";

    public const string ExtrasOff = "off";        // switch them off
    public const string ExtrasKeep = "keep";      // leave them where they are (moved only if they overlap)
    public const string ExtrasRight = "right";    // line them up past the profile's edge
    public const string ExtrasLeft = "left";
    public const string ExtrasTop = "top";
    public const string ExtrasBottom = "bottom";

    /// <summary>The policy actually in force, resolving the legacy empty value.</summary>
    public string EffectiveExtras => ExtrasPolicy.Length > 0 ? ExtrasPolicy
                                   : AdaptToConnected ? ExtrasKeep : ExtrasOff;

    /// <summary>How a stored monitor is matched to live hardware: false (default) = DevicePath first,
    /// then the EDID pair when the path changed (GPU swap, moved cable). True = DevicePath ONLY.
    ///
    /// Strict exists for the one desk the fallback can betray: a panel wired to TWO adapters at once
    /// (HDMI on the NVIDIA card, HDMI on the iGPU). One EDID, two DevicePaths — the fallback could match
    /// the record onto the connection the profile never meant. Someone with that wiring knows it, and
    /// this is their switch.</summary>
    public bool StrictMatch { get; set; }

    /// <summary>Off (default): the profile is a whole-desktop statement and refuses to apply unless every
    /// monitor it names is connected — a half-applied layout that looks like a success is worse than a
    /// clear refusal. On: unplugged monitors are skipped, and monitors connected but NOT in the profile
    /// keep their place instead of going dark. For a machine whose screens come and go (a dock, a TV that
    /// is not always on) that is the difference between a profile that works and one that never fires.</summary>
    public bool AdaptToConnected { get; set; }

    /// <summary>True when the profile would do nothing at all — the editor refuses to save one.</summary>
    public bool IsEmpty => Layout == null && Preset == null && Audio == null && !SoloPrimary
                           && DisableMonitors.Count == 0;

    internal static string ZoomHelp(string dpiScale) => LayoutPath.ZoomPercent(dpiScale);

    /// <summary>One-line recap for the editor list ("Layout (3 monitors) · 1920x1080@60 · Solo").</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Layout != null)
        {
            int dup = Layout.Paths.Where(r => r.SourceGroup >= 0).GroupBy(r => r.SourceGroup).Count(g => g.Count() > 1);
            parts.Add($"Layout ({Layout.Paths.Count} monitor{(Layout.Paths.Count == 1 ? "" : "s")}"
                      + (dup > 0 ? $", {dup} duplicated set{(dup == 1 ? "" : "s")}" : "") + ")");
        }
        if (Preset is { IsEmpty: false }) parts.Add(Preset.Describe());
        if (SoloPrimary) parts.Add("Solo primary");
        if (DisableMonitors.Count > 0) parts.Add($"{DisableMonitors.Count} monitor(s) off");
        if (Audio != null) parts.Add(Audio.Describe());
        if (AdaptToConnected && Layout != null) parts.Add("adaptive");
        return parts.Count == 0 ? "(empty)" : string.Join(" · ", parts);
    }
}

/// <summary>A whole topology: one record per ACTIVE monitor at capture time. A monitor absent from
/// this list is one the profile turns OFF.</summary>
internal sealed class MonitorLayout
{
    public List<LayoutPath> Paths { get; set; } = new();
}

/// <summary>One monitor inside a layout. Everything here is either an identity (DevicePath / EDID)
/// or a value Windows can be told to reproduce — never an adapter-scoped handle.</summary>
internal sealed class LayoutPath
{
    // ── identity (in match priority order) ──
    /// <summary>`\\?\DISPLAY#GBT2709#5&amp;1bc2f44e&amp;0&amp;UID8451#{guid}` — primary key.</summary>
    public string DevicePath { get; set; } = "";
    /// <summary>EDID manufacture code ("GBT"), the fallback when DevicePath no longer matches
    /// (the path embeds an adapter instance, so a GPU swap can change it).</summary>
    public string EdidManufacture { get; set; } = "";
    /// <summary>EDID product code, paired with <see cref="EdidManufacture"/>.</summary>
    public int EdidProduct { get; set; }
    /// <summary>Human label ("G27Q") — display only, never used to match.</summary>
    public string FriendlyName { get; set; } = "";

    // ── geometry ──
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Refresh in MILLIhertz, as CCD reports it — a 60 Hz panel commonly reads 59951, which is
    /// exactly why the mode search downstream can't demand an exact integer match.</summary>
    public ulong FrequencyMilliHz { get; set; }

    // ── enum names (WindowsDisplayAPI enum member names; empty = NotSpecified) ──
    public string Rotation { get; set; } = "";
    public string Scaling { get; set; } = "";
    public string ScanLineOrdering { get; set; } = "";
    public string PixelFormat { get; set; } = "";

    // ── the TARGET signal (what actually goes down the cable) ──
    // Captured for DIAGNOSTICS, not replayed: handing Windows a target signal makes SetDisplayConfig
    // reject the whole set ("Invalid paths information") even when the signal is reproduced byte for
    // byte — measured on a live 3-monitor config. The source/target mismatch it was meant to fix is
    // handled instead by MonitorProfileApply.ApplyModes, which pins each monitor's mode afterwards.
    // Keeping the values costs nothing and makes --monitor-probe able to show what a panel is really
    // being fed. Zero = nothing captured (profiles saved before this existed).
    public int SignalActiveWidth { get; set; }
    public int SignalActiveHeight { get; set; }
    /// <summary>Active area plus blanking — the pixel clock's real footprint.</summary>
    public int SignalTotalWidth { get; set; }
    public int SignalTotalHeight { get; set; }
    public ulong SignalVSyncMilliHz { get; set; }
    public int SignalVSyncDivider { get; set; }
    /// <summary>VideoSignalStandard member name ("Other", "VesaDmt", …).</summary>
    public string SignalStandard { get; set; } = "";

    /// <summary>True when a full target signal was captured and can be replayed verbatim.</summary>
    public bool HasSignal => SignalActiveWidth > 0 && SignalActiveHeight > 0
                             && SignalTotalWidth > 0 && SignalTotalHeight > 0 && SignalVSyncMilliHz > 0;

    /// <summary>How a below-native mode is presented (DisplayFixedOutput member name). This is the DEVMODE
    /// field, NOT the CCD <see cref="Scaling"/> above — two different settings with confusingly similar
    /// names. Captured because the Display-mode section can SET it, and anything a preset can set the
    /// layout must capture, or the game-exit restore has nothing to put back.</summary>
    public string OutputScaling { get; set; } = "";

    /// <summary>What the panel is actually being fed: "RGB", "YCbCr444", … and the bits per channel.
    /// READ-ONLY — Windows reports them but offers no setter, so they are captured for diagnosis and never
    /// restored. A profile taken in RGB 8bpc facing a screen now in YCbCr422 explains fringed text that
    /// nothing else in this module would account for.</summary>
    public string ColorEncoding { get; set; } = "";
    public int BitsPerChannel { get; set; }

    // ── GPU-output state (captured only when the driving GPU offered it — NVIDIA today). Restored on
    //    apply, vendor-gated again at that moment: the same profile on a machine whose GPU changed skips
    //    these with a note instead of failing. GpuVibrance -1 / empty strings = nothing captured. ──
    public string GpuFormat { get; set; } = "";
    public int GpuDepthBpc { get; set; }
    public string GpuDynamicRange { get; set; } = "";
    public int GpuVibrance { get; set; } = -1;
    /// <summary>NVAPI scaling enum name captured from the driver; restored on apply, NVIDIA-gated.</summary>
    public string GpuScaling { get; set; } = "";

    /// <summary>Per-monitor zoom (DisplayConfigSourceDPIScale member name, e.g. "Scale175Percent";
    /// "Identity" is 100%). Empty = the profile doesn't restore this monitor's zoom.</summary>
    public string DpiScale { get; set; } = "";

    public bool Primary { get; set; }

    /// <summary>HDR ("advanced color") state to restore, or null when the profile does not carry one —
    /// profiles captured before HDR existed here, and monitors whose state could not be read. Null means
    /// LEAVE IT ALONE, which is the only safe reading of "no information".</summary>
    public bool? Hdr { get; set; }

    /// <summary>Whether the panel could do HDR at capture time. Informational: the apply path re-asks the
    /// hardware rather than trusting a recording, since the answer changes with the cable and the mode.</summary>
    public bool HdrSupported { get; set; }

    /// <summary>Which desktop SURFACE this monitor was driven by, as an index within its own capture.
    ///
    /// This is what makes duplicated screens survive. A clone set is not a flag in the data — it is two
    /// monitors hanging off ONE source, which the capture sees directly (a path with several targets) and
    /// which has to be reproduced the same way or the duplicate comes back as an extended desktop.
    ///
    /// The obvious-looking field, DisplayConfig's own CloneGroupId, is NOT usable: it lives in a union
    /// that is only meaningful when the path supports virtual mode, and on ordinary hardware reading it
    /// throws. Relying on it meant every clone captured as null and silently un-cloned itself on the way
    /// back. -1 = captured before this field existed (legacy profiles fall back to CloneGroup).</summary>
    public int SourceGroup { get; set; } = -1;

    /// <summary>LEGACY. DisplayConfig's CloneGroupId, kept so profiles saved before <see cref="SourceGroup"/>
    /// still group their clones. Never written any more — see the note above for why it cannot be trusted.</summary>
    public uint? CloneGroup { get; set; }

    public string Label => string.IsNullOrEmpty(FriendlyName) ? DevicePath : FriendlyName;

    /// <summary>The per-port token out of the DevicePath ("UID8451"), or "". Two monitors of the SAME
    /// model report the same friendly name AND the same EDID pair — only this differs, because it names
    /// the output they hang off rather than the panel. It is what makes twins tellable apart on screen.</summary>
    public string PortId => PortIdOf(DevicePath);

    internal static string PortIdOf(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return "";
        int i = devicePath.IndexOf("UID", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int j = i + 3;
        while (j < devicePath.Length && char.IsDigit(devicePath[j])) j++;
        return j > i + 3 ? devicePath.Substring(i, j - i) : "";
    }

    /// <summary>The zoom as the user knows it — "100%", "175%". <see cref="DpiScale"/> holds Windows'
    /// enum member name ("Identity", "Scale175Percent"), which is fine to persist but unreadable on
    /// screen; "—" when this monitor's zoom was not captured.</summary>
    public string ZoomText => ZoomPercent(DpiScale);

    /// <summary>"Scale175Percent" → "175%", "Identity" → "100%", "" → "—". Windows' enum names are fine to
    /// persist and unreadable on screen.</summary>
    internal static string ZoomPercent(string dpiScale)
    {
        if (string.IsNullOrEmpty(dpiScale)) return "—";
        if (dpiScale == "Identity") return "100%";
        var digits = new string(dpiScale.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits + "%" : dpiScale;
    }

    /// <summary>Refresh in Hz for display — CCD counts in millihertz and a "60 Hz" panel commonly
    /// reports 59951, so two decimals is the honest rendering.</summary>
    public string RefreshText => (FrequencyMilliHz / 1000.0).ToString("0.##") + " Hz";
}

/// <summary>A single monitor's display mode. <see cref="DevicePath"/> empty = "whichever monitor is
/// primary when the profile runs" — which is what makes a "main monitor 1080p60" profile portable
/// between machines.</summary>
internal sealed class MonitorPreset
{
    private static string ZoomPercent(string s) => LayoutPath.ZoomPercent(s);

    public string DevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    /// <summary>EDID identity of the named monitor, so a preset survives a GPU swap the same way a
    /// captured layout does. Empty for "the main monitor", which is resolved by role, not by identity.</summary>
    public string EdidManufacture { get; set; } = "";
    public int EdidProduct { get; set; }
    /// <summary>0 = leave the mode alone. A preset may carry only an HDR choice — "this game hates HDR"
    /// is as common a need as a resolution change, and forcing a resolution just to reach the HDR switch
    /// would be a change the user never asked for.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Target refresh in WHOLE Hz (what the user picks). The search applies the fallback
    /// ladder in <see cref="MonitorProfileApply"/> — never a bare equality test.</summary>
    public int Frequency { get; set; }

    /// <summary>Force HDR on (true) or off (false); null = leave it as it is.</summary>
    public bool? Hdr { get; set; }

    /// <summary>Screen orientation (DisplayOrientation member name: Identity / Rotate90 / Rotate180 /
    /// Rotate270); "" = leave it alone. The TATE case — a vertical shmup asking for a rotated panel — is
    /// the single most common reason to want a per-game display profile on a cabinet.</summary>
    public string Rotation { get; set; } = "";

    /// <summary>How a mode SMALLER than the panel is presented (DisplayFixedOutput member name: Default /
    /// Stretch / Center); "" = leave it alone. 4:3 arcade content on a 16:9 panel is stretched or centred,
    /// and which one is right is a per-game answer.</summary>
    public string OutputScaling { get; set; } = "";

    /// <summary>Windows zoom for this monitor (DisplayConfigSourceDPIScale member name); "" = leave it.</summary>
    public string DpiScale { get; set; } = "";

    // ── GPU output (NVIDIA only — driver-panel settings, unreachable through any Windows API) ──
    /// <summary>"RGB" / "YCbCr444" / "YCbCr422" / "YCbCr420"; "" = leave it.</summary>
    public string GpuFormat { get; set; } = "";
    /// <summary>8 / 10 / 12 bits per channel; 0 = leave it.</summary>
    public int GpuDepthBpc { get; set; }
    /// <summary>"Full" / "Limited"; "" = leave it. The washed-out-blacks setting.</summary>
    public string GpuDynamicRange { get; set; } = "";
    /// <summary>Digital vibrance level; -1 = leave it. NVIDIA's own scale (typically 0–63, default ~50).</summary>
    public int GpuVibrance { get; set; } = -1;

    /// <summary>GPU scaling — the NVIDIA panel's per-display "Scaling" (mode + device), stored as the
    /// NVAPI enum name ("ToAspectScanOutToClosest", …); "" = leave it. What decides whether 4:3 content
    /// is stretched, boxed or centred, and whether the GPU or the panel does the work.</summary>
    public string GpuScaling { get; set; } = "";

    /// <summary>G-Sync / VRR: "" = leave it, "off", "fullscreen", "always". DRIVER-WIDE — the one setting
    /// in this group that is not per-monitor; it is snapshotted and restored at the profile level.</summary>
    public string GpuVrr { get; set; } = "";

    public bool HasGpu => GpuFormat.Length > 0 || GpuDepthBpc > 0 || GpuDynamicRange.Length > 0 || GpuVibrance >= 0
                          || GpuVrr.Length > 0 || GpuScaling.Length > 0;

    /// <summary>Make this monitor the primary one — the desktop origin moves onto it and every other
    /// screen shifts to match. Only meaningful for a NAMED monitor: "the main monitor" already is one.</summary>
    public bool MakePrimary { get; set; }

    /// <summary>What to do when the monitor cannot do the requested mode: true = fall back to the closest
    /// it can, false = refuse the profile and say so.
    ///
    /// Separate from MonitorProfile.AdaptToConnected on purpose — those answer different questions. That
    /// one is about a monitor being ABSENT; this one is about a present monitor not offering the mode.
    /// The distinction earns its keep because the Display-mode section may target "the main monitor",
    /// whose identity the profile itself decides, so its capabilities are simply not knowable while
    /// editing.</summary>
    public bool AdjustToClosest { get; set; } = true;

    public bool HasMode => Width > 0 && Height > 0;
    public bool IsEmpty => !HasMode && Hdr == null && !MakePrimary
                           && Rotation.Length == 0 && OutputScaling.Length == 0 && DpiScale.Length == 0
                           && !HasGpu;

    public string Describe()
    {
        string who = string.IsNullOrEmpty(DevicePath) ? "Main" : (string.IsNullOrEmpty(FriendlyName) ? "Monitor" : FriendlyName);
        string what = HasMode ? $"{Width}x{Height}" + (Frequency > 0 ? $"@{Frequency}" : "") : "";
        if (Hdr != null) what = (what.Length > 0 ? what + " " : "") + (Hdr.Value ? "HDR" : "SDR");
        if (Rotation is { Length: > 0 } and not "Identity") what = (what.Length > 0 ? what + " " : "") + Rotation;
        if (OutputScaling.Length > 0) what = (what.Length > 0 ? what + " " : "") + OutputScaling.ToLowerInvariant();
        if (DpiScale.Length > 0) what = (what.Length > 0 ? what + " " : "") + "zoom " + ZoomPercent(DpiScale);
        if (HasGpu) what = (what.Length > 0 ? what + " " : "") + "GPU("
            + string.Join(" ", new[] { GpuFormat, GpuDepthBpc > 0 ? GpuDepthBpc + "bpc" : "", GpuDynamicRange,
                                       GpuVibrance >= 0 ? "vib" + GpuVibrance : "" }.Where(x => x.Length > 0)) + ")";
        if (MakePrimary) what = (what.Length > 0 ? what + ", " : "") + "primary";
        return $"{who}: {what}";
    }
}

/// <summary>Default playback endpoint and/or its master volume.</summary>
internal sealed class AudioPreset
{
    /// <summary>Endpoint friendly name ("Speakers (Realtek…)"); empty = don't change the device.</summary>
    public string Device { get; set; } = "";
    /// <summary>Master volume 0..100, or null = don't change the volume.</summary>
    public int? Volume { get; set; }

    public string Describe()
    {
        if (!string.IsNullOrEmpty(Device) && Volume.HasValue) return $"Audio: {Device} @ {Volume}%";
        if (!string.IsNullOrEmpty(Device)) return "Audio: " + Device;
        return Volume.HasValue ? $"Volume: {Volume}%" : "Audio";
    }
}
