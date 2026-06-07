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
            if (platform != null) return;   // only on global-ready
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
