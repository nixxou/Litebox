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

    /// <summary>When the last full sweep finished — LaunchBox's own field, so a sweep on either side
    /// counts for both and neither redoes the other's work minutes later.
    ///
    /// Two traps in its format, both observed on a real file. Despite the name it is written in LOCAL
    /// time with an offset ("2026-08-27T04:39:05.13416+02:00"), and the fractional part has its trailing
    /// zeros trimmed — the shape XmlConvert produces, not DateTime.ToString("o"). Writing it any other
    /// way would still parse, but the file would stop looking like one LaunchBox wrote.</summary>
    public static DateTime? LastScanUtc
    {
        get
        {
            try
            {
                var v = Store?.Get("LastLibrarySaveScanUtc", "");
                if (string.IsNullOrWhiteSpace(v)) return null;
                return DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                       ? d.UtcDateTime : (DateTime?)null;
            }
            catch { return null; }
        }
        private set
        {
            try
            {
                Store?.Set("LastLibrarySaveScanUtc", value == null
                    ? ""
                    : System.Xml.XmlConvert.ToString(value.Value.ToLocalTime(),
                                                     System.Xml.XmlDateTimeSerializationMode.Local));
            }
            catch { }
        }
    }

    // ── 1. Game close — the authoritative moment ──────────────────────────────

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
        foreach (var g in scan.Files.Concat(scan.States))
        {
            var all = g.Backups.OrderByDescending(b => b.CreatedUtc).ToList();
            if (all.Count <= cap) continue;
            foreach (var old in all.Skip(cap))
            {
                var err = SaveManager.DeleteBackup(old, game);
                if (err == null) g.Backups.Remove(old);
                else LbLog.Info("saves", "prune failed: " + err);
            }
            LbLog.Info("saves", $"pruned {all.Count - cap} old backup(s) of \"{g.GroupName}\"");
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
                    var scan = SaveManager.ScanBase(game);
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
    // LaunchBox exposes two buttons under this name. What its own versions do is NOT observable from the
    // plugin sources — the host is obfuscated and neither operation goes through the SDK — so these are
    // LiteBox's semantics, spelled out in the UI, not a parity claim.

    public sealed class MaintenanceResult
    {
        public int RowsRemoved;
        public int GamesTouched;
        public override string ToString()
            => $"{RowsRemoved} record(s) dropped over {GamesTouched} game(s)";
    }

    /// <summary>Removes save metadata that no longer describes anything: a record whose file is gone and
    /// which has no backup to restore from, a backup record whose copy is gone, and the fossil record a
    /// version leaves behind when it starts covering the game's own ROM. Nothing that still resolves is
    /// touched, and no save file and no backup is ever deleted — only dangling bookkeeping.</summary>
    public static MaintenanceResult RepairMetadata(CancellationToken ct = default)
    {
        var res = new MaintenanceResult();

        // There is no vault index to clean any more — the folder IS the index. What can still dangle is
        // a RECORD: one pointing at a file that is gone, or an older record left behind when a version
        // started covering the game's own ROM.

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
                if (rows.Count == 0) continue;

                // Title SET = a backup row. Not one particular literal: "Saved Game" for a save file,
                // "Save State <slot>" for a state.
                static bool IsBackup(Dictionary<string, string> r)
                    => !string.IsNullOrWhiteSpace(r.GetValueOrDefault("Title"));

                int before = rows.Count;
                rows.RemoveAll(r =>
                {
                    var path = r.GetValueOrDefault("FilePath") ?? "";
                    if (path.Length == 0) return true;                       // a record addressing nothing
                    var abs = SaveManager.AbsPath(path);
                    if (System.IO.File.Exists(abs) || System.IO.Directory.Exists(abs)) return false;

                    // A backup record whose copy is gone is pure noise. A live record whose save is gone is
                    // still worth keeping WHEN backups remain — that is the "deleted the save, kept the
                    // versions" case, and dropping it would orphan them.
                    if (IsBackup(r)) return true;
                    // Does any surviving backup row still belong to this group? If so the record is the
                    // only thing tying those copies to the game — keep it.
                    return !rows.Any(o => IsBackup(o)
                        && string.Equals(o.GetValueOrDefault("SaveGroupId"),
                                         r.GetValueOrDefault("SaveGroupId"), StringComparison.OrdinalIgnoreCase)
                        && FileOrDirExists(SaveManager.AbsPath(o.GetValueOrDefault("FilePath") ?? "")));
                });

                // Duplicate records. Once a version points at the game's own ROM, RetroArch's scan skips
                // the game entirely and every save comes back attributed to the version — but LaunchBox
                // leaves the older game-attributed record behind for ever. The result is one save shown
                // twice, the second copy claiming its file is missing. Drop the fossil, never the one
                // that carries history: a row is only removed when no backup and no vault entry
                // references its group.
                var carriesHistory = new HashSet<string>(
                    rows.Where(IsBackup).Select(r => r.GetValueOrDefault("SaveGroupId") ?? ""),
                    StringComparer.OrdinalIgnoreCase);

                var byTarget = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var fossils = new List<Dictionary<string, string>>();
                foreach (var r in rows.Where(r => !IsBackup(r)))
                {
                    var abs = SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? "");
                    if (abs.Length == 0) continue;
                    string key = abs + "|" + (r.GetValueOrDefault("Slot") ?? "");
                    if (!byTarget.TryGetValue(key, out var kept)) { byTarget[key] = r; continue; }

                    // Two records for the same file+slot. Keep the one with history; failing that, the
                    // one already kept (document order — LaunchBox writes the current one first).
                    bool keptHas = carriesHistory.Contains(kept.GetValueOrDefault("SaveGroupId") ?? "");
                    bool mineHas = carriesHistory.Contains(r.GetValueOrDefault("SaveGroupId") ?? "");
                    if (mineHas && !keptHas) { fossils.Add(kept); byTarget[key] = r; }
                    else if (!mineHas) fossils.Add(r);
                    // both carry history → leave both alone, this is not a fossil we can judge
                }
                foreach (var f in fossils) rows.Remove(f);

                if (rows.Count != before)
                {
                    lbg.SetSubEntities("GameSave", rows);
                    res.RowsRemoved += before - rows.Count;
                    res.GamesTouched++;
                }
            }
            catch (Exception ex) { LbLog.Info("saves", $"repair \"{Safe(() => game.Title)}\": {ex.Message}"); }
        }

        LbLog.Info("saves", "repair: " + res);
        return res;
    }

    /// <summary>Drops every ACTIVE save record library-wide and rebuilds them from a fresh scan. Backups —
    /// ours and LaunchBox's — are deliberately left alone: a button about metadata has no business
    /// deleting a user's saved versions, and the records that point at them are re-derived anyway.</summary>
    public static MaintenanceResult ClearAndRescan(CancellationToken ct = default,
                                                   Action<int, int, string>? progress = null)
    {
        var res = new MaintenanceResult();

        IGame[] games;
        try { games = PluginHelper.DataManager.GetAllGames() ?? Array.Empty<IGame>(); }
        catch { return res; }

        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) return res;
            if (game is not ILiteBoxGame lbg) continue;
            try
            {
                var rows = lbg.GetSubEntities("GameSave")
                    .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
                int before = rows.Count;
                // Title SET = backup row (see RepairMetadata). Active records are the ones with no Title.
                rows.RemoveAll(r => string.IsNullOrWhiteSpace(r.GetValueOrDefault("Title")));
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

        // Re-scan from scratch: the scan itself is what re-creates a record for every group it finds.
        SetCursor(null);
        RunLibraryScan(ct, progress);
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
