// The ONE write discipline for every LaunchBox XML this process touches — extracted from
// GameStore.FlushOpsToXml so the direct writers (Platforms.xml / Parents.xml editors) share it
// instead of each truncating the destination in place with a bare StreamWriter.
//
// Three guarantees, in order:
//   1. LB/BB not running — re-checked HERE, at commit time, not only at the caller's entry point.
//      LaunchBox holds these files in memory and rewrites them wholesale at exit: anything we
//      write while it runs is silently lost (or resurrects what we deleted). A refusal returns
//      false; for the journal flush that means "ops stay pending", for a direct writer it means
//      "tell the user to close LaunchBox".
//   2. Backup — the pristine originals (including files about to be DELETED) go into a small
//      timestamped zip under <LB>\Backups\LiteBox before anything is overwritten. Best-effort:
//      a backup failure is logged, never blocks the write.
//   3. Atomicity — serialize to a .tmp sibling, then File.Replace: at no instant does the real
//      file hold a truncated document. A power cut mid-write leaves an orphan .tmp, not a
//      destroyed Platforms.xml.
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
    /// <summary>Single-file safe write (the direct writers' entry point): LB-guard + backup +
    /// tmp + atomic swap. Returns false when refused (LB running) or when the write failed —
    /// the destination is untouched in both cases.</summary>
    public static bool Save(XDocument doc, string path)
    {
        if (GameStore.IsLaunchBoxRunning())
        { Console.WriteLine($"[safewrite] refused {Path.GetFileName(path)}: LaunchBox/BigBox is running"); return false; }
        Backup(new[] { path }, DataRootOf(path));
        return WriteSwap(doc, path);
    }

    /// <summary>Batch commit (the journal flush's write phase): backup every touched file —
    /// including the ones about to be deleted — then write all .tmp, swap all, delete. Returns
    /// false if ANY write failed or the whole batch was refused (LB running): the caller must
    /// keep its ops pending, re-applying is idempotent.</summary>
    public static bool Commit(IReadOnlyDictionary<string, XDocument> docs, IReadOnlyCollection<string> deletes, string? dataRoot)
    {
        if (docs.Count == 0 && deletes.Count == 0) return true;
        if (GameStore.IsLaunchBoxRunning())
        { Console.WriteLine("[safewrite] commit refused: LaunchBox/BigBox is running — nothing written"); return false; }

        Backup(docs.Keys.Concat(deletes), dataRoot);

        // Phase 1: every touched doc to .tmp. A failure here means that doc's changes never make
        // it to disk this pass, so the whole batch must NOT be considered flushed (allOk=false).
        bool allOk = true;
        var swaps = new List<(string tmp, string file)>();
        foreach (var kv in docs)
        {
            string tmp = kv.Key + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { LbXml.Save(kv.Value, tmp); swaps.Add((tmp, kv.Key)); }
            catch (Exception ex) { Console.WriteLine("[safewrite] save tmp " + kv.Key + ": " + ex.Message); allOk = false; }
        }
        // Phase 2: swap all .tmp → real file (atomic per file).
        foreach (var (tmp, file) in swaps)
            if (!ReplaceAtomic(tmp, file)) allOk = false;
        // Phase 2b: whole-file deletes.
        foreach (var df in deletes)
            try { if (File.Exists(df)) File.Delete(df); }
            catch (Exception ex) { Console.WriteLine("[safewrite] delete " + df + ": " + ex.Message); allOk = false; }
        return allOk;
    }

    // ── Atomic swap (tmp → dest). Returns whether dest now actually holds tmp's content. ──
    public static bool ReplaceAtomic(string tmp, string dest)
    {
        try
        {
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[safewrite] atomic replace {dest}: {ex.Message}");
            try { File.Copy(tmp, dest, true); File.Delete(tmp); return true; }
            catch (Exception ex2) { Console.WriteLine($"[safewrite] fallback copy {dest}: {ex2.Message}"); return false; }
        }
    }

    // ── Backup: only the dirty files, sub-path relative to <LB>\Data preserved, into a small
    // timestamped zip under <LB>\Backups\LiteBox. Best-effort — never blocks the write. ──
    private static void Backup(IEnumerable<string> files, string? dataRoot)
    {
        try
        {
            var list = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return;
            dataRoot ??= DataRootOf(list[0]);
            string? lbRoot = dataRoot != null ? Path.GetDirectoryName(dataRoot) : null;
            if (string.IsNullOrEmpty(dataRoot) || string.IsNullOrEmpty(lbRoot)) return;
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
        }
        catch (Exception ex) { Console.WriteLine("[safewrite] backup skipped: " + ex.Message); }
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

    private static bool WriteSwap(XDocument doc, string path)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { LbXml.Save(doc, tmp); }
        catch (Exception ex) { Console.WriteLine("[safewrite] save tmp " + path + ": " + ex.Message); return false; }
        return ReplaceAtomic(tmp, path);
    }

    /// <summary>Walk up from a file to the enclosing "Data" directory (backup rel-path anchor).
    /// Null when the file isn't under one — backup is then skipped, the write still proceeds.</summary>
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
