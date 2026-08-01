// Pooling one game's media into another's, when a combine folds them together.
//
// WHEN. Only when the two games are the SAME database entry (equal DatabaseID). Different entries
// are different games that happen to be merged for convenience, and pooling their art would be
// one-way: an expand hands a game back with a new id and possibly a different title, so nothing
// could be sorted out afterwards.
//
// WHAT NOT TO BRING. Three separate reasons to leave a file behind, and they are not the same test:
//
//   SAME FILE      the two games already resolve to the same file — a title they share, most often.
//                  Nothing to move, and moving would rename art the other game still needs.
//   ALREADY THERE  the destination already holds this exact content ANYWHERE in its media, not just
//                  in the same type and region. A box front filed under World and the same bytes
//                  filed under Europe are one picture, and the second copy is noise.
//   TOO SIMILAR    the destination holds a picture close enough to be the same shot. Perceptual,
//                  not byte-exact, so it catches a re-encode or a rescale. IMAGES ONLY: a video, a
//                  manual or a music track has no meaningful "looks alike", and comparing them by
//                  eye-hash would be nonsense.
//
// BOTH NAMING FORMS. A game's files are either "<title>-NN.ext" (plain, shared by every game
// answering to that title) or "<title>.<guid>-NN.ext" (GUID, belonging to exactly one game). The
// GUID form exists precisely BECAUSE two games can share a title, so the same-title case — the one
// it was tempting to dismiss as "nothing to do, one collection already" — is exactly where it turns
// up. Skipping it meant the absorbed game's GUID files stayed named after a game that no longer
// exists: invisible, unreferenced, and not even offered for deletion. A real library here has 1149
// of them, and nine shared titles on one platform alone.
//
// What comes over takes the form the DESTINATION already uses in that folder. Mixing the two in one
// folder is not neutral: a GUID file present hides the plain ones for that game, so converting half
// a folder would make the other half disappear.
//
// Similarity uses the CPU engines (dhash / phash) the media list already ships, never the CNN one:
// a combine is an interactive action and nobody should wait on a neural net for it. The engine is
// tri-state and a "cannot tell" answer keeps the file — failing open, since dropping art we were
// unsure about is the one outcome with no undo.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Media.Dedup;

namespace LbApiHost.Host.Media;

/// <summary>Why one file is or is not coming along.</summary>
internal enum MergeVerdict
{
    Move,           // brought over
    SameFile,       // both games already point at it
    AlreadyThere,   // identical content already in the destination's media
    TooSimilar,     // near-identical picture already in the destination's media
}

internal sealed record MergeItem(string From, string To, MergeVerdict Verdict, double? Score = null)
{
    public bool Moves => Verdict == MergeVerdict.Move;
}

internal sealed class MergePlan
{
    public readonly List<MergeItem> Items = new();
    public int Moving => Items.Count(i => i.Moves);
    public int Skipped => Items.Count(i => !i.Moves);
    public IEnumerable<MergeItem> Moves => Items.Where(i => i.Moves);
    public override string ToString()
    {
        var by = Items.GroupBy(i => i.Verdict).ToDictionary(g => g.Key, g => g.Count());
        return string.Join(", ", by.Select(kv => $"{kv.Key.ToString().ToLowerInvariant()}={kv.Value}"));
    }
}

