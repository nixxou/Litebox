// Command-line driver for the Monitor Profiles module — `LiteBox.exe --monitor <command>`.
//
// Two jobs. The first is diagnosis: `probe` prints what the module SEES — identities, the layout a
// capture would store, the modes on offer, the audio endpoints, and the three sizes that decide whether
// a desktop fills its panel. The second is DRIVING it: apply, restore, capture, flip HDR, without going
// through the window. That half exists because most of this module's bugs only appear on a real desk
// with real panels, and "click this, then tell me what you see" is a slow and lossy way to find them.
//
// Everything runs against the DEPLOYED install's own database (AppContext.BaseDirectory), so it acts on
// the same profiles the app shows — this is not a sandbox.
//
// probe / state / list change nothing. The others deliberately move the desktop, and each one prints the
// resulting geometry afterwards, so an effect is measured rather than assumed.

#nullable enable

using System;
using System.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Monitors;

internal static class MonitorProbe
{
    public static int Run(string[] args)
    {
        LiteBoxOptionsDb.Open();

        string cmd = Arg(args, 0)?.ToLowerInvariant() ?? "probe";
        return cmd switch
        {
            "probe" => Probe(),
            "state" => State(),
            "list" => List(),
            "apply" => Apply(Arg(args, 1)),
            "restore" => Restore(),
            "forget" => Forget(),
            "capture" => Capture(Arg(args, 1)),
            "hdr" => Hdr(Arg(args, 1), Arg(args, 2)),
            "gpu" => Gpu(),
            "audio" => Audio(Arg(args, 1)),
            "volume" => Volume(Arg(args, 1)),
            _ => Usage(cmd),
        };
    }

