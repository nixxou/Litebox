// The ONE write discipline for every LaunchBox XML this process touches — extracted from
// GameStore.FlushOpsToXml so the direct writers (Platforms.xml / Parents.xml editors) share it
// instead of each truncating the destination in place with a bare StreamWriter.
//
// Three guarantees, in order:
//   1. LB/BB not running — checked at entry AND again immediately before the first swap. The
//      second check is the one that matters: the caller's own check can be minutes old, and even
//      Commit's entry check precedes the backup and the staging, which take real time. LaunchBox
//      holds these files in memory and rewrites them wholesale at exit, so anything we write while
//      it runs is silently lost (or resurrects what we deleted). A refusal returns false; for the
//      journal flush that means "ops stay pending", for a direct writer it means "tell the user to
//      close LaunchBox".
//   2. Backup — the pristine originals (including files about to be DELETED) go into a small
//      timestamped zip under <LB>\Backups\LiteBox before anything is overwritten. NOT best-effort:
//      it is the rollback's only source, so a batch that cannot be backed up is refused outright.
//   3. Atomicity — serialize to a .tmp sibling, then File.Replace: at no instant does the real
//      file hold a truncated document. A power cut mid-write leaves an orphan .tmp, not a
//      destroyed Platforms.xml. Staging happens for the WHOLE batch before the first swap, and
//      deletions only after every swap landed, so a failure is caught while it is still undoable;
//      what did change is then put back from the backup. Across several files this is
//      recoverable, not atomic — NTFS offers no multi-file transaction — but every visible change
//      is reversible until the last one succeeds.
//
// Callers MUST honour the return value: false means the destination does NOT hold the new
// content (see the WAL golden rule in GameStore.FlushOpsToXml — never clear ops that didn't land).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace LbApiHost.Host.Data;

