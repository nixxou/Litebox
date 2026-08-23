// The bridge between LiteBox's stored records and the live display hardware.
//
// Three jobs, all of them "translate between a durable identity and a volatile handle":
//   Capture()      the current topology → LayoutPath records (nothing adapter-scoped kept).
//   Resolve*()     a stored record → the PathDisplayTarget / Display that is present RIGHT NOW.
//   PickMode()     a (width, height, Hz) wish → an actually-supported DisplayPossibleSetting.
//
// MATCHING. DevicePath first (it is the same string on PathDisplayTarget, DisplayDevice and Display —
// verified on real hardware, and the only identifier shared by all three). It embeds an adapter
// instance though (`…#5&1bc2f44e&0&UID8451#…`), so a GPU swap can change it while the monitor is
// physically the same panel — hence the EDID (manufacture code + product code) fallback.
//
// PICKMODE'S LADDER is not defensive coding, it is a correction of a real failure. Windows reports a
// "60 Hz" panel as 59951 mHz through CCD and rounds it to 59 through EnumDisplaySettings — asking for
// exactly 60 finds nothing on hardware that plainly supports it (reproduced on this machine's G27Q).
// So: exact → one Hz below (the 59-for-60 case) → nearest above → nearest below. The 32-bit /
// non-interlaced filter is equally load-bearing: the raw enumeration returns lower colour depths and
// duplicate entries that make a naive First() pick a mode the user never asked for.
//
// Every accessor here swallows and logs: a phantom display source (Windows enumerates plenty of
// inactive ones) throws on its DPI properties rather than returning a value.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using LbApiHost.Host.Diag;
using WindowsDisplayAPI;
using WindowsDisplayAPI.DisplayConfig;
using WindowsDisplayAPI.Native.DisplayConfig;

namespace LbApiHost.Host.Monitors;

/// <summary>One monitor as offered to the user in the editor.</summary>
internal sealed record MonitorInfo(string DevicePath, string FriendlyName, string EdidManufacture,
                                   int EdidProduct, bool Active, bool Primary, string CurrentMode,
                                   string DisplayName = "", string Gpu = "", string Connector = "");

internal static class DisplayTargets
{
    private const string Tag = "monitors";

    // ── capture ──────────────────────────────────────────────────────────────

