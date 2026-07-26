// Disk cache of baked 3D case models: one GLB per (game, resolved model settings, art sources) under
// Core\litebox\cache\3d\<md5>.glb.
//
// IDENTITY — the filename is the MD5 of a canonical MANIFEST built from:
//   • a BakerVersion salt (bump on any geometry/material/thumb pipeline change → whole cache invalidates);
//   • the RESOLVED settings map (game override → platform override → LB hardcoded defaults → ctor
//     defaults), sorted key=value — so editing a PLATFORM's model settings naturally re-keys every game;
//   • one line per art source the builders can consume (front / clear logo / spine / back / full-scan /
//     custom spine file), each `slot|path|size|mtime` or `slot|-` when absent. Path+size+mtime — the
//     dup-check poisoning taught us path+size alone can miss a same-size replacement.
// Art resolution mirrors HomeModel3d EXACTLY (same ResolveArt → ImageByTitle chain), so the key changes
// iff what the builder would consume changes. The manifest (and the game identity) is also stored inside
// the GLB (extras.litebox) — the GC and the debug UI read a file's identity from the file itself.
//
// The GC (SweepStale, kicked from ThumbGc's once-per-launch pass) deletes a cached file when its recorded
// game no longer exists or when that game's CURRENT key no longer matches the filename (art changed,
// settings changed, baker bumped). CleanAll wipes the folder outright (Options → Caches).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Model3d;

internal static class Model3dCache
{
    /// <summary>Salts every key — bump when the bake output changes (geometry, materials, thumb pose/size,
    /// GLB layout) so stale files are re-keyed away in one move.</summary>
    public const int BakerVersion = 1;

    public static string Dir
    {
        get
        {
            string d = Path.Combine(LiteBoxPaths.Dir("cache"), "3d");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>A game's resolved identity in the cache: its key, GLB path, and the pieces that formed it.
    /// <c>ImgOv</c> = the EFFECTIVE per-slot image override (null = auto / invalidated) — fed to the bake so
    /// key and textures agree by construction.</summary>
    internal sealed record Identity(string Key, string GlbPath, string Manifest, Dictionary<string, string>? Map,
                                    string Platform, string Title, string GameId, bool HasArt,
                                    Dictionary<string, string>? ImgOv);

    /// <summary>Resolve a game's settings map + art sources and compute its cache key. Cheap (a few stats,
    /// no decode) — safe to call per selection on a background thread. Null on unusable input.</summary>
    public static Identity? Resolve(IGame g)
    {
        string platform, title, id;
        try { platform = g.Platform ?? ""; title = g.Title ?? ""; id = g.Id ?? ""; } catch { return null; }
        if (platform.Length == 0 || title.Length == 0) return null;

        string scrapeAs = "";
        try { scrapeAs = PluginHelper.DataManager?.GetPlatformByName(platform)?.ScrapeAs ?? ""; } catch { }
        Dictionary<string, string>? map = null;
        try
        {
            map = (id.Length > 0 ? Platforms.PlatformModelStore.ReadGame(platform, id) : null)
                  ?? Platforms.PlatformModelStore.Read(platform)
                  ?? Platforms.ModelDefaults.TryGet(platform, scrapeAs)
                  ?? Platforms.EditPlatformModel.CtorDefaults();
        }
        catch { }

        var sb = new StringBuilder();
        sb.Append("v").Append(BakerVersion).Append('\n');
        if (map != null)
            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key.ToLowerInvariant()).Append('=').Append(kv.Value ?? "").Append('\n');
        sb.Append("--\n");

        bool hasArt = false;
        void Slot(string name, string? path)
        {
            sb.Append(name).Append('|');
            long size = -1; long mtime = 0;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
                }
            }
            catch { }
            if (size < 0) { sb.Append("-\n"); return; }
            hasArt = true;
            sb.Append(path!.ToLowerInvariant()).Append('|').Append(size).Append('|').Append(mtime).Append('\n');
        }
        // The slots the builders can consume — resolution identical to HomeModel3d's own calls, INCLUDING
        // the per-game image override (custom fields 3D.Image*, Edit Game → Image Selection; Effective =
        // null when any pick is missing on disk → the whole override is ignored, back to full auto).
        var ov = Model3dImageStore.Effective(g);
        Slot("front", Platforms.HomeModel3d.ResolveSlot(ov, "front", platform, title, Media.MediaResolver.Front));
        Slot("logo", Platforms.HomeModel3d.ResolveSlot(ov, "logo", platform, title, Media.MediaResolver.ClearLogo));
        Slot("spine", Platforms.HomeModel3d.ResolveSlot(ov, "spine", platform, title, new[] { "Box - Spine" }));
        Slot("back", Platforms.HomeModel3d.ResolveSlot(ov, "back", platform, title, new[] { "Box - Back" }));
        bool fullScan = (map != null && map.TryGetValue("UseFullScanImages", out var ufs)
                         && ufs.Equals("true", StringComparison.OrdinalIgnoreCase))
                        || Platforms.HomeModel3d.FullForced(ov);
        Slot("full", fullScan ? Platforms.HomeModel3d.ResolveSlot(ov, "full", platform, title, new[] { "Box - Full" }) : null);
        // A CUSTOM spine image is a real file whose change must re-key; embedded {Resources} presets are
        // already covered by the params (name) + BakerVersion (content ships with LiteBox).
        string spineSpec = map != null && map.TryGetValue("FrontSpineImage", out var ss) ? (ss ?? "") : "";
        Slot("spinefile", spineSpec.Length > 0 && !spineSpec.StartsWith("{Resources}", StringComparison.OrdinalIgnoreCase) ? spineSpec : null);

