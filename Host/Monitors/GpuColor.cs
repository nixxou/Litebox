// GPU-output colour control — the settings that live in the DRIVER's panel, not in Windows: output
// colour format (RGB / YCbCr), bits per channel, dynamic range (Full / Limited), digital vibrance.
//
// The FACADE is vendor-neutral and the only thing the rest of the module talks to; NVIDIA (NvAPIWrapper)
// is its first backend. AMD (ADL) and Intel (IGCL) have equivalents but no maintained .NET binding, so
// on their monitors every call answers Supported=false with the vendor named — an explicit refusal, never
// a silent no-op. That is the contract the whole module runs on.
//
// VENDOR-GATED PER MONITOR, NOT PER MACHINE. The gate is the vendor id inside the adapter's DevicePath
// (VEN_10DE = NVIDIA): on a mixed machine, the monitor on the iGPU refuses while the one on the NVIDIA
// card answers. And the match from a monitor to an NVAPI display goes DevicePath → the GDI name of its
// ACTIVE path ("\\.\DISPLAY1") → NvAPI display of the same name. Never by EDID — the one panel plugged
// into an NVIDIA card AND another card at once has one EDID but two DevicePaths, and an EDID match
// could steer an NVIDIA call at the connection the other card owns.
//
// nvapi64.dll ships with the NVIDIA driver, nothing else: the first failed Initialize marks the backend
// dead for the session and every later call short-circuits. No retry loop — a driver does not appear
// mid-session.

#nullable enable

using System;
using System.Linq;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Monitors;

/// <summary>One monitor's GPU-output colour state. <c>Supported</c> false = this monitor's GPU offers no
/// control here; <c>Vendor</c> says which GPU that is, so the refusal can name itself.</summary>
internal readonly record struct GpuColorState(
    bool Supported, string Vendor,
    string Format = "", int DepthBpc = 0, string DynamicRange = "",
    int Vibrance = -1, int VibranceMin = 0, int VibranceMax = 0, int VibranceDefault = 0);

internal static class GpuColor
{
    private const string Tag = "monitors";

    private static bool _nvTried, _nvAlive;
    private static readonly object _gate = new();

    /// <summary>PCI vendor of the adapter driving a monitor, from its adapter DevicePath.</summary>
    public static string VendorOf(string monitorDevicePath)
    {
        try
        {
            var t = WindowsDisplayAPI.DisplayConfig.PathDisplayTarget.GetDisplayTargets()
                ?.FirstOrDefault(x => string.Equals(SafePath(x), monitorDevicePath, StringComparison.OrdinalIgnoreCase));
            string ap = "";
            try { ap = t?.Adapter?.DevicePath ?? ""; } catch { }
            if (ap.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) return "NVIDIA";
            if (ap.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0
                || ap.IndexOf("VEN_1022", StringComparison.OrdinalIgnoreCase) >= 0) return "AMD";
            if (ap.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0) return "Intel";
            return ap.Length > 0 ? "unknown GPU" : "";
        }
        catch { return ""; }
    }

