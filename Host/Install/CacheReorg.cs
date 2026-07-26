// One-shot relocation of the REBUILDABLE cache directories that historically sat loose at the
// <LB>\Core\litebox\ root into the single litebox\cache\ tree (R3 of the storage standardisation).
//
// Why MOVE and not just delete-and-let-rebuild: these are all rebuildable, but re-downloading the RA
// and store badge images or re-extracting romcache after every upgrade is a needless cost spike.
// Directory.Move is atomic on the same volume (everything here is under one folder), so a half-moved
// state can't happen.
//
// Idempotent: a dir already relocated (or never created) is skipped; if BOTH the old and new location
// exist (an interrupted run, or a stale root copy next to a newer cache copy), the cache\ copy is
// authoritative and the loose root copy is dropped. Runs at boot BEFORE anything opens these caches
// (RA service, WebView2, romcache) — so nothing holds a lock on them yet. Never throws.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host.Install;

internal static class CacheReorg
{
    // The dirs that used to be created at litebox\<name>; now LiteBoxPaths.CacheDir puts them at
    // litebox\cache\<name>. Keep in sync with the CacheDir call sites (one chokepoint per name).
    private static readonly string[] _dirs =
    {
        "romcache", "emumovies", "steam",
        "ra-cache", "ra-badges", "store-ach-cache", "store-ach-badges",
        "webview2-yt", "webview2-yt-page", "webview2-kiosk",
    };

    /// <summary>Relocate any loose root cache dir under litebox\cache\. Call once at boot, before the
    /// caches are opened. No-op on a clean/current install.</summary>
    public static void Run()
    {
        int moved = 0, dropped = 0;
        try
        {
            string root = LiteBoxPaths.Data;              // <LB>\Core\litebox
            string cache = LiteBoxPaths.Cache;            // <LB>\Core\litebox\cache (ensured)
            foreach (var name in _dirs)
            {
                string src = Path.Combine(root, name);
                string dst = Path.Combine(cache, name);
                try
                {
                    if (!Directory.Exists(src)) continue;         // nothing at the old spot
                    if (Directory.Exists(dst))
                    {
                        Directory.Delete(src, true);              // cache\ copy wins; drop the stale root copy
                        dropped++;
                    }
                    else
                    {
                        Directory.Move(src, dst);                 // atomic on the same volume
                        moved++;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[cache-reorg] {name} failed: {ex.Message}"); }
            }
            if (moved > 0 || dropped > 0)
                Console.WriteLine($"[cache-reorg] relocated {moved} cache dir(s) under litebox\\cache\\"
                                  + (dropped > 0 ? $" ({dropped} stale root copy dropped)" : ""));
        }
        catch (Exception ex) { Console.WriteLine("[cache-reorg] failed: " + ex.Message); }
    }
}
