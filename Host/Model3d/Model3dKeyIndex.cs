// Which games have a baked 3D model on disk — answered by the one thing that is already cheap to read:
// the NAMES of the files in cache\3d. A model is <gameId>.glb and its snapshot <gameId>.png, so the
// directory listing IS the index. Nothing is opened, parsed or resolved to build it (~18 ms for 673
// files, once per launch).
//
// This replaces a pass that computed every game's bake KEY at boot — and again after every game exit,
// riding the GameCache's ready signal — purely so a filename could be derived from it: 5075 games, tens
// of seconds, hundreds of thousands of directory probes, to answer a question the filename can answer
// for free. Naming the artifact after the game removed the question rather than optimising it.
//
// What this does NOT answer: whether a model still matches what the builders would produce today. That
// is Model3dCache.IsCurrent, asked for the ONE game being looked at, against the key stored inside the
// file itself. Identity is stable and belongs in the name; currency changes and belongs in the file.
//
// Kept exact between listings by the hooks below: a bake adds, a restored sidecar adds, Delete-all
// clears, and an in-app edit that invalidates a model DELETES it on the spot.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Model3d;

internal static class Model3dKeyIndex
{
    private static readonly object _lock = new();
    private static HashSet<string>? _glb;   // gameIds with a model
    private static HashSet<string>? _png;   // gameIds with a snapshot

    private static (HashSet<string> glb, HashSet<string> png) Sets()
    {
        lock (_lock)
        {
            if (_glb == null || _png == null) Build();
            return (_glb!, _png!);
        }
    }

    private static void Build()
    {
        var glb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var png = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Model3dCache.Dir))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) glb.Add(name);
                else if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) png.Add(name);
            }
        }
        catch (Exception ex) { Console.WriteLine("[model3d] index: " + ex.Message); }
        _glb = glb; _png = png;
        Console.WriteLine($"[model3d] index: {glb.Count} model(s), {png.Count} snapshot(s)");
    }

    /// <summary>Has this game a baked model? A set lookup — no IO, no art resolution, nothing to wait
    /// for. This is what the fast-scroll path asks, and the only thing it needs.</summary>
    public static bool HasModel(string? gameId)
        => !string.IsNullOrEmpty(gameId) && Sets().glb.Contains(gameId!);

    /// <summary>Has this game its snapshot PNG beside the model? (A missing one is restored from the GLB
    /// on first read — this only says whether that restore is still owed.)</summary>
    public static bool HasThumb(string? gameId)
        => !string.IsNullOrEmpty(gameId) && Sets().png.Contains(gameId!);

    /// <summary>Drop the listing; it is rebuilt on the next question. For after a sweep, a Delete-all, or
    /// anything that changed the folder behind our back.</summary>
    public static void Refresh() { lock (_lock) { _glb = null; _png = null; } }

    private static int _sweptThisLaunch;

    /// <summary>Once per launch, on a background thread: drop what belongs to nobody. Gated on the
    /// CleanModel3d opt-out. Cheap now — one directory listing and a set of game ids, no art resolved —
    /// which is why it can simply ride the moment the library is known instead of needing a pass of its
    /// own. Also what retires artifacts from the old key-named scheme: their names are not game ids.</summary>
    public static void SweepOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref _sweptThisLaunch, 1) == 1) return;
        if (!LiteBoxConfig.LoadForExe().GetBool("CleanModel3d", true)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var games = PluginHelper.DataManager?.GetAllGames();
                if (games is { Length: > 0 }) Model3dCache.SweepStale(games);
            }
            catch (Exception ex) { Console.WriteLine("[model3d] sweep: " + ex.Message); }
        });
    }

    // ── hooks: keep the sets exact without re-listing ────────────────────────

    /// <summary>A bake just wrote &lt;gameId&gt;.glb and its sidecar.</summary>
    public static void NotifyBaked(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        lock (_lock) { _glb?.Add(gameId!); _png?.Add(gameId!); }
    }

    /// <summary>A missing sidecar PNG was re-extracted beside its model.</summary>
    public static void NotifySidecar(string glbPath)
    {
        string name = Path.GetFileNameWithoutExtension(glbPath);
        lock (_lock) { _png?.Add(name); }
    }

    /// <summary>Options → Caches "Delete all": the folder is empty now.</summary>
    public static void NotifyAllDeleted() { lock (_lock) { _glb?.Clear(); _png?.Clear(); } }

    // ── invalidation at the source ───────────────────────────────────────────
    // An edit made INSIDE LiteBox knows exactly what it broke, so it deletes on the spot instead of
    // leaving the lazy currency check to discover it later. Only edits that change what the BUILDER
    // consumes belong here. The display rules — Require Back / Require Spine / Accept full scan, and the
    // 16:9 toggle — decide whether a model is worth SHOWING, not what it contains: wiring them here
    // would wipe the cache for nothing.

    /// <summary>This game's 3D settings or image selection changed: its model no longer describes it.</summary>
    public static void DropGame(IGame? g) => DropId(SafeId(g));

    /// <summary>A platform's 3D settings changed: every game of that platform is out of date. No art is
    /// resolved here — the files are named after the games, so the list of victims is the platform's own
    /// game list.</summary>
    public static void DropPlatform(string? platform)
    {
        if (string.IsNullOrEmpty(platform)) return;
        IGame[]? games = null;
        try
        {
            games = PluginHelper.DataManager?.GetAllGames()
                ?.Where(g => string.Equals(Safe(() => g.Platform), platform, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch { }
        if (games == null) return;
        int n = 0;
        foreach (var g in games) if (DropId(SafeId(g))) n++;
        if (n > 0) Console.WriteLine($"[model3d] {platform}: {n} model(s) dropped (platform settings changed)");
    }

    private static bool DropId(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        bool had = false;
        try
        {
            string glb = Path.Combine(Model3dCache.Dir, gameId + ".glb");
            if (File.Exists(glb)) { File.Delete(glb); had = true; }
            string png = Model3dCache.PngPathFor(glb);
            if (File.Exists(png)) File.Delete(png);
        }
        catch { }
        lock (_lock) { _glb?.Remove(gameId!); _png?.Remove(gameId!); }
        return had;
    }

    private static string? SafeId(IGame? g) { try { return g?.Id; } catch { return null; } }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
