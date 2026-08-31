// La passe : construire romm_games pour toute la bibliothèque, plateforme par plateforme.
//
// Runs on a background thread once everything else has loaded — the IGames above all — and the RomM
// server does not listen until it has finished. A client that gets a refused connection retries; a
// client that gets a wrong answer caches it, and we spent a night learning that.
//
// Per platform, and that is not tidiness: it bounds the working set, it keeps each transaction to a
// sensible size, and it turns "one query per game" into two queries per platform — the rows, and the
// launch history.
//
// Nothing here opens an archive or stats a ROM. Validation compares RECORDED paths, and archive contents
// come from the listing cache the desktop picker already fills.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>What one full pass did — the line that goes in the log.</summary>
internal sealed class RommIndexReport
{
    public int Games, Advertised, Added, Modified, Silent;
    public readonly List<string> SilentReasons = new();
    public long ElapsedMs;

    public override string ToString()
        => $"{Advertised}/{Games} game(s) advertised, {Added} row(s) added, {Modified} updated, " +
           $"{Silent} not advertised, {ElapsedMs} ms";
}

internal static class RommIndexer
{
    private static readonly object _gate = new();   // ONE writer for romm_games, always

    /// <summary>Rebuilds the whole index. Returns what it did, for the log and the self-test.</summary>
    public static RommIndexReport RunFull()
    {
        var rep = new RommIndexReport();
        var sw = Stopwatch.StartNew();

        lock (_gate)
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) { LbLog.Warn("romm", "index: no database"); return rep; }

            RommFiles.ForgetEmulatorMap();
            Listen();                       // idempotent : la premiere passe arme les declencheurs
            long gen = RommGamesTable.NextGeneration(conn);
            var clients = SyncClients(conn);
            var history = RommLaunchMemory.Load();

            var platforms = SafePlatforms();
            LbLog.Info("romm", $"index: {platforms.Count} platform(s) to walk");

            foreach (var p in platforms)
            {
                int platformId = RommDb.PlatformId(p);
                var byGuid = RommGamesTable.ByPlatform(conn, platformId);
                var touched = new List<RommGameRow>();
                var games = SafeGamesOf(p);
                LbLog.Info("romm", $"index: \"{p}\" — {games.Count} game(s)");

                foreach (var game in games)
                {
                    var guid = RommLibrary.IdOf(game);
                    if (guid.Length == 0) continue;
                    rep.Games++;

                    if (!byGuid.TryGetValue(guid, out var rows)) byGuid[guid] = rows = new List<RommGameRow>();

                    RommPassResult r;
                    try { r = RommIndexPass.Run(game, rows, platformId, gen, clients, history); }
                    catch (Exception ex)
                    {
                        LbLog.Warn("romm", $"index: \"{RommLibrary.TitleOf(game)}\" failed: {ex.Message}");
                        continue;
                    }

                    if (r.Advertised) rep.Advertised++;
                    else
                    {
                        rep.Silent++;
                        if (r.Reason != null && rep.SilentReasons.Count < 40)
                            rep.SilentReasons.Add($"{RommLibrary.TitleOf(game)} — {r.Reason}");
                    }

                    foreach (var row in rows)
                    {
                        if (row.Action == RommRowAction.Add) rep.Added++;
                        else if (row.Action == RommRowAction.Modify) rep.Modified++;
                        if (row.Action != RommRowAction.None) touched.Add(row);
                    }
                }

                if (touched.Count > 0) RommGamesTable.Flush(conn, touched);
            }

