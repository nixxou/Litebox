// What deleting a game could take with it.
//
// A game's media are found by NAME, not by ownership: `<Sanitized Title>-01.png` answers for every
// game on the platform carrying that title, while `<Title>.<GUID>-…-01.png` answers for exactly one.
// So a plain-named file is only ever this game's to delete when no OTHER game — one that is not
// being deleted — would resolve it too. Same for the four path overrides (ManualPath, MusicPath,
// VideoPath, ThemeVideoPath): a file another game names outright is that game's.
//
// Shared files are not merely left alone: they are excluded from the counts, so the dialog never
// offers to delete something it will not delete.
//
// Second rule, on top of ownership: only files stored INSIDE the platform's own media folders are
// ours. A ManualPath or MusicPath (or an additional document) pointing at a file on another drive,
// in someone's own library, is a REFERENCE — deleting the game drops the reference and leaves the
// file exactly where it was.
//
// WHERE THE FILE LISTS COME FROM — nothing here globs the disk per game:
//   • images and videos: the host GameCache (Host/Gc), the same RAM index the list and the detail
//     pane read. Instant, whatever the selection size.
//   • manuals and music: the cache does not hold them, so each platform's Manuals\ and Music\ folder
//     is listed ONCE — through Everything when its index is up (milliseconds), else one recursive
//     directory walk — and matched in memory by GUID or sanitized title, exactly as MediaResolver
//     would have matched them file by file.
// Only when the cache is off (or a platform is not built yet) does the old per-game resolution run,
// and only then does the selection size cap apply.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LbApiHost.Host.Data;
using LbApiHost.Host.Gc;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Games;

internal static class GameMediaDeleter
{
    private static readonly Regex GuidInName = new(
        @"\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal sealed class MediaGroup
    {
        public string Label = "";
        public List<string> Files = new();
        public int Count => Files.Count;
    }

    internal sealed class Plan
    {
        public List<MediaGroup> Groups = new();
        public int Shared;                     // files left out because another game uses them
        public bool Skipped;                   // selection too large to scan — media were not examined
        public bool HasMedia => Groups.Any(g => g.Count > 0);
        public IEnumerable<string> AllFiles => Groups.SelectMany(g => g.Files);
    }

    /// <summary>Cap for the SLOW path only — a platform whose GameCache isn't built, where images and
    /// videos have to be resolved by globbing a couple of dozen folders per game. With the cache up
    /// there is no per-game IO at all and no cap applies.</summary>
    public const int MaxScannedGames = 250;

    /// <summary>Every media file the selection owns OUTRIGHT, grouped by kind. Files another game
    /// would also resolve are dropped (and counted in <see cref="Plan.Shared"/>).</summary>
    public static Plan Build(IGame[] games, IDataManager dm)
    {
        var plan = new Plan();
        var images = new MediaGroup { Label = "Images" };
        var videos = new MediaGroup { Label = "Videos" };
        var music = new MediaGroup { Label = "Music" };
        var manuals = new MediaGroup { Label = "Manuals" };
        plan.Groups.AddRange(new[] { images, videos, music, manuals });
        if (games == null || games.Length == 0) return plan;

        // The cache answers for images and videos without touching the disk. Only a platform it has
        // not built forces the per-game resolution — and only that case is capped.
        var platformNames = games.Select(g => Safe(() => g.Platform) ?? "")
                                 .Where(p => p.Length > 0)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var cached = platformNames.Where(p => Safe(() => HostGameCache.Ready(p)))
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (games.Length > MaxScannedGames && cached.Count < platformNames.Length)
        { plan.Skipped = true; plan.Groups.Clear(); return plan; }

        var doomed = new HashSet<string>(games.Select(g => Safe(() => g.Id) ?? "").Where(s => s.Length > 0),
                                         StringComparer.OrdinalIgnoreCase);
        // Manuals and music: one listing per platform folder, matched in memory afterwards.
        var flat = new Dictionary<string, (FlatIndex manuals, FlatIndex music)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in platformNames)
            flat[p] = (FlatIndex.Of(ManualDir(p), MediaResolver.ManualExtensions),
                       FlatIndex.Of(MusicDir(p), MediaResolver.MusicExtensions));
        // ONE pass over the whole library answers both questions (who else holds this title, who
        // names this file outright). Asking per game re-walked the platform per title — quadratic,
        // and on a five-thousand-game selection that alone was the freeze.
        var survivors = SurvivorClaims.Build(dm, doomed, platformNames);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int shared = 0;

        foreach (var g in games)
        {
            string plat = Safe(() => g.Platform) ?? "";
            string title = Safe(() => g.Title) ?? "";
            string idStr = Safe(() => g.Id) ?? "";
            if (plat.Length == 0 || !Guid.TryParse(idStr, out var id)) continue;

            string sani = MediaResolver.Sanitize(title);
            bool rivals = survivors == null || survivors.TitleHeld(plat, sani);

            void Take(MediaGroup into, IEnumerable<string> paths)
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
                    // A GUID-stamped name answers for this game alone; a plain one answers for the
                    // title, so a surviving twin keeps it. Either way an override elsewhere wins.
                    bool mine = GuidInName.Match(Path.GetFileNameWithoutExtension(p)) is { Success: true } m
                                && string.Equals(m.Groups[1].Value, idStr, StringComparison.OrdinalIgnoreCase);
                    if ((!mine && rivals) || survivors?.Claimed.Contains(p) == true) { shared++; continue; }
                    into.Files.Add(p);
                }
            }

