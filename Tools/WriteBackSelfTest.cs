// Self-contained round-trip test for the write-back op-log (--selftest-writeback).
// Operates ONLY on throwaway files in a temp dir — never touches the real LB data or
// the real Core\LiteBox.pending.db. Validates:
//   1. OpLog over SQLite (open/append/read/clear) actually binds at runtime.
//   2. The XML applier modifies the targeted fields, preserves unknown fields, leaves
//      untouched games alone, and atomically replaces the file (no .tmp leftovers).

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
            // Mirror the LB layout (temp\Data\Platforms) so backups derive into temp\Backups\LiteBox.
            string platformsDir = Path.Combine(temp, "Data", "Platforms");
            Directory.CreateDirectory(platformsDir);

            fails += TestOpLog(temp);
            fails += TestRoundTrip(platformsDir);
            fails += TestChildEntities(platformsDir);
            fails += TestGameAddDelete(platformsDir);
            fails += TestEmulators(platformsDir);
            fails += TestPlatforms(platformsDir);
            fails += TestGameMove(platformsDir);
            fails += TestPlaylists(platformsDir);
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

        // Backup: a zip of the pristine originals under <LB>\Backups\LiteBox, sub-path preserved.
        string dataRoot = Path.GetDirectoryName(platformsDir);
        string backupDir = Path.Combine(Path.GetDirectoryName(dataRoot), "Backups", "LiteBox");
        var zips = Directory.Exists(backupDir) ? Directory.GetFiles(backupDir, "*.zip") : Array.Empty<string>();
        f += Check("backup zip created", zips.Length == 1);
        if (zips.Length == 1)
        {
            using var za = ZipFile.OpenRead(zips[0]);
            var entry = za.GetEntry("Platforms/TestPlat.xml");      // Data-relative sub-path kept
            f += Check("backup keeps Data-relative subpath", entry != null);
            if (entry != null)
            {
                using var sr = new StreamReader(entry.Open());
                string original = sr.ReadToEnd();
                f += Check("backup holds pristine original", original.Contains("OldDev") && original.Contains("<Notes>old</Notes>"));
            }
        }
        return f;
    }

    private static int TestChildEntities(string platformsDir)
    {
        int f = 0;
        var gid = Guid.NewGuid();
        string xml = Path.Combine(platformsDir, "ChildPlat.xml");
        File.WriteAllText(xml,
            "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n" +
            $"  <Game><ID>{gid}</ID><Title>Child Host</Title><Platform>ChildPlat</Platform></Game>\n" +
            "</LaunchBox>\n");

        // Add an additional application + a custom field via the plugin API (IGame).
        var store = GameStore.Load(platformsDir, Path.Combine(platformsDir, "..", "child.pending.db"));
        store.ReadOnly = false;
        if (!store.ById.TryGetValue(gid, out var i)) { Check("child: game found", false); store.CloseLog(); return 1; }
        var hg = new HostGame(store, i);
        var app = hg.AddNewAdditionalApplication();
        app.Name = "Disc 2"; app.ApplicationPath = @"games\disc2.cue"; app.Disc = 2;
        var cf = hg.AddNewCustomField(); cf.Name = "Achievements"; cf.Value = "42";
        store.Flush();
        store.CloseLog();

        var doc = XDocument.Load(xml);
        var aa = doc.Root.Elements("AdditionalApplication").FirstOrDefault(e => (string)e.Element("GameID") == gid.ToString());
        var custom = doc.Root.Elements("CustomField").FirstOrDefault(e => (string)e.Element("GameID") == gid.ToString());
        f += Check("child: AdditionalApplication written (Name/Disc)", aa != null && (string)aa.Element("Name") == "Disc 2" && (string)aa.Element("Disc") == "2");
        f += Check("child: AdditionalApplication GameID link", (string)aa?.Element("GameID") == gid.ToString());
        f += Check("child: CustomField written (Name/Value)", custom != null && (string)custom.Element("Name") == "Achievements" && (string)custom.Element("Value") == "42");

        // Reload via a fresh store (proves persistence), then remove the additional app.
        var store2 = GameStore.Load(platformsDir, Path.Combine(platformsDir, "..", "child2.pending.db"));
        store2.ReadOnly = false;
        var hg2 = new HostGame(store2, store2.ById[gid]);
        var apps = hg2.GetAllAdditionalApplications();
        f += Check("child: reload sees 1 additional app", apps.Length == 1);
        bool removed = apps.Length == 1 && hg2.TryRemoveAdditionalApplication(apps[0]);
        store2.Flush();
        store2.CloseLog();
        var doc2 = XDocument.Load(xml);
        bool stillThere = doc2.Root.Elements("AdditionalApplication").Any(e => (string)e.Element("GameID") == gid.ToString());
        f += Check("child: remove → node gone, CustomField kept", removed && !stillThere
            && doc2.Root.Elements("CustomField").Any(e => (string)e.Element("GameID") == gid.ToString()));
        return f;
    }

    private static int TestGameAddDelete(string platformsDir)
    {
        int f = 0;
        // Add a brand-new game on a platform with no existing file → the file must be created.
        var store = GameStore.Load(platformsDir, Path.Combine(platformsDir, "..", "add.pending.db"));
        store.ReadOnly = false;
        int idx = store.AddGameRow("Brand New Game", out var gid);
        var hg = new HostGame(store, idx);
        hg.Platform = "NewConsole";
        hg.Developer = "Acme";
        hg.ReleaseDate = new DateTime(1999, 1, 1);
        hg.Favorite = true;
        store.Flush();
        store.CloseLog();

        string newFile = Path.Combine(platformsDir, "NewConsole.xml");
        f += Check("add: new platform file created", File.Exists(newFile));
        if (File.Exists(newFile))
        {
            var doc = XDocument.Load(newFile);
            var ge = doc.Root.Elements("Game").FirstOrDefault(e => (string)e.Element("ID") == gid.ToString());
            f += Check("add: <Game> node created", ge != null);
            f += Check("add: fields written", ge != null
                && (string)ge.Element("Title") == "Brand New Game"
                && (string)ge.Element("Platform") == "NewConsole"
                && (string)ge.Element("Developer") == "Acme"
                && (string)ge.Element("Favorite") == "true"
                && ((string)ge.Element("ReleaseDate"))?.StartsWith("1999-01-01") == true);
        }

        // Reload (the new game is now a normal game) and delete it.
        var store2 = GameStore.Load(platformsDir, Path.Combine(platformsDir, "..", "add2.pending.db"));
        store2.ReadOnly = false;
        bool del = store2.DeleteGameRow(gid);
        store2.Flush();
        store2.CloseLog();
        var doc2 = XDocument.Load(newFile);
        bool gone = !doc2.Root.Elements("Game").Any(e => (string)e.Element("ID") == gid.ToString());
        f += Check("delete: added game removed from file", del && gone);
        return f;
    }

    private static int TestEmulators(string platformsDir)
    {
        int f = 0;
        string dataDir = Path.GetDirectoryName(platformsDir);
        var emuId = Guid.NewGuid().ToString();
        string emuFile = Path.Combine(dataDir, "Emulators.xml");
        File.WriteAllText(emuFile,
            "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n" +
            $"  <Emulator><ID>{emuId}</ID><Title>RetroArch</Title><ApplicationPath>ra.exe</ApplicationPath></Emulator>\n" +
            $"  <EmulatorPlatform><Emulator>{emuId}</Emulator><Platform>MS-DOS</Platform><CommandLine>-L old</CommandLine></EmulatorPlatform>\n" +
            "</LaunchBox>\n");

        var store = GameStore.Load(platformsDir, Path.Combine(dataDir, "emu.pending.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, Path.Combine(dataDir, "..", "Images")) { ReadOnly = false };

        var emu = dm.GetEmulatorById(emuId);
        f += Check("emu: found", emu != null);
        if (emu == null) { store.CloseLog(); return f; }
        emu.Title = "RetroArch (edited)";
        emu.CommandLine = "-L global";
        emu.EnableHardcoreAchievements = true;
        emu.GetAllEmulatorPlatforms()[0].CommandLine = "-L core2";       // edit existing per-platform cmd
        var ep = emu.AddNewEmulatorPlatform(); ep.Platform = "SNES"; ep.CommandLine = "-L snes"; ep.M3uDiscLoadEnabled = true;
        var ne = dm.AddNewEmulator(); ne.Title = "Standalone"; ne.ApplicationPath = "stand.exe";
        dm.Save(true);
        store.CloseLog();

        var doc = XDocument.Load(emuFile);
        var ee = doc.Root.Elements("Emulator").FirstOrDefault(e => (string)e.Element("ID") == emuId);
        f += Check("emu: modify (Title/CommandLine/flag)", ee != null
            && (string)ee.Element("Title") == "RetroArch (edited)"
            && (string)ee.Element("CommandLine") == "-L global"
            && (string)ee.Element("EnableHardcoreAchievements") == "true");
        var eps = doc.Root.Elements("EmulatorPlatform").Where(e => (string)e.Element("Emulator") == emuId).ToList();
        f += Check("emu: existing platform edited + new platform added", eps.Count == 2
            && eps.Any(e => (string)e.Element("Platform") == "MS-DOS" && (string)e.Element("CommandLine") == "-L core2")
            && eps.Any(e => (string)e.Element("Platform") == "SNES" && (string)e.Element("M3uDiscLoadEnabled") == "true"));
        f += Check("emu: AddNewEmulator persisted", doc.Root.Elements("Emulator").Any(e => (string)e.Element("Title") == "Standalone" && (string)e.Element("ApplicationPath") == "stand.exe"));
        return f;
    }

    private static int TestPlatforms(string platformsDir)
    {
        int f = 0;
        string dataDir = Path.GetDirectoryName(platformsDir);
        string pfile = Path.Combine(dataDir, "Platforms.xml");
        File.WriteAllText(pfile,
            "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n" +
            "  <Platform><Name>TestPlat</Name><Developer>OldDev</Developer></Platform>\n" +
            "  <PlatformCategory><Name>TestCat</Name><Notes>old</Notes></PlatformCategory>\n" +
            "</LaunchBox>\n");

        var store = GameStore.Load(platformsDir, Path.Combine(dataDir, "plat.pending.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, Path.Combine(dataDir, "..", "Images")) { ReadOnly = false };

        var p = dm.GetPlatformByName("TestPlat");
        f += Check("plat: found", p != null);
        if (p == null) { store.CloseLog(); return f; }
        p.Developer = "NewDev"; p.Notes = "platnotes"; p.Media = "Cartridge";
        var c = dm.GetPlatformCategoryByName("TestCat");
        if (c != null) { c.Notes = "catnotes"; c.SortTitle = "ZZ"; }
        var np = dm.AddNewPlatform("Brand New Platform"); np.Developer = "Acme"; np.Manufacturer = "AcmeCorp";
        var nc = dm.AddNewPlatformCategory("Brand New Cat"); nc.Notes = "newcat";
        dm.Save(true);
        store.CloseLog();

        var doc = XDocument.Load(pfile);
        var pe = doc.Root.Elements("Platform").FirstOrDefault(e => (string)e.Element("Name") == "TestPlat");
        f += Check("plat: modify (Developer/Notes/Media)", pe != null
            && (string)pe.Element("Developer") == "NewDev" && (string)pe.Element("Notes") == "platnotes" && (string)pe.Element("Media") == "Cartridge");
        var ce = doc.Root.Elements("PlatformCategory").FirstOrDefault(e => (string)e.Element("Name") == "TestCat");
        f += Check("cat: modify (Notes/SortTitle)", ce != null && (string)ce.Element("Notes") == "catnotes" && (string)ce.Element("SortTitle") == "ZZ");
        f += Check("plat: AddNewPlatform persisted", doc.Root.Elements("Platform").Any(e => (string)e.Element("Name") == "Brand New Platform" && (string)e.Element("Developer") == "Acme"));
        f += Check("cat: AddNewPlatformCategory persisted", doc.Root.Elements("PlatformCategory").Any(e => (string)e.Element("Name") == "Brand New Cat" && (string)e.Element("Notes") == "newcat"));
        return f;
    }

    private static int TestGameMove(string platformsDir)
    {
        int f = 0;
        var gid = Guid.NewGuid();
        string aFile = Path.Combine(platformsDir, "MoveA.xml");
        File.WriteAllText(aFile, "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n" +
            $"  <Game><ID>{gid}</ID><Title>Mover</Title><Platform>MoveA</Platform><Developer>KeepMe</Developer></Game>\n" +
            "</LaunchBox>\n");

        var store = GameStore.Load(platformsDir, Path.Combine(platformsDir, "..", "move.pending.db"));
        store.ReadOnly = false;
        if (!store.ById.TryGetValue(gid, out var i)) { Check("move: game found", false); store.CloseLog(); return 1; }
        new HostGame(store, i).Platform = "MoveB";   // existing game → relocate node
        store.Flush();
        store.CloseLog();

        string bFile = Path.Combine(platformsDir, "MoveB.xml");
        var aDoc = XDocument.Load(aFile);
        f += Check("move: gone from old platform file", !aDoc.Root.Elements("Game").Any(e => (string)e.Element("ID") == gid.ToString()));
        f += Check("move: new platform file created", File.Exists(bFile));
        if (File.Exists(bFile))
        {
            var ge = XDocument.Load(bFile).Root.Elements("Game").FirstOrDefault(e => (string)e.Element("ID") == gid.ToString());
            f += Check("move: in new file, Platform + fields preserved", ge != null
                && (string)ge.Element("Platform") == "MoveB" && (string)ge.Element("Developer") == "KeepMe" && (string)ge.Element("Title") == "Mover");
        }
        return f;
    }

    private static int TestPlaylists(string platformsDir)
    {
        int f = 0;
        string dataDir = Path.GetDirectoryName(platformsDir);
        string plDir = Path.Combine(dataDir, "Playlists");
        Directory.CreateDirectory(plDir);
        var pid = Guid.NewGuid().ToString();
        string pfile = Path.Combine(plDir, "Manual.xml");
        File.WriteAllText(pfile, "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n" +
            $"  <Playlist><PlaylistId>{pid}</PlaylistId><Name>OldList</Name></Playlist>\n" +
            $"  <PlaylistGame><PlaylistId>{pid}</PlaylistId><GameId>g-1</GameId><GameTitle>One</GameTitle></PlaylistGame>\n" +
            "</LaunchBox>\n");

        var store = GameStore.Load(platformsDir, Path.Combine(dataDir, "pl.pending.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, dataDir, Path.Combine(dataDir, "..", "Images")) { ReadOnly = false };

        // Several IPlaylist members are get-only on the SDK interface; set via the concrete type to
        // exercise the write-back machinery (a real plugin reaches the interface-settable subset).
        var pl = dm.GetPlaylistById(pid) as HostPlaylist;
        f += Check("playlist: found", pl != null);
        if (pl == null) { store.CloseLog(); return f; }
        pl.Name = "NewList"; pl.Notes = "plnotes"; pl.SortBy = "Title";
        var pg = (HostPlaylistGame)pl.AddNewPlaylistGame(); pg.GameId = "g-2"; pg.GameTitle = "Two";
        var npl = (HostPlaylist)dm.AddNewPlaylist("Brand New List"); npl.Notes = "fresh";
        dm.Save(true);
        store.CloseLog();

        var doc = XDocument.Load(pfile);
        var pe = doc.Root.Element("Playlist");
        f += Check("playlist: modify (Name/Notes/SortBy)", pe != null
            && (string)pe.Element("Name") == "NewList" && (string)pe.Element("Notes") == "plnotes" && (string)pe.Element("SortBy") == "Title");
        var pgs = doc.Root.Elements("PlaylistGame").ToList();
        f += Check("playlist: game added (now 2)", pgs.Count == 2
            && pgs.Any(e => (string)e.Element("GameId") == "g-2" && (string)e.Element("GameTitle") == "Two")
            && pgs.Any(e => (string)e.Element("GameId") == "g-1"));
        string nfile = Path.Combine(plDir, "Brand New List.xml");
        f += Check("playlist: AddNewPlaylist created file+node", File.Exists(nfile)
            && XDocument.Load(nfile).Root.Elements("Playlist").Any(e => (string)e.Element("Name") == "Brand New List" && (string)e.Element("Notes") == "fresh"));
        return f;
    }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "[selftest] PASS  " : "[selftest] FAIL  ") + name);
        return ok ? 0 : 1;
    }
}
