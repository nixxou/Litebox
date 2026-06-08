// Self-contained round-trip test for the write-back op-log (--selftest-writeback).
// Operates ONLY on throwaway files in a temp dir — never touches the real LB data or
// the real Core\LiteBox.pending.db. Validates:
//   1. OpLog over SQLite (open/append/read/clear) actually binds at runtime.
//   2. The XML applier modifies the targeted fields, preserves unknown fields, leaves
//      untouched games alone, and atomically replaces the file (no .tmp leftovers).

using System;
using System.IO;
using System.Xml.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Tools;

internal static class WriteBackSelfTest
{
    public static int Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "LiteBoxWbTest_" + Guid.NewGuid().ToString("N"));
        int fails = 0;
        try
        {
            Directory.CreateDirectory(temp);
            string platformsDir = Path.Combine(temp, "Platforms");
            Directory.CreateDirectory(platformsDir);

            fails += TestOpLog(temp);
            fails += TestRoundTrip(platformsDir);
        }
        catch (Exception ex) { Console.WriteLine("[selftest] EXCEPTION: " + ex); fails++; }
        finally { try { Directory.Delete(temp, true); } catch { } }

        Console.WriteLine(fails == 0 ? "[selftest] ALL PASS" : $"[selftest] {fails} FAILURE(S)");
        return fails == 0 ? 0 : 1;
    }

    private static int TestOpLog(string dir)
    {
        int f = 0;
        var log = OpLog.Open(Path.Combine(dir, "ops.db"));
        f += Check("oplog enabled (SQLite binds)", log.Enabled);
        log.Append("modify", "Game", "id-1", null, "Developer", "Dev");
        log.Append("modify", "Game", "id-1", null, "Favorite", "true");
        log.Append("delete", "Game", "id-2", null, null, null);
        var ops = log.ReadAll();
        f += Check("oplog read 3 ops in order", ops.Count == 3 && ops[0].Field == "Developer" && ops[2].OpType == "delete");
        log.Clear();
        f += Check("oplog cleared", log.Count() == 0);
        log.Dispose();
        return f;
    }

    private static int TestRoundTrip(string platformsDir)
    {
        int f = 0;
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        string xml = Path.Combine(platformsDir, "TestPlat.xml");
        File.WriteAllText(xml,
            "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n" +
            $"  <Game><ID>{g1}</ID><Title>Game One</Title><Platform>TestPlat</Platform>" +
            "<Developer>OldDev</Developer><FutureField>keep-me</FutureField><Notes>old</Notes></Game>\n" +
            $"  <Game><ID>{g2}</ID><Title>Game Two</Title><Platform>TestPlat</Platform><Developer>Two</Developer></Game>\n" +
            "</LaunchBox>\n");

        var store = GameStore.Load(platformsDir, Path.Combine(platformsDir, "..", "test.pending.db"));
        store.ReadOnly = false;
        int i = store.ById.TryGetValue(g1, out var idx) ? idx : -1;
        f += Check("game1 found in store", i >= 0);
        if (i < 0) { store.CloseLog(); return f; }

        store.SetGameField(i, "Developer", "NewDev");
        store.JournalFavorite(i, true);
        store.JournalStarRating(i, 4f);
        store.SetGameField(i, "ReleaseDate", "2001-02-03T00:00:00.0000000");
        store.SetGameField(i, "Notes", "");                 // clearing → element removed
        store.FlushJournalIfSafe();                          // LB not running → writes XML now
        store.CloseLog();

        var doc = XDocument.Load(xml);
        XElement Game(Guid id) => System.Linq.Enumerable.FirstOrDefault(doc.Root.Elements("Game"),
            e => (string)e.Element("ID") == id.ToString());
        var e1 = Game(g1);
        var e2 = Game(g2);

        f += Check("modified Developer", (string)e1?.Element("Developer") == "NewDev");
        f += Check("modified Favorite", (string)e1?.Element("Favorite") == "true");
        f += Check("StarRating pair written", (string)e1?.Element("StarRatingFloat") == "4" && (string)e1?.Element("StarRating") == "4");
        f += Check("ReleaseDate written", ((string)e1?.Element("ReleaseDate"))?.StartsWith("2001-02-03") == true);
        f += Check("UNKNOWN field preserved", (string)e1?.Element("FutureField") == "keep-me");
        f += Check("cleared Notes removed", e1?.Element("Notes") == null);
        f += Check("untouched game intact", (string)e2?.Element("Developer") == "Two");
        f += Check("no .tmp leftovers", Directory.GetFiles(platformsDir, "*.tmp").Length == 0);
        return f;
    }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "[selftest] PASS  " : "[selftest] FAIL  ") + name);
        return ok ? 0 : 1;
    }
}