        string manifest = sb.ToString();
        string key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        return new Identity(key, Path.Combine(Dir, key + ".glb"), manifest, map, platform, title, id, hasArt, ov);
    }

    /// <summary>Get the game's cached GLB, baking it if missing. Blocking (bakes serialize on the STA
    /// worker) — call from a background thread. Null when the game has no art or the bake failed.</summary>
    public static string? Ensure(IGame g, bool allowBake = true)
    {
        var idn = Resolve(g);
        if (idn == null || !idn.HasArt) return null;   // nothing real to show → no bake, block hides
        try { if (File.Exists(idn.GlbPath)) return idn.GlbPath; } catch { }
        if (!allowBake) return null;
        return BakeTo(idn) ? idn.GlbPath : null;
    }

    private static bool BakeTo(Identity idn)
    {
        try
        {
            // The WHOLE bake-and-write runs as one job on the (serializing) bake thread: two selections
            // racing the same missing key both queue a job — the second sees the file the first WROTE and
            // no-ops. (Checking inside but writing outside the job left a window where both baked.)
            return Model3dBaker.Run(() =>
            {
                if (File.Exists(idn.GlbPath)) return true;
                var baked = Model3dBaker.Bake(idn.Map, idn.Title, idn.Platform, idn.ImgOv);
                if (baked == null) return false;
                var (meshes, mats, thumb) = baked.Value;
                GlbFile.Write(idn.GlbPath, meshes, mats, thumb,
                              new GlbInfo(idn.Key, idn.GameId, idn.Platform, idn.Title, BakerVersion, idn.Manifest));
                Console.WriteLine($"[model3d] baked {idn.Title} → {Path.GetFileName(idn.GlbPath)} ({new FileInfo(idn.GlbPath).Length / 1024} KB)");
                return true;
            });
        }
        catch (Exception ex) { Console.WriteLine("[model3d] bake failed (" + idn.Title + "): " + ex.Message); return false; }
    }

    // ── maintenance ──────────────────────────────────────────────────────────

    public static (int files, long bytes) Stats()
    {
        int n = 0; long b = 0;
        try { foreach (var f in Directory.EnumerateFiles(Dir, "*.glb")) { n++; try { b += new FileInfo(f).Length; } catch { } } }
        catch { }
        return (n, b);
    }

    /// <summary>Delete every cached model (Options → Caches "Delete all").</summary>
    public static int CleanAll()
    {
        int n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir))
                try { File.Delete(f); n++; } catch { }
        }
        catch { }
        return n;
    }

    /// <summary>Mark-and-sweep: delete cached models whose game is gone or whose CURRENT key no longer
    /// matches the filename (stale bake). Identity read from each file's extras — self-contained.
    /// Returns (kept, deleted).</summary>
    public static (int kept, int deleted) SweepStale(IGame[] games)
    {
        int kept = 0, deleted = 0;
        var byId = new Dictionary<string, IGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
            try { if (g.Id is { Length: > 0 } id) byId[id] = g; } catch { }
        // Current-key memo: many files can belong to the same game (stale generations) — resolve once.
        var curKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { try { File.Delete(f); } catch { } continue; }
                if (!f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) continue;
                var info = GlbFile.ReadInfo(f);
                bool stale;
                if (info == null || info.GameId.Length == 0) stale = true;          // unreadable/foreign → out
                else if (!byId.TryGetValue(info.GameId, out var g)) stale = true;   // game gone
                else
                {
                    if (!curKey.TryGetValue(info.GameId, out var k))
                        curKey[info.GameId] = k = Resolve(g)?.Key;
                    stale = k == null || !string.Equals(k, Path.GetFileNameWithoutExtension(f), StringComparison.OrdinalIgnoreCase);
                }
                if (stale) { try { File.Delete(f); deleted++; } catch { } }
                else kept++;
            }
        }
        catch (Exception ex) { Console.WriteLine("[model3d] sweep: " + ex.Message); }
        if (deleted > 0) Console.WriteLine($"[model3d] sweep: {deleted} stale model(s) deleted, {kept} kept");
        return (kept, deleted);
    }
}
