// Disk cache of baked 3D case models: ONE GLB per game, at Core\litebox\cache\3d\<gameId>.glb, with its
// snapshot beside it as <gameId>.png.
//
// IDENTITY is the game; CURRENCY is the manifest. The two used to be one thing — the filename WAS the
// manifest's MD5 — which read elegantly and cost a fortune: finding a game's model meant computing that
// key, so the whole library's art had to be resolved at every boot (and again after every game exit)
// only to answer "which file?". Naming the artifact after the game answers it for free, and leaves the
// manifest to do the one job it is actually good at.
//
// CURRENCY — the key is the MD5 of a canonical MANIFEST built from:
//   • a BakerVersion salt (bump on any geometry/material/thumb pipeline change → whole cache invalidates);
//   • the RESOLVED settings map (game override → platform override → LB hardcoded defaults → ctor
//     defaults), sorted key=value — so editing a PLATFORM's model settings naturally re-keys every game;
//   • one line per art source the builders can consume (front / clear logo / spine / back / full-scan /
//     custom spine file), each `slot|path|size|mtime` or `slot|-` when absent. Path+size+mtime — the
//     dup-check poisoning taught us path+size alone can miss a same-size replacement.
// Art is resolved ONCE (Model3dArt) and the resolved paths are carried to the bake, so the key changes
// iff what the builder will consume changes — the two can no longer drift apart. That resolution goes
// through MediaResolver with the game's id, so a ready game cache answers it from memory: deciding
// whether an existing model is still current costs lookups, not a walk through the art directories.
// The manifest and the game identity are stored INSIDE the
// GLB (extras.litebox), which is what makes a file able to answer "am I still what you would bake?" on
// its own — IsCurrent is one header read against a key we just computed. It is asked for the ONE game
// being looked at, not for the library, and never on the fast-scroll path.
//
// A model that turns out to be out of date is not deleted: it sits in its game's own slot and the next
// bake writes over it. So the sweep has only ownership left to judge — a file whose name is not a game
// id, or whose game has left the library (SweepStale). CleanAll wipes the folder outright (Options →
// Caches), and an in-app edit that invalidates a model deletes it at the source (Model3dKeyIndex).

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
    public const int BakerVersion = 20;   // v20: the double jewel wears LB's front hinge cap — the front insert narrows to X[-0.42..] and the "<preset> - NA Double Jewel" strip fills [-0.493..-0.42], the black left border the oracle showed and LiteBox lacked; paper quads are double-faced like the dump's (v19: the double-jewel rule is PS1-only and needs BOTH signals (declared multi-disc + a spine measuring 0.09-0.11), and every auto case rule now stands down in front of a written ModelSettings block, v18: a LYING-DOWN spine scan is stood upright for every builder, not just BuildBox — the other four drew it sideways, v17: a jewel-case game whose own Box - Spine scan measures a double-width case is built as one (Model3dAutoDoubleJewel), v16: Saturn/Sega CD fall back from the long box to a plain jewel case when the front art fits that shape better (Model3dAutoJewelCase) — both decisions are made in the builder, so unlike the Genesis fix they do NOT move the key on their own, v15: a forced ModelSizeString is normalised onto the unit box, so LB's Genesis/Master System defaults ("5;7.165;1" — inches) stop baking a model seven units tall in front of the camera, v14: art resolves ONCE, through MediaResolver with the game's id -> the game cache answers it (fast validation), GUID-form images become visible, and an image in a type folder is no longer read as a region, v13: Dreamcast Auto-Detect picks its spine COLOUR by measuring the artwork, v12: auto version follows the front art's region, v11: double-jewel strips keep the game scan, v10: doubleSided through the GLB)

    // One lock object per game id, so two bake workers never build the same model at once. Keyed by game
    // rather than by file so it holds whatever the file is called.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _bakeGates
        = new(StringComparer.OrdinalIgnoreCase);

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
    /// <c>Art</c> = the five resolved art paths (image override already applied) — the SAME object is fed to
    /// the bake, so the key and the textures cannot describe different files.</summary>
    internal sealed record Identity(string Key, string GlbPath, string Manifest, Dictionary<string, string>? Map,
                                    string Platform, string Title, string GameId, bool HasArt, Model3dArt Art);

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
        if (id.Length == 0) return null;   // the id NAMES the artifact now — no id, nothing addressable to cache

        string scrapeAs = "";
        try { scrapeAs = ctx?.ScrapeAs(platform) ?? PluginHelper.DataManager?.GetPlatformByName(platform)?.ScrapeAs ?? ""; } catch { }
        Dictionary<string, string>? map = null;
        // WHICH source answered matters, not just what it said: a block somebody wrote (per game, then per
        // platform) is a human choice, and HomeModel3d.RefineCaseType's auto rules stand down in front of it.
        // The ?? chain is kept lazy — the platform file is still not read when the game has its own block.
        bool overridden = false;
        try
        {
            var own = id.Length > 0 ? (ctx != null ? ctx.GameSettings(platform, id) : Platforms.PlatformModelStore.ReadGame(platform, id)) : null;
            var plat = own ?? (ctx != null ? ctx.PlatformSettings(platform) : Platforms.PlatformModelStore.Read(platform));
            overridden = plat != null;
            map = plat
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
        // The slots the builders can consume, resolved ONCE — the very paths the bake will load, including
        // the per-game image override (custom fields 3D.Image*, Edit Game → Image Selection; Effective =
        // null when any pick is missing on disk → the whole override is ignored, back to full auto).
        // Resolution goes through MediaResolver with the game's id, so a ready game cache answers it from
        // memory: validating an already-current model costs a handful of lookups, not a directory walk.
        var ov = Model3dImageStore.Effective(g);
        Guid.TryParse(id, out var gid);
        // The scrape-resolved name is what the auto rules key on, so a custom-named PS1 library behaves like
        // one — the same identity ModelDefaults matches. MultiDisc is only ASKED for on the platform that can
        // act on it: GetAllAdditionalApplications is an in-memory catalogue call, but the key-index pass runs
        // over the whole library and there is no reason to pay it thousands of times for nothing.
        string platKey = scrapeAs.Length > 0 ? scrapeAs : platform;
        bool multiDisc = Platforms.HomeModel3d.CanAutoDoubleJewel(platKey) && IsMultiDisc(g);
        var art = Model3dArt.Resolve(map, platform, gid, title, ov, platKey, multiDisc, overridden);
        // Both change what gets BUILT from identical art, so both belong in the key: adding a second disc
        // must re-bake, and so must ticking Override on a platform whose fields happen to match the defaults.
        sb.Append("md=").Append(multiDisc ? '1' : '0').Append(" ov=").Append(overridden ? '1' : '0').Append('\n');
        Slot("front", art.Front);
        Slot("logo", art.Logo);
        Slot("spine", art.Spine);
        Slot("back", art.Back);
        Slot("full", art.Full);   // null unless the sheet mode applies to this game — see Model3dArt.Resolve
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
        // The GAME names the file; the key rides INSIDE it. Deriving the name from the key instead made
        // finding a game's model require computing that key — art resolution for the whole library, at
        // every boot, only to answer "which file?". Identity is stable, currency is not: keep them apart.
        return new Identity(key, Path.Combine(Dir, id + ".glb"), manifest, map, platform, title, id, hasArt, art);
    }

    // The instant-lookup index that used to live here is gone: reading each GLB's header to learn which
    // game owns it only made sense while the FILENAME could not say. Now it does, so "has this game a
    // model?" is an answer the directory listing already carries — see Model3dKeyIndex.

    // ── snapshot sidecar (<key>.png next to <key>.glb) ───────────────────────
    // The baked thumb ALSO lives as a loose PNG beside the GLB: the instant/strip display is then a
    // plain ReadAllBytes (no GLB header walk, and the OS caches the small file independently). The GLB
    // keeps embedding the thumb (single self-contained artifact, web/three.js reuse); the sidecar is a
    // pure accelerator, restored from the GLB whenever it's missing.

    /// <summary>Two or more DISTINCT disc numbers among the game's additional applications — the same rule
    /// the "Multiple Discs" badge applies, and the same doctrine as M3uPlaylistPlanner: discs are FIELDS,
    /// never parsed out of file names. In-memory catalogue call, no I/O.</summary>
    private static bool IsMultiDisc(IGame g)
    {
        try
        {
            var apps = g.GetAllAdditionalApplications();
            if (apps == null) return false;
            var seen = new HashSet<int>();
            foreach (var a in apps)
            {
                int? d = null;
                try { d = a.Disc; } catch { }
                if (d.HasValue && seen.Add(d.Value) && seen.Count >= 2) return true;
            }
            return false;
        }
        catch { return false; }
    }

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
    /// stale job still baked in turn — the queue ground for seconds behind games long since left.
    /// <paramref name="force"/> (the bulk generator's "Regenerate everything") re-bakes even a CURRENT
    /// model — transactionally: GlbFile.Write is tmp+move, so a failed bake leaves the old GLB in
    /// place instead of an emptied slot.</summary>
    public static string? Ensure(IGame g, bool allowBake = true, Func<bool>? stillWanted = null, bool force = false)
    {
        // No authoritative media state → do not judge. The host game cache is dropped while a game runs
        // (its RAM belongs to the game) and is not up yet while it builds. Asking "is this model still
        // current?" then means walking the art directories LiteBox just stepped aside to stop touching,
        // and a verdict reached that way can be WRONG in the expensive direction: a mistaken "stale"
        // queues a bake — a WPF scene and its textures — behind a running game.
        //
        // So we answer from the name alone: the file is the game's slot, it was judged current when it
        // was written, and nothing we can trust says otherwise yet. Zero IO, not even the header read.
        // The next selection once the cache is back validates normally and re-bakes if it must.
        if (Gc.HostGameCache.Enabled && !Gc.GameCache.IsGlobalReady)
        {
            try
            {
                string gid = g.Id ?? "";
                if (gid.Length > 0)
                {
                    string path = Path.Combine(Dir, gid + ".glb");
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }

        var idn = Resolve(g);
        if (idn == null || !idn.HasArt) return null;   // nothing real to show → no bake, block hides
        try
        {
            if (File.Exists(idn.GlbPath))
            {
                // The file is the game's slot, so it is always the RIGHT file — the only question left is
                // whether it is still current. Asking here, for the one game being looked at, is what
                // replaced asking for all 5000 at boot. (force: current is not enough — re-bake anyway.)
                if (!force && IsCurrent(idn))
                {
                    if (!File.Exists(PngPathFor(idn.GlbPath))) ReadThumbPng(idn.GlbPath);   // restore sidecar
                    return idn.GlbPath;
                }
                if (!allowBake) return idn.GlbPath;   // caller can't bake: a stale model beats none
            }
        }
        catch { }
        if (!allowBake) return null;
        return BakeTo(idn, stillWanted, force) ? idn.GlbPath : null;   // re-bake overwrites its own slot
    }

    /// <summary>Does the file at <c>idn.GlbPath</c> still describe what the builders would produce now?
    /// The GLB carries the key and the baker version it was made with, so this is one small header read
    /// against a key we have just computed — no second source of truth, and nothing to keep in sync.</summary>
    public static bool IsCurrent(Identity idn)
    {
        try
        {
            var info = GlbFile.ReadInfo(idn.GlbPath);
            return info != null && info.BakerVersion == BakerVersion
                   && string.Equals(info.Key, idn.Key, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool BakeTo(Identity idn, Func<bool>? stillWanted = null, bool force = false)
    {
        try
        {
            // The WHOLE bake-and-write runs as one job on the (serializing) bake thread: two selections
            // racing the same missing key both queue a job — the second sees the file the first WROTE and
            // no-ops. (Checking inside but writing outside the job left a window where both baked.)
            return Model3dBaker.Run(() =>
            {
                // ONE gate per game. The queue is drained by SEVERAL bake workers, so two jobs for the
                // same game can run at the same instant: both find no file, both build it, both write it.
                // Checking for the file first never closed that window — it only made it small. The loser
                // waits here, then finds the winner's file on the line below and stops.
                lock (_bakeGates.GetOrAdd(idn.GameId, _ => new object()))
                {
                // Only a CURRENT file lets us off: a stale one is exactly what we came to replace.
                // (force came here to replace even that — the bulk phase runs each game once, so no
                // double bake; the per-game gate still serializes against a concurrent selection bake.)
                if (!force && File.Exists(idn.GlbPath) && IsCurrent(idn)) return true;
                if (stillWanted != null && !stillWanted()) return false;   // selection moved on → skip, drain the queue
                var baked = Model3dBaker.Bake(idn.Map, idn.Title, idn.Art);
                if (baked == null) return false;
                var (meshes, mats, thumb) = baked.Value;
                GlbFile.Write(idn.GlbPath, meshes, mats, thumb,
                              new GlbInfo(idn.Key, idn.GameId, idn.Platform, idn.Title, BakerVersion, idn.Manifest));
                if (thumb != null) TryWritePng(PngPathFor(idn.GlbPath), thumb);   // sidecar, same bake
                Model3dKeyIndex.NotifyBaked(idn.GameId);
                Console.WriteLine($"[model3d] baked {idn.Title} → {Path.GetFileName(idn.GlbPath)} ({new FileInfo(idn.GlbPath).Length / 1024} KB)");
                return true;
                }
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

    /// <summary>Delete what belongs to NOBODY: a leftover .tmp, a file whose name is not a game id, one
    /// whose game has left the library, and a sidecar without its model. Every verdict is an affirmative
    /// statement about that one file — nothing is deleted because a lookup came back empty. That is the
    /// whole difference with the key-named scheme, where "no current key claims this file" also described
    /// a resolution that had merely failed, and one unreadable art directory could take a whole category
    /// of valid models with it.
    ///
    /// Staleness is no longer a sweep concern: a stale model sits in its own game's slot, and the next
    /// bake writes over it. Returns (kept, deleted) counted in models, not files.</summary>
    public static (int kept, int deleted) SweepStale(IGame[] games)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
            try { if (g.Id is { Length: > 0 } id) ids.Add(id); } catch { }
        if (ids.Count == 0) return (0, 0);   // no library in hand → nothing can be judged, so judge nothing

        int kept = 0, deleted = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir))
            {
                bool glb = f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
                bool png = f.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                string name = Path.GetFileNameWithoutExtension(f);

                bool drop;
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) drop = true;
                else if (!glb && !png) continue;                                  // foreign extension: not ours to judge
                else if (!Guid.TryParseExact(name, "D", out _)) drop = true;      // not a game id (e.g. a key-named file)
                else if (!ids.Contains(name)) drop = true;                        // the game has left the library
                else if (png && !File.Exists(Path.ChangeExtension(f, ".glb"))) drop = true;   // sidecar with no model
                else drop = false;

                if (drop) { try { File.Delete(f); if (glb) deleted++; } catch { } }
                else if (glb) kept++;
            }
        }
        catch (Exception ex) { Console.WriteLine("[model3d] sweep: " + ex.Message); }
        if (deleted > 0)
        {
            Model3dKeyIndex.Refresh();
            Console.WriteLine($"[model3d] sweep: {deleted} unowned model(s) deleted, {kept} kept");
        }
        return (kept, deleted);
    }
}