internal static class SafeXmlWrite
{
    /// <summary>Single-file safe write (the direct writers' entry point). A one-element
    /// <see cref="Commit"/>, deliberately: one code path means one behaviour, and in particular
    /// one rollback — a Save of its own would have been the only write here that could not undo
    /// itself.</summary>
    public static bool Save(XDocument doc, string path)
        => Commit(new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase) { [path] = doc },
                  Array.Empty<string>(), DataRootOf(path));

    /// <summary>Batch commit. Backs up every touched file — including the ones about to be
    /// DELETED — then stages, swaps and deletes, in that order and only that order. On any
    /// failure the batch is rolled back from the backup, so the caller either gets all of it or
    /// none of it. Returns false when refused (read-only intent, LaunchBox running, no usable
    /// backup) or when the batch failed; a false return means the destination files hold their
    /// ORIGINAL content, not a half-applied mixture.</summary>
    public static bool Commit(IReadOnlyDictionary<string, XDocument> docs, IReadOnlyCollection<string> deletes, string? dataRoot)
    {
        if (docs.Count == 0 && deletes.Count == 0) return true;
        if (GameStore.IsLaunchBoxRunning())
        { Console.WriteLine("[safewrite] commit refused: LaunchBox/BigBox is running — nothing written"); return false; }

        // What exists RIGHT NOW is what the backup can hold, and therefore what a rollback can
        // put back. Files we are about to CREATE are absent from it on purpose: nothing to save,
        // and undoing them is a delete, not a restore.
        var existedBefore = docs.Keys.Concat(deletes)
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // The backup used to be a courtesy that must "never block the write". It is the rollback's
        // only source now, so no backup means no way back, and we do not start.
        string? zip = Backup(existedBefore, dataRoot, out var zipRoot);
        if (existedBefore.Count > 0 && zip == null)
        { Console.WriteLine("[safewrite] commit refused: the safety backup could not be written"); return false; }

        // Phase 1 — STAGE. Nothing visible changes here: every document is written beside its
        // target. A failure means we abandon before touching anything, which is the cheapest
        // possible outcome and needs no rollback at all.
        var swaps = new List<(string tmp, string file)>();
        bool staged = true;
        foreach (var kv in docs)
        {
            string tmp = kv.Key + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { LbXml.Save(kv.Value, tmp); swaps.Add((tmp, kv.Key)); }
            catch (Exception ex) { Console.WriteLine("[safewrite] stage " + kv.Key + ": " + ex.Message); staged = false; break; }
        }
        if (!staged)
        {
            foreach (var (tmp, _) in swaps) TryDelete(tmp);
            Console.WriteLine("[safewrite] staging failed — nothing was written");
            return false;
        }

        // Ownership is re-checked HERE, not only at entry. Everything above — zipping the backup,
        // serialising each document — takes real time on a large library, and LaunchBox starting in
        // that gap would load the OLD files, sit on them, and write its own snapshot back at exit:
        // our swap would land, the journal would be cleared, and the change would be gone with
        // nothing left to replay. Checking again shrinks that gap from seconds to the instant
        // between this line and the first swap. It cannot be closed entirely — no check can — but
        // this is where it is cheapest to narrow.
        if (GameStore.IsLaunchBoxRunning())
        {
            foreach (var (tmp, _) in swaps) TryDelete(tmp);
            Console.WriteLine("[safewrite] commit abandoned: LaunchBox/BigBox started while staging — nothing was written");
            return false;
        }

        // Phase 2 — SWAP. From here on the library is visibly changing, so a failure has to be undone.
        // What actually CHANGED is tracked as it happens: the loop stops at the first failure, so
        // the remaining targets still hold their original bytes and must not be touched by the
        // rollback. Deleting them only to restore the same content back would widen the blast
        // radius of a failure for no gain — and lose a healthy file if the restore then failed too.
        bool ok = true;
        var swapped = new List<string>();   // now holds NEW content (or is a file we created)
        var erased = new List<string>();    // actually deleted
        foreach (var (tmp, file) in swaps)
            if (ReplaceAtomic(tmp, file)) swapped.Add(file);
            else { ok = false; break; }

        // Phase 3 — DELETE, and only once every swap landed. Erasure is the one act with no
        // natural inverse; keeping it last means a failed batch never reaches it.
        if (ok)
            foreach (var df in deletes)
                try { if (File.Exists(df)) { File.Delete(df); erased.Add(df); } }
                catch (Exception ex) { Console.WriteLine("[safewrite] delete " + df + ": " + ex.Message); ok = false; break; }

        if (!ok)
        {
            foreach (var (tmp, _) in swaps) TryDelete(tmp);       // leftovers from the aborted loop
            var toRestore = swapped.Concat(erased)
                .Where(f => existedBefore.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            Rollback(swapped, toRestore, zip, zipRoot);
            return false;
        }
        return true;
    }

    /// <summary>Put the library back as it was, using the backup taken moments ago.
    ///
    /// Deleting comes FIRST and restoring second, which is not cosmetic: the likeliest reason a
    /// commit failed is a full disk, and freeing the space the new versions occupy is what lets
    /// the originals fit back. Files we CREATED are not in <paramref name="existedBefore"/>, so
    /// step one is also what removes them — no special case needed.</summary>
    private static void Rollback(IEnumerable<string> written, List<string> existedBefore, string? zip, string? zipRoot)
    {
        int removed = 0, restored = 0, failed = 0;
        foreach (var f in written)                                  // 1. everything we wrote, created or modified
            if (TryDelete(f)) removed++;
        if (zip != null && zipRoot != null)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var f in existedBefore)                     // 2. the originals, modified AND deleted
                {
                    string rel = Path.GetRelativePath(zipRoot, f).Replace('\\', '/');
                    var entry = archive.GetEntry(rel);
                    if (entry == null) { failed++; continue; }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                        entry.ExtractToFile(f, overwrite: true);
                        restored++;
                    }
                    catch (Exception ex) { failed++; Console.WriteLine($"[safewrite] restore {Path.GetFileName(f)}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Console.WriteLine("[safewrite] rollback could not read the backup: " + ex.Message); failed++; }
        }
        Console.WriteLine(failed == 0
            ? $"[safewrite] ROLLED BACK cleanly ({removed} removed, {restored} restored from {Path.GetFileName(zip) ?? "-"})"
            : $"[safewrite] rollback INCOMPLETE ({restored} restored, {failed} failed) — recover by hand from {zip ?? "(no backup)"}");
    }

    /// <summary>Test-only hook, fired just before each swap with the destination path; return true
    /// to make THAT swap fail. Two things need it, and both are invisible in normal operation: the
    /// rollback (which never runs when nothing goes wrong, so it would rot unnoticed — and a broken
    /// rollback is worse than none, since it deletes before it restores), and the journal race,
    /// which needs an op appended while a flush is mid-flight. Same shape as
    /// GameStore.ForceLaunchBoxRunning; never set outside a self-test.</summary>
    internal static Func<string, bool>? BeforeSwap;

    // ── Atomic swap (tmp → dest). Returns whether dest now actually holds tmp's content. ──
    //
    // Fails on the FIRST refusal, deliberately. An earlier version retried five times with backoff,
    // copied from ExtDbDownloader — but that retry answers a lock which is EXPECTED there: the
    // extended database is being replaced while LiteBox or the plugin still has it open. Nothing of
    // ours holds these files. LaunchBox and BigBox are checked closed, XDocument.Load opens, parses
    // and closes, and the write goes to a .tmp; only a passing third party (an antivirus, the search
    // indexer) can be in the way, and that is the exceptional case, not the normal one.
    //
    // Waiting for it cost two seconds per file on the UI thread, and — worse — widened the two
    // windows that actually lose data: ops appended to the journal mid-flush, and LaunchBox starting
    // between the ownership check and the first swap. It made real failures likelier to defend
    // against a hypothetical one. Every caller already answers a refusal correctly: the flush keeps
    // its ops pending and retries at the next safe moment, the editors roll back and say so.
    //
    // There is likewise NO copy-over fallback: overwriting the destination with a plain File.Copy is
    // not atomic and destroys the original, the exact opposite of this method's job.
    public static bool ReplaceAtomic(string tmp, string dest)
    {
        if (BeforeSwap?.Invoke(dest) == true)
        { Console.WriteLine($"[safewrite] swap {Path.GetFileName(dest)}: forced failure (self-test)"); return false; }
        try
        {
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
            return true;
        }
        catch (Exception ex)
        { Console.WriteLine($"[safewrite] swap {Path.GetFileName(dest)}: {ex.Message}"); return false; }
    }

    private static bool TryDelete(string path)
    { try { if (File.Exists(path)) { File.Delete(path); return true; } } catch { } return false; }

    // ── Backup: only the dirty files, sub-path relative to <LB>\Data preserved, into a small
    // timestamped zip under <LB>\Backups\LiteBox.
    //
    // Returns the zip's path, or null when nothing could be written — and the caller REFUSES the
    // commit on null. This used to be best-effort ("a backup problem must never block the write"),
    // which was fair while it was a courtesy; it is the rollback's only source now, so a batch
    // without one is a batch that cannot be undone. `zipRoot` comes back too: entry names are
    // relative to it, and the restore needs the same anchor to find them again.
    private static string? Backup(IEnumerable<string> files, string? dataRoot, out string? zipRoot)
    {
        zipRoot = null;
        try
        {
            var list = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return null;
            dataRoot ??= DataRootOf(list[0]);
            string? lbRoot = dataRoot != null ? Path.GetDirectoryName(dataRoot) : null;
            if (string.IsNullOrEmpty(dataRoot) || string.IsNullOrEmpty(lbRoot)) return null;
            string dir = Path.Combine(lbRoot, "Backups", "LiteBox");
            Directory.CreateDirectory(dir);

            var now = DateTime.Now;
            string zipPath = Path.Combine(dir, $"LiteBox Data Backup {now:yyyy-MM-dd HH-mm-ss}.zip");
            int n = 1;
            while (File.Exists(zipPath))   // two commits in the same second
                zipPath = Path.Combine(dir, $"LiteBox Data Backup {now:yyyy-MM-dd HH-mm-ss} ({n++}).zip");

            int added = 0;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var f in list)
                {
                    string rel = Path.GetRelativePath(dataRoot, f).Replace('\\', '/');
                    zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                    added++;
                }

            PruneBackups(dir, 50);
            Console.WriteLine($"[safewrite] backed up {added} file(s) → {zipPath}");
            zipRoot = dataRoot;
            return zipPath;
        }
        catch (Exception ex) { Console.WriteLine("[safewrite] backup FAILED: " + ex.Message); return null; }
    }

    private static void PruneBackups(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "LiteBox Data Backup *.zip");
            if (files.Length <= keep) return;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // timestamped name sorts chronologically
            for (int i = 0; i < files.Length - keep; i++) { try { File.Delete(files[i]); } catch { } }
        }
        catch { }
    }

    // (WriteSwap is gone: Save routes through Commit now, so the single-file path gets the same
    // staging, the same ordering and the same rollback as everything else.)

    /// <summary>Walk up from a file to the enclosing "Data" directory (backup rel-path anchor).
    /// Null when the file isn't under one — the commit is then refused rather than run blind.</summary>
    private static string? DataRootOf(string path)
    {
        try
        {
            for (var d = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (string.Equals(Path.GetFileName(d), "Data", StringComparison.OrdinalIgnoreCase)) return d;
        }
        catch { }
        return null;
    }
}

