// Applying and undoing a monitor profile — the orchestration layer.
//
// ORDER MATTERS and is fixed: restore point → layout → solo → preset → audio. Layout first because it
// can turn monitors on (the preset may target one of them); solo after it (it only ever turns things
// off); preset last among the display parts so a mode override wins over whatever the layout set;
// audio at the end since it can't fail in a way that affects the rest.
//
// THE RESTORE POINT is captured ONCE, on the first Apply after a clean state, and held until Restore().
// Applying profile B while A is active does NOT overwrite it — otherwise "go back" would land on A
// instead of on what the user actually started from. It is also written to the options DB, so a crash
// mid-session still leaves "Restore original layout" able to do its job on the next run.
//
// FOUR RULES inherited from the previous generation (BigBoxProfile / TeknoparrotAutoXinput), each of
// them a scar rather than a precaution:
//   1. never demand an exact refresh rate — see DisplayTargets.PickMode.
//   2. filter modes to 32-bit progressive before searching — same place.
//   3. one restore point, taken before the first change, guarded by a flag — here.
//   4. let the driver settle after a change, and retry a failed restore once — here.
// The old code also needed a heap of LUID rematching; that whole class of problem is gone because
// nothing adapter-scoped is persisted (see MonitorProfile's header).
//
// Every step is independently guarded: a profile whose layout fails still applies its audio, and says so.
//
// HARDWARE THAT DOESN'T MATCH is the profile's own choice (MonitorProfile.AdaptToConnected):
//   strict (default) — a monitor named by the profile but absent right now is REPORTED, not silently
//                      skipped. A layout is a whole-desktop statement, and a half-applied one that looks
//                      like a success is the failure mode we're avoiding.
//   adaptive         — unplugged monitors are dropped, connected-but-unlisted ones are carried over at
//                      their current geometry, and the set is re-anchored so a primary still sits at the
//                      origin. For a desk whose screens come and go, that is the difference between a
//                      profile that works and one that never fires.
// Restore() is always adaptive: it is best-effort, and refusing it would strand the user on the profile.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using WindowsDisplayAPI;
using WindowsDisplayAPI.DisplayConfig;
using WindowsDisplayAPI.Native.DisplayConfig;

namespace LbApiHost.Host.Monitors;

/// <summary>Outcome of an apply/restore: what happened, in words the UI can show as-is.</summary>
internal sealed record ApplyResult(bool Ok, string Message)
{
    public static ApplyResult Fine(string m) => new(true, m);
    public static ApplyResult Bad(string m) => new(false, m);
}

internal static class MonitorProfileApply
{
    private const string Tag = "monitors";

    /// <summary>Let the driver land before anything else touches the desktop. The old code slept a
    /// flat second after every change; 900 ms covers the same ground with the two-phase commit the
    /// library does for us.</summary>
    private const int SettleMs = 900;
    private const int RestoreRetryMs = 2000;
    /// <summary>How long to wait for a monitor-bound audio endpoint to be published after the displays
    /// moved. Paid only when the named device is missing on the first try.</summary>
    private const int AudioSettleMs = 3000;

    private static readonly object _gate = new();
    private static MonitorLayout? _savedLayout;
    private static string _savedAudioDevice = "";
    private static int _savedVolume = -1;
    private static bool _savedVrrKnown, _savedVrrHadEntry;
    private static uint _savedVrrValue;
    private static bool _loaded;

    // No "currently active profile" is tracked, deliberately. Nothing stops the user from rearranging
    // the desktop in Windows' own settings right after applying a profile, so any such flag would be a
    // claim we cannot keep. Picking a profile switches to it; that is the whole contract.

    /// <summary>True when a restore point is held (this session or recovered from a previous one).</summary>
    public const string KeyLaunchDelay = "MonitorLaunchDelay";
    public const int LaunchDelayDefault = 2;

    /// <summary>Seconds a game launch waits after its profile switch, so the emulator starts on a desktop
    /// that has finished changing — a mode set the driver acknowledged can still be settling when the
    /// next process asks Windows what the resolution is. Absent = 2; clamped to 0–60.</summary>
    public static int LaunchDelaySeconds
    {
        get
        {
            try
            {
                var raw = Data.LiteBoxOptionsDb.GetGlobal(KeyLaunchDelay);
                if (string.IsNullOrWhiteSpace(raw)) return LaunchDelayDefault;
                return int.TryParse(raw.Trim(), out var v) ? Math.Clamp(v, 0, 60) : LaunchDelayDefault;
            }
            catch { return LaunchDelayDefault; }
        }
    }

    public static bool CanRestore
    {
        get { lock (_gate) { Load(); return _savedLayout != null || _savedAudioDevice.Length > 0 || _savedVolume >= 0; } }
    }

    /// <summary>What the restore point currently holds, in one line; "" when none is held.
    ///
    /// This state used to be invisible, and invisible state lies. It is taken ONCE — before the first
    /// profile — and persists across restarts so a crash still leaves the desktop recoverable. Both are
    /// wanted, but together they mean a snapshot taken at an unlucky moment (HDR already on from an
    /// earlier experiment, a resolution already changed by hand) becomes a permanent, silent, wrong
    /// definition of "original". Showing it is what makes that recoverable; Forget() is the way out.</summary>
    public static string RestoreSummary()
    {
        lock (_gate)
        {
            Load();
            if (!CanRestoreUnlocked()) return "";

            var bits = new List<string>();
            if (_savedLayout != null)
            {
                bits.Add($"{_savedLayout.Paths.Count} monitor{(_savedLayout.Paths.Count == 1 ? "" : "s")}");
                foreach (var r in _savedLayout.Paths.Where(r => r.Hdr != null))
                    bits.Add($"{r.Label} HDR {HdrControl.Text(r.Hdr)}");
            }
            if (_savedAudioDevice.Length > 0) bits.Add("audio " + _savedAudioDevice);
            if (_savedVolume >= 0) bits.Add($"volume {_savedVolume}%");
            return string.Join(", ", bits);
        }
    }

    /// <summary>Drops the restore point WITHOUT applying it: the next profile switch takes a fresh one
    /// from whatever the desktop looks like then. The escape hatch for a snapshot caught at a bad moment
    /// — arrange the desktop the way you actually call normal, forget, and the next switch records that.</summary>
    public static void Forget()
    {
        lock (_gate)
        {
            Load();
            _savedLayout = null; _savedAudioDevice = ""; _savedVolume = -1;
            Persist();
            LbLog.Info(Tag, "restore point forgotten on request");
        }
    }

    // ── the GAME scope ───────────────────────────────────────────────────────
    //
    // A launch that carries a profile takes its OWN snapshot and puts it back when the game exits. That
    // snapshot is deliberately separate from the one "Restore Original Layout" holds:
    //
    //   * the manual point answers "what did my desktop look like before I started switching profiles",
    //     is taken once, and survives restarts;
    //   * the game point answers "what did it look like a second before this game started", and dies
    //     with the game.
    //
    // Merging them would break both. A game launch would either overwrite the user's idea of "original"
    // with a mid-session state, or — if it deferred to an existing point — put the desktop back to
    // something from hours ago when the game quit. They are different questions, so they are different
    // snapshots, and a game launch never touches the manual one.

    private static MonitorLayout? _gameLayout;
    private static string _gameAudioDevice = "";
    private static int _gameVolume = -1;
    private static bool _gameVrrKnown, _gameVrrHadEntry;
    private static uint _gameVrrValue;
    private static bool _gameScopeHeld;

    /// <summary>True while a game launch is holding its own snapshot.</summary>
    public static bool GameScopeActive { get { lock (_gate) return _gameScopeHeld; } }

