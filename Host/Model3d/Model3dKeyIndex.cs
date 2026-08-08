// The GameCache-companion 3D index: for every game, its CURRENT bake key and whether the cache holds
// the GLB / the PNG sidecar — so the instant/post-load pipeline answers "3D available?" from RAM.
//
// WHY: the instant image path runs for every game a fast scroll transits; resolving the key there
// (art slots, platform settings, per-slot stats) froze the transit loader. This index moves ALL of
// that to ONE background pass hooked to the GameCache's ready signal — which also fires after the
// post-game rebuild (UnloadGameCacheDuringGame wipes RAM), so the index rides the same lifecycle.
//
// THE UNIFIED PASS (RebuildAll) — one walk, no duplicate work:
//   1. keys    — Model3dCache.Resolve for every game (THE single source of truth — never a re-
//                implementation), with art-slot paths already warm in the GameCache and sizes/mtimes
//                served from ONE bulk Everything query (FileInfo fallback per miss / no Everything);
//   2. presence— ONE enumeration of cache\3d; a name matching a current key sets HasGlb/HasPng;
//   3. sweep   — a GLB matching NO current key is stale, a PNG without its GLB is an orphan → deleted
//                (destructive part ONCE per launch, gated on the CleanModel3d opt-out — this REPLACES
//                the old per-file SweepStale walk with its ReadInfo + per-game Resolve);
//   4. repair  — a GLB whose sidecar PNG is missing gets it re-extracted right here.
//
// LIVE HOOKS keep it exact between rebuilds: a bake sets the flags; a sidecar restore sets HasPng;
// Delete-all clears the flags; the 3D editors (platform settings, per-game settings, Image Selection)
// and the 16:9 flip (the aspect is part of the key) re-key their scope. Everything is generation-
// tokened: a full rebuild invalidates in-flight partial recomputes.
//
// Degraded mode (host GameCache disabled): the index never becomes Ready and consumers fall back to
// Model3dCache.CachedGlbForInstant (the header-scan index) / plain Resolve.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Model3d;

internal static class Model3dKeyIndex
{
    internal sealed record Entry(string Key, bool HasGlb, bool HasPng);

    private static readonly object _lock = new();
    private static Dictionary<string, Entry> _byGame = new(StringComparer.OrdinalIgnoreCase);   // gameId → entry
    private static Dictionary<string, string> _gameByKey = new(StringComparer.OrdinalIgnoreCase); // key → gameId
    private static int _generation;
    private static int _sweepDone;   // destructive sweep once per launch

    /// <summary>True once a full pass has completed — consumers may trust Get(). Never true when the
    /// host GameCache is disabled (the pass only runs off its ready signal).</summary>
    public static bool Ready { get; private set; }

    /// <summary>The game's entry, or null when it has no possible model (no art) / unknown.</summary>
    public static Entry? Get(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;
        lock (_lock) return _byGame.TryGetValue(gameId!, out var e) ? e : null;
    }

    // ── live hooks ───────────────────────────────────────────────────────────

