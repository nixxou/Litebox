// Keeping a game's media files attached to it when its title changes.
//
// LaunchBox names media after the game's TITLE, so renaming a game orphans every file unless they
// move with it. Two naming forms exist, and MediaResolver already reads both:
//
//   plain   Street Fighter II-01.jpg
//   GUID    Street Fighter II.<game guid>[-suffix…]-01.jpg
//
// The GUID form is matched on the GUID ALONE — MediaResolver.TryMatch ignores the title part of the
// name — so a GUID-named file follows its game whatever the title becomes. That property is what
// makes deferred writes survivable.
//
// THE THREE REGIMES, decided by the caller:
//
//   read-only        nothing is written to the XML ever, so nothing may move: renaming files would
//                    orphan them against a title that reverts on close.
//   write now        the XML gets the new title immediately → plain(old) becomes plain(new).
//   write deferred   LaunchBox holds the XMLs, so it still reads the OLD title while LiteBox shows
//                    the new one. Files go to the GUID form, which BOTH find. Once the flush lands
//                    the title, Reconcile brings them back to plain. The GUID form is a transit
//                    state, not a destination.
//
// THE UNIT OF WORK mirrors GameCache.Freeze, which is what decides whether a file is visible:
//
//   images   one unit per image TYPE (Freeze filters per ImageTypeIndex)
//   videos   ONE unit for the whole game — Freeze filters videos globally, so a single GUID video
//            hides every plain video of that game across all subfolders
//   manuals  one unit per folder
//   music    one unit per folder
//
// AND THE RULE THAT KEEPS ORDER INTACT. Order is not positional: BestInDir picks the file with the
// lowest -NN. Preserving order therefore means carrying the number over unchanged, which is only
// possible when the target form is free. So a unit is converted ONLY when the target form holds
// none of this game's files. A MIXED unit (both forms present) is left completely alone: its plain
// files are already dropped by Freeze, and its GUID files already follow the game — it is safe as
// it stands, and touching it would resurrect hidden files and force a renumbering.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LbApiHost.Host.Media;

internal enum MediaNameForm { Plain, Guid }

internal sealed record MediaMove(string From, string To);

internal static class GameMediaRenamer
{
    private const int MaxIndex = 9999;

    /// <summary>Every folder holding media for one game, grouped into units. Images are per type,
    /// videos are one unit for the whole game — see the header.</summary>
    public static List<List<string>> Units(string lbRoot, string platform)
    {
        var units = new List<List<string>>();
        if (string.IsNullOrEmpty(lbRoot) || string.IsNullOrEmpty(platform)) return units;
        string plat = MediaResolver.Sanitize(platform);

        string images = Path.Combine(lbRoot, "Images", plat);
        if (Directory.Exists(images))
            foreach (var typeDir in SafeDirs(images))
                units.Add(new List<string> { typeDir });   // one unit per image type

        // Videos: the root plus its known subfolders form a SINGLE unit, because Freeze drops every
        // non-GUID video of a game as soon as one GUID video exists anywhere.
        string videos = Path.Combine(lbRoot, "Videos", plat);
        if (Directory.Exists(videos))
        {
            var group = new List<string> { videos };
            group.AddRange(SafeDirs(videos));
            units.Add(group);
        }

        foreach (var kind in new[] { "Manuals", "Music" })
        {
            string dir = Path.Combine(lbRoot, kind, plat);
            if (Directory.Exists(dir)) units.Add(new List<string> { dir });
        }
        return units;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Plans the moves for one game. <paramref name="diskTitle"/> is the title the plain
    /// files on disk are named after (the OLD one when renaming); <paramref name="targetTitle"/> is
    /// what a plain target should be called. Nothing is touched here.</summary>
    public static List<MediaMove> Plan(
        string lbRoot, Guid id, string platform, string diskTitle, string targetTitle, MediaNameForm target)
    {
        var moves = new List<MediaMove>();
        if (id == Guid.Empty) return moves;
        string fromSani = MediaResolver.Sanitize(diskTitle ?? "");
        string toSani = MediaResolver.Sanitize(targetTitle ?? "");
        if (target == MediaNameForm.Plain && toSani.Length == 0) return moves;

        foreach (var unit in Units(lbRoot, platform))
        {
            var plain = new List<(string Path, int Num)>();
            var guid = new List<(string Path, int Num, string Suffix)>();
            foreach (var dir in unit)
                foreach (var file in SafeFiles(dir))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (TryGuid(name, id, out int gnum, out string suffix)) guid.Add((file, gnum, suffix));
                    else if (fromSani.Length > 0 && TryPlain(name, fromSani, out int pnum)) plain.Add((file, pnum));
                }

            // Mixed: already safe, and converting would un-hide files and force a renumbering.
            if (plain.Count > 0 && guid.Count > 0) continue;

            if (target == MediaNameForm.Guid)
            {
                if (guid.Count > 0 || plain.Count == 0) continue;   // already there, or nothing to move
                var taken = TakenNumbers(unit, id, toSani.Length > 0 ? toSani : fromSani, MediaNameForm.Guid);
                foreach (var (path, num) in plain.OrderBy(p => p.Num))
                {
                    int n = FreeNumber(taken, num);
                    moves.Add(new MediaMove(path, GuidPath(path, fromSani, id, "", n)));
                }
            }
            else
            {
                if (plain.Count > 0 || guid.Count == 0) continue;   // already plain, or nothing to move
                // A suffixed GUID file cannot become plain: "Title-Europe-01" would be read as the
                // game "Title-Europe" and never found again. If any file in the unit carries one,
                // the whole unit stays in GUID form rather than half-converting it into a mixed
                // state, which would hide whatever we did convert.
                if (guid.Any(g => g.Suffix.Length > 0)) continue;
                var taken = TakenNumbers(unit, id, toSani, MediaNameForm.Plain);
                foreach (var (path, num, _) in guid.OrderBy(g => g.Num))
                {
                    int n = FreeNumber(taken, num);
                    moves.Add(new MediaMove(path, PlainPath(path, toSani, n)));
                }
            }
        }
        return moves;
    }

