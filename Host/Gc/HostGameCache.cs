// Direct accessor over the host-backported GameCache (Host/Gc/GameCache.cs) — NO reflection, same
// assembly. It is the 2nd-tier media source: GameCacheBridge prefers ExtendDB's own GameCache when
// that plugin is loaded, and falls back to this host cache otherwise (then to on-demand IO). Also
// owns the cache lifecycle (build at boot / clear at game launch / reload at game end).

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Gc
{
    internal static class HostGameCache
    {
        /// <summary>INI option (UseGameCache) AND ExtendDB-absent. When false the host cache is never built/used.</summary>
        public static bool Enabled;

        /// <summary>INI option (UnloadGameCacheDuringGame): drop the cache while a game runs, rebuild on exit.</summary>
        public static bool UnloadDuringGame;

        public static bool Ready(string plat)
            => Enabled && GameCache.IsGlobalReady && !string.IsNullOrEmpty(plat)
               && GameCache.Platforms != null && GameCache.Platforms.ContainsKey(plat);

        private static GameCacheGame Game(string plat, Guid id)
        {
            try
            {
                if (GameCache.Platforms != null && GameCache.Platforms.TryGetValue(plat, out var p) && p != null
                    && p.GamesByUUID != null && p.GamesByUUID.TryGetValue(id, out var g)) return g;
            }
            catch { }
            return null;
        }

        public static string BestImage(string plat, Guid id, string imageType)
        { try { return Game(plat, id)?.GetBestImageOfType(imageType)?.FullPath; } catch { return null; } }

        public static string BestImageTypeFirst(string plat, Guid id, string regroupement)
        { try { return Game(plat, id)?.GetBestImageTypeFirst(regroupement)?.FullPath; } catch { return null; } }

        /// <summary>The ★★ pick as a REF (path + lazily-memoised FileSize) — lets the thumb GC build its
        /// valid-set without disk stats when the cache was Everything-built (sizes come from the index).</summary>
        public static GameCacheImageRef BestImageRefTypeFirst(string plat, Guid id, string regroupement)
        { try { return Game(plat, id)?.GetBestImageTypeFirst(regroupement); } catch { return null; } }

        public static List<string> AllImagesTypeFirst(string plat, Guid id, string regroupement, int max)
        {
            var res = new List<string>();
            try
            {
                var g = Game(plat, id); if (g == null) return res;
                var list = g.GetAllImagesTypeFirst(regroupement, max);
                if (list != null) foreach (var r in list) if (r?.FullPath is { Length: > 0 } s) res.Add(s);
            }
            catch { }
            return res;
        }

        /// <summary>Every image file the cache knows for the game, all types and regions — the list a
        /// delete or an audit works from. Empty when the platform isn't cached.</summary>
        public static List<string> AllImagePaths(string plat, Guid id)
        { try { return Game(plat, id)?.AllImagePaths() ?? new List<string>(); } catch { return new List<string>(); } }

        public static string Video(string plat, Guid id, string subDir)
        {
            try
            {
                var list = Game(plat, id)?.FindVideos(subDir);
                if (list != null) foreach (var v in list) if (v?.FullPath is { Length: > 0 } s) return s;
            }
            catch { }
            return null;
        }

        public static bool HasAnyVideo(string plat, Guid id)
        { try { var l = Game(plat, id)?.FindAllVideos(); return l != null && l.Count > 0; } catch { return false; } }

        /// <summary>Every video of the game as REFS (path + lazily-memoised FileSize/ModifiedTicks) —
        /// lets the thumb GC build the vid- valid-set without disk stats (both fields ride the build).</summary>
        public static List<GameCacheVideoRef> AllVideoRefs(string plat, Guid id)
        { try { return Game(plat, id)?.FindAllVideos() ?? new List<GameCacheVideoRef>(); } catch { return new List<GameCacheVideoRef>(); } }

        // ── Lifecycle ─────────────────────────────────────────────────────────
        /// <summary>Build the cache (async; flips IsGlobalReady when done). No-op if disabled.
        /// Logs whether Everything is active, and the element counts once the build finishes.</summary>
        public static void Build()
        {
            try
            {
                if (!Enabled) return;
                Console.WriteLine($"[gamecache] building host cache — Everything active = {EverythingBridge.IsEverythingAvailable()}");
                GameCache.ReadyChanged -= OnReady;   // log counts on (re)build completion (idempotent subscribe)
                GameCache.ReadyChanged += OnReady;
                GameCache.Initialize();
            }
            catch { }
        }

        // Fired by GameCache when a platform (name) or the whole cache (null) becomes ready.
        private static void OnReady(string platform)
        {
            // A platform-scoped rebuild used to re-key that platform's 3D entries here. Nothing to do
            // now: models are named after their game, so a media edit cannot move one — it can only make
            // one out of date, which the currency check catches when the game is next looked at.
            if (platform != null) return;
            try
            {
                int plats = 0; long games = 0, images = 0, videos = 0;
                var ps = GameCache.Platforms;
                if (ps != null)
                    foreach (var kv in ps)
                    {
                        var p = kv.Value; if (p?.GamesByUUID == null) continue;
                        plats++;
                        foreach (var g in p.GamesByUUID.Values)
                        {
                            games++;
                            if (g?.Images != null) images += g.Images.Length;
                            if (g?.Videos != null) videos += g.Videos.Length;
                        }
                    }
                Console.WriteLine($"[gamecache] host cache ready: {plats} platforms, {games} games, {images} images, {videos} videos");
            }
            catch { }
            // The cache is settled — the degraded-thumbs mark-and-sweep can now build its valid-set
            // (zero/low-IO: sizes ride the cache). Once per process; later re-readies are no-ops.
            try { Media.ThumbGc.Kick(); } catch { }
            // The 3D index no longer hangs off this signal. It was here because computing every game's
            // bake key needed the media cache warm; naming models after their game removed the need for
            // the keys, and with them a pass that re-ran in full after every game exit. What is left is
            // the once-per-launch ownership sweep, which only needs the library to be known.
            try { Model3d.Model3dKeyIndex.SweepOnce(); } catch { }
        }

        /// <summary>Drop the whole cache to free memory (e.g. while a game runs).</summary>
        public static void ClearForMemory()
        {
            try
            {
                GameCache.IsGlobalReady = false;
                GameCache.Platforms = new Dictionary<string, GameCachePlatform>();
            }
            catch { }
            try { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } catch { }
        }

        /// <summary>Rebuild after a clear (e.g. when a game exits). No-op if disabled.</summary>
        public static void Reload() { try { if (Enabled) GameCache.RebuildAll(false); } catch { } }
    }
}