/// <summary>The internal guard every writer that touches a LaunchBox-owned file consults ITSELF —
/// defense in depth: the greyed-out UI is convenience, this is the mechanism. Wired at boot
/// (HostBoot) to the live store, so a read-only toggle applied mid-session is seen immediately.</summary>
internal static class WriteGuard
{
    /// <summary>Live read-only state; null (not booted / self-test) counts as writable —
    /// the LB-running check below still applies.</summary>
    internal static Func<bool>? IsReadOnly;

    /// <summary>True when writing LaunchBox files must be refused right now, with the reason.</summary>
    public static bool Refuse(out string why)
    {
        if (IsReadOnly?.Invoke() == true)
        { why = "LiteBox is in read-only mode — nothing is written to the LaunchBox files."; return true; }
        if (GameStore.IsLaunchBoxRunning())
        { why = "LaunchBox / BigBox is running — it owns the XML files. Close it first."; return true; }
        why = "";
        return false;
    }

    /// <summary>Should a DIRECT-write editor tab open LOCKED? Read-only, or LaunchBox/BigBox holding
    /// the files — in which case anything typed there would be refused at OK, so it is kinder to say
    /// so before the work than after it.
    ///
    /// Journal-backed tabs must NOT use this: a game's fields, a platform's metadata and its notes
    /// are recorded and applied later, so they stay perfectly editable while LaunchBox runs. Locking
    /// a whole window would take that away.</summary>
    public static bool DirectWriteLocked(bool readOnly) => readOnly || GameStore.IsLaunchBoxRunning();

    /// <summary>Marker appended to a locked tab's section title (empty when nothing is locked).</summary>
    public static string TabLockMark(bool readOnly) => DirectWriteLocked(readOnly) ? " 🔒" : "";

    /// <summary>Window-title marker naming what is locked and why. Read-only wins when both apply —
    /// it locks strictly more (every tab, not just the direct-write ones).</summary>
    public static string TitleMark(bool readOnly)
        => readOnly ? "   [READ-ONLY]"
         : GameStore.IsLaunchBoxRunning() ? "   [LAUNCHBOX OPEN — 🔒 tabs are locked until it closes]"
         : "";

    /// <summary>Sentence for a locked tab's own info line, when it has one.</summary>
    public static string TabLockNote(bool readOnly)
        => readOnly ? "  Read-only: changes here are not saved."
         : GameStore.IsLaunchBoxRunning() ? "  LOCKED: LaunchBox / BigBox is running and owns this file — close it to edit."
         : "";

    /// <summary>Refusal message box for the editors: "«what» was not saved — reason".</summary>
    public static void WarnBlocked(string what, string why)
    {
        try
        {
            System.Windows.Forms.MessageBox.Show($"{what} was not saved.\n\n{why}",
                "LiteBox", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        }
        catch { }
    }
}