    /// <summary>Read a monitor's GPU-output state. Never throws; a non-NVIDIA monitor (or a dead NVAPI)
    /// comes back Supported=false with the vendor filled in.</summary>
    public static GpuColorState Query(string monitorDevicePath)
    {
        string vendor = VendorOf(monitorDevicePath);
        if (vendor != "NVIDIA" || !NvAlive()) return new GpuColorState(false, vendor);

        try
        {
            var nv = NvDisplayOf(monitorDevicePath);
            if (nv == null) return new GpuColorState(false, vendor);

            var dd = nv.DisplayDevice;
            var cd = dd.CurrentColorData;
            int vib = -1, vmin = 0, vmax = 0, vdef = 0;
            try
            {
                var dvc = nv.DigitalVibranceControl;
                vib = dvc.CurrentLevel; vmin = dvc.MinimumLevel; vmax = dvc.MaximumLevel; vdef = dvc.DefaultLevel;
            }
            catch { }

            return new GpuColorState(true, vendor,
                Format: cd.ColorFormat.ToString(),
                DepthBpc: DepthToBpc(cd.ColorDepth?.ToString() ?? ""),
                DynamicRange: cd.DynamicRange?.ToString() switch { "VESA" => "Full", "CEA" => "Limited", _ => "" },
                Vibrance: vib, VibranceMin: vmin, VibranceMax: vmax, VibranceDefault: vdef);
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, "NVAPI query failed: " + ex.Message);
            return new GpuColorState(false, vendor);
        }
    }

    /// <summary>Apply what is asked ("" / 0 / -1 = leave that part alone). Returns a note for the apply
    /// result — including the explicit refusal when the monitor's GPU is not NVIDIA.</summary>
    public static string Apply(string monitorDevicePath, string format, int depthBpc, string dynamicRange, int vibrance)
    {
        bool wantsColor = format.Length > 0 || depthBpc > 0 || dynamicRange.Length > 0;
        if (!wantsColor && vibrance < 0) return "";

        string vendor = VendorOf(monitorDevicePath);
        if (vendor != "NVIDIA" || !NvAlive())
            return $"GPU output skipped ({(vendor.Length > 0 ? vendor : "no NVIDIA driver")} — NVIDIA only)";

        try
        {
            var nv = NvDisplayOf(monitorDevicePath);
            if (nv == null) return "GPU output skipped (monitor not found on the NVIDIA side)";

            var notes = new System.Collections.Generic.List<string>();

            if (wantsColor)
            {
                var dd = nv.DisplayDevice;
                var cur = dd.CurrentColorData;

                var fmt = format.Length > 0
                    ? Enum.Parse<NvAPIWrapper.Native.Display.ColorDataFormat>(format == "YCbCr444" ? "YUV444" : format == "YCbCr422" ? "YUV422" : format == "YCbCr420" ? "YUV420" : format)
                    : cur.ColorFormat;
                var depth = depthBpc > 0
                    ? Enum.Parse<NvAPIWrapper.Native.Display.ColorDataDepth>("BPC" + depthBpc)
                    : cur.ColorDepth ?? NvAPIWrapper.Native.Display.ColorDataDepth.Default;
                var range = dynamicRange.Length > 0
                    ? (dynamicRange == "Full" ? NvAPIWrapper.Native.Display.ColorDataDynamicRange.VESA
                                              : NvAPIWrapper.Native.Display.ColorDataDynamicRange.CEA)
                    : cur.DynamicRange ?? NvAPIWrapper.Native.Display.ColorDataDynamicRange.Auto;

                var wanted = new NvAPIWrapper.Display.ColorData(fmt,
                    NvAPIWrapper.Native.Display.ColorDataColorimetry.Auto,
                    range, depth,
                    NvAPIWrapper.Native.Display.ColorDataSelectionPolicy.User, null);

                // The driver's own preflight — the same reason the mode path validates before applying:
                // a combination the link cannot carry (10 bpc RGB at full rate on a starved cable) must
                // refuse by name, not half-apply.
                if (!dd.IsColorDataSupported(wanted))
                    notes.Add("GPU colour refused (the link cannot carry that combination)");
                else
                {
                    dd.SetColorData(wanted);
                    notes.Add(("GPU colour: "
                        + (format.Length > 0 ? format + " " : "")
                        + (depthBpc > 0 ? depthBpc + "bpc " : "")
                        + (dynamicRange.Length > 0 ? dynamicRange : "")).TrimEnd());
                }
            }

            if (vibrance >= 0)
            {
                try
                {
                    var dvc = nv.DigitalVibranceControl;
                    int clamped = Math.Clamp(vibrance, dvc.MinimumLevel, dvc.MaximumLevel);
                    if (dvc.CurrentLevel != clamped) { dvc.CurrentLevel = clamped; notes.Add("vibrance " + clamped); }
                }
                catch (Exception ex) { notes.Add("vibrance failed (" + ex.Message + ")"); }
            }

            return string.Join(", ", notes.Where(n => n.Length > 0));
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, "NVAPI apply failed: " + ex.Message);
            return "GPU output failed (" + ex.Message + ")";
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static bool NvAlive()
    {
        lock (_gate)
        {
            if (_nvTried) return _nvAlive;
            _nvTried = true;
            try { NvAPIWrapper.NVIDIA.Initialize(); _nvAlive = true; }
            catch (Exception ex) { LbLog.Once(Tag, "NVAPI unavailable: " + ex.Message); _nvAlive = false; }
            return _nvAlive;
        }
    }

    /// <summary>Monitor DevicePath → the NVAPI display of the SAME active path, correlated through the
    /// GDI name. Null when the monitor is inactive or not on an NVIDIA output — the dual-connection case
    /// resolves correctly here because each connection has its own DevicePath and only the active one
    /// has a GDI name.</summary>
    private static NvAPIWrapper.Display.Display? NvDisplayOf(string monitorDevicePath)
    {
        string gdi = "";
        try { gdi = DisplayTargets.ResolveDisplay(monitorDevicePath)?.DisplayName ?? ""; } catch { }
        if (gdi.Length == 0) return null;
        try
        {
            return NvAPIWrapper.Display.Display.GetDisplays()
                .FirstOrDefault(d => string.Equals(d.Name, gdi, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static int DepthToBpc(string depth)
        => depth.StartsWith("BPC") && int.TryParse(depth.Substring(3), out int v) ? v : 0;

    private static string SafePath(WindowsDisplayAPI.DisplayConfig.PathDisplayTarget t)
    {
        try { return t.DevicePath ?? ""; } catch { return ""; }
    }

    // ── GPU scaling — the NVIDIA panel's per-display "Scaling" (mode + device) ──
    //
    // The one setting that resisted everything: Windows' CCD refuses it, and NvAPIWrapper's high-level
    // SetDisplaysConfig silently drops it — its GetPathAdvancedTargetInfo conversion returns
    // scale=Default regardless of what the managed Scaling property was set to, so the driver receives
    // "you pick" every time. Verified by round-trip: the HIGH-LEVEL write never takes, the NATIVE
    // structure with an explicit PathAdvancedTargetInfo takes immediately.
    //
    // Hence everything here is built at the native layer. For the monitors NOT being changed, their
    // CURRENT scaling is re-injected from the high-level property (which reads correctly) — passing the
    // wrapper's lying Default for them could reset scaling the user set elsewhere.

    /// <summary>The current scaling of one monitor, as the NVAPI enum name ("ToAspectScanOutToClosest"),
    /// or "" when unreadable / not NVIDIA.</summary>
    public static string ScalingGet(string monitorDevicePath)
    {
        if (VendorOf(monitorDevicePath) != "NVIDIA" || !NvAlive()) return "";
        try
        {
            var nv = NvDisplayOf(monitorDevicePath);
            if (nv == null) return "";
            uint id = nv.DisplayDevice.DisplayId;
            return NvAPIWrapper.Display.PathInfo.GetDisplaysConfig()
                .SelectMany(pi => pi.TargetsInfo)
                .First(t => t.DisplayDevice.DisplayId == id)
                .Scaling.ToString();
        }
        catch { return ""; }
    }

    /// <summary>Set one monitor's scaling (NVAPI enum name). Returns a note; "" on the empty request.</summary>
    public static string ScalingSet(string monitorDevicePath, string scalingName)
    {
        if (scalingName.Length == 0) return "";
        string vendor = VendorOf(monitorDevicePath);
        if (vendor != "NVIDIA" || !NvAlive())
            return $"GPU scaling skipped ({(vendor.Length > 0 ? vendor : "no NVIDIA driver")} — NVIDIA only)";

        try
        {
            var nv = NvDisplayOf(monitorDevicePath);
            if (nv == null) return "GPU scaling skipped (monitor not found on the NVIDIA side)";
            uint id = nv.DisplayDevice.DisplayId;

            if (!Enum.TryParse<NvAPIWrapper.Native.Display.Scaling>(scalingName, out var wanted))
                return "GPU scaling skipped (unknown mode " + scalingName + ")";

            var managed = NvAPIWrapper.Display.PathInfo.GetDisplaysConfig();
            var native = new System.Collections.Generic.List<NvAPIWrapper.Native.Interfaces.Display.IPathInfo>();
            foreach (var pi in managed)
            {
                var v2 = pi.GetPathInfoV2();
                var targets = new System.Collections.Generic.List<NvAPIWrapper.Native.Display.Structures.PathTargetInfoV2>();
                foreach (var ti in pi.TargetsInfo)
                {
                    var adv = ti.GetPathAdvancedTargetInfo();
                    // Rebuild with the values the HIGH-LEVEL properties report (they read correctly);
                    // adv itself carries the conversion bug this whole method exists to work around.
                    var scale = ti.DisplayDevice.DisplayId == id ? wanted : ti.Scaling;
                    var rebuilt = new NvAPIWrapper.Native.Display.Structures.PathAdvancedTargetInfo(
                        ti.Rotation, scale, adv.RefreshRateInMillihertz, adv.TimingOverride,
                        adv.IsInterlaced, adv.IsClonePrimary, adv.IsClonePanAndScanTarget,
                        adv.DisableVirtualModeSupport, adv.IsPreferredUnscaledTarget);
                    targets.Add(new NvAPIWrapper.Native.Display.Structures.PathTargetInfoV2(ti.DisplayDevice.DisplayId, rebuilt));
                }
                native.Add(new NvAPIWrapper.Native.Display.Structures.PathInfoV2(targets.ToArray(), v2.SourceModeInfo, v2.SourceId));
            }

            NvAPIWrapper.Native.DisplayApi.SetDisplayConfig(native.ToArray(),
                NvAPIWrapper.Native.Display.DisplayConfigFlags.SaveToPersistence);
            LbLog.Info(Tag, $"GPU scaling → {scalingName}");
            return "GPU scaling: " + ScalingLabel(scalingName);
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, "GPU scaling failed: " + ex.Message);
            return "GPU scaling failed (" + ex.Message + ")";
        }
    }

    /// <summary>Human name for a stored NVAPI scaling value — the NVIDIA panel's own words.</summary>
    public static string ScalingLabel(string v) => v switch
    {
        "ToAspectScanOutToClosest" => "aspect ratio (display)",
        "ToAspectScanOutToNative" => "aspect ratio (GPU)",
        "ToClosest" => "full-screen (display)",
        "ToNative" => "full-screen (GPU)",
        "GPUScanOutToClosest" => "no scaling (display)",
        "GPUScanOutToNative" => "no scaling (GPU)",
        "Default" => "driver default",
        _ => v,
    };

    // ── VRR (G-Sync) — a DRIVER-WIDE setting, not a per-monitor one ─────────
    //
    // VRRMode lives in the driver's BASE profile: one value for the whole machine. So unlike everything
    // else in this file it is not keyed by monitor, and its snapshot/restore rides the profile-level
    // snapshots (restore point, game scope) rather than the per-monitor layout.
    //
    // The driver's virgin state is NO ENTRY (its own default, FullScreenOnly). Restoring must reproduce
    // that exactly: put the old value back when there was one, DELETE the entry when there was none —
    // measured live, SetSetting+Save writes and DeleteSetting+Save removes cleanly across sessions.

    /// <summary>The base profile's VRR entry: (an entry exists, its value 0..2). No NVIDIA → (false, 0)
    /// with <paramref name="supported"/> false.</summary>
    public static (bool Supported, bool HasEntry, uint Value) VrrGet()
    {
        if (!NvAlive()) return (false, false, 0);
        try
        {
            using var session = NvAPIWrapper.DRS.DriverSettingsSession.CreateAndLoad();
            try
            {
                var st = session.BaseProfile.GetSetting(NvAPIWrapper.DRS.KnownSettingId.VRRMode);
                return (true, true, Convert.ToUInt32(st.CurrentValue));
            }
            catch { return (true, false, 0); }   // no explicit entry = driver default
        }
        catch (Exception ex) { LbLog.Warn(Tag, "VRR read failed: " + ex.Message); return (false, false, 0); }
    }

    /// <summary>Write the base profile's VRR mode (0=off, 1=fullscreen only, 2=fullscreen and windowed).</summary>
    public static bool VrrSet(uint mode)
    {
        if (!NvAlive()) return false;
        try
        {
            using var session = NvAPIWrapper.DRS.DriverSettingsSession.CreateAndLoad();
            session.BaseProfile.SetSetting(NvAPIWrapper.DRS.KnownSettingId.VRRMode, mode);
            session.Save();
            LbLog.Info(Tag, "VRR mode → " + mode);
            return true;
        }
        catch (Exception ex) { LbLog.Warn(Tag, "VRR write failed: " + ex.Message); return false; }
    }

    /// <summary>Put the driver back where a snapshot found it — value restored, or entry deleted when
    /// there was none. The delete matters: leaving an explicit entry at the default value is not the
    /// state the machine was in.</summary>
    public static bool VrrRestore(bool hadEntry, uint value)
    {
        if (!NvAlive()) return false;
        try
        {
            using var session = NvAPIWrapper.DRS.DriverSettingsSession.CreateAndLoad();
            if (hadEntry) session.BaseProfile.SetSetting(NvAPIWrapper.DRS.KnownSettingId.VRRMode, value);
            else
            {
                try { session.BaseProfile.DeleteSetting(NvAPIWrapper.DRS.KnownSettingId.VRRMode); }
                catch { return true; }   // already absent
            }
            session.Save();
            LbLog.Info(Tag, hadEntry ? "VRR restored to " + value : "VRR entry removed (driver default)");
            return true;
        }
        catch (Exception ex) { LbLog.Warn(Tag, "VRR restore failed: " + ex.Message); return false; }
    }
}
