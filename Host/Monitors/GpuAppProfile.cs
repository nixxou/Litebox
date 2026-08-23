// Transient per-executable NVIDIA driver profiles — the "Program Settings" tab, driven by a launch.
//
// Some driver settings only exist PER APPLICATION (VRRApplicationOverride — per-app G-Sync — being the
// one that matters here; the driver-wide VRRMode is a much blunter tool). The lifecycle is the user's
// own idea and it is the right one: create a DRS profile for the emulator's exe just before the spawn,
// and tear it down — because DRS settings are read once, when the process initialises its graphics
// context, the teardown does not need to wait for the game to EXIT. It only needs to wait for the game
// to have STARTED. So the profile lives for seconds, not hours, and the crash window shrinks to almost
// nothing.
//
// TWO CASES, and the difference is sacred:
//   * the exe has NO profile → we create one ("LiteBox — <exe>") and DELETE it on release. Ours.
//   * the exe ALREADY has one (the user's own retroarch.exe entry in the NVIDIA panel) → we modify THAT
//     profile's settings, remember what each was (present + value, or absent), and put exactly that
//     back. Never Delete on a profile we did not create.
//
// CRASH SWEEP. The pending state is persisted (options db) before the driver is touched, and swept at
// boot: if LiteBox died inside the window, the next start undoes whatever was left. The early release is
// the first line of defence; the sweep is the backstop.

#nullable enable

using System;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Monitors;

internal static class GpuAppProfile
{
    private const string Tag = "monitors";
    public const string MarkerKey = "MonitorDrsTransient";

    /// <summary>What Begin did, persisted verbatim so a crash can be undone at the next boot.</summary>
    internal sealed class Pending
    {
        public string Exe { get; set; } = "";
        public string ProfileName { get; set; } = "";
        /// <summary>True = the profile is ours, release deletes it. False = it existed, release restores.</summary>
        public bool Created { get; set; }
        public bool PriorHadVrr { get; set; }
        public uint PriorVrr { get; set; }
    }

    private static readonly object _gate = new();
    private static Pending? _active;

    /// <summary>Arm the per-app override for one executable ("retroarch.exe"). Idempotent per launch:
    /// a second Begin while one is pending releases the first. Returns a note for the launch log.</summary>
    public static string Begin(string exeFileName, uint vrrOverride)
    {
        if (string.IsNullOrWhiteSpace(exeFileName)) return "";
        lock (_gate)
        {
            if (_active != null) ReleaseLocked();   // stale from a same-session launch that never released

            try
            {
                using var session = NvAPIWrapper.DRS.DriverSettingsSession.CreateAndLoad();

                NvAPIWrapper.DRS.DriverSettingsProfile? prof = null;
                try { prof = session.FindApplicationProfile(exeFileName); } catch { }

                var pending = new Pending { Exe = exeFileName };
                if (prof == null)
                {
                    pending.Created = true;
                    pending.ProfileName = "LiteBox — " + exeFileName;
                    // A leftover with our name (an unswept crash predating the marker) is ours to reuse.
                    try { prof = session.FindProfileByName(pending.ProfileName); } catch { }
                    if (prof == null)
                    {
                        prof = NvAPIWrapper.DRS.DriverSettingsProfile.CreateProfile(session, pending.ProfileName, null);
                        NvAPIWrapper.DRS.ProfileApplication.CreateApplication(prof, exeFileName, null, null, null, false, null);
                    }
                }
                else
                {
                    pending.ProfileName = prof.Name;
                    try
                    {
                        var st = prof.GetSetting(NvAPIWrapper.DRS.KnownSettingId.VRRApplicationOverride);
                        pending.PriorHadVrr = true;
                        pending.PriorVrr = Convert.ToUInt32(st.CurrentValue);
                    }
                    catch { pending.PriorHadVrr = false; }
                }

                // The marker goes to disk BEFORE the driver write: a crash between the two leaves a
                // no-op sweep, the opposite order would leave an untracked change.
                try { LiteBoxOptionsDb.SetJson(LiteBoxOptionsDb.Global, "", MarkerKey, pending); } catch { }

                prof.SetSetting(NvAPIWrapper.DRS.KnownSettingId.VRRApplicationOverride, vrrOverride);
                session.Save();
                _active = pending;

                string what = vrrOverride switch { 1 => "force off", 4 => "fixed refresh", 0 => "allow", _ => vrrOverride.ToString() };
                LbLog.Info(Tag, $"per-app VRR {what} for {exeFileName} ({(pending.Created ? "transient profile" : "existing profile, prior recorded")})");
                return $"per-app VRR {what} for {exeFileName}";
            }
            catch (Exception ex)
            {
                LbLog.Warn(Tag, "per-app profile failed: " + ex.Message);
                try { LiteBoxOptionsDb.SetJson<Pending>(LiteBoxOptionsDb.Global, "", MarkerKey, null); } catch { }
                _active = null;
                return "per-app VRR skipped (" + ex.Message + ")";
            }
        }
    }

    /// <summary>Undo Begin — delete our profile, or put the user's back the way it was. Safe to call
    /// twice (the second is a no-op); called BOTH by the early release once the game is confirmed
    /// running AND by the game-exit path, whichever comes first.</summary>
    public static void Release()
    {
        lock (_gate) ReleaseLocked();
    }

    private static void ReleaseLocked()
    {
        var p = _active;
        _active = null;
        if (p == null) return;
        try
        {
            Undo(p);
            LiteBoxOptionsDb.SetJson<Pending>(LiteBoxOptionsDb.Global, "", MarkerKey, null);
        }
        catch (Exception ex) { LbLog.Warn(Tag, "per-app profile release failed: " + ex.Message); }
    }

    /// <summary>Boot-time backstop: undo whatever a crashed session left behind. The early release makes
    /// this rare — the profile only exists between the spawn and the game's first frames.</summary>
    public static void SweepOrphans()
    {
        try
        {
            var p = LiteBoxOptionsDb.GetJson<Pending>(LiteBoxOptionsDb.Global, "", MarkerKey);
            if (p == null) return;
            LbLog.Info(Tag, $"sweeping a per-app driver profile left by a previous session ({p.Exe})");
            Undo(p);
            LiteBoxOptionsDb.SetJson<Pending>(LiteBoxOptionsDb.Global, "", MarkerKey, null);
        }
        catch (Exception ex) { LbLog.Warn(Tag, "per-app profile sweep failed: " + ex.Message); }
    }

    private static void Undo(Pending p)
    {
        using var session = NvAPIWrapper.DRS.DriverSettingsSession.CreateAndLoad();
        if (p.Created)
        {
            NvAPIWrapper.DRS.DriverSettingsProfile? prof = null;
            try { prof = session.FindProfileByName(p.ProfileName); } catch { }
            if (prof == null) return;   // already gone
            prof.Delete();
            session.Save();
            LbLog.Info(Tag, $"transient driver profile removed ({p.Exe})");
        }
        else
        {
            NvAPIWrapper.DRS.DriverSettingsProfile? prof = null;
            try { prof = session.FindProfileByName(p.ProfileName); } catch { }
            if (prof == null) return;   // the user deleted their own profile meanwhile — nothing to restore
            if (p.PriorHadVrr) prof.SetSetting(NvAPIWrapper.DRS.KnownSettingId.VRRApplicationOverride, p.PriorVrr);
            else prof.DeleteSetting(NvAPIWrapper.DRS.KnownSettingId.VRRApplicationOverride);
            session.Save();
            LbLog.Info(Tag, $"user profile restored ({p.Exe})");
        }
    }
}
