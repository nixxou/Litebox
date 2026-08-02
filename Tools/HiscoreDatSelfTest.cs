// --selftest-hiscore-dat : le parser de hiscore.dat, sur les formes réellement rencontrées.
//
// Le fichier de MAME 0.288 fait 18 909 lignes pour 5 856 entrées, et trois de ses formes cassent un parser
// naïf : l'indentation avant le nom, le commentaire en fin de ligne (« galaga84:  ; missing », qui compte
// quand même — il partage le bloc @ de son groupe), et « machine,software: » des softlists. Le reste du
// fichier — les blocs « @ », les commentaires, les lignes vides — ne doit rien produire.

using System;
using System.Collections.Generic;
using System.IO;
using LbApiHost.Host.Mame;

namespace LbApiHost.Tools;

internal static class HiscoreDatSelfTest
{
    private const string Sample = """
; commentaire d'en-tete
;@s:acorn/ertictac.cpp

ertictaca:
ertictacb:
	timesold:
@:maincpu,program,bb1c,2e,46,ca

;(clay shoot) (by GeoMan)
clayshoo:
@:maincpu,program,2140,8,be,00

galaga84:  ; missing
gameboy,tetris:
@:maincpu,program,8a20,2d,00,18
""";

    public static int Run()
    {
        int fail = 0;
        string dir = Path.Combine(Path.GetTempPath(), "LiteBoxHiscore_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "hiscore.dat");
            File.WriteAllText(file, Sample);

            var got = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!HiscoreDat.ParseInto(file, got)) { Console.WriteLine("[hiscore-test] FAIL fichier illisible"); return 1; }

            fail += Has(got, "ertictaca", "un nom simple est retenu");
            fail += Has(got, "ertictacb", "deux noms qui se suivent avant le bloc @ comptent tous les deux");
            fail += Has(got, "timesold", "un nom indente est retenu");
            fail += Has(got, "clayshoo", "un nom precede d'un commentaire est retenu");
            fail += Has(got, "galaga84", "un nom suivi d'un commentaire ';' est retenu");
            fail += Has(got, "gameboy", "'machine,software:' retient la MACHINE");

            fail += Hasnt(got, "tetris", "le nom de software n'est PAS retenu");
            fail += Hasnt(got, "@", "les blocs @ ne produisent rien");
            fail += Hasnt(got, "maincpu", "le contenu d'un bloc @ ne produit rien");

            if (got.Count != 6) { Console.WriteLine($"[hiscore-test] FAIL {got.Count} entrees au lieu de 6 : {string.Join(", ", got)}"); fail++; }
            else Console.WriteLine("[hiscore-test] PASS rien d'autre n'est retenu");

            // Un fichier absent ne casse rien et n'ajoute rien.
            var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (HiscoreDat.ParseInto(Path.Combine(dir, "nope.dat"), none) || none.Count != 0)
            { Console.WriteLine("[hiscore-test] FAIL un fichier absent devrait etre un echec silencieux"); fail++; }
            else Console.WriteLine("[hiscore-test] PASS un fichier absent est un echec silencieux");
        }
        catch (Exception ex) { Console.WriteLine("[hiscore-test] FATAL " + ex); fail++; }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine(fail == 0 ? "[hiscore-test] ALL PASS" : $"[hiscore-test] {fail} FAILURE(S)");
        return fail;
    }

    private static int Has(HashSet<string> set, string name, string what)
    {
        if (set.Contains(name)) { Console.WriteLine("[hiscore-test] PASS " + what); return 0; }
        Console.WriteLine($"[hiscore-test] FAIL {what} — '{name}' absent");
        return 1;
    }

    private static int Hasnt(HashSet<string> set, string name, string what)
    {
        if (!set.Contains(name)) { Console.WriteLine("[hiscore-test] PASS " + what); return 0; }
        Console.WriteLine($"[hiscore-test] FAIL {what} — '{name}' present");
        return 1;
    }
}
