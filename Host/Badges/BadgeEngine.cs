// Which badges apply to which game — computed ONCE for the whole library, kept in one packed table,
// read by every surface (hero, game list, poster tiles).
//
// Why a cache at all: the cheap badges are field reads, but Documents wants the Manuals folder, the
// save badges want the vault, MAME High Scores wants hiscore.dat. Evaluated per selection that was
// fine for one game and hopeless for a 5000-row list; evaluated once in the background it costs a few
// hundred milliseconds of a worker thread and every surface afterwards reads an int.
//
// WHAT IS STORED, and what is not:
//   • per game — one int, the COMBINATION id, living in GameRow (Tier 1: it survives the launch-time
//     drop, and costs 4 bytes rather than the ~630 an object graph per game used to);
//   • per distinct combination — ~7 packed bytes in BadgeTable;
//   • nothing else. The enabled filter and the user's draw order are applied while MATERIALISING, for
//     the ~40 rows actually on screen, so toggling a badge or reordering invalidates no state at all.
//
// Two modes (Options ▸ Display):
//   • post-load (default) — the pass starts once the window is up; badges appear as platforms land.
//   • at load — the same pass, started during boot, so the first list is already badged.
// Either way a game the pass has not reached is evaluated on the spot when asked for, so no surface
// has to know where the pass is.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Badges;

internal static class BadgeEngine
{
    /// <summary>The packed library-wide state. Public because the list reads combination ids and the
    /// snapshot writer serialises it.</summary>
    public static readonly BadgeTable Table = new();

    // Games not backed by a store row (plugin-provided, synthetic) have nowhere to keep their int.
    private static readonly ConcurrentDictionary<Guid, int> _fallback = new();

    private static readonly BadgeContext _adhoc = new(indexManuals: false);   // UI-thread, one game at a time
    private static readonly object _adhocLock = new();
    private static readonly byte[] _adhocSlots = new byte[512];
    private static int _passRunning;
    private static Func<IReadOnlyList<IGame>>? _snapshot;

    /// <summary>Raised (off the UI thread) as the background pass publishes batches, and once when it
    /// ends. Surfaces marshal to their own thread and repaint.</summary>
    public static event Action? Changed;

    /// <summary>True once the full-library pass has finished at least once.</summary>
    public static bool PassComplete { get; private set; }

    /// <summary>Distinct combinations known (diagnostics).</summary>
    public static int CacheCount => Table.ComboCount;

    /// <summary>The widest ENABLED badge count in the library — the list sizes every strip on it so
    /// the titles line up. Memoised against the settings version: it walks the combinations, not the
    /// games, so it is microseconds, but it is asked for on every row-image rebuild.</summary>
    public static int MaxEnabledSlots
    {
        get
        {
            int v = BadgeSettings.Version;
            if (_maxSlotsVersion != v || _maxSlotsCombos != Table.ComboCount)
            {
                _maxSlots = Table.MaxEnabledSlots();
                _maxSlotsVersion = v; _maxSlotsCombos = Table.ComboCount;
            }
            return _maxSlots;
        }
    }
    private static int _maxSlots, _maxSlotsVersion = -1, _maxSlotsCombos = -1;

    // ── reads ────────────────────────────────────────────────────────────────

    /// <summary>The combination id of a game: the identity of its badge set, and the key every
    /// surface caches on. <see cref="BadgeTable.Unknown"/> when the pass has not reached it.</summary>
    public static int Combo(IGame? game)
    {
        if (game is HostGame hg) return hg.BadgeCombo;
        if (game == null) return BadgeTable.Unknown;
        return Guid.TryParse(Safe(() => game.Id) ?? "", out var id) && _fallback.TryGetValue(id, out var c)
             ? c : BadgeTable.Unknown;
    }

    /// <summary>Same, but evaluates the game on the spot when the pass has not reached it.</summary>
    public static int ComboOrEvaluate(IGame? game)
    {
        if (game == null) return BadgeTable.Unknown;
        int c = Combo(game);
        return c != BadgeTable.Unknown ? c : Evaluate(game);
    }

