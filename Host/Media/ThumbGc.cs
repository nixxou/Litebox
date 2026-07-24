// Mark-and-sweep GC for the DEGRADED thumb cache (Core\litebox\cache\thumbs\degraded).
//
// The age-based budget sweep in ThumbCache (500→400 MB, LastWriteTime order) deletes by GENERATION age —
// it can evict a thumb the UI shows daily while keeping one never read again. This GC replaces that
// heuristic for the degraded set with an exact reachability test:
//
//   mark : for every game, the exact cache FILENAMES the UI's consumers would request — the same
//          resolution chain as the consumers themselves (MainWindow.ResolveCacheSources → ★★ pick per
//          regroupement via the game cache, IGame fallbacks) — so by construction the sweep can never
//          delete a thumb the UI will ask for, even where our pick order differs from real LaunchBox.
//          File sizes (part of the cache key) come from the game cache when it was Everything-built
//          (zero disk stats); a stat is paid only for fallback-resolved sources / unknown sizes.
//   sweep: delete every degraded\ file whose name is not in the valid-set. In-flight .tmp files and
//          files younger than the grace window are spared (a thumb generated for a source the cache
//          does not know YET — fresh image, stale cache — must not churn). The budget sweep stays as
//          the global backstop for video\/webimg\/docs\ (no valid-set of their own yet).
//
// Runs ONCE per process, in the background, kicked when the host GameCache flips global-ready
// (HostGameCache.OnReady) — the mark needs the cache settled. Cache disabled → never runs (the
// budget sweep still protects the size).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class ThumbGc
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(48);
    private static int _ran;

    /// <summary>Run the degraded-thumbs mark-and-sweep once, in the background. Safe to call
    /// repeatedly (rebuilds after a game exits re-fire ReadyChanged) — only the first call runs.</summary>
    public static void Kick()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1) return;
        _ = Task.Run(Run);
    }

    private static void Run()
    {
        try
        {
            var games = PluginHelper.DataManager?.GetAllGames();
            if (games == null || games.Length == 0) return;

            // ── mark ──
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stats = 0;
            foreach (var g in games)
            {
                var srcs = MainWindow.ResolveCacheSources(g);
                if (srcs == null) continue;
                for (int slot = 0; slot < 3; slot++)
                {
                    string? src = srcs[slot];
                    if (string.IsNullOrEmpty(src)) continue;
                    long size = SourceSize(g, slot, src, ref stats);
                    if (size < 0) continue;
                    // slot 0 = clear logo → alpha/webp; the others → opaque/jpg (the consumers' rule).
                    valid.Add(ThumbCache.FileNameFor(src, size, ThumbCache.DefaultMaxDim, keepAlpha: slot == 0));
                }
            }
            if (valid.Count == 0) return;   // degenerate mark — never wipe the folder on an empty set

            // ── sweep ──
            int kept = 0, deleted = 0, spared = 0;
            var cutoff = DateTime.UtcNow - Grace;
            foreach (var f in Directory.GetFiles(ThumbCache.DegradedFolder))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // in-flight generation
                if (valid.Contains(name)) { kept++; continue; }
                // Grace only protects files that COULD be legitimate thumbs (a fresh source the cache does
                // not know yet). A name outside the key format (<16hex>_<size>_<dim>[a].jpg/.webp) never is
                // — stray copies, hand-dropped files — and is swept regardless of age.
                if (LooksLikeThumbKey(name))
                    try { if (File.GetLastWriteTimeUtc(f) > cutoff) { spared++; continue; } } catch { }
                try { File.Delete(f); deleted++; } catch { }
            }
            Console.WriteLine($"[thumbgc] degraded: {valid.Count} valid keys over {games.Length} games "
                + $"({stats} disk stats) — kept {kept}, deleted {deleted}, spared {spared} recent");
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] failed: " + ex.Message); }
    }

    // <16 hex>_<digits>_<digits>[a] + .jpg/.webp — ThumbCache.KeyFor's exact shape.
    private static bool LooksLikeThumbKey(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(
               name, @"^[0-9a-f]{16}_\d+_\d+a?\.(jpg|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Size of a resolved source, cache-first: when the source IS the game cache's ★★ pick for the slot's
    // regroupement, its FileSize rides along (free when Everything-built, one memoised stat otherwise).
    // Fallback-resolved sources (cache had no pick — e.g. Box3D standing in for a missing Front) pay a stat.
    private static long SourceSize(IGame g, int slot, string src, ref int stats)
    {
        try
        {
            string plat = g.Platform;
            if (!string.IsNullOrEmpty(plat) && Gc.HostGameCache.Ready(plat) && Guid.TryParse(g.Id, out var id))
            {
                string regroupement = slot == 0 ? "ClearLogo" : slot == 1 ? "Front" : "Screenshots";
                var r = Gc.HostGameCache.BestImageRefTypeFirst(plat, id, regroupement);
                if (r != null && string.Equals(r.FullPath, src, StringComparison.OrdinalIgnoreCase))
                {
                    if (r.Value.FileSize >= 0) return r.Value.FileSize;
                    stats++;
                    return r.GetFileSize();
                }
            }
        }
        catch { }
        stats++;
        try { var fi = new FileInfo(src); return fi.Exists ? fi.Length : -1; } catch { return -1; }
    }
}
