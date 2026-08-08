// --selftest-model3d [sampleGames] [bakeGames]: checks the one property the 3D cache rests on and that
// nothing else can observe — that a model, once baked, is still judged CURRENT the next time it is looked
// at. When that fails, nothing breaks visibly: every display simply rebakes the model it already has, and
// the only trace is a [model3d] baked line in a log nobody reads.
//
// The four questions asked here are the ones a manual "empty the cache, browse, relaunch" answers, minus
// the browsing:
//   A  the key is DETERMINISTIC — resolving the same game twice gives the same key;
//   B  the key does not depend on WHERE the art came from — the game cache in memory and the directory
//      walk must agree, or a model baked while the cache was up is judged stale as soon as it is down
//      (which is what happens for the duration of every game the user launches);
//   C  a fresh bake is immediately current, and lands in the file named after the game;
//   D  asking again does NOT rewrite it — the file's timestamp is the honest witness, since a rebake
//      cannot help but touch it.
//
// D is run in the OTHER cache regime than the bake, which is the case the code is most likely to get
// wrong and the least likely to be noticed by hand.
//
// Runs inside HostBoot once the catalogue is up, before the UI. It bakes for real, into the real
// cache\3d — a handful of models, exactly as the app would have.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Diag;

internal static class Model3dSelfTest
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        if (!ok) _fail++;
        Console.WriteLine($"[selftest-model3d] {(ok ? "ok  " : "FAIL")}  {what}");
    }

    public static int Run(int sample, int bakes)
    {
        Console.WriteLine($"[selftest-model3d] sample={sample} bakes={bakes}  cache dir={Model3d.Model3dCache.Dir}");

        IGame[] all;
        try { all = PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>(); }
        catch (Exception ex) { Console.WriteLine("[selftest-model3d] no catalogue: " + ex.Message); return 1; }
        if (all.Length == 0) { Console.WriteLine("[selftest-model3d] no games — nothing to check"); return 1; }

        // Spread the sample over the whole library rather than taking a prefix: the first N games are one
        // platform, and platform settings are half of what the key is made of.
        var picked = new List<IGame>();
        int step = Math.Max(1, all.Length / Math.Max(1, sample));
        for (int i = 0; i < all.Length && picked.Count < sample; i += step)
        {
            var idn = Safe(() => Model3d.Model3dCache.Resolve(all[i]));
            if (idn != null && idn.HasArt) picked.Add(all[i]);
        }
        Console.WriteLine($"[selftest-model3d] {picked.Count} game(s) with art, out of {all.Length} in the library");
        if (picked.Count == 0) { Console.WriteLine("[selftest-model3d] no game has case art — cannot judge"); return 1; }

        bool cacheWasReady = Safe(() => Gc.GameCache.IsGlobalReady);
        Console.WriteLine($"[selftest-model3d] game cache ready at start = {cacheWasReady}");

        // ── A: the key is deterministic ──────────────────────────────────────
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int unstable = 0;
        foreach (var g in picked)
        {
            var a = Safe(() => Model3d.Model3dCache.Resolve(g));
            var b = Safe(() => Model3d.Model3dCache.Resolve(g));
            if (a == null || b == null) continue;
            first[a.GameId] = a.Key;
            if (!string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase))
            {
                unstable++;
                if (unstable <= 3) Console.WriteLine($"[selftest-model3d]   unstable: \"{a.Title}\" {a.Key} vs {b.Key}");
            }
        }
        Check(unstable == 0, $"A  key is deterministic ({picked.Count} games, {unstable} unstable)");

        // ── C: bake a few, and they must be current straight away ────────────
        var baked = new List<(IGame g, string path, DateTime stamp)>();
        foreach (var g in picked.Take(Math.Max(0, bakes)))
        {
            string? path = Safe(() => Model3d.Model3dCache.Ensure(g));
            string title = Safe(() => g.Title) ?? "?";
            if (path == null) { Check(false, $"C  bake produced a model for \"{title}\""); continue; }

            string id = Safe(() => g.Id) ?? "";
            Check(string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase),
                  $"C  \"{title}\" is stored under its game id ({Path.GetFileName(path)})");

            var idn = Safe(() => Model3d.Model3dCache.Resolve(g));
            Check(idn != null && Model3d.Model3dCache.IsCurrent(idn), $"C  \"{title}\" is current right after baking");
            try { baked.Add((g, path, File.GetLastWriteTimeUtc(path))); } catch { }
        }
        if (baked.Count == 0) { Console.WriteLine("[selftest-model3d] nothing baked — B and D cannot be judged"); return _fail == 0 ? 1 : _fail; }

        // ── B: switch the art source, the key must not move ──────────────────
        // This is the regime change the app goes through on its own: the cache is dropped while a game
        // runs and rebuilt afterwards. A key that follows the source would rebake the whole library on
        // every game exit.
        bool flipped = FlipCacheRegime(cacheWasReady);
        bool nowReady = Safe(() => Gc.GameCache.IsGlobalReady);
        Console.WriteLine($"[selftest-model3d] game cache ready now = {nowReady} (regime {(flipped ? "CHANGED" : "unchanged")})");

        int moved = 0;
        foreach (var g in picked)
        {
            var idn = Safe(() => Model3d.Model3dCache.Resolve(g));
            if (idn == null || !first.TryGetValue(idn.GameId, out var was)) continue;
            if (!string.Equals(was, idn.Key, StringComparison.OrdinalIgnoreCase))
            {
                moved++;
                if (moved <= 3) Console.WriteLine($"[selftest-model3d]   moved: \"{idn.Title}\" {was} -> {idn.Key}");
            }
        }
        if (!flipped)
            Console.WriteLine("[selftest-model3d] note: the regime could not be changed, so B only repeats A");
        Check(moved == 0, $"B  key survives the art source changing ({picked.Count} games, {moved} moved)");

        // ── D: asking again must not rewrite the file ────────────────────────
        int rebaked = 0;
        foreach (var (g, path, stamp) in baked)
        {
            Safe(() => Model3d.Model3dCache.Ensure(g));
            DateTime now;
            try { now = File.GetLastWriteTimeUtc(path); } catch { continue; }
            if (now != stamp)
            {
                rebaked++;
                Console.WriteLine($"[selftest-model3d]   rebaked: \"{Safe(() => g.Title)}\" {stamp:HH:mm:ss.fff} -> {now:HH:mm:ss.fff}");
            }
        }
        Check(rebaked == 0, $"D  a second look does not rebake ({baked.Count} models, {rebaked} rewritten)");

        Console.WriteLine(_fail == 0
            ? "[selftest-model3d] PASS"
            : $"[selftest-model3d] FAILED ({_fail} check(s))");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Put the art source in the other regime and wait for it. Returns false when it could not be
    /// changed (cache disabled and refusing to build, or never becoming ready) — the caller then knows B
    /// proved nothing rather than believing it passed.</summary>
    private static bool FlipCacheRegime(bool wasReady)
    {
        try
        {
            // ClearForMemory is exactly what the app does when a game starts — the regime this is meant
            // to reproduce, not an approximation of it.
            if (wasReady) { Gc.HostGameCache.ClearForMemory(); return !Gc.GameCache.IsGlobalReady; }
            Gc.HostGameCache.Enabled = true;
            Gc.HostGameCache.Build();
            for (int i = 0; i < 600 && !Gc.GameCache.IsGlobalReady; i++) System.Threading.Thread.Sleep(100);
            return Gc.GameCache.IsGlobalReady;
        }
        catch (Exception ex) { Console.WriteLine("[selftest-model3d] regime flip: " + ex.Message); return false; }
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
}
