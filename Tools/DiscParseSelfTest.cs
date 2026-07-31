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
        Console.WriteLine(fail == 0
            ? $"[disc] ALL PASS ({Cases.Length} notations, toutes relevees sur LaunchBox)"
            : $"[disc] {fail}/{Cases.Length} FAILED");
        return fail == 0 ? 0 : 1;
    }

    private static string Show(int? v) => v?.ToString() ?? "aucun";
}
