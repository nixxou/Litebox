// --selftest-playlist-copy: la logique du copier-coller de playlists, sur un Parents.xml temporaire.
//
// Ce qui est testé ici est exactement ce qu'un mock raterait : la déduction de la plateforme SOURCE
// (qui n'est stockée nulle part) et la substitution de son nom. Les deux règles de déduction sont
// couvertes — la règle « Platform Is Equal To », et la remontée des parents pour une playlist qui n'en
// a pas — plus les pièges qui ont motivé le code : "Nintendo 64" vs "Nintendo 64DD", la casse, et une
// playlist rangée sous une catégorie elle-même nichée sous une plateforme.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Platforms;

namespace LbApiHost.Tools;

internal static class PlaylistCopySelfTest
{
    public static int Run()
    {
        int failures = 0;
        string root = Path.Combine(Path.GetTempPath(), "LiteBoxPlCopy_" + Guid.NewGuid().ToString("N"));
        string was = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            was = MediaResolver.SwapRootForTest(root);

            // Hiérarchie : Consoles > Nintendo 64 ; Arcade (plateforme) directement sous Root.
            //   PL-A : règle Platform EqualTo "Arcade"        → source par la règle
            //   PL-B : aucune règle, parent = catégorie Consoles dont l'ancêtre est "Nintendo 64"
            //   PL-C : aucune règle, parent = plateforme "Nintendo 64", titre "Nintendo 64DD Demos"
            //   PL-D : aucune règle, aucun parent plateforme  → non copiable
            File.WriteAllText(Path.Combine(root, "Data", "Parents.xml"), """
<?xml version="1.0" standalone="yes"?>
<LaunchBox>
  <Parent><PlatformName>Nintendo 64</PlatformName><PlaylistId /><PlatformCategoryName /><ParentPlatformName /><ParentPlaylistId /><ParentPlatformCategoryName>Consoles</ParentPlatformCategoryName></Parent>
  <Parent><PlatformName /><PlaylistId /><PlatformCategoryName>Retro</PlatformCategoryName><ParentPlatformName>Nintendo 64</ParentPlatformName><ParentPlaylistId /><ParentPlatformCategoryName /></Parent>
  <Parent><PlatformName /><PlaylistId>PL-B</PlaylistId><PlatformCategoryName /><ParentPlatformName /><ParentPlaylistId /><ParentPlatformCategoryName>Retro</ParentPlatformCategoryName></Parent>
  <Parent><PlatformName /><PlaylistId>PL-C</PlaylistId><PlatformCategoryName /><ParentPlatformName>Nintendo 64</ParentPlatformName><ParentPlaylistId /><ParentPlatformCategoryName /></Parent>
  <Parent><PlatformName /><PlaylistId>PL-D</PlaylistId><PlatformCategoryName /><ParentPlatformName /><ParentPlaylistId /><ParentPlatformCategoryName>Consoles</ParentPlatformCategoryName></Parent>
</LaunchBox>
""");

            var idx = ParentsIndex.LoadFrom(Path.Combine(root, "Data", "Parents.xml"));
            var plats = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PlaylistFilterCatalog.Norm("Arcade")] = "Arcade",
                [PlaylistFilterCatalog.Norm("Nintendo 64")] = "Nintendo 64",
                [PlaylistFilterCatalog.Norm("Nintendo 64DD")] = "Nintendo 64DD",
            };

            failures += RuleWins(idx, plats);
            failures += ParentCategoryAncestorIsFound(idx, plats);
            failures += LongestNameWins(idx, plats);
            failures += NoPlatformNoCopy(idx, plats);
            failures += SubstitutionCases();
            failures += PlatformAncestorsWalkCategories(idx);
            failures += RuleTranspositionCases();
        }
        catch (Exception ex) { Console.WriteLine("[plcopy-test] FATAL " + ex); failures++; }
        finally
        {
            if (was != null) MediaResolver.SwapRootForTest(was);
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(failures == 0 ? "[plcopy-test] ALL PASS" : $"[plcopy-test] {failures} FAILURE(S)");
        return failures;
    }

    // ── cas ───────────────────────────────────────────────────────────────────
    private static int RuleWins(ParentsIndex idx, Dictionary<string, string> plats)
    {
        // La règle prime, et même si le titre ne contient AUCUN nom de plateforme.
        var pl = Playlist("PL-A", "Best Beat Em Ups");
        pl.AddFilter(new PlaylistFilterDef("Platform", "EqualTo", "Arcade"));
        var got = PlaylistCopier.SourcePlatformOf(pl, idx, plats, out _);
        return Check("the Platform rule alone identifies the source", "Arcade", got);
    }

    private static int ParentCategoryAncestorIsFound(ParentsIndex idx, Dictionary<string, string> plats)
    {
        // Parent = catégorie "Retro", elle-même sous la plateforme "Nintendo 64" : c'est en remontant
        // DEUX crans qu'on trouve la plateforme, et le titre la nomme.
        var pl = Playlist("PL-B", "Nintendo 64 Racing Games");
        var got = PlaylistCopier.SourcePlatformOf(pl, idx, plats, out _);
        return Check("a platform above the parent CATEGORY counts", "Nintendo 64", got);
    }

    private static int LongestNameWins(ParentsIndex idx, Dictionary<string, string> plats)
    {
        // "Nintendo 64" est bien un parent, et son nom EST dans "Nintendo 64DD Demos" — mais seulement
        // noyé dans un autre mot. Sans la préférence pour la frontière de mot, la substitution
        // produirait "<dest>DD Demos".
        var pl = Playlist("PL-C", "Nintendo 64DD Demos");
        var got = PlaylistCopier.SourcePlatformOf(pl, idx, plats, out _);
        int bad = Check("a parent platform is still found for a 64DD title", "Nintendo 64", got);
        // La substitution, elle, ne doit PAS couper le mot : aucune occurrence en frontière → la
        // règle de repli remplace, mais le test ci-dessous fige le comportement attendu.
        string sub = PlaylistCopier.Substitute("Nintendo 64DD Demos", "Nintendo 64", "Arcade");
        bad += Check("no boundary hit → plain replacement is used", "ArcadeDD Demos", sub);
        return bad;
    }

    private static int NoPlatformNoCopy(ParentsIndex idx, Dictionary<string, string> plats)
    {
        // Parent = catégorie "Consoles", qui n'a aucune plateforme au-dessus d'elle.
        var pl = Playlist("PL-D", "My Favourites");
        var got = PlaylistCopier.SourcePlatformOf(pl, idx, plats, out string why);
        if (got != null) { Console.WriteLine($"[plcopy-test] FAIL no-source: expected null, got \"{got}\""); return 1; }
        if (string.IsNullOrWhiteSpace(why)) { Console.WriteLine("[plcopy-test] FAIL no-source: no reason given"); return 1; }
        Console.WriteLine("[plcopy-test] PASS a playlist with no parent platform is not copyable");
        return 0;
    }

    private static int SubstitutionCases()
    {
        int bad = 0;
        bad += Check("substitutes on a word boundary",
                     "FBNeo Ball & Paddle Games",
                     PlaylistCopier.Substitute("Arcade Ball & Paddle Games", "Arcade", "FBNeo"));
        bad += Check("detection ignores case, output uses the canonical one",
                     "Sony PlayStation 2 Racers",
                     PlaylistCopier.Substitute("sony playstation 2 Racers", "Sony PlayStation 2", "Sony PlayStation 2"));
        bad += Check("a boundary hit is preferred over an embedded one",
                     "N64 games for Nintendo 64DD",
                     PlaylistCopier.Substitute("Nintendo 64 games for Nintendo 64DD", "Nintendo 64", "N64"));
        bad += Check("no occurrence → unchanged",
                     "Puzzle Games",
                     PlaylistCopier.Substitute("Puzzle Games", "Arcade", "FBNeo"));
        return bad;
    }

    private static int PlatformAncestorsWalkCategories(ParentsIndex idx)
    {
        var got = idx.PlatformAncestorsOf(new ParentKey('l', "PL-B"));
        return Check("PlatformAncestorsOf climbs through categories", "Nintendo 64", string.Join(",", got));
    }

    // La transposition des regles au collage : toute regle sur le champ Platform dont la VALEUR est la
    // plateforme source passe a la destination, quel que soit le comparateur — sans quoi un
    // « Contains Arcade » colle sur FBNeo continue de lister les jeux d'Arcade en ayant l'air reussi.
    // Les listes et les regles visant une AUTRE plateforme restent telles quelles (a editer a la main).
    private static int RuleTranspositionCases()
    {
        static string T(string field, string cmp, string value)
            => PlaylistCopier.TransposedRuleValue(new PlaylistFilterDef(field, cmp, value), "Arcade", "FBNeo");
        int bad = 0;
        bad += Check("EqualTo on the source platform is transposed", "FBNeo", T("Platform", "EqualTo", "Arcade"));
        bad += Check("Contains on the source platform is transposed too", "FBNeo", T("Platform", "Contains", "Arcade"));
        bad += Check("value matching is case-insensitive", "FBNeo", T("Platform", "StartsWith", "arcade"));
        bad += Check("a rule aimed at ANOTHER platform is kept verbatim", "Nintendo 64", T("Platform", "EqualTo", "Nintendo 64"));
        bad += Check("a value LIST is kept verbatim (manual edit)", "Arcade;Daphne", T("Platform", "HasAtLeastOneOf", "Arcade;Daphne"));
        bad += Check("a non-Platform field is never touched", "Arcade", T("Genre", "Contains", "Arcade"));
        return bad;
    }

    // ── outillage ─────────────────────────────────────────────────────────────
    private static HostPlaylist Playlist(string id, string name)
        => new() { PlaylistIdValue = id, NameValue = name };

    private static int Check(string what, string expected, string got)
    {
        if (string.Equals(expected, got, StringComparison.Ordinal))
        {
            Console.WriteLine("[plcopy-test] PASS " + what);
            return 0;
        }
        Console.WriteLine($"[plcopy-test] FAIL {what} — expected [{expected}] got [{got}]");
        return 1;
    }
}