    /// <summary>The n-th token after --monitor (or --monitor-probe), or null.</summary>
    private static string? Arg(string[] args, int n)
    {
        int i = Array.FindIndex(args, a => a.StartsWith("--monitor", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        int want = i + 1 + n;
        return want < args.Length && !args[want].StartsWith("--") ? args[want] : null;
    }

    private static int Usage(string bad)
    {
        Console.WriteLine($"unknown command: {bad}");
        Console.WriteLine("usage: LiteBox.exe --monitor <command>");
        Console.WriteLine("  probe                 full read-only report (default)");
        Console.WriteLine("  state                 restore point + saved profiles");
        Console.WriteLine("  list                  profile names only");
        Console.WriteLine("  apply <name>          apply a profile (name or unique prefix) - MOVES THE DESKTOP");
        Console.WriteLine("  restore               restore the saved original            - MOVES THE DESKTOP");
        Console.WriteLine("  forget                drop the restore point (changes nothing on screen)");
        Console.WriteLine("  capture <name>        (re)capture the current layout into that profile");
        Console.WriteLine("  hdr on|off [monitor]  set HDR directly; default every HDR-capable monitor");
        Console.WriteLine("  gpu                   read every monitor's GPU-output state (vendor, format, range, vibrance)");
        Console.WriteLine("  audio <name>          make that playback device the default (substring match)");
        Console.WriteLine("  volume <0-100>        set the default device's master volume");
        return 2;
    }

    // ── read-only ────────────────────────────────────────────────────────────

    private static int Probe()
    {
        Console.WriteLine("=== monitors ===");
        var monitors = DisplayTargets.Enumerate();
        if (monitors.Count == 0) Console.WriteLine("  (none - the display query returned nothing)");
        foreach (var m in monitors)
        {
            Console.WriteLine($"  {m.FriendlyName}{(m.Primary ? "  [primary]" : "")}{(m.Active ? "" : "  [not attached]")}");
            Console.WriteLine($"      EDID       {m.EdidManufacture}/{m.EdidProduct}");
            Console.WriteLine($"      DevicePath {m.DevicePath}");
            if (m.CurrentMode.Length > 0) Console.WriteLine($"      mode       {m.CurrentMode}");
        }

        Console.WriteLine();
        Console.WriteLine("=== layout Capture() would store ===");
        var layout = DisplayTargets.Capture();
        if (layout == null) Console.WriteLine("  (capture failed)");
        else
            foreach (var r in layout.Paths)
                Console.WriteLine($"  {r.Label,-12} {r.Width}x{r.Height} @ {r.RefreshText}  pos=({r.X},{r.Y})"
                                  + $"  rot={r.Rotation} scal={r.Scaling} fmt={r.PixelFormat}"
                                  + $"  zoom={r.ZoomText}"
                                  + $"  HDR={(r.HdrSupported ? HdrControl.Text(r.Hdr) : "n/a")}"
                                  + (r.ColorEncoding.Length > 0 ? $"  {r.ColorEncoding} {r.BitsPerChannel}bpc" : "")
                                  + (r.OutputScaling is not ("" or "Default") ? $"  fixout={r.OutputScaling}" : "")
                                  + (r.GpuFormat.Length > 0 ? $"  gpu[{r.GpuFormat} {r.GpuDepthBpc}bpc {r.GpuDynamicRange}{(r.GpuVibrance >= 0 ? " vib" + r.GpuVibrance : "")}]" : "")
                                  + $"  surface={r.SourceGroup}"
                                  + (r.Primary ? "  primary" : ""));

        Console.WriteLine();
        Console.WriteLine("=== geometry: source vs target vs panel  (run this WHILE a border is visible) ===");
        foreach (var line in DisplayTargets.GeometryReport()) Console.WriteLine("  " + line);

        Console.WriteLine();
        Console.WriteLine("=== modes offered per monitor (32-bit, progressive, de-duplicated) ===");
        foreach (var m in monitors.Where(x => x.Active))
        {
            var d = DisplayTargets.ResolveDisplay(m.DevicePath);
            if (d == null) continue;
            var modes = DisplayTargets.Modes(d);
            Console.WriteLine($"  {m.FriendlyName}: {modes.Count} mode(s)");
            foreach (var g in modes.GroupBy(x => (x.Resolution.Width, x.Resolution.Height))
                                   .OrderByDescending(g => g.Key.Width).ThenByDescending(g => g.Key.Height)
                                   .Take(6))
                Console.WriteLine($"      {g.Key.Width}x{g.Key.Height}   {string.Join(", ", g.Select(x => x.Frequency + "Hz"))}");
        }

        Console.WriteLine();
        Console.WriteLine("=== audio ===");
        Console.WriteLine($"  default : {AudioEndpoints.CurrentDefault()}");
        Console.WriteLine($"  volume  : {AudioEndpoints.GetVolume()}%");
        foreach (var d in AudioEndpoints.Playback()) Console.WriteLine("  device  : " + d);

        Console.WriteLine();
        return State();
    }

    private static int State()
    {
        Console.WriteLine("=== restore point ===");
        var held = MonitorProfileApply.RestoreSummary();
        Console.WriteLine("  " + (held.Length > 0 ? held : "<none held>"));

        Console.WriteLine("=== profiles ===");
        var profiles = MonitorProfileStore.All();
        if (profiles.Count == 0) Console.WriteLine("  (none saved)");
        foreach (var p in profiles)
        {
            Console.WriteLine($"  {p.Name}: {p.Summary()}");
            foreach (var r in p.Layout?.Paths ?? Enumerable.Empty<LayoutPath>())
                Console.WriteLine($"      {r.Label,-10} {r.Width}x{r.Height} @ {r.RefreshText}  at {r.X},{r.Y}"
                                  + $"  zoom {r.ZoomText}  HDR {HdrControl.Text(r.Hdr)}  surface={r.SourceGroup}");
            if (p.Preset != null) Console.WriteLine($"      preset: {p.Preset.Describe()}");
            if (p.Audio != null) Console.WriteLine($"      audio:  {p.Audio.Describe()}");
            if (p.AdaptToConnected) Console.WriteLine("      adaptive");
        }
        return 0;
    }

    private static int List()
    {
        foreach (var p in MonitorProfileStore.All()) Console.WriteLine(p.Name);
        return 0;
    }

    // ── driving ──────────────────────────────────────────────────────────────

    private static MonitorProfile? Find(string? name)
    {
        var all = MonitorProfileStore.All();
        if (string.IsNullOrWhiteSpace(name)) { Console.WriteLine("a profile name is required"); return null; }

        var hit = all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) Console.WriteLine($"no profile matching \"{name}\" (have: {string.Join(", ", all.Select(p => p.Name))})");
        return hit;
    }

    private static int Apply(string? name)
    {
        var p = Find(name);
        if (p == null) return 1;
        Console.WriteLine($"applying \"{p.Name}\" - {p.Summary()}");
        var res = MonitorProfileApply.Apply(p);
        Console.WriteLine((res.Ok ? "OK: " : "FAILED: ") + res.Message.Replace("\n", "\n       "));
        Console.WriteLine();
        return AfterMath(res.Ok);
    }

    private static int Restore()
    {
        var held = MonitorProfileApply.RestoreSummary();
        Console.WriteLine("restoring: " + (held.Length > 0 ? held : "<nothing held>"));
        var res = MonitorProfileApply.Restore();
        Console.WriteLine((res.Ok ? "OK: " : "FAILED: ") + res.Message.Replace("\n", "\n       "));
        Console.WriteLine();
        return AfterMath(res.Ok);
    }

    private static int Forget()
    {
        var held = MonitorProfileApply.RestoreSummary();
        Console.WriteLine("was holding: " + (held.Length > 0 ? held : "<nothing>"));
        MonitorProfileApply.Forget();
        Console.WriteLine("forgotten.");
        return 0;
    }

    private static int Capture(string? name)
    {
        var p = Find(name);
        if (p == null) return 1;
        var layout = DisplayTargets.Capture();
        if (layout == null) { Console.WriteLine("capture failed"); return 1; }

        var all = MonitorProfileStore.All();
        var target = all.First(x => x.Id == p.Id);
        target.Layout = layout;
        MonitorProfileStore.Save(all);
        Console.WriteLine($"captured into \"{target.Name}\":");
        foreach (var r in layout.Paths)
            Console.WriteLine($"   {r.Label,-10} {r.Width}x{r.Height} @ {r.RefreshText}  zoom {r.ZoomText}"
                              + $"  HDR {(r.HdrSupported ? HdrControl.Text(r.Hdr) : "n/a")}  surface={r.SourceGroup}");
        return 0;
    }

    /// <summary>Read-only: each monitor's GPU-output state through the vendor facade — the quickest way
    /// to see the per-monitor vendor gate answer on a mixed machine.</summary>
    private static int Gpu()
    {
        var vrr = GpuColor.VrrGet();
        Console.WriteLine(vrr.Supported
            ? "  VRR (driver-wide): " + (vrr.HasEntry ? vrr.Value switch { 0u => "off", 2u => "fullscreen and windowed", _ => "fullscreen only" } : "driver default (no explicit entry)")
            : "  VRR: no NVIDIA driver");
        foreach (var m in DisplayTargets.Enumerate().Where(x => x.Active))
        {
            var g = GpuColor.Query(m.DevicePath);
            Console.WriteLine(g.Supported
                ? $"  {m.FriendlyName,-10} {g.Vendor,-8} {g.Format} {g.DepthBpc}bpc range={g.DynamicRange}"
                  + $" scaling={GpuColor.ScalingLabel(GpuColor.ScalingGet(m.DevicePath))}"
                  + (g.Vibrance >= 0 ? $" vibrance={g.Vibrance}" : "")
                : $"  {m.FriendlyName,-10} {(g.Vendor.Length > 0 ? g.Vendor : "?"),-8} not supported");
        }
        return 0;
    }

    private static int Hdr(string? onOff, string? monitor)
    {
        if (onOff is not ("on" or "off")) { Console.WriteLine("usage: --monitor hdr on|off [monitor name]"); return 2; }
        bool want = onOff == "on";

        int touched = 0;
        foreach (var t in WindowsDisplayAPI.DisplayConfig.PathDisplayTarget.GetDisplayTargets())
        {
            string label;
            try { label = t.FriendlyName ?? ""; } catch { continue; }
            if (!string.IsNullOrWhiteSpace(monitor)
                && label.IndexOf(monitor, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var state = HdrControl.Query(t);
            if (!state.Supported) { Console.WriteLine($"  {label,-10} HDR not supported"); continue; }
            if (state.Enabled == want) { Console.WriteLine($"  {label,-10} already {onOff}"); touched++; continue; }
            Console.WriteLine($"  {label,-10} {HdrControl.Text(state.Enabled)} -> {onOff}: "
                              + (HdrControl.Set(t, want) ? "OK" : "REFUSED"));
            touched++;
        }
        if (touched == 0) Console.WriteLine("  no matching HDR-capable monitor");
        return 0;
    }

    private static int Audio(string? name)
    {
        var devices = AudioEndpoints.Playback();
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("current default: " + AudioEndpoints.CurrentDefault());
            foreach (var d in devices) Console.WriteLine("  " + d);
            return 0;
        }

        var hit = devices.FirstOrDefault(d => d.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        if (hit == null) { Console.WriteLine($"no playback device matching \"{name}\""); return 1; }

        Console.WriteLine($"was: {AudioEndpoints.CurrentDefault()}");
        bool ok = AudioEndpoints.SetDefault(hit);
        Console.WriteLine((ok ? "set: " : "FAILED to set: ") + hit);
        Console.WriteLine("now: " + AudioEndpoints.CurrentDefault());
        return ok ? 0 : 1;
    }

    private static int Volume(string? value)
    {
        if (!int.TryParse(value, out int v)) { Console.WriteLine("usage: --monitor volume <0-100>"); return 2; }
        Console.WriteLine($"was: {AudioEndpoints.GetVolume()}%");
        bool ok = AudioEndpoints.SetVolume(v);
        Console.WriteLine((ok ? "set" : "FAILED") + $"; now: {AudioEndpoints.GetVolume()}%");
        return ok ? 0 : 1;
    }

    /// <summary>After anything that moved the desktop: print the geometry and the restore point, so the
    /// effect is measured rather than assumed. Most of the bugs in this module were "it should have worked".</summary>
    private static int AfterMath(bool ok)
    {
        DisplayTargets.Invalidate();
        Console.WriteLine("=== resulting geometry ===");
        foreach (var line in DisplayTargets.GeometryReport()) Console.WriteLine("  " + line);
        Console.WriteLine("=== restore point now ===");
        var held = MonitorProfileApply.RestoreSummary();
        Console.WriteLine("  " + (held.Length > 0 ? held : "<none held>"));
        return ok ? 0 : 1;
    }
}
