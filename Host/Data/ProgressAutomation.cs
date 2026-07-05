// LaunchBox's "Automatic Progress Tracking" engine, replicated over LiteBox's data.
//
// Rule precedence (most specific wins — the reverse of the options page's reading order):
//   Mastered (every achievement, hardcore)  >  Completed (every achievement, softcore)
//   >  Beaten hardcore  >  Beaten softcore  >  has ≥1 achievement  >  playtime ≥ threshold
//   >  not-started; then the "In Progress but inactive for N days" demotion to Paused.
//
// A game is only touched while AUTOMATION OWNS its value: current Progress is empty, equals one of
// the rule TARGET values, or is listed in AutoProgressIncludedValues — a manually-set value outside
// that set is never clobbered. A blank rule target disables that rule (LB's "leave blank to skip").
//
// Achievement facts come from LiteBox's RetroAchievements cache (Host/Ra — offline read; a game
// without a resolved raid or cached progress simply skips the RA rules). Runs after every game exit
// for that game, and as a full-library sweep shortly after boot (both gated on the master switch).

#nullable enable

using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Ra;

namespace LbApiHost.Host.Data;

internal static class ProgressAutomation
{
    /// <summary>Re-evaluates ONE game (call after its launch ends — play time just changed).</summary>
    public static void ApplyToGame(IGame? g)
    {
        try
        {
            if (g == null) return;
            var c = Cfg.Load();
            if (c == null) return;
            string? v = Compute(g, c, cachedRaids: null);   // single game → the per-raid Exists probe is fine
            if (v == null) return;
            g.Progress = v;
            Console.WriteLine($"[progress] \"{SafeStr(() => g.Title)}\" → \"{v}\"");
        }
        catch (Exception ex) { Console.WriteLine("[progress] apply: " + ex.Message); }
    }

    /// <summary>Full-library pass on a background thread (boot / after the options change).</summary>
    public static void SweepAsync()
    {
        Task.Run(() =>
        {
            try
            {
                var c = Cfg.Load();
                if (c == null) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // One directory listing instead of a File.Exists per raid-bearing game — on a fully
                // RA-resolved 100K library the per-game probes alone cost seconds.
                var cachedRaids = RaService.CachedRaids();
                int changed = 0, total = 0;
                foreach (var g in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
                {
                    if (g == null) continue;
                    total++;
                    string? v = Compute(g, c, cachedRaids);
                    if (v == null) continue;
                    try { g.Progress = v; changed++; } catch { }
                }
                Console.WriteLine($"[progress] sweep: {changed} game(s) updated (of {total}) in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) { Console.WriteLine("[progress] sweep: " + ex.Message); }
        });
    }

    // ── Rule evaluation ────────────────────────────────────────────────────

    private static string? Compute(IGame g, Cfg c, HashSet<int>? cachedRaids)
    {
        string cur = (SafeStr(() => g.Progress) ?? "").Trim();
        if (cur.Length > 0 && !c.Owned.Contains(cur)) return null;   // manual value — hands off

        RaGameCache? ra = null;
        try
        {
            int raid = RaFields.Raid(g);
            if (raid > 0 && (cachedRaids == null || cachedRaids.Contains(raid)))
                ra = RaService.ReadCache(raid);
        }
        catch { }

        string? target = null;
        if (ra != null && ra.total > 0)
        {
            if (c.Mastered.Length > 0 && ra.unlockedHardcore >= ra.total) target = c.Mastered;
            else if (c.Completed.Length > 0 && ra.unlocked >= ra.total) target = c.Completed;
            else if (c.BeatenHardcore.Length > 0 && ra.beatenHardcore) target = c.BeatenHardcore;
            else if (c.BeatenSoftcore.Length > 0 && ra.beatenSoftcore) target = c.BeatenSoftcore;
            else if (c.HasAchievements.Length > 0 && ra.unlocked > 0) target = c.HasAchievements;
        }
        if (target == null && c.PlaytimeReached.Length > 0 && SafeInt(() => g.PlayTime) >= c.MinPlaytimeMinutes * 60L)
            target = c.PlaytimeReached;
        if (target == null && c.NotStarted.Length > 0)
            target = c.NotStarted;

        // "In Progress but inactive for N days" — demote an in-progress RESULT to Paused.
        if (target != null && c.Paused.Length > 0 && c.PausePeriodDays > 0
            && (target == c.PlaytimeReached || target == c.HasAchievements))
        {
            DateTime? lp = null; try { lp = g.LastPlayedDate; } catch { }
            if (lp is DateTime d && (DateTime.Now - d).TotalDays >= c.PausePeriodDays)
                target = c.Paused;
        }

        return target != null && !string.Equals(target, cur, StringComparison.Ordinal) ? target : null;
    }

    // ── Settings snapshot ─────────────────────────────────────────────────

    private sealed class Cfg
    {
        public string NotStarted = "", PlaytimeReached = "", HasAchievements = "", Paused = "";
        public string BeatenSoftcore = "", BeatenHardcore = "", Completed = "", Mastered = "";
        public int MinPlaytimeMinutes = 30, PausePeriodDays = 30;
        public HashSet<string> Owned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Null when Settings.xml is absent or the master switch is off.</summary>
        public static Cfg? Load()
        {
            var s = ProgressModel.Store;
            if (s == null || !s.Loaded || !s.GetBool("EnableAutoProgressTracking")) return null;
            var c = new Cfg
            {
                NotStarted = s.Get("AutoProgressNotStartedValue").Trim(),
                PlaytimeReached = s.Get("AutoProgressMinPlaytimeReachedValue").Trim(),
                HasAchievements = s.Get("AutoProgressHasAchievementsValue").Trim(),
                Paused = s.Get("AutoProgressPausedValue").Trim(),
                BeatenSoftcore = s.Get("AutoProgressBeatenSoftcoreValue").Trim(),
                BeatenHardcore = s.Get("AutoProgressBeatenHardcoreValue").Trim(),
                Completed = s.Get("AutoProgressCompletedValue").Trim(),
                Mastered = s.Get("AutoProgressMasteredValue").Trim(),
            };
            if (int.TryParse(s.Get("AutoProgressMinPlaytime", "30"), out var m)) c.MinPlaytimeMinutes = m;
            if (int.TryParse(s.Get("AutoProgressPausePeriod", "30"), out var p)) c.PausePeriodDays = p;
            foreach (var v in new[] { c.NotStarted, c.PlaytimeReached, c.HasAchievements, c.Paused,
                                      c.BeatenSoftcore, c.BeatenHardcore, c.Completed, c.Mastered })
                if (v.Length > 0) c.Owned.Add(v);
            foreach (var part in s.Get("AutoProgressIncludedValues").Split(';'))
            { var t = part.Trim(); if (t.Length > 0) c.Owned.Add(t); }
            return c;
        }
    }

    private static string? SafeStr(Func<string?> f) { try { return f(); } catch { return null; } }
    private static long SafeInt(Func<long> f) { try { return f(); } catch { return 0; } }
}
