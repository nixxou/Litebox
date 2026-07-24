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
//          does not know YET — fresh image, stale cache — must not churn). Then the video-thumb sweep
//          (SweepVideos) and the document-thumb sweep (SweepDocs) run in turn. The budget sweep stays
//          as the global backstop for webimg\. All mark structures are scoped to the run and cleared.
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

            // Per-cleaner opt-outs (Options → Caches). Read once; the GC runs once per launch anyway.
            var cfg = LiteBoxConfig.LoadForExe();
            bool doImages = cfg.GetBool("CleanThumbsImages", true);
            bool doVideos = cfg.GetBool("CleanThumbsVideo", true);
            bool doDocs = cfg.GetBool("CleanThumbsDocs", true);
            bool doWebImg = cfg.GetBool("CleanThumbsWebImg", true);
            bool doRelated = cfg.GetBool("CleanThumbsRelated", true);

            if (doImages) SweepDegraded(games);
            else Console.WriteLine("[thumbgc] degraded sweep disabled (option)");
            if (doVideos) SweepVideos(games); else Console.WriteLine("[thumbgc] video sweep disabled (option)");
            if (doDocs) SweepDocs(games); else Console.WriteLine("[thumbgc] docs sweep disabled (option)");
            if (doWebImg) SweepWebImg(); else Console.WriteLine("[thumbgc] webimg sweep disabled (option)");
            if (doRelated) SweepRelated(); else Console.WriteLine("[thumbgc] related sweep disabled (option)");
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] failed: " + ex.Message); }
    }

    private static void SweepDegraded(IGame[] games)
    {
        try
        {
            // ── mark: the ★★ pick of EVERY regroupement the bulk generator can produce (same list, same
            // resolution) — a generatable thumb must be a marked thumb, or the sweep would eat it. ──
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stats = 0;
            foreach (var g in games)
            {
                foreach (var (reg, _) in MainWindow.CacheRegroupements)
                {
                    string? src = MainWindow.CacheSourceFor(g, reg);
                    if (string.IsNullOrEmpty(src)) continue;
                    long size = SourceSize(g, reg, src, ref stats);
                    if (size < 0) continue;
                    foreach (var name in ThumbCache.FileNamesFor(src, size, ThumbCache.DefaultMaxDim, ThumbCache.FormatFor(reg)))
                        valid.Add(name);
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
            valid.Clear();          // release the mark structures before the next sweeps run
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] degraded failed: " + ex.Message); }
    }

    // Document-thumb sweep (thumbs\docs), after the video one. Documents are AdditionalApplication
    // Section=Document rows in the platform XMLs (already in RAM — zero IO to enumerate), NOT indexed by
    // the game cache; sizes/mtimes come from a one-query Everything prefetch of <LB>\Manuals\ (where LB
    // keeps documents) and one FileInfo stat only for documents living elsewhere. Keys are exact since
    // DocRenderDim froze the dimension (DPI-independent). Stored paths can be relative-to-LB-root or
    // absolute — resolved with the editor's own DocResolve so both key identically.
    private static void SweepDocs(IGame[] games)
    {
        try
        {
            // Everything prefetch: path(lower) → (size, mtime ticks). Scoped to this sweep, cleared below.
            var pre = new Dictionary<string, (long Size, long Ticks)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string root = MediaResolver.LbRoot ?? "";
                string manuals = root.Length > 0 ? Path.Combine(root, "Manuals") : "";
                if (manuals.Length > 0 && Directory.Exists(manuals) && Gc.EverythingBridge.IsEverythingAvailable())
                    foreach (var f in Gc.EverythingBridge.GetFilesWithInfoExtended(manuals))
                        pre[f.FullPath] = (f.FileSize, f.DateModified.Ticks);
            }
            catch { }

            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stats = 0, docs = 0;
            foreach (var g in games)
            {
                var apps = SafeApps(g);
                if (apps == null) continue;
                foreach (var a in apps)
                {
                    if (a is not Data.HostAdditionalApplication { IsDocument: true } h) continue;
                    string abs = EditGameWindow.DocResolve(h.ApplicationPath);
                    if (string.IsNullOrEmpty(abs)) continue;
                    docs++;
                    long size, ticks;
                    if (pre.TryGetValue(abs, out var m)) { size = m.Size; ticks = m.Ticks; }
                    else
                    {
                        stats++;
                        try { var fi = new FileInfo(abs); if (!fi.Exists) continue; size = fi.Length; ticks = fi.LastWriteTimeUtc.Ticks; }
                        catch { continue; }
                    }
                    valid.Add(EditGameWindow.DocThumbFileName(abs, size, ticks));
                }
            }
            pre.Clear();   // prefetch no longer needed once the mark is built

            int kept = 0, deleted = 0, spared = 0;
            var cutoff = DateTime.UtcNow - Grace;
            foreach (var f in Directory.GetFiles(ThumbCache.DocFolder))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // in-flight render
                bool del;
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^doc-[0-9a-f]{32}\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    if (valid.Contains(name)) { kept++; continue; }
                    try { del = File.GetLastWriteTimeUtc(f) <= cutoff; } catch { del = false; }
                    if (!del) { spared++; continue; }
                }
                else del = true;   // never a legitimate doc thumb — swept regardless of age
                try { File.Delete(f); deleted++; } catch { }
            }
            Console.WriteLine($"[thumbgc] docs: {valid.Count} valid keys over {docs} documents "
                + $"({stats} disk stats) — kept {kept}, deleted {deleted}, spared {spared} recent");
            valid.Clear();
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] docs failed: " + ex.Message); }
    }

    private static Unbroken.LaunchBox.Plugins.Data.IAdditionalApplication[]? SafeApps(IGame g)
    { try { return g.GetAllAdditionalApplications(); } catch { return null; } }

    // Web-image previews (thumbs\webimg): no local source to mark against — their "source" is a remote DB
    // row (editor stand-ins keyed MD5(WebImage.Key), 32-hex; web-UI materialised covers keyed <dbid>.<ext>).
    // Validity therefore = USE: both read paths TouchForLru on hit, and anything not touched for 30 days is
    // dropped (a re-fetch costs network, so the TTL is deliberately long — 48h would defeat multi-day
    // scraping sessions). Junk names are swept on sight as everywhere else.
    private static readonly TimeSpan WebImgTtl = TimeSpan.FromDays(30);

    private static void SweepWebImg()
    {
        try
        {
            int kept = 0, deleted = 0;
            var cutoff = DateTime.UtcNow - WebImgTtl;
            foreach (var f in Directory.GetFiles(ThumbCache.WebImgFolder))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // in-flight write
                bool conforming =
                    System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9a-f]{32}\.jpg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+\.[a-z0-9]{1,5}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bool del = !conforming;
                if (conforming)
                    try { del = File.GetLastWriteTimeUtc(f) <= cutoff; } catch { del = false; }
                if (!del) { kept++; continue; }
                try { File.Delete(f); deleted++; } catch { }
            }
            Console.WriteLine($"[thumbgc] webimg: TTL {WebImgTtl.TotalDays:0}d — kept {kept}, deleted {deleted}");
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] webimg failed: " + ex.Message); }
    }

    // Related-games card thumbs (cache\related-thumbs): already self-bounded by design (RAM LRU 300 +
    // disk count-cap 500→400 at write time, see RelatedGamesUi.RelatedThumbCache) — only the junk-name
    // rule applies here ({dbid}.jpg is the one legitimate shape).
    private static void SweepRelated()
    {
        try
        {
            string? dir = LiteBoxPaths.Dir("cache") is { } c ? Path.Combine(c, "related-thumbs") : null;
            if (dir == null || !Directory.Exists(dir)) return;
            int deleted = 0;
            foreach (var f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+\.jpg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                try { File.Delete(f); deleted++; } catch { }
            }
            if (deleted > 0) Console.WriteLine($"[thumbgc] related-thumbs: deleted {deleted} junk file(s)");
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] related failed: " + ex.Message); }
    }

    // Video-thumb sweep (thumbs\video), right after the degraded one:
    //   vid-    (local-video frames)  : mark-and-sweep — valid-set = the vid- key of EVERY video the game
    //           cache knows (path + size + mtime ride the cache build in both modes, so marking is
    //           IO-free); unknown keys older than the grace window are deleted (a replaced/renamed video
    //           re-keys, orphaning its old frame).
    //   vidweb- (web-video frames)    : short-lived editor previews keyed by DB row CRC — no local file to
    //           mark against; anything older than the grace window is deleted (re-decoded on demand).
    //   other names                   : never legitimate here (except in-flight .tmp) — deleted on sight.
    private static void SweepVideos(IGame[] games)
    {
        try
        {
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stats = 0;
            foreach (var g in games)
            {
                string plat = g.Platform;
                if (string.IsNullOrEmpty(plat) || !Gc.HostGameCache.Ready(plat) || !Guid.TryParse(g.Id, out var id)) continue;
                foreach (var v in Gc.HostGameCache.AllVideoRefs(plat, id))
                {
                    if (v?.FullPath is not { Length: > 0 }) continue;
                    long size = v.Value.FileSize, ticks = v.Value.ModifiedTicks;
                    if (size < 0) { stats++; size = v.GetFileSize(); }
                    if (ticks <= 0) { stats++; ticks = v.GetModifiedTicks(); }
                    if (size < 0 || ticks <= 0) continue;
                    valid.Add(Video.VideoThumbnailer.CacheFileName(v.FullPath, size, ticks));
                }
            }

            int kept = 0, deleted = 0, spared = 0;
            var cutoff = DateTime.UtcNow - Grace;
            bool Old(string f) { try { return File.GetLastWriteTimeUtc(f) <= cutoff; } catch { return false; } }
            foreach (var f in Directory.GetFiles(ThumbCache.VideoFolder))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // in-flight extraction
                bool del;
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^vid-[0-9a-f]{32}\.jpg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    del = !valid.Contains(name) && Old(f);
                else if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^vidweb-[0-9a-f]{32}\.jpg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    del = Old(f);
                else
                    del = true;   // never a legitimate video thumb — swept regardless of age
                if (!del) { if (valid.Contains(name)) kept++; else spared++; continue; }
                try { File.Delete(f); deleted++; } catch { }
            }
            Console.WriteLine($"[thumbgc] video: {valid.Count} valid vid- keys ({stats} disk stats) "
                + $"— kept {kept}, deleted {deleted}, spared {spared} recent");
            valid.Clear();
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] video failed: " + ex.Message); }
    }

    // <16 hex>_<digits>_<digits>[a] + .jpg/.webp — ThumbCache.KeyFor's exact shape.
    private static bool LooksLikeThumbKey(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(
               name, @"^[0-9a-f]{16}_\d+_\d+a?\.(jpg|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Size of a resolved source, cache-first: when the source IS the game cache's ★★ pick for the slot's
    // regroupement, its FileSize rides along (free when Everything-built, one memoised stat otherwise).
    // Fallback-resolved sources (cache had no pick — e.g. Box3D standing in for a missing Front) pay a stat.
    private static long SourceSize(IGame g, string regroupement, string src, ref int stats)
    {
        try
        {
            string plat = g.Platform;
            if (!string.IsNullOrEmpty(plat) && Gc.HostGameCache.Ready(plat) && Guid.TryParse(g.Id, out var id))
            {
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