internal static class GameMediaMerge
{
    /// <summary>Image extensions worth comparing by eye. Anything else is content-compared only.</summary>
    private static readonly HashSet<string> PictureExts =
        new(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Decides, file by file, what comes over. Reads nothing but the disk — no store, no
    /// data manager — so it can be planned, shown, and applied separately.</summary>
    public static MergePlan Plan(string lbRoot, string platform, string sourceTitle, string destTitle,
                                 DupEngineMode mode, double threshold)
        => Plan(lbRoot, platform, Guid.Empty, sourceTitle, Guid.Empty, destTitle, mode, threshold);

    public static MergePlan Plan(string lbRoot, string platform, Guid sourceId, string sourceTitle,
                                 Guid destId, string destTitle, DupEngineMode mode, double threshold)
    {
        var plan = new MergePlan();
        if (string.IsNullOrEmpty(lbRoot) || string.IsNullOrEmpty(platform)) return plan;
        if (string.IsNullOrEmpty(sourceTitle) || string.IsNullOrEmpty(destTitle)) return plan;

        string from = MediaResolver.Sanitize(sourceTitle), to = MediaResolver.Sanitize(destTitle);
        // Same title does NOT mean one collection: each game may still have GUID-named files of its
        // own. What it does mean is that the PLAIN files are literally the same files for both, so
        // only the GUID ones are separate and worth moving.

        // Two different reaches, on purpose.
        //
        // BYTE-IDENTICAL is judged against the destination's WHOLE collection: the same bytes filed
        // as a box front and as a screenshot are one file however they are labelled, and a second
        // copy is noise wherever it lands.
        //
        // LOOKS-ALIKE is judged only against the SAME type and region — the same folder. Across
        // types it would be nonsense: a box front that happens to resemble a screenshot is not a
        // duplicate of it, and skipping on that would throw away the only art of a category.
        var destByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var destFiles = new List<string>();
        foreach (var unit in GameMediaRenamer.Units(lbRoot, platform))
            foreach (var dir in unit)
            {
                var here = FilesOf(dir, to, destId).ToList();
                destByDir[dir] = here;
                destFiles.AddRange(here);
            }

        // Pinned only matters on the SOURCE side. A pinned file already at the destination is a
        // legitimate resident: comparing against it is exactly right, and skipping a source file
        // because it duplicates one is the outcome we want. What must never happen is the source's
        // own pinned file being moved out from under the path that names it.
        var pinned = PinnedMedia.For(lbRoot, platform);

        var destCrc = new HashSet<uint>();
        foreach (var f in destFiles) { uint? c = Crc(f); if (c.HasValue) destCrc.Add(c.Value); }

        foreach (var unit in GameMediaRenamer.Units(lbRoot, platform))
        {
            // The form the destination already uses HERE decides the form of what arrives, so a
            // folder never ends up holding both for the same game.
            bool destUsesGuid = destId != Guid.Empty && unit.SelectMany(d => Safe(d))
                .Any(f => GameMediaRenamer.TryGuid(Path.GetFileNameWithoutExtension(f), destId, out _, out _));
            var taken = GameMediaRenamer.TakenNumbers(unit, destId, to,
                destUsesGuid ? MediaNameForm.Guid : MediaNameForm.Plain);
            int next = taken.Count > 0 ? taken.Max() + 1 : 0;   // FreeNumber clamps 0 up to 1

            foreach (var dir in unit)
                foreach (var file in FilesOf(dir, from, sourceId))
                {
                    // Not moved, and not orphaned either: the caller builds its orphan list from
                    // these items, so leaving it out here is what keeps a combine from deleting a
                    // document that a surviving game still points at.
                    if (PinnedMedia.IsPinned(pinned, file)) continue;
                    int n = GameMediaRenamer.FreeNumber(taken, next++);
                    string target = destUsesGuid
                        ? GameMediaRenamer.GuidPath(file, to, destId, "", n)
                        : GameMediaRenamer.PlainPath(file, to, n);

                    // Also what protects the plain files when the two games share a title: they are
                    // literally in the destination's own list, so they are caught here. A guard for
                    // that case was written and removed — it could never run, and a mutation test
                    // showed it: disabling it changed nothing.
                    if (destFiles.Any(d => string.Equals(d, file, StringComparison.OrdinalIgnoreCase)))
                    { plan.Items.Add(new MergeItem(file, target, MergeVerdict.SameFile)); continue; }

                    uint? crc = Crc(file);
                    if (crc.HasValue && destCrc.Contains(crc.Value))
                    { plan.Items.Add(new MergeItem(file, target, MergeVerdict.AlreadyThere)); continue; }

                    if (IsPicture(file))
                    {
                        var here = destByDir.TryGetValue(dir, out var l)
                            ? l.Where(IsPicture).ToList() : new List<string>();
                        var (dup, score) = NearestPicture(file, here, mode, threshold);
                        if (dup == true)
                        { plan.Items.Add(new MergeItem(file, target, MergeVerdict.TooSimilar, score)); continue; }
                    }

                    plan.Items.Add(new MergeItem(file, target, MergeVerdict.Move));
                }
        }

        return plan;
    }

    /// <summary>Carries the plan out. The targets are the plan's own — the renamer cannot help
    /// here, because it retags ONE game's files and a merge retags one game's files with another
    /// game's identity.
    ///
    /// <paramref name="sharedSource"/> only ever applies to PLAIN files: those are named after a
    /// title, so a third game answering to it owns them too and they must be copied rather than
    /// moved. A GUID file names one game and no other, so it always moves.</summary>
    public static GameMediaRenamer.MediaMoveResult Apply(MergePlan plan, string lbRoot, Guid sourceId,
                                                        string platform, string sourceTitle, string destTitle,
                                                        bool sharedSource)
    {
        if (plan == null || plan.Moving == 0) return new GameMediaRenamer.MediaMoveResult();
        string from = MediaResolver.Sanitize(sourceTitle ?? "");
        var moves = plan.Moves.Select(i =>
        {
            bool plain = GameMediaRenamer.TryPlain(Path.GetFileNameWithoutExtension(i.From), from, out _);
            return new MediaMove(i.From, i.To, sharedSource && plain);
        });
        return GameMediaRenamer.Apply(moves);
    }

    /// <summary>Every file of that game in one folder. The rule is GameMediaRenamer's, not a copy
    /// of it: a plan that recognises files the mover does not would promise moves that never
    /// happen, which is exactly what a looser copy here did.</summary>
    private static IEnumerable<string> FilesOf(string dir, string sanitizedTitle, Guid id)
        => Safe(dir).Where(f =>
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (id != Guid.Empty && GameMediaRenamer.TryGuid(n, id, out _, out _)) return true;
            return GameMediaRenamer.TryPlain(n, sanitizedTitle, out _);
        }).ToList();

    private static List<string> Safe(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch { return new List<string>(); }
    }

    private static bool IsPicture(string path)
    {
        try { return PictureExts.Contains(Path.GetExtension(path)); } catch { return false; }
    }

    private static uint? Crc(string path)
    {
        try { return CrcBridge.Crc(path); } catch { return null; }
    }

    /// <summary>True when the destination already holds a picture close enough to this one. Null from
    /// the engine means "could not tell", and that keeps the file.</summary>
    private static (bool? dup, double? score) NearestPicture(string file, IReadOnlyList<string> candidates,
                                                            DupEngineMode mode, double threshold)
    {
        if (candidates.Count == 0) return (false, null);
        if (mode == DupEngineMode.Cnn) mode = DupEngineMode.PHash;   // never on an interactive path
        if (!DedupEngine.IsAvailable(mode)) return (null, null);
        // The engine compares against the whole reference set in one call and memoises each
        // fingerprint for the session, so one picture is decoded once however many it is checked
        // against.
        return DedupEngine.Evaluate(mode, threshold, gpu: false, file, candidates);
    }
}
