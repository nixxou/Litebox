// Automatic save backups — the half of save management LaunchBox exposes and LiteBox never implemented.
//
// Until now the ONLY thing that ever wrote to the vault was the "Backup Save" button. LaunchBox's own
// settings for this (SaveBackupOnGameClose, PeriodicSaveBackupEnabled, MaxAutoBackupsPerGame) sat in
// Settings.xml unread, so a user who had switched them on in LaunchBox got nothing here. They are the
// settings this service honours, read from the same file LaunchBox reads, so configuring it once
// configures both frontends.
//
// Three entry points:
//   • OnGameClosed — the one moment attribution is KNOWN rather than inferred. The game that just ran is
//     the game whose save changed, and the launched archive entry is on record (LaunchHistoryDb), so no
//     file name has to be matched to know what a new save belongs to.
//   • RunLibraryScan — the sweep behind "Backup Now" and the periodic task. Resumable through a cursor,
//     because a library-wide scan drives every integration plugin over every game and must survive being
//     interrupted.
//   • The scheduler — at startup, then on an interval, and only while nothing is going on (§ Idle).
//
// Every backup goes through SaveManager.Backup(force: false), so the existing dirty-check decides: a save
// identical to its latest backup produces nothing. That is what makes running this often harmless.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Saves;

internal static class SaveBackupService
{
    private const string Section = "Saves";

    // ── Settings ──────────────────────────────────────────────────────────────
    //
    // The four LaunchBox ones live in ITS Settings.xml (LbSettingsStore) so both frontends share them.
    // The two scheduling knobs have no LaunchBox equivalent — LB's periodic backup has no exposed
    // interval — so they are LiteBox's own, in LiteBox.ini [Saves].

    private static LbSettingsStore? Store =>
        (PluginHelper.DataManager as HostDataManagerXml)?.LbSettings;

    private static bool LbBool(string field, bool fallback)
    {
        var v = Store?.Get(field, "");
        if (string.IsNullOrEmpty(v)) return fallback;
        return v!.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>LaunchBox's master switch, stored INVERTED (DisableSaveManagement).</summary>
    public static bool Enabled => !LbBool("DisableSaveManagement", false);

    public static bool AutomaticBackups => LbBool("EnableAutomaticSaveBackups", false);
    public static bool OnGameClose => LbBool("SaveBackupOnGameClose", true);
    public static bool Periodic => LbBool("PeriodicSaveBackupEnabled", true);

    public static int MaxVersionsPerGame
    {
        get
        {
            var v = Store?.Get("MaxAutoBackupsPerGame", "");
            return int.TryParse(v, out var n) && n > 0 ? n : 25;
        }
    }

    /// <summary>Take the group's OLDEST backup off the rotation, so retention can never reach it
    /// ([Saves] ProtectOldestBackup, default off). LiteBox only — LaunchBox has no such notion.
    ///
    /// It exists because nothing in LaunchBox's format distinguishes a backup you asked for from one the
    /// software took on its own: same folder, same naming, no marker. Retention therefore cannot spare a
    /// deliberate copy, and the one people mind losing is usually the FIRST — the clean save, the one
    /// from before the fork in the road.
    ///
    /// Protecting "the oldest" is not the same as protecting "the manual ones", and the help text says
    /// so. It is the cheap approximation that needs no new field, no new folder, and nothing LaunchBox
    /// could fail to understand: on disk it is an ordinary copy that we simply never choose to delete.</summary>
    public static bool ProtectOldestBackup => IniBool("ProtectOldestBackup", false);

    /// <summary>Hours between two library sweeps ([Saves] PeriodicHours, default 24).</summary>
    public static int PeriodicHours => IniInt("PeriodicHours", 24, 1, 24 * 30);

    /// <summary>How long everything must have been quiet before a sweep starts ([Saves] IdleMinutes,
    /// default 60). A library sweep drives every integration plugin over every game — doing that while
    /// somebody is using the app is exactly the wrong moment.</summary>
    public static int IdleMinutes => IniInt("IdleMinutes", 60, 0, 24 * 60);

    private static int IniInt(string key, int fallback, int min, int max)
    {
        try
        {
            var raw = LiteBoxConfig.LoadForExe().GetSec(Section, key);
            return int.TryParse(raw, out var n) ? Math.Clamp(n, min, max) : fallback;
        }
        catch { return fallback; }
    }

    private static bool IniBool(string key, bool fallback)
    {
        try
        {
            var raw = LiteBoxConfig.LoadForExe().GetSec(Section, key);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Trim();
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1"
                   || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch { return fallback; }
    }

    /// <summary>When the last full sweep finished — LaunchBox's own field, so a sweep on either side
    /// counts for both and neither redoes the other's work minutes later.
    ///
    /// Its format is wrong, and has to be reproduced wrongly or the two frontends disagree by the UTC
    /// offset. LaunchBox writes the UTC value but tags it with the LOCAL offset, then reads it back
    /// ignoring the tag. Measured: a sweep run at 14:50:42 local (UTC+2) was stored as
    ///
    ///     2026-08-27T12:50:42.6874085+02:00        &lt;- 12:50 is UTC, "+02:00" is the local offset
    ///
    /// and its own UI displayed "2:50 PM" — so it read the number back as UTC and ignored the offset it
    /// had just written. Writing a correct local timestamp (14:50:42+02:00, which is the same instant)
    /// would show up in LaunchBox two hours late.
    ///
    /// So: write the UTC number as Unspecified through XmlConvert in Local mode, which appends the local
    /// offset without converting; read the number back as UTC and drop the offset. The fractional part
    /// has its trailing zeros trimmed, which is XmlConvert's doing, not ours.</summary>
    public static DateTime? LastScanUtc
    {
        get
        {
            try
            {
                var v = Store?.Get("LastLibrarySaveScanUtc", "");
                if (string.IsNullOrWhiteSpace(v)) return null;
                // Deliberately parsed WITHOUT the offset: the number is UTC whatever the tag claims.
                int cut = v!.LastIndexOfAny(new[] { '+', 'Z' });
                if (cut > 10) v = v.Substring(0, cut);
                else if (v.EndsWith("Z", StringComparison.Ordinal)) v = v.TrimEnd('Z');
                return DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.None, out var d)
                       ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null;
            }
            catch { return null; }
        }
        private set
        {
            try
            {
                Store?.Set("LastLibrarySaveScanUtc", value == null
                    ? ""
                    : System.Xml.XmlConvert.ToString(
                          DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified),
                          System.Xml.XmlDateTimeSerializationMode.Local));
            }
            catch { }
        }
    }