    /// <summary>Every badge that applies to the game (computed here if the pass hasn't reached it).
    /// Not filtered by the Badges menu — see <see cref="Visible"/>.</summary>
    public static BadgeHit[] Get(IGame? game) => Table.Materialize(ComboOrEvaluate(game), filtered: false);

    /// <summary>The badges to DRAW: applicable, enabled in the Badges menu, in the user's order.</summary>
    public static IReadOnlyList<BadgeHit> Visible(IGame? game) => Table.Materialize(ComboOrEvaluate(game), filtered: true);

    /// <summary>Same, but NEVER computes: a game the pass hasn't reached yet answers empty. This is
    /// what the bulk surfaces (list rows, poster tiles) use — asking them to evaluate thousands of
    /// games on the UI thread is exactly what the background pass exists to avoid. They repaint on
    /// <see cref="Changed"/>.</summary>
    public static IReadOnlyList<BadgeHit> VisibleCached(IGame? game) => Table.Materialize(Combo(game), filtered: true);

    /// <summary>One-line "why does this game show no badges?" answer (diagnostics).</summary>
    public static string Diag(IGame game)
    {
        string raw = Safe(() => game.Id) ?? "";
        int combo = Combo(game);
        var all = Table.Materialize(combo, filtered: false);
        var vis = Table.Materialize(combo, filtered: true);
        return $"id='{raw}' row={game is HostGame} combo={combo} hits={all.Length} enabled={vis.Length} "
             + $"[{string.Join(",", all.Select(x => x.Id))}]";
    }

    // ── writing ──────────────────────────────────────────────────────────────

    private static int Evaluate(IGame game)
    {
        int combo;
        lock (_adhocLock)   // the ad-hoc context carries per-game memos; one caller at a time
        {
            _adhoc.BeginGame(game);
            int n = BadgeCatalog.EvaluatePacked(game, _adhoc, Table, _adhocSlots);
            combo = Table.Intern(_adhocSlots, n);
        }
        SetCombo(game, combo);
        return combo;
    }

    private static void SetCombo(IGame game, int combo)
    {
        _dirty = 1;
        if (game is HostGame hg) { hg.BadgeCombo = combo; return; }
        if (Guid.TryParse(Safe(() => game.Id) ?? "", out var id) && id != Guid.Empty) _fallback[id] = combo;
    }

    private static int _dirty;

    /// <summary>Write the pass result to disk if anything moved since the last write. Called after the
    /// store flushes to XML — never before: the snapshot's validity stamp carries those files'
    /// timestamps, so a snapshot written first would be invalidated by our own flush.</summary>
    public static void SaveSnapshot(bool force = false)
    {
        if (!PassComplete) return;
        if (Interlocked.Exchange(ref _dirty, 0) == 0 && !force) return;
        BadgeSnapshot.Save(Store, Table);
    }

    // ── invalidation ─────────────────────────────────────────────────────────

    /// <summary>A game changed (edit, favourite toggle, install state). Its badges are recomputed on
    /// next read.</summary>
    public static void Invalidate(IGame? game)
    {
        if (game != null) SetCombo(game, BadgeTable.Unknown);
    }

    /// <summary>Re-evaluate ONE game now and publish it. Not just an eviction: the list and the tiles
    /// read the cache and never compute, so an evicted game would show NO badge until the next full
    /// pass.</summary>
    public static void Recompute(IGame? game)
    {
        if (game == null) return;
        try { Evaluate(game); } catch { }
    }

    /// <summary>Drop a game that no longer exists.</summary>
    public static void Forget(Guid id) => _fallback.TryRemove(id, out _);

    /// <summary>Re-run the whole pass (the bulk escape hatch: past a few dozen games it is cheaper
    /// than the individual recomputes, because it indexes each platform's media once).</summary>
    public static void RestartPass()
    {
        Interlocked.Increment(ref _generation);   // a pass in flight is now working on stale premises
        var snapshot = _snapshot;
        if (snapshot != null) StartPass(snapshot, forceRecompute: true);
    }