            bool fromCache = cached.Contains(plat);

            Take(images, fromCache
                ? Safe(() => HostGameCache.AllImagePaths(plat, id)) ?? new List<string>()
                : Safe(() => MediaResolver.AllImageFiles(plat, id, title))?.Select(x => x.path) ?? Enumerable.Empty<string>());
            Take(videos, fromCache
                ? Safe(() => HostGameCache.AllVideoRefs(plat, id))?.Select(v => v?.FullPath ?? "") ?? Enumerable.Empty<string>()
                : Safe(() => MediaResolver.AllVideoFiles(plat, id, title))?.Select(x => x.path) ?? Enumerable.Empty<string>());

            var idx = flat.TryGetValue(plat, out var f) ? f : default;
            Take(music, idx.music?.For(idStr, sani) ?? Enumerable.Empty<string>());
            Take(manuals, idx.manuals?.For(idStr, sani) ?? Enumerable.Empty<string>());

            // A path field (and an additional document) can point ANYWHERE — a manual on another
            // drive, a music file in the user's own library. Those are references, not our files:
            // only what sits inside the platform's own Manuals / Music / Videos folder is ours to
            // delete. Everything above came from those folders already; these did not.
            Take(videos, Inside(Resolved(Safe(() => g.VideoPath), Safe(() => g.ThemeVideoPath)), VideoDir(plat)));
            Take(music, Inside(Resolved(Safe(() => g.MusicPath)), MusicDir(plat)));
            Take(manuals, Inside(Resolved(Safe(() => g.ManualPath)), ManualDir(plat)));
            Take(manuals, Inside(DocumentPaths(g), ManualDir(plat)));
        }

        plan.Shared = shared;
        plan.Groups = plan.Groups.Where(x => x.Count > 0).ToList();   // a kind with nothing has no line
        return plan;
    }

    /// <summary>Deletes the files, best effort. Returns how many went and how many refused.</summary>
    public static (int deleted, int failed) Delete(IEnumerable<string> files)
    {
        int ok = 0, ko = 0;
        foreach (var f in files ?? Enumerable.Empty<string>())
        {
            try { if (File.Exists(f)) File.Delete(f); ok++; }
            catch (Exception ex) { ko++; Console.WriteLine("[delete] " + f + ": " + ex.Message); }
        }
        return (ok, ko);
    }

    // ── flat media folders (Manuals, Music) ──────────────────────────────────
    /// <summary>One folder, listed once, keyed the way LaunchBox names media: by the game GUID the
    /// filename embeds, or by the sanitized title it starts with. Answering per game is then a
    /// dictionary hit instead of a directory glob — which is the whole point on a big selection.
    /// Everything serves the listing when its index is up; otherwise one recursive walk does.</summary>
    private sealed class FlatIndex
    {
        private readonly Dictionary<string, List<string>> _byGuid = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _byTitle = new(StringComparer.OrdinalIgnoreCase);

        public static FlatIndex? Of(string dir, IReadOnlyCollection<string> exts)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var ix = new FlatIndex();
            foreach (var f in List(dir))
            {
                if (!exts.Contains(Path.GetExtension(f))) continue;
                string name = Path.GetFileNameWithoutExtension(f);
                var gm = GuidInName.Match(name);
                if (gm.Success) { Add(ix._byGuid, gm.Groups[1].Value, f); continue; }
                // "<title>-01" → the title; a bare "<title>" is a valid flat-media name too.
                var dash = Numbered.Match(name);
                Add(ix._byTitle, dash.Success ? dash.Groups[1].Value : name, f);
            }
            return ix;
        }

        private static void Add(Dictionary<string, List<string>> map, string key, string file)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var l)) map[key] = l = new List<string>();
            l.Add(file);
        }

        public IEnumerable<string> For(string gameId, string sanitizedTitle)
        {
            if (_byGuid.TryGetValue(gameId, out var a)) foreach (var f in a) yield return f;
            if (_byTitle.TryGetValue(sanitizedTitle, out var b)) foreach (var f in b) yield return f;
        }

        private static IEnumerable<string> List(string dir)
        {
            try
            {
                if (EverythingBridge.IsEverythingAvailable())
                {
                    var hit = EverythingBridge.GetFiles(dir, "*");
                    if (hit is { Length: > 0 }) return hit;
                }
            }
            catch { }
            try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return Array.Empty<string>(); }
        }
    }

    private static readonly Regex Numbered = new(@"^(.+)-(\d+)$", RegexOptions.Compiled);

    // ── ownership tests ──────────────────────────────────────────────────────
    /// <summary>What the games that SURVIVE lay claim to. Titles are per-platform — a plain-named
    /// file only answers inside its own platform's folders — but the outright path claims (the four
    /// override fields AND additional-document records) are GLOBAL: any surviving game, on any
    /// platform, can point at any file, including one sitting in a doomed game's media folder. One
    /// walk over the whole library; pure memory reads, except the File.Exists behind each non-empty
    /// override. Null when the library can't be read — the caller then spares every shareable file.</summary>
    private sealed class SurvivorClaims
    {
        private readonly Dictionary<string, HashSet<string>> _titles = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Claimed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a surviving game on this platform answers for this sanitized title.
        /// An unindexed platform reads as "held" — the conservative answer.</summary>
        public bool TitleHeld(string platform, string sanitizedTitle)
            => !_titles.TryGetValue(platform, out var t) || t.Contains(sanitizedTitle);

        public static SurvivorClaims? Build(IDataManager dm, HashSet<string> doomed, string[] doomedPlatforms)
        {
            try
            {
                var c = new SurvivorClaims();
                // Title sets only matter for the platforms being deleted from; claims come from everyone.
                foreach (var p in doomedPlatforms) c._titles[p] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in dm?.GetAllGames() ?? Array.Empty<IGame>())
                {
                    string oid = Safe(() => g.Id) ?? "";
                    if (oid.Length == 0 || doomed.Contains(oid)) continue;
                    if (c._titles.TryGetValue(Safe(() => g.Platform) ?? "", out var titles))
                        titles.Add(MediaResolver.Sanitize(Safe(() => g.Title) ?? ""));
                    foreach (var p in Resolved(Safe(() => g.ManualPath), Safe(() => g.MusicPath),
                                               Safe(() => g.VideoPath), Safe(() => g.ThemeVideoPath)))
                        c.Claimed.Add(p);
                    foreach (var p in DocumentPaths(g)) c.Claimed.Add(p);
                }
                return c;
            }
            catch { return null; }   // can't tell → the caller deletes nothing shareable
        }
    }

    // ── "ours to delete": inside the platform's managed media folder ─────────
    private static string ManualDir(string platform)
        => MediaResolver.LbRoot is { Length: > 0 } root ? ManualLibrary.PlatformDir(root, platform) : "";

    private static string MusicDir(string platform)
        => MediaResolver.LbRoot is { Length: > 0 } root ? ManualLibrary.MusicDir(root, platform) : "";

    /// <summary>The platform's video folder — its custom VideoPath when it has one, else
    /// &lt;LB&gt;\Videos\&lt;platform&gt; (mirrors MediaResolver's own resolution).</summary>
    private static string VideoDir(string platform)
    {
        string root = MediaResolver.LbRoot ?? "";
        string custom = Safe(() => PluginHelper.DataManager?.GetPlatformByName(platform)?.VideoPath) ?? "";
        try
        {
            if (custom.Length > 0)
                return Path.IsPathRooted(custom) ? Path.GetFullPath(custom)
                     : root.Length > 0 ? Path.GetFullPath(Path.Combine(root, custom)) : "";
            return root.Length > 0 ? Path.Combine(root, "Videos", MediaResolver.Sanitize(platform)) : "";
        }
        catch { return ""; }
    }

    private static IEnumerable<string> Inside(IEnumerable<string> paths, string dir)
    {
        if (string.IsNullOrEmpty(dir)) yield break;   // can't tell where it belongs → keep nothing
        string prefix;
        try { prefix = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        catch { yield break; }
        foreach (var p in paths)
        {
            string full;
            try { full = Path.GetFullPath(p); } catch { continue; }
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return full;
        }
    }

    private static IEnumerable<string> Resolved(params string?[] stored)
    {
        foreach (var s in stored)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var abs = Safe(() => MediaResolver.Override(s!));
            if (!string.IsNullOrEmpty(abs)) yield return abs!;
        }
    }

    /// <summary>The game's additional-DOCUMENT records (manuals and guides attached as extra apps).</summary>
    private static IEnumerable<string> DocumentPaths(IGame g)
    {
        IAdditionalApplication[] apps;
        try { apps = g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { yield break; }
        foreach (var a in apps)
        {
            if (a is not HostAdditionalApplication { IsDocument: true }) continue;
            var abs = Safe(() => MediaResolver.Override(Safe(() => a.ApplicationPath) ?? ""));
            if (!string.IsNullOrEmpty(abs)) yield return abs!;
        }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
