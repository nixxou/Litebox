using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Generated;
using LbApiHost.Host.Games;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class M3uPlaylistSelfTest
{
    private sealed class TestGame : DummyGame
    {
        public IAdditionalApplication[] Apps = Array.Empty<IAdditionalApplication>();
        public override IAdditionalApplication[] GetAllAdditionalApplications() => Apps;
    }

    public static int Run()
    {
        int fail = 0;
        fail += TestDiscs();
        fail += TestDefaultLaunchAndVersionScore();
        fail += TestVersionTokens();
        fail += TestSides();
        fail += TestDiscSides();
        fail += TestOrdinaryVersion();
        fail += TestSingleEntry();
        fail += TestBrokenSiblings();
        fail += TestNameProximity();
        Console.WriteLine(fail == 0 ? "[m3u] ALL PASS" : $"[m3u] {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }

    private static int TestDiscs()
    {
        var usa1 = App("usa1", @"C:\FF9 USA\Final Fantasy IX (USA) (Disc 1) (Rev 1).zip", 1, "USA", "Rev 1");
        var eu1  = App("eu1",  @"C:\FF9 Europe\Final Fantasy IX (Europe) (Disc 1).chd", 1, "Europe", "");
        // Compound region on purpose: "Europe, Australia" must still count as Europe for the scoring.
        var eu2  = App("eu2",  @"C:\FF9 Europe\Final Fantasy IX (Europe) (Disc 2).chd", 2, "Europe, Australia", "");
        var us2  = App("usa2", @"C:\FF9 USA\Final Fantasy IX (USA) (Disc 2) (Rev 1).zip", 2, "USA", "Rev 1");
        var eu3  = App("eu3",  @"C:\FF9 Europe\Final Fantasy IX (Europe) (Disc 3).chd", 3, "Europe", "");
        var us3  = App("usa3", @"C:\FF9 USA\Final Fantasy IX (USA) (Disc 3) (Rev 1).zip", 3, "USA", "Rev 1");
        var eu4  = App("eu4",  @"C:\FF9 Europe\Final Fantasy IX (Europe) (Disc 4).chd", 4, "Europe", "");
        var us4  = App("usa4", @"C:\FF9 USA\Final Fantasy IX (USA) (Disc 4) (Rev 1).zip", 4, "USA", "Rev 1");
        var game = new TestGame { ApplicationPath = usa1.ApplicationPath, Region = "USA", Version = "Rev 1",
            Apps = new IAdditionalApplication[] { usa1, eu1, eu2, us2, eu3, us3, eu4, us4 } };

        int fail = 0;
        fail += Check("Europe disc 1 selects the Europe release", Plan(game, eu1), eu1, eu2, eu3, eu4);
        fail += Check("Europe disc 2 starts on disc 2 then keeps its release", Plan(game, eu2), eu2, eu1, eu3, eu4);

        game.Apps = game.Apps.Where(a => a != eu3).ToArray();
        fail += Check("missing Europe disc falls back instead of filtering USA", Plan(game, eu1), eu1, eu2, us3, eu4);

        // Fields only: strip the Disc fields and the planner refuses — filenames are never parsed here,
        // the combine already did that once when it wrote the fields.
        eu1.Disc = eu2.Disc = eu4.Disc = null;
        game.Apps = new IAdditionalApplication[] { eu1, eu2, eu4 };
        fail += CheckNull("no Disc fields → no m3u", Plan(game, eu1));
        return fail;
    }

    private static int TestDefaultLaunchAndVersionScore()
    {
        // No selected version: the self-version registered on the launched file donates the fields.
        var main1    = App("main1", @"C:\EU Rev1\Game (Disc 1) (Rev 1).chd", 1, "", "Rev 1");
        var eu2Plain = App("plain", @"C:\EU\Game (Disc 2).chd", 2, "", "Original");
        var eu2Rev1  = App("rev1",  @"C:\EU Rev1\Game (Disc 2) (Rev 1).chd", 2, "", "Rev 1");
        var game = new TestGame
        {
            ApplicationPath = main1.ApplicationPath,
            Region = "", Version = "Rev 1",
            Apps = new IAdditionalApplication[] { main1, eu2Plain, eu2Rev1 },
        };
        return Check("a default launch anchors on its self-version and Version breaks the tie",
            Plan(game, null), main1, eu2Rev1);
    }

    private static int TestVersionTokens()
    {
        // "Disc 1" must not steal credit from "Rev 1" through the shared digit: a lone number glues
        // to its word, so the decoy scores 0 and enumeration order decides.
        var anchor1 = App("a1", @"C:\V\Game (Disc 1).chd", 1, "", "Rev 1");
        var plain2  = App("p2", @"C:\V\Game (Disc 2) plain.chd", 2, "", "Original");
        var decoy2  = App("d2", @"C:\V\Game (Disc 2) decoy.chd", 2, "", "Disc 1");
        var game = new TestGame { ApplicationPath = anchor1.ApplicationPath,
            Apps = new IAdditionalApplication[] { anchor1, plain2, decoy2 } };
        int fail = Check("a stray shared digit does not outrank enumeration order",
            Plan(game, anchor1), anchor1, plain2);

        // Bracketed tags are single tokens and match whole, whatever the wrapping style.
        var anchorB = App("b1", @"C:\W\Game (Disc 1).chd", 1, "", "[Custom Hack]");
        var otherB  = App("b3", @"C:\W\Game (Disc 2) other.chd", 2, "", "[Other Hack]");
        var matchB  = App("b2", @"C:\W\Game (Disc 2) hack.chd", 2, "", "(Custom Hack)");
        game = new TestGame { ApplicationPath = anchorB.ApplicationPath,
            Apps = new IAdditionalApplication[] { anchorB, otherB, matchB } };
        fail += Check("a bracketed tag matches whole across wrapping styles",
            Plan(game, anchorB), anchorB, matchB);
        return fail;
    }

    private static int TestSides()
    {
        var usA = App("usa", @"C:\Tape USA\Game (Side A).tap", null, "USA", ""); usA.SideA = true;
        var euA = App("eua", @"C:\Tape EU\Game (Side A).tap", null, "Europe", ""); euA.SideA = true;
        var usB = App("usb", @"C:\Tape USA\Game (Side B).tap", null, "USA", ""); usB.SideB = true;
        var euB = App("eub", @"C:\Tape EU\Game (Side B).tap", null, "Europe", ""); euB.SideB = true;
        var game = new TestGame { ApplicationPath = usA.ApplicationPath, Region = "USA",
            Apps = new IAdditionalApplication[] { usA, euA, usB, euB } };
        return Check("selected side B is first and selects the matching side A", Plan(game, euB), euB, euA);
    }

    private static int TestDiscSides()
    {
        var d1a = App("1a", @"C:\Combo\Game (Disc 1) (Side A).chd", 1, "Europe", ""); d1a.SideA = true;
        var d1b = App("1b", @"C:\Combo\Game (Disc 1) (Side B).chd", 1, "Europe", ""); d1b.SideB = true;
        var d2a = App("2a", @"C:\Combo\Game (Disc 2) (Side A).chd", 2, "Europe", ""); d2a.SideA = true;
        var d2b = App("2b", @"C:\Combo\Game (Disc 2) (Side B).chd", 2, "Europe", ""); d2b.SideB = true;
        var game = new TestGame { ApplicationPath = d1a.ApplicationPath, Region = "Europe",
            Apps = new IAdditionalApplication[] { d1a, d1b, d2a, d2b } };
        return Check("disc+side uses composite buckets", Plan(game, d2b), d2b, d1a, d1b, d2a);
    }

    private static int TestOrdinaryVersion()
    {
        var normal = App("normal", @"C:\Game\Game (Europe).chd", null, "Europe", "");
        var alternate = App("alt", @"C:\Game\Game (USA).chd", null, "USA", "");
        var game = new TestGame { ApplicationPath = normal.ApplicationPath,
            Apps = new IAdditionalApplication[] { normal, alternate } };
        return CheckNull("ordinary versions do not generate an M3U", Plan(game, normal));
    }

    private static int TestSingleEntry()
    {
        // A lone "Disc 1" version: an m3u with one line is pointless — launch the rom itself.
        var d1 = App("d1", @"C:\Solo\Game (Disc 1).chd", 1, "USA", "");
        var game = new TestGame { ApplicationPath = d1.ApplicationPath,
            Apps = new IAdditionalApplication[] { d1 } };
        return CheckNull("a single-entry set writes no m3u", Plan(game, d1));
    }

    private static int TestBrokenSiblings()
    {
        // The anchor is a disc but NO sibling carries a field: broken metadata, not a lone disc.
        // Everything is appended — best score first (region), then filename order. Apps scrambled
        // on purpose to prove the sort.
        var usa1 = App("usa1", @"C:\Broken\Game (USA) (Disc 1).chd", 1, "USA", "");
        var no2  = App("no2",  @"C:\Broken\Game (USA) (Disc 2).chd", null, "USA", "");
        var no3  = App("no3",  @"C:\Broken\Game (USA) (Disc 3).chd", null, "USA", "");
        var euX  = App("eux",  @"C:\Broken\Game (Europe) (Disc 2).chd", null, "Europe", "");
        var game = new TestGame { ApplicationPath = usa1.ApplicationPath,
            Apps = new IAdditionalApplication[] { usa1, no3, euX, no2 } };
        int fail = Check("fieldless siblings of a disc anchor are appended by score then name",
            Plan(game, usa1), usa1, no2, no3, euX);

        // Same rule for sides.
        var sA = App("sa", @"C:\TapeX\Game (Side A).tap", null, "", ""); sA.SideA = true;
        var sX = App("sx", @"C:\TapeX\Game (Side B).tap", null, "", "");
        game = new TestGame { ApplicationPath = sA.ApplicationPath,
            Apps = new IAdditionalApplication[] { sA, sX } };
        fail += Check("fieldless sibling of a side anchor is appended too", Plan(game, sA), sA, sX);
        return fail;
    }

    private static int TestNameProximity()
    {
        // Equal scores in a bucket → the filename closest to the launched one wins, whatever the
        // enumeration order: sibling discs of one release differ by a digit.
        var a1   = App("a1",   @"C:\Prox\Game v2 (Disc 1).chd", 1, "", "");
        var far  = App("far",  @"C:\Prox\Game v1 (Disc 2).chd", 2, "", "");
        var near = App("near", @"C:\Prox\Game v2 (Disc 2).chd", 2, "", "");
        var game = new TestGame { ApplicationPath = a1.ApplicationPath,
            Apps = new IAdditionalApplication[] { a1, far, near } };
        return Check("a score tie falls to the closest filename", Plan(game, a1), a1, near);
    }

    private static DummyAdditionalApplication App(string id, string path, int? disc, string region, string version) => new()
    {
        Id = id, Name = System.IO.Path.GetFileNameWithoutExtension(path), ApplicationPath = path,
        Disc = disc, Region = region, Version = version, UseEmulator = true,
    };

    private static IReadOnlyList<string> Plan(TestGame game, IAdditionalApplication selected)
        => M3uPlaylistPlanner.Plan(game, selected, selected?.ApplicationPath ?? game.ApplicationPath, game.Apps, p => p);

    private static int Check(string name, IReadOnlyList<string> got, params IAdditionalApplication[] expected)
    {
        var want = expected.Select(a => a.ApplicationPath).ToArray();
        bool ok = got != null && got.SequenceEqual(want, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"[m3u] {(ok ? "PASS" : "FAIL")} {name}" + (ok ? "" : $"\n  want: {string.Join(" | ", want)}\n  got:  {string.Join(" | ", got ?? Array.Empty<string>())}"));
        return ok ? 0 : 1;
    }

    private static int CheckNull(string name, IReadOnlyList<string> got)
    {
        bool ok = got == null;
        Console.WriteLine($"[m3u] {(ok ? "PASS" : "FAIL")} {name}" + (ok ? "" : $" → got: {string.Join(" | ", got)}"));
        return ok ? 0 : 1;
    }
}
