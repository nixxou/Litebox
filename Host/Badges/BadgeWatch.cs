// Keeping the badge cache honest while the app runs.
//
// One listener on GameStore.GameChanged — the single point every library mutation converges to,
// whoever wrote it (Edit Game, the bulk editors, the store/RA syncs, a plugin through
// ILiteBoxGame). Instrumenting IGame's setters or the UI call sites would each miss the paths they
// don't know about; this one cannot.
//
// Three things make it cheap:
//   • a SENSITIVE FIELD set — the fields the built-in badges read, plus the fields the user's custom
//     rules reference (recomputed when those rules change). A Notes edit doesn't wake anything. The
//     point isn't CPU (a dictionary remove is free) — it's not touching the save vault for nothing.
//   • COALESCING — ids pile into a set behind a short debounce, so Edit Game's forty writes on OK,
//     or a bulk edit's hundreds, collapse into one batch.
//   • a BULK ESCAPE HATCH — past a threshold, re-running the whole pass is FASTER than recomputing
//     the games one by one, because the pass indexes each platform's manuals once instead of per
//     game. 5000 games in ~300 ms beats 500 individual recomputes.
//
// What it deliberately does NOT do: react while GameStore.OptionalDropped is set. A running game
// keeps writing PlayCount/PlayTime while the display sub-entities (controller support!) are freed —
// recomputing then would cache "this game has no controller support".

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Badges;

internal static class BadgeWatch
{
    /// <summary>Past this many pending games, the whole pass is cheaper than the individual recomputes.</summary>
    private const int BulkThreshold = 50;
    private const int DebounceMs = 200;

    private static readonly object _lock = new();
    private static readonly HashSet<Guid> _pending = new();
    private static System.Windows.Forms.Timer? _timer;
    private static bool _started;

    /// <summary>Games whose badges were just recomputed — the surfaces repaint only those.</summary>
    public static event Action<IReadOnlyList<Guid>>? Recomputed;

    /// <summary>Starts listening. <paramref name="resolve"/> turns an id back into the IGame the
    /// predicates need; it runs on the UI thread, like the timer.</summary>
    public static void Start(Func<Guid, IGame?> resolve)
    {
        if (_started) return;
        _started = true;
        _resolve = resolve;
        _timer = new System.Windows.Forms.Timer { Interval = DebounceMs };
        _timer.Tick += (_, _) => Flush();
        GameStore.GameChanged += OnGameChanged;
        BadgeCustomStore.Changed += () => { lock (_lock) _sensitive = null; };

        // Two inputs live outside the game store, so they get their own wire.
        Saves.SaveVault.VaultChanged += gameId =>
        {
            if (Guid.TryParse(gameId, out var id)) { lock (_lock) _pending.Add(id); }
            try { _timer?.Stop(); _timer?.Start(); } catch { }
        };
        // A controller's category decides which badge its games show — one edit re-groups the whole
        // library, so that one is a full pass, not a per-game recompute.
        ControllerCatalogStore.CatalogChanged += () =>
        {
            BadgeContext.InvalidateControllers();
            BadgeEngine.RestartPass();
        };
    }

    private static Func<Guid, IGame?>? _resolve;

    private static void OnGameChanged(Guid id, string field)
    {
        // field == "" means something coarse changed (a child collection, a move, an add/delete):
        // no way to tell whether it matters, so it always counts.
        if (field.Length > 0 && !Sensitive().Contains(field)) return;

        // The MAME memo answers from a per-game cache keyed on the rom path and the emulator; a
        // change to either makes it lie, and nothing else clears it per game.
        if (field is "ApplicationPath" or "EmulatorId") GameSortCatalog.ForgetMameSupport(id.ToString());

        lock (_lock) _pending.Add(id);
        LbLog.Info("badges", $"watch: {field} on {id} queued");
        try { _timer?.Stop(); _timer?.Start(); } catch { }
    }

    private static void Flush()
    {
        try { _timer?.Stop(); } catch { }
        if (GameStore.OptionalDropped)
        { LbLog.Info("badges", "watch: flush deferred (a game is running)"); try { _timer?.Start(); } catch { } return; }

        Guid[] ids;
        lock (_lock) { ids = _pending.ToArray(); _pending.Clear(); }
        if (ids.Length == 0) return;

        if (ids.Length > BulkThreshold) { BadgeEngine.RestartPass(); return; }

        var done = new List<Guid>(ids.Length);
        foreach (var id in ids)
        {
            var g = _resolve?.Invoke(id);
            if (g == null) { BadgeEngine.Forget(id); continue; }   // deleted → drop its entry
            BadgeEngine.Recompute(g);
            done.Add(id);
        }
        LbLog.Info("badges", $"watch: flushed {ids.Length} queued, {done.Count} recomputed");
        if (done.Count > 0) { try { Recomputed?.Invoke(done); } catch { } }
    }

    /// <summary>Recompute these games now (the Edit Game safety net: its pages also write FILES —
    /// manuals, images — which no store notification can see).</summary>
    public static void RecomputeNow(IEnumerable<IGame>? games)
    {
        if (games == null || GameStore.OptionalDropped) return;
        var done = new List<Guid>();
        foreach (var g in games)
        {
            if (g == null) continue;
            BadgeEngine.Recompute(g);
            if (Guid.TryParse(Safe(() => g.Id) ?? "", out var id)) done.Add(id);
        }
        if (done.Count > 0) { try { Recomputed?.Invoke(done); } catch { } }
    }

    // ── the sensitive field set ──────────────────────────────────────────────

    private static HashSet<string>? _sensitive;

    /// <summary>Fields whose change can alter a badge: what the built-ins read, plus whatever the
    /// user's custom rules target (their field keys are the same XML names).</summary>
    private static HashSet<string> Sensitive()
    {
        lock (_lock)
        {
            if (_sensitive != null) return _sensitive;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // built-in badges
                "Favorite", "Broken", "Hide", "Portable", "Installed", "Progress", "Source",
                "ManualPath",                       // Documents (the pinned one)
                "RetroAchievementsHash",            // Achievements
                "ApplicationPath", "EmulatorId",    // MAME High Scores
                "Platform", "Title",                // platform rules, and the title matches manuals/media
            };
            foreach (var c in BadgeCustomStore.All())
                foreach (var r in c.Rules)
                    if (!string.IsNullOrWhiteSpace(r.Field)) set.Add(r.Field.Trim());
            return _sensitive = set;
        }
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
}