    /// <summary>A bake just wrote &lt;key&gt;.glb + .png for this game.</summary>
    public static void NotifyBaked(string gameId, string key)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            if (_byGame.TryGetValue(gameId, out var old) && old.Key.Length > 0) _gameByKey.Remove(old.Key);
            _byGame[gameId] = new Entry(key, true, true);
            _gameByKey[key] = gameId;
        }
    }

    /// <summary>A missing sidecar PNG was just re-extracted beside its GLB.</summary>
    public static void NotifySidecar(string glbPath)
    {
        string key = Path.GetFileNameWithoutExtension(glbPath);
        lock (_lock)
        {
            if (_gameByKey.TryGetValue(key, out var gid) && _byGame.TryGetValue(gid, out var e))
                _byGame[gid] = e with { HasPng = true };
        }
    }

    /// <summary>Options → Caches "Delete all": every model file is gone; the keys stay valid.</summary>
    public static void NotifyAllDeleted()
    {
        lock (_lock)
            foreach (var kv in _byGame.ToArray())
                _byGame[kv.Key] = kv.Value with { HasGlb = false, HasPng = false };
    }

    /// <summary>Re-read cache\3d and resync the presence flags (after a manual stale-sweep).</summary>
    public static void RefreshPresence()
    {
        var names = ListCacheDir();
        lock (_lock)
            foreach (var kv in _byGame.ToArray())
                _byGame[kv.Key] = kv.Value with
                {
                    HasGlb = names.Contains(kv.Value.Key + ".glb"),
                    HasPng = names.Contains(kv.Value.Key + ".png"),
                };
    }

    // ── rebuild entry points ─────────────────────────────────────────────────

    /// <summary>Full pass — hook this to the GameCache global-ready signal (boot + post-game rebuild).</summary>
    public static void KickAll() => Kick(null, null);

    /// <summary>Re-key one platform's games (3D platform settings changed, or a platform-scoped
    /// GameCache rebuild landed — media edits arrive through that path).</summary>
    public static void KickPlatform(string platform) { if (!string.IsNullOrEmpty(platform)) Kick(platform, null); }

    /// <summary>Re-key a single game (its 3D settings or Image Selection changed).</summary>
    public static void KickGame(IGame g) { if (g != null) Kick(null, g); }

    private static void Kick(string? platform, IGame? single)
    {
        int gen = System.Threading.Interlocked.Increment(ref _generation);
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                if (single != null) RecomputeGames(new[] { single }, gen, partial: true);
                else if (platform != null)
                {
                    var games = PluginHelper.DataManager?.GetAllGames()
                        ?.Where(g => string.Equals(Safe(() => g.Platform), platform, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (games is { Length: > 0 }) RecomputeGames(games, gen, partial: true);
                }
                else
                {
                    var games = PluginHelper.DataManager?.GetAllGames();
                    if (games is { Length: > 0 }) RebuildAll(games, gen);
                }
            }
            catch (Exception ex) { Console.WriteLine("[model3d] index: " + ex.Message); }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
    }

    // ── the unified pass ─────────────────────────────────────────────────────

    private static void RebuildAll(IGame[] games, int gen)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ctx = new Model3dCache.ResolveContext { Stat = BuildStatProvider(out bool everything) };

        // 1. keys — the ONE place the whole library's keys are computed.
        var byGame = new Dictionary<string, Entry>(games.Length, StringComparer.OrdinalIgnoreCase);
        var gameByKey = new Dictionary<string, string>(games.Length, StringComparer.OrdinalIgnoreCase);
        // Resolve() asks the same few hundred art directories once PER GAME — the same question with a
        // different title each time. Under this scope each of them is read once and answered from RAM
        // for the rest of the walk; the memo is dropped at the closing brace.
        using (Media.MediaResolver.ScopedDirCache())
            foreach (var g in games)
            {
                if (gen != _generation) return;   // superseded by a newer rebuild
                try
                {
                    var idn = Model3dCache.Resolve(g, ctx);
                    if (idn is { HasArt: true })
                    {
                        byGame[idn.GameId] = new Entry(idn.Key, false, false);
                        gameByKey[idn.Key] = idn.GameId;
                    }
                }
                catch { }
            }

        // 2. presence (+ 3. sweep, once per launch + 4. sidecar repair) — ONE directory walk.
        bool sweep = System.Threading.Interlocked.Exchange(ref _sweepDone, 1) == 0
                     && LiteBoxConfig.LoadForExe().GetBool("CleanModel3d", true);
        int stale = 0, orphans = 0, repaired = 0;
        var repair = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Model3dCache.Dir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { try { File.Delete(f); } catch { } continue; }
                bool isGlb = name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
                bool isPng = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                if (!isGlb && !isPng) continue;
                string key = Path.GetFileNameWithoutExtension(name);
                if (gameByKey.TryGetValue(key, out var gid))
                {
                    var e = byGame[gid];
                    byGame[gid] = isGlb ? e with { HasGlb = true } : e with { HasPng = true };
                }
                else if (sweep)
                {
                    // No current key owns this file: a stale GLB (art/settings changed, game gone) or
                    // an already-orphaned PNG. This replaces the old per-file ReadInfo+Resolve sweep.
                    try { File.Delete(f); if (isGlb) stale++; else orphans++; } catch { }
                }
            }
            foreach (var kv in byGame)
                if (kv.Value.HasGlb && !kv.Value.HasPng)
                    repair.Add(Path.Combine(Model3dCache.Dir, kv.Value.Key + ".glb"));
        }
        catch (Exception ex) { Console.WriteLine("[model3d] index walk: " + ex.Message); }

        if (gen != _generation) return;
        lock (_lock) { _byGame = byGame; _gameByKey = gameByKey; Ready = true; }

        // 4. sidecar repair AFTER publishing (ReadThumbPng → NotifySidecar updates the live entries).
        foreach (var glb in repair)
        {
            if (gen != _generation) break;
            try { if (Model3dCache.ReadThumbPng(glb) != null) repaired++; } catch { }
        }

        DumpKeys(byGame);

        int nGlb = 0, nPng = 0;
        lock (_lock) foreach (var e in _byGame.Values) { if (e.HasGlb) nGlb++; if (e.HasPng) nPng++; }
        Console.WriteLine($"[model3d] index: {byGame.Count} bakeable game(s), {nGlb} glb, {nPng} png"
            + (sweep ? $", swept {stale} stale + {orphans} orphan(s)" : "")
            + (repaired > 0 ? $", {repaired} sidecar(s) repaired" : "")
            + $" ({sw.ElapsedMilliseconds} ms, everything={everything})");
    }

    // Partial re-key (single game / one platform): keys recomputed, presence checked per file — the
    // set is small, per-file File.Exists is fine here.
    private static void RecomputeGames(IGame[] games, int gen, bool partial)
    {
        var ctx = new Model3dCache.ResolveContext { Stat = BuildStatProvider(out _) };
        // Worth memoising for a PLATFORM re-key (hundreds of games over the same directories), never for
        // a single game: reading a whole directory to answer one question is the wrong trade.
        using (games.Length > 1 ? Media.MediaResolver.ScopedDirCache() : null)
        foreach (var g in games)
        {
            if (gen != _generation) return;
            string id = Safe(() => g.Id) ?? "";
            if (id.Length == 0) continue;
            Entry? entry = null;
            try
            {
                var idn = Model3dCache.Resolve(g, ctx);
                if (idn is { HasArt: true })
                {
                    string glbPath = idn.GlbPath;
                    bool hasGlb; try { hasGlb = File.Exists(glbPath); } catch { hasGlb = false; }
                    bool hasPng; try { hasPng = File.Exists(Model3dCache.PngPathFor(glbPath)); } catch { hasPng = false; }
                    entry = new Entry(idn.Key, hasGlb, hasPng);
                }
            }
            catch { }
            lock (_lock)
            {
                if (_byGame.TryGetValue(id, out var old) && old.Key.Length > 0) _gameByKey.Remove(old.Key);
                if (entry == null) _byGame.Remove(id);
                else { _byGame[id] = entry; _gameByKey[entry.Key] = id; }
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Opt-in oracle (LITEBOX_3DKEYDUMP=1): every game's key, sorted by game id, into
    // Core\litebox\model3d-keys.txt. The pass costs seconds and touches the whole library through a
    // long resolution chain, so any change to that chain — a faster listing, a different art source —
    // has to prove it produced the SAME keys, not merely the same counts (two errors cancel out in a
    // count). Writes nothing at all without the variable.
    private static void DumpKeys(Dictionary<string, Entry> byGame)
    {
        if (Environment.GetEnvironmentVariable("LITEBOX_3DKEYDUMP") is not { Length: > 0 }) return;
        try
        {
            string path = Path.Combine(LiteBoxPaths.Data, "model3d-keys.txt");
            File.WriteAllLines(path, byGame.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                           .Select(kv => kv.Key + "  " + kv.Value.Key));
            Console.WriteLine($"[model3d] key dump → {path} ({byGame.Count} entries)");
        }
        catch (Exception ex) { Console.WriteLine("[model3d] key dump failed: " + ex.Message); }
    }

    // One bulk Everything query over the LB image tree → RAM dict; per-path misses (art outside the
    // tree, custom spine files) fall back to FileInfo inside Resolve's Slot().
    private static Func<string, (long size, long mtimeTicks)?>? BuildStatProvider(out bool everything)
    {
        everything = false;
        try
        {
            if (!Gc.EverythingBridge.IsEverythingAvailable()) return null;
            string root = Path.Combine(Media.MediaResolver.LbRoot ?? "", "Images");
            if (root.Length <= 7 || !Directory.Exists(root)) return null;
            var infos = Gc.EverythingBridge.GetFilesWithInfoExtended(root);
            var map = new Dictionary<string, (long, long)>(infos.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var fi in infos)
                if (fi.DateModified > DateTime.MinValue) map[fi.FullPath] = (fi.FileSize, fi.DateModified.Ticks);
            everything = map.Count > 0;
            return everything ? p => map.TryGetValue(p, out var v) ? v : null : null;
        }
        catch { return null; }
    }

    private static HashSet<string> ListCacheDir()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var f in Directory.EnumerateFiles(Model3dCache.Dir)) names.Add(Path.GetFileName(f)); } catch { }
        return names;
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
