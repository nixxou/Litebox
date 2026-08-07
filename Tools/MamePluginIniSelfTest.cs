// --selftest-mame-plugin : l'éditeur de plugin.ini / mame.ini, sur les formes que MAME écrit vraiment.
//
// Ce code modifie un fichier de configuration qui ne nous appartient pas, dans l'installation MAME de
// l'utilisateur. Les deux garanties qui comptent sont donc : (1) une seule ligne change, tout le reste
// survit intact, et (2) rien ne s'écrit quand la valeur est déjà bonne. Le reste des cas vient du format
// lui-même — séparateur espace OU tabulation, lignes de commentaire « # » qui ne sont PAS l'option même
// quand elles la nomment, et option absente qu'on ajoute (plugin.ini) ou pas (mame.ini, où l'absence vaut
// déjà le défaut voulu).

using System;
using System.IO;
using LbApiHost.Host.Mame;

namespace LbApiHost.Tools;

internal static class MamePluginIniSelfTest
{
    private static readonly string[] Header = { "#", "# PLUGINS OPTIONS", "#" };

    public static int Run()
    {
        int fail = 0;
        string dir = Path.Combine(Path.GetTempPath(), "LiteBoxMamePlugin_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);

            // ── plugin.ini absent → créé, en-tête compris
            {
                string f = Path.Combine(dir, "a.ini");
                fail += Is(MameHiscorePlugin.SetOption(f, "hiscore", "1", Header, true), "created", "fichier absent");
                string txt = File.ReadAllText(f);
                fail += True(txt.Contains("# PLUGINS OPTIONS"), "l'en-tete MAME est ecrit");
                fail += True(Value(f, "hiscore") == "1", "hiscore vaut 1");
            }

            // ── hiscore 0 → passe à 1, les autres plugins ne bougent pas
            {
                string f = Write(dir, "b.ini", "#", "# PLUGINS OPTIONS", "#",
                                 "autofire                  0", "cheat                     1", "hiscore                   0");
                fail += Is(MameHiscorePlugin.SetOption(f, "hiscore", "1", Header, true), "set", "valeur a corriger");
                fail += True(Value(f, "hiscore") == "1", "hiscore passe a 1");
                fail += True(Value(f, "cheat") == "1" && Value(f, "autofire") == "0", "les autres plugins gardent leur valeur");
                fail += True(File.ReadAllLines(f).Length == 6, "aucune ligne ajoutee ni perdue");
            }

            // ── déjà à 1 → rien n'est réécrit (le fichier reste identique à l'octet près)
            {
                string f = Write(dir, "c.ini", "hiscore                   1");
                var before = File.GetLastWriteTimeUtc(f);
                string raw = File.ReadAllText(f);
                fail += Is(MameHiscorePlugin.SetOption(f, "hiscore", "1", Header, true), "already", "valeur deja bonne");
                fail += True(File.ReadAllText(f) == raw && File.GetLastWriteTimeUtc(f) == before, "le fichier n'est pas reecrit");
            }

            // ── option absente : ajoutée dans plugin.ini, ignorée dans mame.ini
            {
                string f = Write(dir, "d.ini", "#", "cheat                     0");
                fail += Is(MameHiscorePlugin.SetOption(f, "hiscore", "1", Header, true), "added", "option absente, ajout demande");
                fail += True(Value(f, "hiscore") == "1", "hiscore ajoute");
                fail += True(Value(f, "cheat") == "0", "la ligne existante survit a l'ajout");

                string g = Write(dir, "e.ini", "#", "readconfig                1");
                string raw = File.ReadAllText(g);
                fail += Is(MameHiscorePlugin.SetOption(g, "plugins", "1", null, false), "already", "option absente, ajout NON demande");
                fail += True(File.ReadAllText(g) == raw, "mame.ini sans 'plugins' reste intact");
            }

            // ── un commentaire qui NOMME l'option n'est pas l'option
            {
                string f = Write(dir, "f.ini", "# hiscore                 1", "#hiscore                  1");
                fail += Is(MameHiscorePlugin.SetOption(f, "hiscore", "1", Header, true), "added", "les '#' ne comptent pas comme l'option");
                fail += True(File.ReadAllLines(f).Length == 3, "les deux commentaires sont conserves");
            }

            // ── séparateur tabulation, et indentation devant le nom
            {
                string f = Write(dir, "g.ini", "\thiscore\t0");
                fail += Is(MameHiscorePlugin.SetOption(f, "hiscore", "1", Header, true), "set", "tabulation + indentation reconnues");
                fail += True(Value(f, "hiscore") == "1", "hiscore passe a 1 malgre la tabulation");
                fail += True(File.ReadAllLines(f).Length == 1, "la ligne est remplacee, pas doublee");
            }

            // ── mame.ini : le maître 'plugins 0' est corrigé
            {
                string f = Write(dir, "h.ini", "plugins                   0", "skip_gameinfo             1");
                fail += Is(MameHiscorePlugin.SetOption(f, "plugins", "1", null, false), "set", "master switch a 0");
                fail += True(Value(f, "plugins") == "1" && Value(f, "skip_gameinfo") == "1", "seul 'plugins' change");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine(fail == 0 ? "[mame-plugin-test] OK" : $"[mame-plugin-test] {fail} ECHEC(S)");
        return fail == 0 ? 0 : 1;
    }

    private static string Write(string dir, string name, params string[] lines)
    {
        string f = Path.Combine(dir, name);
        File.WriteAllLines(f, lines);
        return f;
    }

    /// <summary>La valeur de l'option, lue comme MAME la lit (premier token = nom, reste = valeur).</summary>
    private static string Value(string file, string name)
    {
        foreach (var raw in File.ReadAllLines(file))
        {
            string t = raw.TrimStart();
            if (t.Length == 0 || t[0] == '#') continue;
            int sp = t.IndexOfAny(new[] { ' ', '\t' });
            if (!(sp < 0 ? t : t.Substring(0, sp)).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return sp < 0 ? "" : t.Substring(sp).Trim();
        }
        return "(absent)";
    }

    private static int Is(string got, string want, string what)
    {
        if (got == want) return 0;
        Console.WriteLine($"[mame-plugin-test] FAIL {what} : attendu \"{want}\", obtenu \"{got}\"");
        return 1;
    }

    private static int True(bool ok, string what)
    {
        if (ok) return 0;
        Console.WriteLine($"[mame-plugin-test] FAIL {what}");
        return 1;
    }
}
