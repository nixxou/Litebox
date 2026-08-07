// Deletion of platforms / categories / playlists from the source tree (single or homogeneous multi).
// Referential integrity: every deletion removes the node's Parents.xml rows on BOTH sides — its own
// memberships AND the rows where it is the PARENT (its children simply lose that parent; with no rows left
// they fall back to Root). Multi-delete processes LEAF-MOST first (deepest in the parent chain) so no step
// ever leaves a dangling parent reference. Media/image files are NOT deleted.
//
// Per kind:
//   platform: <Platform> node + <PlatformFolder>/<ModelSettings>/<PlatformDocument> rows (Platforms.xml),
//             Parents.xml rows (child + parent side), Data\Platforms\<name>.xml (its games), and the game
//             rows in memory (store.DropPlatformRows — NOT journaled: the whole file is removed, journaling
//             would make the flush recreate a skeleton).
//   category: <PlatformCategory> node + Parents.xml rows (child + parent side).
//   playlist: its Data\Playlists\<file> + Parents.xml child rows (playlists are never parents).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class NodeDeleter
{
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");
    private static string ParentsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Parents.xml");

    // Deletion is EXCLUSIVE work: it edits Platforms.xml/Parents.xml directly and deletes whole
    // files — no journal to replay it, no way for erasure to be repaired. The gate requires
    // write-mode, LB/BB closed, a healthy journal, and drains pending edits first (they were
    // computed against the files about to change).
    //
    // Called AFTER the confirmation, never before: the gate drains, and the user then sits on a modal
    // for as long as they like. Background threads journal during that wait (the progress sweep, the RA
    // heartbeat, the store sync), and an op written against a platform about to vanish is dropped at the
    // next flush without a word. Draining last keeps that window to the few milliseconds between the
    // click and the first removal.
    private static bool GateOrExplain(HostDataManagerXml? dm, IWin32Window? owner, string title)
    {
        if (ExclusiveGate.CanRun(dm?.Store, out var why)) return true;
        MessageBox.Show(owner, why, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>The file's DOM, loaded ONCE for the whole batch.
    ///
    /// Three outcomes, and the middle one is why this is not a plain try/catch returning null:
    /// loaded, legitimately ABSENT (a library with no Parents.xml is normal), or UNREADABLE —
    /// malformed, locked, denied. Collapsing the last two meant a deletion would carry on and
    /// erase a platform's games file while its &lt;Platform&gt; node and parent rows stayed on
    /// disk, because "could not parse Platforms.xml" looked exactly like "there is nothing to
    /// remove there". Returns false only on a real read failure.</summary>
    private static bool TryLoad(string file, IWin32Window? owner, string title, out XDocument? doc)
    {
        doc = null;
        if (!File.Exists(file)) return true;                     // absent is a valid state, not a failure
        try { doc = XDocument.Load(file); return true; }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                $"{Path.GetFileName(file)} could not be read, so nothing was deleted:\n\n{ex.Message}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>Commit the batch and say so when part of it did not land — an erasure that half
    /// happened must not look like one that did.</summary>
    private static bool CommitOrWarn(Dictionary<string, XDocument> docs, List<string> deletes,
                                     IWin32Window? owner, string title)
    {
        if (SafeXmlWrite.Commit(docs, deletes, null)) return true;
        MessageBox.Show(owner,
            "Nothing was deleted.\n\nThe library could not be written, so the batch was rolled back "
            + "to its previous state. See the log for what failed; the originals are also kept in "
            + "Backups\\LiteBox.",
            title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // ── platforms ──
    public static bool DeletePlatforms(List<IPlatform> plats, HostDataManagerXml? dm, IWin32Window? owner)
    {
        if (plats == null || plats.Count == 0 || dm == null) return false;
        var names = plats.Select(p => Safe(() => p.Name) ?? "").Where(n => n.Length > 0).ToList();
        int games = 0; foreach (var p in plats) try { games += p.GetAllGames(true, true)?.Length ?? 0; } catch { }
        string what = names.Count == 1 ? $"platform \"{names[0]}\"" : $"{names.Count} platforms";
        if (MessageBox.Show(owner,
                $"Delete {what} and its {games} game(s) from the library?\n\n" +
                "Nested categories/playlists lose this parent (they keep their other parents or move to Root).\n" +
                "Media and ROM files on disk are NOT deleted.",
                "Delete Platform", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!GateOrExplain(dm, owner, "Delete Platform")) return false;

        // ONE transaction for the whole selection: both DOMs edited in memory, every platform file
        // collected, then a single Commit. Removing one platform at a time meant a failure midway
        // through a multi-delete left some nodes gone from Platforms.xml and their Parents rows
        // behind - and one backup zip per platform to reconcile afterwards.
        // Both documents must be READABLE before anything is decided: a games file is about to be
        // erased on the strength of what they say.
        if (!TryLoad(PlatformsFile, owner, "Delete Platform", out var pdoc)) return false;
        if (!TryLoad(ParentsFile, owner, "Delete Platform", out var rdoc)) return false;
        var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        var deletes = new List<string>();
        var going = new List<IPlatform>();
        foreach (var p in plats)
        {
            string name = Safe(() => p.Name) ?? ""; if (name.Length == 0) continue;
            going.Add(p);
            if (pdoc != null)
            {
                int n = 0;
                n += RemoveAll(pdoc, "Platform", e => Eq((string?)e.Element("Name"), name));
                n += RemoveAll(pdoc, "PlatformFolder", e => Eq((string?)e.Element("Platform"), name));
                n += RemoveAll(pdoc, "ModelSettings", e => Eq((string?)e.Element("PlatformName"), name));
                n += RemoveAll(pdoc, "PlatformDocument", e => Eq((string?)e.Element("Platform"), name));
                if (n > 0) docs[PlatformsFile] = pdoc;
            }
            if (rdoc != null && RemoveAll(rdoc, "Parent", e => Eq((string?)e.Element("PlatformName"), name) || Eq((string?)e.Element("ParentPlatformName"), name)) > 0)
                docs[ParentsFile] = rdoc;
            string gamesFile = Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms", Sanitize(name) + ".xml");
            if (File.Exists(gamesFile)) deletes.Add(gamesFile);
        }
        // Memory follows the DISK, never leads it: a refused or rolled-back commit leaves the
        // library exactly as it was, and dropping the rows first would have desynchronised the two.
        if (!CommitOrWarn(docs, deletes, owner, "Delete Platform")) return false;
        foreach (var p in going) dm.DeletePlatformInternal(p);
        return true;
    }

    // ── categories (leaf-most first among the selection) ──
    public static bool DeleteCategories(List<HostPlatformCategory> cats, HostDataManagerXml? dm, IWin32Window? owner)
    {
        if (cats == null || cats.Count == 0 || dm == null) return false;
        var names = cats.Select(c => Safe(() => c.Name) ?? "").Where(n => n.Length > 0).ToList();
        string what = names.Count == 1 ? $"category \"{names[0]}\"" : $"{names.Count} categories";
        if (MessageBox.Show(owner,
                $"Delete {what}?\n\nIts platforms/playlists/sub-categories are NOT deleted — they lose this parent " +
                "(they keep their other parents or move to Root). No games or files are touched.",
                "Delete Category", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!GateOrExplain(dm, owner, "Delete Category")) return false;

        if (!TryLoad(PlatformsFile, owner, "Delete Category", out var pdoc)) return false;
        if (!TryLoad(ParentsFile, owner, "Delete Category", out var rdoc)) return false;
        var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        var going = new List<HostPlatformCategory>();
        foreach (var c in cats.OrderByDescending(c => Depth(Safe(() => c.Name) ?? "")))   // leaf-most first
        {
            string name = Safe(() => c.Name) ?? ""; if (name.Length == 0) continue;
            going.Add(c);
            if (pdoc != null && RemoveAll(pdoc, "PlatformCategory", e => Eq((string?)e.Element("Name"), name)) > 0)
                docs[PlatformsFile] = pdoc;
            if (rdoc != null && RemoveAll(rdoc, "Parent", e => Eq((string?)e.Element("PlatformCategoryName"), name) || Eq((string?)e.Element("ParentPlatformCategoryName"), name)) > 0)
                docs[ParentsFile] = rdoc;
        }
        if (!CommitOrWarn(docs, new List<string>(), owner, "Delete Category")) return false;
        foreach (var c in going) dm.DeleteCategoryInternal(c);
        return true;
    }

    // ── playlists ──
    public static bool DeletePlaylists(List<HostPlaylist> pls, HostDataManagerXml? dm, IWin32Window? owner)
    {
        if (pls == null || pls.Count == 0 || dm == null) return false;
        var names = pls.Select(p => Safe(() => p.Name) ?? "").Where(n => n.Length > 0).ToList();
        string what = names.Count == 1 ? $"playlist \"{names[0]}\"" : $"{names.Count} playlists";
        if (MessageBox.Show(owner,
                $"Delete {what}?\n\nOnly the playlist definition is removed — no games or files are touched.",
                "Delete Playlist", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!GateOrExplain(dm, owner, "Delete Playlist")) return false;

        if (!TryLoad(ParentsFile, owner, "Delete Playlist", out var rdoc)) return false;
        var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        var deletes = new List<string>();
        var going = new List<HostPlaylist>();
        foreach (var pl in pls)
        {
            string id = Safe(() => pl.PlaylistIdValue) ?? "";
            going.Add(pl);
            if (id.Length > 0 && rdoc != null && RemoveAll(rdoc, "Parent", e => Eq((string?)e.Element("PlaylistId"), id)) > 0)
                docs[ParentsFile] = rdoc;
            string file = Safe(() => pl.FileValue) ?? "";
            if (file.Length > 0 && File.Exists(file)) deletes.Add(file);
        }
        if (!CommitOrWarn(docs, deletes, owner, "Delete Playlist")) return false;
        foreach (var pl in going) dm.DeletePlaylistInternal(pl);
        return true;
    }

    // Longest ancestor chain of a category in Parents.xml (any parent kind counts as a hop) — used to order
    // multi-delete leaf-most first.
    private static int Depth(string catName)
    {
        try
        {
            if (!File.Exists(ParentsFile) || catName.Length == 0) return 0;
            var parentsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in XDocument.Load(ParentsFile).Root?.Elements("Parent") ?? Enumerable.Empty<XElement>())
            {
                string child = ((string?)e.Element("PlatformCategoryName") ?? "").Trim();
                if (child.Length == 0) continue;
                string parent = (((string?)e.Element("ParentPlatformCategoryName") ?? "").Trim());
                if (parent.Length == 0) parent = (((string?)e.Element("ParentPlatformName") ?? "").Trim());
                if (parent.Length == 0) continue;
                if (!parentsOf.TryGetValue(child, out var l)) parentsOf[child] = l = new List<string>();
                l.Add(parent);
            }
            int Walk(string n, HashSet<string> path)
            {
                if (!parentsOf.TryGetValue(n, out var ps) || path.Contains(n)) return 0;
                path.Add(n);
                int best = 0;
                foreach (var p in ps) best = Math.Max(best, 1 + Walk(p, path));
                path.Remove(n);
                return best;
            }
            return Walk(catName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    private static bool Eq(string? a, string b) => string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);
    private static int RemoveAll(XDocument doc, string element, Func<XElement, bool> match)
    {
        var hits = doc.Root?.Elements(element).Where(match).ToList();
        if (hits == null || hits.Count == 0) return 0;
        foreach (var e in hits) e.Remove();
        return hits.Count;
    }
    // (SurgicalRemove is gone: each deletion used to load, edit and save a file on its own, which is
    // exactly the per-file commit the batch above replaces.)
    private static string Sanitize(string sn)
    {
        if (string.IsNullOrEmpty(sn)) return sn;
        foreach (var c in Path.GetInvalidFileNameChars()) sn = sn.Replace(c, '_');
        return sn;
    }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