    /// <summary>Backs up whatever the session just changed. Called from the exit sequence BEFORE the ROM
    /// extractor purges \tmp: with savefiles_in_content_dir the emulator wrote its save inside the
    /// extraction folder, and that folder is about to be deleted recursively.</summary>
    public static void OnGameClosed(IGame? game)
    {
        if (game == null || !Enabled || !AutomaticBackups || !OnGameClose) return;
        try
        {
            var scan = SaveManager.ScanBase(game);
            if (scan.Error != null) { LbLog.Info("saves", "auto-backup skipped: " + scan.Error); return; }

            int made = 0;
            foreach (var g in scan.Files.Concat(scan.States))
            {
                if (g.Active == null) continue;
                var r = SaveManager.Backup(g, force: false, auto: true);
                if (r.Entry != null) made++;
                else if (r.Error != null) LbLog.Info("saves", $"auto-backup \"{g.GroupName}\": {r.Error}");
            }
            if (made > 0)
            {
                LbLog.Info("saves", $"auto-backup on close: {made} new version(s) for \"{Safe(() => game.Title)}\"");
                Prune(game, scan);
            }
        }
        catch (Exception ex) { LbLog.Warn("saves", "auto-backup on close failed: " + ex.Message); }
    }

    // ── 2. Retention ──────────────────────────────────────────────────────────

