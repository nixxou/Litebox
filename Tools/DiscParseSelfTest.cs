// Pins how a disc number is read out of a rom name, against LaunchBox's own answers.
//
// Every expectation below was produced by LaunchBox, not by me: forty files named
// "Final Fantasy VIII [TestVersion] <notation>.txt" were fed to its import wizard, which shows the
// disc it derives in a column of its own. The surprises are the point — a bare "Disc 10" is not a
// marker but "- Disc 19" is, "Disk" is a spelling it accepts and "Disque" is not, and it will
// happily return 37 for "(Disc 37 of 2)".
//
// If this ever needs changing, change it from a new observation, not from an opinion about what
// the rule ought to be.

using System;
using LbApiHost.Host.Games;

namespace LbApiHost.Tools;

internal static class DiscParseSelfTest
{
    // Every name is prefixed with "Final Fantasy VIII [TestVersion] " exactly as imported, so the
    // leading unrelated tag is part of what the parser has to skip.
    private static readonly (string Suffix, int? Disc)[] Cases =
    {
        // recognised
        ("(Disc 0)", 0),
        ("(Disc 2 of 50)", 2),
        ("[Disc 3 of 50]", 3),
        ("(Disc 4-50)", 4),
        ("(Disc 9)", 9),
        ("[Disc 11]", 11),
        ("(Disk 13)", 13),                  // "disk" spelling
        ("(DISC 15)", 15),                  // case-insensitive
        ("(Disc 28) (Rev A)", 28),
        ("(Disc 29)(CD 30)", 29),           // first DISC token, not first token
        ("(Disc 36 of 50) [!]", 36),
        ("(Disc 37 of 2)", 37),             // no sanity check against the total
        ("(Disc 100)", 100),
        ("- Disc 19", 19),                  // a dash introduces it just as well as a bracket

        // rejected — wrong keyword
        ("(CD 6)", null),
        ("(CD 31 of 32)", null),
        ("(D35)", null),
        ("(Disque 20)", null),              // close to "disc", still not it
        ("(Part 21)", null),
        ("(Tape 22)", null),
        ("(Cassette 33)", null),
        ("(Volume 34)", null),
        ("(Side A)", null),
        ("(Side B)", null),

        // rejected — no number
        ("(Disc A)", null),
        ("(Disc IV)", null),
        ("(Disc Two)", null),

        // rejected — not introduced by a delimiter
        ("CD5", null),
        ("Disc 1 of 50", null),
        ("Disc 1 of 50 (Rev 1)", null),
        ("Disc 10", null),
        ("disc 14", null),
        ("Disc16", null),
        ("Disc_17", null),
        ("Disc-18", null),
        ("Disk 12", null),
        ("Face A", null),
        ("Face B", null),
        ("Side 1", null),

        // rejected — nothing at all
        ("", null),
    };

