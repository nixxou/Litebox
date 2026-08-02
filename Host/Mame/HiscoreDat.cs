// HiscoreDat — QUELS jeux savent produire un high score, lu des hiscore.dat réellement installés.
//
// Le fichier est la base de support du plugin hiscore de MAME (<mame>\plugins\hiscore\hiscore.dat) et du
// core FBNeo de RetroArch (<system>\fbneo\hiscore.dat, celui que FbneoHiscore déploie). Il ne décrit QUE
// les machines dont on sait où lire le score en mémoire : un jeu absent du fichier n'écrira jamais de .hi,
// donc il n'a pas de high score à afficher ni à soumettre. C'est la seule source de vérité disponible —
// LaunchBox n'expose rien d'équivalent côté XML.
//
// LE FORMAT, tel qu'il se présente vraiment (mesuré sur les 18 909 lignes du dat de MAME 0.288, 5 856
// entrées) : une ligne « <machine>: » déclare un jeu, et plusieurs peuvent se suivre avant le bloc « @ »
// qu'elles partagent. À ignorer : les lignes vides, les commentaires « ; », et les lignes « @ » qui
// décrivent l'adresse mémoire. Trois formes mesurées qu'un parser naïf raterait :
//   • indentation avant le nom (« \ttimesold: »)
//   • commentaire en fin de ligne (« galaga84:  ; missing » — le jeu compte quand même, il partage le
//     bloc @ du groupe ; « missing » est une note de l'auteur sur SA collection, pas une absence de support)
//   • « <machine>,<software>: » pour les machines à softlist (7 cas : gameboy,tetris…). On retient la
//     MACHINE, seule à correspondre à un nom de rom.
//
// Le cache est global et invalidé — pas rechargé — quand le paysage change (émulateur installé en cours de
// session, hiscore.dat déployé pour FBNeo) : la relecture se fait à la prochaine question, sur le thread
// qui la pose, plutôt que d'imposer une E/S à l'événement.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Mame;

internal static class HiscoreDat
{
    private static readonly object _gate = new();
    private static HashSet<string>? _roms;      // null = à (re)construire

    /// <summary>Le paysage a changé (émulateur installé, hiscore.dat déployé) : la prochaine question
    /// relira les fichiers. Ne fait aucune E/S ici. Le cache PAR JEU de GameSortCatalog est vidé aussi,
    /// sans quoi un jeu jugé non supporté avant l'installation le resterait toute la session.</summary>
    public static void Invalidate()
    {
        lock (_gate) _roms = null;
        try { GameSortCatalog.ClearMameSupportCache(); } catch { }
    }

    /// <summary>Combien de jeux sont supportés, tous fichiers confondus (0 = aucun dat trouvé).</summary>
    public static int Count => Names.Count;

    /// <summary>Ce jeu (nom de rom sans extension) peut-il avoir un high score ? Faux quand aucun
    /// hiscore.dat n'est installé — sans le fichier, aucun score n'existe.</summary>
    public static bool Supports(string? romName)
        => !string.IsNullOrWhiteSpace(romName) && Names.Contains(romName.Trim());

    private static HashSet<string> Names
    {
        get
        {
            lock (_gate)
            {
                if (_roms != null) return _roms;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in DatFiles())
                {
                    int before = set.Count;
                    if (ParseInto(file, set))
                        Console.WriteLine($"[hiscore] {set.Count - before} new rom(s) from {file}");
                }
                Console.WriteLine($"[hiscore] {set.Count} rom(s) support high scores");
                return _roms = set;
            }
        }
    }

    /// <summary>Les hiscore.dat installés : celui du plugin hiscore de chaque MAME, et celui du core FBNeo
    /// de chaque RetroArch (là où FbneoHiscore l'écrit). Sans doublon de chemin.</summary>
    internal static List<string> DatFiles()
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path); } catch { return; }
            if (File.Exists(path) && seen.Add(path)) files.Add(path);
        }

        foreach (var e in Safe(() => PluginHelper.DataManager?.GetAllEmulators()) ?? Array.Empty<IEmulator>())
        {
            string dir = EmuDir(e);
            if (dir.Length == 0) continue;
            if (MameLeaderboards.IsMameEmulator(e)) Add(Path.Combine(dir, "plugins", "hiscore", "hiscore.dat"));
            if (MameLeaderboards.IsFbneoRetroArch(e)) Add(FbneoHiscore.DeployedPath(e));
        }
        return files;
    }

    private static string EmuDir(IEmulator? e)
    {
        try
        {
            var ap = Safe(() => e?.ApplicationPath);
            if (string.IsNullOrWhiteSpace(ap)) return "";
            return Path.GetDirectoryName(Path.GetFullPath(ap!)) ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Ajoute les machines déclarées par ce fichier. Retourne false si illisible.</summary>
    internal static bool ParseInto(string file, ISet<string> into)
    {
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '@') continue;

                // « nom: » éventuellement suivi d'un commentaire. Le deux-points est obligatoire : sans lui
                // la ligne est de la donnée, pas une déclaration.
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string head = line.Substring(0, colon).Trim();
                string tail = line.Substring(colon + 1).Trim();
                if (tail.Length > 0 && tail[0] != ';') continue;   // pas une déclaration (donnée après le « : »)

                // « machine,software: » → la machine, seule à correspondre à un nom de rom.
                int comma = head.IndexOf(',');
                if (comma >= 0) head = head.Substring(0, comma).Trim();
                if (head.Length == 0 || head.IndexOf(' ') >= 0) continue;
                into.Add(head);
            }
            return true;
        }
        catch (Exception ex) { Console.WriteLine($"[hiscore] unreadable {file}: {ex.Message}"); return false; }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