    /// <summary>Drops the oldest versions past the cap.
    ///
    /// It cannot spare the ones you asked for by hand. LaunchBox's format has no field marking a backup
    /// automatic, and every copy now sits in the same folder under the same naming, so nothing on disk
    /// tells them apart — the setting is called MaxAutoBackupsPerGame, but there is no "auto" to read.
    /// Separating them needs somewhere to put that bit; that is what the Manual\ / Auto\ split was for,
    /// and it comes back with it.</summary>
    private static void Prune(IGame game, SaveScan scan)
    {
        int cap = MaxVersionsPerGame;
        bool keepOldest = ProtectOldestBackup;

        foreach (var g in scan.Files.Concat(scan.States))
        {
            // Oldest FIRST, by record order — the creation order, which is what LaunchBox evicts on.
            // Sorting on CreatedUtc (the file's mtime) is what we used to do, and two measurements rule
            // it out: see VaultEntry.Ordinal. The two agree in normal use and part company the moment a
            // file is touched, restored or moved.
            var all = g.Backups.OrderBy(b => b.Ordinal).ToList();
            if (all.Count <= cap) continue;

            // The anchor, when the option is on: the group's oldest copy is taken off the rotation.
            // Everything after it is the rolling window, so the cap still bounds the file count — the
            // promise the setting's name makes.
            var doomed = all.Skip(keepOldest ? 1 : 0).ToList();
            int excess = all.Count - cap;
            if (excess <= 0 || doomed.Count == 0) continue;
            if (excess > doomed.Count)
            {
                // Only reachable with the anchor on and a cap so low the rotation cannot hold anything.
                // Keeping the anchor is the point of the option, so the cap gives way, loudly.
                LbLog.Info("saves", $"\"{g.GroupName}\": cap {cap} cannot be met while the oldest backup "
                                    + $"is protected — keeping {all.Count - doomed.Count + 0} of {all.Count}");
                excess = doomed.Count;
            }

            foreach (var old in doomed.Take(excess))
            {
                var err = SaveManager.DeleteBackup(old, game);
                if (err == null) g.Backups.Remove(old);
                else LbLog.Info("saves", "prune failed: " + err);
            }
            LbLog.Info("saves", $"pruned {excess} old backup(s) of \"{g.GroupName}\""
                                + (keepOldest ? " (oldest kept)" : ""));
        }
    }

    // ── 3. The library sweep ──────────────────────────────────────────────────

