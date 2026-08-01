// Decides WHEN a game's media follow a title change, and drives GameMediaRenamer accordingly.
// GameMediaRenamer knows how to move files; this decides whether to, and toward which form.
//
// The regime comes straight from the write-back model (see GameStore):
//
//   read-only            the title is never written to the XML — it reverts when LiteBox closes.
//                        Moving files would orphan them against a title that never existed on
//                        disk, so NOTHING is touched.
//   LaunchBox running    the XML keeps the old title for now. Files go to the GUID form, which
//                        LaunchBox finds under the old title and LiteBox under the new one.
//                        ReconcileAfterFlush brings them back to plain once the title lands.
//   otherwise            the XML is about to receive the new title → plain(old) → plain(new).
//
// A COLLISION — another game on the same platform already carrying the new title — pins both games
// to the GUID form for good, because a plain name is shared by every game with that title.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class GameMediaSync
{
    private static GameStore? _store;

    /// <summary>Options → General. Read live so toggling it takes effect without a restart.</summary>
    private static bool Enabled
    {
        get { try { return LiteBoxConfig.LoadForExe().RenameMediaWithGame; } catch { return false; } }
    }

    /// <summary>Wires the flush hook: once a Title op reaches the XML, the transit GUID names can
    /// go back to plain. Called once at boot.</summary>
    public static void Attach(GameStore store)
    {
        _store = store;
        if (store == null) return;
        store.TitlesFlushed = ids => { try { ReconcileAfterFlush(ids); } catch { } };
    }

    /// <summary>Called right after a game's Title has been changed in memory.</summary>
    public static void OnTitleChanged(IGame game, string oldTitle, string newTitle)
    {
        try
        {
            if (game == null) return;
            oldTitle ??= ""; newTitle ??= "";
            if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal)) return;
            // Le titre a change, le NOM DE FICHIER pas forcement : c'est la seule chose qui compte
            // ici. Rien a deplacer, rien a convertir, aucun transit a mettre en place.
            if (GameMediaRenamer.SameTargetName(oldTitle, newTitle)) return;
            // Read-only: the rename is a memory-only illusion, so the files must not follow it.
            if (_store == null || _store.ReadOnly) return;
            // Opt-in: LaunchBox itself leaves media behind on a rename, so this only runs when asked.
            if (!Enabled) return;

            string lbRoot = MediaResolver.LbRoot;
            string platform = Safe(() => game.Platform) ?? "";
            if (string.IsNullOrEmpty(lbRoot) || platform.Length == 0) return;
            if (!Guid.TryParse(Safe(() => game.Id) ?? "", out var id) || id == Guid.Empty) return;

            var rival = FindRival(game, platform, newTitle);
            bool merge = rival != null && SameDatabaseEntry(game, rival);
            bool deferred = GameStore.IsLaunchBoxRunning();

            // A rival is NOT automatically a collision. Two entries carrying the same database id
            // are the same game, so the user is consolidating: that is a MERGE, and the files
            // already there are the destination — they must not move, ours join them numbered
            // after. A genuine collision is a rival with a DIFFERENT database id, and only that
            // pins both to the GUID form.
            var target = (deferred || (rival != null && !merge)) ? MediaNameForm.Guid : MediaNameForm.Plain;

            // Another game still answering to the OLD title means these files are its media too:
            // copy rather than move, or renaming this game would strip that one.
            bool sharedSource = FindRival(game, platform, oldTitle) != null;

            Guid.TryParse(Safe(() => rival?.Id) ?? "", out var destGuid);
            int moved = Move(lbRoot, id, platform, oldTitle, newTitle, target, merge, sharedSource,
                             merge ? destGuid : default);

            // Only a real collision drags the other game along: a plain name belongs to a TITLE
            // rather than to a game, so leaving both plain would make the two indistinguishable.
            if (rival != null && !merge && !sharedSource
                && Guid.TryParse(Safe(() => rival.Id) ?? "", out var rid) && rid != Guid.Empty)
                moved += Move(lbRoot, rid, platform, newTitle, newTitle, MediaNameForm.Guid);

            if (moved > 0) RebuildCache(platform);
        }
        catch { }
    }

    /// <summary>Pools one game's media into another's, for a COMBINE of two entries that are the
    /// same database game. The destination keeps its files and their order — the source's are
    /// appended after the highest number already present. Only ever called when the two share a
    /// DatabaseID: for anything else the merge cannot be undone by an expand, so the files stay.</summary>
    public static void MergeInto(IGame source, IGame destination)
    {
        try
        {
            if (source == null || destination == null || !Enabled) return;
            if (_store == null || _store.ReadOnly) return;

            string lbRoot = MediaResolver.LbRoot;
            string platform = Safe(() => source.Platform) ?? "";
            string from = Safe(() => source.Title) ?? "";
            string to = Safe(() => destination.Title) ?? "";
            if (string.IsNullOrEmpty(lbRoot) || platform.Length == 0 || from.Length == 0 || to.Length == 0) return;
            if (!Guid.TryParse(Safe(() => source.Id) ?? "", out var id) || id == Guid.Empty) return;

            // A THIRD game still answering to the source title means these files are its media too,
            // so copy rather than move — exactly as on a rename. The destination itself does not
            // count: it is the one we are pooling into.
            var rival = FindRival(source, platform, from);
            bool shared = rival != null && !string.Equals(
                Safe(() => rival.Id) ?? "", Safe(() => destination.Id) ?? "", StringComparison.OrdinalIgnoreCase);

            if (Move(lbRoot, id, platform, from, to, MediaNameForm.Plain, merge: true, sharedSource: shared) > 0)
                RebuildCache(platform);
        }
        catch { }
    }

    /// <summary>Called once a flush has written Title ops: the transit form has served its purpose,
    /// so each of those games goes back to plain names — unless a rival still holds that title.</summary>
    public static void ReconcileAfterFlush(IReadOnlyList<Guid> gameIds)
    {
        if (gameIds == null || gameIds.Count == 0 || !Enabled) return;
        string lbRoot = MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(lbRoot)) return;

        var platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in gameIds)
        {
            try
            {
                var game = FindGame(id);
                if (game == null) continue;
                string platform = Safe(() => game.Platform) ?? "";
                string title = Safe(() => game.Title) ?? "";
                if (platform.Length == 0 || title.Length == 0) continue;
                // Still shared with another game → the GUID form is the destination, not a transit.
                if (FindRival(game, platform, title) != null) continue;
                if (Move(lbRoot, id, platform, title, title, MediaNameForm.Plain) > 0) platforms.Add(platform);
            }
            catch { }
        }
        foreach (var p in platforms) RebuildCache(p);
    }

    private static int Move(string lbRoot, Guid id, string platform, string diskTitle, string targetTitle,
        MediaNameForm form, bool merge = false, bool sharedSource = false, Guid destId = default)
    {
        var plan = GameMediaRenamer.Plan(lbRoot, id, platform, diskTitle, targetTitle, form, merge, sharedSource);
        if (plan.Count == 0) return 0;

        // A MERGE goes through the same filters a combine uses. Two ways of putting one game's media
        // on another's title were giving two different results: the combine dropped exact duplicates
        // and near-identical pictures, the rename piled everything up. Same destination, same
        // question, so the same answer.
        if (merge)
        {
            var decided = GameMediaMerge.Plan(lbRoot, platform, id, diskTitle, destId, targetTitle,
                                              Dedup.DupEngineMode.DHash,
                                              Dedup.DedupEngine.DefaultThreshold(Dedup.DupEngineMode.DHash));
            var keep = new HashSet<string>(decided.Moves.Select(i => i.From), StringComparer.OrdinalIgnoreCase);
            var left = plan.Where(m => !keep.Contains(m.From)).Select(m => m.From).ToList();
            plan = plan.Where(m => keep.Contains(m.From)).ToList();
            // What the filters turned down has nobody left to belong to once the rename lands.
            MediaCleanup.Delete(left, "merge par renommage");
            if (plan.Count == 0) return 0;
        }
        var result = GameMediaRenamer.Apply(plan);
        // One line per rename that was not perfectly clean, so a half-moved collection can be
        // explained after the fact instead of looking like the feature failing to run.
        if (!result.AllGood)
            Diag.LbLog.Warn("media", $"\"{targetTitle}\" [{platform}] {form}: {result} (planned {plan.Count})");
        return result.Reached;
    }

    /// <summary>Same entry in the LaunchBox database — the two rows describe ONE game, so bringing
    /// their media together is a merge rather than a clash. An id missing on either side means we
    /// cannot tell, and an unprovable merge is treated as a collision: pinning both to GUID is
    /// recoverable, pouring one game's media into another's is not.</summary>
    private static bool SameDatabaseEntry(IGame a, IGame b)
    {
        int? ida = Safe(() => a.LaunchBoxDbId), idb = Safe(() => b.LaunchBoxDbId);
        return ida.HasValue && idb.HasValue && ida.Value == idb.Value;
    }

    /// <summary>Another game of the same platform already carrying this title. Compared on the
    /// SANITIZED name, since that is what ends up in a filename — two titles differing only by a
    /// character the sanitizer folds would still collide on disk.</summary>
    /// <summary>Another game of the same platform answering to that title — so the files named
    /// after it are its media too. Internal because a combine has to ask the same question before
    /// it moves or deletes anything.</summary>
    internal static IGame? FindRival(IGame game, string platform, string title)
    {
        string wanted = MediaResolver.Sanitize(title ?? "");
        if (wanted.Length == 0) return null;
        string ownId = Safe(() => game.Id) ?? "";
        try
        {
            var plat = PluginHelper.DataManager?.GetPlatformByName(platform);
            var games = plat?.GetAllGames(true, true) ?? Array.Empty<IGame>();
            foreach (var other in games)
            {
                if (other == null) continue;
                if (string.Equals(Safe(() => other.Id) ?? "", ownId, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(MediaResolver.Sanitize(Safe(() => other.Title) ?? ""), wanted, StringComparison.OrdinalIgnoreCase))
                    return other;
            }
        }
        catch { }
        return null;
    }

    private static IGame? FindGame(Guid id)
    {
        try { return PluginHelper.DataManager?.GetGameById(id.ToString("D")); }
        catch { return null; }
    }

    private static void RebuildCache(string platform)
    {
        try
        {
            var plat = PluginHelper.DataManager?.GetPlatformByName(platform);
            if (plat != null) GameCacheBridge.RebuildPlatform(plat);
        }
        catch { }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
