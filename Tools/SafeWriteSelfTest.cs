// Failure-injection tests for SafeXmlWrite — the half of that file that never runs when things go
// well, and would therefore rot without anyone noticing.
//
// A rollback that is broken is WORSE than no rollback: it deletes before it restores, so a bug there
// destroys files that a plain "leave the mess and warn" would have left intact. That asymmetry is why
// this exists, and why every case below ends by asserting the ORIGINAL bytes are back.
//
// The cases mirror how a commit actually dies in the field: a document that cannot be staged (full
// disk, denied path), a swap that will not take (the file held open by a scanner), and a batch that
// mixes writes with deletions — the one shape where a partial application used to erase a games file
// while leaving its metadata behind.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Tools;

internal static class SafeWriteSelfTest
{
    public static int Run()
    {
        int fail = 0;
        bool? forcedLb = GameStore.ForceLaunchBoxRunning;
        GameStore.ForceLaunchBoxRunning = false;   // the tree below is one LaunchBox never heard of
        try
        {
            fail += StagingFailureTouchesNothing();
            fail += SwapFailureRollsBackEveryFile();
            fail += DeletionsNeverRunWhenASwapFails();
            fail += UntouchedFilesAreLeftAloneOnRollback();
            fail += SuccessStillWrites();
        }
        finally
        {
            SafeXmlWrite.FailSwapFor = null;
            GameStore.ForceLaunchBoxRunning = forcedLb;
        }
        Console.WriteLine(fail == 0 ? "[safewrite] ALL PASS" : $"[safewrite] {fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static int Check(string what, bool ok)
    { Console.WriteLine((ok ? "[safewrite] PASS  " : "[safewrite] FAIL  ") + what); return ok ? 0 : 1; }

    /// <summary>A throw-away &lt;root&gt;/Data tree — SafeXmlWrite anchors its backup on the enclosing
    /// "Data" directory, so the shape matters, not just the files.</summary>
    private static string NewTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "lb-safewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "Platforms"));
        return root;
    }

    private static string Write(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<?xml version=\"1.0\" standalone=\"yes\"?>\r\n<LaunchBox>\r\n  <Marker>{title}</Marker>\r\n</LaunchBox>");
        return path;
    }

    private static XDocument Doc(string marker)
        => new(new XElement("LaunchBox", new XElement("Marker", marker)));

    private static string Marker(string path)
    { try { return XDocument.Load(path).Root?.Element("Marker")?.Value ?? "?"; } catch { return "<unreadable>"; } }

    private static void Nuke(string dir) { try { Directory.Delete(dir, true); } catch { } }

    // ── cases ─────────────────────────────────────────────────────────────────

