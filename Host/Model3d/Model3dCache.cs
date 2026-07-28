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
    public const int BakerVersion = 13;   // v13: Dreamcast Auto-Detect picks its spine COLOUR by measuring the artwork (PAL -> blue, else black-vs-white on the spine scan or the front's right 3%) (v12: auto version follows the front art's region, v11: double-jewel strips keep the game scan, v10: doubleSided through the GLB)

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

    /// <summary>Per-pass memoization for bulk key computation (Model3dKeyIndex): platform settings and
    /// per-game overrides are parsed ONCE PER FILE instead of once per game (the naive per-game reads
    /// re-parsed multi-MB platform XMLs thousands of times — a 10-minute pass), and size/mtime can be
    /// served from a bulk Everything map. Single-game runtime callers pass null and keep per-call reads.</summary>
    internal sealed class ResolveContext
    {
        public Func<string, (long size, long mtimeTicks)?>? Stat;
        private readonly Dictionary<string, Dictionary<string, string>?> _platform = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _games = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _scrapeAs = new(StringComparer.OrdinalIgnoreCase);

        public string ScrapeAs(string platform)
        {
            if (_scrapeAs.TryGetValue(platform, out var v)) return v;
            string s = "";
            try { s = PluginHelper.DataManager?.GetPlatformByName(platform)?.ScrapeAs ?? ""; } catch { }
            return _scrapeAs[platform] = s;
        }

        public Dictionary<string, string>? PlatformSettings(string platform)
        {
            if (_platform.TryGetValue(platform, out var v)) return v;
            return _platform[platform] = Platforms.PlatformModelStore.Read(platform);
        }

        public Dictionary<string, string>? GameSettings(string platform, string gameId)
        {
            if (!_games.TryGetValue(platform, out var per))
                _games[platform] = per = Platforms.PlatformModelStore.ReadAllGameOverrides(platform);
            return per.TryGetValue(gameId, out var m) ? m : null;
        }
    }

    /// <summary>Resolve a game's settings map + art sources and compute its cache key. Cheap (a few stats,
    /// no decode) — safe to call per selection on a background thread. Null on unusable input.
    /// <paramref name="ctx"/> (optional) memoizes the per-file reads for bulk passes.</summary>
    public static Identity? Resolve(IGame g, ResolveContext? ctx = null)
    {
        string platform, title, id;
        try { platform = g.Platform ?? ""; title = g.Title ?? ""; id = g.Id ?? ""; } catch { return null; }
        if (platform.Length == 0 || title.Length == 0) return null;

        string scrapeAs = "";
        try { scrapeAs = ctx?.ScrapeAs(platform) ?? PluginHelper.DataManager?.GetPlatformByName(platform)?.ScrapeAs ?? ""; } catch { }
        Dictionary<string, string>? map = null;
        try
        {
            map = (id.Length > 0 ? (ctx != null ? ctx.GameSettings(platform, id) : Platforms.PlatformModelStore.ReadGame(platform, id)) : null)
                  ?? (ctx != null ? ctx.PlatformSettings(platform) : Platforms.PlatformModelStore.Read(platform))
                  ?? Platforms.ModelDefaults.TryGet(platform, scrapeAs)
                  ?? Platforms.EditPlatformModel.CtorDefaults();
        }
        catch { }

        var sb = new StringBuilder();
        sb.Append("v").Append(BakerVersion)
          .Append('@').Append(Model3dBaker.BakeAspect.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
          .Append('\n');   // the bake aspect is now a CONSTANT: ONE artifact per game, and flipping the
                           // 16:9/poster display option never re-bakes (the display crops instead).
                           // Kept in the manifest at its historical 16:9 value so those caches stay valid.
        if (map != null)
            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key.ToLowerInvariant()).Append('=').Append(kv.Value ?? "").Append('\n');
        sb.Append("--\n");

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // slots that actually exist on disk
        void Slot(string name, string? path)
        {
            sb.Append(name).Append('|');
            long size = -1; long mtime = 0;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    if (ctx?.Stat?.Invoke(path!) is { } st) { size = st.size; mtime = st.mtimeTicks; }
                    else
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
                    }
                }
            }
            catch { }
            if (size < 0) { sb.Append("-\n"); return; }
            present.Add(name);
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
        // Worth showing? Configurable (Display → right panel): the FRONT is always required, Back/Spine can
        // be demanded on top, and a Box - Full sheet is an alternative on its own. "full" is only ever
        // resolved above when full-scan mode applies TO THIS GAME, so the option's per-game scope is
        // honoured without the UI having to know which level (global/platform/game) won.
        // NOTE: the rule is NOT part of the manifest — it decides whether to SHOW/bake a model, not what the
        // bake produces, so tightening it must never re-key (and re-bake) the models that stay valid.
        bool hasArt = Model3dOptions.Valid(
            present.Contains("front"), present.Contains("back"), present.Contains("spine"), present.Contains("full"));
        return new Identity(key, Path.Combine(Dir, key + ".glb"), manifest, map, platform, title, id, hasArt, ov);
    }

    // ── instant lookup index (gameId → cached GLB path) ──────────────────────
    // The INSTANT image path runs for EVERY game a fast scroll transits — Resolve() there (art-slot
    // IO, per-slot stats, platform settings) made each step cost tens of ms and froze the transit
    // loader. The instant question is only "does this game have a cached model?": answered from a RAM
    // index built once from the GLB headers, updated by bakes, invalidated by sweeps. A slightly stale
    // hit is harmless — the settle-time pipeline re-resolves properly and re-bakes.

    private static Dictionary<string, string>? _instantIndex;   // gameId → glb path
    private static readonly object _indexLock = new();

    /// <summary>The cached GLB for <paramref name="g"/> per the RAM index — O(1), no art resolution.
    /// Null when the game has no baked model (the caller falls back to a plain image family).</summary>
    public static string? CachedGlbForInstant(IGame g)
    {
        string id;
        try { id = g.Id ?? ""; } catch { return null; }
        if (id.Length == 0) return null;
        lock (_indexLock)
        {
            if (_instantIndex == null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(Dir, "*.glb"))
                        try { if (GlbFile.ReadInfo(f) is { GameId.Length: > 0 } info) map[info.GameId] = f; } catch { }
                }
                catch { }
                _instantIndex = map;
                Console.WriteLine($"[model3d] instant index built ({map.Count} model(s))");
            }
            return _instantIndex.TryGetValue(id, out var p) ? p : null;
        }
    }

    private static void IndexAdd(string gameId, string glbPath)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        lock (_indexLock) { if (_instantIndex != null) _instantIndex[gameId] = glbPath; }
    }

    private static void IndexInvalidate()
    {
        lock (_indexLock) { _instantIndex = null; }   // rebuilt lazily on the next instant lookup
    }

    // ── snapshot sidecar (<key>.png next to <key>.glb) ───────────────────────
    // The baked thumb ALSO lives as a loose PNG beside the GLB: the instant/strip display is then a
    // plain ReadAllBytes (no GLB header walk, and the OS caches the small file independently). The GLB
    // keeps embedding the thumb (single self-contained artifact, web/three.js reuse); the sidecar is a
    // pure accelerator, restored from the GLB whenever it's missing.

    /// <summary>The sidecar snapshot path of a cached GLB.</summary>
    public static string PngPathFor(string glbPath) => Path.ChangeExtension(glbPath, ".png");

    /// <summary>The baked snapshot PNG bytes: the sidecar when present (fast path), else extracted
    /// from the GLB head — and written back beside it so the next read is the fast path.</summary>
    public static byte[]? ReadThumbPng(string glbPath)
    {
        string png = PngPathFor(glbPath);
        try { if (File.Exists(png)) return File.ReadAllBytes(png); } catch { }
        var bytes = GlbFile.ReadThumb(glbPath);
        if (bytes != null) { TryWritePng(png, bytes); Model3dKeyIndex.NotifySidecar(glbPath); }   // restore the sidecar
        return bytes;
    }

    private static void TryWritePng(string png, byte[] bytes)
    {
        try
        {
            string tmp = png + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";   // unique: parallel bakes may race
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, png, overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine("[model3d] sidecar write failed: " + ex.Message); }
    }

    /// <summary>Get the game's cached GLB, baking it if missing. Blocking (bakes serialize on the STA
    /// worker) — call from a background thread. Null when the game has no art or the bake failed.
    /// A present GLB with a MISSING sidecar PNG gets it restored here — which makes the bulk
    /// Generate-Media-Cache pass repair sidecar-less caches for free.
    /// <paramref name="stillWanted"/> (optional) is re-checked INSIDE the STA job right before the
    /// expensive bake: fast scrolling queues one bake per settled game, and without this check every
    /// stale job still baked in turn — the queue ground for seconds behind games long since left.</summary>
    public static string? Ensure(IGame g, bool allowBake = true, Func<bool>? stillWanted = null)
    {
        var idn = Resolve(g);
        if (idn == null || !idn.HasArt) return null;   // nothing real to show → no bake, block hides
        try
        {
            if (File.Exists(idn.GlbPath))
            {
                if (!File.Exists(PngPathFor(idn.GlbPath))) ReadThumbPng(idn.GlbPath);   // restore sidecar
                return idn.GlbPath;
            }
        }
        catch { }
        if (!allowBake) return null;
        return BakeTo(idn, stillWanted) ? idn.GlbPath : null;
    }

    private static bool BakeTo(Identity idn, Func<bool>? stillWanted = null)
    {
        try
        {
            // The WHOLE bake-and-write runs as one job on the (serializing) bake thread: two selections
            // racing the same missing key both queue a job — the second sees the file the first WROTE and
            // no-ops. (Checking inside but writing outside the job left a window where both baked.)
            return Model3dBaker.Run(() =>
            {
                if (File.Exists(idn.GlbPath)) return true;
                if (stillWanted != null && !stillWanted()) return false;   // selection moved on → skip, drain the queue
                var baked = Model3dBaker.Bake(idn.Map, idn.Title, idn.Platform, idn.ImgOv);
                if (baked == null) return false;
                var (meshes, mats, thumb) = baked.Value;
                GlbFile.Write(idn.GlbPath, meshes, mats, thumb,
                              new GlbInfo(idn.Key, idn.GameId, idn.Platform, idn.Title, BakerVersion, idn.Manifest));
                if (thumb != null) TryWritePng(PngPathFor(idn.GlbPath), thumb);   // sidecar, same bake
                IndexAdd(idn.GameId, idn.GlbPath);
                Model3dKeyIndex.NotifyBaked(idn.GameId, idn.Key);
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
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir))   // bytes: GLBs + their PNG sidecars
            {
                if (f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) n++;
                try { b += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return (n, b);
    }

    /// <summary>Delete every cached model (Options → Caches "Delete all").</summary>
    public static int CleanAll()
    {
        IndexInvalidate();
        Model3dKeyIndex.NotifyAllDeleted();
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
                if (stale)
                {
                    try { File.Delete(f); deleted++; } catch { }
                    try { File.Delete(PngPathFor(f)); } catch { }   // the sidecar follows its GLB out
                }
                else kept++;
            }
            // Orphan sidecars: a PNG whose GLB is gone (deleted above, or externally) has no source of
            // truth left — drop it. A PNG WITH its GLB is never touched here.
            foreach (var f in Directory.EnumerateFiles(Dir, "*.png"))
                try { if (!File.Exists(Path.ChangeExtension(f, ".glb"))) File.Delete(f); } catch { }
        }
        catch (Exception ex) { Console.WriteLine("[model3d] sweep: " + ex.Message); }
        if (deleted > 0) { IndexInvalidate(); Console.WriteLine($"[model3d] sweep: {deleted} stale model(s) deleted, {kept} kept"); }
        return (kept, deleted);
    }
}