    /// <summary>Everything changed (a custom badge was added or edited, the pack was swapped, the
    /// controller catalog moved). The table is emptied and the pass re-run: the bulk surfaces read
    /// combination ids and NEVER compute, so an empty table means "no badges anywhere" until a pass
    /// refills it.</summary>
    public static void InvalidateAll()
    {
        Interlocked.Increment(ref _generation);   // BEFORE clearing: a pass in flight must see it move
        Table.Clear();
        _fallback.Clear();
        Store?.ClearBadgeCombos();
        BadgeSnapshot.Delete();       // the catalog itself moved: the file on disk is about it, not just stale
        PassComplete = false;
        BadgeContext.InvalidateControllers();
        Changed?.Invoke();
        var snapshot = _snapshot;
        if (snapshot != null) StartPass(snapshot, forceRecompute: true);
    }

    private static GameStore? Store => (PluginHelper.DataManager as HostDataManagerXml)?.Store;

    // ── the background pass ──────────────────────────────────────────────────

    // Bumped by every InvalidateAll/RestartPass. A pass captures it on entry and compares on exit:
    // a mismatch means someone cleared or re-premised the state UNDER the running pass — the rows it
    // wrote before the clear are gone, so its "complete" result is a mix that must neither be
    // published as complete nor snapshotted. StartPass returning early when a pass is already running
    // used to silently DROP such a request; now the running pass itself notices and goes again.
    private static int _generation;

    /// <summary>Compute the whole library, off the UI thread. No-op when one is already running —
    /// safely: the running pass re-runs itself if the state was invalidated under it.
    /// Games are handed in as a snapshot — the caller owns "which games exist".</summary>
    public static void StartPass(Func<IReadOnlyList<IGame>> snapshot) => StartPass(snapshot, forceRecompute: false);

    private static void StartPass(Func<IReadOnlyList<IGame>> snapshot, bool forceRecompute)
    {
        _snapshot = snapshot;   // remembered so an invalidation can re-run the pass on its own
        if (Interlocked.Exchange(ref _passRunning, 1) == 1) return;
        Task.Run(() =>
        {
            using var job = BackgroundJobs.Enter("badges");
            try
            {
                bool recompute = forceRecompute;
                for (int attempt = 0; attempt < 5; attempt++)   // bounded: a hot loop beats a livelock
                {
                    int gen = Volatile.Read(ref _generation);
                    // The pass is the expensive half of the badge system and the ONLY thing that
                    // grows with the library — so it is skipped outright when last session's result
                    // is still valid. A restart the user forced (a custom badge changed, the
                    // controller catalog moved) never takes that path: recomputing is the point.
                    if (!recompute && BadgeSnapshot.TryLoad(Store, Table)) { Restored(); _dirty = 0; }
                    else Pass(_snapshot?.Invoke() ?? snapshot());
                    if (Volatile.Read(ref _generation) == gen)
                    {
                        _doneGeneration = gen;
                        PassComplete = true;
                        SaveSnapshot(force: recompute);
                        break;
                    }
                    LbLog.Info("badges", "pass invalidated while running — going again");
                    recompute = true;   // whatever invalidated us deleted the snapshot too
                }
            }
            catch (Exception ex) { LbLog.Warn("badges", "pass failed: " + ex.Message); }
            finally
            {
                Volatile.Write(ref _passRunning, 0);
                PassComplete = true;
                Changed?.Invoke();
                // An invalidation can still land between the loop's last generation check and the
                // flag release above — its StartPass saw the flag up and returned. Now that the flag
                // is down, that request would be lost; catching it here closes the last window.
                if (Volatile.Read(ref _generation) != _doneGeneration)
                {
                    var again = _snapshot;
                    if (again != null) StartPass(again, forceRecompute: true);
                }
            }
        });
    }

    private static int _doneGeneration;   // generation of the last pass that ran to completion

    // A restored snapshot brings the combinations back but not how many games carry each — the strip
    // cache fills itself with the ones that pay best, so they are counted here (one pass over an int
    // per row, milliseconds even at 300k).
    private static void Restored()
    {
        var store = Store;
        if (store == null) return;
        var counts = new int[Table.ComboCount + 2];
        foreach (ref readonly var row in store.Rows.AsSpan())
            if (row.BadgeCombo > 0 && row.BadgeCombo < counts.Length) counts[row.BadgeCombo]++;
        Table.SetOccurrences(counts);
        LbLog.Info("badges", CacheReport(store.Rows.Length));
    }