    /// <summary>The current topology as durable records. Null when the query fails (nothing to save).</summary>
    public static MonitorLayout? Capture()
    {
        PathInfo[] paths;
        try { paths = PathInfo.GetActivePaths(); }
        catch (Exception ex) { LbLog.Warn(Tag, "GetActivePaths failed: " + ex.Message); return null; }
        if (paths == null || paths.Length == 0) { LbLog.Warn(Tag, "no active display path to capture"); return null; }

        var layout = new MonitorLayout();
        int sourceGroup = -1;
        foreach (var p in paths)
        {
            sourceGroup++;                 // one desktop surface; several targets on it = a clone set
            // One PathInfo = one desktop SOURCE; a clone set carries several targets on the same
            // source. Each target becomes its own record, sharing the source's geometry — which is
            // exactly what rebuilding a clone group needs.
            PathTargetInfo[] targets;
            try { targets = p.TargetsInfo ?? Array.Empty<PathTargetInfo>(); }
            catch { continue; }

            foreach (var t in targets)
            {
                var tgt = Try(() => t.DisplayTarget);
                if (tgt == null) continue;

                var rec = new LayoutPath
                {
                    DevicePath = Try(() => tgt.DevicePath) ?? "",
                    EdidManufacture = Try(() => tgt.EDIDManufactureCode) ?? "",
                    EdidProduct = TryVal(() => (int)tgt.EDIDProductCode),
                    FriendlyName = Try(() => tgt.FriendlyName) ?? "",
                    X = TryVal(() => p.Position.X),
                    Y = TryVal(() => p.Position.Y),
                    Width = TryVal(() => p.Resolution.Width),
                    Height = TryVal(() => p.Resolution.Height),
                    FrequencyMilliHz = (ulong)TryVal(() => (long)t.FrequencyInMillihertz),
                    Rotation = Name(() => t.Rotation),
                    Scaling = Name(() => t.Scaling),
                    ScanLineOrdering = Name(() => t.ScanLineOrdering),
                    PixelFormat = Name(() => p.PixelFormat),
                    DpiScale = CaptureDpi(p),
                    OutputScaling = CaptureOutputScaling(rec0DevicePath: Try(() => tgt.DevicePath) ?? ""),
                    ColorEncoding = HdrOf(tgt) is { } hs && hs.Encoding.Length > 0 ? hs.Encoding : "",
                    BitsPerChannel = HdrOf(tgt).Bits,
                    GpuFormat = GpuOf(tgt).Supported ? GpuOf(tgt).Format : "",
                    GpuDepthBpc = GpuOf(tgt).Supported ? GpuOf(tgt).DepthBpc : 0,
                    GpuDynamicRange = GpuOf(tgt).Supported ? GpuOf(tgt).DynamicRange : "",
                    GpuVibrance = GpuOf(tgt).Supported ? GpuOf(tgt).Vibrance : -1,
                    GpuScaling = GpuOf(tgt).Supported ? GpuColor.ScalingGet(Try(() => tgt.DevicePath) ?? "") : "",
                    SignalActiveWidth = TryVal(() => t.IsSignalInformationAvailable ? t.SignalInfo.ActiveSize.Width : 0),
                    SignalActiveHeight = TryVal(() => t.IsSignalInformationAvailable ? t.SignalInfo.ActiveSize.Height : 0),
                    SignalTotalWidth = TryVal(() => t.IsSignalInformationAvailable ? t.SignalInfo.TotalSize.Width : 0),
                    SignalTotalHeight = TryVal(() => t.IsSignalInformationAvailable ? t.SignalInfo.TotalSize.Height : 0),
                    SignalVSyncMilliHz = (ulong)TryVal(() => t.IsSignalInformationAvailable ? (long)t.SignalInfo.VerticalSyncFrequencyInMillihertz : 0L),
                    SignalVSyncDivider = TryVal(() => t.IsSignalInformationAvailable ? t.SignalInfo.VerticalSyncFrequencyDivider : 0),
                    SignalStandard = t.IsSignalInformationAvailable ? Name(() => t.SignalInfo.VideoStandard) : "",
                    Primary = TryVal(() => p.IsGDIPrimary ? 1 : 0) == 1,
                    SourceGroup = sourceGroup,
                    Hdr = HdrOf(tgt).Supported ? HdrOf(tgt).Enabled : (bool?)null,
                    HdrSupported = HdrOf(tgt).Supported,
                };
                if (string.IsNullOrEmpty(rec.DevicePath)) continue;   // unidentifiable → unrestorable
                layout.Paths.Add(rec);
            }
        }

        if (layout.Paths.Count == 0) { LbLog.Warn(Tag, "captured 0 identifiable monitors"); return null; }
        foreach (var g in layout.Paths.GroupBy(r => r.SourceGroup).Where(g => g.Count() > 1))
            LbLog.Info(Tag, $"clone set captured: {string.Join(" = ", g.Select(r => r.Label))}");
        LbLog.Info(Tag, $"captured layout: {string.Join(", ", layout.Paths.Select(r => $"{r.Label} {r.Width}x{r.Height}@{r.FrequencyMilliHz / 1000.0:0.###}{(r.Primary ? "*" : "")}"))}");
        return layout;
    }

    /// <summary>The DEVMODE output-scaling of the display currently on <paramref name="rec0DevicePath"/>,
    /// as an enum name; "" when unreadable. Distinct from the CCD path scaling.</summary>
    private static string CaptureOutputScaling(string rec0DevicePath)
    {
        try
        {
            var d = ResolveDisplay(rec0DevicePath);
            return d?.CurrentSetting?.OutputScalingMode.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>HDR state of one target, memoised for the duration of a capture: the record needs the
    /// answer three times and each call is a round trip through DisplayConfigGetDeviceInfo.</summary>
    private static PathDisplayTarget? _gpuCacheKey;
    private static GpuColorState _gpuCacheValue;

    private static GpuColorState GpuOf(PathDisplayTarget target)
    {
        if (ReferenceEquals(_gpuCacheKey, target)) return _gpuCacheValue;
        _gpuCacheKey = target;
        string path = "";
        try { path = target.DevicePath ?? ""; } catch { }
        _gpuCacheValue = GpuColor.Query(path);
        return _gpuCacheValue;
    }

    private static PathDisplayTarget? _hdrCacheKey;
    private static HdrControl.HdrState _hdrCacheValue;

    private static HdrControl.HdrState HdrOf(PathDisplayTarget target)
    {
        if (ReferenceEquals(_hdrCacheKey, target)) return _hdrCacheValue;
        _hdrCacheKey = target;
        _hdrCacheValue = HdrControl.Query(target);
        return _hdrCacheValue;
    }

    /// <summary>The source's zoom, as an enum member name. Empty when the source has none (Windows
    /// enumerates inactive sources whose DPI properties THROW rather than return a value).</summary>
    private static string CaptureDpi(PathInfo p)
    {
        try
        {
            var src = p.DisplaySource;
            if (src == null) return "";
            return src.CurrentDPIScale.ToString();
        }
        catch { return ""; }
    }

    // ── resolve ──────────────────────────────────────────────────────────────

    /// <summary>The live target for a stored record: DevicePath first, EDID pair as fallback.
    /// Null when that monitor is not attached right now.</summary>
    public static PathDisplayTarget? ResolveTarget(LayoutPath rec)
    {
        PathDisplayTarget[] all;
        try { all = PathDisplayTarget.GetDisplayTargets(); }
        catch (Exception ex) { LbLog.Warn(Tag, "GetDisplayTargets failed: " + ex.Message); return null; }
        if (all == null) return null;

        var hit = all.FirstOrDefault(t => string.Equals(Try(() => t.DevicePath) ?? "", rec.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;

        if (!string.IsNullOrEmpty(rec.EdidManufacture))
        {
            hit = all.FirstOrDefault(t => string.Equals(Try(() => t.EDIDManufactureCode) ?? "", rec.EdidManufacture, StringComparison.OrdinalIgnoreCase)
                                       && TryVal(() => (int)t.EDIDProductCode) == rec.EdidProduct);
            if (hit != null)
            {
                LbLog.Info(Tag, $"{rec.Label}: DevicePath changed, matched on EDID {rec.EdidManufacture}/{rec.EdidProduct}");
                return hit;
            }
        }
        return null;
    }

    // ── enumeration cache ────────────────────────────────────────────────────
    //
    // Display.GetDisplays() walks the device tree and GetPossibleSettings() loops EnumDisplaySettings
    // until it runs dry — 112 modes on this machine's main panel, each one a struct marshal. The editor
    // asks for both every time a checkbox moves, which turned a click into a visible freeze. Neither
    // answer changes unless the hardware or the configuration does, so both are cached until something
    // we do (a capture, an apply) or the user does (plugging a monitor) can have changed them.
    //
    // Invalidate() is called explicitly rather than on a timer: a stale mode list is only reachable by
    // hot-plugging a monitor while the editor sits open, and the editor re-enumerates on capture anyway.

    private static Display[]? _displaysCache;
    private static readonly Dictionary<string, List<DisplayPossibleSetting>> _modesCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop the cached enumerations — after anything that can change the display configuration.</summary>
    public static void Invalidate()
    {
        _displaysCache = null;
        _modesCache.Clear();
    }

    private static Display[] Displays()
    {
        if (_displaysCache != null) return _displaysCache;
        try { return _displaysCache = Display.GetDisplays()?.ToArray() ?? Array.Empty<Display>(); }
        catch (Exception ex) { LbLog.Warn(Tag, "GetDisplays failed: " + ex.Message); return _displaysCache = Array.Empty<Display>(); }
    }

    /// <summary>Resolve a WHOLE set of records to live targets at once, never handing the same monitor
    /// to two records.
    ///
    /// IDENTICAL MONITORS are why this cannot be a per-record lookup. Two screens of the same model report
    /// the same friendly name and the same EDID pair; only the DevicePath differs, by the UID naming the
    /// output. So the exact DevicePath match is done FIRST, for every record, and only what is left over
    /// falls back to EDID — against the targets nobody has claimed. Resolving record by record would let
    /// the EDID fallback hand the same panel to both twins, producing a path set that names one monitor
    /// twice: Windows either refuses it or does something arbitrary.
    ///
    /// When several unclaimed twins still match, they are taken in record order — deterministic, and the
    /// only honest answer once the ports have genuinely changed identity. It is reported, because the two
    /// screens may end up swapped in position.</summary>
    public static Dictionary<LayoutPath, PathDisplayTarget> ResolveTargets(IEnumerable<LayoutPath> records, List<string> notes,
                                                                          bool strictMatch = false)
    {
        var map = new Dictionary<LayoutPath, PathDisplayTarget>();
        PathDisplayTarget[] all;
        try { all = PathDisplayTarget.GetDisplayTargets() ?? Array.Empty<PathDisplayTarget>(); }
        catch (Exception ex) { LbLog.Warn(Tag, "GetDisplayTargets failed: " + ex.Message); return map; }

        var free = all.ToList();
        var pending = new List<LayoutPath>();

        // Pass 1 — exact DevicePath. Unique by construction, twins included.
        foreach (var rec in records)
        {
            var hit = free.FirstOrDefault(t => string.Equals(Try(() => t.DevicePath) ?? "", rec.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { map[rec] = hit; free.Remove(hit); }
            else pending.Add(rec);
        }

        // Pass 2 — EDID, among what pass 1 did not claim. A profile may forbid this pass outright:
        // on a panel wired to two adapters at once, one EDID covers two connections and the fallback
        // could steer the record at the one the profile never meant.
        if (strictMatch)
        {
            foreach (var rec in pending)
                notes?.Add($"{rec.Label}: not found by DevicePath (strict matching — EDID fallback disabled)");
            return map;
        }
        foreach (var rec in pending)
        {
            if (string.IsNullOrEmpty(rec.EdidManufacture)) continue;
            var candidates = free.Where(t => string.Equals(Try(() => t.EDIDManufactureCode) ?? "", rec.EdidManufacture, StringComparison.OrdinalIgnoreCase)
                                          && TryVal(() => (int)t.EDIDProductCode) == rec.EdidProduct).ToList();
            if (candidates.Count == 0) continue;

            var pick = candidates[0];
            map[rec] = pick;
            free.Remove(pick);
            string msg = candidates.Count == 1
                ? $"{rec.Label}: matched on EDID {rec.EdidManufacture}/{rec.EdidProduct} (its port changed)"
                : $"{rec.Label}: {candidates.Count} identical monitors match and none is on the expected port — picked one; the two may end up swapped";
            LbLog.Info(Tag, msg);
            notes?.Add(msg);
        }
        return map;
    }

    /// <summary>The attached <see cref="Display"/> for a DevicePath (empty = the current primary).
    /// Displays are what carry GetPossibleSettings / SetSettings.</summary>
    public static Display? ResolveDisplay(string devicePath)
    {
        var all = Displays();

        if (string.IsNullOrEmpty(devicePath))
            return all.FirstOrDefault(d => TryVal(() => d.IsGDIPrimary ? 1 : 0) == 1) ?? all.FirstOrDefault();

        return all.FirstOrDefault(d => string.Equals(Try(() => d.DevicePath) ?? "", devicePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every monitor the machine knows about, for the editor's pickers.</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        PathDisplayTarget[] targets;
        try { targets = PathDisplayTarget.GetDisplayTargets() ?? Array.Empty<PathDisplayTarget>(); }
        catch (Exception ex) { LbLog.Warn(Tag, "Enumerate failed: " + ex.Message); return list; }

        foreach (var t in targets)
        {
            string path = Try(() => t.DevicePath) ?? "";
            if (string.IsNullOrEmpty(path)) continue;
            var disp = ResolveDisplay(path);
            string mode = "";
            if (disp != null)
            {
                var cur = Try(() => disp.CurrentSetting);
                if (cur != null) mode = $"{cur.Resolution.Width}x{cur.Resolution.Height} @ {cur.Frequency} Hz";
            }
            list.Add(new MonitorInfo(
                path,
                Try(() => t.FriendlyName) ?? path,
                Try(() => t.EDIDManufactureCode) ?? "",
                TryVal(() => (int)t.EDIDProductCode),
                Active: disp != null,
                Primary: disp != null && TryVal(() => disp.IsGDIPrimary ? 1 : 0) == 1,
                CurrentMode: mode,
                DisplayName: Try(() => disp?.DisplayName) ?? "",
                Gpu: Try(() => t.Adapter?.ToDisplayAdapter()?.DeviceName) ?? "",
                Connector: ConnectorText(t)));
        }

        return list;
    }

    /// <summary>The three sizes that decide whether a desktop fills its panel, per active path:
    ///   source  — what Windows draws (PathInfo.Resolution)
    ///   target  — the signal the panel receives (SignalInfo.ActiveSize)
    ///   panel   — the panel's own native size (PathDisplayTarget.PreferredResolution)
    /// plus the scaling policy and the desktop-image region when Windows exposes one.
    ///
    /// source != target with Scaling=Identity is the textbook letterbox: the desktop is painted 1:1
    /// inside a bigger signal. target != panel means the PANEL is doing the upscale, which is normal and
    /// invisible — unless the monitor's own OSD is set to 1:1, which no API here can see or change.
    /// Printing all three is the only way to tell those apart from the outside.</summary>
    public static List<string> GeometryReport()
    {
        var lines = new List<string>();
        PathInfo[] paths;
        try { paths = PathInfo.GetActivePaths() ?? Array.Empty<PathInfo>(); }
        catch (Exception ex) { lines.Add("query failed: " + ex.Message); return lines; }

        foreach (var p in paths)
        {
            foreach (var t in p.TargetsInfo ?? Array.Empty<PathTargetInfo>())
            {
                string name = Try(() => t.DisplayTarget?.FriendlyName) ?? "?";
                string src = $"{TryVal(() => p.Resolution.Width)}x{TryVal(() => p.Resolution.Height)}";
                bool hasSig = TryVal(() => t.IsSignalInformationAvailable ? 1 : 0) == 1;
                string tgt = hasSig
                    ? $"{TryVal(() => t.SignalInfo.ActiveSize.Width)}x{TryVal(() => t.SignalInfo.ActiveSize.Height)}"
                    : "<none>";
                string panel = $"{TryVal(() => t.DisplayTarget.PreferredResolution.Width)}x{TryVal(() => t.DisplayTarget.PreferredResolution.Height)}";
                string scal = Name(() => t.Scaling);

                string flag = "";
                if (hasSig && src != tgt) flag = "   <<< SOURCE != TARGET (letterbox if scaling is Identity)";
                else if (tgt != "<none>" && tgt != panel) flag = "   (panel upscales)";

                string hdr = "";
                try
                {
                    var h = HdrControl.Query(t.DisplayTarget);
                    hdr = h.Supported ? (h.Enabled ? " HDR=on" : " HDR=off") : " HDR=n/a";
                }
                catch { }

                lines.Add($"{name,-8} source={src,-11} target={tgt,-11} panel={panel,-11} scaling={scal,-22}{hdr}{flag}");

                if (TryVal(() => t.IsDesktopImageInformationAvailable ? 1 : 0) == 1)
                    lines.Add($"{"",-8}   desktopImage surface={Try(() => t.DesktopImage.MonitorSurfaceSize.ToString()) ?? "?"}"
                              + $" region={Try(() => t.DesktopImage.ImageRegion.ToString()) ?? "?"}"
                              + $" clip={Try(() => t.DesktopImage.ImageClip.ToString()) ?? "?"}");
            }
        }
        return lines;
    }

    /// <summary>DevicePath → the size of the signal each attached panel is currently being fed, paired
    /// with the desktop surface drawn on it. When the two differ and the scaling is Identity, the desktop
    /// is painted 1:1 inside a larger signal — the letterbox. Empty when the query fails.</summary>
    public static Dictionary<string, (Size Source, Size Target)> SourceTargetSizes()
    {
        var map = new Dictionary<string, (Size, Size)>(StringComparer.OrdinalIgnoreCase);
        PathInfo[] paths;
        try { paths = PathInfo.GetActivePaths() ?? Array.Empty<PathInfo>(); }
        catch { return map; }

        foreach (var p in paths)
            foreach (var t in p.TargetsInfo ?? Array.Empty<PathTargetInfo>())
            {
                try
                {
                    string path = t.DisplayTarget?.DevicePath ?? "";
                    if (path.Length == 0 || !t.IsSignalInformationAvailable) continue;
                    map[path] = (p.Resolution, t.SignalInfo.ActiveSize);
                }
                catch { }
            }
        return map;
    }

    /// <summary>The panel's own native resolution, or Size.Empty when unknown.</summary>
    public static Size PanelNative(PathDisplayTarget target)
    {
        try { return target.PreferredResolution; } catch { return Size.Empty; }
    }

    /// <summary>"DisplayPort #1", "HDMI #0" — the physical output, the one thing that tells two identical
    /// panels apart in words a human recognises. From the active path's OutputTechnology; falls back to
    /// the target's connector instance alone when the path is not active.</summary>
    public static string ConnectorText(PathDisplayTarget target)
    {
        try
        {
            string tech = "";
            foreach (var p in PathInfo.GetActivePaths() ?? Array.Empty<PathInfo>())
                foreach (var t in p.TargetsInfo ?? Array.Empty<PathTargetInfo>())
                    if (string.Equals(Try(() => t.DisplayTarget?.DevicePath) ?? "", Try(() => target.DevicePath) ?? "", StringComparison.OrdinalIgnoreCase))
                    { tech = t.OutputTechnology.ToString(); break; }
            tech = tech.Replace("External", "").Replace("Embedded", " (internal)");
            int inst = TryVal(() => target.ConnectorInstance);
            return tech.Length > 0 ? $"{tech} #{inst}" : $"connector #{inst}";
        }
        catch { return ""; }
    }

    // ── mode search ──────────────────────────────────────────────────────────

    /// <summary>The modes a monitor really supports, filtered to 32-bit progressive and de-duplicated
    /// (the raw enumeration repeats entries and includes legacy colour depths).</summary>
    public static List<DisplayPossibleSetting> Modes(Display display)
    {
        string key = Try(() => display.DevicePath) ?? Try(() => display.DisplayName) ?? "";
        if (key.Length > 0 && _modesCache.TryGetValue(key, out var hit)) return hit;
        var list = ModesUncached(display);
        if (key.Length > 0) _modesCache[key] = list;
        return list;
    }

    private static List<DisplayPossibleSetting> ModesUncached(Display display)
    {
        try
        {
            return (display.GetPossibleSettings() ?? Enumerable.Empty<DisplayPossibleSetting>())
                .Where(m => m.ColorDepth == WindowsDisplayAPI.ColorDepth.Depth32Bit && !m.IsInterlaced)
                .GroupBy(m => (m.Resolution.Width, m.Resolution.Height, m.Frequency))
                .Select(g => g.First())
                .OrderBy(m => m.Resolution.Width).ThenBy(m => m.Resolution.Height).ThenBy(m => m.Frequency)
                .ToList();
        }
        catch (Exception ex) { LbLog.Warn(Tag, "GetPossibleSettings failed: " + ex.Message); return new List<DisplayPossibleSetting>(); }
    }

    /// <summary>Pick the mode for a (w, h, Hz) wish, applying the fallback ladder. Null when the
    /// RESOLUTION itself is unsupported — a refresh rate always resolves to something.</summary>
    public static DisplayPossibleSetting? PickMode(Display display, int width, int height, int hz)
    {
        var at = Modes(display).Where(m => m.Resolution.Width == width && m.Resolution.Height == height).ToList();
        if (at.Count == 0)
        {
            LbLog.Warn(Tag, $"{display.DisplayName}: no {width}x{height} mode");
            return null;
        }

        // 1. exact.
        var hit = at.FirstOrDefault(m => m.Frequency == hz);
        // 2. one Hz below — Windows reports a 60 Hz panel as 59 (59951 mHz truncated).
        hit ??= at.Where(m => m.Frequency < hz && m.Frequency >= hz - 1).OrderByDescending(m => m.Frequency).FirstOrDefault();
        // 3. nearest above, then 4. nearest below.
        hit ??= at.Where(m => m.Frequency > hz).OrderBy(m => m.Frequency).FirstOrDefault();
        hit ??= at.Where(m => m.Frequency < hz).OrderByDescending(m => m.Frequency).FirstOrDefault();

        if (hit != null && hit.Frequency != hz)
            LbLog.Info(Tag, $"{display.DisplayName}: {width}x{height}@{hz} unavailable, using {hit.Frequency} Hz");
        return hit;
    }

    /// <summary>What a monitor can actually do with a requested (width, height, Hz), decided BEFORE
    /// anything is sent to Windows.
    ///
    /// A profile is a recording of a past desktop, so nothing guarantees the hardware still agrees: a
    /// monitor can be replaced by a smaller one, a driver can drop modes, an EDID can be read differently
    /// over a KVM. Submitting a mode the panel does not have gets the WHOLE arrangement refused, with a
    /// message that names nothing useful — so the request is reconciled here instead.
    ///
    ///   resolution supported      → the frequency ladder picks the closest rate (see PickMode)
    ///   unsupported, strict       → null: the caller refuses, naming the monitor and the mode
    ///   unsupported, adaptive     → the largest mode that FITS inside the request, else the smallest
    ///                               available; never something bigger than asked for, which would push
    ///                               windows off the desktop the profile described
    ///
    /// The note is non-empty whenever the answer is not exactly what was asked, so callers can report the
    /// substitution rather than let the user discover it on screen.</summary>
    public static (DisplayPossibleSetting? Mode, string Note) ResolveMode(Display display, int width, int height, int hz, bool adapt)
    {
        var modes = Modes(display);
        if (modes.Count == 0) return (null, $"{display.DisplayName}: no display mode could be read");

        // hz <= 0 means "whatever rate suits": keep what the monitor is already running at this
        // resolution, else its highest. A profile that only cares about resolution should not silently
        // drag the refresh rate down to some default.
        if (hz <= 0)
        {
            int atRes = modes.Where(m => m.Resolution.Width == width && m.Resolution.Height == height)
                             .Select(m => m.Frequency).DefaultIfEmpty(0).Max();
            try
            {
                var cur = display.CurrentSetting;
                if (cur != null && cur.Resolution.Width == width && cur.Resolution.Height == height) atRes = cur.Frequency;
            }
            catch { }
            hz = atRes > 0 ? atRes : 60;
        }

        bool resOk = modes.Any(m => m.Resolution.Width == width && m.Resolution.Height == height);
        if (resOk)
        {
            var exact = PickMode(display, width, height, hz);
            if (exact == null) return (null, $"{display.DisplayName}: {width}x{height} became unavailable");
            return (exact, exact.Frequency == hz ? "" : $"{exact.Frequency} Hz instead of {hz} Hz");
        }

        if (!adapt) return (null, $"does not support {width}x{height}");

        var fit = modes.Where(m => m.Resolution.Width <= width && m.Resolution.Height <= height)
                       .OrderByDescending(m => (long)m.Resolution.Width * m.Resolution.Height)
                       .ThenByDescending(m => m.Frequency)
                       .FirstOrDefault()
                  ?? modes.OrderBy(m => (long)m.Resolution.Width * m.Resolution.Height)
                          .ThenByDescending(m => m.Frequency)
                          .FirstOrDefault();
        if (fit == null) return (null, $"does not support {width}x{height}");

        var best = PickMode(display, fit.Resolution.Width, fit.Resolution.Height, hz) ?? fit;
        return (best, $"{best.Resolution.Width}x{best.Resolution.Height}@{best.Frequency} instead of {width}x{height}@{hz}");
    }

    // ── enum-name helpers (records store names, not numbers) ──────────────────

    public static T ParseEnum<T>(string name, T fallback) where T : struct, Enum
        => !string.IsNullOrEmpty(name) && Enum.TryParse<T>(name, out var v) ? v : fallback;

    private static string Name<T>(Func<T> f) where T : struct, Enum
    {
        try { return f().ToString(); } catch { return ""; }
    }

    private static TR? Try<TR>(Func<TR> f) where TR : class
    {
        try { return f(); } catch { return null; }
    }

    private static int TryVal(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }

    private static long TryVal(Func<long> f)
    {
        try { return f(); } catch { return 0; }
    }
}
