using System;
using LbApiHost.Host;

namespace LbApiHost.Tools;

internal static class TitleSortSelfTest
{
    public static int Run()
    {
        int failures = 0;

        Check("without ignores SortTitle", "The Legend of Zelda",
            TitleSortNormalizer.Normalize("The Legend of Zelda", " Zelda 1 ", TitleSortNormalization.Without));
        Check("without keeps raw Title", " The Legend of Zelda ",
            TitleSortNormalizer.Normalize(" The Legend of Zelda ", "", TitleSortNormalization.Without));

        Check("simple uses SortTitle", "zelda1",
            TitleSortNormalizer.Normalize("The Legend of Zelda", "Zelda 1", TitleSortNormalization.Simple));
        Check("simple leading article", "legendofzelda",
            TitleSortNormalizer.Normalize("The Legend of Zelda", "", TitleSortNormalization.Simple));
        Check("simple keeps bracket contents", "fifausa98",
            TitleSortNormalizer.Normalize("FIFA (USA) 98", "", TitleSortNormalization.Simple));
        Check("simple keeps internal articles", "boyandhisblob",
            TitleSortNormalizer.Normalize("A Boy and His Blob", "", TitleSortNormalization.Simple));

        Check("advanced uses SortTitle", "ZELDA 1",
            TitleSortNormalizer.Normalize("The Legend of Zelda", "Zelda 1", TitleSortNormalization.Advanced));
        Check("advanced removes bracket contents", "FIFA 98",
            TitleSortNormalizer.Normalize("FIFA (USA) 98", "", TitleSortNormalization.Advanced));
        Check("advanced joins glued brackets", "ROCKBAND",
            TitleSortNormalizer.Normalize("Rock(USA)Band", "", TitleSortNormalization.Advanced));
        Check("advanced removes articles anywhere", "BOY HIS BLOB",
            TitleSortNormalizer.Normalize("A Boy and His Blob", "", TitleSortNormalization.Advanced));
        Check("advanced separates punctuation", "SPIDER MAN 2",
            TitleSortNormalizer.Normalize("Spider-Man 2", "", TitleSortNormalization.Advanced));
        Check("advanced converts supported Roman numerals", "FINAL FANTASY 7",
            TitleSortNormalizer.Normalize("Final Fantasy VII", "", TitleSortNormalization.Advanced));
        Check("advanced Roman conversion is case-sensitive", "FINAL FANTASY VII",
            TitleSortNormalizer.Normalize("Final Fantasy vii", "", TitleSortNormalization.Advanced));
        Check("advanced keeps accents", "POKÉMON",
            TitleSortNormalizer.Normalize("Pokémon", "", TitleSortNormalization.Advanced));

        Console.WriteLine(failures == 0
            ? "[title-sort-selftest] ALL PASS"
            : $"[title-sort-selftest] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;

        void Check(string name, string expected, string actual)
        {
            bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
            Console.WriteLine($"[title-sort-selftest] {(ok ? "PASS" : "FAIL")} {name}: \"{actual}\"");
            if (!ok)
            {
                Console.WriteLine($"  expected: \"{expected}\"");
                failures++;
            }
        }
    }
}