    private static void Pass(IReadOnlyList<IGame> games)
    {
        if (games == null || games.Count == 0) return;
        Data.Mem.Report("before badge pass");
        var sw = Stopwatch.StartNew();
        // Grouped by platform so the manual index is built once per platform and dropped as we move on
        // (a single context walks the whole pass, holding one index per platform it has met).
        // Re-read the pack folder: art added or replaced since the last pass would otherwise stay
        // unseen, and the evaluation SKIPS a badge whose image is missing.
        BadgeImages.Reset();

        var ctx = new BadgeContext(indexManuals: true);
        var slots = new byte[512];
        int done = 0, withBadges = 0;
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        var occurrences = new Dictionary<int, int>();

        // Custom badges have two silent ways to show nothing — a rule set that resolves to nothing
        // evaluable, or a missing image (a badge with no art is skipped like any other). One line each
        // says which, instead of leaving the user to guess why their badge never appears.
        foreach (var c in BadgeCustomStore.All())
            LbLog.Info("badges", $"custom \"{c.Name}\" [{c.Id}]: rules={c.Rules.Count} "
                               + $"evaluable={BadgeCustomStore.IsEvaluable(c)} image={BadgeImages.Has(c.Id)}");

        var all = BadgeCatalog.All;
        foreach (var group in games.GroupBy(g => Safe(() => g.Platform) ?? ""))
        {
            foreach (var g in group)
            {
                if (g == null) continue;
                int n;
                try { ctx.BeginGame(g); n = BadgeCatalog.EvaluatePacked(g, ctx, Table, slots); }
                catch { n = 0; }
                int combo = Table.Intern(slots, n);
                SetCombo(g, combo);
                occurrences[combo] = occurrences.TryGetValue(combo, out var oc) ? oc + 1 : 1;
                done++;
                if (n > 0) withBadges++;
                for (int i = 0; i < n; i++)
                {
                    int bi = slots[i * 2];
                    if (bi >= all.Count) continue;
                    string id = all[bi].Id;
                    tally[id] = tally.TryGetValue(id, out var t) ? t + 1 : 1;
                }
            }
            Changed?.Invoke();   // one repaint per platform, not per game
        }

        // The strip cache fills itself with the combinations that pay best; it should not have to walk
        // the library to find out which those are.
        var counts = new int[Table.ComboCount + 1];
        foreach (var kv in occurrences) if (kv.Key >= 0 && kv.Key < counts.Length) counts[kv.Key] = kv.Value;
        Table.SetOccurrences(counts);

        LbLog.Info("badges", $"pass done: {done} games, {withBadges} badged, {sw.ElapsedMilliseconds} ms");
        foreach (var kv in tally.OrderByDescending(k => k.Value))
            LbLog.Info("badges", $"  {kv.Key}: {kv.Value}");
        LbLog.Info("badges", CacheReport(done));
        Data.Mem.Report("after badge pass");
    }

    /// <summary>What the badge state costs now, counted rather than estimated: the packed table plus
    /// four bytes per game (which live inside GameRow, so they are free of any per-game object).</summary>
    public static string CacheReport(int games = 0)
    {
        long table = Table.Bytes;
        long perGame = 4L * games;
        long total = table + perGame;
        return $"cache: {Table.ComboCount} combos for {games} games, "
             + $"{table / 1024} KB table + {perGame / 1024} KB row indexes = {total / 1024} KB"
             + (games > 0 ? $" ({total / games} B/game)" : "");
    }

    /// <summary>How many DISTINCT badge combinations the library produces — the number the whole
    /// design rests on. Kept as a diagnostic because it is the one figure that decides whether the
    /// packed table pays or degenerates to one entry per game.</summary>
    public static string CombosReport()
    {
        var occ = Table.Occurrences;
        int n = 0, games = 0;
        foreach (var c in occ) { if (c > 0) n++; games += c; }
        var top = occ.OrderByDescending(c => c).Take(5).Sum();
        return $"combos: {n} distinct for {games} games, top5 cover {(games == 0 ? 0 : top * 100 / games)}%";
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
}