    /// <summary>Prints what the parser makes of each name in a file — one per line. Used to record a
    /// prediction BEFORE looking at LaunchBox's answer, so a wrong rule cannot be quietly fitted to
    /// the result afterwards.</summary>
    public static int Predict(string listPath)
    {
        foreach (string raw in System.IO.File.ReadAllLines(listPath))
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            int? d = GameCombiner.DiscIn(name);
            char? s = GameCombiner.SideIn(name);
            Console.WriteLine($"{d?.ToString() ?? "",-4}|{s?.ToString() ?? "",-2}|{name}");
        }
        return 0;
    }

    public static int Run()
    {
        int fail = 0;
        foreach (var (suffix, want) in Cases)
        {
            string name = ("Final Fantasy VIII [TestVersion] " + suffix).TrimEnd();
            int? got = GameCombiner.DiscIn(name);
            if (got != want)
            {
                Console.WriteLine($"[disc] FAIL {suffix,-24} attendu={Show(want)} obtenu={Show(got)}");
                fail++;
            }
        }
        fail += RunSides();
        fail += RunFfx();
        fail += RunTitles();
        fail += RunVersions();
        Console.WriteLine(fail == 0
            ? $"[disc] ALL PASS ({Cases.Length + SideCases.Length + FfxCases.Length} notations + {TitleCases.Length} titres + {VersionCases.Length} versions, toutes relevees sur LaunchBox)"
            : $"[disc] {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }

    // Side markers, from the same 44-game combine: LaunchBox agreed with us on every field of every
    // version except these two, which is how they were found at all.
    private static readonly (string Suffix, char? Side)[] SideCases =
    {
        ("(Side A)", 'A'),
        ("(Side B)", 'B'),
        ("Side 1", null),          // bare: no delimiter, no marker
        ("Face A", null),          // "face" is not a word it knows
        ("Face B", null),
        ("(Disc 9)", null),        // a disc marker is not a side marker
        ("", null),
    };

    private static int RunSides()
    {
        int fail = 0;
        foreach (var (suffix, want) in SideCases)
        {
            string name = ("Final Fantasy VIII [TestVersion] " + suffix).TrimEnd();
            char? got = GameCombiner.SideIn(name);
            if (got != want)
            {
                Console.WriteLine($"[disc] FAIL side {suffix,-19} attendu={want?.ToString() ?? "aucun"} obtenu={got?.ToString() ?? "aucun"}");
                fail++;
            }
        }
        return fail;
    }

    // The second experiment: 130 names combined in one go, covering keyword x separator x delimiter
    // x position for both markers. Full names this time, because position turned out to matter — a
    // name that OPENS with "(Disc 3)" yields nothing. Disc and side are checked together since
    // LaunchBox derives them in one pass.
    private static readonly (string Name, int? Disc, char? Side)[] FfxCases =
    {
        ("(Disc 3) Final Fantasy X", null, null),
        ("Final Fantasy X ( Disc 3 )", null, null),
        ("Final Fantasy X (CD 3)", null, null),
        ("Final Fantasy X (CD 3) (Disc 7)", 7, null),
        ("Final Fantasy X (CD-3)", null, null),
        ("Final Fantasy X (CD.3)", null, null),
        ("Final Fantasy X (CD3)", null, null),
        ("Final Fantasy X (CD_3)", null, null),
        ("Final Fantasy X (D 3)", null, null),
        ("Final Fantasy X (DVD 3)", null, null),
        ("Final Fantasy X (Dis 3)", null, null),
        ("Final Fantasy X (Disc  3)", 3, null),
        ("Final Fantasy X (Disc 03)", 3, null),
        ("Final Fantasy X (Disc 3 Side B)", 3, 'B'),
        ("Final Fantasy X (Disc 3 of 5) (Disc 8)", 3, null),
        ("Final Fantasy X (Disc 3)", 3, null),
        ("Final Fantasy X (Disc 3) (Disc 7)", 3, null),
        ("Final Fantasy X (Disc 3) (Side B)", 3, 'B'),
        ("Final Fantasy X (Disc 3) [Rev A]", 3, null),
        ("Final Fantasy X (Disc 3a)", null, null),
        ("Final Fantasy X (Disc-3)", 3, null),
        ("Final Fantasy X (Disc.3)", 3, null),
        ("Final Fantasy X (Disc3)", null, null),
        ("Final Fantasy X (Disc3a)", null, null),
        ("Final Fantasy X (Disc_3)", 3, null),
        ("Final Fantasy X (Discs 3)", 3, null),
        ("Final Fantasy X (Disk 3)", 3, null),
        ("Final Fantasy X (Disk-3)", 3, null),
        ("Final Fantasy X (Disk.3)", 3, null),
        ("Final Fantasy X (Disk3)", null, null),
        ("Final Fantasy X (Disk_3)", 3, null),
        ("Final Fantasy X (Disque 3)", null, null),
        ("Final Fantasy X (Face A)", null, null),
        ("Final Fantasy X (Face B)", null, null),
        ("Final Fantasy X (Face-A)", null, null),
        ("Final Fantasy X (Face-B)", null, null),
        ("Final Fantasy X (Face.A)", null, null),
        ("Final Fantasy X (Face.B)", null, null),
        ("Final Fantasy X (FaceA)", null, null),
        ("Final Fantasy X (FaceB)", null, null),
        ("Final Fantasy X (Face_A)", null, null),
        ("Final Fantasy X (Face_B)", null, null),
        ("Final Fantasy X (Rev A Disc 3)", null, null),
        ("Final Fantasy X (Rev A) (Disc 3)", 3, null),
        ("Final Fantasy X (Side 1)", null, null),
        ("Final Fantasy X (Side 2)", null, null),
        ("Final Fantasy X (Side A)", null, 'A'),
        ("Final Fantasy X (Side A1)", null, 'A'),
        ("Final Fantasy X (Side AB)", null, 'A'),
        ("Final Fantasy X (Side B)", null, 'B'),
        ("Final Fantasy X (Side B) (Disc 3)", 3, 'B'),
        ("Final Fantasy X (Side C)", null, null),
        ("Final Fantasy X (Side-A)", null, null),
        ("Final Fantasy X (Side-B)", null, null),
        ("Final Fantasy X (Side.A)", null, null),
        ("Final Fantasy X (Side.B)", null, null),
        ("Final Fantasy X (SideA)", null, null),
        ("Final Fantasy X (SideB)", null, null),
        ("Final Fantasy X (Side_A)", null, null),
        ("Final Fantasy X (Side_B)", null, null),
        ("Final Fantasy X (Sides A)", null, null),
        ("Final Fantasy X (SuperDisc 3)", null, null),
        ("Final Fantasy X (The Disc 3)", null, null),
        ("Final Fantasy X - Disc 3", 3, null),
        ("Final Fantasy X - Disc-3", 3, null),
        ("Final Fantasy X - Disc.3", 3, null),
        ("Final Fantasy X - Disc3", null, null),
        ("Final Fantasy X - Disc_3", 3, null),
        ("Final Fantasy X - Side A", null, 'A'),
        ("Final Fantasy X - Side B", null, 'B'),
        ("Final Fantasy X - Side-A", null, null),
        ("Final Fantasy X - Side-B", null, null),
        ("Final Fantasy X - Side.A", null, null),
        ("Final Fantasy X - Side.B", null, null),
        ("Final Fantasy X - SideA", null, null),
        ("Final Fantasy X - SideB", null, null),
        ("Final Fantasy X - Side_A", null, null),
        ("Final Fantasy X - Side_B", null, null),
        ("Final Fantasy X Disc 3", null, null),
        ("Final Fantasy X Disc-3", null, null),
        ("Final Fantasy X Disc.3", null, null),
        ("Final Fantasy X Disc3", null, null),
        ("Final Fantasy X Disc_3", null, null),
        ("Final Fantasy X Side A", null, 'A'),
        ("Final Fantasy X Side B", null, 'B'),
        ("Final Fantasy X Side-A", null, null),
        ("Final Fantasy X Side-B", null, null),
        ("Final Fantasy X Side.A", null, null),
        ("Final Fantasy X Side.B", null, null),
        ("Final Fantasy X SideA", null, null),
        ("Final Fantasy X SideB", null, null),
        ("Final Fantasy X Side_A", null, null),
        ("Final Fantasy X Side_B", null, null),
        ("Final Fantasy X [CD 3]", null, null),
        ("Final Fantasy X [CD-3]", null, null),
        ("Final Fantasy X [CD.3]", null, null),
        ("Final Fantasy X [CD3]", null, null),
        ("Final Fantasy X [CD_3]", null, null),
        ("Final Fantasy X [Disc 3]", 3, null),
        ("Final Fantasy X [Disc-3]", 3, null),
        ("Final Fantasy X [Disc.3]", 3, null),
        ("Final Fantasy X [Disc3]", null, null),
        ("Final Fantasy X [Disc_3]", 3, null),
        ("Final Fantasy X [Disk 3]", 3, null),
        ("Final Fantasy X [Disk-3]", 3, null),
        ("Final Fantasy X [Disk.3]", 3, null),
        ("Final Fantasy X [Disk3]", null, null),
        ("Final Fantasy X [Disk_3]", 3, null),
        ("Final Fantasy X [Face A]", null, null),
        ("Final Fantasy X [Face B]", null, null),
        ("Final Fantasy X [Face-A]", null, null),
        ("Final Fantasy X [Face-B]", null, null),
        ("Final Fantasy X [Face.A]", null, null),
        ("Final Fantasy X [Face.B]", null, null),
        ("Final Fantasy X [FaceA]", null, null),
        ("Final Fantasy X [FaceB]", null, null),
        ("Final Fantasy X [Face_A]", null, null),
        ("Final Fantasy X [Face_B]", null, null),
        ("Final Fantasy X [Rev A] (Disc 3)", 3, null),
        ("Final Fantasy X [Side A]", null, 'A'),
        ("Final Fantasy X [Side B]", null, 'B'),
        ("Final Fantasy X [Side-A]", null, null),
        ("Final Fantasy X [Side-B]", null, null),
        ("Final Fantasy X [Side.A]", null, null),
        ("Final Fantasy X [Side.B]", null, null),
        ("Final Fantasy X [SideA]", null, null),
        ("Final Fantasy X [SideB]", null, null),
        ("Final Fantasy X [Side_A]", null, null),
        ("Final Fantasy X [Side_B]", null, null),
        ("[Disc 3] Final Fantasy X", null, null),
    };

    private static int RunFfx()
    {
        int fail = 0;
        foreach (var (name, disc, side) in FfxCases)
        {
            int? d = GameCombiner.DiscIn(name);
            char? c = GameCombiner.SideIn(name);
            if (d != disc || c != side)
            {
                Console.WriteLine($"[disc] FAIL {name,-46} attendu=disc {Show(disc)}/side {side?.ToString() ?? "aucun"}"
                                  + $" obtenu=disc {Show(d)}/side {c?.ToString() ?? "aucun"}");
                fail++;
            }
        }
        return fail;
    }

    // The title a restored game gets, which is NOT the version label but the file name cleaned the
    // way LaunchBox's importer cleans one. Every pair below is a real (file name -> title) LaunchBox
    // produced, taken from three separate imports; the last two carry a disc, which is the one thing
    // the expand adds and the import does not — the same "[Disk.3]" file imports as "Final Fantasy X"
    // and expands as "Final Fantasy X (Disc 3)".
    private static readonly (string Name, int? Disc, string Title)[] TitleCases =
    {
        (@"(Disc 3) Final Fantasy X", null, @"Final Fantasy X"),
        (@"A.IV - Evolution Global", null, @"A.IV: Evolution Global"),
        (@"Disc 3 Final Fantasy X", null, @"Disc 3 Final Fantasy X"),
        (@"Final Fantasy 7 (Disc 1)", null, @"Final Fantasy 7"),
        (@"Final Fantasy VIII (Disc 1)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII (Disc 2)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [SpecialEdition] (Disc 1)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [SpecialEdition] (Disc 2)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion]", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (CD 31 of 32)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (CD 6)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Cassette 33)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (D35)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (DISC 15)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 0)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 100)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 2 of 50)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 28) (Rev A)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 29)(CD 30)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 36 of 50) [!]", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 37 of 2)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 4-50)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc 9)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc A)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc IV)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disc Two)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disk 13)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Disque 20)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Part 21)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Side A)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Side B)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Tape 22)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] (Volume 34)", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] - Disc 19", null, @"Final Fantasy VIII: Disc 19"),
        (@"Final Fantasy VIII [TestVersion] CD5", null, @"Final Fantasy VIII CD5"),
        (@"Final Fantasy VIII [TestVersion] Disc 1 of 50", null, @"Final Fantasy VIII Disc 1 of 50"),
        (@"Final Fantasy VIII [TestVersion] Disc 1 of 50 (Rev 1)", null, @"Final Fantasy VIII Disc 1 of 50"),
        (@"Final Fantasy VIII [TestVersion] Disc 10", null, @"Final Fantasy VIII Disc 10"),
        (@"Final Fantasy VIII [TestVersion] Disc-18", null, @"Final Fantasy VIII Disc-18"),
        (@"Final Fantasy VIII [TestVersion] Disc16", null, @"Final Fantasy VIII Disc16"),
        (@"Final Fantasy VIII [TestVersion] Disc_17", null, @"Final Fantasy VIII Disc 17"),
        (@"Final Fantasy VIII [TestVersion] Disk 12", null, @"Final Fantasy VIII Disk 12"),
        (@"Final Fantasy VIII [TestVersion] Face A", null, @"Final Fantasy VIII Face A"),
        (@"Final Fantasy VIII [TestVersion] Face B", null, @"Final Fantasy VIII Face B"),
        (@"Final Fantasy VIII [TestVersion] Side 1", null, @"Final Fantasy VIII Side 1"),
        (@"Final Fantasy VIII [TestVersion] [Disc 11]", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] [Disc 3 of 50]", null, @"Final Fantasy VIII"),
        (@"Final Fantasy VIII [TestVersion] disc 14", null, @"Final Fantasy VIII disc 14"),
        (@"Final Fantasy X ( Disc 3 )", null, @"Final Fantasy X"),
        (@"Final Fantasy X (CD 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (CD 3) (Disc 7)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (CD-3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (CD.3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (CD3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (CD_3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (D 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (DVD 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Dis 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc  3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 03)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3 Side B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3 of 5) (Disc 8)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3) (Disc 7)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3) (Side B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3) [Rev A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc 3a)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc-3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc.3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc3a)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disc_3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Discs 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disk 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disk-3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disk.3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disk3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disk_3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Disque 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face-A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face-B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face.A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face.B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (FaceA)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (FaceB)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face_A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Face_B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Rev A Disc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Rev A) (Disc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side 1)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side 2)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side A1)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side AB)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side B) (Disc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side C)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side-A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side-B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side.A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side.B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (SideA)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (SideB)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side_A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Side_B)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (Sides A)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (SuperDisc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X (The Disc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X - Disc 3", null, @"Final Fantasy X: Disc 3"),
        (@"Final Fantasy X - Disc-3", null, @"Final Fantasy X: Disc-3"),
        (@"Final Fantasy X - Disc.3", null, @"Final Fantasy X: Disc.3"),
        (@"Final Fantasy X - Disc3", null, @"Final Fantasy X: Disc3"),
        (@"Final Fantasy X - Disc_3", null, @"Final Fantasy X: Disc 3"),
        (@"Final Fantasy X - Side A", null, @"Final Fantasy X: Side A"),
        (@"Final Fantasy X - Side B", null, @"Final Fantasy X: Side B"),
        (@"Final Fantasy X - Side-A", null, @"Final Fantasy X: Side-A"),
        (@"Final Fantasy X - Side-B", null, @"Final Fantasy X: Side-B"),
        (@"Final Fantasy X - Side.A", null, @"Final Fantasy X: Side.A"),
        (@"Final Fantasy X - Side.B", null, @"Final Fantasy X: Side.B"),
        (@"Final Fantasy X - SideA", null, @"Final Fantasy X: SideA"),
        (@"Final Fantasy X - SideB", null, @"Final Fantasy X: SideB"),
        (@"Final Fantasy X - Side_A", null, @"Final Fantasy X: Side A"),
        (@"Final Fantasy X - Side_B", null, @"Final Fantasy X: Side B"),
        (@"Final Fantasy X Disc 3", null, @"Final Fantasy X Disc 3"),
        (@"Final Fantasy X Disc-3", null, @"Final Fantasy X Disc-3"),
        (@"Final Fantasy X Disc.3", null, @"Final Fantasy X Disc.3"),
        (@"Final Fantasy X Disc3", null, @"Final Fantasy X Disc3"),
        (@"Final Fantasy X Disc_3", null, @"Final Fantasy X Disc 3"),
        (@"Final Fantasy X Side A", null, @"Final Fantasy X Side A"),
        (@"Final Fantasy X Side B", null, @"Final Fantasy X Side B"),
        (@"Final Fantasy X Side-A", null, @"Final Fantasy X Side-A"),
        (@"Final Fantasy X Side-B", null, @"Final Fantasy X Side-B"),
        (@"Final Fantasy X Side.A", null, @"Final Fantasy X Side.A"),
        (@"Final Fantasy X Side.B", null, @"Final Fantasy X Side.B"),
        (@"Final Fantasy X SideA", null, @"Final Fantasy X SideA"),
        (@"Final Fantasy X SideB", null, @"Final Fantasy X SideB"),
        (@"Final Fantasy X Side_A", null, @"Final Fantasy X Side A"),
        (@"Final Fantasy X Side_B", null, @"Final Fantasy X Side B"),
        (@"Final Fantasy X [CD 3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [CD-3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [CD.3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [CD3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [CD_3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disc 3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disc-3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disc.3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disc3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disc_3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disk 3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disk-3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disk.3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disk3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disk_3]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face-A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face-B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face.A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face.B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [FaceA]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [FaceB]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face_A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Face_B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Rev A] (Disc 3)", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side-A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side-B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side.A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side.B]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [SideA]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [SideB]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side_A]", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Side_B]", null, @"Final Fantasy X"),
        (@"[Disc 3] Final Fantasy X", null, @"Final Fantasy X"),
        (@"Final Fantasy X [Disk.3]", 3, @"Final Fantasy X (Disc 3)"),
        (@"[Disc 3] Final Fantasy X", null, @"Final Fantasy X"),
    };

    private static int RunTitles()
    {
        int fail = 0;
        foreach (var (name, disc, want) in TitleCases)
        {
            string got = GameCombiner.TitleFromFileName(name + ".txt", disc);
            if (got != want)
            {
                Console.WriteLine($"[disc] FAIL title {name,-44} attendu=\"{want}\" obtenu=\"{got}\"");
                fail++;
            }
        }
        return fail;
    }

    // The Version a restored game gets. NOT the label the version carried — every pair below came
    // from an expand where 30 of the labels had been deliberately falsified ("ALTERED-004" on a file
    // called "… [Side A].txt") and LaunchBox ignored them outright, deriving from the file name
    // instead. 99 of the other cases agree with the label, which is exactly why copying it survived
    // three experiments before this one.
    private static readonly (string Name, string Version)[] VersionCases =
    {
        (@"(Disc 3) Final Fantasy X", @"(Disc 3) Final Fantasy X"),
        (@"Final Fantasy X ( Disc 3 )", @"( Disc 3 )"),
        (@"Final Fantasy X (CD 3)", @"(CD 3)"),
        (@"Final Fantasy X (CD 3) (Disc 7)", @"(CD 3) (Disc 7)"),
        (@"Final Fantasy X (CD-3)", @"(CD-3)"),
        (@"Final Fantasy X (CD.3)", @"(CD.3)"),
        (@"Final Fantasy X (CD3)", @"(CD3)"),
        (@"Final Fantasy X (CD_3)", @"(CD_3)"),
        (@"Final Fantasy X (D 3)", @"(D 3)"),
        (@"Final Fantasy X (DVD 3)", @"(DVD 3)"),
        (@"Final Fantasy X (Dis 3)", @"(Dis 3)"),
        (@"Final Fantasy X (Disc  3)", @"(Disc  3)"),
        (@"Final Fantasy X (Disc 03)", @"(Disc 03)"),
        (@"Final Fantasy X (Disc 3 Side B)", @"(Disc 3 Side B)"),
        (@"Final Fantasy X (Disc 3 of 5) (Disc 8)", @"(Disc 3 of 5) (Disc 8)"),
        (@"Final Fantasy X (Disc 3)", @"(Disc 3)"),
        (@"Final Fantasy X (Disc 3) (Disc 7)", @"(Disc 3) (Disc 7)"),
        (@"Final Fantasy X (Disc 3) (Side B)", @"(Disc 3) (Side B)"),
        (@"Final Fantasy X (Disc 3) [Rev A]", @"(Disc 3) [Rev A]"),
        (@"Final Fantasy X (Disc 3a)", @"(Disc 3a)"),
        (@"Final Fantasy X (Disc-3)", @"(Disc-3)"),
        (@"Final Fantasy X (Disc.3)", @"(Disc.3)"),
        (@"Final Fantasy X (Disc3)", @"(Disc3)"),
        (@"Final Fantasy X (Disc3a)", @"(Disc3a)"),
        (@"Final Fantasy X (Disc_3)", @"(Disc_3)"),
        (@"Final Fantasy X (Discs 3)", @"(Discs 3)"),
        (@"Final Fantasy X (Disk 3)", @"(Disk 3)"),
        (@"Final Fantasy X (Disk-3)", @"(Disk-3)"),
        (@"Final Fantasy X (Disk.3)", @"(Disk.3)"),
        (@"Final Fantasy X (Disk3)", @"(Disk3)"),
        (@"Final Fantasy X (Disk_3)", @"(Disk_3)"),
        (@"Final Fantasy X (Disque 3)", @"(Disque 3)"),
        (@"Final Fantasy X (Face A)", @"(Face A)"),
        (@"Final Fantasy X (Face B)", @"(Face B)"),
        (@"Final Fantasy X (Face-A)", @"(Face-A)"),
        (@"Final Fantasy X (Face-B)", @"(Face-B)"),
        (@"Final Fantasy X (Face.A)", @"(Face.A)"),
        (@"Final Fantasy X (Face.B)", @"(Face.B)"),
        (@"Final Fantasy X (FaceA)", @"(FaceA)"),
        (@"Final Fantasy X (FaceB)", @"(FaceB)"),
        (@"Final Fantasy X (Face_A)", @"(Face_A)"),
        (@"Final Fantasy X (Face_B)", @"(Face_B)"),
        (@"Final Fantasy X (Rev A Disc 3)", @"(Rev A Disc 3)"),
        (@"Final Fantasy X (Rev A) (Disc 3)", @"(Rev A) (Disc 3)"),
        (@"Final Fantasy X (Side 1)", @"(Side 1)"),
        (@"Final Fantasy X (Side 2)", @"(Side 2)"),
        (@"Final Fantasy X (Side A)", @"(Side A)"),
        (@"Final Fantasy X (Side A1)", @"(Side A1)"),
        (@"Final Fantasy X (Side B)", @"(Side B)"),
        (@"Final Fantasy X (Side B) (Disc 3)", @"(Side B) (Disc 3)"),
        (@"Final Fantasy X (Side C)", @"(Side C)"),
        (@"Final Fantasy X (Side-A)", @"(Side-A)"),
        (@"Final Fantasy X (Side-B)", @"(Side-B)"),
        (@"Final Fantasy X (Side.A)", @"(Side.A)"),
        (@"Final Fantasy X (Side.B)", @"(Side.B)"),
        (@"Final Fantasy X (SideA)", @"(SideA)"),
        (@"Final Fantasy X (SideB)", @"(SideB)"),
        (@"Final Fantasy X (Side_A)", @"(Side_A)"),
        (@"Final Fantasy X (Side_B)", @"(Side_B)"),
        (@"Final Fantasy X (Sides A)", @"(Sides A)"),
        (@"Final Fantasy X (SuperDisc 3)", @"(SuperDisc 3)"),
        (@"Final Fantasy X (The Disc 3)", @"(The Disc 3)"),
        (@"Final Fantasy X - Disc 3", @"Final Fantasy X - Disc 3"),
        (@"Final Fantasy X - Disc-3", @"Final Fantasy X - Disc-3"),
        (@"Final Fantasy X - Disc.3", @"Final Fantasy X - Disc.3"),
        (@"Final Fantasy X - Disc3", @"Final Fantasy X - Disc3"),
        (@"Final Fantasy X - Disc_3", @"Final Fantasy X - Disc_3"),
        (@"Final Fantasy X - Side A", @"Final Fantasy X - Side A"),
        (@"Final Fantasy X - Side B", @"Final Fantasy X - Side B"),
        (@"Final Fantasy X - Side-A", @"Final Fantasy X - Side-A"),
        (@"Final Fantasy X - Side-B", @"Final Fantasy X - Side-B"),
        (@"Final Fantasy X - Side.A", @"Final Fantasy X - Side.A"),
        (@"Final Fantasy X - Side.B", @"Final Fantasy X - Side.B"),
        (@"Final Fantasy X - SideA", @"Final Fantasy X - SideA"),
        (@"Final Fantasy X - SideB", @"Final Fantasy X - SideB"),
        (@"Final Fantasy X - Side_A", @"Final Fantasy X - Side_A"),
        (@"Final Fantasy X - Side_B", @"Final Fantasy X - Side_B"),
        (@"Final Fantasy X Disc 3", @"Final Fantasy X Disc 3"),
        (@"Final Fantasy X Disc-3", @"Final Fantasy X Disc-3"),
        (@"Final Fantasy X Disc.3", @"Final Fantasy X Disc.3"),
        (@"Final Fantasy X Disc3", @"Final Fantasy X Disc3"),
        (@"Final Fantasy X Disc_3", @"Final Fantasy X Disc_3"),
        (@"Final Fantasy X Side A", @"Final Fantasy X Side A"),
        (@"Final Fantasy X Side B", @"Final Fantasy X Side B"),
        (@"Final Fantasy X Side-A", @"Final Fantasy X Side-A"),
        (@"Final Fantasy X Side-B", @"Final Fantasy X Side-B"),
        (@"Final Fantasy X Side.A", @"Final Fantasy X Side.A"),
        (@"Final Fantasy X Side.B", @"Final Fantasy X Side.B"),
        (@"Final Fantasy X SideA", @"Final Fantasy X SideA"),
        (@"Final Fantasy X SideB", @"Final Fantasy X SideB"),
        (@"Final Fantasy X Side_A", @"Final Fantasy X Side_A"),
        (@"Final Fantasy X Side_B", @"Final Fantasy X Side_B"),
        (@"Final Fantasy X [CD 3]", @"[CD 3]"),
        (@"Final Fantasy X [CD-3]", @"[CD-3]"),
        (@"Final Fantasy X [CD.3]", @"[CD.3]"),
        (@"Final Fantasy X [CD3]", @"[CD3]"),
        (@"Final Fantasy X [CD_3]", @"[CD_3]"),
        (@"Final Fantasy X [Disc 3]", @"[Disc 3]"),
        (@"Final Fantasy X [Disc-3]", @"[Disc-3]"),
        (@"Final Fantasy X [Disc.3]", @"[Disc.3]"),
        (@"Final Fantasy X [Disc3]", @"[Disc3]"),
        (@"Final Fantasy X [Disc_3]", @"[Disc_3]"),
        (@"Final Fantasy X [Disk 3]", @"[Disk 3]"),
        (@"Final Fantasy X [Disk-3]", @"[Disk-3]"),
        (@"Final Fantasy X [Disk.3]", @"[Disk.3]"),
        (@"Final Fantasy X [Disk3]", @"[Disk3]"),
        (@"Final Fantasy X [Disk_3]", @"[Disk_3]"),
        (@"Final Fantasy X [Face A]", @"[Face A]"),
        (@"Final Fantasy X [Face B]", @"[Face B]"),
        (@"Final Fantasy X [Face-A]", @"[Face-A]"),
        (@"Final Fantasy X [Face-B]", @"[Face-B]"),
        (@"Final Fantasy X [Face.A]", @"[Face.A]"),
        (@"Final Fantasy X [Face.B]", @"[Face.B]"),
        (@"Final Fantasy X [FaceA]", @"[FaceA]"),
        (@"Final Fantasy X [FaceB]", @"[FaceB]"),
        (@"Final Fantasy X [Face_A]", @"[Face_A]"),
        (@"Final Fantasy X [Face_B]", @"[Face_B]"),
        (@"Final Fantasy X [Rev A] (Disc 3)", @"[Rev A] (Disc 3)"),
        (@"Final Fantasy X [Side A]", @"[Side A]"),
        (@"Final Fantasy X [Side B]", @"[Side B]"),
        (@"Final Fantasy X [Side-A]", @"[Side-A]"),
        (@"Final Fantasy X [Side-B]", @"[Side-B]"),
        (@"Final Fantasy X [Side.A]", @"[Side.A]"),
        (@"Final Fantasy X [Side.B]", @"[Side.B]"),
        (@"Final Fantasy X [SideA]", @"[SideA]"),
        (@"Final Fantasy X [SideB]", @"[SideB]"),
        (@"Final Fantasy X [Side_A]", @"[Side_A]"),
        (@"Final Fantasy X [Side_B]", @"[Side_B]"),
        (@"[Disc 3] Final Fantasy X", @"[Disc 3] Final Fantasy X"),
    };

    private static int RunVersions()
    {
        int fail = 0;
        foreach (var (name, want) in VersionCases)
        {
            string got = GameCombiner.VersionFromFileName(name + ".txt");
            if (got != want)
            {
                Console.WriteLine($"[disc] FAIL version {name,-42} attendu=<{want}> obtenu=<{got}>");
                fail++;
            }
        }
        return fail;
    }

    private static string Show(int? v) => v?.ToString() ?? "aucun";
}