    /// <summary>A document that cannot even be staged must leave the library untouched — and must
    /// not reach the swap, so there is nothing to roll back.</summary>
    private static int StagingFailureTouchesNothing()
    {
        string root = NewTree();
        try
        {
            string a = Write(Path.Combine(root, "Data", "Platforms.xml"), "ORIGINAL-A");
            // A path inside a file: staging it throws, which is what a denied or full target does.
            string bogus = Path.Combine(a, "impossible", "Parents.xml");
            var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase)
            { [a] = Doc("NEW-A"), [bogus] = Doc("NEW-B") };

            bool ok = SafeXmlWrite.Commit(docs, Array.Empty<string>(), null);
            int f = Check("staging failure: commit refuses", !ok);
            f += Check("staging failure: the other file is untouched", Marker(a) == "ORIGINAL-A");
            f += Check("staging failure: no .tmp left behind",
                !Directory.EnumerateFiles(Path.Combine(root, "Data"), "*.tmp").Any());
            return f;
        }
        finally { Nuke(root); }
    }

    /// <summary>The dangerous one: the first file swaps, the second refuses. Both must come back.</summary>
    private static int SwapFailureRollsBackEveryFile()
    {
        string root = NewTree();
        try
        {
            string a = Write(Path.Combine(root, "Data", "Platforms.xml"), "ORIGINAL-A");
            string b = Write(Path.Combine(root, "Data", "Parents.xml"), "ORIGINAL-B");
            var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase)
            { [a] = Doc("NEW-A"), [b] = Doc("NEW-B") };

            SafeXmlWrite.FailSwapFor = p => string.Equals(p, b, StringComparison.OrdinalIgnoreCase);
            bool ok = SafeXmlWrite.Commit(docs, Array.Empty<string>(), null);
            SafeXmlWrite.FailSwapFor = null;

            int f = Check("swap failure: commit reports failure", !ok);
            f += Check("swap failure: the file that DID swap is restored", Marker(a) == "ORIGINAL-A");
            f += Check("swap failure: the file that did not swap is intact", Marker(b) == "ORIGINAL-B");
            f += Check("swap failure: no .tmp left behind",
                !Directory.EnumerateFiles(Path.Combine(root, "Data"), "*.tmp").Any());
            return f;
        }
        finally { SafeXmlWrite.FailSwapFor = null; Nuke(root); }
    }

    /// <summary>Deletions are last on purpose. A swap that fails must mean the games file is still
    /// there — the exact case where a partial commit used to erase games and keep their metadata.</summary>
    private static int DeletionsNeverRunWhenASwapFails()
    {
        string root = NewTree();
        try
        {
            string a = Write(Path.Combine(root, "Data", "Platforms.xml"), "ORIGINAL-A");
            string games = Write(Path.Combine(root, "Data", "Platforms", "Doomed.xml"), "GAMES");
            var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase) { [a] = Doc("NEW-A") };

            SafeXmlWrite.FailSwapFor = p => string.Equals(p, a, StringComparison.OrdinalIgnoreCase);
            bool ok = SafeXmlWrite.Commit(docs, new[] { games }, null);
            SafeXmlWrite.FailSwapFor = null;

            int f = Check("delete ordering: commit reports failure", !ok);
            f += Check("delete ordering: the games file was NOT erased", File.Exists(games));
            f += Check("delete ordering: metadata is unchanged", Marker(a) == "ORIGINAL-A");
            return f;
        }
        finally { SafeXmlWrite.FailSwapFor = null; Nuke(root); }
    }

    /// <summary>A file the batch never wrote must not be deleted-and-restored on the way out: the
    /// rollback has to touch what changed, nothing else. Detected by making it read-only — a
    /// needless delete would throw, a correct rollback never tries.</summary>
    private static int UntouchedFilesAreLeftAloneOnRollback()
    {
        string root = NewTree();
        string c = Path.Combine(root, "Data", "Emulators.xml");
        try
        {
            string a = Write(Path.Combine(root, "Data", "Platforms.xml"), "ORIGINAL-A");
            Write(c, "ORIGINAL-C");
            File.SetAttributes(c, FileAttributes.ReadOnly);
            var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase)
            { [a] = Doc("NEW-A"), [c] = Doc("NEW-C") };

            // A fails first, so C is never swapped and must be left exactly as it is.
            SafeXmlWrite.FailSwapFor = p => string.Equals(p, a, StringComparison.OrdinalIgnoreCase);
            bool ok = SafeXmlWrite.Commit(docs, Array.Empty<string>(), null);
            SafeXmlWrite.FailSwapFor = null;

            int f = Check("rollback scope: commit reports failure", !ok);
            f += Check("rollback scope: the never-swapped file still exists", File.Exists(c));
            f += Check("rollback scope: its content is untouched", Marker(c) == "ORIGINAL-C");
            return f;
        }
        finally
        {
            SafeXmlWrite.FailSwapFor = null;
            try { if (File.Exists(c)) File.SetAttributes(c, FileAttributes.Normal); } catch { }
            Nuke(root);
        }
    }

    /// <summary>And the happy path still works — a rollback that fired on success would be the
    /// funniest possible regression.</summary>
    private static int SuccessStillWrites()
    {
        string root = NewTree();
        try
        {
            string a = Write(Path.Combine(root, "Data", "Platforms.xml"), "ORIGINAL-A");
            string games = Write(Path.Combine(root, "Data", "Platforms", "Doomed.xml"), "GAMES");
            var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase) { [a] = Doc("NEW-A") };

            bool ok = SafeXmlWrite.Commit(docs, new[] { games }, null);
            int f = Check("success: commit reports success", ok);
            f += Check("success: the write landed", Marker(a) == "NEW-A");
            f += Check("success: the deletion happened", !File.Exists(games));
            f += Check("success: a backup zip was written",
                Directory.Exists(Path.Combine(root, "Backups", "LiteBox"))
                && Directory.EnumerateFiles(Path.Combine(root, "Backups", "LiteBox"), "*.zip").Any());
            return f;
        }
        finally { Nuke(root); }
    }
}