    private static int _running;
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Sweeps the library, backing up anything that changed. Resumable: the cursor is persisted
    /// after each game, so an interrupted sweep continues where it stopped instead of restarting a job
    /// that drives every plugin over every game.</summary>
    public static void RunLibraryScan(CancellationToken ct = default, Action<int, int, string>? progress = null)
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _running, 1) != 0) return;   // one sweep at a time
        try
        {
            IGame[] games;
            try { games = PluginHelper.DataManager.GetAllGames() ?? Array.Empty<IGame>(); }
            catch (Exception ex) { LbLog.Warn("saves", "sweep: GetAllGames failed: " + ex.Message); return; }

            // Resume where the last run stopped. The cursor is a game id, so a library edited in between
            // simply resumes at the first game at-or-after it rather than breaking.
            string? cursor = null;
            try { cursor = Store?.Get("LastLibrarySaveScanCursorGameId", ""); } catch { }
            int start = 0;
            if (!string.IsNullOrEmpty(cursor))
            {
                int at = Array.FindIndex(games, g => string.Equals(Safe(() => g.Id), cursor, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) start = at;
            }

            int made = 0;
            for (int i = start; i < games.Length; i++)
            {
                if (ct.IsCancellationRequested) { SetCursor(Safe(() => games[i].Id)); return; }

                var game = games[i];
                progress?.Invoke(i - start + 1, games.Length - start, Safe(() => game.Title) ?? "");
                try
                {
                    // The base view only covers the game's own saves and those of a version sharing its
                    // ROM path. A version pointing elsewhere has its own saves, invisible from here — so
                    // each one is scanned in its own right. Without this the sweep silently skipped every
                    // version-attributed save, which is most of a library imported with versions.
                    foreach (var scan in ScansFor(game))
                    {
                        if (scan.Error != null) continue;
                        bool any = false;
                        foreach (var g in scan.Files.Concat(scan.States))
                        {
                            if (g.Active == null) continue;
                            var r = SaveManager.Backup(g, force: false, auto: true);
                            if (r.Entry != null) { made++; any = true; }
                        }
                        if (any) Prune(game, scan);
                    }
                }
                catch (Exception ex) { LbLog.Info("saves", $"sweep \"{Safe(() => game.Title)}\": {ex.Message}"); }
            }

            SetCursor(null);
            LastScanUtc = DateTime.UtcNow;
            LbLog.Info("saves", $"library sweep done: {made} new version(s) over {games.Length - start} game(s)");
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    /// <summary>LaunchBox's resume cursor: two fields that exist only while a sweep is in flight, and
    /// that it drops from the file when one finishes.
    ///
    /// We can only write them empty, not remove them — the settings store has no delete — so a finished
    /// sweep leaves "&lt;LastLibrarySaveScanCursorGameId /&gt;" where LaunchBox would leave nothing. Both
    /// read back as "no cursor", so the behaviour matches even though the file does not, byte for byte.
    ///
    /// The second field stays empty: our sweep walks games, and scans each one's versions as part of it,
    /// so there is never a version to resume at on its own.</summary>
    /// <summary>Every view a game's saves can live in: its own, then one per additional application.
    /// A save attributed to a version is not visible from the base view unless that version shares the
    /// game's ROM path, so anything walking a library has to ask for each of them.</summary>
    private static IEnumerable<SaveScan> ScansFor(IGame game)
    {
        SaveScan? bas = null;
        try { bas = SaveManager.ScanBase(game); } catch { }
        if (bas != null) yield return bas;

        IAdditionalApplication[] apps;
        try { apps = game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { yield break; }

        string gamePath = "";
        try { gamePath = game.ApplicationPath ?? ""; } catch { }
        foreach (var a in apps)
        {
            string ap = "";
            try { ap = a.ApplicationPath ?? ""; } catch { }
            // An entry with no FILE NAME is not scannable, and feeding it to a plugin is actively
            // harmful: RetroArch derives its search from Path.GetFileNameWithoutExtension, so an empty
            // one produces the pattern "*.*" and the entry claims every save in the folder. That is the
            // defect we measured in LaunchBox's own vault re-scan — and this loop reintroduced it on our
            // side the moment it started scanning versions individually, because the base view used to
            // discard those results. A Link holding "https://site.com/" is exactly such an entry.
            if (Path.GetFileNameWithoutExtension(ap).Length == 0) continue;
            if (SaveManager.PathEq(ap, gamePath)) continue;                // already in the base view
            SaveScan? v = null;
            try { v = SaveManager.ScanApp(game, a); } catch { }
            if (v != null) yield return v;
        }
    }

    private static void SetCursor(string? gameId)
    {
        try
        {
            Store?.Set("LastLibrarySaveScanCursorGameId", gameId ?? "");
            Store?.Set("LastLibrarySaveScanCursorAdditionalAppId", "");
        }
        catch { }
    }

    // ── 4. Scheduling ─────────────────────────────────────────────────────────

    private static System.Threading.Timer? _timer;
    private static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(5);

    /// <summary>Arms the periodic sweep. Called once at boot; safe to call again (idempotent).</summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new System.Threading.Timer(_ => Tick(), null, TickEvery, TickEvery);
        LbLog.Info("saves", $"periodic backups armed (every {PeriodicHours}h, after {IdleMinutes}min idle)");
    }

    public static void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
    }

    private static void Tick()
    {
        try
        {
            if (!Enabled || !AutomaticBackups || !Periodic) return;
            if (IsRunning) return;

            // Never while a game is running or an archive is being extracted. Input idleness alone is not
            // enough: somebody playing with a gamepad registers as idle to Windows, and a sweep that drives
            // every plugin over every game is the last thing that should start mid-session.
            if (Web.RecentState.IsGameRunning || Web.RecentState.IsExtractionInProgress) return;
            if (BackgroundJobs.Busy) return;

            var last = LastScanUtc;
            if (last != null && DateTime.UtcNow - last.Value < TimeSpan.FromHours(PeriodicHours)) return;

            int idleNeeded = IdleMinutes;
            if (idleNeeded > 0 && IdleMinutes > 0)
            {
                var idle = InputIdle();
                if (idle == null || idle.Value < TimeSpan.FromMinutes(idleNeeded)) return;
            }

            LbLog.Info("saves", "periodic sweep starting (idle threshold met)");
            Task.Run(() => RunLibraryScan());
        }
        catch (Exception ex) { LbLog.Warn("saves", "periodic tick failed: " + ex.Message); }
    }

    /// <summary>Runs the sweep once at startup when it is overdue — the "or at LiteBox launch" arm. Fired
    /// on a delay so it never competes with boot, and still gated on nothing else going on.</summary>
    public static void RunAtStartupIfDue()
    {
        if (!Enabled || !AutomaticBackups || !Periodic) return;
        var last = LastScanUtc;
        if (last != null && DateTime.UtcNow - last.Value < TimeSpan.FromHours(PeriodicHours)) return;

        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            if (Web.RecentState.IsGameRunning || BackgroundJobs.Busy || IsRunning) return;
            LbLog.Info("saves", "startup sweep (overdue)");
            RunLibraryScan();
        });
    }

    // ── 5. Maintenance tools ──────────────────────────────────────────────────
    //
    // The two buttons LaunchBox exposes under "Maintenance Tools". Repair is now measured (below);
    // Clear-and-rescan still is not, and says so.

    public sealed class MaintenanceResult
    {
        public int RowsRemoved;      // "Removed missing records"
        public int SavesAdded;       // "Added emulator saves"
        public int GamesTouched;     // "Games updated"
        public override string ToString()
            => $"{RowsRemoved} missing record(s) removed, {SavesAdded} emulator save(s) added, {GamesTouched} game(s) updated";
    }

    /// <summary>"Repair Save Metadata", measured against LaunchBox on five deliberately broken records.
    /// Its report names three numbers, and they are the three things it does:
    ///
    ///   Removed missing records   a record whose file is gone, or whose FilePath is empty
    ///   Added emulator saves      a live save the plugins can see and no record names yet
    ///   Games updated             how many games any of that touched
    ///
    /// It also drops EXACT duplicates — two records with the same SaveGroupId naming the same file, which
    /// is what its own Import produces — without counting them anywhere. Three records vanished on a run
    /// reporting "Removed missing records: 1".
    ///
    /// What it deliberately does NOT do, both verified:
    ///   • it keeps a FOSSIL — a second record naming the same live file under a DIFFERENT SaveGroupId.
    ///     We used to remove those. That was a divergence, and removing them is not obviously right
    ///     either: a fossil is a group, and a group may be something the user made on purpose.
    ///   • it does not adopt a vault file that no record names. Such a file stays invisible to LaunchBox,
    ///     which is consistent with everything else — the folder is not an index.
    ///
    /// And it does NOT set SaveVaultMetadataRepairComplete: the flag stayed false across a completed run,
    /// so it is not the one-shot marker for this button, whatever else it is for.
    ///
    /// No save file and no backup is ever deleted — only bookkeeping.</summary>
    public static MaintenanceResult RepairMetadata(CancellationToken ct = default)
    {
        var res = new MaintenanceResult();

        IGame[] games;
        try { games = PluginHelper.DataManager.GetAllGames() ?? Array.Empty<IGame>(); }
        catch { return res; }

        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) break;
            if (game is not ILiteBoxGame lbg) continue;
            try
            {
                var rows = lbg.GetSubEntities("GameSave")
                    .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
                int before = rows.Count;
                int removedMissing = 0;

                // 1. Records naming nothing, or naming a file that is gone.
                rows.RemoveAll(r =>
                {
                    var path = r.GetValueOrDefault("FilePath") ?? "";
                    if (path.Length == 0) { removedMissing++; return true; }
                    if (FileOrDirExists(SaveManager.AbsPath(path))) return false;
                    removedMissing++;
                    return true;
                });

                // 2. Exact duplicates: same group, same file, same slot. Keep the first.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rows.RemoveAll(r => !seen.Add(string.Join("|",
                    r.GetValueOrDefault("SaveGroupId") ?? "",
                    SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? ""),
                    r.GetValueOrDefault("Slot") ?? "")));

                bool dirty = rows.Count != before;
                if (dirty)
                {
                    lbg.SetSubEntities("GameSave", rows);
                    res.RowsRemoved += removedMissing;
                }

                // 3. Live saves nobody has recorded yet. The scan creates their records as a side effect,
                //    which is precisely what "Added emulator saves" counts.
                try
                {
                    int had = rows.Count;
                    var scan = SaveManager.ScanBase(game);
                    if (scan.Error == null)
                    {
                        int now = lbg.GetSubEntities("GameSave").Count();
                        if (now > had) { res.SavesAdded += now - had; dirty = true; }
                    }
                }
                catch { }

                if (dirty) res.GamesTouched++;
            }
            catch (Exception ex) { LbLog.Info("saves", $"repair \"{Safe(() => game.Title)}\": {ex.Message}"); }
        }

        LbLog.Info("saves", "repair: " + res);
        return res;
    }

    /// <summary>Drops every ACTIVE save record library-wide and rebuilds them by asking the plugins.
    /// Records of vault COPIES are left untouched, so the backup history survives.
    ///
    /// LaunchBox's button of this name does more, and measurably worse. It clears everything, then adopts
    /// every file it finds in the vault as a save in its own right: 22 records became 42, and NOT ONE of
    /// the 42 carried a Title — every archived copy had been promoted to a live save in a brand-new group.
    /// Where the user had three groups with their history, they had thirty-odd independent groups each
    /// pointing at one vault file. The files themselves were untouched, as its confirmation promises; the
    /// structure was gone, which its confirmation does not mention.
    ///
    /// Its adoption also has no guard against an empty base name. An Additional Application of type Link
    /// (ApplicationPath = a URL) yields one, and the resulting "" + "*.*" glob claims the whole platform
    /// folder: 14 of 36 records, all naming Secret of Mana backups, came back attributed to an unrelated
    /// game through such an entry.
    ///
    /// So this is a deliberate divergence, for the same reason as the one in Backup: reproducing it would
    /// destroy the user's backup history at the exact moment they click a button meant to repair it.</summary>
    public static MaintenanceResult ClearAndRescan(CancellationToken ct = default,
                                                   Action<int, int, string>? progress = null)
    {
        var res = new MaintenanceResult();
        var identity = new Dictionary<string, (string gid, string lineage, string name)>(StringComparer.OrdinalIgnoreCase);

        IGame[] games;
        try { games = PluginHelper.DataManager.GetAllGames() ?? Array.Empty<IGame>(); }
        catch { return res; }

        // 1. Clear the ACTIVE records. Records of vault copies are kept, so a group's history survives
        //    the rebuild — this is where LaunchBox loses everything.
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) return res;
            if (game is not ILiteBoxGame lbg) continue;
            try
            {
                var rows = lbg.GetSubEntities("GameSave")
                    .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
                int before = rows.Count;

                // Remember what each cleared record said about its file. Rebuilding mints a NEW
                // SaveGroupId, and a new id detaches the group's copies — the history would survive the
                // clear only to be orphaned by the rebuild. Restoring the identity of any record that
                // comes back naming the same file keeps them together.
                foreach (var r in rows.Where(r => !SaveManager.IsVaultPath(r.GetValueOrDefault("FilePath") ?? "")))
                {
                    var abs = SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? "");
                    if (abs.Length == 0) continue;
                    identity[abs] = (r.GetValueOrDefault("SaveGroupId") ?? "",
                                     r.GetValueOrDefault("MatchLineageId") ?? "",
                                     r.GetValueOrDefault("SaveGroupName") ?? "");
                }

                rows.RemoveAll(r => !SaveManager.IsVaultPath(r.GetValueOrDefault("FilePath") ?? ""));
                if (rows.Count != before)
                {
                    lbg.SetSubEntities("GameSave", rows);
                    res.RowsRemoved += before - rows.Count;
                    res.GamesTouched++;
                }
            }
            catch { }
        }
        LbLog.Info("saves", "clear: " + res);

        // 2. Rebuild the live records by asking the plugins, and back up what needs it.
        SetCursor(null);
        RunLibraryScan(ct, progress);

        // 3. Give back the identity of every rebuilt record that names a file we knew, so the copies
        //    that kept the old group id are still attached to it.
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) return res;
            if (game is not ILiteBoxGame lbg2) continue;
            try
            {
                var rows = lbg2.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
                bool dirty = false;
                foreach (var r in rows)
                {
                    var abs = SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? "");
                    if (abs.Length == 0 || !identity.TryGetValue(abs, out var id) || id.gid.Length == 0) continue;
                    if (r.GetValueOrDefault("SaveGroupId") == id.gid) continue;
                    r["SaveGroupId"] = id.gid;
                    if (id.lineage.Length > 0) r["MatchLineageId"] = id.lineage;
                    if (id.name.Length > 0) r["SaveGroupName"] = id.name;
                    dirty = true;
                }
                if (dirty) lbg2.SetSubEntities("GameSave", rows);
            }
            catch { }
        }

        // 4. Adopt the vault files nothing references. This is the half of LaunchBox's button worth
        //    keeping — it is how a copy left behind by its own sweep becomes visible again.
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) return res;
            try { res.SavesAdded += SaveManager.AdoptOrphanCopies(game); }
            catch (Exception ex) { LbLog.Info("saves", $"adopt \"{Safe(() => game.Title)}\": {ex.Message}"); }
        }

        LbLog.Info("saves", "clear+rescan: " + res);
        return res;
    }

    // ── Idle ──────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Time since the last keyboard or mouse input, or null when it cannot be read. Session-wide,
    /// so it measures the machine, not this window — which is what "wait until nobody is around" means.</summary>
    public static TimeSpan? InputIdle()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return null;
            uint ticks = (uint)Environment.TickCount;
            return TimeSpan.FromMilliseconds(ticks >= lii.dwTime ? ticks - lii.dwTime : 0);
        }
        catch { return null; }
    }

    private static bool FileOrDirExists(string p)
        => p.Length > 0 && (System.IO.File.Exists(p) || System.IO.Directory.Exists(p));

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
}
