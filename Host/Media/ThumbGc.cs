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

    // ── the mark-and-sweep's load-bearing precondition ──────────────────────────
    // Every sweep below MARKS from the game cache and then DELETES whatever is unmarked. If the cache
    // goes away between the two (a game launch clears it for memory — HostGameCache.ClearForMemory), the
    // valid-set comes out empty or half-built and the sweep eats perfectly good thumbnails. So: remember
    // whether the cache was usable when we started, and abort any sweep whose marking could have been
    // starved. Nothing is deleted on a doubt; the GC simply runs again next launch.
    private static bool _needCache;

    /// <summary>False when the cache we marked against has since been dropped → do not delete anything.</summary>
    private static bool MarkingTrustworthy()
    {
        if (!_needCache) return true;                      // cache not in use: marking is IO-based, always valid
        if (Gc.GameCache.IsGlobalReady) return true;
        // Re-arm: Kick() runs the GC once per PROCESS, so without this an abort would mean no cleanup at all
        // this session. Clearing the latch lets the next ReadyChanged — the cache rebuild after the game
        // exits — kick a fresh, complete pass.
        Interlocked.Exchange(ref _ran, 0);
        Console.WriteLine("[thumbgc] aborted: the game cache was unloaded mid-run (a game launched?) — "
                          + "deleting nothing; a fresh pass runs when the cache is rebuilt");
        return false;
    }

    private static void Run()
    {
        try
        {
            var games = PluginHelper.DataManager?.GetAllGames();
            if (games == null || games.Length == 0) return;
            _needCache = Gc.HostGameCache.Enabled && Gc.GameCache.IsGlobalReady;

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
            // 3D models: swept by Model3dKeyIndex's unified pass (same CleanModel3d opt-out) —
            // the old per-file SweepStale walk here would be duplicate work.
            // Options-db: rows of entities that no longer exist (games/emulators/playlists by guid,
            // platforms by name — their natural key).
            if (cfg.GetBool("CleanOptionsDb", true)) SweepOptionsDb(games);
            else Console.WriteLine("[thumbgc] options-db sweep disabled (option)");
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] failed: " + ex.Message); }
    }

    // Options-db orphan sweep: one live-id set per scope, straight from the data manager. Platform rows
    // are NAME-keyed (LB platforms have no guid). Global scope is never swept (SweepOrphans refuses it).
    private static void SweepOptionsDb(IGame[] games)
    {
        try
        {
            var dm = PluginHelper.DataManager;

            // A live-set is only trustworthy when the enumeration SUCCEEDED and returned something: a
            // swallowed failure (or a not-yet-populated collection) would look like "every entity is
            // gone" and wipe perfectly valid overrides. So each scope is swept ONLY on an explicit
            // success flag + non-empty set; anything else skips that scope entirely (rows survive to
            // the next launch — an orphan row is inert, a deleted override is user data lost).
            static bool Collect<T>(Func<T[]?> enumerate, Func<T, string?> idOf, HashSet<string> into)
            {
                try
                {
                    var items = enumerate();
                    if (items == null || items.Length == 0) return false;
                    foreach (var it in items) { try { if (idOf(it) is { Length: > 0 } id) into.Add(id); } catch { } }
                    return into.Count > 0;
                }
                catch { return false; }
            }

            var gameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool okGames = Collect(() => games, g => g.Id, gameIds);
            var emuIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool okEmus = Collect(() => dm?.GetAllEmulators(), e => e.Id, emuIds);
            var platNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool okPlats = Collect(() => dm?.GetAllPlatforms(), p => p.Name, platNames);
            var playlistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool okPlaylists = Collect(() => dm?.GetAllPlaylists(), p => p.PlaylistId, playlistIds);

            int n = 0;
            if (okGames) n += Data.LiteBoxOptionsDb.SweepOrphans("game", gameIds);
            if (okEmus) n += Data.LiteBoxOptionsDb.SweepOrphans("emulator", emuIds);
            if (okPlats) n += Data.LiteBoxOptionsDb.SweepOrphans("platform", platNames);
            if (okPlaylists) n += Data.LiteBoxOptionsDb.SweepOrphans("playlist", playlistIds);
            if (!okGames || !okEmus || !okPlats || !okPlaylists)
                Console.WriteLine($"[thumbgc] options-db: skipped scope(s) with no reliable live set " +
                                  $"(games={okGames} emulators={okEmus} platforms={okPlats} playlists={okPlaylists})");
            if (n > 0) Console.WriteLine($"[thumbgc] options-db: {n} orphan row(s) deleted");
        }
        catch (Exception ex) { Console.WriteLine("[thumbgc] options-db sweep failed: " + ex.Message); }
    }

    private static void SweepDegraded(IGame[] games)
    {
        try
        {
            // ── mark: the ★★ pick of EVERY regroupement the bulk generator can produce (same list, same
            // resolution) — a generatable thumb must be a marked thumb, or the sweep would eat it. ──
            // valid  = the CURRENT-format filenames (kept verbatim).
            // bases  = the ext-less keys of known sources → lets us tell an OBSOLETE file (known key, wrong
            //          container after a PNG↔WebP switch → delete now) from an UNKNOWN key (grace).
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stats = 0;
            foreach (var g in games)
            {
                foreach (var (reg, _) in MainWindow.CacheRegroupements)
                {
                    string? src = MainWindow.CacheSourceFor(g, reg);
                    if (string.IsNullOrEmpty(src)) continue;
                    long size = SourceSize(g, reg, src, ref stats);
                    if (size < 0) continue;
                    var fmt = ThumbCache.FormatFor(reg);
                    foreach (var name in ThumbCache.FileNamesFor(src, size, ThumbCache.DefaultMaxDim, fmt))
                        valid.Add(name);
                    bases.Add(ThumbCache.KeyBaseFor(src, size, ThumbCache.DefaultMaxDim, fmt));
                }
            }
            if (valid.Count == 0) return;   // degenerate mark — never wipe the folder on an empty set

            // ── sweep ──
            if (!MarkingTrustworthy()) return;   // cache vanished while marking → delete nothing
            int kept = 0, deleted = 0, obsolete = 0, spared = 0;
            var cutoff = DateTime.UtcNow - Grace;
            foreach (var f in Directory.GetFiles(ThumbCache.DegradedFolder))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // in-flight generation
                if (valid.Contains(name)) { kept++; continue; }
                if (LooksLikeThumbKey(name))
                {
                    // Known key, but this file is not the current-format one → obsolete container left by a
                    // format switch: delete immediately (the correct format regenerates on next view).
                    if (bases.Contains(Path.GetFileNameWithoutExtension(name)))
                    { try { File.Delete(f); obsolete++; } catch { } continue; }
                    // Unknown key that still LOOKS like a thumb → a fresh source the cache may not know yet:
                    // grace-protect the recent ones.
                    try { if (File.GetLastWriteTimeUtc(f) > cutoff) { spared++; continue; } } catch { }
                }
                try { File.Delete(f); deleted++; } catch { }   // junk name, or grace-expired unknown key
            }
            Console.WriteLine($"[thumbgc] degraded: {valid.Count} valid keys over {games.Length} games "
                + $"({stats} disk stats) — kept {kept}, deleted {deleted}, obsolete-format {obsolete}, spared {spared} recent");
            valid.Clear(); bases.Clear();   // release the mark structures before the next sweeps run
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

            if (!MarkingTrustworthy()) return;   // cache vanished while marking → delete nothing
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

            if (!MarkingTrustworthy()) return;   // cache vanished while marking → delete nothing
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
               name, @"^[0-9a-f]{16}_\d+_\d+a?\.(jpg|png|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