    /// <summary>Numbers already used in the target form for this game, so the plan never proposes a
    /// name that exists. The set is fed as moves are planned, so two sources cannot claim one slot.</summary>
    private static HashSet<int> TakenNumbers(List<string> unit, Guid id, string sani, MediaNameForm form)
    {
        var taken = new HashSet<int>();
        foreach (var dir in unit)
            foreach (var file in SafeFiles(dir))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (form == MediaNameForm.Guid) { if (TryGuid(name, id, out int n, out _)) taken.Add(n); }
                else if (sani.Length > 0 && TryPlain(name, sani, out int n2)) taken.Add(n2);
            }
        return taken;
    }

    /// <summary>The safety belt: keep the number when it is free, otherwise take the next one. It
    /// only ever runs inside THIS game's namespace — the caller never targets the plain form when
    /// another game owns that title, precisely so that bumping can never drop a file into someone
    /// else's collection.</summary>
    private static int FreeNumber(HashSet<int> taken, int wanted)
    {
        int n = Math.Max(1, wanted);
        while (n <= MaxIndex && !taken.Add(n)) n++;
        return n;
    }

    private static string GuidPath(string source, string sani, Guid id, string suffix, int num)
        => Path.Combine(Path.GetDirectoryName(source)!,
            $"{sani}.{id:D}{suffix}-{num:D2}{Path.GetExtension(source)}");

    private static string PlainPath(string source, string sani, int num)
        => Path.Combine(Path.GetDirectoryName(source)!, $"{sani}-{num:D2}{Path.GetExtension(source)}");

    private static bool TryPlain(string nameNoExt, string sani, out int num)
    {
        num = 0;
        int dash = nameNoExt.LastIndexOf('-');
        if (dash <= 0) return false;
        if (!string.Equals(nameNoExt.Substring(0, dash), sani, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(nameNoExt.Substring(dash + 1), out num);
    }

    private static bool TryGuid(string nameNoExt, Guid id, out int num, out string suffix)
    {
        num = 0; suffix = "";
        int dot = nameNoExt.IndexOf('.' + id.ToString("D"), StringComparison.OrdinalIgnoreCase);
        if (dot < 0) return false;
        string rest = nameNoExt.Substring(dot + 1 + 36);          // everything after ".<guid>"
        int dash = rest.LastIndexOf('-');
        if (dash < 0 || !int.TryParse(rest.Substring(dash + 1), out num)) return false;
        suffix = rest.Substring(0, dash);                          // "" or "-Europe", kept verbatim
        return true;
    }

    /// <summary>Runs a plan. A file that cannot be moved is COPIED instead; if the source then
    /// cannot be deleted it is left where it is rather than failing the whole rename. Directories
    /// are never removed. Returns how many files ended up at their target.</summary>
    public static int Apply(IEnumerable<MediaMove> moves)
    {
        int done = 0;
        foreach (var m in moves ?? Array.Empty<MediaMove>())
        {
            if (string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase)) { done++; continue; }
            try
            {
                if (File.Exists(m.To)) continue;      // planned against a stale listing — never clobber
                File.Move(m.From, m.To);
                done++;
            }
            catch
            {
                // Locked by a viewer, or across volumes: a copy still saves the association.
                try
                {
                    if (File.Exists(m.To)) continue;
                    File.Copy(m.From, m.To, overwrite: false);
                    done++;
                    try { File.Delete(m.From); } catch { /* duplicate left behind, on purpose */ }
                }
                catch { /* give up on this file only — the others still move */ }
            }
        }
        return done;
    }
}