    /// <summary>Snapshot the desktop, then apply <paramref name="profile"/> — for the duration of a game.
    /// Re-entrant by design: a nested call keeps the FIRST snapshot, so an add-app launched from inside a
    /// game does not make the outer exit restore an intermediate state.</summary>
    public static ApplyResult BeginGameScope(MonitorProfile profile)
    {
        lock (_gate)
        {
            if (!_gameScopeHeld)
            {
                _gameLayout = DisplayTargets.Capture();
                _gameAudioDevice = AudioEndpoints.CurrentDefault();
                _gameVolume = AudioEndpoints.GetVolume();
                var gvrr = GpuColor.VrrGet();
                _gameVrrKnown = gvrr.Supported; _gameVrrHadEntry = gvrr.HasEntry; _gameVrrValue = gvrr.Value;
                _gameScopeHeld = true;
                LbLog.Info(Tag, $"game scope opened (layout={(_gameLayout != null ? _gameLayout.Paths.Count + " monitors" : "none")}, audio={_gameAudioDevice}, volume={_gameVolume})");
            }
        }
        // Applied OUTSIDE the snapshot lock section above but through the same public entry, so the
        // profile goes on exactly as a manual switch would — including its own restore-point behaviour.
        return Apply(profile);
    }

    /// <summary>Put the desktop back the way the launch found it. No-op when no game scope is open.</summary>
    public static ApplyResult EndGameScope()
    {
        lock (_gate)
        {
            if (!_gameScopeHeld) return ApplyResult.Fine("");
            _gameScopeHeld = false;

            var notes = new List<string>();
            bool ok = true;

            if (_gameLayout != null)
            {
                var r = ApplyLayout(_gameLayout, adapt: true, preset: null, out _);
                if (!r.Ok)
                {
                    LbLog.Warn(Tag, "game-scope restore failed, retrying in " + RestoreRetryMs + " ms");
                    Thread.Sleep(RestoreRetryMs);
                    r = ApplyLayout(_gameLayout, adapt: true, preset: null, out _);
                }
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            if (_gameAudioDevice.Length > 0 || _gameVolume >= 0)
            {
                var r = ApplyAudio(new AudioPreset
                {
                    Device = _gameAudioDevice,
                    Volume = _gameVolume >= 0 ? _gameVolume : null,
                }, displaysMoved: _gameLayout != null);
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            if (_gameVrrKnown)
            {
                var now = GpuColor.VrrGet();
                if (now.Supported && (now.HasEntry != _gameVrrHadEntry || now.Value != _gameVrrValue))
                    notes.Add(GpuColor.VrrRestore(_gameVrrHadEntry, _gameVrrValue) ? "VRR restored" : "VRR restore failed");
            }

            _gameLayout = null; _gameAudioDevice = ""; _gameVolume = -1; _gameVrrKnown = false;
            LbLog.Info(Tag, "game scope closed: " + string.Join(" | ", notes));
            return new ApplyResult(ok, string.Join("\n", notes));
        }
    }

    // ── apply ────────────────────────────────────────────────────────────────

    public static ApplyResult Apply(MonitorProfile profile)
    {
        if (profile == null) return ApplyResult.Bad("No profile.");
        lock (_gate)
        {
            Load();
            TakeRestorePoint();

            var notes = new List<string>();
            bool ok = true;

            bool presetMerged = false;
            if (profile.Layout != null)
            {
                var r = ApplyLayout(profile.Layout, profile.AdaptToConnected, profile.Preset, out presetMerged,
                                    profileExtras: profile.ExtrasPolicy, strictMatch: profile.StrictMatch);
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            if (profile.SoloPrimary)
            {
                var r = ApplySolo();
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            if (profile.DisableMonitors.Count > 0)
            {
                var r = ApplyDisableNamed(profile.DisableMonitors);
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            // Only as its own step when the layout did not already carry it — either there is no layout,
            // or the preset names a monitor the layout does not.
            if (profile.Preset != null && !presetMerged)
            {
                var r = ApplyPreset(profile.Preset, profile.AdaptToConnected, profile.StrictMatch);
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            // The GPU-colour half of a preset that was MERGED into the layout: the merge carries mode and
            // HDR through the path set, but the driver-side settings have no seat there — apply them to
            // the preset's monitor directly, exactly as the standalone preset path would have.
            if (presetMerged && profile.Preset is { } mp2 && (mp2.GpuFormat.Length > 0 || mp2.GpuDepthBpc > 0
                || mp2.GpuDynamicRange.Length > 0 || mp2.GpuVibrance >= 0 || mp2.GpuScaling.Length > 0))
            {
                try
                {
                    var d0 = DisplayTargets.ResolveDisplay(mp2.DevicePath);
                    string live0 = d0 != null ? (Try(() => d0.DevicePath) ?? mp2.DevicePath) : mp2.DevicePath;
                    RememberGpu(mp2.DevicePath, live0);
                    string gnote = GpuColor.Apply(live0, mp2.GpuFormat, mp2.GpuDepthBpc, mp2.GpuDynamicRange, mp2.GpuVibrance);
                    if (mp2.GpuScaling.Length > 0)
                    {
                        string sn = GpuColor.ScalingSet(live0, mp2.GpuScaling);
                        gnote = gnote.Length > 0 ? gnote + ", " + sn : sn;
                    }
                    if (gnote.Length > 0) notes.Add(gnote);
                }
                catch (Exception ex) { LbLog.Warn(Tag, "merged-preset GPU failed: " + ex.Message); }
            }

            // VRR is driver-wide, so it applies once per profile, not per monitor. Its "before" state is
            // recorded by the snapshots (restore point / game scope), which restore it the same way.
            if (profile.Preset is { GpuVrr.Length: > 0 } vp)
            {
                // A restore point recovered from a pre-VRR build has no "before": learn it now, while
                // it is still the before.
                if (!_savedVrrKnown && CanRestoreUnlocked())
                {
                    var was = GpuColor.VrrGet();
                    if (was.Supported) { _savedVrrKnown = true; _savedVrrHadEntry = was.HasEntry; _savedVrrValue = was.Value; }
                }
                uint mode = vp.GpuVrr switch { "off" => 0u, "always" => 2u, _ => 1u };
                notes.Add(GpuColor.VrrSet(mode)
                    ? $"VRR → {vp.GpuVrr} (driver-wide)"
                    : "VRR skipped (no NVIDIA driver)");
            }

            if (profile.Audio != null)
            {
                bool displaysMoved = profile.Layout != null || profile.SoloPrimary || profile.Preset != null;
                var r = ApplyAudio(profile.Audio, displaysMoved);
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            Persist();
            LbLog.Info(Tag, $"applied \"{profile.Name}\": {string.Join(" | ", notes)}");
            return new ApplyResult(ok, notes.Count == 0 ? "Nothing to apply." : string.Join("\n", notes));
        }
    }

    // ── restore ──────────────────────────────────────────────────────────────

    /// <summary>Puts back what was there before the first profile of the session, and drops the
    /// restore point. The layout replay gets one retry — a driver that just refused a change is
    /// often ready two seconds later, and that retry is why the old setup could be trusted.</summary>
    public static ApplyResult Restore()
    {
        lock (_gate)
        {
            Load();
            if (!CanRestoreUnlocked()) return ApplyResult.Fine("Nothing to restore.");

            var notes = new List<string>();
            bool ok = true;

            if (_savedLayout != null)
            {
                // Restore is ALWAYS adaptive: it is a best-effort "put it back", and refusing because a
                // monitor was unplugged in the meantime would strand the user on the profile's layout.
                var r = ApplyLayout(_savedLayout, adapt: true, preset: null, out _);
                if (!r.Ok)
                {
                    LbLog.Warn(Tag, "restore failed, retrying in " + RestoreRetryMs + " ms");
                    Thread.Sleep(RestoreRetryMs);
                    r = ApplyLayout(_savedLayout, adapt: true, preset: null, out _);
                }
                ok &= r.Ok;
                notes.Add("Layout: " + r.Message);
            }

            if (_savedAudioDevice.Length > 0 || _savedVolume >= 0)
            {
                var r = ApplyAudio(new AudioPreset
                {
                    Device = _savedAudioDevice,
                    Volume = _savedVolume >= 0 ? _savedVolume : null,
                }, displaysMoved: _savedLayout != null);
                ok &= r.Ok;
                notes.Add(r.Message);
            }

            if (_savedVrrKnown)
            {
                var now = GpuColor.VrrGet();
                if (now.Supported && (now.HasEntry != _savedVrrHadEntry || now.Value != _savedVrrValue))
                    notes.Add(GpuColor.VrrRestore(_savedVrrHadEntry, _savedVrrValue) ? "VRR restored" : "VRR restore failed");
            }

            _savedLayout = null; _savedAudioDevice = ""; _savedVolume = -1; _savedVrrKnown = false;
            Persist();
            return new ApplyResult(ok, string.Join("\n", notes));
        }
    }

    // ── the parts ────────────────────────────────────────────────────────────

    /// <summary>One monitor on its way into the submitted path set. Position is a WORKING copy: the
    /// stored record is never mutated (the profile in memory may still be saved afterwards).</summary>
    private sealed class Placed
    {
        public LayoutPath Rec = null!;
        public PathDisplayTarget Target = null!;
        /// <summary>Working copies. The stored record is never mutated — the profile in memory may still
        /// be saved afterwards — and the Preset needs to override the mode WITHOUT rewriting the layout.</summary>
        public int X, Y, W, H;
        public ulong FreqMilliHz;
        public bool? Hdr;

        /// <summary>The DevicePath the resolved target has RIGHT NOW — which is not the one the profile
        /// stored whenever the EDID fallback did the matching (a new GPU, a moved cable). Everything
        /// downstream must key on this: looking the monitor back up by the stored path would find nothing
        /// and silently skip its mode and its zoom, on a layout that otherwise applied fine.</summary>
        public string LivePath = "";
        public bool Extra;       // connected but not named by the profile (adaptive mode only)
        public bool Presetted;   // mode comes from the profile's Preset, not from the layout
    }

    /// <summary>Rebuilds live PathInfo objects from the stored records and hands them to Windows.
    /// The records carry no adapter handle, so every source/target is re-resolved here against the
    /// hardware actually present — which is the whole reason a saved layout survives a GPU swap.
    ///
    /// <paramref name="adapt"/> = the profile's "adapt to the monitors actually connected" flag.
    /// STRICT (default): a layout is a whole-desktop statement, so a missing monitor is refused by name
    /// rather than silently producing a different desktop than the one that was saved.
    /// ADAPTIVE: unplugged monitors are dropped from the set, and monitors that ARE connected but absent
    /// from the profile are carried over at their current geometry instead of going dark — because
    /// ApplyPathInfos replaces the WHOLE desktop, "leave them alone" has to be spelled out as paths.</summary>
    private static string ExtrasPolicyOf(string? policy, bool adapt)
        => policy is { Length: > 0 } p ? p : (adapt ? MonitorProfile.ExtrasKeep : MonitorProfile.ExtrasOff);

    private static ApplyResult ApplyLayout(MonitorLayout layout, bool adapt, MonitorPreset? preset, out bool presetMerged,
                                           string? forceAnchorPath = null, string? profileExtras = null,
                                           bool strictMatch = false)
    {
        presetMerged = false;
        var notes = new List<string>();
        var items = new List<Placed>();
        var missing = new List<string>();

        // One assignment pass for the whole set: a per-record lookup would let two identical monitors
        // both fall back onto the same panel. See DisplayTargets.ResolveTargets.
        var resolved = DisplayTargets.ResolveTargets(layout.Paths, notes, strictMatch);
        foreach (var rec in layout.Paths)
        {
            if (!resolved.TryGetValue(rec, out var t)) { missing.Add(rec.Label); continue; }
            items.Add(new Placed { Rec = rec, Target = t, X = rec.X, Y = rec.Y,
                                   W = rec.Width, H = rec.Height, FreqMilliHz = rec.FrequencyMilliHz,
                                   Hdr = rec.Hdr, LivePath = LivePathOf(t, rec.DevicePath) });
        }

        if (missing.Count > 0)
        {
            if (!adapt)
                return ApplyResult.Bad($"Monitor{(missing.Count == 1 ? "" : "s")} not connected: {string.Join(", ", missing)}"
                                       + "  —  turn on \"Adapt to the monitors actually connected\" to apply anyway.");
            notes.Add("skipped, not connected: " + string.Join(", ", missing));
        }
        if (items.Count == 0)
            return ApplyResult.Bad("None of the profile's monitors is connected.");

        // WHICH SCREEN ENDS UP PRIMARY is decided once, here, and drives both the preset's "main monitor"
        // and the re-anchoring below. It cannot be read off the CURRENT desktop: the profile is about to
        // change which screen sits at the origin. And it cannot be the layout's recorded primary either —
        // in adaptive mode that screen may be unplugged, in which case another one inherits the role.
        //
        // A preset asking for "make this monitor the main one" overrides all of that: it is the most
        // explicit statement the profile contains about the origin, so it wins.
        Placed? presetTarget = preset != null && !string.IsNullOrEmpty(preset.DevicePath)
            ? items.FirstOrDefault(i => !i.Extra && string.Equals(i.Rec.DevicePath, preset.DevicePath, StringComparison.OrdinalIgnoreCase))
            : null;

        var anchor = (preset?.MakePrimary == true ? presetTarget : null)
                     ?? (forceAnchorPath != null
                         ? items.FirstOrDefault(i => string.Equals(i.LivePath, forceAnchorPath, StringComparison.OrdinalIgnoreCase))
                         : null)
                     ?? ChooseAnchor(items);

        // MERGE THE PRESET INTO THE LAYOUT rather than running it afterwards. A profile that carries both
        // is making ONE statement about the desktop, so it must reach Windows as one: applying the layout
        // and then overriding a mode meant the screen changed mode twice (two blackouts, two settling
        // delays), and the second change escaped the letterbox verification that lives in this pass.
        // Positions stay as captured — the same thing Windows does when you change one screen's resolution
        // in its own settings.
        //
        // Merged BEFORE the extras are placed, so their overlap test sees the size the screen will really
        // have; and resolved against `anchor`, so "main monitor" means the one this profile is about to
        // make primary, not the one that happens to be primary right now.
        if (preset != null && !preset.IsEmpty)
        {
            var hit = string.IsNullOrEmpty(preset.DevicePath) ? anchor : presetTarget;
            if (hit != null)
            {
                var what = new List<string>();
                if (preset.MakePrimary && ReferenceEquals(hit, anchor)) what.Add("primary");
                if (preset.HasMode)
                {
                    hit.W = preset.Width; hit.H = preset.Height;
                    hit.FreqMilliHz = (ulong)Math.Max(0, preset.Frequency) * 1000UL;   // 0 = any rate
                    what.Add($"{preset.Width}x{preset.Height}" + (preset.Frequency > 0 ? $"@{preset.Frequency}" : ""));
                }
                if (preset.Hdr != null) { hit.Hdr = preset.Hdr; what.Add(preset.Hdr.Value ? "HDR on" : "HDR off"); }
                hit.Presetted = true;
                presetMerged = true;
                notes.Add($"display mode on {hit.Rec.Label}: {string.Join(" + ", what)}");
            }
            // No hit and a NAMED monitor = that screen is not in the layout (unplugged, or simply not part
            // of it). Left unmerged on purpose: Apply then runs the preset on its own, where the adaptive
            // flag decides between "skipped, not connected" and a refusal.
        }

        // PRE-FLIGHT. Every mode about to be submitted is reconciled with what the panel can really do,
        // BEFORE the path set is built — a single unsupported mode gets the whole arrangement refused by
        // Windows, with a message that names neither the monitor nor the mode. Doing it here means the
        // profile either applies, or explains itself.
        var unsupported = new List<string>();
        foreach (var it in items.Where(i => !i.Extra).ToList())
        {
            var display = DisplayTargets.ResolveDisplay(it.LivePath);
            if (display == null) continue;                      // target resolved but no attached display
            int hz = (int)Math.Round(it.FreqMilliHz / 1000.0);
            // The preset's own screen answers to the preset's flag: its mode was chosen from a generic
            // catalogue, against a monitor whose identity this very profile decides, so "adjust if
            // unsupported" is the setting that belongs to it. Layout screens keep the profile's flag —
            // their modes came from a real capture of those exact panels.
            bool allowAdjust = it.Presetted ? (preset?.AdjustToClosest ?? adapt) : adapt;
            var (mode, note) = DisplayTargets.ResolveMode(display, it.W, it.H, hz, allowAdjust);
            if (mode == null) { unsupported.Add($"{it.Rec.Label} {it.W}x{it.H}" + (note.Length > 0 ? $" ({note})" : "")); continue; }
            if (note.Length > 0)
            {
                it.W = mode.Resolution.Width; it.H = mode.Resolution.Height;
                it.FreqMilliHz = (ulong)mode.Frequency * 1000UL;
                notes.Add($"{it.Rec.Label}: {note}");
                LbLog.Info(Tag, $"{it.Rec.Label}: {note}");
            }
        }
        if (unsupported.Count > 0)
            return ApplyResult.Bad("Unsupported display mode: " + string.Join("; ", unsupported)
                + "  —  turn on \"Adjust to the closest supported value\" to fall back instead of refusing.");

        // What happens to monitors the profile does NOT name is its own policy — a separate question
        // from adapt, which is about monitors the profile names being absent. "off" simply leaves them
        // out of the submitted path set, which is how ApplyPathInfos turns a screen off.
        string extras = ExtrasPolicyOf(profileExtras, adapt);
        if (extras != MonitorProfile.ExtrasOff) CarryOverExtras(items, notes, extras);
        Normalize(items, anchor);

        // Source ids are per-adapter desktop surfaces and interchangeable — position and resolution
        // come from the record, so handing them out in order is enough. Build one pool per adapter.
        var pools = new Dictionary<PathDisplayAdapter, Queue<PathDisplaySource>>();
        try
        {
            foreach (var src in PathDisplaySource.GetDisplaySources() ?? Array.Empty<PathDisplaySource>())
            {
                PathDisplayAdapter? ad = null;
                try { ad = src.Adapter; } catch { }
                if (ad == null) continue;
                if (!pools.TryGetValue(ad, out var q)) pools[ad] = q = new Queue<PathDisplaySource>();
                q.Enqueue(src);
            }
        }
        catch (Exception ex) { return ApplyResult.Bad("Cannot enumerate display sources: " + ex.Message); }

        // One PathInfo per desktop SURFACE — that, and only that, is what a duplicate is: several targets
        // sharing one source. Grouping on the captured SourceGroup reproduces exactly what was seen.
        //
        // The key is namespaced by origin: a carried-over extra's group index comes from a DIFFERENT
        // capture than the profile's, so "0" on each side means two unrelated surfaces. Without the
        // prefix they would be merged into one PathInfo and Windows would be told to duplicate two
        // screens that have nothing to do with each other.
        var groups = items
            .Select((x, i) => (Item: x, Key: x.Rec.SourceGroup >= 0 ? (x.Extra ? "x" : "p") + x.Rec.SourceGroup
                                           : x.Rec.CloneGroup.HasValue ? "legacy:" + x.Rec.CloneGroup.Value   // profiles saved before SourceGroup
                                           : "solo:" + i))
            .GroupBy(x => x.Key);

        var infos = new List<PathInfo>();
        foreach (var g in groups)
        {
            var first = g.First().Item;
            PathDisplayAdapter? adapter = null;
            try { adapter = first.Target.Adapter; } catch { }
            if (adapter == null || !pools.TryGetValue(adapter, out var pool) || pool.Count == 0)
                return ApplyResult.Bad($"No free display source for {first.Rec.Label}");
            var source = pool.Dequeue();

            var targets = g.Select(x => BuildTarget(x.Item)).ToArray();

            infos.Add(new PathInfo(
                source,
                new Point(first.X, first.Y),
                new Size(first.W, first.H),
                DisplayTargets.ParseEnum(first.Rec.PixelFormat, DisplayConfigPixelFormat.PixelFormat32Bpp),
                targets,
                first.Rec.CloneGroup ?? 0u));
        }

        // Ask BEFORE committing. This is the same SDC_VALIDATE the apply runs internally, so a refusal
        // here is a refusal there — but asking first turns Windows' opaque "Invalid paths information"
        // into a message that at least names the arrangement being refused.
        try
        {
            if (!PathInfo.ValidatePathInfos(infos, true))
                return ApplyResult.Bad("Windows rejects this arrangement: "
                    + string.Join(", ", items.Select(i => $"{i.Rec.Label} {i.W}x{i.H} at {i.X},{i.Y}")));
        }
        catch (Exception ex) { LbLog.Warn(Tag, "ValidatePathInfos threw: " + ex.Message); }

        try
        {
            // allowChanges: let Windows adjust what it must to make the set valid.
            // saveToDatabase: remember it as this monitor set's configuration, like the Settings app.
            PathInfo.ApplyPathInfos(infos, allowChanges: true, saveToDatabase: true, forceModeEnumeration: false);
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, "ApplyPathInfos failed: " + ex.Message);
            return ApplyResult.Bad("Layout refused by Windows: " + ex.Message);
        }

        Thread.Sleep(SettleMs);
        DisplayTargets.Invalidate();   // the desktop just changed; cached enumerations are stale
        ApplyModes(items);             // pin each monitor's mode (see the letterbox note there)
        ApplyHdr(items);               // after the modes: toggling HDR re-negotiates the colour pipeline
        ApplyGpu(items, notes);        // after HDR: both renegotiate the link, HDR is the bigger change
        ApplyDpi(items);
        notes.Insert(0, $"Layout applied ({infos.Count} desktop{(infos.Count == 1 ? "" : "s")}).");
        return ApplyResult.Fine(string.Join("  ", notes));
    }

    /// <summary>One path target, declared by REFRESH RATE only.
    ///
    /// Not by its full signal, and that is a measured decision rather than a shortcut. The obvious cure
    /// for the letterbox described on ApplyModes would be to hand Windows the exact target signal the
    /// capture recorded — the library can build one, and the reconstruction comes out byte-identical to
    /// the original on this hardware. But PathInfo.ValidatePathInfos REFUSES any set whose targets carry
    /// a signal, identical values or not, and SetDisplayConfig then answers "Invalid paths information".
    /// Verified against a live 3-monitor config: refresh-only validates, signal never does, and neither
    /// the source pairing nor the scaling changes that verdict.
    ///
    /// So the topology is declared here without a target mode, and the exact per-monitor mode is set
    /// afterwards through the DEVMODE path — see ApplyModes.</summary>
    private static PathTargetInfo BuildTarget(Placed item)
    {
        var rec = item.Rec;
        return new PathTargetInfo(
            item.Target,
            item.FreqMilliHz,
            DisplayTargets.ParseEnum(rec.ScanLineOrdering, DisplayConfigScanLineOrdering.Progressive),
            DisplayTargets.ParseEnum(rec.Rotation, DisplayConfigRotation.Identity),
            DisplayTargets.ParseEnum(rec.Scaling, DisplayConfigScaling.Identity),
            isVirtualModeSupported: true);
    }

    /// <summary>Second pass: put each monitor's display mode back in step, the way Windows' own Settings
    /// app does it.
    ///
    /// THE LETTERBOX, measured rather than reasoned about. A path has two sizes — the source (the desktop
    /// Windows draws) and the target (the signal the panel receives). ApplyPathInfos is only told the
    /// source, so Windows picks the target, and it picks the panel's NATIVE mode. Paired with a Scaling of
    /// Identity — which every normal capture records, because at capture time the two agree — the desktop
    /// is then painted 1:1 inside a larger signal: black borders on all four sides, the panel itself
    /// plainly still at full size.
    ///
    /// Reproduced on a live 3-monitor desk, one call apart:
    ///     before                 G27Q source=1920x1080 target=1920x1080   ok
    ///     after ApplyPathInfos   G27Q source=1920x1080 target=2560x1440   letterboxed
    ///     after SetSettings      G27Q source=1920x1080 target=1920x1080   ok
    /// The 1440p panels broke, the 1080p one never did — it has no larger mode to be promoted to.
    ///
    /// Declaring the target signal in the path would be the direct cure, and the library can build one;
    /// but ValidatePathInfos refuses ANY set whose targets carry a signal, byte-identical values included
    /// (also measured). So the mode is re-asserted afterwards through SetSettings, which resolves the
    /// target the way the Settings app does — and which the Preset already uses, so it is the most-worn
    /// path in this module rather than a new one.
    ///
    /// The condition is the MEASUREMENT, not a guess about which monitors are at risk: a screen is touched
    /// when its source mode is off, or when Windows just gave it a signal that does not match its desktop.
    /// Everything else is left alone, so a no-op switch makes nothing blink.</summary>
    private static void ApplyModes(List<Placed> items)
    {
        var sizes = DisplayTargets.SourceTargetSizes();
        bool changed = false;

        foreach (var it in items)
        {
            var rec = it.Rec;
            if (it.Extra || it.W <= 0 || it.H <= 0) continue;              // extras keep what they have
            try
            {
                var display = DisplayTargets.ResolveDisplay(it.LivePath);
                if (display == null) continue;

                int hz = (int)Math.Round(it.FreqMilliHz / 1000.0);
                var cur = display.CurrentSetting;
                bool modeWrong = cur == null || cur.Resolution.Width != it.W
                                 || cur.Resolution.Height != it.H || Math.Abs(cur.Frequency - hz) > 1;

                bool letterboxed = sizes.TryGetValue(it.LivePath, out var st) && st.Source != st.Target;

                if (!modeWrong && !letterboxed) continue;

                var mode = DisplayTargets.PickMode(display, it.W, it.H, hz);
                if (mode == null) { LbLog.Warn(Tag, $"{rec.Label}: no {it.W}x{it.H} mode to pin"); continue; }

                display.SetSettings(new DisplaySetting(mode, new Point(it.X, it.Y)), true);
                LbLog.Info(Tag, $"{rec.Label}: mode pinned to {mode.Resolution.Width}x{mode.Resolution.Height}@{mode.Frequency}"
                                + (letterboxed ? $" (signal was {st.Target.Width}x{st.Target.Height} for a {st.Source.Width}x{st.Source.Height} desktop)" : ""));
                changed = true;
            }
            catch (Exception ex) { LbLog.Warn(Tag, $"{rec.Label}: mode pin failed ({ex.Message})"); }
        }

        if (!changed) return;
        Thread.Sleep(SettleMs);
        DisplayTargets.Invalidate();

        // Say so if a screen is STILL letterboxed: better a line in the log than a user staring at black
        // bars wondering whether the profile applied.
        foreach (var kv in DisplayTargets.SourceTargetSizes())
            if (kv.Value.Source != kv.Value.Target)
                LbLog.Warn(Tag, $"still mismatched after the mode pass: desktop {kv.Value.Source.Width}x{kv.Value.Source.Height}"
                                + $" on a {kv.Value.Target.Width}x{kv.Value.Target.Height} signal");
    }

    /// <summary>The target's current DevicePath, falling back to the stored one if it cannot be read.</summary>
    private static string LivePathOf(PathDisplayTarget target, string stored)
    {
        try { return target.DevicePath ?? stored; } catch { return stored; }
    }

    /// <summary>Adaptive mode: monitors that ARE connected but absent from the profile keep their place
    /// instead of going dark. They are re-declared from their CURRENT geometry — and moved aside only if
    /// they would land on top of the profile's own screens, since the profile's coordinates come from a
    /// desktop that no longer exists and the two systems can collide.</summary>
    /// <summary>Bring the connected-but-unnamed monitors into the path set, placed per the profile's
    /// extras policy: kept where they are (moved only on overlap), or lined up past one edge of the
    /// profile's footprint — stacked in order, so two extras can never collide with anything.</summary>
    private static void CarryOverExtras(List<Placed> items, List<string> notes, string policy)
    {
        MonitorLayout? current;
        try { current = DisplayTargets.Capture(); } catch { return; }
        if (current == null) return;

        bool Named(LayoutPath c) => items.Any(i =>
            string.Equals(i.Rec.DevicePath, c.DevicePath, StringComparison.OrdinalIgnoreCase));

        var extras = current.Paths.Where(c => !Named(c)).ToList();
        if (extras.Count == 0) return;

        // The profile's footprint — the edges extras are stacked against.
        int left = items.Min(i => i.X), right = items.Max(i => i.X + i.W);
        int top = items.Min(i => i.Y), bottom = items.Max(i => i.Y + i.H);
        bool Overlaps(int x, int y, int w, int h) => items.Any(i =>
            x < i.X + i.W && i.X < x + w && y < i.Y + i.H && i.Y < y + h);

        foreach (var c in extras)
        {
            var t = DisplayTargets.ResolveTarget(c);
            if (t == null) continue;          // came from a live capture, so this is theory only

            int x, y;
            switch (policy)
            {
                case MonitorProfile.ExtrasRight: x = right; y = 0; right += c.Width; break;
                case MonitorProfile.ExtrasLeft: left -= c.Width; x = left; y = 0; break;
                case MonitorProfile.ExtrasTop: top -= c.Height; x = 0; y = top; break;
                case MonitorProfile.ExtrasBottom: x = 0; y = bottom; bottom += c.Height; break;
                default:   // keep — where it is, garaged right only if it would land on the profile
                    x = c.X; y = c.Y;
                    if (Overlaps(x, y, c.Width, c.Height)) { x = right; y = 0; right += c.Width; }
                    else right = Math.Max(right, x + c.Width);
                    break;
            }
            items.Add(new Placed { Rec = c, Target = t, X = x, Y = y, Extra = true,
                                   W = c.Width, H = c.Height, FreqMilliHz = c.FrequencyMilliHz, Hdr = c.Hdr,
                                   LivePath = LivePathOf(t, c.DevicePath) });
        }

        var kept = items.Where(i => i.Extra).Select(i => i.Rec.Label).ToList();
        if (kept.Count > 0)
            notes.Add((policy == MonitorProfile.ExtrasKeep ? "left as they are: " : $"placed {policy}: ")
                      + string.Join(", ", kept));
    }

    /// <summary>The screen this layout will make primary — Windows defines the primary as the surface at
    /// the origin, so this is also the translation anchor.
    ///
    /// In order: whoever already sits at (0,0), else the layout's recorded primary, else the surviving
    /// layout screen closest to the old origin. The last two matter in adaptive mode, where the recorded
    /// primary may be unplugged and the role has to be inherited by something.</summary>
    private static Placed? ChooseAnchor(List<Placed> items)
        => items.FirstOrDefault(i => !i.Extra && i.X == 0 && i.Y == 0)
           ?? items.FirstOrDefault(i => !i.Extra && i.Rec.Primary)
           ?? items.Where(i => !i.Extra).OrderBy(i => Math.Abs(i.X) + Math.Abs(i.Y)).FirstOrDefault()
           ?? items.FirstOrDefault();

    /// <summary>Windows wants exactly one desktop surface at the origin. After dropping or adding
    /// monitors the profile's own origin may be gone, so the whole set is translated to put the chosen
    /// anchor back at (0,0).</summary>
    private static void Normalize(List<Placed> items, Placed? anchor)
    {
        if (anchor == null) return;
        int dx = anchor.X, dy = anchor.Y;
        if (dx == 0 && dy == 0) return;
        foreach (var i in items) { i.X -= dx; i.Y -= dy; }
        LbLog.Info(Tag, $"layout re-anchored on {anchor.Rec.Label} (shifted by {-dx},{-dy})");
    }

    /// <summary>Restore each monitor's HDR state, once its mode is final — enabling advanced colour
    /// re-negotiates the colour pipeline, so doing it before the mode would just make the panel blank
    /// twice.
    ///
    /// A record with no captured state is LEFT ALONE: "no information" must never be read as "turn it
    /// off". Support is re-asked of the hardware rather than trusted from the recording, because whether
    /// a panel can do HDR depends on the cable and the current mode — a profile that drops a screen to a
    /// bandwidth-hungry refresh rate can genuinely lose the ability on the way.</summary>
    private static void ApplyHdr(List<Placed> items)
    {
        foreach (var it in items)
        {
            if (it.Extra) continue;                       // extras keep whatever they have
            var want = it.Hdr;
            if (want == null) continue;
            try
            {
                var state = HdrControl.Query(it.Target);
                if (!state.Supported)
                {
                    if (want == true) LbLog.Warn(Tag, $"{it.Rec.Label}: HDR wanted but the panel does not offer it now");
                    continue;
                }
                if (state.Enabled == want.Value) continue;
                RememberHdr(it.Rec.DevicePath, state.Enabled);   // the RECORD's path: that is the key the snapshot uses
                if (HdrControl.Set(it.Target, want.Value))
                    LbLog.Info(Tag, $"{it.Rec.Label}: HDR → {HdrControl.Text(want)}");
            }
            catch (Exception ex) { LbLog.Warn(Tag, $"{it.Rec.Label}: HDR failed ({ex.Message})"); }
        }
    }

    /// <summary>Restore each monitor's GPU-output colour, where it was captured AND the driving GPU
    /// still answers. Vendor-gated per monitor at apply time — the refusal names the GPU, so a profile
    /// captured on NVIDIA and replayed after a card swap says why nothing happened.</summary>
    private static void ApplyGpu(List<Placed> items, List<string> notes)
    {
        foreach (var it in items)
        {
            var rec = it.Rec;
            if (it.Extra) continue;
            if (rec.GpuFormat.Length == 0 && rec.GpuDepthBpc <= 0 && rec.GpuDynamicRange.Length == 0 && rec.GpuVibrance < 0
                && rec.GpuScaling.Length == 0) continue;
            try
            {
                RememberGpu(rec.DevicePath, it.LivePath);
                string note = GpuColor.Apply(it.LivePath, rec.GpuFormat, rec.GpuDepthBpc, rec.GpuDynamicRange, rec.GpuVibrance);
                if (rec.GpuScaling.Length > 0 && !string.Equals(GpuColor.ScalingGet(it.LivePath), rec.GpuScaling, StringComparison.OrdinalIgnoreCase))
                {
                    string sn = GpuColor.ScalingSet(it.LivePath, rec.GpuScaling);
                    note = note.Length > 0 ? note + ", " + sn : sn;
                }
                if (note.Length > 0) { LbLog.Info(Tag, $"{rec.Label}: {note}"); notes.Add($"{rec.Label}: {note}"); }
            }
            catch (Exception ex) { LbLog.Warn(Tag, $"{rec.Label}: GPU output failed ({ex.Message})"); }
        }
    }

    /// <summary>Per-monitor zoom, after the topology has settled — the source objects only exist once
    /// the paths are live. A record with no stored zoom is left alone.</summary>
    private static void ApplyDpi(List<Placed> items)
    {
        PathInfo[] active;
        try { active = PathInfo.GetActivePaths() ?? Array.Empty<PathInfo>(); }
        catch { return; }

        foreach (var it in items)
        {
            var rec = it.Rec;
            if (it.Extra || string.IsNullOrEmpty(rec.DpiScale)) continue;
            var wanted = DisplayTargets.ParseEnum(rec.DpiScale, DisplayConfigSourceDPIScale.Identity);
            foreach (var p in active)
            {
                try
                {
                    if (p.TargetsInfo == null) continue;
                    // Keyed on the LIVE path, not the stored one — see Placed.LivePath.
                    bool mine = p.TargetsInfo.Any(t =>
                        string.Equals(t.DisplayTarget?.DevicePath ?? "", it.LivePath, StringComparison.OrdinalIgnoreCase));
                    if (!mine) continue;
                    var src = p.DisplaySource;
                    if (src == null) continue;
                    if (src.CurrentDPIScale == wanted) break;
                    src.CurrentDPIScale = wanted;
                    LbLog.Info(Tag, $"{rec.Label}: zoom → {wanted}");
                    break;
                }
                catch (Exception ex) { LbLog.Warn(Tag, $"{rec.Label}: zoom failed ({ex.Message})"); break; }
            }
        }
    }

    /// <summary>Turns off every attached display but the primary one.</summary>
    private static ApplySoloResult ApplySoloCore()
    {
        Display[] all;
        try { all = Display.GetDisplays()?.ToArray() ?? Array.Empty<Display>(); }
        catch (Exception ex) { return new ApplySoloResult(false, 0, ex.Message); }

        var others = all.Where(d => { try { return !d.IsGDIPrimary; } catch { return false; } }).ToList();
        if (others.Count == 0) return new ApplySoloResult(true, 0, "");

        int done = 0;
        foreach (var d in others)
        {
            try { d.Disable(true); done++; }
            catch (Exception ex) { LbLog.Warn(Tag, $"disable {d.DisplayName} failed: {ex.Message}"); }
        }
        if (done > 0) { Thread.Sleep(SettleMs); DisplayTargets.Invalidate(); }
        return new ApplySoloResult(done == others.Count, done, done == others.Count ? "" : "some monitors refused to turn off");
    }

    private sealed record ApplySoloResult(bool Ok, int Count, string Error);

    /// <summary>Switch off the profile's NAMED monitors — the precise form of solo. A named monitor that
    /// is not attached is a silent skip: turning off an absent screen is already done.</summary>
    private static ApplyResult ApplyDisableNamed(List<string> devicePaths)
    {
        int done = 0;
        var failed = new List<string>();
        foreach (var path in devicePaths)
        {
            try
            {
                var d = DisplayTargets.ResolveDisplay(path);
                if (d == null) continue;
                if (TryVal(() => d.IsGDIPrimary ? 1 : 0) == 1)
                {
                    // Refusing beats obeying here: turning the PRIMARY off strands the desktop, and a
                    // profile that also moves the primary elsewhere has already done so by this point.
                    failed.Add(d.DisplayName + " (primary — not turned off)");
                    continue;
                }
                d.Disable(true);
                done++;
            }
            catch (Exception ex) { failed.Add(path + " (" + ex.Message + ")"); }
        }
        if (done > 0) { Thread.Sleep(SettleMs); DisplayTargets.Invalidate(); }
        string msg = $"Monitors off: {done}" + (failed.Count > 0 ? "; refused: " + string.Join(", ", failed) : "");
        return failed.Count == 0 ? ApplyResult.Fine(msg) : ApplyResult.Bad(msg);
    }

    private static int TryVal(Func<int> f) { try { return f(); } catch { return 0; } }

    private static ApplyResult ApplySolo()
    {
        var r = ApplySoloCore();
        if (!r.Ok) return ApplyResult.Bad($"Solo primary: {r.Error}");
        return ApplyResult.Fine(r.Count == 0 ? "Solo primary: already alone." : $"Solo primary: {r.Count} monitor(s) off.");
    }

    /// <summary>One monitor's display mode. An empty DevicePath means "whichever is primary now",
    /// which is what makes a plain "main monitor 1080p60" profile portable between machines.</summary>
    private static ApplyResult ApplyPreset(MonitorPreset preset, bool adapt, bool strictMatch = false)
    {
        // Same two-pass identity as a layout: the stored path first, then the EDID pair, so a preset
        // aimed at a NAMED monitor survives a GPU swap exactly like a captured layout does.
        var display = DisplayTargets.ResolveDisplay(preset.DevicePath);
        if (display == null && !string.IsNullOrEmpty(preset.DevicePath) && !string.IsNullOrEmpty(preset.EdidManufacture))
        {
            var byEdid = strictMatch ? new Dictionary<LayoutPath, PathDisplayTarget>() : DisplayTargets.ResolveTargets(
                new[] { new LayoutPath { DevicePath = preset.DevicePath, FriendlyName = preset.FriendlyName,
                                         EdidManufacture = preset.EdidManufacture, EdidProduct = preset.EdidProduct } },
                null);
            if (byEdid.Count > 0)
                display = DisplayTargets.ResolveDisplay(LivePathOf(byEdid.Values.First(), preset.DevicePath));
        }
        if (display == null)
        {
            string who = string.IsNullOrEmpty(preset.FriendlyName) ? preset.DevicePath : preset.FriendlyName;
            // Adaptive: an absent monitor is a skip, not a failure — same contract as the layout.
            return adapt ? ApplyResult.Fine($"Preset: skipped, {who} is not connected")
                         : ApplyResult.Bad($"Preset: monitor not available ({who})");
        }

        var done = new List<string>();

        // A preset may carry only an HDR choice; skip the mode work entirely then.
        DisplayPossibleSetting? mode = null;
        string note = "";
        if (preset.HasMode)
        {
            (mode, note) = DisplayTargets.ResolveMode(display, preset.Width, preset.Height, preset.Frequency, preset.AdjustToClosest);
            if (mode == null)
                return ApplyResult.Bad($"Preset: {display.DisplayName} {note}"
                    + "  —  turn on \"Adjust to the closest supported value\" to fall back instead of refusing.");
            if (note.Length > 0) LbLog.Info(Tag, $"{display.DisplayName}: {note}");
        }

        bool wantsSetting = mode != null || preset.Rotation.Length > 0 || preset.OutputScaling.Length > 0;
        if (wantsSetting)
        {
            try
            {
                // Keep the monitor where it is — a preset changes the MODE, never the desktop arrangement.
                Point pos;
                try { pos = display.CurrentSetting?.Position ?? Point.Empty; } catch { pos = Point.Empty; }

                // Rotation and output scaling ride the same DEVMODE write as the mode. With no mode chosen
                // the current one is re-used, so "rotate only" does not also change the resolution.
                var cur = display.CurrentSetting;
                var baseMode = mode ?? (cur != null ? DisplayTargets.PickMode(display, cur.Resolution.Width, cur.Resolution.Height, cur.Frequency) : null);
                if (baseMode == null) return ApplyResult.Bad($"Preset: {display.DisplayName} has no readable current mode");

                var rot = preset.Rotation.Length > 0
                    ? DisplayTargets.ParseEnum(preset.Rotation, WindowsDisplayAPI.Native.DeviceContext.DisplayOrientation.Identity)
                    : (cur?.Orientation ?? WindowsDisplayAPI.Native.DeviceContext.DisplayOrientation.Identity);
                var fix0 = preset.OutputScaling.Length > 0
                    ? DisplayTargets.ParseEnum(preset.OutputScaling, WindowsDisplayAPI.Native.DeviceContext.DisplayFixedOutput.Default)
                    : (cur?.OutputScalingMode ?? WindowsDisplayAPI.Native.DeviceContext.DisplayFixedOutput.Default);

                display.SetSettings(new DisplaySetting(baseMode, pos, rot, fix0), true);
                mode ??= baseMode;
            }
            catch (Exception ex)
            {
                LbLog.Warn(Tag, "SetSettings failed: " + ex.Message);
                return ApplyResult.Bad("Preset refused by Windows: " + ex.Message);
            }

            Thread.Sleep(SettleMs);
            DisplayTargets.Invalidate();
            done.Add($"{mode!.Resolution.Width}x{mode.Resolution.Height} @ {mode.Frequency} Hz" + (note.Length > 0 ? $" ({note})" : ""));
            if (preset.Rotation is { Length: > 0 } and not "Identity") done.Add(preset.Rotation);
            if (preset.OutputScaling.Length > 0) done.Add("scaling " + preset.OutputScaling.ToLowerInvariant());
        }

        // Zoom last among the display parts — it acts on the SOURCE, which the mode write above may have
        // just re-created.
        if (preset.DpiScale.Length > 0)
        {
            try
            {
                var wanted = DisplayTargets.ParseEnum(preset.DpiScale, WindowsDisplayAPI.Native.DisplayConfig.DisplayConfigSourceDPIScale.Identity);
                string live = Try(() => display.DevicePath) ?? preset.DevicePath;
                bool set = false;
                foreach (var pi in PathInfo.GetActivePaths() ?? Array.Empty<PathInfo>())
                {
                    if (!(pi.TargetsInfo ?? Array.Empty<PathTargetInfo>()).Any(t =>
                        string.Equals(Try(() => t.DisplayTarget?.DevicePath) ?? "", live, StringComparison.OrdinalIgnoreCase))) continue;
                    var src = pi.DisplaySource;
                    if (src != null && src.CurrentDPIScale != wanted) { src.CurrentDPIScale = wanted; set = true; }
                    break;
                }
                done.Add("zoom " + MonitorProfile.ZoomHelp(preset.DpiScale) + (set ? "" : " (already)"));
            }
            catch (Exception ex) { LbLog.Warn(Tag, "preset zoom failed: " + ex.Message); done.Add("zoom failed"); }
        }

        // "Make this the main monitor" with no layout to lean on: take the desktop as it stands and
        // re-anchor it. Going through ApplyLayout rather than nudging one screen means the positions of
        // all the others shift with it, which is what moving the origin actually implies.
        if (preset.MakePrimary)
        {
            try
            {
                string live = Try(() => display.DevicePath) ?? preset.DevicePath;
                var current = DisplayTargets.Capture();
                if (current == null) done.Add("could not read the layout to set the main monitor");
                else if (current.Paths.Any(r => string.Equals(r.DevicePath, live, StringComparison.OrdinalIgnoreCase)
                                                && r.X == 0 && r.Y == 0))
                    done.Add("already the main monitor");
                else
                {
                    var r2 = ApplyLayout(current, adapt: true, preset: null, out _, forceAnchorPath: live);
                    done.Add(r2.Ok ? "set as the main monitor" : "could not be set as the main monitor");
                    if (!r2.Ok) LbLog.Warn(Tag, "make-primary failed: " + r2.Message);
                }
            }
            catch (Exception ex) { LbLog.Warn(Tag, "make-primary failed: " + ex.Message); done.Add("main-monitor change failed"); }
        }

        if (preset.GpuScaling.Length > 0)
        {
            try
            {
                string live1 = Try(() => display.DevicePath) ?? preset.DevicePath;
                RememberGpu(preset.DevicePath, live1);
                string sn = GpuColor.ScalingSet(live1, preset.GpuScaling);
                if (sn.Length > 0) done.Add(sn);
            }
            catch (Exception ex) { done.Add("GPU scaling failed (" + ex.Message + ")"); }
        }

        // GpuVrr is deliberately absent here: driver-wide, it is applied once at the profile level.
        if (preset.GpuFormat.Length > 0 || preset.GpuDepthBpc > 0 || preset.GpuDynamicRange.Length > 0 || preset.GpuVibrance >= 0)
        {
            try
            {
                string live0 = Try(() => display.DevicePath) ?? preset.DevicePath;
                RememberGpu(preset.DevicePath, live0);
                string gnote = GpuColor.Apply(live0, preset.GpuFormat, preset.GpuDepthBpc, preset.GpuDynamicRange, preset.GpuVibrance);
                if (gnote.Length > 0) done.Add(gnote);
            }
            catch (Exception ex) { done.Add("GPU output failed (" + ex.Message + ")"); }
        }

        // HDR last, and after the mode: toggling advanced colour re-negotiates the colour pipeline.
        if (preset.Hdr != null)
        {
            try
            {
                var tgt = display.ToPathDisplayTarget();
                var state = tgt != null ? HdrControl.Query(tgt) : default;
                if (!state.Supported) done.Add("HDR not available on this monitor");
                else if (state.Enabled == preset.Hdr.Value) done.Add(preset.Hdr.Value ? "HDR already on" : "HDR already off");
                else
                {
                    // A preset-only profile carries no layout, so the restore point is the ONLY record of
                    // what this screen looked like. Make sure it knows, before we overwrite it.
                    RememberHdr(Try(() => tgt!.DevicePath) ?? "", state.Enabled);
                    if (HdrControl.Set(tgt!, preset.Hdr.Value)) done.Add(preset.Hdr.Value ? "HDR on" : "HDR off");
                    else done.Add("HDR could not be changed");
                }
            }
            catch (Exception ex) { LbLog.Warn(Tag, "preset HDR failed: " + ex.Message); done.Add("HDR failed"); }
        }

        // Same outcome check the layout pass does: a mode set through SetSettings normally brings the
        // target signal in step with the desktop, but "normally" is not "measured".
        string warn = "";
        try
        {
            if (mode == null) throw new OperationCanceledException();   // HDR-only: no mode to verify
            var sizes = DisplayTargets.SourceTargetSizes();
            string key = Try(() => display.DevicePath) ?? "";
            if (key.Length > 0 && sizes.TryGetValue(key, out var st) && st.Source != st.Target)
            {
                warn = $"  (WARNING: {st.Source.Width}x{st.Source.Height} desktop on a {st.Target.Width}x{st.Target.Height} signal — black borders)";
                LbLog.Warn(Tag, $"{display.DisplayName}: preset left a letterboxed desktop{warn}");
            }
        }
        catch { }

        // A preset can legitimately produce no display-side action here (e.g. VRR only, which the profile
        // level applies) — say nothing rather than print a dangling arrow.
        return done.Count == 0 ? ApplyResult.Fine("")
             : ApplyResult.Fine($"Preset: {display.DisplayName} → {string.Join(", ", done)}" + warn);
    }

    /// <summary>Default playback device and/or master volume — the last step, and the one that may have
    /// to wait for the ones before it.
    ///
    /// <paramref name="displaysMoved"/> says whether this profile touched the displays. When it did, a
    /// missing endpoint is not treated as a verdict: a monitor's own audio device (HDMI, DisplayPort)
    /// only exists while that monitor is active, and Windows publishes it slightly AFTER the topology
    /// settles. So the profile that lights up the TV and sends sound to it — the whole point of pairing
    /// the two in one profile — would otherwise lose the race by a fraction of a second, every time.
    ///
    /// The wait is bounded and only paid on failure, and NOT paid at all when no display moved: there,
    /// a device that is absent is simply absent (a renamed card, a typo) and waiting would just stall
    /// the switch.</summary>
    private static ApplyResult ApplyAudio(AudioPreset audio, bool displaysMoved)
    {
        var notes = new List<string>();
        bool ok = true;

        if (!string.IsNullOrWhiteSpace(audio.Device))
        {
            bool done = AudioEndpoints.SetDefault(audio.Device);
            int waited = -1;

            if (!done && displaysMoved)
            {
                waited = AudioEndpoints.WaitForPlayback(audio.Device, AudioSettleMs);
                if (waited >= 0)
                {
                    LbLog.Info(Tag, $"audio endpoint \"{audio.Device}\" appeared after {waited} ms");
                    done = AudioEndpoints.SetDefault(audio.Device);
                }
            }

            if (done) notes.Add("Audio: " + audio.Device + (waited > 0 ? $" (after {waited} ms)" : ""));
            else { ok = false; notes.Add("Audio: device not found (" + audio.Device + ")"); }
        }

        if (audio.Volume.HasValue)
        {
            // One retry: the endpoint we just made default can still be settling, and the volume call
            // lands on whatever IS default at that instant.
            bool done = AudioEndpoints.SetVolume(audio.Volume.Value);
            if (!done && displaysMoved)
            {
                Thread.Sleep(400);
                done = AudioEndpoints.SetVolume(audio.Volume.Value);
            }
            if (done) notes.Add($"Volume: {audio.Volume}%");
            else { ok = false; notes.Add("Volume: could not be set"); }
        }

        return new ApplyResult(ok, notes.Count == 0 ? "Audio: nothing to do." : string.Join(" | ", notes));
    }

    // ── restore point ────────────────────────────────────────────────────────

    private static TR? Try<TR>(Func<TR> f) where TR : class
    {
        try { return f(); } catch { return null; }
    }

    /// <summary>Teach the restore point a monitor's CURRENT HDR state, right before we change it — but
    /// only when it holds no opinion yet.
    ///
    /// The restore point is a full snapshot taken ONCE, and it outlives the session (it is persisted, so
    /// a crash still leaves the desktop recoverable). Both properties are wanted, and together they make
    /// it possible to hold a snapshot that predates a field this module only learned to record later —
    /// which is exactly how an "HDR on" profile could be undone by a Restore that had never been told
    /// what HDR was. The same would happen to any field added in the future.
    ///
    /// So instead of trusting the snapshot to be complete, every write back-fills what it is about to
    /// overwrite. A value already recorded is never touched: the snapshot's own reading of the world
    /// before the FIRST profile is the one that matters.</summary>
    private static void RememberHdr(string devicePath, bool current)
    {
        var layout = _savedLayout;
        if (layout == null || string.IsNullOrEmpty(devicePath)) return;

        bool learned = false;
        foreach (var r in layout.Paths)
        {
            if (!string.Equals(r.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Hdr != null) continue;
            r.Hdr = current;
            r.HdrSupported = true;
            learned = true;
        }
        if (!learned) return;

        LbLog.Info(Tag, $"restore point learned HDR={HdrControl.Text(current)} for {devicePath}");
        Persist();
    }

    /// <summary>Same contract as <see cref="RememberHdr"/>, for the driver-side monitor settings:
    /// before the first GPU write lands on a monitor, make sure the restore point knows what was
    /// there. A snapshot persisted by a build that predates these fields holds ""/-1 — "no
    /// information" — and restoring it would leave the profile's colours and scaling behind.
    /// A record that already knows ANY of the fields is left alone: it was captured whole.</summary>
    private static void RememberGpu(string storedPath, string livePath)
    {
        var layout = _savedLayout;
        if (layout == null) return;

        bool Match(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        bool learned = false;
        foreach (var r in layout.Paths)
        {
            if (!Match(r.DevicePath, storedPath) && !Match(r.DevicePath, livePath)) continue;
            if (r.GpuFormat.Length > 0 || r.GpuDepthBpc > 0 || r.GpuDynamicRange.Length > 0
                || r.GpuVibrance >= 0 || r.GpuScaling.Length > 0) continue;
            var g = GpuColor.Query(livePath);
            if (!g.Supported) continue;
            r.GpuFormat = g.Format; r.GpuDepthBpc = g.DepthBpc; r.GpuDynamicRange = g.DynamicRange;
            r.GpuVibrance = g.Vibrance; r.GpuScaling = GpuColor.ScalingGet(livePath);
            learned = true;
        }
        if (!learned) return;

        LbLog.Info(Tag, $"restore point learned the GPU output for {livePath}");
        Persist();
    }

    private static bool CanRestoreUnlocked() => _savedLayout != null || _savedAudioDevice.Length > 0 || _savedVolume >= 0;

    /// <summary>Captures the current state — ONCE. A second Apply while a profile is active keeps the
    /// original point, so "restore" always means "back to where I started", not "back to the previous
    /// profile".</summary>
    private static void TakeRestorePoint()
    {
        if (CanRestoreUnlocked()) return;
        _savedLayout = DisplayTargets.Capture();
        _savedAudioDevice = AudioEndpoints.CurrentDefault();
        _savedVolume = AudioEndpoints.GetVolume();
        var vrr = GpuColor.VrrGet();
        _savedVrrKnown = vrr.Supported; _savedVrrHadEntry = vrr.HasEntry; _savedVrrValue = vrr.Value;
        LbLog.Info(Tag, $"restore point taken (layout={(_savedLayout != null ? _savedLayout.Paths.Count + " monitors" : "none")}, audio={_savedAudioDevice}, volume={_savedVolume})");
    }

    // ── persistence of the live state (crash recovery) ───────────────────────

    private sealed class SavedState
    {
        public MonitorLayout? Layout { get; set; }
        public string AudioDevice { get; set; } = "";
        public int Volume { get; set; } = -1;
        public bool VrrKnown { get; set; }
        public bool VrrHadEntry { get; set; }
        public uint VrrValue { get; set; }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var s = LiteBoxOptionsDb.GetJson<SavedState>(LiteBoxOptionsDb.Global, "", "MonitorRestorePoint");
            if (s == null) return;
            _savedLayout = s.Layout;
            _savedAudioDevice = s.AudioDevice ?? "";
            _savedVolume = s.Volume;
            _savedVrrKnown = s.VrrKnown; _savedVrrHadEntry = s.VrrHadEntry; _savedVrrValue = s.VrrValue;
            if (CanRestoreUnlocked())
                LbLog.Info(Tag, "recovered a restore point from a previous session");
        }
        catch (Exception ex) { LbLog.Warn(Tag, "restore point load failed: " + ex.Message); }
    }

    private static void Persist()
    {
        try
        {
            if (!CanRestoreUnlocked())
            {
                LiteBoxOptionsDb.SetJson<SavedState>(LiteBoxOptionsDb.Global, "", "MonitorRestorePoint", null);
                return;
            }
            LiteBoxOptionsDb.SetJson(LiteBoxOptionsDb.Global, "", "MonitorRestorePoint", new SavedState
            {
                Layout = _savedLayout,
                AudioDevice = _savedAudioDevice,
                Volume = _savedVolume,
                VrrKnown = _savedVrrKnown,
                VrrHadEntry = _savedVrrHadEntry,
                VrrValue = _savedVrrValue,
            });
        }
        catch (Exception ex) { LbLog.Warn(Tag, "restore point save failed: " + ex.Message); }
    }
}