            RommRoms.Reload(conn);
        }

        rep.ElapsedMs = sw.ElapsedMilliseconds;
        return rep;
    }

    /// <summary>Re-runs the pass for ONE game — what every trigger calls. Same writer lock as the full
    /// pass: a background trigger and a client being added must not interleave on the clients column.</summary>
    public static void RunGame(IGame game)
    {
        var guid = RommLibrary.IdOf(game);
        if (guid.Length == 0) return;

        lock (_gate)
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return;

            long gen = RommGamesTable.NextGeneration(conn);
            var clients = SyncClients(conn);
            var rows = RommGamesTable.ByGame(conn, guid);
            int platformId = RommDb.PlatformId(RommLibrary.PlatformOf(game));

            try { RommIndexPass.Run(game, rows, platformId, gen, clients, RommLaunchMemory.Load()); }
            catch (Exception ex) { LbLog.Warn("romm", $"index: \"{guid}\" failed: {ex.Message}"); return; }

            var touched = rows.Where(r => r.Action != RommRowAction.None).ToList();
            if (touched.Count > 0) RommGamesTable.Flush(conn, touched);
            RommRoms.ReloadGame(conn, guid);
        }
    }

    /// <summary>Gives every paired token a client index, and retires the ones whose token is gone. The
    /// pass then removes retired indices from every row it touches.</summary>
    private static Dictionary<int, int> SyncClients(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        try
        {
            var tokens = RommAuth.ListTokens();
            foreach (var t in tokens) RommGamesTable.ClientIdFor(conn, t.Id);

            var live = RommGamesTable.LiveClients(conn);
            var alive = new HashSet<int>(tokens.Select(t => t.Id));
            foreach (var (clientId, tokenId) in live.ToList())
                if (!alive.Contains(tokenId))
                {
                    RommGamesTable.RetireClient(conn, clientId);
                    live.Remove(clientId);
                }
            return live;
        }
        catch (Exception ex)
        {
            LbLog.Warn("romm", "index: client sync failed: " + ex.Message);
            return new Dictionary<int, int>();
        }
    }

    // ── Les écritures venues de l'interface ───────────────────────────────────

    /// <summary>Starts the full pass on a background thread. The server does not listen until
    /// RommRoms.Ready — a refused connection is retried, a wrong answer is cached.</summary>
    private static bool _passRunning;
    private static readonly List<Action> _afterPass = new();

    public static void StartBackground(Action? whenDone = null)
    {
        lock (_afterPass)
        {
            // Deja indexe : la suite s'execute tout de suite, sans repasser.
            if (RommRoms.Ready && !_passRunning)
            {
                if (whenDone != null) { try { whenDone(); } catch { } }
                return;
            }
            // Une passe tourne deja : on s'y accroche au lieu d'en lancer une seconde.
            if (_passRunning)
            {
                if (whenDone != null) _afterPass.Add(whenDone);
                return;
            }
            _passRunning = true;
            if (whenDone != null) _afterPass.Add(whenDone);
        }
        // Re-entrant on purpose: RommServer asks for a pass when it finds the index cold, and boot asks
        // for one too. The second caller must not start a second pass — it waits behind the same lock
        // and then runs its continuation against a ready index.
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                WaitForLibrary();
                var rep = RunFull();
                LbLog.Info("romm", "index: " + rep);
                foreach (var why in rep.SilentReasons) LbLog.Info("romm", "index: not advertised — " + why);
            }
            catch (Exception ex) { LbLog.Warn("romm", "index: pass failed: " + ex.Message); }
            List<Action> waiting;
            lock (_afterPass)
            {
                _passRunning = false;
                waiting = _afterPass.ToList();
                _afterPass.Clear();
            }
            foreach (var a in waiting)
                try { a(); }
                catch (Exception ex) { LbLog.Warn("romm", "index: continuation failed: " + ex.Message); }
        })
        { IsBackground = true, Name = "romm-index" };
        t.Start();
    }

    /// <summary>Moves a client onto one specific file of a game — what the assignment screen does.
    /// Creating the row if nobody had ever wanted that file.</summary>
    public static bool PinClient(IGame game, int tokenId, string appId, string filePath, string romPath,
                                 bool isExtract)
    {
        var guid = RommLibrary.IdOf(game);
        if (guid.Length == 0 || tokenId <= 0) return false;

        lock (_gate)
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return false;

            int clientId = RommGamesTable.ClientIdFor(conn, tokenId);
            int platformId = RommDb.PlatformId(RommLibrary.PlatformOf(game));
            var rows = RommGamesTable.ByGame(conn, guid);

            var target = rows.FirstOrDefault(r => RommIndexPass.PathEq(r.FilePath, filePath)
                                               && RommIndexPass.PathEq(r.RomPath, romPath));
            if (target == null)
            {
                target = new RommGameRow
                {
                    GuidLb = guid, PlatformId = platformId, Emulated = true,
                    AppId = appId, FilePath = filePath, RomPath = romPath, IsExtract = isExtract,
                    Action = RommRowAction.Add,
                };
                rows.Add(target);
            }
            if (target.Disabled != 0) { target.Disabled = 0; target.DisabledUtc = null; target.Touch(); }

            // A client sits on exactly ONE row per game.
            foreach (var r in rows)
                if (!ReferenceEquals(r, target) && r.Clients.Remove(clientId)) r.Touch();
            if (!target.Clients.Contains(clientId)) { target.Clients.Add(clientId); target.Touch(); }

            RommGamesTable.Flush(conn, rows.Where(r => r.Action != RommRowAction.None).ToList());
            RommRoms.ReloadGame(conn, guid);
        }
        return true;
    }

    /// <summary>Puts a client back on the game's default.</summary>
    public static bool UnpinClient(IGame game, int tokenId)
    {
        var guid = RommLibrary.IdOf(game);
        if (guid.Length == 0 || tokenId <= 0) return false;

        lock (_gate)
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return false;

            int clientId = RommGamesTable.ClientIdFor(conn, tokenId);
            var rows = RommGamesTable.ByGame(conn, guid);
            var def = rows.FirstOrDefault(r => r.IsValid && r.IsDefaultUtc != null);

            foreach (var r in rows)
                if (!ReferenceEquals(r, def) && r.Clients.Remove(clientId)) r.Touch();
            if (def != null && !def.Clients.Contains(clientId)) { def.Clients.Add(clientId); def.Touch(); }

            RommGamesTable.Flush(conn, rows.Where(r => r.Action != RommRowAction.None).ToList());
            RommRoms.ReloadGame(conn, guid);
        }
        return true;
    }

    /// <summary>A client has just been paired: it must have an answer for every game before it lists
    /// one. Fast path — one statement puts it on every default; then the games whose default moved
    /// outside a full pass go through the procedure, because their marker cannot be trusted.</summary>
    public static void AddClient(int tokenId)
    {
        if (tokenId <= 0) return;
        lock (_gate)
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return;

            int clientId = RommGamesTable.ClientIdFor(conn, tokenId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE romm_games SET clients = CASE WHEN clients = '' THEN $one " +
                    "                                     ELSE clients || $tail END " +
                    "WHERE is_default IS NOT NULL AND disabled = 0 " +
                    "  AND clients NOT LIKE $like";
                cmd.Parameters.AddWithValue("$one", "," + clientId + ",");
                cmd.Parameters.AddWithValue("$tail", clientId + ",");
                cmd.Parameters.AddWithValue("$like", "%," + clientId + ",%");
                cmd.ExecuteNonQuery();
            }
            RommRoms.Reload(conn);
        }

        // The dirty ones: their default was settled outside a full pass, so re-run rather than trust it.
        foreach (var guid in RommRoms.DirtyDefaults())
        {
            try
            {
                var g = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetGameById(guid);
                if (g != null) RunGame(g);
            }
            catch { }
        }
    }

    /// <summary>A client is gone: its index is retired for good and struck from every row. Nobody else
    /// changes file because one client left, so no pass is needed.</summary>
    public static void RemoveClient(int tokenId)
    {
        lock (_gate)
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return;
            int clientId = RommRoms.ClientIndexOf(tokenId);
            if (clientId <= 0) clientId = RommGamesTable.ClientIdFor(conn, tokenId);
            RommGamesTable.RetireClient(conn, clientId);
            RommRoms.Reload(conn);
        }
    }

    /// <summary>What a rom id names, straight from the table — one query, on the download path only.</summary>
    public static RommGameRow? RowOf(long romId)
    {
        try
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return null;
            var map = RommGamesTable.ByIds(conn, new[] { romId });
            return map.TryGetValue(romId, out var row) ? row : null;
        }
        catch { return null; }
    }

    // Les declencheurs, empiles et differes

    private static readonly object _queueGate = new();
    private static readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private static System.Threading.Timer? _queueTimer;

    /// <summary>Something that could change this game's answer just happened. Queued and processed after
    /// a silence, never synchronously.
    ///
    /// The debounce is not politeness: the desktop picker fills the archive listing cache one archive at
    /// a time while you browse a platform, so a synchronous pass per write would run one per game of the
    /// platform, in the middle of the UI thread's work.</summary>
    public static void Touch(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId) || !RommRoms.Ready) return;
        lock (_queueGate)
        {
            _queued.Add(gameId!);
            _queueTimer ??= new System.Threading.Timer(_ => DrainQueue(), null,
                                                       System.Threading.Timeout.Infinite,
                                                       System.Threading.Timeout.Infinite);
            try { _queueTimer.Change(2500, System.Threading.Timeout.Infinite); } catch { }
        }
    }

    /// <summary>Same, when the game object is at hand.</summary>
    public static void Touch(IGame? game)
    {
        if (game == null) return;
        try { Touch(RommLibrary.IdOf(game)); } catch { }
    }

    /// <summary>Listens to the one point every library mutation converges to — Edit Game, the bulk
    /// editors, the store syncs, a plugin through ILiteBoxGame. Instrumenting the setters or the UI call
    /// sites would each miss the paths they do not know about; this one cannot.</summary>
    public static void Listen()
    {
        if (_listening) return;
        _listening = true;

        Data.GameStore.GameChanged += (id, field) =>
        {
            // An empty field means something coarse — a child collection, an add, a delete, a move. No
            // way to tell whether it matters, so it always counts. An additional application appearing
            // or losing its path arrives this way.
            if (field.Length > 0 && !Watched.Contains(field)) return;
            Touch(id.ToString());
        };

        // The extractor's per-(platform, emulator) settings decide whether an archive is ours to take
        // apart at all — the MAME/Arcade case. One switch can change the answer for a whole platform,
        // so this is a full pass rather than a game-by-game queue.
        Rom.RomConfig.Changed += () =>
        {
            if (!RommRoms.Ready) return;
            LbLog.Info("romm", "index: extractor settings changed, full pass queued");
            StartBackground();
        };
    }

    private static bool _listening;

    /// <summary>The fields that can change what a game offers or how it is reached. A Notes edit wakes
    /// nothing.</summary>
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal)
    {
        "ApplicationPath", "EmulatorId", "Platform", "UseDosBox", "UseScummVM", "Title",
    };

    private static void DrainQueue()
    {
        // NEVER while the store is degreased. A running game keeps writing PlayCount while the display
        // sub-entities are freed — and this pass reads ADDITIONAL APPLICATIONS. Running now would see a
        // game with no versions and disable perfectly good rows.
        if (Data.GameStore.OptionalDropped)
        {
            LbLog.Info("romm", "index: deferred pass held back (a game is running)");
            lock (_queueGate)
                try { _queueTimer?.Change(5000, System.Threading.Timeout.Infinite); } catch { }
            return;
        }

        List<string> batch;
        lock (_queueGate) { batch = _queued.ToList(); _queued.Clear(); }
        foreach (var guid in batch)
        {
            try
            {
                var g = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetGameById(guid);
                if (g != null) RunGame(g);
            }
            catch (Exception ex) { LbLog.Warn("romm", $"index: deferred pass on {guid} failed: {ex.Message}"); }
        }
        if (batch.Count > 0) LbLog.Info("romm", $"index: {batch.Count} game(s) re-settled after a change");
    }

    /// <summary>Waits until the library is actually loaded. Measured the hard way: the first pass ran
    /// while the game cache was still rebuilding and enumerated 58 games out of 3058 — it finished after
    /// the cache was ready, but it had already read.</summary>
    private static void WaitForLibrary()
    {
        try
        {
            for (int i = 0; i < 600 && !Gc.GameCache.IsGlobalReady; i++)
                System.Threading.Thread.Sleep(100);
            if (!Gc.GameCache.IsGlobalReady)
                LbLog.Warn("romm", "index: the library never reported ready — walking it as it stands");
        }
        catch { }
    }

    private static List<string> SafePlatforms()
    {
        // ignorePins: la passe doit voir la bibliotheque telle qu'elle est, pas telle que l'index
        // (vide, a ce stade) la laisse voir.
        try { return RommLibrary.Platforms(null, null, ignorePins: true).Select(p => p.LbName).ToList(); }
        catch { return new List<string>(); }
    }

    private static List<IGame> SafeGamesOf(string platform)
    {
        // ignorePins: the index decides what is advertisable — it must not be filtered by its own
        // previous conclusions, or a game that dropped out could never come back.
        try { return RommLibrary.GamesOf(platform, null, ignorePins: true); }
        catch { return new List<IGame>(); }
    }
}
